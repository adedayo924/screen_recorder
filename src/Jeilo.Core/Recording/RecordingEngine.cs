using System.Diagnostics;
using Jeilo.Core.Audio;
using Jeilo.Core.Capture;
using Jeilo.Core.Encoding;
using NAudio.CoreAudioApi;
using Windows.Graphics.Capture;

namespace Jeilo.Core.Recording;

public sealed class RecordingEngine : IAsyncDisposable
{
    private readonly string? _ffmpegPath;
    private RecordingClock _clock = new();
    private readonly object _stateLock = new();

    private RecordingOptions? _options;
    private WgcCapture? _capture;
    private AudioMixRecorder? _audio;
    private FfmpegMuxer? _muxer;
    private MMDevice? _microphoneDevice;
    private MMDevice? _renderDevice;
    private Process? _appProcess;
    private Task? _monitorTask;
    private TaskCompletionSource<RecordingResult>? _completion;
    private Stopwatch _statsStopwatch = new();
    private TimeSpan _lastProcessorTime;
    private string? _outputBasePath;
    private string? _mkvPath;
    private string? _mp4Path;
    private volatile bool _stopRequested;
    private bool _disposed;

    public RecordingEngine(string? ffmpegPath = null)
    {
        _ffmpegPath = FfmpegLocator.Find(ffmpegPath);
    }

    public RecordingState State { get; private set; } = RecordingState.Idle;

    public string? LastOutputPath => _mp4Path;

    public bool IsRecording => State is RecordingState.Preparing or RecordingState.Recording or RecordingState.Finalizing;

    public string? FfmpegPath => _ffmpegPath;

    public event Action<RecordingProgress>? ProgressChanged;

    public event Action<RecordingResult>? Completed;

    public event Action<Exception>? Failed;

    public async Task StartAsync(RecordingOptions options)
    {
        lock (_stateLock)
        {
            if (IsRecording)
            {
                throw new InvalidOperationException("A recording is already in progress.");
            }

            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(RecordingEngine));
            }
        }

        if (_ffmpegPath is null)
        {
            throw new FileNotFoundException(
                "FFmpeg was not found. Place ffmpeg.exe next to the app or in the tools folder.");
        }

        _clock = new RecordingClock();
        _options = options;
        _stopRequested = false;
        _completion = new TaskCompletionSource<RecordingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        State = RecordingState.Preparing;
        ReportProgress(null, "Getting the recorder ready...");

        try
        {
            await Task.Run(Prepare).ConfigureAwait(false);
        }
        catch
        {
            AbortAfterFailure();
            State = RecordingState.Failed;
            ReportProgress(null, "Could not start the recording.");
            throw;
        }

        State = RecordingState.Recording;
        _clock.Start();
        _statsStopwatch = Stopwatch.StartNew();
        _appProcess = Process.GetCurrentProcess();
        _lastProcessorTime = _appProcess.TotalProcessorTime;
        _monitorTask = Task.Run(MonitorAsync);
    }

    public async Task<RecordingResult?> StopAsync()
    {
        _stopRequested = true;
        var completion = _completion;
        if (completion is null)
        {
            return null;
        }

        return await completion.Task.ConfigureAwait(false);
    }

    private void Prepare()
    {
        var options = _options ?? throw new InvalidOperationException("Recording options were not set.");

        Directory.CreateDirectory(options.OutputDirectory);

        var fileName = string.IsNullOrWhiteSpace(options.FileName)
            ? $"Jeilo {DateTime.Now:yyyy-MM-dd HH-mm-ss}"
            : Path.GetFileNameWithoutExtension(options.FileName);
        _outputBasePath = MakeUniqueBasePath(options.OutputDirectory, fileName);
        _mkvPath = _outputBasePath + ".mkv";
        _mp4Path = _outputBasePath + ".mp4";

        ReportProgress(null, "Starting screen capture...");

        var item = CreateCaptureItem(options);
        _capture = new WgcCapture(item, options.Fps, options.IncludeCursor);
        _capture.Start();

        if (!_capture.WaitForFirstFrame(TimeSpan.FromSeconds(15)))
        {
            throw new TimeoutException("Windows screen capture did not deliver a frame within 15 seconds.");
        }

        if (!options.MicrophoneEnabled && !options.SystemAudioEnabled)
        {
            throw new InvalidOperationException("Enable the microphone or system audio before recording.");
        }

        _microphoneDevice = options.MicrophoneEnabled
            ? ResolveDevice(DataFlow.Capture, Role.Communications, options.MicrophoneId, "microphone")
            : null;
        _renderDevice = options.SystemAudioEnabled
            ? ResolveDevice(DataFlow.Render, Role.Console, options.SystemAudioId, "system audio")
            : null;

        var encoder = EncoderCatalog.Resolve(_ffmpegPath!, options.Encoder);
        ReportProgress(null, "Starting the encoder...");

        _muxer = new FfmpegMuxer(
            _ffmpegPath!,
            _mkvPath,
            _capture.Width,
            _capture.Height,
            options.Fps,
            encoder,
            options.Quality);
        _muxer.Start();

        _audio = new AudioMixRecorder(
            _microphoneDevice,
            _renderDevice,
            _muxer.AudioStream,
            mixedWavPath: null,
            micGain: options.MicrophoneGain,
            systemGain: options.SystemAudioGain);
        _audio.StartCaptures();

        _audio.StartOutput(_clock, options.MaxDuration);
        _capture.StartOutput(_clock, _muxer.VideoStream, options.MaxDuration);
    }

    private async Task MonitorAsync()
    {
        try
        {
            var maxDuration = _options?.MaxDuration ?? TimeSpan.FromHours(4);
            var nextReport = TimeSpan.Zero;
            var micPeak = 0f;
            var systemPeak = 0f;
            var lastCpu = _lastProcessorTime;
            var lastWall = Stopwatch.StartNew();

            while (!_stopRequested && _clock.Elapsed < maxDuration)
            {
                await Task.Delay(100).ConfigureAwait(false);

                if (_audio is not null)
                {
                    var peaks = _audio.ConsumePeaks();
                    micPeak = Math.Max(micPeak, peaks.Mic);
                    systemPeak = Math.Max(systemPeak, peaks.System);
                }

                if (_clock.Elapsed < nextReport)
                {
                    continue;
                }

                nextReport = _clock.Elapsed + TimeSpan.FromMilliseconds(250);

                var cpuPercent = 0.0;
                var memory = 0L;
                if (_appProcess is not null)
                {
                    _appProcess.Refresh();
                    var elapsedWall = lastWall.Elapsed;
                    var processorDelta = _appProcess.TotalProcessorTime - lastCpu;
                    if (elapsedWall.TotalMilliseconds > 0)
                    {
                        cpuPercent = 100.0 * processorDelta.TotalMilliseconds /
                                     (elapsedWall.TotalMilliseconds * Environment.ProcessorCount);
                    }

                    lastCpu = _appProcess.TotalProcessorTime;
                    lastWall.Restart();
                    memory = _appProcess.WorkingSet64;
                }

                ReportProgress(
                    new RecordingProgress(
                        RecordingState.Recording,
                        _clock.Elapsed,
                        _capture?.FramesWritten ?? 0,
                        _capture?.FramesDropped ?? 0,
                        _audio?.SamplesWritten ?? 0,
                        micPeak,
                        systemPeak,
                        cpuPercent,
                        memory,
                        _mp4Path ?? string.Empty));

                micPeak = 0;
                systemPeak = 0;
            }

            var duration = _clock.Elapsed;
            State = RecordingState.Finalizing;
            ReportProgress(
                new RecordingProgress(
                    RecordingState.Finalizing,
                    duration,
                    _capture?.FramesWritten ?? 0,
                    _capture?.FramesDropped ?? 0,
                    _audio?.SamplesWritten ?? 0,
                    0,
                    0,
                    0,
                    _appProcess?.WorkingSet64 ?? 0,
                    _mp4Path ?? string.Empty,
                    "Finishing the video..."),
                "Finishing the video...");

            var result = await Task.Run(() => FinalizeRecording(duration)).ConfigureAwait(false);
            var audioSamples = _audio?.SamplesWritten ?? 0;
            var memoryAfter = _appProcess?.WorkingSet64 ?? 0;
            ReleaseResources();

            State = RecordingState.Completed;
            ReportProgress(
                new RecordingProgress(
                    RecordingState.Completed,
                    result.Duration,
                    result.FramesWritten,
                    result.FramesDropped,
                    audioSamples,
                    0,
                    0,
                    0,
                    memoryAfter,
                    result.FilePath,
                    "Recording saved."),
                "Recording saved.");

            Completed?.Invoke(result);
            _completion?.TrySetResult(result);
        }
        catch (Exception ex)
        {
            State = RecordingState.Failed;
            ReportProgress(
                new RecordingProgress(
                    RecordingState.Failed,
                    _clock.Elapsed,
                    _capture?.FramesWritten ?? 0,
                    _capture?.FramesDropped ?? 0,
                    _audio?.SamplesWritten ?? 0,
                    0,
                    0,
                    0,
                    _appProcess?.WorkingSet64 ?? 0,
                    _mp4Path ?? string.Empty,
                    ex.Message),
                ex.Message);

            AbortAfterFailure();
            Failed?.Invoke(ex);
            _completion?.TrySetException(ex);
        }
    }

    private RecordingResult FinalizeRecording(TimeSpan duration)
    {
        _capture?.Stop();
        _audio?.Stop();

        var mp4Path = _muxer?.Complete()
            ?? throw new InvalidOperationException("The encoder was not started.");

        if (_mkvPath is not null && File.Exists(_mkvPath))
        {
            File.Delete(_mkvPath);
        }

        if (_mkvPath is not null && File.Exists(_mkvPath + ".ffmpeg.log"))
        {
            File.Delete(_mkvPath + ".ffmpeg.log");
        }

        var file = new FileInfo(mp4Path);
        return new RecordingResult(
            mp4Path,
            duration,
            file.Exists ? file.Length : 0,
            _capture?.FramesWritten ?? 0,
            _capture?.FramesDropped ?? 0,
            DateTimeOffset.Now);
    }

    private GraphicsCaptureItem CreateCaptureItem(RecordingOptions options)
    {
        if (options.TargetKind == CaptureTargetKind.Window && options.WindowHandle != IntPtr.Zero)
        {
            return WgcInterop.CreateForWindow(options.WindowHandle);
        }

        var monitors = MonitorCatalog.Enumerate();
        if (monitors.Count == 0)
        {
            throw new InvalidOperationException("No monitor was found to record.");
        }

        var index = Math.Clamp(options.MonitorIndex, 0, monitors.Count - 1);
        return WgcInterop.CreateForMonitor(monitors[index].Handle);
    }

    private MMDevice ResolveDevice(DataFlow flow, Role defaultRole, string? deviceId, string label)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return AudioDeviceCatalog.GetDefault(flow, defaultRole);
        }

        try
        {
            return AudioDeviceCatalog.Find(flow, deviceId);
        }
        catch
        {
            // A saved device may have been unplugged. Fall back to the Windows default.
            return AudioDeviceCatalog.GetDefault(flow, defaultRole);
        }
    }

    private static string MakeUniqueBasePath(string directory, string fileName)
    {
        var basePath = Path.Combine(directory, fileName);
        if (!File.Exists(basePath + ".mp4") && !File.Exists(basePath + ".mkv"))
        {
            return basePath;
        }

        for (var suffix = 2; suffix < 1000; suffix++)
        {
            var candidate = Path.Combine(directory, $"{fileName} ({suffix})");
            if (!File.Exists(candidate + ".mp4") && !File.Exists(candidate + ".mkv"))
            {
                return candidate;
            }
        }

        return Path.Combine(directory, $"{fileName} {Guid.NewGuid():N}");
    }

    private void ReportProgress(RecordingProgress? progress, string? message = null)
    {
        progress ??= new RecordingProgress(
            State,
            _clock.Elapsed,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            _mp4Path ?? string.Empty,
            message);

        try
        {
            ProgressChanged?.Invoke(progress);
        }
        catch
        {
            // UI event handlers must never break the recorder.
        }
    }

    private void AbortAfterFailure()
    {
        try
        {
            _capture?.Stop();
        }
        catch
        {
            // Best effort.
        }

        try
        {
            _audio?.Stop();
        }
        catch
        {
            // Best effort.
        }

        try
        {
            _muxer?.Abort();
        }
        catch
        {
            // Best effort.
        }

        ReleaseResources();
    }

    private void ReleaseResources()
    {
        _audio?.Dispose();
        _audio = null;

        _capture?.Dispose();
        _capture = null;

        _muxer?.Dispose();
        _muxer = null;

        _microphoneDevice?.Dispose();
        _microphoneDevice = null;

        _renderDevice?.Dispose();
        _renderDevice = null;

        _appProcess?.Dispose();
        _appProcess = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (IsRecording)
        {
            try
            {
                await StopAsync().ConfigureAwait(false);
            }
            catch
            {
                // Shutdown is best effort.
            }
        }

        ReleaseResources();
    }
}
