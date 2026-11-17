# Findings

What was found while building the mod: causes, fixes and measurements, numbered so other docs can cite them as "findings §N". The plan these serve is in [PLAN.md](PLAN.md); the current state is in [STATUS.md](../STATUS.md).

*Sections 1–11 from 2026-11-15, §12 and §13 from 2026-11-16, §14 (the spike results) from 2026-11-15.*


## 1. Window tiling: works, no change

`tests/session.sh` + `tests/place-window.ps1`: host at x=0, guest at x=656 (640×360 windows), same row. The helper scripts exit with the games.

## 2. Battle drift (finding 8): fixed

**Wrong lead.** The live-battle reproduction in the plan passed `--sosonline-encounter` to the host only, which desyncs everything. Pass it to every PC (`tests/battle-live.sh` does).

**Cause.** The battle start's party dash (`PlayerDashState.UpdateAnticipation`) advances a 0.1 s anticipation by `Time.smoothDeltaTime / 0.1`. `smoothDeltaTime` is Unity's average of that PC's own recent frame times, so it differs in the last bits between PCs. Six frames make exactly 0.1 s: progress was 1.0000004 on the host (dash on frame 6) and 0.9999998 on the guest (frame 7). The whole battle then ran a frame apart, so the guest corrected positions on every snapshot.

Ruled out along the way:
- Asynchronous loads at battle start: `Encounter`, `EncounterIntro` and `CombatManager` calls land on identical frames.
- Physics steps: identical per frame.

**Also wrong, and fixed:**
- Each PC's absolute `Time.time` differs.
- A guest whose title screen is paused (window without focus, timeScale 0) loses frames of game time before lockstep takes over. In one run the clocks were 6 frames apart at the first load.
- `unscaledTime`/`unscaledDeltaTime` are wall-clock.

**Fix:** `Lockstep/GameClock.cs`.
- **Hooks.** Native detours on Unity's icalls (`il2cpp_resolve_icall` + `INativeDetour`, so even inlined callers are covered) for `time`, `timeAsDouble`, `unscaledTime(AsDouble)`, `unscaledDeltaTime`, `smoothDeltaTime` and `frameCount`.
- **Values during lockstep.**
  - `time` is local time plus an offset.
  - Unscaled time advances 1/60 per lockstep frame.
  - `smoothDeltaTime` returns `deltaTime`.
  - `frameCount` is 10,000,000 + the lockstep frame.
- **Base.** Set at session start (the `Start` message) and on the join frame (`DropIn`/`DropInReload`), and re-set on every load release (the `LoadRelease` message carries the base). The clock is kept after a session, and bases only move forward.
- Protocol version 5.

**Result:** a live two-instance battle gives 0 corrections and 0 mismatches in 121 snapshots (before: 10 corrections, 2 mismatches). Spike B's replay is bit-identical to the recording for 3,898 frames, 1,383 of them in battle; the replay adopts the recording's clock at L through `Session.ReleaseClock`.

**Open (latent):** the fixed step is 0.02 s against 1/60 s frames, so the frames that get no physics step depend on each PC's start phase. They were identical in every measured run. `--sosonline-fixedlog=F1,F2` logs the steps per frame.

## 3. Level load running on the join frame: fixed

**Before:** every PC called `ReloadInPlace` over the running load. The barrier's held load was discarded, and the host would wait forever for the joiner's report of that load.

**Now** (`Session.ScheduleReloadInPlace` → `PostponeDropIn`):
- On the join frame, every PC already in checks `LoadingLevel || InBattle`. That state is identical on each of them, so they all decide the same way.
- If either is true, the joining slot leaves on that frame (`SetLeaveFrame`). The host sends `DropInAbort`; the joiner stops lockstep and waits. The drop-in then restarts with a fresh autosave.
- Also fixed: stopping lockstep while a load is held now releases it (`AfterLockstep`).

**Test:** `HOSTFLAGS="--sosonline-loadatjoin --sosonline-nocatchup" tests/dropin.sh`. Postponed at f1908–f1911, joined about 245 frames later; joiner and guest identical to the host.

## 4. Joining from inside your own game: works

**Test:** `JOIN_DELAY=6 JOINFLAGS="--sosonline-play=0 --sosonline-joinafter=15" tests/dropin.sh`. The joiner plays its own save for 15 s, then joins and is replaced in place (Stage 2) or catches up (Stage 3). It is identical to the host afterwards in both cases.

**Caveat:** its own game was the same save and area as the host's. A different area is untested.

## 5. Stage 3 (only the joiner waits): implemented, working on loopback

On by default; `--sosonline-nocatchup` forces Stage 2.

### Flow

**Every PC, at every load release (frame L).**
- `OnFrameApplied` → `AreaLog.SaveAtEntry` autosaves.
- The host keeps a copy of the save file and the index. It also records what the save doesn't hold: party positions, clock base, slot→player map, occupied slots, and which co-op players are playing. From L on it logs every frame's inputs (serialized) and its state snapshots (`Net/AreaLog.cs`).

**Host (`UpdateCatchUpHost`, per pending peer).**
- **None:** if `WantsCatchUp`, send the files, with the L save replacing the current autosave.
- **Files:** once the joiner is ready, `SendCatchUp`: the `CatchUp` message, the log as `InputLog` blocks of 600 frames, the logged snapshots, and the inputs already buffered. Live inputs, snapshots and `LoadRelease` messages then reach the joiner as usual.
- **Running:** on `CaughtUp`, `MergeCaughtUp`: merge frame = applied + Delay + 3, `Merge` to everyone (joiners still replaying included), `AddSlot` everywhere.

**Roster changes since L (`AreaLog.Roster`).**
- The host logs each change the inputs don't reproduce, with its frame: a Stage 3 merge (`MergeCaughtUp`) and a leave after a lost connection (`DropPeer` → `ApplySlotLeave`).
- A leave from the synced pause menu (`LeaveSlotDeterministic`) is input, so the replay reproduces it and it isn't logged.
- The `CatchUp` message carries the list. Right after `BeginLockstep` at L, the joiner applies each change on its frame: `AddSlot` + `RegisterSlotPlayers`, or `SetLeaveFrame` + `MakeLeave`, as the session did. Changes after the `CatchUp` reach a replaying joiner live (`Merge`, `SlotLeft`).
- A load release keeps only the changes after its frame; the occupied slots recorded at L cover the rest.
- A Stage 2 drop-in still invalidates the log: everyone reloads, and the reload's load release starts a new one.

**Joiner (`OnCatchUp`).**
- `BeginLockstep` at frame L with its own slot not in the session, then `ReloadInPlace` of the save. `Engine.Hold` keeps frame L from running until the save's load is held at the barrier, so the load ends on L.
- On L: `CatchUpEntry` restores exact positions and calls `Join()` for the co-op players playing at L.
- It replays uncapped (vsync off, `targetFrameRate` −1, cameras off, Wwise volume 0) and checks the host's snapshots as it goes.
- When it is within Delay+2 frames of the newest frame known, for 30 updates, with no desync streak: it sends `CaughtUp`, turns fast-forward off, and on `Merge` arms the auto-join.

**Engine changes (`LockstepEngine`).**
- `Hold` gate.
- `NewestKnownFrame`.
- `OnRemoteFrame` takes a whole logged frame.
- Local input is stored and sent only when the slot is in the session and `target >= joinFrame + Delay`.
- `HoldFrame` also blocks a load's release frame until that load is held on this PC; a fast replay would otherwise get there first.

**Fallbacks to Stage 2 (`ForceStage2`).**
- The area log is invalid:
  - the save wasn't allowed at L;
  - a Stage 2 drop-in since L (everyone reloaded);
  - more than 54,000 frames since L.
- The replay diverged (`CatchUpFailed`).
- The catch-up took over 120 s.
- A Stage 2 drop-in started meanwhile (`CancelCatchUp`).
- An area change during the file transfer restarts the catch-up from the new entry.

**Messages** (protocol version 6): `CatchUp` 25, `InputLog` 26, `CaughtUp` 27, `Merge` 28, `CatchUpFailed` 29 (plus `DropInAbort` 24).

### Results (`tests/dropin.sh`, three loopback instances)

| Case | Flags | Result |
|---|---|---|
| Free roaming | none | 1,420 frames replayed, caught up 3.6 s after start (1.8 s loading); P3 playing 6 frames after the merge; joiner 129/129 snapshots identical; guest 1 unaffected |
| P2 playing at L | `HOSTFLAGS=--sosonline-reload=1300 JOIN_DELAY=40` | Restarted at the new entry f1504; `Join()` re-created P2 on the joiner; 129/129 identical |
| During a battle | `HOSTFLAGS="--sosonline-encounter=1500 --sosonline-bot" JOINFLAGS=--sosonline-encounter=1500` | Merged and took P3 mid-battle; 129/129 identical |
| Own game | `--sosonline-play=0` | 1,491 frames in 3.3 s; identical |
| Divergence | `JOINFLAGS=--sosonline-hpdiverge=600` | Caught at f630/f660 mid-replay → Stage 2 → joined at f2060; identical afterwards |
| Second joiner, same area | `tests/dropin-two.sh` | A merged at f1714; B replayed that join from the area log and merged at f2997; both 169/169 identical |
| Second joiner after the first left | `AFLAGS=--sosonline-quit=2400 tests/dropin-two.sh` | B replayed A's join (f1734) and leave (f2404); 169/169 identical |
| Two joiners at once | `JOIN2_DELAY=1 tests/dropin-two.sh` | B was replaying when A merged (live `Merge`); both 170/170 identical |

**Note for tests:** harness frame actions (`--sosonline-encounter`, `--sosonline-reload`, …) must go to the joiner too; it replays those frames.

**Bug found on the way (fixed):** the host's save transfer id was global, so a second joiner's transfer made it ignore the first joiner's `Ready`, and the first joiner waited forever. Each peer now keeps the id of its own transfer (`Peer.TransferId`).

### Left for Stage 3

1. ~~Roster changes since L~~: done, see above.
2. ~~Joiner's screen during catch-up~~: done, see *Catch-up screen* below.
3. **Real Steam P2P:** the log blocks and snapshot burst over Valve's relay are untested, and so is everything Steam-related (no second account here).
4. **Catch-up time budget:** the cut-off is a fixed 54,000 frames plus a 120 s timeout. Measure the replay speed on real hardware, and decide on Stage 3 or Stage 2 from (frames since L) ÷ (speed − 60), as the plan says.
5. **Physics step phase** (above). Watch for it and align it if it ever diverges.
6. **Joining from another area of your own game** is untested.
7. **Merging during a battle:** the auto-join retries every 90 frames up to 12 times. Check that a long battle doesn't exhaust it.
8. **Pause-menu leave during a replay:** not logged, on the assumption that the replayed menu input calls `LeaveSlotDeterministic` on the joiner too. Untested; `--sosonline-pauseleave` calls it directly, so it doesn't cover this.

## 6. Catch-up screen: done

`UI/CatchUpScreen.cs`, drawn with `UI/GameUi.cs`, which builds the mod's screens from the game's own UI art.
- **Look.** It uses the pause menu's recipe, measured with `--sosonline-uiprobe`. All the art is in one UI atlas, drawn at the game's 640×360 reference size:
  - **Panel:** tiled `panel-background-t1`, a sliced `panel-ornament-2-full-t1` frame, a `panel-border-rounded` outline and `Shadow-rounded`.
  - **Separators:** two-tone `panel-separator-base` bars.
  - **Fonts:** `copy1056-webfontBitmap` for main text and `kroegerBitmap` in grey for secondary text, both 8 px with the drop-shadow materials.
  - **Highlight:** the cursor's cyan.
- **Isolation.** Everything sits on the mod's own overlay canvas. Nothing is added to the game's UI hierarchy or view stack, so the simulation never sees it. Without the art (before the title screen has loaded it), a plain IMGUI version is drawn instead.
- **Content.** A black screen with one panel showing "Joining X's game", the step (loading the area, catching up, joining the party), a progress bar, frames replayed, replay speed and the estimated seconds left. The replay speed is measured every 0.5 s and the estimate is frames left ÷ (speed − 60). Toasts wait until the screen is gone.
- **Camera.** Overlay canvases only render when a camera renders, and fast-forward turns every camera off, so the screen used to freeze on the last frame drawn. A UI-only camera draws nothing of the scene, clears to black and renders one frame every 0.1 s. Fast-forward skips it. The replay speed is unchanged (1,442 frames in 3.7 s).
- **Fonts.** The bitmap fonts lack some characters (`@`, `›`, most non-Latin scripts). Names are drawn without the characters the font can't draw (`GameUi.Drawable`).

## 7. Pause and menus online: how they work, and a menu banner

**How it works.** There is one game, so a pause is the same on every PC:
- Any player can pause (`PauseManager.TogglePauseMenu(Player)`) or open the main menu. It opens on every PC on the same frame, and the game stops for everyone.
- The game makes that player the UI player (`SinglePlayerPlusManager.uiInputsPlayer`). Only they can move in the menus; everyone else watches the same screens.
- This is the game's own couch co-op behaviour, carried over by lockstep.

**Banner (`UI/MenuStatus.cs`).** It says who has the game paused or a menu open, and which menu:
- Others see, for example, "P2 Alex paused the game: Pause menu > Options. They have the controls until they close it." The player in the menu sees "Paused for everyone: …".
- It is built from the game's art (a small panel at the bottom centre) and draws on every PC.
- It needs no network messages. Every 0.15 s it reads the game's pause flag, the open screens (`UIManager.screensByType`: pause menu, main menu with its section, map, shop, options, controls, Assign Players and others), and the UI player mapped to a slot. All of this is identical on every PC.
- It logs `[menu]` lines for tests (`tests/menu-status.sh`).

**Found: Options opened by a guest desyncs the menus.**
- When a guest opens Options, the game shows that player's controls page, built from the controllers assigned to that player on this PC.
- On the guest's PC that is its keyboard. On the host and the other guests, that player has no controllers, so the page is empty and ignores the guest's Back. Those PCs stayed in Options until the end of the test, while the guest's own PC closed it and went on to the host's main menu.
- The game state stayed identical (0 snapshot differences): only the UI diverged. It would turn into a game-state divergence as soon as the stuck PCs' menus take input differently.
- The same steps by the host stay in sync on all three PCs: they all show the General tab and close on the same frames.
- Fixed: see §8. Outside battles and cutscenes the pause (and its Options) is local now; in a shared pause, Options is blocked for guests.

## 8. Local pause screen (tier 1): done

**What changed.** In free gameplay a pause opens on the player's own PC only, for the host too (`UI/LocalPause.cs`):
- The pause press, and everything pressed while the screen is open, is replaced by neutral input before it is sent (the last stage of `Engine.LocalInputFilter`, after the test scripts). Every PC gets the same input, so the shared game never sees the pause and the world keeps running; the paused player's character stands still.
- Buttons still held when the screen closes are kept out until released, so the closing press doesn't act in the game.
- Free gameplay means no battle, cutscene (`SaveManager.CanSave`), loading, `PauseManager.pauseDisablers`, or shared pause or menu. Everywhere else the press goes to the game as before (shared pause), so cutscene skipping and the like still work.
- The screen: Resume, Options, Leave session (guests) or End session (host), each leave with a confirmation page. Game-styled on the mod's own canvas.

**"P2 is paused".** Others see it in the banner (`MenuStatus`) when no shared menu is open ("P1 and P2 are paused" for several). It comes from `Msg.PauseStatus` (any → host, host relays; display only, never input). A joiner gets the current ones on Welcome. Protocol version 7.

**Leave and End session from the local screen.** They no longer run inside a synced menu, so the host picks the frame:
- Guest: `Msg.LeaveRequest` → the host applies a slot leave on `AppliedFrame + Delay + 2` and sends `SlotLeft`, like a drop-out (logged for joiners).
- Host: `Msg.EndAt` (frame, reason) to everyone; `LockstepEngine.EndAt` stops lockstep at the start of that frame on every PC. Pending joiners get `Stop`.
- That frame is safe: a guest can't be past the frames whose System input the host has sent, and the message arrives before anything the host sends later.

**Options (`UI/LocalOptions.cs`).** Master, music, sound effects, ambience (with environment), voices, brightness, display mode. Applied with the game's setters on `SaveManager.SaveData.optionsSaveData` (`ApplyAudio`, `ApplyVideo`) and written with `SaveManager.SaveOptions` when leaving the page or closing the screen.
- Options files are now each PC's own: `SaveMirror` no longer sends `SOS_Options.sos` and never redirects it into the session copy. Before, a guest loaded the host's options (volumes, display, controls) at session start.
- Left out: language and "Timed Hit HUD feedback" (they may change what the shared game shows or does), and the control mappings (the game's controls pages need its own Options screen).
- Risk to watch: guests now keep their own language too. If text length or language ever drives game timing, that would diverge; nothing seen so far.

**Shared pause, guest Options.** (Untested: see the table.) `PauseMenu.Options` is skipped when the UI player is a guest (synced state, so every PC skips it on the same frame); the guest gets a toast pointing to their own pause screen. Fixes the §7 desync where it can still happen (battles, cutscenes).

**Tests** (scripts in `tests/scripts`, run with `tests/menu-status.sh`; set `HOSTSCRIPT`, `GUESTSCRIPT`, `EXTRA`):

| Scripts | Result |
|---|---|
| `host-local-pause.txt` + `guest-local-pause.txt` | No `[menu]` lines on any PC; `[localpause]` open/close lines; others log and show "P1/P2 paused"; 0 snapshot differences |
| `host-local-end.txt` + `guest-local-leave.txt` | Guest 1 leaves on f2454 on all three PCs; host and joiner stop on f2901; 0 differences up to there |
| `host-local-pause.txt` + `guest-local-options.txt` | Only guest 1's own options file changes (master 10→8, brightness up); host's and joiner's unchanged; nothing in the session copy; 0 differences |
| `host-battle-options.txt` / `host-walk.txt` + `guest-battle-options.txt`, `EXTRA=--sosonline-encounter=1500` | No pause menu opened at all during the battle, for the host (player or System channel) or the guest: the game seems to ignore `UI_Start` there. So the Options guard is in place but untested; it matters for cutscenes or wherever else a shared pause still opens. 0 differences |
| `tests/dropin.sh`, `tests/dropin-two.sh` | Pass, 0 differences |

Not covered: a joiner that catches up *after* a local-screen leave (the leave is in the area log like a drop-out, so it should replay); a leave or end while a joiner is mid-catch-up; changing display mode (only volumes and brightness were exercised). Test scripts mark a held key as repeating on every frame, so a held left/right in a script moves a setting several steps; hardware repeats at Rewired's interval.

**Test games after a session ends:** `--sosonline-quit` counts lockstep frames, which stop at End session or Leave. The harness now counts the frames that were left in Unity frames (at least one second) and quits then.

## 9. Stage 1: done on loopback

**Open to friends** (`Settings.OpenToFriends`, `BepInEx/config/sosonline.coop.cfg`; a toggle in the title screen's Online menu, and the pause menu's Online entry offers it). While on, a Steam lobby opens whenever this PC plays its own game (`Session.AutoOpen`), with no hosting step.

**Solo to co-op** (`Session.UpdateSoloCoop`). A friend connects while the host plays its own game (lobby open, no lockstep):
- Once the friend's ping is measured, lockstep starts right there with the host alone (frame 0, no load), and the friend is queued like any joiner (`Welcome` sent again with `Pending`).
- The friend drops in through the existing paths: a reload in place (Stage 2), or a catch-up (Stage 3) once the host has entered an area since.
- The host's input runs `Delay` frames late from then on, as in any session.

**Co-op to solo.** When the last guest has left (leave, drop-out), the host stops lockstep and keeps playing; the lobby stays open. The next friend starts a new run. A new run clears the area log entirely (`AreaLog.Clear`): its roster changes counted frames of the old run, and a joiner replaying them diverged (found by the test below).

**Re-sync on desync.** A guest whose state check fails twice (`StateSync.Desynced`) sends `ResyncRequest`; the host makes it leave on a frame every PC can reach, then queues it again (`ForceStage2`), and it drops in by a reload in place. Nobody else stops.

**Characters.** The host remembers each guest's character by Steam id (`_lastCharacter`). When the guest begins playing again with another one (the game hands out the next free character), the host schedules `Msg.AssignCharacter` on a safe frame and every PC calls `UnassignCharacter` + `AssignCharacter` there, if that character is still free (not the leader, nobody else's). The area log is invalidated and running catch-ups cancelled, since a replay can't reproduce it.
- **Not done: a direct `SinglePlayerPlusPlayer.Join` on a scheduled frame.** The synced Join press stays. A press is input, so a Stage 3 replay reproduces it for free; a direct call would have to be logged for every kind of join (start, reload in place, merge) to keep catch-up working. The press's slowness (finding 4) is cosmetic.

**Only the session's slot is sent:** reverted on 2026-11-15, see §10. The whole save folder is sent again: the game's save screens must show the same slots on every PC.

**Join from a Steam invite while playing** (`SteamInvites`): one confirmation popup ("Join your friend's game? Your own game stops here…"), then the mod leaves this PC's session if any and joins; the drop-in replaces this PC's game in place. Not offered while this PC is in a running co-op session (a popup there would be a game view on one PC only); the toast asks to leave first.

**Fixed along the way:**
- A joiner could be stuck in the drop-in queue forever: when the party changed area during a reload drop-in, the joiner stayed on the stale joiner list, which also blocked the catch-up the new area made possible. The list is now cleared when the drop-in starts over.
- A PC whose own game stood still for a while (a save loading while it boots) dropped its peers as timed out right after. Time the game stood still is no longer counted as anyone's silence.

**Tests:**

| Script | Result |
|---|---|
| `tests/stage1.sh` (host plays its own save; A joins, gets GARL back with `--sosonline-rememberchar=last`, leaves from the pause screen; host back to solo; B joins later) | Co-op starts from the host's game twice; A dropped in by a reload, B by a catch-up; both handed GARL on every PC; 2 save files sent; A 0 of 26 snapshots differ, B 0 of 70 |
| `tests/resync.sh` (guest's HP lowered by 1 on its PC at f1500) | Found at f1530 and f1560; left at f1567; dropped in again at f1729; 0 of 59 snapshots differ after |
| `tests/dropin.sh`, `tests/dropin-two.sh`, local pause (`menu-status.sh`) | Pass: 0 differences, 0 corrections |

Harness: `--sosonline-play` on the host plays a save with the lobby open; `--sosonline-quitruns=N` keeps a host that goes back to solo running until N runs have ended; a test game that never gets into a session quits after 300 s.

**Pause for everyone** (local pause screen). The local pause had made the game's own pause menu unreachable in free gameplay, and with it Assign Players, Return to title and the host's game Options. The entry closes the local screen and puts a pause press into this PC's next synced input, so the shared pause menu opens on every PC as before (tested: open on f1107–1110 and closed on every PC by the banner's 0.15 s poll; a joiner caught up across it; 0 differences). Calling `PauseManager.TryPauseAndShowAssignControllers` on a scheduled frame was tried first: it returned false on every PC.

| `tests/resync.sh` with `DIVERGE=1600 QUIT=5400 EXTRA="--sosonline-encounter=1500 --sosonline-bot"` (re-sync during a battle) | Found mid-battle at f1620 and f1650 (stats and combat HP); the guest left at f1657, mid-battle, its character back to the party AI; the bots finished the battle; it dropped in again by a reload at f3033, after the battle; 0 of 75 snapshots differ after |
| `tests/dropin.sh` with `HOSTFLAGS=--sosonline-swapat=1500` (a character swap while the third player catches up) | The swap went out for f1505 (guest 1 given GARL on every PC); the host cancelled the joiner's catch-up the same frame; the joiner dropped in by a reload at f1783; guest 1 0 of 129, joiner 0 of 78 snapshots differ |

**Not tested:** everything Steam (lobby, invites, the invite popup, `OpenToFriends`' automatic lobby).

## 10. First Steam session with a friend: fixes (2026-11-15)

Two PCs over Steam (host at 1920×1080, guest at 2560×1440), build `d9d6210`, about 35 ms ping. Both PCs' logs were compared frame by frame.

**Saving ended the co-op session.** At a save point, answering Yes to the game's own save question ran "End session for everyone". The game reuses its `GenericPopupScreen` view and adds its handler to `onPopupAnswered` with `+=`. Our `Popup` set its handler on that event and never removed it, so the game's later question also ran our old choice. **Fix:** `NativeMenus.Popup` ignores any answer that isn't for the pop-up it opened (a token per pop-up) and clears its handler after the answer.

**Drop-in loop.** The friend dropped in again and again every few seconds. "Open to friends" restarted co-op immediately after the session ended. After each drop-in by reload, the only difference was the facing of P2's character (`party=[…/5 …/2]`), which counted as out of sync and forced a full reload every time. **Fixes:**
- `StateSync`: facing is no longer part of the `party` hash. It travels with each character's position and the guest corrects it in place, but only if the character hasn't turned since that frame.
- `Session`: after 3 re-syncs within 2 minutes, the next drop-in waits 60 s. The host sees a toast.

**Input delay from one slow ping.** On a reconnect the first ping read 1630 ms, which set the maximum delay (15 frames, 250 ms) for the whole session. **Fix:** the ping is the median of the last 9 readings, and co-op from the host's own game waits for 3 readings before choosing the delay. The automatic delay is now at least 4: every drop-in at delay 3 went out of sync and none at 5, 6 or 15 did. This works around a cause that wasn't found; revisit it once the fixes below are confirmed.

**Title screen Online menu.** The cursor went back to the first item whenever the page refreshed (a toggle, a player joining), and cancel (B/Esc) did nothing. **Fix:** the cursor stays on the same item, and cancel works like the Back item.

**Battles went out of sync, and the guest's game crashed.**
- **Damage rolls.** In the Mooncradle training, both PCs matched on every frame until a basic attack hit for 11 on the host and 10 on the guest, on the same frame with the same inputs, positions and MP. Hits get a random modifier (`PlayerAttackDamage.GetRandomDamageModifier`, `GlobalCombatSettings.playerRandomDamageRange`). The engine reseeds `UnityEngine.Random` once per frame, but visual effects (screen shakes, randomized sprites and particles, animator offsets) draw from it during the frame, and how often depends on each PC's screen. **Fix:** `Lockstep/DeterministicRandom.cs` runs every gameplay roll on a generator seeded from the session seed, the frame and the roll count, then restores the previous state. It covers the damage modifier, enemy move and pattern choices, random target picks, hit patterns, a few boss mechanics and loot. Not yet confirmed that the game rolls damage with `UnityEngine.Random`: check the `[random]` line at startup and a battle's HP in both state logs.
- **Corrections during battles.** In another training round the guest ran Valere's attack 90 frames (1.5 s) before the host. The position correction then moved Valere 5 times in the middle of the attack, the guest's battle ended early, and its game crashed 2 s after leaving the run (no error in the mod log). In-sync battles need 0 corrections (finding 8), so a gap there means the PCs are at different points of the battle. **Fix:** no position or facing corrections during a battle. A gap over 0.5 m counts as a mismatch, and two in a row start a re-sync.
- **Timing verdicts are always logged** (`[timing] attack impact …` and `[timing] block …`, a few lines per battle), so comparing both PCs' logs shows whether a battle desync came from timing.

**Drop-in autosave between training rounds.** The game allows saving in the short gaps between the training's rounds (`combat=off` for about 2 s), and a drop-in autosave was taken in one of them (f68987). The guest had already crashed by then (15:39:00, save at 15:39:24), so it didn't cause the crash, and the join-frame check would have postponed the join when the next round started. It still saved and sent files for nothing. **Fix:** the host takes a drop-in autosave only once no battle has run for 3 s (`Session.BattleSettleFrames`, 180 frames).

**Saving with P2 (second session, build `8bc51ec`).** P2 saved at a save point. On P2's PC the overwrite question stayed open about 2 s (f1508 to f1631) and closed. On the host's it never closed, so the host's game sat paused in the save menu while the guest's played on. The guest corrected a position and a facing on every snapshot for 30 s, and nothing was reported as out of sync.
- **Cause:** Rewired's hold-duration getters (`GetButtonTimePressed`/`TimeUnpressed`, the negative-button versions, and `GetAxis`/`GetAxisRaw` `TimeActive`/`TimeInactive`) were not overridden. They measure each PC's own controllers, so a hold by a remote player never advanced. IL2CPP strips methods the game never calls, so their presence in the build means the game uses them. **Fix:** `InputOverride` answers them from the synced input (consecutive applied frames, 1/60 s each, counted from the start of each run).
- **Not detected:** the snapshot had no pause state. **Fix:** a `pause` component (`PauseManager.isPaused`). A PC stuck in a menu now goes out of sync after two snapshots.
- The tutorial attack that ran 1.5 s early on the guest (above) may be the same cause, for example a hold to skip the tutorial's text.
- **Edge case:** a PC that drops in counts holds from its join frame, so a button already held at that moment reads shorter there until it is released.

**Drop-in during a level-up (third session, build `8bc51ec`).** The battle ended at f7740 and the drop-in autosave was taken at f7939, after the 3 s wait. The level-up sequence then started at f8040 by loading its own scene, `LevelUp`, on top of the area, and the join frame (f8073) fell inside it. The reload in place left `LevelUp` loaded on the host but not on the joiner. Every check after reported `scenes differs`, and the guest re-synced, reloaded and diverged again. Its game stopped logging during the third reload (crash, no game log).
- **Fix:** `GameBridge.InLevelUp` (a `LevelUp` scene is loaded, the same on every PC) and `GameBridge.DropInBlocker` (level load, battle or level-up). A drop-in is postponed on the join frame for any of them, the game doesn't count as saveable during a level-up, and the wait after a battle or level-up is 6 s (`BattleSettleFrames` 360), since the level-up started about 5 s after the battle.

**Battle desync in the same session.** The PCs matched for 200 frames while the battle waited for a command. Then Zale, the guest's own character, started his attack on the host's PC but not on the guest's. This is consistent with the hold-time getters above (an attack input cooldown would read "never pressed" for a controller that isn't on that PC). Expected to be fixed by them; confirm in the next battle.

**Guest crashes after a re-sync in a battle.** Twice the guest's game stopped within 1 to 2 s of leaving the run in the middle of a battle for a re-sync (15:39:00 and 16:32:14). No error in the mod's or BepInEx's log. Needs the game's `Player.log` from one of these crashes.

**Saving still diverged (fourth session, build `173b4f3`).** P1 saved into slot 1. The host's question closed and its menu closed, but the guest's game stayed paused (`pause differs (local [on])`, caught at once by the new check). Every re-sync reload came back paused too. The guest had no save in slot 1: since §9's change, guests only received the index and the slot in use, so the save screens showed different slots and the game asked different questions on each PC.
- **Fix:** the whole save folder is mirrored again (every slot, every autosave, the index; about 15 KB each).
- **Also fixed:** Unity's EventSystem stops processing UI input while the game window isn't focused, and the game's pop-up questions are answered through it. A player alt-tabbed to chat would never see an answer on their PC. During lockstep the EventSystem is kept focused (`InputOverride.KeepUiFocus`).
- **Also fixed:** after returning to the title screen, the Online menu failed to open (`NullReferenceException`): it still held buttons from the previous title screen. Destroyed buttons are now dropped before reuse.

**Battles: the PCs disagree on whose turn it is (fifth session, v0.1.39, logs sent with the new button).**
- In one run the first battle stayed in sync for about 1,300 frames (every impact's owner and verdict identical on both PCs). A later battle split: P1's attack landed at f7297 on the host and not on the guest.
- In the next run, at the first attack, the host ran Valere's attack bound to Rewired player 0 (P1, impact f3581) while the guest ran it about 20 frames later bound to player 1 (P2, impact f3600). Valere moved on the host from f3540, the guest stayed put.
- So on the same turn, one PC let P1 act and the other waited for P2. That matches the morning's report ("they used Valere instead of Zale"). In co-op battles a "current player" picks the next action (`EncounterController.SetCurrentPlayer`, `AdvancePlayers`), and the PCs' bookkeeping of it differs now and then, with identical inputs. Cause not found yet.
- **Added:** `Lockstep/CombatTrace.cs` logs the turn decisions with the frame (`[combat] … current player`, `selected`, `turn`, `players advance`, `<character> answers to player N`), so the next occurrence shows the first frame where the PCs differ.
- **Also found:** the log zip had no `Player.log`. Sea of Stars writes none: its Unity messages only reach BepInEx's `LogOutput.log`, which is in the zip. A crash leaves a Windows dump in `%LOCALAPPDATA%\CrashDumps\SeaOfStars.exe.<pid>.dmp`; the zip now includes the newest one made since the two included starts.

**Protocol 10:** snapshots carry each character's facing (9) and the game's pause flag (10).

**Open:**
- Why the guest ran Valere's attack 1.5 s early. It could be the same random-roll cause (the training dummy's behaviour); retest with the new build first.
- The crash: no game log was kept. If it happens again, get `Player.log` (or `Player-prev.log` after one restart) from `%USERPROFILE%\AppData\LocalLow\Sabotage Studio\Sea of Stars\` before the game is started twice more.
- The minimum automatic delay of 4.

**Not tested in the game yet:** everything in this section. The game folder was in use during the session, so the fixes were only built.

## 11. Battles and saves under real network conditions (2026-11-15, evening)

New tests reproduce what the Steam sessions showed. Two instances play over a simulated internet link: the UDP transport gets `--sosonline-lag`, `--sosonline-jitter` and `--sosonline-spikes`, because loopback hides anything that depends on real time. The bots press on different schedules (`--sosonline-botphase`) and switch characters (`--sosonline-botswitch`).

| Script | What it checks |
|---|---|
| `tests/battle-turns.sh` | A battle where both players compete for turns. Prints both PCs' turn decisions side by side and the first difference |
| `tests/save-sync.sh` | A manual save at a save point (`--sosonline-saveat=F` opens the nearest one on every PC; `--sosonline-saveby=SLOT` hands its controls to that player). Prints both PCs' menu timelines |

**Battles: the PCs disagreed on whose turn it was, on enemy casts, and on damage.** Without lag the battle stayed in sync; with lag it split in about half the runs. Causes, found one after another with the new turn and damage logs:
- **Random turn picks.** When more than one player could take the next round, the game picks one at random, and an enemy's cast length is rolled too. These rolls now go through `DeterministicRandom` (turn bookkeeping, the turn queue, enemy casting).
- **Clocks the shared clock didn't cover.** `realtimeSinceStartup` (the real-time waits in the turn logic) and `timeSinceLevelLoad` were each PC's own. So were `fixedTime` and `Time.time` read inside a physics step, about 4 s apart between the PCs. All now follow the session.
- **Physics phase.** Physics runs every 0.02 s against 1/60 s frames, so which frames get a step depends on the phase. The two PCs' phases were ~0.001 s apart, which made them step physics on different frames about one session in three. Each PC now aligns its phase to the same value on the frame before every load release.
- **Detection.** Snapshots now include an enemy's casting state. A split there had shown only as a 1-HP difference much later.

Result: 4 consecutive lagged runs with identical turn records and 0 mismatches, and identical turn sequences from run to run.

**Save point in the tests.** A script that walks up to the save point and presses Interact doesn't open it: the leader (Zale, `SunBoy`) isn't close enough to the save book with the crystal in it, at the spot where a session from slot 0 starts. `--sosonline-saveat=F` calls `Savepoint.ShowSaveScreen()` on every PC instead, which skips the book animation (`OnBookAnimEvent`, `WaitAndShowSaveScreen`). A test of the real interaction needs the leader moved into range first.

**Saves: the question closed only on the answering player's PC.** Earlier fixes (all slots mirrored, the UI kept focused) weren't enough. In the test, after Yes, `SaveManager.UpdateDataAsync` started and the write never followed, on either PC. Bisecting with the call probe (`--sosonline-callprobe`) showed that any Harmony patch on `SaveSelectionScreen.ConsumeUIPopupInputs` makes the save complete on every PC, on the same frame. Why is unknown. `Patches.cs` keeps an empty patch there. With it the save completed on both PCs both ways (P1 saving and P2 saving), with 0 mismatches.

**Why the save patch worked (found later the same evening).** A `Finalizer` probe on `ConsumeUIPopupInputs` showed that the game calls it once, on the frame the question is answered, to consume the `UI_Back` and `UI_Confirm` presses. It looks each one up with `InputCategory.GetElementForAction`, which throws a `NullReferenceException` for a Rewired player with no local controller mapped to the action: in a real session, any player whose controller is on another PC; in a test run, every player. Unpatched, the native exception unwinds the rest of the answer, and the save never continues. Any Harmony patch put Il2CppInterop's trampoline in between, which catches and logs the exception (`During invoking native->managed trampoline` in `LogOutput.log`), so the answer went on. That left the PCs inconsistent: the answering player's PC consumed the presses and the others didn't. **Fix:** during lockstep the original is skipped on every PC (`SaveCompletesPatch` is now a prefix), which is what every passing test did in effect. Tested P1 saving and P2 saving: the question opens and closes on the same frames on both PCs, one write each, 0 mismatches, no trampoline errors.

**Combat trace volume.** The last-used-controller probe logged once per frame per battle (4,351 of 4,674 lines in a short battle, ~290 KB). It now logs only when the answer changes, and its first answer in each battle. A host log for the same battle went from 320 KB to 29 KB. In test runs the answer is always `null` (no hardware). In a real session each PC returns its own device, so the next Steam logs will show whether the battle reads it.

**A guest that re-syncs during a battle was stuck.** Its copy of the battle waited for a player whose controller isn't on that PC, and nothing said what was happening. It now shows a full-screen "Re-syncing with <host>" panel until it drops in again, with Esc to leave the session instead (`Session.ResyncWaiting`). Tested: shown at the leave, hidden at the drop-in after the battle, 0 differences after. Esc is not tested (needs a real key).

**Notices** use the game's panel and font, sized to the text, and stay for about reading time (2.5 s + 0.06 s per character, between 3.5 s and 12 s). They are timed on the PC's own clock; the session clock jumps at start, which had dismissed fresh notices at once.

**Not yet tested:** these fixes over Steam with two real PCs.

## 12. After the first release with these fixes (2026-11-16)

Reported by the user from playing v0.1.45 (they reinstalled v0.1.39 to try the updater), then found by tests.

**Ending co-op re-opened it.** *End session* stopped lockstep but left everyone connected. On the next frame the host's `UpdateSoloCoop` saw a connected friend (one still dropping in, or any guest) in its own gameplay and started a new session. Now `Session.CloseAfterEnd`: the host leaves the lobby 1.5 s after the end frame (the guests stop on that frame first) and turns *Open to friends* off, like *Stop hosting*. A guest that ended with it doesn't get a second "host closed" notice.

**After a leave or the end, characters stood still and a guest lost its own game.** Nothing put a PC back to a normal solo game when lockstep stopped:
- During a session each PC's devices drive its own slot's player. A guest's pad and keyboard stayed on P2 afterwards: the guest drove Zale while the leader (Valere, P1) stood still with no devices.
- The guest's own scheduled leave never ran on its PC: its lockstep stops on the leave frame, before that frame's scheduled actions.
- When the host ended co-op, every PC kept the other players' characters on players without devices. They stood still (not the party AI) until someone used Assign Players.

Now `Session.ReturnToSolo`, from `AfterLockstep` (not while a guest re-syncs, which drops in again; if the session ends meanwhile, `Reset` does it): this PC's devices go to P1 and every other co-op player calls the game's `Leave()`, retried every second while the game refuses (battle, cutscene). Verified with the new `[coop]` probe (`CoopProbe.cs`, always on, logs on change: each player's character and devices, each party member's state, `*` = player-controlled): after a guest leaves, after the host ends, and after a guest's game is killed, both PCs show `P1 VALERE [kb]` and `ZALE=FollowerCopyTargetState`.

**Restart to update.** `AutoUpdate.Restart` quits and relaunches through Steam once the process has exited. Offered in the title screen's Online menu, and in the pause menu's Online popup only outside a running session: during a run that popup opens on every PC from synced input, so its choices must not depend on this PC's download.

**Popup buttons too narrow.** The game's `GenericPopupScreen` sizes its buttons for "Yes"/"No". `NativeMenus.FitButtons` widens each button and its fixed-width children (the highlight) to the label, at most 200 reference pixels, then rebuilds the row.

**Short text, safe names.**
- Every player-facing string is in `docs/messages.csv`. The user reviewed all of it; about 110 strings changed ("co-op" instead of "co-op session", P1/P2, no frames or re-sync jargon).
- Toasts: at most 80 characters (`Toast.MaxChars`), cut and logged when longer.
- Names: `PlayerNames.Clean` where a name enters the mod. Before the game's fonts load, Latin-range characters stand in for the font check, so every PC decides the same way ("Player 1" for a Japanese-and-emoji name on both PCs).
- The game's pixel fonts have no "→" or "@": `GameUi.Drawable` drops them, so the mod writes "->".

**Assign Players.** Every online player stands in with the same keyboard icon. Each icon now shows the player's name (12 characters) and is tinted in their colour (P1 cyan 85,241,255 and P2 amber 255,190,19, sampled from the game's bubbles; P3 pink is approximate). The column's `VerticalLayoutGroup` ignores item heights, so its spacing grows to fit the tags when two players stack on one character. Tags, tint and spacing are undone offline. The pool method `OnExitPool` was not patched (small methods are often folded).

**Save slot names (not changed).** The game's save screen shows "1  The Forbidden Cavern  11/15/2026 9:50 PM". The mod's *Online > Start* keeps "Slot 1" by the user's choice. Notes if it's revisited: the location comes from `SaveGameSlot.CheckpointData.level.GetLevelNameLocId()`, but `LocalizationManager.GetText` threw a null reference inside the interop call at the title screen (both overloads, with a controller and player passed). `SaveGameSlot.Timestamp` is .NET ticks in local time.

**Camping.** The campfire near the user's save only says "The campfire feels cozy" (camping isn't unlocked yet in the story). Started through `CampingSpot.ToggleCamping()` instead (`CampingManager.StartCamping` only sets the flag): the intro, camp, the Rest choice (a held Confirm) and the end of camp run on the same frames on both PCs, with 0 mismatches, HP included.

**Bosslug split (fixed in §13).** Since the user's newest save moved into the Forbidden Cavern, `battle-turns.sh` reaches the Bosslug fight. The guest splits at frame 1800 in 4 of 6 runs (1 of 2 on v0.1.45): the Acid Bug adds differ; every turn decision and damage roll before that matches. The re-sync test also splits a second time after its first drop-in (party HP 0 on the guest at f8970), on v0.1.45 too. Next: call-probe the boss's AI between f1700 and f1800.

**Stage 1 test.** Its guest script chose pause entries by cursor moves and reached *Pause for everyone* instead of *Leave co-op*; the host then quit early and the late joiner waited 300 s at the title screen. Now `--sosonline-pausechoose` chooses by label, and `session.sh` skips a late joiner whose host has quit.

## 13. Battle determinism and re-sync by replay (2026-11-16, branch `milestone/battle-sync`)

**A/B first.** `battle-turns.sh` on the Bosslug fight with the released v0.1.45 (`a7d2819`): split in 1 of 4 runs, the same way as today's build. Not a regression.

**Spawns are traced and hashed (`Lockstep/SpawnTrace.cs`).** Postfixes on `SummonMoveHelper.SpawnEnemy` (summons), `Encounter.AddEnemy` (every enemy entering a battle, the opening roster included) and `Encounter.RemoveActor`, each with the class guard and the fold check. Each event is logged once (`[spawn] f1784 #1 add AcidBugEnemy slot 2 at (-35.0,2.0,236.9)`) and goes into a running count and hash, reset on every load release (the same frame on every PC). The snapshot's `spawn` component is that count/hash, and the snapshot carries its window's events, so a guest names the first spawn that differs and its frame: `spawn differs at f1784: host [f1784 add AcidBugEnemy slot 2], local [f1784 add AcidBugEnemy slot 1]` (reproduced with `--sosonline-nolockfix --sosonline-noresync`). A spawn difference is listed first among the reasons.

**The Bosslug split: spell lock types rolled outside the deterministic wrappers.** Found with the call probe on the summon classes, then on `EnemyCastingData`/`EnemyCastingUI`:
1. f1379: the Bosslug starts a cast; `EnemyCastingData.CastMove` picks the spell lock types from the move's `CombatMoveSpellLockSettings`. It wasn't wrapped, so it drew from `UnityEngine.Random` after whatever visual effects consumed that frame: host `[Blunt, Sword]`, guest `[Moon, Moon]` (other runs: `[Sword, Sword]`).
2. f1555: Zale's timed hit calls `BreakLocks(Sword)` on both PCs; it breaks a lock only where there is a Sword lock. Combo points differ from here (+50 vs +200).
3. The lock break makes that PC's Bosslug re-plan (`SummonMoveHelper.ReserveSummonSlot` on one PC only), and from f1784 the summoned Acid Bugs differ: the split the old snapshot found at f1800.

Fix: `CastMove`, `SwapNextMove`, `EnemyCombatActor.GetSpecialMoveLocks`, both `GetSpellLockSettings` and `Bosslug.UpdateNextMove` run with the deterministic generator (`DeterministicRandom.LockTargets`; `--sosonline-nolockfix` leaves them out). None of the folded `CombatActor.On…TurnDone` methods was involved. Result: 5 of 5 consecutive `battle-turns.sh` runs with 0 guest mismatches and identical turn lines; lock types identical on both PCs in every run. Any enemy that casts with locks goes through the same code, so this covers more than the Bosslug.

**Re-sync splitting again after its drop-in (handoff open item 2): a drop-in autosave during a game over.** In `resync.sh` the bot party loses the battle. Both party members at 0 HP, the game still says it can save, and the Stage 2 drop-in autosave was taken at f7586, just before the game's own game-over reload (f7619) restored the party on the host. The guest loaded a party at 0 HP. Now `GameBridge.PartyWiped` blocks `CanSaveNow`, counts as a battle for the settle time, and is a `DropInBlocker` ("a game over"). Rerun: autosave at f8223 (after the game-over reload), 0 differences after the join.

**More combat state in the snapshot.**
- `combat` (structural, never corrected): each actor as `name#slot` (two adds of one kind differ by slot), `/dead`, and for enemies `/cast`, `/t<turns to action>l<total locks>:<locks left>[<lock types, ! revealed>]`; every actor's gameplay modifiers (statuses and buffs, sorted); `turn=<current turn's main actor> round=<round>`.
- `hp`: every actor's HP. `combo`: the party's combo points. `stats` (party HP/MP) as before.
- `spawn` (above), and `test` (only set by `--sosonline-hpdiverge`).
- HP, MP and combo points are corrected in place on the guest, like positions: the snapshot carries them as numbers, and the guest applies host minus its own value at the snapshot frame to the live value. Only when every structural component agrees, all or nothing, and never across 0 (a death is structural). Logged as `[sync] frame 1620: corrected hp:VALERE 48->49`. Tested with `--sosonline-hpnudge=1600` on the guest mid-battle: corrected at the next snapshot, 0 mismatches, 46 of 46 later snapshots identical.
- Cost: `battle-turns.sh` on the Bosslug fight, 167 snapshots per PC: 3.11 ms average, 18.5 ms worst (v0.1.45 on the same test: 3.06 ms, 18.4 ms).
- Because HP alone is now corrected, `--sosonline-hpdiverge` also marks the PC as diverged (the `test` component) so the re-sync tests still re-sync. It fires once per frame per process (a replay passes it without effect) unless `--sosonline-hpdivergeagain` is given.

**Re-sync by replay (Stage 3), mid-battle included.** A guest that diverged used to wait for a saveable moment, so to the end of a battle. Now the host's `RequeueResynced` sends it through the Stage 3 catch-up when the area log is valid: the guest loads the area entry save, replays the log uncapped behind the catch-up screen ("Rejoining *host*'s game", "The others keep playing while you rejoin."), and merges ("You're back in sync."). Falls back to the old way (wait, then reload in place) when:
- there's no valid area log (logged with its reason);
- the replay diverges from a host snapshot (`CatchUpFailed`): the guest goes back to the waiting screen (`Session.RejoinFallBack`, no return to solo) and the host sets `ForceStage2`;
- it takes over 120 s (`DropInAbort`, same fallback);
- it diverges again at the same point: the same area entry and a divergence frame within 120 frames of the one the last replay re-sync was for (`Peer.ReplayL`, `ReplayDivergedAt`; the guest now sends its divergence frame with `ResyncRequest`).
`ResyncLimit`/`ResyncHold` apply: a held peer doesn't start a catch-up before `HoldUntil`.

Two things the catch-up needed for this:
- **Observer mode** (`LockstepEngine.Observer`). A re-syncing guest's own slot was in the session at the area entry. Its catch-up run would have captured its own devices for that slot over the logged input, and stopped itself on its own logged leave frame. While catching up, the local slot's input comes only from the log and its leave doesn't stop the run; cleared on the merge message.
- **Physics phase at L.** `AlignPhysicsPhase` runs on frame L−1, before a catch-up joiner's run starts, so a joiner entered L with its own phase (0.013 instead of 0.005), stepped physics on other frames, and a replayed battle drifted (a character 5.4 m off at f840; replay failed). Now the joiner aligns on its last held frame before L (`CatchUpState.Aligned`). This affected every Stage 3 join whose replay crosses a battle.

Results (`resync.sh`, `DIVERGE=1600`, bot battle from f1530 to f7200, lag 20 ms + jitter 25 ms):
- Diverged at f1654, left at f1662, replayed 1,336 frames from f333 in 3.5 s, merged at f1882, in the battle (P2 playing Zale to its end). After the merge: 241 snapshots compared, 0 differ.
- With the divergence reproducing in the replay (`--sosonline-hpdivergeagain`): the replay diverged at f1650, the guest went back to the waiting screen, and the host reloaded it in place after the battle (join f8349, autosave f8223). 34 snapshots after, 0 differ.
- Gap: right after the area entry a replaying joiner sees the Bosslug NPC active at f390 when the host doesn't (one snapshot, `npcs`). Seen in 7 of 12 later runs, clean ones included; never in the next snapshot, so it doesn't fail a replay, but two in a row would.

**Summon slots reserved with the shared generator (the split after a rejoin).** The second sweep's `resync.sh` rejoined by replay, then split at f5401: same spawns on the same frames, slots swapped (host slot 2 then 1, guest 1 then 2). Lock types matched. Found with a summon probe (`--sosonline-summonprobe[=N]`: the summoner's `summonSlots` list, reservations, assignments, and the `UnityEngine.Random` state):
- One `SummonMoveHelper.ReserveSummonSlot` call per summon (f3472, f5047) reserves every slot it needs; the spawn coroutine then fills them from the end of the list. It ran outside every deterministic wrapper.
- The first summon's list was always `[1 2]` (fresh slots). The second one's, re-reserving the dead bugs' slots, varied: on the host from run to run, and in 1 of 4 probe runs (2 of 10 runs in all) the guest's differed from the host's.
- The guest's generator state at that reservation came in two values, each always giving the same order (`a0f150e5` → `[2 1]` in runs 5 and 8, `2b5cf2f2` → `[1 2]` in runs 6 and 7); the host's differed every run. The order is a pick from the shared generator, which visual effects keep out of step between PCs.
- The plain battle-turns runs end before a second summon, which is why they never showed it.

Fix: `ReserveSummonSlot`, `DoPermanentSlotReservation`, `GetEnemyPrefabsToSummon` and `GetSpawnPosition` run with the deterministic generator (`DeterministicRandom.SummonTargets`; `--sosonline-nosummonfix` leaves them out); 49 rolls wrapped in all. Results:
- The Bosslug fight with the probe drawing 7 (then 3) extra values on the host only, right before each reservation: both PCs reserved `[1 2]` in both summons, 0 mismatches. Before the fix the same setup gave the host different orders from run to run.
- `resync.sh` (to f9500) 6 times in a row: rejoined by replay each time, 241 of 241 snapshots identical after the merge, identical summons on both PCs, no errors.

**Session clock: summed from the base (fixed).** `time` was each PC's own accumulated time plus an offset, so its last bits depended on that PC's clock magnitude (`45.42000099129975` vs `45.420000991299744` at f1417). `fixedTime` was worse: about 8e-10 apart in every battle clock line (0 of 39 identical in the sweep's `battle-turns`). Now, while lockstep ticks, `time` is the agreed base plus each Unity frame's step (the local difference rounded to float, the exact `captureDeltaTime × timeScale`, the same on every PC), advanced lazily on the first read in a frame and from Rewired's Update (`GameClock.OnFrame`); `fixedTime` is the base minus the aligned physics phase (rounded to 1e-5) plus whole fixed steps since the base. When lockstep stops, time carries on from the session value. Result: 45 of 45 battle clock lines identical on both PCs (`time`, `level`, `fixed`); every test since passes.

**Characters given back are logged, not a reason to drop the area log.** A guest that re-synced by replay gets its old character back on merging (`[characters] … giving it back`), and that invalidated the area log: in the second sweep the next divergence had to wait for a reload ("a character was reassigned"), and so would any later drop-in in that area. The give-back is now an area log entry (`AreaLog.Characters`, sent with `CatchUp`; joiners already replaying get the live `AssignCharacter`, which a catching-up PC no longer ignores). Tested with `DIVERGE=1600,3000`: the second divergence rejoined by replay and replayed `P2 plays ZALE from f1905`.
- That second replay then diverged at f1680, where the party AI takes over Valere after the guest's logged leave (f1661): Valere 3.3 m off, the Bosslug casting on one side only. The first replay of the same frames matched (148/148 after merging), so the party AI's battle turn after a leave is intermittently different. It falls back to the reload, as every second divergence did before. Open.
- The "diverges again at the same point" rule (`Peer.ReplayL`, `ReplayDivergedAt`, 120 frames) is removed: a guest merges well past its divergence (about 220 frames), so it could never apply, and a cause that reproduces in the replay already makes the replay diverge.

**The Bosslug a snapshot early in a replay: logic clipping (cause found, not fixed).** `--sosonline-npcprobe` (every frame: active NPCs and each NPC's first inactive ancestor) showed the Bosslug's `NPCController` component switched on f362, off f372, on f418 live (host and guest alike), and on at f392 in a replay. The game's `LogicClippingManager` turns off `ILogicClippable` logic outside the camera's frustum (`FrustumCullingSystem`), and a replay renders behind the catch-up screen. Turning clipping off during lockstep (tried: `DisableClipping` each frame, `EnableClipping`/`ResumeClipping` skipped) kept live play in step but every replay failed: the off-screen Luslugs then wander, and their positions differ in a replay. Reverted. Worth knowing for Steam: what's on screen differs between PCs with different window sizes, so off-screen logic may differ live too; not seen in loopback (same-size windows).

**Return to area start (host).** A friend that waits for a reload (replay off or failed) waits until the game allows saving, the end of a battle at worst. Where saving isn't allowed and a friend waits, the host's Start opens a local pause page with *Return to area start*, *Pause for everyone* (not in battles: the game has no pause there and ignores Start, checked again with `--sosonline-pausestate` at six points of a battle, with and without the bot) and *Back* (cursor on Back; elsewhere Start goes to the game as before). On *Return*: the host writes the area entry save it kept (`AreaLog.SaveFiles`) into its save folder; playing guests get it in `Msg.AreaFiles` and waiting ones in the usual drop-in transfer; every PC has the game read its saves again (`SaveMirror.ReloadSaves`, the reload uses the in-memory slot) and reports `Ready`; then a drop-in reload (`JoinNow`) of that slot with the party where it stood at the area entry, flagged so that a battle doesn't postpone it (only a level load does). Cancelled if the party changes area or after 60 s. Test (scratchpad `returntest.sh`): guest diverged at f1654 in the Bosslug fight with replay off; host chose it at f2397; everyone had the save at f2413 and reloaded; P2 back on Zale at f2505; 56/56 snapshots identical after. Protocol version 12.

**Esc on the waiting screen: tested with a real key press.** Re-sync with `--sosonline-nocatchup` (the guest waits), then a Windows key event to the guest's window: `left the session from the waiting screen`; the host shows "Player… left. The AI plays their character." and goes back to solo.

**P3's colour: now the game's.** Assign Players with three players (`dropin-two.sh` + `--sosonline-assigntest`, `--sosonline-uiprobe`, a screenshot): the game's P1/P2 badges match the mod's colours exactly; its P3 badge is (255,108,174), not (255,92,170) (from the pink "Ready!"). Changed. Seen there too: the "joined" toast overlaps the screen's title.

## Tools added

| Script / flag | Use |
|---|---|
| `tests/battle-live.sh` | Two-instance battle; prints the guest's corrections and mismatches |
| `tests/dropin.sh` | Three-instance drop-in; `HOSTFLAGS`, `JOINFLAGS`, `JOIN_DELAY`, `QUIT`; compares both guests with the host |
| `tests/menu-status.sh` | Host and guest walk; the guest pauses and opens Options, the host opens the main menu; a third instance catches up. `HOSTSCRIPT`, `GUESTSCRIPT`, `SHOTS`, `EXTRA`, `JOIN_DELAY`, `QUIT` |
| `--sosonline-uiprobe=F` | At frame F, log every visible game screen's images (sprite, slicing, colour, size) and texts (font, material, size, colour) |
| `tests/dropin-two.sh` | Host alone, then two joiners in one area; `HOSTFLAGS`, `JOINFLAGS`, `AFLAGS`, `BFLAGS`, `JOIN_DELAY`, `JOIN2_DELAY`, `QUIT` |
| `--sosonline-noclock` | Disable the shared clock (A/B) |
| `--sosonline-nocatchup` | Force Stage 2 |
| `--sosonline-loadatjoin` | Start a level load the frame before the first join frame |
| `--sosonline-play=SLOT`, `--sosonline-joinafter=S` | Joiner plays its own game first |
| `--sosonline-tracecalls=Class[.Method],…` | Log calls with the lockstep frame. Tracing all of `CombatManager` stalls the boot; trace specific methods |
| `--sosonline-dashprobe`, `--sosonline-fixedlog=F1,F2` | The probes that found finding 8 |
| `--sosonline-endsession=F` | The host ends co-op from its pause screen (§12) |
| `--sosonline-pausechoose=F:Label,…` | Choose a local pause entry by label, `_` for a space (§12) |
| `--sosonline-campat=F`, `--sosonline-campstart=F`, `--sosonline-campstop=F` | Campfire interaction; camping past the story unlock (§12) |
| `--sosonline-fakeupdate=V`, `--sosonline-name=TEXT` | Restart-to-update UI; hostile player names (§12) |
| `[coop]` log lines | Who controls which character with which devices, on change (§12) |
| `[spawn]` log lines | Every enemy added to, summoned into or removed from a battle, with the frame (§13) |
| `[combat] … BreakLocks(Type): locks N -> M [...]` | Every spell lock hit, with the lock list before it (§13) |
| `--sosonline-hpnudge=F` | Lower HP by 1 on this PC only, without marking it diverged: the guest corrects it in place (§13) |
| `--sosonline-hpdivergeagain` | `--sosonline-hpdiverge` fires every time the frame is reached, replays included (§13) |
| `--sosonline-nolockfix`, `--sosonline-noresync` | Spell lock rolls left to each PC; a guest that reports differences without re-syncing (§13) |

**Run hygiene:**
- Run `tests/display-prefs.sh save` before a test session and `restore` after.
- Delete `SosOnlineTest-*` and `SosOnlineSession-<port>` folders in the save folder afterwards. Leave `SosOnlineSession` and `SosOnlineBackup` alone.
- Don't build while a test game runs: the build copies the DLL into the game folder.

## 14. Drop-in spikes: results (2026-11-15)

The spikes that gated Stages 2 and 3; their instructions are in [PLAN.md](PLAN.md#appendix-spike-instructions).

### Prerequisite — state log (2026-11-15): pass

- Components logged: `lvl`, `scenes`, `party` (name/facing angle), `stats` (HP/MP per current party member), `gold` (currency amounts), `inv` (kinds/total), `combat` (`off`, or each actor's HP), `npcs` (every active `NPCController`, name and position to 0.1 m), `pos` (party positions, compared with the existing 2 cm drift correction). `RngProbe` is gone.
- Walk scripts, two instances: the 61 snapshots up to frame 1800 match. `--sosonline-hpdiverge=1800` on the guest (ZALE 12 → 11) is reported on the next snapshot, with that component only: `frame 1830 mismatch #1: stats differs (local [ZALE:11/3,VALERE:11/2,GARL:15/2])`.
- Cost: 3.4 ms per snapshot on average, 25.6 ms at worst (81 snapshots). Mostly `FindObjectsOfType<NPCController>`. At the default interval that is 0.1 ms per frame; Stage 0's rotating subset is not needed yet.
- `gold` is empty in the test save (the early-game party owns no currency yet). Spike B should use a save that has some.

### Spike A — reload in place (2026-11-15): pass

How: `GameBridge.ReloadInPlace` calls `SaveManager.LoadSaveSlotAndSetLastIndex(slot)` (its `onSaveSlotLoaded` restores the managers), then `LevelManager.LoadLevel` with `showTransition = true`, `removeTransitionWhenDone = true` and a `GameplayLevelInitializerParams` built from the slot's `checkpointData`. Lockstep keeps running; nothing goes through the title screen.

| Check | Result |
|---|---|
| Title screen in screenshots | None. The game's fade covers the load (frame 1500 mid-fade, 1600 fully visible). |
| State logs after the reload | Identical on both PCs: 31 of 31 snapshots, in each of four runs. |
| Guest controls a character again | Yes, with no new Join press. The co-op player survives the reload (`P2 playing` never goes false); the re-armed auto-join finds it already playing. |
| F+120 to party controllable | 76–84 frames of loading (1.3–1.4 s), then the fade. The guest's scripted walk at frame 1500 moves its character. Well under 300 frames. |
| Loading-finished gap between PCs | Without the barrier: 0 frames for the in-place reloads (1497/1497, 1499/1499, 1496/1496); 1–3 frames for the title-screen load at session start (333/335, 331/330, 327/327). One such gap produced `frame 330 mismatch: npcs differs`. With Stage 0's barrier, both PCs end every load on the same frame (333/333, 1504/1504); the hold costs 8–9 frames on loopback. |
| Where the party reappears | At `checkpointData.position`, the point where the party entered the area (here (9.08, 1.00, 13.83)), not where it stood. Without initializer params the party lands at a level default ((14.67, −1.00, 67.28)) and the screen stays black. |

Position restore: `UpdateCheckpointData` is not needed. `GameplayLevelInitializerParams.spawnPosition` (with `spawnPositionSet = true`) and `playerOrientation` place the leader exactly: 0.00 m from where it stood at the reload frame. The game re-forms the followers around the leader, so `--sosonline-reloadpos` also moves every party member back to its own position on fixed frame F+240. After that, the leader and P2 are within 0.01 m, and Garl (AI follower) is 0.47 m from where the game placed him before resuming his follow. The logs still match.

For Stage 2:
- Take the positions on the reload frame, not the save frame; the party keeps moving in between (2.5 m in this test).
- The position fix-up frame has to come after the load ends. With the barrier, the release frame is known on every PC, so use release + a few frames instead of a fixed offset.
- Inputs a player makes between the reload and the fix-up are undone for their character. Either block movement until the fix-up, or apply it on the release frame.
- `--sosonline-reload` and `--sosonline-reloadpos` remain as regression tests.

### Spike B — save vs live state (2026-11-15): partial

Setup differs from the instructions in three ways, all scripted in `tests/spike-b.sh`:
- No save had to be prepared. Slot 0 starts at the Forbidden Cavern entrance; run A walks out to the world map and back in, so the **third** level load is the area entry (`--sosonline-entryload=3`). The cavern's enemies don't patrol, so the battle is started by `--sosonline-encounterat=700` on both runs, on frame L + 700.
- L is the loading barrier's release frame for the entry load. The release is decided a few frames ahead, so B knows L_B before it captures the inputs for that frame. C came on L itself in every run (`C=f1104 (L+0)`), so nothing changes between L and the autosave.
- B needs A's save index (`SOS_SaveDataIndex.sos`) next to the autosave, or the game reports the autosave slot as missing. Both are copied into B's sandbox (`--sosonline-sandboxfile`). Both runs use the same `--sosonline-seed`, and B reseeds the RNG as on A's frame L_A + k.

Result over 3,896 frames from L (walk, battle, after):

| Run | Identical until | First difference |
|---|---|---|
| 1 | L+1859 | `combat` for 1 frame: Luslug_A 13 HP on A, 14 on B (a hit applied one frame apart), identical again after |
| 2 (`--sosonline-trace`) | L+1120 | `combat` at L+1121, the same kind of one-frame hit timing; then positions, facing, NPCs |
| 3 (exact party positions transferred at L) | L+1859 | as run 1, then a follower 8 cm off after the battle (L+1903), facing (L+1928), NPCs (L+1975) |
| 4 (`--sosonline-trace`) | L+1903 | follower position after the battle |

- The save reproduces the area-entry state: every component is identical at L and for the next 1,120 to 1,900 frames, including the first half of the battle. Party positions agree to the bit: the transfer at L corrected 0.0 on every axis.
- What differs is battle timing, by one frame, at varying points from run to run, with knock-on differences after it. The trace shows identical decisions on the same frames, but the game's timed-input windows are absolute `Time.time` values: `afterEnd=56.265` on A against `43.175` on B. A replaying joiner cannot share the host's time base.
- **This is not specific to the save.** The same check on a normal live two-instance session with a battle (`--sosonline-encounter=1500 --sosonline-bot`) also reports `party differs` at frame 1740 and `npcs differs` at 2040, and the guest corrects all three party positions on every snapshot during the battle. The old snapshot couldn't see either.

**Rerun with the shared game clock (finding 8 fixed): pass.** `tests/spike-b.sh`, with the replay adopting the recording's clock at L: all 3,898 frames from L identical, bit for bit (positions too), 1,383 of them in battle.

Against the pass condition (every component identical for 3,000 frames including the battle) the original runs were a fail. Against the question it was meant to answer (does the save reproduce live state) it is a pass. What blocks Stage 3 is the same drift that live sessions already have; see the plan's new finding 8.

### Spike C — replay speed (2026-11-15): pass (cameras off)

Run B replayed with `--sosonline-ff`, snapshots every 30 frames (every frame costs about 3.4 ms and caps the speed near 300 fps):

| | Free roaming | Battle | Loading |
|---|---|---|---|
| Cameras on | 147–156 fps (2.5×) | 143–145 fps (2.4×) | 218–444 fps |
| Cameras off | 1,008 fps (16.8×) | 984 fps (16.4×) | 1,325–1,572 fps |

- Correctness: with cameras off and snapshots every frame, B matches A up to L+1120 and first differs on the battle hit at L+1121. Unaccelerated runs also diverged on that hit (run 2), so neither fast-forwarding nor disabling the cameras adds visible divergence of its own. No battle-free run longer than 1,120 frames was tested.
- Loading takes the same wall-clock time, so it spans many more frames (5,768 frames at 1,325 fps). Lining up on L absorbs that.
- Rule for Stage 3: catch-up time = frames since area entry ÷ (speed − 60), since the session keeps moving. At about 1,000 fps with cameras off, a 60 s cut-off covers about 56,000 frames, or 15 minutes since the area entry. With cameras on (150 fps) it covers 5,400 frames, or 90 s.
- Not handled: the joiner's screen during catch-up (cameras off means a static frame or an overlay), and the game's own visibility callbacks (`ObjectBecameVisible`) while cameras are off. Neither showed up in the comparison, but a battle-free run longer than 1,120 frames would confirm it.
