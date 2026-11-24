using UdonSharp;
using UnityEngine;
using VRC.SDK3.UdonNetworkCalling;
using VRC.SDKBase;
using VRC.Udon.Common.Interfaces;

/// <summary>Network events: click sends Ping to All and OwnerOnly to the owner.</summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class NetSimTestEvents : UdonSharpBehaviour
{
    private int _sent;

    public override void Interact()
    {
        _sent++;
        SendCustomNetworkEvent(NetworkEventTarget.All, nameof(Ping), _sent, "hello");
        SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(OwnerOnly));
    }

    [NetworkCallable]
    public void Ping(int n, string msg)
    {
        Debug.Log($"[Events] Ping({n}, \"{msg}\") from {NetworkCalling.CallingPlayer.displayName}");
    }

    public void OwnerOnly()
    {
        Debug.Log($"[Events] OwnerOnly ran on {Networking.LocalPlayer.displayName} (sender {NetworkCalling.CallingPlayer.displayName})");
    }
}
