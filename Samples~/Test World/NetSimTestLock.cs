using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon.Common;

/// <summary>
/// Ownership-request test. Click to own it, click again to lock. While locked, a remote player's
/// "Take ownership" is refused by OnOwnershipRequest (watch the ClientSim Net log).
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class NetSimTestLock : UdonSharpBehaviour
{
    [UdonSynced] private bool _locked;

    public override void Interact()
    {
        if (!Networking.IsOwner(gameObject))
        {
            Networking.SetOwner(Networking.LocalPlayer, gameObject);
            Debug.Log("[Lock] Took ownership, click again to lock");
            return;
        }
        _locked = !_locked;
        GetComponent<Renderer>().material.color = _locked ? Color.red : Color.green;
        RequestSerialization();
        Debug.Log($"[Lock] locked={_locked}");
    }

    public override bool OnOwnershipRequest(VRCPlayerApi requestingPlayer, VRCPlayerApi requestedOwner)
    {
        Debug.Log($"[Lock] OnOwnershipRequest from {requestingPlayer.displayName} -> {(_locked ? "DENY" : "allow")}");
        return !_locked;
    }

    public override void OnOwnershipTransferred(VRCPlayerApi player)
    {
        Debug.Log($"[Lock] OnOwnershipTransferred -> {player.displayName}");
        GetComponent<Renderer>().material.color = Color.white;
    }

    public override void OnDeserialization(DeserializationResult result) => Debug.Log($"[Lock] OnDeserialization locked={_locked}");
}
