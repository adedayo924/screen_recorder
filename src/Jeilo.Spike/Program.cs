using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;
using Jeilo.Core.Audio;
using Jeilo.Core.Capture;
using Jeilo.Core.Encoding;
using Jeilo.Core.Recording;
using Jeilo.Spike.Diagnostics;
using Windows.Graphics.Capture;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Jeilo.Spike;

internal static class Program
{
    private static volatile bool _stopRequested;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                PrintHelp();
                return 0;
            }

            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                _stopRequested = true;
                Console.WriteLine("Stop requested. Finalizing the current recording...");
            };

            return args[0].ToLowerInvariant() switch
            {
                "devices" => RunDevices(args),
                "audio-test" => RunAudioTest(args),
                "record" => RunRecord(args),
                "engine-test" => RunEngineTest(args),
                "analyze" => RunAnalyze(args),
                _ => UnknownCommand(args[0])
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    private static int RunDevices(string[] args)
    {
        var ffmpeg = FindFfmpeg(GetArg(args, "--ffmpeg"));
        Console.WriteLine("Jeilo Recorder Phase 0 diagnostics");
        Console.WriteLine("==================================");
        Console.WriteLine();
        Console.WriteLine("Audio:");
        AudioDeviceCatalog.Print(Console.Out);
        Console.WriteLine();
        Console.WriteLine("Monitors:");
        MonitorCatalog.Print(Console.Out);
        Console.WriteLine();
        Console.WriteLine("Windows:");
        WindowCatalog.Print(Console.Out);
        Console.WriteLine();
        Console.WriteLine($"FFmpeg: {ffmpeg}");
        Console.WriteLine($"OS: {Environment.OSVersion}");
        Console.WriteLine($"64-bit process: {Environment.Is64BitProcess}");
        Console.WriteLine($"Processor count: {Environment.ProcessorCount}");
        return 0;
    }

    private static int RunAudioTest(string[] args)
    {
        var seconds = GetInt(args, "--seconds", 10);
        var micQuery = GetArg(args, "--mic") ?? "H340";
        var systemQuery = GetArg(args, "--system");
        var play = HasFlag(args, "--play");
        var duration = TimeSpan.FromSeconds(Math.Max(1, seconds));
        var outputDirectory = Path.Combine(Environment.CurrentDirectory, "artifacts", "phase0");
        Directory.CreateDirectory(outputDirectory);
        var wavPath = Path.Combine(outputDirectory, $"audio-test-{Timestamp()}.wav");

        using var mic = ResolveMic(micQuery);
        using var render = ResolveSystem(systemQuery);

        Console.WriteLine($"Microphone: {mic.FriendlyName}");
        Console.WriteLine($"System audio: {render.FriendlyName}");
        Console.WriteLine($"Recording {duration.TotalSeconds:0}s of mixed audio to {wavPath}");
        Console.WriteLine("Speak now so the microphone path can be verified.");
        Console.WriteLine();

        using var audio = new AudioMixRecorder(mic, render, Stream.Null, wavPath);
        var clock = new RecordingClock { SyncIntervalSeconds = 10 };
        audio.StartCaptures();
        audio.StartOutput(clock, duration);

        using var tone = new SyncTonePlayer();
        tone.Start(clock);

        var app = Process.GetCurrentProcess();
        var started = Stopwatch.StartNew();
        clock.Start();

        var maxMic = 0f;
        var maxSystem = 0f;

        while (started.Elapsed < duration + TimeSpan.FromSeconds(1.5))
        {
            Thread.Sleep(1000);
            var peaks = audio.ConsumePeaks();
            maxMic = Math.Max(maxMic, peaks.Mic);
            maxSystem = Math.Max(maxSystem, peaks.System);
            app.Refresh();
            Console.WriteLine(
                $"{started.Elapsed.TotalSeconds,5:0.0}s  " +
                $"mic={Bar(peaks.Mic)} {peaks.Mic,5:0.000}  " +
                $"system={Bar(peaks.System)} {peaks.System,5:0.000}  " +
                $"wav={new FileInfo(wavPath).Length / 1024.0,7:0.0} KiB  " +
                $"appMem={app.WorkingSet64 / 1024.0 / 1024.0,6:0.0} MiB");
        }

        tone.Stop();
        audio.Stop();

        Console.WriteLine();
        Console.WriteLine($"Max mic peak:    {maxMic:0.000}");
        Console.WriteLine($"Max system peak: {maxSystem:0.000}");
        Console.WriteLine($"Mixed WAV:       {wavPath}");

        if (maxMic < 0.001f)
        {
            Console.WriteLine("WARNING: the microphone produced no measurable signal. Check the selected device and Windows input level.");
        }

        if (maxSystem < 0.001f)
        {
            Console.WriteLine("WARNING: system audio produced no measurable signal. Check the default render device and its volume.");
        }

        if (play)
        {
            PlayWav(wavPath);
        }

        return 0;
    }

    private static int RunEngineTest(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        var seconds = Math.Clamp(GetInt(args, "--seconds", 6), 3, 30);
        var monitor = MonitorCatalog.GetPrimary();
        var duration = TimeSpan.FromSeconds(seconds);
        var outputDirectory = Path.Combine(Environment.CurrentDirectory, "artifacts", "phase1");
        Directory.CreateDirectory(outputDirectory);

        using var patternHost = TestPatternHost.Start(
            new RecordingClock(),
            duration,
            new Rectangle(monitor.Bounds.Left - 1920 + 160, monitor.Bounds.Top + 40, 1920, 1080));

        var engine = new RecordingEngine();
        engine.ProgressChanged += progress =>
        {
            if (progress.State == RecordingState.Recording)
            {
                Console.WriteLine(
                    $"{progress.Elapsed.TotalSeconds,5:0.0}s  " +
                    $"frames={progress.FramesWritten} dropped={progress.FramesDropped}  " +
                    $"mic={progress.MicrophonePeak:0.000} sys={progress.SystemAudioPeak:0.000}  " +
                    $"cpu={progress.CpuPercent:0.0}% mem={progress.MemoryBytes / 1024.0 / 1024.0:0.0}MiB");
            }
            else
            {
                Console.WriteLine($"[{progress.State}] {progress.Message}");
            }
        };

        try
        {
            engine.StartAsync(new RecordingOptions
            {
                OutputDirectory = outputDirectory,
                FileName = "engine-test",
                TargetKind = CaptureTargetKind.Window,
                WindowHandle = patternHost.Handle,
                Fps = 30,
                Quality = VideoQuality.High,
                Encoder = "auto",
                MicrophoneEnabled = true,
                MicrophoneId = "H340",
                SystemAudioEnabled = true,
                IncludeCursor = false,
                MaxDuration = TimeSpan.FromSeconds(30)
            }).GetAwaiter().GetResult();

            Thread.Sleep(duration);
            var result = engine.StopAsync().GetAwaiter().GetResult();
            if (result is null)
            {
                Console.Error.WriteLine("No recording result was returned.");
                return 1;
            }

            Console.WriteLine();
            Console.WriteLine($"File:     {result.FilePath}");
            Console.WriteLine($"Duration: {result.Duration.TotalSeconds:0.000}s");
            Console.WriteLine($"Size:     {result.FileSizeBytes / 1024.0:0.0} KiB");
            Console.WriteLine($"Frames:   {result.FramesWritten} dropped={result.FramesDropped}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Engine test failed: {ex}");
            return 1;
        }
        finally
        {
            engine.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static int RunRecord(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        var source = (GetArg(args, "--source") ?? "window").ToLowerInvariant();
        var duration = TimeSpan.FromSeconds(Math.Max(2, GetInt(args, "--duration", 60)));
        var fps = Math.Clamp(GetInt(args, "--fps", 30), 10, 60);
        var encoder = GetArg(args, "--encoder") ?? "qsv";
        var micQuery = GetArg(args, "--mic") ?? "H340";
        var systemQuery = GetArg(args, "--system");
        var showPattern = HasFlag(args, "--pattern");
        var visible = HasFlag(args, "--visible");
        var syncInterval = GetDouble(args, "--sync-interval", 30);
        var widths = GetInt(args, "--width", 1920);
        var heights = GetInt(args, "--height", 1080);
        var ffmpeg = FindFfmpeg(GetArg(args, "--ffmpeg"));
        var ffprobe = FindFfprobe(ffmpeg);
        var monitor = MonitorCatalog.GetPrimary();

        var outputBase = GetArg(args, "--output") ??
                         Path.Combine(Environment.CurrentDirectory, "artifacts", "phase0", $"recording-{Timestamp()}");
        outputBase = Path.GetFullPath(outputBase);
        Directory.CreateDirectory(Path.GetDirectoryName(outputBase)!);
        var mkvPath = outputBase + ".mkv";

        if (source is not ("window" or "monitor"))
        {
            throw new ArgumentException("--source must be 'window' or 'monitor'.");
        }

        if (source == "monitor" && !showPattern)
        {
            Console.WriteLine("WARNING: monitor capture records the actual desktop. Use --pattern for a test-pattern-only run.");
        }

        var clock = new RecordingClock { SyncIntervalSeconds = syncInterval };
        TestPatternHost? patternHost = null;
        GraphicsCaptureItem? item = null;

        try
        {
            if (source == "window")
            {
                var bounds = visible
                    ? new Rectangle(monitor.Bounds.Left + 80, monitor.Bounds.Top + 80, widths, heights)
                    : new Rectangle(monitor.Bounds.Left - widths + 160, monitor.Bounds.Top + 40, widths, heights);

                patternHost = TestPatternHost.Start(clock, duration, bounds);
                item = WgcInterop.CreateForWindow(patternHost.Handle);
            }
            else
            {
                if (showPattern)
                {
                    patternHost = TestPatternHost.Start(
                        clock,
                        duration,
                        new Rectangle(monitor.Bounds.Left, monitor.Bounds.Top, monitor.Bounds.Width, monitor.Bounds.Height),
                        topMost: true);
                }

                item = WgcInterop.CreateForMonitor(monitor.Handle);
            }

            using var capture = new WgcCapture(item, fps);
            Console.WriteLine($"Capture item: {capture.SourceName} ({capture.Width}x{capture.Height})");
            capture.Start();

            if (!capture.WaitForFirstFrame(TimeSpan.FromSeconds(15)))
            {
                throw new TimeoutException("Windows.Graphics.Capture did not deliver a frame within 15 seconds.");
            }

            using var mic = ResolveMic(micQuery);
            using var render = ResolveSystem(systemQuery);
            Console.WriteLine($"Microphone:  {mic.FriendlyName}");
            Console.WriteLine($"System audio: {render.FriendlyName}");

            using var muxer = new FfmpegMuxer(ffmpeg, mkvPath, capture.Width, capture.Height, fps, encoder);
            Console.WriteLine($"Starting FFmpeg ({encoder})...");
            muxer.Start();

            using var audio = new AudioMixRecorder(mic, render, muxer.AudioStream, outputBase + ".mixed.wav");
            audio.StartCaptures();
            audio.ResetBuffers();

            using var tone = new SyncTonePlayer();
            tone.Start(clock);

            Console.WriteLine("Starting synchronized audio and video writers...");
            audio.StartOutput(clock, duration);
            capture.StartOutput(clock, muxer.VideoStream, duration);

            var app = Process.GetCurrentProcess();
            var indicators = new List<string>();
            var maxMic = 0f;
            var maxSystem = 0f;
            var peakAppCpu = 0.0;
            var peakAppMemory = 0L;
            var peakFfmpegMemory = 0L;
            var lastAppCpu = app.TotalProcessorTime;
            var lastFfCpu = muxer.TotalProcessorTime;
            var lastWall = Stopwatch.StartNew();

            Console.WriteLine($"Recording for {duration.TotalSeconds:0}s -> {mkvPath}");
            clock.Start();

            var expectedFrames = (long)Math.Round(duration.TotalSeconds * fps);
            var expectedSamples = (long)(duration.TotalSeconds * AudioMixRecorder.OutputSampleRate) *
                                  AudioMixRecorder.OutputChannels;
            var deadline = duration + TimeSpan.FromSeconds(20);
            var lastStats = TimeSpan.Zero;

            while (!_stopRequested &&
                   clock.Elapsed < deadline &&
                   (capture.FramesWritten < expectedFrames || audio.SamplesWritten < expectedSamples))
            {
                Thread.Sleep(100);

                if (clock.Elapsed - lastStats < TimeSpan.FromSeconds(2))
                {
                    continue;
                }

                lastStats = clock.Elapsed;
                var peaks = audio.ConsumePeaks();
                maxMic = Math.Max(maxMic, peaks.Mic);
                maxSystem = Math.Max(maxSystem, peaks.System);

                app.Refresh();
                var appCpuDelta = app.TotalProcessorTime - lastAppCpu;
                var ffCpuDelta = muxer.TotalProcessorTime - lastFfCpu;
                var wallDelta = lastWall.Elapsed;
                lastAppCpu = app.TotalProcessorTime;
                lastFfCpu = muxer.TotalProcessorTime;
                lastWall.Restart();

                var appCpuPercent = wallDelta.TotalMilliseconds > 0
                    ? 100.0 * appCpuDelta.TotalMilliseconds / (wallDelta.TotalMilliseconds * Environment.ProcessorCount)
                    : 0;
                var ffCpuPercent = wallDelta.TotalMilliseconds > 0
                    ? 100.0 * ffCpuDelta.TotalMilliseconds / (wallDelta.TotalMilliseconds * Environment.ProcessorCount)
                    : 0;

                peakAppCpu = Math.Max(peakAppCpu, appCpuPercent + ffCpuPercent);
                peakAppMemory = Math.Max(peakAppMemory, app.WorkingSet64);
                peakFfmpegMemory = Math.Max(peakFfmpegMemory, muxer.WorkingSetBytes);

                var indicator = indicators.Count == 0 ? string.Empty : $" [{string.Join(", ", indicators)}]";
                Console.WriteLine(
                    $"{clock.Elapsed.TotalSeconds,6:0.0}s  " +
                    $"video {capture.FramesWritten}/{expectedFrames} " +
                    $"audio {audio.SamplesWritten / AudioMixRecorder.OutputChannels / AudioMixRecorder.OutputSampleRate,4:0.0}s  " +
                    $"mic {Bar(peaks.Mic)} {peaks.Mic:0.000}  " +
                    $"sys {Bar(peaks.System)} {peaks.System:0.000}  " +
                    $"app {appCpuPercent,4:0.0}% {app.WorkingSet64 / 1024.0 / 1024.0,5:0.0}MiB  " +
                    $"ffmpeg {ffCpuPercent,4:0.0}% {muxer.WorkingSetBytes / 1024.0 / 1024.0,5:0.0}MiB" +
                    indicator);
            }

            if (_stopRequested)
            {
                Console.WriteLine("Stopping at user request.");
            }

            capture.Stop();
            audio.Stop();
            tone.Stop();
            patternHost?.Close();

            Console.WriteLine("Finalizing MKV and remuxing to MP4...");
            var mp4Path = muxer.Complete();
            var probeJson = RunFfprobe(ffprobe, mp4Path);
            var probePath = outputBase + ".probe.json";
            File.WriteAllText(probePath, probeJson);

            var results = new
            {
                App = "Jeilo.Spike",
                Phase = 0,
                Timestamp = DateTimeOffset.Now,
                Options = new
                {
                    Source = source,
                    DurationSeconds = duration.TotalSeconds,
                    Fps = fps,
                    Encoder = encoder,
                    Mic = mic.FriendlyName,
                    SystemAudio = render.FriendlyName,
                    SyncIntervalSeconds = syncInterval
                },
                Capture = new
                {
                    capture.Width,
                    capture.Height,
                    ExpectedFrames = expectedFrames,
                    capture.FramesWritten,
                    capture.FramesCaptured,
                    capture.FramesDropped
                },
                Audio = new
                {
                    ExpectedSamples = expectedSamples,
                    audio.SamplesWritten,
                    MaxMicPeak = maxMic,
                    MaxSystemPeak = maxSystem
                },
                Performance = new
                {
                    PeakCombinedCpuPercent = peakAppCpu,
                    PeakAppMemoryMiB = peakAppMemory / 1024.0 / 1024.0,
                    PeakFfmpegMemoryMiB = peakFfmpegMemory / 1024.0 / 1024.0
                },
                Outputs = new
                {
                    Mkv = mkvPath,
                    Mp4 = mp4Path,
                    MixedWav = outputBase + ".mixed.wav",
                    FfmpegLog = mkvPath + ".ffmpeg.log",
                    Probe = probePath
                },
                Probe = ParseProbe(probeJson)
            };

            var resultsPath = outputBase + ".results.json";
            File.WriteAllText(resultsPath, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));

            Console.WriteLine();
            Console.WriteLine($"MP4:      {mp4Path}");
            Console.WriteLine($"Probe:    {probePath}");
            Console.WriteLine($"Results:  {resultsPath}");
            Console.WriteLine($"Video:    {capture.FramesWritten}/{expectedFrames} frames, dropped={capture.FramesDropped}");
            Console.WriteLine($"Audio:    {audio.SamplesWritten}/{expectedSamples} samples, max mic={maxMic:0.000}, max system={maxSystem:0.000}");
            Console.WriteLine($"Peak CPU: {peakAppCpu:0.0}% (app + ffmpeg, normalized across {Environment.ProcessorCount} cores)");
            Console.WriteLine($"Peak mem: app={peakAppMemory / 1024.0 / 1024.0:0.0} MiB, ffmpeg={peakFfmpegMemory / 1024.0 / 1024.0:0.0} MiB");

            if (maxMic < 0.001f)
            {
                Console.WriteLine("WARNING: the microphone produced no measurable signal.");
            }

            if (maxSystem < 0.001f)
            {
                Console.WriteLine("WARNING: system audio produced no measurable signal.");
            }

            return 0;
        }
        finally
        {
            patternHost?.Dispose();
        }
    }

    private static int RunAnalyze(string[] args)
    {
        var input = GetArg(args, "--input") ??
                    throw new ArgumentException("analyze requires --input <recording.mp4>.");
        input = Path.GetFullPath(input);
        if (!File.Exists(input))
        {
            throw new FileNotFoundException($"Recording was not found at {input}.");
        }

        var syncInterval = GetDouble(args, "--sync-interval", 60);
        var ffmpeg = FindFfmpeg(GetArg(args, "--ffmpeg"));
        var ffprobe = FindFfprobe(ffmpeg);
        var probeJson = RunFfprobe(ffprobe, input);
        var duration = GetProbeDuration(probeJson);
        var markers = new List<double>();
        for (var marker = 1.0; marker < duration - 0.5; marker += syncInterval)
        {
            markers.Add(marker);
        }

        Console.WriteLine($"Analyzing {input}");
        Console.WriteLine($"Duration: {duration:0.000}s, markers: {markers.Count}, interval: {syncInterval:0}s");
        Console.WriteLine();
        Console.WriteLine(" marker   video flash      audio beep       offset");
        Console.WriteLine("---------------------------------------------------");

        var results = new List<SyncMarkerAnalysis>();
        foreach (var marker in markers)
        {
            var flash = DetectFlash(ffmpeg, input, marker);
            var beep = DetectBeep(ffmpeg, input, marker);
            double? offsetMs = flash.HasValue && beep.HasValue
                ? (beep.Value - flash.Value) * 1000
                : null;

            results.Add(new SyncMarkerAnalysis(marker, flash, beep, offsetMs));

            Console.WriteLine(
                $"{marker,7:0.0}s  " +
                $"{(flash.HasValue ? $"{flash.Value,8:0.000}s" : "   ----  ")}  " +
                $"{(beep.HasValue ? $"{beep.Value,8:0.000}s" : "   ----  ")}  " +
                $"{(offsetMs.HasValue ? $"{offsetMs.Value,8:+0.0;-0.0;0.0}ms" : "     ----")}");
        }

        var valid = results.Where(r => r.FlashSeconds.HasValue && r.BeepSeconds.HasValue).ToArray();
        double? meanOffset = null;
        double? driftPerMinute = null;
        if (valid.Length > 0)
        {
            var medianOffset = Median(valid.Select(r => r.OffsetMs!.Value).ToArray());
            var filtered = valid
                .Where(r => Math.Abs(r.OffsetMs!.Value - medianOffset) < 200.0)
                .ToArray();

            meanOffset = filtered.Length > 0
                ? filtered.Average(r => r.OffsetMs!.Value)
                : valid.Average(r => r.OffsetMs!.Value);

            if (filtered.Length > 1)
            {
                var meanMarker = filtered.Average(r => r.MarkerSeconds);
                var mean = filtered.Average(r => r.OffsetMs!.Value);
                var covariance = filtered.Sum(r =>
                    (r.MarkerSeconds - meanMarker) * (r.OffsetMs!.Value - mean));
                var variance = filtered.Sum(r =>
                    Math.Pow(r.MarkerSeconds - meanMarker, 2));
                driftPerMinute = variance > 0
                    ? covariance / variance * 60.0
                    : 0;
            }
        }

        Console.WriteLine();
        if (meanOffset.HasValue)
        {
            Console.WriteLine($"Mean A/V offset: {meanOffset.Value:+0.0;-0.0;0.0} ms (positive = audio later)");
        }

        if (driftPerMinute.HasValue)
        {
            Console.WriteLine($"Drift:           {driftPerMinute.Value:+0.0;-0.0;0.0} ms/minute");
        }

        if (valid.Length < results.Count)
        {
            Console.WriteLine($"WARNING: only {valid.Length}/{results.Count} markers were detected.");
        }

        var outputPath = Path.ChangeExtension(input, ".sync.json");
        File.WriteAllText(
            outputPath,
            JsonSerializer.Serialize(new
            {
                Input = input,
                DurationSeconds = duration,
                SyncIntervalSeconds = syncInterval,
                MeanOffsetMs = meanOffset,
                DriftMsPerMinute = driftPerMinute,
                Markers = results
            }, new JsonSerializerOptions { WriteIndented = true }));

        Console.WriteLine($"Analysis:       {outputPath}");
        return 0;
    }

    private static double? DetectFlash(string ffmpeg, string input, double marker)
    {
        const double windowBefore = 2.0;
        const double windowAfter = 2.0;
        var seek = Math.Max(0, marker - windowBefore);
        var length = marker + windowAfter - seek;

        var output = RunProcessCapture(
            ffmpeg,
            new[]
            {
                "-hide_banner", "-loglevel", "info",
                "-ss", seek.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                "-t", length.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                "-i", input,
                "-vf", "signalstats,metadata=print",
                "-an", "-f", "null", "-"
            });

        var samples = ParseMetadataSamples(output, "lavfi.signalstats.YAVG=", seek);
        if (samples.Count == 0)
        {
            return null;
        }

        var baselineSamples = samples
            .Where(s => s.Time < marker - 0.2)
            .Select(s => s.Value)
            .ToArray();
        var baseline = baselineSamples.Length > 0 ? Median(baselineSamples) : 16.0;
        var threshold = baseline + 0.5;

        return samples
            .Where(s => s.Time >= marker - 0.2 && s.Time <= marker + 1.5 && s.Value > threshold)
            .Select(s => (double?)s.Time)
            .FirstOrDefault();
    }

    private static double? DetectBeep(string ffmpeg, string input, double marker)
    {
        const double windowBefore = 2.0;
        const double windowAfter = 2.0;
        var seek = Math.Max(0, marker - windowBefore);
        var length = marker + windowAfter - seek;

        var output = RunProcessCapture(
            ffmpeg,
            new[]
            {
                "-hide_banner", "-loglevel", "info",
                "-ss", seek.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                "-t", length.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                "-i", input,
                "-af", "highpass=f=3800,lowpass=f=4200,astats=metadata=1:reset=1," +
                       "ametadata=print:key=lavfi.astats.Overall.RMS_level",
                "-vn", "-f", "null", "-"
            });

        var samples = ParseMetadataSamples(output, "lavfi.astats.Overall.RMS_level=", seek);
        if (samples.Count == 0)
        {
            return null;
        }

        var baselineSamples = samples
            .Where(s => s.Time < marker - 0.2)
            .Select(s => s.Value)
            .ToArray();
        var baseline = baselineSamples.Length > 0 ? Median(baselineSamples) : -70.0;

        var candidates = samples
            .Where(s => s.Time >= marker - 0.2 && s.Time <= marker + 1.5)
            .ToArray();
        if (candidates.Length < 2)
        {
            return null;
        }

        var peak = candidates.Max(s => s.Value);
        var threshold = Math.Max(baseline + 12.0, peak - 10.0);

        for (var i = 0; i < candidates.Length - 1; i++)
        {
            if (candidates[i].Value > threshold && candidates[i + 1].Value > threshold)
            {
                return candidates[i].Time;
            }
        }

        return null;
    }

    private static List<(double Time, double Value)> ParseMetadataSamples(
        string output,
        string valueKey,
        double seekOffset)
    {
        var samples = new List<(double Time, double Value)>();
        double? pts = null;

        foreach (var line in output.Split('\n'))
        {
            var framePts = ParseNumberAfter(line, "pts_time:");
            if (framePts.HasValue)
            {
                pts = framePts;
                continue;
            }

            var value = ParseNumberAfter(line, valueKey);
            if (value.HasValue && pts.HasValue)
            {
                samples.Add((seekOffset + pts.Value, value.Value));
                pts = null;
            }
        }

        return samples;
    }

    private static double? ParseNumberAfter(string line, string key)
    {
        var index = line.IndexOf(key, StringComparison.Ordinal);
        if (index < 0)
        {
            return null;
        }

        var start = index + key.Length;
        var end = start;
        while (end < line.Length &&
               (char.IsDigit(line[end]) || line[end] is '.' or '-' or '+' or 'e' or 'E'))
        {
            end++;
        }

        return double.TryParse(
            line[start..end],
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;
    }

    private static double Median(double[] values)
    {
        Array.Sort(values);
        var middle = values.Length / 2;
        return values.Length % 2 == 0
            ? (values[middle - 1] + values[middle]) / 2.0
            : values[middle];
    }

    private static double GetProbeDuration(string probeJson)
    {
        using var document = JsonDocument.Parse(probeJson);
        var duration = document.RootElement.GetProperty("format").GetProperty("duration").GetString();
        return double.TryParse(
            duration,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var seconds)
            ? seconds
            : throw new InvalidOperationException("The recording does not report a duration.");
    }

    private static string RunProcessCapture(string fileName, IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(stdoutTask, stderrTask);
        return stderrTask.Result + Environment.NewLine + stdoutTask.Result;
    }

    private sealed record SyncMarkerAnalysis(
        double MarkerSeconds,
        double? FlashSeconds,
        double? BeepSeconds,
        double? OffsetMs);

    private static MMDevice ResolveMic(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return AudioDeviceCatalog.GetDefault(DataFlow.Capture, Role.Communications);
        }

        return AudioDeviceCatalog.Find(DataFlow.Capture, query);
    }

    private static MMDevice ResolveSystem(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return AudioDeviceCatalog.GetDefault(DataFlow.Render, Role.Console);
        }

        return AudioDeviceCatalog.Find(DataFlow.Render, query);
    }

    private static void PlayWav(string path)
    {
        Console.WriteLine("Playing mixed audio...");
        using var reader = new AudioFileReader(path);
        using var output = new WaveOutEvent();
        output.Init(reader);
        output.Play();
        while (output.PlaybackState == PlaybackState.Playing)
        {
            Thread.Sleep(100);
        }
    }

    private static string FindFfmpeg(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var fullPath = Path.GetFullPath(explicitPath);
            if (File.Exists(fullPath))
            {
                return fullPath;
            }

            throw new FileNotFoundException($"FFmpeg was not found at {fullPath}.");
        }

        var environmentPath = Environment.GetEnvironmentVariable("JEILO_FFMPEG");
        if (!string.IsNullOrWhiteSpace(environmentPath) && File.Exists(environmentPath))
        {
            return Path.GetFullPath(environmentPath);
        }

        foreach (var root in CandidateRoots())
        {
            var toolsDirectory = Path.Combine(root, "tools", "ffmpeg");
            if (!Directory.Exists(toolsDirectory))
            {
                continue;
            }

            var found = Directory.GetFiles(toolsDirectory, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (found is not null)
            {
                return found;
            }
        }

        var onPath = FindOnPath("ffmpeg.exe");
        if (onPath is not null)
        {
            return onPath;
        }

        throw new FileNotFoundException(
            "FFmpeg was not found. Put it under tools/ffmpeg, add it to PATH, set JEILO_FFMPEG, or pass --ffmpeg.");
    }

    private static string FindFfprobe(string ffmpegPath)
    {
        var sibling = Path.Combine(Path.GetDirectoryName(ffmpegPath)!, "ffprobe.exe");
        if (File.Exists(sibling))
        {
            return sibling;
        }

        var onPath = FindOnPath("ffprobe.exe");
        if (onPath is not null)
        {
            return onPath;
        }

        throw new FileNotFoundException("ffprobe.exe was not found next to FFmpeg or on PATH.");
    }

    private static string? FindOnPath(string fileName)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // Ignore malformed PATH entries.
            }
        }

        return null;
    }

    private static IEnumerable<string> CandidateRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (seen.Add(directory.FullName))
                {
                    yield return directory.FullName;
                }

                directory = directory.Parent;
            }
        }
    }

    private static string RunFfprobe(string ffprobePath, string inputPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffprobePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in new[]
                 {
                     "-v", "error",
                     "-show_entries",
                     "format=duration,size,bit_rate:stream=index,codec_name,codec_type,width,height,r_frame_rate,avg_frame_rate,duration,nb_frames,sample_rate,channels",
                     "-of", "json",
                     inputPath
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start ffprobe.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffprobe failed with exit code {process.ExitCode}:{Environment.NewLine}{error}");
        }

        return output;
    }

    private static object? ParseProbe(string probeJson)
    {
        try
        {
            using var document = JsonDocument.Parse(probeJson);
            var streams = document.RootElement.GetProperty("streams")
                .EnumerateArray()
                .Select(stream => new
                {
                    Type = stream.TryGetProperty("codec_type", out var type) ? type.GetString() : null,
                    Codec = stream.TryGetProperty("codec_name", out var codec) ? codec.GetString() : null,
                    Width = stream.TryGetProperty("width", out var width) ? width.GetInt32() : (int?)null,
                    Height = stream.TryGetProperty("height", out var height) ? height.GetInt32() : (int?)null,
                    Duration = stream.TryGetProperty("duration", out var duration) ? duration.GetString() : null,
                    Frames = stream.TryGetProperty("nb_frames", out var frames) ? frames.GetString() : null,
                    SampleRate = stream.TryGetProperty("sample_rate", out var sampleRate) ? sampleRate.GetString() : null,
                    Channels = stream.TryGetProperty("channels", out var channels) ? channels.GetInt32() : (int?)null
                })
                .ToArray();

            var format = document.RootElement.GetProperty("format");
            return new
            {
                Duration = format.TryGetProperty("duration", out var duration) ? duration.GetString() : null,
                Size = format.TryGetProperty("size", out var size) ? size.GetString() : null,
                BitRate = format.TryGetProperty("bit_rate", out var bitRate) ? bitRate.GetString() : null,
                Streams = streams
            };
        }
        catch
        {
            return null;
        }
    }

    private static string Bar(float value)
    {
        var width = (int)Math.Round(Math.Clamp(value, 0f, 1f) * 10);
        return new string('#', width).PadRight(10, '.');
    }

    private static string Timestamp() => DateTime.Now.ToString("yyyyMMdd-HHmmss");

    private static bool HasFlag(string[] args, string name) =>
        args.Any(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));

    private static string? GetArg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static int GetInt(string[] args, string name, int fallback)
    {
        var value = GetArg(args, name);
        return int.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private static double GetDouble(string[] args, string name, double fallback)
    {
        var value = GetArg(args, name);
        return double.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'.");
        PrintHelp();
        return 2;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            Jeilo Recorder Phase 0 spike

            Commands:
              devices
                  List WASAPI capture/render endpoints and monitors.

              audio-test [--seconds 10] [--mic H340] [--system <name>] [--play]
                  Verify simultaneous microphone + system-audio capture and mixing.

              record [options]
                  Capture a test pattern window or the primary monitor.

                  --source window|monitor     Default: window
                  --duration 60               Seconds
                  --fps 30                    10-60
                  --encoder qsv|mf|openh264   Default: qsv
                  --mic H340                  Name/index substring, or default
                  --system <name>             Render endpoint, or default
                  --pattern                   Show a fullscreen pattern for monitor capture
                  --visible                   Put the test window on screen instead of mostly offscreen
                  --width 1920 --height 1080  Test window size
                  --sync-interval 30          Seconds between visual/audio sync markers
                  --output <path>             Output base path
                  --ffmpeg <path>             Explicit ffmpeg.exe

              analyze --input <recording.mp4> [--sync-interval 60]
                  Detect the visual flash and 4 kHz sync beep at each marker,
                  report A/V offset and drift, and write a .sync.json file.
            """);
    }
}

