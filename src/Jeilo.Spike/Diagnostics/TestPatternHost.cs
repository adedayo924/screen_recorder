using System.Drawing;
using System.Windows.Forms;
using Jeilo.Core.Recording;

namespace Jeilo.Spike.Diagnostics;

public sealed class TestPatternHost : IDisposable
{
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private TestPatternForm? _form;
    private bool _disposed;

    private TestPatternHost(RecordingClock clock, TimeSpan duration, Rectangle bounds, bool topMost)
    {
        _thread = new Thread(() =>
        {
            _form = new TestPatternForm(clock, duration, bounds)
            {
                TopMost = topMost
            };
            _form.Shown += (_, _) =>
            {
                Handle = _form.Handle;
                _ready.Set();
            };
            Application.Run(_form);
        })
        {
            IsBackground = true,
            Name = "Jeilo test pattern"
        };

        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        if (!_ready.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new TimeoutException("The test pattern window did not appear within 10 seconds.");
        }
    }

    public IntPtr Handle { get; private set; }

    public static TestPatternHost Start(
        RecordingClock clock,
        TimeSpan duration,
        Rectangle bounds,
        bool topMost = false)
    {
        return new TestPatternHost(clock, duration, bounds, topMost);
    }

    public void Close()
    {
        var form = _form;
        if (form is null || form.IsDisposed)
        {
            return;
        }

        try
        {
            form.BeginInvoke(new Action(form.Close));
        }
        catch
        {
            // The window may already be closing.
        }

        _thread.Join(TimeSpan.FromSeconds(5));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Close();
        _ready.Dispose();
    }
}
