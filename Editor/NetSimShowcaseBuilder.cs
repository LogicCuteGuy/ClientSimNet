using System;
using System.Linq;
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDK3.Components;

namespace ClientSimNet
{
    /// <summary>
    /// Builds NetSimShowcase.unity next to the sample scripts: a lit plaza with one station per networking use case,
    /// each with a sign (what it shows, how to try it) and a live line saying whose client you see and who owns it.
    /// </summary>
    public static class NetSimShowcaseBuilder
    {
        const string Source = "Assets/Scenes/VRCDefaultWorldScene.unity";

        static readonly Color Sync = new Color(0.25f, 0.6f, 1f), Events = new Color(1f, 0.6f, 0.2f), Owner = new Color(1f, 0.3f, 0.35f),
            Players = new Color(0.35f, 0.9f, 0.45f), Pickups = new Color(0.7f, 0.45f, 1f), PlayerObj = new Color(0.2f, 0.9f, 0.85f);

        static Font _font;
        static GameObject _root;

        [MenuItem("VRChat SDK/ClientSim Net - Build Showcase Scene")]
        public static void Build()
        {
            if (EditorApplication.isPlaying) { Debug.LogWarning("[ClientSimNet] Exit Play mode first."); return; }
            string status = AssetDatabase.FindAssets("NetSimShowStatus t:MonoScript").Select(AssetDatabase.GUIDToAssetPath).FirstOrDefault();
            if (status == null) { Debug.LogError("[ClientSimNet] Sample scripts not found. Import them: Window > Package Manager > ClientSim Net > Samples > Test World."); return; }
            string target = status.Substring(0, status.LastIndexOf('/')) + "/NetSimShowcase.unity";
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            AssetDatabase.DeleteAsset(target);
            if (!AssetDatabase.CopyAsset(Source, target)) { Debug.LogError($"[ClientSimNet] Could not copy {Source}"); return; }
            var scene = EditorSceneManager.OpenScene(target);
            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            var oldFloor = GameObject.Find("Floor");
            if (oldFloor) UnityEngine.Object.DestroyImmediate(oldFloor);
            Environment();
            _root = new GameObject("ClientSimNet Showcase");

            Banner();

            // row 1: synced variables
            Station(0, 0, Sync, "1  Manual sync", "Click: take ownership, +1, RequestSerialization.\nOthers get it after latency in OnDeserialization.\nTry: click as you and as Bob at once = race.",
                p => Udon(Prim(PrimitiveType.Cube, "Counter (manual sync, click)", p, 0.5f), "NetSimTestCounter"));
            Station(1, 0, Sync, "2  Continuous sync", "The owner spins it, others follow at ~10 Hz\n(unreliable). Click to become the owner.\nTry network Bad: it stutters, still converges.",
                p => Udon(Prim(PrimitiveType.Cylinder, "Spinner (continuous sync, click = own)", p, 0.6f, 0.15f), "NetSimTestSpinner"));
            Station(2, 0, Sync, "3  Late join", "A synced light. Toggle it, then untick\n\"Local Player Is Master\" and Play: the state\narrives in OnDeserialization, not in Start.",
                p => Udon(Prim(PrimitiveType.Cube, "Toggle (late-join state)", p, 0.6f), "NetSimTestToggle"));
            Station(3, 0, Sync, "4  Not synced", "No sync at all: only the clicker sees it change.\nClick it, then view as Bob (Alt+2):\nhis copy is still grey.",
                p => Udon(Prim(PrimitiveType.Sphere, "Local only (no sync)", p, 0.6f), "NetSimShowLocalOnly"));

            // row 2: events and ownership
            Station(0, 1, Events, "5  Network events", "Click: Ping(n, msg) to All + OwnerOnly to Owner.\nCallingPlayer = the sender. The Owner event\nruns only on the owner's client.",
                p => Udon(Prim(PrimitiveType.Sphere, "Events (click = Ping All + Owner)", p, 0.6f), "NetSimTestEvents"));
            Station(1, 1, Events, "6  Rate limit", "Click: 20 events in one frame. VRChat sends\n~5/s: your copy runs now, remote ones trickle in\nover ~4 s. Others / Self targets too.",
                p => Udon(Prim(PrimitiveType.Sphere, "Spam (20 events, rate limit, Others/Self)", p, 0.5f), "NetSimTestSpam"));
            Station(2, 1, Owner, "7  Ownership request", "Click: own it. Click again: lock it.\nLocked: OnOwnershipRequest says no, so Bob\ncan't take it (his request bounces back).",
                p => Udon(Prim(PrimitiveType.Cube, "Lock (click own, click lock; denies remote takeover)", p, 0.6f), "NetSimTestLock"));
            Station(3, 1, Owner, "8  Size limits", "Continuous sync holds ~200 B. Click to grow\nthe payload until OnPostSerialization\nreports success=false (see the debugger).",
                p => Udon(Prim(PrimitiveType.Cylinder, "Payload (continuous 200B limit, click = grow)", p, 0.5f, 0.3f), "NetSimTestPayload"));

            // row 3: players and pickups
            Station(0, 2, Players, "9  Players & master", "OnPlayerJoined / Left, OnMasterTransferred.\nMenu (Esc): + Add player, Rejoin, Leave, Master leaves.\nThe master's join counter moves on.",
                p => Udon(Prim(PrimitiveType.Capsule, "Players (join/leave/master, click = list)", p, 0.5f), "NetSimTestPlayers"));
            Station(1, 2, Pickups, "10  Synced pickup", "Grab it: you become owner, VRCObjectSync moves\nit for everyone. Use (click) fires an All event.\nBob grabbing it steals it from your hand.",
                p => { var g = Pickup(PrimitiveType.Cube, "Pickup (ObjectSync)", p, 0.3f, true, "Synced pickup"); Udon(g, "NetSimTestPickup"); return g; });
            Station(2, 2, Pickups, "11  Physics ball", "Pick it up and throw it. The owner's physics\nruns, everyone else follows ObjectSync.\nEach player's world has its own physics too.",
                p =>
                {
                    var g = Pickup(PrimitiveType.Sphere, "Ball (ObjectSync physics)", p, 0.35f, true, "Throw me");
                    g.GetComponent<Collider>().sharedMaterial = new PhysicMaterial("Bouncy") { bounciness = 0.7f, bounceCombine = PhysicMaterialCombine.Maximum };
                    return g;
                });
            Station(3, 2, Pickups, "12  Unsynced pickup", "No VRCObjectSync: it moves only for the holder.\nGrab it as Bob (view + Control): your copy stays.\nCompare with 10.",
                p => Pickup(PrimitiveType.Cube, "Unsynced pickup (no ObjectSync: moves only for whoever holds it)", p, 0.3f, false, "Unsynced pickup"));

            // row 4: PlayerObjects (cards line up by playerId to the right of the template)
            var board = (UdonSharpBehaviour)null;
            Station(0, 3, PlayerObj, "13  PlayerObjects", "Every player gets a card they own for the\nwhole session. Click yours: +score, colour.\nClick someone else's: asks its owner (Cheer).",
                p =>
                {
                    Block(_root, PrimitiveType.Cube, "Card bench (one card per player)", p + new Vector3(5.4f, -0.95f, 0), new Vector3(12f, 1f, 0.8f), Mat(new Color(0.85f, 0.86f, 0.9f), 0.4f));
                    var cardGo = Prim(PrimitiveType.Cube, "PlayerCard (PlayerObject: click yours = score, others = Cheer)", p, 0.4f);
                    cardGo.AddComponent<VRCPlayerObject>();
                    var card = NetSimTestSceneBuilder.AddUdon(cardGo, "NetSimTestPlayerCard");
                    var boardGo = Prim(PrimitiveType.Cylinder, "Leaderboard (reads every PlayerObject card, click = print)", p + new Vector3(13.5f, 0, 0), 0.5f, 0.2f);
                    board = NetSimTestSceneBuilder.AddUdon(boardGo, "NetSimTestLeaderboard");
                    NetSimTestSceneBuilder.Wire(card, "board", board);
                    NetSimTestSceneBuilder.Wire(board, "template", card);
                    return null; // the cards move (one per player): no owner line
                }, pedestal: false);
            Station(3, 3, PlayerObj, "14  Leaderboard", "Not synced: each client reads every card with\nFindComponentInPlayerObjects, so it can't desync.\nIt grows with the best score. Click: print.",
                p => null);

            // row 5: the two kinds of pickup side by side
            Station(1, 4, Pickups, "15  Pickup: synced vs not", "Left has VRCObjectSync, right has none.\nGrab each as you, then view Bob (Alt+2): the left\none moved in his world too, the right one didn't.",
                p =>
                {
                    var synced = Pickup(PrimitiveType.Cube, "Compare: synced pickup (ObjectSync)", p + new Vector3(-0.95f, 0, 0), 0.3f, true, "Synced");
                    Udon(synced, "NetSimTestPickup");
                    var local = Pickup(PrimitiveType.Cube, "Compare: unsynced pickup (moves only for the holder)", p + new Vector3(0.95f, 0, 0), 0.3f, false, "Not synced");
                    foreach (var x in new[] { -0.95f, 0.95f })
                        Block(_root, PrimitiveType.Cylinder, "Pedestal", p + new Vector3(x, -0.95f, 0), new Vector3(0.6f, 0.5f, 0.6f), Mat(new Color(0.85f, 0.86f, 0.9f), 0.4f));
                    var tag = Canvas(_root, "Synced tag", p + new Vector3(-0.95f, 0.75f, 0), new Vector2(520, 170), 0.0025f);
                    Panel(tag, new Color(0.05f, 0.06f, 0.09f, 0.88f), Vector2.zero, Vector2.one);
                    Label(tag, "<b>SYNCED</b>", 52, new Color(0.75f, 0.6f, 1f), FontStyle.Normal, new Vector2(0.05f, 0.5f), new Vector2(0.95f, 0.98f));
                    var owner = Label(tag, "", 30, new Color(1f, 0.85f, 0.35f), FontStyle.Normal, new Vector2(0.05f, 0.02f), new Vector2(0.95f, 0.5f));
                    var st = NetSimTestSceneBuilder.AddUdon(tag.gameObject, "NetSimShowStatus");
                    Set(st, "label", owner); Set(st, "target", synced);
                    var tag2 = Canvas(_root, "Unsynced tag", p + new Vector3(0.95f, 0.75f, 0), new Vector2(520, 170), 0.0025f);
                    Panel(tag2, new Color(0.05f, 0.06f, 0.09f, 0.88f), Vector2.zero, Vector2.one);
                    Label(tag2, "<b>NOT SYNCED</b>", 52, new Color(1f, 0.8f, 0.45f), FontStyle.Normal, new Vector2(0.05f, 0.5f), new Vector2(0.95f, 0.98f));
                    Label(tag2, "only the holder's copy moves", 30, new Color(0.88f, 0.9f, 0.95f), FontStyle.Normal, new Vector2(0.05f, 0.02f), new Vector2(0.95f, 0.5f));
                    return synced;
                }, pedestal: false);

            typeof(UdonSharpEditorUtility).Assembly.GetType("UdonSharpEditor.UdonSharpEditorManager")
                ?.GetMethod("RunAllUpdates", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)?.Invoke(null, new object[] { null });
            EditorSceneManager.SaveScene(scene);
            Debug.Log($"[ClientSimNet] Built {target}. Press Play, Esc > ClientSim Net > + Add player.");
        }

        // ---------------------------------------------------------------- layout

        static Vector3 Spot(int col, int row) => new Vector3(-6.75f + 4.5f * col, 0, 7 + 5.5f * row);

        static void Station(int col, int row, Color color, string title, string text, Func<Vector3, GameObject> make, bool pedestal = true)
        {
            var at = Spot(col, row);
            var group = new GameObject(title.Trim());
            group.transform.SetParent(_root.transform);
            group.transform.position = at;
            // decoration only: no colliders (the floor already holds you up)
            Block(group, PrimitiveType.Cylinder, "Base", at + new Vector3(0, 0.04f, 0), new Vector3(3.2f, 0.04f, 3.2f), Mat(new Color(0.16f, 0.17f, 0.21f), 0.7f), false);
            Block(group, PrimitiveType.Cylinder, "Ring", at + new Vector3(0, 0.02f, 0), new Vector3(3.45f, 0.02f, 3.45f), Glow(color, 0.9f), false);
            if (pedestal)
                Block(group, PrimitiveType.Cylinder, "Pedestal", at + new Vector3(0, 0.5f, 0), new Vector3(0.7f, 0.5f, 0.7f), Mat(new Color(0.85f, 0.86f, 0.9f), 0.4f));

            var obj = make(at + new Vector3(0, 1.45f, 0));
            if (obj) obj.transform.SetParent(group.transform, true);
            if (obj && !obj.GetComponent<VRCPickup>()) obj.GetComponent<Renderer>().sharedMaterial = Mat(Color.Lerp(Color.white, color, 0.45f), 0.6f);

            var light = new GameObject("Light").AddComponent<Light>();
            light.transform.SetParent(group.transform);
            light.transform.position = at + new Vector3(0, 3.2f, -0.8f);
            light.type = LightType.Point; light.color = color; light.range = 6; light.intensity = 1.6f;

            var sign = Canvas(group, "Sign", at + new Vector3(0, 2.75f, 1.4f), new Vector2(1100, 620), 0.0025f);
            Panel(sign, new Color(0.05f, 0.06f, 0.09f, 0.88f), Vector2.zero, Vector2.one);
            Panel(sign, color, new Vector2(0, 0.94f), Vector2.one);
            Label(sign, title, 78, color, FontStyle.Bold, new Vector2(0.05f, 0.7f), new Vector2(0.95f, 0.92f));
            Label(sign, text, 44, new Color(0.88f, 0.9f, 0.95f), FontStyle.Normal, new Vector2(0.05f, 0.2f), new Vector2(0.95f, 0.7f));
            var line = Label(sign, "", 40, new Color(1f, 0.85f, 0.35f), FontStyle.Normal, new Vector2(0.05f, 0.03f), new Vector2(0.95f, 0.18f));
            var st = NetSimTestSceneBuilder.AddUdon(sign.gameObject, "NetSimShowStatus");
            Set(st, "label", line);
            if (obj) Set(st, "target", obj);
        }

        static void Banner()
        {
            var c = Canvas(_root, "Welcome", new Vector3(0, 3.1f, 4.4f), new Vector2(2400, 620), 0.0022f);
            Panel(c, new Color(0.04f, 0.05f, 0.08f, 0.9f), Vector2.zero, Vector2.one);
            Panel(c, new Color(0.3f, 0.75f, 1f), new Vector2(0, 0.95f), Vector2.one);
            Label(c, "ClientSim Net  Showcase", 90, Color.white, FontStyle.Bold, new Vector2(0.04f, 0.72f), new Vector2(0.96f, 0.93f));
            Label(c,
                "1   Esc > ClientSim Net ▶ > + Add player (Bob, Ann...). Each player gets their own copy of this world.\n" +
                "2   Click the stations. Every client's log: VRChat SDK > ClientSim Net Console.\n" +
                "3   Alt+2 = view and play as Bob (ClientSim's player becomes him, in his world). Alt+0 = back to you.\n" +
                "4   Network: Perfect / LAN / Normal / Bad.  Traffic: Window > Analysis > ClientSim Net Debugger.",
                40, new Color(0.88f, 0.9f, 0.95f), FontStyle.Normal, new Vector2(0.04f, 0.04f), new Vector2(0.96f, 0.7f));
        }

        static void Environment()
        {
            var floor = Block(null, PrimitiveType.Plane, "Floor", new Vector3(0, 0, 10), new Vector3(5, 1, 5), Mat(new Color(0.1f, 0.11f, 0.14f), 0.55f));
            floor.transform.SetSiblingIndex(0);
            // a lit path from the spawn through the rows
            Block(floor, PrimitiveType.Cube, "Path", new Vector3(0, 0.005f, 17), new Vector3(1.4f, 0.01f, 32), Mat(new Color(0.2f, 0.22f, 0.28f), 0.6f));
            foreach (var x in new[] { -0.75f, 0.75f })
                Block(floor, PrimitiveType.Cube, "Path edge", new Vector3(x, 0.012f, 17), new Vector3(0.03f, 0.01f, 32), Glow(new Color(0.3f, 0.75f, 1f), 0.5f));

            var sun = UnityEngine.Object.FindObjectsOfType<Light>().FirstOrDefault(l => l.type == LightType.Directional);
            if (sun)
            {
                sun.color = new Color(1f, 0.9f, 0.78f); sun.intensity = 0.9f; sun.shadows = LightShadows.Soft;
                sun.transform.rotation = Quaternion.Euler(38, -35, 0);
            }
            RenderSettings.fog = true; RenderSettings.fogMode = FogMode.Exponential;
            RenderSettings.fogColor = new Color(0.06f, 0.08f, 0.13f); RenderSettings.fogDensity = 0.025f;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.22f, 0.25f, 0.33f);
        }

        // ---------------------------------------------------------------- pieces

        static GameObject Prim(PrimitiveType t, string name, Vector3 pos, float size, float height = -1)
        {
            var g = GameObject.CreatePrimitive(t);
            g.name = name;
            g.transform.position = pos;
            g.transform.localScale = new Vector3(size, height > 0 ? height : size, size);
            g.GetComponent<Renderer>().sharedMaterial = Mat(Color.white, 0.5f);
            return g;
        }

        static GameObject Pickup(PrimitiveType t, string name, Vector3 pos, float size, bool synced, string text)
        {
            var g = Prim(t, name, pos, size);
            g.GetComponent<Renderer>().sharedMaterial = Mat(synced ? new Color(0.75f, 0.6f, 1f) : new Color(1f, 0.8f, 0.45f), 0.6f);
            g.AddComponent<Rigidbody>();
            g.AddComponent<VRCPickup>().InteractionText = text;
            if (synced) g.AddComponent<VRCObjectSync>();
            return g;
        }

        static GameObject Udon(GameObject g, string type) { NetSimTestSceneBuilder.AddUdon(g, type); return g; }

        static GameObject Block(GameObject parent, PrimitiveType t, string name, Vector3 pos, Vector3 scale, Material m, bool collider = true)
        {
            var g = GameObject.CreatePrimitive(t);
            if (!collider) UnityEngine.Object.DestroyImmediate(g.GetComponent<Collider>());
            g.name = name;
            if (parent) g.transform.SetParent(parent.transform);
            g.transform.position = pos;
            g.transform.localScale = scale;
            g.GetComponent<Renderer>().sharedMaterial = m;
            return g;
        }

        static Material Mat(Color c, float smooth)
        {
            var m = new Material(Shader.Find("Standard")) { color = c };
            m.SetFloat("_Glossiness", smooth);
            return m;
        }

        static Material Glow(Color c, float power)
        {
            var m = Mat(c, 0.8f);
            m.EnableKeyword("_EMISSION");
            m.SetColor("_EmissionColor", c * power);
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            return m;
        }

        static RectTransform Canvas(GameObject parent, string name, Vector3 pos, Vector2 size, float scale)
        {
            var g = new GameObject(name, typeof(RectTransform));
            g.transform.SetParent(parent.transform);
            var c = g.AddComponent<Canvas>();
            c.renderMode = RenderMode.WorldSpace;
            var rt = (RectTransform)g.transform;
            rt.position = pos; rt.sizeDelta = size; rt.localScale = Vector3.one * scale;
            g.AddComponent<CanvasScaler>().dynamicPixelsPerUnit = 10;
            return rt;
        }

        static void Panel(RectTransform parent, Color c, Vector2 min, Vector2 max)
        {
            var g = new GameObject("Panel", typeof(RectTransform));
            g.transform.SetParent(parent, false);
            Fill((RectTransform)g.transform, min, max);
            g.AddComponent<Image>().color = c;
        }

        static Text Label(RectTransform parent, string s, int size, Color c, FontStyle style, Vector2 min, Vector2 max)
        {
            var g = new GameObject("Text", typeof(RectTransform));
            g.transform.SetParent(parent, false);
            Fill((RectTransform)g.transform, min, max);
            var t = g.AddComponent<Text>();
            t.font = _font; t.text = s; t.fontSize = size; t.color = c; t.fontStyle = style;
            t.alignment = TextAnchor.UpperLeft; t.supportRichText = true;
            t.horizontalOverflow = HorizontalWrapMode.Wrap; t.verticalOverflow = VerticalWrapMode.Overflow;
            return t;
        }

        static void Fill(RectTransform rt, Vector2 min, Vector2 max)
        {
            rt.anchorMin = min; rt.anchorMax = max; rt.offsetMin = rt.offsetMax = Vector2.zero;
        }

        static void Set(UdonSharpBehaviour target, string field, UnityEngine.Object value)
        {
            if (!target) return;
            target.GetType().GetField(field).SetValue(target, value);
            UdonSharpEditorUtility.CopyProxyToUdon(target);
            EditorUtility.SetDirty(target);
        }
    }
}
