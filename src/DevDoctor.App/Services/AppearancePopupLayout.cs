using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace DevDoctor.App.Services;

public static class AppearancePopupLayout
{
    public static Rect WorkAreaFor(Window window)
    {
        var origin = window.PointToScreen(new Point());
        var dpi = VisualTreeHelper.GetDpi(window);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (GetMonitorInfo(MonitorFromWindow(new WindowInteropHelper(window).Handle, 2), ref info))
            return new((info.Work.Left - origin.X) / dpi.DpiScaleX, (info.Work.Top - origin.Y) / dpi.DpiScaleY,
                (info.Work.Right - info.Work.Left) / dpi.DpiScaleX, (info.Work.Bottom - info.Work.Top) / dpi.DpiScaleY);
        return new(0, 0, window.ActualWidth, window.ActualHeight);
    }

    public static (double Width, double Height, Rect Clip) Measure(Rect window, Rect workArea, Rect anchor, double scale)
    {
        var clip = Rect.Intersect(window, workArea);
        if (clip.IsEmpty) clip = window;
        scale = double.IsFinite(scale) && scale > 0 ? scale : 1;
        var below = Math.Max(60, clip.Bottom - anchor.Bottom - 20);
        var above = Math.Max(60, anchor.Top - clip.Top - 20);
        return (Math.Min(480, Math.Max(80, clip.Width - 24) / scale), Math.Min(690, Math.Max(below, above) / scale), clip);
    }

    public static Point Position(Rect clip, Rect anchor, Size popup)
    {
        var left = Math.Max(clip.Left + 12, Math.Min(anchor.Right - popup.Width, clip.Right - popup.Width - 12));
        var below = clip.Bottom - anchor.Bottom;
        var above = anchor.Top - clip.Top;
        var top = below >= popup.Height + 12 || below >= above ? anchor.Bottom + 6 : anchor.Top - popup.Height - 6;
        top = Math.Max(clip.Top + 8, Math.Min(top, clip.Bottom - popup.Height - 8));
        return new(left - anchor.Left, top - anchor.Top);
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}
