using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;

namespace FilmPick;

record GradeChange(int? Stars = null, Mark? Flag = null)
{
    public GradeChange Merge(GradeChange next) => new(next.Stars ?? Stars, next.Flag ?? Flag);
}

record GradeTarget(string Relative, Stamp? Before);
record GradePlan(MediaFile[] Media, GradeTarget[] Targets);
record GradeStage(GradeTarget Target, Stamp File, string Hash);
record GradeTransaction(Guid Id, GradeChange Change, GradeStage[] Staged);
record PendingGrade(GradeChange Change, Stamp Anchor, GradePlan? Plan = null, GradeTransaction? Transaction = null);

sealed class GradeOutbox
{
    public Dictionary<string, PendingGrade> Entries { get; set; } = new(Paths.Comparer);
}

sealed class GradeWriter(string root, string? executable = null)
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly int[] Percent = [0, 1, 25, 50, 75, 99];
    private readonly string tool = executable ?? Path.Combine(AppContext.BaseDirectory, "metadata", "exiftool.exe");

    public static GradePlan Plan(Catalog catalog, string group, Dictionary<string, string> owners)
    {
        var media = catalog.RawCompanions.TryGetValue(group, out var raw) ? new[] { raw, group } : [group];
        var targets = new List<GradeTarget>();
        foreach (var file in media)
        {
            var target = file;
            if (Catalog.IsRaw(file) && !Path.GetExtension(file).Equals(".dng", StringComparison.OrdinalIgnoreCase))
            {
                var stem = Path.ChangeExtension(file, null);
                target = stem + ".xmp";
                if (catalog.Main.Keys.Any(other => Paths.Comparer.Equals(Path.ChangeExtension(other, null), stem) &&
                    !Paths.Comparer.Equals(catalog.GroupOf(other), group)))
                    throw new IOException("同名 XMP 可能对应多条素材：" + target);
                if (catalog.Files.TryGetValue(target, out var existing)) target = existing.Relative;
                if (owners.TryGetValue(target, out var owner) && !Paths.Comparer.Equals(owner, group))
                    throw new IOException("XMP 已关联到另一条素材：" + target);
            }
            targets.Add(new(target, catalog.Files.GetValueOrDefault(target)?.Stamp));
        }
        return new(media.Select(name => catalog.Files[name]).ToArray(), targets.ToArray());
    }

    public async Task<GradeTransaction> Prepare(GradePlan plan, GradeChange change)
    {
        var id = Guid.NewGuid();
        var prepared = new List<GradeStage>();
        var directory = StageDirectory(plan, id);
        using var pins = new DirectoryPins();
        pins.Pin(Path.GetDirectoryName(directory)!, false);
        CheckMedia(plan, null);
        Directory.CreateDirectory(directory);
        pins.Pin(directory, false);
        try
        {
            for (var i = 0; i < plan.Targets.Length; i++)
            {
                var target = plan.Targets[i];
                var original = Paths.Below(root, target.Relative);
                EnsureStamp(original, target.Before);
                var stage = StagePath(directory, i, target);
                if (target.Before == null)
                    File.WriteAllText(stage, """
                        <x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"><rdf:Description rdf:about=""/></rdf:RDF></x:xmpmeta>
                        """, Utf8);
                else
                    NativeFiles.CopyNew(original, stage);
                EnsureStamp(original, target.Before);

                var embedded = !Path.GetExtension(stage).Equals(".xmp", StringComparison.OrdinalIgnoreCase);
                var before = embedded ? await InspectImage(stage).ConfigureAwait(false) : null;
                var args = WriteArgs(stage, change);
                await Run(args).ConfigureAwait(false);
                var inspected = await InspectTags(stage, embedded).ConfigureAwait(false);
                if (embedded && Read(inspected, "File:ImageDataHash") != before)
                    throw new IOException("图像数据校验失败：" + target.Relative);
                foreach (var field in GradeFields(stage, change))
                    if (!TagMatches(inspected, field.Tag, field.Value))
                        throw new IOException($"元数据回读校验失败：{target.Relative} · {field.Tag}");
                using (var stream = new FileStream(stage, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                    stream.Flush(flushToDisk: true);
                prepared.Add(new(target, NativeFiles.Inspect(stage)!, Hash(stage)));
            }
            CheckMedia(plan, null);
            return new(id, change, prepared.ToArray());
        }
        catch
        {
            Cleanup(plan, id);
            throw;
        }
    }

    public MediaFile[] Publish(GradePlan plan, GradeTransaction transaction)
    {
        var directory = StageDirectory(plan, transaction.Id);
        using var pins = new DirectoryPins();
        pins.Pin(Path.GetDirectoryName(directory)!, false);
        if (Directory.Exists(directory)) pins.Pin(directory, false);
        if (transaction.Staged.Length != plan.Targets.Length ||
            !transaction.Staged.Select(item => item.Target).SequenceEqual(plan.Targets))
            throw new IOException("待写入记录不完整。");
        for (var i = 0; i < transaction.Staged.Length; i++)
            RestoreVacantOriginal(Paths.Below(root, transaction.Staged[i].Target.Relative),
                Path.Combine(directory, $"{i}.backup"), transaction.Staged[i].Target.Before);
        CheckMedia(plan, transaction);
        for (var i = 0; i < transaction.Staged.Length; i++)
        {
            var item = transaction.Staged[i];
            var path = Paths.Below(root, item.Target.Relative);
            if (Published(path, item)) continue;
            EnsureStamp(path, item.Target.Before);
            var stage = StagePath(directory, i, item.Target);
            EnsureStamp(stage, item.File);
            if (Hash(stage) != item.Hash) throw new IOException("暂存评分文件已改变：" + item.Target.Relative);
        }

        var outputs = new List<MediaFile>();
        for (var i = 0; i < transaction.Staged.Length; i++)
        {
            var item = transaction.Staged[i];
            var path = Paths.Below(root, item.Target.Relative);
            if (!Published(path, item))
            {
                var stage = StagePath(directory, i, item.Target);
                using var held = item.Target.Before == null ? null : NativeFiles.OpenMetadataTarget(path);
                if (held != null && NativeFiles.Identify(held) != item.Target.Before)
                    throw new IOException("写入前文件已改变：" + item.Target.Relative);
                if (held == null) File.Move(stage, path);
                else
                {
                    var backup = Path.Combine(directory, $"{i}.backup");
                    if (NativeFiles.Inspect(backup) != null) throw new IOException("评分恢复副本已存在：" + backup);
                    try { File.Replace(stage, path, backup); }
                    catch (IOException)
                    {
                        RestoreVacantOriginal(path, backup, item.Target.Before);
                        throw;
                    }
                }
                if (!Published(path, item)) throw new IOException("发布后的文件校验失败：" + item.Target.Relative);
            }
            outputs.Add(new(item.Target.Relative, NativeFiles.Inspect(path)!));
            SHChangeNotify(0x2000, 5, path, IntPtr.Zero);
        }
        return outputs.ToArray();
    }

    public void RecoverOriginalNames(GradeOutbox outbox)
    {
        foreach (var entry in outbox.Entries.Values)
        {
            if (entry.Plan is not { Media.Length: > 0 } plan || entry.Transaction is not { } transaction ||
                transaction.Id == Guid.Empty) continue;
            var directory = StageDirectory(plan, transaction.Id);
            if (!Directory.Exists(directory)) continue;
            try
            {
                using var pins = new DirectoryPins();
                pins.Pin(directory, false);
                for (var i = 0; i < plan.Targets.Length; i++)
                    RestoreVacantOriginal(Paths.Below(root, plan.Targets[i].Relative),
                        Path.Combine(directory, $"{i}.backup"), plan.Targets[i].Before);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                // Keep the journal and any backup available for retry.
            }
        }
    }

    private void CheckMedia(GradePlan plan, GradeTransaction? transaction)
    {
        foreach (var media in plan.Media)
        {
            var path = Paths.Below(root, media.Relative);
            if (transaction?.Staged.FirstOrDefault(file => Paths.Comparer.Equals(file.Target.Relative, media.Relative))
                is { } written && Published(path, written)) continue;
            EnsureStamp(path, media.Stamp);
        }
    }

    private static void EnsureStamp(string path, Stamp? expected)
    {
        if (NativeFiles.Inspect(path) != expected) throw new IOException("文件已变化或无法确认归属：" + path);
    }

    private static void RestoreVacantOriginal(string path, string backup, Stamp? expected)
    {
        if (expected != null && NativeFiles.Inspect(path) == null && NativeFiles.Inspect(backup) == expected)
            File.Move(backup, path);
    }

    private static bool Published(string path, GradeStage stage)
    {
        var current = NativeFiles.Inspect(path);
        if (current == null) return false;
        var staged = stage.File;
        return current.Volume == staged.Volume && current.IdLow == staged.IdLow && current.IdHigh == staged.IdHigh &&
            current.Length == staged.Length && Hash(path) == stage.Hash;
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public void Cleanup(GradePlan plan, Guid id)
    {
        var directory = StageDirectory(plan, id);
        try
        {
            if (!Directory.Exists(directory)) return;
            using var pins = new DirectoryPins();
            pins.Pin(directory, false);
            for (var i = 0; i < plan.Targets.Length; i++)
            {
                var stage = StagePath(directory, i, plan.Targets[i]);
                File.Delete(stage);
                File.Delete(stage + "_exiftool_tmp");
                File.Delete(Path.Combine(directory, $"{i}.backup"));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { return; }
        try { Directory.Delete(directory); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private string StageDirectory(GradePlan plan, Guid id) =>
        Path.Combine(Path.GetDirectoryName(Paths.Below(root, plan.Media[0].Relative))!, $".filmpick-grade-{id:N}");

    private static string StagePath(string directory, int index, GradeTarget target) =>
        Path.Combine(directory, index.ToString(CultureInfo.InvariantCulture) + Path.GetExtension(target.Relative).ToLowerInvariant());

    private async Task<string> InspectImage(string stage) =>
        Read(await InspectTags(stage, true).ConfigureAwait(false), "File:ImageDataHash")
        ?? throw new IOException("无法校验暂存文件的图像数据。");

    private async Task<JsonElement> InspectTags(string stage, bool embedded)
    {
        var args = new List<string> { "-j", "-G1", "-n", "-Rating", "-RatingPercent", "-XMP-xmpDM:Pick", "-XMP-xmpDM:Good" };
        if (embedded) args.AddRange(["-api", "ImageHashType=SHA256", "-ImageDataHash"]);
        args.Add(stage);
        using var json = JsonDocument.Parse(await Run(args).ConfigureAwait(false));
        return json.RootElement[0].Clone();
    }

    private static string? Read(JsonElement tag, string name) => tag.TryGetProperty(name, out var value) ? value.ToString() : null;

    private static bool TagMatches(JsonElement values, string key, string value)
    {
        var actual = Read(values, key);
        if (value.Length == 0) return actual == null;
        if (key != "XMP-xmpDM:Good") return actual == value;
        return value == "True" ? actual is "True" or "true" or "1" : actual is "False" or "false" or "0";
    }

    private static string[] WriteArgs(string path, GradeChange change) =>
        ["-overwrite_original", .. GradeFields(path, change).Select(field => $"-{field.Tag}={field.Value}"), path];

    private static IEnumerable<(string Tag, string Value)> GradeFields(string path, GradeChange change)
    {
        if (change.Stars is { } stars)
        {
            if (stars is < 0 or > 5) throw new IOException("星级超出范围。");
            var value = stars.ToString(CultureInfo.InvariantCulture);
            yield return ("XMP-xmp:Rating", value);
            if (Path.GetExtension(path) is ".jpg" or ".jpeg")
            {
                var percent = Percent[stars].ToString(CultureInfo.InvariantCulture);
                yield return ("IFD0:Rating", value);
                yield return ("IFD0:RatingPercent", percent);
                yield return ("XMP-microsoft:RatingPercent", percent);
            }
            if (Path.GetExtension(path) == ".png")
                yield return ("XMP-microsoft:RatingPercent", Percent[stars].ToString(CultureInfo.InvariantCulture));
        }
        if (change.Flag is { } flag)
        {
            yield return ("XMP-xmpDM:Pick", flag switch { Mark.Pick => "1", Mark.Reject => "-1", _ => "0" });
            yield return ("XMP-xmpDM:Good", flag switch { Mark.Pick => "True", Mark.Reject => "False", _ => "" });
        }
    }

    private async Task<string> Run(IEnumerable<string> arguments)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var options = new ProcessStartInfo(tool)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, StandardInputEncoding = Utf8, StandardOutputEncoding = Utf8, StandardErrorEncoding = Utf8
        };
        foreach (var arg in new[] { "-config", "", "-charset", "filename=UTF8", "-@", "-" }) options.ArgumentList.Add(arg);
        using var process = Process.Start(options) ?? throw new IOException("无法启动 ExifTool。");
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.StandardInput.WriteAsync(string.Join('\n', arguments).AsMemory(), timeout.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException((await error.ConfigureAwait(false)).Trim());
            return await output.ConfigureAwait(false);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern void SHChangeNotify(int eventId, uint flags, [MarshalAs(UnmanagedType.LPWStr)] string item, IntPtr unused);
}
