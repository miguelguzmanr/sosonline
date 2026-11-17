# Status

Current state of the project, for whoever picks it up next. Update it at the end of every working session: replace what changed, don't append a diary. Technical background is in [CLAUDE.md](CLAUDE.md); causes and fixes in detail are in [docs/FINDINGS.md](docs/FINDINGS.md), the drop-in plan and its progress in [docs/PLAN.md](docs/PLAN.md).

*Last updated: 2026-11-17.*

## State right now

- **Branch:** `main`, clean and pushed (`844ff2f`).
- **Latest release:** `v0.1.56` (`844ff2f`), published 2026-11-17. Earlier ones still around: `v0.1.53`, `v0.1.52` (has the controller crash below), `v0.1.45`.
- **The friend (P2)** updates automatically at their next game start.
- **Test save:** tests run from the user's newest save (`--sosonline-start=0a`). Last known: the Forbidden Cavern, Bosslug fight at `ENCOUNTER=650`, a campfire 8 m from the start. It changes whenever the user plays.

## Open items, by priority

From the first session on v0.1.53 (user's report, 2026-11-16 late): the Bosslug fight was perfect, but there were two desyncs, and **in both the automatic rejoin never worked** (the same rejoin loop as before; both players had to go back to the title screen). Both play with controllers.

1. **The rejoin loop.** Read the two logs the friend sent (`BepInEx\SosOnline\logs\received\*-20261116-225803.zip` and `*-20261116-230629.zip`: their session and state logs from 22:39) next to the host's logs of the same time (`BepInEx\SosOnline\logs\sosonline-20261116-2239*.log`, `state-*`).
2. **The fishing minigame desynced.**
3. **The first battle with Garl in the party desynced.** A title-menu test overwrote the host's `Autosave_SOS_SaveSlot0.sos` twice (2026-11-16 23:26, 23:30). The friend's mirror (`%USERPROFILE%\AppData\LocalLow\Sabotage Studio\Sea of Stars\SosOnlineSession\Autosave_SOS_SaveSlot0.sos`) may still hold the autosave; the user will ask for it. Every test run is sandboxed since (`SosOnlineTest-steam` for non-UDP runs).
4. **Verify over Steam with the friend:** a re-sync during a battle (catch-up log blocks over the relay), a lost battle (game over) while a friend waits to join, windows of different sizes (logic clipping, item 6), **Return to area start** (the area save goes to playing guests in one ~25 KB message), and **Later** on the *Check for updates* popup (the test helper couldn't press it).
5. **Party AI after a leave mid-battle replays differently now and then.** A second re-sync in the same area replays the guest's first leave; once, the AI's Valere moved 3.3 m differently right after it (f1680). Falls back to the reload. Next: call-probe the party AI between the leave and the next turn.
6. **Logic clipping** (`LogicClippingManager`): off-screen NPC logic is paused by camera frustum. A replay sees the Bosslug a snapshot early (f390, never two in a row). Turning clipping off broke replays. Might also differ live between PCs with different window sizes.
7. **Return to area start in a cutscene** (with *Pause for everyone*) is untested: camping in the test save still allows saving.
8. **Test scripts should always save and restore display prefs** (`tests/display-prefs.sh`); single test runs left the game at 960x540 windowed.
9. Older, not touched: guest crash after a re-sync in a battle (no repro; the log zip now includes the crash dump); `Session.MinAutoDelay` 4 (consider 2 after a stable Steam session); folded methods not covered by the deterministic-random wrappers (`CombatActor.OnPlayerPartyTurnDone`, `OnAnyCharacterTurnDone`, `OnPlayerCharacterTurnDone`, `OnEnemyCharacterTurnDone`); probes (turn, damage, clock lines) stay until battles are confirmed over Steam.

## Recently done

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
