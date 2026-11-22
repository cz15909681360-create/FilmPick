using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace FilmPick;

public partial class MainWindow
{
    private DispatcherTimer photoLayoutTimer = null!;
    private CancellationTokenSource? photoCancel, prefetchCancel, photoPaintCancel;
    private PhotoFrame? photoFrame;
    private PreviewBox photoBox;
    private string photoDetail = "";
    private int lastPhotoIndex = -1, photoDirection = 1;
    private int plannedNeighbors;

    private void InitializePhotoPreview()
    {
        photoLayoutTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
        photoLayoutTimer.Tick += (_, _) =>
        {
            photoLayoutTimer.Stop();
            if (busy || view != Kind.Photo || Queue.SelectedItem is not Row row) return;
            var box = CurrentPhotoBox();
            if (box == photoBox) return;
            StartPhotoPreview(row);
        };
        Viewport.SizeChanged += (_, _) => QueuePhotoLayout();
        DpiChanged += (_, _) => QueuePhotoLayout();
    }

    private void QueuePhotoLayout()
    {
        if (busy || view != Kind.Photo || Queue.SelectedItem == null) return;
        photoLayoutTimer.Stop();
        photoLayoutTimer.Start();
    }

    private PreviewBox CurrentPhotoBox()
    {
        var dpi = VisualTreeHelper.GetDpi(Viewport);
        return PreviewBox.FromLayout(Viewport.ActualWidth, Viewport.ActualHeight, dpi.DpiScaleX, dpi.DpiScaleY);
    }

    private void StopPhotoPreview()
    {
        photoLayoutTimer.Stop();
        Cancel(ref prefetchCancel);
        Cancel(ref photoCancel);
        Cancel(ref photoPaintCancel);
        photoFrame = null;
        photoDetail = "";
        plannedNeighbors = 0;
        photos.Prioritize(null, []);
    }

    private static void Cancel(ref CancellationTokenSource? request)
    {
        request?.Cancel();
        request?.Dispose();
        request = null;
    }

    private void StartPhotoPreview(Row row)
    {
        if (root == null) return;
        Cancel(ref prefetchCancel);
        Cancel(ref photoCancel);
        Cancel(ref photoPaintCancel);
        photoCancel = new();
        frameError = null;
        var box = photoBox = CurrentPhotoBox();
        var folder = root;
        (PhotoFile Main, PhotoFile? Raw) Files(Row item)
        {
            var main = catalog.Files[item.Relative];
            var primary = new PhotoFile(Paths.Below(folder, item.Relative), main.Stamp);
            return (primary, catalog.RawCompanions.TryGetValue(item.Relative, out var raw)
                ? new PhotoFile(Paths.Below(folder, raw), catalog.Files[raw].Stamp) : null);
        }
        var candidates = PreviewBox.Neighbors(Queue.SelectedIndex, rows.Count, photoDirection).Select(index => Files(rows[index])).ToArray();
        plannedNeighbors = candidates.Length;
        var (selected, fallback) = Files(row);
        photos.Prioritize(selected, candidates.SelectMany(candidate => candidate.Raw is { } raw ? new[] { candidate.Main, raw } : [candidate.Main]),
            fallback);
        _ = LoadPhoto(selected, fallback, row.Relative, box, candidates, photoCancel.Token);
    }

    private async Task LoadPhoto(PhotoFile selected, PhotoFile? pairedRaw, string relative, PreviewBox box,
        (PhotoFile Main, PhotoFile? Raw)[] candidates, CancellationToken cancel)
    {
        try
        {
            await Task.Delay(14, cancel);
            var rawFile = selected;
            if (!Catalog.IsRaw(selected.Path))
            {
                PhotoFrame? regular = null;
                try { regular = await photos.Read(selected, PhotoStage.JpegOrPng, box, cancel); }
                catch (Exception ex) when (ex is not OperationCanceledException && pairedRaw != null) { }
                if (regular != null)
                {
                    await Present(regular, relative + (pairedRaw != null ? " · RAW＋JPG" : ""), cancel);
                    return;
                }
                rawFile = pairedRaw!.Value;
            }

            var developed = photos.Find(rawFile, PhotoStage.Developed);
            var cachedEmbedded = photos.Find(rawFile, PhotoStage.Embedded);
            if (developed != null && developed.Fits(box) &&
                (cachedEmbedded == null || IsClearer(developed, cachedEmbedded)))
            {
                await Present(developed, Describe(developed), cancel);
                return;
            }
            var embedded = cachedEmbedded;
            if (embedded != null && !embedded.Fits(box)) embedded = null;
            string? previewProblem = null;
            try { embedded = await photos.Read(rawFile, PhotoStage.Embedded, box, cancel) ?? embedded; }
            catch (Exception ex) when (ex is IOException or NotSupportedException) { previewProblem = ex.Message; }
            if (embedded != null)
            {
                await Present(embedded, Describe(embedded), cancel);
                if (!box.NeedsRaw(embedded.Native)) return;
            }
            else if (developed != null)
            {
                await Present(developed, Describe(developed), cancel);
            }
            cancel.ThrowIfCancellationRequested();
            MediaInfo.Text = (photoFrame != null ? photoDetail + " · " : "") + "正在解码 RAW…";
            MediaInfo.ToolTip = MediaInfo.Text;
            try
            {
                developed = await photos.Read(rawFile, PhotoStage.Developed, box, cancel);
                if (developed == null) throw new IOException("RAW 解码未输出画面。");
                if (embedded != null && !IsClearer(developed, embedded))
                    throw new IOException("RAW 解码的预览不比内嵌图更清晰。");
                await Present(developed, Describe(developed), cancel);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && photoFrame != null && !cancel.IsCancellationRequested)
            {
                photoDetail = Describe(photoFrame) + " · 无法提升清晰度";
                MediaInfo.Text = photoDetail;
                MediaInfo.ToolTip = photoDetail + "\n" + ex.Message;
            }
            catch (IOException ex) when (previewProblem != null)
            {
                throw new IOException(previewProblem + "\n" + ex.Message, ex);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (cancel.IsCancellationRequested) return;
            frameError = ex.Message;
            readyTicket = ticket;
            EmptyState("无法预览照片", ex.Message);
            lutApplying = false;
            UpdateLutLabel();
        }
        finally
        {
            if (!cancel.IsCancellationRequested)
            {
                prefetchCancel = new();
                _ = PrefetchPhotos(candidates, box, prefetchCancel.Token);
            }
        }
    }

    private async Task Present(PhotoFrame frame, string detail, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        photoFrame = frame;
        photoDetail = detail;
        MediaInfo.Text = detail;
        MediaInfo.ToolTip = detail;
        await PaintPhoto(ticket);
    }

    private void RefreshPhotoLook(long expected)
    {
        if (photoFrame != null) _ = PaintPhoto(expected);
    }

    private async Task PaintPhoto(long expected)
    {
        if (photoFrame is not { } frame) return;
        Cancel(ref photoPaintCancel);
        photoPaintCancel = new();
        var cancel = photoPaintCancel.Token;
        var image = frame.Image;
        if (lutOn && lut is { } transform)
        {
            try { image = await Task.Run(() => transform.Transform(frame.Image, cancel), cancel); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                if (cancel.IsCancellationRequested || expected != ticket) return;
                LutFailed(ex.Message);
            }
        }
        if (cancel.IsCancellationRequested || expected != ticket) return;
        Photo.Source = image;
        lutApplying = false;
        UpdateLutLabel();
        Empty.Visibility = Visibility.Collapsed;
        readyTicket = expected;
        UpdateCaptureOverlay();
    }

    private async Task PrefetchPhotos((PhotoFile Main, PhotoFile? Raw)[] candidates, PreviewBox box, CancellationToken cancel)
    {
        foreach (var (main, pairedRaw) in candidates)
        {
            if (cancel.IsCancellationRequested) break;
            try
            {
                var file = main;
                if (!Catalog.IsRaw(main.Path))
                {
                    try { await photos.Read(main, PhotoStage.JpegOrPng, box, cancel, prefetch: true); continue; }
                    catch (Exception ex) when (ex is not (OperationCanceledException or PhotoCacheFull) && pairedRaw != null)
                    { file = pairedRaw.Value; }
                }
                var developed = photos.Find(file, PhotoStage.Developed);
                if (developed?.Fits(box) == true) continue;
                var embedded = await photos.Read(file, PhotoStage.Embedded, box, cancel, prefetch: true);
                if (embedded == null || box.NeedsRaw(embedded.Native))
                    await photos.Read(file, PhotoStage.Developed, box, cancel, prefetch: true);
            }
            catch (Exception ex) when (ex is OperationCanceledException or PhotoCacheFull) { break; }
            catch (Exception) { }
        }
    }

    private static bool IsClearer(PhotoFrame developed, PhotoFrame embedded) =>
        (long)developed.Display.Width * developed.Display.Height > (long)embedded.Display.Width * embedded.Display.Height;

    private static string Describe(PhotoFrame frame)
    {
        var size = frame.Stage == PhotoStage.Embedded ? frame.Native : frame.Display;
        var name = frame.Stage == PhotoStage.Embedded ? "内嵌预览" : "RAW 解码预览";
        return $"{name} · {size.Width} × {size.Height}";
    }
}
