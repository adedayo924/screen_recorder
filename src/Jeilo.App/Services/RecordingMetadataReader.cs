using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Jeilo.App.Services;

internal sealed record RecordingMetadata(TimeSpan Duration, string? ThumbnailPath);

internal static partial class RecordingMetadataReader
{
    private static readonly Regex DurationPattern = new(
        @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static async Task<RecordingMetadata?> ReadAsync(
        string ffmpegPath,
        string filePath,
        string cacheDirectory,
        CancellationToken cancellationToken)
    {
        var duration = await ReadDurationAsync(ffmpegPath, filePath, cancellationToken)
            .ConfigureAwait(false);

        if (duration is null)
        {
            return null;
        }

        var thumbnailPath = await EnsureThumbnailAsync(
                ffmpegPath,
                filePath,
                duration.Value,
                cacheDirectory,
                cancellationToken)
            .ConfigureAwait(false);

        return new RecordingMetadata(duration.Value, thumbnailPath);
    }

    private static async Task<TimeSpan?> ReadDurationAsync(
        string ffmpegPath,
        string filePath,
        CancellationToken cancellationToken)
    {
        var result = await RunAsync(
                ffmpegPath,
                new[] { "-hide_banner", "-nostdin", "-i", filePath },
                cancellationToken)
            .ConfigureAwait(false);

        if (result is null)
        {
            return null;
        }

        var match = DurationPattern.Match(result.Value.StandardError);
        if (!match.Success)
        {
            return null;
        }

        var hours = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var minutes = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var seconds = double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        return TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
    }

    private static async Task<string?> EnsureThumbnailAsync(
        string ffmpegPath,
        string filePath,
        TimeSpan duration,
        string cacheDirectory,
        CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(cacheDirectory);

            var file = new FileInfo(filePath);
            var key = $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24];
            var thumbnailPath = Path.Combine(cacheDirectory, hash + ".jpg");

            if (File.Exists(thumbnailPath) && new FileInfo(thumbnailPath).Length > 0)
            {
                return thumbnailPath;
            }

            var seek = Math.Clamp(duration.TotalSeconds * 0.1, 0.2, 3.0);
            var args = new[]
            {
                "-hide_banner",
                "-loglevel", "error",
                "-nostdin",
                "-ss", seek.ToString("0.###", CultureInfo.InvariantCulture),
                "-i", filePath,
                "-frames:v", "1",
                "-vf", "scale=640:-2",
                "-q:v", "5",
                "-y",
                thumbnailPath
            };

            var result = await RunAsync(ffmpegPath, args, cancellationToken).ConfigureAwait(false);
            if (result is null || result.Value.ExitCode != 0 || !File.Exists(thumbnailPath))
            {
                return null;
            }

            return new FileInfo(thumbnailPath).Length > 0 ? thumbnailPath : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<(int ExitCode, string StandardError)?> RunAsync(
        string ffmpegPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return null;
        }

        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // The process may already have exited.
            }

            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return null;
        }

        var standardError = await errorTask.ConfigureAwait(false);
        return (process.ExitCode, standardError);
    }
}
