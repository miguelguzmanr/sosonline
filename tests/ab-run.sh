#!/usr/bin/env bash
# Runs solo battle instances in parallel. Each argument: "port|extra flags".
. "$(dirname "$0")/game-dir.sh"
for spec in "$@"; do
  port="${spec%%|*}"; extra="${spec#*|}"
  (cd "$GAME" && ./SeaOfStars.exe -screen-fullscreen 0 -screen-width 480 -screen-height 270 --sosonline-udp=$port --sosonline-host --sosonline-guests=0 --sosonline-start=0 --sosonline-trace --sosonline-quit=1600 $extra > /dev/null 2>&1 &)
done
sleep 15; while tasklist | grep -qi SeaOfStars; do sleep 3; done
