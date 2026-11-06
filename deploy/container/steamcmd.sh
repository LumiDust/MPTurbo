#!/usr/bin/env bash
set -euo pipefail
cd /steam/steamcmd
# Bootstrap is copied from /opt only by the explicit install stage.
# SteamCMD self-updates exclusively inside its writable authentication cache.
exec ./steamcmd.sh +@sSteamCmdForcePlatformType linux "$@"
