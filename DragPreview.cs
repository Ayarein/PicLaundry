using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace PicLaundry;

internal sealed class DragPreview : Window
{
    private const int ExtendedStyle = -20;
    private const int Transparent = 0x20;
    private const int NoActivate = 0x08000000;
    private const int ToolWindow = 0x80;
    private readonly nint handle;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(nint hwnd, int index, int value);

    internal DragPreview(BitmapSource image)
    {
        Width = 142;
        Height = 109;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowActivated = false;
        ShowInTaskbar = false;
        Topmost = true;
        IsHitTestVisible = false;
        Content = new Image { Source = image, Stretch = Stretch.Uniform, Opacity = 0.9 };
        handle = new WindowInteropHelper(this).EnsureHandle();
        SetWindowLong(handle, ExtendedStyle, GetWindowLong(handle, ExtendedStyle) | Transparent | NoActivate | ToolWindow);
        FollowCursor();
        Show();
    }

    internal void FollowCursor()
    {
        var point = Forms.Cursor.Position;
        double scale = Native.ScaleFor(point);
        Native.SetWindowPos(handle, -1, point.X + 12, point.Y + 16,
            (int)Math.Ceiling(Width * scale), (int)Math.Ceiling(Height * scale), 0x0010);
    }
}
