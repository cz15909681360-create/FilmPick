using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace FilmPick;

sealed record LutEntry(string Hash, string Name, string[] Tags, bool Favorite)
{
    public string TagLine => string.Join(" · ", Tags);
    public string Star => Favorite ? "★" : "☆";
}

sealed record LutIndex(int Version, LutEntry[] Entries, string[] Tags, string? Selected);
sealed record LutImport(int Added, int Duplicate, string? First, string[] Errors);

sealed class LutLibrary(string appData)
{
    private const int Version = 1, MaxIndexBytes = 4 * 1024 * 1024;
    private const long MaxBackupBytes = 2L * 1024 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, IgnoreReadOnlyProperties = true };
    private static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;
    private readonly string folder = Path.Combine(appData, "lut-library");
    private string IndexPath => Path.Combine(folder, "library.json");
    public LutIndex Index { get; private set; } = new(Version, [], [], null);

    public void Load()
    {
        if (!File.Exists(IndexPath)) return;
        using var file = File.OpenRead(IndexPath);
        Index = ReadIndex(file, file.Length);
    }

    public string FileFor(string hash)
    {
        if (!ValidHash(hash)) throw new IOException("无效的 LUT 标识。");
        return Path.Combine(folder, "files", hash + ".cube");
    }

    public Cube Read(string hash)
    {
        var cube = Cube.Read(FileFor(hash));
        CheckHash(cube, hash);
        return cube;
    }

    public LutImport Import(IEnumerable<string> files)
    {
        var entries = Index.Entries.ToList();
        var errors = new List<string>();
        var duplicate = 0;
        string? first = null;
        foreach (var file in files)
        {
            try
            {
                var canonical = Cube.Read(file).Canonical();
                var hash = Cube.Hash(canonical);
                if (entries.Any(item => item.Hash == hash)) duplicate++;
                else
                {
                    WriteCube(hash, canonical);
                    entries.Add(new(hash, UniqueName(Path.GetFileNameWithoutExtension(file), entries), [], false));
                }
                first ??= hash;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                errors.Add(Path.GetFileName(file) + "：" + ex.Message);
            }
        }
        return FinishImport(entries, Index.Tags, duplicate, first, errors.ToArray());
    }

    public LutImport SaveTrial(string name, Cube cube)
    {
        var canonical = cube.Canonical();
        var hash = Cube.Hash(canonical);
        var entries = Index.Entries.ToList();
        var duplicate = entries.Any(item => item.Hash == hash);
        if (!duplicate)
        {
            WriteCube(hash, canonical);
            entries.Add(new(hash, UniqueName(name, entries), [], false));
        }
        return FinishImport(entries, Index.Tags, duplicate ? 1 : 0, hash, []);
    }

    public void Select(string? hash)
    {
        if (hash == Index.Selected) return;
        if (hash != null) Get(hash);
        Commit(Index with { Selected = hash });
    }

    public void Edit(string hash, string name, IEnumerable<string> tags)
    {
        name = Label(name);
        if (Index.Entries.Any(item => item.Hash != hash && Names.Equals(item.Name, name)))
            throw new IOException("已有同名 LUT。");
        var links = tags.Select(tag => Index.Tags.FirstOrDefault(item => Names.Equals(item, tag)) ??
            throw new IOException("标签不存在：" + tag)).Distinct(Names).Order(Names).ToArray();
        Change(Get(hash) with { Name = name, Tags = links });
    }

    public void Favorite(string hash) { var entry = Get(hash); Change(entry with { Favorite = !entry.Favorite }); }

    public void Remove(string hash)
    {
        Get(hash);
        Commit(Index with
        {
            Entries = Index.Entries.Where(item => item.Hash != hash).ToArray(),
            Selected = Index.Selected == hash ? null : Index.Selected
        });
        System.IO.File.Delete(FileFor(hash));
    }

    public void AddTag(string name)
    {
        name = Label(name);
        if (Index.Tags.Contains(name, Names)) throw new IOException("标签已存在。");
        Commit(Index with { Tags = [.. Index.Tags, name] });
    }

    public void ChangeTag(string previous, string? next)
    {
        if (!Index.Tags.Contains(previous, Names)) throw new IOException("标签不存在。");
        if (next != null)
        {
            next = Label(next);
            if (Index.Tags.Any(tag => !Names.Equals(tag, previous) && Names.Equals(tag, next))) throw new IOException("标签已存在。");
        }
        string[] Rewrite(string[] list) => list.Select(tag => Names.Equals(tag, previous) ? next : tag).OfType<string>().ToArray();
        Commit(Index with { Tags = Rewrite(Index.Tags), Entries = Index.Entries.Select(item => item with { Tags = Rewrite(item.Tags) }).ToArray() });
    }

    public static IEnumerable<LutEntry> Filter(LutIndex index, string query, IEnumerable<string> tags, bool favorites)
    {
        var required = tags.ToArray();
        var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return index.Entries.Where(entry => (!favorites || entry.Favorite) &&
            required.All(tag => entry.Tags.Contains(tag, Names)) &&
            words.All(word => entry.Name.Contains(word, StringComparison.OrdinalIgnoreCase) ||
                entry.Tags.Any(tag => tag.Contains(word, StringComparison.OrdinalIgnoreCase))))
            .OrderByDescending(entry => entry.Favorite).ThenBy(entry => entry.Name, Names);
    }

    public void Backup(string path)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                using (var zip = new ZipArchive(file, ZipArchiveMode.Create, true))
                {
                    using (var manifest = zip.CreateEntry("manifest.json").Open())
                        JsonSerializer.Serialize(manifest, Index with { Selected = null }, Json);
                    foreach (var item in Index.Entries)
                    {
                        var bytes = Read(item.Hash).Canonical();
                        using var target = zip.CreateEntry(ZipName(item.Hash)).Open();
                        target.Write(bytes);
                    }
                }
                file.Flush(true);
            }
            File.Move(temporary, path);
        }
        finally { System.IO.File.Delete(temporary); }
    }

    public LutImport Merge(string path)
    {
        using var file = System.IO.File.OpenRead(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Read);
        var files = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries)
            if (!files.TryAdd(entry.FullName, entry)) throw new IOException("备份中有重复路径。");
        if (!files.TryGetValue("manifest.json", out var manifest)) throw new IOException("找不到 FilmPick 备份清单。");
        using var input = manifest.Open();
        var incoming = ReadIndex(input, manifest.Length);
        if (incoming.Selected != null || files.Count != incoming.Entries.Length + 1)
            throw new IOException("备份结构不符。");
        long total = 0;
        foreach (var item in incoming.Entries)
        {
            if (!files.TryGetValue(ZipName(item.Hash), out var archived) ||
                archived.Length > Cube.MaxBytes || (total += archived.Length) > MaxBackupBytes)
                throw new IOException("备份含缺失或过大的 LUT。");
            using var content = archived.Open();
            CheckHash(Cube.Read(content, archived.Length), item.Hash);
        }
        var entries = Index.Entries.ToList();
        var tags = Index.Tags.Concat(incoming.Tags).Distinct(Names).ToArray();
        var duplicate = 0;
        foreach (var item in incoming.Entries)
        {
            if (entries.Any(local => local.Hash == item.Hash)) { duplicate++; continue; }
            var archived = files[ZipName(item.Hash)];
            using var content = archived.Open();
            var canonical = Cube.Read(content, archived.Length).Canonical();
            if (Cube.Hash(canonical) != item.Hash) throw new IOException("备份校验失败。");
            WriteCube(item.Hash, canonical);
            entries.Add(item with { Name = UniqueName(item.Name, entries),
                Tags = item.Tags.Select(tag => tags.First(local => Names.Equals(local, tag))).ToArray() });
        }
        return FinishImport(entries, tags, duplicate, null, []);
    }

    private static void CheckHash(Cube cube, string hash)
    {
        if (Cube.Hash(cube.Canonical()) != hash) throw new IOException("LUT 内容校验失败：" + hash);
    }

    private LutImport FinishImport(List<LutEntry> entries, string[] tags, int duplicate, string? first, string[] errors)
    {
        var added = entries.Count - Index.Entries.Length;
        if (added != 0 || tags.Length != Index.Tags.Length)
            Commit(Index with { Entries = entries.ToArray(), Tags = tags });
        return new(added, duplicate, first, errors);
    }

    private static LutIndex ReadIndex(Stream input, long length)
    {
        if (length is <= 0 or > MaxIndexBytes) throw new IOException("LUT 清单大小无效。");
        var index = JsonSerializer.Deserialize<LutIndex>(input, Json) ?? throw new IOException("LUT 清单为空。");
        Validate(index);
        return index;
    }

    private static void Validate(LutIndex index)
    {
        if (index.Version != Version || index.Entries == null || index.Tags == null)
            throw new IOException("不支持的 LUT 库版本。");
        var knownTags = new HashSet<string>(Names);
        foreach (var tag in index.Tags)
            if (!ValidLabel(tag) || !knownTags.Add(tag)) throw new IOException("LUT 清单中的标签无效。");
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(Names);
        foreach (var entry in index.Entries)
            if (entry == null || !ValidHash(entry.Hash) || !hashes.Add(entry.Hash) ||
                !ValidLabel(entry.Name) || !names.Add(entry.Name) || entry.Tags == null ||
                entry.Tags.Any(tag => tag == null || !knownTags.Contains(tag)) ||
                entry.Tags.Distinct(Names).Count() != entry.Tags.Length)
                throw new IOException("LUT 清单条目无效。");
        if (index.Selected != null && !hashes.Contains(index.Selected)) throw new IOException("LUT 选择无效。");
    }

    private void Commit(LutIndex next)
    {
        Validate(next);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(next, Json);
        if (bytes.Length > MaxIndexBytes) throw new IOException("LUT 清单超出大小限制。");
        Write(IndexPath, bytes);
        Index = next;
    }

    private void Change(LutEntry entry) => Commit(Index with { Entries = Index.Entries.Select(item => item.Hash == entry.Hash ? entry : item).ToArray() });
    private LutEntry Get(string hash) => Index.Entries.FirstOrDefault(item => item.Hash == hash) ?? throw new IOException("找不到这个 LUT。");
    private static string ZipName(string hash) => "luts/" + hash + ".cube";

    private void WriteCube(string hash, byte[] content) => Write(FileFor(hash), content);

    private static void Write(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 16384, FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.Flush(true);
        }
        NativeFiles.ReplaceRecord(temporary, path);
    }

    private static string UniqueName(string name, List<LutEntry> entries)
    {
        name = name.Trim();
        if (!ValidLabel(name)) name = name.Length > 100 ? name[..100].Trim() : "LUT";
        if (!ValidLabel(name)) name = "LUT";
        if (!entries.Any(item => Names.Equals(item.Name, name))) return name;
        for (var number = 2; ; number++)
        {
            var suffix = $" ({number})";
            var candidate = name[..Math.Min(name.Length, 100 - suffix.Length)] + suffix;
            if (!entries.Any(item => Names.Equals(item.Name, candidate))) return candidate;
        }
    }

    private static bool ValidLabel(string? value) => value is { Length: > 0 and <= 100 } &&
        value == value.Trim() && !value.Any(char.IsControl);
    private static string Label(string value) => ValidLabel(value.Trim()) ? value.Trim() : throw new IOException("名称须为 1–100 个字符。");
    private static bool ValidHash(string? value) => value is { Length: 64 } && value.All(symbol => symbol is >= '0' and <= '9' or >= 'a' and <= 'f');
}
