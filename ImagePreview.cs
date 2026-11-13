using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace PicLaundry;

internal sealed class ImagePreview : Window
{
    private readonly Canvas viewport = new() { ClipToBounds = true, Background = Brushes.Transparent };
    private readonly Image picture;
    private readonly BitmapSource original;
    private double zoom;
    private Point origin;
    private Point? lastPointer;
    private bool adjusted;

    internal ImagePreview(BitmapSource original, string name)
    {
        this.original = original;
        Title = name + " — PicLaundry 原图";
        WindowStartupLocation = WindowStartupLocation.Manual;
        var pointer = Forms.Cursor.Position;
        var area = Forms.Screen.FromPoint(pointer).WorkingArea;
        double dpi = Native.ScaleFor(pointer);
        Width = Math.Min(1100, area.Width / dpi * .9);
        Height = Math.Min(800, area.Height / dpi * .9);
        SourceInitialized += (_, _) => Native.SetWindowPos(
            new System.Windows.Interop.WindowInteropHelper(this).Handle, 0,
            area.Left + (area.Width - (int)(Width * dpi)) / 2,
            area.Top + (area.Height - (int)(Height * dpi)) / 2,
            (int)(Width * dpi), (int)(Height * dpi), 0x0014);
        Background = new SolidColorBrush(Color.FromRgb(27, 29, 32));
        var layout = new DockPanel();
        var hint = new TextBlock
        {
            Text = "滚轮缩放 · 按住左键平移 · Esc 关闭",
            Foreground = Brushes.White, Margin = new Thickness(12, 7, 12, 7)
        };
        DockPanel.SetDock(hint, Dock.Bottom);
        layout.Children.Add(hint);
        layout.Children.Add(viewport);
        Content = layout;

        picture = new Image { Source = original, Stretch = Stretch.Fill, IsHitTestVisible = false };
        RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
        viewport.Children.Add(picture);
        viewport.SizeChanged += (_, _) =>
        {
            if (!adjusted) Fit();
            else Draw();
        };
        viewport.MouseWheel += (_, e) =>
        {
            ZoomAt(e.GetPosition(viewport), e.Delta);
            e.Handled = true;
        };
        viewport.MouseLeftButtonDown += (_, e) =>
        {
            if (!viewport.CaptureMouse()) return;
            lastPointer = e.GetPosition(viewport);
            e.Handled = true;
        };
        viewport.MouseMove += (_, e) =>
        {
            if (lastPointer is not Point previous || e.LeftButton != MouseButtonState.Pressed) return;
            Point next = e.GetPosition(viewport);
            origin += next - previous;
            lastPointer = next;
            adjusted = true;
            Draw();
        };
        viewport.MouseLeftButtonUp += (_, _) => viewport.ReleaseMouseCapture();
        viewport.LostMouseCapture += (_, _) => lastPointer = null;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close();
        };
    }

    private double FitZoom() => Math.Min(1, Math.Min(viewport.ActualWidth / original.PixelWidth,
        viewport.ActualHeight / original.PixelHeight));

    private void Fit()
    {
        zoom = FitZoom();
        origin = new Point((viewport.ActualWidth - original.PixelWidth * zoom) / 2,
            (viewport.ActualHeight - original.PixelHeight * zoom) / 2);
        Draw();
    }

    private void ZoomAt(Point pointer, int wheelDelta)
    {
        if (zoom <= 0 || wheelDelta == 0) return;
        double next = Math.Clamp(zoom * Math.Pow(1.2, wheelDelta / 120.0), FitZoom() / 2, 8);
        origin = pointer + (origin - pointer) * (next / zoom);
        zoom = next;
        adjusted = true;
        Draw();
    }

    private void Draw()
    {
        picture.Width = original.PixelWidth * zoom;
        picture.Height = original.PixelHeight * zoom;
        Canvas.SetLeft(picture, origin.X);
        Canvas.SetTop(picture, origin.Y);
    }
}
