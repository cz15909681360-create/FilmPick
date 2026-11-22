namespace FilmPick;

readonly record struct PixelSize(int Width, int Height)
{
    public int Edge => Math.Max(Width, Height);
}

readonly record struct PreviewBox(int Width, int Height)
{
    public const int MaxEdge = 4096;
    public const long CacheLimit = 1024L * 1024 * 1024;
    public const int LookAhead = 10;

    public static PreviewBox FromLayout(double width, double height, double dpiX, double dpiY) =>
        new(Math.Max(1, (int)Math.Ceiling(width * dpiX)), Math.Max(1, (int)Math.Ceiling(height * dpiY)));

    public PixelSize Fit(PixelSize source, bool allowUpscale = true)
    {
        var scale = Math.Min(Math.Min((double)Width / source.Width, (double)Height / source.Height), (double)MaxEdge / source.Edge);
        if (!allowUpscale) scale = Math.Min(scale, 1);
        return new(Math.Max(1, (int)Math.Ceiling(source.Width * scale - 1e-9)),
            Math.Max(1, (int)Math.Ceiling(source.Height * scale - 1e-9)));
    }

    public bool NeedsRaw(PixelSize embedded) => Fit(embedded).Edge > embedded.Edge;

    public static IEnumerable<int> Neighbors(int index, int count, int direction)
    {
        var step = direction < 0 ? -1 : 1;
        for (var offset = 1; offset <= LookAhead; offset++)
        {
            var next = index + step * offset;
            if (next < 0 || next >= count) yield break;
            yield return next;
        }
    }
}
