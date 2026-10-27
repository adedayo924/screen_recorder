using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Jeilo.Core.Capture;

public sealed record WindowInfo(
    IntPtr Handle,
    string Title,
    string ProcessName,
    string ClassName,
    RECT Bounds);

public static class WindowCatalog
{
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080L;
    private const int DwmwaCloaked = 14;

    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd"
    };

    public static IReadOnlyList<WindowInfo> Enumerate(int excludedProcessId = 0)
    {
        var windows = new List<WindowInfo>();
        var ownProcessId = excludedProcessId > 0 ? excludedProcessId : Environment.ProcessId;

        EnumWindows((handle, _) =>
        {
            if (!IsCapturable(handle))
            {
                return true;
            }

            GetWindowThreadProcessId(handle, out var processId);
            if (processId == 0 || processId == ownProcessId)
            {
                return true;
            }

            var titleLength = GetWindowTextLength(handle);
            if (titleLength <= 0)
            {
                return true;
            }

            var title = new StringBuilder(titleLength + 1);
            if (GetWindowText(handle, title, title.Capacity) <= 0)
            {
                return true;
            }

            var className = GetClassName(handle);
            if (ShellClasses.Contains(className))
            {
                return true;
            }

            GetWindowRect(handle, out var bounds);
            if (bounds.Width < 64 || bounds.Height < 64)
            {
                return true;
            }

            var processName = "Unknown";
            try
            {
                using var process = Process.GetProcessById((int)processId);
                processName = process.ProcessName;
            }
            catch
            {
                // The process may have exited while enumerating.
            }

            windows.Add(new WindowInfo(handle, title.ToString(), processName, className, bounds));
            return true;
        }, IntPtr.Zero);

        return windows
            .OrderBy(window => window.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(window => window.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static bool IsAvailable(IntPtr handle)
    {
        return handle != IntPtr.Zero && IsWindow(handle) && IsCapturable(handle);
    }

    public static void Print(TextWriter writer)
    {
        var windows = Enumerate();
        for (var i = 0; i < windows.Count; i++)
        {
            var window = windows[i];
            writer.WriteLine(
                $"  [{i}] {window.ProcessName}: {window.Title} " +
                $"{window.Bounds.Width}x{window.Bounds.Height}");
        }
    }

    private static bool IsCapturable(IntPtr handle)
    {
        if (!IsWindowVisible(handle) || IsIconic(handle))
        {
            return false;
        }

        var exStyle = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        if ((exStyle & WsExToolWindow) != 0)
        {
            return false;
        }

        try
        {
            if (DwmGetWindowAttribute(handle, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 &&
                cloaked != 0)
            {
                return false;
            }
        }
        catch
        {
            // Older Windows versions may not expose DWM cloaking.
        }

        return true;
    }

    private static string GetClassName(IntPtr handle)
    {
        var className = new StringBuilder(256);
        return GetClassName(handle, className, className.Capacity) > 0
            ? className.ToString()
            : string.Empty;
    }

    private static IntPtr GetWindowLongPtr(IntPtr handle, int index)
    {
        return IntPtr.Size == 8
            ? GetWindowLongPtr64(handle, index)
            : new IntPtr(GetWindowLong32(handle, index));
    }

    private delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr handle, StringBuilder text, int maximum);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr handle, StringBuilder className, int maximum);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr handle, out RECT bounds);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr handle, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr handle, int index);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        IntPtr handle,
        int attribute,
        out int value,
        int valueSize);
}
