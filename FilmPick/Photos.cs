using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FilmPick;

enum PhotoStage { JpegOrPng, Embedded, Developed }

sealed record PhotoFrame(BitmapSource Image, PixelSize Native, PhotoStage Stage, bool Exhausted = false)
{
    public PixelSize Display => new(Image.PixelWidth, Image.PixelHeight);
    public long Bytes => (long)Image.PixelWidth * Image.PixelHeight * Math.Max(4, (Image.Format.BitsPerPixel + 7) / 8);
    public bool Fits(PreviewBox box)
    {
        var needed = box.Fit(Native, allowUpscale: false);
        return Display.Width + 1 >= needed.Width && Display.Height + 1 >= needed.Height;
    }
}

readonly record struct PhotoFile(string Path, Stamp Stamp);

sealed class Photos(long byteLimit = PreviewBox.CacheLimit)
{
    private readonly object gate = new();
    private readonly Dictionary<(PhotoFile, PhotoStage), (PhotoFrame Frame, long Used)> cache = [];
    private readonly Dictionary<PhotoFile, string> failures = [];
    private readonly SemaphoreSlim imageWorkers = new(2);
    private readonly SemaphoreSlim rawWorker = new(1);
    private HashSet<PhotoFile> nearby = [];
    private PhotoFile? current, paired;
    private long clock, cachedBytes;
    private int developedCount;
    public int DevelopedCount => Volatile.Read(ref developedCount);
    public long CachedBytes { get { lock (gate) return cachedBytes; } }

    public void Prioritize(PhotoFile? visible, IEnumerable<PhotoFile> neighbors, PhotoFile? companion = null)
    {
        lock (gate)
        {
            current = visible;
            paired = companion;
            nearby = neighbors.ToHashSet();
        }
    }

    public PhotoFrame? Find(PhotoFile file, PhotoStage stage)
    {
        lock (gate)
        {
            if (!cache.TryGetValue((file, stage), out var hit)) return null;
            cache[(file, stage)] = (hit.Frame, ++clock);
            return hit.Frame;
        }
    }

    public async Task<PhotoFrame?> Read(PhotoFile file, PhotoStage stage, PreviewBox box,
        CancellationToken cancel, bool prefetch = false)
    {
        var hit = Find(file, stage);
        if (Usable(hit, stage, box)) return hit;
        var workers = stage == PhotoStage.Developed ? rawWorker : imageWorkers;
        await workers.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            cancel.ThrowIfCancellationRequested();
            hit = Find(file, stage);
            if (Usable(hit, stage, box)) return hit;
            lock (gate)
            {
                if (stage == PhotoStage.Developed && failures.TryGetValue(file, out var failure)) throw new IOException(failure);
                if (prefetch)
                {
                    var size = hit?.Native ?? Find(file, PhotoStage.Embedded)?.Native;
                    var estimate = size is { } native && stage != PhotoStage.Developed
                        ? box.Fit(native, allowUpscale: false) : box.Fit(new(box.Width, box.Height));
                    var reservation = (long)estimate.Width * estimate.Height * 4 - (hit?.Bytes ?? 0);
                    if (!MakeRoom(reservation, (file, stage), true)) throw new PhotoCacheFull();
                }
            }
            PhotoFrame? frame;
            try
            {
                frame = await Task.Run(() =>
                {
                    cancel.ThrowIfCancellationRequested();
                    return stage switch
                    {
                        PhotoStage.Embedded => RawPreview.ReadEmbedded(file.Path, box, cancel),
                        PhotoStage.Developed => Develop(file.Path, box, cancel),
                        _ => ReadPhoto(file.Path, box)
                    };
                }, cancel).ConfigureAwait(false);
            }
            catch (IOException ex) when (stage == PhotoStage.Developed && !cancel.IsCancellationRequested)
            {
                lock (gate)
                {
                    if (failures.Count >= 128) failures.Remove(failures.Keys.First());
                    failures[file] = ex.Message;
                }
                throw;
            }
            cancel.ThrowIfCancellationRequested();
            if (frame == null) return hit;
            lock (gate)
            {
                var key = (file, stage);
                if (cache.TryGetValue(key, out var previous) && Usable(previous.Frame, stage, box)) return previous.Frame;
                var required = frame.Bytes - (cache.TryGetValue(key, out previous) ? previous.Frame.Bytes : 0);
                if (MakeRoom(required, key, prefetch))
                {
                    cache[key] = (frame, ++clock);
                    cachedBytes += required;
                }
                else if (prefetch) throw new PhotoCacheFull();
            }
            return frame;
        }
        finally { workers.Release(); }
    }

    private PhotoFrame Develop(string path, PreviewBox box, CancellationToken cancel)
    {
        Interlocked.Increment(ref developedCount);
        return RawPreview.Develop(path, box, cancel);
    }

    private bool MakeRoom(long extra, (PhotoFile, PhotoStage) writing, bool prefetch)
    {
        if (cachedBytes + extra <= byteLimit) return true;
        var candidates = cache.Where(pair => pair.Key != writing && pair.Key.Item1 != current && pair.Key.Item1 != paired)
            .Where(pair => !prefetch || !nearby.Contains(pair.Key.Item1))
            .OrderBy(pair => nearby.Contains(pair.Key.Item1)).ThenBy(pair => pair.Value.Used).ToArray();
        foreach (var (key, value) in candidates)
        {
            cache.Remove(key);
            cachedBytes -= value.Frame.Bytes;
            if (cachedBytes + extra <= byteLimit) return true;
        }
        return false;
    }

    private static bool Usable(PhotoFrame? frame, PhotoStage stage, PreviewBox box) => frame != null &&
        frame.Fits(box) && (stage != PhotoStage.Embedded || frame.Exhausted || !box.NeedsRaw(frame.Native));

    internal static PhotoFrame ReadPhoto(string path, PreviewBox box)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return DecodeImage(stream, box, Path.GetExtension(path).StartsWith(".jp", StringComparison.OrdinalIgnoreCase), PhotoStage.JpegOrPng);
    }

    internal static PhotoFrame DecodeImage(Stream stream, PreviewBox box, bool jpeg, PhotoStage stage)
    {
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        var orientation = 1;
        if (jpeg)
        {
            try
            {
                if (frame.Metadata is BitmapMetadata metadata && metadata.GetQuery("/app1/ifd/{ushort=274}") is ushort value)
                    orientation = value;
            }
            catch (Exception ex) when (ex is NotSupportedException or ArgumentException) { }
        }
        var rotated = orientation is >= 5 and <= 8;
        var native = rotated ? new PixelSize(frame.PixelHeight, frame.PixelWidth) : new(frame.PixelWidth, frame.PixelHeight);
        var desired = box.Fit(native, allowUpscale: false);
        stream.Position = 0;
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.StreamSource = stream;
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        if (frame.PixelWidth >= frame.PixelHeight) bitmap.DecodePixelWidth = rotated ? desired.Height : desired.Width;
        else bitmap.DecodePixelHeight = rotated ? desired.Width : desired.Height;
        bitmap.EndInit();
        bitmap.Freeze();
        var matrix = orientation switch
        {
            2 => new Matrix(-1, 0, 0, 1, 0, 0),
            3 => new Matrix(-1, 0, 0, -1, 0, 0),
            4 => new Matrix(1, 0, 0, -1, 0, 0),
            5 => new Matrix(0, 1, 1, 0, 0, 0),
            6 => new Matrix(0, 1, -1, 0, 0, 0),
            7 => new Matrix(0, -1, -1, 0, 0, 0),
            8 => new Matrix(0, -1, 1, 0, 0, 0),
            _ => Matrix.Identity
        };
        BitmapSource image = bitmap;
        if (!matrix.IsIdentity)
        {
            image = new TransformedBitmap(bitmap, new MatrixTransform(matrix));
            image.Freeze();
        }
        return new(image, native, stage);
    }
}

sealed class PhotoCacheFull : Exception;
