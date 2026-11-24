using UnityEditor;
using UnityEngine;

namespace ClientSimNet
{
    /// <summary>
    /// Dockable network debugger, Profiler style. Live in Play mode;
    /// Load… opens a saved stats file (Save / NetSimApi.SaveStats) at any time.
    /// </summary>
    [InitializeOnLoad]
    public class NetSimDebuggerWindow : EditorWindow
    {
        static NetSimDebuggerWindow() => NetSimMenuPage.OpenDebugger = Open; // button in the in-game ClientSim menu

        double _nextRepaint;
        NetSimDebugger _offline; // hidden holder for a loaded file

        [MenuItem("Window/Analysis/ClientSim Net Debugger")]
        [MenuItem("VRChat SDK/ClientSim Net Debugger")]
        public static void Open() => GetWindow<NetSimDebuggerWindow>("Net Debugger").Show();

        void OnDisable() => CloseFile();

        void CloseFile() { if (_offline) DestroyImmediate(_offline.gameObject); _offline = null; }

        void Update()
        {
            if (!EditorApplication.isPlaying || _offline || EditorApplication.timeSinceStartup < _nextRepaint) return;
            _nextRepaint = EditorApplication.timeSinceStartup + 0.1; // ponytail: 10 Hz, matches the sample size
            Repaint();
        }

        void OnGUI()
        {
            EditorGUI.DrawRect(new Rect(0, 0, position.width, position.height), new Color(0.11f, 0.11f, 0.13f));
            var live = EditorApplication.isPlaying ? FindObjectOfType<NetSimDebugger>() : null;
            if (live == _offline) live = null;

            GUILayout.BeginHorizontal(EditorStyles.toolbar);
            if (GUILayout.Button("Load…", EditorStyles.toolbarButton, GUILayout.Width(60)))
            {
                string f = EditorUtility.OpenFilePanel("Load network stats", "Library/ClientSimNet", "json");
                if (!string.IsNullOrEmpty(f))
                {
                    CloseFile();
                    _offline = new GameObject("NetSim stats viewer") { hideFlags = HideFlags.HideAndDontSave }.AddComponent<NetSimDebugger>();
                    _offline.Load(f);
                }
            }
            var dbg = _offline ? _offline : live;
            if (dbg && GUILayout.Button("Save as…", EditorStyles.toolbarButton, GUILayout.Width(70)))
            {
                string f = EditorUtility.SaveFilePanel("Save network stats", "Library/ClientSimNet", $"netstats-{System.DateTime.Now:yyyyMMdd-HHmmss}", "json");
                if (!string.IsNullOrEmpty(f)) dbg.Save(f);
            }
            if (_offline && GUILayout.Button(live ? "Back to live" : "Close file", EditorStyles.toolbarButton, GUILayout.Width(90))) { CloseFile(); dbg = live; }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            if (!dbg) { GUILayout.Label("Enter Play mode with ClientSim Net enabled, or Load… a saved stats file.", EditorStyles.whiteLabel); return; }
            dbg.DrawPanel(Mathf.Clamp(position.height * 0.35f, 120, 400));
        }
    }
}
