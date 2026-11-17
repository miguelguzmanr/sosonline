using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BepInEx;
using SosOnline.Game;
using SosOnline.Lockstep;
using SosOnline.Net;
using UnityEngine;

namespace SosOnline;

/// <summary>
/// Development harness driven by command-line flags, so two game instances on one PC can test a session end to end:
///   --sosonline-udp=PORT          use loopback UDP instead of Steam (peer id = port)
///   --sosonline-host              host automatically
///   --sosonline-join=PORT         join a UDP host automatically
///   --sosonline-start=SLOT[a]     host: start from save SLOT (suffix 'a' = autosave) once guests are in and title is up
///   --sosonline-guests=N          guests to wait for before starting (default 1, 0 = solo lockstep)
///   --sosonline-delay=N           fixed input delay in frames
///   --sosonline-script=FILE       scripted local input: "frame [sys] action holdFrames [axis]" per line
///   --sosonline-shots=F1,F2,...   screenshot at those lockstep frames
///   --sosonline-endsession=F      host: "End session" (local pause screen) at lockstep frame F
///   --sosonline-quitruns=N        with --sosonline-quit: once lockstep has stopped, quit only after N runs have ended
///   --sosonline-quit=F            quit at lockstep frame F (if lockstep stops before F: the remaining frames later, at
///                                 least one second, counted in Unity frames)
///   --sosonline-reload=F          spike A: autosave at F, reload it in place at F+120 (every PC)
///   --sosonline-ff[=nocam]        spike C: uncapped frame rate, audio muted (and cameras off)
///   --sosonline-seed=N            fixed session seed (host)
///   --sosonline-stateevery=N      state snapshot interval in frames (default 30)
///   --sosonline-hpdiverge=F[,F2]  at lockstep frame F (the first time this PC reaches it; a replay passes it again
///                                 without effect), lower the first party member's HP by 1 on this PC only, and mark
///                                 this PC's state as diverged (the "test" snapshot component), which can't be corrected
///   --sosonline-hpdivergeagain    with --sosonline-hpdiverge: fire every time the frame is reached (a cause that
///                                 reproduces on this PC, e.g. in a replay)
///   --sosonline-nolockfix         leave spell lock rolls to each PC (reproduces the Bosslug split of 2026-11-16)
///   --sosonline-nosummonfix       leave summon slot picks to each PC (reproduces the Acid Bugs' swapped slots)
///   --sosonline-pausestate=F[,F2]  log the game's pause state (paused, pause disablers) at those frames
///   --sosonline-titleoptions      open the game's Options from the title screen and log for 10 s (crash check)
///   --sosonline-titleflow         title choices shown again with Online open, then Online > Host > Start > a save (menu checks)
///   --sosonline-updatecheck       title screen: Online page, Check for updates; quits after 90 s
///   --sosonline-fakeavailable=V   act as if the release lookup found V (the version line says "(Update available)")
///   --sosonline-versionshot       title screen: screenshot ui-version with the co-op version line, then quit
///   --sosonline-popupanswer=L,... answer the mod's popups that offer one of these labels ("_" for a space)
///   --sosonline-npcprobe          log every frame the active NPCs and each NPC's first inactive ancestor change
///   --sosonline-summonprobe[=N]   log each summon's slot list, reservations and assignments; N > 0 draws N extra
///                                 random values before each (to show whether the pick reads the shared generator)
///   --sosonline-noresync          guest: report differences but never ask to re-sync (to see later ones, e.g. spawns)
///   --sosonline-hpnudge=F         at lockstep frame F, lower the first party member's HP by 1 on this PC only, without
///                                 the mark: the guest's state check corrects it in place
///   --sosonline-noclock           each PC keeps its own game clock (no session clock, finding 8)
///   --sosonline-play=SLOT[a]      guest: first play save SLOT on its own (offline), then join (--sosonline-join);
///                                 host: play save SLOT with the lobby open (no --sosonline-start), friends join it there
///   --sosonline-rememberchar=ID   host: pretend each guest last played character ID ("last": the last party member)
///   --sosonline-swapat=F          host: at frame F, check every guest's character as if it had just begun playing, with
///                                 the last party member as the one it played before (a swap while a joiner catches up)
///   --sosonline-joinafter=S       with --sosonline-play: join S seconds after its own game is playable (default 10)
///   --sosonline-loadatjoin        every PC in the session: start a level load the frame before the first join frame
///   --sosonline-nocatchup         host: joiners always drop in by a reload in place (Stage 2), never by catching up
///   --sosonline-tracecalls=C[.M],...  log calls to those game classes (or methods) with the lockstep frame
///   --sosonline-fixedlog=F1,F2    log the physics steps run in each lockstep frame from F1 to F2
///   --sosonline-dashprobe         log the battle-entry dash anticipation timers each frame (finding 8)
/// Every state snapshot is also appended to BepInEx/SosOnline/state-&lt;id&gt;.log for offline comparison.
/// </summary>
/// <summary>Per-instance copy of this plugin's log lines (several test instances share BepInEx's log file).</summary>
internal sealed class InstanceLog : BepInEx.Logging.ILogListener
{
    private readonly StreamWriter _w;
    public InstanceLog(string path) { _w = new StreamWriter(path, false) { AutoFlush = true }; }
    public BepInEx.Logging.LogLevel LogLevelFilter => BepInEx.Logging.LogLevel.All;
    public void LogEvent(object sender, BepInEx.Logging.LogEventArgs e)
    {
        if (e.Source?.SourceName == "Sea of Stars Online Co-op") lock (_w) _w.WriteLine($"[{e.Level}] {e.Data}");
    }
    public void Dispose() => _w.Dispose();
}

internal static class TestHarness
{
    private static readonly Dictionary<string, string> Args = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<(int frame, bool sys, int action, int hold, float axis)> Script = new();
    private static readonly HashSet<int> Shots = new();
    private static int _quitFrame = -1;
    private static int _guests = 1;
    private static string _startSlot;
    private static double _titleSince = -1;
    private static bool _autoDone;
    private static StreamWriter _stateLog;
    private static string _scriptPath;

    public static string OutDir => Path.Combine(Paths.BepInExRootPath, "SosOnline");
    public static string InstanceId => Args.TryGetValue("udp", out var p) ? p : "steam";

    public static void Init()
    {
        foreach (var a in Environment.GetCommandLineArgs())
        {
            if (!a.StartsWith("--sosonline-", StringComparison.OrdinalIgnoreCase)) continue;
            var body = a.Substring("--sosonline-".Length);
            int eq = body.IndexOf('=');
            Args[eq < 0 ? body : body[..eq]] = eq < 0 ? "" : body[(eq + 1)..];
        }
        if (Args.Count == 0) return;
        Plugin.Log.LogInfo("[test] args: " + string.Join(" ", Args.Select(kv => $"{kv.Key}={kv.Value}")));

        if (Args.TryGetValue("udp", out var port))
        {
            Session.Instance.UseUdp(ushort.Parse(port));
            // Test instances never touch the real saves, nor each other's.
            SaveMirror.UseSandbox($"SosOnlineTest-{port}");
            SaveMirror.MirrorFolder = $"SosOnlineSession-{port}";
            SpikeB.CopyIntoSandbox();
        }
        else
        {
            // Every test run plays from a copy: a title-screen test that started a game from "Slot 1" overwrote the
            // player's real autosave twice (2026-11-16).
            SaveMirror.UseSandbox("SosOnlineTest-steam");
            SaveMirror.MirrorFolder = "SosOnlineSession-steam";
        }
        if (Args.TryGetValue("fakeupdate", out var fu)) SosOnline.Update.AutoUpdate.FakeDownloaded(Version.Parse(fu));
        if (Args.TryGetValue("fakeavailable", out var fa)) SosOnline.Update.AutoUpdate.FakeAvailable(Version.Parse(fa));
        if (Args.TryGetValue("delay", out var d)) Session.Instance.ConfiguredDelay = int.Parse(d);
        if (Args.TryGetValue("guests", out var g)) _guests = int.Parse(g);
        if (Args.TryGetValue("start", out var s)) _startSlot = s;
        if (Args.TryGetValue("quit", out var q)) _quitFrame = int.Parse(q);
        if (Args.TryGetValue("shots", out var shots))
            foreach (var f in shots.Split(',', StringSplitOptions.RemoveEmptyEntries)) Shots.Add(int.Parse(f));
        Args.TryGetValue("script", out _scriptPath);
        if (Args.ContainsKey("nocomp")) TimingCompensation.Enabled = false;
        if (Args.ContainsKey("noclock")) GameClock.Enabled = false;
        if (Args.ContainsKey("nocatchup")) Session.Instance.CatchUpEnabled = false;
        if (Args.TryGetValue("botoffset", out var bo)) CombatBot.ImpactOffset = int.Parse(bo);
        if (Args.TryGetValue("botphase", out var bp)) CombatBot.Phase = int.Parse(bp);
        if (Args.ContainsKey("castprobe") || Args.ContainsKey("callprobe")) Lockstep.CombatTrace.CastProbe = true;
        if (Args.TryGetValue("readprobe", out var rp)) { var r = rp.Split(','); Lockstep.InputOverride.ReadProbeFrom = int.Parse(r[0]); Lockstep.InputOverride.ReadProbeTo = int.Parse(r[1]); }
        if (Args.TryGetValue("botswitch", out var bsw)) CombatBot.SwitchEvery = int.Parse(bsw);
        if (Args.TryGetValue("lag", out var lag)) Net.UdpTransport.LagMs = int.Parse(lag);
        if (Args.TryGetValue("jitter", out var jit)) Net.UdpTransport.JitterMs = int.Parse(jit);
        if (Args.TryGetValue("spikes", out var spk)) Net.UdpTransport.SpikePercent = int.Parse(spk);
        if (Args.TryGetValue("stateevery", out var se)) StateSync.Interval = Math.Max(1, int.Parse(se));

        Directory.CreateDirectory(OutDir);
        SpikeB.Init();
        FastForward.Init();
        BepInEx.Logging.Logger.Listeners.Add(new InstanceLog(Path.Combine(OutDir, $"log-{InstanceId}.txt")));
        _stateLog = new StreamWriter(Path.Combine(OutDir, $"state-{InstanceId}.log"), false) { AutoFlush = true };
    }

    private static int _uiStep;
    private static double _uiAt;

    /// <summary>--sosonline-uitest: screenshots of the title screen, the Online popup and the friends popup.</summary>
    private static void UiTest()
    {
        if (!GameBridge.AtTitleScreen) return;
        double now = Time.realtimeSinceStartupAsDouble;
        if (_uiStep == 0) { _uiStep = 1; _uiAt = now + 5; return; }
        if (now < _uiAt) return;
        var title = GameBridge.FindTitleScreen();
        switch (_uiStep)
        {
            case 1: title.ShowChoices(); _uiAt = now + 2; break;
            case 2: Shot("ui-title"); _uiAt = now + 1; break;
            case 3: UI.NativeMenus.OpenTitleMenu(); _uiAt = now + 2; break;
            case 4: Shot("ui-online"); _uiAt = now + 1; break;
            case 5: UI.NativeMenus.ClickEntry("Host"); _uiAt = now + 5; break;
            case 6: Shot("ui-hosting"); _uiAt = now + 1; break;
            case 7: UI.NativeMenus.ClickEntry("Start"); _uiAt = now + 2; break;
            case 8: Shot("ui-saves"); Plugin.Log.LogInfo("[test] saves: " + string.Join(" | ", GameBridge.ListSaveSlots().Select(s => s.label))); _uiAt = now + 1; break;
            case 9: Plugin.Log.LogInfo($"[test] lobby={Session.Instance.LobbyId} phase={Session.Instance.Phase}"); Session.Instance.Leave(); _uiAt = now + 2; break;
            case 10: Shot("ui-left"); _uiAt = now + 1; break;
            case 11: UI.NativeMenus.OpenPauseMenuForTest(); _uiAt = now + 2; break;
            case 12: Shot("ui-popup"); _uiAt = now + 1; break;
            case 13: Plugin.Log.LogInfo("[test] ui test done"); Application.Quit(); break;
        }
        _uiStep++;
    }

    /// <summary>--sosonline-titleoptions: open the game's Options from the title screen, log each second, quit.</summary>
    private static void TitleOptionsTest()
    {
        double now = Time.realtimeSinceStartupAsDouble;
        if (_toStep == 0) { if (!GameBridge.AtTitleScreen) return; _toStep = 1; _toAt = now + 5; Plugin.Log.LogInfo("[test] title screen: options test starts in 5 s"); return; }
        if (now < _toAt) return;
        var title = GameBridge.FindTitleScreen();
        if (_toStep == 1) { title?.ShowChoices(); Plugin.Log.LogInfo("[test] title choices shown"); _toAt = now + 2; }
        else if (_toStep == 2) { Plugin.Log.LogInfo("[test] opening Options"); title?.OnOptions(); Plugin.Log.LogInfo("[test] OnOptions returned"); _toAt = now + 1; }
        else if (_toStep < 13) { Plugin.Log.LogInfo($"[test] options open, {_toStep - 2} s"); if (_toStep == 5) Shot("ui-options"); _toAt = now + 1; }
        else { Plugin.Log.LogInfo("[test] options test done"); Application.Quit(); }
        _toStep++;
    }
    /// <summary>--sosonline-updatecheck: Online page → Check for updates (answers via --sosonline-popupanswer), quit after 90 s.</summary>
    private static void UpdateCheckTest()
    {
        double now = Time.realtimeSinceStartupAsDouble;
        if (_ucStep == 0) { if (!GameBridge.AtTitleScreen) return; _ucStep = 1; _ucAt = now + 5; return; }
        if (now < _ucAt) return;
        if (_ucStep == 1) { GameBridge.FindTitleScreen()?.ShowChoices(); _ucAt = now + 2; }
        else if (_ucStep == 2) { UI.NativeMenus.OpenTitleMenu(); _ucAt = now + 2; }
        else if (_ucStep == 3) { Plugin.Log.LogInfo("[test] choosing Check for updates"); UI.NativeMenus.ClickEntry("Check for updates"); _ucAt = now + 90; }
        else { Plugin.Log.LogInfo("[test] update check test done"); Application.Quit(); }
        _ucStep++;
    }
    /// <summary>
    /// --sosonline-titleflow: Online > Host > Start > first save (the menu must go at once), then back to the title
    /// (only the game's own entries), then Online again. Logs the title list at each step; screenshots ui-*.
    /// </summary>
    private static void TitleFlowTest()
    {
        double now = Time.realtimeSinceStartupAsDouble;
        if (now < _tfAt) return;
        string State() => UI.NativeMenus.TitleMenuState();
        switch (_tfStep)
        {
            case 0: if (!GameBridge.AtTitleScreen) return; _tfAt = now + 5; break;
            case 1: GameBridge.FindTitleScreen()?.ShowChoices(); _tfAt = now + 2; break;
            case 2: Plugin.Log.LogInfo($"[test] title: {State()}"); UI.NativeMenus.OpenTitleMenu(); _tfAt = now + 2; break;
            // What the game does when it shows the title again (back from a game): only its own entries must remain.
            case 3: Plugin.Log.LogInfo($"[test] online: {State()}"); GameBridge.FindTitleScreen()?.ShowChoices(); _tfAt = now + 2; break;
            case 4: Plugin.Log.LogInfo($"[test] title choices shown again with Online open: {State()}"); Shot("ui-title-back"); _tfAt = now + 1; break;
            case 5: UI.NativeMenus.OpenTitleMenu(); _tfAt = now + 2; break;
            case 6: Plugin.Log.LogInfo($"[test] online again: {State()}"); UI.NativeMenus.ClickEntry("Host"); _tfAt = now + 6; break;
            case 7: Plugin.Log.LogInfo($"[test] hosting: {State()}"); UI.NativeMenus.ClickEntry("Start"); _tfAt = now + 2; break;
            case 8: Plugin.Log.LogInfo($"[test] saves: {State()}"); UI.NativeMenus.ClickEntry(0); Plugin.Log.LogInfo($"[test] save chosen, same frame: {State()}"); _tfAt = now + 0.3; break;
            case 9: Plugin.Log.LogInfo($"[test] save chosen, 0.3 s: {State()}"); Shot("ui-start-chosen"); _tfAt = now + 8; break;
            default: Plugin.Log.LogInfo("[test] title flow test done"); Session.Instance.Leave(); Application.Quit(); _tfStep = 99; return;
        }
        _tfStep++;
    }
    private static int _tfStep;
    private static double _tfAt;
    private static int _ucStep;
    private static int _vsStep;
    private static double _vsAt;

    /// <summary>--sosonline-versionshot: the title choices, a screenshot (ui-version), quit. Never opens a save.</summary>
    private static void VersionShotTest()
    {
        double now = Time.realtimeSinceStartupAsDouble;
        if (_vsStep == 0) { if (!GameBridge.AtTitleScreen) return; _vsStep = 1; _vsAt = now + 5; return; }
        if (now < _vsAt) return;
        if (_vsStep == 1) { GameBridge.FindTitleScreen()?.ShowChoices(); _vsAt = now + 2; }
        else if (_vsStep == 2) { Plugin.Log.LogInfo($"[test] version line: {UI.VersionLabel.Text}"); Shot("ui-version"); _vsAt = now + 1; }
        else { Plugin.Log.LogInfo("[test] version shot done"); Application.Quit(); }
        _vsStep++;
    }
    private static double _ucAt;
    private static int _toStep;
    private static double _toAt;

    private static void Shot(string name) => ScreenCapture.CaptureScreenshot(Path.Combine(OutDir, $"{name}-{InstanceId}.png"));

    /// <summary>Called from the plugin's Update.</summary>
    private static bool _xrefDone, _connectedOnce;

    private static bool _sawLockstep;
    private static int _quitAtUnityFrame = -1;
    private static int _runsEnded;
    private static bool _runEndCounted;

    /// <summary>
    /// --sosonline-quit counts lockstep frames, which stop when the session ends (End session, Leave, a drop-out):
    /// count the frames that were left in Unity frames instead, so the test still ends.
    /// </summary>
    private static void QuitAfterLockstep()
    {
        if (_quitFrame < 0) return;
        var engine = Session.Instance.Engine;
        if (engine.Running)
        {
            if (!_sawLockstep) _sawLockstep = true;
            _quitAtUnityFrame = -1;
            _runEndCounted = false;
            return;
        }
        // Never in a session (a joiner that found no host): give up after a few minutes.
        if (!_sawLockstep)
        {
            if (Time.realtimeSinceStartup > 300) { Plugin.Log.LogWarning("[test] no session after 300 s; quitting"); Application.Quit(); _quitFrame = -1; }
            return;
        }
        if (!_runEndCounted) { _runEndCounted = true; _runsEnded++; }
        // --sosonline-quitruns=N: a host that goes back to solo keeps going until N runs have ended.
        if (Args.TryGetValue("quitruns", out var qr) && _runsEnded < int.Parse(qr)) return;
        if (_quitAtUnityFrame < 0)
        {
            int left = Math.Max(60, _quitFrame - engine.Frame);
            _quitAtUnityFrame = Time.frameCount + left;
            Plugin.Log.LogInfo($"[test] lockstep stopped at frame {engine.Frame}; quitting in {left} frames");
            return;
        }
        if (Time.frameCount < _quitAtUnityFrame) return;
        Plugin.Log.LogInfo("[test] quitting after the session ended");
        _stateLog?.Flush();
        SpikeB.Close();
        FastForward.Close();
        Application.Quit();
        _quitFrame = -1;
    }

    public static void Update()
    {
        QuitAfterLockstep();
        if (!_xrefDone && Args.ContainsKey("xref") && Time.frameCount > 60) { _xrefDone = true; XrefProbe.Run(); Application.Quit(); }
        if (Args.ContainsKey("uitest")) UiTest();
        if (Args.ContainsKey("titleoptions")) TitleOptionsTest();
        UI.NativeMenus.TestAnswerTick();
        if (Args.ContainsKey("updatecheck")) UpdateCheckTest();
        if (Args.ContainsKey("versionshot")) VersionShotTest();
        if (Args.ContainsKey("titleflow")) TitleFlowTest();
        if (Args.Count == 0 || _autoDone) return;
        var session = Session.Instance;

        // A host with --sosonline-play plays its own game, with the lobby open (Stage 1: friends join it there).
        if (Args.ContainsKey("host") && Args.ContainsKey("play") && !_hostPlaying) _hostPlaying = OwnGameDone();
        if (Args.ContainsKey("host") && !session.InSession && !_connectedOnce) { _connectedOnce = true; session.Host(); }
        if (Args.TryGetValue("join", out var hostPort) && !session.InSession && !_connectedOnce && OwnGameDone()) { _connectedOnce = true; session.JoinUdp(ushort.Parse(hostPort)); }

        if (_startSlot != null && session.IsHost && session.Phase == Phase.Lobby)
        {
            if (GameBridge.AtTitleScreen)
            {
                if (_titleSince < 0) _titleSince = Time.realtimeSinceStartupAsDouble;
                bool guestsIn = session.Peers.Count() >= _guests;
                if (guestsIn && Time.realtimeSinceStartupAsDouble - _titleSince > 4)
                {
                    bool auto = _startSlot.EndsWith("a");
                    int slot = int.Parse(_startSlot.TrimEnd('a'));
                    Plugin.Log.LogInfo($"[test] auto-start from slot {slot} auto={auto}");
                    session.StartFromSave(slot, auto);
                    _autoDone = true;
                }
            }
            else _titleSince = -1;
        }
    }

    private static double _ownGameSince = -1, _ownGamePlayableSince = -1;
    private static bool _hostPlaying;

    /// <summary>
    /// --sosonline-play: the guest plays its own game before joining (drop-in from gameplay rather than from the
    /// title screen). True once it has played for --sosonline-joinafter seconds (always true without the flag).
    /// </summary>
    private static bool OwnGameDone()
    {
        if (!Args.TryGetValue("play", out var slotArg)) return true;
        double now = Time.realtimeSinceStartupAsDouble;
        if (_ownGameSince < 0)
        {
            if (!GameBridge.AtTitleScreen) { _titleSince = -1; return false; }
            if (_titleSince < 0) _titleSince = now;
            if (now - _titleSince < 4) return false;
            _ownGameSince = now;
            bool auto = slotArg.EndsWith("a");
            int slot = int.Parse(slotArg.TrimEnd('a'));
            Plugin.Log.LogInfo($"[test] playing own game from slot {slot} auto={auto} before joining");
            GameBridge.EnterGameFromSlot(slot, auto);
            return false;
        }
        bool playable = GameBridge.Booted && !GameBridge.AtTitleScreen && GameBridge.Party?.Leader != null;
        if (!playable) { _ownGamePlayableSince = -1; return false; }
        if (_ownGamePlayableSince < 0) { _ownGamePlayableSince = now; Plugin.Log.LogInfo($"[test] own game playable, level {GameBridge.CurrentLevelId}"); }
        double after = Args.TryGetValue("joinafter", out var ja) ? double.Parse(ja, CultureInfo.InvariantCulture) : 10;
        if (now - _ownGamePlayableSince < after) return false;
        Plugin.Log.LogInfo($"[test] joining from own game (leader at {GameBridge.Party?.Leader?.transform.position})");
        return true;
    }

    public static bool TraceEnabled => Args.ContainsKey("trace");
    /// <summary>Started with any --sosonline-* flag (a test run): no release lookup at start.</summary>
    public static bool HasArgs => Args.Count > 0;

    public static bool TryArg(string key, out string value) => Args.TryGetValue(key, out value);

    /// <summary>Test input filter (scripts, bots), chained after the session's own filter.</summary>
    public static Func<int, int, InputSnapshot, InputSnapshot> Filter;

    public static void Attach(LockstepEngine engine)
    {
        if (SpikeB.Replaying)
        {
            // Spike B: every input comes from the recording, nothing from scripts or bots.
            Filter = SpikeB.Filter;
            return;
        }
        bool bot = Args.TryGetValue("bot", out var botMode);
        CombatBot.PerfectPresses = bot && botMode != "menu";
        if (_scriptPath == null && !bot) return;
        if (_scriptPath != null) LoadScript();
        Filter = (frame, channel, snap) =>
        {
            if (_scriptPath != null) snap = ApplyScript(frame, channel, snap);
            if (bot && channel != InputOverride.SystemChannel) snap = CombatBot.Apply(frame, snap);
            return snap;
        };
    }

    private static void LoadScript()
    {
        Script.Clear();
        if (!File.Exists(_scriptPath)) { Plugin.Log.LogError($"[test] script not found: {_scriptPath}"); return; }
        foreach (var raw in File.ReadAllLines(_scriptPath))
        {
            var line = raw.Split('#')[0].Trim();
            if (line.Length == 0) continue;
            var t = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            int frame = int.Parse(t[0]);
            bool sys = t.Count > 1 && t[1] == "sys";
            if (sys) t.RemoveAt(1);
            int action = ActionTable.IndexOf(t[1]);
            if (action < 0) { Plugin.Log.LogError($"[test] unknown action '{t[1]}'. Known: {string.Join(", ", ActionTable.Names)}"); continue; }
            int hold = t.Count > 2 ? int.Parse(t[2]) : 1;
            float axis = t.Count > 3 ? float.Parse(t[3], CultureInfo.InvariantCulture) : 0f;
            Script.Add((frame, sys, action, hold, axis));
        }
        Plugin.Log.LogInfo($"[test] loaded {Script.Count} scripted inputs");
    }

    private static InputSnapshot ApplyScript(int frame, int channel, InputSnapshot snap)
    {
        bool sysChannel = channel == InputOverride.SystemChannel;
        foreach (var e in Script)
        {
            if (e.sys != sysChannel) continue;
            int rel = frame - e.frame;
            if (rel < 0 || rel > e.hold) continue;
            if (rel < e.hold)
            {
                if (e.axis < 0)
                {
                    snap.Bits[e.action] |= ActionBits.NegButton | (rel == 0 ? ActionBits.NegButtonDown : 0);
                }
                else
                {
                    snap.Bits[e.action] |= ActionBits.Button | (rel == 0 ? ActionBits.ButtonDown : 0) | ActionBits.Repeating;
                }
                float v = e.axis == 0 ? 1f : e.axis;
                snap.Axis[e.action] = InputSnapshot.Quantize(v);
                snap.AxisRaw[e.action] = InputSnapshot.Quantize(v);
            }
            else if (e.axis >= 0)
            {
                snap.Bits[e.action] |= ActionBits.ButtonUp | (e.hold < 15 ? ActionBits.ShortPress : 0);
            }
        }
        return snap;
    }

    /// <summary>Starts the closest triggerable encounter. Runs on the same frame on every peer.</summary>
    internal static void StartNearestEncounter()
    {
        var leader = GameBridge.Party?.Leader;
        if (leader == null) { Plugin.Log.LogWarning("[test] no party leader"); return; }
        var lp = leader.transform.position;
        var encounters = UnityEngine.Object.FindObjectsOfType<Encounter>()
            .Where(e => e != null)
            .OrderBy(e => (e.transform.position - lp).sqrMagnitude).ThenBy(e => e.gameObject.name)
            .ToList();
        Plugin.Log.LogInfo($"[test] encounters: {string.Join(", ", encounters.Select(e => $"{e.gameObject.name}@{(e.transform.position - lp).magnitude:F1}m trig={e.CanTrigger()}"))}");
        var target = encounters.FirstOrDefault(e => e.CanTrigger()) ?? encounters.FirstOrDefault();
        if (target == null) return;
        Plugin.Log.LogInfo($"[test] initiating encounter {target.gameObject.name}");
        target.InitiateEncounter(leader);
    }

    /// <summary>
    /// --sosonline-hpdiverge fired on this PC since its lockstep run started: the snapshot's "test" component says so,
    /// a difference no correction removes (HP alone is corrected in place since 2026-11-16).
    /// </summary>
    public static bool Diverged;
    private static readonly HashSet<int> _diverged = new();

    private static bool AtList(string flag, int frame)
    {
        if (!Args.TryGetValue(flag, out var v)) return false;
        foreach (var part in v.Split(',')) if (int.TryParse(part, out int f) && f == frame) return true;
        return false;
    }

    /// <summary>Deliberate divergence for testing the state check: this PC only.</summary>
    private static void DivergeHp(int frame, string flag)
    {
        var ids = GameBridge.Party?.CurrentParty;
        if (ids == null || ids.Count == 0) { Plugin.Log.LogWarning($"[test] {flag}: no party"); return; }
        var data = GameBridge.Stats?.GetCharacterData(ids[0]);
        if (data == null) { Plugin.Log.LogWarning($"[test] {flag}: no character data"); return; }
        int hp = data.currentHP;
        data.SetCurrentHP(hp - 1);
        Plugin.Log.LogInfo($"[test] {flag} at {frame}: {ids[0].characterId} HP {hp} -> {data.currentHP}");
    }

    public static void OnSnapshot(StateSnapshot s)
    {
        _stateLog?.WriteLine($"{s.Hash():X8} {s}");
    }

    // --sosonline-npcprobe: every frame, the active NPCs and every NPC's activation chain (its first inactive
    // ancestor), logged when they change, with Unity's own frame count. Why a replaying joiner sees an NPC early.
    private static string _npcs, _npcChain;
    private static void NpcProbe(int frame)
    {
        try
        {
            string now = Lockstep.StateSnapshot.CaptureNpcs();
            var chain = new System.Collections.Generic.List<string>();
            foreach (var n in Resources.FindObjectsOfTypeAll<NPCController>())
            {
                if (n == null || !n.gameObject.scene.IsValid()) continue;
                string off = "";
                for (var t = n.transform; t != null; t = t.parent) if (!t.gameObject.activeSelf) { off = t.name; break; }
                chain.Add($"{n.gameObject.name}:{(n.enabled ? "on" : "off")}{(off.Length > 0 ? "/inactive@" + off : "")}");
            }
            chain.Sort(StringComparer.Ordinal);
            string c = string.Join(" ", chain);
            if (now != _npcs || c != _npcChain)
                Plugin.Log.LogInfo($"[npcprobe] f{frame} (unity frame {UnityEngine.Time.frameCount - Lockstep.GameClock.FrameBase}) active [{now}] all [{c}]");
            _npcs = now;
            _npcChain = c;
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[npcprobe] {e.Message}"); }
    }

    /// <summary>Logs the sprites and fonts of every visible game screen (to style the mod's own UI like the game's).</summary>
    private static void ProbeUi(int frame)
    {
        foreach (var view in UnityEngine.Object.FindObjectsOfType<View>())
        {
            if (view == null || !view.gameObject.activeInHierarchy) continue;
            Plugin.Log.LogInfo($"[uiprobe] f{frame} view {view.GetIl2CppType().Name}");
            foreach (var img in view.GetComponentsInChildren<UnityEngine.UI.Image>(false))
            {
                var sp = img.sprite;
                var rt = img.rectTransform.rect;
                Plugin.Log.LogInfo($"[uiprobe]   img {NodePath(img.transform, view.transform)} sprite={sp?.name} tex={sp?.texture?.name} border={sp?.border} type={img.type} color={img.color} size={rt.width:F0}x{rt.height:F0} ppu={sp?.pixelsPerUnit} mult={img.pixelsPerUnitMultiplier}");
            }
            foreach (var t in view.GetComponentsInChildren<TMPro.TextMeshProUGUI>(false))
                Plugin.Log.LogInfo($"[uiprobe]   txt {NodePath(t.transform, view.transform)} font={t.font?.name} mat={t.fontSharedMaterial?.name} size={t.fontSize} color={t.color} '{Snip(t.text)}'");
        }
        var canvas = Manager<UIManager>.instance?.canvasInstance;
        if (canvas != null) Plugin.Log.LogInfo($"[uiprobe] canvas {canvas.renderMode} scale={canvas.scaleFactor} ref={canvas.GetComponent<UnityEngine.UI.CanvasScaler>()?.referenceResolution} size={canvas.pixelRect}");
    }

    private static string Snip(string text)
    {
        text = (text ?? "").Replace('\n', ' ');
        return text.Length > 40 ? text[..40] : text;
    }

    private static string NodePath(Transform t, Transform root)
    {
        var parts = new List<string>();
        for (; t != null && t != root; t = t.parent) parts.Add(t.name);
        parts.Reverse();
        return string.Join("/", parts);
    }

    private static bool At(string flag, int frame) => Args.TryGetValue(flag, out var v) && int.TryParse(v, out int f) && f == frame;

    /// <summary>
    /// --sosonline-campat=F: on frame F (pass it to every instance) the nearest campfire is used as if P1 pressed
    /// Interact at it, which opens the game's Camp / Cook / Rest choice. Scripted input picks from there.
    /// </summary>
    private static void Camp(int frame)
    {
        try
        {
            var spots = UnityEngine.Object.FindObjectsOfType<CampingSpot>();
            var leader = GameBridge.Party?.Leader;
            CampingSpot best = null;
            float bestD = float.MaxValue;
            foreach (var spot in spots)
            {
                float d = leader == null ? 0 : (spot.transform.position - leader.transform.position).magnitude;
                if (d < bestD) { bestD = d; best = spot; }
            }
            Plugin.Log.LogInfo($"[test] f{frame} camp: {spots.Length} campfire(s) in this area{(best != null ? $", nearest {bestD:F1} m (multiplayer {best.AllowMultiplayer}, inn {best.isInn})" : "")}");
            best?.OnCampingFireInteraction(GameBridge.PlayerForSlot(0));
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[test] camp failed: {e.Message}"); }
    }

    /// <summary>--sosonline-campstart=F / campstop=F: the game's own start and stop of camping at the nearest campfire.</summary>
    private static void CampForce(int frame, bool start)
    {
        try
        {
            var camp = Manager<CampingManager>.instance;
            if (camp == null) { Plugin.Log.LogWarning("[test] no camping manager"); return; }
            var leader = GameBridge.Party?.Leader;
            var spot = UnityEngine.Object.FindObjectsOfType<CampingSpot>()
                .OrderBy(s => leader == null ? 0 : (s.transform.position - leader.transform.position).sqrMagnitude).FirstOrDefault();
            // The campfire's own toggle plays the intro (party sits down) or the outro; CampingManager.StartCamping
            // only sets the camping flag.
            Plugin.Log.LogInfo($"[test] f{frame} {(start ? "start" : "stop")} camping at {(spot == null ? "no campfire" : spot.name)}");
            spot?.ToggleCamping();
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[test] camping {(start ? "start" : "stop")} failed: {e.Message}"); }
    }

    private static string _lastCampState;

    /// <summary>The camping state, logged on change, to compare the PCs frame by frame.</summary>
    private static void CampState(int frame)
    {
        string state;
        try
        {
            var camp = Manager<CampingManager>.instance;
            state = camp == null ? "no camping manager" : $"camping {camp.IsCamping}";
        }
        catch (Exception e) { state = "error " + e.Message; }
        if (state == _lastCampState) return;
        _lastCampState = state;
        Plugin.Log.LogInfo($"[test] f{frame} {state}");
    }

    private static bool _wasLoading = true;
    private static string _lastLevel = "";
    private static readonly bool[] _wasPlaying = new bool[InputOverride.SlotCount];
    private static int _reloadSlot = -1;

    /// <summary>Logs level-loading and co-op playing transitions with their lockstep frames.</summary>
    private static void TrackTransitions(int frame)
    {
        bool loading = GameBridge.LoadingLevel;
        if (loading != _wasLoading)
        {
            string lvl = GameBridge.CurrentLevelId;
            Plugin.Log.LogInfo($"[load] f{frame} loading={loading} level={lvl}{(lvl != _lastLevel ? " (changed)" : "")}");
            if (!loading) _lastLevel = lvl;
            _wasLoading = loading;
        }
        for (int s = 0; s < InputOverride.SlotCount; s++)
        {
            bool p = GameBridge.IsPlaying(s);
            if (p != _wasPlaying[s]) { Plugin.Log.LogInfo($"[load] f{frame} P{s + 1} playing={p}"); _wasPlaying[s] = p; }
        }
    }

    private static int _lastFixedSteps = -1;

    /// <summary>--sosonline-fixedlog=FROM,TO: physics steps run in each lockstep frame from FROM to TO.</summary>
    private static void LogFixedSteps(int frame)
    {
        if (!Args.TryGetValue("fixedlog", out var v)) return;
        var range = v.Split(',');
        int steps = _lastFixedSteps < 0 ? -1 : OnlineBehaviour.FixedSteps - _lastFixedSteps;
        _lastFixedSteps = OnlineBehaviour.FixedSteps;
        if (frame >= int.Parse(range[0]) && frame <= int.Parse(range[1]))
            Plugin.Log.LogInfo($"[fixed] f{frame} {steps} step(s)");
    }

    private static PartySpawn _reloadParty;

    /// <summary>
    /// --sosonline-reload=F: spike A. Autosave at F; at F+120 reload it in place. With --sosonline-reloadpos the
    /// party respawns where it stands at F+120, and at F+240 every member is put back on its own position.
    /// </summary>
    private static void ReloadSpike(int frame)
    {
        if (!Args.TryGetValue("reload", out var v) || !int.TryParse(v, out int f)) return;
        bool restore = Args.ContainsKey("reloadpos");
        if (frame == f)
        {
            _reloadSlot = GameBridge.AutoSave();
            Plugin.Log.LogInfo($"[reload] f{frame} autosave -> slot {_reloadSlot}, leader at {GameBridge.Party?.Leader?.transform.position}");
        }
        if (frame == f + 120)
        {
            if (GameBridge.Saving) Plugin.Log.LogError($"[reload] f{frame} still saving");
            _reloadParty = restore ? GameBridge.CaptureParty() : null;
            Plugin.Log.LogInfo($"[reload] f{frame} reloading slot {_reloadSlot} in place, spawn {(_reloadParty != null ? _reloadParty.ToString() : "checkpoint")}");
            if (_reloadSlot >= 0 && GameBridge.ReloadInPlace(_reloadSlot, true, _reloadParty?.Leader, _reloadParty?.Angle ?? -1)) Session.Instance.RearmAutoJoin();
        }
        if (frame == f + 240 && _reloadParty != null)
        {
            if (GameBridge.LoadingLevel) { Plugin.Log.LogError($"[reload] f{frame} still loading, positions not restored"); return; }
            Plugin.Log.LogInfo($"[reload] f{frame} restored positions: {GameBridge.RestoreParty(_reloadParty)}");
        }
    }

    public static void OnFrame(int frame)
    {
        var session = Session.Instance;
        TrackTransitions(frame);
        LogFixedSteps(frame);
        // --sosonline-saveby=SLOT: 10 frames after the save screen opened, that player has the menu controls (in real
        // play the player who uses the save point gets them).
        if (Args.TryGetValue("saveat", out var sa) && int.TryParse(sa, out int saFrame) && frame == saFrame + 10
            && Args.TryGetValue("saveby", out var by) && int.TryParse(by, out int bySlot))
        {
            Game.GameBridge.Coop?.SetUIInputsPlayer(Game.GameBridge.PlayerForSlot(bySlot));
            Plugin.Log.LogInfo($"[test] f{frame} menu controls to slot {bySlot}");
        }
        if (At("saveat", frame))
        {
            // Every PC on this frame: open the nearest save point's save screen, as if the leader had used it.
            try
            {
                var leader = Game.GameBridge.Party?.Leader;
                Savepoint best = null;
                float bestD = float.MaxValue;
                foreach (var sp in UnityEngine.Object.FindObjectsOfType<Savepoint>())
                {
                    float d = leader == null ? 0 : (sp.transform.position - leader.transform.position).sqrMagnitude;
                    if (d < bestD) { bestD = d; best = sp; }
                }
                var es = UnityEngine.EventSystems.EventSystem.current;
                var mod = es?.currentInputModule;
                Plugin.Log.LogInfo($"[test] event system {es?.name}, input module {mod?.GetIl2CppType()?.FullName}, all: {string.Join(", ", (es?.GetComponents<UnityEngine.EventSystems.BaseInputModule>() ?? new UnityEngine.EventSystems.BaseInputModule[0]).Select(m => m.GetIl2CppType().FullName))}");
                if (best != null) { best.ShowSaveScreen(); Plugin.Log.LogInfo($"[test] f{frame} save screen opened ({Mathf.Sqrt(bestD):F1} m away)"); }
                else Plugin.Log.LogWarning($"[test] f{frame} no save point");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[test] save screen failed: {e.Message}"); }
        }
        ReloadSpike(frame);
        SpikeB.OnFrame(frame);
        FastForward.OnFrame(frame);
        if (At("leave", frame)) { Plugin.Log.LogInfo($"[test] leaving at {frame}"); session.Leave(); }
        if (At("kill", frame)) { Plugin.Log.LogInfo($"[test] killing process at {frame}"); System.Diagnostics.Process.GetCurrentProcess().Kill(); }
        if (At("endsession", frame) && session.IsHost) { Plugin.Log.LogInfo($"[test] host ends the session at {frame}"); session.EndSessionScheduled("The host ended the co-op session"); }
        if (At("pauseleave", frame)) { Plugin.Log.LogInfo($"[test] synced pause-menu leave of P2 at {frame}"); session.LeaveSlotDeterministic(1); }
        if (AtList("hpdiverge", frame) && (Args.ContainsKey("hpdivergeagain") || _diverged.Add(frame))) { DivergeHp(frame, "hpdiverge"); Diverged = true; }
        if (At("hpnudge", frame)) DivergeHp(frame, "hpnudge");
        if (AtList("pausestate", frame))
            try { var pm = Manager<PauseManager>.instance; Plugin.Log.LogInfo($"[test] f{frame} pause state: paused {pm?.isPaused}, disablers {pm?.pauseDisablers}, battle {Game.GameBridge.InBattle}, can save {Game.GameBridge.CanSaveNow}"); }
            catch (Exception e) { Plugin.Log.LogWarning($"[test] pause state: {e.Message}"); }
        if (Args.ContainsKey("npcprobe")) NpcProbe(frame);
        if (At("campat", frame)) Camp(frame);
        // Camping itself, past the story unlock the campfire checks (an early save only gets "The campfire feels cozy").
        if (At("campstart", frame)) CampForce(frame, true);
        if (At("campstop", frame)) CampForce(frame, false);
        if ((Args.ContainsKey("campstart") || Args.ContainsKey("campat")) && frame % 30 == 0) CampState(frame);
        if (Args.TryGetValue("pausechoose", out var pc))
            foreach (var item in pc.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                // F:Label, "_" for a space (the test launcher splits flags on spaces).
                int colon = item.IndexOf(':');
                if (colon <= 0 || !int.TryParse(item[..colon], out int pf) || pf != frame) continue;
                string label = item[(colon + 1)..].Replace('_', ' ');
                Plugin.Log.LogInfo($"[test] f{frame} pause screen: choose '{label}' -> {(UI.LocalPause.ChooseForTest(label) ? "done" : "not on the open page")}");
            }
        if (At("uiprobe", frame)) ProbeUi(frame);
        if (At("assigntest", frame))
        {
            var pm = UnityEngine.Object.FindObjectsOfType<PauseMenu>().FirstOrDefault();
            Plugin.Log.LogInfo($"[test] opening Assign Players at {frame} (pause menu {(pm != null ? "open" : "missing")})");
            if (pm != null) pm.AssignControllers();
            else
            {
                // In co-op Start opens the local pause screen, not the game's: open the screen directly (pass the flag
                // to every instance, so it opens on the same frame everywhere).
                try
                {
                    Manager<UIManager>.instance?.InstantiateViewAsync(Il2CppInterop.Runtime.Il2CppType.Of<AssignControllersScreen>(),
                        EScreenLayers.MODAL, null, true, AnimatorUpdateMode.UnscaledTime,
                        (Action<View>)(view => view?.TryCast<AssignControllersScreen>()?.Init()));
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[test] Assign Players: {e.Message}"); }
            }
        }
        if (frame % 300 == 0 && session.Engine.Running)
        {
            var playing = Enumerable.Range(0, InputOverride.SlotCount).Where(GameBridge.IsPlaying).Select(i => $"P{i + 1}");
            Plugin.Log.LogInfo($"[test] f{frame} playing: {string.Join(",", playing)}");
        }
        if (Args.ContainsKey("assignprobe")) Diagnostics.AssignProbe(frame);
        if (Args.TryGetValue("encounter", out var ef) && int.TryParse(ef, out int encounterFrame) && frame == encounterFrame)
            StartNearestEncounter();
        if (Shots.Contains(frame))
            ScreenCapture.CaptureScreenshot(Path.Combine(OutDir, $"shot-{InstanceId}-{frame:D6}.png"));
        if (frame == _quitFrame)
        {
            Plugin.Log.LogInfo($"[timing] stats: deferred attacks {TimingCompensation.DeferredAttacks}, attack regrades {TimingCompensation.AttackRegrades}, block checks {TimingCompensation.BlockChecks}, upgrades {TimingCompensation.BlockUpgrades}, downgrades {TimingCompensation.BlockDowngrades}, look-ahead wait {Session.Instance.Engine.LookaheadWaitMs:F0} ms");
            Plugin.Log.LogInfo($"[sync] snapshot capture: {StateSnapshot.CaptureCount} taken, avg {StateSnapshot.CaptureMsTotal / Math.Max(1, StateSnapshot.CaptureCount):F2} ms, max {StateSnapshot.CaptureMsMax:F2} ms");
            Plugin.Log.LogInfo($"[test] quitting at frame {frame}");
            _stateLog?.Flush();
            SpikeB.Close();
            FastForward.Close();
            Application.Quit();
        }
    }
}
