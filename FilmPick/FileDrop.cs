using System.IO;
using System.Windows;

namespace FilmPick;

public partial class MainWindow
{
    private sealed record DropBatch(string Folder, string[]? Files)
    {
        public static DropBatch Read(string[] paths)
        {
            var distinct = paths.Select(Normalize).Distinct(Paths.Comparer).ToArray();
            if (distinct.Length == 0) throw new IOException("没有收到素材路径。");
            var directories = 0;
            foreach (var path in distinct)
            {
                var attributes = File.GetAttributes(path);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException("请选择本机磁盘上的实际文件或文件夹。");
                if (attributes.HasFlag(FileAttributes.Directory)) directories++;
            }
            if (directories != 0)
            {
                if (distinct.Length != 1) throw new IOException("请每次只拖入一个文件夹，文件可另行拖入。");
                return new(Paths.Root(distinct[0]), null);
            }
            var folder = Path.GetDirectoryName(distinct[0])!;
            if (distinct.Any(path => !Paths.Comparer.Equals(Path.GetDirectoryName(path), folder)))
                throw new IOException("请按素材所在文件夹分次拖入。");
            return new(Paths.Root(folder), distinct);
        }

        private static string Normalize(string path)
        {
            if (!Path.IsPathFullyQualified(path) || Path.GetPathRoot(path)?.Length != 3)
                throw new IOException("请选择本机磁盘上的素材或文件夹。");
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
    }

    private void PreviewFileDrag(object sender, DragEventArgs args)
    {
        if (!args.Data.GetDataPresent(DataFormats.FileDrop, false)) return;
        args.Handled = true;
        args.Effects = busy || libraryBusy ? DragDropEffects.None : args.AllowedEffects & DragDropEffects.Copy;
    }

    private async void DropFiles(object sender, DragEventArgs args)
    {
        PreviewFileDrag(sender, args);
        if (!args.Handled) return;
        if (busy || libraryBusy) { Status("操作进行中，请稍后拖入素材。"); return; }
        if (args.Effects == DragDropEffects.Copy && args.Data.GetData(DataFormats.FileDrop, false) is string[] paths)
            await OpenDrop(paths);
    }

    private async Task OpenDrop(string[] paths)
    {
        if (busy || libraryBusy) { Status("操作进行中，请稍后拖入素材。"); return; }
        if (!SaveSession()) return;
        SetBusy(true, "正在打开拖入的素材…");
        try
        {
            var batch = await Task.Run(() => DropBatch.Read(paths));
            if (batch.Files == null || root == null || !CoversDropFolder(root, session.Recursive, batch.Folder))
                await LoadRoot(batch.Folder, batch.Files);
            else
                await SelectInOpenFolder(root, batch.Files);
        }
        catch (Exception ex) { Status("无法打开拖入的素材 · " + ex.Message); }
        finally { SetBusy(false); Queue.Focus(); }
    }

    private static bool CoversDropFolder(string current, bool recursive, string folder) =>
        Paths.Comparer.Equals(current, folder) ||
        (recursive && folder.StartsWith(current + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    private async Task SelectInOpenFolder(string current, string[] files)
    {
        var needsScan = files.Any(path => !catalog.Files.ContainsKey(Path.GetRelativePath(current, path)));
        if (needsScan) await gradeTask;
        var next = needsScan ? await Task.Run(() => Catalog.Scan(current, session.Recursive)) : catalog;
        var target = ResolveDroppedAsset(current, next, session, files);
        if (target == null) { ReportUnresolvedDrop(); return; }
        if (needsScan)
        {
            await StopVideo();
            catalog = next;
            catalog.Reconcile(session);
            AttachmentsChanged();
            StartCaptureIndex();
            StartGradeWrites();
            QueueSave();
        }

        var kind = catalog.Main[target];
        var cleared = RevealDroppedAsset(target, kind);
        if (!needsScan && view == kind && cleared.Count == 0 && visibleRows.TryGetValue(target, out var row))
        {
            if (Queue.SelectedItem != row)
            {
                Queue.SelectedItem = row;
                ShowCurrent();
            }
            Queue.ScrollIntoView(row);
        }
        else if (needsScan || view != kind)
            BuildQueue(kind, target);
        else
        {
            RefreshBrowseWidgets();
            RefreshQueue(restore: target);
        }
        var name = Path.GetFileName(target);
        Status(cleared.Count == 0 ? $"已定位 {name}" : $"已定位 {name} · 已清除{string.Join("、", cleared)}条件");
    }

    private static string? ResolveDroppedAsset(string folder, Catalog source, Session saved, string[] paths)
    {
        foreach (var path in paths)
        {
            var relative = Path.GetRelativePath(folder, path);
            if (source.Main.ContainsKey(relative)) return source.GroupOf(relative);
            if (source.Automatic.TryGetValue(relative, out var owner)) return owner;
            if (saved.Links.TryGetValue(relative, out var link) && source.Main.ContainsKey(link.Owner) &&
                source.Files[link.Owner].Stamp == link.OwnerStamp &&
                source.Files.GetValueOrDefault(relative)?.Stamp == link.AttachmentStamp)
                return source.GroupOf(link.Owner);
        }
        return null;
    }

    private static IReadOnlyList<string> RemoveBlockingFilters(ref BrowseFilter filter, BrowseItem target)
    {
        var cleared = new List<string>();
        if (!new BrowseFilter(Name: filter.Name).Includes(target))
        {
            filter = filter with { Name = "" };
            cleared.Add("文件名");
        }
        if (!new BrowseFilter(Date: filter.Date, From: filter.From, To: filter.To).Includes(target))
        {
            filter = filter with { Date = DateFilter.All, From = null, To = null };
            cleared.Add("日期");
        }
        if (!new BrowseFilter(Stars: filter.Stars).Includes(target))
        {
            filter = filter with { Stars = 0 };
            cleared.Add("星级");
        }
        if (!new BrowseFilter(Marks: filter.Marks).Includes(target))
        {
            filter = filter with { Marks = 0 };
            cleared.Add("旗标");
        }
        return cleared;
    }

    private IReadOnlyList<string> RevealDroppedAsset(string target, Kind kind)
    {
        var item = BrowseItemFor(target);
        if (kind == Kind.Photo) return RemoveBlockingFilters(ref photoFilter, item);
        return RemoveBlockingFilters(ref videoFilter, item);
    }

    private void ReportUnresolvedDrop() => Status("找不到可定位的素材。若拖入的是附属文件，请拖入主片。");
}
