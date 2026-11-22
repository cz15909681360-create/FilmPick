using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FilmPick;

record CaptureDate(DateTime Clock, TimeSpan? Offset, string Source = "")
{
    public DateOnly Day => DateOnly.FromDateTime(Clock);
    public string Display => Clock.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
        (Offset is { } offset ? $" {(offset < TimeSpan.Zero ? "-" : "+")}{offset.Duration():hh\\:mm}" : "") +
        (Source.Length > 0 ? $" · {Source}" : "");

    private static readonly Regex Pattern = new(
        @"^(?<date>\d{4}[:-]\d{1,2}[:-]\d{1,2})[ T](?<time>\d{2}:\d{2}:\d{2})(?:[.,](?<fraction>\d+))?\s*(?<zone>Z|[+-]\d{2}:?\d{2})?$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static CaptureDate? Parse(string? value, string? offset = null, string? fraction = null, string source = "")
    {
        if (value == null) return null;
        var match = Pattern.Match(value.Trim());
        if (!match.Success || !DateTime.TryParseExact(match.Groups["date"].Value.Replace(':', '-') + " " +
            match.Groups["time"].Value, "yyyy-M-d HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var time)) return null;
        var digits = match.Groups["fraction"].Success ? match.Groups["fraction"].Value : fraction?.Trim();
        if (!string.IsNullOrEmpty(digits) && int.TryParse(digits[..Math.Min(digits.Length, 7)].PadRight(7, '0'),
            NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)) time = time.AddTicks(ticks);
        var zone = match.Groups["zone"].Success ? match.Groups["zone"].Value : offset?.Trim() ?? "";
        TimeSpan? parsed = null;
        if (zone.Equals("Z", StringComparison.OrdinalIgnoreCase)) parsed = TimeSpan.Zero;
        else if (Regex.IsMatch(zone, @"^[+-]\d{2}:?\d{2}$", RegexOptions.CultureInvariant) &&
            TimeSpan.TryParseExact(zone[1..].Replace(":", "").Insert(2, ":"), @"hh\:mm",
                CultureInfo.InvariantCulture, out var magnitude) && magnitude <= TimeSpan.FromHours(14))
            parsed = zone[0] == '-' ? -magnitude : magnitude;
        return new(time, parsed, source);
    }
}

record Exposure(string? Shutter = null, string? Aperture = null, string? Iso = null,
    double? FocalMm = null, double? EquivalentMm = null, bool FullFrame = false)
{
    public bool HasValues => Shutter != null || Aperture != null || Iso != null || FocalMm != null || EquivalentMm != null;
    public Exposure Fill(Exposure other) => new(Shutter ?? other.Shutter, Aperture ?? other.Aperture,
        Iso ?? other.Iso, FocalMm ?? other.FocalMm, EquivalentMm ?? other.EquivalentMm,
        FullFrame || (FocalMm == null && EquivalentMm == null && other.FullFrame));

    public string? Focal
    {
        get
        {
            static string Mm(double value) => value.ToString("0.#", CultureInfo.InvariantCulture) + " mm";
            if (FocalMm is not { } physical) return EquivalentMm is { } only ? "等效 " + Mm(only) : null;
            if (FullFrame || EquivalentMm is not { } equivalent || Math.Round(physical, 1) == Math.Round(equivalent, 1) ||
                (equivalent == Math.Truncate(equivalent) && Math.Abs(physical - equivalent) <= .5)) return Mm(physical);
            return $"{Mm(physical)}（等效 {Mm(equivalent)}）";
        }
    }

    public static string? Positive(string? raw, string format, bool fraction = false)
    {
        if (PositiveNumber(raw) is not { } number) return null;
        if (fraction && number < 0.5 && 1 / number <= 1e7)
            return $"1/{(1 / number).ToString("0.##", CultureInfo.InvariantCulture)} s";
        return string.Format(CultureInfo.InvariantCulture, format, number);
    }

    public static double? PositiveNumber(string? raw)
    {
        if (raw == null) return null;
        var parts = raw.Split('/');
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return null;
        if (parts.Length == 2 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator))
            number /= denominator;
        else if (parts.Length != 1) return null;
        return double.IsFinite(number) && number is > 0 and <= 1e7 ? number : null;
    }
}

record CaptureMetadata(CaptureDate? Date, CaptureDate? ContainerDate, string? Camera, string? Lens,
    Exposure Exposure, string? Resolution, string? Fps, int? Rating, bool Failed = false, Mark? Flag = null,
    VideoSpec? Video = null, VideoColor? CameraColor = null)
{
    public static CaptureMetadata Empty { get; } = new(null, null, null, null, new(), null, null, null);
    public static CaptureMetadata Unreadable { get; } = Empty with { Failed = true };

    public CaptureMetadata Fill(CaptureMetadata other) => new(Date ?? other.Date, ContainerDate ?? other.ContainerDate,
        Camera ?? other.Camera, Lens ?? other.Lens, Exposure.Fill(other.Exposure),
        Resolution ?? other.Resolution, Fps ?? other.Fps, Rating ?? other.Rating, Failed && other.Failed,
        Flag ?? other.Flag, Video ?? other.Video, CameraColor ?? other.CameraColor);

    public static CaptureMetadata Parse(JsonElement tags)
    {
        string? Tag(params string[] names)
        {
            foreach (var name in names)
                if (tags.TryGetProperty(name, out var value))
                {
                    var clean = string.Join(" ", value.ToString()
                        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                    clean = string.Concat(clean.Where(c => !char.IsControl(c)));
                    if (clean.Length > 0) return clean.Length <= 100 ? clean : clean[..100];
                }
            return null;
        }

        if (Tag("ExifTool:Error", "Error") != null) return Unreadable;
        var date = CaptureDate.Parse(Tag("ExifIFD:DateTimeOriginal"), Tag("ExifIFD:OffsetTimeOriginal"),
            Tag("ExifIFD:SubSecTimeOriginal")) ??
            CaptureDate.Parse(Tag("Keys:CreationDate", "UserData:DateTimeOriginal", "ItemList:ContentCreateDate",
                "QuickTime:DateTimeOriginal", "XMP-exif:DateTimeOriginal"));
        var container = CaptureDate.Parse(Tag("QuickTime:CreateDate"), source: "容器时间");
        if (container?.Clock == new DateTime(1904, 1, 1)) container = null;
        var width = Tag("QuickTime:ImageWidth", "Track1:ImageWidth", "Track2:ImageWidth");
        var height = Tag("QuickTime:ImageHeight", "Track1:ImageHeight", "Track2:ImageHeight");
        var make = Tag("IFD0:Make", "Keys:Make", "UserData:Make", "XMP-tiff:Make");
        var model = Tag("IFD0:Model", "Keys:Model", "UserData:Model", "XMP-tiff:Model");
        var camera = make == null || model?.StartsWith(make, StringComparison.OrdinalIgnoreCase) == true
            ? model : model == null ? make : $"{make} {model}";
        var rawRating = Tag("XMP-xmp:Rating", "IFD0:Rating");
        int? rating = int.TryParse(rawRating, NumberStyles.Integer, CultureInfo.InvariantCulture, out var stars)
            && stars is >= -1 and <= 5 ? Math.Max(0, stars) : null;
        if (rating == null && int.TryParse(Tag("IFD0:RatingPercent", "XMP-microsoft:RatingPercent"), out var percent) &&
            percent is >= 0 and <= 99)
            rating = percent switch { 0 => 0, < 13 => 1, < 38 => 2, < 63 => 3, < 88 => 4, _ => 5 };
        Mark? flag = Tag("XMP-xmpDM:Pick") switch { "1" => Mark.Pick, "-1" => Mark.Reject, "0" => Mark.None, _ => null };
        return new(date, container, camera, Tag("ExifIFD:LensModel", "Keys:LensModel", "XMP-exifEX:LensModel", "XMP-aux:Lens"),
            new(Exposure.Positive(Tag("ExifIFD:ExposureTime", "Keys:ExposureTime", "XMP-exif:ExposureTime"), "{0:0.######} s", true),
                Exposure.Positive(Tag("ExifIFD:FNumber", "Keys:FNumber", "XMP-exif:FNumber"), "f/{0:0.##}"),
                Exposure.Positive(Tag("ExifIFD:ISO", "Keys:ISO", "XMP-exif:ISO"), "ISO {0:0}"),
                Exposure.PositiveNumber(Tag("ExifIFD:FocalLength", "VideoKeys:FocalLength", "Keys:FocalLength", "XMP-exif:FocalLength")),
                Exposure.PositiveNumber(Tag("ExifIFD:FocalLengthIn35mmFormat", "VideoKeys:FocalLengthIn35mmFormat",
                    "Keys:FocalLengthIn35mmFormat", "XMP-exif:FocalLengthIn35mmFormat")),
                Tag("Nikon:CropHiSpeed")?.StartsWith("11 ", StringComparison.Ordinal) == true),
            width != null && height != null && int.TryParse(width, out var w) && int.TryParse(height, out var h) && w > 0 && h > 0
                ? $"{w} × {h}" : null,
            Exposure.Positive(Tag("QuickTime:VideoFrameRate", "Track1:VideoFrameRate", "Track2:VideoFrameRate"), "{0:0.##} fps"),
            rating, Flag: flag, Video: VideoSpec.Parse(tags));
    }
}

sealed class CaptureReader : IDisposable
{
    private const string Tags = "-DateTimeOriginal -OffsetTimeOriginal -SubSecTimeOriginal -CreationDate -ContentCreateDate " +
        "-CreateDate -Make -Model -LensModel -Lens -ExposureTime -FNumber -ISO -FocalLength -ImageWidth -ImageHeight -VideoFrameRate " +
        "-Rating -RatingPercent -XMP-xmpDM:Pick -CompressorID -AVCConfiguration -Unknown_hvcC -ColorPrimaries -TransferCharacteristics " +
        "-FocalLengthIn35mmFormat -CropHiSpeed";
    private static readonly Encoding Utf8 = new UTF8Encoding(false);
    private readonly string executable;
    private Process? process;
    private int sequence;

    public CaptureReader(string? executable = null) =>
        this.executable = executable ?? Path.Combine(AppContext.BaseDirectory, "metadata", "exiftool.exe");

    public async Task<Dictionary<string, CaptureMetadata>> Read(string[] paths, CancellationToken cancel)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        var pending = deadline.Token;
        var values = new Dictionary<string, CaptureMetadata>(Paths.Comparer);
        if (paths.Length == 0) return values;
        if (!File.Exists(executable)) return values;
        try
        {
            process ??= Start();
            var id = (++sequence).ToString(CultureInfo.InvariantCulture);
            var requested = new Dictionary<string, string>(Paths.Comparer);
            foreach (var path in paths) requested.TryAdd(ReadablePath(path), path);
            var command = "-json\n-G1\n-n\n-u\n-b\n" + Tags.Replace(' ', '\n') + "\n" +
                string.Join('\n', requested.Keys) + "\n-execute" + id + "\n";
            await process.StandardInput.WriteAsync(command.AsMemory(), pending).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(pending).ConfigureAwait(false);
            var json = new StringBuilder();
            var ready = "{ready" + id + "}";
            var complete = false;
            while (await process.StandardOutput.ReadLineAsync(pending).ConfigureAwait(false) is { } line)
            {
                if (line == ready) { complete = true; break; }
                json.AppendLine(line);
                if (json.Length > 2_000_000) throw new IOException("拍摄信息响应过大。");
            }
            if (!complete || json.Length == 0) throw new IOException("拍摄信息读取进程中断。");
            using var result = JsonDocument.Parse(json.ToString());
            foreach (var entry in result.RootElement.EnumerateArray())
                if (entry.TryGetProperty("SourceFile", out var path) && path.GetString() is { } file &&
                    requested.TryGetValue(file.Replace('/', '\\'), out var original))
                    values[original] = CaptureMetadata.Parse(entry);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception
            or OperationCanceledException or JsonException)
        {
            Dispose();
            cancel.ThrowIfCancellationRequested();
        }
        return values;
    }

    private static string ReadablePath(string path)
    {
        // ExifTool's Windows Perl file API can fail on names containing UTF-16 surrogate pairs.
        if (!path.Any(char.IsSurrogate)) return path;
        var buffer = new StringBuilder(32768);
        var length = GetShortPathName(path, buffer, (uint)buffer.Capacity);
        return length > 0 && length < buffer.Capacity ? buffer.ToString() : path;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetShortPathNameW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetShortPathName(string path, StringBuilder buffer, uint capacity);

    private Process Start()
    {
        var options = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = Utf8, StandardOutputEncoding = Utf8, StandardErrorEncoding = Utf8,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        foreach (var arg in new[] { "-config", "", "-charset", "filename=UTF8", "-stay_open", "True", "-@", "-" })
            options.ArgumentList.Add(arg);
        var child = Process.Start(options) ?? throw new IOException("无法启动拍摄信息读取进程。");
        child.ErrorDataReceived += (_, _) => { };
        child.BeginErrorReadLine();
        return child;
    }

    public void Dispose()
    {
        if (process == null) return;
        try { if (!process.HasExited) process.Kill(true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        finally { process.Dispose(); process = null; }
    }
}
