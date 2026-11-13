using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace PicLaundry;

internal static class Verification
{
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint window, int message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [STAThread]
    private static int Main(string[] args)
    {
        string root = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(root);
        string folder = Directory.CreateDirectory(Path.Combine(root, "screenshots")).FullName;
        var image = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 9, 20, 40, 255, 1, 4, 5, 255, 9, 8, 7, 255, 60, 40, 20, 255 }, 8);
        string baseline = Path.Combine(folder, "old.png");
        SavePng(baseline, image);
        var initial = new Settings(folder, false, false, ModifierKeys.Control | ModifierKeys.Alt, Key.F9);
        initial.Save(Path.Combine(root, "settings.json"));
        var initialCursor = Forms.Cursor.Position;
        var bounds = Forms.Screen.FromPoint(initialCursor).Bounds;
        var pointer = new System.Drawing.Point(bounds.Left + bounds.Width / 2, bounds.Bottom - 10);
        bool fullscreen = false;
        var app = new Program(root, () => pointer, _ => fullscreen);
        int result = 1;
        app.Startup += async (_, _) =>
        {
            try
            {
                if (args.Contains("--motion-only"))
                {
                    VerifyMotion(root);
                    result = 0;
                    return;
                }
                if (args.Contains("--recycle-only"))
                {
                    VerifyRecycle(root, image);
                    result = 0;
                    return;
                }
                if (args.Contains("--drag-only"))
                {
                    VerifyDrag(root, image);
                    result = 0;
                    return;
                }
                var history = Field<History>(app, "history");
                var shelf = Field<Shelf>(app, "shelf");
                var messageWindow = Field<System.Windows.Interop.HwndSource>(app, "messages");
                var tray = Field<Forms.NotifyIcon>(app, "tray");
                Check(!shelf.IsVisible && !Settings.Load(Path.Combine(root, "settings.json")).AlwaysShow,
                    "desktop shelf is off by default");
                var covering = new Window { ShowActivated = false, Width = 300, Height = 200 };
                covering.Show();
                var coveringHandle = new System.Windows.Interop.WindowInteropHelper(covering).Handle;
                Check(Call(app, "SaveSettings", initial with { AlwaysShow = true }) == null
                    && shelf.IsVisible && !shelf.Topmost && Settings.Load(Path.Combine(root, "settings.json")).AlwaysShow,
                    "desktop shelf appears immediately with empty history and persists its setting");
                var desktopHandle = new System.Windows.Interop.WindowInteropHelper(shelf).Handle;
                var primary = Forms.Screen.PrimaryScreen!.Bounds;
                Check(GetWindowRect(desktopHandle, out var desktopRect) && desktopRect.Left == primary.Left
                    && desktopRect.Right == primary.Right && desktopRect.Top == primary.Top, "desktop shelf uses the primary display");
                Native.SetWindowPos(coveringHandle, 0, primary.Left, primary.Top, 300, 200, 0x0010);
                PostMessage(messageWindow.Handle, Native.HotKeyMessage, Field<int>(app, "hotKey"), 0);
                ((Forms.ToolStripMenuItem)tray.ContextMenuStrip!.Items[0]).PerformClick();
                await Task.Delay(3200);
                Check(shelf.IsVisible && IsAbove(coveringHandle, desktopHandle),
                    "desktop shelf stays visible and behind overlapping window after shortcut, tray command and pointer timeout");
                covering.WindowState = WindowState.Minimized;
                Check(shelf.IsVisible, "minimizing covering window leaves desktop shelf visible");
                covering.Close();
                Check(Call(app, "SaveSettings", initial) == null && !shelf.IsVisible, "disabling desktop shelf restores hidden state");
                var foreground = Native.GetForegroundWindow();
                int shortcutId = Field<int>(app, "hotKey");
                Check(shortcutId != 0, "test global hotkey registered");
                Check(PostMessage(messageWindow.Handle, Native.HotKeyMessage, shortcutId, 0), "posted native hotkey message");
                await Until(() => shelf.IsVisible);
                Check(shelf.IsVisible && Native.GetForegroundWindow() == foreground, "overlay opens without taking foreground");
                var shelfHandle = new System.Windows.Interop.WindowInteropHelper(shelf).Handle;
                Check(GetWindowRect(shelfHandle, out var rect) && rect.Top == bounds.Top
                    && rect.Left == bounds.Left && rect.Right == bounds.Right
                    && Math.Abs(rect.Bottom - rect.Top - shelf.Height * GetDpiForWindow(shelfHandle) / 96) <= 1,
                    "overlay covers the current monitor top edge at its DPI");
                PostMessage(messageWindow.Handle, Native.HotKeyMessage, shortcutId, 0);
                await Until(() => !shelf.IsVisible);
                Check(!shelf.IsVisible, "toggle closes overlay");
                ((Forms.ToolStripMenuItem)tray.ContextMenuStrip!.Items[0]).PerformClick();
                Check(shelf.IsVisible, "tray open command shows overlay");
                Call(app, "Open", true);
                await Task.Delay(100);
                pointer.Y = bounds.Top;
                await Until(() => shelf.IsVisible);
                pointer.Y = bounds.Bottom - 10;
                await Until(() => !shelf.IsVisible);
                Check(true, "top edge opens and leaving the shelf hides it");
                fullscreen = true;
                pointer.Y = bounds.Top;
                await Task.Delay(600);
                Check(!shelf.IsVisible, "fullscreen suppresses edge reveal");
                PostMessage(messageWindow.Handle, Native.HotKeyMessage, shortcutId, 0);
                await Until(() => shelf.IsVisible);
                Check(shelf.IsVisible, "hotkey opens when fullscreen is reported");
                PostMessage(messageWindow.Handle, Native.HotKeyMessage, shortcutId, 0);
                await Until(() => !shelf.IsVisible);
                fullscreen = false;
                pointer.Y = bounds.Bottom - 10;
                var changed = initial with { Key = Key.F10 };
                Check(Call(app, "SaveSettings", changed) == null && Field<int>(app, "hotKey") != 0, "alternate hotkey registered");
                uint conflict = (uint)KeyInterop.VirtualKeyFromKey(Key.F11);
                uint modifiers = Native.HotKeyModifiers(initial.Modifiers);
                Check(Native.RegisterHotKey(0, 81, modifiers, conflict), "reserved conflict shortcut");
                try
                {
                    Check(Call(app, "SaveSettings", changed with { Key = Key.F11 }) != null &&
                        Field<Settings>(app, "settings").Key == Key.F10, "conflict rejected and old hotkey retained");
                }
                finally { Native.UnregisterHotKey(0, 81); }

                string first = Path.Combine(folder, "new.png");
                SavePng(first, image);
                await Until(() => history.Entries.Count == 1);
                Check(history.Entries[0].Path == first && !shelf.IsVisible, "folder capture without clipboard, ignores baseline and stays hidden");
                if (args.Contains("--shelf-only"))
                {
                    string secondImage = Path.Combine(root, "preview-source.png");

                    var legacy = new Entry(Guid.NewGuid(), Source.Clipboard, null, "https://example.invalid/selection", null, DateTimeOffset.Now);
                    Call(history, "Commit", history.Entries.Prepend(legacy).ToList());
                    var full = BitmapSource.Create(640, 480, 96, 96, PixelFormats.Bgra32, null, new byte[640 * 480 * 4], 640 * 4);
                    SavePng(secondImage, full);
                    history.AddScreenshot(secondImage, History.Fingerprint(full), DateTimeOffset.Now);
                    var one = history.Entries.Single(e => e.Path == secondImage);
                    var two = history.Entries.Single(e => e.Path == first);
                    pointer.Y = bounds.Top + 10;
                    Call(app, "Open", false);
                    shelf.UpdateLayout();
                    var imageCards = Field<System.Collections.Generic.Dictionary<Guid, Border>>(shelf, "imageCards");
                    Check(Field<Canvas>(shelf, "cards").Children.Count == 2
                        && new History(root).Entries.Any(e => e == legacy), "legacy text stays on disk but only images are rendered");
                    var originalNeighbor = ((FrameworkElement)imageCards[two.Id].Parent).ActualWidth;
                    Hover(imageCards[one.Id], true);
                    await Task.Delay(200);
                    Check(IsHovered(imageCards[one.Id]), "mouse enter highlights and enlarges image");
                    int interval = Forms.SystemInformation.DoubleClickTime;
                    await WriteClipboard(() => Clipboard.SetText("preview sentinel"));
                    Click(shelf, one);
                    Check(Clipboard.GetText() == "preview sentinel", "first click waits for a potential second click");
                    Click(shelf, one);
                    var viewer = Field<ImagePreview>(shelf, "imagePreview");
                    Check(viewer.IsVisible && Field<BitmapSource>(viewer, "original").PixelWidth == 640
                        && ((BitmapSource)((Image)imageCards[one.Id].Child).Source).PixelWidth == 260,
                        "double click opens full original instead of 260px shelf thumbnail");
                    var viewport = Field<Canvas>(viewer, "viewport");
                    var anchor = new Point(viewport.ActualWidth * .4, viewport.ActualHeight * .55);
                    var origin = Field<Point>(viewer, "origin");
                    double zoom = Field<double>(viewer, "zoom");
                    Call(viewer, "ZoomAt", anchor, 120);
                    double enlarged = Field<double>(viewer, "zoom");
                    Check(enlarged > zoom && ((anchor - Field<Point>(viewer, "origin")) / enlarged
                        - (anchor - origin) / zoom).Length < .001, "wheel zooms in around the pointer");
                    Call(viewer, "ZoomAt", anchor, -120);
                    Check(Math.Abs(Field<double>(viewer, "zoom") - zoom) < .001, "wheel zooms back out");
                    viewer.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
                        System.Windows.PresentationSource.FromVisual(viewer), 0, Key.Escape)
                        { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                    Check(Field<ImagePreview?>(shelf, "imagePreview") == null, "Esc closes the original preview");
                    await Task.Delay(interval + 100);
                    Check(Clipboard.GetText() == "preview sentinel", "double click never copies, even after timeout");
                    Click(shelf, one);
                    await Until(() => Clipboard.ContainsImage());
                    Check(Clipboard.GetImage()?.PixelWidth == 640, "single click copies the original image");
                    await WriteClipboard(() => Clipboard.SetText("different cards"));
                    Click(shelf, one);
                    Click(shelf, two);
                    await Task.Delay(interval + 100);
                    Check(Field<ImagePreview?>(shelf, "imagePreview") == null && Clipboard.GetImage()?.PixelWidth == 2,
                        "clicking different cards copies without opening a preview");
                    Hover(imageCards[one.Id], false);
                    Hover(imageCards[two.Id], true);
                    await Task.Delay(200);
                    Check(IsHovered(imageCards[two.Id]) && !IsHovered(imageCards[one.Id])
                        && ((FrameworkElement)imageCards[two.Id].Parent).ActualWidth == originalNeighbor,
                        "hover follows pointer without resizing layout");
                    var menu = imageCards[two.Id].ContextMenu;
                    menu.PlacementTarget = imageCards[two.Id];
                    menu.IsOpen = true;
                    await Until(() => shelf.MenuOpen);
                    ((MenuItem)menu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    Check(!history.Entries.Any(e => e.Id == two.Id) && !imageCards.ContainsKey(two.Id)
                        && !menu.IsOpen && File.Exists(first),
                        "remove closes menu and immediately removes card while preserving original");
                    history.Place(one.Id, 45.5);
                    var saved = new History(root).Entries.Single(e => e.Id == one.Id);
                    Check(saved.X == 45.5 && saved.Layer > 0, "fractional position and layer persist");
                    history.Place(one.Id, 400.25);
                    history.AddScreenshot(first, "new-test", DateTimeOffset.Now);
                    Check(history.Entries[0].Path == first && history.Entries.Single(e => e.Id == one.Id).X == 400.25,
                        "new image uses free space at left");
                    history.Place(one.Id, 70);
                    var layer = history.Entries.Single(e => e.Id == one.Id).Layer;
                    history.AddScreenshot(Path.Combine(folder, "third.png"), "another-test", DateTimeOffset.Now);
                    var shifted = new History(root).Entries;
                    Check(shifted[0].X == 7 && shifted[1].X == 172 &&
                        shifted.Single(e => e.Id == one.Id).X == 235 && shifted.Single(e => e.Id == one.Id).Layer == layer
                        && shifted.Any(e => e == legacy),
                        "new image shifts the image layout together and preserves legacy records");
                    Check(Shelf.FindDropX(222.5, [7, 172]) == 222.5, "free overlapping position remains exact");
                    Check(Shelf.FindDropX(172, [172]) == 148, "near total overlap leaves a grip");
                    Check(Shelf.FindDropX(50, [100, 160]) == 124,
                        "a third card cannot hide the remaining grip");
                    Check(Shelf.MovedEnough(new Point(0, 0), new Point(5, 0))
                        && !Shelf.MovedEnough(new Point(0, 0), new Point(4, 0)), "fast drag movement threshold");
                    Call(app, "Open", true);
                    result = 0;
                    return;
                }
                string redirected = Directory.CreateDirectory(Path.Combine(root, "redirected")).FullName;
                SavePng(Path.Combine(redirected, "baseline.png"), image);
                changed = changed with { Folder = redirected, Images = true };
                Check(Call(app, "SaveSettings", changed) == null, "directory and image collection settings applied");
                history.Clear();
                await WriteClipboard(() => Clipboard.SetImage(image));
                await Until(() => history.Entries.Count == 1);
                var clipboardCopy = history.Entries[0].Path!;
                Check(history.Entries[0].Source == Source.Clipboard && File.Exists(clipboardCopy), "clipboard-only bitmap collected");
                string duplicate = Path.Combine(redirected, "same.png");
                SavePng(duplicate, image);
                await Until(() => history.Entries.Count == 1 && history.Entries[0].Path == duplicate);
                Check(!File.Exists(clipboardCopy), "cross-source dedupe retains screenshot path and cleans copied bitmap");
                await WriteClipboard(() => Clipboard.SetImage(image));
                await Task.Delay(400);
                Check(history.Entries.Count == 1, "cross-source dedupe also handles file arriving first");
                await WriteClipboard(() => Clipboard.SetText("ignored plain text"));
                await Task.Delay(400);
                await WriteClipboard(() => Clipboard.SetText("https://example.invalid/hangline"));
                await Task.Delay(400);
                Check(history.Entries.Count == 1, "plain text and links are ignored even with image collection enabled");
                string settingsFile = Path.Combine(root, "settings.json");
                File.WriteAllText(settingsFile, File.ReadAllText(settingsFile).Insert(1, "\"Text\":true,"));
                var legacySettings = Settings.Load(settingsFile);
                Check(legacySettings == changed, "old text collection setting is ignored on load");
                Check(Call(app, "SaveSettings", changed with { Images = false }) == null, "image collection can be disabled");
                await WriteClipboard(() => Clipboard.SetImage(image));
                await Task.Delay(400);
                Check(history.Entries.Count == 1, "disabled image collection ignores bitmap");
                Check(Call(app, "SaveSettings", changed) == null, "image collection can be re-enabled");
                var distinct = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 40, 80, 90, 255 }, 4);
                var protectedData = new DataObject();
                protectedData.SetImage(distinct);
                protectedData.SetData(Native.ExcludedClipboard, new MemoryStream(new byte[] { 1, 0, 0, 0 }), false);
                await WriteClipboard(() => Clipboard.SetDataObject(protectedData, true));
                await Task.Delay(400);
                Check(history.Entries.Count == 1, "source exclusion marker honored for images");
                Call(app, "TogglePause");
                await WriteClipboard(() => Clipboard.SetImage(distinct));
                await Task.Delay(400);
                Check(history.Entries.Count == 1, "pause stops image collection");
                var previous = history.Entries.Single(e => e.Path == duplicate);
                string moved = duplicate + ".moved";
                File.Move(duplicate, moved);
                Call(app, "Open", true);
                Check(ContainsText(shelf, "原文件不可用"), "missing screenshot has visible unavailable state");
                history.Remove(previous.Id);
                Check(File.Exists(moved), "removing a screenshot leaves the source alone");
                string hash = History.Fingerprint(image);
                history.AddImage(image, hash, DateTimeOffset.Now);
                var removedCopy = history.Entries[0].Path!;
                history.Remove(history.Entries[0].Id);
                Check(!File.Exists(removedCopy), "removing a clipboard image deletes the owned file");
                for (int i = 0; i < 53; i++)
                    history.AddScreenshot(Path.Combine(folder, i + ".png"), i.ToString(), DateTimeOffset.Now.AddSeconds(i));
                var restored = new History(root);
                Check(restored.Entries.Count == 50 && restored.Entries[0].Hash == "52" &&
                    restored.Entries[^1].Hash == "3", "on-disk history restores the newest 50 images in order");
                history.AddImage(image, hash, DateTimeOffset.Now);
                var clearedCopy = history.Entries[0].Path!;
                history.AddScreenshot(first, hash + "-test", DateTimeOffset.Now);
                history.Clear();
                Check(new History(root).Entries.Count == 0 && !File.Exists(clearedCopy) && File.Exists(first), "clear deletes owned copies and leaves original");
                Console.WriteLine($"Display count: {Forms.Screen.AllScreens.Length}");
                result = 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); }
            finally { app.Shutdown(); }
        };
        app.Run();
        return result;
    }

    private static bool ContainsText(DependencyObject control, string value) =>
        (control is System.Windows.Controls.TextBlock text && text.Text == value)
        || LogicalTreeHelper.GetChildren(control).OfType<DependencyObject>().Any(c => ContainsText(c, value));

    private static bool IsAbove(nint upper, nint lower)
    {
        for (nint window = GetWindow(lower, 3); window != 0; window = GetWindow(window, 3))
            if (window == upper) return true;
        return false;
    }

    private static void Click(Shelf shelf, Entry entry)
    {
        Call(shelf, "BeginClick", entry.Id);
        Call(shelf, "ReleaseClick", entry);
    }

    private static void Hover(Border card, bool entered) => card.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0)
        { RoutedEvent = entered ? Mouse.MouseEnterEvent : Mouse.MouseLeaveEvent });

    private static bool IsHovered(Border card) =>
        ((ScaleTransform)((TransformGroup)card.RenderTransform).Children[0]).ScaleX == 1.12
        && ((SolidColorBrush)card.BorderBrush).Color == Color.FromRgb(255, 194, 77);

    private static T Field<T>(object instance, string name) =>
        (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;

    private static object? Call(object instance, string name, params object[] args) =>
        instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, args);

    private static void VerifyMotion(string root)
    {
        var history = new History(Path.Combine(root, "motion"));
        var shelf = new Shelf(_ => { }, _ => { }, history.Place);
        history.Changed += () => shelf.Render(history.Entries);
        string path = Path.Combine(root, "screenshots", "old.png");
        for (int i = 0; i < 3; i++) history.AddScreenshot(path, i.ToString(), DateTimeOffset.Now);
        var screen = Forms.Screen.PrimaryScreen!;
        shelf.Open(screen, Native.ScaleFor(screen.Bounds.Location));
        shelf.UpdateLayout();
        shelf.TrackMotionPointer(false);
        var canvas = Field<Canvas>(shelf, "cards");
        var motion = Field<DispatcherTimer>(shelf, "motionTimer");
        var idle = Field<DispatcherTimer>(shelf, "idleTimer");
        Grid Card(int index) => (Grid)canvas.Children[index];
        Transform Picture(int index) => Card(index).Children[0].RenderTransform;
        double Angle(int index) => ((RotateTransform)((TransformGroup)Picture(index)).Children[2]).Angle;
        double Y(int index) => ((TranslateTransform)Card(index).RenderTransform).Y;
        void CheckGrip(int index)
        {
            var grip = new Point(History.CardWidth / 2, 0);
            Check((Picture(index).Transform(grip) - grip).Length < 0.00001
                && Card(index).RenderTransform.Value.M12 == 0
                && Card(index).Children[1].RenderTransform.Value.IsIdentity,
                "picture rotates around the grip while clip only translates with rope");
        }
        history.AddScreenshot(path, "new", DateTimeOffset.Now);
        double restingY = Y(0);
        Pump(100);
        Check(motion.IsEnabled && Y(0) > restingY && Y(0) - restingY <= 2
            && Math.Abs(Angle(0)) is > 1 and <= 2.2 && Math.Abs(Angle(1)) > Math.Abs(Angle(3)),
            "new image dips the rope and nearby pictures react first within the motion limits");
        for (int i = 0; i < 3; i++) history.AddScreenshot(path, $"burst-{i}", DateTimeOffset.Now);
        Pump(110);
        CheckGrip(0);
        Check(Math.Abs(Angle(0)) <= 2.2 && Math.Abs(Y(0) - restingY) <= 2, "quick arrivals do not stack motion");
        Pump(790);
        Check(!motion.IsEnabled && Angle(0) == 0 && Y(0) == restingY, "rope and image settle within one second");
        Guid moved = history.Entries[0].Id;
        history.Place(moved, 93.5);
        Pump(100);
        Check(Math.Abs(Angle(0)) > 0 && Canvas.GetLeft(Card(0)) == 93.5, "manual rehang swings at its saved position");
        CheckGrip(0);
        Pump(800);
        Check(idle.IsEnabled && idle.Interval.TotalSeconds is >= 20 and <= 45, "idle delay is 20 to 45 seconds");
        Call(shelf, "PlayIdle");
        Pump(100);
        var angles = Enumerable.Range(0, canvas.Children.Count).Select(Angle).Where(angle => angle != 0).ToArray();
        Check(angles.Length is 1 or 2 && angles.All(angle => Math.Abs(angle) <= 1.4), "idle gently swings one or two images");
        CheckGrip(Enumerable.Range(0, canvas.Children.Count).First(i => Angle(i) != 0));
        shelf.TrackMotionPointer(true);
        Check(!motion.IsEnabled && !idle.IsEnabled && Enumerable.Range(0, canvas.Children.Count).All(i => Angle(i) == 0),
            "entering the shelf immediately steadies idle pictures");
        var body = (Border)Card(0).Children[0];
        Call(shelf, "ArmDrag", moved, body, Card(0).Children[1], ((Image)body.Child).Source, new Point(95, 70));
        Check(Field<DispatcherTimer>(shelf, "holdTimer").IsEnabled && Angle(0) == 0,
            "a steady image can arm a grab after pointer entry");
        history.Place(moved, 108.5);
        shelf.Hide();
        Check(!motion.IsEnabled && !idle.IsEnabled && Canvas.GetLeft(Card(0)) == 108.5,
            "hiding stops all motion and keeps the placement");
        shelf.Close();
    }

    private static void VerifyRecycle(string root, BitmapSource image)
    {
        var history = new History(root);
        string original = Path.Combine(root, "screenshots", "old.png");
        byte[] bytes = File.ReadAllBytes(original);
        history.AddScreenshot(original, "screenshot", DateTimeOffset.Now);
        history.AddImage(image, "clipboard", DateTimeOffset.Now);
        var screenshot = history.Entries.Single(e => e.Source == Source.Screenshot);
        var clipboard = history.Entries.Single(e => e.Source == Source.Clipboard);
        history.Place(screenshot.Id, 90.25);
        history.Place(clipboard.Id, 118.5);

        int scenario = 0;
        string? recycled = null;
        using var dropDestination = new System.IO.MemoryStream();
        var shelf = new Shelf(_ => throw new InvalidOperationException("Drag copied to clipboard"),
            history.Remove, history.Place, (_, payload, _) =>
        {
            var export = ((string[])((DataObject)payload).GetData(DataFormats.FileDrop))[0];
            Check(RecycleBin.FindExport(export) == null, "new export is not already in the Recycle Bin");
            switch (scenario)
            {
                case 0:
                    return DragDropEffects.None; // Escape or rejected drop.
                case 1:
                    File.Move(export, Path.Combine(root, "external-move.png"));
                    return DragDropEffects.Move;
                case 2:
                    File.Delete(export); // Destination did not put the file in the Recycle Bin.
                    return DragDropEffects.None;
                default:
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(export,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    recycled = RecycleBin.FindExport(export);
                    Check(recycled != null, "Windows Shell lists the exact exported image in the Recycle Bin");
                    using (var file = File.OpenRead(recycled!)) file.CopyTo(dropDestination);
                    return DragDropEffects.None;
            }
        });
        history.Changed += () => shelf.Render(history.Entries);
        shelf.Render(history.Entries);
        var screen = Forms.Screen.PrimaryScreen!;
        shelf.Open(screen, Native.ScaleFor(screen.Bounds.Location));
        shelf.UpdateLayout();
        var cards = Field<System.Collections.Generic.Dictionary<Guid, Border>>(shelf, "imageCards");
        void Drag(Guid id)
        {
            var body = cards[id];
            var card = (Grid)body.Parent;
            Call(shelf, "StartDrag", shelf.Content, id, body, card.Children[1], image);
        }

        for (scenario = 0; scenario < 3; scenario++)
        {
            Drag(screenshot.Id);
            var body = cards[screenshot.Id];
            var card = (Grid)body.Parent;
            Check(body.Opacity == 1 && card.Children[1].Opacity == 1
                && Canvas.GetLeft(card) == 90.25 && Panel.GetZIndex(card) == screenshot.Layer + 1
                && new History(root).Entries.Any(e => e.Id == screenshot.Id),
                $"unsuccessful recycle scenario {scenario} restores picture, clip, position and layer");
        }
        foreach (var entry in new[] { screenshot, clipboard })
        {
            byte[] source = File.ReadAllBytes(entry.Path!);
            dropDestination.SetLength(0);
            Drag(entry.Id);
            Check(!cards.ContainsKey(entry.Id) && !Field<Canvas>(shelf, "cards").Children
                .OfType<Grid>().Any(c => Equals(c.Tag, entry.Id)) && !new History(root).Entries.Any(e => e.Id == entry.Id),
                $"recycled {entry.Source} removes picture and clip and persists removal");
            Check(dropDestination.ToArray().SequenceEqual(source), "recycled export matches original bytes");
        }
        Check(File.ReadAllBytes(original).SequenceEqual(bytes), "screenshot original remains readable and unchanged");
        shelf.Close();
    }

    private static void VerifyDrag(string root, BitmapSource image)
    {
        int copies = 0;
        int moves = 0;
        bool cancelOutside = false;
        bool dropOnShelf = false;
        Shelf shelf = null!;
        var entries = Enumerable.Range(0, 25).Select(i => new Entry(Guid.NewGuid(), Source.Screenshot,
            Path.Combine(root, "screenshots", "old.png"), null, null, DateTimeOffset.Now)).ToList();
        shelf = new Shelf(_ => copies++, _ => { }, (_, _) => moves++, (_, payload, effects) =>
        {
            var card = Field<System.Collections.Generic.Dictionary<Guid, Border>>(shelf, "imageCards")[entries[0].Id];
            var layout = (Grid)shelf.Content;
            var pin = (Grid)((Grid)card.Parent).Children[1];
            var jaw = (RotateTransform)((Border)pin.Children[0]).RenderTransform;
            var scroll = Field<ScrollViewer>(shelf, "scroll");
            Check(shelf.Dragging && card.Opacity == 0, "drag removes image from its original clip");
            var data = (DataObject)payload;
            var files = (string[])data.GetData(DataFormats.FileDrop);
            Check(effects.HasFlag(DragDropEffects.Copy) && data.GetImage()?.PixelWidth == image.PixelWidth
                && files.Length == 1 && files[0] != entries[0].Path
                && File.ReadAllBytes(files[0]).SequenceEqual(File.ReadAllBytes(entries[0].Path!)),
                "external target receives original-size bitmap and a separate file copy");
            Check(pin.Opacity == 0, "original clip is hidden while dragging");
            double left = scroll.TranslatePoint(new Point(), layout).X;
            foreach (double x in new[] { 55.25, 65.75 })
            {
                double center = left + x - scroll.HorizontalOffset + History.CardWidth / 2;
                Check((bool)Call(shelf, "UpdateDropTarget", layout, new Point(center, 75))!
                    && Field<double>(shelf, "dropX") == x
                    && Canvas.GetLeft(Field<Border>(shelf, "marker")) + 6 == center,
                    "clip preview follows exact positions within the same gap");
            }
            Check((bool)Call(shelf, "UpdateDropTarget", layout, new Point(shelf.ActualWidth - 25, 75))!,
                "edge is a valid drop target");
            var latest = entries.Append(entries[0] with { Id = Guid.NewGuid() }).ToArray();
            shelf.Render(latest);
            Check(Field<Canvas>(shelf, "cards").Children.Count == entries.Count, "new cards wait for drag to end");
            Pump(350);
            Check(pin.Opacity == 0, "original clip stays hidden during drag");
            Check(scroll.HorizontalOffset > 0, "holding at right edge scrolls automatically");
            Check((bool)Call(shelf, "UpdateDropTarget", layout, new Point(25, 75))!, "left edge accepts drag");
            var offset = scroll.HorizontalOffset;
            Pump(200);
            Check(scroll.HorizontalOffset < offset, "holding at left edge scrolls back");
            if (cancelOutside)
                Check(!(bool)Call(shelf, "UpdateDropTarget", layout, new Point(30, shelf.ActualHeight + 15))!,
                    "point below the shelf rejects a drop");
            var preview = Field<DragPreview>(shelf, "preview");
            Check(preview.IsVisible, "independent preview remains visible outside the shelf");
            if (dropOnShelf)
            {
                Check((bool)Call(shelf, "UpdateDropTarget", layout, new Point(left + 145, 75))!,
                    "returning to the shelf restores the drop marker");
                typeof(Shelf).GetField("dropped", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(shelf, true);
                return DragDropEffects.Move;
            }
            return cancelOutside ? DragDropEffects.None : DragDropEffects.Copy;
        });
        var screen = Forms.Screen.PrimaryScreen!;
        shelf.Render(entries);
        shelf.Open(screen, Native.ScaleFor(screen.Bounds.Location));
        shelf.UpdateLayout();
        foreach (int scenario in new[] { 0, 1, 2 })
        {
            cancelOutside = scenario != 0;
            dropOnShelf = scenario == 2;
            shelf.Render(entries);
            Field<ScrollViewer>(shelf, "scroll").ScrollToHorizontalOffset(0);
            shelf.UpdateLayout();
            var imageCards = Field<System.Collections.Generic.Dictionary<Guid, Border>>(shelf, "imageCards");
            var card = imageCards[entries[0].Id];
            var pin = (Grid)((Grid)card.Parent).Children[1];
            Check(Field<DispatcherTimer>(shelf, "holdTimer").Interval.TotalMilliseconds == 120, "hold threshold is 120 ms");
            Click(shelf, entries[0]);
            Call(shelf, "ArmDrag", entries[0].Id, card, pin, image, new Point(95, 70));
            Call(shelf, "StartDrag", shelf.Content, entries[0].Id, card, pin, image);
            var restored = imageCards[entries[0].Id];
            Check(((Grid)((Grid)restored.Parent).Children[1]).Opacity == 1
                && Field<DragPreview?>(shelf, "preview") == null, "clip restored and floating preview closed");
            var jaw = (RotateTransform)((Border)((Grid)((Grid)restored.Parent).Children[1]).Children[0]).RenderTransform;
            Check(!shelf.Dragging && restored.Opacity == 1 && moves == (dropOnShelf ? 1 : 0)
                && imageCards.Count == entries.Count + 1,
                "only an internal drop changes position and deferred cards are flushed");
            imageCards[entries[1].Id].RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent });
            Pump(Forms.SystemInformation.DoubleClickTime + 100);
            Check(copies == 0 && Field<ImagePreview?>(shelf, "imagePreview") == null,
                "click followed by a drag, including release over another card, neither copies nor opens preview");
            Check(jaw.Angle == 0, "clip finishes closing after cancel");
        }
        shelf.Close();
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static async Task Until(Func<bool> condition)
    {
        for (int i = 0; i < 80; i++)
        {
            if (condition()) return;
            await Task.Delay(75);
        }
        throw new InvalidOperationException("Timed out waiting for collection.");
    }

    private static async Task WriteClipboard(Action write)
    {
        for (int retry = 0; ; retry++)
        {
            try { write(); return; }
            catch (ExternalException) when (retry < 8) { await Task.Delay(80); }
        }
    }

    private static void Check(bool result, string label)
    {
        if (!result) throw new InvalidOperationException(label);
        Console.WriteLine("PASS " + label);
    }

    private static void SavePng(string file, BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(file);
        encoder.Save(output);
    }
}











