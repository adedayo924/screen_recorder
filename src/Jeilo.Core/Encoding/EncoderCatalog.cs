using System.Collections.Concurrent;
using System.Diagnostics;

namespace Jeilo.Core.Encoding;

public sealed record EncoderChoice(string Value, string DisplayName, bool IsAvailable);

public static class EncoderCatalog
{
    private static readonly ConcurrentDictionary<string, HashSet<string>> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<EncoderChoice> Discover(string ffmpegPath)
    {
        var available = GetAvailableEncoders(ffmpegPath);

        return new[]
        {
            new EncoderChoice("auto", "Automatic", true),
            new EncoderChoice("qsv", "Intel Quick Sync", available.Contains("h264_qsv")),
            new EncoderChoice("mf", "Windows Media Foundation", available.Contains("h264_mf")),
            new EncoderChoice("openh264", "OpenH264 (software)", available.Contains("libopenh264"))
        };
    }

    public static string Resolve(string ffmpegPath, string requested)
    {
        var normalized = string.IsNullOrWhiteSpace(requested) ? "auto" : requested.Trim().ToLowerInvariant();
        var available = GetAvailableEncoders(ffmpegPath);

        if (normalized != "auto")
        {
            var codec = ToCodec(normalized);
            if (available.Contains(codec))
            {
                return normalized;
            }
        }

        if (available.Contains("h264_qsv"))
        {
            return "qsv";
        }

        if (available.Contains("h264_mf"))
        {
            return "mf";
        }

        if (available.Contains("libopenh264"))
        {
            return "openh264";
        }

        throw new InvalidOperationException(
            "This FFmpeg build does not provide a supported H.264 encoder (h264_qsv, h264_mf, or libopenh264).");
    }

    public static string? FindFfprobe(string ffmpegPath)
    {
        var directory = Path.GetDirectoryName(ffmpegPath);
        if (directory is null)
        {
            return null;
        }

        var ffprobe = Path.Combine(
            directory,
            OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");

        return File.Exists(ffprobe) ? ffprobe : null;
    }

    private static string ToCodec(string encoder) => encoder switch
    {
        "qsv" => "h264_qsv",
        "mf" => "h264_mf",
        "openh264" => "libopenh264",
        _ => encoder
    };

    private static HashSet<string> GetAvailableEncoders(string ffmpegPath)
    {
        return Cache.GetOrAdd(ffmpegPath, path =>
        {
            var output = RunProcess(
                path,
                new[] { "-hide_banner", "-encoders" });

            var codecs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[0].StartsWith('V'))
                {
                    codecs.Add(parts[1]);
                }
            }

            return codecs;
        });
    }

    private static string RunProcess(string fileName, IEnumerable<string> arguments)
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

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {fileName}.");

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return output + Environment.NewLine + error;
    }
}
