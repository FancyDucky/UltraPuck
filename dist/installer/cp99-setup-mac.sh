#!/usr/bin/env bash
# UltraPuck installer / uninstaller for the native Mac version of ClusterPuck 99.
#   bash install-mac.sh            install or update
#   bash uninstall-mac.sh          remove
# Optional: pass the game folder as the last argument to skip auto-detection.
# Works with the bash 3.2 that ships with macOS.
set -u

MODE="${1:-install}"
GAME="${2:-}"
APPID=337960
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PAYLOAD="$(dirname "$HERE")/files-mac"

say()  { printf '%s\n' "$*"; }
ok()   { printf '\033[32m%s\033[0m\n' "$*"; }
warn() { printf '\033[33m%s\033[0m\n' "$*"; }
fail() { printf '\n\033[31m%s\033[0m\n' "$*"; exit 1; }

# ---------------------------------------------------------------- find the game

steam_libraries() {
  local r="$HOME/Library/Application Support/Steam"
  [ -d "$r" ] || return 0
  printf '%s\n' "$r"
  if [ -f "$r/steamapps/libraryfolders.vdf" ]; then
    sed -n 's/^[[:space:]]*"path"[[:space:]]*"\(.*\)"[[:space:]]*$/\1/p' "$r/steamapps/libraryfolders.vdf"
  fi
}

find_game() {
  local lib folder acf d
  while IFS= read -r lib; do
    [ -n "$lib" ] || continue
    folder="ClusterPuck 99"
    acf="$lib/steamapps/appmanifest_$APPID.acf"
    if [ -f "$acf" ]; then
      d="$(sed -n 's/^[[:space:]]*"installdir"[[:space:]]*"\(.*\)"[[:space:]]*$/\1/p' "$acf" | head -n1)"
      [ -n "$d" ] && folder="$d"
    fi
    if [ -d "$lib/steamapps/common/$folder" ]; then
      printf '%s\n' "$lib/steamapps/common/$folder"
      return 0
    fi
  done < <(steam_libraries | awk '!seen[$0]++')
  return 1
}

find_app() {  # the game's .app bundle inside the game folder
  local a
  for a in "$GAME"/*.app; do
    if [ -d "$a" ]; then printf '%s\n' "$a"; return 0; fi
  done
  return 1
}

# ---------------------------------------------------------------- helpers

wait_game_closed() {
  while pgrep -f "$APP/Contents/MacOS/" >/dev/null 2>&1; do
    warn "ClusterPuck 99 is running. Quit the game, then press Enter."
    read -r _
  done
}

contains() { [ -f "$1" ] && grep -Fxq -- "$2" "$1"; }

remove_empty_dirs() {  # remove_empty_dirs <root> <dir>: delete <dir> and empty parents up to <root>
  local root="$1" dir="$2"
  while [ "${#dir}" -gt "${#root}" ] && [ -d "$dir" ] && [ -z "$(ls -A "$dir")" ]; do
    rmdir "$dir"; dir="$(dirname "$dir")"
  done
}

launch_option() { printf '"%s/run_bepinex.sh" %%command%%' "$GAME"; }

launch_option_help() {
  local opt
  opt="$(launch_option)"
  say ''
  warn 'ONE MORE STEP (Steam needs this to load the mod):'
  say  '  In Steam: right-click ClusterPuck 99 > Properties > General > Launch Options, and paste:'
  say  ''
  say  "      $opt"
  say  ''
  if printf '%s' "$opt" | pbcopy 2>/dev/null; then
    ok '  (Already copied: click in the Launch Options box and press Command+V.)'
  fi
}

check_arch() {
  local name exe info
  name="$(defaults read "$APP/Contents/Info" CFBundleExecutable 2>/dev/null)"
  exe="$APP/Contents/MacOS/$name"
  [ -n "$name" ] && [ -f "$exe" ] || return 0
  info="$(file -b "$exe")"
  case "$info" in
    *x86_64*|*arm64*) ;;
    *) fail "This copy of ClusterPuck 99 is 32-bit only ($info).
The mod loader needs the 64-bit game. Make sure Steam has finished updating the game, then try again." ;;
  esac
}

# ---------------------------------------------------------------- install

install_mod() {
  [ -d "$PAYLOAD" ] || fail "The mod files are missing. Extract the whole zip first, then run this from the extracted folder."
  local version="unknown"
  [ -f "$HERE/version.txt" ] && version="$(tr -d '\r\n' < "$HERE/version.txt")"

  local backup="$GAME/CP99Online-backup"
  local files_list="$backup/files.txt" backed_list="$backup/backedup.txt"
  if [ -f "$backup/version.txt" ]; then
    say "Updating UltraPuck $(cat "$backup/version.txt") -> $version"
  else
    say "Installing UltraPuck $version"
    mkdir -p "$backup" || fail "Can't write to the game folder: $GAME"
    if [ -d "$GAME/BepInEx" ]; then echo 1 > "$backup/had_bepinex"; else echo 0 > "$backup/had_bepinex"; fi
    : > "$files_list"; : > "$backed_list"
  fi

  local count=0 rel src dest
  while IFS= read -r -d '' rel; do
    rel="${rel#./}"
    src="$PAYLOAD/$rel"
    dest="$GAME/$rel"
    # Back up a file the first time we replace it, unless it's already this mod's copy.
    if [ -f "$dest" ] && ! cmp -s "$src" "$dest" && ! contains "$files_list" "$rel" && ! contains "$backed_list" "$rel"; then
      mkdir -p "$(dirname "$backup/$rel")"
      cp -p "$dest" "$backup/$rel"
      printf '%s\n' "$rel" >> "$backed_list"
      say "  backed up $rel"
    fi
    mkdir -p "$(dirname "$dest")"
    cp "$src" "$dest" || fail "Couldn't copy $rel"
    contains "$files_list" "$rel" || printf '%s\n' "$rel" >> "$files_list"
    count=$((count + 1))
  done < <(cd "$PAYLOAD" && find . -type f -print0)
  printf '%s\n' "$version" > "$backup/version.txt"

  # Files from a downloaded zip are quarantined, which can stop macOS from loading the mod loader.
  chmod +x "$GAME/run_bepinex.sh"
  xattr -dr com.apple.quarantine "$GAME/run_bepinex.sh" "$GAME/libdoorstop.dylib" "$GAME/BepInEx" 2>/dev/null || true
  codesign --force --sign - "$GAME/libdoorstop.dylib" >/dev/null 2>&1 || true

  say ''
  ok "Installed $count files into:"
  ok "  $GAME"
  launch_option_help
  say 'Then launch ClusterPuck 99 from Steam. The main menu gets Play Online and Mods entries.'
  say 'To play together: Play Online > Host Game, then Invite Friends. Friends need this mod too.'
}

# ---------------------------------------------------------------- uninstall

uninstall_mod() {
  local backup="$GAME/CP99Online-backup"
  if [ ! -f "$backup/files.txt" ]; then
    warn 'No installer record found (the mod was probably copied in by hand).'
    local f
    for f in run_bepinex.sh libdoorstop.dylib .doorstop_version; do
      if [ -e "$GAME/$f" ]; then rm -f "$GAME/$f"; say "  removed $f"; fi
    done
    if [ -d "$GAME/BepInEx/plugins/CP99Online" ]; then rm -rf "$GAME/BepInEx/plugins/CP99Online"; say '  removed BepInEx/plugins/CP99Online'; fi
    say ''
    ok 'Mod disabled. Also clear the Launch Options you added in Steam.'
    return
  fi

  say "Uninstalling UltraPuck $(cat "$backup/version.txt" 2>/dev/null)"
  local other_plugins=0
  if [ -d "$GAME/BepInEx/plugins" ] && ls -A "$GAME/BepInEx/plugins" | grep -vxq 'CP99Online'; then other_plugins=1; fi

  local rel
  while IFS= read -r rel; do
    [ -n "$rel" ] || continue
    rm -f "$GAME/$rel"
    remove_empty_dirs "$GAME" "$(dirname "$GAME/$rel")"
  done < "$backup/files.txt"
  rm -rf "$GAME/BepInEx/plugins/CP99Online"
  rm -f "$GAME/BepInEx/config/cp99.online.cfg" "$GAME/BepInEx/config/ultrapuck.mods.cfg"

  while IFS= read -r rel; do
    [ -n "$rel" ] || continue
    if [ -f "$backup/$rel" ]; then
      mkdir -p "$(dirname "$GAME/$rel")"
      cp -p "$backup/$rel" "$GAME/$rel"
      say "  restored $rel"
    fi
  done < "$backup/backedup.txt"

  if [ "$(cat "$backup/had_bepinex" 2>/dev/null)" = "0" ] && [ "$other_plugins" = "0" ] && [ -d "$GAME/BepInEx" ]; then
    rm -rf "$GAME/BepInEx"
    say '  removed BepInEx'
  fi
  rm -rf "$backup"

  say ''
  ok 'UltraPuck was removed. Mods you added in the Mods folder were left alone.'
  say 'Also clear the Launch Options in Steam (right-click ClusterPuck 99 > Properties > General).'
}

# ---------------------------------------------------------------- main

say "UltraPuck - $MODE (Mac)"
say ''
[ "$(uname -s)" = "Darwin" ] || fail "This is the Mac installer. On Linux use install-linux.sh, on Windows Install UltraPuck.bat."
if [ -z "$GAME" ]; then
  GAME="$(find_game)" || fail "Couldn't find ClusterPuck 99 in your Steam libraries.
Run this again with the game folder at the end: in Steam, right-click ClusterPuck 99 > Manage >
Browse local files. In Terminal type  bash ${MODE}-mac.sh  and a space, then drag that folder in."
fi
GAME="${GAME%/}"
[ -d "$GAME" ] || fail "Folder not found: $GAME"
say "Game folder: $GAME"

APP="$(find_app)" || fail "No ClusterPuck 99 app found in that folder. Is the game fully installed in Steam?"

case "$MODE" in
  install)   check_arch; wait_game_closed; install_mod ;;
  uninstall) wait_game_closed; uninstall_mod ;;
  *) fail "Unknown mode: $MODE" ;;
esac
