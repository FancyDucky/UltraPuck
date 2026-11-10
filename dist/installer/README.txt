UltraPuck {VERSION} - mod for ClusterPuck 99
Online play for ClusterPuck 99, plus a mod manager with built-in mods (FPS+, Smooth Motion, Better Boost).

INSTALL ON WINDOWS
  1. Extract this zip (right-click > Extract All).
  2. Double-click "Install UltraPuck.bat".
     It finds ClusterPuck 99 through Steam and copies the mod in. If it can't find
     the game it asks you to pick the folder.
  3. Launch ClusterPuck 99 from Steam and press F8.

INSTALL ON LINUX / STEAM DECK (the game runs through Proton)
  1. Extract this zip. On Steam Deck, do this in Desktop Mode.
  2. Open a terminal in the extracted folder (Dolphin: right-click > Open Terminal Here) and run:
         bash install-linux.sh
  3. In Steam: right-click ClusterPuck 99 > Properties > General > Launch Options, paste:
         WINEDLLOVERRIDES="winhttp=n,b" %command%
  4. Launch the game (Game Mode is fine) and press F8.
  If the installer says you have the native Linux version: Properties > Compatibility >
  force a Proton version, let Steam update the game, and run the installer again.
  Uninstall: bash uninstall-linux.sh, then clear the Launch Options.

PLAY ONLINE
  Everyone needs this mod, and the same version.
  Host:  Main menu > Play Online > Host Game, then Invite Friends, then Go to Player Select.
  Join:  Accept the Steam invite, or Main menu > Play Online > Join a Friend.
         You follow the host to the player select screen automatically.
  Then everyone presses A to join a box, picks their own profile and moves up/down to pick a
  team, just like local multiplayer. The host presses Start and picks the arena and mode while
  everyone watches. After the match everyone returns to player select together.
  One player per PC online; the host can also add couch players and AI bots.
  "Host direct IP" also works but needs UDP port 27099 forwarded on the host's router.

MODS
  Main menu > Mods lists every mod. Select one to turn it on or off and change its settings.
  Built in: FPS+ (raise the 60 FPS cap), Smooth Motion (players and name tags move smoothly
  instead of jittering) and Better Boost (no slowdown after boosting).
  Add more: put a mod's .dll in the "Mods" folder in the game folder (Mods > Open Mods Folder),
  then restart the game. Online, the host's gameplay mods apply.
  F8 (or hold View/Back on a controller) opens an overlay with the same options, plus direct IP.

UNINSTALL
  Double-click "Uninstall UltraPuck.bat". It removes the mod and puts back any
  files it replaced. The Mods folder (mods you added) is left alone. (Steam > Properties > Installed Files > Verify also restores the game.)

NOTES
  - Windows may show a security warning for the .bat (it came from a download):
    click Run, or More info > Run anyway.
    The scripts are plain text; open installer\cp99-setup.ps1 in Notepad to see what they do.
  - Some antivirus tools flag winhttp.dll. It is BepInEx's mod loader (BepInEx 5.4.23.5, 32-bit),
    the standard loader for Unity game mods.
  - Windows Firewall may ask for network access the first time you host. Allow it.
  - Custom arenas don't work online yet.
  - Manual install: copy everything inside the "files" folder into the game folder
    (Steam > right-click ClusterPuck 99 > Manage > Browse local files).
