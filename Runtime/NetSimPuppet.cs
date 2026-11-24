using UnityEngine;
using VRC.SDK3.ClientSim;
using VRC.SDKBase;
using VRC.Udon;

namespace ClientSimNet
{
    /// <summary>
    /// Drives one ClientSim remote player. The puppet's "own client" position (_simPos) is authoritative;
    /// what you see is the pose received over the simulated network (10Hz, unreliable) and interpolated.
    /// </summary>
    public class NetSimPuppet : MonoBehaviour
    {
        public enum Mode { Idle, FollowYou, Wander, GoTo, Controlled }

        public VRCPlayerApi Player { get; private set; }
        public Mode mode = Mode.Idle;
        public float speed = 2f; // VRChat default walk speed
        public VRC_Pickup Held { get; private set; }
        /// <summary>Where the player is on their own client (no latency). The visible transform is what you receive.</summary>
        public Vector3 SimPosition => _simPos;
        public Quaternion SimRotation => _simRot;

        NetSim _net;
        Vector3 _simPos, _target, _home, _netPos;
        Quaternion _simRot, _netRot;
        float _nextPose, _nextWander;
        bool _hasPose;
        bool _heldWasKinematic, _heldLocalOnly;
        float _vy, _gravity = 1f; bool _air;
        public bool Grounded => !_air;

        // VRChat reserved layers: Player (9) and PlayerLocal (10) are ignored when grounding.
        static readonly int GroundMask = Physics.DefaultRaycastLayers & ~((1 << 9) | (1 << 10));

        public void Init(NetSim net, VRCPlayerApi player)
        {
            _net = net;
            Player = player;
            _simPos = _netPos = _home = _target = transform.position;
            _simRot = _netRot = transform.rotation;
        }

        public void GoTo(Vector3 p) { _target = p; mode = Mode.GoTo; }

        bool _possessed; Mode _modeBefore;

        /// <summary>You control them with ClientSim's player: its position is theirs (their own client), their capsule is hidden.</summary>
        internal void SetPossessed(bool on)
        {
            if (on == _possessed) return;
            _possessed = on;
            if (on) { _modeBefore = mode == Mode.Controlled ? Mode.Idle : mode; mode = Mode.Controlled; }
            else mode = _modeBefore;
            foreach (var r in GetComponentsInChildren<Renderer>(true)) r.forceRenderingOff = on;
        }

        public void Teleport(Vector3 position, Quaternion rotation)
        {
            _simPos = _target = position;
            _simRot = Quaternion.Euler(0, rotation.eulerAngles.y, 0);
            _vy = 0; _air = false;
        }

        /// <summary>Jump on the puppet's own client (world jump impulse / gravity).</summary>
        public void Jump()
        {
            if (_air) return;
            var local = Networking.LocalPlayer; // worlds set locomotion per client; yours is the best guess for theirs
            float impulse = Utilities.IsValid(local) ? local.GetJumpImpulse() : 3f;
            _vy = impulse > 0 ? impulse : 3f;
            _gravity = Utilities.IsValid(local) && local.GetGravityStrength() > 0 ? local.GetGravityStrength() : 1f;
            _air = true;
        }

        void Update()
        {
            if (_net == null || !Utilities.IsValid(Player)) return;
            if (_possessed) { _simPos = _net.RigPosition; _simRot = Quaternion.Euler(0, _net.RigRotation.eulerAngles.y, 0); _air = false; }
            else { SimulateOwnClient(Time.deltaTime); Ground(Time.deltaTime); }

            if (Time.unscaledTime >= _nextPose)
            {
                _nextPose = Time.unscaledTime + 0.1f;
                Vector3 p = _simPos;
                Quaternion r = _simRot;
                _net.Transport.Send(new NetMessage
                {
                    from = Player.playerId, to = _net.Transport.localId, channel = Channel.Unreliable, kind = "Pose", bytes = 28,
                    deliver = () => { _netPos = p; _netRot = r; _hasPose = true; }
                }, NetSim.Now);
            }

            if (_hasPose)
            {
                float k = 1f - Mathf.Exp(-12f * Time.deltaTime);
                transform.SetPositionAndRotation(Vector3.Lerp(transform.position, _netPos, k), Quaternion.Slerp(transform.rotation, _netRot, k));
            }
        }

        void SimulateOwnClient(float dt)
        {
            switch (mode)
            {
                case Mode.Idle:
                case Mode.Controlled: return;
                case Mode.FollowYou:
                    var local = Networking.LocalPlayer;
                    if (local == null) return;
                    Vector3 lp = local.GetPosition();
                    _target = lp + (_simPos - lp).normalized * 1.5f; // stop 1.5 m away
                    break;
                case Mode.Wander:
                    if (Time.time >= _nextWander || (_simPos - _target).sqrMagnitude < 0.04f)
                    {
                        _nextWander = Time.time + Random.Range(2f, 5f);
                        Vector2 c = Random.insideUnitCircle * 5f;
                        _target = _home + new Vector3(c.x, 0, c.y);
                    }
                    break;
            }

            Vector3 to = _target - _simPos;
            to.y = 0;
            if (to.sqrMagnitude < 0.0025f) return;
            Vector3 step = Vector3.ClampMagnitude(to, speed * dt);
            _simPos += step;
            _simRot = Quaternion.Slerp(_simRot, Quaternion.LookRotation(step.normalized, Vector3.up), 10f * dt);
        }

        void Ground(float dt)
        {
            float g = Physics.gravity.magnitude * _gravity;
            if (_air) { _simPos.y += _vy * dt; _vy -= g * dt; }
            if (!Physics.Raycast(_simPos + Vector3.up * 1.5f, Vector3.down, out var hit, 50f, GroundMask, QueryTriggerInteraction.Ignore)) return;
            if (!_air) { _simPos.y = hit.point.y; return; }
            if (_vy <= 0 && _simPos.y <= hit.point.y) { _simPos.y = hit.point.y; _vy = 0; _air = false; }
        }

        void LateUpdate()
        {
            if (!Held) return;
            if (_heldLocalOnly) // in their own world, where they really are (no latency)
            {
                Held.transform.SetPositionAndRotation(_simPos + _simRot * new Vector3(0.25f, 1.1f, 0.35f), _simRot);
                return;
            }
            if (!Utilities.IsValid(Player) || Networking.GetOwner(Held.gameObject) != Player)
            {
                ReleaseHeld(); // ownership was taken away
                return;
            }
            Vector3 hand = Player.GetBonePosition(HumanBodyBones.RightHand);
            if (hand == Vector3.zero) hand = transform.position + transform.up * 1.1f + transform.forward * 0.35f;
            Held.transform.SetPositionAndRotation(hand, transform.rotation);
            var rb = Held.GetComponent<Rigidbody>();
            if (rb && !rb.isKinematic) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
        }

        public void Grab(VRC_Pickup pickup)
        {
            if (!pickup) return;
            if (Held) Drop();
            if (pickup.GetComponent<IClientSimPositionSyncable>() == null)
            {
                // no VRCObjectSync: they pick up their own copy, nobody else sees it move
                var mine = _net.GoIn(pickup.gameObject, Player.playerId);
                var copy = mine ? mine.GetComponent<VRC_Pickup>() : null;
                if (!copy) { _net.AddLog(LogType.Warning, $"'{pickup.name}' has no VRCObjectSync and {Player.displayName}'s world has no copy of it.", pickup, Player.playerId); return; }
                Held = copy; _heldLocalOnly = true;
                if (copy.TryGetComponent<Rigidbody>(out var crb)) { _heldWasKinematic = crb.isKinematic; crb.isKinematic = true; }
                _net.AddLog(LogType.Log, $"{Player.displayName} picked up their own copy of '{pickup.name}' (no VRCObjectSync: only they see it move).", pickup, Player.playerId);
                OwnClientEvent(copy, "_onPickup");
                return;
            }

            // VRChat: picking up makes them owner on their own client at once, then OnPickup runs there
            _net.TakeOwnershipAsPuppet(this, pickup.gameObject, () =>
            {
                var helper = pickup.GetComponent<IClientSimPickupable>();
                if (helper == null || helper.IsHeld()) return;
                helper.Pickup(Player, VRC_Pickup.PickupHand.Right, _ => ReleaseHeld());
                Held = pickup;
                var sync = pickup.GetComponent<IClientSimPositionSyncable>();
                if (sync != null) { _heldWasKinematic = sync.GetIsKinematic(); sync.SetIsKinematic(true); }
                _net.AddLog(LogType.Log, $"{Player.displayName} picked up '{pickup.name}' (OnPickup does not fire for you: it's their pickup).", pickup);
            });
            if (_net.OwnsOnOwnClient(this, pickup.gameObject) || Networking.GetOwner(pickup.gameObject) == Player)
                OwnClientEvent(pickup, "_onPickup");
        }

        public void Drop()
        {
            if (!Held) return;
            var pickup = Held;
            OwnClientEvent(pickup, "_onDrop");
            if (_heldLocalOnly) { ReleaseHeld(); return; } // their own copy: nothing to send
            _net.Transport.Send(new NetMessage
            {
                from = Player.playerId, to = _net.Transport.localId, channel = Channel.Reliable, kind = "Drop", bytes = 8,
                desc = $"{Player.displayName} drops {pickup.name}",
                deliver = () => { if (Held == pickup) ReleaseHeld(); }
            }, NetSim.Now);
        }

        /// <summary>Use the held pickup (OnPickupUseDown/Up run on the puppet's client).</summary>
        public void Use()
        {
            if (!Held) return;
            OwnClientEvent(Held, "_onPickupUseDown");
            OwnClientEvent(Held, "_onPickupUseUp");
        }

        void OwnClientEvent(Component c, string evt) =>
            _net.RunOnClient(this, c.gameObject, go => { foreach (var ub in go.GetComponents<UdonBehaviour>()) ub.RunEvent(evt); });

        void ReleaseHeld()
        {
            if (!Held) return;
            if (_heldLocalOnly)
            {
                if (Held.TryGetComponent<Rigidbody>(out var rb)) rb.isKinematic = _heldWasKinematic; // falls in their world's physics
                Held = null; _heldLocalOnly = false; return;
            }
            var helper = Held.GetComponent<IClientSimPickupable>();
            helper?.Drop(Player);
            var sync = Held.GetComponent<IClientSimPositionSyncable>();
            if (sync != null) sync.SetIsKinematic(_heldWasKinematic);
            Held = null;
        }

        void OnDestroy() => ReleaseHeld();
    }
}
