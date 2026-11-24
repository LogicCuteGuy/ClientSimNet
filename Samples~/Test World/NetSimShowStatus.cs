using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;

/// <summary>
/// Showcase sign status line: whose client this is and who owns the station's object, as THIS client sees it.
/// Not synced: view as another player (Alt+2...) and it shows their side.
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class NetSimShowStatus : UdonSharpBehaviour
{
    public GameObject target;
    public Text label;
    private float _next;

    private void Update()
    {
        if (Time.time < _next || !Utilities.IsValid(label)) return;
        _next = Time.time + 0.25f;
        VRCPlayerApi me = Networking.LocalPlayer;
        string s = $"<b>{(Utilities.IsValid(me) ? me.displayName : "?")}'s client</b>";
        if (Utilities.IsValid(target))
        {
            VRCPlayerApi owner = Networking.GetOwner(target);
            s += $"    owner: {(Utilities.IsValid(owner) ? owner.displayName : "-")}{(Networking.IsOwner(target) ? "  (me)" : "")}";
        }
        label.text = s;
    }
}
