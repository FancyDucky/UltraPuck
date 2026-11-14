# UltraPuck

A mod for [ClusterPuck 99](https://store.steampowered.com/app/337960/) that adds:

- **Online multiplayer** — host through Steam (friends-only lobby + invites, no port forwarding) or direct IP.
  Everyone shares the real player select screen (own profile, team) and watches the host pick the arena and mode.
  Your own player responds instantly even far from the host (client-side prediction, corrected to the host's game).
- **Mod manager**: Main menu > Mods lists every mod. Select one to turn it on or off and change its settings.
  Built-in mods: **FPS+** (60 / 120 / 144 / 240 / unlimited), **Smooth Motion** (no jittery players and name tags),
  **Better Boost** (no slowdown after boosting), **Dash Meter** (a small bar above each player shows when their dash is
  ready, one segment per charge) and **Charge Zoom** (while you charge a shot, the camera pulls back until the goal
  you're aiming at is in view, capped by Max Zoom Out, 250% by default; charging past full with Over Charge doesn't zoom
  further).
- **Straight to the main menu**: no logo or "Press A to Begin" screen (`SkipIntro` in `cp99.online.cfg` turns it off,
  for example to pick another language).
- **Rounds Mode** (built-in mod, off by default): after each goal everyone picks one of a few random cards that
  power them up for the rest of the match (Double Dash, Speed Up, Power Shot, ...). Online, each player sees only
  their own cards; everyone's cards show in the lower-left corner (hover for details). Setting: each player picks,
  or each team votes for one card (ties are random). Rounds Mode > Toggle Cards turns individual cards on or off.
  Mods can add cards.
- **Make your own mods**: drop a `.dll` in the game's `Mods` folder. See [docs/MAKING-MODS.md](docs/MAKING-MODS.md)
  and [examples/ExampleMod](examples/ExampleMod).
- **Play Online** and **Mods** entries in the game's own main menu (keyboard and controller).
- **Overlay menu** for extras like direct IP — F8, or hold View/Back on a controller.
- **Auto-update** from this repo's GitHub Releases (on by default; Main menu > Mods > Auto Update). A popup says when
  an update is found; once it's installed the game restarts itself through Steam after a 5 s countdown (on the main
  menu, never mid-match or online; Esc / B to restart later). On Linux / Steam Deck it asks you to restart instead.
- Windows, Linux and Steam Deck (via Proton), and Mac (native, new).

## Install

Download the latest `UltraPuck-<version>.zip` from [Releases](../../releases) and extract it. It has one folder per
system; open yours:

- **`Windows`:** double-click `Install UltraPuck.bat`.
- **`Linux and Steam Deck`:** in a terminal in that folder run `bash install-linux.sh`, then set the game's
  Steam launch options to `WINEDLLOVERRIDES="winhttp=n,b" %command%`.
- **`Mac`:** in Terminal run `bash install-mac.sh` from that folder, then paste the launch options line it
  copies for you (`"<game folder>/run_bepinex.sh" %command%`) into the game's Steam Launch Options.

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
- `dist/installer` — install & uninstall scripts in `Windows/`, `Linux/` and `Mac/`, plus the README shipped in the zip.
- `dist/payload` — BepInEx 5.4.23.5 (x86) set up for this game (Camera entrypoint), shipped inside the zip.
- `dist/payload-mac` — BepInEx 5.4.23.5 for macOS (universal `libdoorstop.dylib` + `run_bepinex.sh`); the zip adds
  the same config and plugin.

Built on [BepInEx](https://github.com/BepInEx/BepInEx) (LGPL-2.1) and [HarmonyX](https://github.com/BepInEx/HarmonyX) (MIT).
Not affiliated with the developers of ClusterPuck 99.

## Credits

The main menu title uses the [Chakra Petch](https://fonts.google.com/specimen/Chakra+Petch) font by The Chakra Petch Project Authors, under the SIL Open Font License 1.1.
