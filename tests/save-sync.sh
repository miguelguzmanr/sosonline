#!/usr/bin/env bash
# Manual save at a save point in a two-PC session over a simulated internet link (LAG/JITTER/SPIKES).
# SAVER=host (default) or guest: who opens the save point and answers the questions. Prints both PCs' menu
# and view timelines and the guest's sync result.
cd "$(dirname "$0")/.."
. tests/game-dir.sh; O="$GAME/BepInEx/SosOnline"
S="$(cygpath -m "$PWD/tests/scripts")"
NET="--sosonline-lag=${LAG:-20} --sosonline-jitter=${JITTER:-25} --sosonline-spikes=${SPIKES:-3}"
COMMON="--sosonline-delay=${DELAY:-4} --sosonline-quit=${QUIT:-2000} $NET $*"
if [ "${SAVER:-host}" = "host" ]; then HS="--sosonline-script=$S/host-save.txt"; GS=""; else HS=""; GS="--sosonline-script=$S/host-save.txt"; fi
rm -f "$O/log-27100.txt" "$O/log-27101.txt" "$O/state-27100.log" "$O/state-27101.log"
tests/session.sh 27100 "--sosonline-start=0 --sosonline-guests=1 --sosonline-seed=777 $HS $COMMON" "$GS $COMMON" 4
sleep 10; while tasklist | grep -qi SeaOfStars; do sleep 3; done
for f in 27100 27101; do echo "== $f"; grep -o "\[menu\] f[0-9]* .*\|\[view\] f[0-9]* .*" "$O/log-$f.txt" | head -24; done
echo "== guest sync"; grep -E "mismatch|resync\]" "$O/log-27101.txt" | head -3 | cut -c1-200
echo "guest mismatches: $(grep -c mismatch "$O/log-27101.txt")"
