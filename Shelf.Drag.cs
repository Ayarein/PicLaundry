using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PicLaundry;

internal sealed partial class Shelf
{
    private const string ImageMoveFormat = "PicLaundry.ImageMove";
    private readonly Action<Guid, double> move;
    private readonly Func<DependencyObject, object, DragDropEffects, DragDropEffects> runDrag;
    private readonly DispatcherTimer holdTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly DispatcherTimer edgeTimer = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private readonly Canvas dragLayer = new() { IsHitTestVisible = false };
    private readonly Border marker = new()
    {
        Width = 12, Height = 23, Background = Brush("#FFC24D"), CornerRadius = new CornerRadius(2)
    };
    private DragPreview? preview;
    private System.Collections.Generic.IReadOnlyList<Entry>? deferredEntries;
    private (Guid Id, Border Card, Clothespin Clip, BitmapSource Image, Point PressPoint)? armed;
    private Guid? draggingId;
    private double dropX;
    private Point? dragPoint;
    private bool dropped;

    internal bool Dragging => draggingId.HasValue;

    private void InitializeDrag(Grid layout)
    {
        dragLayer.Visibility = Visibility.Collapsed;
        dragLayer.Children.Add(marker);
        layout.Children.Add(dragLayer);
        // Enable the drop target before the first drag enters the window.
        AllowDrop = true;
        PreviewMouseLeftButtonDown += (_, _) =>
        {
            FinishInterruptedClick();
            armed = null;
            holdTimer.Stop();
        };
        PreviewMouseLeftButtonUp += (_, e) =>
        {
            var press = armed;
            armed = null;
            holdTimer.Stop();
            if (Dragging || press is not { } gesture) return;
            if (gesture.Card.IsMouseOver && !MovedEnough(gesture.PressPoint, e.GetPosition(this)))
                ReleaseClick((Entry)gesture.Card.Tag);
            else if (secondPress) CancelClick();
            e.Handled = true;
        };
        PreviewMouseMove += (_, e) =>
        {
            if (Dragging || armed is not { } press || e.LeftButton != MouseButtonState.Pressed
                || !MovedEnough(press.PressPoint, e.GetPosition(this))) return;
            holdTimer.Stop();
            StartDrag(layout, press.Id, press.Card, press.Clip, press.Image);
        };
        holdTimer.Tick += (_, _) =>
        {
            holdTimer.Stop();
            if (armed is not { } press || Mouse.LeftButton != MouseButtonState.Pressed
                || !press.Card.IsMouseOver || !IsVisible || MenuOpen) return;
            StartDrag(layout, press.Id, press.Card, press.Clip, press.Image);
        };
        edgeTimer.Tick += (_, _) =>
        {
            preview?.FollowCursor();
            if (dragPoint is not Point point) return;
            double left = scroll.TranslatePoint(new Point(), layout).X;
            double right = left + scroll.ViewportWidth;
            double delta = point.X < left + 34 ? -17 : point.X > right - 34 ? 17 : 0;
            if (delta == 0) return;
            scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset + delta);
        };
        scroll.ScrollChanged += (_, _) =>
        {
            if (dragPoint is Point point) UpdateDropTarget(layout, point);
        };
        PreviewDragOver += (_, e) =>
        {
            if (!Dragging || !e.Data.GetDataPresent(ImageMoveFormat)) return;
            e.Effects = UpdateDropTarget(layout, e.GetPosition(layout)) ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        };
        DragLeave += (_, e) =>
        {
            if (!new Rect(layout.RenderSize).Contains(Mouse.GetPosition(layout))) ClearDropTarget();
        };
        PreviewDrop += (_, e) =>
        {
            if (!Dragging || !e.Data.GetDataPresent(ImageMoveFormat)) return;
            dropped = UpdateDropTarget(layout, e.GetPosition(layout));
            e.Effects = dropped ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) return;
            holdTimer.Stop();
            armed = null;
            FinishInterruptedClick();
        };
    }

    internal static bool MovedEnough(Point start, Point current) => (current - start).LengthSquared >= 25;

    private void ArmDrag(Guid id, Border card, Clothespin clip, BitmapSource image, Point pressPoint)
    {
        BeginClick(id);
        StopMotion();
        armed = (id, card, clip, image, pressPoint);
        holdTimer.Stop();
        holdTimer.Start();
    }

    private void StartDrag(Grid layout, Guid id, Border card, Clothespin clip, BitmapSource image)
    {
        StopMotion();
        armed = null;
        holdTimer.Stop();
        draggingId = id;
        var originalCard = (Grid)card.Parent;
        var entry = (Entry)card.Tag;
        double originalX = Canvas.GetLeft(originalCard);
        if (pendingClick?.Id == id) CancelClick();
        dropped = false;
        // A layered window passes zero-alpha pixels through to the desktop.
        layout.Background = Brush("#01000000");
        clip.Opacity = 0;
        card.Opacity = 0;
        dragLayer.Visibility = Visibility.Visible;
        UpdateDropTarget(layout, Mouse.GetPosition(layout));
        edgeTimer.Start();
        bool recycled = false;
        try
        {
            var data = new DataObject(ImageMoveFormat, id.ToString());
            var effects = DragDropEffects.Move;
            string? exportPath = null;
            if (entry.Path is string path && File.Exists(path))
            {
                try
                {
                    data.SetImage(History.Decode(path));
                    exportPath = CreateDropFile(path);
                    data.SetData(DataFormats.FileDrop, new[] { exportPath });
                    effects |= DragDropEffects.Copy;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException
                    or NotSupportedException or ArgumentException or System.Runtime.InteropServices.COMException)
                {
                    // The file can disappear while its thumbnail is still visible.
                }
            }
            preview = new DragPreview(image);
            preview.FollowCursor();
            var result = runDrag(card, data, effects);
            dropped = dropped && result == DragDropEffects.Move;
            // Explorer can return None for a completed deletion. The Shell's
            // matching recycle entry is authoritative, not the drag effect.
            if (!dropped && exportPath != null && !File.Exists(exportPath))
                recycled = RecycleBin.FindExport(exportPath) != null;
        }
        finally
        {
            edgeTimer.Stop();
            preview?.Close();
            preview = null;
            ClearDropTarget();
            draggingId = null;
            armed = null;
            layout.Background = null;
            dragLayer.Visibility = Visibility.Collapsed;
            card.Opacity = 1;
            clip.Opacity = 1;
            if (recycled) remove(id);
            else if (dropped)
            {
                // New captures may have shifted the whole layout while the view was frozen.
                double shift = 0;
                if (deferredEntries is { } latest)
                {
                    for (int i = 0; i < latest.Count; i++)
                        if (latest[i].Id == id) { shift = History.Position(latest[i], i) - originalX; break; }
                }
                move(id, Math.Max(0, dropX + shift));
            }
            FlushDeferredRender();
            ScheduleIdle();
            if (imageCards.TryGetValue(id, out var current) && current.Parent is Grid grid)
                ((Clothespin)grid.Children[1]).Animate(open: false);
        }
    }

    private static string CreateDropFile(string path)
    {
        // A receiver that chooses Move can only move the exported copy.
        var root = Path.Combine(Path.GetTempPath(), "PicLaundry-drag");
        Directory.CreateDirectory(root);
        foreach (var folder in Directory.EnumerateDirectories(root))
        {
            if (!Guid.TryParseExact(Path.GetFileName(folder), "N", out _)
                || Directory.GetLastWriteTimeUtc(folder) >= DateTime.UtcNow.AddDays(-1)) continue;
            try
            {
                var info = new DirectoryInfo(folder);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                foreach (var file in info.EnumerateFiles()) file.Delete();
                info.Delete();
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        var export = Directory.CreateDirectory(Path.Combine(root, Guid.NewGuid().ToString("N")));
        var filePath = Path.Combine(export.FullName, Path.GetFileName(path));
        File.Copy(path, filePath);
        return filePath;
    }

    private bool UpdateDropTarget(Grid layout, Point point)
    {
        if (!new Rect(layout.RenderSize).Contains(point))
        {
            ClearDropTarget();
            return false;
        }
        dragPoint = point;
        double left = scroll.TranslatePoint(new Point(), layout).X;
        double right = left + scroll.ViewportWidth;
        double x = Math.Clamp(point.X, left, right);
        double halfCard = History.CardWidth / 2;
        double requested = x - left + scroll.HorizontalOffset - halfCard;
        var occupied = cards.Children.OfType<Grid>().Where(card => (Guid)card.Tag != draggingId)
            .OrderBy(Panel.GetZIndex).Select(Canvas.GetLeft).ToArray();
        dropX = FindDropX(requested, occupied);
        double markerX = left + dropX - scroll.HorizontalOffset + halfCard;
        Canvas.SetLeft(marker, markerX - marker.Width / 2);
        Canvas.SetTop(marker, 9);
        marker.Visibility = Visibility.Visible;
        return true;
    }

    internal static double FindDropX(double requested, double[] occupied)
    {
        const double gripWidth = 24;
        requested = Math.Max(0, requested);
        // A card needs a contiguous exposed strip, even when several newer cards overlap it.
        return occupied.SelectMany(x => new[] { x - gripWidth, x + gripWidth,
            x - History.CardWidth - gripWidth, x + History.CardWidth + gripWidth,
            x - History.CardWidth + gripWidth, x + History.CardWidth - gripWidth })
            .Prepend(requested).Append(0)
            .Where(x => x >= 0 && occupied.Select((start, i) =>
                HasGrip(start, occupied.Skip(i + 1), gripWidth, x)).All(visible => visible))
            .DefaultIfEmpty(Math.Max(requested, occupied.DefaultIfEmpty(0).Max() + History.CardWidth))
            .MinBy(x => Math.Abs(x - requested));
    }

    private static bool HasGrip(double start, System.Collections.Generic.IEnumerable<double> higher,
        double gripWidth, double candidate)
    {
        double end = start + History.CardWidth;
        double exposed = start;
        foreach (double cover in higher.Append(candidate).Order())
        {
            if (Math.Min(end, cover) - exposed >= gripWidth) return true;
            exposed = Math.Max(exposed, cover + History.CardWidth);
            if (exposed >= end) return false;
        }
        return end - exposed >= gripWidth;
    }

    private void ClearDropTarget()
    {
        dragPoint = null;
        marker.Visibility = Visibility.Collapsed;
    }

    private void FlushDeferredRender()
    {
        if (deferredEntries is { } entries) Render(entries);
    }

    private sealed class Clothespin : Grid
    {
        private readonly RotateTransform left = new();
        private readonly RotateTransform right = new();

        internal Clothespin()
        {
            Width = 22;
            Height = 23;
            HorizontalAlignment = HorizontalAlignment.Center;
            VerticalAlignment = VerticalAlignment.Top;
            IsHitTestVisible = false;
            Children.Add(Jaw(left, HorizontalAlignment.Left, 5));
            Children.Add(Jaw(right, HorizontalAlignment.Right, 5));
            Children.Add(new Border
            {
                Width = 12, Height = 2, Background = Brush("#6E5238"),
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        private static Border Jaw(RotateTransform turn, HorizontalAlignment side, double inset) => new()
        {
            Width = 6, CornerRadius = new CornerRadius(2), Background = Brush("#C49B66"),
            HorizontalAlignment = side, Margin = new Thickness(inset, 0, inset, 0),
            RenderTransform = turn, RenderTransformOrigin = new Point(0.5, 0.5)
        };

        internal void Animate(bool open)
        {
            var duration = TimeSpan.FromMilliseconds(120);
            left.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(open ? 0 : -19, open ? -19 : 0, duration));
            right.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(open ? 0 : 19, open ? 19 : 0, duration));
        }
    }
}
