using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LibVLCSharp.Shared;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;
using Microsoft.Win32;
using ClipForge.Models;
using ClipForge.Services;

namespace ClipForge;

public partial class MainWindow : Window
{
    private static readonly HashSet<string> SupportedInputExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".m4v", ".webm", ".avi", ".wmv",
        ".ts", ".m2ts", ".mts", ".mpg", ".mpeg", ".mpe", ".vob",
        ".flv", ".3gp", ".3g2", ".ogv", ".ogg", ".asf"
    };

    private readonly LibVLC _libVlc;
    private readonly VlcMediaPlayer _player;
    private Media? _media;
    private readonly ExportService _exportService = new();
    private CancellationTokenSource? _exportCts;
    private VideoInfo? _info;
    private string? _inputPath;
    private bool _seeking;
    private bool _updatingTrim;
    private bool _cropDragging;
    private Point _cropStart;

    public MainWindow()
    {
        InitializeComponent();
        _libVlc = new LibVLC("--no-video-title-show", "--quiet", "--no-metadata-network-access");
        _player = new VlcMediaPlayer(_libVlc);
        VideoView.Loaded += (_, _) => VideoView.MediaPlayer = _player;

        _player.TimeChanged += (_, e) => Dispatcher.Invoke(() =>
        {
            if (!_seeking && _info is not null)
                SeekSlider.Value = Math.Clamp(e.Time / 1000.0, 0, _info.Duration.TotalSeconds);
            CurrentTimeText.Text = FormatDisplay(TimeSpan.FromMilliseconds(Math.Max(0, e.Time)));
        });
        _player.Playing += (_, _) => Dispatcher.Invoke(() => PlayPauseButton.Content = "Pause");
        _player.Paused += (_, _) => Dispatcher.Invoke(() => PlayPauseButton.Content = "Play");
        _player.Stopped += (_, _) => Dispatcher.Invoke(() => PlayPauseButton.Content = "Play");

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (args.Length > 0 && File.Exists(args[0])) await LoadVideoAsync(args[0]);
        else await UpdateEncoderStatusAsync();
    }

    private async Task UpdateEncoderStatusAsync()
    {
        try
        {
            if (FfmpegLocator.FindFfmpeg() is null || FfmpegLocator.FindFfprobe() is null)
            {
                StatusText.Text = "FFmpeg not found. Run scripts\\Setup-FFmpeg.ps1, then rebuild/publish.";
                return;
            }
            var encoders = await _exportService.DetectEncodersAsync();
            var nv = new[] { "h264_nvenc", "hevc_nvenc", "av1_nvenc" }.Where(encoders.Contains).ToArray();
            StatusText.Text = nv.Length > 0 ? $"Ready • GPU encoders: {string.Join(", ", nv)}" : "Ready • NVENC not detected; Automatic will use CPU H.264.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Encoder check: " + ex.Message;
        }
    }

    private async Task LoadVideoAsync(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (!IsSupportedVideoPath(fullPath))
                throw new InvalidOperationException("That file type is not in ClipForge's supported local-video list.");

            StatusText.Text = "Reading video…";
            _player.Stop();
            _media?.Dispose();
            _info = await ProbeService.ProbeAsync(fullPath);
            _inputPath = _info.Path;

            FileNameText.Text = Path.GetFileName(_inputPath);
            FileInfoText.Text = _info.Summary + $"  •  {FormatBytes(_info.FileSize)}  •  {FormatDisplay(_info.Duration)}";
            DurationText.Text = FormatDisplay(_info.Duration);
            CurrentTimeText.Text = "00:00.000";

            var seconds = Math.Max(0.001, _info.Duration.TotalSeconds);
            SeekSlider.Maximum = seconds;
            StartSlider.Maximum = seconds;
            EndSlider.Maximum = seconds;
            _updatingTrim = true;
            StartSlider.Value = 0;
            EndSlider.Value = seconds;
            _updatingTrim = false;
            UpdateTrimText();

            CropXBox.Text = "0";
            CropYBox.Text = "0";
            CropWBox.Text = _info.Width.ToString(CultureInfo.InvariantCulture);
            CropHBox.Text = _info.Height.ToString(CultureInfo.InvariantCulture);

            _media = new Media(_libVlc, new Uri(_inputPath));
            // New files start with neutral picture controls; export and preview stay in sync.
            BrightnessSlider.Value = 0;
            ContrastSlider.Value = 1;
            SaturationSlider.Value = 1;

            _player.Play(_media);
            _player.Pause();
            ApplyPicturePreview();
            ExportButton.IsEnabled = true;
            Title = $"ClipForge — {Path.GetFileName(_inputPath)}";
            await UpdateEncoderStatusAsync();
            UpdateCropOverlay();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not open video", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "Open failed.";
        }
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog
        {
            Title = "Open video",
            Filter = "Video files|*.mp4;*.mkv;*.mov;*.m4v;*.webm;*.avi;*.wmv;*.ts;*.m2ts;*.mts;*.mpg;*.mpeg;*.mpe;*.vob;*.flv;*.3gp;*.3g2;*.ogv;*.ogg;*.asf"
        };
        if (d.ShowDialog(this) == true) await LoadVideoAsync(d.FileName);
    }

    private async void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (_inputPath is not null) await LoadVideoAsync(_inputPath);
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        bool valid = e.Data.GetData(DataFormats.FileDrop) is string[] files &&
                     files.Length > 0 &&
                     File.Exists(files[0]) &&
                     IsSupportedVideoPath(files[0]);
        e.Effects = valid ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0 &&
            File.Exists(files[0]) && IsSupportedVideoPath(files[0]))
        {
            await LoadVideoAsync(files[0]);
        }
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_info is null) return;
        if (_player.IsPlaying) _player.Pause(); else _player.Play();
    }

    private void Back5_Click(object sender, RoutedEventArgs e) => SeekRelative(-5000);
    private void Forward5_Click(object sender, RoutedEventArgs e) => SeekRelative(5000);
    private void SeekRelative(long milliseconds)
    {
        if (_info is null) return;
        _player.Time = Math.Clamp(_player.Time + milliseconds, 0, (long)_info.Duration.TotalMilliseconds);
    }

    private void SeekSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _seeking = true;
    private void SeekSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_info is null) return;
        _player.Time = (long)(SeekSlider.Value * 1000.0);
        _seeking = false;
    }
    private void SeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_seeking) CurrentTimeText.Text = FormatDisplay(TimeSpan.FromSeconds(SeekSlider.Value));
    }

    private void MarkStart_Click(object sender, RoutedEventArgs e)
    {
        if (_info is null) return;
        StartSlider.Value = Math.Min(_player.Time / 1000.0, EndSlider.Value - 0.001);
    }
    private void MarkEnd_Click(object sender, RoutedEventArgs e)
    {
        if (_info is null) return;
        EndSlider.Value = Math.Max(_player.Time / 1000.0, StartSlider.Value + 0.001);
    }

    private void TrimSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingTrim || _info is null) return;
        _updatingTrim = true;
        if (StartSlider.Value >= EndSlider.Value)
        {
            if (ReferenceEquals(sender, StartSlider)) EndSlider.Value = Math.Min(_info.Duration.TotalSeconds, StartSlider.Value + 0.001);
            else StartSlider.Value = Math.Max(0, EndSlider.Value - 0.001);
        }
        _updatingTrim = false;
        UpdateTrimText();
    }

    private void UpdateTrimText()
    {
        var start = FormatDisplay(TimeSpan.FromSeconds(StartSlider.Value));
        var end = FormatDisplay(TimeSpan.FromSeconds(EndSlider.Value));
        TrimText.Text = $"{start}  →  {end}";
        if (StartValueText is not null) StartValueText.Text = start;
        if (EndValueText is not null) EndValueText.Text = end;
    }

    private void ResetTrim_Click(object sender, RoutedEventArgs e)
    {
        if (_info is null) return;
        _updatingTrim = true;
        StartSlider.Value = 0;
        EndSlider.Value = _info.Duration.TotalSeconds;
        _updatingTrim = false;
        UpdateTrimText();
    }

    private void MuteBox_Changed(object sender, RoutedEventArgs e)
    {
        VolumeSlider.IsEnabled = MuteBox.IsChecked != true;
        if (_player is not null) _player.Mute = MuteBox.IsChecked == true;
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (VolumeText is null) return;
        VolumeText.Text = $"{VolumeSlider.Value * 100:0}%";
        if (_player is not null && MuteBox?.IsChecked != true) _player.Volume = (int)Math.Round(VolumeSlider.Value * 100);
    }

    private void PictureSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BrightnessValueText is not null)
            BrightnessValueText.Text = $"{BrightnessSlider.Value * 100:+0;-0;0}%";
        if (ContrastValueText is not null)
            ContrastValueText.Text = $"{ContrastSlider.Value * 100:0}%";
        if (SaturationValueText is not null)
            SaturationValueText.Text = $"{SaturationSlider.Value * 100:0}%";

        ApplyPicturePreview();
    }

    /// <summary>
    /// Mirrors ClipForge's FFmpeg picture controls in LibVLC so the user sees
    /// brightness/contrast/saturation changes immediately in the preview.
    ///
    /// FFmpeg's eq brightness is centered on 0.0, while VLC's brightness is
    /// centered on 1.0, so brightness is translated as 1.0 + editor value.
    /// Contrast and saturation use the same 1.0-neutral scale in both paths.
    /// </summary>
    private void ApplyPicturePreview()
    {
        // XAML ValueChanged events can fire during InitializeComponent before
        // the LibVLC player has been assigned.
        if (_player is null || BrightnessSlider is null || ContrastSlider is null || SaturationSlider is null)
            return;

        try
        {
            bool adjusted =
                Math.Abs(BrightnessSlider.Value) > 0.001 ||
                Math.Abs(ContrastSlider.Value - 1.0) > 0.001 ||
                Math.Abs(SaturationSlider.Value - 1.0) > 0.001;

            _player.SetAdjustInt(VideoAdjustOption.Enable, adjusted ? 1 : 0);

            if (adjusted)
            {
                _player.SetAdjustFloat(VideoAdjustOption.Brightness, (float)Math.Clamp(1.0 + BrightnessSlider.Value, 0.0, 2.0));
                _player.SetAdjustFloat(VideoAdjustOption.Contrast, (float)Math.Clamp(ContrastSlider.Value, 0.0, 2.0));
                _player.SetAdjustFloat(VideoAdjustOption.Saturation, (float)Math.Clamp(SaturationSlider.Value, 0.0, 3.0));
            }
        }
        catch
        {
            // Preview adjustment support varies slightly by VLC video output.
            // Export still uses FFmpeg's eq filter, so an unsupported preview
            // adjustment must never break editing or export.
        }
    }

    private void ResetPicture_Click(object sender, RoutedEventArgs e)
    {
        ResetPictureValues();
    }

    private void ResetPictureValues()
    {
        BrightnessSlider.Value = 0;
        ContrastSlider.Value = 1;
        SaturationSlider.Value = 1;
        ApplyPicturePreview();
    }

    private void ResetSlider_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Slider slider) return;

        if (ReferenceEquals(slider, SeekSlider))
        {
            slider.Value = 0;
            if (_info is not null) _player.Time = 0;
        }
        else if (ReferenceEquals(slider, StartSlider))
        {
            slider.Value = 0;
        }
        else if (ReferenceEquals(slider, EndSlider))
        {
            slider.Value = _info?.Duration.TotalSeconds ?? slider.Maximum;
        }
        else if (ReferenceEquals(slider, VolumeSlider))
        {
            slider.Value = 1;
        }
        else if (ReferenceEquals(slider, BrightnessSlider))
        {
            slider.Value = 0;
        }
        else if (ReferenceEquals(slider, ContrastSlider) || ReferenceEquals(slider, SaturationSlider))
        {
            slider.Value = 1;
        }

        e.Handled = true;
    }

    private void CropMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        UpdateCropOverlay();
    }
    private void CropBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded || GetComboText(CropModeCombo) != "Draw Custom") return;
        UpdateCropOverlay();
    }
    private void CropCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateCropOverlay();

    private void CropCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_info is null || GetComboText(CropModeCombo) != "Draw Custom") return;
        var p = e.GetPosition(CropCanvas);
        if (!PointInsideVideo(p)) return;
        _cropDragging = true;
        _cropStart = ClampToVideo(p);
        CropCanvas.CaptureMouse();
        SetOverlayRect(_cropStart.X, _cropStart.Y, 1, 1);
        CropSelection.Visibility = Visibility.Visible;
        SetCropHandlesVisible(true);
    }

    private void CropCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_cropDragging) return;
        var p = ClampToVideo(e.GetPosition(CropCanvas));
        var x = Math.Min(_cropStart.X, p.X);
        var y = Math.Min(_cropStart.Y, p.Y);
        var w = Math.Abs(p.X - _cropStart.X);
        var h = Math.Abs(p.Y - _cropStart.Y);
        SetOverlayRect(x, y, w, h);
    }

    private void CropCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_cropDragging || _info is null) return;
        _cropDragging = false;
        CropCanvas.ReleaseMouseCapture();
        var p = ClampToVideo(e.GetPosition(CropCanvas));
        var x1 = Math.Min(_cropStart.X, p.X);
        var y1 = Math.Min(_cropStart.Y, p.Y);
        var x2 = Math.Max(_cropStart.X, p.X);
        var y2 = Math.Max(_cropStart.Y, p.Y);
        var vr = GetDisplayedVideoRect();
        if (x2 - x1 < 4 || y2 - y1 < 4) return;

        var sx = (x1 - vr.X) / vr.Width * _info.Width;
        var sy = (y1 - vr.Y) / vr.Height * _info.Height;
        var sw = (x2 - x1) / vr.Width * _info.Width;
        var sh = (y2 - y1) / vr.Height * _info.Height;
        CropXBox.Text = Math.Max(0, (int)Math.Round(sx)).ToString(CultureInfo.InvariantCulture);
        CropYBox.Text = Math.Max(0, (int)Math.Round(sy)).ToString(CultureInfo.InvariantCulture);
        CropWBox.Text = Math.Max(2, (int)Math.Round(sw)).ToString(CultureInfo.InvariantCulture);
        CropHBox.Text = Math.Max(2, (int)Math.Round(sh)).ToString(CultureInfo.InvariantCulture);
        UpdateCropOverlay();
    }

    private void UpdateCropOverlay()
    {
        if (_info is null || CropSelection is null) return;
        var mode = GetComboText(CropModeCombo);
        if (mode == "None")
        {
            CropSelection.Visibility = Visibility.Collapsed;
            SetCropHandlesVisible(false);
            return;
        }

        (int x, int y, int w, int h) crop;
        if (mode == "Draw Custom")
        {
            crop = (ParseInt(CropXBox.Text), ParseInt(CropYBox.Text), ParseInt(CropWBox.Text), ParseInt(CropHBox.Text));
            if (crop.w <= 1 || crop.h <= 1) crop = (0, 0, _info.Width, _info.Height);
        }
        else crop = PresetCrop(mode, _info.Width, _info.Height);

        var vr = GetDisplayedVideoRect();
        if (vr.Width <= 0 || vr.Height <= 0) return;
        var x = vr.X + (double)crop.x / _info.Width * vr.Width;
        var y = vr.Y + (double)crop.y / _info.Height * vr.Height;
        var w = (double)crop.w / _info.Width * vr.Width;
        var h = (double)crop.h / _info.Height * vr.Height;
        SetOverlayRect(x, y, w, h);
        CropSelection.Visibility = Visibility.Visible;
        SetCropHandlesVisible(mode == "Draw Custom");
    }

    private Rect GetDisplayedVideoRect()
    {
        if (_info is null || CropCanvas.ActualWidth <= 0 || CropCanvas.ActualHeight <= 0) return Rect.Empty;
        var vw = CropCanvas.ActualWidth;
        var vh = CropCanvas.ActualHeight;
        var srcRatio = (double)_info.Width / Math.Max(1, _info.Height);
        var viewRatio = vw / vh;
        if (viewRatio > srcRatio)
        {
            var h = vh;
            var w = h * srcRatio;
            return new Rect((vw - w) / 2, 0, w, h);
        }
        else
        {
            var w = vw;
            var h = w / srcRatio;
            return new Rect(0, (vh - h) / 2, w, h);
        }
    }

    private bool PointInsideVideo(Point p) => GetDisplayedVideoRect().Contains(p);
    private Point ClampToVideo(Point p)
    {
        var r = GetDisplayedVideoRect();
        return new Point(Math.Clamp(p.X, r.Left, r.Right), Math.Clamp(p.Y, r.Top, r.Bottom));
    }
    private void SetOverlayRect(double x, double y, double w, double h)
    {
        w = Math.Max(1, w);
        h = Math.Max(1, h);
        Canvas.SetLeft(CropSelection, x);
        Canvas.SetTop(CropSelection, y);
        CropSelection.Width = w;
        CropSelection.Height = h;

        PositionCropHandle(CropHandleTL, x, y);
        PositionCropHandle(CropHandleTR, x + w, y);
        PositionCropHandle(CropHandleBL, x, y + h);
        PositionCropHandle(CropHandleBR, x + w, y + h);
    }

    private static void PositionCropHandle(FrameworkElement handle, double x, double y)
    {
        Canvas.SetLeft(handle, x - handle.Width / 2);
        Canvas.SetTop(handle, y - handle.Height / 2);
    }

    private void SetCropHandlesVisible(bool visible)
    {
        var value = visible ? Visibility.Visible : Visibility.Collapsed;
        CropHandleTL.Visibility = value;
        CropHandleTR.Visibility = value;
        CropHandleBL.Visibility = value;
        CropHandleBR.Visibility = value;
    }

    private static (int x, int y, int w, int h) PresetCrop(string mode, int iw, int ih)
    {
        double target = mode switch { "16:9 Center" => 16.0 / 9, "9:16 Center" => 9.0 / 16, "1:1 Center" => 1, "4:3 Center" => 4.0 / 3, _ => (double)iw / ih };
        int w, h;
        if ((double)iw / ih > target) { h = ih; w = (int)Math.Floor(h * target); }
        else { w = iw; h = (int)Math.Floor(w / target); }
        w -= w & 1; h -= h & 1;
        return ((iw - w) / 2, (ih - h) / 2, Math.Max(2, w), Math.Max(2, h));
    }

    private EditSettings GatherSettings()
    {
        return new EditSettings
        {
            Start = TimeSpan.FromSeconds(StartSlider.Value),
            End = TimeSpan.FromSeconds(EndSlider.Value),
            Mute = MuteBox.IsChecked == true,
            Volume = VolumeSlider.Value,
            CropMode = GetComboText(CropModeCombo),
            CropX = ParseInt(CropXBox.Text),
            CropY = ParseInt(CropYBox.Text),
            CropWidth = ParseInt(CropWBox.Text),
            CropHeight = ParseInt(CropHBox.Text),
            Rotation = GetComboText(RotateCombo),
            FlipHorizontal = FlipHBox.IsChecked == true,
            FlipVertical = FlipVBox.IsChecked == true,
            Resolution = GetComboText(ResolutionCombo),
            FrameRate = GetComboText(FpsCombo),
            Brightness = BrightnessSlider.Value,
            Contrast = ContrastSlider.Value,
            Saturation = SaturationSlider.Value,
            Encoder = GetComboText(EncoderCombo),
            Quality = GetComboText(QualityCombo),
            SmartFastCopy = FastCopyBox.IsChecked == true,
            ScrubMetadata = ScrubMetadataBox.IsChecked == true
        };
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_info is null || _inputPath is null) return;
        if (FfmpegLocator.FindFfmpeg() is null)
        {
            MessageBox.Show(this, "FFmpeg is missing. Run scripts\\Setup-FFmpeg.ps1 and rebuild/publish ClipForge.", "FFmpeg missing", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var d = new SaveFileDialog
        {
            Title = "Export video",
            Filter = "MP4 video|*.mp4|Matroska video|*.mkv",
            DefaultExt = ".mp4",
            AddExtension = true,
            FileName = Path.GetFileNameWithoutExtension(_inputPath) + " - Edited.mp4",
            InitialDirectory = Path.GetDirectoryName(_inputPath)
        };
        if (d.ShowDialog(this) != true) return;

        string inputFullPath = Path.GetFullPath(_inputPath);
        string outputFullPath = Path.GetFullPath(d.FileName);
        if (string.Equals(inputFullPath, outputFullPath, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Choose a different output filename. ClipForge will not overwrite the source video.", "Source protected", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            _exportCts = new CancellationTokenSource();
            ExportButton.IsEnabled = false;
            CancelButton.IsEnabled = true;
            ExportProgress.Value = 0;
            ExportProgressText.Text = "0%";
            StatusText.Text = "Exporting…";
            var progress = new Progress<double>(p =>
            {
                ExportProgress.Value = p;
                ExportProgressText.Text = $"{p:0}%";
                StatusText.Text = $"Exporting… {p:0}%";
            });
            await _exportService.ExportAsync(_info, GatherSettings(), d.FileName, progress, _exportCts.Token);
            ExportProgress.Value = 100;
            ExportProgressText.Text = "100%";
            StatusText.Text = "Export complete: " + d.FileName;
            System.Media.SystemSounds.Asterisk.Play();
            var result = MessageBox.Show(this, "Export complete.\n\nOpen the output folder?", "ClipForge", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (result == MessageBoxResult.Yes)
            {
                var explorer = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    UseShellExecute = false
                };
                explorer.ArgumentList.Add("/select," + outputFullPath);
                System.Diagnostics.Process.Start(explorer);
            }
        }
        catch (OperationCanceledException)
        {
            ExportProgressText.Text = "";
            StatusText.Text = "Export cancelled.";
        }
        catch (Exception ex)
        {
            ExportProgressText.Text = "";
            StatusText.Text = "Export failed.";
            MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _exportCts?.Dispose();
            _exportCts = null;
            ExportButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
        }
    }

    private void CancelExport_Click(object sender, RoutedEventArgs e)
    {
        _exportCts?.Cancel();
        _exportService.Cancel();
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        _exportCts?.Cancel();
        _exportService.Cancel();
        _player.Stop();
        _media?.Dispose();
        VideoView.Dispose();
        _player.Dispose();
        _libVlc.Dispose();
    }

    private static bool IsSupportedVideoPath(string path) =>
        SupportedInputExtensions.Contains(Path.GetExtension(path));

    private static int ParseInt(string? s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
    private static string GetComboText(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? combo.Text;
    private static string FormatDisplay(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss\.fff") : t.ToString(@"m\:ss\.fff");
    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "size unknown";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes; int i = 0;
        while (value >= 1024 && i < units.Length - 1) { value /= 1024; i++; }
        return $"{value:0.##} {units[i]}";
    }
}
