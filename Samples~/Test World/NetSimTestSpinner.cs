using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

/// <summary>Continuous sync: the owner spins it, everyone else follows the synced angle.</summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.Continuous)]
public class NetSimTestSpinner : UdonSharpBehaviour
{
    [UdonSynced] private float _angle;

    private void Update()
    {
        if (Networking.IsOwner(gameObject)) _angle = (_angle + 90f * Time.deltaTime) % 360f;
        transform.rotation = Quaternion.Euler(0, _angle, 0);
    }

    public override void Interact() => Networking.SetOwner(Networking.LocalPlayer, gameObject);
}
