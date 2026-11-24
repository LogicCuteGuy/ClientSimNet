using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon.Common.Interfaces;

/// <summary>
/// Pickup + ObjectSync test. Logs who holds it and who owns it. Use (click while held) fires a network event.
/// Make a remote player "Grab pickup" while you hold it: it gets stolen unless DisallowTheft is on.
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
public class NetSimTestPickup : UdonSharpBehaviour
{
    public override void OnPickup() => Debug.Log($"[Pickup] OnPickup, owner={Networking.GetOwner(gameObject).displayName}");

    public override void OnDrop() => Debug.Log("[Pickup] OnDrop");

    public override void OnPickupUseDown() => SendCustomNetworkEvent(NetworkEventTarget.All, nameof(Fire));

    public void Fire() => Debug.Log($"[Pickup] Fire! owner={Networking.GetOwner(gameObject).displayName}");

    public override void OnOwnershipTransferred(VRCPlayerApi player) =>
        Debug.Log($"[Pickup] OnOwnershipTransferred -> {player.displayName}");
}
