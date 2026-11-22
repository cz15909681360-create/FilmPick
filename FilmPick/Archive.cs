using System.IO;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace FilmPick;

record CopyMember(string Relative, Stamp SourceStamp);
record CopyGroup(string Main, Mark Mark, CopyMember[] Members);
record VerifiedCopy(string Relative, int Slot, Stamp Stamp, string Hash);
record CopyTransfer(Mark Mark, Guid Token, CopyMember[] Missing, VerifiedCopy[] Verified);
sealed class CopyJournal
{
    public int Version { get; set; } = 1;
    public CopyTransfer? Pending { get; set; }
}

sealed class LegacyJournal { public LegacyTransfer? Pending { get; set; } }
sealed record LegacyTransfer(LegacyGroup Group);
sealed record LegacyGroup(LegacyMember[] Members);
sealed record LegacyMember(string Source, string Target);
sealed record ArchiveResult(int Completed, int Unchanged, int Conflicts, List<string> Issues, bool RecoveryNeeded);

sealed class Archive(string root, Store store)
{
    internal Action<string>? Checkpoint { get; set; }
    internal string Destination(Mark mark, string name) => Paths.Below(Paths.TargetRoot(root, mark), name);

    public CopyJournal ReadJournal()
    {
        var journal = store.Read<CopyJournal>("copy-journal");
        if (journal.Version != 1) throw new IOException("不支持的复制记录版本。");
        return journal;
    }

    public string[] LegacyWarning()
    {
        try
        {
            var old = store.Read<LegacyJournal>("journal").Pending;
            return old == null ? [] : old.Group.Members.Select(member => member.Source + " ↔ " + member.Target).ToArray();
        }
        catch (Exception ex) { return ["旧版移动记录无法读取：" + ex.Message]; }
    }

    public CopyGroup[] Plan(Catalog catalog, Session session, Func<string, Mark>? effective = null)
    {
        var owners = catalog.Owners(session).ToLookup(pair => pair.Value, pair => pair.Key, Paths.Comparer);
        return catalog.Ordered.Select(relative => (relative, mark: effective?.Invoke(relative) ??
                session.Marks.GetValueOrDefault(relative)?.Mark ?? Mark.None))
            .Where(item => item.mark != Mark.None)
            .Select(item => new CopyGroup(item.relative, item.mark,
                new[] { item.relative }.Concat(owners[item.relative]).Select(file => new CopyMember(file, catalog.Files[file].Stamp)).ToArray()))
            .ToArray();
    }

    public ArchiveResult Run(CopyGroup[] groups)
    {
        var legacy = LegacyWarning();
        if (legacy.Length != 0) return new(0, 0, 0, ["旧版移动尚未核对，已停用此目录的复制归档。", .. legacy], false);
        if (ReadJournal().Pending != null) return new(0, 0, 0, ["请先处理未完成的复制记录。"], true);
        var issues = new List<string>();
        int completed = 0, unchanged = 0, conflicts = 0;
        foreach (var group in groups)
        {
            try
            {
                if (Copy(group)) completed++;
                else unchanged++;
            }
            catch (Exception ex)
            {
                if (ex is TargetConflict) conflicts++;
                issues.Add(group.Main + "：" + ex.Message);
                var recovered = Recover();
                if (recovered.RecoveryNeeded)
                    return new(completed, unchanged, conflicts, [.. issues, .. recovered.Issues], true);
            }
        }
        return new(completed, unchanged, conflicts, issues, false);
    }

    private bool Copy(CopyGroup group)
    {
        using var pins = new DirectoryPins();
        var held = new List<SafeFileHandle>();
        var missing = new List<CopyMember>();
        var input = new List<SafeFileHandle>();
        CopyTransfer? committed = null;
        try
        {
            foreach (var member in group.Members)
            {
                var source = Paths.Below(root, member.Relative);
                var target = Destination(group.Mark, member.Relative);
                pins.Pin(Path.GetDirectoryName(source)!, false);
                pins.Pin(Path.GetDirectoryName(target)!, true);
                var sourceHandle = NativeFiles.OpenRead(source);
                held.Add(sourceHandle);
                if (NativeFiles.Identify(sourceHandle) != member.SourceStamp)
                    throw new IOException("源文件已变化：" + source);
                if (NativeFiles.Exists(target))
                {
                    var targetHandle = NativeFiles.OpenRead(target);
                    held.Add(targetHandle);
                    if (NativeFiles.Identify(targetHandle).Length != member.SourceStamp.Length ||
                        Digest(sourceHandle) != Digest(targetHandle) || !NamedStreamsEqual(source, target))
                        throw new TargetConflict(target);
                }
                else { missing.Add(member); input.Add(sourceHandle); }
            }
            if (missing.Count == 0) return false;
            var transfer = new CopyTransfer(group.Mark, Guid.NewGuid(), missing.ToArray(), []);
            var stage = Stage(transfer);
            if (NativeFiles.Exists(stage)) throw new IOException("复制暂存路径已存在。");
            Save(transfer);
            pins.Pin(stage, true);
            var verified = new List<VerifiedCopy>();
            var outputs = new List<SafeFileHandle>();
            for (var slot = 0; slot < missing.Count; slot++)
            {
                var member = missing[slot];
                var temporary = Temporary(transfer, slot);
                var source = Paths.Below(root, member.Relative);
                var sourceHash = Digest(input[slot]);
                NativeFiles.CopyNew(source, temporary);
                Checkpoint?.Invoke("stage");
                var handle = NativeFiles.OpenCopyOutput(temporary);
                held.Add(handle);
                outputs.Add(handle);
                var stamp = NativeFiles.Identify(handle);
                if (stamp.Length != member.SourceStamp.Length || Digest(handle) != sourceHash ||
                    !NamedStreamsEqual(source, temporary) || NativeFiles.Identify(input[slot]) != member.SourceStamp)
                    throw new IOException("复制校验未通过：" + member.Relative);
                verified.Add(new(member.Relative, slot, stamp, FullDigest(handle, temporary, sourceHash)));
                transfer = transfer with { Verified = verified.ToArray() };
                Save(transfer);
                Checkpoint?.Invoke("verified");
            }
            for (var slot = 0; slot < outputs.Count; slot++)
            {
                var target = Destination(group.Mark, missing[slot].Relative);
                try { NativeFiles.Rename(outputs[slot], target); }
                catch (IOException) when (NativeFiles.Exists(target)) { throw new TargetConflict(target); }
                Checkpoint?.Invoke("publish");
            }
            Save(null);
            committed = transfer;
            return true;
        }
        finally
        {
            foreach (var handle in held) handle.Dispose();
            pins.Dispose();
            if (committed != null) TryRemoveStage(committed);
        }
    }

    public ArchiveResult Recover()
    {
        var transfer = ReadJournal().Pending;
        if (transfer == null) return new(0, 0, 0, [], false);
        var details = new List<string>();
        try
        {
            if (transfer.Token == Guid.Empty || transfer.Missing.Length == 0 ||
                transfer.Verified.Length > transfer.Missing.Length)
                throw new IOException("复制记录不完整。");
            using (var pins = new DirectoryPins())
            {
                var stage = Stage(transfer);
                if (NativeFiles.Exists(stage))
                {
                    pins.Pin(stage, false);
                    var known = Enumerable.Range(0, transfer.Missing.Length).Select(slot => Temporary(transfer, slot))
                        .ToHashSet(Paths.Comparer);
                    if (Directory.EnumerateFileSystemEntries(stage).Any(path => !known.Contains(path)))
                        throw new IOException("暂存目录中有无法确认归属的文件：" + stage);
                }
                for (var slot = 0; slot < transfer.Missing.Length; slot++)
                {
                    var member = transfer.Missing[slot];
                    var temporary = Temporary(transfer, slot);
                    var target = Destination(transfer.Mark, member.Relative);
                    if (slot >= transfer.Verified.Length)
                    {
                        if (NativeFiles.Exists(temporary))
                            throw new IOException("此暂存副本尚未验证，请检查并手动移出暂存目录后重试恢复：" + temporary);
                        continue;
                    }
                    var verified = transfer.Verified[slot];
                    if (verified.Slot != slot || !Paths.Comparer.Equals(verified.Relative, member.Relative))
                        throw new IOException("复制记录不完整。");
                    foreach (var path in new[] { temporary, target })
                    {
                        if (!NativeFiles.Exists(path)) continue;
                        pins.Pin(Path.GetDirectoryName(path)!, false);
                        var found = NativeFiles.Inspect(path);
                        if (found == null) continue;
                        if (!SameIdentity(found, verified.Stamp))
                        {
                            if (Paths.Comparer.Equals(path, target)) continue;
                            throw new IOException("暂存副本身份已变化：" + path);
                        }
                        using var handle = NativeFiles.OpenCopyOutput(path);
                        if (NativeFiles.Identify(handle) != verified.Stamp || FullDigest(handle, path) != verified.Hash)
                            throw new IOException("副本已变化，保留不动：" + path);
                        NativeFiles.RemoveOwnedCopy(handle);
                    }
                }
            }
            Save(null);
            TryRemoveStage(transfer);
            return new(1, 0, 0, [], false);
        }
        catch (Exception ex)
        {
            details.Add(ex.Message);
            try
            {
                details.Add(Stage(transfer));
                details.AddRange(transfer.Missing.Select(member => Destination(transfer.Mark, member.Relative)));
            }
            catch (Exception) { details.Add("复制记录中的路径无效，未批量清理。"); }
            return new(0, 0, 0, details, true);
        }
    }

    private string Stage(CopyTransfer transfer) => Paths.Below(Paths.TargetRoot(root, transfer.Mark),
        ".filmpick-copy-" + transfer.Token.ToString("N"));
    private string Temporary(CopyTransfer transfer, int slot) => Paths.Below(Stage(transfer), slot.ToString("D6") + ".partial");
    private void Save(CopyTransfer? transfer) => store.Write("copy-journal", new CopyJournal { Pending = transfer });

    private void TryRemoveStage(CopyTransfer transfer)
    {
        try { Directory.Delete(Stage(transfer), false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static bool SameIdentity(Stamp left, Stamp right) =>
        left.Volume == right.Volume && left.IdLow == right.IdLow && left.IdHigh == right.IdHigh;

    private static string Digest(SafeFileHandle file)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            long offset = 0;
            int count;
            while ((count = RandomAccess.Read(file, buffer.AsSpan(), offset)) > 0)
            {
                hash.AppendData(buffer.AsSpan(0, count));
                offset += count;
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static bool NamedStreamsEqual(string source, string target)
    {
        var sourceStreams = NativeFiles.NamedStreams(source);
        var targetStreams = NativeFiles.NamedStreams(target);
        if (sourceStreams.Count != targetStreams.Count) return false;
        foreach (var (name, length) in sourceStreams)
        {
            if (!targetStreams.TryGetValue(name, out var targetLength) || length != targetLength) return false;
            using var input = File.OpenRead(source + name);
            using var output = new FileStream(target + name, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (!SHA256.HashData(input).AsSpan().SequenceEqual(SHA256.HashData(output))) return false;
        }
        return true;
    }

    private static string FullDigest(SafeFileHandle handle, string path, string? mainHash = null)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Convert.FromHexString(mainHash ?? Digest(handle)));
        foreach (var (name, _) in NativeFiles.NamedStreams(path).OrderBy(pair => pair.Key, Paths.Comparer))
        {
            hash.AppendData(System.Text.Encoding.Unicode.GetBytes(name.ToUpperInvariant() + "\0"));
            using var stream = new FileStream(path + name, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            hash.AppendData(SHA256.HashData(stream));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private sealed class TargetConflict(string path) : IOException("目标同名且不可复用：" + path);
}
