using System;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace ClientSimNet.Mcp
{
    /// <summary>
    /// unity-mcp tool "clientsim_net": drives NetSimApi (fake remote players, network, POV) and returns the
    /// AI report. Compiled only when com.coplaydev.unity-mcp is installed. Play mode required (except "status").
    /// </summary>
    [McpForUnityTool("clientsim_net", Description =
        "VRChat multiplayer simulator (ClientSim Net). Play mode only. Actions: " +
        "status | report | write_report | add_player(player) | leave(player) | rejoin(player) | master_leaves | " +
        "take_ownership(player,obj) | send_state(player,obj,json) | send_event(player,obj,target,event,args) | " +
        "grab(player,obj) | drop(player) | use(player) | jump(player) | interact(obj[,player]: no player = you, else their client runs it) | goto(player,x,y,z) | mode(player,mode) | " +
        "preset(name: Perfect|LAN|Normal|Bad) | network(latency,jitter,loss) | flush | view(player,third_person) | debugger(show: open/close the Net Debugger window) | save_stats(path?: saves network stats JSON, returns path + per player/kind totals). " +
        "Objects by name, prefix, path or 'Name@Owner'. Actions return the report unless report=false. " +
        "Read Packages/com.logiccuteguy.clientnetsim/AGENTS.md for scenarios.")]
    public static class NetSimMcpTool
    {
        public class Parameters
        {
            [ToolParameter("Action to run (see tool description)")] public string action { get; set; }
            [ToolParameter("Remote player displayName ('You' for view = your own)", Required = false)] public string player { get; set; }
            [ToolParameter("GameObject name, prefix, path or 'Name@Owner'", Required = false)] public string obj { get; set; }
            [ToolParameter("send_state: synced values as JSON object, e.g. {\"_count\":5}", Required = false)] public string json { get; set; }
            [ToolParameter("send_event: All | Others | Owner | Self", Required = false)] public string target { get; set; }
            [ToolParameter("send_event: event/method name", Required = false)] public string @event { get; set; }
            [ToolParameter("send_event: arguments as JSON array, e.g. [1,\"hi\"]", Required = false, DefaultValue = "[]")] public string args { get; set; }
            [ToolParameter("preset name, or mode: Idle | FollowYou | Wander | GoTo", Required = false)] public string name { get; set; }
            [ToolParameter("network: latency ms", Required = false)] public float? latency { get; set; }
            [ToolParameter("network: jitter ms", Required = false)] public float? jitter { get; set; }
            [ToolParameter("network: loss percent", Required = false)] public float? loss { get; set; }
            [ToolParameter("goto position", Required = false)] public float? x { get; set; }
            [ToolParameter("goto position", Required = false)] public float? y { get; set; }
            [ToolParameter("goto position", Required = false)] public float? z { get; set; }
            [ToolParameter("view: third person camera", Required = false, DefaultValue = "false")] public bool? third_person { get; set; }
            [ToolParameter("save_stats: file path (default Library/ClientSimNet/netstats-*.json)", Required = false)] public string path { get; set; }
            [ToolParameter("debugger: open (true) or close (false) the Net Debugger window", Required = false, DefaultValue = "true")] public bool? show { get; set; }
            [ToolParameter("Flush the network before the report (deterministic)", Required = false, DefaultValue = "false")] public bool? flush { get; set; }
            [ToolParameter("Include the report in the result", Required = false, DefaultValue = "true")] public bool? report { get; set; }
            [ToolParameter("Log lines in the report", Required = false, DefaultValue = "50")] public int? log { get; set; }
        }

        public static object HandleCommand(JObject p)
        {
            string action = S(p, "action")?.ToLowerInvariant();
            if (string.IsNullOrEmpty(action)) return new ErrorResponse("'action' is required");
            if (action == "status")
                return new SuccessResponse("status", new { playing = EditorApplication.isPlaying, ready = NetSimApi.IsReady });
            if (!NetSimApi.IsReady)
                return new ErrorResponse(EditorApplication.isPlaying
                    ? "ClientSim Net is not ready yet (or disabled in VRChat SDK > ClientSim Net). Retry in a moment."
                    : "Not in Play mode. Enter Play mode first (e.g. manage_editor action=play), then poll action=status until ready.");

            try
            {
                string player = S(p, "player"), obj = S(p, "obj");
                string msg = action;
                switch (action)
                {
                    case "report": break;
                    case "write_report": msg = NetSimApi.WriteReport(null, I(p, "log", 200)); break;
                    case "add_player": NetSimApi.AddPlayer(Req(player, "player")); break;
                    case "leave": NetSimApi.Leave(Req(player, "player")); break;
                    case "rejoin": NetSimApi.Rejoin(Req(player, "player")); break;
                    case "master_leaves": NetSimApi.MasterLeaves(); break;
                    case "take_ownership": NetSimApi.TakeOwnership(Req(player, "player"), Req(obj, "obj")); break;
                    case "send_state":
                        if (!NetSimApi.SendState(Req(player, "player"), Req(obj, "obj"), Json(p["json"])))
                            return new ErrorResponse("send_state was rejected (see report.log)", Report(p));
                        break;
                    case "send_event":
                        NetSimApi.SendEvent(Req(player, "player"), Req(obj, "obj"), S(p, "target") ?? "All",
                            Req(S(p, "event"), "event"), p["args"] == null ? "[]" : Json(p["args"]));
                        break;
                    case "grab": NetSimApi.Grab(Req(player, "player"), Req(obj, "obj")); break;
                    case "drop": NetSimApi.Drop(Req(player, "player")); break;
                    case "interact":
                        if (player == null) NetSimApi.Interact(Req(obj, "obj")); else NetSimApi.InteractAs(player, Req(obj, "obj"));
                        break;
                    case "use": NetSimApi.Use(Req(player, "player")); break;
                    case "jump": NetSimApi.Jump(Req(player, "player")); break;
                    case "goto": NetSimApi.GoTo(Req(player, "player"), new Vector3(F(p, "x", 0), F(p, "y", 0), F(p, "z", 0))); break;
                    case "mode":
                        if (!Enum.TryParse(Req(S(p, "name"), "name"), true, out NetSimPuppet.Mode m))
                            return new ErrorResponse("mode name: Idle | FollowYou | Wander | GoTo");
                        NetSimApi.SetMode(Req(player, "player"), m);
                        break;
                    case "preset": NetSimApi.Preset(Req(S(p, "name"), "name")); break;
                    case "network": NetSimApi.SetNetwork(F(p, "latency", 0), F(p, "jitter", 0), F(p, "loss", 0)); break;
                    case "flush": NetSimApi.Flush(); break;
                    case "debugger": if (p["show"]?.Value<bool>() ?? true) NetSimDebuggerWindow.Open(); else if (EditorWindow.HasOpenInstances<NetSimDebuggerWindow>()) EditorWindow.GetWindow<NetSimDebuggerWindow>().Close(); break;
                    case "save_stats":
                        string file = System.IO.Path.GetFullPath(NetSimApi.SaveStats(p["path"]?.ToString()));
                        var sum = NetSimApi.StatsSummary();
                        return new SuccessResponse("saved (totals below; the file's samples[] is large, don't read it whole)",
                            new { path = file, sum.seconds, sum.sampleSeconds, sum.players, sum.kinds });
                    case "view": NetSimApi.View(player ?? "You", p["third_person"]?.Value<bool>() ?? false); break;
                    default: return new ErrorResponse($"Unknown action '{action}'. See the tool description.");
                }
                if (p["flush"]?.Value<bool>() == true) NetSimApi.Flush();
                return new SuccessResponse(msg, p["report"]?.Value<bool>() == false ? null : Report(p));
            }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException)
            {
                return new ErrorResponse(e.Message);
            }
        }

        static JObject Report(JObject p) => JObject.Parse(NetSimApi.ReportJson(I(p, "log", 50), false));

        static string S(JObject p, string k) { var t = p[k]; return t == null || t.Type == JTokenType.Null ? null : t.ToString(); }
        static float F(JObject p, string k, float d) => p[k] == null || p[k].Type == JTokenType.Null ? d : p[k].Value<float>();
        static int I(JObject p, string k, int d) => p[k] == null || p[k].Type == JTokenType.Null ? d : p[k].Value<int>();
        // AI clients send JSON either as a string or as an inline object/array
        static string Json(JToken t) => t == null ? "{}" : t.Type == JTokenType.String ? (string)t : t.ToString(Newtonsoft.Json.Formatting.None);
        static string Req(string v, string name) => string.IsNullOrEmpty(v) ? throw new ArgumentException($"'{name}' is required for this action") : v;
    }
}
