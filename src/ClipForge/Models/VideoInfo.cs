using System;

namespace ClipForge.Models;

public sealed class VideoInfo
{
    public string Path { get; init; } = string.Empty;
    public TimeSpan Duration { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public double Fps { get; init; }
    public string VideoCodec { get; init; } = "unknown";
    public bool HasAudio { get; init; }
    public string AudioCodec { get; init; } = "none";
    public long FileSize { get; init; }

    public string Summary => $"{Width}×{Height}  •  {Fps:0.##} FPS  •  {VideoCodec.ToUpperInvariant()}" +
                             (HasAudio ? $" + {AudioCodec.ToUpperInvariant()}" : " • no audio");
}
