using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Jeilo.Core.Encoding;
using Jeilo.Core.Recording;

namespace Jeilo.App.ViewModels;

public sealed record AudioDeviceItem(string Id, string Name, string Detail, bool IsDefault)
{
    public override string ToString() => Name;
}

public sealed record MonitorItem(int Index, string Name, string Resolution, string Detail, bool IsPrimary)
{
    public override string ToString() => Name;
}

public sealed record QualityOption(VideoQuality Value, string Name, string Detail)
{
    public override string ToString() => Name;
}

public sealed record CaptureSourceOption(CaptureTargetKind Kind, string Name, string Detail)
{
    public override string ToString() => Name;
}

public sealed record WindowItem(IntPtr Handle, string Name, string Detail)
{
    public override string ToString() => Name;
}

public sealed record EncoderOption(string Value, string Name, bool IsAvailable)
{
    public override string ToString() => Name;
}

public partial class RecordingItem : ObservableObject
{
    public RecordingItem(FileInfo file)
    {
        FileName = file.Name;
        Path = file.FullName;
        DateText = file.LastWriteTime.ToString("MMM d, yyyy  •  h:mm tt");
        SizeText = FormatSize(file.Length);
    }

    public string FileName { get; }

    public string Path { get; }

    public string DateText { get; }

    public string SizeText { get; }

    public string Meta => $"{DateText}  •  {SizeText}";

    [ObservableProperty]
    private string _durationText = string.Empty;

    [ObservableProperty]
    private bool _hasDuration;

    [ObservableProperty]
    private Bitmap? _thumbnail;

    public void ApplyMetadata(TimeSpan duration, string? thumbnailPath)
    {
        if (duration > TimeSpan.Zero)
        {
            DurationText = FormatDuration(duration);
            HasDuration = true;
        }

        if (string.IsNullOrWhiteSpace(thumbnailPath) || !File.Exists(thumbnailPath))
        {
            return;
        }

        try
        {
            Thumbnail = new Bitmap(thumbnailPath);
        }
        catch
        {
            Thumbnail = null;
        }
    }

    private static string FormatDuration(TimeSpan duration)
    {
        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes}:{duration.Seconds:00}";
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024L * 1024L)
        {
            return $"{bytes / 1024.0 / 1024.0 / 1024.0:0.0} GB";
        }

        if (bytes >= 1024L * 1024L)
        {
            return $"{bytes / 1024.0 / 1024.0:0.0} MB";
        }

        return $"{Math.Max(1, bytes / 1024.0):0} KB";
    }
}
