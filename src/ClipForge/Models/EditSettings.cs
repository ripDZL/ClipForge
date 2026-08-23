using System;

namespace ClipForge.Models;

public sealed class EditSettings
{
    public TimeSpan Start { get; set; }
    public TimeSpan End { get; set; }
    public bool Mute { get; set; }
    public double Volume { get; set; } = 1.0;
    public string CropMode { get; set; } = "None";
    public int CropX { get; set; }
    public int CropY { get; set; }
    public int CropWidth { get; set; }
    public int CropHeight { get; set; }
    public string Rotation { get; set; } = "None";
    public bool FlipHorizontal { get; set; }
    public bool FlipVertical { get; set; }
    public string Resolution { get; set; } = "Original";
    public string FrameRate { get; set; } = "Original";
    public double Brightness { get; set; }
    public double Contrast { get; set; } = 1.0;
    public double Saturation { get; set; } = 1.0;
    public string Encoder { get; set; } = "Automatic (H.264)";
    public string Quality { get; set; } = "Recommended";
    public bool SmartFastCopy { get; set; } = true;
    public bool ScrubMetadata { get; set; }
}
