#!/usr/bin/env bash
# Maps the game's timed-hit grading: one solo run per press offset (frames relative to a reference frame),
# 4 instances at a time. Usage: battle-map.sh DELAY REF_FRAME "offsets..."
. "$(dirname "$0")/game-dir.sh"
OUT="$GAME/BepInEx/SosOnline/map"; mkdir -p "$OUT"
DELAY=$1; REF=$2; OFFSETS=$3; SCRIPTS="$(cd "$(dirname "$0")" && pwd -W)/scripts"
port=27200; batch=0
for k in $OFFSETS; do
  f=$((REF + k))
  echo "$f Interact 1" > "$OUT/press_$k.txt"
  (cd "$GAME" && ./SeaOfStars.exe -screen-fullscreen 0 -screen-width 480 -screen-height 270 --sosonline-udp=$port --sosonline-host --sosonline-guests=0 --sosonline-start=0 --sosonline-delay=$DELAY --sosonline-encounter=700 --sosonline-bot=menu --sosonline-trace --sosonline-script="$(cygpath -w "$OUT/press_$k.txt")" --sosonline-quit=$((REF + 400)) > /dev/null 2>&1 &)
  port=$((port + 1)); batch=$((batch + 1))
  if [ $batch -ge 4 ]; then sleep 15; while tasklist | grep -qi SeaOfStars; do sleep 3; done; batch=0; fi
done
sleep 15; while tasklist | grep -qi SeaOfStars; do sleep 3; done
