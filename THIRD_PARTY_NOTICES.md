# Third-Party Notices

ClipForge relies on third-party software. This file is informational and is not a substitute for reviewing the exact license files shipped by each dependency.

## LibVLCSharp.WPF

- Package: `LibVLCSharp.WPF` 3.10.1
- Project: VideoLAN / LibVLCSharp
- NuGet package metadata identifies the license as LGPL-2.1-or-later.

## VideoLAN.LibVLC.Windows

- Package: `VideoLAN.LibVLC.Windows` 3.0.23.1
- Project: VideoLAN / VLC
- The package contains the native LibVLC runtime used for preview.
- Review the license files included by the package before redistributing binaries.

## FFmpeg / ffprobe

ClipForge's setup script currently downloads the pinned Gyan Windows essentials build of FFmpeg 9.0.2 and records the archive source and SHA-256 in `Tools/ffmpeg/SOURCE.txt`.

Gyan's build page states that its static builds are GPLv3. Binary redistributors are responsible for satisfying the applicable FFmpeg/build licensing terms, including providing required notices/source information where applicable. ClipForge invokes FFmpeg as a separate process; this notice does not make a legal determination about license compatibility for a particular distribution model.

## .NET

ClipForge targets Microsoft .NET 8 / WPF and publishes a self-contained runtime for Windows x64.

## Project license

ClipForge itself is licensed under the GNU General Public License v3.0 (GPL-3.0-only). Third-party components retain their own applicable license terms.
