#!/usr/bin/env bash
# Three-instance drop-in: host (27100) and guest 1 (27101) walk; guest 2 (27102) joins JOIN_DELAY seconds later.
# HOSTFLAGS goes to the host and guest 1, JOINFLAGS to the joiner. Prints the drop-in log lines and compares the
# joiner's and guest 1's state logs with the host's from the joiner's first snapshot on.
cd "$(dirname "$0")/.."
. tests/game-dir.sh; O="$GAME/BepInEx/SosOnline"
S="$(cygpath -m "$PWD/tests/scripts")"
Q=${QUIT:-4200}
COMMON="--sosonline-delay=3 --sosonline-quit=$Q"
tests/session.sh 27100 "--sosonline-start=0 --sosonline-guests=1 --sosonline-seed=777 --sosonline-script=$S/host-walk.txt $COMMON $HOSTFLAGS" \
  "--sosonline-script=$S/guest-join-walk.txt $COMMON $HOSTFLAGS" 4 \
  "$COMMON $JOINFLAGS" "${JOIN_DELAY:-30}"
sleep 10; while tasklist | grep -qi SeaOfStars; do sleep 3; done
grep -h -E "\[dropin\]|\[catchup\] [^f]|\[test\] (f[0-9]+ starting|playing own|joining from|own game)|postponed|\[barrier\].*(stopped|passed|holds)" "$O/log-27100.txt" "$O/log-27102.txt" | cut -c1-230
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
  echo "$p corrections: $(grep -c corrected "$O/log-$p.txt"), mismatches: $(grep -c mismatch "$O/log-$p.txt")"
done
