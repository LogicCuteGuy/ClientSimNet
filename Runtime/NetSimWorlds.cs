using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.ClientSim;
using VRC.SDK3.Components;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Common.Delegates;

namespace ClientSimNet
{
    /// <summary>
    /// One remote player's own client: a full copy of the scene, loaded fresh when they join, that only network
    /// messages change. Their Udon runs here (Networking.LocalPlayer / isLocal / IsOwner answer for them).
    /// Viewing as them renders this copy, so anything that isn't synced looks different, like in VRChat.
    /// </summary>
    public class NetSimWorld
    {
        public NetSimPuppet Puppet;
        public VRCPlayerApi Player => Puppet ? Puppet.Player : null;
        public Scene Scene;
        public GameObject Root;
        internal readonly Dictionary<GameObject, GameObject> CloneOf = new Dictionary<GameObject, GameObject>(), OrigOf = new Dictionary<GameObject, GameObject>();
        internal readonly Dictionary<UdonBehaviour, UdonBehaviour> UbClone = new Dictionary<UdonBehaviour, UdonBehaviour>(), UbOrig = new Dictionary<UdonBehaviour, UdonBehaviour>();
        internal NetSim.Visuals Vis;
        internal (Transform orig, Transform clone)[] ObjectSyncs;
        internal int StartFrame;
        internal bool Joined;
        internal readonly List<Action> AfterJoin = new List<Action>();
        /// <summary>The other players' bodies as this client sees them (their world only has scene objects), by playerId.</summary>
        internal readonly Dictionary<int, Transform> Ghosts = new Dictionary<int, Transform>();
        /// <summary>Every player's spawned PlayerObject (your scene) → this client's own copy of it.</summary>
        internal readonly Dictionary<GameObject, GameObject> PlayerObjects = new Dictionary<GameObject, GameObject>();
    }

    public partial class NetSim
    {
        GameObject _template;
        readonly Dictionary<Transform, Transform> _templOrig = new Dictionary<Transform, Transform>();
        Visuals _mainVis;
        readonly Dictionary<int, NetSimWorld> _worldByPlayer = new Dictionary<int, NetSimWorld>();
        readonly Dictionary<GameObject, NetSimWorld> _worldByRoot = new Dictionary<GameObject, NetSimWorld>();
        GameObject[] _mainRoots = Array.Empty<GameObject>();
        UdonManager _um;
        NetSimWorld _acting; // C# acting for a player on an object their world doesn't copy (yet)
        readonly HashSet<GameObject> _mainPO = new HashSet<GameObject>();
        float _nextView;

        public IEnumerable<NetSimWorld> Worlds => _worldByPlayer.Values;
        public NetSimWorld WorldOfPlayer(int playerId) => _worldByPlayer.TryGetValue(playerId, out var w) ? w : null;
        /// <summary>By root object, not scene: worlds swap scenes while you control someone (see Possess).</summary>
        public NetSimWorld WorldOf(GameObject go) => go && _worldByRoot.TryGetValue(go.transform.root.gameObject, out var w) ? w : null;

        /// <summary>Whose client is running code right now: the world of the executing UdonBehaviour (null = yours).</summary>
        internal NetSimWorld Ctx
        {
            get
            {
                if (_acting != null) return _acting;
                var ub = _um ? _um.currentlyExecuting : null;
                return ub ? WorldOf(ub.gameObject) : null;
            }
        }

        internal int ClientOf(Component c) => WorldOf(c ? c.gameObject : null)?.Player?.playerId ?? _acting?.Player?.playerId ?? Transport.localId;

        public GameObject Orig(GameObject go) { var w = WorldOf(go); return w != null && w.OrigOf.TryGetValue(go, out var o) ? o : go; }
        public Component Orig(Component c)
        {
            if (!c) return c;
            var go = Orig(c.gameObject);
            if (go == c.gameObject) return c;
            var mine = c.gameObject.GetComponents(c.GetType()); var theirs = go.GetComponents(c.GetType());
            int i = Array.IndexOf(mine, c);
            return i >= 0 && i < theirs.Length ? theirs[i] : c;
        }

        /// <summary>The world's copy of a component of yours (same index among its type), or the component itself.</summary>
        internal static Component CompIn(Component c, NetSimWorld w)
        {
            if (!c || w == null || !w.CloneOf.TryGetValue(c.gameObject, out var go) || !go) return c;
            var mine = c.gameObject.GetComponents(c.GetType()); var theirs = go.GetComponents(c.GetType());
            int i = Array.IndexOf(mine, c);
            return i >= 0 && i < theirs.Length ? theirs[i] : c;
        }

        public UdonBehaviour Orig(UdonBehaviour ub) { var w = ub ? WorldOf(ub.gameObject) : null; return w != null && w.UbOrig.TryGetValue(ub, out var o) ? o : ub; }

        /// <summary>The given player's copy of an object (yours = the scene object). Null if their world has no copy (yet).</summary>
        public GameObject GoIn(GameObject go, int playerId)
        {
            go = Orig(go);
            if (playerId == Transport.localId) return go;
            var w = WorldOfPlayer(playerId);
            return w != null && go && w.CloneOf.TryGetValue(go, out var c) ? c : null;
        }

        public UdonBehaviour UbIn(UdonBehaviour ub, int playerId)
        {
            ub = Orig(ub);
            if (playerId == Transport.localId) return ub;
            var w = WorldOfPlayer(playerId);
            return w != null && ub && w.UbClone.TryGetValue(ub, out var c) ? c : null;
        }

        /// <summary>Runs C# on the player's client: on their copy, or (shared objects) with Networking answering for them.</summary>
        public void RunOnClient(NetSimPuppet puppet, GameObject go, Action<GameObject> body)
        {
            if (StillLoading(puppet, () => RunOnClient(puppet, go, body))) return;
            var copy = GoIn(go, puppet.Player.playerId);
            if (copy) { body(copy); return; }
            var prev = _acting;
            _acting = WorldOfPlayer(puppet.Player.playerId);
            try { body(Orig(go)); }
            finally { _acting = prev; }
        }

        /// <summary>Their Udon hasn't started yet (world joins a few frames after they do): run it once it has.</summary>
        bool StillLoading(NetSimPuppet puppet, Action retry)
        {
            var w = WorldOfPlayer(puppet.Player.playerId);
            if (w == null || w.Joined) return false;
            if (w.AfterJoin.Count == 0) AddLog(LogType.Log, $"{puppet.Player.displayName}'s client is still loading the world: their action runs once it has (a few frames).", null, puppet.Player.playerId);
            w.AfterJoin.Add(retry);
            return true;
        }

        // ---------------------------------------------------------------- template (the scene as every client loads it)

        static bool IsClientSim(Component c) => c && ((c.GetType().Namespace ?? "").StartsWith("VRC.SDK3.ClientSim") || c.GetType().Name.StartsWith("ClientSim"));

        /// <summary>Runs before any Start: copies the scene as loaded, so a joining player's world starts from the same state.</summary>
        void CaptureTemplate()
        {
            var scene = SceneManager.GetActiveScene();
            // ClientSim's own roots hold only its components; a world root it networked (root PlayerObject template, root synced object) stays
            // (the descriptor root stays yours: its world-settings Udon sets locomotion on LocalPlayer, which ClientSim refuses for puppets)
            bool ClientSimRoot(GameObject r) { var cs = r.GetComponents<Component>().Where(c => !(c is Transform)).ToArray(); return r.GetComponent<VRC_SceneDescriptor>() || (cs.Length > 0 && cs.All(IsClientSim)); }
            var roots = scene.GetRootGameObjects().Where(r => r.name != "UdonManager" && !ClientSimRoot(r)).ToArray();
            if (roots.Length == 0) return;
            _mainRoots = roots;
            // one Instantiate of everything keeps references between root objects pointing inside the copy
            var order = roots.Select(r => r.transform.GetSiblingIndex()).ToArray();
            var holder = new GameObject("__NetSimCapture");
            var keep = new GameObject("__NetSimWorldTemplate");
            keep.SetActive(false);
            keep.transform.SetParent(transform, false);
            try
            {
                foreach (var r in roots) r.transform.SetParent(holder.transform, true);
                _template = Instantiate(holder, keep.transform);
                var o = holder.GetComponentsInChildren<Transform>(true);
                var t = _template.GetComponentsInChildren<Transform>(true);
                for (int i = 1; i < o.Length && i < t.Length; i++) _templOrig[t[i]] = o[i];
            }
            finally
            {
                foreach (var r in roots) r.transform.SetParent(null, true);
                foreach (var i in Enumerable.Range(0, roots.Length).OrderBy(i => order[i])) roots[i].transform.SetSiblingIndex(order[i]);
                DestroyImmediate(holder);
            }
            _mainVis = new Visuals(_templOrig.Values.Where(x => x).Select(x => x.gameObject));
            Strip(_template);
        }

        /// <summary>What a second client must not duplicate: ClientSim internals, the descriptor, cameras, ObjectSync (follows the owner instead).</summary>
        static void Strip(GameObject t)
        {
            foreach (var go in t.GetComponentsInChildren<Transform>(true).Select(x => x.gameObject).Where(g => g.CompareTag("EditorOnly")).ToArray())
                if (go) DestroyImmediate(go);
            foreach (var c in t.GetComponentsInChildren<Component>(true).Where(c => IsClientSim(c) || c is VRCObjectSync || (c && c.GetType().Name == "PipelineManager")).ToArray())
                TryDestroy(c);
            foreach (var c in t.GetComponentsInChildren<VRC_SceneDescriptor>(true)) TryDestroy(c);
            foreach (var c in t.GetComponentsInChildren<Camera>(true)) c.enabled = false;
            foreach (var c in t.GetComponentsInChildren<AudioListener>(true)) c.enabled = false;
            foreach (var c in t.GetComponentsInChildren<UnityEngine.EventSystems.UIBehaviour>(true))
                if (c is UnityEngine.EventSystems.EventSystem || c is UnityEngine.EventSystems.BaseInputModule) c.enabled = false;
            // templates stay inactive like in your scene; each world spawns its own copies from them (SyncPlayerObjects)
            foreach (var c in t.GetComponentsInChildren<VRCPlayerObject>(true)) c.gameObject.SetActive(false);
        }

        static void TryDestroy(Component c)
        {
            try { if (c) DestroyImmediate(c); }
            catch (Exception e) { Debug.LogWarning($"[ClientSimNet] couldn't strip {c.GetType().Name} from the world template: {e.Message}"); }
        }

        // ---------------------------------------------------------------- per-player worlds

        NetSimWorld CreateWorld(NetSimPuppet puppet)
        {
            if (!_template || !_um) return null;
            var p = puppet.Player;
            var scene = SceneManager.CreateScene($"NetSim World {p.displayName} [{p.playerId}]", new CreateSceneParameters(LocalPhysicsMode.Physics3D));
            var holder = new GameObject($"{p.displayName}'s client");
            holder.SetActive(false);
            SceneManager.MoveGameObjectToScene(holder, scene);
            var root = Instantiate(_template, holder.transform);
            root.name = "World";
            var w = new NetSimWorld { Puppet = puppet, Scene = scene, Root = holder, StartFrame = Time.frameCount };
            var t = _template.GetComponentsInChildren<Transform>(true);
            var c = root.GetComponentsInChildren<Transform>(true);
            for (int i = 1; i < t.Length && i < c.Length; i++)
            {
                if (_templOrig.TryGetValue(t[i], out var o) && o) Link(w, o.gameObject, c[i].gameObject);
            }
            w.ObjectSyncs = w.CloneOf.Where(kv => kv.Key.GetComponent<VRCObjectSync>()).Select(kv => (kv.Key.transform, kv.Value.transform)).ToArray();
            w.Vis = new Visuals(w.CloneOf.Values);
            _worldByRoot[holder] = w;
            _worldByPlayer[p.playerId] = w;
            holder.SetActive(true);
            foreach (var ub in root.GetComponentsInChildren<UdonBehaviour>(true))
            {
                ub.IsNetworkingSupported = true; // scene behaviours get networking (UdonManager.OnSceneLoaded does the same)
                _um.RegisterUdonBehaviour(ub);
            }
            SyncPlayerObjects(w, SpawnedPlayerObjects().ToList());
            ApplyView();
            AddLog(LogType.Log, $"{p.displayName}'s client loaded the world ({w.UbClone.Count} UdonBehaviours run there). Only the network changes it.", null, p.playerId);
            return w;
        }

        static void Link(NetSimWorld w, GameObject o, GameObject c)
        {
            w.CloneOf[o] = c; w.OrigOf[c] = o;
            var ou = o.GetComponents<UdonBehaviour>(); var cu = c.GetComponents<UdonBehaviour>();
            for (int k = 0; k < ou.Length && k < cu.Length; k++) { w.UbClone[ou[k]] = cu[k]; w.UbOrig[cu[k]] = ou[k]; }
        }

        void TickWorlds()
        {
            var spawned = SpawnedPlayerObjects().ToList();
            if (!_mainPO.SetEquals(spawned.Select(s => s.inst)))
            {
                _mainPO.Clear(); _mainPO.UnionWith(spawned.Select(s => s.inst));
                _mainVis?.Show(true); // rebuilt below; ApplyView hides again in the same frame
                _mainVis = new Visuals(_templOrig.Values.Where(x => x).Select(x => x.gameObject)
                    .Concat(_mainPO.SelectMany(i => i.GetComponentsInChildren<Transform>(true)).Select(x => x.gameObject)));
                ApplyView();
            }
            foreach (var w in _worldByPlayer.Values.ToArray())
            {
                if (!w.Puppet || !Utilities.IsValid(w.Player)) { DestroyWorld(w); continue; }
                SyncPlayerObjects(w, spawned);
                if (!w.Joined && Time.frameCount > w.StartFrame + 2)
                {
                    w.Joined = true; JoinWorld(w);
                    foreach (var a in w.AfterJoin.ToArray()) a();
                    w.AfterJoin.Clear();
                }
            }
            if (Time.unscaledTime >= _nextView) { _nextView = Time.unscaledTime + 0.5f; ApplyView(); }
        }

        void DestroyWorld(NetSimWorld w)
        {
            if (w == _possessed) Possess(null);
            _worldByPlayer.Remove(_worldByPlayer.First(kv => kv.Value == w).Key);
            _worldByRoot.Remove(w.Root);
            if (w.Scene.isLoaded) SceneManager.UnloadSceneAsync(w.Scene);
        }

        /// <summary>VRChat: a joining client gets OnPlayerJoined for everyone (itself too), then each owner's current state.</summary>
        void JoinWorld(NetSimWorld w)
        {
            int me = w.Player.playerId;
            foreach (var p in VRCPlayerApi.AllPlayers.Where(p => p != null).OrderBy(p => p.playerId).ToArray())
                foreach (var ub in w.UbOrig.Keys.ToArray())
                    if (ub) ub.RunEvent("_onPlayerJoined", ("player", p));
            foreach (var orig in _synced)
            {
                if (!orig) continue;
                int owner = RawOwnerId(orig.gameObject);
                if (owner == me || owner < 0) continue;
                var src = UbIn(orig, owner) ?? orig;
                string json = ToJson(_codec.Encode(src));
                var o = orig;
                SendNet(owner, me, "Sync", Channel.Reliable, EstimateBytes(src), $"(late-join) {Path(orig)}", sentAt => Receive(me, o, json, sentAt, owner), 0, orig.gameObject);
            }
        }

        /// <summary>Each world has its own physics scene; step it like Unity steps yours (rigidbodies, collisions, triggers).</summary>
        void FixedUpdate()
        {
            foreach (var w in _worldByPlayer.Values)
                if (w != _possessed && w.Scene.IsValid()) w.Scene.GetPhysicsScene().Simulate(Time.fixedDeltaTime);
            if (_possessed != null && _yourHold.IsValid()) _yourHold.GetPhysicsScene().Simulate(Time.fixedDeltaTime);
        }

        /// <summary>
        /// A remote player's world holds only scene objects, so without this they'd see nobody (not you, not each other).
        /// ponytail: a capsule + name per other player, at their own-client position (no latency); shown only while you view that world.
        /// </summary>
        void UpdateGhosts()
        {
            var players = VRCPlayerApi.AllPlayers.Where(p => p != null).ToList();
            var viewed = Pov && !Pov.IsLocal ? WorldOfPlayer(Pov.PlayerId) : null;
            var cam = viewed == null ? null : Camera.allCameras.Where(c => c.enabled && (c.cullingMask & 1) != 0).OrderByDescending(c => c.depth).FirstOrDefault();
            foreach (var w in _worldByPlayer.Values)
            {
                if (!w.Root || w.Player == null) continue;
                foreach (var id in w.Ghosts.Keys.Where(id => id == w.Player.playerId || players.All(p => p.playerId != id)).ToArray())
                { if (w.Ghosts[id]) Destroy(w.Ghosts[id].gameObject); w.Ghosts.Remove(id); }
                foreach (var p in players)
                {
                    if (p == w.Player) continue;
                    if (!w.Ghosts.TryGetValue(p.playerId, out var g) || !g) w.Ghosts[p.playerId] = g = MakeGhost(w, p);
                    var pup = p.isLocal || !p.gameObject ? null : p.gameObject.GetComponent<NetSimPuppet>();
                    if (pup) g.SetPositionAndRotation(pup.SimPosition, pup.SimRotation); else g.SetPositionAndRotation(PosOf(p), RotOf(p));
                    bool show = w == viewed;
                    foreach (var r in g.GetComponentsInChildren<Renderer>()) r.forceRenderingOff = !show;
                    if (show && cam) g.GetChild(1).rotation = cam.transform.rotation;
                }
            }
        }

        static Transform MakeGhost(NetSimWorld w, VRCPlayerApi p)
        {
            var root = new GameObject($"Ghost {p.displayName}").transform;
            root.SetParent(w.Root.transform, false);
            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            DestroyImmediate(body.GetComponent<Collider>()); // a picture of the player, not a physics body
            body.transform.SetParent(root, false);
            body.transform.localPosition = new Vector3(0, 0.8f, 0);
            body.transform.localScale = new Vector3(0.5f, 0.8f, 0.5f);
            var color = Color.HSVToRGB((p.playerId * 0.37f) % 1f, 0.55f, 0.95f);
            body.GetComponent<Renderer>().material.color = color;
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var label = new GameObject("Name").AddComponent<TextMesh>();
            label.transform.SetParent(root, false);
            label.transform.localPosition = new Vector3(0, 1.85f, 0);
            label.font = font; label.GetComponent<MeshRenderer>().sharedMaterial = font.material;
            label.text = p.displayName;
            label.anchor = TextAnchor.LowerCenter; label.alignment = TextAlignment.Center;
            label.fontSize = 64; label.characterSize = 0.04f; label.color = Color.white;
            return root;
        }

        void LateUpdateWorlds()
        {
            UpdateGhosts();
            foreach (var w in _worldByPlayer.Values)
            {
                if (w.Player == null) continue;
                int id = w.Player.playerId;
                // ponytail: ObjectSync copies follow the owner's object at once (no latency/interpolation on their screen).
                // Your scene's object leads, except what the player you control owns: their copy leads then.
                bool lead = w == _possessed;
                foreach (var (o, c) in w.ObjectSyncs)
                    if (o && c) { if (lead && OwnsOn(id, o.gameObject)) Follow(o, c); else Follow(c, o); }
            }
        }

        static void Follow(Transform t, Transform src)
        {
            t.SetPositionAndRotation(src.position, src.rotation);
            if (t.TryGetComponent<Rigidbody>(out var rb) && !rb.isKinematic) { rb.velocity = src.TryGetComponent<Rigidbody>(out var s) ? s.velocity : Vector3.zero; rb.angularVelocity = Vector3.zero; }
        }

        /// <summary>~10 Hz: what each remote player owns with continuous sync leaves their client.</summary>
        void SerializeWorldsContinuous()
        {
            foreach (var w in _worldByPlayer.Values.ToArray())
            {
                if (w.Player == null || !w.Joined) continue;
                int id = w.Player.playerId;
                foreach (var kv in w.UbClone)
                {
                    var orig = kv.Key; var copy = kv.Value;
                    if (orig && copy && copy.SyncIsContinuous && copy.SyncMetadataTable != null && copy.isActiveAndEnabled && OwnsOn(id, orig.gameObject))
                        SerializeFrom(id, copy, true);
                }
            }
        }

        /// <summary>Show the world of whoever you look through; the rest stays invisible and silent.</summary>
        internal void ApplyView()
        {
            int v = Pov ? Pov.PlayerId : -1;
            var viewed = v < 0 ? null : WorldOfPlayer(v);
            _mainVis?.Show(viewed == null);
            foreach (var w in _worldByPlayer.Values) w.Vis.Show(w == viewed);
        }

        internal class Visuals
        {
            readonly Renderer[] _r; readonly Behaviour[] _b; readonly AudioSource[] _a;
            readonly HashSet<Behaviour> _hidden = new HashSet<Behaviour>();

            public Visuals(IEnumerable<GameObject> gos)
            {
                var list = gos.Where(g => g).ToList();
                _r = list.SelectMany(g => g.GetComponents<Renderer>()).ToArray();
                _b = list.SelectMany(g => g.GetComponents<Light>().Cast<Behaviour>().Concat(g.GetComponents<Canvas>())).ToArray();
                _a = list.SelectMany(g => g.GetComponents<AudioSource>()).ToArray();
            }

            public void Show(bool on)
            {
                foreach (var r in _r) if (r) r.forceRenderingOff = !on;
                foreach (var b in _b)
                {
                    if (!b) continue;
                    if (!on) { if (b.enabled) { b.enabled = false; _hidden.Add(b); } }
                    else if (_hidden.Remove(b)) b.enabled = true;
                }
                foreach (var a in _a) if (a) a.mute = !on; // ponytail: overrides a world's own mute while hidden
            }
        }

        // ---------------------------------------------------------------- PlayerObjects per world

        /// <summary>ClientSim's spawned PlayerObjects (your scene), each with the template it came from.</summary>
        static IEnumerable<(GameObject templ, GameObject inst)> SpawnedPlayerObjects()
        {
#if VRC_ENABLE_PLAYER_PERSISTENCE
            var templs = ClientSimNetworkingUtilities.GetPlayerObjectList(); // same order ClientSim spawns them in
            foreach (var p in VRCPlayerApi.AllPlayers)
            {
                var csp = p != null ? p.GetClientSimPlayer() : null;
                var insts = csp ? csp.PlayerPersistenceRootObjects : null; // Unity null, not C# null: no ?.
                if (insts == null) continue;
                for (int i = 0; i < insts.Length && i < templs.Length; i++)
                    if (insts[i] && templs[i]) yield return (templs[i].gameObject, insts[i]);
            }
#else
            yield break;
#endif
        }

        /// <summary>
        /// Every client spawns every player's PlayerObjects from its own scene's template, so each world gets its own
        /// copies (spawned from its untouched template, references point into that world), mapped to yours like scene
        /// objects: sync, events, interacts and FindComponentInPlayerObjects then reach that client's copy.
        /// </summary>
        void SyncPlayerObjects(NetSimWorld w, List<(GameObject templ, GameObject inst)> spawned)
        {
            bool changed = false;
            foreach (var inst in w.PlayerObjects.Keys.Where(k => !k).ToArray()) // its player left: ClientSim destroyed yours
            {
                var copy = w.PlayerObjects[inst];
                w.PlayerObjects.Remove(inst);
                if (!copy) continue;
                foreach (var t in copy.GetComponentsInChildren<Transform>(true))
                {
                    if (w.OrigOf.TryGetValue(t.gameObject, out var o)) { w.CloneOf.Remove(o); w.OrigOf.Remove(t.gameObject); }
                    foreach (var cu in t.GetComponents<UdonBehaviour>())
                        if (w.UbOrig.TryGetValue(cu, out var ou)) { w.UbClone.Remove(ou); w.UbOrig.Remove(cu); }
                }
                Destroy(copy);
                changed = true;
            }
            foreach (var (templ, inst) in spawned)
            {
                if (w.PlayerObjects.ContainsKey(inst) || !w.CloneOf.TryGetValue(templ, out var wt) || !wt) continue;
                var copy = Instantiate(wt, wt.transform.parent); // inactive like the template
                copy.name = inst.name;
                w.PlayerObjects[inst] = copy;
                var idx = templ.GetComponentsInChildren<Transform>(true).Select((t, i) => (t, i)).ToDictionary(x => x.t, x => x.i);
                var it = inst.GetComponentsInChildren<Transform>(true);
                var wts = wt.GetComponentsInChildren<Transform>(true);
                var cs = copy.GetComponentsInChildren<Transform>(true);
                for (int i = 0; i < wts.Length && i < cs.Length; i++)
                    if (w.OrigOf.TryGetValue(wts[i].gameObject, out var o) && idx.TryGetValue(o.transform, out var j) && j < it.Length)
                        Link(w, it[j].gameObject, cs[i].gameObject);
                copy.SetActive(true);
                foreach (var ub in copy.GetComponentsInChildren<UdonBehaviour>(true)) { ub.IsNetworkingSupported = true; _um.RegisterUdonBehaviour(ub); }
                changed = true;
            }
            if (!changed) return;
            w.ObjectSyncs = w.CloneOf.Where(kv => kv.Key && kv.Key.GetComponent<VRCObjectSync>()).Select(kv => (kv.Key.transform, kv.Value.transform)).ToArray();
            w.Vis?.Show(true); // rebuilt; ApplyView hides it again in the same frame
            w.Vis = new Visuals(w.CloneOf.Values);
            ApplyView();
        }

        /// <summary>Your scene's roots plus PlayerObjects ClientSim spawned at the root (their template is a root object).</summary>
        IEnumerable<GameObject> YourRoots() => _mainRoots.Concat(_mainPO.Where(i => i && !i.transform.parent));

        // ---------------------------------------------------------------- control a player with ClientSim's own player

        NetSimWorld _possessed;
        Scene _yourHold;
        Vector3 _yourPos; Quaternion _yourRot;
        Func<VRCPlayerApi, Vector3> _oPos; Func<VRCPlayerApi, Quaternion> _oRot;
        public NetSimWorld Possessed => _possessed;
        /// <summary>Where ClientSim's player really is (the body of whoever you control).</summary>
        internal Vector3 RigPosition => _oPos(_oLocal());
        internal Quaternion RigRotation => _oRot(_oLocal());

        /// <summary>
        /// ClientSim's player becomes that player's body: their world moves into the main scene (ClientSim's
        /// raycasts, CharacterController and pickups only see that scene), yours waits in its own physics scene.
        /// Null = back to you, where you left off.
        /// </summary>
        internal void Possess(NetSimWorld w)
        {
            if (w == _possessed) return;
            var local = _oLocal?.Invoke();
            if (!Utilities.IsValid(local)) return;
            foreach (var hand in new[] { VRC_Pickup.PickupHand.Left, VRC_Pickup.PickupHand.Right })
            {
                var held = local.GetPickupInHand(hand);
                if (held) held.Drop(); // a pickup can't stay in a hand that changes worlds
            }
            var active = SceneManager.GetActiveScene();
            if (_possessed != null)
            {
                var pw = _possessed;
                _possessed = null;
                if (pw.Puppet) { pw.Puppet.Teleport(_oPos(local), _oRot(local)); pw.Puppet.SetPossessed(false); }
                if (pw.Root && pw.Scene.IsValid()) SceneManager.MoveGameObjectToScene(pw.Root, pw.Scene);
            }
            else if (w != null) // leaving your body
            {
                _yourPos = _oPos(local); _yourRot = _oRot(local);
                if (!_yourHold.IsValid()) _yourHold = SceneManager.CreateScene("NetSim World You (while you control someone)", new CreateSceneParameters(LocalPhysicsMode.Physics3D));
                foreach (var r in YourRoots()) if (r && r.scene == active) SceneManager.MoveGameObjectToScene(r, _yourHold);
            }

            if (w == null)
            {
                foreach (var r in YourRoots()) if (r && _yourHold.IsValid() && r.scene == _yourHold) SceneManager.MoveGameObjectToScene(r, active);
                _oTp?.Invoke(local, _yourPos, _yourRot);
                AddLog(LogType.Log, "Back in your own body.");
                return;
            }
            SceneManager.MoveGameObjectToScene(w.Root, active);
            _possessed = w;
            w.Puppet.SetPossessed(true);
            _oTp?.Invoke(local, w.Puppet.SimPosition, w.Puppet.SimRotation);
            AddLog(LogType.Log, $"You control {w.Player.displayName} with ClientSim's player, in their world. You stand still meanwhile.", null, w.Player.playerId);
        }

        /// <summary>While you control someone, ClientSim's player is their body: you stay where you were.</summary>
        Vector3 PosOf(VRCPlayerApi pl)
        {
            if (_possessed == null) return _oPos(pl);
            var local = _oLocal();
            if (pl == local) return _yourPos;
            return pl == _possessed.Player && Ctx == _possessed ? _oPos(local) : _oPos(pl);
        }

        Quaternion RotOf(VRCPlayerApi pl)
        {
            if (_possessed == null) return _oRot(pl);
            var local = _oLocal();
            if (pl == local) return _yourRot;
            return pl == _possessed.Player && Ctx == _possessed ? _oRot(local) : _oRot(pl);
        }

        // ---------------------------------------------------------------- isLocal per world

        const string IsLocalExtern = "__get_isLocal__SystemBoolean";
        Dictionary<string, UdonExternDelegate> _playerApiExterns;
        UdonExternDelegate _origIsLocal;

        /// <summary>isLocal is a field Udon reads through an extern: swap that extern so it answers for the running world.</summary>
        void PatchIsLocal(bool on)
        {
            try
            {
                if (on)
                {
                    var wrapper = _um.GetWrapper();
                    var mod = wrapper.GetType().GetMethod("GetWrapperModuleByName").Invoke(wrapper, new object[] { "VRCSDKBaseVRCPlayerApi" });
                    var lazy = mod.GetType().GetField("_functionDelegates", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(mod);
                    _playerApiExterns = (Dictionary<string, UdonExternDelegate>)lazy.GetType().GetProperty("Value").GetValue(lazy);
                    _origIsLocal = _playerApiExterns[IsLocalExtern];
                    _playerApiExterns[IsLocalExtern] = (heap, a) =>
                    {
                        var p = heap.GetHeapVariable<VRCPlayerApi>(a[0]);
                        var w = Ctx;
                        heap.SetHeapVariable(a[1], w != null ? p == w.Player : p != null && p.isLocal);
                    };
                }
                else if (_playerApiExterns != null && _origIsLocal != null) _playerApiExterns[IsLocalExtern] = _origIsLocal;
            }
            catch (Exception e)
            {
                AddLog(LogType.Warning, $"Couldn't make isLocal per world (SDK changed?): remote worlds see isLocal from your side. {e.Message}");
            }
        }
    }
}
