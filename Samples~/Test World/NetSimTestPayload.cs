using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon.Common;

/// <summary>
/// Size-limit test. Continuous sync caps at ~200 bytes: each click adds 20 chars to a synced string;
/// around the 5th click OnPostSerialization reports failure and remotes stop receiving updates.
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.Continuous)]
public class NetSimTestPayload : UdonSharpBehaviour
{
    [UdonSynced] private string _text = "";

    public override void Interact()
    {
        Networking.SetOwner(Networking.LocalPlayer, gameObject);
        _text += "01234567890123456789";
        Debug.Log($"[Payload] text length={_text.Length} (~{2 + _text.Length * 2} B)");
    }

    public override void OnPostSerialization(SerializationResult result)
    {
        if (!result.success) Debug.LogWarning($"[Payload] serialization FAILED at {result.byteCount} B");
    }

    public override void OnDeserialization() => Debug.Log($"[Payload] received length={_text.Length}");
}
