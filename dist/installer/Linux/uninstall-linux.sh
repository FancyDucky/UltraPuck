#!/usr/bin/env bash
# Linux / Steam Deck: run with   bash uninstall-linux.sh
exec bash "$(dirname "$0")/installer/cp99-setup.sh" uninstall "$@"
