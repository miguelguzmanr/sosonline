#!/usr/bin/env bash
# Automatic re-sync (Stage 1): host and guest start together; at DIVERGE the guest's party HP drops by 1 on its PC only.
# The guest's state check finds it, it leaves on a frame the host picks and rejoins: by replaying the area log when
# the host has one (even mid-battle), else with a reload in place once the game allows saving.
# Compares the guest with the host after the re-sync.
cd "$(dirname "$0")/.."
. tests/game-dir.sh; O="$GAME/BepInEx/SosOnline"
S="$(cygpath -m "$PWD/tests/scripts")"
COMMON="--sosonline-delay=3 --sosonline-quit=${QUIT:-3600} $EXTRA"
tests/session.sh 27100 "--sosonline-start=0 --sosonline-guests=1 --sosonline-seed=777 --sosonline-script=$S/host-walk.txt $COMMON" \
  "--sosonline-script=$S/guest-join-walk.txt --sosonline-hpdiverge=${DIVERGE:-1500} $COMMON" 4
sleep 10; while tasklist | grep -qi SeaOfStars; do sleep 3; done
for p in 27100 27101; do echo "--- $p"; grep -h -E "\[resync\]|hpdiverge|mismatch|\[dropin\] .*(join at|joining at)|\[catchup\] (merging|start|.*replay diverged)|stopped at frame" "$O/log-$p.txt" | cut -c1-170; done
python3 - "$O" <<'PY'
import sys
O = sys.argv[1]
def load(p):
    d = {}
    for line in open(p, encoding='utf-8'):
        t = line.rstrip('\n').split(' ', 2)
        if len(t) > 2 and not t[2].startswith('loading'): d[int(t[1].lstrip('f'))] = t[2]
    return d
h, g = load(f"{O}/state-27100.log"), load(f"{O}/state-27101.log")
import re
def joins(path):
    # A reload in place ("[dropin] joining at f") or a rejoin by replay ("[catchup] merging at f").
    for l in open(path, encoding='utf-8'):
        m = re.search(r'\[(?:dropin\] joining|catchup\] merging) at f(\d+)', l)
        if m: yield int(m.group(1))
join = max(joins(f"{O}/log-27101.txt"), default=None)
if join is None: print("guest never dropped in again"); sys.exit()
common = [f for f in g if f in h and f > join + 120]
diff = [f for f in common if h[f] != g[f]]
print(f"after the re-sync (join f{join}): {len(common)} snapshots compared, {len(diff)} differ" + (f", first f{diff[0]}" if diff else ""))
PY
