# Sea of Stars Online Co-op

[![Latest release](https://img.shields.io/github/v/release/miguelguzmanr/sosonline?label=release)](https://github.com/miguelguzmanr/sosonline/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/miguelguzmanr/sosonline/total)](https://github.com/miguelguzmanr/sosonline/releases)

Play **Sea of Stars: Sunset Edition** co-op online with your Steam friends. The game already has local co-op for up to three players; this mod lets players 2 and 3 join from their own PCs.

## Features

- **Up to three players** over Steam, using the game's own co-op. To the game, your friends are simply players 2 and 3.
- **Drop in, drop out.** Friends join a game in progress from a Steam invite or the friends list and take a party member; anyone can leave while the others keep playing.
- **Timed hits and blocks feel right online.** Each press is judged by when you made it, not when it reached the other PCs.
- **Native menus.** An **Online** entry on the title screen and pause menu, built from the game's own widgets.
- **Your saves stay yours.** Friends play on a copy of the host's save; their own saves are never touched.
- **Fixes itself.** If a friend's game gets out of sync, it corrects itself or rejoins in a few seconds.
- **Signed updates, on your terms.** The title screen shows the mod's version and says when an update is available; you choose when to install it.

## Screenshots

| | |
|---|---|
| ![The Online page on the title screen](docs/screenshots/online-menu.png) | ![Online options, with help text](docs/screenshots/online-options.png) |
| ![Assign Players, online](docs/screenshots/assign-players.png) | ![A battle with two players](docs/screenshots/battle.png) |
| ![Three players in one game](docs/screenshots/three-players.png) | ![Two players exploring, in sync](docs/screenshots/two-players.png) |

## Installation

> [!WARNING]
> The only official download is the [Releases page](https://github.com/miguelguzmanr/sosonline/releases/latest) of this repository.

Everyone in a session needs **the same version of the mod** and of the game. Install it once; after that, update from the game's **Online** menu.

You need Sea of Stars on Steam (Windows) and `SosOnline-<version>.zip` from the [latest release](https://github.com/miguelguzmanr/sosonline/releases/latest).

1. **Install BepInEx 6 (IL2CPP).** Download [BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.793](https://builds.bepinex.dev/projects/bepinex_be/793/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.793%2Bdbc82f0.zip) and extract it into the game folder, next to `SeaOfStars.exe`. To find the folder: Steam → right-click Sea of Stars → *Manage* → *Browse local files*.
2. **Start the game once from Steam** and wait for the title screen. The first start takes a minute or two longer while BepInEx prepares itself; a console window opens next to the game. Then quit.
3. **Extract the mod zip** into the same game folder. It adds `BepInEx\plugins\SosOnline\SosOnline.dll`.
4. Start the game from Steam. The title screen now has an **Online** entry.

<details>
<summary>Updates</summary>

The bottom-left corner of the title screen shows the mod's version, e.g. *Online Co-op Version 0.56*, and adds *(Update available)* when there's a newer release. Nothing is downloaded until you ask:

1. Title screen → **Online** → **Options** → **Check for Updates** → **Install**. The update is installed only if its signature matches.
2. **Restart** (through Steam), or **Later**: it runs from the next start.

At start the mod makes one request to GitHub to see whether there's a newer release; nothing personal is sent.

</details>

<details>
<summary>Updating by hand, uninstalling</summary>

- To update by hand, extract the new zip over the old one.
- To uninstall, delete `BepInEx\plugins\SosOnline`. To remove BepInEx too, delete `winhttp.dll` from the game folder.

</details>

## Playing

**Start from your own game.**
1. **Host:** pause → **Online** → **Open**, then **Invite friends**.
2. **Friend:** accept the invite, or use **Join Game** in the Steam friends list.
3. The friend drops in at the next moment the game allows saving (not in a battle, cutscene or loading), then takes a party member automatically.

**Or start together:** title screen → **Online** → **Host**; friends join, then **Load Game** and pick a save.

**During a session:**
- **Pause** (Start / Esc) pauses only your screen; the game keeps running for everyone. **Pause for everyone** opens the game's own pause menu on every PC, e.g. for **Assign Players** (choose who plays whom).
- **Leave co-op** (pause screen): your character goes back to the party AI and the others keep playing. The host's **End co-op** ends it for everyone.
- Use a gamepad or keyboard; the mouse is not synchronized.
- Saves made during co-op are the host's. Options (volume, display) stay each PC's own.

**Backups.** The mod keeps old versions of your saves, so a save that gets overwritten isn't lost. They're in your save folder (`%USERPROFILE%\AppData\LocalLow\Sabotage Studio\Sea of Stars`), under `SosOnlineBackup`:
- `Saves`: earlier versions of each of your save files (the last 50, plus one per day for 30 days).
- `Session saves`: the same for the host's saves your game played during co-op.
- `Sessions`: the saves as they were when co-op started, ended, or got out of sync.

To go back to one, quit the game and copy the file over the one in the save folder, renamed to its original name (for example `Autosave_SOS_SaveSlot0.sos`).

## Known limitations

- A friend joining mid-session waits for a moment the game allows saving; sometimes everyone sees a short fade and about 2 seconds of progress is undone.
- If the host leaves, the session ends.
- If one PC can't keep 60 fps, everyone slows down with it.
- Tested mostly on one PC with several game instances; real sessions over Steam are still being shaken out. Reports are welcome.

## Something went wrong?

**Friends:** choose **Send logs to host** (pause screen, or title screen → **Online** → **Options**). Your game sends your recent logs, a copy of the session's save, and a crash dump if the game crashed, to the host only.

**Host:** press **F8** → **Open logs folder** (or title screen → **Online** → **Options** → **Open Logs Folder**), and attach the newest `sosonline-*.log` and `state-*.log` to an [issue](https://github.com/miguelguzmanr/sosonline/issues), with roughly what happened and when. The logs contain the Steam names of the players in the session; nothing else personal.

## Credits

- [BepInEx](https://github.com/BepInEx/BepInEx), [Il2CppInterop](https://github.com/BepInEx/Il2CppInterop) and [HarmonyX](https://github.com/BepInEx/HarmonyX), which make the mod possible.
- [Sabotage Studio](https://sabotagestudio.com/), for Sea of Stars and its local co-op. This mod is unofficial and not affiliated with Sabotage Studio.

---

Building from source, how it works and how it's tested: see [CLAUDE.md](CLAUDE.md).
