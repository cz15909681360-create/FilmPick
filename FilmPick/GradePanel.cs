using System.IO;
using System.Windows;

namespace FilmPick;

public partial class MainWindow
{
    private GradeOutbox gradeOutbox = new();
    private readonly Dictionary<string, string> gradeFailures = new(Paths.Comparer);
    private readonly Dictionary<string, GradeChange> publishedGrades = new(Paths.Comparer);
    private Task gradeTask = Task.CompletedTask;
    private bool gradeAvailable = true, closingAfterGrades, testingGradeWrites;

    private bool UsesStandardGrades(string path) => catalog.Main.GetValueOrDefault(path) == Kind.Photo &&
        (!scanningInBenchmark || testingGradeWrites);

    private PendingGrade? PendingFor(string path)
    {
        if (!gradeOutbox.Entries.TryGetValue(path, out var pending) || !catalog.Files.TryGetValue(path, out var file)) return null;
        if (new SavedRating(0, pending.Anchor).Matches(file.Stamp)) return pending;
        // A crash after publishing the primary can leave its newer file identity in the catalogue.
        return pending.Transaction?.Staged.Any(item => Paths.Comparer.Equals(path, item.Target.Relative) &&
            new SavedRating(0, item.File with { Creation = file.Stamp.Creation }).Matches(file.Stamp)) == true ? pending : null;
    }

    private void OpenGrades(GradeOutbox? saved, string? unavailable)
    {
        gradeFailures.Clear();
        publishedGrades.Clear();
        gradeAvailable = true;
        try
        {
            if (saved == null || unavailable != null) throw new IOException(unavailable ?? "待写记录为空。");
            gradeOutbox = new() { Entries = new(saved.Entries, Paths.Comparer) };
            foreach (var pending in gradeOutbox.Entries.Values)
                if (pending.Transaction is { } transaction)
                    foreach (var output in transaction.Staged)
                        if (output.Target.Before is { } before && catalog.Files.TryGetValue(output.Target.Relative, out var found) &&
                            SamePublishedIdentity(output.File, found.Stamp))
                            RebindSessionStamp(output.Target.Relative, before, found.Stamp);
        }
        catch (Exception error)
        {
            gradeAvailable = false;
            gradeOutbox = new();
            Notify("分级待写记录无法读取。\n" + error.Message, "标准分级");
        }
    }

    private bool QueueGrade(string path, GradeChange change)
    {
        if (!gradeAvailable || store == null) { Status("标准分级记录无法保存。"); return false; }
        if (gradeOutbox.Entries.TryGetValue(path, out var previous) && PendingFor(path) == null)
        {
            Status("有一条分级待写记录对应旧文件。请先处理待写记录。");
            return false;
        }
        GradePlan? plan = previous?.Plan;
        if (plan == null)
        {
            try { plan = GradeWriter.Plan(catalog, path, owners); }
            catch (Exception error) { gradeFailures[path] = error.Message; }
        }
        var next = new PendingGrade(previous?.Change.Merge(change) ?? change,
            previous?.Anchor ?? catalog.Files[path].Stamp, plan, previous?.Transaction);
        gradeOutbox.Entries[path] = next;
        if (!SaveGrades())
        {
            if (previous == null) gradeOutbox.Entries.Remove(path);
            else gradeOutbox.Entries[path] = previous;
            return false;
        }
        if (plan != null) gradeFailures.Remove(path);
        StartGradeWrites();
        return true;
    }

    private bool SaveGrades()
    {
        try { store!.Write("grade-writes", gradeOutbox); return true; }
        catch (Exception error) { Status("分级待写记录保存失败：" + error.Message); return false; }
    }

    private void StartGradeWrites()
    {
        if (gradeTask.IsCompleted && gradeAvailable && root != null && gradeOutbox.Entries.Count > 0)
            gradeTask = WriteGrades(new GradeWriter(root));
    }

    private async Task WriteGrades(GradeWriter writer)
    {
        while (true)
        {
            var path = gradeOutbox.Entries.Keys.FirstOrDefault(name => catalog.Main.GetValueOrDefault(name) == Kind.Photo &&
                catalog.Main.ContainsKey(name) && !gradeFailures.ContainsKey(name));
            if (path == null) return;
            try
            {
                if (markBaselines.TryGetValue(path, out var baseline))
                {
                    await baseline;
                    markBaselines.Remove(path);
                }
                var current = PendingFor(path) ?? throw new IOException("待写记录对应的素材已更换。");
                var plan = current.Plan ?? GradeWriter.Plan(catalog, path, owners);
                var transaction = current.Transaction ?? await Task.Run(() => writer.Prepare(plan, current.Change));
                var latest = gradeOutbox.Entries[path];
                gradeOutbox.Entries[path] = latest with { Plan = plan, Transaction = transaction };
                if (!SaveGrades())
                {
                    gradeOutbox.Entries[path] = latest;
                    if (current.Transaction == null) await Task.Run(() => writer.Cleanup(plan, transaction.Id));
                    throw new IOException("无法保存写入进度。");
                }
                var written = await Task.Run(() => writer.Publish(plan, transaction));
                ApplyPublishedGrade(path, written, transaction.Change);
                latest = gradeOutbox.Entries[path];
                if (latest.Change == transaction.Change)
                    gradeOutbox.Entries.Remove(path);
                else
                    gradeOutbox.Entries[path] = new(latest.Change, catalog.Files[path].Stamp, GradeWriter.Plan(catalog, path, owners));
                if (!SaveGrades())
                {
                    gradeOutbox.Entries[path] = latest;
                    throw new IOException("已写入文件，待写记录将在下次核对。");
                }
                await Task.Run(() => writer.Cleanup(plan, transaction.Id));
            }
            catch (Exception error)
            {
                gradeFailures[path] = error.Message;
                Status("分级待写入：" + path + " · " + error.Message);
            }
            UpdateControls();
        }
    }

    private void ApplyPublishedGrade(string group, MediaFile[] written, GradeChange change)
    {
        foreach (var file in written)
        {
            if (catalog.Files.TryGetValue(file.Relative, out var before))
                RebindSessionStamp(file.Relative, before.Stamp, file.Stamp);
            catalog.Files[file.Relative] = file;
            if (Path.GetExtension(file.Relative).Equals(".xmp", StringComparison.OrdinalIgnoreCase) &&
                !catalog.Automatic.ContainsKey(file.Relative) && !session.Links.ContainsKey(file.Relative))
                catalog.Automatic[file.Relative] = group;
        }
        publishedGrades[group] = publishedGrades.GetValueOrDefault(group)?.Merge(change) ?? change;
        if (change.Stars != null)
        {
            session.Ratings.Remove(group);
            if (catalog.RawCompanions.TryGetValue(group, out var raw)) session.Ratings.Remove(raw);
        }
        if (change.Flag != null) session.Marks.Remove(group);
        AttachmentsChanged();
        ScheduleThumbnails();
        QueueSave();
    }

    private void RebindSessionStamp(string relative, Stamp before, Stamp current)
    {
        if (markAnchors.GetValueOrDefault(relative) == before) markAnchors[relative] = current;
        if (session.Marks.TryGetValue(relative, out var mark) && mark.Stamp == before)
            session.Marks[relative] = mark with { Stamp = current };
        if (session.Ratings.TryGetValue(relative, out var rating) && rating.Matches(before))
            session.Ratings[relative] = rating with { Stamp = current };
        foreach (var name in session.Links.Keys.ToArray())
        {
            var link = session.Links[name];
            if (Paths.Comparer.Equals(link.Owner, relative) && link.OwnerStamp == before) link = link with { OwnerStamp = current };
            if (Paths.Comparer.Equals(name, relative) && link.AttachmentStamp == before) link = link with { AttachmentStamp = current };
            session.Links[name] = link;
        }
    }

    private static bool SamePublishedIdentity(Stamp staged, Stamp actual) =>
        staged.Volume == actual.Volume && staged.IdLow == actual.IdLow && staged.IdHigh == actual.IdHigh && staged.Length == actual.Length;

    private async void ReviewGradeWrites(object sender, RoutedEventArgs args)
    {
        if (busy) return;
        var entries = gradeOutbox.Entries.Where(item => catalog.Main.ContainsKey(item.Key)).Select(item =>
            $"{item.Key} · {gradeFailures.GetValueOrDefault(item.Key, "等待写入")}").ToArray();
        if (entries.Length == 0) return;
        var dialog = new GradeWriteDialog(entries, !gradeTask.IsCompleted) { Owner = this };
        dialog.ShowDialog();
        if (dialog.Action == GradeWriteAction.Retry)
        {
            gradeFailures.Clear();
            StartGradeWrites();
        }
        else if (dialog.Action == GradeWriteAction.Discard)
        {
            var changed = gradeOutbox.Entries.Where(item => gradeFailures.ContainsKey(item.Key) &&
                catalog.Main.ContainsKey(item.Key) && item.Value.Transaction == null).ToArray();
            foreach (var (path, _) in changed) gradeOutbox.Entries.Remove(path);
            if (!SaveGrades())
                foreach (var (path, entry) in changed) gradeOutbox.Entries[path] = entry;
            else
                foreach (var (path, _) in changed) gradeFailures.Remove(path);
            AttachmentsChanged();
            RefreshQueue();
        }
        await gradeTask;
    }

    private async void FinishGradeWritesAndClose()
    {
        if (closingAfterGrades) return;
        closingAfterGrades = true;
        await gradeTask;
        closingAfterGrades = false;
        Close();
    }
}
