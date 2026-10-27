using Jeilo.Core.Encoding;

namespace Jeilo.Core.Recording;

public enum CaptureTargetKind
{
    Monitor,
    Window
}

public sealed record RecordingOptions
{
    public string OutputDirectory { get; init; } = Settings.AppSettings.DefaultOutputDirectory();

    public string? FileName { get; init; }

    public CaptureTargetKind TargetKind { get; init; } = CaptureTargetKind.Monitor;

    public int MonitorIndex { get; init; }

    public IntPtr WindowHandle { get; init; }

    public int Fps { get; init; } = 30;

    public VideoQuality Quality { get; init; } = VideoQuality.High;

    public string Encoder { get; init; } = "auto";

    public bool MicrophoneEnabled { get; init; } = true;

    public string? MicrophoneId { get; init; }

    public float MicrophoneGain { get; init; } = 1.0f;

    public bool SystemAudioEnabled { get; init; } = true;

    public string? SystemAudioId { get; init; }

    public float SystemAudioGain { get; init; } = 1.0f;

    public bool IncludeCursor { get; init; } = true;

    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromHours(4);
}
