using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Rewired;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SosOnline.Game;

/// <summary>Thin, defensive wrappers around the bits of Sea of Stars the mod touches.</summary>
internal static class GameBridge
{
    public static InputManager Input => SafeInstance<InputManager>();
    public static SaveManager Saves => SafeInstance<SaveManager>();
    public static SinglePlayerPlusManager Coop => SafeInstance<SinglePlayerPlusManager>();
    public static PlayerPartyManager Party => SafeInstance<PlayerPartyManager>();
    public static CharacterStatsManager Stats => SafeInstance<CharacterStatsManager>();
    public static CurrencyManager Currency => SafeInstance<CurrencyManager>();
    public static InventoryManager Inventory => SafeInstance<InventoryManager>();
    public static CombatManager Combat => SafeInstance<CombatManager>();

    private static T SafeInstance<T>() where T : Manager<T>
    {
        try { return Manager<T>.instance; } catch { return null; }
    }

    /// <summary>
    /// Which of the game's players (P1..P3) each session slot drives. Identity at session start; the Assign Players
    /// screen can reorder it (same on every peer).
    /// </summary>
    public static readonly int[] SlotPlayerIndex = { 0, 1, 2 };

    public static void ResetSlotPlayers()
    {
        for (int i = 0; i < SlotPlayerIndex.Length; i++) SlotPlayerIndex[i] = i;
    }

    /// <summary>Rewired player that session slot <paramref name="slot"/> drives (slot 0 = host).</summary>
    public static Player PlayerForSlot(int slot)
    {
        if (slot >= 0 && slot < SlotPlayerIndex.Length) slot = SlotPlayerIndex[slot];
        return GamePlayer(slot);
    }

    /// <summary>The game's player <paramref name="index"/> (P1 = 0).</summary>
    public static Player GamePlayer(int index)
    {
        int slot = index;
        try
        {
            var im = Input;
            if (im != null)
            {
                var p = im.GetPlayer(slot);
                if (p != null) return p;
            }
        }
        catch { }
        return ReInput.isReady ? ReInput.players.GetPlayer(slot) : null;
    }

    public static TitleScreen FindTitleScreen()
    {
        var all = UnityEngine.Object.FindObjectsOfType<TitleScreen>();
        foreach (var t in all)
            if (t != null && t.isActiveAndEnabled) return t;
        return null;
    }

    private static float _titleCheckedAt = -1;
    private static bool _atTitle;

    /// <summary>Whether the title screen is up (cached for half a second, FindObjectsOfType is slow).</summary>
    public static bool AtTitleScreen
    {
        get
        {
            if (_titleCheckedAt < 0 || Time.unscaledTime - _titleCheckedAt > 0.5f)
            {
                _titleCheckedAt = Time.unscaledTime;
                try { _atTitle = FindTitleScreen() != null; } catch { _atTitle = false; }
            }
            return _atTitle;
        }
    }

    public static string ActiveSceneName
    {
        get
        {
            try
            {
                var names = new List<string>();
                for (int i = 0; i < SceneManager.sceneCount; i++) names.Add(SceneManager.GetSceneAt(i).name);
                names.Sort(StringComparer.Ordinal);
                return string.Join(",", names);
            }
            catch { return "?"; }
        }
    }

    // ---------------- Determinism ----------------

    private static float _savedCaptureDelta;
    private static int _savedTargetFps, _savedVsync;
    private static bool _timeLocked;

    /// <summary>Every frame advances game time by exactly 1/60 s on every peer.</summary>
    public static void LockTime()
    {
        if (_timeLocked) return;
        _savedCaptureDelta = Time.captureDeltaTime;
        _savedTargetFps = Application.targetFrameRate;
        _savedVsync = QualitySettings.vSyncCount;
        Time.captureDeltaTime = 1f / 60f;
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = 60;
        _timeLocked = true;
    }

    public static void UnlockTime()
    {
        if (!_timeLocked) return;
        Time.captureDeltaTime = _savedCaptureDelta;
        QualitySettings.vSyncCount = _savedVsync;
        Application.targetFrameRate = _savedTargetFps;
        _timeLocked = false;
    }

    // ---------------- Session entry ----------------

    /// <summary>
    /// Loads <paramref name="slotIndex"/> (or its autosave) through the title screen's Continue flow.
    /// Runs on the same lockstep frame on every peer.
    /// </summary>
    public static bool EnterGameFromSlot(int slotIndex, bool autoSave)
    {
        var saves = Saves;
        if (saves == null) { Plugin.Log.LogError("SaveManager not available"); return false; }
        var slot = autoSave ? saves.GetAutoSaveSlot(slotIndex) : saves.GetSaveSlot(slotIndex);
        if (slot == null) { Plugin.Log.LogError($"Save slot {slotIndex} (auto={autoSave}) not found: no {(autoSave ? "Autosave_" : "")}SOS_SaveSlot{slotIndex}.sos. Slots are counted from 0 (the game's \"Slot 1\" is 0)."); return false; }
        saves.LoadSaveSlotAndSetLastIndex(slot);
        var title = FindTitleScreen();
        if (title == null) { Plugin.Log.LogError("Title screen not active"); return false; }
        title.OnContinue();
        return true;
    }

    /// <summary>Makes the game consider every occupied slot as a player with an input device.</summary>
    public static void RegisterSlotPlayers(IEnumerable<int> slots)
    {
        var im = Input;
        if (im == null) return;
        foreach (int s in slots)
        {
            var p = PlayerForSlot(s);
            if (p == null) continue;
            try
            {
                if (!im.assignedPlayers.Contains(p)) im.AssignPlayer(p);
            }
            catch (Exception e) { Plugin.Log.LogWarning($"AssignPlayer({s}) failed: {e.Message}"); }
        }
    }

    // ---------------------------------------------------------------- co-op players

    /// <summary>The game's co-op player object for a session slot.</summary>
    public static SinglePlayerPlusPlayer CoopPlayer(int slot)
    {
        try
        {
            var p = PlayerForSlot(slot);
            return p == null ? null : Coop?.GetSinglePlayerPlusPlayer(p);
        }
        catch { return null; }
    }

    public static bool IsPlaying(int slot)
    {
        try { return CoopPlayer(slot)?.Playing ?? false; } catch { return false; }
    }

    /// <summary>Makes a slot's player leave co-op (their character goes back to the AI follower).</summary>
    public static void MakeLeave(int slot)
    {
        try
        {
            var p = CoopPlayer(slot);
            if (p != null && p.Playing) p.Leave();
        }
        catch (Exception e) { Plugin.Log.LogWarning($"Leave({slot}) failed: {e.Message}"); }
    }

    /// <summary>Rewired player id → session slot (or -1).</summary>
    public static int SlotOf(Player p)
    {
        if (p == null) return -1;
        for (int s = 0; s < Lockstep.InputOverride.SlotCount; s++)
            if (PlayerForSlot(s)?.id == p.id) return s;
        return -1;
    }

    // ---------------------------------------------------------------- characters

    private static string IdOf(ISinglePlayerPlusCharacter c)
    {
        try { return c?.CharacterDefinitionId?.characterId ?? ""; } catch { return ""; }
    }

    /// <summary>Character id ("Valere", …) the slot's co-op player controls, or "" when it controls none.</summary>
    public static string CharacterOf(int slot)
    {
        try { return IdOf(CoopPlayer(slot)?.AssignedCharacter); } catch { return ""; }
    }

    /// <summary>The party member that slot <paramref name="slot"/> could be given: in the party and nobody else's.</summary>
    private static ISinglePlayerPlusCharacter FreeCharacter(int slot, string characterId)
    {
        var party = Party?.LeaderAndFollowers;
        if (party == null || string.IsNullOrEmpty(characterId)) return null;
        ISinglePlayerPlusCharacter found = null;
        for (int i = 0; i < party.Count; i++)
        {
            var c = party[i] == null ? null : new ISinglePlayerPlusCharacter(party[i].Pointer);
            if (IdOf(c) == characterId) { found = c; break; }
        }
        if (found == null) return null;
        for (int s = 0; s < Lockstep.InputOverride.SlotCount; s++)
            if (s != slot && IsPlaying(s) && CharacterOf(s) == characterId) return null;
        // The leader is the first player's.
        if (IdOf(new ISinglePlayerPlusCharacter(party[0].Pointer)) == characterId) return null;
        return found;
    }

    public static bool CanAssignCharacter(int slot, string characterId)
    {
        try { return FreeCharacter(slot, characterId) != null; } catch { return false; }
    }

    /// <summary>
    /// Gives slot <paramref name="slot"/>'s co-op player the character <paramref name="characterId"/> if it is still
    /// free. Runs on the same lockstep frame on every PC (the checks read synced game state).
    /// </summary>
    public static void AssignCharacter(int slot, string characterId)
    {
        try
        {
            var player = CoopPlayer(slot);
            var target = FreeCharacter(slot, characterId);
            if (player == null || !player.Playing || target == null)
            {
                Plugin.Log.LogInfo($"[characters] slot {slot}: {characterId} not free any more; keeping {CharacterOf(slot)}");
                return;
            }
            var coop = Coop;
            coop?.BeginBatchCharacterAssignation();
            try
            {
                player.UnassignCharacter();
                player.AssignCharacter(target);
            }
            finally { coop?.EndBatchCharacterAssignation(); }
            Plugin.Log.LogInfo($"[characters] slot {slot} now plays {CharacterOf(slot)}");
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[characters] assigning {characterId} to slot {slot} failed: {e.Message}"); }
    }

    /// <summary>Test helper: the id of the last party member.</summary>
    public static string LastPartyCharacter()
    {
        try
        {
            var party = Party?.LeaderAndFollowers;
            return party == null || party.Count == 0 ? "" : IdOf(new ISinglePlayerPlusCharacter(party[party.Count - 1].Pointer));
        }
        catch { return ""; }
    }

    // ---------------------------------------------------------------- level / saving

    public static LevelManager Levels => SafeInstance<LevelManager>();

    public static string CurrentLevelId
    {
        get { try { return Levels?.CurrentLevel?.levelDefinitionGuid ?? ""; } catch { return ""; } }
    }

    public static bool LoadingLevel
    {
        get { try { return Levels?.LoadingLevel ?? true; } catch { return true; } }
    }

    /// <summary>
    /// The game finished booting (title screen or gameplay) and no level is loading. Before that, the boot sequence
    /// still loads the title screen on its own, on top of anything loaded in the meantime.
    /// </summary>
    public static bool Booted => !LoadingLevel && CurrentLevelId != "";

    /// <summary>A battle is running.</summary>
    public static bool InBattle
    {
        get { try { return Combat?.IsAnEncounterRunning() ?? false; } catch { return false; } }
    }

    /// <summary>
    /// The level-up sequence after a battle is running: it loads its own scene (<c>LevelUp</c>) on top of the area.
    /// A reload in place during it left that scene loaded on the PCs already in, but not on the joiner, and every check
    /// after found the scenes different (2026-11-15). Read from the loaded scenes, so every PC answers the same.
    /// </summary>
    public static bool InLevelUp
    {
        get
        {
            try
            {
                for (int i = 0; i < SceneManager.sceneCount; i++)
                    if (SceneManager.GetSceneAt(i).name.IndexOf("LevelUp", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            catch { }
            return false;
        }
    }

    /// <summary>Something that a drop-in reload must not interrupt: a level load, a battle or a level-up. Null if none.</summary>
    public static string DropInBlocker => LoadingLevel ? "a level load" : InBattle ? "a battle" : InLevelUp ? "a level-up" : PartyWiped ? "a game over" : null;

    /// <summary>
    /// Every current party member is at 0 HP: a lost battle, before the game's own game-over reload. The game still
    /// says it can save then; a drop-in autosave taken there (f7586, 2026-11-16) gave the joiner a party at 0 HP while
    /// the game-over reload had restored the host's, and the joiner split right after joining.
    /// </summary>
    public static bool PartyWiped
    {
        get
        {
            try
            {
                var ids = Party?.CurrentParty;
                var stats = Stats;
                if (ids == null || stats == null || ids.Count == 0) return false;
                for (int i = 0; i < ids.Count; i++)
                {
                    var d = stats.GetCharacterData(ids[i]);
                    if (d == null || d.currentHP > 0) return false;
                }
                return true;
            }
            catch { return false; }
        }
    }

    /// <summary>In free gameplay where the game itself would allow saving (no battle, cutscene, loading or game over).</summary>
    public static bool CanSaveNow
    {
        get
        {
            try
            {
                if (LoadingLevel || Party?.Leader == null) return false;
                var combat = SafeInstance<CombatManager>();
                if (combat != null && combat.IsAnEncounterRunning()) return false;
                if (InLevelUp || PartyWiped) return false;
                return Saves != null && Saves.CanSave() && !Saves.Saving;
            }
            catch { return false; }
        }
    }

    public static bool Saving
    {
        get { try { return Saves?.Saving ?? false; } catch { return false; } }
    }

    /// <summary>Writes an autosave of the current game; returns its slot index (or -1).</summary>
    public static int AutoSave()
    {
        try
        {
            var saves = Saves;
            if (saves == null || !saves.AutoSave()) return -1;
            return saves.LoadedSaveGameSlot?.slotIndex ?? -1;
        }
        catch (Exception e) { Plugin.Log.LogWarning($"AutoSave failed: {e.Message}"); return -1; }
    }

    /// <summary>
    /// Loads autosave <paramref name="slotIndex"/> from gameplay, without the title screen. Restores the
    /// managers from the slot (SaveManager.onSaveSlotLoaded), then loads the slot's checkpoint level behind the
    /// game's own transition. Runs on the same lockstep frame on every peer.
    /// </summary>
    public static bool ReloadInPlace(int slotIndex, bool autoSave = true, Vector3? spawnAt = null, int orientation = -1)
    {
        try
        {
            var saves = Saves;
            var levels = Levels;
            if (saves == null || levels == null) { Plugin.Log.LogError("ReloadInPlace: managers missing"); return false; }
            var slot = autoSave ? saves.GetAutoSaveSlot(slotIndex) : saves.GetSaveSlot(slotIndex);
            if (slot == null) { Plugin.Log.LogError($"ReloadInPlace: slot {slotIndex} (auto={autoSave}) not found"); return false; }
            var cp = slot.checkpointData;
            Plugin.Log.LogInfo($"[reload] slot {slotIndex}: level {cp?.level?.levelDefinitionGuid} savepoint {cp?.savepointId} pos {cp?.position} entrance {slot.lastUsedEntranceId}");
            saves.LoadSaveSlotAndSetLastIndex(slot);
            var init = new GameplayLevelInitializerParams
            {
                SavepointId = cp.savepointId,
                isBoat = cp.isBoat,
                playerOrientation = orientation >= 0 ? orientation : cp.playerOrientation,
                spawnPosition = spawnAt ?? cp.position,
                spawnPositionSet = true,
            };
            var loading = new LevelLoading
            {
                levelToLoad = cp.level,
                showTransition = true,
                removeTransitionWhenDone = true,
                initializerParams = init,
            };
            levels.LoadLevel(loading);
            return true;
        }
        catch (Exception e) { Plugin.Log.LogError($"ReloadInPlace failed: {e}"); return false; }
    }

    /// <summary>Where the party stands now: the leader's position and facing, and every member's position.</summary>
    public static PartySpawn CaptureParty()
    {
        var leader = Party?.Leader;
        if (leader == null) return null;
        var spawn = new PartySpawn { Leader = leader.transform.position };
        try { spawn.Angle = leader.lookDirectionController.CurrentLookAngle; } catch { }
        var list = Party.LeaderAndFollowers;
        for (int i = 0; i < list.Count; i++)
            if (list[i] != null) spawn.Members.Add((list[i].gameObject.name, list[i].transform.position));
        return spawn;
    }

    /// <summary>Puts every party member back on the position recorded in <paramref name="spawn"/>.</summary>
    public static string RestoreParty(PartySpawn spawn)
    {
        var list = Party?.LeaderAndFollowers;
        if (spawn == null || list == null) return "no party";
        var moved = new List<string>();
        for (int i = 0; i < list.Count; i++)
        {
            var c = list[i];
            if (c == null) continue;
            foreach (var (name, pos) in spawn.Members)
                if (name == c.gameObject.name)
                {
                    moved.Add($"{name} {(c.transform.position - pos).magnitude:F2}m");
                    c.transform.position = pos;
                }
        }
        return string.Join(", ", moved);
    }

    public static List<(int index, bool auto, string label)> ListSaveSlots()
    {
        var result = new List<(int, bool, string)>();
        var saves = Saves;
        if (saves == null) return result;
        for (int i = 0; i < SaveManager.MAX_SAVE_SLOT; i++)
        {
            try
            {
                if (saves.GetSaveSlot(i) != null) result.Add((i, false, $"Slot {i + 1}"));
                if (saves.GetAutoSaveSlot(i) != null) result.Add((i, true, $"Slot {i + 1} (Autosave)"));
            }
            catch { }
        }
        return result;
    }
}

/// <summary>Party positions to respawn at after an in-place reload (identical values on every PC).</summary>
internal sealed class PartySpawn
{
    public Vector3 Leader;
    public int Angle = -1;
    public readonly List<(string name, Vector3 pos)> Members = new();

    public void Write(System.IO.BinaryWriter w)
    {
        WriteVec(w, Leader);
        w.Write(Angle);
        w.Write(Members.Count);
        foreach (var (name, pos) in Members) { w.Write(name); WriteVec(w, pos); }
    }

    public static PartySpawn Read(System.IO.BinaryReader r)
    {
        var s = new PartySpawn { Leader = ReadVec(r), Angle = r.ReadInt32() };
        int n = r.ReadInt32();
        for (int i = 0; i < n; i++) s.Members.Add((r.ReadString(), ReadVec(r)));
        return s;
    }

    private static void WriteVec(System.IO.BinaryWriter w, Vector3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
    private static Vector3 ReadVec(System.IO.BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

    public override string ToString() => $"leader {Leader} angle {Angle}, {Members.Count} member(s)";
}
