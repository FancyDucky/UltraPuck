# Builds dist\UltraPuck-<version>.zip:
#   UltraPuck-<version>\
#     Install UltraPuck.bat, Uninstall UltraPuck.bat, README.txt
#     installer\cp99-setup.ps1, installer\version.txt
#     files\...      (Windows / Proton payload: BepInEx x86 + config + the plugin)
#     files-mac\...  (native Mac payload: BepInEx macOS + run_bepinex.sh + config + the plugin)
# Payload comes from dist\payload, with the plugin DLL taken fresh from the Release build.
$ErrorActionPreference = 'Stop'
$dist = $PSScriptRoot
$root = Split-Path -Parent $dist
$plugin = Join-Path $root 'src\CP99Online\bin\Release\CP99Online.dll'
$version = [regex]::Match((Get-Content -LiteralPath (Join-Path $root 'src\CP99Online\Plugin.cs') -Raw), 'Version\s*=\s*"([^"]+)"').Groups[1].Value
if (-not $version) { throw 'Version not found in Plugin.cs' }

$name  = "UltraPuck-$version"
$stage = Join-Path $dist "build\$name"
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path "$stage\installer" | Out-Null

Copy-Item -LiteralPath (Join-Path $dist 'payload') -Destination "$stage\files" -Recurse
New-Item -ItemType Directory -Force -Path "$stage\files\BepInEx\plugins\CP99Online" | Out-Null
Copy-Item -LiteralPath $plugin -Destination "$stage\files\BepInEx\plugins\CP99Online\CP99Online.dll" -Force
# Mac: same config and plugin, Mac loader.
Copy-Item -LiteralPath (Join-Path $dist 'payload-mac') -Destination "$stage\files-mac" -Recurse
New-Item -ItemType Directory -Force -Path "$stage\files-mac\BepInEx\config", "$stage\files-mac\BepInEx\plugins\CP99Online" | Out-Null
Copy-Item -LiteralPath (Join-Path $dist 'payload\BepInEx\config\BepInEx.cfg') -Destination "$stage\files-mac\BepInEx\config\"
Copy-Item -LiteralPath $plugin -Destination "$stage\files-mac\BepInEx\plugins\CP99Online\CP99Online.dll" -Force
Copy-Item -LiteralPath (Join-Path $dist 'installer\cp99-setup.ps1') -Destination "$stage\installer\"
Set-Content -LiteralPath "$stage\installer\version.txt" -Value $version -Encoding ASCII

# Linux and Mac scripts must keep Unix line endings.
$lf = @{ 'install-linux.sh' = $stage; 'uninstall-linux.sh' = $stage; 'cp99-setup.sh' = "$stage\installer";
         'install-mac.sh' = $stage; 'uninstall-mac.sh' = $stage; 'cp99-setup-mac.sh' = "$stage\installer" }
foreach ($f in $lf.Keys) {
    $text = (Get-Content -LiteralPath (Join-Path $dist "installer\$f") -Raw) -replace "`r`n", "`n"
    [IO.File]::WriteAllText((Join-Path $lf[$f] $f), $text, (New-Object Text.ASCIIEncoding))
}

# .bat and .txt files get Windows line endings.
foreach ($f in 'Install UltraPuck.bat', 'Uninstall UltraPuck.bat', 'README.txt') {
    $text = (Get-Content -LiteralPath (Join-Path $dist "installer\$f") -Raw) -replace "`r`n", "`n" -replace "`n", "`r`n"
    $text = $text.Replace('{VERSION}', $version)
    [IO.File]::WriteAllText((Join-Path $stage $f), $text, (New-Object Text.ASCIIEncoding))
}

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
        # Mark shell scripts executable (rwxr-xr-x) for Linux unzip tools.
        if ($f.Extension -eq '.sh') { $e.ExternalAttributes = [int](0x81ED -shl 16) }
    }
} finally { $zip.Dispose() }
# Loose files for the GitHub release: the in-game updater downloads these two from releases/latest.
$rel = Join-Path $dist 'release'
if (Test-Path -LiteralPath $rel) { Remove-Item -LiteralPath $rel -Recurse -Force }
New-Item -ItemType Directory -Force -Path $rel | Out-Null
Copy-Item -LiteralPath $plugin -Destination (Join-Path $rel 'CP99Online.dll')
[IO.File]::WriteAllText((Join-Path $rel 'version.txt'), $version, (New-Object Text.ASCIIEncoding))
Copy-Item -LiteralPath $zipPath -Destination $rel
"Release files in $rel (upload all three to a GitHub release tagged v$version)"
"Built $zipPath ($([math]::Round((Get-Item -LiteralPath $zipPath).Length / 1KB)) KB)"
