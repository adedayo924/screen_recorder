using System.Runtime.InteropServices;

namespace Jeilo.Core.Capture;

public sealed record MonitorInfo(
    IntPtr Handle,
    string DeviceName,
    RECT Bounds,
    RECT WorkArea,
    bool IsPrimary,
    uint Dpi);

public static class MonitorCatalog
{
    public static IReadOnlyList<MonitorInfo> Enumerate()
    {
        var monitors = new List<MonitorInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMonitor, _, _, _) =>
        {
            var info = new MONITORINFOEX
            {
                cbSize = Marshal.SizeOf<MONITORINFOEX>()
            };

            if (GetMonitorInfo(hMonitor, ref info))
            {
                var dpi = 96u;
                try
                {
                    if (GetDpiForMonitor(hMonitor, MonitorDpiType.EffectiveDpi, out var dpiX, out _) == 0)
                    {
                        dpi = dpiX;
                    }
                }
                catch
                {
                    // Keep the 96 DPI fallback if Shcore is unavailable.
                }

                monitors.Add(new MonitorInfo(
                    hMonitor,
                    info.szDevice,
                    info.rcMonitor,
                    info.rcWork,
                    (info.dwFlags & 1) != 0,
                    dpi));
            }

            return true;
        }, IntPtr.Zero);

        return monitors;
    }

    public static MonitorInfo GetPrimary()
    {
        return Enumerate().FirstOrDefault(m => m.IsPrimary)
            ?? throw new InvalidOperationException("No primary monitor was found.");
    }

    public static void Print(TextWriter writer)
    {
        var monitors = Enumerate();
        for (var i = 0; i < monitors.Count; i++)
        {
            var monitor = monitors[i];
            var primary = monitor.IsPrimary ? " primary" : string.Empty;
            writer.WriteLine(
                $"  [{i}] {monitor.DeviceName}{primary} " +
                $"{monitor.Bounds.Width}x{monitor.Bounds.Height} " +
                $"at ({monitor.Bounds.Left},{monitor.Bounds.Top}) dpi={monitor.Dpi}");
        }
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(
        IntPtr hdc,
        IntPtr lprcClip,
        MonitorEnumProc lpfnEnum,
        IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, MonitorDpiType dpiType, out uint dpiX, out uint dpiY);

    private enum MonitorDpiType
    {
        EffectiveDpi = 0
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public readonly int Width => Right - Left;
    public readonly int Height => Bottom - Top;
}
