#!/usr/bin/env bash
# Two joiners in one area: the host (27100) plays alone and walks; joiner A (27101) drops in JOIN_DELAY seconds after
# the host starts, joiner B (27102) JOIN2_DELAY seconds after A. With Stage 3 both catch up from the same area entry:
# B replays A's merge (and A's leave, if AFLAGS makes A quit early, e.g. AFLAGS=--sosonline-quit=3000).
# HOSTFLAGS goes to the host, JOINFLAGS to both joiners, AFLAGS / BFLAGS to one of them. Compares both joiners'
# state logs with the host's.
cd "$(dirname "$0")/.."
. tests/game-dir.sh; O="$GAME/BepInEx/SosOnline"
S="$(cygpath -m "$PWD/tests/scripts")"
Q=${QUIT:-5400}
COMMON="--sosonline-delay=3 --sosonline-quit=$Q"
tests/session.sh 27100 "--sosonline-start=0 --sosonline-guests=0 --sosonline-seed=777 --sosonline-script=$S/host-walk.txt $COMMON $HOSTFLAGS" \
  "$COMMON $JOINFLAGS $AFLAGS" "${JOIN_DELAY:-30}" \
  "$COMMON $JOINFLAGS $BFLAGS" "${JOIN2_DELAY:-20}"
sleep 10; while tasklist | grep -qi SeaOfStars; do sleep 3; done
for p in 27100 27101 27102; do
  echo "--- $p"
  grep -h -E "\[dropin\]|\[catchup\] [^f]|roster change|left|leaves at|postponed|\[barrier\].*(stopped|passed|holds)" "$O/log-$p.txt" | cut -c1-230
done
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
