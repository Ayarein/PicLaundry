using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Threading;

namespace PicLaundry;

internal sealed class ScreenshotCollector : IDisposable
{
    private readonly string folder;
    private readonly Dispatcher dispatcher;
    private readonly DispatcherTimer timer;
    private readonly Dictionary<string, FileStamp> observed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (FileStamp Stamp, long Since)> pending = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? watcher;
    private bool dirty = true;
    private bool disposed;
    private int ticks;

    internal event Action<string, string, DateTimeOffset>? Captured;

    internal ScreenshotCollector(string folder, Dispatcher dispatcher)
    {
        this.folder = folder;
        this.dispatcher = dispatcher;
        foreach (var (path, stamp) in Scan()) observed[path] = stamp;
        StartWatcher();
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) => Tick(), dispatcher);
    }

    private void StartWatcher()
    {
        if (watcher != null || !Directory.Exists(folder)) return;
        try
        {
            var next = new FileSystemWatcher(folder)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
            };
            next.Created += (_, _) => MarkDirty();
            next.Changed += (_, _) => MarkDirty();
            next.Renamed += (_, _) => MarkDirty();
            next.Deleted += (_, _) => MarkDirty();
            next.Error += (_, _) => dispatcher.BeginInvoke(new Action(() =>
            {
                if (disposed || watcher != next) return;
                watcher.Dispose();
                watcher = null;
                dirty = true;
            }));
            try { next.EnableRaisingEvents = true; }
            catch { next.Dispose(); throw; }
            watcher = next;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (ArgumentException) { }
    }

    private void MarkDirty() => dispatcher.BeginInvoke(new Action(() => dirty = true));

    private void Tick()
    {
        // Rescan periodically to recover from a missing directory or lost notifications.
        if (++ticks % 10 == 0)
        {
            StartWatcher();
            dirty = true;
        }
        if (!dirty && pending.Count == 0) return;
        dirty = false;
        var files = Scan();
        foreach (string removed in observed.Keys.Where(p => !files.ContainsKey(p)).ToArray()) observed.Remove(removed);
        foreach (string removed in pending.Keys.Where(p => !files.ContainsKey(p)).ToArray()) pending.Remove(removed);
        foreach (var (path, stamp) in files)
        {
            if (observed.TryGetValue(path, out var previous) && previous == stamp)
            {
                pending.Remove(path);
                continue;
            }
            long now = Environment.TickCount64;
            if (!pending.TryGetValue(path, out var last) || last.Stamp != stamp)
            {
                pending[path] = (stamp, now);
                continue;
            }
            try
            {
                var image = History.Decode(path);
                var hash = History.Fingerprint(image);
                Captured?.Invoke(path, hash, DateTimeOffset.Now);
                observed[path] = stamp;
                pending.Remove(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException
                or ArgumentException or System.Runtime.InteropServices.COMException)
            {
                if (now - last.Since < 10_000) continue;
                observed[path] = stamp;
                pending.Remove(path);
            }
        }
    }

    private Dictionary<string, FileStamp> Scan()
    {
        var files = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (string path in Directory.EnumerateFiles(folder))
            {
                if (Path.GetExtension(path).ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg" or ".bmp")) continue;
                var file = new FileInfo(path);
                try { files[path] = new FileStamp(file.LastWriteTimeUtc, file.Length); }
                catch (IOException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return files;
    }

    public void Dispose()
    {
        disposed = true;
        timer.Stop();
        watcher?.Dispose();
    }

    private readonly record struct FileStamp(DateTime Time, long Length);
}
