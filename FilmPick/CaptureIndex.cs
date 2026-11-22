using System.Collections.Concurrent;
using System.IO;

namespace FilmPick;

record CaptureInput(string Absolute, Stamp Stamp);
record CaptureAsset(string Relative, CaptureInput Main, CaptureInput? Raw, CaptureInput[] Xmp, CaptureInput[] Captions, CaptureInput[] VideoXml);
record IndexedCapture(CaptureMetadata Metadata, CaptureInput? Srt);

sealed class CaptureIndex
{
    private readonly Dictionary<CaptureInput, CaptureMetadata> files = new();
    private readonly ConcurrentDictionary<CaptureInput, CaptureSrt?> headers = new();
    private readonly Dictionary<CaptureInput, VideoColor?> sonyColors = new();
    private string[] priority = [];

    public void Prioritize(string[] paths) => Volatile.Write(ref priority, paths);

    public (CaptureInput? Input, CaptureDate? Start, VideoColor? Color) ReadCaptionHeader(CaptureInput[] captions, CancellationToken cancel)
    {
        CaptureInput? input = null;
        CaptureSrt? match = null;
        foreach (var caption in captions)
        {
            cancel.ThrowIfCancellationRequested();
            var header = headers.GetOrAdd(caption, item => CaptureSrt.Open(item.Absolute, true, cancel));
            if (header?.Recognized != true) continue;
            if (match != null) return (null, null, VideoColor.Unknown);
            input = caption;
            match = header;
        }
        return (input, match?.Start, match?.Color);
    }

    // Calls are serialized by the window. A cancelled scan cannot update a newer catalogue.
    public async Task Read(CaptureAsset[] assets, Action<KeyValuePair<string, IndexedCapture>[]> publish, CancellationToken cancel)
    {
        using var reader = new CaptureReader();
        var pending = assets.ToDictionary(asset => asset.Relative, Paths.Comparer);
        var original = new Queue<CaptureAsset>(assets);
        string[]? lastPriority = null;
        var preferred = new Queue<string>();
        while (pending.Count > 0)
        {
            cancel.ThrowIfCancellationRequested();
            var currentPriority = Volatile.Read(ref priority);
            if (!ReferenceEquals(currentPriority, lastPriority))
            {
                lastPriority = currentPriority;
                preferred = new(currentPriority);
            }
            var batch = new List<CaptureAsset>();
            while (batch.Count < 12 && preferred.TryDequeue(out var name))
                if (pending.Remove(name, out var asset)) batch.Add(asset);
            while (batch.Count < 12 && original.TryDequeue(out var asset))
                if (pending.Remove(asset.Relative)) batch.Add(asset);
            var inputs = batch.SelectMany(asset => Inputs(asset)).Distinct()
                .Where(input => !files.ContainsKey(input)).ToArray();
            var result = await reader.Read(inputs.Select(input => input.Absolute).ToArray(), cancel).ConfigureAwait(false);
            foreach (var input in inputs)
                if (result.TryGetValue(input.Absolute, out var value) && !value.Failed) files[input] = value;
            CaptureMetadata Data(CaptureInput input) => files.GetValueOrDefault(input, CaptureMetadata.Unreadable);
            var results = new List<KeyValuePair<string, IndexedCapture>>();
            foreach (var asset in batch)
            {
                cancel.ThrowIfCancellationRequested();
                var metadata = asset.Raw is { } raw ? Data(raw).Fill(Data(asset.Main)) : Data(asset.Main);
                var main = asset.Raw ?? asset.Main;
                var proprietary = Catalog.IsRaw(main.Absolute) && !Path.GetExtension(main.Absolute).Equals(".dng", StringComparison.OrdinalIgnoreCase);
                var canonical = proprietary ? asset.Xmp.FirstOrDefault(path =>
                    Paths.Comparer.Equals(path.Absolute, Path.ChangeExtension(main.Absolute, ".xmp"))) : null;
                var standard = canonical != null ? Data(canonical) : CaptureMetadata.Empty;
                var ratings = asset.Xmp.Select(Data).ToArray();
                var rating = standard.Rating ?? metadata.Rating ?? ratings.Select(item => item.Rating).FirstOrDefault(value => value.HasValue);
                var flag = standard.Flag ?? metadata.Flag ?? ratings.Select(item => item.Flag).FirstOrDefault(value => value.HasValue);
                var (srt, start, color) = ReadCaptionHeader(asset.Captions, cancel);
                foreach (var xml in asset.VideoXml)
                {
                    cancel.ThrowIfCancellationRequested();
                    if (!sonyColors.TryGetValue(xml, out var mode)) sonyColors[xml] = mode = VideoColors.ReadSony(xml.Absolute).Mode;
                    color = VideoColors.MergeModes(color, mode);
                }
                var date = metadata.Date ?? start ?? metadata.ContainerDate;
                results.Add(new(asset.Relative, new(metadata with { Date = date, Rating = rating, Flag = flag, CameraColor = color }, srt)));
            }
            publish(results.ToArray());
        }
    }

    private static IEnumerable<CaptureInput> Inputs(CaptureAsset asset)
    {
        yield return asset.Main;
        if (asset.Raw != null) yield return asset.Raw;
        foreach (var xmp in asset.Xmp) yield return xmp;
    }
}
