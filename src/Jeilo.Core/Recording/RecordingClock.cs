using System.Diagnostics;

namespace Jeilo.Core.Recording;

public sealed class RecordingClock
{
    private readonly ManualResetEventSlim _started = new(false);
    private readonly Stopwatch _stopwatch = new();

    public bool IsStarted => _started.IsSet;

    public TimeSpan Elapsed => _stopwatch.Elapsed;

    public double SyncIntervalSeconds { get; set; } = 60.0;

    public void Start()
    {
        if (_started.IsSet)
        {
            return;
        }

        _stopwatch.Start();
        _started.Set();
    }

    public bool WaitForStart(TimeSpan timeout) => _started.Wait(timeout);

    public bool IsSyncMarkerActive(double markerLengthSeconds = 0.35)
    {
        if (!IsStarted)
        {
            return false;
        }

        var seconds = Elapsed.TotalSeconds - 1.0;
        if (seconds < 0)
        {
            return false;
        }

        return seconds % SyncIntervalSeconds < markerLengthSeconds;
    }
}
