# Builds dist\UltraPuck-<version>.zip, with one self-contained folder per operating system:
#   UltraPuck-<version>\
#     README.txt
#     Windows\               Install UltraPuck.bat, Uninstall UltraPuck.bat, installer\, files\
#     Linux and Steam Deck\  install-linux.sh, uninstall-linux.sh, installer\, files\
#     Mac\                   install-mac.sh, uninstall-mac.sh, installer\, files\
# Each installer\ holds that OS's setup script + version.txt; files\ is what gets copied into the game folder:
#   Windows and Linux (Proton): dist\payload (BepInEx x86 + config); Mac: dist\payload-mac (BepInEx macOS) + the same config.
# The plugin DLL is taken fresh from the Release build.
$ErrorActionPreference = 'Stop'
$dist = $PSScriptRoot
$root = Split-Path -Parent $dist
$src  = Join-Path $dist 'installer'
$plugin = Join-Path $root 'src\CP99Online\bin\Release\CP99Online.dll'
$version = [regex]::Match((Get-Content -LiteralPath (Join-Path $root 'src\CP99Online\Plugin.cs') -Raw), 'Version\s*=\s*"([^"]+)"').Groups[1].Value
if (-not $version) { throw 'Version not found in Plugin.cs' }

# The map maker (Windows only), built fresh.
$mapMakerDir = Join-Path $root 'src\MapMaker\bin\Release'
& dotnet build (Join-Path $root 'src\MapMaker\MapMaker.csproj') -c Release --nologo -v q | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Map maker build failed' }

$name  = "UltraPuck-$version"
$stage = Join-Path $dist "build\$name"
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null
$ascii = New-Object Text.ASCIIEncoding

function Write-Text($from, $to, [switch]$Unix) {
    $text = (Get-Content -LiteralPath $from -Raw) -replace "`r`n", "`n"
    if (-not $Unix) { $text = $text -replace "`n", "`r`n" }
    [IO.File]::WriteAllText($to, $text.Replace('{VERSION}', $version), $ascii)
}

# One OS folder: launchers at the top, setup script in installer\, payload in files\.
function New-OsFolder($folder, $srcDir, [string[]]$launchers, $setup, $payload, [switch]$Unix, [switch]$MapMaker) {
    $dir = Join-Path $stage $folder
    New-Item -ItemType Directory -Force -Path "$dir\installer" | Out-Null
    foreach ($f in $launchers) { Write-Text (Join-Path $src "$srcDir\$f") (Join-Path $dir $f) -Unix:$Unix }
    if ($Unix) { Write-Text (Join-Path $src "$srcDir\$setup") "$dir\installer\$setup" -Unix }
    else { Copy-Item -LiteralPath (Join-Path $src "$srcDir\$setup") -Destination "$dir\installer\" }
    [IO.File]::WriteAllText("$dir\installer\version.txt", $version, $ascii)

    Copy-Item -LiteralPath (Join-Path $dist $payload) -Destination "$dir\files" -Recurse
    New-Item -ItemType Directory -Force -Path "$dir\files\BepInEx\config", "$dir\files\BepInEx\plugins\CP99Online" | Out-Null
    Copy-Item -LiteralPath (Join-Path $dist 'payload\BepInEx\config\BepInEx.cfg') -Destination "$dir\files\BepInEx\config\" -Force
    Copy-Item -LiteralPath $plugin -Destination "$dir\files\BepInEx\plugins\CP99Online\CP99Online.dll" -Force
    # Example maps for the arena list ("UltraPuck Maps" in the game folder)
    New-Item -ItemType Directory -Force -Path "$dir\files\UltraPuck Maps" | Out-Null
    Copy-Item -Path (Join-Path $root 'maps\*.upmap') -Destination "$dir\files\UltraPuck Maps\"
    if ($MapMaker) {
        New-Item -ItemType Directory -Force -Path "$dir\files\UltraPuck Map Maker" | Out-Null
        foreach ($f in @('UltraPuck Map Maker.exe', 'UltraPuck Map Maker.exe.config')) {
            Copy-Item -LiteralPath (Join-Path $mapMakerDir $f) -Destination "$dir\files\UltraPuck Map Maker\"
        }
    }
}

New-OsFolder 'Windows' 'Windows' @('Install UltraPuck.bat', 'Uninstall UltraPuck.bat') 'cp99-setup.ps1' 'payload' -MapMaker
New-OsFolder 'Linux and Steam Deck' 'Linux' @('install-linux.sh', 'uninstall-linux.sh') 'cp99-setup.sh' 'payload' -Unix
New-OsFolder 'Mac' 'Mac' @('install-mac.sh', 'uninstall-mac.sh') 'cp99-setup-mac.sh' 'payload-mac' -Unix
Write-Text (Join-Path $src 'README.txt') (Join-Path $stage 'README.txt')

# Zip with forward-slash entry names so every unzip tool keeps the folders.
$zipPath = Join-Path $dist "$name.zip"
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::Open($zipPath, 'Create')
try {
    $base = Split-Path -Parent $stage
    foreach ($f in Get-ChildItem -LiteralPath $stage -Recurse -File -Force) {
        $entry = $f.FullName.Substring($base.Length + 1).Replace('\', '/')
        $e = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $f.FullName, $entry, 'Optimal')
        # Mark shell scripts executable (rwxr-xr-x) for Linux / Mac unzip tools.
        if ($f.Extension -eq '.sh') { $e.ExternalAttributes = [int](0x81ED -shl 16) }
    }
} finally { $zip.Dispose() }
# Loose files for the GitHub release: the in-game updater downloads these two from releases/latest.
$rel = Join-Path $dist 'release'
if (Test-Path -LiteralPath $rel) { Remove-Item -LiteralPath $rel -Recurse -Force }
New-Item -ItemType Directory -Force -Path $rel | Out-Null
Copy-Item -LiteralPath $plugin -Destination (Join-Path $rel 'CP99Online.dll')
[IO.File]::WriteAllText((Join-Path $rel 'version.txt'), $version, $ascii)
Copy-Item -LiteralPath $zipPath -Destination $rel
"Release files in $rel (upload all three to a GitHub release tagged v$version)"
"Built $zipPath ($([math]::Round((Get-Item -LiteralPath $zipPath).Length / 1KB)) KB)"
