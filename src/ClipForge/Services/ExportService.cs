using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClipForge.Models;

namespace ClipForge.Services;

public sealed class ExportService
{
    private static readonly TimeSpan EncoderProbeTimeout = TimeSpan.FromSeconds(15);
    private const string AllowedInputProtocols = "file,crypto,data";
    private static readonly HashSet<string> SupportedOutputExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv"
    };

    private readonly SemaphoreSlim _encoderGate = new(1, 1);
    private Process? _process;
    private string? _encoderCache;

    public void Cancel()
    {
        try
        {
            if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best-effort cancellation. The process may already have exited.
        }
    }

    public async Task<string> DetectEncodersAsync(CancellationToken cancellationToken = default)
    {
        if (_encoderCache is not null) return _encoderCache;

        await _encoderGate.WaitAsync(cancellationToken);
        try
        {
            if (_encoderCache is not null) return _encoderCache;

            var ffmpeg = FfmpegLocator.FindFfmpeg() ??
                throw new FileNotFoundException("Bundled ffmpeg.exe was not found.");
            var psi = new ProcessStartInfo(ffmpeg)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            Add(psi, "-hide_banner", "-encoders");

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linkedCts.CancelAfter(EncoderProbeTimeout);
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start FFmpeg.");

            try
            {
                var outputTask = process.StandardOutput.ReadToEndAsync(linkedCts.Token);
                var errorTask = process.StandardError.ReadToEndAsync(linkedCts.Token);
                await process.WaitForExitAsync(linkedCts.Token);
                var output = (await outputTask) + (await errorTask);
                if (process.ExitCode != 0)
                    throw new InvalidOperationException("FFmpeg encoder detection failed.");

                _encoderCache = output;
                return output;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                throw new TimeoutException("FFmpeg encoder detection timed out.");
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }
        }
        finally
        {
            _encoderGate.Release();
        }
    }

    public async Task ExportAsync(VideoInfo info, EditSettings s, string outputPath, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(s);
        if (string.IsNullOrWhiteSpace(outputPath)) throw new ArgumentException("An output path is required.", nameof(outputPath));

        var ffmpeg = FfmpegLocator.FindFfmpeg() ??
            throw new FileNotFoundException("Bundled ffmpeg.exe was not found. Run scripts\\Setup-FFmpeg.ps1 and rebuild ClipForge.");
        if (!File.Exists(info.Path)) throw new FileNotFoundException("The source video no longer exists.", info.Path);

        var inputFullPath = Path.GetFullPath(info.Path);
        var outputFullPath = Path.GetFullPath(outputPath);
        if (string.Equals(inputFullPath, outputFullPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("ClipForge will not overwrite the source file. Choose another output filename.");

        var outExt = Path.GetExtension(outputFullPath);
        if (!SupportedOutputExtensions.Contains(outExt))
            throw new InvalidOperationException("ClipForge currently exports only .mp4 and .mkv files.");

        var outputDirectory = Path.GetDirectoryName(outputFullPath);
        if (string.IsNullOrWhiteSpace(outputDirectory) || !Directory.Exists(outputDirectory))
            throw new DirectoryNotFoundException("The selected output folder does not exist.");

        var duration = s.End > s.Start ? s.End - s.Start : info.Duration - s.Start;
        if (duration <= TimeSpan.Zero) throw new InvalidOperationException("The trim end must be after the trim start.");
        if (s.Start < TimeSpan.Zero || s.Start >= info.Duration)
            throw new InvalidOperationException("The trim start is outside the source video.");
        if (s.End > info.Duration + TimeSpan.FromMilliseconds(50))
            throw new InvalidOperationException("The trim end is outside the source video.");

        var encoderListing = await DetectEncodersAsync(cancellationToken);
        var filters = BuildVideoFilters(info, s);
        bool audioNeedsEncode = !s.Mute && Math.Abs(s.Volume - 1.0) > 0.001;
        bool canStreamCopyVideo = filters.Count == 0;
        bool trimmed = s.Start > TimeSpan.Zero || s.End < info.Duration - TimeSpan.FromMilliseconds(50);
        var inExt = Path.GetExtension(info.Path).ToLowerInvariant();
        bool sameContainer = string.Equals(inExt, outExt, StringComparison.OrdinalIgnoreCase);
        bool streamCopy = s.SmartFastCopy && canStreamCopyVideo && !audioNeedsEncode && sameContainer && s.Encoder == "Automatic (H.264)";

        // H.264/HEVC/AV1 output pixel formats require even dimensions. If a user
        // forces a re-encode of an odd-sized source without another crop/scale,
        // remove at most one edge pixel instead of letting FFmpeg fail late.
        if (!streamCopy && s.CropMode == "None" && s.Resolution == "Original" &&
            (((info.Width & 1) != 0) || ((info.Height & 1) != 0)))
        {
            filters.Add("crop=trunc(iw/2)*2:trunc(ih/2)*2");
        }

        var tempOutput = Path.Combine(
            outputDirectory,
            $".{Path.GetFileNameWithoutExtension(outputFullPath)}.{Guid.NewGuid():N}.clipforge-partial{outExt}");

        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        Add(psi, "-hide_banner", "-nostdin", "-y");
        if (s.Start > TimeSpan.Zero) Add(psi, "-ss", FormatTime(s.Start));
        Add(psi, "-protocol_whitelist", AllowedInputProtocols, "-i", inputFullPath);
        if (trimmed) Add(psi, "-t", FormatTime(duration));
        Add(psi, "-map", "0:v:0");
        if (!s.Mute && info.HasAudio) Add(psi, "-map", "0:a:0?");

        if (s.ScrubMetadata) AddPrivacyMetadataArguments(psi);

        if (streamCopy)
        {
            Add(psi, "-c:v", "copy");
            if (s.Mute || !info.HasAudio) Add(psi, "-an");
            else Add(psi, "-c:a", "copy");
            if (trimmed) Add(psi, "-avoid_negative_ts", "make_zero");
        }
        else
        {
            if (filters.Count > 0) Add(psi, "-vf", string.Join(',', filters));
            AddEncoderArguments(psi, s, encoderListing);
            if (s.Mute || !info.HasAudio)
            {
                Add(psi, "-an");
            }
            else
            {
                if (audioNeedsEncode)
                    Add(psi, "-af", $"volume={Math.Clamp(s.Volume, 0, 2).ToString("0.###", CultureInfo.InvariantCulture)}");
                Add(psi, "-c:a", "aac", "-b:a", "192k");
            }

            if (outExt.Equals(".mp4", StringComparison.OrdinalIgnoreCase)) Add(psi, "-movflags", "+faststart");
        }

        Add(psi, "-progress", "pipe:2", "-nostats", tempOutput);

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stderr = new List<string>();
        try
        {
            _process.Start();
            while (!_process.StandardError.EndOfStream)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = await _process.StandardError.ReadLineAsync(cancellationToken);
                if (line is null) break;
                stderr.Add(line);
                if (stderr.Count > 120) stderr.RemoveAt(0);
                if (line.StartsWith("out_time=", StringComparison.Ordinal) &&
                    TimeSpan.TryParse(line.AsSpan(9), CultureInfo.InvariantCulture, out var outTime))
                {
                    progress?.Report(Math.Clamp(outTime.TotalMilliseconds / duration.TotalMilliseconds * 100.0, 0, 100));
                }
            }

            await _process.WaitForExitAsync(cancellationToken);
            if (_process.ExitCode != 0)
                throw new InvalidOperationException("FFmpeg export failed.\n\n" + string.Join(Environment.NewLine, stderr.TakeLast(30)));

            var tempInfo = new FileInfo(tempOutput);
            if (!tempInfo.Exists || tempInfo.Length <= 0)
                throw new InvalidOperationException("FFmpeg reported success but did not produce a valid output file.");

            // Commit only after a successful export. Existing destination files are
            // replaced at the end, so a failed/cancelled export cannot destroy them.
            File.Move(tempOutput, outputFullPath, overwrite: true);
            progress?.Report(100);
        }
        catch (OperationCanceledException)
        {
            Cancel();
            TryDeletePartialOutput(tempOutput);
            throw;
        }
        catch
        {
            TryDeletePartialOutput(tempOutput);
            throw;
        }
        finally
        {
            _process?.Dispose();
            _process = null;
        }
    }

    private static List<string> BuildVideoFilters(VideoInfo info, EditSettings s)
    {
        var filters = new List<string>();
        switch (s.CropMode)
        {
            case "16:9 Center":
                filters.Add("crop=floor(min(iw\\,ih*16/9)/2)*2:floor(min(ih\\,iw*9/16)/2)*2");
                break;
            case "9:16 Center":
                filters.Add("crop=floor(min(iw\\,ih*9/16)/2)*2:floor(min(ih\\,iw*16/9)/2)*2");
                break;
            case "1:1 Center":
                filters.Add("crop=floor(min(iw\\,ih)/2)*2:floor(min(iw\\,ih)/2)*2");
                break;
            case "4:3 Center":
                filters.Add("crop=floor(min(iw\\,ih*4/3)/2)*2:floor(min(ih\\,iw*3/4)/2)*2");
                break;
            case "Draw Custom":
            {
                var (x, y, width, height) = NormalizeCustomCrop(info, s);
                if (width > 1 && height > 1) filters.Add($"crop={width}:{height}:{x}:{y}");
                break;
            }
        }

        switch (s.Rotation)
        {
            case "90° Right": filters.Add("transpose=clock"); break;
            case "90° Left": filters.Add("transpose=cclock"); break;
            case "180°": filters.Add("hflip"); filters.Add("vflip"); break;
        }
        if (s.FlipHorizontal) filters.Add("hflip");
        if (s.FlipVertical) filters.Add("vflip");

        switch (s.Resolution)
        {
            case "2160p (4K)": filters.Add("scale=-2:2160:flags=lanczos"); break;
            case "1440p": filters.Add("scale=-2:1440:flags=lanczos"); break;
            case "1080p": filters.Add("scale=-2:1080:flags=lanczos"); break;
            case "720p": filters.Add("scale=-2:720:flags=lanczos"); break;
        }

        if (s.FrameRate is "60 FPS" or "30 FPS" or "24 FPS")
            filters.Add("fps=" + s.FrameRate.Split(' ')[0]);

        var brightness = Math.Clamp(s.Brightness, -1.0, 1.0);
        var contrast = Math.Clamp(s.Contrast, 0.0, 2.0);
        var saturation = Math.Clamp(s.Saturation, 0.0, 3.0);
        if (Math.Abs(brightness) > 0.001 || Math.Abs(contrast - 1) > 0.001 || Math.Abs(saturation - 1) > 0.001)
        {
            filters.Add($"eq=brightness={brightness.ToString("0.###", CultureInfo.InvariantCulture)}:" +
                        $"contrast={contrast.ToString("0.###", CultureInfo.InvariantCulture)}:" +
                        $"saturation={saturation.ToString("0.###", CultureInfo.InvariantCulture)}");
        }

        return filters;
    }

    private static (int x, int y, int width, int height) NormalizeCustomCrop(VideoInfo info, EditSettings s)
    {
        int maxWidth = Math.Max(2, info.Width);
        int maxHeight = Math.Max(2, info.Height);
        int x = Math.Clamp(s.CropX, 0, Math.Max(0, maxWidth - 2));
        int y = Math.Clamp(s.CropY, 0, Math.Max(0, maxHeight - 2));
        int width = Math.Clamp(s.CropWidth, 2, maxWidth - x);
        int height = Math.Clamp(s.CropHeight, 2, maxHeight - y);

        width = Even(width);
        height = Even(height);
        if (x + width > maxWidth) x = Math.Max(0, maxWidth - width);
        if (y + height > maxHeight) y = Math.Max(0, maxHeight - height);
        return (x, y, width, height);
    }

    private static void AddEncoderArguments(ProcessStartInfo psi, EditSettings s, string availableEncoders)
    {
        bool Has(string name) => availableEncoders.Contains(name, StringComparison.OrdinalIgnoreCase);
        var encoder = s.Encoder switch
        {
            "NVIDIA H.264" => Has("h264_nvenc") ? "h264_nvenc" : throw new InvalidOperationException("NVIDIA H.264 NVENC is not available in this FFmpeg build or on this system."),
            "NVIDIA HEVC" => Has("hevc_nvenc") ? "hevc_nvenc" : throw new InvalidOperationException("NVIDIA HEVC NVENC is not available in this FFmpeg build or on this system."),
            "NVIDIA AV1" => Has("av1_nvenc") ? "av1_nvenc" : throw new InvalidOperationException("NVIDIA AV1 NVENC is not available in this FFmpeg build or on this system."),
            "CPU H.264" => "libx264",
            _ => Has("h264_nvenc") ? "h264_nvenc" : "libx264"
        };

        if (encoder == "libx264")
        {
            var (preset, crf) = s.Quality switch
            {
                "Fastest" => ("veryfast", "23"),
                "Highest Quality" => ("slow", "16"),
                "Small File" => ("slow", "26"),
                _ => ("medium", "19")
            };
            Add(psi, "-c:v", encoder, "-preset", preset, "-crf", crf, "-pix_fmt", "yuv420p");
        }
        else
        {
            var (preset, cq) = s.Quality switch
            {
                "Fastest" => ("p1", "23"),
                "Highest Quality" => ("p7", "15"),
                "Small File" => ("p6", "27"),
                _ => ("p5", "19")
            };
            Add(psi, "-c:v", encoder, "-preset", preset, "-rc", "vbr", "-cq", cq, "-b:v", "0");
            if (encoder == "h264_nvenc") Add(psi, "-pix_fmt", "yuv420p");
            else Add(psi, "-pix_fmt", "p010le");
        }
    }

    private static void AddPrivacyMetadataArguments(ProcessStartInfo psi)
    {
        // Prevent automatic copying of global, chapter, and stream metadata from
        // the source. FFmpeg may still write generic muxer/technical tags (for
        // example major_brand or its own encoder string); those do not contain
        // the source file's EXIF/location/camera metadata.
        Add(psi,
            "-map_metadata", "-1",
            "-map_metadata:s:v", "-1",
            "-map_metadata:s:a", "-1",
            "-map_chapters", "-1");

        // Explicitly clear common privacy-sensitive aliases as a second layer.
        Add(psi,
            "-metadata", "title=",
            "-metadata", "artist=",
            "-metadata", "album=",
            "-metadata", "comment=",
            "-metadata", "description=",
            "-metadata", "copyright=",
            "-metadata", "creation_time=",
            "-metadata", "location=",
            "-metadata", "location-eng=",
            "-metadata", "com.apple.quicktime.location.ISO6709=",
            "-metadata", "make=",
            "-metadata", "model=",
            "-metadata", "software=");
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort only.
        }
    }

    private static void TryDeletePartialOutput(string outputPath)
    {
        try
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
        catch
        {
            // A failed/cancelled export should not mask the original error.
        }
    }

    private static int Even(int n) => Math.Max(2, n - (n & 1));
    private static string FormatTime(TimeSpan t) => t.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);
    private static void Add(ProcessStartInfo psi, params string[] args)
    {
        foreach (var arg in args) psi.ArgumentList.Add(arg);
    }
}
