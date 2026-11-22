using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace FilmPick;

public partial class MainWindow
{
    private BrowsePreferences browse = new();
    private Store browseStore = null!;
    private BrowseFilter photoFilter = new(), videoFilter = new();
    private BrowseFilter Filter
    {
        get => view == Kind.Photo ? photoFilter : videoFilter;
        set { if (view == Kind.Photo) photoFilter = value; else videoFilter = value; }
    }
    private BrowseOrder Order => view == Kind.Photo ? browse.Photo : browse.Video;
    private bool QueueDependsOnCapture =>
        Order.Field is SortField.Date or SortField.Rating or SortField.Mark ||
        Filter.Date != DateFilter.All || Filter.Stars != 0 || Filter.Marks != 0;
    private bool updatingBrowse;
    private DispatcherTimer searchTimer = null!, indexTimer = null!;
    private CaptureIndex captureIndex = new();
    private CancellationTokenSource? indexCancellation;
    private Task indexTask = Task.CompletedTask;
    private bool captureLoading;
    private readonly Dictionary<string, IndexedCapture> capture = new(Paths.Comparer);
    private string[] modeItems = [];
    private Dictionary<string, Row> visibleRows = new(Paths.Comparer);

    private void InitializeBrowse()
    {
        browseStore = new Store("FilmPick desktop preferences", appData);
        try
        {
            var saved = browseStore.Read<BrowsePreferences>("browse");
            browse = new(ValidOrder(saved.Photo), ValidOrder(saved.Video), saved.ShowCapture);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        searchTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(130), DispatcherPriority.Background, (_, _) =>
        {
            searchTimer!.Stop();
            if (!busy) RefreshQueue();
        }, Dispatcher);
        indexTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(240), DispatcherPriority.Background, (_, _) =>
        {
            if (busy) return;
            indexTimer!.Stop();
            RefreshQueue();
        }, Dispatcher);
        searchTimer.Stop();
        indexTimer.Stop();
        Viewport.SizeChanged += (_, _) => UpdateCaptureOverlay();
        DpiChanged += (_, _) => UpdateCaptureOverlay();
        RefreshBrowseWidgets();
    }

    private static BrowseOrder ValidOrder(BrowseOrder? order) =>
        order != null && Enum.IsDefined(order.Field) ? order : new();

    private void SaveBrowse()
    {
        try { browseStore.Write("browse", browse); }
        catch (Exception error) { Status("无法保存浏览设置：" + error.Message); }
    }

    private void RefreshBrowseWidgets()
    {
        updatingBrowse = true;
        NameFilter.Text = Filter.Name;
        OrderField.SelectedIndex = (int)Order.Field;
        OrderDirection.Content = Order.Descending ? "↓" : "↑";
        OrderDirection.ToolTip = Order.Descending ? "当前为倒序" : "当前为正序";
        UpdateFilterButtons();
        CaptureButton.Content = browse.ShowCapture ? "参数 ✓" : KeyHint("参数", WorkAction.Capture).Split(" / ")[0];
        CaptureButton.ToolTip = KeyHint(browse.ShowCapture ? "隐藏拍摄参数" : "显示拍摄参数", WorkAction.Capture);
        updatingBrowse = false;
    }

    private bool HasAdvancedFilter() => Filter.Date != DateFilter.All || Filter.Stars != 0 || Filter.Marks != 0;

    private void UpdateFilterButtons()
    {
        var advanced = HasAdvancedFilter();
        FilterButton.Content = advanced ? "筛选 •" : "筛选";
        ClearFiltersButton.Visibility = Filter.Name.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearFiltersButton.ToolTip = "清除文件名搜索";
    }

    private void SearchQueue(object sender, TextChangedEventArgs args)
    {
        if (updatingBrowse || searchTimer == null) return;
        Filter = Filter with { Name = NameFilter.Text.Trim() };
        UpdateFilterButtons();
        searchTimer.Stop();
        searchTimer.Start();
    }

    private void ChangeOrder(object sender, SelectionChangedEventArgs args)
    {
        if (browseStore == null || updatingBrowse || busy || OrderField.SelectedIndex < 0) return;
        SetOrder(Order with { Field = (SortField)OrderField.SelectedIndex });
    }

    private void ReverseOrder(object sender, RoutedEventArgs args) => SetOrder(Order with { Descending = !Order.Descending });

    private void SetOrder(BrowseOrder order)
    {
        browse = view == Kind.Photo ? browse with { Photo = order } : browse with { Video = order };
        RefreshBrowseWidgets();
        RefreshQueue();
        SaveBrowse();
        Queue.Focus();
    }

    private void OpenQueueFilters(object sender, RoutedEventArgs args)
    {
        DateMode.SelectedIndex = (int)Filter.Date;
        DateBounds.Visibility = Filter.Date == DateFilter.Range ? Visibility.Visible : Visibility.Collapsed;
        DateFrom.SelectedDate = Filter.From?.ToDateTime(TimeOnly.MinValue);
        DateTo.SelectedDate = Filter.To?.ToDateTime(TimeOnly.MinValue);
        ShowMask(StarFilters, Filter.Stars);
        ShowMask(MarkFilters, Filter.Marks);
        FilterError.Text = "";
        FilterPopup.IsOpen = true;
        DateMode.Focus();
    }

    private void FilterDateModeChanged(object sender, SelectionChangedEventArgs args)
    {
        if (DateBounds != null) DateBounds.Visibility = DateMode.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyQueueFilters(object sender, RoutedEventArgs args)
    {
        var kind = (DateFilter)DateMode.SelectedIndex;
        var from = DateFrom.SelectedDate is { } begin ? DateOnly.FromDateTime(begin) : (DateOnly?)null;
        var to = DateTo.SelectedDate is { } end ? DateOnly.FromDateTime(end) : (DateOnly?)null;
        if (kind == DateFilter.Range && ((from == null && to == null) || from > to))
        {
            FilterError.Text = "请输入有效的日期区间，至少填写一端。";
            return;
        }
        Filter = new(NameFilter.Text.Trim(), kind, from, to, Mask(StarFilters), Mask(MarkFilters));
        CloseQueueFilters();
    }

    private void ClearQueueFilters(object sender, RoutedEventArgs args)
    {
        Filter = new();
        CloseQueueFilters();
    }

    private void ClearNameSearch(object sender, RoutedEventArgs args)
    {
        searchTimer.Stop();
        Filter = Filter with { Name = "" };
        CloseQueueFilters();
    }

    private void CloseQueueFilters()
    {
        FilterPopup.IsOpen = false;
        RefreshBrowseWidgets();
        RefreshQueue();
        Queue.Focus();
    }

    private static int Mask(Panel panel) => panel.Children.OfType<CheckBox>()
        .Select((box, index) => box.IsChecked == true ? 1 << index : 0).Aggregate(0, (left, right) => left | right);

    private static void ShowMask(Panel panel, int mask)
    {
        var index = 0;
        foreach (var box in panel.Children.OfType<CheckBox>()) box.IsChecked = (mask & (1 << index++)) != 0;
    }

    private int? RatingOf(string relative)
    {
        var external = capture.GetValueOrDefault(relative)?.Metadata.Rating;
        if (catalog.Main.GetValueOrDefault(relative) == Kind.Photo)
        {
            var grade = PendingFor(relative)?.Change.Stars ?? publishedGrades.GetValueOrDefault(relative)?.Stars ?? external;
            if (grade.HasValue) return grade;
        }
        if (catalog.RawCompanions.TryGetValue(relative, out var raw) && session.Ratings.TryGetValue(raw, out var rawRating) &&
            rawRating.Matches(catalog.Files[raw].Stamp))
            return rawRating.Stars;
        if (session.Ratings.TryGetValue(relative, out var saved) && saved.Matches(catalog.Files[relative].Stamp)) return saved.Stars;
        if (external.HasValue) return external;
        return capture.ContainsKey(relative) || !captureLoading ? 0 : null;
    }

    private void ClickRating(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: string value } && int.TryParse(value, out var stars)) SetRating(stars);
        Queue.Focus();
    }

    private void SetRating(int stars)
    {
        if (busy || Queue.SelectedItem is not Row row) return;
        if (UsesStandardGrades(row.Relative))
        {
            if (!QueueGrade(row.Relative, new(Stars: stars))) return;
        }
        else
        {
            var key = catalog.RawCompanions.GetValueOrDefault(row.Relative) ?? row.Relative;
            session.Ratings[key] = new(stars, catalog.Files[key].Stamp);
        }
        row.Rating = stars;
        QueueSave();
        if (Order.Field == SortField.Rating || Filter.Stars != 0) RefreshQueue();
        else UpdateControls();
    }

    private void RefreshQueue(bool resetPreview = false, bool advance = false, string? restore = null)
    {
        if (root == null) return;
        var old = (Queue.SelectedItem as Row)?.Relative;
        var filter = Filter;
        var order = Order;
        var listed = new List<BrowseItem>();
        foreach (var path in modeItems)
        {
            var indexed = capture.GetValueOrDefault(path);
            var item = new BrowseItem(path, catalog.RawCompanions.GetValueOrDefault(path), GetMark(path),
                RatingOf(path), indexed?.Metadata.Date, indexed == null && captureLoading);
            if (filter.Includes(item.Name, Path.GetFileName(item.Companion), item.Date, item.Rating, item.Mark, item.Pending))
                listed.Add(item);
        }
        listed.Sort((left, right) => Browse.Compare(left, right, order));
        var visible = listed.Select(item => item.Relative).ToHashSet(Paths.Comparer);
        var selectedPath = restore != null && visible.Contains(restore) ? restore :
            Browse.Next(resetPreview ? null : old, rows.Select(row => row.Relative).ToArray(), visible, advance);
        building = true;
        rows = listed.Select((item, index) => new Row(item.Relative, index + 1, item.Mark) { Rating = item.Rating }).ToList();
        visibleRows = rows.ToDictionary(row => row.Relative, Paths.Comparer);
        Queue.ItemsSource = rows;
        var at = selectedPath == null ? 0 : rows.FindIndex(row => Paths.Comparer.Equals(row.Relative, selectedPath));
        Queue.SelectedIndex = rows.Count == 0 ? -1 : Math.Max(0, at);
        if (Queue.SelectedItem is Row row) Queue.ScrollIntoView(row);
        building = false;
        if (resetPreview || !Paths.Comparer.Equals(old, (Queue.SelectedItem as Row)?.Relative)) ShowCurrent();
        else
        {
            if (view == Kind.Photo) lastPhotoIndex = Queue.SelectedIndex;
            PrioritizeCapture();
        }
        UpdateControls();
        ScheduleThumbnails();
    }

    private void ResetBrowseFilters()
    {
        FilterPopup.IsOpen = false;
        photoFilter = videoFilter = new();
        captureIndex = new();
    }

    private void StartCaptureIndex()
    {
        if (root == null) return;
        var previousCancellation = indexCancellation;
        previousCancellation?.Cancel();
        indexTimer.Stop();
        capture.Clear();
        publishedGrades.Clear();
        RecountMarks();
        var directory = root;
        var currentCatalog = catalog;
        var files = new Dictionary<string, MediaFile>(catalog.Files, Paths.Comparer);
        var attachments = attached;
        var engine = captureIndex;
        var token = new CancellationTokenSource();
        var cancel = token.Token;
        var previous = indexTask;
        indexCancellation = token;
        captureLoading = currentCatalog.Ordered.Count > 0;
        indexTask = Task.Run(async () =>
        {
            try
            {
                await previous.ConfigureAwait(false);
                previousCancellation?.Dispose();
                cancel.ThrowIfCancellationRequested();
                var assets = currentCatalog.Ordered.Select(path => CaptureAssetFor(directory, currentCatalog, files, attachments, path)).ToArray();
                await engine.Read(assets, batch =>
                {
                    // The dispatcher acknowledgment bounds queued results during fast scans.
                    Dispatcher.Invoke(() =>
                    {
                        if (indexCancellation != token) return;
                        var current = (Queue.SelectedItem as Row)?.Relative;
                        var refreshCurrent = false;
                        foreach (var (path, value) in batch)
                        {
                            var prior = GetMark(path);
                            capture[path] = value;
                            var mark = GetMark(path);
                            CountChange(prior, mark);
                            if (visibleRows.TryGetValue(path, out var row))
                            {
                                row.Rating = RatingOf(path);
                                if (prior != mark) row.Mark = mark;
                            }
                            refreshCurrent |= Paths.Comparer.Equals(path, current);
                        }
                        if (QueueDependsOnCapture)
                        {
                            if (!indexTimer.IsEnabled) indexTimer.Start();
                        }
                        if (refreshCurrent)
                        {
                            UpdateCaptureOverlay();
                        }
                        UpdateControls();
                    }, DispatcherPriority.Background, cancel);
                }, cancel).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                _ = Dispatcher.BeginInvoke(() =>
                {
                    if (indexCancellation == token) Status("拍摄信息读取失败：" + error.Message);
                });
            }
            finally
            {
                _ = Dispatcher.BeginInvoke(() =>
                {
                    if (indexCancellation != token) return;
                    captureLoading = false;
                    if (QueueDependsOnCapture)
                    {
                        if (busy) indexTimer.Start();
                        else
                        {
                            indexTimer.Stop();
                            RefreshQueue();
                        }
                    }
                    UpdateControls();
                    UpdateCaptureOverlay();
                });
            }
        });
    }

    private static CaptureAsset CaptureAssetFor(string directory, Catalog current, Dictionary<string, MediaFile> files,
        ILookup<string, string> attachments, string path)
    {
        CaptureInput Input(string name) => new(Paths.Below(directory, name), files[name].Stamp);
        var raw = current.RawCompanions.GetValueOrDefault(path);
        var metadata = attachments[path].Where(file => Path.GetExtension(file).Equals(".xmp", StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => XmpRank(file, path, raw)).ThenBy(file => file, Paths.Comparer);
        var video = current.Main[path] == Kind.Video;
        CaptureInput[] MediaSidecars(string extension) => video
            ? attachments[path].Where(file => Path.GetExtension(file).Equals(extension, StringComparison.OrdinalIgnoreCase)).Select(Input).ToArray()
            : [];
        return new(path, Input(path), raw == null ? null : Input(raw), metadata.Select(Input).ToArray(), MediaSidecars(".srt"), MediaSidecars(".xml"));
    }

    private static int XmpRank(string file, string primary, string? raw) =>
        raw != null && Paths.Comparer.Equals(file, raw + ".xmp") ? 0 :
        Paths.Comparer.Equals(file, Path.ChangeExtension(primary, ".xmp")) ? 1 :
        Paths.Comparer.Equals(file, primary + ".xmp") ? 2 : 3;

    private void PrioritizeCapture()
    {
        var at = Math.Max(0, Queue.SelectedIndex);
        captureIndex.Prioritize(rows.Skip(at).Take(13).Select(row => row.Relative)
            .Concat(modeItems.Take(12)).Distinct(Paths.Comparer).ToArray());
    }

    private void UpdateBrowseIndicators()
    {
        var rating = Queue.SelectedItem is Row row ? RatingOf(row.Relative) : null;
        for (var i = 0; i < 5; i++)
        {
            var star = (Button)RatingStrip.Children[i];
            var filled = rating > i;
            star.Content = filled ? "★" : "☆";
            star.Foreground = (System.Windows.Media.Brush)FindResource(filled ? "Accent" : "Quiet");
        }
        RatingStrip.IsEnabled = !busy && Queue.SelectedItem is Row;
        GradeLocation.Text = Queue.SelectedItem is Row current ? GradeStorageLabel(current.Relative) : "";
        GradeLocation.ToolTip = GradeLocation.Text;
        NameFilter.IsEnabled = OrderField.IsEnabled = FilterButton.IsEnabled = OrderDirection.IsEnabled = !busy && root != null;
        ClearFiltersButton.IsEnabled = !busy && root != null;
        var totals = modeItems.Length == 0 ? "队列为空" : $"匹配 {rows.Count} / {modeItems.Length} 条";
        if (Queue.SelectedIndex >= 0) totals += $" · 第 {Queue.SelectedIndex + 1} 条";
        Totals.Text = totals + (captureLoading ? "\n读取拍摄信息…" : "");
    }

    private bool BrowseTextFocus(KeyEventArgs args) =>
        FilterPopup.IsOpen || OrderField.IsDropDownOpen || LibraryPanel.IsKeyboardFocusWithin ||
        Keyboard.FocusedElement is TextBoxBase or DatePicker or Calendar or ComboBox ||
        args.OriginalSource is TextBoxBase or Slider or Thumb or MenuItem;
}
