using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using SosOnline.UI;
using UnityEngine;

namespace SosOnline.Update;

/// <summary>
/// Manual updates from the project's GitHub releases.
///
/// At game start (in the background) the latest release is only looked up, so the title screen's version line can say
/// "(Update available)" (<see cref="Available"/>); nothing is downloaded. Updating is always the player's choice:
/// <see cref="CheckNow"/> (the title screen's Online page, "Check for updates") downloads the release's zip and
/// installs the DLL inside only if its signature (<c>SosOnline.dll.sig</c>, ECDSA P-256 over SHA-256)
/// matches <see cref="PublicKey"/>, the key whose private half only the maintainer has, and the DLL itself reports
/// a newer version. The running DLL is renamed to <c>SosOnline.dll.old</c> (Windows allows renaming a loaded file)
/// and the new one takes its place: the next game start runs it. Nothing is ever loaded from the download in this
/// session.
///
/// <see cref="CheckNow"/> asks first: "Up to date", or the newer release with Install / Back, then Restart / Later.
/// On a locally modified build it offers the published release even when that isn't newer, so the PC goes back to a
/// build everyone has. No lookup at start in test runs.
/// </summary>
internal static class AutoUpdate
{
    private const string Repository = "miguelguzmanr/sosonline";
    private const string LatestRelease = "https://api.github.com/repos/" + Repository + "/releases/latest";

    /// <summary>SubjectPublicKeyInfo of the release signing key (tools\new-signing-key.ps1).</summary>
    private const string PublicKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEynG2hD1g0qujrTCpsxX31J+B11LrW2eN3XX1m9NTd0YQzHRq4mSW19bO6yUV1IeNC9E1Vh6SmbX+egzSzB/t1g==";

    private const long MaxDownloadBytes = 20 * 1024 * 1024;
    private const string DllName = "SosOnline.dll";

    /// <summary>Work for the main thread (the game's popups), from the background checks.</summary>
    private static readonly ConcurrentQueue<Action> MainThread = new();

    private enum Manual { Idle, Checking, Downloading }
    private static volatile Manual _manual;

    /// <summary>The Online page's entry: "Check for updates", or what it's doing.</summary>
    public static string CheckLabel => _manual switch
    {
        Manual.Checking => "Checking for Updates…",
        Manual.Downloading => "Downloading the Update…",
        _ => "Check for Updates",
    };

    private static bool LocalBuild => SessionLogs.Build.Contains("-modified");

    private sealed class Release
    {
        public string Tag, Url;
        public Version Version;
    }

    public static Version Current => typeof(AutoUpdate).Assembly.GetName().Version;

    /// <summary>
    /// A version as players see it: "0.56" for 0.1.56. Releases are 0.1.&lt;commit count&gt;; the 1 never changes, so
    /// only the count is shown.
    /// </summary>
    public static string Display(Version v) => v.Minor == 1 ? $"{v.Major}.{Math.Max(v.Build, 0)}" : v.ToString(3);

    /// <summary>A newer release than this build, found by the lookup at start or by "Check for updates"; else null.</summary>
    public static Version Available => _available;
    private static volatile Version _available;

    /// <summary>The version installed for the next start this session, or null. Read on the main thread only.</summary>
    public static Version Downloaded => _downloaded;
    private static volatile Version _downloaded;

    /// <summary>Test harness (--sosonline-fakeupdate=V): act as if V had been downloaded.</summary>
    internal static void FakeDownloaded(Version v) => _downloaded = v;

    /// <summary>Test harness (--sosonline-fakeavailable=V): act as if the lookup had found V.</summary>
    internal static void FakeAvailable(Version v) => _available = v;

    /// <summary>"Restart to update (0.39 -> 0.45)", for the Online menus (the game's menu font has no "→").</summary>
    /// <summary>This build as players see it: "0.58", or "0.58-modified" for a locally modified build.</summary>
    public static string DisplayCurrent => Display(Current) + (LocalBuild ? "-modified" : "");

    public static string RestartLabel => _downloaded == null ? null : $"Restart to Update ({DisplayCurrent} -> {Display(_downloaded)})";

    /// <summary>
    /// Quits the game and starts it again through Steam, so the downloaded build runs. A hidden PowerShell waits for
    /// this process to exit (Steam won't start a game it still sees running), then asks Steam to launch the game.
    /// Leaves the co-op session first.
    /// </summary>
    public static void Restart()
    {
        if (_downloaded == null) return;
        Plugin.Log.LogInfo($"[update] restarting the game for {_downloaded.ToString(3)}");
        try { Net.Session.Instance.Leave(); } catch { }
        try
        {
            int pid = System.Diagnostics.Process.GetCurrentProcess().Id;
            string shell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            string script = $"Wait-Process -Id {pid} -ErrorAction SilentlyContinue; Start-Sleep -Seconds 2; Start-Process 'steam://rungameid/{Steam.SteamApi.AppId}'";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(shell, $"-NoProfile -NonInteractive -WindowStyle Hidden -Command \"{script}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception e)
        {
            // The game still quits: the update runs from the next start either way.
            Plugin.Log.LogWarning($"[update] could not schedule the relaunch: {e.Message}");
        }
        Application.Quit();
    }

    public static void Start()
    {
        string dir = PluginDir();
        if (dir == null) return;
        // Left by the previous update; not loaded any more.
        TryDelete(Path.Combine(dir, DllName + ".old"));
        TryDelete(Path.Combine(dir, DllName + ".new"));

        if (TestHarness.HasArgs) return;
        Task.Run(LookUpAsync);
    }

    /// <summary>Main thread, every frame.</summary>
    public static void Update()
    {
        while (MainThread.TryDequeue(out var action))
            try { action(); } catch (Exception e) { Plugin.Log.LogError($"[update] {e}"); }
    }

    /// <summary>
    /// Main thread, from the Online page: look for the latest release now and ask before installing it. Works with
    /// any build, including a locally modified one (then the published release replaces it).
    /// </summary>
    public static void CheckNow()
    {
        if (_manual != Manual.Idle || _downloaded != null || PluginDir() == null) return;
        _manual = Manual.Checking;
        Plugin.Log.LogInfo("[update] checking for updates (asked)");
        Task.Run(async () =>
        {
            Release release = null;
            string error = null;
            try { release = await FindLatestAsync(); }
            catch (Exception e) { error = $"{e.GetType().Name}: {e.Message}"; }
            MainThread.Enqueue(() => OnChecked(release, error));
        });
    }

    private static void OnChecked(Release release, string error)
    {
        _manual = Manual.Idle;
        if (release == null)
        {
            Plugin.Log.LogWarning($"[update] check failed: {error ?? "no release found"}");
            Toast.Show("Couldn't check for updates");
            return;
        }
        var current = Current;
        bool newer = Normalize(release.Version) > Normalize(current);
        _available = newer ? release.Version : null;
        string v = Display(release.Version);
        Plugin.Log.LogInfo($"[update] latest release {release.Tag}; this build {SessionLogs.Build}{(newer ? ": newer" : LocalBuild ? ": local build" : ": up to date")}");
        if (!newer && !LocalBuild)
        {
            Ask($"Up to date ({DisplayCurrent}).", "OK", null, null);
            return;
        }
        string question = newer ? $"Update {v} is available." : $"This is a local build.\nInstall the published {v}?";
        Ask(question, "Install", "Back", () => InstallNow(release));
    }

    private static void InstallNow(Release release)
    {
        if (_manual != Manual.Idle) return;
        _manual = Manual.Downloading;
        string v = Display(release.Version);
        Toast.Show($"Downloading {v}…");
        bool anyVersion = LocalBuild;
        Task.Run(async () =>
        {
            bool done = false;
            try { done = await DownloadAndInstallAsync(release, anyVersion); }
            catch (Exception e) { Plugin.Log.LogWarning($"[update] install failed: {e.GetType().Name}: {e.Message}"); }
            MainThread.Enqueue(() =>
            {
                _manual = Manual.Idle;
                if (!done) { Toast.Show("Couldn't install the update"); return; }
                Ask($"Update {v} installed. Restart now?", "Restart", "Later", Restart);
            });
        });
    }

    /// <summary>The game's popup when one can open (else a notice); <paramref name="onYes"/> on the first choice.</summary>
    private static void Ask(string message, string yes, string no, Action onYes)
    {
        if (!UI.NativeMenus.Popup(message, yes, no, i => { if (i == 0) onYes?.Invoke(); }))
            Toast.Show(message.Replace("\n", " "));
    }

    private static HttpClient Http()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SosOnline-updater");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>The latest release and its zip, or null (none published, or no usable zip).</summary>
    private static async Task<Release> FindLatestAsync()
    {
        using var http = Http();
        using var response = await http.GetAsync(LatestRelease);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) { Plugin.Log.LogInfo("[update] no release published yet"); return null; }
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) { Plugin.Log.LogWarning($"[update] unexpected release tag '{tag}'"); return null; }
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            string name = asset.GetProperty("name").GetString() ?? "";
            if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
            if (asset.GetProperty("size").GetInt64() > MaxDownloadBytes) break;
            return new Release { Tag = tag, Version = latest, Url = asset.GetProperty("browser_download_url").GetString() };
        }
        Plugin.Log.LogWarning($"[update] release {tag} has no usable zip");
        return null;
    }

    /// <summary>At start: only whether a newer release exists, for the title screen's version line.</summary>
    private static async Task LookUpAsync()
    {
        try
        {
            var release = await FindLatestAsync();
            if (release == null) return;
            var current = Current;
            bool newer = Normalize(release.Version) > Normalize(current);
            if (newer) _available = release.Version;
            Plugin.Log.LogInfo($"[update] latest release {release.Tag}; this build {current.ToString(3)}{(newer ? ": update available" : ": up to date")}");
        }
        catch (Exception e)
        {
            // Offline, GitHub unreachable or rate-limited: the version line just doesn't mention an update.
            Plugin.Log.LogWarning($"[update] lookup failed: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// Downloads the release's zip and installs its DLL if the signature matches. <paramref name="anyVersion"/>: also
    /// when it isn't newer (asked for on a local build); otherwise never goes back.
    /// </summary>
    private static async Task<bool> DownloadAndInstallAsync(Release release, bool anyVersion)
    {
        string tag = release.Tag;
        var current = Current;
        using var http = Http();
        {
            Plugin.Log.LogInfo($"[update] {current.ToString(3)} → {tag}: downloading");
            byte[] zipBytes = await http.GetByteArrayAsync(release.Url);
            if (zipBytes.Length > MaxDownloadBytes) return false;

            byte[] dll = null, sig = null;
            using (var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read))
                foreach (var entry in zip.Entries)
                {
                    // Zips made by Windows PowerShell 5 use backslashes.
                    string path = entry.FullName.Replace('\\', '/');
                    if (path.EndsWith("/" + DllName, StringComparison.OrdinalIgnoreCase)) dll = Read(entry);
                    else if (path.EndsWith("/" + DllName + ".sig", StringComparison.OrdinalIgnoreCase)) sig = Read(entry);
                }
            if (dll == null || sig == null) { Plugin.Log.LogWarning($"[update] release {tag}: DLL or signature missing; not installed"); return false; }

            using (var key = ECDsa.Create())
            {
                key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(PublicKey), out _);
                if (!key.VerifyData(dll, sig, HashAlgorithmName.SHA256))
                {
                    Plugin.Log.LogWarning($"[update] release {tag}: signature check failed; not installed");
                    return false;
                }
            }

            return Install(dll, sig, current, tag, anyVersion);
        }
    }

    private static bool Install(byte[] dll, byte[] sig, Version current, string tag, bool anyVersion)
    {
        // Test runs never replace the mod under test (a scripted popup answer once installed the published release).
        if (TestHarness.HasArgs) { Plugin.Log.LogWarning($"[update] test run: {tag} not installed (acting as if it was)"); return true; }
        string dir = PluginDir();
        if (dir == null) return false;
        string target = Path.Combine(dir, DllName), staged = target + ".new", old = target + ".old";

        File.WriteAllBytes(staged, dll);
        var name = AssemblyName.GetAssemblyName(staged);
        if (name.Name != "SosOnline" || name.Version == null || (!anyVersion && Normalize(name.Version) <= Normalize(current)))
        {
            // Signed, but not newer (an old release served again): never go back, unless asked for on a local build.
            TryDelete(staged);
            Plugin.Log.LogWarning($"[update] release {tag} contains {name.Name} {name.Version}; not newer, not installed");
            return false;
        }

        File.Move(target, old, true);
        try { File.Move(staged, target); }
        catch
        {
            File.Move(old, target, true); // put the running build back
            throw;
        }
        // Its signature next to it, as in the release (a local build has none, or a stale one).
        try { File.WriteAllBytes(target + ".sig", sig); } catch (Exception e) { Plugin.Log.LogWarning($"[update] signature file not written: {e.Message}"); }
        _downloaded = name.Version;
        Plugin.Log.LogInfo($"[update] installed {name.Version.ToString(3)} ({tag}); it runs from the next start");
        return true;
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    private static byte[] Read(ZipArchiveEntry entry)
    {
        if (entry.Length > MaxDownloadBytes) return null;
        using var s = entry.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static string PluginDir()
    {
        try
        {
            string location = typeof(AutoUpdate).Assembly.Location;
            return string.IsNullOrEmpty(location) ? null : Path.GetDirectoryName(location);
        }
        catch { return null; }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
