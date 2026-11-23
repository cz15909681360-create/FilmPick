using System.Windows;
using System.Windows.Media;

namespace FilmPick;

public partial class MainWindow
{
    private CancellationTokenSource? trackCancellation;
    private CaptureInput? trackInput;
    private CaptureSrt? activeTrack;

    private void ToggleCaptureButton(object sender, RoutedEventArgs args) => ToggleCapture();

    private void ToggleCapture()
    {
        browse = browse with { ShowCapture = !browse.ShowCapture };
        RefreshBrowseWidgets();
        UpdateCaptureOverlay();
        SaveBrowse();
    }

    private void ResetCaptureOverlay()
    {
        PhotoCapture.Visibility = Visibility.Collapsed;
        player?.SetCapture(ticket, null, 1);
    }

    private void UpdateCaptureOverlay()
    {
        if (!browse.ShowCapture || Queue.SelectedItem is not Row row ||
            (view == Kind.Photo ? Photo.Source == null : !videoReady))
        {
            ResetCaptureOverlay();
            return;
        }

        var data = capture.GetValueOrDefault(row.Relative);
        if (view == Kind.Video && data?.Srt != trackInput)
        {
            trackCancellation?.Cancel();
            trackInput = data?.Srt;
            activeTrack = null;
            if (trackInput is { } input) LoadCaptureTrack(input);
        }
        var text = CaptureText(data?.Metadata, null, captureLoading, view == Kind.Video);
        if (view == Kind.Video)
        {
            PhotoCapture.Visibility = Visibility.Collapsed;
            player?.SetCapture(ticket, text, VisualTreeHelper.GetDpi(Viewport).DpiScaleY, data?.Metadata, activeTrack);
            return;
        }

        var source = Photo.Source;
        var scale = Math.Min(Viewport.ActualWidth / source.Width, Viewport.ActualHeight / source.Height);
        var width = source.Width * scale;
        var height = source.Height * scale;
        PhotoCapture.Visibility = width > 28 && height > 25 ? Visibility.Visible : Visibility.Collapsed;
        PhotoCapture.Margin = new Thickness((Viewport.ActualWidth - width) / 2 + 8, (Viewport.ActualHeight - height) / 2 + 8, 0, 0);
        PhotoCapture.MaxWidth = Math.Max(1, width - 16);
        PhotoCapture.MaxHeight = Math.Max(1, height - 16);
        PhotoCapture.ClipToBounds = true;
        PhotoCaptureText.Text = text;
    }

    private async void LoadCaptureTrack(CaptureInput input)
    {
        using var request = new CancellationTokenSource();
        trackCancellation = request;
        try
        {
            var track = await Task.Run(() => CaptureSrt.Open(input.Absolute, false, request.Token), request.Token);
            if (trackCancellation != request || request.IsCancellationRequested || trackInput != input) return;
            activeTrack = track;
            UpdateCaptureOverlay();
        }
        catch (OperationCanceledException) { }
        finally { if (trackCancellation == request) trackCancellation = null; }
    }

    internal static string CaptureText(CaptureMetadata? metadata, CaptureCue? timed, bool loading, bool video)
    {
        if (metadata == null) return loading ? "读取拍摄参数…" : "拍摄日期未知";
        var exposure = timed?.Values.Fill(metadata.Exposure) ?? metadata.Exposure;
        var date = timed?.Clock is { } clock ? "拍摄 " + clock.Display :
            metadata.Date?.Display ?? (metadata.Failed ? "拍摄日期未知 · 参数读取失败" : "拍摄日期未知");
        var model = Join(metadata.Camera, metadata.Lens);
        var values = Join(exposure.Aperture, exposure.Shutter, exposure.Iso, exposure.Focal);
        var specs = video ? Join(metadata.Resolution, metadata.Fps, metadata.Video?.Codec ?? "编码未知") : "";
        var colorSpecs = video ? Join(metadata.Video?.BitDepth is { } depth ? $"{depth} bit" : "位深未知",
            metadata.Video?.Chroma ?? "色度未知",
            VideoColors.Display(metadata.CameraColor ?? metadata.Video?.ContainerColor)) : "";
        var flight = timed?.Flight;
        var altitude = Join(FormatFlight(flight?.RelativeAltitude, "相对高度 {0:0.0} m"),
            FormatFlight(flight?.AbsoluteAltitude, "绝对高度 {0:0.0} m"), FormatFlight(flight?.Speed, "速度 {0:0.0} m/s"));
        var coordinates = Join(FormatFlight(flight?.Latitude, "纬度 {0:0.000000}"),
            FormatFlight(flight?.Longitude, "经度 {0:0.000000}"));
        return string.Join('\n', new[] { date, model, values, specs, colorSpecs, altitude, coordinates }.Where(line => line.Length > 0));
    }

    private static string? FormatFlight(double? value, string format) => value is { } number
        ? string.Format(System.Globalization.CultureInfo.InvariantCulture, format, number) : null;

    private static string Join(params string?[] parts) =>
        string.Join("  ·  ", parts.Where(part => !string.IsNullOrEmpty(part)));
}
