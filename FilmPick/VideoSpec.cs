using System.Buffers.Binary;
using System.Text.Json;

namespace FilmPick;

record VideoSpec(string? Codec, string? Chroma, VideoColor? ContainerColor = null, int? BitDepth = null)
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
        var (sampling, depth) = codec switch
        {
            "HEVC" => Hevc(Blob(tags, prefix + "Unknown_hvcC")),
            "H.264" => Avc(Blob(tags, prefix + "AVCConfiguration")),
            "AV1" => ((int?)null, Av1Depth(Blob(tags, prefix + "Unknown_av1C"))),
            "VP9" => ((int?)null, Vp9Depth(Blob(tags, prefix + "Unknown_vpcC"))),
            _ => default
        };
        return new(codec, sampling switch { 0 => "单色", 1 => "4:2:0", 2 => "4:2:2", 3 => "4:4:4", _ => null },
            VideoColors.FromTrack(tags, prefix), depth);
    }

    private static byte[] Blob(JsonElement tags, string name)
    {
        if (!tags.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String ||
            value.GetString() is not { Length: < 131072 } blob || !blob.StartsWith("base64:", StringComparison.Ordinal)) return [];
        try { return Convert.FromBase64String(blob[7..]); }
        catch (FormatException) { return []; }
    }

    private static int? Av1Depth(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4 || data[0] != 0x81 || (data[1] >> 5) > 2) return null;
        return (data[2] & 0x60) switch
        {
            0 => 8,
            0x40 => 10,
            0x60 when (data[1] >> 5) == 2 => 12,
            _ => null
        };
    }

    private static int? Vp9Depth(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12 || BinaryPrimitives.ReadUInt32BigEndian(data) != 0x01000000 ||
            BinaryPrimitives.ReadUInt16BigEndian(data[10..]) != data.Length - 12) return null;
        var depth = data[6] >> 4;
        return depth is 8 or 10 or 12 ? depth : null;
    }

    private static (int? Sampling, int? BitDepth) Hevc(ReadOnlySpan<byte> data)
    {
        if (data.Length < 23 || data[0] != 1 || (data[16] & 0xfc) != 0xfc) return default;
        var end = 23;
        for (var group = 0; group < data[22]; group++)
        {
            if (data.Length - end < 3) return default;
            var count = BinaryPrimitives.ReadUInt16BigEndian(data[(end + 1)..]);
            end += 3;
            for (var i = 0; i < count; i++)
            {
                if (data.Length - end < 2) return default;
                var size = BinaryPrimitives.ReadUInt16BigEndian(data[end..]);
                end += 2 + size;
                if (size < 2 || end > data.Length) return default;
            }
        }
        var sampling = data[16] & 3;
        if ((data[17] & 0xf8) != 0xf8 || (data[18] & 0xf8) != 0xf8) return (sampling, null);
        var luma = data[17] & 7;
        var chroma = data[18] & 7;
        return (sampling, sampling == 0 || luma == chroma ? 8 + luma : null);
    }

    private static (int? Sampling, int? BitDepth) Avc(ReadOnlySpan<byte> data)
    {
        if (data.Length < 7 || data[0] != 1) return default;
        (int? Sampling, int? BitDepth) result = default;
        var offset = 6;
        for (var i = 0; i < (data[5] & 31); i++)
        {
            if (data.Length - offset < 2) return default;
            var size = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
            offset += 2;
            if (size > data.Length - offset) return default;
            var (sampling, depth) = Sps(data.Slice(offset, size));
            if (sampling == null || (i > 0 && result.Sampling != sampling)) return default;
            result = (sampling, i == 0 || result.BitDepth == depth ? depth : null);
            offset += size;
        }
        return result;
    }

    private static (int? Sampling, int? BitDepth) Sps(ReadOnlySpan<byte> nal)
    {
        if (nal.Length < 5 || (nal[0] & 31) != 7) return default;
        var rbsp = new byte[nal.Length - 1];
        var count = 0;
        var zeros = 0;
        foreach (var value in nal[1..])
        {
            if (zeros >= 2 && value == 3) { zeros = 0; continue; }
            zeros = value == 0 ? zeros + 1 : 0;
            rbsp[count++] = value;
        }
        var data = rbsp.AsSpan(0, count);
        var bit = 24;
        if (Ue(data, ref bit) is not (>= 0 and <= 31)) return default;
        if (rbsp[0] is 66 or 77 or 88) return (1, 8);
        if (rbsp[0] is not (44 or 83 or 86 or 100 or 110 or 118 or 122 or 128 or 134 or 135 or 138 or 139 or 144 or 244))
            return default;
        var sampling = Ue(data, ref bit);
        if (sampling is < 0 or > 3) return default;
        if (sampling == 3) bit++; // separate_colour_plane_flag
        var luma = Ue(data, ref bit);
        var chroma = Ue(data, ref bit);
        int? depth = luma is >= 0 and <= 6 && chroma is >= 0 and <= 6 && (sampling == 0 || luma == chroma) ? 8 + luma : null;
        return (sampling, depth);
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
