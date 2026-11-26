# unity-mcp bridge scripts (no MCP client needed)

Talk to the unity-mcp Unity bridge directly (TCP 127.0.0.1:6400; set `UNITY_MCP_PORT` if the MCP window shows another socket port). Python 3, stdlib only.
Pass this folder as the first argument (`D`) so the scripts can import `u.py`.

| Script | Does |
|---|---|
| `python u.py <tool> '<json>'` | one raw call, e.g. `u.py clientsim_net '{"action":"status"}'` |
| `python x.py D code.cs` | run C# through `execute_code` |
| `python t.py D scenario.json` | run a scenario: steps are `clientsim_net` params or `{"sleep":s}`; `"show":["Counter"]` prints those objects; prints only new log lines |
| `python cycle.py D [play]` | stop Play, refresh + compile, print console errors, optionally re-enter Play and wait until ready |
| `python replay.py D` | restart Play mode (no recompile) and wait until ready |
| `python wait.py D` / `ready.py D` | wait for the bridge / for ClientSim Net ready |

Scenarios (test scene `NetSimTest.unity`; run `setup.json` first, it adds Bob and Ann):
`counter.json`, `events.json`, `lock.json`, `deny_pickup_leave.json`, `spam_payload_master.json`.
