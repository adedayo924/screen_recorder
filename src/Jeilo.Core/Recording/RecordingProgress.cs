namespace Jeilo.Core.Recording;

public enum RecordingState
{
    Idle,
    Preparing,
    Recording,
    Finalizing,
    Completed,
    Failed
}

public sealed record RecordingProgress(
    RecordingState State,
    TimeSpan Elapsed,
    long FramesWritten,
    long FramesDropped,
    long AudioSamplesWritten,
    float MicrophonePeak,
    float SystemAudioPeak,
    double CpuPercent,
    long MemoryBytes,
    string OutputPath,
    string? Message = null);

public sealed record RecordingResult(
    string FilePath,
    TimeSpan Duration,
    long FileSizeBytes,
    long FramesWritten,
    long FramesDropped,
    DateTimeOffset CompletedAt);
