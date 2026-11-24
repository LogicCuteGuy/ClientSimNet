using System;
using System.Linq;
using UnityEngine;
using VRC.SDKBase;

namespace ClientSimNet
{
    /// <summary>
    /// Look through any player's eyes. Remote players are viewed from their OWN client position (no latency),
    /// so you see where they really are, not the delayed copy you receive. With Control on, ClientSim's own player becomes them (in their world).
    /// Game view keys: Alt+1..9 = player #n, Alt+0 or Alt+` = you, Alt+[ / Alt+] = previous/next, Alt+T = third person, Alt+C = control the viewed player.
    /// </summary>
    [DefaultExecutionOrder(10001)]
    public class NetSimPov : MonoBehaviour
    {
        /// <summary>-1 = your own view (ClientSim camera).</summary>
        public int PlayerId { get; private set; } = -1;
        public bool ThirdPerson;
        /// <summary>While viewing a remote player, you play as them with ClientSim's player; you stand still meanwhile.</summary>
        public bool Control = true;
        public event Action<int> Changed;

        Camera _cam;

        public bool IsLocal => PlayerId < 0;

        public void View(int playerId)
        {
            var p = VRCPlayerApi.GetPlayerById(playerId);
            int id = p == null || p.isLocal ? -1 : playerId;
            if (id == PlayerId) return;
            PlayerId = id;
            Changed?.Invoke(PlayerId);
        }

        public void ViewLocal() => View(-1);

        public void Cycle(int dir)
        {
            var ids = Players().Select(p => p.isLocal ? -1 : p.playerId).ToList();
            if (ids.Count == 0) return;
            int i = Mathf.Max(0, ids.IndexOf(PlayerId));
            View(ids[(i + dir + ids.Count) % ids.Count]);
        }

        static VRCPlayerApi[] Players() =>
            VRCPlayerApi.AllPlayers.Where(p => p != null).OrderBy(p => p.playerId).ToArray();

        void Update()
        {
            var net = NetSim.Instance;
            if (!net) return;
            // Control: ClientSim's own player (walk, jump, interact, pickups, menu) becomes the viewed player, in their world
            net.Possess(Control && !IsLocal ? net.WorldOfPlayer(PlayerId) : null);
        }

        void LateUpdate()
        {
            var p = IsLocal ? null : VRCPlayerApi.GetPlayerById(PlayerId);
            if (!IsLocal && (p == null || !p.gameObject)) { PlayerId = -1; Changed?.Invoke(-1); } // they left

            if (IsLocal) { if (_cam) _cam.enabled = false; return; }

            bool possessed = NetSim.Instance && NetSim.Instance.Possessed?.Player == p;
            if (possessed && !ThirdPerson) { if (_cam) _cam.enabled = false; return; } // ClientSim's camera is their eyes
            EnsureCamera();
            var puppet = p.gameObject.GetComponent<NetSimPuppet>();
            Vector3 pos = puppet ? puppet.SimPosition : p.GetPosition();
            Quaternion rot = puppet ? puppet.SimRotation : p.GetRotation();
            Vector3 eye = pos + Vector3.up * 1.6f;
            if (ThirdPerson)
            {
                Vector3 camPos = eye - rot * Vector3.forward * 2.5f + Vector3.up * 0.6f;
                _cam.transform.SetPositionAndRotation(camPos, Quaternion.LookRotation(eye - camPos));
            }
            else
                _cam.transform.SetPositionAndRotation(eye + rot * Vector3.forward * 0.3f, rot); // just in front of the capsule
            _cam.enabled = true;
        }

        void EnsureCamera()
        {
            if (_cam) return;
            // the world camera (renders Default layer), not ClientSim's UI camera
            var src = Camera.main ? Camera.main : Camera.allCameras.Where(c => (c.cullingMask & 1) != 0).OrderBy(c => c.depth).FirstOrDefault();
            var go = new GameObject("__NetSimPovCamera");
            go.transform.SetParent(transform, false);
            _cam = go.AddComponent<Camera>();
            if (src) { _cam.CopyFrom(src); _cam.depth = src.depth + 0.5f; } // above the world view, below overlay/UI cameras
            _cam.nearClipPlane = 0.05f;
            _cam.enabled = false;
        }

        void OnGUI()
        {
            HandleKeys(Event.current);
        }

        void HandleKeys(Event e)
        {
            if (e.type != EventType.KeyDown || !e.alt) return;
            var players = Players();
            int n = e.keyCode >= KeyCode.Alpha1 && e.keyCode <= KeyCode.Alpha9 ? e.keyCode - KeyCode.Alpha1 : -1;
            if (n >= 0 && n < players.Length) View(players[n].playerId);
            else if (e.keyCode == KeyCode.Alpha0 || e.keyCode == KeyCode.BackQuote) ViewLocal();
            else if (e.keyCode == KeyCode.RightBracket) Cycle(1);
            else if (e.keyCode == KeyCode.LeftBracket) Cycle(-1);
            else if (e.keyCode == KeyCode.T) ThirdPerson = !ThirdPerson;
            else if (e.keyCode == KeyCode.C) Control = !Control;
            else return;
            e.Use();
        }

        void OnDestroy() { if (_cam) Destroy(_cam.gameObject); }

    }
}
