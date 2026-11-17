using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using SosOnline.Lockstep;
using SosOnline.Net;
using SosOnline.Steam;
using SosOnline.UI;
using UnityEngine;

namespace SosOnline;

[BepInPlugin(Guid, "Sea of Stars Online Co-op", Version)]
public sealed class Plugin : BasePlugin
{
    public const string Guid = "sosonline.coop";
    public const string Version = "0.1.0";

    // Static copy of BasePlugin.Log (an instance member) so every class can log; hides it on purpose.
    internal static new ManualLogSource Log;

    public override void Load()
    {
        Log = base.Log;
        SessionLogs.Init();
        try
        {
            var harmony = new Harmony(Guid);
            harmony.PatchAll(typeof(Plugin).Assembly);
            ClassInjector.RegisterTypeInIl2Cpp<OnlineBehaviour>();
            AddComponent<OnlineBehaviour>();
            Settings.Bind(Config);
            TestHarness.Init();
            SosOnline.Update.AutoUpdate.Start();
            GameClock.Install();
            if (TestHarness.TraceEnabled) Diagnostics.InstallTrace(harmony);
            if (TestHarness.TryArg("tracecalls", out var traceClasses)) Diagnostics.InstallCallTrace(harmony, traceClasses);
            if (TestHarness.TryArg("dashprobe", out _)) Diagnostics.InstallDashProbe(harmony);
            TimingCompensation.Install(harmony);
            DeterministicRandom.Install(harmony);
            CombatTrace.Install(harmony);
            SpawnTrace.Install(harmony);
            if (TestHarness.TryArg("whichrva", out var rvas))
                foreach (var hex in rvas.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    Log.LogInfo($"[fold] rva 0x{hex}: {NativeCode.Containing(Convert.ToInt64(hex, 16))}");
            if (TestHarness.TryArg("castprobe", out _) || TestHarness.TryArg("callprobe", out _)) CombatTrace.InstallCallProbe(harmony);
            NativeCode.AuditPatches(harmony, Guid);
            SteamInvites.ParseCommandLine();
            Log.LogInfo($"Sea of Stars Online Co-op {SessionLogs.Build} loaded. Press F8 in game. Logs: {SessionLogs.Folder}");
        }
        catch (Exception e)
        {
            Log.LogError($"Failed to initialize: {e}");
        }
    }
}

/// <summary>The player's settings (BepInEx/config/sosonline.coop.cfg).</summary>
internal static class Settings
{
    private static BepInEx.Configuration.ConfigEntry<bool> _openToFriends;

    public static void Bind(BepInEx.Configuration.ConfigFile config)
    {
        _openToFriends = config.Bind("Online", "OpenToFriends", false,
            "Keep a Steam lobby open while you play, so friends can join your game from Steam at any time.");
    }

    /// <summary>A Steam lobby is open whenever this PC plays its own game; friends join from Steam, no hosting step.</summary>
    public static bool OpenToFriends
    {
        get => _openToFriends?.Value ?? false;
        set { if (_openToFriends != null) _openToFriends.Value = value; }
    }
}

/// <summary>Per-frame driver living on a hidden GameObject.</summary>
public sealed class OnlineBehaviour : MonoBehaviour
{
    public OnlineBehaviour(IntPtr ptr) : base(ptr) { }

    /// <summary>Physics steps Unity has run (diagnostics: --sosonline-fixedlog).</summary>
    internal static int FixedSteps;

    private void FixedUpdate() => FixedSteps++;

    private void Update()
    {
        try
        {
            if (!ActionTable.Ready) ActionTable.TryBuild();
            Session.Instance.Update();
            InputOverride.KeepUiFocus();
            SosOnline.Update.AutoUpdate.Update();
            Toast.Update();
            SessionLogs.Update();
            Overlay.Update();
            NativeMenus.Update();
            VersionLabel.Update();
            SteamInvites.Update();
            CoopProbe.Update();
            TestHarness.Update();
        }
        catch (Exception e)
        {
            Plugin.Log.LogError(e);
        }
    }

    private void OnGUI()
    {
        try { Overlay.OnGUI(); Toast.OnGUI(); }
        catch (Exception e) { Plugin.Log.LogError(e); }
    }
}
