using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

namespace Jeilo.Core.Encoding;

public sealed class FfmpegMuxer : IDisposable
{
    private readonly string _ffmpegPath;
    private readonly string _outputMkvPath;
    private readonly int _width;
    private readonly int _height;
    private readonly int _fps;
    private readonly string _encoder;
    private readonly VideoQuality _quality;
    private readonly string _logPath;
    private readonly List<string> _recentLog = new();
    private readonly object _logLock = new();

    private Process? _process;
    private NamedPipeServerStream? _videoPipe;
    private NamedPipeServerStream? _audioPipe;
    private bool _completed;
    private bool _disposed;

    public FfmpegMuxer(
        string ffmpegPath,
        string outputMkvPath,
        int width,
        int height,
        int fps,
        string encoder,
        VideoQuality quality = VideoQuality.High)
    {
        _ffmpegPath = ffmpegPath;
        _outputMkvPath = outputMkvPath;
        _width = width;
        _height = height;
        _fps = fps;
        _encoder = encoder;
        _quality = quality;
        _logPath = outputMkvPath + ".ffmpeg.log";
    }

    public Stream VideoStream => _videoPipe ?? throw new InvalidOperationException("FFmpeg is not connected.");

    public Stream AudioStream => _audioPipe ?? throw new InvalidOperationException("FFmpeg is not connected.");

    public int ProcessId => _process?.Id ?? 0;

    public long WorkingSetBytes
    {
        get
        {
            try
            {
                using var process = Process.GetProcessById(ProcessId);
                return process.WorkingSet64;
            }
            catch
            {
                return 0;
            }
        }
    }

    public TimeSpan TotalProcessorTime
    {
        get
        {
            try
            {
                using var process = Process.GetProcessById(ProcessId);
                return process.TotalProcessorTime;
            }
            catch
            {
                return TimeSpan.Zero;
            }
        }
    }

    public void Start()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_outputMkvPath)!);

        var id = Guid.NewGuid().ToString("N");
        var videoPipeName = $"jeilo_video_{id}";
        var audioPipeName = $"jeilo_audio_{id}";

        _videoPipe = CreatePipe(videoPipeName);
        _audioPipe = CreatePipe(audioPipeName);

        var arguments = BuildArguments(videoPipeName, audioPipeName);
        File.WriteAllText(
            _logPath,
            $"ffmpeg {string.Join(' ', arguments.Select(QuoteArgument))}{Environment.NewLine}");

        var startInfo = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(_outputMkvPath)!
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _process.ErrorDataReceived += (_, e) => AppendLog(e.Data);
        _process.OutputDataReceived += (_, e) => AppendLog(e.Data);
        _process.Start();
        _process.BeginErrorReadLine();
        _process.BeginOutputReadLine();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            _videoPipe.WaitForConnectionAsync(timeout.Token).GetAwaiter().GetResult();

            // FFmpeg probes the rawvideo input before it opens the second input.
            // Feed it one frame so it can move on to the audio pipe.
            var primingFrame = new byte[checked(_width * _height * 4)];
            _videoPipe.Write(primingFrame, 0, primingFrame.Length);
            _videoPipe.Flush();

            _audioPipe.WaitForConnectionAsync(timeout.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            var earlyExit = _process.HasExited ? $" FFmpeg exit code: {_process.ExitCode}." : string.Empty;
            throw new InvalidOperationException(
                $"Timed out waiting for FFmpeg to open its input pipes.{earlyExit}{Environment.NewLine}{GetLogTail()}");
        }

        if (_process.HasExited)
        {
            throw new InvalidOperationException(
                $"FFmpeg exited before recording started (exit code {_process.ExitCode}).{Environment.NewLine}{GetLogTail()}");
        }
    }

    public string Complete()
    {
        if (_completed)
        {
            return _outputMkvPath;
        }

        _completed = true;

        try
        {
            _videoPipe?.Dispose();
            _audioPipe?.Dispose();
            _videoPipe = null;
            _audioPipe = null;

            if (_process is null)
            {
                throw new InvalidOperationException("FFmpeg was not started.");
            }

            if (!_process.WaitForExit(TimeSpan.FromMinutes(2)))
            {
                _process.Kill(entireProcessTree: true);
                throw new TimeoutException($"FFmpeg did not finish within two minutes.{Environment.NewLine}{GetLogTail()}");
            }

            if (_process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"FFmpeg failed with exit code {_process.ExitCode}.{Environment.NewLine}{GetLogTail()}");
            }
        }
        finally
        {
            _process?.Dispose();
            _process = null;
        }

        var mp4Path = Path.ChangeExtension(_outputMkvPath, ".mp4");
        var remux = RunRemux(mp4Path);
        if (remux.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Remux to MP4 failed (exit code {remux.ExitCode}). The crash-safe MKV is still available at {_outputMkvPath}.{Environment.NewLine}{remux.Output}");
        }

        return mp4Path;
    }

    public void Abort()
    {
        try
        {
            _videoPipe?.Dispose();
            _audioPipe?.Dispose();
        }
        catch
        {
            // Best effort.
        }

        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort.
        }

        _process?.Dispose();
        _process = null;
    }

    private NamedPipeServerStream CreatePipe(string name)
    {
        return new NamedPipeServerStream(
            name,
            PipeDirection.Out,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            1024 * 1024,
            4 * 1024 * 1024);
    }

    private List<string> BuildArguments(string videoPipeName, string audioPipeName)
    {
        var arguments = new List<string>
        {
            "-hide_banner",
            "-nostdin",
            "-loglevel", "warning",
            "-y",
            "-f", "rawvideo",
            "-pix_fmt", "bgra",
            "-video_size", $"{_width}x{_height}",
            "-framerate", _fps.ToString(),
            "-i", $@"\\.\pipe\{videoPipeName}",
            "-f", "s16le",
            "-ar", "48000",
            "-ac", "2",
            "-i", $@"\\.\pipe\{audioPipeName}",
            "-map", "0:v:0",
            "-map", "1:a:0"
        };

        arguments.AddRange(GetEncoderArguments(_encoder));

        arguments.AddRange(new[]
        {
            "-c:a", "aac",
            "-b:a", "192k",
            "-f", "matroska",
            _outputMkvPath
        });

        return arguments;
    }

    private static string QuoteArgument(string argument) =>
        argument.Contains(' ') ? $"\"{argument}\"" : argument;

    private IEnumerable<string> GetEncoderArguments(string encoder)
    {
        return encoder.ToLowerInvariant() switch
        {
            "qsv" => new[]
            {
                "-c:v", "h264_qsv",
                "-preset", _quality >= VideoQuality.High ? "faster" : "veryfast",
                "-global_quality", _quality switch
                {
                    VideoQuality.Low => "30",
                    VideoQuality.Medium => "27",
                    VideoQuality.Ultra => "21",
                    _ => "24"
                },
                "-look_ahead", "0"
            },
            "mf" => new[]
            {
                "-c:v", "h264_mf",
                "-quality", _quality switch
                {
                    VideoQuality.Low => "55",
                    VideoQuality.Medium => "65",
                    VideoQuality.Ultra => "85",
                    _ => "75"
                },
                "-rate_control", "quality"
            },
            "openh264" => new[]
            {
                "-c:v", "libopenh264",
                "-b:v", _quality switch
                {
                    VideoQuality.Low => "4M",
                    VideoQuality.Medium => "6M",
                    VideoQuality.Ultra => "12M",
                    _ => "8M"
                },
                "-g", "60"
            },
            _ => throw new ArgumentException($"Unknown encoder '{encoder}'. Use qsv, mf, or openh264.")
        };
    }

    private (int ExitCode, string Output) RunRemux(string mp4Path)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        foreach (var argument in new[]
                 {
                     "-hide_banner", "-nostdin", "-loglevel", "warning", "-y",
                     "-i", _outputMkvPath,
                     "-c", "copy",
                     "-movflags", "+faststart",
                     mp4Path
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start FFmpeg remux.");
        var output = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }

    private void AppendLog(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        lock (_logLock)
        {
            _recentLog.Add(line);
            if (_recentLog.Count > 200)
            {
                _recentLog.RemoveAt(0);
            }
        }

        try
        {
            File.AppendAllText(_logPath, line + Environment.NewLine);
        }
        catch
        {
            // Logging must never break recording.
        }
    }

    private string GetLogTail()
    {
        lock (_logLock)
        {
            return string.Join(Environment.NewLine, _recentLog.TakeLast(40));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Abort();
    }
}
