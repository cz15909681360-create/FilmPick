using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FilmPick;

public partial class MainWindow
{
    internal async void StartBenchmark(string fixture, string output)
    {
        scanningInBenchmark = true;
        Topmost = true;
        Activate();
        var results = new Dictionary<string, object>();
        var exitCode = 1;
        try
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var transformPath = Path.Combine(appData, "benchmark-channel-swap.cube");
            File.WriteAllText(transformPath, "LUT_3D_SIZE 2\nDOMAIN_MAX 2 2 2\n1E-05 0 0\n0 0 2\n0 2 0\n0 2 2\n2 0 0\n2 0 2\n2 2 0\n2 2 2\n");
            await OpenRoot(fixture);
            if (root == null) throw new IOException("无法打开测试目录。");
            BuildQueue(Kind.Photo);
            if (await CheckAdaptiveRaw(fixture) is { } adaptive) results["adaptiveRaw"] = adaptive;
            if (await MeasureRawSwitch() is { } rawSwitch) results["rawSwitch"] = rawSwitch;
            await Jump(rows.FindIndex(row => row.Name.StartsWith("PHOTO_00", StringComparison.Ordinal)));
            await WaitForPicture(ticket, "red");
            var import = library.Import([transformPath]);
            var saved = import.First ?? throw new IOException("无法导入测试 LUT。");
            await CheckLibraryPanel(saved, Path.ChangeExtension(output, ".panel.png"));
            results["libraryPanelAndKeyboard"] = true;
            results["photo"] = await Measure("PHOTO_", 100);
            await LutCheck("PHOTO_", saved);
            results["photoLut"] = true;
            if (File.Exists(Path.Combine(root, "DJI-air3.cube")))
                await CheckRealLut("PHOTO_", Path.Combine(root, "DJI-air3.cube"));
            await ApplySavedLut(saved);
            results["photoWithLut"] = await Measure("PHOTO_", 100);
            lutOn = false;
            ApplyLut();
            BuildQueue(Kind.Video);
            results["hevc4k"] = await Measure("HEVC_", 100);
            results["h264_1080p"] = await Measure("H264_", 20);
            await LutCheck("HEVC_", saved);
            results["videoLut"] = true;
            if (File.Exists(Path.Combine(root, "DJI-air3.cube")))
            {
                var vendor = Path.Combine(root, "DJI-air3.cube");
                await CheckRealLut("HEVC_", vendor, proxy: true);
                results["vendorLutPhotoVideoProxy"] = true;
            }
            await ApplySavedLut(saved);
            results["hevc4kWithLut"] = await Measure("HEVC_", 100);

            var index = rows.FindIndex(row => row.Name.StartsWith("HEVC_00", StringComparison.Ordinal));
            if (index < 0) throw new IOException("缺少代理测试素材。");
            await Jump(index);
            await WaitForPicture(ticket, "blue");
            ToggleProxy();
            await WaitForPicture(ticket, "red");
            ToggleLut();
            await WaitForPicture(ticket, "blue");
            ToggleProxy();
            await WaitForPicture(ticket, "red");
            results["proxy"] = true;
            results["proxyLut"] = true;

            forceSoftware = true;
            await Jump(index);
            await WaitForPicture(ticket, "red");
            if (lastDecoder != "no") throw new IOException("强制软件解码未生效：" + lastDecoder);
            results["forcedSoftware"] = true;
            forceSoftware = false;

            var opposite = rows.FindIndex(row => row.Name.StartsWith("HEVC_01", StringComparison.Ordinal));
            for (var turn = 0; turn < 50; turn++) Queue.SelectedIndex = turn % 2 == 0 ? index : opposite;
            await WaitForPicture(ticket, "green");
            await Task.Delay(250);
            if (!PixelMatches("green") && !PixelMatches("green", SampleRenderedVideo()))
                throw new IOException("快速切换后画面被旧帧覆盖。");
            results["rapidSwitch"] = true;
            await ApplySavedLut(saved);
            var another = Path.Combine(appData, "different-media-folder");
            Directory.CreateDirectory(another);
            await OpenRoot(another);
            if (lutOn || selectedLut != saved) throw new IOException("切换目录没有保留选择并关闭 LUT。");
            results["folderReset"] = true;
            var reopened = new MainWindow(appData);
            if (reopened.lutOn || reopened.selectedLut != saved || reopened.LibraryPanel.IsVisible)
                throw new IOException("新窗口没有按预期恢复 LUT 状态。");
            reopened.Close();
            results["startupState"] = true;
            exitCode = 0;
        }
        catch (Exception ex) { results["error"] = ex.ToString(); }
        finally
        {
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
            Application.Current.Shutdown(exitCode);
        }
    }

    private async Task<object> Measure(string prefix, int count)
    {
        var group = rows.Select((row, index) => (row, index)).Where(item => item.row.Name.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        if (group.Length < 2 || group.Length % 2 != 0) throw new IOException("测试素材数必须是偶数：" + prefix);
        await Jump(group[^1].index);
        await WaitForPicture(ticket, "green");
        var samples = new List<double>();
        var decoders = new HashSet<string>();
        for (var step = 0; step < count; step++)
        {
            var watch = Stopwatch.StartNew();
            var item = group[step % group.Length];
            await Jump(item.index);
            await WaitForPicture(ticket, step % 2 == 0 ? lutOn ? "blue" : "red" : "green");
            samples.Add(watch.Elapsed.TotalMilliseconds);
            if (view == Kind.Video) decoders.Add(lastDecoder);
        }
        samples.Sort();
        return new { count, p50Ms = Math.Round(samples[(int)Math.Ceiling(count * .5) - 1], 1),
            p95Ms = Math.Round(samples[(int)Math.Ceiling(count * .95) - 1], 1), maxMs = Math.Round(samples[^1], 1),
            decoders = decoders.ToArray() };
    }

    private async Task CheckLibraryPanel(string hash, string screenshot)
    {
        SetLibraryVisible(true);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        LutSearch.Focus();
        var previous = Queue.SelectedIndex;
        var marks = session.Marks.Count;
        var activeTicket = ticket;
        var source = PresentationSource.FromVisual(LutSearch);
        foreach (var key in new[] { Key.D1, Key.D2, Key.D3, Key.NumPad1, Key.NumPad2, Key.NumPad3, Key.L, Key.V, Key.Right })
        {
            var input = new KeyEventArgs(Keyboard.PrimaryDevice, source!, 0, key) { RoutedEvent = PreviewKeyDownEvent };
            LutSearch.RaiseEvent(input);
        }
        if (Queue.SelectedIndex != previous || session.Marks.Count != marks || ticket != activeTicket || lutOn)
            throw new IOException("搜索框输入触发了选片快捷键。");
        LutSearch.Text = "channel-swap";
        if (LutCards.Items.Count != 1) throw new IOException("搜索没有找到已导入条目。");
        LutCards.SelectedIndex = -1;
        LutCards.SelectedIndex = 0;
        var deadline = Stopwatch.StartNew();
        while ((selectedLut != hash || !lutOn) && deadline.ElapsedMilliseconds < 5000) await Task.Delay(15);
        if (selectedLut != hash || !lutOn) throw new IOException("点击库条目没有应用 LUT。");
        await WaitForPicture(ticket, "blue");
        var scale = VisualTreeHelper.GetDpi(Layout);
        var image = new RenderTargetBitmap((int)(Layout.ActualWidth * scale.DpiScaleX), (int)(Layout.ActualHeight * scale.DpiScaleY),
            scale.PixelsPerInchX, scale.PixelsPerInchY, PixelFormats.Pbgra32);
        image.Render(Layout);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using (var file = File.Create(screenshot)) encoder.Save(file);
        lutOn = false;
        ApplyLut();
        SetLibraryVisible(false);
        session.AutoAdvance = false;
        foreach (var (key, mark) in new[]
        {
            (Key.D3, Mark.Pick), (Key.D2, Mark.Reject), (Key.D1, Mark.None),
            (Key.NumPad3, Mark.Pick), (Key.NumPad2, Mark.Reject), (Key.NumPad1, Mark.None)
        })
        {
            var input = new KeyEventArgs(Keyboard.PrimaryDevice, source!, 0, key) { RoutedEvent = PreviewKeyDownEvent };
            RaiseEvent(input);
            if (Queue.SelectedItem is not Row row || GetMark(row.Relative) != mark)
                throw new IOException("数字键未正确标记：" + key);
        }
        session.AutoAdvance = true;
    }

    private async Task LutCheck(string prefix, string hash)
    {
        var index = rows.FindIndex(row => row.Name.StartsWith(prefix, StringComparison.Ordinal));
        await Jump(index);
        await WaitForPicture(ticket, "red");
        var previous = ticket;
        await ApplySavedLut(hash);
        await WaitForPicture(ticket, "blue");
        if (LutToggle.Content is not System.Windows.Controls.TextBlock label || !label.Text.Contains("channel-swap", StringComparison.Ordinal))
            throw new IOException("LUT 按钮没有显示正在使用的名称。");
        if (view == Kind.Video && previous != ticket) throw new IOException("切 LUT 重载了视频。");
        lutOn = false;
        ApplyLut();
        await WaitForPicture(ticket, "red");
    }

    private async Task CheckRealLut(string prefix, string path, bool proxy = false)
    {
        var index = rows.FindIndex(row => row.Name.StartsWith(prefix + "00", StringComparison.Ordinal));
        await Jump(index);
        await WaitForPicture(ticket, "red");
        var hash = library.Import([path]).First ?? throw new IOException("厂商 LUT 无法导入。");
        await CheckCurrent();
        if (proxy)
        {
            ToggleProxy();
            await WaitForPicture(ticket, "blue");
            await CheckCurrent();
            ToggleProxy();
            await WaitForPicture(ticket, "red");
        }

        async Task CheckCurrent()
        {
            var baseline = ComparisonPixel();
            var previous = ticket;
            await ApplySavedLut(hash);
            await ExpectPixelChange(baseline, false);
            if (view == Kind.Video && previous != ticket) throw new IOException("厂商 LUT 重载了视频。");
            ToggleLut();
            await ExpectPixelChange(baseline, true);
        }
    }

    private async Task ExpectPixelChange(uint baseline, bool restored)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 4000)
        {
            if (lutFailure != null) throw new IOException(lutFailure);
            var current = ComparisonPixel();
            if (readyTicket == ticket && !lutApplying && current != uint.MaxValue && current != 0 &&
                (restored ? PixelDistance(baseline, current) <= 2 : PixelDistance(baseline, current) >= 3)) return;
            await Task.Delay(15);
        }
        throw new IOException($"未检测到厂商 LUT 的画面变化（或未恢复）：{baseline:x8} → {ComparisonPixel():x8}，恢复={restored}。");
    }

    private uint ComparisonPixel() => view == Kind.Video ? SampleRenderedVideo() : CenterPixel();

    private static int PixelDistance(uint left, uint right) => new[] { 0, 8, 16 }
        .Sum(shift => Math.Abs((int)((left >> shift) & 255) - (int)((right >> shift) & 255)));

    private async Task<object?> CheckAdaptiveRaw(string fixture)
    {
        var path = Path.Combine(fixture, "RAW_D2H.NEF");
        if (!File.Exists(path)) return null;
        var index = rows.FindIndex(row => Paths.Comparer.Equals(row.Relative, "RAW_D2H.NEF"));
        var originalWidth = Viewport.Width;
        var originalHeight = Viewport.Height;
        try
        {
            var dpi = VisualTreeHelper.GetDpi(Viewport);
            Viewport.Width = 450 / dpi.DpiScaleX;
            Viewport.Height = 310 / dpi.DpiScaleY;
            UpdateLayout();
            await Jump(index);
            await WaitUntil(() => photoFrame?.Stage == PhotoStage.Embedded && readyTicket == ticket, "小窗口 D2H 内嵌图");
            if (photoFrame!.Native != new PixelSize(570, 375) || photos.DevelopedCount != 0)
                throw new IOException("小窗口错误地解码了 D2H RAW，或内嵌分辨率不正确。");

            var watch = Stopwatch.StartNew();
            Viewport.Width = originalWidth;
            Viewport.Height = originalHeight;
            UpdateLayout();
            QueuePhotoLayout();
            await WaitUntil(() => photoFrame?.Stage == PhotoStage.Developed && readyTicket == ticket, "放大后 D2H RAW 提升");
            var upgrade = watch.Elapsed.TotalMilliseconds;
            if (photoFrame!.Display.Edge <= 570 || photoFrame.Display.Edge > 4096 ||
                !MediaInfo.Text.Contains("RAW 解码预览", StringComparison.Ordinal))
                throw new IOException("D2H RAW 提升未产生足够清晰的画面。");

            var before = photos.DevelopedCount;
            RefreshPhotoLook(++ticket);
            await WaitUntil(() => readyTicket == ticket, "重绘 RAW");
            if (photos.DevelopedCount != before) throw new IOException("重绘重复解码 RAW。");
            var he = rows.FindIndex(row => Paths.Comparer.Equals(row.Relative, "RAW_Z8-HE.NEF"));
            if (he >= 0)
            {
                await Jump(he);
                await WaitUntil(() => photoFrame?.Stage == PhotoStage.Embedded && readyTicket == ticket, "Z8 高效率内嵌图");
                var z8 = catalog.Files[rows[he].Relative];
                var heFile = new PhotoFile(Paths.Below(fixture, z8.Relative), z8.Stamp);
                if (photoBox.NeedsRaw(photoFrame!.Native) || photos.Find(heFile, PhotoStage.Developed) != null)
                    throw new IOException("Z8 预览未采用大尺寸内嵌图。");
                var expectedSize = photoFrame.Native;
                for (var turn = 0; turn < 20; turn++) Queue.SelectedIndex = turn % 2 == 0 ? index : he;
                await WaitUntil(() => readyTicket == ticket && photoFrame?.Native == expectedSize, "快速翻片后仍显示 Z8");
                if (photoDirection != 1 || plannedNeighbors > 10) throw new IOException("预取方向或范围错误。");
                await Jump(index);
                await WaitUntil(() => readyTicket == ticket && photoFrame?.Stage == PhotoStage.Developed, "反向翻片读取缓存");
                if (photoDirection != -1 || plannedNeighbors > 10 || photos.CachedBytes > PreviewBox.CacheLimit)
                    throw new IOException("反向预取或内存预算错误。");
            }
            var constrained = new Photos(2 * 1024 * 1024);
            var item = new PhotoFile(path, NativeFiles.Inspect(path)!);
            var small = new PreviewBox(450, 310);
            var large = new PreviewBox(1200, 900);
            constrained.Prioritize(item, [item]);
            var cachedSmall = await constrained.Read(item, PhotoStage.Embedded, small, CancellationToken.None, prefetch: true);
            if (cachedSmall?.Stage != PhotoStage.Embedded || constrained.DevelopedCount != 0)
                throw new IOException("预取时错误地把内嵌图记为完整解码。");
            try
            {
                await constrained.Read(item, PhotoStage.Developed, large, CancellationToken.None, prefetch: true);
                throw new IOException("预取没有在预算前停止。");
            }
            catch (PhotoCacheFull) { }
            var retained = await constrained.Read(item, PhotoStage.Developed, large, CancellationToken.None);
            if (retained?.Stage != PhotoStage.Developed || constrained.CachedBytes > 2 * 1024 * 1024)
                throw new IOException("当前解码被预取预算阻塞或缓存超限。");
            var degraded = rows.FindIndex(row => Paths.Comparer.Equals(row.Relative, "RAW_Z8-SMALL.NEF"));
            if (degraded >= 0)
            {
                await Jump(degraded);
                await WaitUntil(() => readyTicket == ticket && MediaInfo.Text.Contains("无法提升清晰度", StringComparison.Ordinal),
                    "HE RAW 解码失败保留内嵌图");
                if (photoFrame?.Stage != PhotoStage.Embedded || photoFrame.Native.Edge > 640 || Photo.Source == null)
                    throw new IOException("低清 HE 内嵌图没有保留。");
            }
            return new { embedded = "570x375", developed = true, highEfficiency = he >= 0,
                unsupportedKeepsPreview = degraded >= 0, constrainedCache = true,
                resizeToDevelopedMs = Math.Round(upgrade, 1), cacheBytes = photos.CachedBytes };
        }
        finally
        {
            Viewport.Width = originalWidth;
            Viewport.Height = originalHeight;
        }
    }

    private async Task WaitUntil(Func<bool> done, string name)
    {
        var deadline = Stopwatch.StartNew();
        while (!done() && deadline.ElapsedMilliseconds < 6000)
        {
            if (frameError != null) throw new IOException(name + "：" + frameError);
            await Task.Delay(10);
        }
        if (!done()) throw new IOException(name + "超时：" + MediaInfo.Text);
    }

    private async Task<object?> MeasureRawSwitch()
    {
        var indices = rows.Select((entry, index) => (entry, index)).Where(item => Catalog.IsRaw(item.entry.Relative))
            .Select(item => item.index).ToArray();
        if (indices.Length < 2) return null;
        var samples = new List<double>();
        for (var iteration = 0; iteration < 20; iteration++)
        {
            var watch = Stopwatch.StartNew();
            await Jump(indices[iteration % indices.Length]);
            var expected = ticket;
            while (readyTicket != expected && watch.ElapsedMilliseconds < 4000) await Task.Delay(8);
            if (readyTicket != expected || frameError != null || Photo.Source == null)
                throw new IOException("RAW 队列未显示画面：" + frameError);
            samples.Add(watch.Elapsed.TotalMilliseconds);
        }
        samples.Sort();
        return new { count = samples.Count, p95Ms = Math.Round(samples[18], 1) };
    }

    private Task Jump(int index)
    {
        if (Queue.SelectedIndex == index) ShowCurrent();
        else Queue.SelectedIndex = index;
        return Task.CompletedTask;
    }

    private async Task WaitForPicture(long expected, string color)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 6000)
        {
            if (readyTicket == expected && frameError != null) throw new IOException(frameError);
            if (readyTicket == expected && (PixelMatches(color) ||
                (view == Kind.Video && watch.ElapsedMilliseconds > 300 && PixelMatches(color, SampleRenderedVideo())))) return;
            await Task.Delay(8);
        }
        throw new IOException($"未看到正确画面：{MediaName.Text}，预期 {color}，状态：{MediaInfo.Text}；已就绪 {readyTicket}/{expected}；像素 {CenterPixel():x8}。");
    }

    private bool PixelMatches(string color) => PixelMatches(color, CenterPixel());

    private static bool PixelMatches(string color, uint pixel)
    {
        var red = (int)(pixel & 255);
        var green = (int)((pixel >> 8) & 255);
        var blue = (int)((pixel >> 16) & 255);
        return pixel != uint.MaxValue && color switch
        {
            "red" => red > 85 && red > 1.65 * Math.Max(green, blue),
            "green" => green > 85 && green > 1.65 * Math.Max(red, blue),
            "blue" => blue > 85 && blue > 1.65 * Math.Max(red, green),
            _ => false
        };
    }

    private uint SampleRenderedVideo()
    {
        if (!scanningInBenchmark || player == null) return uint.MaxValue;
        var file = Path.Combine(appData, "benchmark-render.png");
        try
        {
            if (player.CaptureBenchmarkFrame(file) < 0) return uint.MaxValue;
            using var stream = File.OpenRead(file);
            var decoded = Photos.DecodeImage(stream, new(384, 384), false, PhotoStage.JpegOrPng);
            return SamplePhoto(decoded.Image);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException) { return uint.MaxValue; }
    }

    private static uint SamplePhoto(BitmapSource bitmap)
    {
        var converted = bitmap.Format == PixelFormats.Bgra32 ? bitmap : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var bytes = new byte[4];
        converted.CopyPixels(new Int32Rect((int)(converted.PixelWidth * .467), (int)(converted.PixelHeight * .479), 1, 1), bytes, 4, 0);
        return (uint)(bytes[2] | bytes[1] << 8 | bytes[0] << 16);
    }

    private uint CenterPixel()
    {
        if (view == Kind.Photo && Photo.Source is BitmapSource bitmap)
            return SamplePhoto(bitmap);
        var point = Viewport.PointToScreen(new Point(Viewport.ActualWidth * .467, Viewport.ActualHeight * .479));
        var desktop = GetDC(IntPtr.Zero);
        try
        {
            var pixel = GetPixel(desktop, (int)point.X, (int)point.Y);
            if (pixel != uint.MaxValue) return pixel;
            var hwnd = new WindowInteropHelper(this).Handle;
            var local = new ScreenPoint { X = (int)point.X, Y = (int)point.Y };
            if (!ScreenToClient(hwnd, ref local)) return pixel;
            var window = GetDC(hwnd);
            try { return GetPixel(window, local.X, local.Y); }
            finally { ReleaseDC(hwnd, window); }
        }
        finally { ReleaseDC(IntPtr.Zero, desktop); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct ScreenPoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr window, ref ScreenPoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr dc, int x, int y);
}
