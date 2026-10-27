namespace Jeilo.Core.Encoding;

public static class FfmpegLocator
{
    public static string? Find(string? preferredPath = null)
    {
        if (!string.IsNullOrWhiteSpace(preferredPath) && File.Exists(preferredPath))
        {
            return Path.GetFullPath(preferredPath);
        }

        var environmentPath = Environment.GetEnvironmentVariable("JEILO_FFMPEG");
        if (!string.IsNullOrWhiteSpace(environmentPath) && File.Exists(environmentPath))
        {
            return Path.GetFullPath(environmentPath);
        }

        var baseDirectory = AppContext.BaseDirectory;
        var candidates = new List<string>
        {
            Path.Combine(baseDirectory, "ffmpeg.exe"),
            Path.Combine(baseDirectory, "tools", "ffmpeg", "ffmpeg.exe")
        };

        var current = new DirectoryInfo(baseDirectory);
        for (var depth = 0; current is not null && depth < 8; depth++, current = current.Parent)
        {
            candidates.Add(Path.Combine(current.FullName, "ffmpeg.exe"));

            var toolsDirectory = Path.Combine(current.FullName, "tools", "ffmpeg");
            if (Directory.Exists(toolsDirectory))
            {
                candidates.AddRange(
                    Directory.EnumerateFiles(toolsDirectory, "ffmpeg.exe", SearchOption.AllDirectories));
            }
        }

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim(), "ffmpeg.exe");
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }
}
