using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using WinRT;

namespace Jeilo.Core.Capture;

public static class WgcInterop
{
    private static readonly Guid GraphicsCaptureItemGuid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    public static readonly Guid IidD3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    public static GraphicsCaptureItem CreateForMonitor(IntPtr hmonitor)
    {
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var itemPointer = interop.CreateForMonitor(hmonitor, GraphicsCaptureItemGuid);
        if (itemPointer == IntPtr.Zero)
        {
            throw new InvalidOperationException("Windows.Graphics.Capture returned a null item for the monitor.");
        }

        var item = GraphicsCaptureItem.FromAbi(itemPointer);
        Marshal.Release(itemPointer);
        return item;
    }

    public static GraphicsCaptureItem CreateForWindow(IntPtr hwnd)
    {
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var itemPointer = interop.CreateForWindow(hwnd, GraphicsCaptureItemGuid);
        if (itemPointer == IntPtr.Zero)
        {
            throw new InvalidOperationException("Windows.Graphics.Capture returned a null item for the window.");
        }

        var item = GraphicsCaptureItem.FromAbi(itemPointer);
        Marshal.Release(itemPointer);
        return item;
    }

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [ComVisible(true)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, in Guid iid);

        IntPtr CreateForMonitor([In] IntPtr monitor, in Guid iid);
    }

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [ComVisible(true)]
    public interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface(in Guid iid);
    }
}
