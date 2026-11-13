using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PicLaundry;

internal enum Source { Screenshot, Clipboard }

internal sealed record Entry(Guid Id, Source Source, string? Path, string? Text, string? Hash, DateTimeOffset Time,
    double? X = null, int Layer = 0)
{
    public bool IsImage => Path != null;
}

internal sealed class History
{
    private static readonly TimeSpan MatchWindow = TimeSpan.FromSeconds(5);
    private readonly string root;
    private readonly string index;
    private readonly string images;
    private List<Entry> entries;
    internal IReadOnlyList<Entry> Entries => entries;
    internal event Action? Changed;

    internal History(string root)
    {
        this.root = root;
        index = Path.Combine(root, "history.json");
        images = Path.Combine(root, "images");
        Directory.CreateDirectory(images);
        entries = File.Exists(index) ? JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(index)) ?? [] : [];
        var active = entries.Where(e => e.Source == Source.Clipboard && e.IsImage)
            .Select(e => ImagePath(e.Id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(images, "*.png"))
            if (!active.Contains(file)) TryDelete(file);
    }

    internal static BitmapSource Decode(string path, int width = 0)
    {
        using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = file;
        if (width > 0) image.DecodePixelWidth = width;
        image.EndInit();
        image.Freeze();
        return image;
    }

    internal static string Fingerprint(BitmapSource image)
    {
        var pixels = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int stride = checked(image.PixelWidth * 4);
        var row = new byte[stride];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(BitConverter.GetBytes(image.PixelWidth));
        hash.AppendData(BitConverter.GetBytes(image.PixelHeight));
        for (int y = 0; y < image.PixelHeight; y++)
        {
            pixels.CopyPixels(new System.Windows.Int32Rect(0, y, image.PixelWidth, 1), row, stride, 0);
            // Clipboard DIBs may have an unused alpha channel.
            for (int x = 3; x < stride; x += 4) row[x] = 255;
            hash.AppendData(row);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal void AddScreenshot(string path, string hash, DateTimeOffset now)
    {
        if (entries.Any(e => e.Source == Source.Screenshot && e.Hash == hash
            && string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase)
            && (now - e.Time).Duration() <= MatchWindow)) return;

        var match = FindOther(Source.Screenshot, hash, now);
        var screenshot = new Entry(match?.Id ?? Guid.NewGuid(), Source.Screenshot, path, null, hash, now);
        if (match == null) Add(screenshot);
        else Commit(entries.Select(e => e == match ? screenshot with { X = e.X, Layer = e.Layer } : e).ToList());
    }

    internal void AddImage(BitmapSource image, string hash, DateTimeOffset now)
    {
        if (FindOther(Source.Clipboard, hash, now) != null) return;
        Guid id = Guid.NewGuid();
        string path = ImagePath(id);
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (var file = File.Create(path)) encoder.Save(file);
            Add(new Entry(id, Source.Clipboard, path, null, hash, now));
        }
        catch
        {
            if (!entries.Any(e => e.Id == id)) TryDelete(path);
            throw;
        }
    }

    internal void Remove(Guid id) => Commit(entries.Where(e => e.Id != id).ToList());

    internal const double CardWidth = 151;
    internal const double CardPitch = 165;
    internal static double Position(Entry entry, int index) => entry.X ?? 7 + index * CardPitch;

    internal void Place(Guid id, double x)
    {
        if (!entries.Any(e => e.Id == id && e.IsImage) || !double.IsFinite(x) || x < 0) return;
        int layer = entries.Max(e => e.Layer) + 1;
        int index = 0;
        Commit(entries.Select(entry =>
        {
            if (!entry.IsImage) return entry;
            double position = Position(entry, index++);
            return entry.Id == id ? entry with { X = x, Layer = layer } : entry with { X = position };
        }).ToList());
    }
    internal void Clear() => Commit([]);

    private string ImagePath(Guid id) => Path.Combine(images, id.ToString("N") + ".png");

    private Entry? FindOther(Source source, string hash, DateTimeOffset now) =>
        entries.FirstOrDefault(e => e.Source != source && e.Hash == hash && (now - e.Time).Duration() <= MatchWindow);

    private void Add(Entry entry)
    {
        var visible = entries.Where(e => e.IsImage).ToArray();
        var next = entries.Prepend(entry).ToList();
        if (visible.Any(e => e.X.HasValue))
        {
            double shift = Math.Max(0, 7 + CardPitch - visible.Select(Position).Min());
            int index = 0;
            next = entries.Select(existing => existing.IsImage
                    ? existing with { X = Position(existing, index++) + shift } : existing)
                .Prepend(entry with { X = 7 }).ToList();
        }
        if (visible.Length >= 50)
        {
            var oldest = visible.MinBy(item => item.Time)!;
            next.RemoveAll(item => item.Id == oldest.Id);
        }
        Commit(next);
    }

    private void Commit(List<Entry> next)
    {
        var pending = Path.Combine(root, "history.tmp");
        File.WriteAllText(pending, JsonSerializer.Serialize(next));
        File.Move(pending, index, true);
        var previous = entries;
        entries = next;
        foreach (var item in previous)
            if (item.Source == Source.Clipboard && item.IsImage
                && !next.Any(e => e.Id == item.Id && e.Source == Source.Clipboard))
                TryDelete(ImagePath(item.Id));
        Changed?.Invoke();
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
