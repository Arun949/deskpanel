#!/bin/sh
# Opens the DeskPanel settings page with the agent's current one-time login link.
# The link lives in an owner-only file next to the agent's data; each link works once, and the agent writes a new one.
set -eu
home="${DESKPANEL_HOME:-$HOME/Library/Application Support/DeskPanel}"
link_file="$home/admin-link"
if [ ! -r "$link_file" ]; then
  echo "No admin link at $link_file. Is the agent running (and is DESKPANEL_HOME the same)?" >&2
  exit 1
fi
exec open "$(cat "$link_file")"
