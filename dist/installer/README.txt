UltraPuck {VERSION} - mod for ClusterPuck 99
Online play for ClusterPuck 99, plus a mod manager with built-in mods (FPS+, Smooth Motion, Better Boost).

START HERE: open the folder for your computer.
  Windows               -> Windows folder
  Linux or Steam Deck   -> "Linux and Steam Deck" folder
  Mac                   -> Mac folder
Each folder has its own installer and uninstaller. You only need the one for your system.

WINDOWS  (Windows folder)
  1. Extract this zip (right-click > Extract All).
  2. Open the Windows folder and double-click "Install UltraPuck.bat".
     It finds ClusterPuck 99 through Steam and copies the mod in. If it can't find
     the game it asks you to pick the folder.
  3. Launch ClusterPuck 99 from Steam.
  Uninstall: double-click "Uninstall UltraPuck.bat" in the same folder.

LINUX / STEAM DECK  ("Linux and Steam Deck" folder; the game runs through Proton)
  1. Extract this zip. On Steam Deck, do this in Desktop Mode.
  2. Open the "Linux and Steam Deck" folder, open a terminal there
     (Dolphin: right-click > Open Terminal Here) and run:
         bash install-linux.sh
  3. In Steam: right-click ClusterPuck 99 > Properties > General > Launch Options, paste:
         WINEDLLOVERRIDES="winhttp=n,b" %command%
  4. Launch the game (Game Mode is fine).
  If the installer says you have the native Linux version: Properties > Compatibility >
  force a Proton version, let Steam update the game, and run the installer again.
  Uninstall: bash uninstall-linux.sh, then clear the Launch Options.

MAC  (Mac folder; native Mac version of the game, new and not yet tested on a real Mac)
  1. Double-click the zip to extract it.
  2. Open Terminal (Applications > Utilities), type  bash  and a space, drag install-mac.sh
     from the Mac folder into the window, and press Return.
  3. The installer copies a Steam Launch Options line for you. In Steam: right-click
     ClusterPuck 99 > Properties > General > Launch Options, and press Command+V.
     (It looks like:  "/Users/you/Library/Application Support/Steam/steamapps/common/ClusterPuck 99/run_bepinex.sh" %command%)
  4. Launch ClusterPuck 99 from Steam. The main menu now has Play Online and Mods.
  Uninstall: same steps with uninstall-mac.sh, then clear the Launch Options.
  If it doesn't load, send the file BepInEx/LogOutput.log from the game folder
  (Steam: right-click the game > Manage > Browse local files).

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
  Use the uninstaller in your system's folder (see above). It removes the mod and puts back
  any files it replaced. The Mods folder (mods you added) is left alone.
  (Steam > Properties > Installed Files > Verify also restores the game.)

NOTES
  - Windows may show a security warning for the .bat (it came from a download):
    click Run, or More info > Run anyway.
    The scripts are plain text; open Windows\installer\cp99-setup.ps1 in Notepad to see what they do.
  - Some antivirus tools flag winhttp.dll. It is BepInEx's mod loader (BepInEx 5.4.23.5, 32-bit),
    the standard loader for Unity game mods.
  - Windows Firewall may ask for network access the first time you host. Allow it.
  - Custom arenas don't work online yet.
  - Manual install: copy everything inside your system folder's "files" folder into the game
    folder (Steam > right-click ClusterPuck 99 > Manage > Browse local files).
