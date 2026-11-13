using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace PicLaundry;

internal sealed partial class Shelf
{
    private IReadOnlyList<Entry> renderedEntries = Array.Empty<Entry>();
    private readonly DispatcherTimer idleTimer = new();
    private readonly DispatcherTimer motionTimer = new(DispatcherPriority.Render)
        { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Stopwatch motionClock = new();
    private Guid? impulseImage;
    private readonly HashSet<Guid> idleImages = new();
    private bool pointerInside;

    private void InitializeMotion()
    {
        SizeChanged += (_, _) => UpdateRope();
        scroll.ScrollChanged += (_, _) => UpdateRope();
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) { idleTimer.Stop(); StopMotion(); }
            else ScheduleIdle();
        };
        MouseEnter += (_, _) => TrackMotionPointer(true);
        idleTimer.Tick += (_, _) => PlayIdle();
        motionTimer.Tick += (_, _) =>
        {
            if (motionClock.Elapsed.TotalSeconds >= 0.88) StopMotion();
            else UpdateRope();
        };
    }

    internal void TrackMotionPointer(bool inside)
    {
        if (pointerInside == inside) return;
        pointerInside = inside;
        if (inside && idleImages.Count > 0) StopMotion();
        ScheduleIdle();
    }

    private void ScheduleIdle()
    {
        idleTimer.Stop();
        if (!IsVisible || pointerInside || Dragging || MenuOpen || imageCards.Count == 0) return;
        idleTimer.Interval = TimeSpan.FromSeconds(Random.Shared.Next(20, 46));
        idleTimer.Start();
    }

    private void PlayIdle()
    {
        if (IsVisible && !pointerInside && !IsMouseOver && !Dragging && !MenuOpen && !motionTimer.IsEnabled)
        {
            foreach (var id in imageCards.Where(pair => pair.Value.Child is Image && pair.Value.Parent is Grid card
                && CardCenter(card) >= 0 && CardCenter(card) <= ActualWidth)
                .Select(pair => pair.Key).OrderBy(_ => Random.Shared.Next()).Take(Random.Shared.Next(1, 3)))
                idleImages.Add(id);
            if (idleImages.Count > 0) BeginMotion();
        }
        ScheduleIdle();
    }

    private void Nudge(Guid id)
    {
        if (!IsVisible || Dragging) return;
        impulseImage = id;
        BeginMotion();
    }

    private void BeginMotion()
    {
        motionClock.Restart();
        motionTimer.Start();
    }

    private void StopMotion()
    {
        if (!motionTimer.IsEnabled) return;
        motionTimer.Stop();
        motionClock.Reset();
        impulseImage = null;
        idleImages.Clear();
        UpdateRope();
    }

    private double CardCenter(Grid card) =>
        scroll.Margin.Left + Canvas.GetLeft(card) + History.CardWidth / 2 - scroll.HorizontalOffset;

    private void UpdateRope()
    {
        double width = ActualWidth;
        double time = motionClock.Elapsed.TotalSeconds;
        double? origin = impulseImage is Guid id && imageCards.TryGetValue(id, out var body)
            && body.Parent is Grid sourceCard ? Math.Clamp(CardCenter(sourceCard), 0, width) : null;

        double RopeY(double x)
        {
            double fraction = width > 0 ? Math.Clamp(x / width, 0, 1) : 0;
            double baseY = 10 + 32 * fraction * (1 - fraction);
            if (origin is not double center) return baseY;
            double distance = Math.Abs(x - center);
            return baseY + 2 * Math.Min(1, 8 * Math.Min(fraction, 1 - fraction))
                * Math.Exp(-distance / 270) * Swing(time - Math.Min(0.2, distance / 1900));
        }

        var geometry = new StreamGeometry();
        using (var drawing = geometry.Open())
        {
            drawing.BeginFigure(new Point(0, RopeY(0)), false, false);
            for (double x = 18; x < width; x += 18)
                drawing.LineTo(new Point(x, RopeY(x)), true, false);
            drawing.LineTo(new Point(width, RopeY(width)), true, false);
        }
        geometry.Freeze();
        rope.Data = geometry;

        foreach (Grid card in cards.Children)
        {
            var cardId = (Guid)card.Tag;
            double x = CardCenter(card);
            var offset = (TranslateTransform)card.RenderTransform;
            offset.Y = RopeY(x) - scroll.Margin.Top - card.Margin.Top;
            if (!imageCards.TryGetValue(cardId, out var picture)) continue;
            var tilt = (RotateTransform)((TransformGroup)picture.RenderTransform).Children[2];
            double distance = origin.HasValue ? Math.Abs(x - origin.Value) : 0;
            tilt.Angle = origin.HasValue
                ? 2.2 * Math.Exp(-distance / 360) * Swing(time - Math.Min(0.2, distance / 1900))
                : idleImages.Contains(cardId) ? 1.4 * Swing(time) : 0;
        }
    }

    private static double Swing(double seconds)
    {
        const double duration = 0.68;
        if (seconds <= 0 || seconds >= duration) return 0;
        double progress = seconds / duration;
        return Math.Sin(progress * 3.5 * Math.PI) * Math.Pow(1 - progress, 1.35);
    }
}
