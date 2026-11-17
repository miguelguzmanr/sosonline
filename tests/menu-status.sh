#!/usr/bin/env bash
# Host (27100) and guest 1 (27101) walk; guest 1 pauses and opens a pause menu entry, the host opens the main menu.
# Guest 2 (27102) drops in by catching up. Screenshots on every PC at SHOTS show the menu banner (and, on the joiner,
# the catch-up screen if a shot falls inside its replay). Prints the [menu] log lines and compares states.
cd "$(dirname "$0")/.."
. tests/game-dir.sh; O="$GAME/BepInEx/SosOnline"
S="$(cygpath -m "$PWD/tests/scripts")"
COMMON="--sosonline-delay=3 --sosonline-quit=${QUIT:-3600} --sosonline-shots=${SHOTS:-900,2260,2420,2980} $EXTRA"
tests/session.sh 27100 "--sosonline-start=0 --sosonline-guests=1 --sosonline-seed=777 --sosonline-script=$S/${HOSTSCRIPT:-host-menu.txt} $COMMON" \
  "--sosonline-script=$S/${GUESTSCRIPT:-guest-pause.txt} $COMMON" 4 \
  "$COMMON $JOINFLAGS" "${JOIN_DELAY:-30}"
sleep 10; while tasklist | grep -qi SeaOfStars; do sleep 3; done
for p in 27100 27101 27102; do echo "--- $p"; grep -h -E "\[menu\]|\[catchup\] (caught|merging)" "$O/log-$p.txt" | cut -c1-200; done
for p in 27101 27102; do
  python3 - "$O/state-27100.log" "$O/state-$p.log" "$p" <<'PY'
import sys
def load(p):
    d = {}
    for line in open(p, encoding='utf-8'):
        t = line.rstrip('\n').split(' ', 2)
        if len(t) > 2 and not t[2].startswith('loading'): d[t[1]] = t[2]
    return d
h, g = load(sys.argv[1]), load(sys.argv[2])
common = [f for f in g if f in h]
diff = [f for f in common if h[f] != g[f]]
print(f"{sys.argv[3]}: {len(common)} snapshots compared, {len(diff)} differ" + (f", first {diff[0]}" if diff else ""))
PY
done
