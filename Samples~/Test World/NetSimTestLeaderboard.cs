using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

/// <summary>
/// Reads every player's PlayerObject card with Networking.FindComponentInPlayerObjects and shows the
/// leader. Not synced itself: each client derives it from the cards, so it can't desync.
/// Click to print the table.
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class NetSimTestLeaderboard : UdonSharpBehaviour
{
    [Tooltip("The original PlayerObject card in the scene (the template ClientSim clones per player).")]
    public NetSimTestPlayerCard template;

    private VRCPlayerApi[] _players = new VRCPlayerApi[100];

    public void Refresh()
    {
        int count = VRCPlayerApi.GetPlayerCount();
        if (_players.Length < count) _players = new VRCPlayerApi[count];
        VRCPlayerApi.GetPlayers(_players);

        int best = -1;
        string leader = "-";
        string table = "";
        for (int i = 0; i < count; i++)
        {
            VRCPlayerApi p = _players[i];
            if (!Utilities.IsValid(p)) continue;
            Component c = Networking.FindComponentInPlayerObjects(p, template);
            if (!Utilities.IsValid(c)) { table += $"{p.displayName}: (no card yet) "; continue; }
            NetSimTestPlayerCard card = c.GetComponent<NetSimTestPlayerCard>();
            table += $"{p.displayName}:{card.Score} ";
            if (card.Score > best) { best = card.Score; leader = p.displayName; }
        }

        transform.localScale = Vector3.one * (0.5f + 0.05f * Mathf.Min(Mathf.Max(best, 0), 20));
        Debug.Log($"[Leaderboard] leader={leader} ({best})  {table}");
    }

    public override void Interact() => Refresh();

    public override void OnPlayerJoined(VRCPlayerApi player) => SendCustomEventDelayedFrames(nameof(Refresh), 2);

    public override void OnPlayerLeft(VRCPlayerApi player) => SendCustomEventDelayedFrames(nameof(Refresh), 2);
}
