using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;
using Forms = System.Windows.Forms;

namespace PicLaundry;

internal sealed partial class Shelf : Window
{
    private readonly Canvas cards = new() { Height = 150 };
    private readonly Dictionary<Guid, (string Path, BitmapSource Image)> thumbnails = new();
    private readonly Dictionary<Guid, Border> imageCards = new();
    private readonly ScrollViewer scroll;
    private readonly Path rope;
    private readonly TextBlock empty;
    private readonly Action<Entry> copy;
    private readonly Action<Guid> remove;
    private HwndSource? source;
    private readonly HwndSourceHook windowHook = WindowMessage;

    internal Shelf(Action<Entry> copy, Action<Guid> remove, Action<Guid, double> move,
        Func<DependencyObject, object, DragDropEffects, DragDropEffects>? runDrag = null)
    {
        this.copy = copy;
        this.remove = remove;
        this.move = move;
        this.runDrag = runDrag ?? DragDrop.DoDragDrop;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Focusable = false;
        Height = 159;
        Width = 600;

        var layout = new Grid { Background = null, ClipToBounds = true };
        rope = new Path { Stroke = Brush("#BA9567"), StrokeThickness = 3, IsHitTestVisible = false };
        layout.Children.Add(rope);
        scroll = new ScrollViewer
        {
            Margin = new Thickness(18, 8, 18, 0),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            PanningMode = PanningMode.HorizontalOnly,
            Content = cards
        };
        scroll.PreviewMouseWheel += (_, e) =>
        {
            scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset - e.Delta * 0.65);
            e.Handled = true;
        };
        layout.Children.Add(scroll);
        scroll.SizeChanged += (_, _) => cards.MinWidth = scroll.ActualWidth;
        empty = new TextBlock
        {
            Text = "还没有内容",
            Foreground = Brushes.White,
            Background = Brush("#25312D"),
            Padding = new Thickness(14, 7, 14, 7),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        layout.Children.Add(empty);
        InitializeClicks();
        InitializeDrag(layout);
        InitializeMotion();
        Content = layout;
        SourceInitialized += (_, _) =>
        {
            source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            source?.AddHook(windowHook);
        };
        Closed += (_, _) => { if (source is { IsDisposed: false }) source.RemoveHook(windowHook); };
    }

    internal void Open(Forms.Screen screen, double scale, bool desktop = false)
    {
        var bounds = screen.Bounds;
        Topmost = !desktop;
        nint layer = desktop ? 1 : -1; // HWND_BOTTOM or HWND_TOPMOST.
        nint hwnd = new WindowInteropHelper(this).EnsureHandle();
        int height = (int)Math.Ceiling(Height * scale);
        // Position in physical pixels to avoid mixing coordinates across monitors.
        Native.SetWindowPos(hwnd, layer, bounds.Left, bounds.Top, bounds.Width, height, 0x0010);
        Show();
        Native.SetWindowPos(hwnd, layer, bounds.Left, bounds.Top, bounds.Width, height, 0x0010);
    }

    internal void Render(IReadOnlyList<Entry> entries)
    {
        entries = entries.Where(entry => entry.IsImage).ToArray();
        if (Dragging || MenuOpen)
        {
            deferredEntries = entries;
            return;
        }
        deferredEntries = null;
        if (pendingClick is { } pending && !entries.Any(e => e.Id == pending.Id)) CancelClick();
        var previous = renderedEntries;
        renderedEntries = entries;
        StopMotion();
        FinishInterruptedClick();
        holdTimer.Stop();
        armed = null;
        double offset = scroll.HorizontalOffset;
        imageCards.Clear();
        cards.Children.Clear();
        double extent = 0;
        var live = new HashSet<Guid>();
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            live.Add(entry.Id);
            var card = Card(entry, i);
            double x = History.Position(entry, i);
            Canvas.SetLeft(card, x);
            Panel.SetZIndex(card, entry.Layer);
            cards.Children.Add(card);
            extent = Math.Max(extent, x + History.CardWidth + 7);
        }
        cards.Width = extent;
        foreach (var id in new List<Guid>(thumbnails.Keys))
            if (!live.Contains(id)) thumbnails.Remove(id);
        empty.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        scroll.ScrollToHorizontalOffset(offset);
        UpdateRope();
        var oldLayers = new Dictionary<Guid, int>();
        foreach (var entry in previous) oldLayers[entry.Id] = entry.Layer;
        foreach (var entry in entries)
        {
            if (!entry.IsImage || !imageCards.TryGetValue(entry.Id, out var body)
                || body.Child is not Image) continue;
            if (!oldLayers.TryGetValue(entry.Id, out int layer) || layer != entry.Layer)
            {
                Nudge(entry.Id);
                break;
            }
        }
        ScheduleIdle();
    }

    internal void RefreshAvailability(IReadOnlyList<Entry> entries)
    {
        foreach (var entry in entries)
        {
            if (entry.Path is not string path || thumbnails.ContainsKey(entry.Id) == File.Exists(path)) continue;
            Render(entries);
            return;
        }
    }

    private UIElement Card(Entry entry, int index)
    {
        var hanging = new Grid { Tag = entry.Id, Width = History.CardWidth, Height = 135, Margin = new Thickness(0, 1, 0, 0) };
        hanging.RenderTransform = new TranslateTransform();

        var clip = new Clothespin();
        var body = new Border
        {
            Tag = entry,
            Margin = new Thickness(0, 16, 0, 5), Padding = new Thickness(7),
            Background = Brush("#F8F2E7"), CornerRadius = new CornerRadius(4),
            BorderBrush = Brush("#D8CBB8"), BorderThickness = new Thickness(1)
        };
        double gripX = History.CardWidth / 2;
        var transforms = new TransformGroup();
        transforms.Children.Add(new ScaleTransform(1, 1, gripX, 0));
        transforms.Children.Add(new RotateTransform(new[] { -2.3, 1.6, -0.8, 2.2 }[index % 4], gripX, 0));
        transforms.Children.Add(new RotateTransform(0, gripX, 0));
        body.RenderTransform = transforms;

        string path = entry.Path!;
        var image = Thumbnail(entry.Id, path);
        body.Child = image != null ? new Image { Source = image, Stretch = Stretch.Uniform }
            : new TextBlock { Text = "原文件不可用", VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        body.ToolTip = image == null ? "原文件不可用" : "单击复制 · 双击看原图 · 拖动移动";
        imageCards.Add(entry.Id, body);
        body.MouseEnter += (_, _) => SetHovered(body, true);
        body.MouseLeave += (_, _) => SetHovered(body, false);
        if (image != null)
        {
            body.Cursor = Cursors.Hand;
            body.MouseLeftButtonDown += (_, e) => ArmDrag(entry.Id, body, clip, image, e.GetPosition(this));
        }
        var menu = new ContextMenu();
        var delete = new MenuItem { Header = "移除" };
        delete.Click += (_, _) =>
        {
            menu.IsOpen = false;
            MenuOpen = false;
            remove(entry.Id);
            FlushDeferredRender();
        };
        menu.Items.Add(delete);
        body.ContextMenu = menu;
        menu.Opened += (_, _) => MenuOpen = true;
        menu.Closed += (_, _) =>
        {
            MenuOpen = false;
            FlushDeferredRender();
        };
        hanging.Children.Add(body);
        hanging.Children.Add(clip);
        return hanging;
    }

    internal bool MenuOpen { get; private set; }

    private static void SetHovered(Border card, bool hovered)
    {
        card.BorderBrush = Brush(hovered ? "#FFC24D" : "#D8CBB8");
        var scale = (ScaleTransform)((TransformGroup)card.RenderTransform).Children[0];
        double target = hovered ? 1.12 : 1;
        var duration = TimeSpan.FromMilliseconds(120);
        var x = new DoubleAnimation(scale.ScaleX, target, duration) { FillBehavior = FillBehavior.Stop };
        var y = new DoubleAnimation(scale.ScaleY, target, duration) { FillBehavior = FillBehavior.Stop };
        scale.ScaleX = scale.ScaleY = target;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, x);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, y);
    }

    private BitmapSource? Thumbnail(Guid id, string path)
    {
        if (!File.Exists(path))
        {
            thumbnails.Remove(id);
            return null;
        }
        if (thumbnails.TryGetValue(id, out var cached) && cached.Path == path) return cached.Image;
        try
        {
            var image = History.Decode(path, 260);
            thumbnails[id] = (path, image);
            return image;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or
            ArgumentException or System.Runtime.InteropServices.COMException) { return null; }
    }

    private static nint WindowMessage(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != Native.MouseActivate) return 0;
        handled = true;
        return Native.NoActivate;
    }

    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
}

