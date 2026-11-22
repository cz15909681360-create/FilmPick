using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace FilmPick;

public partial class MainWindow : Window
{
    private readonly string appData;
    private readonly Photos photos = new();
    private readonly DispatcherTimer saveTimer;
    private readonly DispatcherTimer seekTimer;
    private string? root;
    private Store? store;
    private Archive? archive;
    private Catalog catalog = new();
    private Session session = new();
    private CopyJournal journal = new();
    private string[] legacyWarning = [];
    private Dictionary<string, string> owners = new(Paths.Comparer);
    private ILookup<string, string> attached = Array.Empty<KeyValuePair<string, string>>().ToLookup(pair => pair.Value, pair => pair.Key, Paths.Comparer);
    private List<Row> rows = [];
    private Kind view = Kind.Photo;
    private bool busy, building, updatingTimeline, sessionDirty, journalAvailable = true;
    private long ticket;
    private VideoHost? host;
    private VideoPlayer? player;
    private string? proxy;
    private bool usingProxy, videoReady;
    private bool videoPaused = true;
    private VideoLoad? videoLoad;
    private sealed record VideoLoad(string Relative, double Start, VideoDecodeMode Decode, bool Resume);
    private double position, duration;
    private Lut? lut;
    private string? lutCopy;
    private bool lutOn;
    private long lutRevision;
    private string? lutFailure;
    private bool lutApplying;
    private string? LutName => ActiveLut?.Name ?? trialLabel ?? (lut == null ? null : Path.GetFileNameWithoutExtension(lut.File));
    private bool forceSoftware;
    private bool scanningInBenchmark;
    private string? frameError;
    private string lastDecoder = "";
    private long readyTicket;
    private int selected, rejected;

    public MainWindow(string state)
    {
        appData = state;
        InitializeComponent();
        saveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(275), DispatcherPriority.Background, (_, _) => SaveSession(), Dispatcher);
        saveTimer.Stop();
        seekTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(65), DispatcherPriority.Background, (_, _) =>
        {
            seekTimer!.Stop();
            if (videoReady) player?.Seek(Timeline.Value, false);
        }, Dispatcher);
        seekTimer.Stop();
        InitializePhotoPreview();
        InitializeBrowse();
        InitializeWorkspace();
        InitializeThumbnailQueue();
        UpdateControls();
        InitializeLibrary();
    }

    internal async void OpenFromArgument(string folder) => await OpenRoot(folder);

    private async void ChooseFolder(object sender, RoutedEventArgs args)
    {
        try
        {
            if (FolderPicker.Pick(new System.Windows.Interop.WindowInteropHelper(this).Handle, root) is { } folder)
                await OpenRoot(folder);
        }
        catch (Exception ex) { Fail("无法选择素材文件夹", ex); }
    }

    private async Task OpenRoot(string folder)
    {
        if (busy || libraryBusy || !SaveSession()) return;
        SetBusy(true, "正在扫描素材…");
        try
        {
            await gradeTask;
            if (!SaveSession()) return;
            await StopVideo();
            var nextRoot = await Task.Run(() => Paths.Root(folder));
            var nextStore = new Store(nextRoot, appData);
            var nextSession = await Task.Run(() => nextStore.Read<Session>("session"));
            var (nextCatalog, nextGrades, gradeError) = await Task.Run(() =>
            {
                GradeOutbox? pending = null;
                string? error = null;
                try
                {
                    pending = nextStore.Read<GradeOutbox>("grade-writes");
                    new GradeWriter(nextRoot).RecoverOriginalNames(pending);
                }
                catch (Exception ex) { error = ex.Message; }
                return (Catalog.Scan(nextRoot, nextSession.Recursive), pending, error);
            });
            if (!Paths.Comparer.Equals(root, nextRoot))
            {
                ++lutRequest;
                loadingLut = false;
                lutOn = false;
                lutFailure = null;
                lutApplying = false;
                player?.SetLut(++lutRevision, null);
            }
            root = nextRoot;
            RefreshThumbnailLook();
            markHistory.Clear();
            markBaselines.Clear();
            markAnchors.Clear();
            store = nextStore;
            archive = new Archive(root, store);
            session = nextSession;
            catalog = nextCatalog;
            OpenGrades(nextGrades, gradeError);
            journal = new CopyJournal();
            ReloadJournal();
            legacyWarning = archive.LegacyWarning();
            catalog.Reconcile(session);
            AttachmentsChanged();
            RecursiveBox.IsChecked = session.Recursive;
            AutoBox.IsChecked = session.AutoAdvance;
            FolderLabel.Text = Path.GetFileName(root);
            FolderLabel.ToolTip = root;
            ResetBrowseFilters();
            StartCaptureIndex();
            BuildQueue(session.View ?? (catalog.Ordered.Any(item => catalog.Main[item] == Kind.Photo) ? Kind.Photo : Kind.Video));
            StartGradeWrites();
            Status(ScanSummary());
            if (legacyWarning.Length != 0)
                DetailDialog.Show(this, "旧版移动记录", "本版本不会恢复旧移动。请先自行核对以下位置；该目录暂不接受复制归档。", legacyWarning);
        }
        catch (Exception ex) { Fail("无法打开素材文件夹", ex); ShowCurrent(); }
        finally { SetBusy(false); Queue.Focus(); }
    }

    private async void ChangeRecursion(object sender, RoutedEventArgs args)
    {
        if (root is null || busy) return;
        var desired = RecursiveBox.IsChecked == true;
        SetBusy(true, "正在重新扫描…");
        try
        {
            await gradeTask;
            await StopVideo();
            var next = await Task.Run(() => Catalog.Scan(root, desired));
            catalog = next;
            session.Recursive = desired;
            catalog.Reconcile(session);
            AttachmentsChanged();
            StartCaptureIndex();
            BuildQueue(view);
            StartGradeWrites();
            QueueSave();
            Status(ScanSummary());
        }
        catch (Exception ex) { RecursiveBox.IsChecked = session.Recursive; Fail("无法重新扫描", ex); ShowCurrent(); }
        finally { SetBusy(false); }
    }

    private void BuildQueue(Kind kind)
    {
        lastPhotoIndex = -1;
        photoDirection = 1;
        view = kind;
        ApplyQueuePresentation();
        if (root != null && session.View != kind) { session.View = kind; QueueSave(); }
        modeItems = catalog.Ordered.Where(path => catalog.Main[path] == view).ToArray();
        RefreshBrowseWidgets();
        var previous = view == Kind.Photo ? session.PhotoPosition : session.VideoPosition;
        RefreshQueue(resetPreview: true, restore: previous == null ? null : catalog.GroupOf(previous));
    }

    private void PhotoMode(object sender, RoutedEventArgs args) { if (!busy && view != Kind.Photo) BuildQueue(Kind.Photo); }
    private void VideoMode(object sender, RoutedEventArgs args) { if (!busy && view != Kind.Video) BuildQueue(Kind.Video); }
    private void ChangeAuto(object sender, RoutedEventArgs args) { session.AutoAdvance = AutoBox.IsChecked == true; QueueSave(); }

    private void SelectRow(object sender, SelectionChangedEventArgs args)
    {
        if (building || busy) return;
        ShowCurrent();
        UpdateControls();
    }

    private void ShowCurrent()
    {
        CancelTransportInput();
        StopPhotoPreview();
        ticket++;
        ResetCaptureOverlay();
        PrioritizeCapture();
        frameError = null;
        videoReady = false;
        videoLoad = null;
        MediaInfo.ToolTip = null;
        seekTimer.Stop();
        usingProxy = false;
        proxy = null;
        if (Queue.SelectedItem is not Row row || root is null)
        {
            Photo.Source = null;
            VideoSlot.Visibility = Visibility.Collapsed;
            _ = player?.Stop();
            MediaName.Text = "预览";
            MediaName.ToolTip = null;
            MediaInfo.Text = view == Kind.Photo ? "照片" : "视频";
            EmptyState(root == null ? "打开文件夹，开始选片" : modeItems.Length == 0 ? "这个队列里没有素材" : "没有匹配的素材",
                root == null ? "在此检查画面。归档只复制，源文件保持原处。" : modeItems.Length == 0 ?
                    "切换照片／视频，或勾选包含子文件夹。" : "可调整筛选条件，或在筛选菜单中清除筛选。");
            if (root != null && modeItems.Length == 0)
            {
                var other = view == Kind.Photo ? Kind.Video : Kind.Photo;
                var count = catalog.Ordered.Count(path => catalog.Main[path] == other);
                if (count > 0)
                {
                    var label = other == Kind.Video ? "视频" : "照片";
                    PreviewHint.Text = $"当前扫描范围内有 {count} 个{label}。";
                    SwitchEmptyMode.Content = "切换到" + label;
                    SwitchEmptyMode.Visibility = Visibility.Visible;
                }
            }
            ResetTimeline();
            return;
        }
        var prior = view == Kind.Photo ? session.PhotoPosition : session.VideoPosition;
        if (!Paths.Comparer.Equals(prior, row.Relative))
        {
            if (view == Kind.Photo) session.PhotoPosition = row.Relative;
            else session.VideoPosition = row.Relative;
            QueueSave();
        }
        MediaName.Text = row.Name;
        MediaName.ToolTip = row.Relative;
        MediaInfo.Text = row.Relative;
        if (catalog.RawCompanions.ContainsKey(row.Relative)) MediaInfo.Text += " · RAW＋JPG";
        ResetTimeline();
        if (view == Kind.Photo)
        {
            if (VideoSlot.Visibility == Visibility.Visible) _ = player?.Stop();
            VideoSlot.Visibility = Visibility.Collapsed;
            Photo.Source = null;
            EmptyState("正在读取照片…", "");
            var index = Queue.SelectedIndex;
            if (lastPhotoIndex >= 0 && index != lastPhotoIndex) photoDirection = Math.Sign(index - lastPhotoIndex);
            lastPhotoIndex = index;
            StartPhotoPreview(row);
        }
        else
        {
            var proxies = attached[row.Relative].Where(file => Path.GetExtension(file).Equals(".lrf", StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
            proxy = proxies.Length == 1 ? proxies[0] : null;
            LoadVideo(row.Relative, 0);
        }
    }

    private async void LoadVideo(string relative, double start, VideoDecodeMode? requested = null, bool resume = false)
    {
        if (root == null) return;
        CancelTransportInput();
        var expected = ++ticket;
        videoLoad = null;
        frameError = null;
        position = start;
        videoPaused = !resume;
        seekTimer.Stop();
        player?.SetCapture(expected, null, 1);
        Photo.Source = null;
        VideoSlot.Visibility = Visibility.Visible;
        Empty.Visibility = Visibility.Collapsed;
        videoReady = false;
        MediaInfo.Text = (usingProxy ? "LRF 代理" : "原片") + "  ·  正在读取…";
        if (host == null)
        {
            host = new VideoHost();
            VideoSlot.Children.Add(host);
        }
        host.Cover(true);
        UpdateControls();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        if (expected != ticket) return;
        var decode = requested ?? (forceSoftware ? VideoDecodeMode.Software : VideoDecodeMode.Default);
        if (requested == null && !forceSoftware && !usingProxy)
            decode = await DecodeOriginal(relative);
        if (expected != ticket) return;
        videoLoad = new(relative, start, decode, resume);
        if (host.VideoWindow == IntPtr.Zero)
        {
            OnVideoFrame(new(expected, "", 0, "无法创建视频预览窗口。"));
            return;
        }
        if (player == null)
        {
            player = new VideoPlayer(host.VideoWindow,
                frame => Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => OnVideoFrame(frame))),
                (id, at, paused, muted) => Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => Progress(id, at, paused, muted))),
                (request, error) => Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => OnVideoLut(request, error))));
            if (lutOn) player.SetLut(lutRevision, lutCopy);
        }
        player.Load(expected, Paths.Below(root, relative), start, decode, resume);
        player.SetSpeed(NormalSpeed);
        UpdateControls();
        await Task.Delay(TimeSpan.FromSeconds(12));
        if (expected == ticket && readyTicket != expected)
            OnVideoFrame(new(expected, "", 0, "等待首帧超时"));
    }

    private async Task<VideoDecodeMode> DecodeOriginal(string relative)
    {
        var indexed = capture.GetValueOrDefault(relative);
        var color = indexed?.Metadata.CameraColor;
        if (indexed == null)
        {
            var captions = attached[relative].Where(file => Path.GetExtension(file).Equals(".srt", StringComparison.OrdinalIgnoreCase))
                .Select(file => new CaptureInput(Paths.Below(root!, file), catalog.Files[file].Stamp)).ToArray();
            if (captions.Length > 0)
            {
                var index = captureIndex;
                using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
                var cancel = deadline.Token;
                try
                {
                    color = (await Task.Run(() => index.ReadCaptionHeader(captions, cancel), cancel).WaitAsync(cancel)).Color;
                }
                catch (OperationCanceledException) { }
            }
        }
        return color is VideoColor.DLog or VideoColor.DLogM ? VideoDecodeMode.Compatible : VideoDecodeMode.Default;
    }

    private void OnVideoFrame(VideoFrame result)
    {
        if (result.Ticket != ticket || view != Kind.Video) return;
        if (result.Error != null)
        {
            CancelTransportInput();
            if (!videoReady && videoLoad is { Decode: VideoDecodeMode.Compatible } initial)
            {
                Status("兼容硬解未能显示首帧，正在用软解重试。");
                LoadVideo(initial.Relative, initial.Start, VideoDecodeMode.Software, initial.Resume);
                return;
            }
            if (usingProxy && Queue.SelectedItem is Row row)
            {
                usingProxy = false;
                proxy = null;
                Status("代理无法播放，已切回原片：" + result.Error);
                LoadVideo(row.Relative, position);
                return;
            }
            frameError = result.Error;
            videoReady = false;
            _ = player?.Stop();
            VideoSlot.Visibility = Visibility.Collapsed;
            EmptyState("无法预览视频", result.Error + "。仍可标记或归档这条素材。");
            readyTicket = result.Ticket;
            UpdateControls();
            return;
        }
        duration = result.Duration;
        updatingTimeline = true;
        Timeline.Maximum = Math.Max(duration, .001);
        updatingTimeline = false;
        host?.Cover(false);
        videoReady = true;
        lastDecoder = result.Decoder;
        readyTicket = result.Ticket;
        MediaInfo.Text = (usingProxy ? "LRF 代理" : "原片") + "  ·  " + (result.Decoder == "no" ? "软解" : result.Decoder);
        UpdateCaptureOverlay();
        UpdateControls();
    }

    private void Progress(long id, double at, bool paused, bool muted)
    {
        if (id != ticket || !videoReady || view != Kind.Video || busy) return;
        position = at;
        videoPaused = paused;
        UpdateCaptureOverlay();
        updatingTimeline = true;
        if (!seekTimer.IsEnabled && Mouse.LeftButton != MouseButtonState.Pressed) Timeline.Value = at;
        updatingTimeline = false;
        TimeText.Text = FormatTime(at) + " / " + FormatTime(duration);
        PlayButton.Content = paused ? "播放" : "暂停";
        SoundButton.Content = muted ? "静音" : "有声";
    }

    private void ResetTimeline()
    {
        position = duration = 0;
        videoPaused = true;
        updatingTimeline = true;
        Timeline.Value = 0;
        Timeline.Maximum = 1;
        updatingTimeline = false;
        TimeText.Text = "0:00 / 0:00";
        PlayButton.Content = "播放";
    }

    private static string FormatTime(double value)
    {
        var span = TimeSpan.FromSeconds(Math.Clamp(double.IsFinite(value) ? value : 0, 0, 36000000));
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}" : $"{span.Minutes}:{span.Seconds:00}";
    }

    private void SeekTime(object sender, RoutedPropertyChangedEventArgs<double> args)
    {
        if (!updatingTimeline && videoReady && !busy) { seekTimer.Stop(); seekTimer.Start(); }
    }

    private void PlayButtonClick(object sender, RoutedEventArgs args) { if (videoReady && !busy) player?.TogglePlayback(); }
    private void Sound(object sender, RoutedEventArgs args) { if (videoReady && !busy) player?.ToggleMute(); }
    private void ProxyClick(object sender, RoutedEventArgs args) => ToggleProxy();

    private async void RetrySoftwareClick(object sender, RoutedEventArgs args)
    {
        if (!CanRetrySoftware || videoLoad == null) return;
        var expected = ticket;
        var relative = videoLoad.Relative;
        var snapshot = (Position: position, Paused: videoPaused);
        if (videoReady && player != null)
        {
            try { snapshot = await player.ReadPlayback().WaitAsync(TimeSpan.FromMilliseconds(250)); }
            catch (TimeoutException) { }
        }
        if (expected != ticket || busy) return;
        LoadVideo(relative, snapshot.Position, VideoDecodeMode.Software, !snapshot.Paused);
    }

    private bool CanRetrySoftware => !busy && view == Kind.Video && videoLoad is { Decode: not VideoDecodeMode.Software } &&
        (videoReady ? lastDecoder != "no" : frameError != null);
    private void ToggleProxy()
    {
        if (busy || view != Kind.Video || proxy == null || Queue.SelectedItem is not Row row) return;
        usingProxy = !usingProxy;
        frameError = null;
        LoadVideo(usingProxy ? proxy : row.Relative, position);
    }

    private void ToggleLutButton(object sender, RoutedEventArgs args) => ToggleLut();
    private async void ToggleLut()
    {
        if (busy || libraryBusy || (lut == null && selectedLut == null)) return;
        if (loadingLut)
        {
            ++lutRequest;
            loadingLut = false;
            lutOn = false;
            ApplyLut();
            return;
        }
        if (!lutOn && (lut == null || lutFailure != null) && selectedLut is { } saved)
        {
            await ApplySavedLut(saved);
            return;
        }
        lutOn = !lutOn;
        ApplyLut();
    }

    private void ApplyLut()
    {
        lutFailure = null;
        RefreshThumbnailLook();
        lutApplying = lutOn && Queue.SelectedItem is Row;
        player?.SetLut(++lutRevision, lutOn ? lutCopy : null);
        if (view == Kind.Photo && Queue.SelectedItem is Row)
        {
            frameError = null;
            RefreshPhotoLook(++ticket);
        }
        Status(lutOn && lut != null ? "LUT 预览：" + (ActiveLut?.Name ?? trialLabel ?? Path.GetFileName(lut.File)) + " · 不写入素材" : "已关闭 LUT 预览");
        UpdateControls();
    }

    private void OnVideoLut(VideoLut request, string? error)
    {
        if (request.Revision != lutRevision || !lutOn || view != Kind.Video) return;
        if (error != null) LutFailed(error);
        else { lutApplying = false; UpdateLutLabel(); }
    }

    private void LutFailed(string error)
    {
        lutFailure = error;
        lutApplying = false;
        lutOn = false;
        player?.SetLut(++lutRevision, null);
        RefreshThumbnailLook();
        Status("LUT 应用失败，已回到原貌：" + error);
        UpdateLutLabel();
    }

    private void UpdateLutLabel()
    {
        var name = LutName;
        var state = loadingLut || lutApplying ? "正在应用" : lutFailure != null ? "应用失败" : lutOn ? "LUT 开" : "LUT 关";
        var label = LutToggle.Content as TextBlock;
        if (label == null)
        {
            label = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 192 };
            LutToggle.Content = label;
        }
        label.Text = name == null ? KeyHint(state, WorkAction.Lut).Split(" / ")[0] : state + " · " + name;
        LutToggle.Foreground = (Brush)FindResource(lutFailure != null ? "Reject" : lutOn ? "Accent" : "Ink");
        LutToggle.ToolTip = lutFailure == null ? KeyHint($"{state} · {name ?? "未选择"}", WorkAction.Lut) : $"{name}\n{lutFailure}";
    }

    private void Previous(object sender, RoutedEventArgs args) => MoveCursor(-1);
    private void Next(object sender, RoutedEventArgs args) => MoveCursor(1);
    private void MoveCursor(int delta)
    {
        if (busy || rows.Count == 0) return;
        Queue.SelectedIndex = Math.Clamp(Queue.SelectedIndex + delta, 0, rows.Count - 1);
        if (Queue.SelectedItem is Row row) Queue.ScrollIntoView(row);
    }

    private Mark GetMark(string relative)
    {
        if (catalog.Main.GetValueOrDefault(relative) == Kind.Photo)
        {
            var grade = PendingFor(relative)?.Change.Flag ?? publishedGrades.GetValueOrDefault(relative)?.Flag ??
                capture.GetValueOrDefault(relative)?.Metadata.Flag;
            if (grade is { } flag) return flag;
        }
        return session.Marks.TryGetValue(relative, out var saved) && catalog.Files.GetValueOrDefault(relative)?.Stamp == saved.Stamp
            ? saved.Mark : Mark.None;
    }
    private void PickClick(object sender, RoutedEventArgs args) => SetMark(Mark.Pick);
    private void RejectClick(object sender, RoutedEventArgs args) => SetMark(Mark.Reject);
    private void UnmarkClick(object sender, RoutedEventArgs args) => SetMark(Mark.None);
    private void SetMark(Mark mark)
    {
        if (busy || undoingMark || Queue.SelectedItem is not Row row) return;
        var previous = GetMark(row.Relative);
        var baseline = PreviousMark(row.Relative, previous);
        if (!ApplyMark(row.Relative, mark))
        {
            if (PendingFor(row.Relative) == null) markBaselines.Remove(row.Relative);
            return;
        }
        if (!baseline.IsCompletedSuccessfully || baseline.Result != mark)
        {
            markHistory.Push(new(row.Relative, baseline, mark));
            markAnchors[row.Relative] = catalog.Files[row.Relative].Stamp;
        }
        var advance = mark != Mark.None && session.AutoAdvance;
        if (Order.Field == SortField.Mark || Filter.Marks != 0) RefreshQueue(advance: advance);
        else
        {
            UpdateControls();
            if (advance) MoveCursor(1);
        }
    }

    private bool ApplyMark(string relative, Mark mark)
    {
        var previous = GetMark(relative);
        if (UsesStandardGrades(relative))
        {
            if (!QueueGrade(relative, new(Flag: mark))) return false;
        }
        else if (mark == Mark.None) session.Marks.Remove(relative);
        else session.Marks[relative] = new(mark, catalog.Files[relative].Stamp);
        CountChange(previous, mark);
        if (visibleRows.TryGetValue(relative, out var row)) row.Mark = mark;
        QueueSave();
        return true;
    }

    private void AttachmentsChanged()
    {
        owners = catalog.Owners(session);
        attached = owners.ToLookup(pair => pair.Value, pair => pair.Key, Paths.Comparer);
        RecountMarks();
    }

    private void RecountMarks()
    {
        selected = rejected = 0;
        foreach (var name in catalog.Ordered) CountChange(Mark.None, GetMark(name));
    }

    private void CountChange(Mark previous, Mark next)
    {
        if (previous == next) return;
        if (previous == Mark.Pick) selected--;
        if (previous == Mark.Reject) rejected--;
        if (next == Mark.Pick) selected++;
        if (next == Mark.Reject) rejected++;
    }

    private void EditAttachments(object sender, RoutedEventArgs args)
    {
        if (busy || Queue.SelectedItem is not Row row) return;
        EditLinks(row.Relative);
        ShowCurrent();
        UpdateControls();
    }

    private void EditLinks(string owner)
    {
        var dialog = new AttachmentDialog(owner, catalog, session, () =>
        {
            AttachmentsChanged();
            StartCaptureIndex();
            RefreshQueue();
            QueueSave();
        }) { Owner = this };
        dialog.ShowDialog();
    }

    private async void ReviewArchive(object sender, RoutedEventArgs args)
    {
        if (busy || archive == null || root == null || journal.Pending != null || !journalAvailable || legacyWarning.Length != 0) return;
        SetBusy(true, "正在核对分级和待写记录…");
        try { await gradeTask; await indexTask; }
        finally { SetBusy(false); }
        if (session.Links.Any(pair => catalog.Main.ContainsKey(pair.Value.Owner) && GetMark(catalog.GroupOf(pair.Value.Owner)) != Mark.None &&
            !catalog.Files.ContainsKey(pair.Key)))
        {
            Notify("请先勾选「包含子文件夹」，以便核对跨目录的附属文件。", "尚未归档");
            return;
        }
        var plan = archive.Plan(catalog, session, GetMark);
        var skipped = plan.Where(group => gradeOutbox.Entries.ContainsKey(group.Main)).Select(group =>
            group.Main + " · " + gradeFailures.GetValueOrDefault(group.Main, "分级尚未写入")).ToArray();
        var groups = plan.Where(group => !gradeOutbox.Entries.ContainsKey(group.Main)).ToArray();
        if (groups.Length == 0)
        {
            if (skipped.Length > 0) DetailDialog.Show(this, "分级尚未写入", "请处理待写记录后再归档。", skipped);
            else Status("扫描范围内没有已选或已排除的素材。");
            return;
        }
        var unassigned = catalog.Files.Keys.Where(file => !owners.ContainsKey(file) && catalog.IsKnownAttachment(file)).Order().ToArray();
        var hidden = groups.Count(group => !visibleRows.ContainsKey(group.Main));
        var dialog = new ReviewDialog(root, groups, unassigned, hidden, skipped) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        await ExecuteArchive(() => archive.Run(groups), "复制归档");
    }

    private async void RecoverArchive(object sender, RoutedEventArgs args)
    {
        if (busy || archive == null || journal.Pending == null) return;
        await ExecuteArchive(archive.Recover, "恢复");
    }

    private async Task ExecuteArchive(Func<ArchiveResult> operation, string name)
    {
        if (root == null || !SaveSession()) return;
        SetBusy(true, "正在" + name + "，请勿关闭窗口…");
        try
        {
            await StopVideo();
            var result = await Task.Run(operation);
            ReloadJournal();
            ShowCurrent();
            var summary = name == "恢复" ? "已处理未完成的复制记录。" :
                $"新复制 {result.Completed} 组 · 已完整存在 {result.Unchanged} 组 · 冲突 {result.Conflicts} 组 · 其他失败 {Math.Max(0, result.Issues.Count - result.Conflicts)} 项";
            Status(result.RecoveryNeeded ? "复制已暂停，请检查目标侧的未完成记录。" : summary);
            if (result.Issues.Count > 0) DetailDialog.Show(this, name + (result.RecoveryNeeded ? "需要恢复" : "报告"),
                summary + "。源文件保持原处。", result.Issues);
        }
        catch (Exception ex) { ReloadJournal(); Fail(name + "未完成", ex); ShowCurrent(); }
        finally { SetBusy(false); }
    }

    private async Task StopVideo()
    {
        CancelTransportInput();
        ticket++;
        videoLoad = null;
        videoReady = false;
        StopPhotoPreview();
        host?.Cover(true);
        await DrainThumbnails();
        if (player != null) await player.Stop();
    }

    private void ReloadJournal()
    {
        try { journal = archive?.ReadJournal() ?? new CopyJournal(); journalAvailable = true; }
        catch (Exception ex)
        {
            journalAvailable = false;
            Notify("复制记录无法读取，归档功能已停用。\n" + ex.Message, "归档记录");
        }
    }

    private void EmptyState(string title, string hint)
    {
        SwitchEmptyMode.Visibility = Visibility.Collapsed;
        PreviewTitle.Text = title;
        PreviewHint.Text = hint;
        Empty.Visibility = Visibility.Visible;
    }

    private void JumpEmptyMode(object sender, RoutedEventArgs args)
    {
        if (busy || root == null || modeItems.Length != 0 || SwitchEmptyMode.Visibility != Visibility.Visible) return;
        BuildQueue(view == Kind.Photo ? Kind.Video : Kind.Photo);
        Queue.Focus();
    }

    private string ScanSummary() => legacyWarning.Length != 0 ? "有旧版移动记录待核对；不会自动修改文件。" :
        journal.Pending != null ? "发现未完成的复制，请先恢复。" :
        catalog.Warnings.Count > 0 ? $"已扫描 {catalog.Ordered.Count} 条素材 · {catalog.Warnings.Count} 项无法读取" :
        $"已扫描 {catalog.Ordered.Count} 条素材 · 归档只复制";

    private void SetBusy(bool value, string? message = null)
    {
        if (value) CancelTransportInput();
        busy = value;
        if (value) PauseThumbnails();
        else ScheduleThumbnails();
        if (message != null) Status(message);
        UpdateControls();
    }

    private void UpdateControls()
    {
        var hasItem = Queue.SelectedItem is Row;
        PickTotals.Text = $"已选 {selected}   ·   已排除 {rejected}";
        UpdateBrowseIndicators();
        PhotoTab.Foreground = (Brush)FindResource(view == Kind.Photo ? "Accent" : "Ink");
        VideoTab.Foreground = (Brush)FindResource(view == Kind.Video ? "Accent" : "Ink");
        Queue.IsEnabled = PhotoTab.IsEnabled = VideoTab.IsEnabled = AutoBox.IsEnabled = RecursiveBox.IsEnabled = !busy && root != null;
        QueueTextButton.IsEnabled = QueueGridButton.IsEnabled = !busy;
        OpenButton.IsEnabled = !busy && !libraryBusy;
        ArchiveButton.IsEnabled = !busy && journalAvailable && journal.Pending == null && legacyWarning.Length == 0 &&
            (selected + rejected > 0 || captureLoading);
        var gradeCount = gradeOutbox.Entries.Keys.Count(catalog.Main.ContainsKey);
        GradeStatus.Visibility = gradeCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        GradeStatus.Content = $"分级待写 · {gradeCount}";
        GradeStatus.IsEnabled = !busy;
        RecoveryButton.IsEnabled = !busy && journalAvailable;
        RecoveryButton.Visibility = journal.Pending != null ? Visibility.Visible : Visibility.Collapsed;
        PickButton.IsEnabled = RejectButton.IsEnabled = UnmarkButton.IsEnabled = AttachmentButton.IsEnabled = !busy && hasItem;
        AttachmentButton.Content = Queue.SelectedItem is Row row ? "附属文件 · " + attached[row.Relative].Count() : "附属文件";
        PlayButton.IsEnabled = Timeline.IsEnabled = SoundButton.IsEnabled = !busy && videoReady;
        SpeedButton.IsEnabled = SpeedMenuButton.IsEnabled = !busy && videoReady;
        SwitchEmptyMode.IsEnabled = !busy;
        UpdateBottomLayout();
        UpdateSpeedControl();
        RetrySoftwareButton.Visibility = CanRetrySoftware ? Visibility.Visible : Visibility.Collapsed;
        ProxyButton.IsEnabled = !busy && view == Kind.Video && proxy != null;
        ProxyButton.Content = KeyHint(usingProxy ? "回原片" : "切代理", WorkAction.Proxy).Split(" / ")[0];
        ProxyButton.ToolTip = KeyHint(usingProxy ? "回原片" : "切代理", WorkAction.Proxy);
        LutToggle.IsEnabled = !busy && !libraryBusy && (lut != null || selectedLut != null);
        UpdateLutLabel();
        UpdateLibraryControls();
    }

    private void QueueSave()
    {
        if (store == null || scanningInBenchmark) return;
        sessionDirty = true;
        saveTimer.Stop();
        saveTimer.Start();
    }

    private bool SaveSession()
    {
        saveTimer.Stop();
        if (store == null || !sessionDirty) return true;
        try { store.Write("session", session); sessionDirty = false; return true; }
        catch (Exception ex) { Status("无法保存选片进度：" + ex.Message); return false; }
    }

    private void OnClosing(object? sender, CancelEventArgs args)
    {
        if (busy || libraryBusy || loadingLut) { args.Cancel = true; return; }
        if (!gradeTask.IsCompleted)
        {
            args.Cancel = true;
            FinishGradeWritesAndClose();
            return;
        }
        if (!SaveSession())
            args.Cancel = MessageBox.Show(this, "进度未保存。仍要退出？", "FilmPick", MessageBoxButton.YesNo) != MessageBoxResult.Yes;
    }

    private void OnClosed(object? sender, EventArgs args)
    {
        CancelTransportInput();
        PauseThumbnails();
        thumbnailVideo.Stop();
        ticket++;
        indexCancellation?.Cancel();
        trackCancellation?.Cancel();
        indexTimer.Stop();
        searchTimer.Stop();
        StopPhotoPreview();
        player?.Dispose();
        CloseLutLibrary();
    }
    private void Status(string message) { StatusLine.Text = message; StatusLine.ToolTip = message; }
    private void Fail(string title, Exception ex) { Status(title + "：" + ex.Message); Notify(ex.Message, title); }
    private void Notify(string message, string title) => MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    private sealed class Row(string relative, int number, Mark mark) : INotifyPropertyChanged
    {
        private Mark value = mark;
        private System.Windows.Media.Imaging.BitmapSource? thumbnail;
        public System.Windows.Media.Imaging.BitmapSource? Thumbnail
        {
            get => thumbnail;
            set
            {
                if (ReferenceEquals(thumbnail, value)) return;
                thumbnail = value;
                PropertyChanged?.Invoke(this, new(nameof(Thumbnail)));
            }
        }
        public string Relative { get; } = relative;
        public string Name => Path.GetFileName(Relative);
        public string Number => number.ToString("000");
        public string Badge => Mark switch { Mark.Pick => "已选", Mark.Reject => "排除", _ => "" };
        public string BadgeColor => Mark == Mark.Pick ? "#82E6CF" : "#FFC194";
        private int? rating;
        public string Stars => rating is > 0 ? $"{rating}★" : "";
        public int? Rating
        {
            get => rating;
            set
            {
                if (rating == value) return;
                rating = value;
                PropertyChanged?.Invoke(this, new(nameof(Stars)));
            }
        }
        public Mark Mark
        {
            get => value;
            set
            {
                this.value = value;
                PropertyChanged?.Invoke(this, new(nameof(Badge)));
                PropertyChanged?.Invoke(this, new(nameof(BadgeColor)));
            }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
