using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace ClientSimNet
{
    /// <summary>
    /// Per-player console: what each player's VRChat client would show. Your tab has your Udon logs and
    /// everything you sent/received; a remote player's tab has their traffic (they run no Udon here).
    /// </summary>
    public class NetSimConsoleWindow : EditorWindow
    {
        const int AllTab = int.MinValue;
        int _tab = AllTab;
        bool _net = true, _udon = true, _sim = true, _onlyProblems, _follow = true, _followPov = true;
        int _lastPov = int.MinValue;
        string _search = "";
        Vector2 _scroll;
        double _nextRepaint;
        int _lastCount;

        [MenuItem("VRChat SDK/ClientSim Net Console")]
        public static void Open() => GetWindow<NetSimConsoleWindow>("ClientSim Net Console");

        void Update()
        {
            if (!EditorApplication.isPlaying || EditorApplication.timeSinceStartup < _nextRepaint) return;
            _nextRepaint = EditorApplication.timeSinceStartup + 0.25;
            var net = NetSim.Instance;
            if (net && net.Log.Count != _lastCount) Repaint();
        }

        void OnGUI()
        {
            var net = NetSim.Instance;
            if (!net)
            {
                EditorGUILayout.HelpBox("Enter Play mode with ClientSim Net enabled.", MessageType.Info);
                return;
            }
            _lastCount = net.Log.Count;
            if (_followPov && net.Pov && net.Pov.PlayerId != _lastPov) // switching POV switches the console tab
            {
                _lastPov = net.Pov.PlayerId;
                _tab = net.Pov.IsLocal ? net.Transport.localId : _lastPov;
            }

            DrawTabs(net);
            DrawToolbar(net);
            var lines = Visible(net).ToList();

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var e in lines.Skip(Math.Max(0, lines.Count - 500)))
                DrawLine(net, e);
            EditorGUILayout.EndScrollView();
            if (_follow && Event.current.type == EventType.Repaint) _scroll.y = float.MaxValue;
        }

        void DrawTabs(NetSim net)
        {
            // players present now, plus anyone who appears in the log (left players keep their tab)
            var ids = net.Log.Select(e => e.player).Concat(VRC.SDKBase.VRCPlayerApi.AllPlayers.Where(p => p != null).Select(p => p.playerId))
                .Where(id => id >= 0).Distinct().OrderBy(id => id).ToList();
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if (GUILayout.Toggle(_tab == AllTab, "All", EditorStyles.toolbarButton, GUILayout.Width(40))) _tab = AllTab;
                foreach (int id in ids)
                {
                    var p = VRC.SDKBase.VRCPlayerApi.GetPlayerById(id);
                    string label = $"[{id}] {net.PlainName(id)}{(p == null ? " (left)" : p.isMaster ? " ★" : "")}";
                    int problems = net.Log.Count(e => e.player == id && IsProblem(e));
                    if (problems > 0) label += $"  ⚠{problems}";
                    var old = GUI.contentColor;
                    GUI.contentColor = PlayerColor(id);
                    if (GUILayout.Toggle(_tab == id, label, EditorStyles.toolbarButton)) _tab = id;
                    GUI.contentColor = old;
                }
                GUILayout.FlexibleSpace();
            }
        }

        void DrawToolbar(NetSim net)
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                _net = GUILayout.Toggle(_net, new GUIContent("Network", "→ sent  ← received  ✗ lost"), EditorStyles.toolbarButton, GUILayout.Width(60));
                _udon = GUILayout.Toggle(_udon, new GUIContent("Udon", "Debug.Log from your scripts (runs on your client)"), EditorStyles.toolbarButton, GUILayout.Width(45));
                _sim = GUILayout.Toggle(_sim, new GUIContent("Notes", "Simulator explanations: ownership, drops, limits"), EditorStyles.toolbarButton, GUILayout.Width(45));
                _onlyProblems = GUILayout.Toggle(_onlyProblems, "⚠ only", EditorStyles.toolbarButton, GUILayout.Width(50));
                _search = GUILayout.TextField(_search, EditorStyles.toolbarSearchField, GUILayout.MinWidth(80));
                _follow = GUILayout.Toggle(_follow, "Follow", EditorStyles.toolbarButton, GUILayout.Width(50));
                _followPov = GUILayout.Toggle(_followPov, new GUIContent("👁 POV", "Tab follows the POV player"), EditorStyles.toolbarButton, GUILayout.Width(50));
                if (GUILayout.Button(new GUIContent("Copy", "Copy the visible lines as text (paste to an AI or an issue)"), EditorStyles.toolbarButton, GUILayout.Width(40)))
                    EditorGUIUtility.systemCopyBuffer = AsText(net, Visible(net));
                if (GUILayout.Button("Clear", EditorStyles.toolbarButton, GUILayout.Width(40))) net.Log.Clear();
            }
        }

        IEnumerable<NetSim.LogEntry> Visible(NetSim net) => net.Log.Where(e =>
            (_tab == AllTab || e.player == _tab) &&
            (e.kind == "udon" ? _udon : e.kind == "sim" ? _sim : _net) &&
            (!_onlyProblems || IsProblem(e)) &&
            (_search.Length == 0 || e.text.IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0));

        void DrawLine(NetSim net, NetSim.LogEntry e)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label($"{e.t,7:0.00}", Styles.Time, GUILayout.Width(50));
                if (_tab == AllTab)
                {
                    var old = GUI.contentColor;
                    GUI.contentColor = PlayerColor(e.player);
                    GUILayout.Label(net.PlainName(e.player), Styles.Tag, GUILayout.Width(70));
                    GUI.contentColor = old;
                }
                GUILayout.Label(Icon(e), Styles.Tag, GUILayout.Width(16));
                var style = e.type == LogType.Error || e.type == LogType.Exception || e.type == LogType.Assert ? Styles.Error
                    : e.type == LogType.Warning ? Styles.Warn
                    : e.kind == "udon" ? Styles.Udon : e.kind == "sim" ? Styles.Note : Styles.Net;
                if (GUILayout.Button(e.text, style) && e.ctx) { EditorGUIUtility.PingObject(e.ctx); Selection.activeObject = e.ctx; }
            }
        }

        static bool IsProblem(NetSim.LogEntry e) => e.type != LogType.Log;

        static string Icon(NetSim.LogEntry e) =>
            e.type == LogType.Error || e.type == LogType.Exception ? "✖" : e.type == LogType.Warning && e.kind != "lost" ? "⚠" :
            e.kind == "udon" ? "•" : e.kind == "sim" ? "i" : e.kind == "lost" ? "✗" : e.kind == "out" ? "→" : "←";

        static string AsText(NetSim net, IEnumerable<NetSim.LogEntry> lines)
        {
            var sb = new StringBuilder();
            foreach (var e in lines)
                sb.Append($"{e.t,7:0.00} [{net.PlainName(e.player)}] {e.kind,-4} {e.type,-7} {e.text}\n");
            return sb.ToString();
        }

        static readonly Color[] Palette =
        {
            new Color(0.45f, 0.8f, 1f), new Color(1f, 0.6f, 0.85f), new Color(0.6f, 1f, 0.6f),
            new Color(1f, 0.85f, 0.45f), new Color(0.75f, 0.65f, 1f), new Color(0.5f, 1f, 0.9f),
        };

        static Color PlayerColor(int id) => id < 0 ? Color.gray : Palette[(id - 1 + Palette.Length) % Palette.Length];

        static class Styles
        {
            static GUIStyle Mini(Color c, bool wrap = true) => new GUIStyle(EditorStyles.label)
            { wordWrap = wrap, richText = false, fontSize = 11, normal = { textColor = c }, hover = { textColor = c }, padding = new RectOffset(2, 2, 1, 1) };
            public static readonly GUIStyle Time = Mini(Color.gray, false);
            public static readonly GUIStyle Tag = new GUIStyle(EditorStyles.boldLabel) { fontSize = 11 };
            public static readonly GUIStyle Net = Mini(new Color(0.7f, 0.7f, 0.7f));
            public static readonly GUIStyle Udon = Mini(EditorGUIUtility.isProSkin ? Color.white : Color.black);
            public static readonly GUIStyle Note = Mini(new Color(0.55f, 0.75f, 1f));
            public static readonly GUIStyle Warn = Mini(new Color(1f, 0.75f, 0.2f));
            public static readonly GUIStyle Error = Mini(new Color(1f, 0.4f, 0.4f));
        }
    }
}
