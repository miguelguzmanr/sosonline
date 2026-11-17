#!/usr/bin/env bash
# Spike B: does the area-entry autosave reproduce the live state? Usage: spike-b.sh [extra flags for both runs]
# Run A (port 27100) walks from slot 0 out of the Forbidden Cavern and back in (third load = area entry), autosaves
# at C, walks, fights (bot), and records its inputs. Run B (27200) loads that autosave and replays A's inputs,
# aligned on the entry load's end frame L. Then compares the per-frame state logs from L on.
cd "$(dirname "$0")/.."
. tests/game-dir.sh; O="$GAME/BepInEx/SosOnline"
SEED=${SEED:-12345}
wait_game() { sleep 5; while tasklist | grep -qi SeaOfStars; do sleep 3; done; }
# SKIP_A=1 reuses the last recording (spike C reruns B only). STATEEVERY sets B's snapshot interval.
if [ -z "$SKIP_A" ]; then
rm -f "$O"/spike-b-*
tests/session.sh 27100 "--sosonline-start=0 --sosonline-guests=0 --sosonline-delay=3 --sosonline-seed=$SEED --sosonline-stateevery=1 --sosonline-script=$(cygpath -m "$PWD/tests/scripts/spike-b-walk.txt") --sosonline-bot --sosonline-entryload=3 --sosonline-saveatentry --sosonline-partyatentry=BepInEx/SosOnline/spike-b-party.txt --sosonline-encounterat=700 --sosonline-record=BepInEx/SosOnline/spike-b-inputs.bin --sosonline-quit=5000 --sosonline-nocatchup $*"
wait_game
cp "$O/state-27100.log" "$O/state-A.log"; cp "$O/log-27100.txt" "$O/log-A.txt"
fi
LA=$(grep -o "entry load 3 ends on f[0-9]*" "$O/log-A.txt" | grep -o "[0-9]*$")
[ -z "$LA" ] && { echo "run A: no entry load"; exit 1; }
tests/session.sh 27200 "--sosonline-start=0a --sosonline-guests=0 --sosonline-delay=3 --sosonline-seed=$SEED --sosonline-stateevery=${STATEEVERY:-1} --sosonline-sandboxfile=BepInEx/SosOnline/spike-b-autosave.sos,BepInEx/SosOnline/spike-b-index.sos --sosonline-entryload=1 ${PARTY:---sosonline-partyatentry=BepInEx/SosOnline/spike-b-party.txt} --sosonline-encounterat=700 --sosonline-replay=BepInEx/SosOnline/spike-b-inputs.bin --sosonline-replayfrom=$LA --sosonline-quit=${QUIT_B:-4250} --sosonline-nocatchup $*"
wait_game
cp "$O/state-27200.log" "$O/state-B.log"; cp "$O/log-27200.txt" "$O/log-B.txt"
LB=$(grep -o "entry load 1 ends on f[0-9]*" "$O/log-B.txt" | grep -o "[0-9]*$")
echo "L_A=$LA L_B=$LB"
python3 tests/spike-b-compare.py "$O/state-A.log" "$LA" "$O/state-B.log" "$LB"
