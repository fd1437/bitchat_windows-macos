using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Shell;

namespace BitChat.Windows;

internal static class WindowChromeWorkAreaHook
{
    private const int WmGetMinMaxInfo=0x0024;
    private const uint MonitorDefaultToNearest=2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd,uint flags);
    [DllImport("user32.dll",CharSet=CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor,ref MonitorInfo info);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd,out NativeRect rect);

    internal static void Attach(Window window)
    {
        var hwnd=new WindowInteropHelper(window).Handle;
        HwndSource.FromHwnd(hwnd)?.AddHook(WindowHook);
    }

    private static IntPtr WindowHook(IntPtr hwnd,int message,IntPtr wParam,IntPtr lParam,ref bool handled)
    {
        if(message!=WmGetMinMaxInfo||lParam==IntPtr.Zero)return IntPtr.Zero;
        IntPtr monitor=MonitorFromWindow(hwnd,MonitorDefaultToNearest);
        if(monitor==IntPtr.Zero)return IntPtr.Zero;
        var info=new MonitorInfo{Size=Marshal.SizeOf<MonitorInfo>()};
        if(!GetMonitorInfo(monitor,ref info))return IntPtr.Zero;
        var limits=Marshal.PtrToStructure<MinMaxInfo>(lParam);
        limits.MaxPosition=new(){X=info.Work.Left-info.Monitor.Left,Y=info.Work.Top-info.Monitor.Top};
        limits.MaxSize=new(){X=info.Work.Right-info.Work.Left,Y=info.Work.Bottom-info.Work.Top};
        Marshal.StructureToPtr(limits,lParam,false);
        handled=true;
        return IntPtr.Zero;
    }

    internal static bool IsWithinMonitorWorkArea(IntPtr hwnd,int tolerance=8)
    {
        IntPtr monitor=MonitorFromWindow(hwnd,MonitorDefaultToNearest);
        if(monitor==IntPtr.Zero)return false;
        var info=new MonitorInfo{Size=Marshal.SizeOf<MonitorInfo>()};
        if(!GetMonitorInfo(monitor,ref info)||!GetWindowRect(hwnd,out var window))return false;
        return window.Left>=info.Work.Left-tolerance&&window.Top>=info.Work.Top-tolerance&&
            window.Right<=info.Work.Right+tolerance&&window.Bottom<=info.Work.Bottom+tolerance;
    }
}
