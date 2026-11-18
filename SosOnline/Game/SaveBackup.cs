using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEngine;

namespace SosOnline.Game;

/// <summary>
/// Keeps old versions of the saves, so no write can lose progress for good. Saves were lost twice: a test overwrote the
/// player's autosave (2026-11-16), and a playtest's state was gone once later autosaves replaced it. The game keeps a
/// single autosave per slot, and during co-op every area entry autosaves, so a state is overwritten within minutes.
///
/// <list type="bullet">
/// <item>Before the game writes or deletes a save file, and before a new session replaces the mirror folder, the old
/// file is copied to <c>SosOnlineBackup\Saves\&lt;file&gt;\</c> (the PC's own saves) or
/// <c>SosOnlineBackup\Session saves\&lt;file&gt;\</c> (the host's saves mirrored on a guest), named by date and time.
/// Each file keeps its last <see cref="KeepRecent"/> versions plus the newest of each day for <see cref="KeepDays"/>
/// days.</item>
/// <item>When co-op starts, when it ends and when a desync is found, the session's save files are copied to
/// <c>SosOnlineBackup\Sessions\&lt;date-time&gt; &lt;what&gt;\</c>; the last <see cref="KeepSessions"/> are kept.</item>
/// <item>Test runs (any --sosonline- flag) keep their backups inside their sandbox, and may only write saves inside a
/// test folder: anything else goes to <c>SosOnlineTest-blocked</c> with an error in the log.</item>
/// </list>
/// Options (volumes, display) aren't backed up. Saves are about 24 KB, so this stays within a few MB.
/// </summary>
internal static class SaveBackup
{
    public const string FolderName = "SosOnlineBackup";
    public const string BlockedFolder = "SosOnlineTest-blocked";
    private const int KeepRecent = 50;
    private const int KeepDays = 30;
    private const int KeepSessions = 30;
    private const string Stamp = "yyyyMMdd-HHmmss-fff";

    private static string _saveDir;
    private static readonly object Gate = new();

    /// <summary>Main thread, at load: the game's save folder (Unity's API can't be read from other threads).</summary>
    public static void Init()
    {
        try { _saveDir = Application.persistentDataPath; } catch { }
    }

    private static string SaveDir => _saveDir ??= Application.persistentDataPath;

    /// <summary>Where backups go: the real backup folder, or inside the test sandbox in test runs.</summary>
    public static string Root => SaveMirror.SandboxFolder != null
        ? Path.Combine(SaveDir, SaveMirror.SandboxFolder, FolderName)
        : Path.Combine(SaveDir, FolderName);

    /// <summary>
    /// The full path of a save file as the game names it: relative to the save folder, and without ".sos" (the game
    /// adds it), e.g. "SosOnlineSession\Autosave_SOS_SaveSlot0".
    /// </summary>
    public static string FullPath(string gameName)
    {
        string path = Path.IsPathRooted(gameName) ? gameName : Path.Combine(SaveDir, gameName);
        return File.Exists(path) || path.EndsWith(".sos", StringComparison.OrdinalIgnoreCase) ? path : path + ".sos";
    }

    /// <summary>Before a save file is overwritten or deleted: keep a copy of what it holds now.</summary>
    public static void BeforeWrite(string fullPath, string why)
    {
        try
        {
            if (!File.Exists(fullPath) || SaveMirror.IsOptionsFile(fullPath)) return;
            string folder = Path.GetFileName(Path.GetDirectoryName(fullPath)) ?? "";
            bool mirror = folder.StartsWith(SaveMirror.MirrorFolder, StringComparison.OrdinalIgnoreCase)
                          || folder.StartsWith("SosOnlineSession", StringComparison.OrdinalIgnoreCase);
            string dir = Path.Combine(Root, mirror ? "Session saves" : "Saves", Path.GetFileNameWithoutExtension(fullPath));
            lock (Gate)
            {
                Directory.CreateDirectory(dir);
                byte[] data = File.ReadAllBytes(fullPath);
                var newest = Directory.GetFiles(dir, "*.sos").OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal).FirstOrDefault();
                // The same content as the newest copy (an autosave of an unchanged state): nothing to keep.
                if (newest != null && SameContent(newest, data)) return;
                // Named by when this version was written, not when it's replaced.
                string dest = Path.Combine(dir, File.GetLastWriteTime(fullPath).ToString(Stamp, CultureInfo.InvariantCulture) + ".sos");
                if (File.Exists(dest)) dest = Path.Combine(dir, DateTime.Now.ToString(Stamp, CultureInfo.InvariantCulture) + ".sos");
                File.WriteAllBytes(dest, data);
                Prune(dir);
            }
            Plugin.Log.LogInfo($"[backup] {Path.GetFileName(fullPath)} kept before {why}");
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[backup] {Path.GetFileName(fullPath)} not backed up: {e.Message}"); }
    }

    /// <summary>
    /// Copies the save files the game is using now (the mirror during co-op on a guest, else this PC's own) to
    /// Sessions\&lt;date-time&gt; &lt;what&gt;. File I/O only: reads no game state, so it can't affect the simulation.
    /// </summary>
    public static void SnapshotSession(string what)
    {
        try
        {
            string source = SaveMirror.Active ? SaveMirror.MirrorDir : SaveMirror.OwnDir;
            if (!Directory.Exists(source)) return;
            var files = Directory.GetFiles(source, "*.sos").Where(f => !SaveMirror.IsOptionsFile(f)).ToList();
            if (files.Count == 0) return;
            string label = new string(what.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == ' ' ? c : '_').ToArray()).Trim();
            string sessions = Path.Combine(Root, "Sessions");
            string dir = Path.Combine(sessions, $"{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)} {label}");
            lock (Gate)
            {
                Directory.CreateDirectory(dir);
                foreach (var f in files) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)), true);
                foreach (var old in Directory.GetDirectories(sessions).OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal).Skip(KeepSessions))
                    Directory.Delete(old, true);
            }
            Plugin.Log.LogInfo($"[backup] session saves kept ({what}): {files.Count} file(s) in {Path.GetFileName(dir)}");
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[backup] session saves not kept ({what}): {e.Message}"); }
    }

    /// <summary>The session's save files for a log bundle: (name, path) of every save the game is using now.</summary>
    public static List<(string entry, string path)> SessionFilesForLogs()
    {
        var result = new List<(string, string)>();
        try
        {
            string source = SaveMirror.Active ? SaveMirror.MirrorDir : SaveMirror.OwnDir;
            if (Directory.Exists(source))
                foreach (var f in Directory.GetFiles(source, "*.sos").Where(f => !SaveMirror.IsOptionsFile(f)))
                    result.Add(("saves/" + Path.GetFileName(f), f));
        }
        catch { }
        return result;
    }

    /// <summary>
    /// Test runs: a save path the game is about to write must be inside a test folder (the sandbox or a test mirror).
    /// Returns the path to use: unchanged, or one inside <see cref="BlockedFolder"/>.
    /// </summary>
    public static string GuardTestWrite(string gameName)
    {
        if (!TestHarness.HasArgs || string.IsNullOrEmpty(gameName)) return gameName;
        string full = Path.GetFullPath(FullPath(gameName));
        string dir = Path.GetFullPath(Path.GetDirectoryName(full) ?? "");
        string top = Path.GetFullPath(SaveDir);
        string first = dir.StartsWith(top, StringComparison.OrdinalIgnoreCase)
            ? dir.Substring(top.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0]
            : "";
        if (first.StartsWith("SosOnlineTest-", StringComparison.OrdinalIgnoreCase) || first.StartsWith("SosOnlineSession-", StringComparison.OrdinalIgnoreCase))
            return gameName;
        string blocked = Path.Combine(top, BlockedFolder);
        Directory.CreateDirectory(blocked);
        Plugin.Log.LogError($"[backup] test run tried to write {full} outside a test folder; written to {BlockedFolder} instead");
        return Path.Combine(BlockedFolder, Path.GetFileName(gameName));
    }

    private static bool SameContent(string path, byte[] data)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length != data.Length) return false;
            using var sha = SHA256.Create();
            return sha.ComputeHash(File.ReadAllBytes(path)).SequenceEqual(sha.ComputeHash(data));
        }
        catch { return false; }
    }

    /// <summary>Keeps the newest <see cref="KeepRecent"/> versions, plus the newest of each day for <see cref="KeepDays"/> days.</summary>
    private static void Prune(string dir)
    {
        var files = Directory.GetFiles(dir, "*.sos").OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal).ToList();
        if (files.Count <= KeepRecent) return;
        var daysKept = new HashSet<string>();
        foreach (var f in files.Take(KeepRecent)) daysKept.Add(Path.GetFileName(f).Substring(0, 8));
        string oldestDay = DateTime.Now.AddDays(-KeepDays).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        foreach (var f in files.Skip(KeepRecent))
        {
            string day = Path.GetFileName(f).Length >= 8 ? Path.GetFileName(f).Substring(0, 8) : "";
            if (string.CompareOrdinal(day, oldestDay) >= 0 && daysKept.Add(day)) continue; // the newest of its day
            try { File.Delete(f); } catch { }
        }
    }
}
