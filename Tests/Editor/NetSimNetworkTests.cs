using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using VRC.SDK3.UdonNetworkCalling;
using VRC.SDKBase;
using VRC.Udon;

namespace ClientSimNet.Tests
{
    /// <summary>EditMode tests for the loopback wire, size estimates and the SDK hooks NetSim depends on.</summary>
    public class NetSimNetworkTests
    {
        const int Local = 1, A = 2, B = 3;

        static NetSimTransport Wire(float latency = 100, float jitter = 0, float loss = 0) =>
            new NetSimTransport { latencyMs = latency, jitterMs = jitter, lossPercent = loss, rng = new System.Random(42), localId = Local };

        static void Send(NetSimTransport t, int from, int to, Channel ch, Action deliver, double now = 0, int bytes = 10) =>
            t.Send(new NetMessage { from = from, to = to, channel = ch, bytes = bytes, deliver = deliver }, now);

        // ------------------------------------------------------------ latency

        [Test]
        public void Delivers_AfterLatency_NotBefore()
        {
            var t = Wire(latency: 100);
            bool got = false;
            Send(t, Local, A, Channel.Reliable, () => got = true);
            t.Tick(0.099);
            Assert.IsFalse(got, "delivered before latency");
            t.Tick(0.100);
            Assert.IsTrue(got);
            Assert.AreEqual(0, t.Queued);
        }

        [Test]
        public void Jitter_StaysWithinBounds()
        {
            var t = Wire(latency: 100, jitter: 30);
            var msgs = new List<NetMessage>();
            for (int i = 0; i < 500; i++)
            {
                var m = new NetMessage { from = Local, to = A, channel = Channel.Unreliable, bytes = 1 };
                t.Send(m, 0);
                msgs.Add(m);
            }
            Assert.That(msgs.Min(m => m.deliverAt), Is.GreaterThanOrEqualTo(0.070 - 1e-9));
            Assert.That(msgs.Max(m => m.deliverAt), Is.LessThanOrEqualTo(0.130 + 1e-9));
            Assert.Greater(msgs.Select(m => m.deliverAt).Distinct().Count(), 1, "jitter had no effect");
        }

        [Test]
        public void ExtraDelay_IsAdded()
        {
            var t = Wire(latency: 100);
            var m = new NetMessage { from = Local, to = A, channel = Channel.Reliable };
            t.Send(m, 1.0, extraDelay: 0.5);
            Assert.AreEqual(1.6, m.deliverAt, 1e-9);
        }

        // ------------------------------------------------------------ reliability / ordering

        [Test]
        public void Reliable_NeverLost_AndInOrder_UnderJitterAndLoss()
        {
            var t = Wire(latency: 100, jitter: 90, loss: 100);
            var got = new List<int>();
            for (int i = 0; i < 200; i++)
            {
                int n = i;
                Send(t, Local, A, Channel.Reliable, () => got.Add(n), now: i * 0.001);
            }
            t.Tick(10);
            CollectionAssert.AreEqual(Enumerable.Range(0, 200), got);
        }

        [Test]
        public void Reliable_OrderingIsPerPair_OtherPairsNotHeldBack()
        {
            var t = Wire(latency: 100);
            var got = new List<string>();
            t.Send(new NetMessage { from = A, to = Local, channel = Channel.Reliable, deliver = () => got.Add("A") }, 0, extraDelay: 5);
            Send(t, B, Local, Channel.Reliable, () => got.Add("B"));
            t.Tick(0.2);
            CollectionAssert.AreEqual(new[] { "B" }, got, "B's message waited on A's");
            t.Tick(6);
            CollectionAssert.AreEqual(new[] { "B", "A" }, got);
        }

        [Test]
        public void Unreliable_LossRate_MatchesSetting()
        {
            var t = Wire(loss: 30);
            int delivered = 0;
            for (int i = 0; i < 2000; i++) Send(t, Local, A, Channel.Unreliable, () => delivered++);
            t.Tick(10);
            Assert.That(delivered, Is.InRange(1200, 1600), $"~70% expected, got {delivered}/2000");
        }

        [Test]
        public void Unreliable_ZeroLoss_AllDelivered()
        {
            var t = Wire(loss: 0);
            int delivered = 0;
            for (int i = 0; i < 100; i++) Send(t, Local, A, Channel.Unreliable, () => delivered++);
            t.Tick(10);
            Assert.AreEqual(100, delivered);
        }

        [Test]
        public void Unreliable_CanReorder_UnderJitter()
        {
            var t = Wire(latency: 100, jitter: 80);
            var got = new List<int>();
            for (int i = 0; i < 50; i++)
            {
                int n = i;
                Send(t, Local, A, Channel.Unreliable, () => got.Add(n), now: i * 0.01);
            }
            t.Tick(10);
            Assert.AreEqual(50, got.Count);
            CollectionAssert.AreNotEqual(Enumerable.Range(0, 50), got, "unreliable never reordered: jitter not simulated");
        }

        [Test]
        public void Tick_DeliversInDeliverAtOrder()
        {
            var t = Wire(latency: 100);
            var got = new List<string>();
            t.Send(new NetMessage { from = A, to = Local, channel = Channel.Unreliable, deliver = () => got.Add("late") }, 0, extraDelay: 0.3);
            t.Send(new NetMessage { from = B, to = Local, channel = Channel.Unreliable, deliver = () => got.Add("early") }, 0);
            t.Tick(1);
            CollectionAssert.AreEqual(new[] { "early", "late" }, got);
        }

        // ------------------------------------------------------------ robustness

        [Test]
        public void ThrowingHandler_DoesNotBlockOtherMessages()
        {
            var t = Wire();
            bool second = false;
            Send(t, Local, A, Channel.Reliable, () => throw new InvalidOperationException("boom"));
            Send(t, Local, A, Channel.Reliable, () => second = true);
            LogAssert.Expect(LogType.Exception, new System.Text.RegularExpressions.Regex("boom"));
            t.Tick(1);
            Assert.IsTrue(second);
        }

        [Test]
        public void Clear_DropsInFlight_AndResetsOrderingAndBandwidth()
        {
            var t = Wire(latency: 100);
            bool got = false;
            t.Send(new NetMessage { from = Local, to = A, channel = Channel.Reliable, bytes = 50, deliver = () => got = true }, 0, extraDelay: 10);
            t.Clear();
            Assert.AreEqual(0, t.Queued);
            Assert.AreEqual(0, t.LocalOutBytesPerSecond(0));

            var m = new NetMessage { from = Local, to = A, channel = Channel.Reliable };
            t.Send(m, 0);
            Assert.AreEqual(0.1, m.deliverAt, 1e-9, "reliable ordering clamp survived Clear");
            t.Tick(20);
            Assert.IsFalse(got);
        }

        [Test]
        public void Logged_ReportsSentDeliveredLost()
        {
            var t = Wire(loss: 100);
            var log = new List<string>();
            t.Logged += (m, s) => log.Add($"{m.channel}:{s}");
            Send(t, Local, A, Channel.Reliable, null);
            Send(t, Local, A, Channel.Unreliable, null);
            t.Tick(1);
            CollectionAssert.AreEqual(new[] { "Reliable:sent", "Unreliable:lost", "Reliable:delivered" }, log);
        }

        // ------------------------------------------------------------ bandwidth

        [Test]
        public void Bandwidth_CountsOnlyLocalSends_OverOneSecondWindow()
        {
            var t = Wire();
            Send(t, Local, A, Channel.Reliable, null, now: 0.0, bytes: 100);
            Send(t, Local, B, Channel.Unreliable, null, now: 0.5, bytes: 50);
            Send(t, A, Local, Channel.Reliable, null, now: 0.5, bytes: 999); // remote → not ours
            Assert.AreEqual(150, t.LocalOutBytesPerSecond(0.9));
            Assert.AreEqual(50, t.LocalOutBytesPerSecond(1.2));
            Assert.AreEqual(0, t.LocalOutBytesPerSecond(2.0));
        }

        [Test]
        public void Bandwidth_LostPacketsStillCount()
        {
            var t = Wire(loss: 100);
            Send(t, Local, A, Channel.Unreliable, null, bytes: 70);
            Assert.AreEqual(70, t.LocalOutBytesPerSecond(0.1), "a dropped packet still used upload bandwidth");
        }

        [Test]
        public void Bandwidth_LimitConstantIsAbove10KB()
        {
            Assert.AreEqual(11 * 1024, NetSim.BandwidthLimit);
            Assert.AreEqual(200, NetSim.ContinuousLimit);
            Assert.AreEqual(280496, NetSim.ManualLimit);
        }

        [Test]
        public void SelfCheck_Passes()
        {
            LogAssert.Expect(LogType.Log, "[ClientSimNet] Transport self-check passed.");
            NetSimTransport.SelfCheck();
        }

        // ------------------------------------------------------------ payload size / parsing

        [TestCase(true, 1)]
        [TestCase(7, 4)]
        [TestCase(7L, 8)]
        [TestCase(1.5f, 4)]
        [TestCase(1.5, 8)]
        [TestCase("abc", 8)]
        [TestCase("", 2)]
        [TestCase(null, 1)]
        public void SizeOf_Primitives(object value, int bytes) => Assert.AreEqual(bytes, NetSim.SizeOf(value));

        [Test]
        public void SizeOf_UnityTypesAndArrays()
        {
            Assert.AreEqual(12, NetSim.SizeOf(Vector3.one));
            Assert.AreEqual(16, NetSim.SizeOf(Quaternion.identity));
            Assert.AreEqual(2 + 3 * 4, NetSim.SizeOf(new[] { 1, 2, 3 }));
            Assert.AreEqual(2 + 2 * 12, NetSim.SizeOf(new[] { Vector3.zero, Vector3.up }));
            Assert.AreEqual(2 + 5 * 2, NetSim.SizeOf(new VRCUrl("hello")));
        }

        [Test]
        public void SizeOf_LongStringExceedsContinuousLimit()
        {
            Assert.Greater(NetSim.SizeOf(new string('x', 100)), NetSim.ContinuousLimit);
            Assert.LessOrEqual(NetSim.SizeOf(new string('x', 99)), NetSim.ContinuousLimit);
        }

        [Test]
        public void TryParse_AcceptsObject_RejectsOthers()
        {
            LogAssert.ignoreFailingMessages = true; // VRCJson may log on malformed input
            Assert.IsTrue(NetSim.TryParse("{\"count\":3,\"name\":\"x\"}", out var d, out var err), err);
            Assert.AreEqual(3, (int)d["count"].Number);
            Assert.AreEqual("x", d["name"].String);

            Assert.IsFalse(NetSim.TryParse("[1,2]", out _, out err));
            Assert.IsNotEmpty(err);
            Assert.IsFalse(NetSim.TryParse("not json", out _, out _));
            Assert.IsFalse(NetSim.TryParse(null, out _, out _));
        }

        // ------------------------------------------------------------ SDK hooks (fail here first after an SDK update)

        const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        [Test]
        public void Sdk_UdonBehaviourHasProgramField() =>
            Assert.IsNotNull(NetSim.ProgramField, "UdonBehaviour._program moved (SDK changed?): synced values would decode by heap type");

        [Test]
        public void Sdk_SendCustomNetworkEventProxy_HasExpectedShape()
        {
            var prop = typeof(NetworkCalling).GetProperty("SendCustomNetworkEventProxy", AnyStatic);
            Assert.IsNotNull(prop, "NetworkCalling.SendCustomNetworkEventProxy missing: SDK changed, NetSim can't route events");
            Assert.IsTrue(prop.CanRead && prop.CanWrite);
            var invoke = prop.PropertyType.GetMethod("Invoke");
            Assert.IsNotNull(invoke);
            var ps = invoke.GetParameters();
            Assert.AreEqual(4, ps.Length, "proxy delegate signature changed");
            Assert.AreEqual(typeof(string), ps[2].ParameterType);

            // NetSim binds an instance method with the same signature; make sure binding still works.
            var target = typeof(NetSim).GetMethod("SendCustomNetworkEventProxy", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(target);
            var go = new GameObject("netsim-test");
            go.SetActive(false);
            try
            {
                var sim = go.AddComponent<NetSim>();
                Assert.DoesNotThrow(() => Delegate.CreateDelegate(prop.PropertyType, sim, target));
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        [Test]
        public void Sdk_WithNetworkCallingContext_Exists()
        {
            var m = typeof(NetworkCalling).GetMethods(AnyStatic).FirstOrDefault(x =>
                x.Name == "WithNetworkCallingContext" && !x.IsGenericMethod &&
                x.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(VRCPlayerApi), typeof(Action) }));
            Assert.IsNotNull(m, "NetworkCalling.WithNetworkCallingContext(VRCPlayerApi, Action) missing: CallingPlayer emulation broken");
            Assert.DoesNotThrow(() => Delegate.CreateDelegate(typeof(Action<VRCPlayerApi, Action>), m));
        }

        [Test]
        public void Sdk_RequestSerializationHook_Exists()
        {
            var f = (MemberInfo)typeof(UdonBehaviour).GetField("RequestSerializationHook", AnyStatic)
                    ?? typeof(UdonBehaviour).GetProperty("RequestSerializationHook", AnyStatic);
            Assert.IsNotNull(f, "UdonBehaviour.RequestSerializationHook missing: RequestSerialization can't be intercepted");
        }

        // ------------------------------------------------------------ NetSimApi

        [Test]
        public void Api_ThrowsClearly_WhenNotRunning()
        {
            Assert.IsFalse(NetSimApi.IsReady);
            var e = Assert.Throws<InvalidOperationException>(() => NetSimApi.Flush());
            StringAssert.Contains("Play mode", e.Message);
        }

        [Test]
        public void Debugger_SaveLoad_KeepsTimelineAndTotals()
        {
            var go = new GameObject("dbg");
            try
            {
                var d = go.AddComponent<NetSimDebugger>();
                var s = new NetSimDebugger.Sample { index = 10 };
                s.Msgs.Add(new NetSimDebugger.Entry { status = "sent", kind = "Sync", from = 1, to = 2, bytes = 8, fromName = "You", toName = "Bob" });
                s.Msgs.Add(new NetSimDebugger.Entry { status = "lost", kind = "Event", from = 1, to = 2, bytes = 30, fromName = "You", toName = "Bob" });
                s.Msgs.Add(new NetSimDebugger.Entry { status = "delivered", kind = "Sync", from = 1, to = 2, bytes = 8, fromName = "You", toName = "Bob" });
                d.Samples.Add(s);
                var c = d.BuildCapture();
                var you = c.players.Find(p => p.name == "You"); var bob = c.players.Find(p => p.name == "Bob");
                Assert.AreEqual(38, you.outBytes); Assert.AreEqual(1, you.lost); Assert.AreEqual(8, bob.inBytes);
                Assert.AreEqual(380, you.maxOutBytesPerSec, 0.01f);
                string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "netsim-stats-test.json");
                d.Save(path);
                var d2 = new GameObject("dbg2").AddComponent<NetSimDebugger>();
                d2.Load(path);
                Assert.AreEqual(1, d2.Samples.Count); Assert.AreEqual(3, d2.Samples[0].Msgs.Count); Assert.AreEqual("Bob", d2.Samples[0].Msgs[0].toName);
                Assert.IsFalse(d2.Recording);
                UnityEngine.Object.DestroyImmediate(d2.gameObject);
                System.IO.File.Delete(path);
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        [Test]
        public void Debugger_Rate_RollingOneSecondWindow()
        {
            var r = new NetSimDebugger.Rate();
            r.Add(0.0, 100); r.Add(0.5, 50); r.Lost++;
            Assert.AreEqual(150, r.BytesPerSec(0.9));
            Assert.AreEqual(2, r.PerSec(0.9));
            Assert.AreEqual(50, r.BytesPerSec(1.2), "first message left the 1 s window");
            Assert.AreEqual(0, r.BytesPerSec(3));
            Assert.AreEqual(2, r.Total); Assert.AreEqual(1, r.Lost); Assert.AreEqual(0.5, r.Last);
        }

        [Test]
        public void Api_Find_ByNamePrefixAndPath()
        {
            var root = new GameObject("NetSimApiRoot");
            var child = new GameObject("ZzApiTest Counter");
            child.transform.SetParent(root.transform);
            new GameObject("ZzApiTestSystem").transform.SetParent(root.transform); // like EventSystem vs "Events (...)"
            try
            {
                Assert.AreSame(child, NetSimApi.Find("ZzApiTest"), "whole-word prefix beats a longer word");
                Assert.AreSame(child, NetSimApi.Find("ZzApiTest Counter"));
                Assert.AreSame(child, NetSimApi.Find("zzapitest cou"));
                Assert.AreSame(child, NetSimApi.Find("NetSimApiRoot/ZzApiTest Counter"));
                Assert.Throws<ArgumentException>(() => NetSimApi.Find("DoesNotExist_xyz"));
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }
    }
}
