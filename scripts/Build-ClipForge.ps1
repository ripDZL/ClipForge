[CmdletBinding()]
param(
    [switch]$SkipFFmpeg
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $Root 'src\ClipForge\ClipForge.csproj'
$NuGetConfig = Join-Path $Root 'NuGet.config'
$PublishRoot = Join-Path $Root 'publish'
$Publish = Join-Path $PublishRoot 'ClipForge-win-x64'
$BuildLog = Join-Path $Root 'build-last.log'
$MinimumSdk = [Version]'8.0.423'

Write-Host 'ClipForge v0.4.2 - Audited Build + Publish' -ForegroundColor Cyan

function Get-CompatibleDotNet8Sdk([string]$DotnetExe) {
    if (-not $DotnetExe -or -not (Test-Path -LiteralPath $DotnetExe)) { return $null }
    try {
        $versions = @()
        foreach ($line in (& $DotnetExe --list-sdks 2>$null)) {
            if ($line -match '^(8\.\d+\.\d+)') {
                try { $versions += [Version]$Matches[1] } catch { }
            }
        }
        return $versions | Where-Object { $_ -ge $MinimumSdk } | Sort-Object -Descending | Select-Object -First 1
    }
    catch { return $null }
}

$dotnetCmd = Get-Command dotnet.exe -ErrorAction SilentlyContinue
$DotnetPath = if ($dotnetCmd) { $dotnetCmd.Source } else { $null }
$Sdk = Get-CompatibleDotNet8Sdk $DotnetPath
if (-not $Sdk) {
    $candidate = 'C:\Program Files\dotnet\dotnet.exe'
    $candidateSdk = Get-CompatibleDotNet8Sdk $candidate
    if ($candidateSdk) { $DotnetPath = $candidate; $Sdk = $candidateSdk }
}

if (-not $Sdk) {
    throw @"
A security-patched .NET 8 SDK was not found.
ClipForge v0.4.2 requires .NET 8 SDK $MinimumSdk or newer in the 8.x line.
Install the current .NET 8 SDK from Microsoft, then run Build.bat again.
The build intentionally no longer downloads and executes a remote installer script.
"@
}

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_UI_LANGUAGE = 'en-US'
Set-Location -LiteralPath $Root

Write-Host "Using .NET SDK: $Sdk" -ForegroundColor Green

if (-not $SkipFFmpeg) {
    & (Join-Path $PSScriptRoot 'Setup-FFmpeg.ps1')
}

if (Test-Path -LiteralPath $Publish) { Remove-Item -LiteralPath $Publish -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Publish | Out-Null
Remove-Item -LiteralPath $BuildLog -Force -ErrorAction SilentlyContinue

function Invoke-DotNetStep([string]$Name, [string[]]$Arguments) {
    Write-Host $Name
    $output = & $DotnetPath @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    $output | Tee-Object -FilePath $BuildLog -Append | ForEach-Object { Write-Host $_ }
    if ($exitCode -ne 0) { throw "$Name failed. See build-last.log." }
}

Invoke-DotNetStep 'Restoring NuGet packages with vulnerability audit...' @(
    'restore', $Project, '-r', 'win-x64', '--configfile', $NuGetConfig
)

Write-Host 'Dependency vulnerability report...'
$packageAudit = & $DotnetPath list $Project package --vulnerable --include-transitive --no-restore 2>&1
$packageAudit | Tee-Object -FilePath $BuildLog -Append | ForEach-Object { Write-Host $_ }
if ($LASTEXITCODE -ne 0) { throw 'NuGet vulnerability inspection failed. See build-last.log.' }

Invoke-DotNetStep 'Compiling Release x64...' @('build', $Project, '-c', 'Release', '-r', 'win-x64', '--no-restore')
Invoke-DotNetStep 'Publishing self-contained x64 build...' @(
    'publish', $Project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', $Publish,
    '--no-restore', '-p:PublishReadyToRun=false', '-p:DebugType=None', '-p:DebugSymbols=false'
)

$Exe = Join-Path $Publish 'ClipForge.exe'
$Ffmpeg = Join-Path $Publish 'Tools\ffmpeg\bin\ffmpeg.exe'
$Ffprobe = Join-Path $Publish 'Tools\ffmpeg\bin\ffprobe.exe'
$FfmpegSource = Join-Path $Publish 'Tools\ffmpeg\SOURCE.txt'
if (-not (Test-Path -LiteralPath $Exe)) { throw 'Publish completed but ClipForge.exe is missing.' }
if (-not (Test-Path -LiteralPath $Ffmpeg) -or -not (Test-Path -LiteralPath $Ffprobe)) { throw 'Publish completed but bundled FFmpeg tools are missing.' }
if (-not (Test-Path -LiteralPath $FfmpegSource)) { throw 'Publish completed but the FFmpeg dependency record is missing.' }

# Re-verify the packaged helper executables against the hashes recorded when the
# verified archive was installed. This also protects builds made with -SkipFFmpeg.
$FfmpegMarker = Get-Content -LiteralPath $FfmpegSource -Raw
$FfmpegHashMatch = [regex]::Match($FfmpegMarker, '(?im)^ffmpeg\.exe SHA-256:\s*([0-9a-f]{64})\s*$')
$FfprobeHashMatch = [regex]::Match($FfmpegMarker, '(?im)^ffprobe\.exe SHA-256:\s*([0-9a-f]{64})\s*$')
if (-not $FfmpegHashMatch.Success -or -not $FfprobeHashMatch.Success) { throw 'FFmpeg dependency record does not contain helper hashes.' }
$PublishedFfmpegHash = (Get-FileHash -LiteralPath $Ffmpeg -Algorithm SHA256).Hash.ToLowerInvariant()
$PublishedFfprobeHash = (Get-FileHash -LiteralPath $Ffprobe -Algorithm SHA256).Hash.ToLowerInvariant()
if ($PublishedFfmpegHash -ne $FfmpegHashMatch.Groups[1].Value.ToLowerInvariant()) { throw 'Published ffmpeg.exe failed integrity verification.' }
if ($PublishedFfprobeHash -ne $FfprobeHashMatch.Groups[1].Value.ToLowerInvariant()) { throw 'Published ffprobe.exe failed integrity verification.' }

$Zip = Join-Path $PublishRoot 'ClipForge-win-x64.zip'
$HashFile = "$Zip.sha256"
if (Test-Path -LiteralPath $Zip) { Remove-Item -LiteralPath $Zip -Force }
if (Test-Path -LiteralPath $HashFile) { Remove-Item -LiteralPath $HashFile -Force }
Compress-Archive -Path (Join-Path $Publish '*') -DestinationPath $Zip -CompressionLevel Optimal
$ZipHash = (Get-FileHash -LiteralPath $Zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$ZipHash  ClipForge-win-x64.zip" | Set-Content -LiteralPath $HashFile -Encoding ASCII

Write-Host ''
Write-Host 'BUILD COMPLETE' -ForegroundColor Green
Write-Host "App    : $Exe"
Write-Host "ZIP    : $Zip"
Write-Host "SHA256 : $ZipHash"
Write-Host "Log    : $BuildLog"
