# UltraPuck

A mod for [ClusterPuck 99](https://store.steampowered.com/app/337960/) that adds:

- **Online multiplayer** — host through Steam (friends-only lobby + invites, no port forwarding) or direct IP.
  Everyone shares the real player select screen (own profile, team) and watches the host pick the arena and mode.
- **Mod manager**: Main menu > Mods lists every mod. Select one to turn it on or off and change its settings.
  Built-in mods: **FPS+** (60 / 120 / 144 / 240 / unlimited) and **Better Boost** (no slowdown after boosting).
- **Make your own mods**: drop a `.dll` in the game's `Mods` folder. See [docs/MAKING-MODS.md](docs/MAKING-MODS.md)
  and [examples/ExampleMod](examples/ExampleMod).
- **Play Online** and **Mods** entries in the game's own main menu (keyboard and controller).
- **Overlay menu** for extras like direct IP — F8, or hold View/Back on a controller.
- **Auto-update** from this repo's GitHub Releases (on by default; Main menu > Mods > Auto Update).
- Windows, Linux and Steam Deck (via Proton).

## Install

Download the latest `UltraPuck-<version>.zip` from [Releases](../../releases), extract it, then:

- **Windows:** double-click `Install UltraPuck.bat`.
- **Linux / Steam Deck:** in a terminal in the extracted folder run `bash install-linux.sh`, then set the game's
  Steam launch options to `WINEDLLOVERRIDES="winhttp=n,b" %command%`.

Everyone playing together needs the mod. See `README.txt` in the zip for hosting/joining steps.

## Building

Requirements: .NET SDK, and ClusterPuck 99 installed (the build references the game's own DLLs).

```
cd src/CP99Online
dotnet build -c Release          # builds and copies the plugin into the game's BepInEx/plugins folder
powershell -ExecutionPolicy Bypass -File ../../dist/make-dist.ps1   # builds dist/UltraPuck-<version>.zip
```

## Releasing an update

1. Bump `Version` in `src/CP99Online/Plugin.cs`, build, and run `dist/make-dist.ps1`.
2. Create a GitHub release tagged `v<version>` and attach the three files from `dist/release/`:
   `UltraPuck-<version>.zip`, `CP99Online.dll` and `version.txt`.
3. Players' games pick up `version.txt` + `CP99Online.dll` from the latest release at startup and swap the DLL in;
   the new version runs after their next restart. Loader (BepInEx) changes still need the installer.

If the game isn't at the default Steam path, set `GameDir` in `src/CP99Online/CP99Online.csproj`.

Layout:

- `src/CP99Online` — the BepInEx plugin (Harmony patches, networking, menus). `Modding/` holds the mod API.
- `examples/ExampleMod` — a small mod to copy when making your own.
- `docs/MAKING-MODS.md` — guide for mod makers.
- `dist/installer` — Windows/Linux install & uninstall scripts.
- `dist/payload` — BepInEx 5.4.23.5 (x86) set up for this game (Camera entrypoint), shipped inside the zip.

Built on [BepInEx](https://github.com/BepInEx/BepInEx) (LGPL-2.1) and [HarmonyX](https://github.com/BepInEx/HarmonyX) (MIT).
Not affiliated with the developers of ClusterPuck 99.
