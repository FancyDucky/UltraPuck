CP99 Online {VERSION} - mod for ClusterPuck 99
Online play, no slowdown after boost, uncapped FPS, and an in-game mod menu (F8).

INSTALL ON WINDOWS
  1. Extract this zip (right-click > Extract All).
  2. Double-click "Install CP99 Online.bat".
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
  Host:  Main menu > Play Online > Host Game, then Invite Friends.
         Then Start Match, pick your own players and a built-in arena.
         Online players join their teams automatically.
  Join:  Accept the Steam invite, or Main menu > Play Online > Join a Friend.
         Team and Controls (keyboard or gamepad) are on the same screen.
  Mods:  Main menu > Mods (boost slowdown, FPS limit). F8 (or hold View/Back on a controller)
         opens the overlay menu with extra options such as direct IP.
  "Host direct IP" also works but needs UDP port 27099 forwarded on the host's router.

UNINSTALL
  Double-click "Uninstall CP99 Online.bat". It removes the mod and puts back any
  files it replaced. (Steam > Properties > Installed Files > Verify also restores the game.)

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
