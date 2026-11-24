using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon.Common;

/// <summary>Manual sync: click to take ownership and increment. Remote edits arrive via OnDeserialization.</summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class NetSimTestCounter : UdonSharpBehaviour
{
    [UdonSynced, FieldChangeCallback(nameof(Count))] private int _count;

    public int Count
    {
        get => _count;
        set
        {
            Debug.Log($"[Counter] FieldChangeCallback {_count} -> {value}");
            _count = value;
            transform.localScale = Vector3.one * (0.5f + 0.1f * Mathf.Clamp(_count, 0, 20));
        }
    }

    public override void Interact()
    {
        if (!Networking.IsOwner(gameObject)) Networking.SetOwner(Networking.LocalPlayer, gameObject);
        Count = _count + 1;
        RequestSerialization();
    }

    public override void OnPreSerialization() => Debug.Log($"[Counter] OnPreSerialization count={_count}");

    public override void OnPostSerialization(SerializationResult result) =>
        Debug.Log($"[Counter] OnPostSerialization success={result.success} bytes={result.byteCount}");

    public override void OnDeserialization(DeserializationResult result) =>
        Debug.Log($"[Counter] OnDeserialization count={_count} latency={(result.receiveTime - result.sendTime) * 1000f:0}ms");

    public override void OnOwnershipTransferred(VRCPlayerApi player) =>
        Debug.Log($"[Counter] OnOwnershipTransferred -> {player.displayName}");
}
