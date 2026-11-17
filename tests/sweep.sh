#!/usr/bin/env bash
# Every loopback test, one after another (about 50 minutes), for a check before a release.
# Usage: tests/sweep.sh [output folder]   (default: BepInEx/SosOnline/sweep in the game folder)
# Writes <test>.txt (the script's output, how long it took, any errors in the logs) and each instance's log per test,
# and progress.txt. Close the game first: the tests start their own instances. Builds must not run meanwhile.
cd "$(dirname "$0")/.." || exit 1
REPO="$PWD"
. tests/game-dir.sh; O="$GAME/BepInEx/SosOnline"
OUT="${1:-$O/sweep}"; mkdir -p "$OUT"; rm -f "$OUT"/*.txt
SAVES="$LOCALAPPDATA/../LocalLow/Sabotage Studio/Sea of Stars"
S="$(cygpath -m "$REPO/tests/scripts")"
# Generated input scripts: --sosonline-script paths can't have spaces, and the game folder's has ("Sea of Stars").
GEN="$(cygpath -m "$(mktemp -d)")"

clean() {
  taskkill //F //IM SeaOfStars.exe >/dev/null 2>&1; sleep 3
  rm -rf "$SAVES"/SosOnlineTest-* "$SAVES"/SosOnlineSession-27*
}
waitgames() { local t=0; sleep 10; while tasklist | grep -qi SeaOfStars && [ $t -lt ${1:-400} ]; do sleep 3; t=$((t+3)); done; }
keeplogs() {
  local name=$1
  for p in 27100 27101 27102; do
    [ -f "$O/log-$p.txt" ] || continue
    grep -h "\[Error\|Exception\|over 80 characters\|too long for a button" "$O/log-$p.txt" | grep -v "During invoking native->managed" | head -5 | sed "s/^/[$p] /" >> "$OUT/$name.txt"
    cp "$O/log-$p.txt" "$OUT/$name-log-$p.txt"
  done
}
# run NAME COMMAND...: a test script; its output, duration and errors.
run() {
  local name=$1; shift
  clean
  local start=$(date +%s)
  echo "### $name: $*" > "$OUT/$name.txt"
  "$@" >> "$OUT/$name.txt" 2>&1
  waitgames
  echo "### took $(( $(date +%s) - start )) s" >> "$OUT/$name.txt"
  keeplogs "$name"
  echo "done $name" >> "$OUT/progress.txt"
}
# session NAME SECONDS HOSTFLAGS GUESTFLAGS: host playing its own save + one guest, stopped after SECONDS.
session() {
  local name=$1 secs=$2 hf=$3 gf=$4
  clean
  echo "### $name" > "$OUT/$name.txt"
  bash tests/session.sh 27100 "--sosonline-play=0 --sosonline-seed=777 $hf" "$gf" 25 >> "$OUT/$name.txt" 2>&1
  sleep "$secs"; taskkill //F //IM SeaOfStars.exe >/dev/null 2>&1; sleep 3
  for p in 27100 27101; do
    echo "--- $p" >> "$OUT/$name.txt"
    grep -h -E "\[coop\]|\[toast\]|stopped at frame|\[session\] (left|back to|P[23] |ended|slot)|camping|mismatch|Error|Exception" "$O/log-$p.txt" | grep -v "During invoking" | tail -16 >> "$OUT/$name.txt"
  done
  keeplogs "$name"
  echo "done $name" >> "$OUT/progress.txt"
}

rm -f "$OUT/progress.txt"
bash tests/display-prefs.sh save > /dev/null 2>&1

# Battles split intermittently: several runs.
for i in 1 2 3; do run battle-turns-$i env ENCOUNTER=650 QUIT=5000 bash tests/battle-turns.sh --sosonline-start=0a; done
run save-p1 env QUIT=1500 bash tests/save-sync.sh --sosonline-saveat=600
run save-p2 env SAVER=guest QUIT=1500 bash tests/save-sync.sh --sosonline-saveat=600 --sosonline-saveby=1
run resync env DIVERGE=1600 QUIT=9500 EXTRA="--sosonline-encounter=1500 --sosonline-bot --sosonline-lag=20 --sosonline-jitter=25" bash tests/resync.sh
run battle-live bash tests/battle-live.sh
run dropin-catchup bash tests/dropin.sh
run dropin-reload env HOSTFLAGS=--sosonline-nocatchup JOINFLAGS=--sosonline-nocatchup bash tests/dropin.sh
run dropin-loadatjoin env HOSTFLAGS="--sosonline-nocatchup --sosonline-loadatjoin" JOINFLAGS=--sosonline-nocatchup bash tests/dropin.sh
run dropin-owngame env JOINFLAGS=--sosonline-play=0 bash tests/dropin.sh
run dropin-two bash tests/dropin-two.sh
run stage1 bash tests/stage1.sh
run menu-status bash tests/menu-status.sh

# Leaving and ending: afterwards every PC should show "P1 ... [kb]" and the other characters with the party AI.
session leave-guest 110 "--sosonline-script=$S/host-walk.txt" "--sosonline-pauseleave=1500"
session end-playing 110 "--sosonline-endsession=1500 --sosonline-script=$S/host-walk.txt" ""
session end-joining 80 "--sosonline-endsession=120 --sosonline-script=$S/host-walk.txt" ""
session guest-killed 110 "--sosonline-script=$S/host-walk.txt" "--sosonline-kill=1500"

# Assign Players with both players on one character (screenshot on both PCs at 1740).
printf '1690 UIAssignMoveHorizontal 10 -1\n' > "$GEN/assign-guest-left.txt"
A="--sosonline-assigntest=1640 --sosonline-shots=1740 --sosonline-quit=1780"
session assign 95 "$A" "--sosonline-script=$(cygpath -m "$GEN/assign-guest-left.txt") $A"
cp "$O"/shot-27100-001740.png "$OUT/assign-27100.png" 2>/dev/null

# Camping: intro at 1500, the Rest choice at 2400 (a held Confirm picks it), end of camp.
printf '2470 Interact 4\n2560 UI_Confirm 30\n2680 UI_Confirm 120\n' > "$GEN/camp-host.txt"
printf '1800 MoveHorizontal 30 1\n' > "$GEN/camp-guest.txt"
C="--sosonline-campstart=1500 --sosonline-campstop=2400 --sosonline-shots=1900,2560,3200 --sosonline-quit=3800"
session camp 155 "--sosonline-script=$(cygpath -m "$GEN/camp-host.txt") $C" "--sosonline-script=$(cygpath -m "$GEN/camp-guest.txt") $C"
for f in "$O"/shot-2710*-001900.png "$O"/shot-2710*-003200.png; do cp "$f" "$OUT/camp-$(basename "$f")" 2>/dev/null; done

# Title screen menus and the restart popup (hosts a real Steam lobby briefly).
clean
rm -f "$O"/ui-*-steam.png
cmd //c "$(cygpath -w "$REPO/tests/uitest.cmd")" --sosonline-fakeupdate=0.1.999 > "$OUT/uitest.txt" 2>&1
waitgames 240
grep -h "\[test\]\|\[menu\] popup\|Error\|Exception" "$O/log-steam.txt" | grep -v "During invoking" >> "$OUT/uitest.txt"
for f in "$O"/ui-*-steam.png; do cp "$f" "$OUT/"; done
echo "done uitest" >> "$OUT/progress.txt"

clean
rm -f "$O"/shot-2710*.png "$O"/ui-*-steam.png
bash tests/display-prefs.sh restore > /dev/null 2>&1
echo "SWEEP FINISHED" >> "$OUT/progress.txt"
echo "Results in $OUT"
