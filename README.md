# ClipForge

**Cut. Shape. Finish.**

ClipForge is a lightweight Windows video editor designed for fast, phone-style editing without the overhead of a full nonlinear editor. Open or drop a local video, make common edits, preview picture changes live, and export.

> Current development version: **0.4.2**

## Current features

- Drag-and-drop or **Open Video**
- LibVLC-based local preview
- Play/pause, seek, and ±5-second jumps
- Start/end trim controls with Mark Start / Mark End
- Crop presets: 16:9, 9:16, 1:1, 4:3
- Draw a custom crop region directly over the preview
- Rotate 90° left/right/180°
- Horizontal and vertical flip
- Mute/remove audio
- Volume 0–200%
- Brightness, contrast, and saturation with **live preview**
- Double-click editable sliders to reset to defaults
- Original/4K/1440p/1080p/720p output presets
- Original/60/30/24 FPS presets
- Automatic H.264, NVIDIA H.264, NVIDIA HEVC, NVIDIA AV1, or CPU H.264
- Quality presets for speed, balanced use, maximum quality, and smaller files
- Smart stream-copy when re-encoding is unnecessary
- Optional **metadata / EXIF privacy scrub** on export
- FFmpeg export progress and cancellation
- Source-overwrite protection and atomic final-output replacement
- High-contrast dark UI, custom dropdowns, and readable tooltips

## Security baseline

Version 0.4.2 hardens the media and build pipeline:

- FFmpeg and ffprobe are executed **only from ClipForge's app-local tools directory**, never from the working directory or `PATH`.
- Native DLL search is hardened before LibVLC initialization so the working directory/PATH are excluded from implicit DLL resolution.
- All FFmpeg/ffprobe command arguments use `ProcessStartInfo.ArgumentList`; ClipForge does not build a shell command from filenames or UI values.
- Local input is restricted to a defined video-extension list.
- FFmpeg/ffprobe input protocols are allow-listed to `file,crypto,data` to prevent network retrieval through media input URLs.
- LibVLC preview starts with network metadata fetching disabled.
- ffprobe and encoder detection have timeouts and process-tree cancellation.
- FFmpeg setup pins version **9.0.2**, verifies the vendor-published SHA-256, and validates ZIP paths before extraction.
- NuGet audit checks direct and transitive packages; high/critical advisories fail the build.
- Dependabot checks NuGet and GitHub Actions dependencies weekly.
- The app manifest requests normal-user `asInvoker` privileges only.


## Build on Windows 10/11

1. Install **.NET 8 SDK 8.0.423 or newer in the 8.x line** from Microsoft.
2. Clone or download this repository.
3. Double-click `Build.bat`.
4. On the first build, ClipForge downloads the pinned FFmpeg Essentials archive, verifies its SHA-256, then extracts it.
5. NuGet restores and audits LibVLCSharp/native LibVLC packages.
6. A self-contained Windows x64 build is produced at:

```text
publish\ClipForge-win-x64\ClipForge.exe
```

A distributable ZIP and checksum are produced at:

```text
publish\ClipForge-win-x64.zip
publish\ClipForge-win-x64.zip.sha256
```

### Why the SDK is no longer auto-installed

Earlier development builds downloaded and executed Microsoft's `dotnet-install.ps1` automatically. The audited build deliberately removes that behavior: bootstrap scripts should not silently download and execute a moving remote PowerShell program.

The project pins the current security-patched .NET 8 SDK baseline in `global.json`. `.NET 8` reaches end of support in November 2026, so migrating ClipForge to .NET 10 LTS is on the near-term maintenance list.

## Run from source

After the SDK is installed, use:

```text
Run-Source.bat
```

The script securely provisions the pinned FFmpeg dependency if needed.

## Privacy scrub

Video containers commonly store identifying information as container/stream metadata rather than photographic EXIF alone. When **Scrub metadata / EXIF from exported video** is enabled, ClipForge disables source global metadata, video/audio stream metadata, and chapters and clears common location/creation aliases.

FFmpeg may still write ordinary technical muxer fields describing the output format/encoder. Those are generated for the new file rather than copied private source metadata.

Metadata removal can still use Smart Fast Copy when no video processing requires re-encoding.

## Smart Fast Copy

When no video filters or audio-volume processing are required, ClipForge can copy the compressed stream rather than re-encode it. This makes operations such as removing audio or metadata very fast.

A trim made with Fast Copy may align to codec keyframes. Disable Fast Copy when frame-accurate trim points matter more than export speed.

## Source layout

```text
ClipForge.sln
Directory.Build.props
global.json
NuGet.config
LICENSE
THIRD_PARTY_NOTICES.md
src/
  ClipForge/
    App.xaml
    MainWindow.xaml
    MainWindow.xaml.cs
    Models/
    Services/
scripts/
  Build-ClipForge.ps1
  Setup-FFmpeg.ps1
.github/
  dependabot.yml
  workflows/
    windows-build.yml
    security-audit.yml
```

## Third-party components

ClipForge uses FFmpeg/ffprobe, LibVLCSharp/LibVLC, and .NET/WPF. See [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md).

The current Gyan static FFmpeg build is identified by its provider as GPLv3. Review the exact third-party licensing obligations before distributing ClipForge binaries containing that build.

## License

ClipForge is licensed under the **GNU General Public License v3.0 (GPL-3.0-only)**. See [`LICENSE`](LICENSE) for the full license text.

GPLv3 is a strong copyleft license: redistribution and modified versions must preserve the GPL terms and provide corresponding source as required by the license.
