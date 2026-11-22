using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace FilmPick;

enum Kind { Photo, Video }
enum Mark { None, Pick, Reject }

record Stamp(ulong Volume, ulong IdLow, ulong IdHigh, long Creation, long Modified, long Length);
record MediaFile(string Relative, Stamp Stamp);
record SavedMark(Mark Mark, Stamp Stamp);
record SavedRating(int Stars, Stamp Stamp)
{
    public bool Matches(Stamp stamp) => Stamp.Volume == stamp.Volume && Stamp.IdLow == stamp.IdLow &&
        Stamp.IdHigh == stamp.IdHigh && Stamp.Creation == stamp.Creation;
}
record SavedLink(string Owner, Stamp OwnerStamp, Stamp AttachmentStamp);

sealed class Session
{
    public bool Recursive { get; set; }
    public bool AutoAdvance { get; set; } = true;
    public Kind? View { get; set; }
    public string? PhotoPosition { get; set; }
    public string? VideoPosition { get; set; }
    public Dictionary<string, SavedMark> Marks { get; set; } = new(Paths.Comparer);
    public Dictionary<string, SavedRating> Ratings { get; set; } = new(Paths.Comparer);
    public Dictionary<string, SavedLink> Links { get; set; } = new(Paths.Comparer);
}

static class Paths
{
    public static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    public static string Root(string input)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(input));
        var driveRoot = Path.GetPathRoot(full)!;
        if (driveRoot.Length != 3 || Comparer.Equals(full, driveRoot))
            throw new IOException("请选择本机盘符下的素材文件夹，不要选盘符根目录或网络路径。");
        var drive = new DriveInfo(driveRoot);
        if (drive.DriveType != DriveType.Fixed || drive.DriveFormat != "NTFS")
            throw new IOException("首版只支持本机 NTFS 固定磁盘。");
        using var pins = new DirectoryPins();
        pins.Pin(full, false);
        return full;
    }

    public static string TargetRoot(string root, Mark mark) =>
        root + (mark switch { Mark.Pick => "_已选", Mark.Reject => "_已排除", _ => throw new ArgumentException() });

    public static string Below(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains(':')) throw new IOException("无效的相对素材路径。");
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("素材路径超出扫描范围。");
        return full;
    }
}

sealed class Store(string root, string? appData = null)
{
    public static string DefaultData => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FilmPick");
    private readonly string folder = Path.Combine(appData ?? DefaultData,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant()))));
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public T Read<T>(string name) where T : new()
    {
        var path = Path.Combine(folder, name + ".json");
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(stream, Options) ?? throw new IOException("本地记录为空：" + path);
        }
        catch (FileNotFoundException) { return new T(); }
        catch (DirectoryNotFoundException) { return new T(); }
    }

    public void Write<T>(string name, T value)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name + ".json");
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 16384, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, value, Options);
            stream.Flush(true);
        }
        NativeFiles.ReplaceRecord(temporary, path);
    }
}

sealed class Catalog
{
    private static readonly HashSet<string> Photos = new([".jpg", ".jpeg", ".png"], Paths.Comparer);
    private static readonly HashSet<string> Raw = new([".nef", ".nrw", ".cr2", ".cr3", ".arw", ".raf", ".dng"], Paths.Comparer);
    private static readonly HashSet<string> Videos = new([".mp4", ".mov"], Paths.Comparer);
    private static readonly HashSet<string> Sidecars = new([".lrf", ".srt", ".xml", ".xmp", ".acr", ".lrv", ".thm", ".aae", ".vtt", ".ass", ".scc", ".json"], Paths.Comparer);
    public Dictionary<string, MediaFile> Files { get; } = new(Paths.Comparer);
    public Dictionary<string, Kind> Main { get; } = new(Paths.Comparer);
    public Dictionary<string, string> Automatic { get; } = new(Paths.Comparer);
    public Dictionary<string, string> Pairs { get; } = new(Paths.Comparer);
    public Dictionary<string, string> RawCompanions { get; } = new(Paths.Comparer);
    public List<string> Warnings { get; } = [];
    public List<string> Ordered { get; } = [];
    public static bool IsRaw(string path) => Raw.Contains(Path.GetExtension(path));
    public static string MediaFilter => string.Join(";", Photos.Concat(Raw).Concat(Videos).Order(Paths.Comparer).Select(ext => "*" + ext));
    public string GroupOf(string path) => Pairs.TryGetValue(path, out var group) ? group : path;

    public static Catalog Scan(string root, bool recursive)
    {
        var result = new Catalog();
        var todo = new Stack<string>();
        todo.Push(root);
        while (todo.TryPop(out var directory))
        {
            try
            {
                using var pins = new DirectoryPins();
                pins.Pin(directory, false);
                foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
                {
                    var attrs = entry.Attributes;
                    if ((attrs & FileAttributes.ReparsePoint) != 0) continue;
                    if ((attrs & FileAttributes.Directory) != 0)
                    {
                        const string stagePrefix = ".filmpick-grade-";
                        if (recursive && !(entry.Name.StartsWith(stagePrefix, StringComparison.OrdinalIgnoreCase) &&
                            Guid.TryParseExact(entry.Name[stagePrefix.Length..], "N", out _))) todo.Push(entry.FullName);
                        continue;
                    }
                    var relative = Path.GetRelativePath(root, entry.FullName);
                    try
                    {
                        var stamp = NativeFiles.Inspect(entry.FullName) ?? throw new IOException("文件已消失。");
                        if (!result.Files.TryAdd(relative, new MediaFile(relative, stamp)))
                            throw new IOException("存在仅大小写不同的文件名，不能安全归档。");
                        if (Photos.Contains(entry.Extension) || Raw.Contains(entry.Extension)) result.Main.Add(relative, Kind.Photo);
                        else if (Videos.Contains(entry.Extension)) result.Main.Add(relative, Kind.Video);
                    }
                    catch (Exception ex) when (ex is IOException or Win32Exception or UnauthorizedAccessException)
                    {
                        result.Warnings.Add(relative + "：" + ex.Message);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or Win32Exception or UnauthorizedAccessException)
            {
                if (Paths.Comparer.Equals(root, directory)) throw;
                result.Warnings.Add(directory + "：" + ex.Message);
            }
        }

        var stems = result.Main.Keys.GroupBy(name => Path.ChangeExtension(name, null), Paths.Comparer)
            .ToDictionary(group => group.Key, group => group.ToArray(), Paths.Comparer);
        foreach (var candidates in stems.Values)
        {
            var raw = candidates.Where(IsRaw).Take(2).ToArray();
            var jpeg = candidates.Where(name => Paths.Comparer.Equals(Path.GetExtension(name), ".jpg") ||
                Paths.Comparer.Equals(Path.GetExtension(name), ".jpeg")).Take(2).ToArray();
            if (raw.Length == 1 && jpeg.Length == 1)
            {
                result.Pairs.Add(raw[0], jpeg[0]);
                result.RawCompanions.Add(jpeg[0], raw[0]);
            }
        }
        var groups = stems.ToDictionary(pair => pair.Key, pair => pair.Value.Select(result.GroupOf)
            .Distinct(Paths.Comparer).ToArray(), Paths.Comparer);
        foreach (var file in result.Files.Keys)
        {
            if (result.Main.ContainsKey(file) || !Sidecars.Contains(Path.GetExtension(file))) continue;
            var prefix = Path.ChangeExtension(file, null);
            if (result.Main.ContainsKey(prefix)) result.Automatic.Add(file, result.GroupOf(prefix));
            else if (groups.TryGetValue(prefix, out var candidates) && candidates.Length == 1)
                result.Automatic.Add(file, candidates[0]);
            else if (Path.GetExtension(file).Equals(".xml", StringComparison.OrdinalIgnoreCase) &&
                prefix.EndsWith("M01", StringComparison.OrdinalIgnoreCase) &&
                groups.TryGetValue(prefix[..^3], out var sony) && sony.Length == 1 && result.Main[sony[0]] == Kind.Video &&
                VideoColors.ReadSony(Paths.Below(root, file)).Recognized)
                result.Automatic.Add(file, sony[0]);
        }
        result.Ordered.AddRange(result.Main.Keys.Where(name => !result.Pairs.ContainsKey(name)).Order(Comparer<string>.Create((left, right) =>
        {
            var natural = NativeFiles.NaturalCompare(left, right);
            return natural != 0 ? natural : Paths.Comparer.Compare(left, right);
        })));
        return result;
    }

    public void Reconcile(Session session)
    {
        session.Ratings = (session.Ratings ?? new(Paths.Comparer)).Where(pair => pair.Value is { Stars: >= 0 and <= 5 }
            && (Outside(pair.Key) || (Main.ContainsKey(pair.Key) && pair.Value.Matches(Files[pair.Key].Stamp))))
            .ToDictionary(pair => pair.Key, pair => pair.Value, Paths.Comparer);
        session.Marks = session.Marks.Where(pair => Outside(pair.Key) ||
            (Main.ContainsKey(pair.Key) && Files[pair.Key].Stamp == pair.Value.Stamp))
            .ToDictionary(pair => pair.Key, pair => pair.Value, Paths.Comparer);
        foreach (var (raw, jpeg) in Pairs)
        {
            if (session.Marks.Remove(raw, out var saved) && !session.Marks.ContainsKey(jpeg))
                session.Marks.Add(jpeg, new(saved.Mark, Files[jpeg].Stamp));
        }
        session.Links = session.Links.Where(pair => Outside(pair.Key) || Outside(pair.Value.Owner) ||
            (!Main.ContainsKey(pair.Key) && !Automatic.ContainsKey(pair.Key)
            && Files.TryGetValue(pair.Key, out var file) && file.Stamp == pair.Value.AttachmentStamp
            && Main.ContainsKey(pair.Value.Owner) && Files[pair.Value.Owner].Stamp == pair.Value.OwnerStamp))
            .ToDictionary(pair => pair.Key, pair => pair.Value, Paths.Comparer);
        bool Outside(string path) => !session.Recursive && !string.IsNullOrEmpty(Path.GetDirectoryName(path));
    }

    public Dictionary<string, string> Owners(Session session)
    {
        var owners = new Dictionary<string, string>(Automatic, Paths.Comparer);
        foreach (var (raw, jpeg) in Pairs) owners.Add(raw, jpeg);
        foreach (var (attachment, link) in session.Links)
            if (Files.ContainsKey(attachment) && Main.ContainsKey(link.Owner)) owners.TryAdd(attachment, GroupOf(link.Owner));
        return owners;
    }

    public bool IsKnownAttachment(string file) => Sidecars.Contains(Path.GetExtension(file));
}

sealed class DirectoryPins : IDisposable
{
    private readonly Dictionary<string, SafeFileHandle> handles = new(Paths.Comparer);

    public void Pin(string directory, bool create)
    {
        if (handles.ContainsKey(directory)) return;
        if (Path.GetDirectoryName(directory) is { } parent) Pin(parent, create);
        if (create) Directory.CreateDirectory(directory);
        var handle = NativeFiles.OpenDirectory(directory);
        handles.Add(directory, handle);
    }

    public void Dispose()
    {
        foreach (var handle in handles.Values.Reverse()) handle.Dispose();
        handles.Clear();
    }
}

static class NativeFiles
{
    private const uint ReadAttributes = 0x80, Delete = 0x10000;
    private const uint OpenReparse = 0x00200000, BackupSemantics = 0x02000000;

    public static Stamp? Inspect(string path)
    {
        using var handle = CreateFile(Long(path), ReadAttributes, 7, IntPtr.Zero, 3, OpenReparse | BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid && Marshal.GetLastWin32Error() is 2 or 3) return null;
        CheckHandle(handle, path, false);
        return Identify(handle);
    }

    public static bool Exists(string path)
    {
        var attrs = GetFileAttributes(Long(path));
        if (attrs != uint.MaxValue) return true;
        if (Marshal.GetLastWin32Error() is 2 or 3) return false;
        throw Error(path);
    }

    public static SafeFileHandle OpenRead(string path) => Open(path, 0x80000000, 1, false);
    public static SafeFileHandle OpenMetadataTarget(string path) => Open(path, 0x80000000, 5, false);
    public static SafeFileHandle OpenCopyOutput(string path) => Open(path, 0x80000000 | Delete, 1, false);
    public static SafeFileHandle OpenDirectory(string path) => Open(path, ReadAttributes, 3, true);

    private static SafeFileHandle Open(string path, uint access, uint share, bool directory)
    {
        var handle = CreateFile(Long(path), access, share, IntPtr.Zero, 3, OpenReparse | BackupSemantics, IntPtr.Zero);
        try { CheckHandle(handle, path, directory); return handle; }
        catch { handle.Dispose(); throw; }
    }

    private static void CheckHandle(SafeFileHandle handle, string path, bool directory)
    {
        if (handle.IsInvalid) throw Error(path);
        if (!GetFileInformationByHandle(handle, out var info)) throw Error(path);
        if ((info.Attributes & 0x400) != 0 || ((info.Attributes & 0x10) != 0) != directory)
            throw new IOException("不跟随链接，也不归档非普通文件：" + path);
    }

    public static Stamp Identify(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info) ||
            !GetFileInformationByHandleEx(handle, 18, out var id, 24)) throw Error("读取文件标识");
        return new(id.Volume, id.Low, id.High, Join(info.CreationHigh, info.CreationLow),
            Join(info.WriteHigh, info.WriteLow), Join(info.SizeHigh, info.SizeLow));
    }

    public static void Rename(SafeFileHandle handle, string target)
    {
        var bytes = Encoding.Unicode.GetBytes(Long(target));
        var buffer = Marshal.AllocHGlobal(24 + bytes.Length);
        try
        {
            Marshal.WriteInt64(buffer, 0, 0);
            Marshal.WriteInt64(buffer, 8, 0);
            Marshal.WriteInt32(buffer, 16, bytes.Length);
            Marshal.Copy(bytes, 0, buffer + 20, bytes.Length);
            Marshal.WriteInt16(buffer, 20 + bytes.Length, 0);
            if (!SetFileInformationByHandle(handle, 3, buffer, (uint)(24 + bytes.Length)))
                throw Error(target);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public static void ReplaceRecord(string source, string target)
    {
        if (!MoveFileEx(Long(source), Long(target), 1 | 8)) throw Error(target);
    }

    public static void CopyNew(string source, string target)
    {
        if (!CopyFile(Long(source), Long(target), true)) throw Error(target);
    }

    public static Dictionary<string, long> NamedStreams(string path)
    {
        var result = new Dictionary<string, long>(Paths.Comparer);
        var search = FindFirstStream(Long(path), 0, out var stream, 0);
        if (search == new IntPtr(-1))
        {
            if (Marshal.GetLastWin32Error() == 38) return result;
            throw Error(path);
        }
        try
        {
            do
            {
                if (stream.Name != "::$DATA") result.Add(stream.Name, stream.Length);
            } while (FindNextStream(search, out stream));
            if (Marshal.GetLastWin32Error() != 38) throw Error(path);
            return result;
        }
        finally { FindClose(search); }
    }

    public static void RemoveOwnedCopy(SafeFileHandle handle)
    {
        var flags = 0x13;
        if (!SetFileInformationByHandle(handle, 21, ref flags, 4)) throw Error("清理未提交的归档副本");
    }

    private static long Join(uint high, uint low) => ((long)high << 32) | low;
    private static string Long(string path) => @"\\?\" + path;
    private static IOException Error(string path)
    {
        var error = new Win32Exception(Marshal.GetLastWin32Error());
        return new IOException($"{path}：{error.Message}", error);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandle
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh,
            Serial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileId { public ulong Volume, Low, High; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StreamInfo
    {
        public long Length;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)] public string Name;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFileAttributes(string path);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandle info);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind, out FileId info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int kind, IntPtr buffer, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int kind, ref int buffer, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CopyFile(string source, string target, bool failIfExists);
    [DllImport("kernel32.dll", EntryPoint = "FindFirstStreamW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstStream(string path, int level, out StreamInfo data, int flags);
    [DllImport("kernel32.dll", EntryPoint = "FindNextStreamW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool FindNextStream(IntPtr handle, out StreamInfo data);
    [DllImport("kernel32.dll")] private static extern bool FindClose(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileEx(string source, string target, uint flags);
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, EntryPoint = "StrCmpLogicalW")]
    public static extern int NaturalCompare(string left, string right);
}
