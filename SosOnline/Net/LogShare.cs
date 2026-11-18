using SosOnline.Game;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SosOnline.Net;

/// <summary>
/// "Send logs to the host": a guest zips its logs and sends them over the session, the host saves them in
/// <c>logs\received</c>. Only on the player's own button press, and only to the host they are connected to.
///
/// The zip holds this PC's last two starts (mod log and state file: after a crash the previous start is the one that
/// matters), BepInEx's <c>LogOutput.log</c> (it also captures the game's Unity messages), the game's
/// <c>Player.log</c>/<c>Player-prev.log</c> if there are any (Sea of Stars normally writes none), and the newest
/// Windows crash dump of the game made since those starts. It is built off the main thread (a few hundred ms) so lockstep never waits for it, and sent in
/// pieces (<see cref="ChunkBytes"/>: under the UDP test transport's datagram limit, far under Steam's 512 KB).
/// </summary>
internal static class LogShare
{
    public const int ChunkBytes = 60 * 1024;
    private const long MaxFileBytes = 40L * 1024 * 1024;
    public const int MaxBundleBytes = 40 * 1024 * 1024;

    private static Task<byte[]> _building;

    public static bool Busy => _building != null && !_building.IsCompleted;

    /// <summary>Starts zipping in the background. False if a zip is already being built.</summary>
    public static bool Begin()
    {
        if (Busy) return false;
        string modLogs = SessionLogs.Folder;
        string gameDir = SosOnline.Game.SaveMirror.SaveDir; // Unity's persistent data folder: Player.log lives there too
        string bepinex = BepInEx.Paths.BepInExRootPath;
        _building = Task.Run(() => Build(modLogs, gameDir, bepinex));
        return true;
    }

    /// <summary>The finished zip once (null while building, after a failure, or when nothing was started).</summary>
    public static byte[] TakeReady()
    {
        if (_building == null || !_building.IsCompleted) return null;
        var task = _building;
        _building = null;
        if (task.IsFaulted) { Plugin.Log.LogWarning($"[logs] could not pack the logs: {task.Exception?.GetBaseException().Message}"); return null; }
        return task.Result;
    }

    private static byte[] Build(string modLogs, string gameDir, string bepinex)
    {
        var files = new List<(string entry, string path)>();
        if (Directory.Exists(modLogs))
            foreach (var log in new DirectoryInfo(modLogs).GetFiles("sosonline-*.log").OrderByDescending(f => f.Name).Take(2))
            {
                files.Add((log.Name, log.FullName));
                string state = Path.Combine(modLogs, "state-" + log.Name.Substring("sosonline-".Length));
                if (File.Exists(state)) files.Add((Path.GetFileName(state), state));
            }
        foreach (var name in new[] { "Player.log", "Player-prev.log" })
        {
            string path = Path.Combine(gameDir, name);
            if (File.Exists(path)) files.Add(("game/" + name, path));
        }
        string output = Path.Combine(bepinex, "LogOutput.log");
        if (File.Exists(output)) files.Add(("bepinex/LogOutput.log", output));
        // The session's saves (about 24 KB each): what a desync report needs most, and a copy of the progress.
        files.AddRange(SaveBackup.SessionFilesForLogs());

        // The game writes no Player.log; a crash leaves a Windows crash dump instead (a snapshot of the game's memory).
        // Only one made since the older of the two starts above, so an old crash is never sent.
        try
        {
            var oldestStart = files.Where(f => f.entry.StartsWith("sosonline-", StringComparison.Ordinal))
                .Select(f => File.GetCreationTime(f.path)).DefaultIfEmpty(DateTime.Now.AddHours(-6)).Min();
            string dumps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrashDumps");
            var dump = Directory.Exists(dumps)
                ? new DirectoryInfo(dumps).GetFiles("SeaOfStars.exe.*.dmp").Where(d => d.LastWriteTime >= oldestStart).OrderByDescending(d => d.LastWriteTime).FirstOrDefault()
                : null;
            if (dump != null) files.Add(("crash/" + dump.Name, dump.FullName));
        }
        catch { }

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            foreach (var (entry, path) in files)
            {
                try
                {
                    // The game and the mod are still writing some of these.
                    using var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (src.Length > MaxFileBytes)
                    {
                        if (entry.StartsWith("crash/", StringComparison.Ordinal)) continue; // a cut dump is useless
                        src.Seek(-MaxFileBytes, SeekOrigin.End); // a log: keep the end
                    }
                    var e = zip.CreateEntry(entry, CompressionLevel.Optimal);
                    using var dst = e.Open();
                    src.CopyTo(dst);
                }
                catch (Exception ex) { Plugin.Log.LogWarning($"[logs] skipped {entry}: {ex.Message}"); }
            }
            var info = zip.CreateEntry("about.txt");
            using var w = new StreamWriter(info.Open(), Encoding.UTF8);
            w.WriteLine($"Sea of Stars Online Co-op {SessionLogs.Build}, packed {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
            w.WriteLine("Files: " + string.Join(", ", files.Select(f => f.entry)));
        }
        if (ms.Length > MaxBundleBytes) throw new IOException($"logs too large to send ({ms.Length / 1048576} MB)");
        return ms.ToArray();
    }

    // ------------------------------------------------------------------ receiving (host)

    private sealed class Incoming
    {
        public int Count;
        public byte[][] Chunks;
        public int Received;
        public long Bytes;
    }

    private static readonly Dictionary<(ulong from, int id), Incoming> Pending = new();

    /// <summary>Host: a piece arrived. Returns the saved file's path once the last piece is in, else null.</summary>
    public static string Receive(ulong from, string senderName, int id, int index, int count, byte[] chunk)
    {
        if (count <= 0 || count > MaxBundleBytes / ChunkBytes + 1 || index < 0 || index >= count || chunk.Length > ChunkBytes) return null;
        var key = (from, id);
        if (!Pending.TryGetValue(key, out var inc))
        {
            // One transfer per sender at a time: a new one replaces an unfinished one.
            foreach (var k in Pending.Keys.Where(k => k.from == from).ToList()) Pending.Remove(k);
            Pending[key] = inc = new Incoming { Count = count, Chunks = new byte[count][] };
        }
        if (inc.Count != count || inc.Chunks[index] != null) return null;
        inc.Chunks[index] = chunk;
        inc.Received++;
        inc.Bytes += chunk.Length;
        if (inc.Bytes > MaxBundleBytes) { Pending.Remove(key); return null; }
        if (inc.Received < inc.Count) return null;

        Pending.Remove(key);
        string dir = Path.Combine(SessionLogs.Folder, "received");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"{SafeName(senderName)}-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        using (var f = new FileStream(path, FileMode.Create, FileAccess.Write))
            foreach (var c in inc.Chunks) f.Write(c, 0, c.Length);
        return path;
    }

    public static void Forget(ulong from)
    {
        foreach (var k in Pending.Keys.Where(k => k.from == from).ToList()) Pending.Remove(k);
    }

    private static string SafeName(string name)
    {
        var sb = new StringBuilder();
        foreach (char c in name ?? "") if (char.IsLetterOrDigit(c) || c == '-' || c == '_') sb.Append(c);
        return sb.Length == 0 ? "friend" : sb.ToString().Substring(0, Math.Min(sb.Length, 32));
    }
}
