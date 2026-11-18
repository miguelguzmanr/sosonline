#!/usr/bin/env bash
# Screenshots for the README, from loopback test sessions at 1280x720 with neutral player names (never Steam names:
# the repository is public). Every instance takes a screenshot every 50 frames; the picks go to docs/screenshots by
# hand. Output: BepInEx/SosOnline/readme/<run>-shot-<port>-<frame>.png in the game folder. About 12 minutes.
# Usage: tests/readme-shots.sh [run...]   runs: online party dropin assign battle resync (default: all)
# Close the game first. Tests play from sandbox copies of the saves (--sosonline-start / --sosonline-play).
cd "$(dirname "$0")/.." || exit 1
. tests/game-dir.sh; O="$GAME/BepInEx/SosOnline"; OUT="$O/readme"; mkdir -p "$OUT"
S="$(cygpath -m "$PWD/tests/scripts")"
SAVES="$LOCALAPPDATA/../LocalLow/Sabotage Studio/Sea of Stars"
export WIN_W=1280 WIN_H=720
every() { seq -s, "$1" 50 "$2"; }
clean() { rm -f "$O"/shot-*.png; rm -rf "$SAVES"/SosOnlineTest-* "$SAVES"/SosOnlineSession-27*; }
waitgames() { sleep 10; while tasklist | grep -qi SeaOfStars; do sleep 3; done; }
keep() { for f in "$O"/shot-*.png; do [ -f "$f" ] && mv "$f" "$OUT/$1-$(basename "$f")"; done; clean; }
names=("--sosonline-name=Alex" "--sosonline-name=Sam" "--sosonline-name=Robin")

online() { # The title screen's Online page, hosting (UDP, no Steam lobby), Options, the version line.
(cd "$GAME" && ./SeaOfStars.exe -screen-fullscreen 0 -screen-width 1280 -screen-height 720 --sosonline-udp=27100 --sosonline-onlineshot >/dev/null 2>&1 &)
waitgames; mv "$O"/ui-online-*.png "$OUT/" 2>/dev/null
}

party() { # Three players from the game's Autosave Slot 1 (file 0a; a sandbox copy), walking together.
P="--sosonline-delay=3 --sosonline-quit=2600 --sosonline-shots=$(every 1200 2550)"
tests/session.sh 27100 "--sosonline-start=0a --sosonline-guests=2 --sosonline-seed=777 --sosonline-script=$S/host-walk.txt ${names[0]} $P"   "--sosonline-script=$S/guest-join-walk.txt ${names[1]} $P" 4 "--sosonline-script=$S/guest-join-walk.txt ${names[2]} $P" 4
waitgames; keep party
}

dropin() { # Drop-in and the local pause: the host plays its own save; Sam drops in, walks, pauses on their PC only (1700),
#    then leaves; Robin drops in later.
C="--sosonline-delay=3 --sosonline-quit=2600 --sosonline-shots=$(every 300 2550)"
tests/session.sh 27100 "--sosonline-play=0 --sosonline-seed=777 --sosonline-script=$S/host-walk.txt ${names[0]} $C" \
  "--sosonline-script=$S/guest-stage1-leave.txt --sosonline-pausechoose=1790:Leave_co-op,1830:Leave ${names[1]} $C" 25 \
  "${names[2]} $C" 30
waitgames; keep dropin
}

assign() { # Assign Players: Sam moves to Alex's character.
printf '1690 UIAssignMoveHorizontal 10 -1\n' > "$OUT/assign-guest.txt"
A="--sosonline-delay=3 --sosonline-assigntest=1640 --sosonline-shots=$(every 1650 1800) --sosonline-quit=1820"
tests/session.sh 27100 "--sosonline-play=0 --sosonline-seed=777 --sosonline-script=$S/host-walk.txt ${names[0]} $A" \
  "--sosonline-script=$(cygpath -m "$OUT/assign-guest.txt") ${names[1]} $A" 25
waitgames; keep assign
}

battle() { # A battle together, the bot timing hits.
B="--sosonline-delay=3 --sosonline-encounter=1500 --sosonline-bot --sosonline-quit=2700 --sosonline-shots=$(every 1550 2650)"
tests/session.sh 27100 "--sosonline-start=0 --sosonline-guests=1 --sosonline-seed=777 --sosonline-script=$S/host-walk.txt ${names[0]} $B" \
  "--sosonline-script=$S/guest-join-walk.txt ${names[1]} $B" 4
waitgames; keep battle
}

resync() { # Out of sync: Sam's game diverges at 1500 and rejoins by replay.
R="--sosonline-delay=3 --sosonline-quit=2400 --sosonline-shots=$(every 1450 2350)"
tests/session.sh 27100 "--sosonline-start=0 --sosonline-guests=1 --sosonline-seed=777 --sosonline-script=$S/host-walk.txt ${names[0]} $R" \
  "--sosonline-script=$S/guest-join-walk.txt --sosonline-hpdiverge=1500 ${names[1]} $R" 4
waitgames; keep resync
}

bash tests/display-prefs.sh save
clean
for run in ${@:-online party dropin assign battle resync}; do echo "run $run"; "$run"; done
bash tests/display-prefs.sh restore
ls "$OUT" | grep -c png
