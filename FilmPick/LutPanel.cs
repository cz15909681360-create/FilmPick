using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace FilmPick;

public partial class MainWindow
{
    private LutLibrary library = null!;
    private LutThumbnails thumbnails = null!;
    private string? libraryError, selectedLut, trialLabel;
    private string? trialDirectory;
    private bool libraryBusy, loadingLut, fillingCards;
    private long lutRequest;
    private string[] tagFilter = [];
    private readonly CancellationTokenSource thumbnailLife = new();
    private LutEntry? ActiveLut => selectedLut == null ? null : library?.Index.Entries.FirstOrDefault(entry => entry.Hash == selectedLut);

    private void InitializeLibrary()
    {
        library = new LutLibrary(appData);
        thumbnails = new LutThumbnails(library);
        try
        {
            library.Load();
            selectedLut = library.Index.Selected;
        }
        catch (Exception ex) { libraryError = "无法读取 LUT 库，未改写原索引：" + ex.Message; }
        UpdateControls();
    }

    private void ToggleLibrary(object sender, RoutedEventArgs args) => SetLibraryVisible(LibraryPanel.Visibility != Visibility.Visible);

    private void SetLibraryVisible(bool visible)
    {
        LibraryPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ApplyWorkspaceWidths();
        LibraryButton.Content = visible ? "收起 LUT 库" : "LUT 库";
        LibraryButton.Foreground = (Brush)FindResource(visible ? "Accent" : "Ink");
        if (visible) RefreshLibrary();
        else LibraryButton.Focus();
    }

    private void UpdateLibraryControls()
    {
        if (library == null) return;
        var enabled = !busy && !libraryBusy && libraryError == null;
        LibraryCommands.IsEnabled = LibraryActions.IsEnabled = LutCards.IsEnabled = enabled;
        FilterTagsButton.IsEnabled = enabled;
        TrialBanner.Visibility = trialLabel == null ? Visibility.Collapsed : Visibility.Visible;
        TrialBanner.IsEnabled = enabled && !loadingLut;
        TrialName.Text = trialLabel == null ? "" : "试用 · " + trialLabel;
        TrialName.ToolTip = trialLabel;
        EditLutButton.IsEnabled = RemoveLutButton.IsEnabled = LutCards.SelectedItem is LutEntry;
        if (libraryError != null) LibraryStatus.Text = libraryError;
    }

    private void FilterChanged(object sender, RoutedEventArgs args)
    {
        if (library != null) RefreshLibrary();
    }

    private void RefreshLibrary()
    {
        if (LibraryPanel.Visibility != Visibility.Visible || libraryError != null) return;
        var index = library.Index;
        tagFilter = tagFilter.Where(tag => index.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)).ToArray();
        var filtered = LutLibrary.Filter(index, LutSearch.Text, tagFilter, OnlyFavorites.IsChecked == true).ToArray();
        fillingCards = true;
        LutCards.ItemsSource = filtered;
        LutCards.SelectedItem = filtered.FirstOrDefault(entry => entry.Hash == selectedLut);
        fillingCards = false;
        FilterTagsButton.Content = tagFilter.Length == 0 ? "筛选标签" : $"标签 · {tagFilter.Length}";
        FilterTagsButton.ToolTip = tagFilter.Length == 0 ? "可同时匹配多个标签" : string.Join("、", tagFilter);
        LibraryStatus.Text = index.Entries.Length == 0 ? "库为空 · 导入 .cube，或临时试用" :
            filtered.Length == 0 ? "没有匹配的 LUT" : $"{filtered.Length} / {index.Entries.Length} 项";
        UpdateLibraryControls();
    }

    private async void SelectLutCard(object sender, SelectionChangedEventArgs args)
    {
        UpdateLibraryControls();
        if (!fillingCards && !busy && !libraryBusy && LutCards.SelectedItem is LutEntry entry)
            await ApplySavedLut(entry.Hash);
    }

    private async void LutCardClick(object sender, MouseButtonEventArgs args)
    {
        if (busy || libraryBusy || loadingLut || lutOn || LutCards.SelectedItem is not LutEntry entry) return;
        if (ItemsControl.ContainerFromElement(LutCards, args.OriginalSource as DependencyObject) is ListBoxItem { IsSelected: true })
            await ApplySavedLut(entry.Hash);
    }

    private async Task ApplySavedLut(string hash)
    {
        if (libraryError != null || libraryBusy) return;
        var request = ++lutRequest;
        loadingLut = true;
        UpdateLutLabel();
        try
        {
            var file = library.FileFor(hash);
            var decoded = hash == selectedLut && lut != null && trialLabel == null ? lut : null;
            var directory = PreviewDirectory();
            var prepared = decoded != null && lutCopy != null && lutFailure == null
                ? (decoded, lutCopy)
                : await Task.Run(() =>
                {
                    var loaded = decoded ?? new Lut(file, library.Read(hash));
                    return (loaded, loaded.SavePreview(directory));
                });
            if (request != lutRequest || busy) return;
            library.Select(hash);
            selectedLut = hash;
            trialLabel = null;
            lut = prepared.Item1;
            lutCopy = prepared.Item2;
            lutOn = true;
            ApplyLut();
            if (LutCards.SelectedItem is not LutEntry visible || visible.Hash != hash) RefreshLibrary();
        }
        catch (Exception ex)
        {
            if (request == lutRequest)
            {
                var resetPhoto = lutOn && view == Kind.Photo;
                LutFailed(ex.Message);
                if (resetPhoto) RefreshPhotoLook(++ticket);
                RefreshLibrary();
            }
        }
        finally
        {
            if (request == lutRequest)
            {
                loadingLut = false;
                UpdateLutLabel();
            }
        }
    }

    private async void ImportLuts(object sender, RoutedEventArgs args)
    {
        var dialog = new OpenFileDialog { Title = "导入 LUT 到本机库", Filter = "3D LUT (*.cube)|*.cube", Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        var result = await LibraryOperation(() => library.Import(dialog.FileNames), "导入 LUT");
        if (result == null) return;
        ImportSummary(result, "LUT 导入");
        if (result.First is { } hash) await ApplySavedLut(hash);
    }

    private async void TryLut(object sender, RoutedEventArgs args)
    {
        var dialog = new OpenFileDialog { Title = "临时试用 LUT", Filter = "3D LUT (*.cube)|*.cube" };
        if (dialog.ShowDialog(this) != true) return;
        var directory = PreviewDirectory();
        var result = await LibraryOperation(() =>
        {
            var loaded = Lut.Read(dialog.FileName);
            return Tuple.Create(loaded, loaded.SavePreview(directory));
        }, "试用 LUT");
        if (result == null) return;
        selectedLut = null;
        trialLabel = Path.GetFileNameWithoutExtension(dialog.FileName);
        lut = result.Item1;
        lutCopy = result.Item2;
        lutOn = true;
        ApplyLut();
        RefreshLibrary();
    }

    private async void SaveTrial(object sender, RoutedEventArgs args)
    {
        if (trialLabel == null || lut == null) return;
        var name = trialLabel;
        var current = lut;
        var result = await LibraryOperation(() => library.SaveTrial(name, current.Data), "保存到 LUT 库");
        if (result?.First is not { } hash) return;
        var enabled = lutOn;
        try
        {
            library.Select(hash);
            selectedLut = hash;
            trialLabel = null;
            lutOn = enabled;
            ApplyLut();
            RefreshLibrary();
            Status(result.Duplicate == 0 ? "试用 LUT 已保存到本机库" : "库中已有相同 LUT，已切换到现有项");
        }
        catch (Exception ex) { Fail("无法记住 LUT 选择", ex); }
    }

    private async void EditLut(object sender, RoutedEventArgs args)
    {
        if (LutCards.SelectedItem is not LutEntry entry) return;
        var editor = new LutEditDialog(entry, library.Index.Tags) { Owner = this };
        if (editor.ShowDialog() == true)
            await LibraryOperation(() => library.Edit(entry.Hash, editor.Label, editor.Tags), "编辑 LUT");
    }

    private async void FavoriteLut(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { DataContext: LutEntry entry })
            await LibraryOperation(() => library.Favorite(entry.Hash), "更新收藏");
    }

    private async void RemoveLut(object sender, RoutedEventArgs args)
    {
        if (LutCards.SelectedItem is not LutEntry entry) return;
        if (MessageBox.Show(this, $"从本机库移除「{entry.Name}」？\n不会修改外部 LUT 或素材。之后可以重新导入。",
            "移除 LUT", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        if (entry.Hash == selectedLut)
        {
            selectedLut = null;
            lut = null;
            lutCopy = null;
            lutOn = false;
            ApplyLut();
        }
        await LibraryOperation(() => library.Remove(entry.Hash), "移除 LUT");
        if (library.Index.Entries.Any(item => item.Hash == entry.Hash) && library.Index.Selected == entry.Hash && trialLabel == null)
        {
            selectedLut = entry.Hash;
            RefreshLibrary();
            UpdateControls();
        }
    }

    private void ChooseTagFilter(object sender, RoutedEventArgs args)
    {
        var dialog = new LutTagFilterDialog(library.Index.Tags, tagFilter) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        tagFilter = dialog.Tags;
        RefreshLibrary();
    }

    private void ManageTags(object sender, RoutedEventArgs args)
    {
        var dialog = new LutTagsDialog(library, (before, after) =>
        {
            tagFilter = tagFilter.Select(tag => tag.Equals(before, StringComparison.OrdinalIgnoreCase) ? after : tag)
                .OfType<string>().ToArray();
            RefreshLibrary();
        }) { Owner = this };
        dialog.ShowDialog();
        RefreshLibrary();
    }

    private async void ExportLibrary(object sender, RoutedEventArgs args)
    {
        var dialog = new SaveFileDialog { Title = "导出 LUT 库", Filter = "FilmPick LUT 备份 (*.zip)|*.zip",
            DefaultExt = ".zip", FileName = "FilmPick-LUT-备份.zip", OverwritePrompt = false };
        if (dialog.ShowDialog(this) == true && await LibraryOperation(() => library.Backup(dialog.FileName), "导出 LUT 备份"))
            Status("已导出 LUT 备份：" + dialog.FileName);
    }

    private async void RestoreLibrary(object sender, RoutedEventArgs args)
    {
        var dialog = new OpenFileDialog { Title = "合并 LUT 库备份", Filter = "FilmPick LUT 备份 (*.zip)|*.zip" };
        if (dialog.ShowDialog(this) != true) return;
        var result = await LibraryOperation(() => library.Merge(dialog.FileName), "合并 LUT 备份");
        if (result != null) ImportSummary(result, "备份合并");
    }

    private void ImportSummary(LutImport result, string caption)
    {
        var summary = $"{caption} · 新增 {result.Added}，重复 {result.Duplicate}，失败 {result.Errors.Length}";
        Status(summary);
        if (result.Errors.Length != 0) DetailDialog.Show(this, caption + "报告", summary, result.Errors);
    }

    private async Task<bool> LibraryOperation(Action action, string caption)
    {
        return await LibraryOperation(() => { action(); return new object(); }, caption) != null;
    }

    private async Task<T?> LibraryOperation<T>(Func<T> action, string caption) where T : class
    {
        if (busy || libraryBusy || libraryError != null) return null;
        ++lutRequest;
        loadingLut = false;
        libraryBusy = true;
        UpdateControls();
        Status("正在" + caption + "…");
        try { var result = await Task.Run(action); Status(caption + "完成"); return result; }
        catch (Exception ex) { Fail(caption + "失败", ex); return null; }
        finally { libraryBusy = false; RefreshLibrary(); UpdateControls(); }
    }

    private void LoadLutCardImage(object sender, RoutedEventArgs args) { if (sender is Image image) FillCardImage(image); }
    private void LutCardImageChanged(object sender, DependencyPropertyChangedEventArgs args) { if (sender is Image image) FillCardImage(image); }

    private async void FillCardImage(Image image)
    {
        image.Source = null;
        if (!image.IsLoaded || image.DataContext is not LutEntry entry) return;
        try
        {
            var bitmap = await thumbnails.Get(entry.Hash, thumbnailLife.Token);
            if (image.DataContext is LutEntry current && current.Hash == entry.Hash) image.Source = bitmap;
        }
        catch (Exception) { }
    }

    private void CloseLutLibrary()
    {
        thumbnailLife.Cancel();
        thumbnailLife.Dispose();
        if (trialDirectory == null || !Directory.Exists(trialDirectory)) return;
        try
        {
            foreach (var path in Directory.EnumerateFiles(trialDirectory, "*.cube")) File.Delete(path);
            Directory.Delete(trialDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private string PreviewDirectory() => trialDirectory ??= Path.Combine(appData, "lut-trial", Guid.NewGuid().ToString("N"));
}

sealed class LutThumbnails(LutLibrary library)
{
    private static readonly Lazy<BitmapSource> Sample = new(MakeSample);
    private readonly Dictionary<string, Task<BitmapSource>> cache = [];
    private readonly SemaphoreSlim worker = new(1);

    public Task<BitmapSource> Get(string hash, CancellationToken cancel)
    {
        if (cache.TryGetValue(hash, out var task))
        {
            if (!task.IsCanceled && !task.IsFaulted) return task;
            cache.Remove(hash);
        }
        task = Create(hash, cancel);
        cache.Add(hash, task);
        if (cache.Count > 256)
        {
            var old = cache.FirstOrDefault(item => item.Key != hash && item.Value.IsCompleted);
            if (old.Key != null) cache.Remove(old.Key);
        }
        return task;
    }

    private async Task<BitmapSource> Create(string hash, CancellationToken cancel)
    {
        await worker.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => new Lut(library.FileFor(hash), library.Read(hash)).Transform(Sample.Value, cancel), cancel)
                .ConfigureAwait(false);
        }
        finally { worker.Release(); }
    }

    private static BitmapSource MakeSample()
    {
        const int width = 136, height = 84;
        var pixels = new byte[width * height * 4];
        for (var row = 0; row < height; row++)
        for (var column = 0; column < width; column++)
        {
            var horizontal = column / (double)(width - 1);
            var vertical = row / (double)(height - 1);
            var ridge = .57 - .14 * Math.Sin(horizontal * 9) - .045 * Math.Cos(horizontal * 23);
            var sky = vertical < ridge;
            var sun = Math.Pow(Math.Max(0, 1 - Math.Sqrt(Math.Pow(horizontal - .69, 2) + Math.Pow(vertical - .29, 2)) * 5), 2);
            var band = Math.Max(0, vertical - ridge);
            var index = (row * width + column) * 4;
            pixels[index] = (byte)(sky ? 184 - vertical * 80 + sun * 18 : 77 + band * 70);
            pixels[index + 1] = (byte)(sky ? 105 + vertical * 90 + sun * 70 : 96 - band * 80 + horizontal * 17);
            pixels[index + 2] = (byte)(sky ? 89 + vertical * 120 + sun * 105 : 47 + band * 66 + horizontal * 35);
            pixels[index + 3] = 255;
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }
}
