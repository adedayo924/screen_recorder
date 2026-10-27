param(
    [string]$Version = "0.1.0",
    [switch]$SkipZip,
    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$distRoot = Join-Path $root "dist"
$publishDirectory = Join-Path $distRoot "publish"
$ffmpegSource = Join-Path $root "tools\ffmpeg\ffmpeg-n8.1-latest-win64-lgpl-8.1\bin\ffmpeg.exe"
$ffmpegLicense = Join-Path $root "tools\ffmpeg\ffmpeg-n8.1-latest-win64-lgpl-8.1\LICENSE.txt"
$project = Join-Path $root "src\Jeilo.App\Jeilo.App.csproj"

if (-not (Test-Path -LiteralPath $ffmpegSource)) {
    throw "FFmpeg was not found at $ffmpegSource"
}

$dotnet = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"
if (-not (Test-Path -LiteralPath $dotnet)) {
    $dotnet = "dotnet"
}

New-Item -ItemType Directory -Force -Path $distRoot | Out-Null

Write-Host "Publishing Jeilo Recorder $Version (win-x64, self-contained)..."
if (Test-Path -LiteralPath $publishDirectory) {
    Remove-Item -LiteralPath $publishDirectory -Recurse -Force
}

& $dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -o $publishDirectory `
    -p:Version=$Version `
    -p:InformationalVersion=$Version `
    -p:DebugType=None `
    -p:DebugSymbols=false

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

Write-Host "Removing debug symbols..."
Get-ChildItem -LiteralPath $publishDirectory -Filter "*.pdb" -Recurse -File |
    Remove-Item -Force

Write-Host "Bundling FFmpeg..."
Copy-Item -LiteralPath $ffmpegSource -Destination (Join-Path $publishDirectory "ffmpeg.exe") -Force

if (Test-Path -LiteralPath $ffmpegLicense) {
    Copy-Item -LiteralPath $ffmpegLicense `
        -Destination (Join-Path $publishDirectory "FFmpeg-LICENSE.txt") -Force
}

$files = Get-ChildItem -LiteralPath $publishDirectory -Recurse -File
$sizeMb = [math]::Round((($files | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Host "Publish output: $publishDirectory"
Write-Host "Files: $($files.Count)  Size: $sizeMb MB"

if (-not $SkipZip) {
    $zipPath = Join-Path $distRoot "JeiloRecorder-$Version-win-x64.zip"
    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }

    Write-Host "Creating portable ZIP..."
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $publishDirectory,
        $zipPath,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false)
    Write-Host "ZIP: $zipPath"
}

if (-not $SkipInstaller) {
    $isccCandidates = @(
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
    )

    $iscc = $isccCandidates |
        Where-Object { $_ -and (Test-Path -LiteralPath $_) } |
        Select-Object -First 1

    if (-not $iscc) {
        $command = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
        if ($command) {
            $iscc = $command.Source
        }
    }

    if ($iscc) {
        $iss = Join-Path $root "installer\JeiloRecorder.iss"
        Write-Host "Building installer with $iscc..."
        & $iscc "/DAppVersion=$Version" $iss

        if ($LASTEXITCODE -ne 0) {
            throw "Inno Setup failed with exit code $LASTEXITCODE"
        }
    }
    else {
        Write-Host "Inno Setup was not found; skipped installer. Run installer\JeiloRecorder.iss with ISCC to build it."
    }
}
