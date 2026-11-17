#!/usr/bin/env bash
# Battle turns with two players competing for them over a simulated internet link.
# Host (P1) and guest (P2) bots press menus on different schedules (BOTPHASE), the guest sometimes switches to the
# other character before confirming (SWITCH), and every packet waits LAG ms + random JITTER ms (SPIKES % of them
# 150-300 ms more). Prints both PCs' turn decisions side by side and the first state snapshot that differs.
# Usage: tests/battle-turns.sh [extra flags for both]   env: LAG JITTER SPIKES SWITCH QUIT ENCOUNTER DELAY
cd "$(dirname "$0")/.."
. tests/game-dir.sh; O="$GAME/BepInEx/SosOnline"
S="$(cygpath -m "$PWD/tests/scripts")"
NET="--sosonline-lag=${LAG:-20} --sosonline-jitter=${JITTER:-25} --sosonline-spikes=${SPIKES:-3}"
COMMON="--sosonline-delay=${DELAY:-4} --sosonline-encounter=${ENCOUNTER:-1500} --sosonline-bot --sosonline-quit=${QUIT:-6000} $NET $*"
rm -f "$O/log-27100.txt" "$O/log-27101.txt" "$O/state-27100.log" "$O/state-27101.log"
tests/session.sh 27100 "--sosonline-start=0 --sosonline-guests=1 --sosonline-seed=777 --sosonline-botphase=0 --sosonline-script=$S/host-walk.txt $COMMON" \
  "--sosonline-botphase=${GUESTPHASE:-22} --sosonline-botswitch=${SWITCH:-3} --sosonline-script=$S/guest-join-walk.txt $COMMON" 4
sleep 10; while tasklist | grep -qi SeaOfStars; do sleep 3; done

echo "== turn decisions (host | guest), first 40"
paste -d'|' <(grep -o "\[combat\] f[0-9]* .*" "$O/log-27100.txt" | head -40) <(grep -o "\[combat\] f[0-9]* .*" "$O/log-27101.txt" | head -40) | cut -c1-200
echo "== first differing turn decision"
diff <(grep -o "\[combat\] f[0-9]* .*" "$O/log-27100.txt") <(grep -o "\[combat\] f[0-9]* .*" "$O/log-27101.txt") | head -6
echo "== timing verdict differences"
diff <(grep -o "\[timing\] .*" "$O/log-27100.txt") <(grep -o "\[timing\] .*" "$O/log-27101.txt") | head -6
echo "== guest sync"
grep -E "mismatch|resync\]|corrected" "$O/log-27101.txt" | head -5 | cut -c1-220
echo "battle frames: $(grep -c "combat=\[on" "$O/state-27100.log") host snapshots in battle; guest mismatches: $(grep -c mismatch "$O/log-27101.txt"); turn lines host/guest: $(grep -c "\[combat\]" "$O/log-27100.txt")/$(grep -c "\[combat\]" "$O/log-27101.txt")"
