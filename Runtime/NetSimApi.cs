using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using VRC.SDK3.ClientSim;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Common.Interfaces;

namespace ClientSimNet
{
    /// <summary>
    /// One-line control surface for scripts and AI agents (e.g. unity-mcp execute_code). Play mode only.
    /// Players are named by displayName; objects by name, name prefix or hierarchy path, optionally
    /// "Name@Player" to pick the copy owned by that player (PlayerObjects). Misuse throws with a clear message.
    /// </summary>
    public static class NetSimApi
    {
        static NetSim Net => NetSim.Instance && NetSim.Instance.Ready
            ? NetSim.Instance
            : throw new InvalidOperationException("ClientSim Net is not running: enter Play mode with the simulator enabled and wait until Ready.");

        public static bool IsReady => NetSim.Instance && NetSim.Instance.Ready;

        // ------------------------------------------------------------ players

        /// <summary>Spawns a remote player and attaches its puppet immediately.</summary>
        public static NetSimPuppet AddPlayer(string name)
        {
            var net = Net;
            // VRChat display names are unique and players are looked up by name: adding an existing one is a no-op
            if (VRCPlayerApi.AllPlayers.Any(p => p != null && !p.isLocal && p.displayName == name)) { net.AttachPuppets(); return Puppet(name); }
            ClientSimMain.SpawnRemotePlayer(name);
            net.AttachPuppets();
            return Puppet(name);
        }

        public static NetSimPuppet Puppet(string name)
        {
            var net = Net;
            net.AttachPuppets();
            return net.Puppets.FirstOrDefault(p => p.Player.displayName == name)
                   ?? throw new ArgumentException($"No remote player '{name}'. Players: {string.Join(", ", VRCPlayerApi.AllPlayers.Select(p => p.displayName))}");
        }

        public static void Leave(string name) => ClientSimMain.RemovePlayer(Puppet(name).Player);

        /// <summary>Leave and join again: a fresh world with a new player id, getting the late-join snapshot.</summary>
        public static NetSimPuppet Rejoin(string name) { Leave(name); return AddPlayer(name); }

        /// <summary>The current master leaves (only works when a remote player is master).</summary>
        public static void MasterLeaves()
        {
            _ = Net;
            var m = Networking.Master;
            if (m == null || m.isLocal) throw new InvalidOperationException("The local player is master; start Play with ClientSim 'Local Player Is Master' unticked, or make a remote master first.");
            ClientSimMain.RemovePlayer(m);
        }

        /// <summary>Look through a player's eyes ("You" or your displayName = back to your own view).</summary>
        public static void View(string name, bool thirdPerson = false)
        {
            var net = Net;
            net.Pov.ThirdPerson = thirdPerson;
            var local = Networking.LocalPlayer;
            if (name == "You" || (local != null && name == local.displayName)) net.Pov.ViewLocal();
            else net.Pov.View(Puppet(name).Player.playerId);
        }

        public static void GoTo(string name, Vector3 position) => Puppet(name).GoTo(position);
        public static void SetMode(string name, NetSimPuppet.Mode mode) => Puppet(name).mode = mode;

        // ------------------------------------------------------------ actions as a remote player

        public static void TakeOwnership(string player, string obj) => Net.TakeOwnershipAsPuppet(Puppet(player), Find(obj));

        public static void Grab(string player, string obj)
        {
            var pickup = Find(obj).GetComponent<VRC_Pickup>();
            if (!pickup) throw new ArgumentException($"'{obj}' has no VRC_Pickup");
            Puppet(player).Grab(pickup);
        }

        public static void Drop(string player) => Puppet(player).Drop();
        /// <summary>Use the held pickup: OnPickupUseDown/Up run on the player's client.</summary>
        public static void Use(string player) => Puppet(player).Use();
        public static void Jump(string player) => Puppet(player).Jump();

        /// <summary>The remote player clicks the object: Interact runs on THEIR client (LocalPlayer = them);
        /// their SetOwner / RequestSerialization / events reach you with latency.</summary>
        public static void InteractAs(string player, string obj) => Net.InteractAsPuppet(Puppet(player), Udon(obj));

        /// <summary>The player (must own the object) sends synced values, e.g. {"_count":5}. Keys = UdonSharp field names.</summary>
        public static bool SendState(string player, string obj, string json) => Net.SendStateAsPuppet(Puppet(player), Udon(obj), json);

        /// <summary>target: All | Others | Owner | Self. argsJson: JSON array, e.g. [1,"hi"].</summary>
        public static void SendEvent(string player, string obj, string target, string eventName, string argsJson = "[]")
        {
            var net = Net;
            var ub = Udon(obj);
            if (!Enum.TryParse(target, true, out NetworkEventTarget t)) throw new ArgumentException($"target must be All, Others, Owner or Self, got '{target}'");
            net.SendEventAsPuppet(Puppet(player), ub, t, eventName, net.ParseEventArgs(ub, eventName, argsJson));
        }

        /// <summary>Runs Interact on the object as the local player (like clicking it).</summary>
        public static void Interact(string obj) => Udon(obj).SendCustomEvent("_interact");

        // ------------------------------------------------------------ network

        public static void SetNetwork(float latencyMs, float jitterMs = 0, float lossPercent = 0)
        {
            var t = Net.Transport;
            t.latencyMs = latencyMs; t.jitterMs = jitterMs; t.lossPercent = lossPercent;
        }

        /// <summary>Perfect | LAN | Normal | Bad (same as the window presets).</summary>
        public static void Preset(string name)
        {
            switch (name.ToLowerInvariant())
            {
                case "perfect": SetNetwork(0); break;
                case "lan": SetNetwork(10, 3); break;
                case "normal": SetNetwork(100, 30, 0.5f); break;
                case "bad": SetNetwork(300, 120, 5); break;
                default: throw new ArgumentException("Preset: Perfect | LAN | Normal | Bad");
            }
        }

        /// <summary>
        /// Serializes pending RequestSerialization calls and delivers everything in flight now (ignores latency,
        /// keeps order). Use for deterministic checks; real timing needs waiting a few frames instead.
        /// </summary>
        public static void Flush()
        {
            var net = Net;
            for (int i = 0; i < 10; i++)
            {
                net.SerializePending();
                if (net.Transport.Queued == 0) return;
                net.Transport.Tick(double.MaxValue);
            }
        }

        public static int InFlight => Net.Transport.Queued;

        // ------------------------------------------------------------ lookup

        public static GameObject Find(string spec)
        {
            if (string.IsNullOrEmpty(spec)) throw new ArgumentException("object name is empty");
            string owner = null;
            int at = spec.LastIndexOf('@');
            if (at > 0) { owner = spec.Substring(at + 1); spec = spec.Substring(0, at); }

            var all = Resources.FindObjectsOfTypeAll<Transform>()
                .Where(t => t.gameObject.scene.IsValid() && (!NetSim.Instance || NetSim.Instance.WorldOf(t.gameObject) == null)) // your copies; others' via GoIn
                .Select(t => t.gameObject)
                .Where(g => owner == null || Networking.GetOwner(g)?.displayName == owner)
                .ToList();
            var hit = Pick(all.Where(g => FullPath(g) == spec || NetSim.Path(g) == spec))
                      ?? Pick(all.Where(g => g.name == spec))
                      // "Events" should find "Events (click...)" before "EventSystem"
                      ?? Pick(all.Where(g => g.name.StartsWith(spec, StringComparison.OrdinalIgnoreCase) &&
                                             (g.name.Length == spec.Length || !char.IsLetterOrDigit(g.name[spec.Length]))))
                      ?? Pick(all.Where(g => g.name.StartsWith(spec, StringComparison.OrdinalIgnoreCase)));
            return hit ? hit : throw new ArgumentException($"No GameObject matches '{spec}'{(owner != null ? $" owned by {owner}" : "")}");
        }

        /// <summary>The synced UdonBehaviour on the object (first one with sync, else first).</summary>
        public static UdonBehaviour Udon(string obj)
        {
            var ubs = Find(obj).GetComponents<UdonBehaviour>();
            return ubs.FirstOrDefault(u => u.SyncMethod != Networking.SyncType.None) ?? ubs.FirstOrDefault()
                   ?? throw new ArgumentException($"'{obj}' has no UdonBehaviour");
        }

        static GameObject Pick(IEnumerable<GameObject> gs) => gs.OrderByDescending(g => g.activeInHierarchy).FirstOrDefault();

        static string FullPath(GameObject g) => g.transform.parent ? FullPath(g.transform.parent.gameObject) + "/" + g.name : g.name;

        // ------------------------------------------------------------ report

        [Serializable] public class Report
        {
            public double time;
            public bool ready, hooksInstalled;
            /// <summary>Whose eyes the Game view shows ("You" = your own camera).</summary>
            public string pov;
            public float latencyMs, jitterMs, lossPercent;
            public int inFlight, outBytesPerSec, bandwidthLimit, warnings, errors;
            public List<PlayerInfo> players = new List<PlayerInfo>();
            public List<ObjectInfo> objects = new List<ObjectInfo>();
            public List<LogInfo> log = new List<LogInfo>();
        }

        [Serializable] public class PlayerInfo
        {
            public int id; public string name; public bool isLocal, isMaster; public string mode, holding; public Vector3 position;
        }

        [Serializable] public class ObjectInfo
        {
            public string path, behaviour, sync, owner;
            /// <summary>Synced vars as JSON: what you have / what the remotes last received.</summary>
            public string local, remote;
            /// <summary>false = remotes hold different values than you (or never received any).</summary>
            public bool inSync;
            public int bytes, limit;
            /// <summary>Network debugger: messages you/the owner sent for it in the last second; seconds since last send / receive (-1 = never).</summary>
            public int sentPerSec;
            public float lastSentAgo, lastReceivedAgo;
        }

        /// <summary>player = whose console the line is in; kind = out | in | lost | udon | sim.</summary>
        [Serializable] public class LogInfo { public float t; public string player, kind, type, text, obj; }

        /// <summary>Snapshot of players, synced objects (local vs remote state), network and the last log entries.</summary>
        public static Report GetReport(int logEntries = 50)
        {
            var net = Net;
            var r = new Report
            {
                time = Time.timeAsDouble, ready = net.Ready, hooksInstalled = net.HooksInstalled,
                latencyMs = net.Transport.latencyMs, jitterMs = net.Transport.jitterMs, lossPercent = net.Transport.lossPercent,
                inFlight = net.Transport.Queued, outBytesPerSec = net.Transport.LocalOutBytesPerSecond(NetSim.Now),
                bandwidthLimit = NetSim.BandwidthLimit,
                pov = net.Pov.IsLocal ? "You" : net.PlainName(net.Pov.PlayerId),
                warnings = net.Log.Count(e => e.type == LogType.Warning),
                errors = net.Log.Count(e => e.type == LogType.Error || e.type == LogType.Exception),
            };

            foreach (var p in VRCPlayerApi.AllPlayers.Where(p => p != null))
            {
                var puppet = p.isLocal || !p.gameObject ? null : p.gameObject.GetComponent<NetSimPuppet>();
                r.players.Add(new PlayerInfo
                {
                    id = p.playerId, name = p.displayName, isLocal = p.isLocal, isMaster = p.isMaster, position = p.GetPosition(),
                    mode = puppet ? puppet.mode.ToString() : "", holding = puppet && puppet.Held ? puppet.Held.name : "",
                });
            }

            net.RefreshSynced();
            foreach (var ub in net.Synced.Where(u => u && u.gameObject.activeInHierarchy))
            {
                string local = net.LocalStateJson(ub);
                bool hasRemote = net.RemoteState.TryGetValue(ub, out var remote);
                r.objects.Add(new ObjectInfo
                {
                    path = FullPath(ub.gameObject),
#if UNITY_EDITOR
                    behaviour = ub.programSource ? ub.programSource.name : "",
#endif
                    sync = ub.SyncMethod.ToString(),
                    owner = Networking.GetOwner(ub.gameObject)?.displayName,
                    local = local, remote = hasRemote ? remote : null, inSync = hasRemote && remote == local,
                    bytes = NetSim.EstimateBytes(ub), limit = ub.SyncIsContinuous ? NetSim.ContinuousLimit : NetSim.ManualLimit,
                    sentPerSec = net.Debugger.Stats(ub.gameObject).Out.PerSec(NetSim.Now),
                    lastSentAgo = Ago(net.Debugger.Stats(ub.gameObject).Out.Last), lastReceivedAgo = Ago(net.Debugger.Stats(ub.gameObject).In.Last),
                });
            }

            foreach (var e in net.Log.Skip(Math.Max(0, net.Log.Count - logEntries)))
                r.log.Add(new LogInfo { t = e.t, player = net.PlainName(e.player), kind = e.kind, type = e.type.ToString(), text = e.text, obj = e.ctx ? e.ctx.name : "" });
            return r;
        }

        static float Ago(double t) => t < 0 ? -1f : (float)(NetSim.Now - t);

        /// <summary>Saves the network debugger's timeline + per player / per kind totals as JSON; returns the path.</summary>
        public static string SaveStats(string path = null) => Net.Debugger.Save(path);

        /// <summary>Per player / per kind totals of the debugger timeline (no samples), ~1-2 KB.</summary>
        public static NetSimDebugger.Capture StatsSummary() => Net.Debugger.Summary();

        public static string ReportJson(int logEntries = 50, bool pretty = true) => JsonUtility.ToJson(GetReport(logEntries), pretty);

        /// <summary>Writes the report (default Library/ClientSimNet/report.json) and returns the path.</summary>
        public static string WriteReport(string path = null, int logEntries = 200)
        {
            path ??= Path.Combine(Application.dataPath, "..", "Library", "ClientSimNet", "report.json");
            path = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, ReportJson(logEntries));
            return path;
        }
    }
}
