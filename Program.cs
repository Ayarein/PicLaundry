using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace PicLaundry;

internal sealed class Program : Application
{
    private readonly string settingsPath;
    private readonly HwndSource messages;
    private readonly HwndSourceHook messageHook;
    private readonly Shelf shelf;
    private readonly Forms.NotifyIcon tray;
    private readonly Forms.ToolStripMenuItem pause;
    private readonly System.Drawing.Icon icon;
    private readonly History history;
    private readonly DispatcherTimer pointerTimer;
    private readonly Func<System.Drawing.Point> pointerPosition;
    private readonly Func<Forms.Screen, bool> isFullscreen;
    private Settings settings;
    private SettingsWindow? settingsWindow;
    private ScreenshotCollector screenshots;
    private bool listening;
    private int hotKey;
    private int nextHotKey = 1;
    private uint lastSequence;
    private uint ownSequence;
    private bool touchedShelf;
    private bool blockEdge;
    private string? edgeScreen;
    private long edgeSince;
    private long openedAt;
    private long lastShelfRefresh;
    private string? activeScreen;
    private System.Drawing.Rectangle shelfRegion;

    [STAThread]
    private static int Main()
    {
        using var mutex = new Mutex(true, @"Local\Hangline.88c7e623", out bool first);
        if (!first) return 0;
        try
        {
            string root = DataDirectory(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            return new Program(root).Run();
        }
        catch (Exception ex)
        {
            MessageBox.Show("PicLaundry 无法启动。\n" + ex.Message, "PicLaundry", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }

    internal static string DataDirectory(string localAppData)
    {
        var configured = Environment.GetEnvironmentVariable("PICLAUNDRY_DATA_DIR");
        if (!string.IsNullOrEmpty(configured)) return configured;
        configured = Environment.GetEnvironmentVariable("HILINE_DATA_DIR");
        if (!string.IsNullOrEmpty(configured)) return configured;
        configured = Environment.GetEnvironmentVariable("HANGLINE_DATA_DIR");
        if (!string.IsNullOrEmpty(configured)) return configured;

        // History stores absolute image paths, so keep existing profiles in place.
        string current = Path.Combine(localAppData, "PicLaundry");
        if (Directory.Exists(current)) return current;
        string hiline = Path.Combine(localAppData, "Hiline");
        if (Directory.Exists(hiline)) return hiline;
        string hangline = Path.Combine(localAppData, "Hangline");
        return Directory.Exists(hangline) ? hangline : current;
    }

    internal Program(string root, Func<System.Drawing.Point>? pointerPosition = null,
        Func<Forms.Screen, bool>? isFullscreen = null)
    {
        this.pointerPosition = pointerPosition ?? (() => Forms.Cursor.Position);
        this.isFullscreen = isFullscreen ?? Native.IsFullscreen;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        settingsPath = Path.Combine(root, "settings.json");
        history = new History(root);
        settings = Settings.Load(settingsPath);
        shelf = new Shelf(Copy, id => ChangeHistory(() => history.Remove(id)),
            (id, x) => ChangeHistory(() => history.Place(id, x)));
        history.Changed += () => { if (shelf.IsVisible) shelf.Render(history.Entries); };
        messages = new HwndSource(new HwndSourceParameters("PicLaundry messages") { ParentWindow = new nint(-3) });
        messageHook = Message;
        messages.AddHook(messageHook);
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开晾衣绳", null, (_, _) => Open(false));
        menu.Items.Add("设置…", null, (_, _) => OpenSettings());
        pause = (Forms.ToolStripMenuItem)menu.Items.Add("", null, (_, _) => TogglePause());
        menu.Items.Add("清空历史…", null, (_, _) =>
        {
            if (MessageBox.Show("清空晾衣绳上的所有条目？", "PicLaundry", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                ChangeHistory(history.Clear);
        });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Shutdown());
        using (var trayIcon = typeof(Program).Assembly.GetManifestResourceStream("PicLaundry.TrayIcon"))
            icon = trayIcon == null ? (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone()
                : new System.Drawing.Icon(trayIcon);
        tray = new Forms.NotifyIcon { Icon = icon, Text = "PicLaundry", ContextMenuStrip = menu, Visible = true };
        tray.MouseDoubleClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) Open(false); };
        screenshots = StartScreenshots(settings.Folder);
        UpdateClipboard();
        if (!SetHotKey(settings)) Startup += (_, _) => Notice("快捷键已被占用。请在设置中换键。");
        pointerTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(60), DispatcherPriority.Background, (_, _) => TrackPointer(), Dispatcher);
        if (settings.AlwaysShow) Startup += (_, _) => ShowDesktopShelf();
    }

    private ScreenshotCollector StartScreenshots(string folder)
    {
        var collector = new ScreenshotCollector(folder, Dispatcher);
        collector.Captured += (path, hash, now) => history.AddScreenshot(path, hash, now);
        return collector;
    }

    private void OpenSettings()
    {
        if (settingsWindow != null) { settingsWindow.Activate(); return; }
        if (!settings.AlwaysShow) HideShelf();
        settingsWindow = new SettingsWindow(settings, SaveSettings);
        settingsWindow.Closed += (_, _) => settingsWindow = null;
        settingsWindow.Show();
    }

    private string? SaveSettings(Settings next)
    {
        var previous = settings;
        next = next with { Paused = previous.Paused };
        if (!SetHotKey(next))
        {
            Notice("快捷键已被占用。请在设置中换键。");
            return "快捷键已被占用，请换一个。";
        }
        try { next.Save(settingsPath); }
        catch
        {
            ReplaceHotKey(previous);
            throw;
        }
        settings = next;
        if (previous.AlwaysShow != next.AlwaysShow)
        {
            if (next.AlwaysShow) ShowDesktopShelf();
            else HideShelf();
        }
        if (!string.Equals(previous.Folder, next.Folder, StringComparison.OrdinalIgnoreCase))
        {
            screenshots.Dispose();
            screenshots = StartScreenshots(next.Folder);
        }
        UpdateClipboard();
        return null;
    }

    private void TogglePause()
    {
        var next = settings with { Paused = !settings.Paused };
        ChangeHistory(() =>
        {
            next.Save(settingsPath);
            settings = next;
            UpdateClipboard();
        });
    }

    private bool SetHotKey(Settings next) =>
        (hotKey != 0 && next.Key == settings.Key && next.Modifiers == settings.Modifiers) || ReplaceHotKey(next);

    private bool ReplaceHotKey(Settings next)
    {
        int id = nextHotKey == 1 ? 2 : 1;
        if (!Native.RegisterHotKey(messages.Handle, id, Native.HotKeyModifiers(next.Modifiers) | Native.NoRepeat,
            (uint)KeyInterop.VirtualKeyFromKey(next.Key))) return false;
        if (hotKey != 0) Native.UnregisterHotKey(messages.Handle, hotKey);
        hotKey = nextHotKey = id;
        return true;
    }

    private void UpdateClipboard()
    {
        pause.Text = settings.Paused ? "继续收集剪贴板" : "暂停收集剪贴板";
        bool wanted = !settings.Paused && settings.Images;
        lastSequence = Native.GetClipboardSequenceNumber();
        if (wanted && !listening)
        {
            listening = Native.AddClipboardFormatListener(messages.Handle);
            if (!listening) Notice("无法监听剪贴板。");
        }
        else if (!wanted && listening)
        {
            Native.RemoveClipboardFormatListener(messages.Handle);
            listening = false;
        }
    }

    private nint Message(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == Native.HotKeyMessage && wParam == hotKey) { handled = true; Open(true); }
        if (message == Native.ClipboardUpdate && listening)
        {
            uint sequence = Native.GetClipboardSequenceNumber();
            Dispatcher.BeginInvoke(() => ReadClipboard(sequence, 0), DispatcherPriority.Background);
        }
        return 0;
    }

    private async void ReadClipboard(uint sequence, int retry)
    {
        if (!listening || sequence == lastSequence || sequence == ownSequence
            || sequence != Native.GetClipboardSequenceNumber()) return;
        try
        {
            if (Native.ClipboardIsExcluded(messages.Handle))
            {
                lastSequence = sequence;
                return;
            }
            BitmapSource? image = settings.Images ? ReadClipboardImage() : null;
            if (sequence != Native.GetClipboardSequenceNumber()) return;
            lastSequence = sequence;
            if (image != null && (long)image.PixelWidth * image.PixelHeight <= 64_000_000)
                history.AddImage(image, History.Fingerprint(image), DateTimeOffset.Now);
        }
        catch (ExternalException)
        {
            if (retry >= 5) return;
            await System.Threading.Tasks.Task.Delay(40 * (retry + 1));
            ReadClipboard(sequence, retry + 1);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            Notice("无法保存剪贴板内容。");
        }
    }

    private static BitmapSource? ReadClipboardImage()
    {
        var data = Clipboard.GetDataObject();
        if (data?.GetDataPresent("PNG", false) == true && data.GetData("PNG", false) is MemoryStream png)
        {
            using (png)
            {
                png.Position = 0;
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = png;
                image.EndInit();
                return image;
            }
        }
        return Clipboard.GetImage();
    }

    private void Copy(Entry entry)
    {
        try
        {
            var data = new DataObject();
            if (entry.Path is string path) data.SetImage(History.Decode(path));
            else return;
            data.SetData(Native.ExcludedClipboard, new MemoryStream(new byte[] { 1, 0, 0, 0 }), false);
            Clipboard.SetDataObject(data, true);
            ownSequence = Native.GetClipboardSequenceNumber();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ExternalException or NotSupportedException or ArgumentException)
        {
            Notice("无法复制。原文件可能已移走，或剪贴板正在使用中。");
            shelf.Render(history.Entries);
        }
    }

    private void ChangeHistory(Action change)
    {
        try { change(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Notice("无法写入 PicLaundry 数据。"); }
    }

    private void Open(bool toggle)
    {
        if (shelf.Dragging) return;
        if (settings.AlwaysShow) return;
        var point = pointerPosition();
        var screen = Forms.Screen.FromPoint(point);
        if (toggle && shelf.IsVisible && screen.DeviceName == activeScreen) { HideShelf(); return; }
        var b = screen.Bounds;
        double scale = Native.ScaleFor(point);
        shelfRegion = new System.Drawing.Rectangle(b.Left, b.Top, b.Width, (int)Math.Ceiling(shelf.Height * scale));
        activeScreen = screen.DeviceName;
        openedAt = lastShelfRefresh = Environment.TickCount64;
        touchedShelf = shelfRegion.Contains(point);
        shelf.Render(history.Entries);
        shelf.Open(screen, scale);
    }

    private void ShowDesktopShelf()
    {
        var screen = Forms.Screen.PrimaryScreen ?? Forms.Screen.FromPoint(pointerPosition());
        var bounds = screen.Bounds;
        var center = new System.Drawing.Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        lastShelfRefresh = Environment.TickCount64;
        shelfRegion = new System.Drawing.Rectangle(bounds.Left, bounds.Top, bounds.Width,
            (int)Math.Ceiling(shelf.Height * Native.ScaleFor(center)));
        shelf.Render(history.Entries);
        shelf.Open(screen, Native.ScaleFor(center), desktop: true);
    }

    private void HideShelf()
    {
        shelf.Hide();
        edgeScreen = null;
        blockEdge = true;
    }

    private void TrackPointer()
    {
        var point = pointerPosition();
        long now = Environment.TickCount64;
        if (shelf.IsVisible)
        {
            shelf.TrackMotionPointer(shelfRegion.Contains(point));
            if (shelf.MenuOpen || shelf.Dragging) return;
            if (!settings.AlwaysShow)
            {
                if (shelfRegion.Contains(point)) touchedShelf = true;
                else if (touchedShelf || now - openedAt >= 3000) { HideShelf(); return; }
            }
            if (now - lastShelfRefresh >= 1200)
            {
                lastShelfRefresh = now;
                // Reconcile unavailable screenshot cards without rebuilding on every tick.
                shelf.RefreshAvailability(history.Entries);
            }
            return;
        }
        if (settings.AlwaysShow) return;
        var screen = Forms.Screen.FromPoint(point);
        bool atEdge = point.Y >= screen.Bounds.Top && point.Y <= screen.Bounds.Top + 1;
        if (!atEdge) { edgeScreen = null; blockEdge = false; return; }
        if (blockEdge || settingsWindow != null) return;
        if (screen.DeviceName != edgeScreen)
        {
            edgeScreen = screen.DeviceName;
            edgeSince = now;
        }
        else if (now - edgeSince >= 250 && !isFullscreen(screen)) Open(false);
    }

    private void Notice(string message) => tray.ShowBalloonTip(3000, "PicLaundry", message, Forms.ToolTipIcon.Info);

    protected override void OnExit(ExitEventArgs e)
    {
        pointerTimer.Stop();
        screenshots.Dispose();
        if (listening) Native.RemoveClipboardFormatListener(messages.Handle);
        if (hotKey != 0) Native.UnregisterHotKey(messages.Handle, hotKey);
        messages.RemoveHook(messageHook);
        messages.Dispose();
        tray.Visible = false;
        tray.ContextMenuStrip?.Dispose();
        tray.Dispose();
        icon.Dispose();
        base.OnExit(e);
    }
}
