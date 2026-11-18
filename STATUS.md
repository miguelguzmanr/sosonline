# Status

Current state of the project, for whoever picks it up next. Update it at the end of every working session: replace what changed, don't append a diary. Technical background is in [CLAUDE.md](CLAUDE.md); causes and fixes in detail are in [docs/FINDINGS.md](docs/FINDINGS.md), the drop-in plan and its progress in [docs/PLAN.md](docs/PLAN.md).

*Last updated: 2026-11-18.*

## State right now

- **Branch:** `main`, pushed; everything is released in `v0.1.61`.
- **Latest release:** `v0.1.61`, published 2026-11-18: save backups (below). Tested before it: save-sync, resync, end-playing, dropin-catchup and dropin-reload, all 0 differences, the real save folder unchanged. Before: `v0.1.60`, published 2026-11-18: manual updates, the co-op version line, the reorganised Online page (Options sub-page, Title Case, B on popups). Sweep before it (2026-11-18): 20 of 21 passed (dropin-reload's host gave up with no session after 300 s, passed on a rerun); battle-turns-1 had the intermittent battle split (guest mismatch at f1800, re-synced by itself), runs 2 and 3 had 0 mismatches. Before: `v0.1.56` (`844ff2f`). Earlier ones still around: `v0.1.53`, `v0.1.52` (has the controller crash below), `v0.1.45`.
- **Installed on the user's PC:** a test build of the save backups (`…-modified`, same code as v0.1.61).
- **Updates are manual from v0.1.60 on.** v0.1.56 and older download v0.1.60 at their next start (the friend, P2, gets it that way, one last time); after that, *(Update available)* on the title screen and *Check for updates*.
- **Test save:** tests run from the user's newest save (`--sosonline-start=0a`). Last known: the Forbidden Cavern, Bosslug fight at `ENCOUNTER=650`, a campfire 8 m from the start. It changes whenever the user plays.

## Open items, by priority

From the first session on v0.1.53 (user's report, 2026-11-16 late): the Bosslug fight was perfect, but there were two desyncs, and **in both the automatic rejoin never worked** (the same rejoin loop as before; both players had to go back to the title screen). Both play with controllers.

1. **The rejoin loop.** Read the two logs the friend sent (`BepInEx\SosOnline\logs\received\*-20261116-225803.zip` and `*-20261116-230629.zip`: their session and state logs from 22:39) next to the host's logs of the same time (`BepInEx\SosOnline\logs\sosonline-20261116-2239*.log`, `state-*`).
2. **The fishing minigame desynced.**
3. **The first battle with Garl in the party desynced.** A title-menu test overwrote the host's `Autosave_SOS_SaveSlot0.sos` twice (2026-11-16 23:26, 23:30). The friend's mirror (`%USERPROFILE%\AppData\LocalLow\Sabotage Studio\Sea of Stars\SosOnlineSession\Autosave_SOS_SaveSlot0.sos`) may still hold the autosave; the user will ask for it. Checked 2026-11-17: no copy with Garl on the host PC. Both local slot-0 files, Steam Cloud (same checksums, synced 23:30:53) and the copies in Downloads hold the Forbidden Cavern state (Zale and Valere, level 2). Windows shadow copies need admin (Explorer → the save folder → Properties → Previous Versions). Every test run is sandboxed since (`SosOnlineTest-steam` for non-UDP runs).
4. **Ask the friend to copy their `SosOnlineSession` folder** before their next online session (the playtest's state may only be there; v0.1.61 backs it up by itself once installed).
5. **The reorganised Online page over Steam:** hosting adds Invite Friends, and a Join page full of friends (the status lines hide when the list reaches the copyright). Tested on UDP only; B on popups and on the page confirmed by the user with a pad.
6. **Verify over Steam with the friend:** a re-sync during a battle (catch-up log blocks over the relay), a lost battle (game over) while a friend waits to join, windows of different sizes (logic clipping, item 7), **Return to area start** (the area save goes to playing guests in one ~25 KB message), and **Later** on the *Check for updates* popup (the test helper couldn't press it).
7. **Party AI after a leave mid-battle replays differently now and then.** A second re-sync in the same area replays the guest's first leave; once, the AI's Valere moved 3.3 m differently right after it (f1680). Falls back to the reload. Next: call-probe the party AI between the leave and the next turn.
8. **Logic clipping** (`LogicClippingManager`): off-screen NPC logic is paused by camera frustum. A replay sees the Bosslug a snapshot early (f390, never two in a row). Turning clipping off broke replays. Might also differ live between PCs with different window sizes.
9. **Return to area start in a cutscene** (with *Pause for everyone*) is untested: camping in the test save still allows saving.
10. **Test scripts should always save and restore display prefs** (`tests/display-prefs.sh`); single test runs left the game at 960x540 windowed.
11. Older, not touched: guest crash after a re-sync in a battle (no repro; the log zip now includes the crash dump); `Session.MinAutoDelay` 4 (consider 2 after a stable Steam session); folded methods not covered by the deterministic-random wrappers (`CombatActor.OnPlayerPartyTurnDone`, `OnAnyCharacterTurnDone`, `OnPlayerCharacterTurnDone`, `OnEnemyCharacterTurnDone`); probes (turn, damage, clock lines) stay until battles are confirmed over Steam.

## Recently done

- **v0.1.61 (2026-11-18): save backups** (`Game/SaveBackup.cs`), after the playtest's save was lost (the second lost save; Steam Cloud only had the same files). Every save write or delete, and a new session replacing a guest's mirror, keeps the old file in `SosOnlineBackup/Saves` or `Session saves` (last 50 + one per day for 30 days); session snapshots in `SosOnlineBackup/Sessions` at co-op start, join, end and desync (last 30); the session's saves go into Send Logs to Host; test runs may only write saves in test folders (else `SosOnlineTest-blocked`, logged as an error). Tested on UDP: save-sync (manual save, logs zip with `saves/`), resync (snapshots 'desync P2' on the host, 'desync' and 'joined' on the guest; 250 snapshots, 0 differ), end-playing ('co-op ended' on both; the guest's old mirror backed up before the new session's saves), `--sosonline-guardtest`; the user's real save folder was byte-for-byte unchanged; dropin-catchup and dropin-reload 0 differ with 'joined' snapshots on the joiner.

- **v0.1.60 (2026-11-18):** the title screen's Online page and its popups. B on one of the mod's popups now chooses its last choice (Back, Later, OK): the game's own Back only moved the popup's cursor, the selection then went to the menu behind, and the unanswered popup blocked every later one (they showed as toasts). After a popup the cursor goes back to the entry that opened it; the page redraws once a check finishes (it kept *Checking for updates…*). B on the page puts the cursor on *Back*; B on *Back* goes back (confirmed by the user with a pad). A local build shows its version as *0.58-modified*. Test runs never install a release (a scripted answer once replaced the test build with v0.1.56), and the test helper's popup answers choose the right button.

- **Also in v0.1.60:** updates are manual (only a lookup at start, no download); *Online Co-op Version 0.57* in the title screen's bottom-left, mirroring the game's version line, with *(Update available)* when there is a newer release. Versions show as 0.57 instead of 0.1.57. Tested on the title screen (screenshots, both texts); *Check for updates* itself is unchanged apart from the version format.

- **v0.1.56:** *Check for updates* in the title screen's Online menu, with a confirmation before installing (on a modified build it offers the published release). The title menu's Online entries reset whenever the game shows its title choices again, and hide once a save is chosen.
- **v0.1.53:** removed the diagnostic patch on `InputManager.OnActiveControllerChanged` that crashed the game at start and in Options with a gamepad (in v0.1.52).
- **v0.1.52:** *Return to area start*; battle determinism (spell lock types, summon slots, session clock); re-sync by replay mid-battle. Details in findings §13.

## Verification history

### 2026-11-13, game build 3.0.60151

- Plugin loads; all Harmony patches apply, none on shared native code.
- Battles online: tutorial encounter at 8 frames of delay with host and guest each timing hits. Timing verdicts and all 87 state snapshots identical on both PCs.
- Title screen Online button, submenu and status line render with the game's widgets; the pause menu Online item appears.
- Steam lobby creation and leaving (real Steam).
- Two instances over UDP: connect, mirror 4 save files, start together; host walks, guest presses Join and walks Moongirl; 81 of 81 snapshots identical over 2,400 frames; identical screenshots.
- Pause menu opened by the host appears on the guest (synced menus).

### 2026-11-16, build `3fd5a2c` (Forbidden Cavern save)

Every loopback test in one sweep passes with 0 differences: saves (P1 and P2), live battle, drop-ins (catch-up, reload in place, level load on the join frame, from the joiner's own game), two joiners, menu banners, Stage 1, leaving, ending, a guest's game killed, Assign Players, title-screen menus and the restart popup. Camping runs on the same frames on both PCs.

### 2026-11-16, branch `milestone/battle-sync`

The Bosslug fight runs in step: 11 runs in a row of `battle-turns.sh` with 0 differences. A guest that gets out of sync mid-battle rejoins by replay in about 3.5 s. After the summon-slot fix, `resync.sh` passed 6 times in a row (241 of 241 snapshots identical after the rejoin).

### Not yet verified

- **Steam P2P beyond a few real sessions.** The rejoin over Steam is broken (open item 1).
- **Combat beyond the tested fights:** combos, ultimate moves, Moonerang deflections, the co-op "additional player" QTEs (`*AdditionalPlayersQTEState`). `TimeQTEHandler` compensation is untested.
- **Long sessions, area transitions with heavy streaming, cutscenes.** Async loading is the most likely source of desyncs.
- **Three players in combat**, and Assign Players with three players (P3's colour is approximate).
- Names with accented letters keep them, but the game's small font may not draw them.

## Known limitations (by design, for now)

- Joining mid-session waits for a moment the game allows saving. If nobody joined, left or swapped characters since the area entry, only the joiner waits; otherwise everyone briefly reloads an autosave in place (~1.5 s fade, ~2 s of progress other than positions undone).
- Guests can't open Options from the shared pause in battles and cutscenes.
- If the host leaves, the session ends; the host's save is the shared one.
- A desync of the host's own game can't be detected (the host is the reference).
- If one PC can't keep 60 fps, everyone slows down with it.
- Mod and game versions must match on every PC (checked when joining).
