using System.IO;
using System.Text.Json;
using System.Xml;

namespace FilmPick;

enum VideoColor
{
    Unknown, Normal, DLog, DLogM, SLog2, SLog3, SCinetone, Rec709, Hlg, Pq, Tagged709, TaggedHlg, TaggedPq
}

static class VideoColors
{
    public static string Display(VideoColor? color) => color switch
    {
        VideoColor.Normal => "普通色彩",
        VideoColor.DLog => "D-Log",
        VideoColor.DLogM => "D-Log M",
        VideoColor.SLog2 => "S-Log2",
        VideoColor.SLog3 => "S-Log3",
        VideoColor.SCinetone => "S-Cinetone",
        VideoColor.Rec709 => "Rec.709",
        VideoColor.Hlg => "HLG",
        VideoColor.Pq => "PQ",
        VideoColor.Tagged709 => "Rec.709（标记）",
        VideoColor.TaggedHlg => "HLG（标记）",
        VideoColor.TaggedPq => "PQ（标记）",
        _ => "色彩未知"
    };

    public static VideoColor? FromDji(string value) => Normalize(value) switch
    {
        "dlog" => VideoColor.DLog,
        "dlogm" => VideoColor.DLogM,
        "default" or "normal" => VideoColor.Normal,
        "hlg" => VideoColor.Hlg,
        _ => null
    };

    public static VideoColor? FromTrack(JsonElement tags, string prefix)
    {
        var transfer = Code("TransferCharacteristics");
        return transfer switch
        {
            1 when Code("ColorPrimaries") == 1 => VideoColor.Tagged709,
            16 => VideoColor.TaggedPq,
            18 => VideoColor.TaggedHlg,
            _ => null
        };

        int? Code(string name) => tags.TryGetProperty(prefix + name, out var tag) &&
            tag.ValueKind == JsonValueKind.Number && tag.TryGetInt32(out var number) ? number : null;
    }

    public static VideoColor? MergeModes(VideoColor? first, VideoColor? second) =>
        first == null || first == second ? second : second == null ? first : VideoColor.Unknown;

    public static (bool Recognized, VideoColor? Mode) ReadSony(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var xml = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 1024 * 1024
            });
            if (xml.MoveToContent() != XmlNodeType.Element || xml.LocalName != "NonRealTimeMeta" ||
                !xml.NamespaceURI.StartsWith("urn:schemas-professionalDisc:nonRealTimeMeta:ver.", StringComparison.Ordinal))
                return default;
            var ns = xml.NamespaceURI;
            var groupDepth = -1;
            VideoColor? mode = null;
            while (xml.Read())
            {
                if (xml.NodeType == XmlNodeType.EndElement && xml.Depth == groupDepth) groupDepth = -1;
                if (xml.NodeType != XmlNodeType.Element || xml.NamespaceURI != ns) continue;
                if (xml.LocalName == "Group" && !xml.IsEmptyElement && xml.GetAttribute("name") == "CameraUnitMetadataSet")
                    groupDepth = xml.Depth;
                if (groupDepth < 0 || xml.Depth != groupDepth + 1 || xml.LocalName != "Item" ||
                    xml.GetAttribute("name") != "CaptureGammaEquation") continue;
                mode = MergeModes(mode, Normalize(xml.GetAttribute("value") ?? "") switch
                {
                    "slog2" => VideoColor.SLog2,
                    "slog3" or "slog3cine" => VideoColor.SLog3,
                    "scinetone" => VideoColor.SCinetone,
                    "rec709" => VideoColor.Rec709,
                    "hlg" or "hlg1" or "hlg2" or "hlg3" => VideoColor.Hlg,
                    "pq" => VideoColor.Pq,
                    _ => null
                });
            }
            return (true, mode);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or XmlException) { return default; }
    }

    private static string Normalize(string value) => string.Concat(value.Where(char.IsLetterOrDigit)).ToLowerInvariant();
}
