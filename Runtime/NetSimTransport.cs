using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ClientSimNet
{
    public enum Channel { Reliable, Unreliable }

    public class NetMessage
    {
        public int from, to;
        public Channel channel;
        public string kind, desc;
        public GameObject obj; // synced object it belongs to (network debugger), null for player traffic
        public int bytes;
        public double sentAt, deliverAt;
        public Action deliver;
    }

    /// <summary>
    /// Loopback "wire": every cross-client action goes through here with latency, jitter and loss.
    /// Reliable messages are never lost and never reordered per (from,to) pair; unreliable ones can be both.
    /// Pure C# (explicit clock) so it can be self-checked outside Play mode.
    /// </summary>
    public class NetSimTransport
    {
        public float latencyMs = 80f, jitterMs = 20f, lossPercent = 0f;
        public System.Random rng = new System.Random();
        public int localId = -1;

        public event Action<NetMessage, string> Logged; // status: sent | delivered | lost

        readonly List<NetMessage> _queue = new List<NetMessage>();
        readonly Dictionary<(int, int), double> _lastReliable = new Dictionary<(int, int), double>();
        readonly Queue<(double t, int b)> _localOut = new Queue<(double, int)>();
        int _localOutSum;

        public int Queued => _queue.Count;

        public void Send(NetMessage m, double now, double extraDelay = 0)
        {
            m.sentAt = now;
            if (m.from == localId) { _localOut.Enqueue((now, m.bytes)); _localOutSum += m.bytes; }

            if (m.channel == Channel.Unreliable && rng.NextDouble() * 100 < lossPercent)
            {
                Logged?.Invoke(m, "lost");
                return;
            }

            double d = (latencyMs + (rng.NextDouble() * 2 - 1) * jitterMs) / 1000.0;
            m.deliverAt = now + extraDelay + Math.Max(0, d);
            if (m.channel == Channel.Reliable)
            {
                var key = (m.from, m.to);
                if (_lastReliable.TryGetValue(key, out var last) && m.deliverAt < last) m.deliverAt = last;
                _lastReliable[key] = m.deliverAt;
            }
            _queue.Add(m);
            Logged?.Invoke(m, "sent");
        }

        public void Tick(double now)
        {
            if (_queue.Count == 0) return;
            var due = _queue.Where(m => m.deliverAt <= now).OrderBy(m => m.deliverAt).ToList(); // OrderBy is stable
            if (due.Count == 0) return;
            _queue.RemoveAll(m => m.deliverAt <= now);
            foreach (var m in due)
            {
                Logged?.Invoke(m, "delivered");
                try { m.deliver?.Invoke(); }
                catch (Exception e) { Debug.LogException(e); }
            }
        }

        /// <summary>Bytes the local player sent in the last second.</summary>
        public int LocalOutBytesPerSecond(double now)
        {
            while (_localOut.Count > 0 && _localOut.Peek().t < now - 1.0) _localOutSum -= _localOut.Dequeue().b;
            return _localOutSum;
        }

        public void Clear() { _queue.Clear(); _lastReliable.Clear(); _localOut.Clear(); _localOutSum = 0; }

        /// <summary>Assert-based check of delay, reliable ordering and loss. Throws on failure.</summary>
        public static void SelfCheck()
        {
            var t = new NetSimTransport { latencyMs = 100, jitterMs = 50, lossPercent = 100, rng = new System.Random(1), localId = 1 };
            var got = new List<int>();
            for (int i = 0; i < 20; i++)
            {
                int n = i;
                t.Send(new NetMessage { from = 1, to = 2, channel = Channel.Reliable, bytes = 10, deliver = () => got.Add(n) }, 0);
            }
            bool lostDelivered = false;
            t.Send(new NetMessage { from = 1, to = 2, channel = Channel.Unreliable, bytes = 10, deliver = () => lostDelivered = true }, 0);

            t.Tick(0.049);
            Check(got.Count == 0, "delivered before minimum latency");
            t.Tick(1);
            Check(got.Count == 20, $"reliable lost: {got.Count}/20");
            for (int i = 0; i < 20; i++) Check(got[i] == i, "reliable reordered");
            Check(!lostDelivered, "100% loss still delivered unreliable");
            Check(t.LocalOutBytesPerSecond(0.5) == 210, "bandwidth counter");
            Check(t.LocalOutBytesPerSecond(2) == 0, "bandwidth window");
            Debug.Log("[ClientSimNet] Transport self-check passed.");
        }

        static void Check(bool ok, string msg)
        {
            if (!ok) throw new Exception("[ClientSimNet] Self-check failed: " + msg);
        }
    }
}
