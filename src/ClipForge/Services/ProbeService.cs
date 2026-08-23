using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClipForge.Models;

namespace ClipForge.Services;

public static class ProbeService
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);
    private const string AllowedInputProtocols = "file,crypto,data";

    public static async Task<VideoInfo> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A video path is required.", nameof(path));

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("The selected video file no longer exists.", fullPath);

        var ffprobe = FfmpegLocator.FindFfprobe() ??
            throw new FileNotFoundException("Bundled ffprobe.exe was not found. Run scripts\\Setup-FFmpeg.ps1 and rebuild ClipForge.");

        var psi = new ProcessStartInfo
        {
            FileName = ffprobe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        Add(psi,
            "-v", "error",
            "-protocol_whitelist", AllowedInputProtocols,
            "-show_streams", "-show_format",
            "-of", "json",
            fullPath);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(ProbeTimeout);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start ffprobe.");

        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(linkedCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(linkedCts.Token);
            await process.WaitForExitAsync(linkedCts.Token);

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"ffprobe failed: {TrimForMessage(stderr)}");

            using var doc = JsonDocument.Parse(stdout, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64
            });

            var root = doc.RootElement;
            if (!root.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("ffprobe returned no stream information.");

            JsonElement? video = null;
            JsonElement? audio = null;
            foreach (var stream in streams.EnumerateArray())
            {
                if (!stream.TryGetProperty("codec_type", out var type)) continue;
                if (type.GetString() == "video" && video is null) video = stream;
                if (type.GetString() == "audio" && audio is null) audio = stream;
            }

            if (video is null)
                throw new InvalidOperationException("No video stream was found in this file.");

            root.TryGetProperty("format", out var format);
            var duration = ParseDouble(format, "duration");
            if (duration <= 0) duration = ParseDouble(video.Value, "duration");

            var fps = ParseRate(video.Value, "avg_frame_rate");
            if (fps <= 0) fps = ParseRate(video.Value, "r_frame_rate");

            var width = GetInt(video.Value, "width");
            var height = GetInt(video.Value, "height");
            if (width <= 0 || height <= 0)
                throw new InvalidOperationException("The video dimensions could not be determined safely.");
            if (duration <= 0 || double.IsNaN(duration) || double.IsInfinity(duration))
                throw new InvalidOperationException("The video duration could not be determined safely.");

            return new VideoInfo
            {
                Path = fullPath,
                Duration = TimeSpan.FromSeconds(duration),
                Width = width,
                Height = height,
                Fps = double.IsFinite(fps) && fps > 0 ? fps : 0,
                VideoCodec = GetString(video.Value, "codec_name", "unknown"),
                HasAudio = audio is not null,
                AudioCodec = audio is null ? "none" : GetString(audio.Value, "codec_name", "unknown"),
                FileSize = new FileInfo(fullPath).Length
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException($"ffprobe did not finish within {ProbeTimeout.TotalSeconds:0} seconds. The file may be damaged or unusually complex.");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        catch (JsonException ex)
        {
            TryKill(process);
            throw new InvalidOperationException("ffprobe returned malformed metadata for this video.", ex);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort only; the process may have already exited.
        }
    }

    private static string TrimForMessage(string text)
    {
        text = (text ?? string.Empty).Trim();
        const int max = 4000;
        return text.Length <= max ? text : text[^max..];
    }

    private static int GetInt(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : 0;

    private static string GetString(JsonElement e, string name, string fallback) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v.GetString() ?? fallback : fallback;

    private static double ParseDouble(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var numeric)) return numeric;
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var text)) return text;
        return 0;
    }

    private static double ParseRate(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return 0;
        var s = v.GetString();
        if (string.IsNullOrWhiteSpace(s)) return 0;
        var parts = s.Split('/');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var a) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var b) && b != 0)
            return a / b;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0;
    }

    private static void Add(ProcessStartInfo psi, params string[] args)
    {
        foreach (var arg in args) psi.ArgumentList.Add(arg);
    }
}
