using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDK3.ClientSim;
using VRC.SDKBase;

namespace ClientSimNet
{
    /// <summary>
    /// Adds a "ClientSim Net" page to ClientSim's in-game menu (Esc / Tab): network status, presets, the player
    /// list (view / control / leave), add player, master leaves, POV options, debugger and stats.
    /// Built at runtime by cloning the menu's own buttons and texts, so the SDK package is not modified.
    /// </summary>
    public class NetSimMenuPage : MonoBehaviour
    {
        /// <summary>Set by the editor assembly: opens the Net Debugger window.</summary>
        public static Action OpenDebugger;

        static readonly string[] Names = { "Bob", "Ann", "Cat", "Dan", "Eve", "Fay", "Gus", "Hal", "Ivy", "Jon" };
        static readonly (string name, float lat, float jit, float loss)[] Presets =
            { ("Perfect", 0, 0, 0), ("LAN", 10, 3, 0), ("Normal", 100, 30, 0.5f), ("Bad", 300, 120, 5) };

        NetSim _net;
        GameObject _pause, _page, _btnTemplate, _textTemplate;
        Transform _list;
        Text _status, _playersTitle;
        readonly List<Button> _presetButtons = new List<Button>();
        Button _third, _control, _masterLeaves;
        readonly Dictionary<int, (Text name, Button view)> _rows = new Dictionary<int, (Text, Button)>();
        string _rowsKey = "";
        float _nextTry, _nextRefresh;

        void Start() => _net = GetComponent<NetSim>();

        void OnDestroy() { if (_page) Destroy(_page); }

        void Update()
        {
            if (!_page)
            {
                if (Time.unscaledTime < _nextTry) return;
                _nextTry = Time.unscaledTime + 1;
                Build();
                return;
            }
            if (_page.activeSelf && _pause.activeSelf) _page.SetActive(false); // ClientSim switched pages itself
            if (_page.activeInHierarchy && Time.unscaledTime >= _nextRefresh) Refresh();
        }

        void Build()
        {
            var menu = Resources.FindObjectsOfTypeAll<ClientSimMenu>().FirstOrDefault(m => m.gameObject.scene.IsValid());
            var pause = menu ? menu.transform.Find("MenuCanvas/PauseMenu") : null;
            var btn = pause ? pause.Find("OtherOptions/Player/Items/SpawnRemoteButton") : null;
            var txt = pause ? pause.Find("OtherOptions/Player/Items/PlayerId/Name") : null;
            if (!pause || !btn || !txt) return; // ClientSim not started yet (or its menu changed)
            _pause = pause.gameObject; _btnTemplate = btn.gameObject; _textTemplate = txt.gameObject;

            // entry button in the main page's title line (the Actions row has no room for a 5th button)
            var open = (RectTransform)MakeButton(_pause.transform, "ClientSim Net ▶", () => Show(true)).transform;
            open.anchorMin = open.anchorMax = open.pivot = new Vector2(1, 1);
            open.anchoredPosition = new Vector2(-25, -18);
            open.sizeDelta = new Vector2(120, 30);

            // the page: a copy of the pause menu with only its background and title
            _page = Instantiate(_pause, _pause.transform.parent);
            _page.name = "ClientSimNetPage";
            foreach (Transform c in _page.transform) if (c.name != "Background" && c.name != "Title") Destroy(c.gameObject);
            _page.transform.Find("Title").GetComponent<Text>().text = "ClientSim Net";
            _page.SetActive(false);

            var root = new GameObject("Content", typeof(RectTransform)).GetComponent<RectTransform>();
            root.SetParent(_page.transform, false);
            root.anchorMin = Vector2.zero; root.anchorMax = Vector2.one;
            root.offsetMin = new Vector2(25, 18); root.offsetMax = new Vector2(-25, -58);
            var v = root.gameObject.AddComponent<VerticalLayoutGroup>();
            v.spacing = 5; v.childControlWidth = v.childControlHeight = true; v.childForceExpandWidth = true; v.childForceExpandHeight = false;

            _status = Label(root, "", 13, 34);

            var presets = Row(root, 26);
            Label(presets, "Network", 13, 26, 62);
            foreach (var p in Presets)
            {
                var pr = p;
                _presetButtons.Add(MakeButton(presets, pr.name, () => NetSimApi.SetNetwork(pr.lat, pr.jit, pr.loss)));
            }

            _playersTitle = Label(root, "Players", 14, 20);
            var list = new GameObject("Players", typeof(RectTransform));
            list.transform.SetParent(root, false);
            var lv = list.AddComponent<VerticalLayoutGroup>();
            lv.spacing = 3; lv.childControlWidth = lv.childControlHeight = true; lv.childForceExpandWidth = true; lv.childForceExpandHeight = false;
            list.AddComponent<LayoutElement>().flexibleHeight = 1;
            _list = list.transform;

            var r1 = Row(root, 26);
            MakeButton(r1, "+ Add player", AddPlayer);
            _masterLeaves = MakeButton(r1, "Master leaves", NetSimApi.MasterLeaves);
            _third = MakeButton(r1, "", () => _net.Pov.ThirdPerson = !_net.Pov.ThirdPerson);
            _control = MakeButton(r1, "", () => _net.Pov.Control = !_net.Pov.Control);

            var r2 = Row(root, 26);
            if (OpenDebugger != null) MakeButton(r2, "Net Debugger", () => OpenDebugger());
            MakeButton(r2, "Save stats", () => NetSimApi.SaveStats());
            MakeButton(r2, "◀ Back", () => Show(false));
        }

        void Show(bool net)
        {
            _page.SetActive(net);
            _pause.SetActive(!net);
            if (net) Refresh();
        }

        static void AddPlayer()
        {
            var used = new HashSet<string>(VRCPlayerApi.AllPlayers.Where(p => p != null).Select(p => p.displayName));
            NetSimApi.AddPlayer(Names.FirstOrDefault(n => !used.Contains(n)) ?? $"Player {VRCPlayerApi.AllPlayers.Count + 1}");
        }

        void Refresh()
        {
            _nextRefresh = Time.unscaledTime + 0.25f;
            if (!_net) return;
            var t = _net.Transport;
            int myOut = t.LocalOutBytesPerSecond(NetSim.Now);
            int warnings = _net.Log.Count(e => e.type == LogType.Warning), errors = _net.Log.Count(e => e.type == LogType.Error || e.type == LogType.Exception);
            bool bad = errors > 0 || myOut > NetSim.BandwidthLimit;
            _status.text = $"{t.latencyMs:0} ± {t.jitterMs:0} ms   loss {t.lossPercent:0.#}%   in flight {t.Queued}\n" +
                           $"You send {myOut} / {NetSim.BandwidthLimit} B/s{(myOut > NetSim.BandwidthLimit ? " CLOGGED" : "")}   warnings {warnings}   errors {errors}";
            _status.color = bad ? new Color(1f, 0.5f, 0.4f) : Color.white;
            for (int i = 0; i < Presets.Length; i++)
            {
                var p = Presets[i];
                bool on = Mathf.Approximately(t.latencyMs, p.lat) && Mathf.Approximately(t.jitterMs, p.jit) && Mathf.Approximately(t.lossPercent, p.loss);
                SetLabel(_presetButtons[i], on ? $"[{p.name}]" : p.name);
            }
            SetLabel(_third, "3rd person: " + (_net.Pov.ThirdPerson ? "On" : "Off"));
            SetLabel(_control, "Control: " + (_net.Pov.Control ? "On" : "Off"));
            var master = Networking.Master;
            _masterLeaves.gameObject.SetActive(master != null && !master.isLocal); // the button style has no disabled look

            var players = VRCPlayerApi.AllPlayers.Where(p => p != null).OrderBy(p => p.playerId).ToList();
            _playersTitle.text = $"Players ({players.Count})";
            string key = string.Join(",", players.Select(p => p.playerId));
            if (key != _rowsKey) RebuildRows(players, key);
            foreach (var p in players)
            {
                if (!_rows.TryGetValue(p.playerId, out var row)) continue;
                var puppet = p.isLocal ? null : p.gameObject.GetComponent<NetSimPuppet>();
                string extra = puppet == null ? "" : $"   {puppet.mode}{(puppet.Held ? $", holds {puppet.Held.name}" : "")}";
                row.name.text = $"[{p.playerId}] {(p.isLocal ? "You" : p.displayName)}{(p.isMaster ? " ★" : "")}{extra}";
                bool viewing = p.isLocal ? _net.Pov.IsLocal : _net.Pov.PlayerId == p.playerId;
                SetLabel(row.view, viewing ? "Viewing" : "View");
            }
        }

        void RebuildRows(List<VRCPlayerApi> players, string key)
        {
            _rowsKey = key;
            foreach (Transform c in _list) Destroy(c.gameObject);
            _rows.Clear();
            foreach (var p in players)
            {
                var pl = p;
                var row = Row(_list, 24);
                var name = Label(row, "", 13, 24);
                name.GetComponent<LayoutElement>().flexibleWidth = 1;
                var view = MakeButton(row, "View", () => { if (pl.isLocal) _net.Pov.ViewLocal(); else _net.Pov.View(pl.playerId); }, 70);
                if (!pl.isLocal)
                {
                    var nm = pl.displayName;
                    MakeButton(row, "Rejoin", () => NetSimApi.Rejoin(nm), 60);
                    MakeButton(row, "Leave", () => ClientSimMain.RemovePlayer(pl), 60);
                }
                else Label(row, "", 13, 24, 124);
                _rows[p.playerId] = (name, view);
            }
        }

        // ---------------------------------------------------------------- widgets cloned from ClientSim's menu

        Button MakeButton(Transform parent, string label, Action onClick, float width = -1)
        {
            var go = Instantiate(_btnTemplate, parent);
            go.name = "NetSim " + label;
            var b = go.GetComponent<Button>();
            b.onClick = new Button.ButtonClickedEvent(); // drop ClientSim's persistent listener (Spawn Remote Player)
            b.onClick.AddListener(() =>
            {
                try { onClick(); }
                catch (Exception e) { Debug.LogWarning($"[ClientSimNet] {e.Message}"); }
                if (_page && _page.activeInHierarchy) Refresh();
            });
            var t = b.GetComponentInChildren<Text>();
            t.fontSize = 13; t.resizeTextForBestFit = true; t.resizeTextMinSize = 9; t.resizeTextMaxSize = 13;
            SetLabel(b, label);
            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = 26;
            if (width > 0) le.preferredWidth = width; else le.flexibleWidth = 1;
            return b;
        }

        static void SetLabel(Button b, string s) => b.GetComponentInChildren<Text>().text = s;

        Text Label(Transform parent, string s, int size, float height, float width = -1)
        {
            var go = Instantiate(_textTemplate, parent);
            go.name = "NetSim Text";
            var t = go.GetComponent<Text>();
            t.text = s; t.fontSize = size; t.alignment = TextAnchor.MiddleLeft;
            t.horizontalOverflow = HorizontalWrapMode.Wrap; t.verticalOverflow = VerticalWrapMode.Truncate;
            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = height;
            if (width > 0) le.preferredWidth = width;
            return t;
        }

        static Transform Row(Transform parent, float height)
        {
            var go = new GameObject("Row", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var h = go.AddComponent<HorizontalLayoutGroup>();
            h.spacing = 4; h.childControlWidth = h.childControlHeight = true; h.childForceExpandWidth = false; h.childForceExpandHeight = true;
            go.AddComponent<LayoutElement>().preferredHeight = height;
            return go.transform;
        }
    }
}
