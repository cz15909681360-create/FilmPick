using System.Buffers.Binary;
using System.Text.Json;

namespace FilmPick;

record VideoSpec(string? Codec, string? Chroma, VideoColor? ContainerColor = null)
{
    public static VideoSpec? Parse(JsonElement tags)
    {
        var tracks = tags.EnumerateObject().Where(p => p.Name.StartsWith("Track", StringComparison.Ordinal) &&
            p.Name.EndsWith(":CompressorID", StringComparison.Ordinal) && p.Value.ValueKind == JsonValueKind.String).ToArray();
        if (tracks.Length != 1 || tracks[0].Value.GetString() is not { Length: 4 } id) return null;
        var prefix = tracks[0].Name[..^"CompressorID".Length];
        var codec = id switch
        {
            "avc1" or "avc2" or "avc3" or "avc4" => "H.264",
            "hvc1" or "hev1" or "dvh1" or "dvhe" => "HEVC",
            "apco" or "apcs" or "apcn" or "apch" or "ap4h" or "ap4x" => "ProRes",
            "av01" => "AV1",
            "vp09" => "VP9",
            _ => id
        };
        int? sampling = codec switch
        {
            "HEVC" => Hevc(Blob(tags, prefix + "Unknown_hvcC")),
            "H.264" => Avc(Blob(tags, prefix + "AVCConfiguration")),
            _ => null
        };
        return new(codec, sampling switch { 0 => "单色", 1 => "4:2:0", 2 => "4:2:2", 3 => "4:4:4", _ => null },
            VideoColors.FromTrack(tags, prefix));
    }

    private static byte[] Blob(JsonElement tags, string name)
    {
        if (!tags.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String ||
            value.GetString() is not { Length: < 131072 } blob || !blob.StartsWith("base64:", StringComparison.Ordinal)) return [];
        try { return Convert.FromBase64String(blob[7..]); }
        catch (FormatException) { return []; }
    }

    private static int? Hevc(ReadOnlySpan<byte> data)
    {
        if (data.Length < 23 || data[0] != 1 || (data[16] & 0xfc) != 0xfc) return null;
        var end = 23;
        for (var group = 0; group < data[22]; group++)
        {
            if (data.Length - end < 3) return null;
            var count = BinaryPrimitives.ReadUInt16BigEndian(data[(end + 1)..]);
            end += 3;
            for (var i = 0; i < count; i++)
            {
                if (data.Length - end < 2) return null;
                var size = BinaryPrimitives.ReadUInt16BigEndian(data[end..]);
                end += 2 + size;
                if (size < 2 || end > data.Length) return null;
            }
        }
        return data[16] & 3;
    }

    private static int? Avc(ReadOnlySpan<byte> data)
    {
        if (data.Length < 7 || data[0] != 1) return null;
        int? result = null;
        var offset = 6;
        for (var i = 0; i < (data[5] & 31); i++)
        {
            if (data.Length - offset < 2) return null;
            var size = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
            offset += 2;
            if (size > data.Length - offset || Sps(data.Slice(offset, size)) is not int sampling ||
                (result != null && result != sampling)) return null;
            result = sampling;
            offset += size;
        }
        return result;
    }

    private static int? Sps(ReadOnlySpan<byte> nal)
    {
        if (nal.Length < 5 || (nal[0] & 31) != 7) return null;
        var rbsp = new byte[nal.Length - 1];
        var count = 0;
        var zeros = 0;
        foreach (var value in nal[1..])
        {
            if (zeros >= 2 && value == 3) { zeros = 0; continue; }
            zeros = value == 0 ? zeros + 1 : 0;
            rbsp[count++] = value;
        }
        var bit = 24;
        if (Ue(rbsp.AsSpan(0, count), ref bit) is not (>= 0 and <= 31)) return null;
        return rbsp[0] switch
        {
            66 or 77 or 88 => 1,
            44 or 83 or 86 or 100 or 110 or 118 or 122 or 128 or 134 or 135 or 138 or 139 or 144 or 244
                => Ue(rbsp.AsSpan(0, count), ref bit) is >= 0 and <= 3 and var c ? c : null,
            _ => null
        };
    }

    private static int Ue(ReadOnlySpan<byte> data, ref int bit)
    {
        var zeros = 0;
        while (bit < data.Length * 8 && ReadBit(data, bit) == 0)
        {
            bit++;
            if (++zeros > 15) return -1;
        }
        if (bit + zeros >= data.Length * 8) return -1;
        bit++;
        var value = 1;
        for (var i = 0; i < zeros; i++) value = (value << 1) | ReadBit(data, bit++);
        return value - 1;
    }

    private static int ReadBit(ReadOnlySpan<byte> data, int bit) => (data[bit / 8] >> (7 - bit % 8)) & 1;
}
