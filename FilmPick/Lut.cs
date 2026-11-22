using System.IO;
using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FilmPick;

sealed class Lut(string file, Cube cube)
{
    public string File { get; } = file;
    public Cube Data { get; } = cube;
    private readonly int size = cube.Size;
    private readonly Vector3 minimum = cube.Minimum, maximum = cube.Maximum;
    private readonly Vector3[] colors = cube.Colors;

    public static Lut Read(string path) => new(path, Cube.Read(path));

    public string SavePreview(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".cube");
        var render = Data;
        if (minimum != Vector3.Zero || maximum != Vector3.One)
        {
            const int samples = 65;
            var values = new Vector3[samples * samples * samples];
            for (var blue = 0; blue < samples; blue++)
            for (var green = 0; green < samples; green++)
            for (var red = 0; red < samples; red++)
            {
                var position = new Vector3(red, green, blue) / (samples - 1);
                position = Vector3.Clamp((position - minimum) / (maximum - minimum), Vector3.Zero, Vector3.One) * (size - 1);
                values[(blue * samples + green) * samples + red] = Interpolate(position);
            }
            render = new Cube(samples, Vector3.Zero, Vector3.One, values);
        }
        System.IO.File.WriteAllBytes(path, render.Canonical(renderer: true));
        return path;
    }

    private Vector3 Interpolate(Vector3 position)
    {
        var red = Math.Min(size - 2, (int)position.X);
        var green = Math.Min(size - 2, (int)position.Y);
        var blue = Math.Min(size - 2, (int)position.Z);
        var start = (blue * size + green) * size + red;
        var next = start + size * size;
        var redMix = position.X - red;
        var greenMix = position.Y - green;
        return Vector3.Lerp(Vector3.Lerp(Vector3.Lerp(colors[start], colors[start + 1], redMix),
                Vector3.Lerp(colors[start + size], colors[start + size + 1], redMix), greenMix),
            Vector3.Lerp(Vector3.Lerp(colors[next], colors[next + 1], redMix),
                Vector3.Lerp(colors[next + size], colors[next + size + 1], redMix), greenMix), position.Z - blue);
    }

    public BitmapSource Transform(BitmapSource image, CancellationToken cancel, int parallelism = 4)
    {
        var source = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        source.Freeze();
        var stride = checked(source.PixelWidth * 4);
        var bytes = new byte[checked(source.PixelHeight * stride)];
        source.CopyPixels(bytes, stride, 0);
        var axisRed = Axis(minimum.X, maximum.X);
        var axisGreen = Axis(minimum.Y, maximum.Y);
        var axisBlue = Axis(minimum.Z, maximum.Z);
        var plane = size * size;
        Parallel.For(0, source.PixelHeight, new ParallelOptions
        {
            CancellationToken = cancel, MaxDegreeOfParallelism = Math.Min(parallelism, Environment.ProcessorCount)
        }, row =>
        {
            var end = (row + 1) * stride;
            for (var index = row * stride; index < end; index += 4)
            {
                var (blue, blueMix) = axisBlue[bytes[index]];
                var (green, greenMix) = axisGreen[bytes[index + 1]];
                var (red, redMix) = axisRed[bytes[index + 2]];
                var start = blue * plane + green * size + red;
                var lower = Vector3.Lerp(Vector3.Lerp(colors[start], colors[start + 1], redMix),
                    Vector3.Lerp(colors[start + size], colors[start + size + 1], redMix), greenMix);
                start += plane;
                var upper = Vector3.Lerp(Vector3.Lerp(colors[start], colors[start + 1], redMix),
                    Vector3.Lerp(colors[start + size], colors[start + size + 1], redMix), greenMix);
                var rgb = Vector3.Clamp(Vector3.Lerp(lower, upper, blueMix), Vector3.Zero, Vector3.One) * 255;
                bytes[index] = (byte)MathF.Round(rgb.Z);
                bytes[index + 1] = (byte)MathF.Round(rgb.Y);
                bytes[index + 2] = (byte)MathF.Round(rgb.X);
            }
        });
        var output = BitmapSource.Create(source.PixelWidth, source.PixelHeight, source.DpiX, source.DpiY, PixelFormats.Bgra32, null, bytes, stride);
        output.Freeze();
        return output;
    }

    private (int, float)[] Axis(float min, float max) => Enumerable.Range(0, 256).Select(channel =>
    {
        var scaled = Math.Clamp(((channel / 255f) - min) / (max - min), 0, 1) * (size - 1);
        var offset = Math.Min(size - 2, (int)scaled);
        return (offset, scaled - offset);
    }).ToArray();

}
