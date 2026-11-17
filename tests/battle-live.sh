#!/usr/bin/env bash
# Live two-instance battle (finding 8): host and guest walk, a battle starts on frame 1500 on both PCs, the bot plays
# it. Prints the guest's position corrections and mismatches. Extra arguments go to both instances.
cd "$(dirname "$0")/.."
. tests/game-dir.sh; O="$GAME/BepInEx/SosOnline"
S="$(cygpath -m "$PWD/tests/scripts")"
COMMON="--sosonline-delay=3 --sosonline-encounter=1500 --sosonline-bot --sosonline-quit=${QUIT:-3600} $*"
tests/session.sh 27100 "--sosonline-start=0 --sosonline-guests=1 --sosonline-seed=777 --sosonline-script=$S/host-walk.txt $COMMON" "--sosonline-script=$S/guest-join-walk.txt $COMMON" 4
sleep 10; while tasklist | grep -qi SeaOfStars; do sleep 3; done
grep -E "\[clock\]|mismatch|corrected" "$O/log-27101.txt" | cut -c1-220
echo "corrections: $(grep -c corrected "$O/log-27101.txt"), mismatches: $(grep -c mismatch "$O/log-27101.txt"), snapshots: $(wc -l < "$O/state-27101.log")"
