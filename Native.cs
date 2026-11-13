using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Input;
using Forms = System.Windows.Forms;

namespace PicLaundry;

internal static class Native
{
    internal const int ClipboardUpdate = 0x031D;
    internal const int HotKeyMessage = 0x0312;
    internal const int MouseActivate = 0x0021;
    internal const int NoActivate = 3;
    internal const string ExcludedClipboard = "ExcludeClipboardContentFromMonitorProcessing";
    internal const string HistoryClipboard = "CanIncludeInClipboardHistory";
    internal const uint NoRepeat = 0x4000;

    [DllImport("user32.dll")] internal static extern bool AddClipboardFormatListener(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool RemoveClipboardFormatListener(nint hwnd);
    [DllImport("user32.dll")] internal static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string format);
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] private static extern bool OpenClipboard(nint window);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern nint GetClipboardData(uint format);
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll")] private static extern nuint GlobalSize(nint memory);
    [DllImport("user32.dll")] internal static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetDesktopWindow();
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out Rect rect, int size);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(Point point, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
    [DllImport("shell32.dll")] private static extern int SHGetKnownFolderPath(in Guid id, uint flags, nint token, out nint path);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    internal static string ScreenshotsFolder()
    {
        var id = new Guid("b7bede81-df94-4682-a7d8-57a52620b86f");
        var result = SHGetKnownFolderPath(in id, 0x4000, 0, out nint path);
        try
        {
            return result == 0 && Marshal.PtrToStringUni(path) is string folder
                ? folder : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
        }
        finally { Marshal.FreeCoTaskMem(path); }
    }

    internal static bool ClipboardIsExcluded(nint window)
    {
        if (IsClipboardFormatAvailable(RegisterClipboardFormat(ExcludedClipboard))) return true;
        uint format = RegisterClipboardFormat(HistoryClipboard);
        if (!IsClipboardFormatAvailable(format)) return false;
        if (!OpenClipboard(window)) throw new ExternalException("Clipboard is busy.");
        try
        {
            nint memory = GetClipboardData(format);
            if (memory == 0 || GlobalSize(memory) < 4) return true;
            nint value = GlobalLock(memory);
            if (value == 0) return true;
            try { return Marshal.ReadInt32(value) == 0; }
            finally { GlobalUnlock(memory); }
        }
        finally { CloseClipboard(); }
    }

    internal static double ScaleFor(Point point) =>
        GetDpiForMonitor(MonitorFromPoint(point, 2), 0, out uint x, out _) == 0 ? x / 96.0 : 1;

    internal static bool IsFullscreen(Forms.Screen screen)
    {
        nint window = GetForegroundWindow();
        if (window == 0 || window == GetDesktopWindow() || window == GetShellWindow()) return false;
        if (DwmGetWindowAttribute(window, 9, out Rect r, Marshal.SizeOf<Rect>()) != 0
            && !GetWindowRect(window, out r)) return false;
        var b = screen.Bounds;
        return r.Left <= b.Left && r.Top <= b.Top && r.Right >= b.Right && r.Bottom >= b.Bottom;
    }

    internal static uint HotKeyModifiers(ModifierKeys keys)
    {
        uint result = 0;
        if (keys.HasFlag(ModifierKeys.Alt)) result |= 1;
        if (keys.HasFlag(ModifierKeys.Control)) result |= 2;
        if (keys.HasFlag(ModifierKeys.Shift)) result |= 4;
        if (keys.HasFlag(ModifierKeys.Windows)) result |= 8;
        return result;
    }
}
