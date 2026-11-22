using System.Globalization;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace FilmPick;

sealed record Cube(int Size, Vector3 Minimum, Vector3 Maximum, Vector3[] Colors)
{
    public const int MaxBytes = 24 * 1024 * 1024;

    public static Cube Read(string path)
    {
        if (!Path.GetExtension(path).Equals(".cube", StringComparison.OrdinalIgnoreCase))
            throw new IOException("请选择 .cube 文件。");
        using var stream = File.OpenRead(path);
        return Read(stream, stream.Length);
    }

    public static Cube Read(Stream stream, long length)
    {
        if (length is <= 0 or > MaxBytes) throw new IOException("LUT 文件为空或过大。");
        var bytes = new byte[(int)length];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new IOException("LUT 文件长度不符。");
        using var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false, true));
        var size = 0;
        var colors = new List<Vector3>();
        var minimum = Vector3.Zero;
        var maximum = Vector3.One;
        var domainMin = false;
        var domainMax = false;
        while (reader.ReadLine() is { } raw)
        {
            var comment = raw.IndexOf('#');
            var line = (comment < 0 ? raw : raw[..comment]).Trim();
            if (line.Length == 0) continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            switch (parts[0])
            {
                case "TITLE" when colors.Count == 0: break;
                case "LUT_3D_SIZE" when size == 0 && parts.Length == 2 &&
                    int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out size) && size is >= 2 and <= 65:
                    break;
                case "DOMAIN_MIN" when colors.Count == 0 && !domainMin:
                    minimum = Vector(parts.AsSpan(1));
                    domainMin = true;
                    break;
                case "DOMAIN_MAX" when colors.Count == 0 && !domainMax:
                    maximum = Vector(parts.AsSpan(1));
                    domainMax = true;
                    break;
                case "LUT_3D_INPUT_RANGE" when colors.Count == 0 && !domainMin && !domainMax && parts.Length == 3:
                    minimum = new Vector3(Number(parts[1]));
                    maximum = new Vector3(Number(parts[2]));
                    domainMin = domainMax = true;
                    break;
                default:
                    if (size < 2 || colors.Count >= size * size * size)
                        throw new IOException("仅支持 2–65 阶的 3D .cube；不支持 1D 或组合 LUT。");
                    colors.Add(Vector(parts));
                    break;
            }
        }
        if (size == 0 || colors.Count != size * size * size ||
            minimum.X >= maximum.X || minimum.Y >= maximum.Y || minimum.Z >= maximum.Z)
            throw new IOException("3D LUT 采样数或输入范围无效。");
        return new Cube(size, minimum, maximum, colors.ToArray());
    }

    public byte[] Canonical(bool renderer = false)
    {
        using var stream = new MemoryStream();
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\n" })
        {
            writer.WriteLine(FormattableString.Invariant($"LUT_3D_SIZE {Size}"));
            writer.WriteLine("DOMAIN_MIN " + Format(Minimum));
            writer.WriteLine("DOMAIN_MAX " + Format(Maximum));
            foreach (var rgb in Colors)
            {
                var value = Format(rgb);
                writer.WriteLine(renderer ? value.Replace('E', 'e') : value);
            }
        }
        return stream.ToArray();
    }

    public static string Hash(byte[] canonical) => Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();
    private static string Format(Vector3 rgb) => FormattableString.Invariant($"{rgb.X:R} {rgb.Y:R} {rgb.Z:R}");

    private static Vector3 Vector(ReadOnlySpan<string> items) => items.Length != 3
        ? throw new IOException("无效的 LUT RGB 行。")
        : new Vector3(Number(items[0]), Number(items[1]), Number(items[2]));

    private static float Number(string item)
    {
        if (!float.TryParse(item, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ||
            !float.IsFinite(number) || Math.Abs(number) > 10000) throw new IOException("无效的 LUT 数值。");
        return number == 0 ? 0 : number;
    }
}
