using Jeilo.Core.Encoding;

namespace Jeilo.Core.Settings;

public sealed class AppSettings
{
    public string OutputDirectory { get; set; } = DefaultOutputDirectory();

    public bool MicrophoneEnabled { get; set; } = true;

    public string? MicrophoneId { get; set; }

    public string? SystemAudioId { get; set; }

    public bool SystemAudioEnabled { get; set; } = true;

    public int MonitorIndex { get; set; }

    public int Fps { get; set; } = 30;

    public VideoQuality Quality { get; set; } = VideoQuality.High;

    public string Encoder { get; set; } = "auto";

    public bool IncludeCursor { get; set; } = true;

    public float MicrophoneGain { get; set; } = 1.0f;

    public float SystemAudioGain { get; set; } = 1.0f;

    public string ThemeMode { get; set; } = "Dark";

    public int CountdownSeconds { get; set; } = 3;

    public bool MinimizeOnRecord { get; set; } = false;

    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(OutputDirectory))
        {
            OutputDirectory = DefaultOutputDirectory();
        }

        if (ThemeMode != "Dark" && ThemeMode != "Light" && ThemeMode != "System")
        {
            ThemeMode = "Dark";
        }

        CountdownSeconds = Math.Clamp(CountdownSeconds, 0, 10);
        Fps = Math.Clamp(Fps, 10, 60);
        MicrophoneGain = Math.Clamp(MicrophoneGain, 0.0f, 2.0f);
        SystemAudioGain = Math.Clamp(SystemAudioGain, 0.0f, 2.0f);
    }

    public static string DefaultOutputDirectory()
    {
        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (string.IsNullOrWhiteSpace(videos))
        {
            videos = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        return Path.Combine(videos, "Jeilo Recorder");
    }
}
