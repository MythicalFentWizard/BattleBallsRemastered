# Builds the downloadable boxes into _revival\dist.
#
#   Server-win-x64     master + game server + operator console   (Windows)
#   Server-linux-x64   master only                               (Linux)
#   Client-win-x64     player box                                (Windows)
#
# BattleBit ships no Linux dedicated server and no Linux client, so Linux gets the
# master only - which is the useful half to put on a cheap VPS anyway. Windows machines
# run the game servers and register to it.
#
# Each box is ONE executable with the whole runtime bundled inside it, plus its config
# and two small folders. Published the ordinary way a box is ~340 files of .NET runtime
# DLLs, which buries the three files anyone actually opens. The DLLs cannot simply be
# moved into a subfolder: a self-contained executable loads its runtime from its own
# directory, and the code finds server.properties, accounts.json and bans.json next to
# the executable via Environment.ProcessPath - so moving it would drag the config in
# with it. Bundling keeps the data path correct with no code change.
#
# -FrameworkDependent drops the bundled runtime for a much smaller download that needs
# .NET 10 installed.

param([switch]$FrameworkDependent)

$ErrorActionPreference = "Stop"
$root  = $PSScriptRoot
$dist  = Join-Path $root "dist"
# Everything is assembled here first and only then moved into place. Building straight
# into dist meant a single locked file both failed the build AND left the previous boxes
# half-deleted, since each one is cleared before its publish. A staging folder is only
# ever written by this script, so nothing else can be holding a file in it.
$stage = Join-Path $dist ".build"

$label = if ($FrameworkDependent) { "framework-dependent (needs .NET 10)" } else { "self-contained" }
Write-Host "Building $label, single file per box..." -ForegroundColor Cyan

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory $stage -Force | Out-Null

$boxes = @(
    @{ Name = "Server-win-x64";   Project = "RevivalServer"; Rid = "win-x64"   },
    @{ Name = "Server-linux-x64"; Project = "RevivalServer"; Rid = "linux-x64" },
    @{ Name = "Client-win-x64";   Project = "Launcher";      Rid = "win-x64"   }
)

foreach ($box in $boxes) {
    Write-Host "  $($box.Name)"
    $out = "$stage\$($box.Name)"

    # Splatting an array of switches into a native exe mangles it on PowerShell 5.1,
    # so the two forms are spelled out.
    if ($FrameworkDependent) {
        dotnet publish "$root\$($box.Project)" -c Release -r $box.Rid --no-self-contained `
            -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
            -p:DebugType=none `
            -o $out --nologo -v q
    } else {
        dotnet publish "$root\$($box.Project)" -c Release -r $box.Rid --self-contained `
            -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
            -p:DebugType=none -p:EnableCompressionInSingleFile=true `
            -o $out --nologo -v q
    }
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $($box.Name)" }

    # The MasterServer project is a library that is also built as an exe, so publishing
    # anything that references it leaves its runtimeconfig behind. Nothing reads it.
    Remove-Item "$out\MasterServer.runtimeconfig.json" -Force -ErrorAction SilentlyContinue
}

# ---- the GUI, alongside the console server on Windows ----
# It is a front end for RevivalServer.exe, not a second server: it edits
# server.properties and runs that executable as a child process. Windows only, because
# it is WPF - the Linux box keeps the console build, which is the half that belongs on
# a VPS anyway.
Write-Host "  Server-win-x64 (GUI)"
$guiOut = "$stage\Server-win-x64"
if ($FrameworkDependent) {
    dotnet publish "$root\ServerLauncher" -c Release -r win-x64 --no-self-contained `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=none -o $guiOut --nologo -v q
} else {
    dotnet publish "$root\ServerLauncher" -c Release -r win-x64 --self-contained `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=none -p:EnableCompressionInSingleFile=true -o $guiOut --nologo -v q
}
if ($LASTEXITCODE -ne 0) { throw "publish failed for the server GUI" }
Remove-Item "$guiOut\MasterServer.runtimeconfig.json" -Force -ErrorAction SilentlyContinue

# ---- docs, in docs\ so the box root stays readable ----
$serverDocs = "SERVER-GUIDE.md", "COMMANDS.md", "TROUBLESHOOTING.md", "RAHNAMA-FA.md"
$clientDocs = "CLIENT-GUIDE.md", "COMMANDS.md", "TROUBLESHOOTING.md", "RAHNAMA-FA.md"

foreach ($box in "Server-win-x64", "Server-linux-x64") {
    New-Item -ItemType Directory "$stage\$box\docs" -Force | Out-Null
    foreach ($d in $serverDocs) { Copy-Item "$root\$d" "$stage\$box\docs\" -Force }
}
New-Item -ItemType Directory "$stage\Client-win-x64\docs" -Force | Out-Null
foreach ($d in $clientDocs) { Copy-Item "$root\$d" "$stage\Client-win-x64\docs\" -Force }

# ---- the patch scripts, in tools\ ----
# Only the Windows boxes can patch the game; the Linux box never touches it. Both
# scripts walk up to 8 parent folders looking for GameAssembly.dll, so tools\ inside a
# box inside the game folder still finds it. patch_languages.ps1 is not optional:
# without it a fresh install renders every string as "MISSING [n]", because the game
# still fetches its language files from a domain that no longer resolves.
foreach ($box in "Server-win-x64", "Client-win-x64") {
    New-Item -ItemType Directory "$stage\$box\tools" -Force | Out-Null
    foreach ($t in "patch_auth.ps1", "patch_languages.ps1") {
        Copy-Item "$root\$t" "$stage\$box\tools\" -Force
    }
}

# ---- start scripts ----
# Each one sets the working directory first, so server.properties, accounts.json and
# the logs land in the box rather than wherever a shortcut was launched from.
@'
@echo off
cd /d "%~dp0"
start "" RevivalServerLauncher.exe
'@ | Set-Content "$stage\Server-win-x64\Server Launcher.bat" -Encoding ascii

@'
@echo off
title BattleBit Revival Server
cd /d "%~dp0"
RevivalServer.exe
echo.
echo Server stopped. Press any key to close.
pause >nul
'@ | Set-Content "$stage\Server-win-x64\Start Server (console).bat" -Encoding ascii

# Built from an array so the LF endings are guaranteed; bash will not run a script
# with CRLF line endings.
$sh = @(
    '#!/usr/bin/env bash',
    '# BattleBit Revival - master server',
    'cd "$(dirname "$0")"',
    'chmod +x ./RevivalServer 2>/dev/null',
    './RevivalServer'
) -join "`n"
[IO.File]::WriteAllText("$stage\Server-linux-x64\start-server.sh", $sh + "`n")

@'
@echo off
cd /d "%~dp0"
start "" RevivalLauncher.exe --client
'@ | Set-Content "$stage\Client-win-x64\Start Client.bat" -Encoding ascii

@'
@echo off
cd /d "%~dp0"
start "" RevivalLauncher.exe
'@ | Set-Content "$stage\Client-win-x64\Start Client (host and play).bat" -Encoding ascii

# ---- nothing from a test run ships ----
foreach ($box in $boxes.Name) {
    Get-ChildItem "$stage\$box" -Include accounts.json, bans.json, bans.pending.json, `
        revival.json, anticheat.json, progression.json, guard.json, server.properties, `
        server_console.log, game_server.log, launcher_log.txt -Recurse -ErrorAction SilentlyContinue |
        Remove-Item -Force
}

# Ship a server.properties to edit, rather than making the owner start the server once
# before the file the guide tells them to open exists. Generated by the Windows build
# and copied, since the template is identical on both platforms.
& "$stage\Server-win-x64\RevivalServer.exe" --init | Out-Null
Copy-Item "$stage\Server-win-x64\server.properties" "$stage\Server-linux-x64\" -Force

@'
# BattleBit Revival - which folder do I want?

| I want to... | download |
|---|---|
| Play on someone's cluster | `Client-win-x64` |
| Host a cluster on my gaming PC | `Server-win-x64` (or the client box's "host and play") |
| Host a cluster on a Linux VPS | `Server-linux-x64` |

Nothing else needs installing - each folder runs on its own.

**Linux runs the master only.** BattleBit has no Linux build, so the game server itself
must run on Windows. The usual setup is the master on a small Linux VPS with Windows
machines running game servers that register to it.

Windows server: **`Server Launcher.bat`** - checkboxes and sliders for every setting,
and a button to start the server. `Start Server (console).bat` is the same server
without the window, if you prefer typing.
Windows client: `Start Client.bat`.
Linux: `chmod +x start-server.sh && ./start-server.sh` (console only)

## What is in a box

| | |
|---|---|
| the one `.exe` | The whole program, with the .NET runtime bundled inside it. |
| `RevivalServerLauncher.exe` | Windows server box only: the settings GUI and a start button. |
| `server.properties` | Every setting, in one file. The GUI writes this, or edit it by hand. |
| `docs/` | The guides, including Persian, and the full console command reference. |
| `tools/` | `patch_auth.ps1` and `patch_languages.ps1`, for patching your own game install. Run **both**. |

Read `docs/SERVER-GUIDE.md` or `docs/CLIENT-GUIDE.md` next. Stuck? `docs/TROUBLESHOOTING.md`.
'@ | Set-Content "$dist\README.md" -Encoding utf8

# ---- move the finished boxes into place ----
# A single-file exe stays locked for a while after it has been run - the runtime unpacks
# native libraries and the scanner holds the image, with nothing showing in the task
# list. A box whose destination will not clear is left in staging and named, rather than
# failing a build that has already succeeded: the zips are the deliverable and can be
# made from either location.
$final = @{}
$stranded = @()
foreach ($box in $boxes.Name) {
    $src = "$stage\$box"
    $dst = "$dist\$box"
    if (Test-Path $dst) {
        foreach ($try in 1..3) {
            try { Remove-Item $dst -Recurse -Force -ErrorAction Stop; break }
            catch { Start-Sleep -Seconds 3 }
        }
    }
    # Track the move itself. Asking whether the destination has files afterwards is not
    # the same question: a destination that could not be cleared still holds the PREVIOUS
    # build, which looks like success and would both report a stale box and delete the
    # fresh one from staging.
    $moved = $false
    if (-not (Test-Path $dst)) {
        try { Move-Item $src $dst -Force -ErrorAction Stop; $moved = $true } catch { }
    }
    if ($moved) { $final[$box] = $dst }
    else { $final[$box] = $src; $stranded += $box }
}
if ($stranded.Count -eq 0) { Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue }

Write-Host ""
Write-Host "Packaged ($label):" -ForegroundColor Green
foreach ($box in $boxes.Name) {
    $p = $final[$box]
    $files = Get-ChildItem $p -Recurse -File
    $size = [math]::Round(($files | Measure-Object Length -Sum).Sum / 1MB, 1)
    $where = if ($stranded -contains $box) { "  <- left in .build, destination locked" } else { "" }
    Write-Host ("  {0,-18} {1,6} MB  {2,3} files{3}" -f $box, $size, $files.Count, $where)
}
if ($stranded.Count -gt 0) {
    Write-Host ""
    Write-Warning ("Could not replace: " + ($stranded -join ", ") +
                   ". The build is complete and the folders are under dist\.build - " +
                   "close anything running from dist and re-run to move them into place.")
}
Write-Host ""
Write-Host "dist\README.md says which folder a user wants."
