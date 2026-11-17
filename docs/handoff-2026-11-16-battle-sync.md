# Handoff: battle determinism and re-sync by replay (2026-11-16, evening)

For the next agent. Follows [handoff-2026-11-16.md](handoff-2026-11-16.md); the causes and fixes in detail are in [drop-in-findings.md](drop-in-findings.md) §13.

## State right now

- **Branch:** `milestone/battle-sync`, merged into `main` (not pushed, not released: the user held the release for the re-sync split below). The latest release is still `v0.1.45`.
- **Installed on the user's PC:** a test build of this branch (`…-modified`), deployed into the game folder by the test runs. It never updates itself; a release or a deploy of `main` replaces it.
- **Nothing here is verified over Steam with two real PCs.** Everything was tested with two or three local instances over loopback UDP with simulated lag (20 ms + 25 ms jitter).

## What changed

| Area | What |
|---|---|
| `Lockstep/SpawnTrace.cs` (new) | Every enemy added to, summoned into or removed from a battle is logged with the lockstep frame (`[spawn]`). A running count and hash since the last load release is the snapshot's `spawn` component; the snapshot carries its window's events, so a guest names the first spawn that differs and its frame. |
| `Lockstep/DeterministicRandom.cs` | The Bosslug split's cause: spell lock types (`EnemyCastingData.CastMove` and the lock-settings getters, `Bosslug.UpdateNextMove`) now use the deterministic generator. |
| `Lockstep/StateSync.cs` | `combat` now holds the battle's structure (actors by name and slot, dead, casting, turns to action, lock types, modifiers, current turn and round). New numeric `hp` and `combo` components. HP, MP and combo points are corrected in place on the guest when nothing structural differs. New `test` component for `--sosonline-hpdiverge`. |
| `Lockstep/CombatTrace.cs` | `[combat] … BreakLocks(Type): locks N -> M [...]` on every hit on an enemy with locks. |
| `Lockstep/LockstepEngine.cs` | `Observer`: a catch-up joiner's own slot input comes from the log. |
| `Net/Session.cs` | Re-sync by replay (Stage 3) right away, mid-battle included, with fallbacks to the old wait-and-reload. Catch-up joiners line up the physics phase before L. A wiped party is treated like a battle for drop-in saves. |
| `Game/GameBridge.cs` | `PartyWiped`: no drop-in autosave during a game over (open item 2 of the previous handoff). |
| `UI/CatchUpScreen.cs` | "Rejoining *host*'s game" and "The others keep playing while you rejoin." while rejoining by replay. |
| `TestHarness.cs` | `--sosonline-hpnudge`, `--sosonline-hpdivergeagain`, `--sosonline-nolockfix`, `--sosonline-noresync`; `--sosonline-hpdiverge` takes a list and fires once per frame per process. |
| `docs/messages.csv` | Rows 232–234: "You're back in sync.", "Rejoining the party…", "The others keep playing while you rejoin." |
| `tests/resync.sh` | Recognises a rejoin by replay. |
| `tests/sweep.sh` | Generated input scripts go to a temp folder (the game folder's path has spaces, so the camp and Assign Players scripts were never found). |

## Results

| Test | Result |
|---|---|
| A/B: `battle-turns.sh` (Bosslug) on v0.1.45 | Split in 1 of 4 runs: not a regression |
| Spawn report (`--sosonline-nolockfix --sosonline-noresync`) | `spawn differs at f1784: host [f1784 add AcidBugEnemy slot 2], local [f1784 add AcidBugEnemy slot 1]`; the cause itself now shows at f1380 as different lock types in `combat` |
| `battle-turns.sh` (Bosslug) with the fix | 5 of 5 consecutive runs: 0 guest mismatches, identical turn lines, same lock types on both PCs |
| Snapshot cost (same test, 167 snapshots) | 3.11 ms average, 18.5 ms worst (v0.1.45: 3.06 ms, 18.4 ms) |
| HP nudge on the guest mid-battle | Corrected at the next snapshot (`hp:VALERE 48->49`), 0 mismatches |
| `resync.sh` (divergence mid-battle) | Rejoined by replay in 3.5 s at f1882, the battle ran to f7200; 241 snapshots after, 0 differ |
| `resync.sh` with the replay diverging too | Back to the waiting screen; reloaded in place after the battle (f8349); 34 snapshots after, 0 differ; no second split |
| Full sweep | See *Sweep* below |

### Sweep

`tests/sweep.sh` without its last step (the title-screen test hosts a real Steam lobby; not run), on this branch's build:

| Test | Result |
|---|---|
| `battle-turns.sh` ×3 (Bosslug) | 0 guest mismatches, 318/318 turn lines each (8 clean runs in a row with the 5 above) |
| `battle-live.sh` | 121 snapshots, 0 corrections, 0 mismatches |
| `save-sync.sh`, P1 and P2 saving | 0 guest mismatches |
| `resync.sh` | Rejoined by replay (1,328 frames in 3.4 s, merged f1875); 241 snapshots after, 0 differ |
| `dropin.sh`: catch-up, `--sosonline-nocatchup`, `--sosonline-loadatjoin`, `JOINFLAGS=--sosonline-play=0` | Both guests 0 differ in every case (66–132 snapshots each), 0 corrections, 0 mismatches |
| `dropin-two.sh` | Both joiners 169 snapshots, 0 differ |
| `stage1.sh` | 27 and 67 snapshots, 0 differ |
| `menu-status.sh` | 112 and 109 snapshots, 0 differ |
| Leave, end (playing / still joining), guest killed | Both PCs end with `P1 VALERE [kb]`, Zale with the party AI |
| Assign Players, camping | 0 mismatches. In the sweep these two ran without their input scripts: `sweep.sh` wrote them into the game folder, whose path has spaces. Fixed (`$GEN`, a temp folder) and rerun: the guest's cursor move ran (both players on Valere in the screenshot), camping ran, 0 mismatches |

No errors or exceptions in any log apart from those two missing scripts.

### Second sweep (full, title-screen test included)

Run on the same build before committing. Every test passes with 0 differences (battle-turns ×3: 302/302 turn lines, 0 mismatches; title-screen menus and restart popup with a real Steam lobby: fine) **except `resync`**:

- The guest rejoined by replay as intended (f1654 → merged f1875, 3.3 s).
- At f5401 it split again in the Bosslug fight: same spawns on the same frames, slots swapped. Host `f5401 summon AcidBugEnemy slot 2`, `f5408 … slot 1`; guest `f5401 … slot 1`, `f5408 … slot 2`. Lock types and every `BreakLocks` line matched on both PCs, so it's not the lock cause.
- Second divergence → reload fallback ("a character was reassigned"): the guest sat on the waiting screen until the test quit at f9500 (the battle was still on).
- The replayed guest again saw the Bosslug one snapshot early at f390 (`npcs`): the second time (open item 3), so possibly related.
- Logs: `BepInEx/SosOnline/sweep/resync-log-2710{0,1}.txt` in the game folder.

## Open items, by priority

**First session on v0.1.53 (user's report, 2026-11-16 late):** the Bosslug fight was perfect. Two desyncs, and in both **the automatic rejoin never worked: the same rejoin loop as before; both players had to go back to the title screen.** Both play with controllers.
1. **The rejoin loop** (top priority): read the two logs KocoloX sent (`BepInEx\SosOnline\logs\received\KocoloX-20261116-225803.zip` and `-230629.zip`: their session log and state log from 22:39) next to the host's logs of the same time (`BepInEx\SosOnline\logs\sosonline-20261116-2239*.log`, `state-*`).
2. **The fishing minigame desynced.**
3. **The first battle with Garl in the party desynced.** The user pointed at autosave 1, but **a title-menu test overwrote the host's `Autosave_SOS_SaveSlot0.sos` twice (23:26, 23:30)**: it started a game from the real saves. KocoloX's mirror (`%USERPROFILE%\AppData\LocalLow\Sabotage Studio\Sea of Stars\SosOnlineSession\Autosave_SOS_SaveSlot0.sos`) should hold the same autosave until their next co-op area change; the user will ask for it. Since then every test run is sandboxed (`TestHarness`: non-UDP runs use `SosOnlineTest-steam`); verified with hashes of the real saves before and after.

**Done since (commit `577c182` and after, not published):**
- **Check for updates** (title screen, Online page): on demand, asks before installing; on a modified build offers the published release (also older or equal), writes its signature. Tested: local build → Install → published 0.1.53 in place; Restart relaunched through Steam into 0.1.53; clean build → "Up to date (0.1.55)." **Later is untested**: the test answer helper and synthetic keys didn't move the popup's answer (it answers index 0); the popup is the one the existing two-choice menus use. Ask the user to try Later once.
- **Title menu reset**: whenever the game shows its title choices again, the Online page's entries are hidden and its state reset (they showed mixed with the game's after a session). **Hidden at once when a save is chosen** under Online > Start (it stayed visible and usable while the game started); back if the start falls through. Tested with `--sosonline-titleflow` (the return from a real game simulated with `ShowChoices`; `TitleSequenceManager.GoToTitleScreen` does nothing in game).

**Since then (evening):** v0.1.52 published (`5a2c79c`). Then the game crashed at start and when opening Options with a gamepad: the diagnostic patch on `InputManager.OnActiveControllerChanged` (32 bytes; breakpoint at GameAssembly.dll+0xc7fcbe, found with `--sosonline-whichrva`). Removed with the other controller probes in `8c9a08c`, deployed to the user's game (labelled `0.1.52+5a2c79c-modified`), committed, **not pushed or published** (publishing was blocked by a permission check; v0.1.53 waits for the user's go-ahead). v0.1.52 still has the patch: the friend may crash with a gamepad. Also left by the tests: the game's display settings at 960x540 windowed (`tests/display-prefs.sh` wasn't run around the single tests; its saved copy is a test size too). Make the test scripts save and restore them every time.

New, not committed yet: **Return to area start** (host's pause page where saving isn't allowed while a friend waits; findings §13). Tested once mid-battle (0 differences after), plus the reload drop-in and Stage 1 tests. Not over Steam: the area save goes to playing guests in one message (about 25 KB). Untested: the page in a cutscene (with Pause for everyone): camping in the test save still allows saving, so the friend joined the usual way.


Fixed since the merge into `main` (not committed yet; findings §13): summon slots (`resync.sh` 6/6, `battle-turns.sh` 2/2, `battle-live.sh` clean), the session clock (battle clock lines identical on both PCs), characters given back kept in the area log, the dead "same point" rule removed, P3's colour. Tested: Esc on the waiting screen (real key press).

0. **Todo (user, 2026-11-16): "Check for updates" in the Online menu, with a confirmation before updating.** Today `Update/AutoUpdate.cs` checks GitHub only at game start and installs a newer signed release without asking (next start runs it); the Online menu only shows "Restart to update" afterwards (`NativeMenus.cs`, `RestartLabel`). Plan:
   - An entry in the title screen's Online page (a per-PC menu, not synced, so fine there; not in the in-session pause popup, which runs on every PC and may only offer the same choices everywhere).
   - It runs the check on demand (`CheckAsync`) and reports in a popup: up to date / update X available → confirm → download, verify the signature, install → the existing "Restart to update".
   - Open questions for the user: should the automatic check at start also ask before installing (the "confirmation" part), or keep installing silently? For a locally modified build (updates off today), show "Local build: no updates" or allow it?
   - Wording per `docs/messages.csv` style (short; "Check for updates", "Up to date.", "Update 0.1.54 is available.", "Install", "Back").

1. **Verify over Steam with the friend** (as before, plus): a re-sync during a battle (the catch-up's log blocks over the relay are untested), a lost battle (game over) while a friend waits to join, and windows of different sizes (logic clipping, below).
2. **Party AI after a leave mid-battle replays differently now and then.** A second re-sync in the same area replays the guest's first leave; once, the AI's Valere moved 3.3 m differently right after it (f1680). Falls back to the reload. Next: call-probe the party AI between the leave and the next turn.
3. **Logic clipping** (`LogicClippingManager`): off-screen NPC logic is paused, by camera frustum. A replay sees the Bosslug a snapshot early (f390, never two in a row). Turning clipping off broke replays (off-screen Luslugs wander differently). Might also differ live between PCs with different window sizes.
4. Earlier items not touched: guest crash after a re-sync in a battle (no repro), `MinAutoDelay` 4 (after Steam). (Toasts now move off any text they would cover: fixed in c124677.)

## Testing

As in the previous handoff, plus:
- `ENCOUNTER=650 QUIT=5000 bash tests/battle-turns.sh --sosonline-start=0a --sosonline-nolockfix` reproduces the Bosslug split (about half the runs).
- `DIVERGE=1600 QUIT=9500 EXTRA="--sosonline-encounter=1500 --sosonline-bot --sosonline-lag=20 --sosonline-jitter=25" bash tests/resync.sh` is now the rejoin-by-replay test; add `--sosonline-hpdivergeagain` to `EXTRA` for the fallback.
- `dotnet build` deploys into the game folder by default; pass `-p:DeployToGame=false` for a compile check while a test runs.
