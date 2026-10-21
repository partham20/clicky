<#
.SYNOPSIS
  Publishes Clicky (self-contained, win-x64) and packs it with Velopack.

.DESCRIPTION
  Output goes to -OutputDir (default artifacts\releases): Clicky-win-Setup.exe, the full and delta
  .nupkg files, releases.win.json and a portable zip. Put earlier releases in -OutputDir first
  (vpk download) to get delta packages. Used by .github/workflows/release.yml and for local tests.

.EXAMPLE
  .\build\pack.ps1 -Version 0.2.0
#>
param(
    [Parameter(Mandatory)] [string] $Version,
    [string] $OutputDir,
    [string] $Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$OutputDir = if ($OutputDir) { [IO.Path]::GetFullPath($OutputDir) } else { Join-Path $root 'artifacts\releases' }
$publishDir = Join-Path $root "artifacts\publish\$Runtime"

if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
New-Item -ItemType Directory -Force $OutputDir | Out-Null

# Self-contained: no .NET runtime install (and no UAC prompt) on a fresh PC.
dotnet publish (Join-Path $root 'src\Clicky.App\Clicky.App.csproj') `
    -c Release -r $Runtime --self-contained true -o $publishDir `
    -p:Version=$Version -p:DebugType=embedded -p:SatelliteResourceLanguages=en
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

# Whisper.net copies every platform's native libraries (Linux Vulkan alone is 56 MB); keep ours.
Get-ChildItem (Join-Path $publishDir 'runtimes') -Directory -Recurse |
    Where-Object { $_.Name -match '^(win|linux|osx|macos|android|ios|tvos|maccatalyst|browser)-' -and $_.Name -ne $Runtime } |
    Remove-Item -Recurse -Force

Push-Location $root
try {
    dotnet tool restore | Out-Null
    # The pack id is the install folder (%LocalAppData%\ClickyApp) and must never change, or installed
    # copies stop updating. Not "Clicky": Setup empties its folder, and %LocalAppData%\Clicky holds
    # the downloaded speech models.
    dotnet vpk pack `
        --packId ClickyApp `
        --packVersion $Version `
        --packTitle Clicky `
        --packAuthors Clicky `
        --packDir $publishDir `
        --mainExe Clicky.exe `
        --icon (Join-Path $root 'src\Clicky.App\Assets\clicky.ico') `
        --runtime $Runtime `
        --outputDir $OutputDir
    if ($LASTEXITCODE -ne 0) { throw "vpk pack failed ($LASTEXITCODE)" }
}
finally {
    Pop-Location
}
