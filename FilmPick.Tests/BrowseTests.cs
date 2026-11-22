using System.Text.Json;
using FilmPick;

static class BrowseTests
{
    public static void Run(string area, Catalog catalog)
    {
        using var json = JsonDocument.Parse("""
            {"ExifIFD:DateTimeOriginal":"2025:11:21 00:15:30","ExifIFD:OffsetTimeOriginal":"+08:00",
             "ExifIFD:ExposureTime":0.004,"ExifIFD:FNumber":2.8,"ExifIFD:ISO":800,"XMP-xmp:Rating":4}
            """);
        var capture = CaptureMetadata.Parse(json.RootElement);
        var day = new DateOnly(2025, 11, 21);
        Check(capture.Date?.Day == day && capture.Date.Offset == TimeSpan.FromHours(8) &&
            capture.Exposure.Shutter == "1/250 s" && capture.Rating == 4, "EXIF, wall clock, fractional shutter and rating");
        Check(CaptureDate.Parse("2025:11:21 00:10:00", "+::::")?.Offset == null &&
            CaptureDate.Parse("0000:00:00 00:00:00") == null, "invalid EXIF date and zone");
        using var focal = JsonDocument.Parse("""{"ExifIFD:FocalLength":28,"ExifIFD:FocalLengthIn35mmFormat":42}""");
        using var fullFrame = JsonDocument.Parse("""{"ExifIFD:FocalLength":24,"ExifIFD:FocalLengthIn35mmFormat":24,"Nikon:CropHiSpeed":"11 8280 5520 8280 5520 0 0"}""");
        Check(CaptureMetadata.Parse(focal.RootElement).Exposure.Focal == "28 mm（等效 42 mm）" &&
            CaptureMetadata.Parse(fullFrame.RootElement).Exposure.Focal == "24 mm" &&
            new Exposure(EquivalentMm: 168).Focal == "等效 168 mm", "physical, equivalent-only and full-frame focal lengths");
        var filter = new BrowseFilter("nef", DateFilter.Range, day, day,
            Stars: (1 << 4) | (1 << 5), Marks: (1 << (int)Mark.Pick) | (1 << (int)Mark.None));
        Check(filter.Includes("A.JPG", "A.NEF", capture.Date, 4, Mark.Pick, false) &&
            !filter.Includes("A.JPG", "A.NEF", capture.Date, 3, Mark.Pick, false) &&
            !filter.Includes("A.JPG", "A.NEF", capture.Date, 5, Mark.Reject, false), "combined browse filters");
        var unknown = new BrowseFilter(Date: DateFilter.Unknown);
        Check(!unknown.Includes("A.jpg", null, null, 0, Mark.None, true) &&
            unknown.Includes("A.jpg", null, null, 0, Mark.None, false), "pending date is distinct from unknown");
        BrowseItem known = new("IMG_2.jpg", null, Mark.Pick, 4, capture.Date, false);
        BrowseItem missing = new("IMG_10.jpg", null, Mark.None, 0, null, false);
        Check(Browse.Compare(known, missing, new(SortField.Date)) < 0 &&
            Browse.Compare(known, missing, new(SortField.Date, true)) < 0 &&
            Browse.Compare(known, missing, new()) < 0 &&
            Browse.Compare(known, missing, new(SortField.Mark, true)) < 0, "date, natural name and grade order");
        var visible = new HashSet<string>(["a", "c"], Paths.Comparer);
        Check(Browse.Next("b", ["a", "b", "c"], visible, false) == "c" &&
            Browse.Next("b", ["a", "c", "b"], visible, true) == "c", "filtered selection and single advance");

        using var text = new StringReader("""
            1
            00:00:00,000 --> 00:00:00,500
            <font size="28">FrameCnt: 1, DiffTime: 33ms
            2025-11-21 23:59:59.500
            [iso:100] [shutter:1/200] [fnum:2.8] [focal_len:24]</font>

            2
            00:00:00,600 --> 00:00:01,000
            FrameCnt: 2
            2025-11-22 00:00:00.100
            [iso:800] [shutter:1/100] [color_md: d_log]
            """);
        var srt = CaptureSrt.Parse(text, false);
        Check(srt.Recognized && srt.Start?.Day == day && srt.At(.1)?.Iso == "ISO 100" &&
            srt.At(.55) == null && srt.At(.65)?.Iso == "ISO 800" && srt.At(1) == null, "DJI timed exposure and midnight");
        Check(srt.Color == VideoColor.DLog && VideoColors.FromDji("dlog_m") == VideoColor.DLogM &&
            VideoColors.MergeModes(srt.Color, VideoColor.DLogM) == VideoColor.Unknown, "DJI recording profiles and conflicts");
        Check(srt.At(.1) is { FocalMm: null, EquivalentMm: 24 }, "DJI SRT focal length is equivalent");
        using var flightText = new StringReader("""
            1
            00:00:00,000 --> 00:00:02,000
            FrameCnt: 1
            2025-11-21 23:59:59.500
            [rel_alt:-4.2] [abs_alt:30] [drone_speedx:-3] [drone_speedy:4] [latitude:-12.5] [longitude:44]

            2
            00:00:00,300 --> 00:00:00,500
            FrameCnt: 2
            [speed:36 km/h] [latitude:-12.6] [longtitude:45]
            """);
        var track = CaptureSrt.Parse(flightText, false);
        Check(track.SampleAt(.2)?.Flight is { RelativeAltitude: -4.2, Speed: 5, Latitude: -12.5 } &&
            track.SampleAt(.4)?.Flight is { Speed: 10, Longitude: 45 } &&
            track.SampleAt(.5)?.Flight.Speed == 5 && track.SampleAt(2) == null, "DJI signed telemetry, units, overlap and gaps");
        using var ordinary = new StringReader("1\n00:00:00,000 --> 00:00:02,000\nThis is an ordinary subtitle.\n");
        Check(!CaptureSrt.Parse(ordinary, false).Recognized, "ordinary subtitles ignored");

        var path = catalog.Ordered.First();
        var local = new Store("rating-test", area);
        var records = new Session();
        records.Ratings[path] = new(0, catalog.Files[path].Stamp);
        local.Write("session", records);
        var saved = local.Read<Session>("session");
        catalog.Reconcile(saved);
        Check(saved.Ratings.GetValueOrDefault(path)?.Stars == 0 && saved.Marks.Count == 0 &&
            saved.Ratings[path].Matches(catalog.Files[path].Stamp with { Modified = 0 }), "persist explicit zero separately");
        local.Write("browse", new BrowsePreferences(new(SortField.Date, true), new(), false));
        var options = local.Read<BrowsePreferences>("browse");
        Check(options.Photo == new BrowseOrder(SortField.Date, true) && options.Video == new BrowseOrder() && !options.ShowCapture,
            "per-mode ordering and shared overlay preference");
        using var flags = JsonDocument.Parse("""{"XMP-xmp:Rating":0,"IFD0:Rating":5,"XMP-xmpDM:Pick":-1}""");
        var standard = CaptureMetadata.Parse(flags.RootElement);
        Check(standard.Rating == 0 && standard.Flag == Mark.Reject, "independent flag and explicit unrated");
        using var avc = JsonDocument.Parse("""{"Track1:CompressorID":"avc1","Track1:AVCConfiguration":"base64:AWQAKP/hABtnZAAorNlAeAIn5cBEAAADAAQAAAMA8DxgxlgBAARo74/L/fj4AA=="}""");
        Check(VideoSpec.Parse(avc.RootElement) == new VideoSpec("H.264", "4:2:0"), "AVC configuration");
        var hevc = new byte[23];
        hevc[0] = 1;
        hevc[16] = 0xfe;
        using var hevcJson = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["Track1:CompressorID"] = "hvc1", ["Track1:Unknown_hvcC"] = "base64:" + Convert.ToBase64String(hevc)
        }));
        Check(VideoSpec.Parse(hevcJson.RootElement) == new VideoSpec("HEVC", "4:2:2"), "HEVC configuration");
        using var noConfig = JsonDocument.Parse("""{"Track1:CompressorID":"hvc1"}""");
        Check(VideoSpec.Parse(noConfig.RootElement) == new VideoSpec("HEVC", null), "unknown chroma isn't inferred from codec");
        using var labeled = JsonDocument.Parse("""{"Track1:CompressorID":"hvc1","Track1:ColorPrimaries":1,"Track1:TransferCharacteristics":1,"Track2:TransferCharacteristics":18}""");
        Check(VideoSpec.Parse(labeled.RootElement)?.ContainerColor == VideoColor.Tagged709, "709 marker belongs to the video track");
        using var incomplete = JsonDocument.Parse("""{"Track1:CompressorID":"hvc1","Track1:ColorPrimaries":1}""");
        Check(VideoSpec.Parse(incomplete.RootElement)?.ContainerColor == null, "709 primaries alone do not establish a profile");
        var sony = Path.Combine(area, "sony-color");
        Directory.CreateDirectory(sony);
        File.WriteAllText(Path.Combine(sony, "C0001.MP4"), "test");
        File.WriteAllText(Path.Combine(sony, "C0001M01.XML"), """
            <NonRealTimeMeta xmlns="urn:schemas-professionalDisc:nonRealTimeMeta:ver.2.20">
              <AcquisitionRecord><Group name="CameraUnitMetadataSet">
                <Item name="CaptureGammaEquation" value="s-log3-cine"/>
                <Item name="CodingEquations" value="rec709"/>
              </Group></AcquisitionRecord>
            </NonRealTimeMeta>
            """);
        Check(VideoColors.ReadSony(Path.Combine(sony, "C0001M01.XML")) == (true, VideoColor.SLog3) &&
            Catalog.Scan(sony, false).Automatic["C0001M01.XML"] == "C0001.MP4", "Sony acquisition gamma and M01 pairing");
        Console.WriteLine("PASS: capture metadata, filters, ordering, selection, local rating, preferences and timed DJI SRT.");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + name);
    }
}
