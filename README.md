# ClusterPuck 99 Ultra

A mod for [ClusterPuck 99](https://store.steampowered.com/app/337960/) that adds:

- **Online multiplayer** — host through Steam (friends-only lobby + invites, no port forwarding) or direct IP.
- **No slowdown after boost** (toggle).
- **Uncapped FPS** (60 / 120 / 144 / 240 / unlimited).
- **In-game mod menu** — F8, or hold View/Back on a controller.
- Windows, Linux and Steam Deck (via Proton).

## Install

Download the latest `CP99Online-<version>.zip` from [Releases](../../releases), extract it, then:

- **Windows:** double-click `Install CP99 Online.bat`.
- **Linux / Steam Deck:** in a terminal in the extracted folder run `bash install-linux.sh`, then set the game's
  Steam launch options to `WINEDLLOVERRIDES="winhttp=n,b" %command%`.

Everyone playing together needs the mod. See `README.txt` in the zip for hosting/joining steps.

## Building

Requirements: .NET SDK, and ClusterPuck 99 installed (the build references the game's own DLLs).

```
cd src/CP99Online
dotnet build -c Release          # builds and copies the plugin into the game's BepInEx/plugins folder
powershell -ExecutionPolicy Bypass -File ../../dist/make-dist.ps1   # builds dist/CP99Online-<version>.zip
```

If the game isn't at the default Steam path, set `GameDir` in `src/CP99Online/CP99Online.csproj`.

Layout:

- `src/CP99Online` — the BepInEx plugin (Harmony patches, networking, menus).
- `dist/installer` — Windows/Linux install & uninstall scripts.
- `dist/payload` — BepInEx 5.4.23.5 (x86) set up for this game (Camera entrypoint), shipped inside the zip.

Built on [BepInEx](https://github.com/BepInEx/BepInEx) (LGPL-2.1) and [HarmonyX](https://github.com/BepInEx/HarmonyX) (MIT).
Not affiliated with the developers of ClusterPuck 99.
