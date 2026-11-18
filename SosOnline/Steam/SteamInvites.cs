using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using HarmonyLib;
using SosOnline.Game;
using SosOnline.Net;
using Steamworks;

namespace SosOnline.Steam;

/// <summary>
/// Joining from Steam itself:
///  - Cold start: Steam launches the game with "+connect_lobby &lt;id&gt;" (invite accepted, or "Join Game" while the
///    game isn't running).
///  - Game running: Steam posts GameLobbyJoinRequested_t (overlay invite) or GameRichPresenceJoinRequested_t
///    ("Join Game" in the friends list). Facepunch runs Steam in manual-dispatch mode and drops callbacks nobody
///    registered for, so we observe them on their way through Facepunch's dispatcher.
/// The actual join waits until the game reaches the title screen.
/// </summary>
internal static class SteamInvites
{
    private const int GameLobbyJoinRequested = 333; // { CSteamID lobby; CSteamID friend; }
    private const int GameRichPresenceJoinRequested = 337; // { CSteamID friend; char connect[256]; }

    private static ulong _pendingLobby;

    public static void ParseCommandLine()
    {
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals("+connect_lobby", StringComparison.OrdinalIgnoreCase) && ulong.TryParse(args[i + 1], out var lobby))
            {
                _pendingLobby = lobby;
                Plugin.Log.LogInfo($"[invite] launched to join lobby {lobby}");
            }
    }

    /// <summary>Called every frame; joins a pending invite once it is safe to.</summary>
    public static void Update()
    {
        if (_pendingLobby == 0 || _asking) return;
        // From the title screen at once; from gameplay once the player confirmed (Stage 1: join from anywhere).
        if (!GameBridge.AtTitleScreen && !_confirmed)
        {
            if (!GameBridge.Booted) return;
            if (Session.Instance.Phase == Phase.Running)
            {
                // A popup is a game view: opened on this PC only, it would put this PC out of step with the session.
                UI.Toast.Show("Leave co-op first, then accept the invite");
                _pendingLobby = 0;
                return;
            }
            Ask();
            return;
        }
        if (!SteamApi.TryInit()) return;
        if (Session.Instance.InSession) Session.Instance.Leave();
        ulong lobby = _pendingLobby;
        _pendingLobby = 0;
        _confirmed = false;
        Session.Instance.JoinLobby(lobby, "your friend's game");
    }

    private static bool _asking, _confirmed;

    /// <summary>One confirmation, then the mod leaves this game for the player: the drop-in replaces it in place.</summary>
    private static void Ask()
    {
        var s = Session.Instance;
        string mine = s.InSession && s.IsHost && s.Peers.Any()
            ? "\nYour friends will continue playing without you."
            : "";
        _asking = UI.NativeMenus.Popup($"Join your friend's game?\nAny progress since your last save will be lost.{mine}",
            "Join", "Not now", i =>
            {
                _asking = false;
                if (i == 0) { _confirmed = true; Plugin.Log.LogInfo("[invite] confirmed: joining from gameplay"); }
                else { _pendingLobby = 0; Plugin.Log.LogInfo("[invite] declined"); }
            }); // not opened (another popup is up): asked again next frame
    }

    private static void OnLobbyRequest(ulong lobby)
    {
        if (lobby == 0 || lobby == Session.Instance.LobbyId) return;
        _pendingLobby = lobby;
        _confirmed = false;
    }

    private static ulong ParseConnect(string connect)
    {
        var parts = (connect ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length - 1; i++)
            if (parts[i] == "+connect_lobby" && ulong.TryParse(parts[i + 1], out var id)) return id;
        return 0;
    }

    [HarmonyPatch(typeof(Dispatch), nameof(Dispatch.ProcessCallback))]
    private static class P_ProcessCallback
    {
        private static unsafe void Prefix(Dispatch.CallbackMsg_t __0)
        {
            try
            {
                int type = (int)__0.Type;
                if (type != GameLobbyJoinRequested && type != GameRichPresenceJoinRequested) return;
                byte* data = (byte*)__0.Data;
                if (data == null) return;
                if (type == GameLobbyJoinRequested)
                {
                    ulong lobby = *(ulong*)data;
                    Plugin.Log.LogInfo($"[invite] lobby join requested: {lobby}");
                    OnLobbyRequest(lobby);
                }
                else
                {
                    string connect = Marshal.PtrToStringUTF8((IntPtr)(data + 8), Math.Max(0, Math.Min(256, __0.DataSize - 8)))?.TrimEnd('\0');
                    Plugin.Log.LogInfo($"[invite] rich presence join requested: {connect}");
                    OnLobbyRequest(ParseConnect(connect));
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[invite] {e}");
            }
        }
    }
}
