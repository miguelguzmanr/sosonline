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
- **Automatic, signed updates** from this repository.

## Installation

> [!WARNING]
> The only official download is the [Releases page](https://github.com/miguelguzmanr/sosonline/releases/latest) of this repository.

Everyone in a session needs **the same version of the mod** and of the game. Install it once; after that it updates itself.

You need Sea of Stars on Steam (Windows) and `SosOnline-<version>.zip` from the [latest release](https://github.com/miguelguzmanr/sosonline/releases/latest).

1. **Install BepInEx 6 (IL2CPP).** Download [BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.793](https://builds.bepinex.dev/projects/bepinex_be/793/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.793%2Bdbc82f0.zip) and extract it into the game folder, next to `SeaOfStars.exe`. To find the folder: Steam → right-click Sea of Stars → *Manage* → *Browse local files*.
2. **Start the game once from Steam** and wait for the title screen. The first start takes a minute or two longer while BepInEx prepares itself; a console window opens next to the game. Then quit.
3. **Extract the mod zip** into the same game folder. It adds `BepInEx\plugins\SosOnline\SosOnline.dll`.
4. Start the game from Steam. The title screen now has an **Online** entry.

<details>
<summary>Updates</summary>

At every start the mod checks this repository for a newer release and downloads it in the background. It installs it only if its signature matches. A notice tells you when it's ready; choose **Restart to update** in the title screen's **Online** menu. You can also choose **Check for updates** there at any time.

To turn automatic updates off, set `AutoUpdate = false` under `[Updates]` in `BepInEx\config\sosonline.coop.cfg`. The check is one request to GitHub; nothing personal is sent.

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

**Or start together:** title screen → **Online** → **Host**; friends join, then **Start** and pick a save.

**During a session:**
- **Pause** (Start / Esc) pauses only your screen; the game keeps running for everyone. **Pause for everyone** opens the game's own pause menu on every PC, e.g. for **Assign Players** (choose who plays whom).
- **Leave co-op** (pause screen): your character goes back to the party AI and the others keep playing. The host's **End co-op** ends it for everyone.
- Use a gamepad or keyboard; the mouse is not synchronized.
- Saves made during co-op are the host's. Options (volume, display) stay each PC's own.

## Known limitations

- A friend joining mid-session waits for a moment the game allows saving; sometimes everyone sees a short fade and about 2 seconds of progress is undone.
- If the host leaves, the session ends.
- If one PC can't keep 60 fps, everyone slows down with it.
- Tested mostly on one PC with several game instances; real sessions over Steam are still being shaken out. Reports are welcome.

## Something went wrong?

**Friends:** choose **Send logs to host** (pause screen, or title screen → **Online**). Your game sends your recent logs, and a crash dump if the game crashed, to the host only.

**Host:** press **F8** → **Open logs folder** (or title screen → **Online** → **Open logs folder**), and attach the newest `sosonline-*.log` and `state-*.log` to an [issue](https://github.com/miguelguzmanr/sosonline/issues), with roughly what happened and when. The logs contain the Steam names of the players in the session; nothing else personal.

## Credits

- [BepInEx](https://github.com/BepInEx/BepInEx), [Il2CppInterop](https://github.com/BepInEx/Il2CppInterop) and [HarmonyX](https://github.com/BepInEx/HarmonyX), which make the mod possible.
- [Sabotage Studio](https://sabotagestudio.com/), for Sea of Stars and its local co-op. This mod is unofficial and not affiliated with Sabotage Studio.

---

Building from source, how it works and how it's tested: see [CLAUDE.md](CLAUDE.md).
