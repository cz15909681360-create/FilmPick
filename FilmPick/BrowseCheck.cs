using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace FilmPick;

public partial class MainWindow
{
    internal async void StartBrowseCheck(string fixture, string output)
    {
        scanningInBenchmark = true;
        testingGradeWrites = true;
        Topmost = true;
        Activate();
        try
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var media = Path.Combine(appData, "合成素材 " + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(media);
            File.Copy(Path.Combine(fixture, "PHOTO_00.jpg"), Path.Combine(media, "样片2.jpg"));
            File.Copy(Path.Combine(fixture, "PHOTO_01.jpg"), Path.Combine(media, "样片10.jpg"));
            File.Copy(Path.Combine(fixture, "RAW_D2H.NEF"), Path.Combine(media, "样片2.NEF"));
            File.Copy(Path.Combine(fixture, "HEVC_00.mov"), Path.Combine(media, "视频.mov"));
            File.Copy(Path.Combine(fixture, "HEVC_00.LRF"), Path.Combine(media, "视频.LRF"));
            File.WriteAllText(Path.Combine(media, "样片2.xmp"), """
                <x:xmpmeta xmlns:x="adobe:ns:meta/">
                 <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
                  <rdf:Description rdf:about="" xmlns:xmp="http://ns.adobe.com/xap/1.0/" xmp:Rating="4"
                   xmlns:crs="http://ns.adobe.com/camera-raw-settings/1.0/" crs:Exposure2012="+0.50"/>
                 </rdf:RDF>
                </x:xmpmeta>
                """);
            File.WriteAllText(Path.Combine(media, "视频.SRT"), """
                1
                00:00:00,000 --> 00:00:00,350
                FrameCnt: 1
                2025-11-21 23:59:59.500
                [iso:100] [shutter:1/200] [fnum:2.8] [focal_len:24] [color_md: dlog_m]
                [rel_alt:12.4] [abs_alt:93.0] [drone_speedx:-3] [drone_speedy:4] [latitude:22.3200] [longitude:114.1700]

                2
                00:00:00,500 --> 00:00:01,000
                FrameCnt: 2
                2025-11-22 00:00:00.000
                [iso:1600] [shutter:1/50] [fnum:5.6] [focal_len:35]
                [rel_alt:14.2] [speed:7.2 km/h] [latitude:22.3210] [longtitude:114.1710]
                """);
            await OpenRoot(media);
            await WaitUntil(() => !captureLoading && readyTicket == ticket, "中文目录元数据");
            Check(rows.Select(row => row.Name).SequenceEqual(["样片2.jpg", "样片10.jpg"]), "自然名称正序");
            Check(RatingOf("样片2.jpg") == 4 && RatingOf("样片10.jpg") == 0, "已关联 XMP 星级");
            var photoTicket = ticket;
            ChangeQueuePresentation(true);
            await WaitUntil(() => Queue.SelectedItem is Row { Thumbnail: not null }, "照片缩略图");
            var firstCard = (Row)Queue.SelectedItem;
            var originalCardColor = SamplePhoto(firstCard.Thumbnail!);
            Check(ticket == photoTicket && firstCard.Relative == "样片2.jpg" && firstCard.Stars == "4★",
                "网格保留选中照片和星级");
            Check(FindQueueVisual<ThumbnailGrid>(Queue) is { ViewportHeight: > 0 } &&
                  Queue.ItemContainerGenerator.ContainerFromIndex(1) is FrameworkElement below &&
                  Queue.ItemContainerGenerator.ContainerFromIndex(0) is FrameworkElement top &&
                  below.TranslatePoint(new Point(), Queue).Y > top.TranslatePoint(new Point(), Queue).Y,
                "默认单列照片网格布局");
            var queueWidth = ui.QueueWidth;
            ui = ui with { QueueWidth = 418 };
            ApplyWorkspaceWidths();
            await WaitUntil(() => Queue.ItemContainerGenerator.ContainerFromIndex(1) is FrameworkElement second &&
                Queue.ItemContainerGenerator.ContainerFromIndex(0) is FrameworkElement first &&
                Math.Abs(second.TranslatePoint(new Point(), Queue).Y - first.TranslatePoint(new Point(), Queue).Y) < 2,
                "拉宽后同排显示两张");
            ui = ui with { QueueWidth = queueWidth };
            ApplyWorkspaceWidths();
            var thumbnailCube = Path.Combine(appData, "thumbnail-check.cube");
            File.WriteAllText(thumbnailCube, "LUT_3D_SIZE 2\n0 0 0\n0 0 1\n0 1 0\n0 1 1\n1 0 0\n1 0 1\n1 1 0\n1 1 1\n");
            var thumbnailLut = library.Import([thumbnailCube]).First!;
            await ApplySavedLut(thumbnailLut);
            await WaitUntil(() => firstCard.Thumbnail is { } image && SamplePhoto(image) != originalCardColor, "照片缩略图跟随 LUT");
            ToggleLut();
            await WaitUntil(() => firstCard.Thumbnail is { } image && SamplePhoto(image) == originalCardColor, "关闭 LUT 恢复缩略图");
            await WaitUntil(() => readyTicket == ticket, "照片恢复主预览");
            Check(PhotoCapture.Visibility == Visibility.Visible, "照片叠字");
            Check(!VideoControls.IsVisible && GradeLocation.Text == "照片＋XMP", "照片模式收起视频控件并提示分级位置");
            Check(capture["样片2.jpg"].Metadata.Camera?.Contains("D2H", StringComparison.OrdinalIgnoreCase) == true &&
                PhotoCaptureText.Text.Contains("2005-09-24", StringComparison.Ordinal) &&
                PhotoCaptureText.Text.Contains("28 mm（等效 42 mm）", StringComparison.Ordinal), "RAW＋JPG 的 RAW 拍摄参数");
            Tab();
            Check(PhotoCapture.Visibility == Visibility.Collapsed, "Tab 收起照片叠字");
            Tab();
            Filter = new(Name: ".NEF", Date: DateFilter.Range, From: new(2005, 9, 24), To: new(2005, 9, 24));
            SetOrder(new(SortField.Date, true));
            Check(rows.Count == 1 && MediaName.Text == "样片2.jpg", "RAW 文件名与日期区间筛选");
            ClearNameSearch(this, new RoutedEventArgs());
            Check(Filter.Name == "" && Filter.Date == DateFilter.Range && rows.Count == 1, "只清除文件名");
            SetOrder(new(SortField.Rating, true));
            Filter = new(Stars: (1 << 4) | 1);
            RefreshQueue();
            ((ButtonBase)RatingStrip.Children[4]).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(rows.Count == 1 && MediaName.Text == "样片10.jpg", "修改评分后去下一条");
            session.AutoAdvance = false;
            SetMark(Mark.Pick);
            Filter = new(Marks: 1 << (int)Mark.None);
            RefreshQueue();
            Check(rows.Count == 1 && MediaName.Text == "样片2.jpg" &&
                archive?.Plan(catalog, session, GetMark).Single().Main == "样片10.jpg", "过滤隐藏的已选仍在归档");
            await gradeTask;
            Check(gradeOutbox.Entries.Count == 0, "标准元数据写入 " + string.Join(" | ", gradeFailures.Values));
            using (var exif = new CaptureReader())
            {
                var scored = new[] { "样片2.jpg", "样片2.xmp", "样片10.jpg" }.Select(name => Path.Combine(media, name)).ToArray();
                var result = await exif.Read(scored, CancellationToken.None);
                Check(result[scored[0]].Rating == 5 && result[scored[1]].Rating == 5 && result[scored[2]].Flag == Mark.Pick,
                    "回读 JPG 星级、RAW XMP 星级与 Lightroom 旗标");
            }
            Check(File.ReadAllText(Path.Combine(media, "样片2.xmp")).Contains("Exposure2012", StringComparison.Ordinal),
                "保留 ACR 编辑字段");
            var jpeg = Path.Combine(media, "样片2.jpg");
            var rawHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(media, "样片2.NEF"))));
            using (var locked = new FileStream(jpeg, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                SetMark(Mark.Reject);
                await gradeTask;
                Check(gradeOutbox.Entries.TryGetValue("样片2.jpg", out var pending) && pending.Transaction != null,
                    "占用时保留可重试的同组写入记录");
            }
            gradeFailures.Clear();
            StartGradeWrites();
            await gradeTask;
            Check(gradeOutbox.Entries.Count == 0 &&
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(media, "样片2.NEF")))) == rawHash,
                "重试已部分写入的 RAW＋JPG 组，RAW 字节完全相同");
            var scoredGroups = archive!.Plan(catalog, session, GetMark);
            var copies = await Task.Run(() => archive.Run(scoredGroups));
            Check(copies.Completed == 2 && copies.Issues.Count == 0, "照片标准分级完成后复制归档");
            using (var exif = new CaptureReader())
            {
                var jpgCopy = archive.Destination(Mark.Reject, "样片2.jpg");
                var xmpCopy = archive.Destination(Mark.Reject, "样片2.xmp");
                var values = await exif.Read([jpgCopy, xmpCopy], CancellationToken.None);
                Check(values[jpgCopy] is { Rating: 5, Flag: Mark.Reject } && values[xmpCopy] is { Rating: 5, Flag: Mark.Reject },
                    "归档副本可直接读出五星和排除旗标");
            }
            NameFilter.Text = "没有匹配项";
            await WaitUntil(() => rows.Count == 0, "名称搜索");
            ClearQueueFilters(this, new RoutedEventArgs());
            Check(rows.Count == 2, "清空筛选");

            // Hide cached flags to exercise the write queue's pre-edit metadata read.
            capture.Remove("样片2.jpg");
            publishedGrades.Remove("样片2.jpg");
            RecountMarks();
            Queue.SelectedItem = rows.Single(row => row.Relative == "样片2.jpg");
            SetMark(Mark.Pick);
            SetMark(Mark.None);
            UndoMark();
            await WaitUntil(() => !undoingMark && GetMark("样片2.jpg") == Mark.Pick, "撤销未写完的旗标");
            UndoMark();
            await WaitUntil(() => !undoingMark && GetMark("样片2.jpg") == Mark.Reject, "撤销恢复实际原旗标");
            await gradeTask;
            using (var rereader = new CaptureReader())
            {
                var saved = await rereader.Read([jpeg], CancellationToken.None);
                Check(saved[jpeg] is { Rating: 5, Flag: Mark.Reject }, "多步撤销写回且保留星级");
            }

            BuildQueue(Kind.Video);
            await WaitForPicture(ticket, "red");
            Check(!thumbnailView && ui.PhotoThumbnails && !ui.VideoThumbnails, "照片与视频单独记住视图");
            var videoTicket = ticket;
            ChangeQueuePresentation(true);
            await WaitUntil(() => Queue.SelectedItem is Row { Thumbnail: not null }, "视频缩略图");
            var videoCard = (Row)Queue.SelectedItem;
            var videoColor = SamplePhoto(videoCard.Thumbnail!);
            await ApplySavedLut(thumbnailLut);
            await WaitUntil(() => videoCard.Thumbnail is { } image && SamplePhoto(image) != videoColor, "视频缩略图跟随 LUT");
            ToggleLut();
            await WaitUntil(() => videoCard.Thumbnail is { } image && SamplePhoto(image) == videoColor, "视频缩略图恢复原画面");
            ChangeQueuePresentation(false);
            Check(ticket == videoTicket && Queue.SelectedItem == videoCard && videoReady, "切换视图无需重新打开视频");
            await WaitUntil(() => activeTrack?.At(0)?.Iso == "ISO 100", "载入同名 DJI SRT");
            var video = capture["视频.mov"].Metadata;
            Check(video.Date?.Day == new DateOnly(2025, 11, 21) && video.Resolution == "3840 × 2160" &&
                video.Fps == "30 fps" && video.Video is { Codec: "HEVC", Chroma: "4:2:0", BitDepth: 10 }, "视频日期、编码、色度采样与原片位深");
            var overlay = CaptureText(video, null, false, true);
            Check(overlay.Contains("10 bit", StringComparison.Ordinal), "视频参数区显示原片位深");
            Check(overlay.Contains("D-Log M", StringComparison.Ordinal) &&
                videoLoad?.Decode == VideoDecodeMode.Compatible, "由原片 SRT 选择兼容硬解与色彩标记");
            Check(activeTrack?.SampleAt(0)?.Flight.Speed == 5 && activeTrack.SampleAt(.42) == null &&
                activeTrack.SampleAt(.75)?.Flight.Speed == 2 && activeTrack.At(.75)?.Focal == "等效 35 mm", "SRT 的速度、坐标、等效焦段和空段");
            var first = await VideoSnapshot("capture-first.png");
            Tab();
            var hidden = await VideoSnapshot("capture-hidden.png");
            Check(first != hidden, "视频 OSD 确实绘制");
            Tab();
            player?.Seek(.42, false);
            await WaitUntil(() => position >= .36 && position < .5, "定位到 SRT 空段");
            var gap = await VideoSnapshot("capture-gap.png");
            Check(first != gap && activeTrack?.SampleAt(position) == null, "空段清除飞行数据");
            player?.TogglePlayback();
            await WaitUntil(() => position >= .55, "连续播放进入第二个 SRT 段");
            player?.Seek(0, false);
            await WaitUntil(() => position < .2, "回跳到片头");
            player?.TogglePlayback();
            player?.Seek(.75, false);
            await WaitUntil(() => position >= .5, "定位到第二段 SRT");
            var later = await VideoSnapshot("capture-later.png");
            Check(activeTrack?.At(position)?.Iso == "ISO 1600" && first != later, "时间轴参数更新");
            ToggleProxy();
            await WaitForPicture(ticket, "blue");
            await WaitUntil(() => position >= .5, "切代理保留时间轴位置");
            Check(activeTrack?.At(position)?.Iso == "ISO 1600" && capture["视频.mov"].Metadata == video, "代理沿用主片参数");
            Check(videoLoad?.Decode == VideoDecodeMode.Default, "LRF 使用默认解码");
            await VideoSnapshot("capture-proxy.png");
            if (CanRetrySoftware)
            {
                var start = position;
                RetrySoftwareClick(this, new RoutedEventArgs());
                await WaitUntil(() => videoReady && lastDecoder == "no", "手动软解重试");
                Check(usingProxy && Math.Abs((await player!.ReadPlayback()).Position - start) < .07, "软解重试保留代理和时间轴");
            }
            using (var exif = new CaptureReader())
            {
                var specs = new Dictionary<string, VideoSpec>(Paths.Comparer)
                {
                    ["H264_00.mp4"] = new("H.264", "4:2:0", BitDepth: 8),
                    ["avc422.mp4"] = new("H.264", "4:2:2", BitDepth: 8),
                    ["hevc422.mov"] = new("HEVC", "4:2:2", BitDepth: 10),
                    ["avc10.mp4"] = new("H.264", "4:2:2", BitDepth: 10),
                    ["hevc8.mp4"] = new("HEVC", "4:2:0", BitDepth: 8),
                    ["hevc12.mov"] = new("HEVC", "4:4:4", BitDepth: 12),
                    ["av1-10.mp4"] = new("AV1", null, BitDepth: 10),
                    ["vp9-10.mp4"] = new("VP9", null, BitDepth: 10)
                };
                var paths = specs.Keys.Select(name => Path.Combine(fixture, name)).Where(File.Exists).ToArray();
                var samples = await exif.Read(paths, CancellationToken.None);
                foreach (var path in paths)
                    Check(samples[path].Video is { } spec && spec with { ContainerColor = null } ==
                        specs[Path.GetFileName(path)], "码流配置及位深：" + path);

                var gradeFolder = Path.Combine(appData, "额外格式 " + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(gradeFolder);
                foreach (var name in new[] { "grade.png", "grade.dng" })
                {
                    var input = Path.Combine(fixture, name);
                    if (!File.Exists(input)) continue;
                    var target = Path.Combine(gradeFolder, name);
                    File.Copy(input, target);
                    var scans = Catalog.Scan(gradeFolder, false);
                    var writer = new GradeWriter(gradeFolder);
                    var plan = GradeWriter.Plan(scans, name, scans.Owners(new Session()));
                    var write = await Task.Run(() => writer.Prepare(plan, new(Stars: 3, Flag: Mark.Pick)));
                    await Task.Run(() => writer.Publish(plan, write));
                    writer.Cleanup(plan, write.Id);
                    var result = await exif.Read([target], CancellationToken.None);
                    Check(result[target] is { Rating: 3, Flag: Mark.Pick }, "保存内嵌评分：" + name);
                }
            }
            var photoOnly = Directory.CreateDirectory(Path.Combine(appData, "仅照片")).FullName;
            File.Copy(Path.Combine(fixture, "PHOTO_00.jpg"), Path.Combine(photoOnly, "仅照片.jpg"));
            await OpenRoot(photoOnly);
            BuildQueue(Kind.Video);
            Check(SwitchEmptyMode.IsVisible && Equals(SwitchEmptyMode.Content, "切换到照片") && !VideoControls.IsVisible, "空视频队列提示切到照片");
            JumpEmptyMode(this, new RoutedEventArgs());
            Check(view == Kind.Photo && Queue.SelectedItem is Row && !SwitchEmptyMode.IsVisible, "切回照片");
            await OpenRoot(Directory.CreateDirectory(Path.Combine(appData, "空目录")).FullName);
            Check(rows.Count == 0 && !SwitchEmptyMode.IsVisible, "空目录不显示切换按钮");
            await CheckDroppedAssets();
            File.WriteAllText(output, "PASS: WPF browse, grades and multi-undo, archive, file drop, codec, chroma, bit depth, color, focal length, Tab, timed DJI data, LRF and software retry.");
            Application.Current.Shutdown(0);

            async Task CheckDroppedAssets()
            {
                var raw = Path.Combine(media, "样片2.NEF");
                var videoFile = Path.Combine(media, "视频.mov");
                var lrf = Path.Combine(media, "视频.LRF");
                var orphan = Path.Combine(media, "独立.XMP");
                File.WriteAllText(orphan, "");
                await OpenDrop([media]);
                Check(Paths.Comparer.Equals(root, media), "拖入文件夹");
                await OpenDrop([orphan, raw, videoFile]);
                Check(view == Kind.Photo && Queue.SelectedItem is Row { Relative: "样片2.jpg" }, "同目录批次按顺序定位 RAW 组");
                await OpenDrop([lrf]);
                await WaitForPicture(ticket, "red");
                Check(view == Kind.Video && Queue.SelectedItem is Row { Relative: "视频.mov" }, "拖代理定位原片");
                var currentTicket = ticket;
                await OpenDrop([lrf]);
                Check(ticket == currentTicket, "同一视频无需重新打开");
                await OpenDrop([orphan]);
                await OpenDrop([photoOnly, videoFile]);
                await OpenDrop([Path.Combine(photoOnly, "仅照片.jpg"), videoFile]);
                await OpenDrop([Path.Combine(media, "失效.mp4")]);
                Check(ticket == currentTicket && Paths.Comparer.Equals(root, media), "错误批次保留当前预览");
                await OpenDrop([Path.Combine(photoOnly, "仅照片.jpg")]);
                Check(Paths.Comparer.Equals(root, photoOnly) && Queue.SelectedItem is Row { Relative: "仅照片.jpg" }, "文件打开新目录并定位");
                await OpenDrop([raw]);
                await WaitUntil(() => !captureLoading, "拖放目录元数据");
                photoFilter = new(Name: "找不到", Marks: 1 << (int)GetMark("样片2.jpg"));
                var marks = photoFilter.Marks;
                RefreshQueue();
                await OpenDrop([raw]);
                Check(Queue.SelectedItem is Row { Relative: "样片2.jpg" } && photoFilter is { Name.Length: 0 } &&
                    photoFilter.Marks == marks && StatusLine.Text.Contains("已清除文件名"), "只清除挡住目标的条件");
                var sub = Directory.CreateDirectory(Path.Combine(media, "子目录")).FullName;
                RecursiveBox.IsChecked = true;
                ChangeRecursion(this, new RoutedEventArgs());
                await WaitUntil(() => !busy && session.Recursive, "启用子目录扫描");
                var added = Path.Combine(sub, "新照片.jpg");
                File.Copy(Path.Combine(fixture, "PHOTO_01.jpg"), added);
                await OpenDrop([added]);
                Check(Paths.Comparer.Equals(root, media) && Queue.SelectedItem is Row { Relative: @"子目录\新照片.jpg" },
                    "定位到新出现的递归子目录素材");
            }
        }
        catch (Exception error)
        {
            File.WriteAllText(output, error.ToString());
            Application.Current.Shutdown(1);
        }

        void Tab()
        {
            Queue.Focus();
            var source = PresentationSource.FromVisual(this);
            RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source!, 0, Key.Tab) { RoutedEvent = PreviewKeyDownEvent });
        }

        async Task<string> VideoSnapshot(string name)
        {
            await Task.Delay(300);
            var path = Path.Combine(appData, name);
            Check(player?.CaptureBenchmarkFrame(path) == 0 && File.Exists(path), name);
            return Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)));
        }

        static void Check(bool ok, string description)
        {
            if (!ok) throw new IOException("验证失败：" + description);
        }
    }
}
