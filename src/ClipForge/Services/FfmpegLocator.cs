using System;
using System.IO;

namespace ClipForge.Services;

public static class FfmpegLocator
{
    public static string? FindFfmpeg() => FindBundledTool("ffmpeg.exe");
    public static string? FindFfprobe() => FindBundledTool("ffprobe.exe");

    private static string? FindBundledTool(string fileName)
    {
        // Security boundary: ClipForge intentionally does not execute ffmpeg/ffprobe
        // from the process working directory or PATH. A bundled, app-local tool is
        // required so an unrelated executable earlier in PATH cannot be substituted.
        var baseDir = AppContext.BaseDirectory;
        string[] candidates =
        {
            Path.Combine(baseDir, "Tools", "ffmpeg", "bin", fileName),
            Path.Combine(baseDir, "ffmpeg", "bin", fileName),
            Path.Combine(baseDir, fileName)
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        return null;
    }
}
