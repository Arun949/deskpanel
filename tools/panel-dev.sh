#!/bin/sh
# Runs the panel in a desktop browser against a local agent, through Vite's dev proxy.
# Usage: DESKPANEL_DEV_AUTH_FILE=<file holding "Device <id>.<key>"> tools/panel-dev.sh [vite args]
# The key is read from a file (mode 600) so it never appears in a command line or in .claude/launch.json.
set -eu
cd "$(dirname "$0")/../panel"
if [ -n "${DESKPANEL_DEV_AUTH_FILE:-}" ]; then
  DESKPANEL_DEV_AUTH="$(cat "$DESKPANEL_DEV_AUTH_FILE")"
  export DESKPANEL_DEV_AUTH
fi
exec npx vite "$@"
