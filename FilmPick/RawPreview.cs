using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FilmPick;

static class RawPreview
{
    private const string Dll = "libraw_r-25.dll";
    public static PhotoFrame? ReadEmbedded(string path, PreviewBox box, CancellationToken cancel)
    {
        using var raw = Open(path, cancel);
        PhotoFrame? best = null;
        for (var index = -1; index < 8; index++)
        {
            cancel.ThrowIfCancellationRequested();
            var unpack = index == -1 ? libraw_unpack_thumb(raw.Handle) : libraw_unpack_thumb_ex(raw.Handle, index);
            if (unpack == -9) break;
            if (unpack != 0) continue;
            var image = libraw_dcraw_make_mem_thumb(raw.Handle, out _);
            if (image == IntPtr.Zero) continue;
            try
            {
                var candidate = ReadImage(image, box, PhotoStage.Embedded, cancel);
                if (!box.NeedsRaw(candidate.Native)) return candidate;
                if (best == null || (long)candidate.Native.Width * candidate.Native.Height > (long)best.Native.Width * best.Native.Height)
                    best = candidate;
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException or InvalidOperationException) { }
            finally { libraw_dcraw_clear_mem(image); }
        }
        return best == null ? null : best with { Exhausted = true };
    }

    public static PhotoFrame Develop(string path, PreviewBox box, CancellationToken cancel)
    {
        using var raw = Open(path, cancel);
        var context = raw.Handle;
        var unpacker = Marshal.PtrToStringAnsi(libraw_unpack_function_name(context));
        if (unpacker?.Contains("nikon_he_", StringComparison.OrdinalIgnoreCase) == true)
            throw new IOException("当前解码器不支持 Nikon HE／HE★ 原始数据。");
        if ((long)libraw_get_raw_width(context) * libraw_get_raw_height(context) > 110_000_000)
            throw new IOException("RAW 像素数超出预览解码限制。");
        libraw_set_demosaic(context, 0);
        libraw_set_output_color(context, 1);
        libraw_set_output_bps(context, 8);
        libraw_set_gamma(context, 0, 1 / 2.4f);
        libraw_set_gamma(context, 1, 12.92f);
        var white = Enumerable.Range(0, 3).Select(channel => libraw_get_cam_mul(context, channel)).ToArray();
        if (white.All(value => float.IsFinite(value) && value > 0))
        {
            for (var channel = 0; channel < 3; channel++) libraw_set_user_mul(context, channel, white[channel]);
            var green = libraw_get_cam_mul(context, 3);
            libraw_set_user_mul(context, 3, green > 0 && float.IsFinite(green) ? green : white[1]);
        }
        Check(libraw_unpack(context), cancel);
        Check(libraw_dcraw_process(context), cancel);
        var image = libraw_dcraw_make_mem_image(context, out var error);
        if (image == IntPtr.Zero) { Check(error, cancel); throw new IOException("RAW 解码未输出画面。"); }
        try { return ReadImage(image, box, PhotoStage.Developed, cancel); }
        finally { libraw_dcraw_clear_mem(image); }
    }

    private static RawContext Open(string path, CancellationToken cancel)
    {
        IntPtr context = IntPtr.Zero;
        try
        {
            cancel.ThrowIfCancellationRequested();
            context = libraw_init(0);
            if (context == IntPtr.Zero) throw new IOException("无法初始化 RAW 解码器。");
            Check(libraw_open_wfile(context, path), cancel);
            var raw = new RawContext(context, cancel);
            context = IntPtr.Zero;
            return raw;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            throw new IOException("内置 RAW 解码器不可用，请保留便携包内的全部 DLL。", ex);
        }
        finally { if (context != IntPtr.Zero) libraw_close(context); }
    }

    private static PhotoFrame ReadImage(IntPtr image, PreviewBox box, PhotoStage stage, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        var kind = Marshal.ReadInt32(image);
        var height = (ushort)Marshal.ReadInt16(image, 4);
        var width = (ushort)Marshal.ReadInt16(image, 6);
        var channels = (ushort)Marshal.ReadInt16(image, 8);
        var depth = (ushort)Marshal.ReadInt16(image, 10);
        var length = Marshal.ReadInt32(image, 12);
        if (length <= 0 || length > 440_000_000) throw new IOException("无效的 RAW 预览尺寸。");
        if (kind == 1)
        {
            var bytes = new byte[length];
            Marshal.Copy(image + 16, bytes, 0, length);
            using var stream = new MemoryStream(bytes, false);
            return Photos.DecodeImage(stream, box, true, stage);
        }
        if (kind != 2 || depth != 8 || channels is not (1 or 3) || width == 0 || height == 0 || (long)width * height * channels != length)
            throw new IOException("不支持此 RAW 内嵌预览的像素格式。");
        var native = new PixelSize(width, height);
        var (outputWidth, outputHeight) = box.Fit(native, allowUpscale: false);
        var rgb = new byte[outputWidth * outputHeight * channels];
        var row = new byte[width * channels];
        for (var y = 0; y < outputHeight; y++)
        {
            cancel.ThrowIfCancellationRequested();
            Marshal.Copy(image + 16 + y * height / outputHeight * row.Length, row, 0, row.Length);
            for (var x = 0; x < outputWidth; x++)
                row.AsSpan(x * width / outputWidth * channels, channels).CopyTo(rgb.AsSpan((y * outputWidth + x) * channels, channels));
        }
        var result = BitmapSource.Create(outputWidth, outputHeight, 96, 96, channels == 3 ? PixelFormats.Rgb24 : PixelFormats.Gray8, null, rgb, outputWidth * channels);
        result.Freeze();
        return new(result, native, stage);
    }

    private static void Check(int error, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        if (error != 0) throw new IOException("RAW 无法读取或不支持此机型／压缩：" + Marshal.PtrToStringAnsi(libraw_strerror(error)));
    }

    private sealed class RawContext : IDisposable
    {
        public IntPtr Handle { get; }
        private readonly Progress progress;

        public RawContext(IntPtr handle, CancellationToken cancel)
        {
            Handle = handle;
            progress = (_, _, _, _) => cancel.IsCancellationRequested ? 1 : 0;
            libraw_set_progress_handler(handle, progress, IntPtr.Zero);
        }

        public void Dispose()
        {
            libraw_close(Handle);
            GC.KeepAlive(progress);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Progress(IntPtr data, int stage, int iteration, int expected);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void libraw_set_progress_handler(IntPtr context, Progress progress, IntPtr data);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr libraw_init(uint flags);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] private static extern int libraw_open_wfile(IntPtr context, [MarshalAs(UnmanagedType.LPWStr)] string path);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr libraw_unpack_function_name(IntPtr context);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr libraw_strerror(int error);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int libraw_get_raw_width(IntPtr context);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int libraw_get_raw_height(IntPtr context);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern float libraw_get_cam_mul(IntPtr context, int channel);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern void libraw_set_user_mul(IntPtr context, int channel, float value);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern void libraw_set_gamma(IntPtr context, int index, float value);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern void libraw_set_demosaic(IntPtr context, int value);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern void libraw_set_output_color(IntPtr context, int value);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern void libraw_set_output_bps(IntPtr context, int value);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int libraw_unpack_thumb(IntPtr context);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int libraw_unpack_thumb_ex(IntPtr context, int index);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int libraw_unpack(IntPtr context);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int libraw_dcraw_process(IntPtr context);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr libraw_dcraw_make_mem_thumb(IntPtr context, out int error);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr libraw_dcraw_make_mem_image(IntPtr context, out int error);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern void libraw_dcraw_clear_mem(IntPtr image);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern void libraw_close(IntPtr context);
}
