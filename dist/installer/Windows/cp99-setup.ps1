# UltraPuck installer / uninstaller for ClusterPuck 99.
# Run through "Install UltraPuck.bat" or "Uninstall UltraPuck.bat".
#
# Install:   finds the game through Steam, backs up any file it would replace into
#            <game>\CP99Online-backup, then copies the mod files from ..\files.
# Uninstall: removes exactly the files the installer added and puts the backups back.
param(
    [ValidateSet('Install', 'Uninstall')] [string]$Mode = 'Install',
    [string]$GameDir,  # optional: skip auto-detection
    [switch]$DetectOnly  # print the detected game folder and exit (for testing)
)

$ErrorActionPreference = 'Stop'
$AppId   = '337960'
$ExeName = 'ClusterPuck99.exe'
$Payload = Join-Path (Split-Path -Parent $PSScriptRoot) 'files'

function Say($text, $color = 'Gray') { Write-Host $text -ForegroundColor $color }
function Fail($text) { Say ''; Say $text 'Red'; exit 1 }

# ----------------------------------------------------------------------------- find the game

function Get-SteamLibraries {
    $roots = New-Object System.Collections.Generic.List[string]
    foreach ($key in 'HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam', 'HKLM:\SOFTWARE\Valve\Steam') {
        try {
            $props = Get-ItemProperty -LiteralPath $key -ErrorAction Stop
            foreach ($name in 'SteamPath', 'InstallPath') {
                if ($props.$name) { $roots.Add(($props.$name -replace '/', '\')) }
            }
        } catch {}
    }
    $roots.Add((Join-Path ${env:ProgramFiles(x86)} 'Steam'))

    $libs = New-Object System.Collections.Generic.List[string]
    foreach ($root in ($roots | Select-Object -Unique)) {
        $libs.Add($root)
        $vdf = Join-Path $root 'steamapps\libraryfolders.vdf'
        if (Test-Path -LiteralPath $vdf) {
            $text = Get-Content -LiteralPath $vdf -Raw
            foreach ($m in [regex]::Matches($text, '"path"\s+"([^"]+)"')) {
                $libs.Add(($m.Groups[1].Value -replace '\\\\', '\'))
            }
        }
    }
    $libs | Select-Object -Unique
}

function Find-Game {
    foreach ($lib in Get-SteamLibraries) {
        $folder = 'ClusterPuck 99'
        $acf = Join-Path $lib "steamapps\appmanifest_$AppId.acf"
        if (Test-Path -LiteralPath $acf) {
            $m = [regex]::Match((Get-Content -LiteralPath $acf -Raw), '"installdir"\s+"([^"]+)"')
            if ($m.Success) { $folder = $m.Groups[1].Value }
        }
        $dir = Join-Path $lib "steamapps\common\$folder"
        if (Test-Path -LiteralPath (Join-Path $dir $ExeName)) { return $dir }
    }
    return $null
}

function Ask-ForGame {
    Say "Couldn't find ClusterPuck 99 automatically." 'Yellow'
    Say "In Steam: right-click ClusterPuck 99 > Manage > Browse local files, then pick that folder."
    Add-Type -AssemblyName System.Windows.Forms
    $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
    $dlg.Description = 'Select the ClusterPuck 99 folder (the one containing ClusterPuck99.exe)'
    if ($dlg.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { return $null }
    return $dlg.SelectedPath
}

# ----------------------------------------------------------------------------- helpers

function Wait-GameClosed($game) {
    while ($true) {
        $running = Get-Process -Name 'ClusterPuck99' -ErrorAction SilentlyContinue |
            Where-Object { try { $_.Path -and $_.Path.StartsWith($game, [StringComparison]::OrdinalIgnoreCase) } catch { $true } }
        if (-not $running) { return }
        Say 'ClusterPuck 99 is running. Close the game, then press Enter.' 'Yellow'
        [void](Read-Host)
    }
}

function Read-State($stateFile) {
    if (-not (Test-Path -LiteralPath $stateFile)) { return $null }
    $raw = Get-Content -LiteralPath $stateFile -Raw | ConvertFrom-Json
    return @{
        Version    = [string]$raw.Version
        HadBepInEx = [bool]$raw.HadBepInEx
        Files      = @($raw.Files)
        BackedUp   = @($raw.BackedUp)
    }
}

function Write-State($stateFile, $state) {
    $obj = New-Object PSObject -Property @{
        Version = $state.Version; HadBepInEx = $state.HadBepInEx
        Files = @($state.Files); BackedUp = @($state.BackedUp)
    }
    $obj | ConvertTo-Json | Set-Content -LiteralPath $stateFile -Encoding UTF8
}

function Remove-EmptyDirs($root, $start) {
    $dir = $start
    while ($dir -and $dir.Length -gt $root.Length -and (Test-Path -LiteralPath $dir)) {
        if (Get-ChildItem -LiteralPath $dir -Force) { break }
        Remove-Item -LiteralPath $dir -Force
        $dir = Split-Path -Parent $dir
    }
}

# ----------------------------------------------------------------------------- install

function Install-Mod($game) {
    if (-not (Test-Path -LiteralPath $Payload)) {
        Fail "The mod files are missing. Extract the whole zip first (right-click it > Extract All), then run the installer from the extracted folder."
    }
    $version = 'unknown'
    $vfile = Join-Path $PSScriptRoot 'version.txt'
    if (Test-Path -LiteralPath $vfile) { $version = ([IO.File]::ReadAllText($vfile)).Trim() }

    $backupDir = Join-Path $game 'CP99Online-backup'
    $stateFile = Join-Path $backupDir 'install-state.json'
    $state = Read-State $stateFile
    if ($state) {
        Say "Updating UltraPuck $($state.Version) -> $version"
    } else {
        $state = @{ Version = $version; HadBepInEx = (Test-Path -LiteralPath (Join-Path $game 'BepInEx')); Files = @(); BackedUp = @() }
        Say "Installing UltraPuck $version"
    }
    New-Item -ItemType Directory -Force -Path $backupDir | Out-Null

    $files = Get-ChildItem -LiteralPath $Payload -Recurse -File -Force
    $count = 0
    foreach ($f in $files) {
        $rel  = $f.FullName.Substring($Payload.Length).TrimStart('\')
        $dest = Join-Path $game $rel
        # Back up an existing file the first time we replace it - unless it is already this mod's copy
        # (from an earlier install, or from copying the zip in by hand).
        $exists = Test-Path -LiteralPath $dest
        $same = $exists -and ((Get-FileHash -LiteralPath $dest).Hash -eq (Get-FileHash -LiteralPath $f.FullName).Hash)
        if ($exists -and -not $same -and ($state.Files -notcontains $rel) -and ($state.BackedUp -notcontains $rel)) {
            $bak = Join-Path $backupDir $rel
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $bak) | Out-Null
            Copy-Item -LiteralPath $dest -Destination $bak -Force
            $state.BackedUp += $rel
            Say "  backed up $rel"
        }
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dest) | Out-Null
        Copy-Item -LiteralPath $f.FullName -Destination $dest -Force
        try { Unblock-File -LiteralPath $dest } catch {}
        if ($state.Files -notcontains $rel) { $state.Files += $rel }
        $count++
    }
    $state.Version = $version
    Write-State $stateFile $state

    Say ''
    Say "Done! Installed $count files into:" 'Green'
    Say "  $game" 'Green'
    Say ''
    Say 'Launch ClusterPuck 99 from Steam. The main menu gets Play Online and Mods entries.'
    Say 'To play together: Play Online > Host Game, then Invite Friends. Friends need this mod too.'
}

# ----------------------------------------------------------------------------- uninstall

function Uninstall-Mod($game) {
    $backupDir = Join-Path $game 'CP99Online-backup'
    $stateFile = Join-Path $backupDir 'install-state.json'
    $state = Read-State $stateFile

    if (-not $state) {
        # Installed by hand (copied the zip in): remove the loader so the game starts unmodded.
        Say 'No installer record found (the mod was probably copied in by hand).' 'Yellow'
        foreach ($rel in 'winhttp.dll', 'doorstop_config.ini', '.doorstop_version') {
            $p = Join-Path $game $rel
            if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Force; Say "  removed $rel" }
        }
        $plug = Join-Path $game 'BepInEx\plugins\CP99Online'
        if (Test-Path -LiteralPath $plug) { Remove-Item -LiteralPath $plug -Recurse -Force; Say '  removed BepInEx\plugins\CP99Online' }
        Say ''
        Say 'Mod disabled. The game will start without mods. (The BepInEx folder was left in place; it does nothing on its own.)' 'Green'
        return
    }

    Say "Uninstalling UltraPuck $($state.Version)"
    $plugins = Join-Path $game 'BepInEx\plugins'
    $otherPlugins = (Test-Path -LiteralPath $plugins) -and
        (Get-ChildItem -LiteralPath $plugins -Force | Where-Object { $_.Name -ne 'CP99Online' })

    foreach ($rel in $state.Files) {
        $p = Join-Path $game $rel
        if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Force }
        Remove-EmptyDirs $game (Split-Path -Parent $p)
    }
    $plug = Join-Path $game 'BepInEx\plugins\CP99Online'   # also holds the mod's logs
    if (Test-Path -LiteralPath $plug) { Remove-Item -LiteralPath $plug -Recurse -Force }
    $cfg = Join-Path $game 'BepInEx\config\cp99.online.cfg'
    if (Test-Path -LiteralPath $cfg) { Remove-Item -LiteralPath $cfg -Force }
    $cfg = Join-Path $game 'BepInEx\config\ultrapuck.mods.cfg'
    if (Test-Path -LiteralPath $cfg) { Remove-Item -LiteralPath $cfg -Force }

    foreach ($rel in $state.BackedUp) {
        $bak = Join-Path $backupDir $rel
        if (Test-Path -LiteralPath $bak) {
            $dest = Join-Path $game $rel
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dest) | Out-Null
            Copy-Item -LiteralPath $bak -Destination $dest -Force
            Say "  restored $rel"
        }
    }

    # BepInEx came with this mod and nothing else uses it: remove the rest of it (cache, logs, config).
    $bep = Join-Path $game 'BepInEx'
    if (-not $state.HadBepInEx -and -not $otherPlugins -and (Test-Path -LiteralPath $bep)) {
        Remove-Item -LiteralPath $bep -Recurse -Force
        Say '  removed BepInEx'
    }
    Remove-Item -LiteralPath $backupDir -Recurse -Force

    Say ''
    Say 'UltraPuck was removed. The game is back to how it was before the install.' 'Green'
}

# ----------------------------------------------------------------------------- main

try {
    Say "UltraPuck - $Mode" 'Cyan'
    Say ''
    $game = $GameDir
    if (-not $game) { $game = Find-Game }
    if ($DetectOnly) { Say "Detected: $game"; exit 0 }
    if (-not $game) { $game = Ask-ForGame }
    if (-not $game) { Fail 'Cancelled - no game folder selected.' }
    $game = (Resolve-Path -LiteralPath $game).Path.TrimEnd('\')
    if (-not (Test-Path -LiteralPath (Join-Path $game $ExeName))) {
        Fail "That folder doesn't contain $ExeName. Pick the ClusterPuck 99 game folder."
    }
    Say "Game folder: $game"
    Wait-GameClosed $game

    if ($Mode -eq 'Install') { Install-Mod $game } else { Uninstall-Mod $game }
} catch {
    Fail ("Something went wrong: " + $_.Exception.Message + "`nIf the game is installed under Program Files, try right-clicking the .bat > Run as administrator.")
}
