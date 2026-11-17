#!/usr/bin/env bash
# Summarizes battle-map.sh runs: offset, frame the game saw the press, first attack / block result.
. "$(dirname "$0")/game-dir.sh"; OUT="$GAME/BepInEx/SosOnline"
for f in "$OUT"/log-272*.txt; do
  k=$(grep -o -m1 'press_-\?[0-9]*' "$f" | sed 's/press_//')
  p=$(grep -m1 -E 'OnInputPressed' "$f" | grep -o ' f[0-9]*' | head -1)
  a=$(grep -m1 'OnAttackResultReady' "$f" | sed -E 's/.* (f[0-9]+) .*result=([A-Za-z]+).*/\2@\1/')
  b=$(grep -m1 'GetBlockResult' "$f" | sed -E 's/.* (f[0-9]+) .*best=([A-Za-z]+).*/\2@\1/')
  echo "$k pressed@${p:- none} attack=$a block=$b"
done | sort -n
