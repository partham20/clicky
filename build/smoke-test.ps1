<#
.SYNOPSIS
  Installs, updates and uninstalls a real Velopack build of Clicky, silently, and checks each step.

.DESCRIPTION
  Packs 0.0.1 and 0.0.2 into one local feed, installs 0.0.1 with Setup --silent, lets the
  installed app's UpdateService find and download 0.0.2 from that feed (settings.updates.feedUrl),
  restarts it so Velopack applies the update at startup, then uninstalls with the quiet uninstall
  command from Apps & features. Along the way it checks the Start-with-Windows Run entry points at
  the stable root stub and survives the update, and that uninstall removes it.

  The app runs with CLICKY_DATA_DIR set, so it never touches %AppData%\Clicky. It refuses to run if
  ClickyApp is already installed for this user, because Setup would replace that install. Meant for
  the Installer workflow on windows-latest; it also runs on a spare Windows box or VM.

.EXAMPLE
  .\build\smoke-test.ps1
#>
param(
    [string] $WorkDir
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$WorkDir = if ($WorkDir) { [IO.Path]::GetFullPath($WorkDir) } else { Join-Path $root 'artifacts\smoke' }
$feed = Join-Path $WorkDir 'feed'
$data = Join-Path $WorkDir 'data'
$installRoot = Join-Path $env:LOCALAPPDATA 'ClickyApp'
$stub = Join-Path $installRoot 'Clicky.exe'
$currentExe = Join-Path $installRoot 'current\Clicky.exe'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ClickyApp'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$expectedRun = "`"$stub`" --autostart"

function Step([string] $Text) { Write-Host "`n== $Text" -ForegroundColor Cyan }

function Wait-Until([scriptblock] $Condition, [int] $Seconds, [string] $What) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while (-not (& $Condition)) {
        if ((Get-Date) -gt $deadline) { throw "Timed out after $Seconds s waiting for: $What" }
        Start-Sleep -Milliseconds 500
    }
    Write-Host "ok: $What"
}

function Assert([bool] $Condition, [string] $What) {
    if (-not $Condition) { throw "Failed: $What" }
    Write-Host "ok: $What"
}

function Installed-Version {
    if (-not (Test-Path $currentExe)) { return $null }
    return (Get-Item $currentExe).VersionInfo.FileVersion
}

function Run-Entry { (Get-ItemProperty $runKey -ErrorAction SilentlyContinue).Clicky }

function Show-Clicky {
    Get-CimInstance Win32_Process -Filter "Name = 'Clicky.exe' OR Name = 'Update.exe'" |
        Format-Table ProcessId, ParentProcessId, CreationDate, CommandLine -AutoSize -Wrap | Out-String | Write-Host
}

# Kills on every pass: right after an update Velopack's updater starts the new version once more.
function Stop-Clicky {
    try {
        Wait-Until {
            Get-Process Clicky -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
            Start-Sleep -Milliseconds 500
            -not (Get-Process Clicky -ErrorAction SilentlyContinue)
        } 30 'Clicky stopped'
    }
    catch {
        Show-Clicky
        throw
    }
}

function Start-Clicky { Start-Process -FilePath $stub -WorkingDirectory $installRoot | Out-Null }

if (Test-Path $currentExe) { throw "ClickyApp is already installed in $installRoot; uninstall it first." }
if (Test-Path $WorkDir) { Remove-Item -Recurse -Force $WorkDir }
New-Item -ItemType Directory -Force $feed, $data | Out-Null

Step 'Pack 0.0.1 and 0.0.2 into one feed'
& (Join-Path $PSScriptRoot 'pack.ps1') -Version 0.0.1 -OutputDir $feed
$setup = Get-ChildItem $feed -Filter '*-Setup.exe' | Select-Object -First 1
Assert ($null -ne $setup) 'vpk wrote a Setup.exe'
$setup001 = Join-Path $WorkDir "0.0.1-$($setup.Name)"
Copy-Item $setup.FullName $setup001
& (Join-Path $PSScriptRoot 'pack.ps1') -Version 0.0.2 -OutputDir $feed
Get-ChildItem $feed | Format-Table Name, Length -AutoSize | Out-String | Write-Host
Assert (Test-Path (Join-Path $feed 'ClickyApp-0.0.2-delta.nupkg')) '0.0.2 has a delta package against 0.0.1'

# Cloud speech-to-text: no Whisper model download or GPU warm-up on a CI runner.
@{
    general = @{ startWithWindows = $true }
    voice   = @{ engine = 'Cloud' }
    updates = @{ feedUrl = $feed }
} | ConvertTo-Json | Set-Content -Encoding utf8 (Join-Path $data 'settings.json')
$env:CLICKY_DATA_DIR = $data

Step 'Install 0.0.1 silently'
$p = Start-Process -FilePath $setup001 -ArgumentList '--silent' -Wait -PassThru
Assert ($p.ExitCode -eq 0) "Setup exited with 0 (got $($p.ExitCode))"
Assert (Test-Path $stub) 'root launcher stub exists'
Assert (Test-Path (Join-Path $installRoot 'Update.exe')) 'Update.exe exists'
Assert ((Installed-Version) -eq '0.0.1.0') "current\Clicky.exe is 0.0.1.0 (got $(Installed-Version))"
Assert (Test-Path $uninstallKey) 'Apps & features entry exists'

# Setup starts the app itself; restart it so this copy surely has CLICKY_DATA_DIR.
Start-Sleep -Seconds 5
Stop-Clicky
Start-Clicky
Wait-Until { (Run-Entry) -eq $expectedRun } 60 "Run entry is $expectedRun"

Step 'Find and download 0.0.2 in the background'
$downloaded = Join-Path $installRoot 'packages\ClickyApp-0.0.2-full.nupkg'
Wait-Until { Test-Path $downloaded } 300 'UpdateService downloaded ClickyApp-0.0.2-full.nupkg'
Assert ($null -ne (Get-Process Clicky -ErrorAction SilentlyContinue)) 'Clicky still running after the download'
Assert ((Installed-Version) -eq '0.0.1.0') 'nothing applied before a restart'

Step 'Restart: Velopack applies the waiting update at startup'
Stop-Clicky
Start-Clicky
Wait-Until { (Installed-Version) -eq '0.0.2.0' } 120 'current\Clicky.exe is 0.0.2.0'
Wait-Until { $null -ne (Get-Process Clicky -ErrorAction SilentlyContinue) } 60 'Clicky 0.0.2 running'
Wait-Until { Select-String -Path (Join-Path $data 'logs\*') -Pattern 'Clicky 0\.0\.2' -Quiet } 60 'log says Clicky 0.0.2 started'
Assert ((Run-Entry) -eq $expectedRun) 'Run entry still points at the stub after the update'

Step 'Uninstall silently'
Show-Clicky
Stop-Clicky
$entry = Get-ItemProperty $uninstallKey
$command = if ($entry.QuietUninstallString) { $entry.QuietUninstallString } else { "$($entry.UninstallString) --silent" }
Write-Host "uninstall command: $command"
if ($command -notmatch '^"([^"]+)"\s*(.*)$') { throw "Unexpected uninstall command: $command" }
$p = Start-Process -FilePath $Matches[1] -ArgumentList $Matches[2] -Wait -PassThru
Assert ($p.ExitCode -eq 0) "uninstall exited with 0 (got $($p.ExitCode))"
Wait-Until { -not (Test-Path $currentExe) } 60 'installed files removed'
Assert (-not (Test-Path $uninstallKey)) 'Apps & features entry removed'
Assert ($null -eq (Run-Entry)) 'Run entry removed'
Assert (Test-Path (Join-Path $data 'settings.json')) 'user settings kept'

Write-Host "`nInstall, update and uninstall all passed." -ForegroundColor Green
