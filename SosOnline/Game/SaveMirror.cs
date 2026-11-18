using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace SosOnline.Game;

/// <summary>
/// Keeps every peer on the host's save data.
///
/// Host: packs its save folder (index, slots, autosaves; options stay each PC's own).
/// Guest: writes those files into &lt;saves&gt;/SosOnlineSession and redirects the game's file I/O there, then
/// reloads SaveManager. The guest's own saves are never read or written while the mirror is active, and the
/// in-game saves made during co-op land in the mirror folder (the host keeps the canonical copy).
/// </summary>
internal static class SaveMirror
{
    /// <summary>Guests' copy of the host's saves (per instance in test mode, so local test instances don't collide).</summary>
    public static string MirrorFolder = "SosOnlineSession";

    /// <summary>
    /// Test mode: this instance's own saves live in a sandbox copy, so test runs never write the player's real saves.
    /// </summary>
    public static string SandboxFolder;

    /// <summary>Copies the real saves into a fresh sandbox and plays from it (call before the game loads its saves).</summary>
    public static void UseSandbox(string folder)
    {
        var dir = Path.Combine(SaveDir, folder);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        foreach (var f in Directory.GetFiles(SaveDir, "*.sos")) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
        SandboxFolder = folder;
        Plugin.Log.LogInfo($"[saves] sandboxed in {dir}");
    }

    /// <summary>Where this instance's own saves are (the real folder, or the test sandbox).</summary>
    public static string OwnDir => SandboxFolder != null ? Path.Combine(SaveDir, SandboxFolder) : SaveDir;

    public static bool Active { get; private set; }

    public static string SaveDir => Application.persistentDataPath;
    public static string MirrorDir => Path.Combine(SaveDir, MirrorFolder);

    public const string IndexFile = "SOS_SaveDataIndex.sos";

    /// <summary>File name of save slot <paramref name="slot"/> (or of its autosave).</summary>
    public static string SlotFile(int slot, bool auto) => auto ? $"Autosave_SOS_SaveSlot{slot}.sos" : $"SOS_SaveSlot{slot}.sos";

    /// <summary>
    /// The host's save files a session needs: the index and the slots named in <paramref name="only"/> (all slots when
    /// null, or when one of them isn't on disk under the expected name).
    /// </summary>
    public static List<(string name, byte[] data)> PackHostFiles(ICollection<string> only = null)
    {
        if (only != null && only.Any(n => !File.Exists(Path.Combine(OwnDir, n))))
        {
            Plugin.Log.LogWarning($"[saves] {string.Join(", ", only)} not all found; sending every slot");
            only = null;
        }
        var files = new List<(string, byte[])>();
        // Slots, autosaves (Autosave_SOS_SaveSlotN.sos) and index. Not the options: those stay each PC's own.
        foreach (var path in Directory.GetFiles(OwnDir, "*.sos"))
        {
            string name = Path.GetFileName(path);
            if (IsOptionsFile(path)) continue;
            if (only != null && name != IndexFile && !only.Contains(name)) continue;
            files.Add((name, File.ReadAllBytes(path)));
        }
        return files;
    }

    public static void BeginReceive()
    {
        Directory.CreateDirectory(MirrorDir);
        // A guest's mirror may hold the only copy of an earlier session's progress: keep it before it's replaced.
        foreach (var f in Directory.GetFiles(MirrorDir))
        {
            if (f.EndsWith(".sos", StringComparison.OrdinalIgnoreCase)) SaveBackup.BeforeWrite(f, "a new session's saves");
            File.Delete(f);
        }
    }

    public static void WriteFile(string name, byte[] data)
    {
        // Never let a peer write outside the mirror folder.
        var safe = Path.GetFileName(name);
        if (string.IsNullOrEmpty(safe) || !safe.EndsWith(".sos", StringComparison.OrdinalIgnoreCase)
            || !(safe.StartsWith("SOS_", StringComparison.Ordinal) || safe.StartsWith("Autosave_SOS_", StringComparison.Ordinal))) return;
        File.WriteAllBytes(Path.Combine(MirrorDir, safe), data);
    }

    private static Il2CppSystem.Threading.Tasks.Task _reload;

    /// <summary>Activates/deactivates redirection and asks SaveManager to reload everything from disk.</summary>
    public static void SetActiveAndReload(bool active)
    {
        Active = active;
        var saves = GameBridge.Saves;
        if (saves == null) return;
        try
        {
            var standalone = saves.saveLoad?.TryCast<SaveLoadStandalone>();
            // The registry backup would otherwise copy the host's data over the guest's own backup.
            if (standalone != null) standalone.registryBackup = !active && _savedRegistryBackup;
        }
        catch { }
        _reload = saves.LoadSaveData();
    }

    /// <summary>Asks SaveManager to read every slot from disk again (where it reads now), changing nothing else.</summary>
    public static void ReloadSaves()
    {
        var saves = GameBridge.Saves;
        if (saves != null) _reload = saves.LoadSaveData();
    }

    private static bool _savedRegistryBackup = true;

    public static void RememberRegistryBackup()
    {
        try
        {
            var standalone = GameBridge.Saves?.saveLoad?.TryCast<SaveLoadStandalone>();
            if (standalone != null) _savedRegistryBackup = standalone.registryBackup;
        }
        catch { }
    }

    public static bool ReloadDone => _reload == null || _reload.IsCompleted;

    private static string _optionsName;

    public static bool IsOptionsFile(string fileName)
    {
        if (_optionsName == null) { try { _optionsName = SaveManager.OPTIONS_FILENAME ?? ""; } catch { _optionsName = ""; } }
        return _optionsName.Length > 0 && string.Equals(Path.GetFileNameWithoutExtension(fileName), Path.GetFileNameWithoutExtension(_optionsName), StringComparison.OrdinalIgnoreCase);
    }

    private static string Redirect(string fileName)
    {
        // Options (volumes, display, controls) belong to each PC: never the host's copy, and a change made during a
        // session goes to this PC's own file and stays after it.
        string folder = Active && !IsOptionsFile(fileName) ? MirrorFolder : SandboxFolder;
        if (folder == null || string.IsNullOrEmpty(fileName)) return fileName;
        if (fileName.Contains(folder)) return fileName;
        if (Path.IsPathRooted(fileName))
            return Path.Combine(Path.GetDirectoryName(fileName) ?? SaveDir, folder, Path.GetFileName(fileName));
        return Path.Combine(folder, fileName);
    }

    [HarmonyPatch(typeof(SaveLoadStandalone), nameof(SaveLoadStandalone.SaveFile))]
    private static class P_Save
    {
        private static void Prefix(SaveLoadStandalone __instance, ref string __1)
        {
            string before = __1;
            try { if (NativeCode.Is<SaveLoadStandalone>(__instance)) __1 = Redirect(__1); }
            catch (Exception e) { Plugin.Log.LogError($"[saves] redirect failed for {before}: {e}"); }
            try { __1 = SaveBackup.GuardTestWrite(__1); } catch (Exception e) { Plugin.Log.LogError($"[saves] test guard failed for {__1}: {e}"); }
            Plugin.Log.LogInfo($"[saves] write {before} -> {__1} (thread {Environment.CurrentManagedThreadId})");
            SaveBackup.BeforeWrite(SaveBackup.FullPath(__1), "a save");
        }
    }

    [HarmonyPatch(typeof(SaveLoadStandalone), nameof(SaveLoadStandalone.LoadFile))]
    private static class P_Load { private static void Prefix(SaveLoadStandalone __instance, ref string __0) { if (NativeCode.Is<SaveLoadStandalone>(__instance)) __0 = Redirect(__0); } }

    [HarmonyPatch(typeof(SaveLoadStandalone), nameof(SaveLoadStandalone.DeleteFile))]
    private static class P_Delete
    {
        private static void Prefix(SaveLoadStandalone __instance, ref string __0)
        {
            if (!NativeCode.Is<SaveLoadStandalone>(__instance)) return;
            __0 = Redirect(__0);
            try { __0 = SaveBackup.GuardTestWrite(__0); } catch (Exception e) { Plugin.Log.LogError($"[saves] test guard failed for {__0}: {e}"); }
            SaveBackup.BeforeWrite(SaveBackup.FullPath(__0), "a delete");
        }
    }
}
