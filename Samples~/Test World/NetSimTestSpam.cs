using UdonSharp;
using UnityEngine;
using VRC.SDK3.UdonNetworkCalling;
using VRC.SDKBase;
using VRC.Udon.Common.Interfaces;

/// <summary>
/// Event rate-limit + targets test. Click fires 20 numbered events in one frame:
/// All runs locally right away; remote copies are paced (~5/s) so they trickle out over ~4 s.
/// Also sends Others (never runs on the sender) and Self.
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)] // None can't send network events (SDK refuses)
public class NetSimTestSpam : UdonSharpBehaviour
{
    private int _expected;

    public override void Interact()
    {
        for (int i = 0; i < 20; i++) SendCustomNetworkEvent(NetworkEventTarget.All, nameof(Seq), i);
        SendCustomNetworkEvent(NetworkEventTarget.Others, nameof(OthersOnly));
        SendCustomNetworkEvent(NetworkEventTarget.Self, nameof(SelfOnly));
    }

    [NetworkCallable]
    public void Seq(int n)
    {
        string from = NetworkCalling.CallingPlayer.displayName;
        if (n != _expected && n != 0) Debug.LogWarning($"[Spam] out of order: got {n}, expected {_expected} (from {from})");
        _expected = n + 1;
        Debug.Log($"[Spam] Seq {n} from {from} at {Time.time:0.00}");
    }

    public void OthersOnly() => Debug.Log($"[Spam] OthersOnly from {NetworkCalling.CallingPlayer.displayName} (must NOT be you)");

    public void SelfOnly() => Debug.Log("[Spam] SelfOnly ran locally");
}
