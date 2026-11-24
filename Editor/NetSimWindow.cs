using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.ClientSim;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Common.Interfaces;

namespace ClientSimNet
{
    public class NetSimWindow : EditorWindow
    {
        Vector2 _scroll;
        int _actingIdx, _syncedIdx, _lastPov = -1;
        string _stateEdit = "", _eventName = "", _eventArgs = "[]";
        NetworkEventTarget _eventTarget = NetworkEventTarget.All;
        bool _pickGoTo;
        UdonBehaviour _editFor;

        [MenuItem("VRChat SDK/ClientSim Net")]
        static void Open() => GetWindow<NetSimWindow>("ClientSim Net");

        void OnEnable() { EditorApplication.update += Tick; SceneView.duringSceneGui += OnSceneGUI; }
        void OnDisable() { EditorApplication.update -= Tick; SceneView.duringSceneGui -= OnSceneGUI; }

        double _nextRepaint;
        void Tick()
        {
            if (!EditorApplication.isPlaying || EditorApplication.timeSinceStartup < _nextRepaint) return;
            _nextRepaint = EditorApplication.timeSinceStartup + 0.2;
            Repaint();
        }

        void OnSelectionChange() => Repaint();

        void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            DrawHeader();
            var net = NetSim.Instance;
            if (!EditorApplication.isPlaying)
                EditorGUILayout.HelpBox("Enter Play mode (with ClientSim on) to simulate. Tip: to join late as a non-master, untick 'Local Player Is Master' in ClientSim settings before Play.", MessageType.Info);
            else if (net == null || !net.Ready)
                EditorGUILayout.HelpBox(net == null ? "ClientSim Net is disabled or ClientSim isn't running." : "Waiting for ClientSim to be ready…", MessageType.Info);
            else
            {
                DrawNetwork(net);
                DrawPlayers(net);
                DrawSelection(net);
                DrawLog(net);
            }
            EditorGUILayout.EndScrollView();
        }

        void DrawHeader()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                bool on = PlayerPrefs.GetInt(NetSim.PrefEnabled, 1) == 1;
                bool next = EditorGUILayout.ToggleLeft("Enable (applies on next Play)", on);
                if (next != on) PlayerPrefs.SetInt(NetSim.PrefEnabled, next ? 1 : 0);
                if (GUILayout.Button("Run self-check", GUILayout.Width(110))) NetSimTransport.SelfCheck();
                using (new EditorGUI.DisabledScope(!NetSimApi.IsReady))
                    if (GUILayout.Button(new GUIContent("Copy AI report", "Writes Library/ClientSimNet/report.json and copies the JSON to the clipboard"), GUILayout.Width(110)))
                    {
                        string path = NetSimApi.WriteReport();
                        EditorGUIUtility.systemCopyBuffer = System.IO.File.ReadAllText(path);
                        Debug.Log($"[ClientSimNet] Report written to {path} and copied to clipboard.");
                    }
                if (GUILayout.Button("Build test scene", GUILayout.Width(110))) NetSimTestSceneBuilder.Build();
            }
        }

        void DrawNetwork(NetSim net)
        {
            var t = net.Transport;
            EditorGUILayout.LabelField("Network", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Perfect")) Preset(t, 0, 0, 0);
                if (GUILayout.Button("LAN")) Preset(t, 10, 3, 0);
                if (GUILayout.Button("Normal")) Preset(t, 100, 30, 0.5f);
                if (GUILayout.Button("Bad")) Preset(t, 300, 120, 5);
            }
            t.latencyMs = EditorGUILayout.Slider("Latency (ms)", t.latencyMs, 0, 1000);
            t.jitterMs = EditorGUILayout.Slider("Jitter (ms)", t.jitterMs, 0, 500);
            t.lossPercent = EditorGUILayout.Slider("Loss % (unreliable)", t.lossPercent, 0, 50);
            int bps = t.LocalOutBytesPerSecond(NetSim.Now);
            EditorGUILayout.LabelField($"In flight: {t.Queued} msgs   Your outbound: {bps} B/s {(bps > NetSim.BandwidthLimit ? "⚠ CLOGGED" : "")}");
        }

        static void Preset(NetSimTransport t, float lat, float jit, float loss) { t.latencyMs = lat; t.jitterMs = jit; t.lossPercent = loss; }

        void DrawPlayers(NetSim net)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Players", EditorStyles.boldLabel);
            foreach (var p in VRCPlayerApi.AllPlayers.ToArray())
            {
                if (p == null) continue;
                using (new EditorGUILayout.HorizontalScope())
                {
                    bool viewing = p.isLocal ? net.Pov.IsLocal : net.Pov.PlayerId == p.playerId;
                    if (GUILayout.Toggle(viewing, new GUIContent("👁", "View through this player's eyes (Game view: Alt+1..9, Alt+0 = you)"), EditorStyles.miniButton, GUILayout.Width(26)) && !viewing)
                        net.Pov.View(p.playerId);
                    EditorGUILayout.LabelField($"[{p.playerId}] {p.displayName}{(p.isLocal ? " (you)" : "")}{(p.isMaster ? " ★master" : "")}", GUILayout.MinWidth(160));
                    var puppet = p.isLocal || !p.gameObject ? null : p.gameObject.GetComponent<NetSimPuppet>();
                    if (puppet)
                    {
                        puppet.mode = (NetSimPuppet.Mode)EditorGUILayout.EnumPopup(puppet.mode, GUILayout.Width(80));
                        if (GUILayout.Button("Leave", GUILayout.Width(50))) ClientSimMain.RemovePlayer(p);
                    }
                }
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("+ Add remote player")) ClientSimMain.SpawnRemotePlayer($"Puppet {VRCPlayerApi.AllPlayers.Count}");
                var master = Networking.Master;
                GUI.enabled = master != null && !master.isLocal;
                if (GUILayout.Button("Master leaves")) ClientSimMain.RemovePlayer(master);
                GUI.enabled = true;
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("◀ POV")) net.Pov.Cycle(-1);
                if (GUILayout.Button("POV ▶")) net.Pov.Cycle(1);
                net.Pov.ThirdPerson = GUILayout.Toggle(net.Pov.ThirdPerson, "3rd person", EditorStyles.miniButton, GUILayout.Width(80));
                net.Pov.Control = GUILayout.Toggle(net.Pov.Control, "Control", EditorStyles.miniButton, GUILayout.Width(60));
            }
        }

        NetSimPuppet ActingPuppet(NetSim net)
        {
            var puppets = net.Puppets.ToArray();
            if (puppets.Length == 0)
            {
                EditorGUILayout.HelpBox("Add a remote player to act as someone else.", MessageType.None);
                return null;
            }
            if (net.Pov.PlayerId != _lastPov) // "Act as" follows the POV
            {
                _lastPov = net.Pov.PlayerId;
                int i = Array.FindIndex(puppets, p => p.Player.playerId == _lastPov);
                if (i >= 0) _actingIdx = i;
            }
            _actingIdx = Mathf.Clamp(_actingIdx, 0, puppets.Length - 1);
            _actingIdx = EditorGUILayout.Popup("Act as", _actingIdx, puppets.Select(p => $"[{p.Player.playerId}] {p.Player.displayName}").ToArray());
            return puppets[_actingIdx];
        }

        void DrawSelection(NetSim net)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Act as a remote player", EditorStyles.boldLabel);
            var puppet = ActingPuppet(net);

            if (puppet)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    _pickGoTo = GUILayout.Toggle(_pickGoTo, _pickGoTo ? "Click in Scene view…" : "Walk to (click Scene)", "Button");
                    if (GUILayout.Button("Come to me")) puppet.mode = NetSimPuppet.Mode.FollowYou;
                    if (puppet.Held && GUILayout.Button($"Drop {puppet.Held.name}")) puppet.Drop();
                }
            }

            var go = Selection.activeGameObject;
            if (!go || EditorUtility.IsPersistent(go))
            {
                EditorGUILayout.HelpBox("Select a synced object in the Hierarchy to inspect it and act on it.", MessageType.None);
                return;
            }

            var owner = Networking.GetOwner(go);
            EditorGUILayout.LabelField($"Selected: {go.name}", $"owner: {(owner == null ? "-" : $"[{owner.playerId}] {owner.displayName}")}");
            if (!puppet) return;

            using (new EditorGUILayout.HorizontalScope())
            {
                GUI.enabled = owner != puppet.Player;
                if (GUILayout.Button("Take ownership")) net.TakeOwnershipAsPuppet(puppet, go);
                GUI.enabled = true;
                var pickup = go.GetComponent<VRC_Pickup>();
                if (pickup && GUILayout.Button("Grab pickup")) puppet.Grab(pickup);
            }

            var behaviours = go.GetComponents<UdonBehaviour>().Where(u => u.SyncMethod != Networking.SyncType.None && u.SyncMetadataTable != null).ToArray();
            if (behaviours.Length == 0) return;
            _syncedIdx = Mathf.Clamp(_syncedIdx, 0, behaviours.Length - 1);
            if (behaviours.Length > 1)
                _syncedIdx = EditorGUILayout.Popup("Behaviour", _syncedIdx, behaviours.Select((u, i) => $"{i}: {u.programSource?.name}").ToArray());
            var ub = behaviours[_syncedIdx];

            EditorGUILayout.LabelField("Sync", $"{ub.SyncMethod}   ≈{NetSim.EstimateBytes(ub)} B");
            EditorGUILayout.LabelField("Your values", net.LocalStateJson(ub), EditorStyles.wordWrappedMiniLabel);
            net.RemoteState.TryGetValue(ub, out var remote);
            EditorGUILayout.LabelField("Remotes have", remote ?? "(nothing received yet)", EditorStyles.wordWrappedMiniLabel);

            if (_editFor != ub) { _editFor = ub; _stateEdit = remote ?? net.LocalStateJson(ub); }
            EditorGUILayout.LabelField($"Values {puppet.Player.displayName} sends (JSON):");
            _stateEdit = EditorGUILayout.TextArea(_stateEdit, GUILayout.MinHeight(36));
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Reset to your values")) _stateEdit = net.LocalStateJson(ub);
                if (GUILayout.Button($"Send as {puppet.Player.displayName}")) net.SendStateAsPuppet(puppet, ub, _stateEdit);
            }

            EditorGUILayout.Space();
            var callable = ub.GetNetworkCallingMetadata();
            if (callable != null && callable.Length > 0)
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("[NetworkCallable]", GUILayout.Width(110));
                    foreach (var m in callable)
                        if (GUILayout.Button(m.Name, EditorStyles.miniButton)) _eventName = m.Name;
                }
            _eventName = EditorGUILayout.TextField("Event", _eventName);
            _eventArgs = EditorGUILayout.TextField("Args (JSON array)", _eventArgs);
            using (new EditorGUILayout.HorizontalScope())
            {
                _eventTarget = (NetworkEventTarget)EditorGUILayout.EnumPopup(_eventTarget);
                if (GUILayout.Button($"Send event as {puppet.Player.displayName}") && !string.IsNullOrEmpty(_eventName))
                {
                    try { net.SendEventAsPuppet(puppet, ub, _eventTarget, _eventName, net.ParseEventArgs(ub, _eventName, _eventArgs)); }
                    catch (Exception e) { net.AddLog(LogType.Error, e.Message, ub); }
                }
            }
        }

        void DrawLog(NetSim net)
        {
            EditorGUILayout.Space();
            int problems = net.Log.Count(e => e.type != LogType.Log);
            if (GUILayout.Button($"Open per-player console ({net.Log.Count} lines{(problems > 0 ? $", ⚠ {problems}" : "")})", GUILayout.Height(24)))
                NetSimConsoleWindow.Open();
        }

        void OnSceneGUI(SceneView view)
        {
            if (!_pickGoTo || !EditorApplication.isPlaying) return;
            var e = Event.current;
            HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));
            if (e.type != EventType.MouseDown || e.button != 0) return;
            var net = NetSim.Instance;
            var puppets = net ? net.Puppets.ToArray() : Array.Empty<NetSimPuppet>();
            if (puppets.Length > 0 && Physics.Raycast(HandleUtility.GUIPointToWorldRay(e.mousePosition), out var hit, 1000f))
                puppets[Mathf.Clamp(_actingIdx, 0, puppets.Length - 1)].GoTo(hit.point);
            _pickGoTo = false;
            e.Use();
            Repaint();
        }

        static class Styles
        {
            public static readonly GUIStyle Warn = new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = new Color(1f, 0.75f, 0.2f) } };
            public static readonly GUIStyle Error = new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = new Color(1f, 0.4f, 0.4f) } };
        }
    }
}
