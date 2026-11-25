using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FilmPick;

sealed record VideoThumbnailRequest(string Original, string? Proxy);

sealed class ThumbnailVideoClient
{
    internal const int Edge = 320;
    private Process? worker;

    public async Task<BitmapSource?> Read(VideoThumbnailRequest request, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(TimeSpan.FromSeconds(6));
        var token = deadline.Token;
        try
        {
            var process = worker ??= Start();
            var stdin = process.StandardInput.BaseStream;
            var stdout = process.StandardOutput.BaseStream;
            await stdin.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request) + "\n"), token);
            await stdin.FlushAsync(token);
            var header = new byte[8];
            await stdout.ReadExactlyAsync(header, token);
            var width = BinaryPrimitives.ReadInt32LittleEndian(header);
            var height = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
            if (width == 0 && height == 0) return null;
            if (width is < 1 or > Edge || height is < 1 or > Edge) throw new IOException("无效的视频缩略图尺寸。");
            var stride = width * 4;
            var pixels = new byte[stride * height];
            await stdout.ReadExactlyAsync(pixels, token);
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, stride);
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Stop();
            cancel.ThrowIfCancellationRequested();
            return null;
        }
    }

    private static Process Start()
    {
        var executable = Environment.ProcessPath;
        if (executable == null || string.Equals(Path.GetFileName(executable), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
            executable = Path.Combine(AppContext.BaseDirectory, "FilmPick.exe");
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true
        };
        info.ArgumentList.Add("--thumbnail-worker");
        info.StandardInputEncoding = new UTF8Encoding(false);
        var process = Process.Start(info) ?? throw new IOException("视频缩略图后台未启动。");
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        return process;
    }

    public void Stop()
    {
        var process = worker;
        worker = null;
        if (process == null) return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
                process.WaitForExit(1000);
            }
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        finally { process.Dispose(); }
    }
}

sealed partial class VideoPlayer
{
    internal static int RunThumbnailWorker()
    {
        try
        {
            using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            using var output = new BinaryWriter(Console.OpenStandardOutput());
            while (input.ReadLine() is { } line)
            {
                var request = JsonSerializer.Deserialize<VideoThumbnailRequest>(line);
                var frame = request == null ? null : ReadVideoThumbnail(request);
                output.Write(frame?.Width ?? 0);
                output.Write(frame?.Height ?? 0);
                if (frame != null) output.Write(frame.Pixels);
                output.Flush();
            }
            return 0;
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidOperationException) { return 1; }
    }

    private static ThumbnailFrame? ReadVideoThumbnail(VideoThumbnailRequest request)
    {
        var handle = mpv_create();
        if (handle == IntPtr.Zero) return null;
        try
        {
            var edge = ThumbnailVideoClient.Edge;
            foreach (var (option, value) in new (string, string)[]
            {
                ("config", "no"), ("load-scripts", "no"), ("input-default-bindings", "no"), ("terminal", "no"),
                ("autoload-files", "no"), ("access-references", "no"), ("vo", "null"), ("screenshot-sw", "yes"),
                ("hwdec", "no"), ("vd-lavc-threads", "2"), ("aid", "no"), ("sid", "no"), ("audio-display", "no"),
                ("demuxer-thread", "no"), ("cache", "no"),
                ("idle", "yes"), ("keep-open", "yes"), ("pause", "yes"),
                ("vf", $"lavfi=[scale=w={edge}:h={edge}:force_original_aspect_ratio=decrease:force_divisible_by=2:reset_sar=1]")
            })
                if (mpv_set_option_string(handle, option, value) < 0) return null;
            if (mpv_initialize(handle) < 0) return null;
            return (request.Proxy == null ? null : ReadThumbnailClip(handle, request.Proxy)) ??
                ReadThumbnailClip(handle, request.Original);
        }
        // mpv can report idle while an asynchronous demux cleanup still holds the file.
        finally { mpv_terminate_destroy(handle); }
    }

    private static ThumbnailFrame? ReadThumbnailClip(IntPtr handle, string path)
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path)) return null;
        return CaptureThumbnailAt(handle, path, "0.15") ?? CaptureThumbnailAt(handle, path, "0");
    }

    private static ThumbnailFrame? CaptureThumbnailAt(IntPtr handle, string path, string start)
    {
        while (Marshal.PtrToStructure<MpvEvent>(mpv_wait_event(handle, 0)).Id != 0) { }
        if (Command(handle, "loadfile", path, "replace", "-1", "start=" + start) < 0) return null;
        var clock = Stopwatch.StartNew();
        var started = false;
        ThumbnailFrame? result = null;
        while (clock.ElapsedMilliseconds < 1200)
        {
            var evt = Marshal.PtrToStructure<MpvEvent>(mpv_wait_event(handle, .015));
            if (evt.Id == 6) started = true;
            if (started && evt.Id == 7) break;
            if (!started || evt.Id != 21) continue;
            result = ScreenshotThumbnail(handle);
            break;
        }
        Command(handle, "stop");
        clock.Restart();
        while (Number(handle, "idle-active", 3) != 1)
        {
            if (clock.ElapsedMilliseconds > 600) throw new IOException("视频缩略图未释放文件。");
            mpv_wait_event(handle, .015);
        }
        return result;
    }

    private static ThumbnailFrame? ScreenshotThumbnail(IntPtr handle)
    {
        var args = new IntPtr[4];
        try
        {
            args[0] = Marshal.StringToCoTaskMemUTF8("screenshot-raw");
            args[1] = Marshal.StringToCoTaskMemUTF8("video");
            args[2] = Marshal.StringToCoTaskMemUTF8("bgr0");
            if (mpv_command_ret(handle, args, out var result) < 0) return null;
            try
            {
                if (result.Format != 8 || result.Data == 0) return null;
                var map = Marshal.PtrToStructure<ThumbnailNodeList>((IntPtr)result.Data);
                var width = 0;
                var height = 0;
                var stride = 0;
                ThumbnailByteArray image = default;
                for (var i = 0; i < map.Count; i++)
                {
                    var key = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(map.Keys, i * IntPtr.Size));
                    var value = Marshal.PtrToStructure<ThumbnailNode>(map.Values + i * Marshal.SizeOf<ThumbnailNode>());
                    if (value.Format == 4 && value.Data is > 0 and <= int.MaxValue)
                    {
                        if (key == "w") width = (int)value.Data;
                        else if (key == "h") height = (int)value.Data;
                        else if (key == "stride") stride = (int)value.Data;
                    }
                    else if (key == "data" && value.Format == 9 && value.Data != 0)
                        image = Marshal.PtrToStructure<ThumbnailByteArray>((IntPtr)value.Data);
                }
                if (width is < 1 or > ThumbnailVideoClient.Edge || height is < 1 or > ThumbnailVideoClient.Edge ||
                    stride < width * 4 || image.Data == IntPtr.Zero || (ulong)image.Size < ((ulong)stride * (uint)(height - 1) + (uint)(width * 4)))
                    return null;
                var packed = new byte[width * height * 4];
                for (var y = 0; y < height; y++)
                    Marshal.Copy(image.Data + y * stride, packed, y * width * 4, width * 4);
                return new(width, height, packed);
            }
            finally { mpv_free_node_contents(ref result); }
        }
        finally { foreach (var arg in args) if (arg != IntPtr.Zero) Marshal.FreeCoTaskMem(arg); }
    }

    private sealed record ThumbnailFrame(int Width, int Height, byte[] Pixels);
    [StructLayout(LayoutKind.Sequential)] private struct ThumbnailNode { public long Data; public int Format; }
    [StructLayout(LayoutKind.Sequential)] private struct ThumbnailNodeList { public int Count; public IntPtr Values, Keys; }
    [StructLayout(LayoutKind.Sequential)] private struct ThumbnailByteArray { public IntPtr Data; public nuint Size; }
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mpv_command_ret(IntPtr handle, IntPtr[] args, out ThumbnailNode result);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void mpv_free_node_contents(ref ThumbnailNode result);
}
