using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FilmPick;

public partial class MainWindow
{
    private readonly ThumbnailVideoClient thumbnailVideo = new();
    private readonly ThumbnailCache thumbnailBases = new();
    private readonly ThumbnailCache thumbnailLooks = new();
    private readonly Dictionary<ThumbnailKey, Row> thumbnailTargets = new();
    private readonly HashSet<ThumbnailKey> finishedThumbnails = new();
    private DispatcherTimer thumbnailTimer = null!;
    private DataTemplate? queueTextTemplate;
    private ItemsPanelTemplate? queueTextPanel;
    private bool thumbnailView;
    private Lut? thumbnailLook;
    private ThumbnailKey? thumbnailWorking;
    private bool gradingThumbnail;
    private CancellationTokenSource? thumbnailCancel;
    private Task thumbnailLoop = Task.CompletedTask;

    private readonly record struct ThumbnailKey(Kind Kind, PhotoFile Main, PhotoFile? Companion);

    private void InitializeThumbnailQueue()
    {
        queueTextTemplate = Queue.ItemTemplate;
        queueTextPanel = Queue.ItemsPanel;
        thumbnailTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(55) };
        thumbnailTimer.Tick += (_, _) =>
        {
            thumbnailTimer.Stop();
            SyncThumbnailQueue();
        };
        Queue.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => ScheduleThumbnails()));
        Queue.SizeChanged += (_, _) => ScheduleThumbnails();
        ApplyQueuePresentation();
    }

    private void ShowTextQueue(object sender, RoutedEventArgs args) => ChangeQueuePresentation(false);
    private void ShowThumbnailQueue(object sender, RoutedEventArgs args) => ChangeQueuePresentation(true);

    private void ChangeQueuePresentation(bool images)
    {
        if (busy || thumbnailView == images) return;
        var grid = FindQueueVisual<ThumbnailGrid>(Queue);
        var scroll = FindQueueVisual<ScrollViewer>(Queue);
        var first = grid?.FirstVisible ?? (int)(scroll?.VerticalOffset ?? 0);
        var end = grid?.VisibleEnd ?? first + (int)Math.Ceiling(scroll?.ViewportHeight ?? 0);
        var selectedIndex = Queue.SelectedIndex;
        var anchor = selectedIndex >= first && selectedIndex < end ? selectedIndex : first;
        var item = rows.ElementAtOrDefault(anchor);
        if (item == null) item = Queue.SelectedItem as Row;
        ui = view == Kind.Photo ? ui with { PhotoThumbnails = images } : ui with { VideoThumbnails = images };
        ApplyQueuePresentation();
        SaveUi();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (thumbnailView != images) return;
            if (item != null && visibleRows.GetValueOrDefault(item.Relative) is { } current) Queue.ScrollIntoView(current);
            Queue.Focus();
            ScheduleThumbnails();
        });
    }

    private void ApplyQueuePresentation()
    {
        var images = view == Kind.Photo ? ui.PhotoThumbnails : ui.VideoThumbnails;
        Queue.Tag = view == Kind.Photo ? 2d / 3 : 9d / 16;
        QueueTextButton.Foreground = (Brush)FindResource(images ? "Ink" : "Accent");
        QueueGridButton.Foreground = (Brush)FindResource(images ? "Accent" : "Ink");
        AutomationProperties.SetHelpText(QueueTextButton, images ? "" : "当前视图");
        AutomationProperties.SetHelpText(QueueGridButton, images ? "当前视图" : "");
        if (thumbnailView == images) return;
        thumbnailView = images;
        var wasBuilding = building;
        building = true;
        try
        {
            Queue.ItemTemplate = images ? (DataTemplate)Queue.Resources["ThumbnailCard"] : queueTextTemplate;
            Queue.ItemsPanel = images ? (ItemsPanelTemplate)Queue.Resources["ThumbnailGridItems"] : queueTextPanel;
            Queue.ItemContainerStyle = images ? (Style)Queue.Resources["ThumbnailItem"] : null;
            Queue.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty,
                images ? ScrollBarVisibility.Visible : ScrollBarVisibility.Auto);
        }
        finally { building = wasBuilding; }

        if (images) ScheduleThumbnails();
        else
        {
            PauseThumbnails();
            thumbnailVideo.Stop();
        }
    }

    private static T? FindQueueVisual<T>(DependencyObject element) where T : DependencyObject
    {
        if (element is T match) return match;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            if (FindQueueVisual<T>(VisualTreeHelper.GetChild(element, index)) is { } child) return child;
        return null;
    }

    private void ScheduleThumbnails()
    {
        if (!thumbnailView || busy || root == null || thumbnailTimer == null || thumbnailTimer.IsEnabled) return;
        thumbnailTimer.Start();
    }

    private void PauseThumbnails()
    {
        thumbnailTimer?.Stop();
        thumbnailCancel?.Cancel();
        foreach (var row in thumbnailTargets.Values) row.Thumbnail = null;
        thumbnailTargets.Clear();
        finishedThumbnails.Clear();
    }

    private async Task DrainThumbnails()
    {
        PauseThumbnails();
        await thumbnailLoop;
        thumbnailVideo.Stop();
    }

    private void RefreshThumbnailLook()
    {
        var look = lutOn ? lut : null;
        if (ReferenceEquals(look, thumbnailLook)) return;
        thumbnailLook = look;
        thumbnailLooks.Clear();
        finishedThumbnails.Clear();
        if (gradingThumbnail) thumbnailCancel?.Cancel();
        foreach (var (key, row) in thumbnailTargets)
            row.Thumbnail = thumbnailBases.TryGet(key, out var image) ? image : null;
        ScheduleThumbnails();
    }

    private void SyncThumbnailQueue()
    {
        if (!thumbnailView || busy || root == null || rows.Count == 0) { PauseThumbnails(); return; }
        if (FindQueueVisual<ThumbnailGrid>(Queue) is not { ViewportHeight: > 0 } grid) { ScheduleThumbnails(); return; }
        var next = new Dictionary<ThumbnailKey, Row>();
        void Add(int index)
        {
            var row = rows[index];
            if (!catalog.Files.TryGetValue(row.Relative, out var media)) return;
            string? companion;
            if (view == Kind.Photo)
                companion = catalog.RawCompanions.GetValueOrDefault(row.Relative);
            else
            {
                var proxies = attached[row.Relative].Where(file => Path.GetExtension(file).Equals(".lrf", StringComparison.OrdinalIgnoreCase))
                    .Take(2).ToArray();
                companion = proxies.Length == 1 ? proxies[0] : null;
            }
            var key = new ThumbnailKey(view, new(Paths.Below(root, row.Relative), media.Stamp),
                companion != null && catalog.Files.TryGetValue(companion, out var related)
                    ? new PhotoFile(Paths.Below(root, companion), related.Stamp) : null);
            next.TryAdd(key, row);
        }

        if (Queue.SelectedIndex >= grid.FirstVisible && Queue.SelectedIndex < grid.VisibleEnd) Add(Queue.SelectedIndex);
        for (var index = Math.Max(0, grid.FirstVisible); index < Math.Min(rows.Count, grid.BufferedEnd); index++) Add(index);
        foreach (var (key, row) in thumbnailTargets)
            if (!ReferenceEquals(next.GetValueOrDefault(key), row))
            {
                row.Thumbnail = null;
                finishedThumbnails.Remove(key);
            }
        thumbnailTargets.Clear();
        foreach (var (key, row) in next)
        {
            thumbnailTargets.Add(key, row);
            if (finishedThumbnails.Contains(key)) continue;
            if (thumbnailLook != null && thumbnailLooks.TryGet(key, out var graded))
            {
                row.Thumbnail = graded;
                finishedThumbnails.Add(key);
            }
            else if (thumbnailBases.TryGet(key, out var original))
            {
                row.Thumbnail = original;
                if (original == null || thumbnailLook == null) finishedThumbnails.Add(key);
            }
        }
        if (thumbnailWorking is { } active && !thumbnailTargets.ContainsKey(active) &&
            (active.Kind != Kind.Video || view != Kind.Video || gradingThumbnail)) thumbnailCancel?.Cancel();
        if (thumbnailLoop.IsCompleted) thumbnailLoop = WorkThumbnails();
    }

    private async Task WorkThumbnails()
    {
        while (TryNextThumbnail(out var key, out var source))
        {
            using var request = new CancellationTokenSource();
            thumbnailCancel = request;
            thumbnailWorking = key;
            var token = request.Token;
            var look = thumbnailLook;
            gradingThumbnail = source != null;
            try
            {
                if (source != null && look != null)
                {
                    var graded = await Task.Run(() => look.Transform(source, token, parallelism: 1), token);
                    if (token.IsCancellationRequested || !ReferenceEquals(look, thumbnailLook)) continue;
                    thumbnailLooks.Set(key, graded);
                    if (thumbnailTargets.TryGetValue(key, out var row))
                    {
                        row.Thumbnail = graded;
                        finishedThumbnails.Add(key);
                    }
                }
                else
                {
                    var original = await ReadThumbnail(key, token);
                    if (token.IsCancellationRequested) continue;
                    thumbnailBases.Set(key, original);
                    if (thumbnailTargets.TryGetValue(key, out var row))
                    {
                        row.Thumbnail = original;
                        if (original == null || thumbnailLook == null) finishedThumbnails.Add(key);
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception error) when (IsThumbnailError(error))
            {
                if (!token.IsCancellationRequested)
                {
                    if (source != null && ReferenceEquals(look, thumbnailLook)) thumbnailLooks.Set(key, source);
                    else if (source == null) thumbnailBases.Set(key, null);
                    if (thumbnailTargets.ContainsKey(key)) finishedThumbnails.Add(key);
                }
            }
            finally
            {
                thumbnailCancel = null;
                thumbnailWorking = null;
                gradingThumbnail = false;
            }
        }
    }

    private bool TryNextThumbnail(out ThumbnailKey next, out BitmapSource? original)
    {
        foreach (var key in thumbnailTargets.Keys)
            if (!finishedThumbnails.Contains(key) &&
                (!thumbnailBases.TryGet(key, out original) ||
                 (original != null && thumbnailLook != null && !thumbnailLooks.TryGet(key, out _))))
            {
                next = key;
                return true;
            }
        next = default;
        original = null;
        return false;
    }

    private Task<BitmapSource?> ReadThumbnail(ThumbnailKey key, CancellationToken token)
    {
        if (key.Kind == Kind.Video)
            return thumbnailVideo.Read(new(key.Main.Path, key.Companion?.Path), token);
        return Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            var box = new PreviewBox(ThumbnailVideoClient.Edge, ThumbnailVideoClient.Edge);
            if (Catalog.IsRaw(key.Main.Path)) return RawPreview.ReadEmbedded(key.Main.Path, box, token)?.Image;
            try { return Photos.ReadPhoto(key.Main.Path, box).Image; }
            catch (Exception error) when (IsThumbnailError(error) && key.Companion is { } raw)
            {
                return RawPreview.ReadEmbedded(raw.Path, box, token)?.Image;
            }
        }, token);
    }

    private static bool IsThumbnailError(Exception error) =>
        error is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or InvalidOperationException
            or System.Runtime.InteropServices.ExternalException;

    private sealed class ThumbnailCache
    {
        private const long ByteLimit = 32L * 1024 * 1024;
        private readonly Dictionary<ThumbnailKey, (BitmapSource? Image, long Cost, long Used)> entries = new();
        private long bytes, clock;

        public bool TryGet(ThumbnailKey key, out BitmapSource? image)
        {
            if (entries.TryGetValue(key, out var entry))
            {
                image = entry.Image;
                entries[key] = (entry.Image, entry.Cost, ++clock);
                return true;
            }
            image = null;
            return false;
        }

        public void Set(ThumbnailKey key, BitmapSource? image)
        {
            if (entries.Remove(key, out var removed)) bytes -= removed.Cost;
            var cost = image == null ? 0 : (long)image.PixelHeight * image.PixelWidth * Math.Max(4, (image.Format.BitsPerPixel + 7) / 8);
            entries[key] = (image, cost, ++clock);
            bytes += cost;
            while (bytes > ByteLimit || entries.Count > 256)
            {
                var oldest = entries.MinBy(pair => pair.Value.Used);
                entries.Remove(oldest.Key);
                bytes -= oldest.Value.Cost;
            }
        }

        public void Clear() { entries.Clear(); bytes = 0; }
    }
}
