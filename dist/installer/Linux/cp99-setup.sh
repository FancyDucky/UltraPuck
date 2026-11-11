#!/usr/bin/env bash
# UltraPuck installer / uninstaller for ClusterPuck 99 on Linux and Steam Deck (runs through Proton).
#   bash install-linux.sh            install or update
#   bash uninstall-linux.sh          remove and restore replaced files
# Optional: pass the game folder as the last argument to skip auto-detection.
set -u

MODE="${1:-install}"
GAME="${2:-}"
APPID=337960
EXE="ClusterPuck99.exe"
LAUNCH_OPT='WINEDLLOVERRIDES="winhttp=n,b" %command%'
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PAYLOAD="$(dirname "$HERE")/files"

say()  { printf '%s\n' "$*"; }
ok()   { printf '\033[32m%s\033[0m\n' "$*"; }
warn() { printf '\033[33m%s\033[0m\n' "$*"; }
fail() { printf '\n\033[31m%s\033[0m\n' "$*"; exit 1; }

# ---------------------------------------------------------------- find the game

steam_libraries() {
  local roots=(
    "$HOME/.steam/steam"
    "$HOME/.steam/root"
    "$HOME/.local/share/Steam"
    "$HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam"
    "$HOME/snap/steam/common/.local/share/Steam"
  )
  local r
  for r in "${roots[@]}"; do
    [ -d "$r" ] || continue
    printf '%s\n' "$r"
    if [ -f "$r/steamapps/libraryfolders.vdf" ]; then
      sed -n 's/^[[:space:]]*"path"[[:space:]]*"\(.*\)"[[:space:]]*$/\1/p' "$r/steamapps/libraryfolders.vdf" | sed 's/\\\\/\\/g'
    fi
  done
}

find_game() {
  local lib folder acf
  while IFS= read -r lib; do
    [ -n "$lib" ] || continue
    folder="ClusterPuck 99"
    acf="$lib/steamapps/appmanifest_$APPID.acf"
    if [ -f "$acf" ]; then
      local d
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

# ---------------------------------------------------------------- helpers

wait_game_closed() {
  command -v pgrep >/dev/null 2>&1 || return 0
  while pgrep -f "$EXE" >/dev/null 2>&1; do
    warn "ClusterPuck 99 is running. Close the game, then press Enter."
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

launch_option_help() {
  say ''
  warn 'ONE MORE STEP (Steam needs this to load the mod under Proton):'
  say  '  In Steam: right-click ClusterPuck 99 > Properties > General > Launch Options, and paste:'
  say  ''
  say  "      $LAUNCH_OPT"
  say  ''
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
    for f in winhttp.dll doorstop_config.ini .doorstop_version; do
      [ -e "$GAME/$f" ] && rm -f "$GAME/$f" && say "  removed $f"
    done
    [ -d "$GAME/BepInEx/plugins/CP99Online" ] && rm -rf "$GAME/BepInEx/plugins/CP99Online" && say '  removed BepInEx/plugins/CP99Online'
    say ''
    ok 'Mod disabled. You can also clear the Launch Options you added in Steam.'
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
  ok 'UltraPuck was removed.'
  say 'Also clear the Launch Options in Steam (right-click ClusterPuck 99 > Properties > General).'
}

# ---------------------------------------------------------------- main

say "UltraPuck - $MODE (Linux / Steam Deck)"
say ''
if [ -z "$GAME" ]; then
  GAME="$(find_game)" || fail "Couldn't find ClusterPuck 99 in your Steam libraries.
Run this again with the game folder at the end, for example:
  bash ${MODE}-linux.sh \"\$HOME/.local/share/Steam/steamapps/common/ClusterPuck 99\"
(Steam: right-click the game > Manage > Browse local files shows the folder.)"
fi
GAME="${GAME%/}"
[ -d "$GAME" ] || fail "Folder not found: $GAME"
say "Game folder: $GAME"

if [ ! -f "$GAME/$EXE" ]; then
  fail "This is the native Linux version of the game. The mod needs the Windows version running through Proton:
  In Steam: right-click ClusterPuck 99 > Properties > Compatibility >
  tick 'Force the use of a specific Steam Play compatibility tool' and pick Proton.
  Let Steam finish updating the game, then run this again."
fi

wait_game_closed
case "$MODE" in
  install)   install_mod ;;
  uninstall) uninstall_mod ;;
  *) fail "Unknown mode: $MODE" ;;
esac
