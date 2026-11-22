using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace FilmPick;

record Flight(double? RelativeAltitude, double? AbsoluteAltitude, double? Speed, double? Latitude, double? Longitude)
{
    public bool HasValues => RelativeAltitude != null || AbsoluteAltitude != null || Speed != null ||
        Latitude != null || Longitude != null;
}

record CaptureCue(double From, double To, Exposure Values, CaptureDate? Clock, Flight Flight);

sealed class CaptureSrt
{
    private static readonly Regex Timing = new(
        @"^\s*(?<a>\d{2,}:\d{2}:\d{2}[,.]\d{3})\s+-->\s+(?<b>\d{2,}:\d{2}:\d{2}[,.]\d{3})(?:\s|$)",
        RegexOptions.CultureInvariant);
    private static readonly Regex Field = new(
        @"\b(?<key>iso|shutter|fnum|focal_len|rel_alt|abs[ _]+alt|latitude|longitude|longtitude|drone_speed[xy]|h_speed|hspeed|speed)\s*:\s*(?<value>[+-]?(?:[0-9]+(?:\.[0-9]+)?|\.[0-9]+)(?:\s*/\s*[0-9]+(?:\.[0-9]+)?)?)(?:\s*(?<unit>km/h|m/s)\b)?",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex LegacyGps = new(
        @"\bGPS\s*\(\s*(?<lon>[+-]?\d+(?:\.\d+)?)\s*,\s*(?<lat>[+-]?\d+(?:\.\d+)?)(?:\s*,\s*[+-]?\d+(?:\.\d+)?)?\s*\)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex LegacyAltitude = new(@"\bH\s*[:=]?\s*(?<height>[+-]?\d+(?:\.\d+)?)\s*m\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex Clock = new(
        @"\b\d{4}[:-]\d{1,2}[:-]\d{1,2}[ T]\d{2}:\d{2}:\d{2}(?:[.,]\d+)?(?:Z|[+-]\d{2}:?\d{2})?",
        RegexOptions.CultureInvariant);
    private static readonly Regex FrameMarker = new(@"\b(?:FrameCnt|DiffTime)\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ColorMode = new(@"\[\s*color_md\s*:\s*(?<value>[a-z0-9_ -]{1,32})\s*\]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private readonly CaptureCue[] cues;
    private readonly double[] coverage;
    public CaptureDate? Start { get; }
    public bool Recognized { get; }
    public VideoColor? Color { get; }

    private CaptureSrt(List<CaptureCue> cues, CaptureDate? start, bool recognized, VideoColor? color)
    {
        this.cues = cues.OrderBy(cue => cue.From).ToArray();
        coverage = new double[this.cues.Length];
        for (var i = 0; i < coverage.Length; i++)
            coverage[i] = Math.Max(i == 0 ? 0 : coverage[i - 1], this.cues[i].To);
        Start = start;
        Recognized = recognized;
        Color = color;
    }

    public Exposure? At(double seconds) => SampleAt(seconds)?.Values;

    public CaptureCue? SampleAt(double seconds)
    {
        var low = 0;
        var high = cues.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (cues[middle].From <= seconds) low = middle + 1;
            else high = middle;
        }
        for (var i = low - 1; i >= 0 && seconds < coverage[i]; i--)
            if (seconds < cues[i].To) return cues[i];
        return null;
    }

    public static CaptureSrt? Open(string file, bool headerOnly, CancellationToken cancel)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (headerOnly)
            {
                var bytes = new byte[(int)Math.Min(stream.Length, 64 * 1024)];
                stream.ReadExactly(bytes);
                using var sample = new MemoryStream(bytes);
                using var preview = new StreamReader(sample, Encoding.UTF8, true);
                return Parse(preview, true, cancel);
            }
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            return Parse(reader, false, cancel);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public static CaptureSrt Parse(TextReader reader, bool headerOnly, CancellationToken cancel = default)
    {
        var cues = new List<CaptureCue>();
        var text = new StringBuilder();
        CaptureDate? start = null;
        var first = double.MaxValue;
        var recognized = false;
        VideoColor? color = null;
        double from = 0, to = 0;

        void FinishCue()
        {
            if (to <= from || text.Length == 0) return;
            var body = text.ToString();
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var units = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in Field.Matches(body))
            {
                var key = match.Groups["key"].Value;
                if (key.StartsWith("abs", StringComparison.OrdinalIgnoreCase)) key = "abs_alt";
                if (fields.TryAdd(key, match.Groups["value"].Value)) units[key] = match.Groups["unit"].Value;
            }
            var gps = LegacyGps.Match(body);
            var height = LegacyAltitude.Match(body);
            var date = CaptureDate.Parse(Clock.Match(body).Value);
            var cueColor = VideoColors.FromDji(ColorMode.Match(body).Groups["value"].Value);
            if (date == null && !FrameMarker.IsMatch(body) && !(gps.Success && height.Success) &&
                cueColor == null &&
                !(fields.ContainsKey("iso") && (fields.ContainsKey("shutter") || fields.ContainsKey("fnum"))) &&
                !(fields.ContainsKey("latitude") && (fields.ContainsKey("longitude") || fields.ContainsKey("longtitude")) &&
                  (fields.ContainsKey("rel_alt") || fields.ContainsKey("abs_alt")))) return;
            var latitude = Number(fields.GetValueOrDefault("latitude") ?? gps.Groups["lat"].Value, -90, 90);
            var longitude = Number(fields.GetValueOrDefault("longitude") ?? fields.GetValueOrDefault("longtitude") ??
                gps.Groups["lon"].Value, -180, 180);
            double? speed = Velocity("h_speed") ?? Velocity("hspeed") ?? Velocity("speed");
            if (speed == null && Velocity("drone_speedx", signed: true) is { } x && Velocity("drone_speedy", signed: true) is { } y)
                speed = Math.Sqrt(x * x + y * y);
            var flight = new Flight(Number(fields.GetValueOrDefault("rel_alt") ?? height.Groups["height"].Value, -100000, 100000),
                Number(fields.GetValueOrDefault("abs_alt"), -100000, 100000), speed, latitude, longitude);
            var values = new Exposure(
                Exposure.Positive(fields.GetValueOrDefault("shutter"), "{0:0.######} s", true),
                Exposure.Positive(fields.GetValueOrDefault("fnum"), "f/{0:0.##}"),
                Exposure.Positive(fields.GetValueOrDefault("iso"), "ISO {0:0}"),
                EquivalentMm: Exposure.PositiveNumber(fields.GetValueOrDefault("focal_len")));
            if (!values.HasValues && !flight.HasValues && cueColor == null && (date == null || !FrameMarker.IsMatch(body))) return;
            recognized = true;
            color = VideoColors.MergeModes(color, cueColor);
            if (date != null && from < first && date.Clock.Ticks >= TimeSpan.FromSeconds(from).Ticks)
            {
                first = from;
                start = date with { Clock = date.Clock.AddSeconds(-from), Source = "DJI SRT" };
            }
            if (!headerOnly) cues.Add(new(from, to, values, date, flight));

            double? Velocity(string key, bool signed = false)
            {
                var speed = Number(fields.GetValueOrDefault(key), signed ? -500 : 0, 500);
                return string.Equals(units.GetValueOrDefault(key), "km/h", StringComparison.OrdinalIgnoreCase) ? speed / 3.6 : speed;
            }
        }

        while (reader.ReadLine() is { } line)
        {
            cancel.ThrowIfCancellationRequested();
            if (line.Length > 8192) continue;
            var match = Timing.Match(line);
            if (match.Success || string.IsNullOrWhiteSpace(line))
            {
                FinishCue();
                text.Clear();
                from = match.Success ? Seconds(match.Groups["a"].Value) : 0;
                to = match.Success ? Seconds(match.Groups["b"].Value) : 0;
                if (!double.IsFinite(from) || !double.IsFinite(to)) from = to = 0;
            }
            else if (to > from && text.Length < 8192) text.Append(' ').Append(line);
        }
        FinishCue();
        return new(cues, start, recognized, color);
    }

    private static double? Number(string? value, double min, double max) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) &&
        double.IsFinite(result) && result >= min && result <= max ? result : null;

    private static double Seconds(string value)
    {
        var parts = value.Replace(',', '.').Split(':');
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) ||
            !double.TryParse(parts[2], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds) ||
            hours > 9999 || minutes >= 60 || seconds >= 60) return double.NaN;
        return hours * 3600.0 + minutes * 60 + seconds;
    }
}
