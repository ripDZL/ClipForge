# Changelog

## 0.4.1 — security/code audit

- Restricted FFmpeg/ffprobe discovery to bundled app-local executables; removed working-directory/PATH execution fallback.
- Hardened native DLL resolution so the process working directory/PATH are excluded from implicit DLL search.
- Pinned FFmpeg Essentials 9.0.1 and verify its vendor SHA-256 before extraction.
- Added ZIP path-traversal validation to the FFmpeg bootstrap.
- Removed automatic download/execution of the moving `dotnet-install.ps1` bootstrap.
- Pinned the patched .NET 8 SDK baseline to 8.0.423.
- Enabled direct/transitive NuGet vulnerability auditing; high/critical advisories fail builds.
- Added weekly dependency audit and Dependabot configuration.
- Added ffprobe and encoder-detection timeouts/process-tree cancellation.
- Restricted media subprocess input protocols to local `file,crypto,data`.
- Disabled LibVLC network metadata access for previews.
- Added supported-local-video extension validation for dialogs, command-line input, and drag/drop.
- Changed export to a temporary sibling file followed by final replacement, protecting existing output from failed/cancelled exports.
- Restricted exports to MP4/MKV and hardened odd-dimension encode handling.
- Expanded privacy scrub to global, per-video, per-audio metadata, chapters, and common private aliases.
- Hardened Explorer invocation with structured argument passing.
- Added `THIRD_PARTY_NOTICES.md`, deterministic/analyzer settings, and output SHA-256 generation.

## 0.4.0

- Renamed the application and project from QuickVideo to **ClipForge**.
- Added repository-ready GitHub metadata and Windows build workflow.
- Retains the v0.3.1 UI/readability improvements, live picture preview, slider double-click reset behavior, and metadata/privacy scrub.

## 0.3.1

- Added global high-contrast dark tooltip styling.

## 0.3.0

- Added custom high-contrast dropdown styling.
- Added live brightness/contrast/saturation preview through LibVLC.
- Added double-click slider reset behavior.
- Added optional metadata / EXIF privacy scrub.

## 0.2.0

- Major dark-theme/readability redesign.
- Improved crop controls, editing cards, status area, and transport controls.

## 0.1.x

- Initial functional editor, FFmpeg export pipeline, LibVLC preview, and build bootstrap.
