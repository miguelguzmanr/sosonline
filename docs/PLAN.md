# Plan: seamless drop-in / drop-out

Goal: joining and leaving a co-op session should feel like Divinity: Original Sin 2 or Baldur's Gate 3. A friend joins a game in progress from Steam, the players already in it are barely interrupted, and nobody visits the title screen.

Spike instructions are in the [appendix](#appendix-spike-instructions); their results are [findings §14](FINDINGS.md#14-drop-in-spikes-results-2026-11-15).
Causes and fixes found along the way: [FINDINGS.md](FINDINGS.md). Current state and open items: [STATUS.md](../STATUS.md).

## Where the mod stands

**Drop-out is already at the target.** A player leaves on an agreed frame, their character returns to the party AI, and the others never stop.

**Drop-in is the gap.** Today a joiner:
1. is admitted as *waiting*,
2. waits for the party's next area change (or the host's *Let waiting players in now*),
3. then everyone returns to the title screen and reloads the host's autosave.

**Why.** Larian's games stream world state from the host. This mod syncs inputs only (deterministic lockstep), so a newcomer needs a simulation identical to everyone else's. The only way to get one today is a shared save load.

**Approach.** Keep lockstep. Shrink the shared reload in stages until only the joiner notices it.

## Review findings the plan depends on

| # | Finding | Where |
|---|---|---|
| 1 | **The desync check is nearly blind.** The engine reseeds `UnityEngine.Random` every frame, and the state snapshot probes the RNG right after. The probe matches on every PC by construction. Only scene names and party positions are really compared, so a divergence in HP, enemies or inventory goes undetected. | `Lockstep/LockstepEngine.cs` (reseed in `Tick`), `Lockstep/StateSync.cs` (`CaptureLocal`) |
| 2 | **Joiners are refused during a start or reload.** The host answers "try again in a moment"; the guest then leaves the lobby. There is no queue or retry. | `Net/Session.cs` (`Msg.Hello`, `Msg.Reject`) |
| 3 | **A crashed player freezes everyone for 6 s.** The engine blocks the game thread while it waits for input, and the host only drops the slot after 6 s. | `LockstepEngine.Tick`, `Session.OnStalled` |
| 4 | **Taking a character is slow.** After a start the auto-join waits 240 frames, then retries a simulated Join press every 90 frames. | `Session.AutoJoin` |
| 5 | **The reload is manual and timer-driven.** Wall-clock waits (0.5 s, 2 s), guests must already be on the title screen, and the host must start a session from the title screen. | `Session.UpdateDropIn`, `Session.StartFromSave` |
| 6 | **No loading barrier.** Levels finish loading 1–2 frames apart on different PCs. | README, *Not yet verified* |
| 7 | **The whole save folder is sent on every start**, including slots the session doesn't use. | `Game/SaveMirror.PackHostFiles` |
| 8 | **Live sessions drift around battles** (found by Stage 0's state hash). In a two-instance battle the guest corrects all party positions on every snapshot, and facing and enemy positions differ by small amounts afterwards. **Fixed:** each PC's own clock. The party's dash into a battle times its 0.1 s anticipation with `smoothDeltaTime` (Unity's average of that PC's recent frame times) against absolute `Time.time`; six frames make exactly 0.1 s, so float rounding ended it on frame 6 on one PC and frame 7 on the other. `GameClock` makes every time getter read the same on every PC. | `Lockstep/GameClock.cs` |

Finding 1 also means the README's "N of N snapshots identical" results prove less than they read: they confirm scenes and party positions only.

## Stages

Each stage is shippable on its own. Stages 2 and 3 are gated by spikes.

### Stage 0 — Foundations

Needed by every later stage, and by the spikes.

- **Real state hash.** Replace the RNG probe with game state: level id, party HP/MP, gold and inventory, combat actors and their HP. Log each component separately so a mismatch names what diverged.
- **Loading barrier.** Each PC reports "level loaded" with its frame. The host picks a release frame, and every PC resumes the simulation on it.
- **Faster drop detection.** Use Steam's connection status to drop a lost peer at once, and cut the silent-peer stall from 6 s to about 2 s.

Done when: a deliberately injected divergence (for example, one PC's party HP changed by a test flag) is reported within one snapshot interval, and two instances leave every level load on the same frame.

### Stage 1 — Always joinable, one re-sync procedure

No new game behaviour is required; this uses calls the mod already makes.

- **Open to friends.** A setting in the Online menu. The host plays normally with a Steam lobby open. No hosting step from the title screen.
- **One procedure, three uses.** *Re-sync*: autosave on an agreed frame, send that slot to whoever lacks it, reload, start lockstep with the new roster. Use it for:
  - solo → co-op (first friend joins),
  - drop-in (another friend joins),
  - automatic recovery when the state hash reports a desync.
- **Queue joiners.** Never reject for timing. Trigger the re-sync at the first moment the game allows saving, not at the next area change. During a battle or cutscene, show "*name* joins after this".
- **Join from anywhere.** A guest who accepts an invite while in their own game gets one confirmation; the mod then leaves that game for them.
- **Co-op → solo.** When the last guest leaves, lockstep stops on an agreed frame and the host continues in the normal game with the lobby still open.
- **Characters.** Remember which character each Steam ID played, and give it back on rejoin. Join the co-op player directly on a scheduled frame (`SinglePlayerPlusPlayer.Join`) instead of simulating button presses.
- **Send only the slot in use** (plus index and options).

Done when: with the host mid-game and never having opened the Online menu beyond the setting, a friend accepts a Steam invite and both are playing within one reload, with no manual steps on either PC.

### Stage 2 — Reload in place

Gated by **spike A**.

- Load the autosave from gameplay behind the game's own transition, skipping the title screen.
- Restore party positions after the load so nobody snaps back to the area entrance.
- Target: a fade of a few seconds for everyone, comparable to Larian's "player is joining" sync.

Done when: a join during free roaming costs the existing players under 5 s, and they resume where they stood.

### Stage 3 — Only the joiner waits (stretch)

Gated by **spikes B and C**.

- Every PC keeps the input log since the party entered the current area.
- The joiner loads the area-entry autosave, replays that log fast-forwarded with rendering and audio suppressed, and merges into the live session on an agreed frame.
- If the estimated catch-up is too long, or the state hash disagrees at the merge, fall back to Stage 2.

Done when: a third instance joins two that are walking around, and the two never pause.

## Progress by stage (2026-11-15, Stage 3 updated 2026-11-16)

- **Stage 0: done.** Real state hash, loading barrier (`Lockstep/LoadBarrier.cs`), Steam connection loss and a 2 s silent-peer drop. Steam's connection-loss path is untested (no second Steam account here); the 2 s drop is tested on loopback.
- **Spike A: pass.** Stage 2 is possible.
- **Spike B: pass** since the shared game clock (finding 8): the replay is bit-identical to the recording for all 3,898 frames from the area entry, 1,383 of them in battle.
- **Spike C: pass with cameras off** (about 16× real time).
- **Stage 1: done (loopback; nothing Steam-specific tested).** See findings §9. Done: open to friends, co-op from the host's own game and back to solo, re-sync on desync, characters given back on rejoin, only the session's slot sent, join from a Steam invite in gameplay with one confirmation. Not done: joining by a direct `SinglePlayerPlusPlayer.Join` (the synced Join press stays, see §9). The plan's risk table still asks for a real two-account Steam test.
- **Stage 2: done (loopback).** `Session.UpdateDropIn`: autosave on an agreed frame, files to the joiner, then a join frame on which every PC, joiner included, reloads in place with the party's positions. The engine adds slots mid-run (`LockstepEngine.AddSlot`, `Start(startFrame)`). Measured in three-instance runs: 92–95 frames (about 1.6 s) from the join frame to the release, guests playing 5 frames later, the joiner's state identical to the host's from then on. Also takes in two Stage 1 items: joiners queue for the first saveable moment (no area-change wait, no "let them in now"), and nothing is rejected during a drop-in.
  - Co-op players are not in the save and survive an in-place load, so every PC makes the guests leave on the join frame and they rejoin by synced Join press.
  - A guest that is still booting must not drop in (or start a session): the boot sequence later loads the title screen on top of the game. Guests now report ready only once `GameBridge.Booted`.
  - A save older than 5 s when the joiners are ready is replaced by a fresh one.
  - A level load or battle running on the join frame: every PC already in sees it on that frame and postpones; the joining slot leaves again on the same frame, the joiner stops and waits, and the drop-in starts over with a fresh autosave (`--sosonline-loadatjoin`: postponed at f1911, joined at f2155, joiner identical to the host afterwards).
  - Joining from a guest's own gameplay: tested (`--sosonline-play`). The joiner's game is replaced in place; identical to the host afterwards.
- **Finding 8: fixed** (`Lockstep/GameClock.cs`). Live two-instance battle: 0 corrections and 0 mismatches in 121 snapshots (was 10 corrections and 2 mismatches).
- **Stage 3: working (loopback), on by default, falls back to Stage 2.** See *Stage 3 status* below.

### Stage 3 status

How it runs:
- On every load release (frame L) every PC autosaves. The host keeps a copy of that save, what the save doesn't hold (party positions, game clock, slot → player map, co-op players playing), every frame's inputs and its state snapshots from L on (`Net/AreaLog.cs`).
- A joiner gets that save, starts lockstep on frame L behind everyone (the engine holds until the save has loaded), replays the log uncapped with cameras and audio off, and then follows the live inputs as an observer. It checks every host snapshot on the way.
- Once it runs within the input delay of the session, the host picks a merge frame; every PC adds the slot there and the joiner presses Join. Nobody already playing reloads or waits.
- Joins and leaves since L are logged with their frames and replayed on the joiner, so a second joiner in the same area catches up too.
- Falls back to Stage 2 when: no valid log (the entry save wasn't allowed, a Stage 2 drop-in since L, more than 54,000 frames since L), the replay diverges from a host snapshot, it takes over 120 s, or a Stage 2 drop-in starts meanwhile.
- A frame on which a load is released doesn't run before that load has reached its end on this PC (a joiner replaying uncapped would otherwise get there first).
- The joiner lines up its physics phase on its last held frame before L (the session did it on L−1, before the joiner's run starts). Without it a replay through a battle drifted (findings §13).
- **Re-sync by replay (2026-11-16):** a guest that diverged rejoins the same way, right away, even mid-battle. Its own slot's input comes from the log while it catches up (`LockstepEngine.Observer`). Falls back to the old wait-and-reload when there's no valid log, the replay diverges, it times out, or it diverges again at the same point of the same area (findings §13).

Measured (three loopback instances, `tests/dropin.sh`):

| Case | Result |
|---|---|
| Joiner during free roaming | 1,420 frames replayed, caught up 3.6 s after starting (1.8 s of it loading the save); merged; P3 playing 6 frames later. Joiner identical to the host in all 129 snapshots; guest 1 likewise. |
| Area entry after P2 was playing | The joiner's first catch-up was overtaken by a level load and restarted from the new entry; P2 joined on the joiner at L; identical in all 129 snapshots. |
| Joiner arrives during a battle | The replay crossed the battle start; merged and took P3 mid-battle; identical in all 129 snapshots. |
| Joiner from its own game | Same as from the title screen: 1,491 frames in 3.3 s, identical. |
| Divergence injected during the replay (`--sosonline-hpdiverge`) | Found at the next two host snapshots (f630, f660), mid-replay; fell back to Stage 2 and joined at f2060; identical afterwards. |
| Second joiner in the same area (`tests/dropin-two.sh`) | Replayed the first joiner's merge from the log; both identical in all 169 snapshots. Also with the first joiner gone before the second arrived (join and leave replayed), and with both joining at once (merge applied live mid-replay). |

Not done yet:
- Real Steam P2P (sending the log in 600-frame blocks is untested over the relay).
- Physics steps: the game's fixed step is 0.02 s against 1/60 s frames, so one frame in six has no physics step, and which one depends on when each PC's clock started. Identical in every run measured; not forced.

## Independent pause and menus (tier 1 done)

**Today.** A pause or menu opens in the shared game, stops it for everyone, and only its opener can move in it. A banner now says who and which menu.

**Asked for.** Each player pauses or browses menus on their own while the world keeps running.

**Constraint.** Lockstep needs every PC to run the identical simulation. A menu opened inside one PC's copy of the game changes that copy: the game pauses, the UI player changes, and the menu's own state moves. So that PC would diverge. The game's own menus therefore can't open on one PC only. An independent menu has to be drawn by the mod, outside the simulation, while that player's character gets neutral input (or the party AI) and the world runs on.

| Tier | What | Effort |
|---|---|---|
| 1 | Local pause screen: Resume, Options (audio, video and controls are per-PC settings), Online (leave). The opener's pause and game-menu presses are kept out of the synced input. Also fixes the guest Options desync (findings §7). | Moderate |
| 2 | Read-only browsing: party, stats, equipment, inventory, quests, drawn by the mod from the game state. Reading changes nothing. | Large (own screens) |
| 3 | Changes from a local menu (equip, use an item, relics, party order, shop purchases): each becomes a deterministic command in the input stream, applied on the same frame on every PC through the game's own methods. Conflicts (two players using the last potion) are resolved by command order. | Hard |

**Must stay shared:** saving, return to title, Assign Players, dialogues, cutscenes and battle menus (battle commands are already turns of the shared game).

**Status:** tier 1 done (findings §8). Next: tier 2.

## Order of work

1. Stage 0.
2. Spike A (one day). Decides whether Stage 2 exists.
3. Stage 1. Independent of the spikes, so it can run alongside them.
4. Spike B, then spike C only if B passes. Decides whether Stage 3 exists.
5. Stage 2, then Stage 3, as the spikes allow.

## Not in scope

- **Host migration.** As in Larian's games, the host owns the save; the session ends when the host leaves.
- **Joining mid-battle or mid-cutscene.** Joiners are queued until the game allows saving. Stage 3 may lift this later, since a replaying joiner does not need a save at the join moment.
- **More than three players.** The game's local co-op limit.

## Risks

| Risk | Effect | Mitigation |
|---|---|---|
| A save does not reproduce the live state (spike B fails) | No Stage 3 | Stage 2 is the end state: a short fade for everyone |
| In-place load leaves stale managers or co-op state (spike A fails) | No Stage 2 | Keep the title-screen route, automated and hidden behind an overlay |
| Real Steam P2P has never run end to end | Everything | Test two real accounts before Stage 1 UI work |
| A richer state hash costs frame time | Stutter every 30 frames | Measured: 3.4 ms average, 25.6 ms worst, every 30 frames. Acceptable for now |
| Absolute game time differs between PCs (finding 8) | Small drift in battles; a replaying joiner can never match the host exactly | Done: `GameClock` hooks Unity's time getters (time, unscaled time, smoothDeltaTime, frameCount) and re-bases them on every load release |
| Physics step phase differs between PCs | A `FixedUpdate` on different frames on different PCs | Not seen yet. If it shows up: align the phase on a load release (one frame with an adjusted fixed step) |

## Appendix: spike instructions

Three short experiments that decide whether Stages 2 and 3 of this plan are possible. Each has a pass condition and a time box. Stop at the time box and record what blocked you.

| Spike | Question | Gates | Time box |
|---|---|---|---|
| A | Can a save be loaded from gameplay, without the title screen, under lockstep? | Stage 2 | 1 day |
| B | Does loading the area-entry autosave reproduce the live state at area entry? | Stage 3 | 2 days |
| C | How fast can the game replay recorded input? | Stage 3 | ½ day |

Run B only after the prerequisite below. Run C only if B passes.

### Setup

- Game build 3.0.60151, mod built and deployed (`tools\deploy.ps1`).
- `steam_appid.txt` containing `1244090` in the game folder.
- Wrap every run: `tests/display-prefs.sh save` before, `tests/display-prefs.sh restore` after.
- Outputs land in `<game>\BepInEx\SosOnline\`: `log-<port>.txt`, `state-<port>.log`, screenshots.
- Test instances play from sandbox copies of the saves. Delete `SosOnlineTest-*` and `SosOnlineSession-*` in the save folder afterwards.

#### Finding game calls

The interop assemblies list every game class. To print a class's members:

```powershell
$g='W:\SteamLibrary\steamapps\common\Sea of Stars\BepInEx'
[AppDomain]::CurrentDomain.add_AssemblyResolve({param($s,$e) $n=($e.Name -split ',')[0]; foreach($d in "$g\interop","$g\core"){ $p=Join-Path $d "$n.dll"; if(Test-Path $p){ return [Reflection.Assembly]::LoadFrom($p) } }; $null})
$a=[Reflection.Assembly]::LoadFrom("$g\interop\Assembly-CSharp.dll")
try{$t=$a.GetTypes()}catch{$t=$_.Exception.InnerException.Types | ? {$_}}
($t | ? Name -eq 'SaveManager').GetMembers('Public,Instance,Static,DeclaredOnly') | % Name | sort -Unique
```

To see what a game method calls, add it to `Targets` in `SosOnline/XrefProbe.cs` and start the game with `--sosonline-xref`. The call list is written to the log and the game quits.

Calls confirmed to exist in this build (signatures only; behaviour is what the spikes test):

| Class | Members |
|---|---|
| `SaveManager` | `AutoSave()`, `LoadSaveSlot(SaveGameSlot)`, `LoadSaveSlotAndSetLastIndex(SaveGameSlot)`, `GetAutoSaveSlot(int)`, `CreateTemporarySaveSlot()`, `UnloadSaveSlotData()`, `saving`, `CanSave()` |
| `SaveGameSlot` | `checkpointData`, `lastUsedEntranceId`, `PartyData`, `InventorySaveData`, `currencySaveData`, `characterStatsSaveData` |
| `CheckpointData` | `level`, `position`, `playerOrientation`, `savepointId` |
| `LevelManager` | `LoadLevel(LevelLoading)`, `GetInitialLevelInfo()`, `UpdateCheckpointData(ESavepointId, Vector3, int)`, `LoadingLevel`, `onLevelLoaded`, `EndCurrentLevelLoading()` |
| `TitleScreen` | `OnContinue()`, `WaitAndContinueGame()` |
| `SinglePlayerPlusPlayer` | `Join()`, `Leave()`, `BeginPlaying()`, `playing` |
| `SinglePlayerPlusManager` | `RemoveAllActivePlayers()`, `AssignCharactersToPlayers(bool, bool)`, `SnapCharactersOnCharacter(...)` |

### Prerequisite — a state log that can see divergence

The current snapshot compares scenes and party positions only; its RNG probe always matches (see finding 1 in the plan). The spikes need more.

1. In `Lockstep/StateSync.cs`, extend `StateSnapshot` with separately logged components:
   - level id (`GameBridge.CurrentLevelId`),
   - per party member: HP, MP, position, facing,
   - gold and an inventory item count,
   - whether an encounter is running, and each combat actor's HP,
   - every enemy or NPC in the level that moves: name and position (sorted by name).
2. Remove `RngProbe`.
3. Make `ToString()` print one `key=value` group per component, so `diff` shows which one differs.
4. Add a harness flag `--sosonline-stateevery=N` (default 30) that sets the snapshot interval. Spike B needs `1`.

**Check.** Run `tests\two-instance.cmd` with the walk scripts. The two `state-*.log` files must still match. Then add a throwaway flag that changes one PC's party HP on a given frame, and confirm the next snapshot differs in the HP component only.

### Spike A — reload in place

**Question.** While lockstep runs, can every PC load an autosave on an agreed frame and resume, without the title screen?

#### Steps

1. **Learn the load path.** Add to `XrefProbe.Targets` and run `--sosonline-xref`:
   - `TitleScreen.OnContinue`, `TitleScreen.WaitAndContinueGame`
   - `LevelManager.GetInitialLevelInfo`, `LevelManager.LoadLevel`
   - `SaveManager.LoadSaveSlot`, `SaveManager.UnloadSaveSlotData`

   Write down the sequence the title screen uses to go from a loaded save slot to a `LevelLoading`.
2. **Add `GameBridge.ReloadInPlace(int slotIndex)`.** Reproduce that sequence from gameplay: load the autosave slot, build the same `LevelLoading`, call `LevelManager.LoadLevel`. Keep `showTransition = true` so the game's own fade covers it.
3. **Add a harness flag `--sosonline-reload=F`** in `TestHarness.OnFrame`, on every PC:
   - frame F: `GameBridge.AutoSave()`. Each PC saves its own copy; guests write to their mirror folder.
   - frame F+120: log an error if `GameBridge.Saving` is still true, then call `ReloadInPlace`. Use a fixed frame, not "when saving finishes": completion time differs between PCs.
   - Lockstep keeps running throughout. Do not stop the engine.
4. **Re-join the guest.** After the load, the guest's co-op player is probably gone. Re-arm the auto-join (`Session._autoJoin`), or call `SinglePlayerPlusPlayer.Join()` on a fixed frame on every PC.
5. **Log** on every PC: the frame `LoadingLevel` becomes true, the frame it becomes false, and the frame the guest's `playing` becomes true.

#### Run

```bash
tests/display-prefs.sh save
tests/session.sh 27100 "--sosonline-start=0 --sosonline-guests=1 --sosonline-delay=3 --sosonline-script=tests/scripts/host-walk.txt --sosonline-reload=1300 --sosonline-shots=1290,1500,2300 --sosonline-quit=2400" "--sosonline-delay=3 --sosonline-script=tests/scripts/guest-join-walk.txt --sosonline-reload=1300 --sosonline-shots=1290,1500,2300 --sosonline-quit=2400"
```

Script paths are relative to the game folder; copy the scripts there or pass absolute paths. Frame 1300 is after the guest has joined (frame 1000) and walked. `--sosonline-reload` goes to **every** instance: each PC saves and reloads itself. Add `--sosonline-reloadpos` to both for the position-restore variant.

Compare:

```bash
diff <(cut -d' ' -f2- "BepInEx/SosOnline/state-27100.log") <(cut -d' ' -f2- "BepInEx/SosOnline/state-27101.log")
```

#### Pass

- No title screen in any screenshot.
- State logs identical on both PCs from the first snapshot after the reload to the end.
- The guest controls a character again without manual input.
- From frame F+120 to the party being controllable: under 5 s (300 frames).

#### Also record

- The gap, in frames, between the two PCs' "loading finished" frames. This sizes the loading barrier in Stage 0.
- Where the party reappears: the area entrance, the last save point, or where it stood.

#### Then, if it passed — position restore

Before the autosave at frame F, try `LevelManager.UpdateCheckpointData(savepointId, position, orientation)` with the leader's current position, and check whether the reload spawns the party there. If not, record the party's positions at frame F and move the party to them on a fixed frame after the load, on every PC.

Pass: the party is within 0.1 m of its pre-reload positions and the logs still match.

#### If it fails

Record which call failed or what state was left behind (stale co-op players, missing camera, input disabled). Stage 2 then falls back to the automated title-screen route.

### Spike B — does a save reproduce the live state?

**Question.** Take PC "A", which walked into an area. Take PC "B", which loaded the autosave A made on arriving there. Fed the same inputs from the moment each finished loading, do they stay identical?

If yes, a joiner can catch up by replay and nobody else has to reload.

#### What to build

1. **Input recording.** Harness flag `--sosonline-record=FILE`. After each frame is applied, append the frame number and `InputOverride.Current` for the slot 0 and system channels (`InputSnapshot.Write`).
2. **Input replay.** Harness flag `--sosonline-replay=FILE` with `--sosonline-replayfrom=N`. In `TestHarness.Filter`, replace the local and system snapshots for target frame *f* with the recorded snapshots of frame *f − offset*, where the offset aligns the two "loading finished" frames (see below). Solo lockstep (`--sosonline-guests=0`) has only those two channels, so the filter is enough.
3. **Area-entry markers.** Log the first frame `LoadingLevel` is false after the level id changed (call it **L**), and the first later frame `GameBridge.CanSaveNow` is true (**C**).
4. **Autosave at C.** Harness flag `--sosonline-saveatentry`: on frame C call `GameBridge.AutoSave()`, and when saving ends copy the autosave file to `BepInEx\SosOnline\spike-b-autosave.sos`.

#### Prepare

- A save standing a few steps from an area transition, with a visible patrolling enemy on the far side. Put it in slot 0.
- An input script that walks through the transition, **stands still until after frame C** (so nothing persistent changes between L and C), then walks around for about 50 s, including into the enemy to start a battle. Add `--sosonline-bot` to play the battle.

#### Run A (records)

```bash
tests/session.sh 27100 "--sosonline-start=0 --sosonline-guests=0 --sosonline-delay=3 --sosonline-stateevery=1 --sosonline-script=<walk script> --sosonline-bot --sosonline-saveatentry --sosonline-record=BepInEx/SosOnline/spike-b-inputs.bin --sosonline-quit=<L + 3600>"
```

Keep `state-27100.log` as `state-A.log`. Note **L_A** from the log.

#### Run B (replays)

Copy `spike-b-autosave.sos` over the slot 0 autosave in a fresh sandbox, then:

```bash
tests/session.sh 27200 "--sosonline-start=0a --sosonline-guests=0 --sosonline-delay=3 --sosonline-stateevery=1 --sosonline-replay=BepInEx/SosOnline/spike-b-inputs.bin --sosonline-replayfrom=<L_A> --sosonline-quit=<L_B + 3600>"
```

**L_B** is B's first frame with `LoadingLevel` false after loading the save. The replay maps A's frame L_A + k to B's frame L_B + k.

The sandbox is rebuilt from your real saves at each launch (`SaveMirror.UseSandbox`), so for this run add a harness flag that copies a given file into the sandbox after it is created.

#### Compare

Shift frame numbers so L_A and L_B are both 0, drop the hash column, and diff the two state logs.

#### Pass

Every component identical from frame L+60 at the latest, for 3,000 frames, including the battle.

#### Partial results and what they mean

| What differs | Meaning |
|---|---|
| Party position or facing in the first frames only | Patchable. Record the party's positions at L on A, apply them on B at L, and rerun. |
| Enemy or NPC positions | A arrived through a door and B spawned from a save; if their patrols started at different times, this is not patchable by a simple transfer. Try aligning on the frame the level's objects activate instead of `LoadingLevel`. If still different: fail. |
| HP, inventory, gold | The save was taken after something changed. Check the script stood still until C, then rerun. |
| Battle outcomes only | Timing-sensitive state outside the hash. Add `--sosonline-trace` and compare the combat timing lines. |

#### If it fails

Stage 3 is dropped. Record the first differing component and frame. Stage 2 becomes the end state.

### Spike C — replay speed

Run only if B passed.

**Question.** How many simulated frames per second can a PC replay?

#### Steps

1. Add `--sosonline-ff` to the replay mode: set `QualitySettings.vSyncCount = 0` and `Application.targetFrameRate = -1`, and leave `Time.captureDeltaTime` at 1/60. The simulation then advances one fixed step per rendered frame, as fast as the PC can go.
2. Mute audio while fast-forwarding.
3. Second variant: also disable the level cameras while fast-forwarding, and re-enable them at the end.
4. Log simulated frames per wall-clock second every 5 s, tagged with free roaming, battle, or loading.
5. Rerun spike B's run B with `--sosonline-ff` and confirm the state log still matches A's.

#### Pass

- State log still identical to A's.
- At least 5× real time while roaming (300 simulated frames per second).

#### Record

- Speed in each of the three situations, with and without cameras.
- The resulting rule for Stage 3: catch-up time = frames since area entry ÷ speed. Propose the cut-off above which the mod uses Stage 2 instead (starting point: 60 s).

### Reporting

For each spike, add a short section to this file:

- pass / partial / fail,
- the numbers asked for under *Record*,
- the log lines or diff that show it,
- anything the next stage must handle.

Keep the harness flags you added; later stages reuse them as regression tests.
