using System;
using System.Linq;
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDK3.Components;

namespace ClientSimNet
{
    /// <summary>Creates Assets/ClientSimNet/Tests/NetSimTest.unity from the default VRChat world scene.</summary>
    public static class NetSimTestSceneBuilder
    {
        const string Source = "Assets/Scenes/VRCDefaultWorldScene.unity";

        [MenuItem("VRChat SDK/ClientSim Net - Build Test Scene")]
        public static void Build()
        {
            if (EditorApplication.isPlaying) { Debug.LogWarning("[ClientSimNet] Exit Play mode first."); return; }
            string counter = AssetDatabase.FindAssets("NetSimTestCounter t:MonoScript").Select(AssetDatabase.GUIDToAssetPath).FirstOrDefault();
            if (counter == null) { Debug.LogError("[ClientSimNet] Test scripts not found. Import them: Window > Package Manager > ClientSim Net > Samples > Test World."); return; }
            string Target = counter.Substring(0, counter.LastIndexOf('/')) + "/NetSimTest.unity";
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            AssetDatabase.DeleteAsset(Target);
            if (!AssetDatabase.CopyAsset(Source, Target)) { Debug.LogError($"[ClientSimNet] Could not copy {Source}"); return; }
            EditorSceneManager.OpenScene(Target);

            var root = new GameObject("ClientSimNet Test");
            Spawn(root, "Counter (manual sync, click)", PrimitiveType.Cube, new Vector3(-3, 1, 4), "NetSimTestCounter");
            Spawn(root, "Spinner (continuous sync, click = own)", PrimitiveType.Cylinder, new Vector3(0, 1, 4), "NetSimTestSpinner");
            Spawn(root, "Events (click = Ping All + Owner)", PrimitiveType.Sphere, new Vector3(3, 1, 4), "NetSimTestEvents");
            Spawn(root, "Toggle (late-join state)", PrimitiveType.Cube, new Vector3(-3, 1, 7), "NetSimTestToggle");
            Spawn(root, "Lock (click own, click lock; denies remote takeover)", PrimitiveType.Cube, new Vector3(0, 1, 7), "NetSimTestLock");
            Spawn(root, "Players (join/leave/master, click = list)", PrimitiveType.Capsule, new Vector3(3, 1, 7), "NetSimTestPlayers");
            Spawn(root, "Payload (continuous 200B limit, click = grow)", PrimitiveType.Cylinder, new Vector3(-3, 1, 10), "NetSimTestPayload");
            Spawn(root, "Spam (20 events, rate limit, Others/Self)", PrimitiveType.Sphere, new Vector3(0, 1, 10), "NetSimTestSpam");

            var pickup = GameObject.CreatePrimitive(PrimitiveType.Cube);
            pickup.name = "Pickup (ObjectSync)";
            pickup.transform.SetParent(root.transform);
            pickup.transform.position = new Vector3(0, 1, 2);
            pickup.transform.localScale = Vector3.one * 0.3f;
            pickup.AddComponent<Rigidbody>();
            pickup.AddComponent<VRCPickup>();
            pickup.AddComponent<VRCObjectSync>();
            AddUdon(pickup, "NetSimTestPickup");

            // PlayerObjects: ClientSim clones the card once per player (owned by that player); the leaderboard reads all copies.
            var board = Spawn(root, "Leaderboard (reads every PlayerObject card, click = print)", PrimitiveType.Cylinder, new Vector3(3, 1, 10), "NetSimTestLeaderboard");
            var cardGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cardGo.name = "PlayerCard (PlayerObject: click yours = score, others = Cheer)";
            cardGo.transform.SetParent(root.transform);
            cardGo.transform.position = new Vector3(-3, 1, 13);
            cardGo.AddComponent<VRCPlayerObject>();
            var card = AddUdon(cardGo, "NetSimTestPlayerCard");
            Wire(card, "board", board);
            Wire(board, "template", card);

            // UdonSharp applies [UdonBehaviourSyncMode] to new behaviours only on its next editor update; do it now so the saved scene is right
            typeof(UdonSharpEditorUtility).Assembly.GetType("UdonSharpEditor.UdonSharpEditorManager")
                ?.GetMethod("RunAllUpdates", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)?.Invoke(null, new object[] { null });
            EditorSceneManager.SaveScene(root.scene);
            Debug.Log($"[ClientSimNet] Built {Target}. Press Play, open VRChat SDK/ClientSim Net, add remote players.");
        }

        static UdonSharpBehaviour Spawn(GameObject root, string name, PrimitiveType shape, Vector3 pos, string typeName)
        {
            var go = GameObject.CreatePrimitive(shape);
            go.name = name;
            go.transform.SetParent(root.transform);
            go.transform.position = pos;
            return AddUdon(go, typeName);
        }

        internal static void Wire(UdonSharpBehaviour target, string field, UdonSharpBehaviour value)
        {
            if (!target || !value) return;
            target.GetType().GetField(field).SetValue(target, value);
            UdonSharpEditorUtility.CopyProxyToUdon(target);
            EditorUtility.SetDirty(target);
        }

        internal static UdonSharpBehaviour AddUdon(GameObject go, string typeName)
        {
            Type type = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType(typeName)).FirstOrDefault(t => t != null);
            if (type == null) { Debug.LogError($"[ClientSimNet] Type {typeName} not found (did scripts compile?)"); return null; }
            EnsureProgramAsset(type);
            return UdonSharpUndo.AddComponent(go, type);
        }

        static void EnsureProgramAsset(Type type)
        {
            if (UdonSharpProgramAsset.GetProgramAssetForClass(type) != null) return;
            string guid = AssetDatabase.FindAssets($"{type.Name} t:MonoScript").FirstOrDefault();
            var script = AssetDatabase.LoadAssetAtPath<MonoScript>(AssetDatabase.GUIDToAssetPath(guid));
            var asset = ScriptableObject.CreateInstance<UdonSharpProgramAsset>();
            asset.sourceCsScript = script;
            AssetDatabase.CreateAsset(asset, AssetDatabase.GUIDToAssetPath(guid).Replace(".cs", ".asset"));
            AssetDatabase.SaveAssets();
            // the class -> asset cache doesn't see a new asset; stale cache = "program asset is null" in Wire
            typeof(UdonSharpProgramAsset).GetMethod("ClearProgramAssetCache", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)?.Invoke(null, null);
            UdonSharpProgramAsset.CompileAllCsPrograms(true);
        }
    }
}
