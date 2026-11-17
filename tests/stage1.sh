#!/usr/bin/env bash
# Stage 1 on loopback: the host plays its own save with the lobby open (no title-screen start). Guest A joins mid-game
# (solo to co-op), walks and leaves from the pause screen (co-op to solo). Guest B joins later (solo to co-op again).
# The host pretends each guest last played the last party member (--sosonline-rememberchar=last), so a join that gets
# another character is handed that one back. Compares each guest with the host, run by run.
cd "$(dirname "$0")/.."
. tests/game-dir.sh; O="$GAME/BepInEx/SosOnline"
S="$(cygpath -m "$PWD/tests/scripts")"
COMMON="--sosonline-delay=3 --sosonline-quit=${QUIT:-3000} $EXTRA"
tests/session.sh 27100 "--sosonline-play=0 --sosonline-seed=777 --sosonline-rememberchar=${CHAR:-last} --sosonline-quitruns=2 --sosonline-script=$S/host-walk.txt $COMMON $HOSTFLAGS" \
  "--sosonline-script=$S/guest-stage1-leave.txt --sosonline-pausechoose=1760:Leave_co-op,1800:Leave $COMMON" "${A_DELAY:-25}" \
  "$COMMON" "${B_DELAY:-75}"
sleep 10; while tasklist | grep -qi SeaOfStars; do sleep 3; done
for p in 27100 27101 27102; do echo "--- $p"; grep -h -E "\[session\] (co-op starts|back to solo|slot|mirrored)|\[dropin\] .*(join at|joining at)|\[characters\]|\[resync\]|stopped at frame|\[saves\]" "$O/log-$p.txt" | cut -c1-170; done
python3 - "$O" <<'PY'
import sys
O = sys.argv[1]
def runs(p):
    out, cur, prev = [], {}, None
    for line in open(p, encoding='utf-8'):
        t = line.rstrip('\n').split(' ', 2)
        if len(t) < 3 or t[2].startswith('loading'): continue
        f = int(t[1].lstrip('f'))
        if prev is not None and f < prev: out.append(cur); cur = {}
        prev = f; cur[f] = t[2]
    out.append(cur)
    return out
host = runs(f"{O}/state-27100.log")
for p in ("27101", "27102"):
    for g in runs(f"{O}/state-{p}.log"):
        if not g: continue
        best = max(host, key=lambda h: len(set(h) & set(g)))
        common = sorted(set(best) & set(g)); diff = [f for f in common if best[f] != g[f]]
        print(f"{p}: run {min(g)}..{max(g)}: {len(common)} snapshots compared with host run {host.index(best) + 1}, {len(diff)} differ" + (f", first f{diff[0]}" if diff else ""))
PY
