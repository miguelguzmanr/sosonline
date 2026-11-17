# Sourced by the test scripts: sets GAME to the Sea of Stars folder (a POSIX path), found by tools/game-dir.ps1
# (SOS_GAME_DIR, game-dir.txt in the repository root, or Steam's libraries).
_sos_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
_sos_dir="$(powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$(cygpath -w "$_sos_root/tools/game-dir.ps1")" | tr -d '\r')"
if [ -z "$_sos_dir" ]; then echo "Sea of Stars folder not found (see tools/game-dir.ps1)." >&2; exit 1; fi
GAME="$(cygpath -u "$_sos_dir")"
unset _sos_root _sos_dir
