using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon.Common;

/// <summary>
/// Late-join test: a synced on/off light. Toggle it, then rejoin late (untick "Local Player Is Master"):
/// the state must arrive through OnDeserialization, not Start.
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class NetSimTestToggle : UdonSharpBehaviour
{
    [UdonSynced] private bool _on;
    private Renderer _renderer;

    private void Start()
    {
        _renderer = GetComponent<Renderer>();
        Apply();
        Debug.Log($"[Toggle] Start on={_on} (late joiner sees default here, real value comes in OnDeserialization)");
    }

    public override void Interact()
    {
        Networking.SetOwner(Networking.LocalPlayer, gameObject);
        _on = !_on;
        Apply();
        RequestSerialization();
    }

    public override void OnDeserialization(DeserializationResult result)
    {
        Debug.Log($"[Toggle] OnDeserialization on={_on}");
        Apply();
    }

    private void Apply()
    {
        if (_renderer) _renderer.material.color = _on ? Color.yellow : Color.gray;
    }
}
