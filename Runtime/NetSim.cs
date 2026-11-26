using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using VRC.SDK3.ClientSim;
using VRC.SDK3.ClientSim.EncodeDecoders;
using VRC.SDK3.Data;
using VRC.SDK3.Network;
using VRC.SDK3.UdonNetworkCalling;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Common;
using VRC.Udon.Common.Interfaces;

namespace ClientSimNet
{
    /// <summary>
    /// Makes ClientSim behave like a real VRChat instance with N-1 remote players (puppets).
    /// Each remote player has their own copy of the world where their Udon runs (NetSimWorlds.cs); everything that
    /// crosses the "wire" goes through encode → bytes → delayed transport → decode into the receiver's copy.
    /// </summary>
    [DefaultExecutionOrder(10000)]
    public partial class NetSim : MonoBehaviour
    {
        public const string PrefEnabled = "ClientSimNet.Enabled";
        public const int ContinuousLimit = 200;      // ~bytes per serialization (VRChat docs)
        public const int ManualLimit = 280496;       // bytes per serialization (VRChat docs)
        public const int BandwidthLimit = 11 * 1024; // ~outbound bytes/s before VRChat starts clogging
        const float SerializeInterval = 0.1f;        // network tick for manual requests
        const float ContinuousInterval = 0.1f;       // ~10Hz continuous sync
        const float PoseInterval = 0.1f;

        public static NetSim Instance { get; private set; }

        public readonly NetSimTransport Transport = new NetSimTransport();
        public readonly List<LogEntry> Log = new List<LogEntry>();
        /// <summary>Last state each behaviour's remote copies hold (JSON of synced vars).</summary>
        public readonly Dictionary<UdonBehaviour, string> RemoteState = new Dictionary<UdonBehaviour, string>();
        public bool HooksInstalled { get; private set; }
        public bool Ready { get; private set; }
        public NetSimPov Pov { get; private set; }
        public NetSimDebugger Debugger { get; private set; }

        /// <summary>
        /// One console line, from one player's point of view (whose console it is in VRChat).
        /// kind: out (they sent) | in (they received) | lost | udon (Debug.Log on the local client) | sim (simulator note)
        /// </summary>
        public struct LogEntry { public float t; public LogType type; public string text; public UnityEngine.Object ctx; public int player; public string kind; }

        readonly ClientSimUdonEncodeDecode _codec = new ClientSimUdonEncodeDecode();
        readonly Dictionary<UdonBehaviour, int> _pendingSerialize = new Dictionary<UdonBehaviour, int>(); // copy -> sending client
        readonly Dictionary<UdonBehaviour, string> _lastSent = new Dictionary<UdonBehaviour, string>();
        Action<VRCPlayerApi, GameObject> _origSetOwner; // ClientSim's (instant, never asks OnOwnershipRequest)
        Func<VRCPlayerApi> _oLocal; Func<bool> _oIsMaster; Func<GameObject, VRCPlayerApi> _oGetOwner;
        Func<VRCPlayerApi, GameObject, bool> _oIsOwner, _oPIsOwner;
        Action<VRCPlayerApi, Vector3, Quaternion> _oTp;
        Func<VRCPlayerApi, Component, Component> _oFindPO;
        readonly Dictionary<GameObject, int> _lastOwner = new Dictionary<GameObject, int>();
        readonly Dictionary<GameObject, NetSimPuppet> _pendingOwner = new Dictionary<GameObject, NetSimPuppet>(); // puppet already owns it on its own client
        readonly Dictionary<(UdonBehaviour, string), double> _eventPacing = new Dictionary<(UdonBehaviour, string), double>();
        List<UdonBehaviour> _synced = new List<UdonBehaviour>();
        float _nextSerialize, _nextContinuous, _nextRefresh, _nextBandwidthWarn, _snapshotAt = -1;
        Action _restoreProxy;

        public static double Now => Time.realtimeSinceStartupAsDouble;

        // ---------------------------------------------------------------- lifecycle

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (PlayerPrefs.GetInt(PrefEnabled, 1) == 0) return;
            var go = new GameObject("__ClientSimNet");
            DontDestroyOnLoad(go);
            go.AddComponent<NetSim>();
        }

        void Awake()
        {
            Instance = this;
            Pov = gameObject.AddComponent<NetSimPov>();
            Debugger = gameObject.AddComponent<NetSimDebugger>();
            gameObject.AddComponent<NetSimMenuPage>();
            Pov.Changed += _ => ApplyView();
            CaptureTemplate();
            Transport.Logged += (m, status) =>
            {
                if (m.kind == "Pose") return; // too noisy
                string body = $"{m.kind,-6} {m.bytes,5} B  {m.desc}";
                switch (status)
                {
                    case "sent": AddLog(LogType.Log, $"→ {PlainName(m.to),-10} {body}", null, m.from, "out"); break;
                    case "delivered": AddLog(LogType.Log, $"← {PlainName(m.from),-10} {body}", null, m.to, "in"); break;
                    default: AddLog(LogType.Warning, $"✗ LOST → {PlainName(m.to),-10} {body}", null, m.from, "lost", echo: false); break;
                }
            };
            Application.logMessageReceived += OnUnityLog;
        }

        // Everything Udon prints runs on the local client, so it belongs in the local player's console.
        void OnUnityLog(string message, string stackTrace, LogType type)
        {
            if (message.StartsWith("[ClientSimNet]") || message.Contains("MCP-FOR-UNITY")) return;
            // ClientSim's own lines look like "[10:40:30.494][ClientSimMain] ..."
            if (message.Length > 15 && message[0] == '[' && message.IndexOf("][ClientSim", StringComparison.Ordinal) > 0)
            { AddLog(type, message, null, Transport.localId, "sim", echo: false); return; }
            AddLog(type, message, null, Ctx?.Player?.playerId ?? Transport.localId, "udon", echo: false);
        }

        void OnDestroy()
        {
            Application.logMessageReceived -= OnUnityLog;
            Uninstall();
            if (Instance == this) Instance = null;
        }

        void Install()
        {
            var original = SdkInternals.Proxy.GetValue(null);
            var mine = typeof(NetSim).GetMethod(nameof(SendCustomNetworkEventProxy), BindingFlags.Instance | BindingFlags.NonPublic);
            SdkInternals.Proxy.SetValue(null, Delegate.CreateDelegate(SdkInternals.Proxy.PropertyType, this, mine));
            // ClientSim removes its own links (and nulls its instance) on dispose. If it is already gone,
            // restoring its delegate would leak a dead handler into the next session.
            _restoreProxy = () => SdkInternals.Proxy.SetValue(null, ClientSimMain.HasInstance() ? original : null);
            UdonBehaviour.RequestSerializationHook += OnRequestSerialization;
            _um = UdonManager.Instance;
            _origSetOwner = Networking._SetOwner;
            _oLocal = Networking._LocalPlayer; _oIsMaster = Networking._IsMaster; _oGetOwner = Networking._GetOwner;
            _oIsOwner = Networking._IsOwner; _oPIsOwner = VRCPlayerApi._IsOwner; _oTp = VRCPlayerApi._TeleportTo;
            _oFindPO = Networking._FindComponentInPlayerObjects;
            Networking._FindComponentInPlayerObjects = (pl, c) => CompIn(_oFindPO(pl, Orig(c)), Ctx); // ClientSim looks up yours; each world gets its own copy
            // every Networking answer depends on whose client runs the code (see Ctx); a remote world's objects map to yours
            Networking._SetOwner = OnSetOwner;
            Networking._LocalPlayer = () => Ctx?.Player ?? _oLocal();
            Networking._IsMaster = () => { var w = Ctx; return w != null ? w.Player.isMaster : _oIsMaster(); };
            Networking._GetOwner = go => { var o = Orig(go); var w = Ctx; return w != null && OwnsOnOwnClient(w.Puppet, o) ? w.Player : _oGetOwner(o); };
            Networking._IsOwner = (pl, go) => IsOwnerOn(_oIsOwner, pl, go);
            VRCPlayerApi._IsOwner = (pl, go) => IsOwnerOn(_oPIsOwner, pl, go);
            // their client teleports them; you see it when their next pose arrives
            VRCPlayerApi._TeleportTo = (pl, pos, rot) => { var w = Ctx; if (w != null && pl == w.Player) w.Puppet.Teleport(pos, rot); else _oTp?.Invoke(pl, pos, rot); };
            _oPos = VRCPlayerApi._GetPosition; _oRot = VRCPlayerApi._GetRotation;
            VRCPlayerApi._GetPosition = PosOf; VRCPlayerApi._GetRotation = RotOf;
            PatchIsLocal(true);
            HooksInstalled = true;
            AddLog(LogType.Log, "Hooks installed (network events, RequestSerialization, per-player worlds).");
        }

        bool IsOwnerOn(Func<VRCPlayerApi, GameObject, bool> orig, VRCPlayerApi pl, GameObject go)
        {
            var o = Orig(go); var w = Ctx;
            return (w != null && pl == w.Player && OwnsOnOwnClient(w.Puppet, o)) || (orig != null && orig(pl, o));
        }

        void Uninstall()
        {
            if (!HooksInstalled) return;
            try { Possess(null); } catch (Exception e) { Debug.LogWarning($"[ClientSimNet] {e.Message}"); }
            UdonBehaviour.RequestSerializationHook -= OnRequestSerialization;
            _restoreProxy?.Invoke();
            bool alive = ClientSimMain.HasInstance();
            Networking._SetOwner = alive ? _origSetOwner : null;
            Networking._LocalPlayer = alive ? _oLocal : null; Networking._IsMaster = alive ? _oIsMaster : null; Networking._GetOwner = alive ? _oGetOwner : null;
            Networking._FindComponentInPlayerObjects = alive ? _oFindPO : null;
            Networking._IsOwner = alive ? _oIsOwner : null; VRCPlayerApi._IsOwner = alive ? _oPIsOwner : null; VRCPlayerApi._TeleportTo = alive ? _oTp : null;
            VRCPlayerApi._GetPosition = alive ? _oPos : null; VRCPlayerApi._GetRotation = alive ? _oRot : null;
            PatchIsLocal(false);
            HooksInstalled = false;
        }

        /// <summary>The SDK only exposes these to VRC.ClientSim (InternalsVisibleTo), so reach them by reflection.</summary>
        static class SdkInternals
        {
            const BindingFlags Any = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            public static readonly PropertyInfo Proxy = typeof(NetworkCalling).GetProperty("SendCustomNetworkEventProxy", Any)
                ?? throw new MissingMemberException("NetworkCalling.SendCustomNetworkEventProxy (SDK changed?)");
            public static readonly Action<VRCPlayerApi, Action> WithCaller = (Action<VRCPlayerApi, Action>)Delegate.CreateDelegate(
                typeof(Action<VRCPlayerApi, Action>),
                typeof(NetworkCalling).GetMethods(Any).First(m => m.Name == "WithNetworkCallingContext" && !m.IsGenericMethod &&
                    m.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(VRCPlayerApi), typeof(Action) })));
        }

        void Update()
        {
            // ClientSim assigns its proxy during init; replace it only after that, never combine.
            if (!HooksInstalled && ClientSimMain.HasInstance() && SdkInternals.Proxy.GetValue(null) != null)
                Install();
            if (!HooksInstalled) return;

            var local = Networking.LocalPlayer;
            if (local != null) Transport.localId = local.playerId;

            if (!Ready && Networking.IsNetworkSettled && local != null)
            {
                Ready = true;
                RefreshSynced();
                _snapshotAt = Time.unscaledTime + 0.5f; // let Start() run so master's state reflects it
            }
            if (!Ready) return;

            AttachPuppets();
            TickWorlds();
            if (_snapshotAt > 0 && Time.unscaledTime >= _snapshotAt) { _snapshotAt = -1; SendLateJoinSnapshot(); }
            if (Time.unscaledTime >= _nextRefresh) { _nextRefresh = Time.unscaledTime + 2f; RefreshSynced(); }

            Transport.Tick(Now);
        }

        void LateUpdate()
        {
            if (!Ready) return;
            float t = Time.unscaledTime;

            if (t >= _nextSerialize)
            {
                _nextSerialize = t + SerializeInterval;
                SerializePending();
            }

            if (t >= _nextContinuous)
            {
                _nextContinuous = t + ContinuousInterval;
                foreach (var ub in _synced)
                    if (ub && ub.SyncIsContinuous && ub.isActiveAndEnabled && OwnsOn(Transport.localId, ub.gameObject))
                        SerializeFrom(Transport.localId, ub, true);
                SerializeWorldsContinuous();
            }

            LateUpdateWorlds();
            TrackOwnership();

            int bps = Transport.LocalOutBytesPerSecond(Now);
            if (bps > BandwidthLimit && t >= _nextBandwidthWarn)
            {
                _nextBandwidthWarn = t + 2f;
                AddLog(LogType.Warning, $"Outbound {bps} B/s > ~{BandwidthLimit} B/s: in VRChat the network would clog (Networking.IsClogged) and sync would lag.");
            }
        }

        // ---------------------------------------------------------------- players

        public IEnumerable<NetSimPuppet> Puppets =>
            VRCPlayerApi.AllPlayers.Where(p => p != null && !p.isLocal && p.gameObject)
                .Select(p => p.gameObject.GetComponent<NetSimPuppet>()).Where(p => p);

        internal void AttachPuppets()
        {
            foreach (var p in VRCPlayerApi.AllPlayers)
                if (p != null && !p.isLocal && p.gameObject && !p.gameObject.GetComponent<NetSimPuppet>())
                {
                    var puppet = p.gameObject.AddComponent<NetSimPuppet>();
                    puppet.Init(this, p);
                    CreateWorld(puppet);
                }
        }

        /// <summary>displayName, "You" for the local player, or #id once they left.</summary>
        public string PlainName(int playerId)
        {
            if (playerId < 0) return "You"; // before the local player exists
            var p = VRCPlayerApi.GetPlayerById(playerId);
            if (p == null) return _names.TryGetValue(playerId, out var n) ? n : $"#{playerId}";
            _names[playerId] = p.isLocal ? "You" : p.displayName; // kept after they leave
            return _names[playerId];
        }
        readonly Dictionary<int, string> _names = new Dictionary<int, string>();

        public string Name(int playerId)
        {
            var p = VRCPlayerApi.GetPlayerById(playerId);
            return p == null ? $"[{playerId}]{PlainName(playerId)}(left)" : $"[{playerId}]{PlainName(playerId)}{(p.isLocal ? "(you)" : "")}";
        }


        void SendNet(int from, int to, string kind, Channel ch, int bytes, string desc, Action<double> onDeliver, double extraDelay = 0, GameObject obj = null)
        {
            var m = new NetMessage { from = from, to = to, channel = ch, kind = kind, bytes = bytes, desc = desc, obj = obj };
            if (onDeliver != null) m.deliver = () => onDeliver(m.sentAt);
            Transport.Send(m, Now, extraDelay);
        }

        static List<int> OtherIds(int except) =>
            VRCPlayerApi.AllPlayers.Where(p => p != null && p.playerId != except).Select(p => p.playerId).ToList();

        int RawOwnerId(GameObject orig) => orig && _oGetOwner != null ? _oGetOwner(orig)?.playerId ?? -1 : -1;

        /// <summary>Owns it on that client: the owner, or a puppet that took it and the transfer hasn't reached you yet.</summary>
        internal bool OwnsOn(int client, GameObject orig) =>
            RawOwnerId(orig) == client || (_pendingOwner.TryGetValue(orig, out var pp) && pp && pp.Player.playerId == client);

        string Who(int id) => id == Transport.localId ? "you" : PlainName(id);

        // ---------------------------------------------------------------- synced variables

        public void RefreshSynced()
        {
            _synced = FindObjectsOfType<UdonBehaviour>(true)
                .Where(u => u.SyncMethod != Networking.SyncType.None && u.SyncMetadataTable != null && WorldOf(u.gameObject) == null).ToList(); // yours; remote copies live in their worlds
            // every client loads the same scene and spawns PlayerObjects from the same template, so remotes start
            // with the same values. ponytail: seeded when first seen (refresh ~1 s), an unsent write before that counts as synced.
            if (Ready)
                foreach (var ub in _synced) if (ub && !RemoteState.ContainsKey(ub)) RemoteState[ub] = ToJson(_codec.Encode(ub));
        }

        public IReadOnlyList<UdonBehaviour> Synced => _synced;

        internal void SerializePending()
        {
            foreach (var kv in _pendingSerialize.ToArray()) if (kv.Key) SerializeFrom(kv.Value, kv.Key, false);
            _pendingSerialize.Clear();
        }

        void OnRequestSerialization(UdonBehaviour ub)
        {
            if (ub == null || ub.SyncMethod == Networking.SyncType.None) return;
            int client = ClientOf(ub);
            var orig = Orig(ub);
            if (!OwnsOn(client, orig.gameObject))
            {
                AddLog(LogType.Warning, $"RequestSerialization on '{Path(orig)}' ignored: {Who(client)} not the owner (owner: {PlainName(RawOwnerId(orig.gameObject))}). VRChat drops this silently.", orig, client);
                return;
            }
            _pendingSerialize[ub] = client; // VRChat coalesces requests and serializes on the next network tick
        }

        /// <summary>The client <paramref name="from"/> serializes its copy and sends it to everyone else's copy.</summary>
        void SerializeFrom(int from, UdonBehaviour copy, bool continuous, string json = null, int bytes = -1)
        {
            var orig = Orig(copy);
            if (json == null) { _codec.PreEncode(copy); json = ToJson(_codec.Encode(copy)); } // OnPreSerialization on their copy
            if (bytes < 0) bytes = EstimateBytes(copy);
            int limit = copy.SyncIsContinuous ? ContinuousLimit : ManualLimit;
            if (bytes > limit)
            {
                AddLog(LogType.Error, $"Serialization of '{Path(orig)}' FAILED: ≈{bytes} B > {limit} B limit for {copy.SyncMethod} sync.", orig, from);
                copy.OnPostSerialization(new SerializationResult(false, bytes));
                return;
            }
            bool changed = !_lastSent.TryGetValue(copy, out var prev) || prev != json;
            if (!continuous || changed)
            {
                _lastSent[copy] = json;
                var others = OtherIds(from);
                if (from != Transport.localId || others.Count == 0) RemoteState[orig] = json;
                var ch = copy.SyncIsContinuous ? Channel.Unreliable : Channel.Reliable;
                foreach (var to in others)
                {
                    int t = to;
                    SendNet(from, t, "Sync", ch, bytes, $"{Path(orig)} {json}", sentAt => Receive(t, orig, json, sentAt, from), 0, orig.gameObject);
                }
            }
            copy.OnPostSerialization(new SerializationResult(true, bytes));
        }

        /// <summary>A puppet (the owner) sends new synced values (edited in the window / API) from its client.</summary>
        public bool SendStateAsPuppet(NetSimPuppet puppet, UdonBehaviour ub, string json)
        {
            ub = Orig(ub);
            if (!TryParse(json, out var data, out var err)) { AddLog(LogType.Error, "Invalid JSON: " + err); return false; }
            int id = puppet.Player.playerId;
            if (!OwnsOn(id, ub.gameObject))
            {
                AddLog(LogType.Warning, $"{puppet.Player.displayName} is not the owner of '{Path(ub)}'. In VRChat a non-owner's serialization is never sent. Take ownership first.", ub, id);
                return false;
            }
            var copy = UbIn(ub, id);
            if (copy) { SetState(copy, data); SerializeFrom(id, copy, false); return true; }
            // shared object (PlayerObject): their copy is yours, so send the values without touching it
            int bytes = 4;
            foreach (var meta in ub.SyncMetadataTable.GetAllSyncMetadata())
                bytes += SizeOf(data.ContainsKey(meta.Name) ? _codec.Get(VarType(ub, meta.Name), data[meta.Name]) : ub.GetProgramVariable(meta.Name));
            SerializeFrom(id, ub, false, json, bytes);
            return true;
        }

        void SendLateJoinSnapshot()
        {
            var master = Networking.Master;
            if (master == null || master.isLocal) return;
            int me = Transport.localId;
            AddLog(LogType.Log, $"You joined late: owners send you the current state of {_synced.Count} synced behaviours.");
            foreach (var orig in _synced)
            {
                if (!orig) continue;
                int owner = RawOwnerId(orig.gameObject);
                if (owner == me) continue;
                int from = owner >= 0 && VRCPlayerApi.GetPlayerById(owner) != null ? owner : master.playerId;
                var src = UbIn(orig, from) ?? orig;
                string json = ToJson(_codec.Encode(src));
                RemoteState[orig] = json;
                var o = orig;
                SendNet(from, me, "Sync", Channel.Reliable, EstimateBytes(src), $"(late-join) {Path(orig)}", sentAt => Receive(me, o, json, sentAt, from), 0, orig.gameObject);
            }
        }

        /// <summary>State arrives at client <paramref name="to"/>: applied to their copy, then OnDeserialization there.</summary>
        void Receive(int to, UdonBehaviour orig, string json, double sentAt, int from)
        {
            if (!orig) return;
            var copy = UbIn(orig, to);
            if (!copy) { RemoteState[orig] = json; return; } // shared object: only your copy exists
            if (to != Transport.localId && WorldOfPlayer(to)?.Joined != true) return; // still loading; the join snapshot follows
            if (OwnsOn(to, orig.gameObject))
            {
                // continuous updates still in flight after a takeover are normal; a dropped manual sync is worth a warning
                AddLog(orig.SyncIsContinuous ? LogType.Log : LogType.Warning, $"Dropped state for '{Path(orig)}' from {PlainName(from)}: {Who(to)} own(s) it now (ownership race).", orig, to);
                return;
            }
            if (!TryParse(json, out var data, out _)) return;
            SetState(copy, data); // SetProgramVariable fires UdonSharp [FieldChangeCallback] (_onVarChange_ with _old_), like VRChat
            if (to != Transport.localId) RemoteState[orig] = ToJson(_codec.Encode(copy));
            if (copy.isActiveAndEnabled) copy.OnDeserialization(new DeserializationResult((float)sentAt, (float)Now, false));
        }

        void SetState(UdonBehaviour ub, DataDictionary data)
        {
            foreach (var meta in ub.SyncMetadataTable.GetAllSyncMetadata())
                if (data.ContainsKey(meta.Name)) ub.SetProgramVariable(meta.Name, _codec.Get(VarType(ub, meta.Name), data[meta.Name]));
        }

        // ---------------------------------------------------------------- network events

        void SendCustomNetworkEventProxy(IUdonEventReceiver receiver, NetworkEventTarget target, string eventName, Memory<object> parameters)
        {
            if (!(receiver is UdonBehaviour ub))
            {
                Debug.LogWarning($"Attempted to send network event {eventName}, but the IUdonEventReceiver was null or the wrong type.");
                return;
            }
            RouteEvent(ClientOf(ub), ub, target, eventName, parameters.ToArray());
        }

        /// <summary>Client <paramref name="from"/> sends an event from its copy: it runs at once there (if targeted), and on the others' copies after latency.</summary>
        void RouteEvent(int from, UdonBehaviour copy, NetworkEventTarget target, string eventName, object[] args)
        {
            if (!Validate(copy, eventName, args, out var meta)) return;
            var orig = Orig(copy);
            var go = orig.gameObject;
            var caller = VRCPlayerApi.GetPlayerById(from);
            int owner = OwnsOn(from, go) ? from : RawOwnerId(go);
            if (target == NetworkEventTarget.Owner && owner < 0) return;
            var to = VRCPlayerApi.AllPlayers.Where(p => p != null).Select(p => p.playerId).Where(id =>
                target == NetworkEventTarget.All || (target == NetworkEventTarget.Others && id != from) ||
                (target == NetworkEventTarget.Self && id == from) || (target == NetworkEventTarget.Owner && id == owner)).ToList();
            string desc = $"{target} {Path(orig)}.{eventName}({string.Join(", ", args.Select(a => a ?? "null"))})";

            // the sender runs it at once (no pacing); a shared object's copy is yours, so only you run it
            if (to.Remove(from) && (from == Transport.localId || copy != orig)) RunEvent(copy, eventName, meta, args, caller);
            if (to.Count == 0)
            {
                if (target == NetworkEventTarget.Others || target == NetworkEventTarget.All) AddLog(LogType.Log, $"Event {desc} → nobody else is here.", orig, from);
                return;
            }
            int bytes = 8 + eventName.Length * 2 + args.Sum(SizeOf);
            double delay = Pace(copy, eventName, meta);
            foreach (var id in to)
            {
                int t = id;
                SendNet(from, t, "Event", Channel.Reliable, bytes, desc, _ => RunEvent(UbIn(orig, t), eventName, meta, args, caller), delay, go);
            }
        }

        /// <summary>[NetworkCallable(maxEventsPerSecond)] pacing: excess remote sends queue on the sender.</summary>
        double Pace(UdonBehaviour ub, string eventName, NetworkCallingEntrypointMetadata meta)
        {
            if (meta == null) return 0;
            double rate = meta.MaxEventsPerSecond > 0 ? meta.MaxEventsPerSecond : 5;
            var key = (ub, eventName);
            double now = Now;
            double next = _eventPacing.TryGetValue(key, out var n) ? Math.Max(n, now) : now;
            _eventPacing[key] = next + 1.0 / rate;
            double delay = next - now;
            if (delay > 0.001)
                AddLog(LogType.Warning, $"'{eventName}' exceeds {rate}/s: queued {delay * 1000:0} ms on the sender (NetworkCallable rate limit).", ub);
            return delay;
        }

        /// <summary>A puppet sends a network event from its client (window / API).</summary>
        public void SendEventAsPuppet(NetSimPuppet puppet, UdonBehaviour ub, NetworkEventTarget target, string eventName, object[] args) =>
            RouteEvent(puppet.Player.playerId, UbIn(ub, puppet.Player.playerId) ?? Orig(ub), target, eventName, args);

        void SendFromPuppet(NetSimPuppet puppet, string kind, Channel ch, int bytes, string desc, Action<double> onDeliver, double extraDelay = 0, GameObject obj = null) =>
            SendNet(puppet.Player.playerId, Transport.localId, kind, ch, bytes, desc, onDeliver, extraDelay, obj);

        bool Validate(UdonBehaviour ub, string eventName, object[] args, out NetworkCallingEntrypointMetadata meta)
        {
            meta = null;
            if (ub.SyncMethod == Networking.SyncType.None)
            {
                Debug.LogError($"Unable to send network event to UdonBehaviour '{ub.name}' with SyncType '{ub.SyncMethod}'.", ub);
                return false;
            }
            if (!ub.TryGetEntrypointHashFromName(eventName, out _))
            {
                Debug.LogError($"Unable to send network event '{eventName}' as it does not exist on the target Udon behaviour '{ub.name}'.", ub);
                return false;
            }
            meta = ub.GetNetworkCallingMetadata(eventName);
            if (meta == null)
            {
                if (eventName.StartsWith("_"))
                {
                    Debug.LogError($"Unable to send local event '{eventName}' as an RPC. Events starting with an underscore may not be run remotely.", ub);
                    return false;
                }
                if (args.Length > 0)
                    throw new ArgumentException($"Network call to '{eventName}' had {args.Length} parameters, but no metadata was found. Are you missing a [NetworkCallable] attribute?");
                return true;
            }
            meta.ValidateOnce();
            if (meta.Parameters.Length != args.Length)
                throw new ArgumentException($"Parameter count mismatch for network call to '{meta.Name}'. Expected {meta.Parameters.Length}, got {args.Length}.");
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == null) continue;
                if (VRCUdonSyncTypeConverter.TypeToUdonType(args[i].GetType()) != meta.Parameters[i].Type)
                    throw new ArgumentException($"Parameter '{meta.Parameters[i].Name}' in network call to '{meta.Name}' had incorrect type '{args[i].GetType()}'.");
            }
            return true;
        }

        void RunEvent(UdonBehaviour ub, string eventName, NetworkCallingEntrypointMetadata meta, object[] args, VRCPlayerApi caller)
        {
            if (!ub) return;
            try
            {
                SdkInternals.WithCaller(caller, () =>
                {
                    if (meta == null)
                    {
                        // legacy events target every synced UdonBehaviour on the GameObject
                        foreach (var c in ub.GetComponents<UdonBehaviour>())
                            if (c && c.SyncMethod != Networking.SyncType.None) c.RunEventAdvanced(eventName, true);
                        return;
                    }
                    for (int i = 0; i < meta.Parameters.Length; i++)
                        ub.SetProgramVariable(meta.Parameters[i].Name, i < args.Length ? args[i] : null);
                    ub.RunEventAdvanced(eventName, true);
                });
            }
            catch (Exception e)
            {
                Debug.LogError($"Error running Udon network event '{eventName}' on {ub.name}: {e}");
            }
        }

        /// <summary>Parse a JSON array of event arguments into the types the event's parameters declare.</summary>
        public object[] ParseEventArgs(UdonBehaviour ub, string eventName, string jsonArray)
        {
            var meta = ub.GetNetworkCallingMetadata(eventName);
            if (meta == null || meta.Parameters.Length == 0) return Array.Empty<object>();
            if (!VRCJson.TryDeserializeFromJson(string.IsNullOrWhiteSpace(jsonArray) ? "[]" : jsonArray, out var tok) || tok.TokenType != TokenType.DataList)
                throw new ArgumentException("Arguments must be a JSON array, e.g. [5, \"hi\", true]");
            var list = tok.DataList;
            var result = new object[meta.Parameters.Length];
            for (int i = 0; i < result.Length; i++)
            {
                if (i >= list.Count) continue;
                Type t = VarType(ub, meta.Parameters[i].Name);
                result[i] = t == null ? null : _codec.Get(t, list[i]);
            }
            return result;
        }

        // ---------------------------------------------------------------- acting as a remote client

        /// <summary>The puppet clicks the object: its Interact runs in the puppet's own world.</summary>
        public void InteractAsPuppet(NetSimPuppet puppet, UdonBehaviour ub)
        {
            ub = Orig(ub);
            if (StillLoading(puppet, () => InteractAsPuppet(puppet, ub))) return;
            AddLog(LogType.Log, $"{puppet.Player.displayName} interacts with '{Path(ub)}'", ub, puppet.Player.playerId);
            var copy = UbIn(ub, puppet.Player.playerId);
            if (copy) copy.SendCustomEvent("_interact");
            else RunOnClient(puppet, ub.gameObject, _ => ub.SendCustomEvent("_interact"));
        }

        // ---------------------------------------------------------------- ownership

        /// <summary>True when the puppet already owns it on its own client (optimistic, not yet arrived at yours).</summary>
        public bool OwnsOnOwnClient(NetSimPuppet puppet, GameObject go) =>
            _pendingOwner.TryGetValue(go, out var p) && p == puppet;

        public void TakeOwnershipAsPuppet(NetSimPuppet puppet, GameObject go, Action onTransferred = null)
        {
            go = Orig(go);
            // VRChat runs OnOwnershipRequest on the requester first (here: the puppet's client), then on the owner
            var current = Networking.GetOwner(go);
            if (current != puppet.Player && !AllowsOn(puppet.Player, go, puppet.Player))
            {
                AddLog(LogType.Warning, $"{puppet.Player.displayName}'s own OnOwnershipRequest returned false for '{Path(go)}': SetOwner cancelled on their client.", go, puppet.Player.playerId);
                return;
            }
            _pendingOwner[go] = puppet;
            SendFromPuppet(puppet, "Owner", Channel.Reliable, 8, $"{puppet.Player.displayName} takes {Path(go)}", _ =>
            {
                if (_pendingOwner.TryGetValue(go, out var p) && p == puppet) _pendingOwner.Remove(go);
                if (!go || !Utilities.IsValid(puppet.Player)) return;
                var owner = Networking.GetOwner(go);
                if (owner != null && owner != puppet.Player && !AllowsOn(owner, go, puppet.Player))
                {
                    AddLog(LogType.Warning, $"{puppet.Player.displayName} asked for '{Path(go)}': {(owner.isLocal ? "your" : owner.displayName + "'s")} OnOwnershipRequest returned false, ownership denied.", go, owner.playerId);
                    return;
                }
                var pickup = go.GetComponent<VRC_Pickup>();
                if (pickup && Utilities.IsValid(pickup.currentPlayer) && pickup.currentPlayer.isLocal)
                {
                    if (pickup.DisallowTheft)
                    {
                        AddLog(LogType.Warning, $"{puppet.Player.displayName} tried to take '{go.name}' from your hand, but DisallowTheft is on.", go, puppet.Player.playerId);
                        return;
                    }
                    ClientSimPickupHelper.ForceDrop(pickup);
                }
                _origSetOwner(puppet.Player, go);
                onTransferred?.Invoke();
            }, 0, go);
        }

        /// <summary>OnOwnershipRequest evaluated on <paramref name="client"/>'s copy (their world runs their Udon).</summary>
        bool AllowsOn(VRCPlayerApi client, GameObject go, VRCPlayerApi requester)
        {
            var copy = GoIn(go, client.playerId);
            return !copy || OwnerAllows(copy, requester);
        }

        /// <summary>
        /// Every SetOwner on your client (your Udon, your hand grabbing a pickup): your OnOwnershipRequest runs first,
        /// you own it at once (optimistic, like VRChat), then the remote owner's check runs on their client after
        /// latency and a "no" takes it back.
        /// </summary>
        void OnSetOwner(VRCPlayerApi requested, GameObject go)
        {
            var w = Ctx ?? WorldOf(go); // only that player's client can touch their copy (ClientSim's hand, while you control them)
            go = Orig(go);
            if (w != null && requested == _oLocal()) requested = w.Player; // ClientSim's player is their body
            if (w != null) // a remote player's client, in their world
            {
                if (requested == w.Player) { if (!OwnsOn(w.Player.playerId, go)) TakeOwnershipAsPuppet(w.Puppet, go); }
                else AddLog(LogType.Warning, $"{w.Player.displayName} gave '{Path(go)}' to {requested?.displayName}: not simulated (only taking ownership is).", go, w.Player.playerId);
                return;
            }
            var current = go ? Networking.GetOwner(go) : null;
            // ponytail: stack check so ClientSim's leave hand-off (owner gone: VRChat skips the callback) stays direct
            if (requested == null || current == null || current == requested ||
                new System.Diagnostics.StackTrace().ToString().Contains("ClientSimSyncedObjectManager"))
            { _origSetOwner(requested, go); return; }

            var local = Networking.LocalPlayer;
            if (!OwnerAllows(go, local))
            {
                AddLog(LogType.Warning, $"Your OnOwnershipRequest returned false for '{Path(go)}': SetOwner cancelled.", go);
                return;
            }
            _origSetOwner(requested, go);
            var ownerPuppet = current.isLocal || !current.gameObject ? null : current.gameObject.GetComponent<NetSimPuppet>();
            if (!ownerPuppet) return;
            // the request travels to the owner; a denial travels back and reverts
            Transport.Send(new NetMessage
            {
                from = local.playerId, to = current.playerId, channel = Channel.Reliable, kind = "Owner", bytes = 8, desc = $"you ask {current.displayName} for {Path(go)}", obj = go,
                deliver = () =>
                {
                    if (!go || !ownerPuppet || AllowsOn(current, go, local)) return;
                    SendFromPuppet(ownerPuppet, "Owner", Channel.Reliable, 8, $"{current.displayName} denies {Path(go)}", _ =>
                    {
                        if (!go || Networking.GetOwner(go) != requested) return;
                        AddLog(LogType.Warning, $"{current.displayName}'s OnOwnershipRequest returned false: '{Path(go)}' goes back to them (you owned it for one round trip).", go);
                        _origSetOwner(current, go);
                    }, 0, go);
                }
            }, Now);
        }

        /// <summary>VRChat asks the current owner's behaviours; any false denies the transfer.</summary>
        static bool OwnerAllows(GameObject go, VRCPlayerApi requester)
        {
            foreach (var ub in go.GetComponents<UdonBehaviour>())
            {
                if (!ub.RunEvent("_onOwnershipRequest", ("requester", requester), ("newOwner", requester))) continue; // RunEvent mangles to onOwnershipRequestRequester/NewOwner; false = no handler
                if (ub.GetProgramVariable(UdonBehaviour.ReturnVariableName) is bool ok && !ok) return false;
            }
            return true;
        }

        void TrackOwnership()
        {
            foreach (var ub in _synced)
            {
                if (!ub) continue;
                var go = ub.gameObject;
                var owner = Networking.GetOwner(go);
                int id = owner?.playerId ?? -1;
                if (_lastOwner.TryGetValue(go, out var last) && last != id)
                {
                    AddLog(LogType.Log, $"Owner of '{Path(go)}': {Name(last)} → {Name(id)}", go);
                    // ClientSim tells your copy; the other worlds hear it too
                    foreach (var w in _worldByPlayer.Values)
                        if (owner != null && w.CloneOf.TryGetValue(go, out var c) && c)
                            foreach (var cu in c.GetComponents<UdonBehaviour>()) cu.RunEvent("_onOwnershipTransferred", ("player", owner));
                }
                _lastOwner[go] = id;
            }
        }

        // ---------------------------------------------------------------- helpers

        public string LocalStateJson(UdonBehaviour ub) => ToJson(_codec.Encode(ub));

        public static readonly FieldInfo ProgramField = typeof(UdonBehaviour).GetField("_program", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>
        /// A variable's declared type. Don't use GetProgramVariableType: it reads the heap slot, which becomes Object
        /// after UdonSharp FieldChangeCallback code runs, so decoding by it yields null.
        /// </summary>
        internal static Type VarType(UdonBehaviour ub, string name)
        {
            var table = (ProgramField?.GetValue(ub) as IUdonProgram)?.SymbolTable;
            return table != null && table.HasAddressForSymbol(name) ? table.GetSymbolType(name) : ub.GetProgramVariableType(name);
        }

        static string ToJson(DataDictionary d) =>
            VRCJson.TrySerializeToJson(d, JsonExportType.Minify, out DataToken json) ? json.String : "{}";


        public static bool TryParse(string json, out DataDictionary data, out string error)
        {
            data = null;
            error = null;
            if (!VRCJson.TryDeserializeFromJson(json ?? "", out DataToken tok) || tok.TokenType != TokenType.DataDictionary)
            {
                error = "expected a JSON object like {\"count\":3}";
                return false;
            }
            data = tok.DataDictionary;
            return true;
        }

        /// <summary>Approximate VRChat wire size: strings 2 B/char, primitives by width.</summary>
        public static int EstimateBytes(UdonBehaviour ub)
        {
            int total = 4;
            foreach (var meta in ub.SyncMetadataTable.GetAllSyncMetadata())
                total += SizeOf(ub.GetProgramVariable(meta.Name));
            return total;
        }

        public static int SizeOf(object o)
        {
            switch (o)
            {
                case null: return 1;
                case bool _: case byte _: case sbyte _: return 1;
                case char _: case short _: case ushort _: return 2;
                case int _: case uint _: case float _: case Color32 _: return 4;
                case long _: case ulong _: case double _: case Vector2 _: return 8;
                case Vector3 _: return 12;
                case Vector4 _: case Quaternion _: case Color _: return 16;
                case string s: return 2 + s.Length * 2;
                case VRCUrl u: return 2 + (u.Get()?.Length ?? 0) * 2;
                case Array a:
                    int sum = 2;
                    foreach (var e in a) sum += SizeOf(e);
                    return sum;
                default: return 4;
            }
        }

        public static string Path(Component c) => c ? Path(c.gameObject) : "null";
        public static string Path(GameObject go) => go ? (go.transform.parent ? go.transform.parent.name + "/" : "") + go.name : "null";

        /// <summary>player: whose console (default: local player). echo: mirror warnings/errors to the Unity console.</summary>
        public void AddLog(LogType type, string text, UnityEngine.Object ctx = null, int player = int.MinValue, string kind = "sim", bool echo = true)
        {
            Log.Add(new LogEntry { t = Time.unscaledTime, type = type, text = text, ctx = ctx, player = player == int.MinValue ? Transport.localId : player, kind = kind });
            if (Log.Count > 2000) Log.RemoveRange(0, 400);
            if (!echo) return;
            if (type == LogType.Error) Debug.LogError("[ClientSimNet] " + text, ctx);
            else if (type == LogType.Warning) Debug.LogWarning("[ClientSimNet] " + text, ctx);
        }
    }
}
