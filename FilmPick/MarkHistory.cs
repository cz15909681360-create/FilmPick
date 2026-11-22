using System.IO;
using System.Windows.Controls;

namespace FilmPick;

public partial class MainWindow
{
    private sealed record MarkEdit(string Relative, Task<Mark?> Previous, Mark Applied);
    private readonly Stack<MarkEdit> markHistory = new();
    private readonly Dictionary<string, Stamp> markAnchors = new(Paths.Comparer);
    private readonly Dictionary<string, Task<Mark?>> markBaselines = new(Paths.Comparer);
    private bool undoingMark;

    private Task<Mark?> PreviousMark(string relative, Mark displayed)
    {
        if (!UsesStandardGrades(relative) || PendingFor(relative)?.Change.Flag != null ||
            publishedGrades.GetValueOrDefault(relative)?.Flag != null ||
            capture.GetValueOrDefault(relative)?.Metadata is { Failed: false })
            return Task.FromResult<Mark?>(displayed);

        if (markBaselines.TryGetValue(relative, out var baseline)) return baseline;
        var asset = CaptureAssetFor(root!, catalog, catalog.Files, attached, relative);
        return markBaselines[relative] = Task.Run<Mark?>(async () =>
        {
            try
            {
                CaptureMetadata? metadata = null;
                await new CaptureIndex().Read([asset], batch => metadata = batch.Single().Value.Metadata, CancellationToken.None);
                return metadata is { Failed: false } ? metadata.Flag ?? displayed : null;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                return null;
            }
        });
    }

    private async void UndoMark()
    {
        if (busy || undoingMark || root == null) return;
        var current = catalog;
        undoingMark = true;
        try
        {
            while (markHistory.TryPeek(out var entry))
            {
                var previous = await entry.Previous;
                if (busy || current != catalog || !markHistory.TryPeek(out var latest) || !ReferenceEquals(latest, entry)) return;
                var relative = entry.Relative;
                if (!catalog.Main.ContainsKey(relative) || !markAnchors.TryGetValue(relative, out var anchor) ||
                    catalog.Files[relative].Stamp != anchor ||
                    (catalog.Main[relative] == Kind.Video && NativeFiles.Inspect(Paths.Below(root, relative)) != anchor))
                {
                    markHistory.Pop();
                    Status("素材已变化，无法恢复旗标：" + Path.GetFileName(relative));
                    return;
                }
                if (previous == null)
                {
                    markHistory.Pop();
                    Status("无法读取改动前的旗标：" + Path.GetFileName(relative));
                    return;
                }
                if (previous == entry.Applied) { markHistory.Pop(); continue; }
                if (!ApplyMark(relative, previous.Value)) return;
                markHistory.Pop();
                RefreshQueue(restore: relative);
                Status("已撤销旗标 · " + Path.GetFileName(relative));
                return;
            }
            Status("没有可撤销的旗标操作。");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Status("无法撤销旗标：" + error.Message);
        }
        finally { undoingMark = false; }
    }

    private string GradeStorageLabel(string path)
    {
        if (catalog.Main[path] == Kind.Video) return "仅 FilmPick";
        if (catalog.RawCompanions.TryGetValue(path, out var raw))
            return Path.GetExtension(raw).Equals(".dng", StringComparison.OrdinalIgnoreCase) ? "写回两份原片" : "照片＋XMP";
        return Catalog.IsRaw(path) && !Path.GetExtension(path).Equals(".dng", StringComparison.OrdinalIgnoreCase) ? "写入 XMP" : "写回照片";
    }
}
