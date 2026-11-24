using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using VRC.SDKBase;

namespace ClientSimNet
{
    /// <summary>
    /// Network debugger data + UI (shown by Window > Analysis > ClientSim Net Debugger), laid out like Unity's Profiler: a timeline of stacked bars
    /// (bytes on the wire per 0.1 s sample, by kind), Rec / Clear / ◀ ▶ / Live, click the chart to select a
    /// sample, and detail tabs below (Messages / Players in that sample, Objects now).
    /// </summary>
    public class NetSimDebugger : MonoBehaviour
    {
        /// <summary>Rolling 1 s window of bytes and message count.</summary>
        public class Rate
        {
            readonly Queue<(double t, int b)> _q = new Queue<(double, int)>();
            int _sum;
            public int Total, Lost;
            public double Last = -1;
            public void Add(double t, int b) { _q.Enqueue((t, b)); _sum += b; Total++; Last = t; }
            void Trim(double now) { while (_q.Count > 0 && _q.Peek().t < now - 1.0) _sum -= _q.Dequeue().b; }
            public int BytesPerSec(double now) { Trim(now); return _sum; }
            public int PerSec(double now) { Trim(now); return _q.Count; }
        }

        public class ObjStats { public readonly Rate Out = new Rate(), In = new Rate(); }

        [System.Serializable] public class Entry { public double t, latencyMs; public string status, kind, desc, fromName, toName; public int from, to, bytes; }
        [System.Serializable] public class Sample { public long index; public List<Entry> Msgs = new List<Entry>(); }

        /// <summary>Totals over a capture, per player ("You", "Bob", ...) and per kind.</summary>
        [System.Serializable] public class Total { public string name; public int outBytes, outMsgs, inBytes, inMsgs, lost; public float outBytesPerSec, maxOutBytesPerSec; }

        /// <summary>Saved statistics file: summary + the whole timeline (load it in the Net Debugger window).</summary>
        [System.Serializable]
        public class Capture
        {
            public string saved; public float sampleSeconds, seconds;
            public List<Total> players = new List<Total>(), kinds = new List<Total>();
            public List<Sample> samples = new List<Sample>();
        }

        /// <summary>Set when showing a loaded file instead of the live session.</summary>
        public string LoadedFrom { get; private set; }

        public const float SampleSeconds = 0.1f;
        public const int MaxSamples = 300; // 30 s
        static readonly string[] KindNames = { "Sync", "Event", "Owner", "Pose", "Other" };
        static readonly Color[] KindColors =
        {
            new Color(0.30f, 0.60f, 1.00f), new Color(1.00f, 0.60f, 0.20f), new Color(0.75f, 0.45f, 1.00f),
            new Color(0.40f, 0.80f, 0.40f), new Color(0.60f, 0.60f, 0.60f)
        };

        // "out" = sent by that player, "in" = received by that player (their client's view); used by the report
        public readonly Dictionary<int, Rate> PlayerOut = new Dictionary<int, Rate>(), PlayerIn = new Dictionary<int, Rate>();
        public readonly Dictionary<string, Rate> Kinds = new Dictionary<string, Rate>();
        public readonly Dictionary<GameObject, ObjStats> Objects = new Dictionary<GameObject, ObjStats>();

        /// <summary>Timeline, oldest first. Recording = false pauses it (like the Profiler's record button).</summary>
        public List<Sample> Samples = new List<Sample>();
        public bool Recording = true;

        NetSim _net;
        Vector2 _scroll;
        int _sel = -1;      // selected sample, -1 = live (newest)
        int _who = All;     // chart / list filter
        int _tab;
        bool _poses;
        readonly bool[] _show = { true, true, true, true, true };
        const int All = int.MinValue;

        void Start()
        {
            _net = GetComponent<NetSim>();
            if (_net) _net.Transport.Logged += OnMessage; // no NetSim = viewer for a loaded file
        }

        void OnDestroy() { if (_net) _net.Transport.Logged -= OnMessage; }

        static Rate Get<K>(Dictionary<K, Rate> d, K k) => d.TryGetValue(k, out var r) ? r : d[k] = new Rate();

        public ObjStats Stats(GameObject go) => Objects.TryGetValue(go, out var s) ? s : Objects[go] = new ObjStats();

        void Update() { if (Recording && _net) Current(NetSim.Now); } // empty samples keep the timeline moving

        Sample Current(double now)
        {
            long idx = (long)(now / SampleSeconds);
            while (Samples.Count == 0 || Samples[Samples.Count - 1].index < idx)
            {
                long next = Samples.Count == 0 ? idx : System.Math.Max(Samples[Samples.Count - 1].index + 1, idx - MaxSamples + 1);
                Samples.Add(new Sample { index = next });
                if (Samples.Count > MaxSamples) { Samples.RemoveAt(0); if (_sel > 0) _sel--; }
            }
            return Samples[Samples.Count - 1];
        }

        void OnMessage(NetMessage m, string status)
        {
            double now = NetSim.Now;
            if (status == "sent")
            {
                Get(PlayerOut, m.from).Add(now, m.bytes);
                Get(Kinds, m.kind ?? "?").Add(now, m.bytes);
                if (m.obj) Stats(m.obj).Out.Add(now, m.bytes);
            }
            else if (status == "delivered")
            {
                Get(PlayerIn, m.to).Add(now, m.bytes);
                if (m.obj && m.to == _net.Transport.localId) Stats(m.obj).In.Add(now, m.bytes);
            }
            else // lost
            {
                Get(PlayerOut, m.from).Add(now, m.bytes); // it left the sender's uplink
                Get(PlayerOut, m.from).Lost++;
                Get(Kinds, m.kind ?? "?").Lost++;
                if (m.obj) Stats(m.obj).Out.Lost++;
            }
            if (Recording)
                Current(now).Msgs.Add(new Entry
                {
                    t = now, status = status, kind = m.kind ?? "?", desc = m.desc, from = m.from, to = m.to, bytes = m.bytes,
                    fromName = Name(m.from), toName = Name(m.to),
                    latencyMs = status == "delivered" ? (now - m.sentAt) * 1000 : -1
                });
        }

        static int KindIndex(string k) { int i = System.Array.IndexOf(KindNames, k); return i < 0 || i == 4 ? 4 : i; }
        static bool OnWire(Entry e) => e.status != "delivered"; // sent or lost: left the sender's uplink
        bool Involves(Entry e) => _who == All || e.from == _who || e.to == _who;
        bool SentBy(Entry e) => _who == All || e.from == _who;
        string Name(int id)
        {
            if (_net) return id == _net.Transport.localId ? "You" : _net.PlainName(id);
            foreach (var s in Samples) foreach (var m in s.Msgs) { if (m.from == id) return m.fromName; if (m.to == id) return m.toName; }
            return $"[{id}]";
        }

        /// <summary>Recorded player ids (offline) or the players in the instance (live).</summary>
        IEnumerable<int> PlayerIds() => _net
            ? VRCPlayerApi.AllPlayers.Where(p => p != null).OrderBy(p => p.playerId).Select(p => p.playerId)
            : Samples.SelectMany(s => s.Msgs).SelectMany(m => new[] { m.from, m.to }).Distinct().OrderBy(i => i);

        public Capture BuildCapture()
        {
            var c = new Capture { saved = System.DateTime.Now.ToString("s"), sampleSeconds = SampleSeconds, seconds = Samples.Count * SampleSeconds, samples = Samples };
            float secs = Mathf.Max(SampleSeconds, c.seconds);
            var all = Samples.SelectMany(x => x.Msgs).ToList();
            foreach (int id in PlayerIds())
            {
                var o = all.Where(m => OnWire(m) && m.from == id).ToList();
                var i = all.Where(m => m.status == "delivered" && m.to == id).ToList();
                c.players.Add(new Total
                {
                    name = Name(id), outBytes = o.Sum(m => m.bytes), outMsgs = o.Count, inBytes = i.Sum(m => m.bytes), inMsgs = i.Count,
                    lost = o.Count(m => m.status == "lost"), outBytesPerSec = o.Sum(m => m.bytes) / secs,
                    maxOutBytesPerSec = Samples.Count == 0 ? 0 : Samples.Max(x => x.Msgs.Where(m => OnWire(m) && m.from == id).Sum(m => m.bytes)) / SampleSeconds
                });
            }
            foreach (var g in all.Where(OnWire).GroupBy(m => m.kind).OrderBy(g => g.Key))
                c.kinds.Add(new Total { name = g.Key, outBytes = g.Sum(m => m.bytes), outMsgs = g.Count(), lost = g.Count(m => m.status == "lost"), outBytesPerSec = g.Sum(m => m.bytes) / secs });
            return c;
        }

        /// <summary>Totals only (no timeline): small enough to hand to an AI.</summary>
        public Capture Summary() { var c = BuildCapture(); c.samples = new List<Sample>(); return c; }

        /// <summary>Writes the recorded timeline + totals as JSON (default Library/ClientSimNet/netstats-*.json); returns the path.</summary>
        public string Save(string path = null)
        {
            path = path ?? System.IO.Path.Combine("Library", "ClientSimNet", $"netstats-{System.DateTime.Now:yyyyMMdd-HHmmss}.json");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)));
            System.IO.File.WriteAllText(path, JsonUtility.ToJson(BuildCapture(), false)); // compact: ~60% smaller than pretty
            Debug.Log($"[ClientSimNet] Network stats saved: {System.IO.Path.GetFullPath(path)}");
            return path;
        }

        /// <summary>Shows a saved file (recording stops; works outside Play mode on a debugger with no NetSim).</summary>
        public void Load(string path)
        {
            Recording = false;
            Samples = JsonUtility.FromJson<Capture>(System.IO.File.ReadAllText(path)).samples;
            LoadedFrom = path; _sel = Samples.Count > 0 ? 0 : -1; _who = All;
        }

        /// <summary>The whole debugger UI (GUILayout), drawn by the Net Debugger editor window.</summary>
        public void DrawPanel(float chartHeight)
        {
            double now = _net ? NetSim.Now : Samples.Count == 0 ? 0 : Samples[Samples.Count - 1].index * SampleSeconds;
            if (_sel >= Samples.Count) _sel = -1;
            if (_who != All && !PlayerIds().Contains(_who)) _who = All;

            // toolbar
            GUILayout.BeginHorizontal();
            if (_net) Recording = GUILayout.Toggle(Recording, Recording ? "● Rec" : "○ Rec", "Button", GUILayout.Width(56));
            if (GUILayout.Button("Clear", GUILayout.Width(48))) { Samples.Clear(); _sel = -1; }
            if (GUILayout.Button("◀", GUILayout.Width(26)) && Samples.Count > 0) _sel = Mathf.Max(0, (_sel < 0 ? Samples.Count - 1 : _sel) - 1);
            if (GUILayout.Button("▶", GUILayout.Width(26)) && _sel >= 0) _sel = Mathf.Min(Samples.Count - 1, _sel + 1);
            bool live = GUILayout.Toggle(_sel < 0, "Live", "Button", GUILayout.Width(44));
            if (live) _sel = -1; else if (_sel < 0) _sel = Mathf.Max(0, Samples.Count - 1);
            if (GUILayout.Button("Player: " + (_who == All ? "All" : Name(_who)), GUILayout.Width(120))) _who = NextWho();
            if (GUILayout.Button("Save", GUILayout.Width(44))) Save();
            if (!_net) { GUILayout.Label("File: " + System.IO.Path.GetFileName(LoadedFrom), Styles.Head); GUILayout.EndHorizontal(); }
            else Header(now);

            DrawRest(chartHeight, now);
        }

        void Header(double now)
        {
            var t = _net.Transport;
            int myOut = t.LocalOutBytesPerSecond(now);
            bool clogged = myOut > NetSim.BandwidthLimit;
            GUILayout.Label($"{t.latencyMs:0}±{t.jitterMs:0} ms  loss {t.lossPercent:0.#}%  in flight {t.Queued}  you out {myOut}/{NetSim.BandwidthLimit} B/s{(clogged ? " CLOGGED" : "")}",
                            clogged ? Styles.Bad : Styles.Mono);
            GUILayout.EndHorizontal();
        }

        void DrawRest(float chartHeight, double now)
        {
            // legend (click = hide series), like Profiler module counters
            GUILayout.BeginHorizontal();
            for (int k = 0; k < KindNames.Length; k++)
            {
                var c = GUI.contentColor; GUI.contentColor = _show[k] ? KindColors[k] : Color.gray;
                _show[k] = GUILayout.Toggle(_show[k], "■ " + KindNames[k], Styles.Legend);
                GUI.contentColor = c;
            }
            GUILayout.FlexibleSpace();
            GUILayout.Label($"bytes on the wire per {SampleSeconds}s, last {MaxSamples * SampleSeconds:0}s", Styles.Hint);
            GUILayout.EndHorizontal();

            Chart(GUILayoutUtility.GetRect(10, chartHeight, GUILayout.ExpandWidth(true)));

            // selected sample summary
            var sel = Samples.Count == 0 ? null : Samples[_sel < 0 ? Samples.Count - 1 : _sel];
            if (sel != null)
            {
                var wire = sel.Msgs.Where(x => OnWire(x) && SentBy(x)).ToList();
                GUILayout.Label($"Sample {sel.index * SampleSeconds:0.0}s ({(now - sel.index * SampleSeconds):0.0}s ago)   out {wire.Sum(x => x.bytes)} B   " +
                                $"{wire.Count} msgs   lost {wire.Count(x => x.status == "lost")}{(_sel < 0 ? "   [live]" : "")}", Styles.Head);
            }

            GUILayout.BeginHorizontal();
            _tab = GUILayout.Toolbar(_tab, new[] { "Messages", "Players", "Objects (now)" }, GUILayout.Width(330));
            if (_tab == 0) _poses = GUILayout.Toggle(_poses, "show poses");
            GUILayout.EndHorizontal();

            _scroll = GUILayout.BeginScrollView(_scroll);
            if (_tab == 0) Messages(sel);
            else if (_tab == 1) Players(sel);
            else if (_net) ObjectsNow(now);
            else GUILayout.Label("Object state is live only (not in saved files).", Styles.Hint);
            GUILayout.EndScrollView();
        }

        void Chart(Rect r)
        {
            GUI.color = new Color(0.03f, 0.03f, 0.04f, 1f); GUI.DrawTexture(r, Texture2D.whiteTexture); GUI.color = Color.white;
            float barW = r.width / MaxSamples;
            int limit = _who == All ? 0 : Mathf.RoundToInt(NetSim.BandwidthLimit * SampleSeconds);
            var stacks = new int[Samples.Count, KindNames.Length];
            int max = Mathf.Max(100, limit);
            for (int i = 0; i < Samples.Count; i++)
            {
                int total = 0;
                foreach (var m in Samples[i].Msgs)
                    if (OnWire(m) && SentBy(m) && _show[KindIndex(m.kind)]) { stacks[i, KindIndex(m.kind)] += m.bytes; total += m.bytes; }
                max = Mathf.Max(max, total);
            }
            float scale = (r.height - 4) / (max * 1.1f);

            for (int i = 0; i < Samples.Count; i++)
            {
                float x = r.xMax - (Samples.Count - i) * barW, y = r.yMax;
                for (int k = 0; k < KindNames.Length; k++)
                {
                    float h = stacks[i, k] * scale;
                    if (h <= 0) continue;
                    y -= h;
                    GUI.color = KindColors[k]; GUI.DrawTexture(new Rect(x, y, Mathf.Max(1, barW - 0.5f), h), Texture2D.whiteTexture);
                }
                if (Samples[i].Msgs.Any(m => m.status == "lost" && SentBy(m)))
                { GUI.color = Color.red; GUI.DrawTexture(new Rect(x, r.y, Mathf.Max(1, barW - 0.5f), 3), Texture2D.whiteTexture); }
            }
            if (limit > 0)
            {
                GUI.color = new Color(1, 0.3f, 0.3f, 0.8f);
                GUI.DrawTexture(new Rect(r.x, r.yMax - limit * scale, r.width, 1), Texture2D.whiteTexture);
            }
            int s = _sel < 0 ? Samples.Count - 1 : _sel;
            if (s >= 0)
            {
                float x = r.xMax - (Samples.Count - s) * barW;
                GUI.color = new Color(1, 1, 1, 0.15f); GUI.DrawTexture(new Rect(x - 2, r.y, barW + 4, r.height), Texture2D.whiteTexture);
                GUI.color = Color.white; GUI.DrawTexture(new Rect(x + barW / 2, r.y, 1, r.height), Texture2D.whiteTexture);
            }
            GUI.color = Color.white;
            GUI.Label(new Rect(r.x + 3, r.y + 1, 200, 16), $"{Short((int)(max * 1.1f))} B" + (limit > 0 ? $"   red = limit {limit} B" : ""), Styles.Hint);
            GUI.Label(new Rect(r.x + 3, r.yMax - 16, 60, 16), $"-{MaxSamples * SampleSeconds:0}s", Styles.Hint);

            var e = Event.current;
            if ((e.type == EventType.MouseDown || e.type == EventType.MouseDrag) && e.button == 0 && r.Contains(e.mousePosition) && Samples.Count > 0)
            {
                _sel = Mathf.Clamp(Samples.Count - 1 - (int)((r.xMax - e.mousePosition.x) / barW), 0, Samples.Count - 1);
                e.Use();
            }
        }

        void Messages(Sample sel)
        {
            GUILayout.Label("time      status     from → to              kind     bytes  latency  what", Styles.Head);
            if (sel == null) return;
            int shown = 0;
            foreach (var m in sel.Msgs)
            {
                if (!Involves(m) || (!_poses && m.kind == "Pose")) continue;
                shown++;
                string arrow = m.status == "lost" ? "✗" : "→";
                GUILayout.Label($"{m.t,8:0.000}  {m.status,-9}  {Cut(m.fromName, 9),-9} {arrow} {Cut(m.toName, 9),-9}  {m.kind,-6} {m.bytes,6}  " +
                                $"{(m.latencyMs < 0 ? "" : $"{m.latencyMs:0} ms"),7}  {m.desc}", m.status == "lost" ? Styles.Bad : Styles.Mono);
            }
            if (shown == 0) GUILayout.Label(sel.Msgs.Count == 0 ? "No messages in this sample." : "Only poses in this sample (tick \"show poses\").", Styles.Hint);
        }

        void Players(Sample sel)
        {
            GUILayout.Label("PLAYER            out B  msgs   in B  lost   (this sample)    out B/s  in B/s (now)", Styles.Head);
            foreach (int pid in PlayerIds())
            {
                var p = _net ? VRCPlayerApi.GetPlayerById(pid) : null;
                var o = sel == null ? new List<Entry>() : sel.Msgs.Where(m => OnWire(m) && m.from == pid).ToList();
                int inB = sel == null ? 0 : sel.Msgs.Where(m => m.status == "delivered" && m.to == pid).Sum(m => m.bytes);
                double now = NetSim.Now;
                string name = Name(pid) + (p != null && p.isMaster ? " *" : "");
                int outPerSec = p == null ? 0 : Get(PlayerOut, pid).BytesPerSec(now);
                string nowCols = p == null ? "" : $"{outPerSec,7} {Get(PlayerIn, pid).BytesPerSec(now),7}";
                GUILayout.Label($"{Cut(name, 16),-16} {o.Sum(m => m.bytes),6} {o.Count,5} {inB,6} {o.Count(m => m.status == "lost"),5}                    {nowCols}",
                                outPerSec > NetSim.BandwidthLimit ? Styles.Bad : Styles.Mono);
            }
        }

        void ObjectsNow(double now)
        {
            GUILayout.Label("OBJECT                 sync  owner       size/limit  out/s  sent ago  recv ago  sync", Styles.Head);
            foreach (var ub in _net.Synced.Where(u => u && u.gameObject.activeInHierarchy).OrderBy(u => u.name))
            {
                var s = Stats(ub.gameObject);
                int bytes = NetSim.EstimateBytes(ub), limit = ub.SyncIsContinuous ? NetSim.ContinuousLimit : NetSim.ManualLimit;
                bool inSync = _net.RemoteState.TryGetValue(ub, out var remote) && remote == _net.LocalStateJson(ub);
                var owner = Networking.GetOwner(ub.gameObject);
                string row = $"{Cut(ub.name, 22),-22} {(ub.SyncIsContinuous ? "cont" : "man"),4}  {Cut(owner == null ? "-" : owner.isLocal ? "You" : owner.displayName, 10),-10} " +
                             $"{bytes,5}/{Short(limit),-5} {s.Out.PerSec(now),5}  {Ago(s.Out.Last, now),8}  {Ago(s.In.Last, now),8}  {(inSync ? "ok" : "DIFF")}";
                GUILayout.Label(row, bytes > limit || !inSync ? Styles.Bad : Styles.Mono);
            }
            GUILayout.Label("DIFF = remotes hold other values (non-owner write, never sent, or still in flight)", Styles.Hint);
        }

        int NextWho()
        {
            var ids = new List<int> { All };
            ids.AddRange(PlayerIds());
            return ids[(ids.IndexOf(_who) + 1) % ids.Count];
        }

        static string Cut(string s, int n) => s == null ? "" : s.Length <= n ? s : s.Substring(0, n - 1) + "…";
        static string Short(int b) => b >= 1000 ? $"{b / 1000}k" : b.ToString();
        static string Ago(double last, double now) => last < 0 ? "never" : $"{now - last:0.0}s";

        static class Styles
        {
            static GUIStyle Make(Color c, FontStyle f = FontStyle.Normal)
            {
                var s = new GUIStyle(GUI.skin.label) { fontSize = 11, fontStyle = f, wordWrap = false, richText = false };
                s.font = Font.CreateDynamicFontFromOSFont(new[] { "Consolas", "Menlo", "Courier New" }, 11);
                s.normal.textColor = c;
                return s;
            }
            public static readonly GUIStyle Mono = Make(Color.white);
            public static readonly GUIStyle Head = Make(new Color(0.6f, 0.85f, 1f), FontStyle.Bold);
            public static readonly GUIStyle Bad = Make(new Color(1f, 0.45f, 0.35f));
            public static readonly GUIStyle Hint = new GUIStyle(GUI.skin.label) { fontSize = 10, normal = { textColor = new Color(1f, 0.9f, 0.4f) } };
            public static readonly GUIStyle Legend = new GUIStyle(GUI.skin.label) { fontSize = 11, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
        }
    }
}
