# CLAUDE.md

Guidance for Claude Code (and any developer) working in this repository. The README is for players; everything technical lives here.

**Sea of Stars Online Co-op** is a BepInEx 6 (IL2CPP) plugin for **Sea of Stars: Sunset Edition** (Steam, Windows) that turns the game's local co-op (up to three players) into online co-op between Steam friends, by deterministic lockstep over the game's own input. The game ships Steamworks (`steam_api64.dll`, initialized by the game through Facepunch.Steamworks); the mod calls Steam's flat API directly.

## Start here

1. **Read [STATUS.md](STATUS.md) first.** It holds the current state (branch, latest release, what is installed on the user's PC, what is committed/pushed/published), the open items by priority, and what has and hasn't been verified.
2. Then, as needed:
   - [docs/FINDINGS.md](docs/FINDINGS.md): causes and fixes found so far, in detail (numbered sections; other docs cite them as "findings §N"). New findings get the next number.
   - [docs/PLAN.md](docs/PLAN.md): the drop-in / drop-out plan, its stages, progress and risks, with the spike instructions as an appendix.
   - [docs/messages.csv](docs/messages.csv): every player-facing string, with where it shows, its length and its source line.
3. Before ending a session with meaningful changes, update STATUS.md (state, what changed, open items). Don't add handoff files to the repo; a handoff for a fresh chat goes through the `handoff` skill (see *Long sessions*).

Determinism is the whole game: anything a PC reads that isn't synced input or synced state (clocks, hardware, focus, random, async I/O, UI timing) eventually splits the PCs.

## Working rules

- **Commits:** only when asked, straight to `main` (`tools/publish-release.ps1` needs a clean `main`).
- **Releases:** only on an explicit go-ahead. The friend's game installs every release by itself.
- **Never touch the game folder while the user is playing.** Check that `SeaOfStars` isn't running before building, deploying or testing: `dotnet build` deploys into the game folder by default (pass `-p:DeployToGame=false` for a compile check).
- **Never touch the user's real saves.** Tests play from sandbox copies (`SosOnlineTest-*`). Delete `SosOnlineTest-*` and `SosOnlineSession-<port>` in `%USERPROFILE%\AppData\LocalLow\Sabotage Studio\Sea of Stars` afterwards; never `SosOnlineSession` or `SosOnlineBackup`.
- **Before a release:** a full sweep (`tests/sweep.sh`, about 50 minutes). If a sweep already ran that day and only a small fix landed since, test what the fix touches.
- **Player-facing text:** short. Toasts at most 80 characters, popup choices at most about 25. A one-sentence toast has no final period ("Robin joined"); periods only between two or more sentences; "!" stays. Add or change strings in `docs/messages.csv` first; the user reviews wording there.
- **Keep the game's own behaviour** where it has one (two players may pick the same character; save slots keep "Slot N").
- **Guard user-supplied text** (player names) against length and special characters.
- **Public repo** (`miguelguzmanr/sosonline`): keep Steam names out of commits, docs and screenshots (they appear in logs).

## Long sessions

A Stop hook (`.claude/hooks/context-handoff.js`, set up in `.claude/settings.json`) checks the chat's size at the end of every turn. From about 375K tokens, approaching the 400K limit, it asks Claude to finish the current step, update STATUS.md, call the `handoff` skill (`.claude/skills/handoff`, from mattpocock/skills), and post a short message starting with "Handoff from the previous chat". The handoff document goes to `%TEMP%`, not the repo. It asks again every further 25K tokens until that message is posted. The user can also type `/handoff` at any time.

## Layout

```
SosOnline/          plugin source (net6.0, references the game's BepInEx interop assemblies)
  Game/             game wrappers, save mirroring
  Lockstep/         input capture/override, lockstep engine, state sync
  Net/              session, protocol, transports (Steam, UDP)
  Steam/            steam_api64 flat-API bindings, invites
  UI/               native menus, localization bridge, toasts, debug panel
  Update/           manual updates from signed GitHub releases
tests/              two-instance test scripts and input scripts
tools/install-to-game.ps1   build and install into your own game folder (installs BepInEx first if missing)
tools/make-release-zip.ps1  build the signed zip players install, in dist/ (nothing uploaded)
tools/publish-release.ps1   push main and create the GitHub release (runs make-release-zip.ps1)
tools/new-signing-key.ps1   create the release signing key (once)
```

## How it works

**Deterministic lockstep over the game's own input.**
- Every peer runs the full game from the same save.
- Each frame, every peer sends its controller input to the others.
- Every peer feeds all players' inputs into the game at the same frame, with a fixed 1/60 s time step.
- Same code, same input, same time step: everyone runs the same simulation. Local co-op does the rest. A remote player is, to the game, simply local player 2 or 3.

| Piece | What it does | Code |
|---|---|---|
| Input override | Rewired's `Player.GetButton*/GetAxis*` (all 25 getters) answer from the synced frame. Every game system (movement, menus, combat, the co-op Join and Leave) reads input through them. | `Lockstep/InputOverride.cs` |
| Input capture | Reads the local hardware through the original getters: 13 flags (down, up, repeat, short or long press, …) and axis values for each of the game's 59 actions. | `Lockstep/InputCapture.cs` |
| Lockstep engine | Runs in Rewired's update. Captures input for frame N + delay and sends it. Waits for every player's input for frame N, applies it, and reseeds `UnityEngine.Random` from (session seed, frame). | `Lockstep/LockstepEngine.cs` |
| Shared game clock | Unity's time getters (`time`, unscaled time, `smoothDeltaTime`, `frameCount`) are hooked at the native level and read the same on every PC: local time plus an offset re-based on every level load's release. Each PC's own clock made 0.1 s timers end a frame apart (float rounding at a different magnitude), which drifted every battle. | `Lockstep/GameClock.cs` |
| Device routing | During a session your pads and keyboard drive your slot's player. The game's automatic controller (re)assignment and its pause on focus loss or Steam overlay are suspended, because they would only happen on one PC. | `Patches/Patches.cs` |
| Save mirroring | The host sends the save the session plays from (that slot and the index) to the guests. Guests redirect the game's save I/O to `SosOnlineSession\`, so their own saves are never read or overwritten. Options stay each PC's own. | `Game/SaveMirror.cs` |
| Host fixes | Every 30 frames the host sends a snapshot: scenes, RNG, party positions. Guests compare it with their own state at that frame, correct small position drift in place, and report real divergence as a desync. | `Lockstep/StateSync.cs` |
| Session and transport | Friends-only Steam lobby, Steam Networking Messages (relayed by Valve, NAT handled), star topology through the host, handshake checks (mod protocol, game version, input configuration). | `Net/Session.cs`, `Net/Transport.cs`, `Steam/SteamApi.cs` |
| Steam joins | `+connect_lobby` at launch, overlay invites and friends-list **Join Game** while running (observed from Facepunch's callback dispatcher). | `Steam/SteamInvites.cs` |

### Client-side timing for timed hits and blocks

**The problem.**
- Under lockstep, every press reaches the game exactly *delay* frames after the player made it. At 50–100 ms of ping that is about 3–8 frames.
- Sea of Stars' timing windows are about the same size, so judging presses on arrival would turn timing into a ping lottery. At 8 frames of delay a perfectly timed hit misses.

**The fix.**
- Every timing decision is judged against the moment the player actually pressed, relative to what they saw.
- With lockstep delay D, inputs up to D frames ahead of the current frame are already captured by every peer; they are what players pressed while seeing the current frame. So every peer can grade each press identically from its *origin* frame, with no extra round trip.
- The cost is a short wait on impact frames when a peer's input is still in flight.

| Mechanic | Decision point | How it is judged |
|---|---|---|
| **Timed attack** | `TimedAttackHandler.GetResult` at impact (asynchronous) | The call is deferred by D frames, and the game's after-hit window is stretched by D so late-arriving presses still count. The verdict is replaced with a grade from origin times using the game's own rules. Before impact: the latest press within `beforeWindow`. After impact: the first press within `afterWindow` of the impact. The game's frame quantization is reproduced (a press read in the frame that times out still counts). |
| **Timed block** | `PlayerCombatActor.GetBlockResult` at the enemy hit (synchronous) | Look ahead D frames for the player's presses. Success if the latest press made at or before the hit still had its block up (block duration), with the same frame rule. A press used retroactively is consumed so the block animation doesn't replay late. |
| **Time-to-event QTEs** | `TimeQTEHandler` | Windows are evaluated as of D frames ago. *Not exercised yet: no such QTE in the test battle.* |

**Verified** with timing bots that press relative to what they see on screen (`tests/ab-run.sh`):
- **Attacks:** the game's local rule is success for presses from 7 frames before to 13 frames after impact. At D = 8 with compensation, every edge case matched: −8 fail, −7/−3 before-success, +1/+6/+12/+13 after-success, +14 fail.
- **Blocks:** the local rule is 8 frames before to the hit. At D = 8: −9 fail, −8/−3/0 success, +1 fail.
- **Without compensation:** a press 6 frames after impact fails, a block 3 frames before the hit fails, and a too-early block 9 frames before the hit succeeds.
- **Online:** host and guest each made their own timed hits at D = 8. The timing verdicts were identical on both PCs and 87 of 87 state snapshots matched. Look-ahead waited 49–101 ms in total over the whole battle on loopback.

**Trade-offs:**
- A timed hit resolves up to D frames later than in local play: the damage number appears about 0.1 s later at D = 8.
- A missed one waits that much longer before the game moves on.
- Visual feedback follows the game's own check, so in rare edge cases the success sparkle can disagree with the graded result for one hit. The damage always follows the graded result.

### Drop-in / drop-out

**Drop-out.** A player can leave at any time; the others keep playing.

| How they leave | What happens |
|---|---|
| Pause screen → **Leave co-op**, or pause menu → **Online** → **Leave** | The game's pause menu is driven by synced input, so every PC runs the choice on the same frame. The player's inputs already sent are still used; the slot is empty from frame + delay + 1, on every PC. |
| Quitting, or the title-screen **Leave** | The host picks the leave frame: the first frame after the last input it received from them, which it has already forwarded to everyone. It broadcasts that frame. |
| Crash or lost connection | The host notices at once when Steam reports the connection lost, or after 2 s without input from that player, then does the same as above. |

- On the leave frame, every PC calls the game's own `SinglePlayerPlusPlayer.Leave()`, and their character goes back to the party AI.
- The leaver keeps playing solo on their copy of the host's save.
- Only the host ending co-op ends it for everyone. The host then leaves the Steam lobby 1.5 s after the end frame (guests stop on that frame first) and turns **Open to friends** off, so a friend who was still joining can't start a new session.
- **Back to solo.** Whenever lockstep stops on a PC (a leave, the end, a lost connection), that PC moves its keyboard and pads to the game's P1, who controls the leader, and makes every other co-op player leave, so the party AI takes their character. If the game refuses a leave (a battle, a cutscene) it retries every second. A guest that is re-syncing is left alone: it drops in again.

**Drop-in.** Lockstep syncs inputs, not world state, so a newcomer can't be streamed into a running game. It rebuilds the game instead, by replay if it can, by a short reload for everyone if not.

*Catching up (only the joiner waits).*
- On every level load's last frame (the area entry), every PC autosaves. The host keeps a copy of that save, the inputs of every frame since, and its state snapshots (`Net/AreaLog.cs`).
- A joiner gets that save, loads it, and replays the inputs since the area entry with rendering and sound off (about 800 frames per second on loopback), checking the host's snapshots as it goes. Then it follows the live inputs.
- Once it is level with the session, the host picks a merge frame: every PC adds the player there and the joiner takes a party member. The players already in never stop.
- If a player joined or left since the area entry, the replay would be over 15 minutes, it disagrees with a host snapshot, or it takes over 2 minutes, the joiner drops in by a reload instead (below).

*Reload in place (fallback).* Everyone reloads a fresh autosave in place, behind the game's own fade, without leaving gameplay:
- A friend who joins mid-session is admitted as *waiting*. Once the game itself has allowed saving for half a second (no battle, cutscene or loading), every PC in the session autosaves on an agreed frame. Lockstep keeps running.
- The host sends its saves to the joiner, who loads them into its mirror folder wherever it is (title screen or its own game, once the game has finished booting) and reports ready. If that took more than 5 s, the host saves again first, so little progress is undone.
- The host picks a join frame a few frames ahead and sends it, with where every party member stands, to everyone. The joiner starts lockstep on that frame with the inputs the host has buffered for it.
- On the join frame every PC, the joiner included, loads the autosave in place (`GameBridge.ReloadInPlace`): the leader spawns where it stood, the loading barrier ends the load on one frame everywhere, and two frames later every party member is put back on its own position.
- Co-op players aren't in the save, so on the join frame every PC lets the guests leave co-op, and each guest takes a party member again automatically: the mod presses the game's co-op **Join** for them, through synced input. The same happens after a session start.
- Progress made between the autosave and the join frame (usually 2 s) is undone, except positions.
- If a level load or battle is running on the join frame, every PC sees it on that frame and the join is postponed: the drop-in starts over with a fresh autosave once it is over.

**Assign Players.** The game's own screen, made session-aware.
- Offline it is device-driven. A gamepad joins by pressing a button (the screen polls every *local* device), and finishing moves devices between the game's players P1–P3.
- Online:
  - The screen is rebuilt identically on every PC (the players currently playing, plus whoever opened it).
  - Players join and confirm with their synced input. Keyboard players work too, through fallbacks from the regular UI actions.
  - The game's device shuffling is ignored during sessions.
  - On submit, each session slot is remapped to the player number its cursor chose, on every PC. Cursor, ready and the game's character assignment already run on synced input.
  - Every online player stands in with the keyboard icon. Each icon shows the player's name under it and is tinted in their colour (P1 cyan, P2 amber, P3 pink), so a player's keyboard reads as theirs even in another player's column. Two players can pick the same character, as in the game; their icons stack in that column.

**Verified** (loopback, 2–3 instances):

| Scenario | Result |
|---|---|
| Guest leaves | Applied on frame 905; the host keeps playing alone. |
| Guest process killed | The host drops the slot after 2 s and keeps playing. |
| Synced pause-menu leave | Applied on frame 905 on both PCs. |
| Third player joins mid-session (drop-in in place) | The joiner is booting when it joins; the first autosave goes stale and is replaced. Existing players: 92–95 frames (about 1.6 s) from the join frame to the load's release, never via the title screen; both guests play again 5 frames later and the joiner walks its character. After the join, the joiner's state matches the host's exactly (73–81 of 73–81 snapshots, four runs); guest 1 differs only by up to 1 cm in positions, within the 2 cm correction, with no mismatch reported. |
| Assign Players | Opened online; P2's cursor took the P1 column. After confirm, on both PCs the guest drives P1/Zale and the host P2/Valere. The screen closes through the game's own path; all 57 snapshots match. |

### Native-code safety

IL2CPP folds identical native functions (empty virtuals, trivial getters and setters, shared generic code) into one. Detouring such a function also hooks every method sharing it, with unrelated `this` objects. Two defenses (`NativeCode.cs`):
- **Audit at load.** The plugin enumerates every method of every class in the IL2CPP runtime (80,235 native functions) and removes any of its own patches that land on shared code. The current build audits 103 patched methods with 0 shared.
  - Early on, this caught three such patches: `SteamPlatform.IsInFocus` was shared with Rewired joystick-map getters. All three were replaced.
- **Runtime class guard.** Every instance patch first checks the real IL2CPP class of `this`. This also covers sharing with generic instantiations, which metadata can't enumerate. For example, `InputManager.SetControllerAssignmentMode` turned out to share code with `ObjectPool<T>` methods.

### Native menus

The menus use the game's own widgets, so they look, sound and navigate like the rest of the UI. This follows the sh2-online approach.

- **Title screen:**
  - An **Online** entry, copied from the Options button.
  - Choosing it swaps the title list for the co-op entries, with a status line under them.
  - Entries: **Host**, **Join** (friends who are hosting), **Load Game** (pick the save), **Invite Friends** (Steam overlay), **Stop Hosting** / **Leave Co-op**, **Options**, **Back**. **Options** holds **Input Delay**, **Open to Friends**, **Check for Updates**, **Send Logs to Host** / **Ask for Logs** and **Open Logs Folder**, so the list and its status lines stay above the game's copyright row (the status is hidden on a page long enough to reach it). Entries are in Title Case. B puts the cursor on **Back**; B on **Back** goes back, and the cursor lands on the entry that opened the page. Once an update is downloaded, **Restart to Update (0.45 -> 0.46)** comes first.
- **Pause menu:**
  - An **Online** item, added with the game's own `PauseMenu.AddPauseMenuItem`.
  - It opens the game's standard two-choice popup.
  - During a session the pause menu is driven by synced input, so its choices act as deterministic commands. *End co-op* stops co-op on every PC on the same frame, and everyone keeps playing solo from an identical state. Its choices may depend only on synced state, so **Restart** (which depends on this PC's download) is offered there only outside a running session.
  - The game's popup buttons are sized for "Yes"/"No"; the mod widens each to its label, up to 200 reference pixels (about 25 characters), and cuts a longer one with an ellipsis.
- **Text:** the mod's strings go through the game's localization (`LocalizationManager.GetText` patched for a private `SosOnline` category).
  - Every player-facing string is listed in [docs/messages.csv](docs/messages.csv): where it shows, its length and its source line. Wording changes are reviewed there first.
  - Toasts (the notices at the top) hold at most 80 characters: one short sentence about what happened, at most what to do. Longer text is cut at a word and logged as a warning. They stay 2.5 s + 0.06 s per character (3.5–12 s).
  - Player names are cleaned where they enter the mod (`UI/PlayerNames.cs`): no control, zero-width or line-break characters, `<` and `>` replaced so a name can't inject text markup, at most 24 characters, and "Player N" when the game's fonts can't draw most of it.
- **F8** opens a small debug panel, and a one-line HUD shows players, ping and sync state during a session.

## Build and install

**Game folder.** Every tool, test script and the build find Sea of Stars through `tools/game-dir.ps1`: `-GameDir`, else the `SOS_GAME_DIR` environment variable, else `game-dir.txt` in the repository root, else Steam's library folders. The folder it finds is written to `game-dir.txt`, which is gitignored: never put a PC's own path in a committed file. Test scripts get it as `$GAME` from `tests/game-dir.sh` (the `.cmd` scripts call the resolver directly).

Requirements: Sea of Stars on Steam (Windows) and the .NET 6+ SDK.

```powershell
powershell -ExecutionPolicy Bypass -File tools\install-to-game.ps1
```

- The first run installs BepInEx 6 IL2CPP (it asks first). Start the game once so BepInEx can generate its interop assemblies, quit, then run the script again to build and install the plugin.
- To pack a zip without releasing it (needs PowerShell 7):

```powershell
pwsh -File tools\make-release-zip.ps1
```

  It writes `dist\SosOnline-<version>.zip`, signed when the release key exists.

## Updates (manual)

Updates are manual since 2026-11-17 (`Update/AutoUpdate.cs`); before, the mod downloaded and installed every newer release at start.

- **At start** the mod only looks up the [latest release](https://github.com/miguelguzmanr/sosonline/releases/latest) (one request to `api.github.com`; not in test runs). If it's newer, `AutoUpdate.Available` is set.
- **Title screen version line** (`UI/VersionLabel.cs`): *Online Co-op Version 0.56*, plus *(Update available)*, bottom-left. It's a copy of the game's own version text (`VersionInfo`), moved to the root canvas (the game's line sits in a layout group) and mirrored: as far from the left edge as the game's drawn text is from the right edge, same bottom.
- **Versions as players see them:** `AutoUpdate.Display` shows 0.1.56 as "0.56" (releases are always 0.1.&lt;commit count&gt;). Logs and assembly versions keep the full 0.1.56.
- **Check for Updates** (title screen → Online → Options) looks up the release and asks: *Up to date (0.56).*, or *Update 0.57 is available.* → Install / Back. It downloads the zip and installs the DLL only if its signature matches the release key built into the mod (the running DLL is renamed to `.old`; nothing downloaded is loaded in the running game). On a locally modified build (`…-modified`) it offers the published release even when that isn't newer.
- **Restart to update (0.56 -> 0.57)** then appears in the Online menu (and **Restart** in the pause menu's Online popup, outside a running session). It quits the game; a hidden PowerShell waits for it to exit and starts it again through Steam (`steam://rungameid/1244090`).
- If someone joins with a different version, the host refuses them and names both versions.
- The old `AutoUpdate` setting in `BepInEx\config\sosonline.coop.cfg` is no longer read.

## Releasing

Needs PowerShell 7, the GitHub CLI (`gh`, logged in), and the release signing key.

1. Once: `pwsh -File tools\new-signing-key.ps1` creates the key in `%APPDATA%\SosOnline\release-signing-key.txt` and prints the public key, which goes in `SosOnline\Update\AutoUpdate.cs`. **Back up the private key and never commit it.** Without it, players need one manual install of a build signed with a new key.
2. For each release, commit everything on `main`, then run:

```powershell
pwsh -File tools\publish-release.ps1
```

   It pushes `main`, builds and signs `dist\SosOnline-0.1.<count>+<commit>.zip`, and creates release `v0.1.<count>`. The count is the number of commits, so versions always increase. Players see *(Update available)* at their next game start and install it from the Online menu.

## Testing

`tests\` runs two game instances on one PC over loopback UDP (no Steam networking), with scripted input, screenshots and per-frame state logs in `BepInEx\SosOnline\`.

| Script | What it checks |
|---|---|
| `tests\solo.cmd` | Hosts with no guests, starts from save 0, screenshots, quits. |
| `tests\two-instance.cmd` | Host (UDP 27100) and guest (27101): handshake, save mirroring, synced start. Set `SCRIPT_HOST`/`SCRIPT_GUEST` to input scripts in `tests\scripts\`. |
| `tests\uitest.cmd` | Screenshots of the title screen Online menu, hosts a real Steam lobby, leaves. |
| `tests/ab-run.sh` | Runs solo battle instances in parallel (each `"port\|flags"`), e.g. timing bots with and without `--sosonline-nocomp`. |
| `tests/battle-map.sh`, `tests/map-results.sh` | Maps the game's timed-hit and block grading: one run per press offset. |
| `tests/battle-live.sh` | Two instances, a battle on both from frame 1500: prints the guest's corrections and mismatches (finding 8). |
| `tests/dropin.sh` | Three instances: a third player joins 30 s in (`JOIN_DELAY`). `HOSTFLAGS`/`JOINFLAGS` add flags, e.g. `--sosonline-nocatchup` (reload in place), `--sosonline-loadatjoin`, `--sosonline-play=0` (join from its own game). Compares both guests' states with the host's. |
| `tests/dropin-two.sh` | Host alone, then two joiners in one area; both catch up from the same area entry. |
| `tests/stage1.sh` | The host plays its own save with the lobby open; guest A joins, leaves from the pause screen (co-op to solo), guest B joins later. |
| `tests/menu-status.sh` | Pause screen and menu banner while a third player catches up. |
| `tests/battle-turns.sh` | A battle where both players take turns over a simulated lagged link (`LAG`, `JITTER`, `SPIKES`); prints both PCs' turn decisions and the first differing snapshot. Run it 3–4 times: battle splits are intermittent. |
| `tests/save-sync.sh` | A manual save (`SAVER=host` or `guest`) over a lagged link; both PCs must open and close the question on the same frames. |
| `tests/resync.sh` | A forced divergence (`DIVERGE`): the guest rejoins by replay (or a reload); compares it with the host afterwards. `EXTRA=--sosonline-hpdivergeagain` makes the replay diverge too (the reload fallback). |
| `tests/sweep.sh` | Every test above that checks the session, one after another (about 50 minutes), plus leaving, ending, Assign Players, camping and the title-screen menus: the check before a release. Results and logs per test in `BepInEx/SosOnline/sweep`. |
| `tests/spike-b.sh` | Records a run, replays it from the area entry autosave, compares every frame. |

Battle flags: `--sosonline-encounter=F` starts the nearest encounter on lockstep frame F (every peer). `--sosonline-bot` confirms menus and presses timed hits `--sosonline-botoffset` frames after the impact it sees; `--sosonline-bot=menu` only confirms menus. `--sosonline-trace` logs the combat timing calls, and `--sosonline-nocomp` disables client-side timing.

Session flags:

| Flag | Effect |
|---|---|
| `--sosonline-leave=F` | Guest leaves over the network on frame F. |
| `--sosonline-kill=F` | Guest's process is killed on frame F. |
| `--sosonline-pauseleave=F` | Synced pause-menu leave of P2 on frame F. |
| `--sosonline-assigntest=F` | Opens Assign Players on frame F (from the game's pause menu, or directly when it isn't open; pass it to every instance). |
| `--sosonline-endsession=F` | The host ends co-op from its pause screen on frame F. |
| `--sosonline-pausechoose=F:Label,…` | Chooses a local pause screen entry by its label on frame F (`_` for a space), e.g. `1760:Leave_co-op,1800:Leave`. |
| `--sosonline-campat=F` | Uses the nearest campfire as if P1 pressed Interact (pass it to every instance). An early save only gets "The campfire feels cozy". |
| `--sosonline-campstart=F`, `--sosonline-campstop=F` | The campfire's own camping toggle, past the story unlock: intro, camp, and the Rest choice when stopped. |
| `--sosonline-fakeupdate=V` | Acts as if update V was downloaded (shows **Restart to update**). |
| `--sosonline-fakeavailable=V` | Acts as if the lookup at start found V (the version line says *(Update available)*). |
| `--sosonline-versionshot` | Title screen: screenshot `ui-version` with the version line, then quit. Never opens a save. |
| `--sosonline-name=TEXT` | A test player name; escapes like `\n` and `\u0020` for characters a command line can't carry. |

`tests/session.sh` launches a host plus guests, staggering late joiners, and tiles their windows side by side, host on the left (`tests/place-window.ps1`; `NO_TILE=1` turns it off). It doesn't start a late joiner once the host's game has quit.

Test instances never touch your real saves:
- Each UDP test instance plays from its own sandbox copy (`<saves>\SosOnlineTest-<port>`) and its own mirror folder.
- Delete those folders when done.

Unity stores the `-screen-*` test flags as the game's display preferences, so wrap test runs with `tests/display-prefs.sh save` and `tests/display-prefs.sh restore`.

These scripts need `steam_appid.txt` (containing `1244090`) in the game folder, so the exe can start directly with the test flags.

Compare runs with:

```bash
diff <(cut -d' ' -f2- "BepInEx/SosOnline/state-27100.log") <(cut -d' ' -f2- "BepInEx/SosOnline/state-27101.log")
```

The command-line flags are documented in `SosOnline/TestHarness.cs`.

### Practical notes

Close the game first; builds deploy into the game folder, and a build during a test replaces the mod under it. `steam_appid.txt` must be in the game folder. Window tiling (`session.sh`) works; leave it on.

| Command | Use |
|---|---|
| `bash tests/sweep.sh` | Everything, about 50 minutes; results in `BepInEx/SosOnline/sweep` |
| `ENCOUNTER=650 QUIT=5000 bash tests/battle-turns.sh --sosonline-start=0a` | Battle from the newest autosave over a lagged link. Pass: `guest mismatches 0`, identical turn lines. Run 3–4 times. Add `--sosonline-nolockfix` to reproduce the old Bosslug split |
| `QUIT=1500 bash tests/save-sync.sh --sosonline-saveat=600` | P1 saves; `SAVER=guest ... --sosonline-saveby=1` for P2 |
| `DIVERGE=1600 QUIT=9500 EXTRA="--sosonline-encounter=1500 --sosonline-bot --sosonline-lag=20 --sosonline-jitter=25" bash tests/resync.sh` | Forced re-sync mid-battle (rejoin by replay); add `--sosonline-hpdivergeagain` to `EXTRA` for the reload fallback |
| `tests\uitest.cmd --sosonline-fakeupdate=0.1.99` (cmd or PowerShell) | Title screen menus, *Online > Start*, the restart popup; screenshots `ui-*-steam.png` |

Notes:
- Battle bugs need `--sosonline-lag`/`--sosonline-jitter`: loopback hides real-time dependence.
- **Save slot numbers count from 0.** `--sosonline-start=SLOT[a]` and `--sosonline-play=SLOT` take the file number, not the number the game shows: the game's "Slot 1" is `0` (`SOS_SaveSlot0.sos`; with `a`, `Autosave_SOS_SaveSlot0.sos`), "Slot 2" is `1`, and so on. A slot with no file fails with `Save slot N (auto=…) not found` and the test starts nothing. When the user names a slot, subtract one, and check the file exists in `%USERPROFILE%\AppData\LocalLow\Sabotage Studio\Sea of Stars` first.
- Tests run from the user's newest save (`--sosonline-start=0a`, `--sosonline-play=0`), so what they reach changes when the user plays. STATUS.md notes where that save currently is (area, encounter frame, nearest campfire).
- Run hygiene: `tests/display-prefs.sh save`/`restore` around tests; delete `SosOnlineTest-*` and `SosOnlineSession-<port>` in `%USERPROFILE%\AppData\LocalLow\Sabotage Studio\Sea of Stars` afterwards, never `SosOnlineSession` or `SosOnlineBackup`.
- Choose local pause entries by label (`--sosonline-pausechoose=1760:Leave_co-op,1800:Leave`), not by cursor moves: the entry list changes.
- Message wording: `docs/messages.csv` lists every player-facing string; the user reviews it there. Keep toasts at most 80 characters and popup choices at most about 25.

## Investigation techniques that worked

- Interop assemblies (`BepInEx\interop\*.dll`) can be reflected from PowerShell (copy them to a scratch folder, `Assembly.LoadFrom` with an `AssemblyResolve` handler) to list types, methods and properties. No method bodies: behaviour has to be found by logging.
- `--sosonline-callprobe=TypeA,TypeB` logs the first call per frame of every method on those types; diffing two PCs' call sequences finds the first divergent call.
- `--sosonline-uiprobe=F` logs every visible screen's images and texts (sprites, fonts, colours): how the Assign Players colours and the game's save screen layout were found.
- `NativeCode.IsFolded`: never patch a method that shares native code with others; prefer postfixes on methods already patched.
- Il2CppInterop rejects `null` for object parameters of native calls (`Il2CppObjectBaseToPtrNotNull`).

## Player-facing behaviour, in full

The README has the short version for players; this is the complete one.

**Start from your own game (no title screen).**
1. **Host:** pause → **Online** → **Open**. A Steam lobby stays open while you play (also on the next start: it's a setting, toggled in the title screen's **Online** → **Options**). **Invite friends** opens the Steam overlay.
2. **Friend:** accept the invite, or use **Join Game** in the Steam friends list. From the title screen, **Online** → **Join a friend** works too. Accepting while in your own game asks once, then replaces your game with the host's.
3. The friend drops in at the next moment the game allows saving (no battle, cutscene or loading): either they catch up on their own PC while the host keeps playing, or everyone sees a short fade. Then they take a party member automatically.

**Or start together from the title screen:** Title screen → **Online** → **Host**, friends join, then **Load Game** and pick a save.

**During a session:**
- **Pause** (Start / Esc) opens a pause screen on your PC only; the game keeps running for everyone and the others see "P2 is paused". It has **Options** (volumes, brightness, display: this PC only), **Pause for everyone** and **Leave co-op** (the host: **End co-op** for everyone). In battles and cutscenes the game's own pause still stops everyone.
- **Pause for everyone** opens the game's own pause menu on every PC, as in local co-op: the game stops for everyone and only you move in it. Use it for **Assign Players**, **Return to title** and the like.
- **Choosing who plays whom:** pause → **Pause for everyone** → **Assign Players**; each player moves their own cursor. A friend who rejoins gets the character they had back, if it's free.
- **Leaving:** pause → **Leave co-op**. Your character goes back to the party AI and the others keep playing; your own game is yours again (your pad and keyboard drive the leader). When the last friend leaves, the host simply keeps playing; the lobby stays open.
- **Ending:** the host's pause → **End co-op**. Everyone keeps playing on their own; the lobby closes and **Open to friends** turns off.
- **Camping** works as in local co-op (tested on loopback: intro, camp, Rest and the end of camp on the same frames on both PCs).
- If a friend's game gets out of sync, it fixes itself: small differences (HP, MP, combo points, positions) are corrected in place; otherwise their PC rejoins by replaying the area behind a loading screen (a few seconds, even mid-battle), or, if that can't work, with a short reload once the game allows saving. Nobody else waits.
- **Check for Updates** (title screen, Online → Options): looks for a newer release now and asks before installing it; on a locally modified build it offers the published release instead, so the PC is back on a build everyone has. Then Restart (through Steam) or Later.
- **Return to area start** (host): while a friend waits to join where the game doesn't allow saving (a battle, a cutscene), the host's pause screen offers only this, Back and, outside battles, Pause for everyone (the game has no pause in battles). Everyone goes back to how things were on entering the area, and the friend joins right away; progress since then is lost.

Use a gamepad or keyboard; the mouse is not synchronized. Saves made during co-op are written by the host as usual. On guests they go to a separate session folder, so the guests' own saves are never touched and the host keeps the shared progress. Options stay each PC's own.

### Checklist for a session with a friend over Steam

Nothing Steam-specific has been tested end to end yet (see *Not yet verified* in STATUS.md). Worth checking, in this order:
1. The friend sees the invite and joins from the title screen; both PCs show the friend in the HUD line (F8 opens the debug panel).
2. The friend drops in and takes a party member; both walk around for a few minutes. The HUD should keep saying *synced*.
3. Pause on one PC: only that PC shows the pause screen, the other shows "P… is paused".
4. A battle with timed hits from both players.
5. The friend leaves and rejoins (gets the same character back); the host walks into a new area first so the rejoin catches up instead of reloading.
6. Accept an invite while in your own game (one confirmation, then the join).

## Logs

Every start of the game writes its own log, so nothing is lost when you restart.

**Friends: press a button.** While connected to the host's session (also after a crash: rejoin first), choose **Send logs to host**. It's on the pause screen (Esc, outside battles and cutscenes) and in the title screen's **Online** → **Options**. Your game zips your last two starts' logs and BepInEx's `LogOutput.log` (which also has the game's own messages), and sends them to the host only. If the game crashed since, the zip also includes the newest Windows crash dump of the game (`%LOCALAPPDATA%\CrashDumps\SeaOfStars.exe.*.dmp`, a snapshot of the game's memory at the crash). The host gets a toast and the zip in `BepInEx\SosOnline\logs\received`. The host can also choose **Ask for logs**, which shows friends a reminder; they still decide.

**By hand** (the host's own logs, or without a session), from the same session on **both PCs**:

1. Open the logs folder: press **F8** in game → **Open logs folder**, or title screen → **Online** → **Options** → **Open Logs Folder**. It is `BepInEx\SosOnline\logs` in the game folder.
2. Send the newest `sosonline-<date-time>.log` and `state-<date-time>.log` (the file names carry when the game was started). If you restarted the game since the problem, take the pair from that earlier start; the last 10 starts are kept.
3. Say roughly what happened and when (the log lines carry the time of day).
4. If the game crashed, also send `BepInEx\LogOutput.log` from the game folder (it has the game's own messages) and the newest `SeaOfStars.exe.*.dmp` from `%LOCALAPPDATA%\CrashDumps\` (Windows' crash dump of the game). Sea of Stars writes no `Player.log`.

What's in them: everything the mod logged (joins, drop-ins, desyncs, pauses, leaves, and `[coop]` lines saying who controls which character with which devices, written when that changes), the game's own errors, the mod build (the commit, e.g. `0.1.0+e3d3ca0`), the game version, Windows version and graphics card, and a session summary every minute (players, ping, input delay, sync checks). The state file has a snapshot of the game state every half second, so the host's and a guest's can be compared frame by frame. The logs contain the Steam names of the players in the session and Steam lobby ids (sometimes a player's Steam id); nothing else personal.

If the friend runs a different build than the host, the host gets a message when they join: use the same zip on both PCs.
