using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

/// <summary>
/// Join/leave + master test. The master keeps a synced join counter; when the master leaves, the new
/// master must take over (OnMasterTransferred, object owner migrates). Add/remove remote players to drive it.
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class NetSimTestPlayers : UdonSharpBehaviour
{
    [UdonSynced] private int _totalJoins;

    public override void OnPlayerJoined(VRCPlayerApi player)
    {
        Debug.Log($"[Players] Joined {player.displayName} (id {player.playerId}) count={VRCPlayerApi.GetPlayerCount()}");
        if (!Networking.IsOwner(gameObject)) return; // only the owner (master by default) writes
        _totalJoins++;
        RequestSerialization();
    }

    public override void OnPlayerLeft(VRCPlayerApi player)
    {
        // player is still valid here but about to go away
        Debug.Log($"[Players] Left {player.displayName}; owner now {Networking.GetOwner(gameObject).displayName}");
    }

    public override void OnMasterTransferred(VRCPlayerApi newMaster) =>
        Debug.Log($"[Players] Master -> {newMaster.displayName} (local is master: {Networking.IsMaster})");

    public override void OnOwnershipTransferred(VRCPlayerApi player) =>
        Debug.Log($"[Players] Owner -> {player.displayName}");

    public override void OnDeserialization() => Debug.Log($"[Players] totalJoins={_totalJoins}");

    public override void Interact()
    {
        var players = new VRCPlayerApi[VRCPlayerApi.GetPlayerCount()];
        VRCPlayerApi.GetPlayers(players);
        string s = "";
        foreach (var p in players)
            if (Utilities.IsValid(p)) s += $"{p.displayName}{(p.isMaster ? "*" : "")}{(Networking.IsOwner(p, gameObject) ? "(owner)" : "")} ";
        Debug.Log($"[Players] {s} totalJoins={_totalJoins}");
    }
}
