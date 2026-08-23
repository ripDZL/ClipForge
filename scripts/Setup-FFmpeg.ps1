[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$Root = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $Root 'src\ClipForge'
$Tools = Join-Path $Project 'Tools\ffmpeg'
$Bin = Join-Path $Tools 'bin'
$Ffmpeg = Join-Path $Bin 'ffmpeg.exe'
$Ffprobe = Join-Path $Bin 'ffprobe.exe'
$SourceMarker = Join-Path $Tools 'SOURCE.txt'

# Pinned release: never execute an unverified moving "latest" archive.
$FfmpegVersion = '9.0.1'
$ArchiveName = "ffmpeg-$FfmpegVersion-essentials_build.zip"
$Url = "https://www.gyan.dev/ffmpeg/builds/packages/$ArchiveName"
$ExpectedSha256 = 'fec81ae03971d9dd4be3ebe02e263bd2ec1d789483f931bdba5f5715e65da2e9'

Write-Host "ClipForge - FFmpeg Setup (pinned $FfmpegVersion)" -ForegroundColor Cyan

function Test-SecureExistingInstall {
    if (-not (Test-Path $Ffmpeg) -or -not (Test-Path $Ffprobe) -or -not (Test-Path $SourceMarker)) { return $false }
    try {
        $marker = Get-Content -LiteralPath $SourceMarker -Raw
        if (-not (($marker -match [regex]::Escape($ExpectedSha256)) -and ($marker -match [regex]::Escape($ArchiveName)))) { return $false }

        $ffMatch = [regex]::Match($marker, '(?im)^ffmpeg\.exe SHA-256:\s*([0-9a-f]{64})\s*$')
        $fpMatch = [regex]::Match($marker, '(?im)^ffprobe\.exe SHA-256:\s*([0-9a-f]{64})\s*$')
        if (-not $ffMatch.Success -or -not $fpMatch.Success) { return $false }

        $actualFfmpeg = (Get-FileHash -LiteralPath $Ffmpeg -Algorithm SHA256).Hash.ToLowerInvariant()
        $actualFfprobe = (Get-FileHash -LiteralPath $Ffprobe -Algorithm SHA256).Hash.ToLowerInvariant()
        return (($actualFfmpeg -eq $ffMatch.Groups[1].Value.ToLowerInvariant()) -and ($actualFfprobe -eq $fpMatch.Groups[1].Value.ToLowerInvariant()))
    }
    catch { return $false }
}

if (Test-SecureExistingInstall) {
    Write-Host "Verified pinned FFmpeg setup already present: $Bin" -ForegroundColor Green
    & $Ffmpeg -hide_banner -version | Select-Object -First 1
    exit 0
}

$Temp = Join-Path $env:TEMP ('ClipForge-FFmpeg-' + [guid]::NewGuid().ToString('N'))
$Zip = Join-Path $Temp $ArchiveName
$Extract = Join-Path $Temp 'extract'
New-Item -ItemType Directory -Force -Path $Temp, $Extract, $Bin | Out-Null

try {
    Write-Host "Downloading pinned FFmpeg $FfmpegVersion..."
    $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest -UseBasicParsing -Uri $Url -OutFile $Zip

    $ActualSha256 = (Get-FileHash -LiteralPath $Zip -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($ActualSha256 -ne $ExpectedSha256) {
        throw "FFmpeg archive SHA-256 mismatch. Expected $ExpectedSha256 but received $ActualSha256. Nothing was installed."
    }
    Write-Host 'SHA-256 verified.' -ForegroundColor Green

    # Validate archive paths before Expand-Archive to prevent path traversal if a
    # compromised/malformed ZIP is ever served despite the checksum guard.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($Zip)
    try {
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName.Replace('\', '/')
            if ([System.IO.Path]::IsPathRooted($entry.FullName) -or
                $name.StartsWith('/') -or
                $name -match '^[A-Za-z]:' -or
                $name -match '(^|/)\.\.(/|$)') {
                throw "Unsafe path found in FFmpeg archive: $($entry.FullName)"
            }
        }
    }
    finally {
        $archive.Dispose()
    }

    Write-Host 'Extracting verified archive...'
    Expand-Archive -LiteralPath $Zip -DestinationPath $Extract -Force

    $ff = Get-ChildItem -LiteralPath $Extract -Filter ffmpeg.exe -Recurse -File | Select-Object -First 1
    $fp = Get-ChildItem -LiteralPath $Extract -Filter ffprobe.exe -Recurse -File | Select-Object -First 1
    if (-not $ff -or -not $fp) { throw 'Verified archive did not contain ffmpeg.exe and ffprobe.exe.' }

    Copy-Item -LiteralPath $ff.FullName -Destination $Ffmpeg -Force
    Copy-Item -LiteralPath $fp.FullName -Destination $Ffprobe -Force

    $license = Get-ChildItem -LiteralPath $Extract -File -Recurse | Where-Object { $_.Name -match '^LICENSE(?:\.txt)?$' } | Select-Object -First 1
    $readme = Get-ChildItem -LiteralPath $Extract -File -Recurse | Where-Object { $_.Name -ieq 'README.txt' } | Select-Object -First 1
    if ($license) { Copy-Item -LiteralPath $license.FullName -Destination (Join-Path $Tools 'LICENSE.txt') -Force }
    if ($readme) { Copy-Item -LiteralPath $readme.FullName -Destination (Join-Path $Tools 'README.txt') -Force }

    $InstalledFfmpegSha256 = (Get-FileHash -LiteralPath $Ffmpeg -Algorithm SHA256).Hash.ToLowerInvariant()
    $InstalledFfprobeSha256 = (Get-FileHash -LiteralPath $Ffprobe -Algorithm SHA256).Hash.ToLowerInvariant()

    @"
ClipForge FFmpeg dependency record
Archive: $ArchiveName
Version: $FfmpegVersion
Source: $Url
Archive SHA-256: $ExpectedSha256
ffmpeg.exe SHA-256: $InstalledFfmpegSha256
ffprobe.exe SHA-256: $InstalledFfprobeSha256
Build provider: gyan.dev
Provider states its static release builds are GPLv3. Review the included license/readme and FFmpeg licensing requirements before redistributing binaries.
"@ | Set-Content -LiteralPath $SourceMarker -Encoding UTF8

    Write-Host "Installed verified FFmpeg tools to: $Bin" -ForegroundColor Green
    & $Ffmpeg -hide_banner -version | Select-Object -First 1
}
finally {
    Remove-Item -LiteralPath $Temp -Recurse -Force -ErrorAction SilentlyContinue
}
