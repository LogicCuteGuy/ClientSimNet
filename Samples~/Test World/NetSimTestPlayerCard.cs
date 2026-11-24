using UdonSharp;
using UnityEngine;
using VRC.SDK3.UdonNetworkCalling;
using VRC.SDKBase;
using VRC.Udon.Common;
using VRC.Udon.Common.Interfaces;

/// <summary>
/// PlayerObject test (put on a GameObject with VRCPlayerObject). Every player gets their own copy,
/// owned by them for the whole session: "my data" without ownership fights.
///   - Click YOUR card: you are the owner, so you write directly (color + score) and serialize.
///   - Click SOMEONE ELSE'S card: you can't write their data, so you ask the owner with an Owner
///     network event (Cheer). The owner validates CallingPlayer and writes.
/// Cards line up by playerId so every copy is clickable.
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class NetSimTestPlayerCard : UdonSharpBehaviour
{
    public NetSimTestLeaderboard board;

    [UdonSynced] private int _score;
    [UdonSynced] private int _color;
    [UdonSynced] private int _cheersReceived;

    private Vector3 _origin;
    private Renderer _renderer;
    private Color[] _palette;

    public int Score => _score;

    private void Start()
    {
        _palette = new Color[] { Color.white, Color.red, Color.green, Color.blue, Color.yellow, Color.magenta };
        _renderer = GetComponent<Renderer>();
        _origin = transform.position;
        VRCPlayerApi owner = Networking.GetOwner(gameObject);
        transform.position = _origin + Vector3.right * 1.2f * (owner.playerId - 1);
        Debug.Log($"[Card] {name} belongs to {owner.displayName} (local owns: {Networking.IsOwner(gameObject)})");
        Apply();
    }

    public override void Interact()
    {
        if (Networking.IsOwner(gameObject))
        {
            _color = (_color + 1) % _palette.Length;
            _score++;
            Apply();
            RequestSerialization();
            Debug.Log($"[Card] my card: score={_score} color={_color}");
        }
        else
        {
            Debug.Log($"[Card] asking {Networking.GetOwner(gameObject).displayName} to accept a cheer");
            SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(Cheer));
        }
    }

    /// <summary>Runs on the card's owner. Other players can only ask; the owner decides.</summary>
    [NetworkCallable]
    public void Cheer()
    {
        VRCPlayerApi from = NetworkCalling.CallingPlayer;
        if (!Networking.IsOwner(gameObject)) return;                 // stale target: not ours anymore
        if (!Utilities.IsValid(from) || from.isLocal)                 // can't cheer yourself
        {
            Debug.LogWarning("[Card] Cheer rejected: self or invalid sender");
            return;
        }
        _cheersReceived++;
        _score += 2;
        Apply();
        RequestSerialization();
        Debug.Log($"[Card] cheer from {from.displayName}: score={_score} cheers={_cheersReceived}");
    }

    public override void OnDeserialization(DeserializationResult result)
    {
        Debug.Log($"[Card] {Networking.GetOwner(gameObject).displayName}'s card updated: score={_score} color={_color} cheers={_cheersReceived}");
        Apply();
    }

    private void Apply()
    {
        if (_renderer && _palette != null) _renderer.material.color = _palette[Mathf.Clamp(_color, 0, _palette.Length - 1)];
        transform.localScale = Vector3.one * (0.4f + 0.05f * Mathf.Min(_score, 20));
        if (Utilities.IsValid(board)) board.Refresh();
    }
}
