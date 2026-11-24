using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

/// <summary>Not synced at all: clicking changes it only on the clicker's client. View as someone else: theirs is unchanged.</summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class NetSimShowLocalOnly : UdonSharpBehaviour
{
    private bool _on;

    public override void Interact()
    {
        _on = !_on;
        GetComponent<Renderer>().material.color = _on ? new Color(0.2f, 0.9f, 1f) : Color.gray;
        Debug.Log($"[LocalOnly] {Networking.LocalPlayer.displayName} toggled it {(_on ? "on" : "off")}: nobody else sees this");
    }
}
