using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace FilmPick;

sealed class VideoHost : HwndHost
{
    public IntPtr VideoWindow { get; private set; }
    private IntPtr surface;
    private IntPtr cover;
    private bool covered = true;
    public event Action? Created;

    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        surface = Create(0, "STATIC", "", 0x56000004, 0, 0, 1, 1, parent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        VideoWindow = Create(0, "STATIC", "", 0x56000004, 0, 0, 1, 1, surface, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        cover = Create(0, "STATIC", "", 0x54000004, 0, 0, 1, 1, surface, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (surface == IntPtr.Zero || VideoWindow == IntPtr.Zero || cover == IntPtr.Zero)
            throw new InvalidOperationException("无法创建视频预览窗。");
        Created?.Invoke();
        return new HandleRef(this, surface);
    }

    protected override void DestroyWindowCore(HandleRef hwnd) => DestroyWindow(hwnd.Handle);

    protected override void OnWindowPositionChanged(System.Windows.Rect bounds)
    {
        base.OnWindowPositionChanged(bounds);
        if (surface == IntPtr.Zero || !GetClientRect(surface, out var size)) return;
        SetWindowPos(VideoWindow, IntPtr.Zero, 0, 0, size.Right, size.Bottom, 0x14);
        SetWindowPos(cover, IntPtr.Zero, 0, 0, size.Right, size.Bottom, covered ? 0x50u : 0x94u);
    }

    public void Cover(bool value)
    {
        covered = value;
        if (cover == IntPtr.Zero || !GetClientRect(surface, out var size)) return;
        SetWindowPos(cover, IntPtr.Zero, 0, 0, size.Right, size.Bottom, value ? 0x50u : 0x94u);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode)]
    private static extern IntPtr Create(uint ex, string cls, string name, uint style, int x, int y, int w, int h,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int w, int h, uint flags);
}

record VideoFrame(long Ticket, string Decoder, double Duration, string? Error);
record VideoLut(long Revision, string? Path);
record VideoCapture(long Ticket, string? Text, double Scale, CaptureMetadata? Metadata, CaptureSrt? Track);
enum VideoDecodeMode { Default, Compatible, Software }

sealed partial class VideoPlayer : IDisposable
{
    private readonly Task loop;
    private readonly ConcurrentQueue<Action<IntPtr>> commands = new();
    private readonly Action<VideoFrame> report;
    private readonly Action<long, double, bool, bool> progress;
    private readonly Action<VideoLut, string?> reportLut;
    private IntPtr engine;
    private readonly object wakeGate = new();
    private Request? latest;
    private VideoLut? desiredLut;
    private VideoCapture? desiredCapture;
    private volatile bool disposed;
    private string? initializationError;
    private int clearPlayback;
    private long transportTicket = -1;
    private double normalSpeed = 1;
    private ShuttleRequest? shuttleRequest;
    // The playback worker owns this while a precise backward frame seek is settling.
    private long backstepStarted;
    private sealed record ShuttleRequest(long Ticket, double Rate);
    private sealed record Request(long Ticket, string Path, double Position, VideoDecodeMode Decode, bool Resume);

    public VideoPlayer(IntPtr hwnd, Action<VideoFrame> report, Action<long, double, bool, bool> progress,
        Action<VideoLut, string?> reportLut)
    {
        this.report = report;
        this.progress = progress;
        this.reportLut = reportLut;
        loop = Task.Run(() => Run(hwnd));
    }

    public void Load(long ticket, string file, double position, VideoDecodeMode decode, bool resume)
    {
        if (Volatile.Read(ref initializationError) is { } error) { report(new(ticket, "", 0, error)); return; }
        Volatile.Write(ref transportTicket, -1);
        Interlocked.Exchange(ref latest, new(ticket, file, position, decode, resume));
        Wake();
    }

    public Task Stop()
    {
        Volatile.Write(ref transportTicket, -1);
        Volatile.Write(ref shuttleRequest, null);
        Interlocked.Exchange(ref latest, null);
        Interlocked.Exchange(ref clearPlayback, 1);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (loop.IsCompleted) done.TrySetResult();
        else Queue(handle =>
        {
            Command(handle, "stop");
            var timeout = Stopwatch.StartNew();
            while (Number(handle, "idle-active", 3) != 1 && timeout.ElapsedMilliseconds < 2000)
                mpv_wait_event(handle, 0.025);
            if (Number(handle, "idle-active", 3) == 1) done.TrySetResult();
            else done.TrySetException(new IOException("视频仍占用文件，请稍后重试。"));
        });
        return Task.WhenAny(done.Task, loop).Unwrap();
    }

    public void TogglePlayback() => Queue(handle => Command(handle, "cycle", "pause"));
    public void ToggleMute() => Queue(handle => Command(handle, "cycle", "mute"));
    public void SetSpeed(double speed) { Volatile.Write(ref normalSpeed, speed); Wake(); }

    public void SetShuttle(long ticket, double speed)
    {
        Volatile.Write(ref shuttleRequest, speed == 0 ? null : new(ticket, speed));
        Wake();
    }

    public void Step(long ticket, bool forward) => Queue(handle =>
    {
        if (Volatile.Read(ref transportTicket) != ticket) return;
        Command(handle, "set", "pause", "yes");
        if (!forward)
        {
            Command(handle, "set", "hr-seek-framedrop", "no");
            backstepStarted = Stopwatch.GetTimestamp();
        }
        Command(handle, forward ? "frame-step" : "frame-back-step");
    });

    public void SeekBy(long ticket, double seconds) => Queue(handle =>
    {
        if (Volatile.Read(ref transportTicket) != ticket) return;
        var end = Double(handle, "duration");
        var target = Math.Clamp(Double(handle, "time-pos") + seconds, 0, end > 0 ? end : double.MaxValue);
        Command(handle, "seek", target.ToString("0.000000", CultureInfo.InvariantCulture), "absolute+exact");
    });
    public Task<(double Position, bool Paused)> ReadPlayback()
    {
        var result = new TaskCompletionSource<(double, bool)>(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue(handle => result.TrySetResult((Double(handle, "time-pos"), Number(handle, "pause", 3) != 0)));
        return result.Task;
    }
    internal int CaptureBenchmarkFrame(string destination)
    {
        lock (wakeGate)
            return engine == IntPtr.Zero ? -1 : Command(engine, "screenshot-to-file", destination, "window");
    }
    public void SetLut(long revision, string? path)
    {
        if (Volatile.Read(ref initializationError) is { } error) { reportLut(new(revision, path), error); return; }
        Volatile.Write(ref desiredLut, new(revision, path));
        Wake();
    }
    public void SetCapture(long ticket, string? text, double scale, CaptureMetadata? metadata = null, CaptureSrt? track = null)
    {
        var current = new VideoCapture(ticket, text, scale, metadata, track);
        if (current == Volatile.Read(ref desiredCapture)) return;
        Volatile.Write(ref desiredCapture, current);
        Wake();
    }
    public void Seek(double time, bool relative) => Queue(handle => Command(handle, "seek",
        time.ToString("0.000", CultureInfo.InvariantCulture), relative ? "relative+exact" : "absolute+exact"));

    private void Queue(Action<IntPtr> command) { commands.Enqueue(command); Wake(); }
    private void Wake() { lock (wakeGate) if (engine != IntPtr.Zero) mpv_wakeup(engine); }

    private void Run(IntPtr window)
    {
        IntPtr handle = IntPtr.Zero;
        Request? active = null;
        long playlist = -1;
        long started = -1;
        var shown = false;
        var tick = Stopwatch.StartNew();
        var changed = Stopwatch.StartNew();
        VideoLut? applied = null;
        VideoCapture? lastCapture = null;
        CaptureCue? lastCue = null;
        var seeking = false;
        var layout = Stopwatch.StartNew();
        var firstFrame = Stopwatch.StartNew();
        string? displayed = null;
        var notified = false;
        ShuttleRequest? shuttle = null;
        var wasPaused = true;
        var wasMuted = true;
        var rewindStart = 0d;
        var rewindClock = new Stopwatch();
        var rewindSeekAt = -1d;
        var speedApplied = 1d;

        void RestoreShuttle(bool resume)
        {
            if (shuttle == null) return;
            Command(handle, "set", "mute", wasMuted ? "yes" : "no");
            if (resume)
                Command(handle, "set", "pause", wasPaused || Number(handle, "eof-reached", 3) == 1 ? "yes" : "no");
            shuttle = null;
        }

        void FinishBackstep()
        {
            if (backstepStarted == 0) return;
            Command(handle, "set", "hr-seek-framedrop", "yes");
            backstepStarted = 0;
        }
        try
        {
            handle = mpv_create();
            if (handle == IntPtr.Zero) throw new InvalidOperationException("视频内核初始化失败。");
            foreach (var (option, value) in new (string, string)[]
            {
                ("config", "no"), ("load-scripts", "no"), ("input-default-bindings", "no"),
                ("input-vo-keyboard", "no"), ("input-cursor", "no"), ("osc", "no"), ("drag-and-drop", "no"),
                ("input-builtin-drag-and-drop", "no"),
                ("sub-auto", "no"), ("audio-file-auto", "no"), ("access-references", "no"),
                ("wid", window.ToInt64().ToString(CultureInfo.InvariantCulture)),
                ("vo", "gpu-next"), ("gpu-api", "d3d11"), ("hwdec", "d3d11va,d3d11va-copy"),
                ("lut-type", "conversion"), ("linear-downscaling", "no"), ("sigmoid-upscaling", "no"),
                ("target-colorspace-hint", "no"),
                ("keep-open", "yes"), ("keep-open-pause", "yes"), ("idle", "yes"),
                ("mute", "yes"), ("osd-level", "0"), ("terminal", "no")
            })
            {
                var result = mpv_set_option_string(handle, option, value);
                if (result < 0) throw new InvalidOperationException(option + "：" + Error(result));
            }
            var initialized = mpv_initialize(handle);
            if (initialized < 0) throw new InvalidOperationException(Error(initialized));
            mpv_request_log_messages(handle, "warn");
            lock (wakeGate) engine = handle;
            while (!disposed)
            {
                while (commands.TryDequeue(out var command)) command(handle);
                if (Interlocked.Exchange(ref clearPlayback, 0) != 0)
                {
                    FinishBackstep();
                    RestoreShuttle(false);
                    active = null;
                    shown = false;
                    seeking = false;
                    ClearCapture(handle, ref displayed);
                }
                var request = Interlocked.Exchange(ref latest, null);
                var desired = Volatile.Read(ref desiredLut);
                if (desired != null && desired != applied)
                {
                    applied = desired;
                    var result = Command(handle, "set", "lut", desired.Path ?? "");
                    notified = desired.Path == null || result < 0;
                    if (notified) reportLut(desired, result < 0 ? Error(result) : null);
                    changed.Restart();
                }
                if (request != null)
                {
                    FinishBackstep();
                    RestoreShuttle(false);
                    active = request;
                    started = playlist = -1;
                    shown = false;
                    seeking = false;
                    var decoderOption = request.Decode switch
                    {
                        VideoDecodeMode.Compatible => ",hwdec=d3d11va-copy",
                        VideoDecodeMode.Software => ",hwdec=no",
                        _ => ""
                    };
                    var options = "pause=yes" + decoderOption +
                        (request.Position > 0 ? ",start=" + request.Position.ToString("0.000", CultureInfo.InvariantCulture) : "");
                    var result = Command(handle, "loadfile", request.Path, "replace", "-1", options);
                    ClearCapture(handle, ref displayed);
                    if (result < 0) report(new(request.Ticket, "", 0, Error(result)));
                    playlist = Number(handle, "playlist/0/id", 4);
                }
                var eventPointer = mpv_wait_event(handle, 0.035);
                var evt = Marshal.PtrToStructure<MpvEvent>(eventPointer);
                if (disposed) break;
                if (evt.Id == 2 && evt.Data != IntPtr.Zero && applied?.Path is { } lutPath)
                {
                    var line = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(evt.Data, IntPtr.Size * 2)) ?? "";
                    if (line.Contains(lutPath, StringComparison.OrdinalIgnoreCase) ||
                        (line.Contains("LUT", StringComparison.OrdinalIgnoreCase) &&
                         (line.Contains("fail", StringComparison.OrdinalIgnoreCase) || line.Contains("invalid", StringComparison.OrdinalIgnoreCase))))
                    {
                        notified = true;
                        reportLut(applied, line.Trim());
                    }
                }
                if (active == null) continue;
                switch (evt.Id)
                {
                    case 6:
                        started = Marshal.ReadInt64(evt.Data);
                        break;
                    case 7 when evt.Data != IntPtr.Zero && Marshal.ReadInt64(evt.Data, 8) == playlist:
                        var reason = Marshal.ReadInt32(evt.Data);
                        if (reason == 4 || (!shown && reason == 0))
                        {
                            report(new(active.Ticket, "", 0, reason == 4 ? Error(Marshal.ReadInt32(evt.Data, 4)) : "找不到可预览的视频帧。"));
                            active = null;
                        }
                        break;
                    case 20 when started == playlist && playlist >= 0:
                        seeking = true;
                        break;
                    case 21 when started == playlist && playlist >= 0:
                        FinishBackstep();
                        seeking = false;
                        if (shown) break;
                        if (StringProperty(handle, "video-format") is null)
                            report(new(active.Ticket, "", 0, "找不到视频轨道。"));
                        else
                        {
                            var decoder = StringProperty(handle, "hwdec-current") ?? "no";
                            Volatile.Write(ref transportTicket, active.Ticket);
                            report(new(active.Ticket, decoder, Double(handle, "duration"), null));
                            if (active.Resume && Volatile.Read(ref latest) == null) Command(handle, "set", "pause", "no");
                        }
                        shown = true;
                        firstFrame.Restart();
                        break;
                }
                if (backstepStarted != 0 && Stopwatch.GetElapsedTime(backstepStarted).TotalSeconds > 2) FinishBackstep();
                if (active == null) { RestoreShuttle(false); ClearCapture(handle, ref displayed); continue; }
                var wantedShuttle = Volatile.Read(ref shuttleRequest);
                if (wantedShuttle?.Ticket != active.Ticket || !shown) wantedShuttle = null;
                if (shuttle != wantedShuttle)
                {
                    RestoreShuttle(true);
                    if (wantedShuttle != null)
                    {
                        shuttle = wantedShuttle;
                        wasPaused = Number(handle, "pause", 3) == 1;
                        wasMuted = Number(handle, "mute", 3) == 1;
                        rewindStart = Double(handle, "time-pos");
                        rewindClock.Restart();
                        rewindSeekAt = -1;
                        Command(handle, "set", "mute", "yes");
                        Command(handle, "set", "pause", shuttle.Rate < 0 ? "yes" : "no");
                    }
                }
                var speed = shuttle is { Rate: > 0 } ? shuttle.Rate : Volatile.Read(ref normalSpeed);
                if (speed != speedApplied)
                {
                    Command(handle, "set", "speed", speed.ToString(CultureInfo.InvariantCulture));
                    speedApplied = speed;
                }
                var elapsed = rewindClock.Elapsed.TotalSeconds;
                if (shuttle is { Rate: < 0 } && !seeking && elapsed - rewindSeekAt >= .13)
                {
                    var target = Math.Max(0, rewindStart + elapsed * shuttle.Rate);
                    rewindSeekAt = elapsed;
                    var at = Double(handle, "time-pos");
                    if (at > target && (at - target >= .02 || target == 0))
                        seeking = Command(handle, "seek", target.ToString("0.000000", CultureInfo.InvariantCulture), "absolute+exact") >= 0;
                }
                if (shown && tick.ElapsedMilliseconds > 150)
                {
                    tick.Restart();
                    progress(active.Ticket, Double(handle, "time-pos"), Number(handle, "pause", 3) != 0, Number(handle, "mute", 3) != 0);
                }
                var capture = Volatile.Read(ref desiredCapture);
                if (capture?.Ticket != active.Ticket || capture?.Text == null || !shown)
                    ClearCapture(handle, ref displayed);
                else if (firstFrame.ElapsedMilliseconds >= 120)
                {
                    var cue = !seeking && capture.Track != null ? capture.Track.SampleAt(Double(handle, "time-pos")) : null;
                    if (capture != lastCapture || !ReferenceEquals(cue, lastCue) || layout.ElapsedMilliseconds >= 250)
                    {
                        layout.Restart();
                        lastCapture = capture;
                        lastCue = cue;
                        var text = cue == null ? capture.Text : MainWindow.CaptureText(capture.Metadata, cue, false, true);
                        DrawCapture(handle, capture with { Text = text }, ref displayed);
                    }
                }
                if (shown && !notified && applied is { Path: not null } && changed.ElapsedMilliseconds >= 110)
                {
                    notified = true;
                    reportLut(applied, null);
                }
            }
        }
        catch (Exception ex)
        {
            Volatile.Write(ref initializationError, ex.Message);
            if (Volatile.Read(ref desiredLut) is { Path: not null } failed) reportLut(failed, ex.Message);
            if (Interlocked.Exchange(ref latest, null) is { } queued) report(new(queued.Ticket, "", 0, ex.Message));
            else if (active != null) report(new(active.Ticket, "", 0, ex.Message));
        }
        finally
        {
            lock (wakeGate) engine = IntPtr.Zero;
            if (handle != IntPtr.Zero) mpv_terminate_destroy(handle);
        }
    }

    private static void ClearCapture(IntPtr handle, ref string? displayed)
    {
        if (displayed == null) return;
        Command(handle, "osd-overlay", "1", "none", "");
        displayed = null;
    }

    private static void DrawCapture(IntPtr handle, VideoCapture capture, ref string? displayed)
    {
        var w = Number(handle, "osd-dimensions/w", 4);
        var h = Number(handle, "osd-dimensions/h", 4);
        if (w <= 0 || h <= 0) return;
        var left = Math.Max(0, Number(handle, "osd-dimensions/ml", 4));
        var top = Math.Max(0, Number(handle, "osd-dimensions/mt", 4));
        var right = w - Math.Max(0, Number(handle, "osd-dimensions/mr", 4));
        var bottom = h - Math.Max(0, Number(handle, "osd-dimensions/mb", 4));
        var pad = (int)Math.Round(9 * capture.Scale);
        var font = (int)Math.Round(14 * capture.Scale);
        if (right - left < 2 * pad || bottom - top < 2 * pad)
        {
            ClearCapture(handle, ref displayed);
            return;
        }
        var plain = capture.Text!.Replace('\\', '＼').Replace('{', '｛').Replace('}', '｝').Replace("\n", @"\N");
        var text = FormattableString.Invariant(
            $"{{\\an7\\pos({left + pad},{top + pad})\\clip({left},{top},{right},{bottom})\\q2\\fs{font}\\bord2\\shad1}}{plain}");
        var key = FormattableString.Invariant($"{w}×{h}:{text}");
        if (displayed == key) return;
        if (Command(handle, "osd-overlay", "1", "ass-events", text,
            w.ToString(CultureInfo.InvariantCulture), h.ToString(CultureInfo.InvariantCulture)) >= 0) displayed = key;
    }

    private static int Command(IntPtr handle, params string[] values)
    {
        var args = new IntPtr[values.Length + 1];
        try
        {
            for (var index = 0; index < values.Length; index++) args[index] = Marshal.StringToCoTaskMemUTF8(values[index]);
            return mpv_command(handle, args);
        }
        finally { foreach (var arg in args) if (arg != IntPtr.Zero) Marshal.FreeCoTaskMem(arg); }
    }

    private static string Error(int result) => Marshal.PtrToStringUTF8(mpv_error_string(result)) ?? "无法播放";
    private static string? StringProperty(IntPtr handle, string name)
    {
        var buffer = mpv_get_property_string(handle, name);
        if (buffer == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUTF8(buffer); }
        finally { mpv_free(buffer); }
    }

    private static double Double(IntPtr handle, string name)
    {
        var result = mpv_get_property(handle, name, 5, out var bits);
        return result < 0 ? 0 : BitConverter.Int64BitsToDouble(bits);
    }

    private static long Number(IntPtr handle, string name, int type) =>
        mpv_get_property(handle, name, type, out var value) >= 0 ? type == 3 ? (int)value : value : -1;

    public void Dispose()
    {
        disposed = true;
        Wake();
        loop.Wait();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MpvEvent { public int Id, Error; public ulong Reply; public IntPtr Data; }
    private const string Dll = "libmpv-2.dll";
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr mpv_create();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int mpv_initialize(IntPtr handle);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern void mpv_terminate_destroy(IntPtr handle);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern void mpv_wakeup(IntPtr handle);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr mpv_wait_event(IntPtr handle, double timeout);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int mpv_command(IntPtr handle, IntPtr[] values);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mpv_set_option_string(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mpv_request_log_messages(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string level);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr mpv_get_property_string(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mpv_get_property(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format, out long value);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr mpv_error_string(int result);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern void mpv_free(IntPtr buffer);
}
