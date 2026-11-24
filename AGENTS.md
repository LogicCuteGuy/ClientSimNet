# ClientSim Net: development guide for AI agents

Use this whenever you write, review or debug **networked UdonSharp** in a VRChat world that has the
`com.logiccuteguy.clientnetsim` package. Stock ClientSim hides most networking bugs (no latency, no
OnDeserialization, Owner/Others events run locally). ClientSim Net makes them show up. **Networked code
is not done until it has passed the scenarios below in Play mode with at least 2 remote players.**

## Mental model (what is real, what is not)

- One Unity editor, **one world copy per player**. Each remote player (puppet) gets its own copy of the scene
  (a hidden scene `NetSim World <name> [id]`), loaded fresh when they join, where **their Udon runs**:
  `Networking.LocalPlayer`, `player.isLocal`, `IsOwner`, `IsMaster` answer for them there. Their copy changes
  **only** through the network (synced vars → OnDeserialization, network events, ObjectSync, ownership).
  Anything not synced (a local toggle, an unsynced pickup, a non-synced field) stays different, like VRChat.
- Viewing as a player (POV / menu "View") renders **their** copy, so you can see what they see.
- `InteractAs`, puppet pickups and POV Control (ClientSim's player as them) run on the puppet's copy. A puppet grabbing a pickup without
  VRCObjectSync moves only their copy.
- So you can test **sender** and **receiver** code on every client: e.g. you click → Bob's copy gets it after
  latency (`GoIn(go, bobId)` / `UbIn(ub, bobId)` read Bob's copy); Bob clicks → yours gets it.
- Everything crossing the wire goes encode → bytes → delayed queue → decode, with VRChat's rules:
  - `RequestSerialization` from a non-owner is dropped (warning).
  - Manual sync: batched per 0.1 s tick, reliable, limit 280,496 B. Continuous: 10 Hz, unreliable, ~200 B.
    Over the limit: `OnPostSerialization(success=false)` and nothing is sent.
  - Events: `All` runs locally now + sends; `Others` never runs locally; `Owner` runs locally only if
    you own it; `Self` local only. Remote sends are paced at `MaxEventsPerSecond` (default 5/s).
    Remote-sent events run with `NetworkCalling.CallingPlayer` = the puppet.
  - `OnOwnershipRequest` runs on **both** the requester and the owner (VRChat docs). Your `SetOwner` (or your
    hand grabbing a pickup): your check, you own it at once, the remote owner's check runs after latency and a
    `false` takes it back one round trip later. A puppet's request: its own check, then the owner's, after latency.
    The callback is skipped when the owner left. A pickup in your hand is stolen unless `DisallowTheft`.
  - Late join (untick ClientSim "Local Player Is Master" before Play): the remote master sends every
    synced behaviour's state 0.5 s after the network settles.
  - Bandwidth warning above ~11 KB/s outbound.

## Workflow

1. Write the UdonSharp script following VRChat rules (owner writes, then `RequestSerialization`;
   visuals applied in both the writer path and `OnDeserialization`/FieldChangeCallback).
2. Make sure Unity compiled with no console errors (UdonSharp too).
3. Enter Play mode in the world scene (or the sample scene: Package Manager > ClientSim Net > Samples >
   Test World > Import, open `NetSimTest.unity`; `VRChat SDK/ClientSim Net - Build Test Scene` rebuilds it).
   For a guided tour open `NetSimShowcase.unity` (same sample; `VRChat SDK/ClientSim Net - Build Showcase Scene`
   rebuilds it): one signed station per use case, each sign's yellow line shows whose client you see and who owns it. Set the network to **Normal** (100 ms ± 30, 0.5% loss) for realism, or **Perfect** (0/0/0) when you
   need deterministic assertions.
4. Spawn 2 remote players, run the scenarios, read the log, fix, repeat.
5. Run the EditMode tests (`Test Runner > EditMode > ClientSimNet.Tests`) after changing the package itself.

## Driving it from code: `ClientSimNet.NetSimApi` (Play mode, e.g. unity-mcp `execute_code`)

Players by displayName (unique, as in VRChat: `AddPlayer` of an existing name returns that player). Objects by name, name prefix or hierarchy path; `"Name@Player"` picks the copy
owned by that player (PlayerObjects). Misuse throws an exception whose message says what's wrong.

```csharp
using ClientSimNet; using UnityEngine;
if (!NetSimApi.IsReady) return "not ready";          // enter Play mode, wait until the network settles
NetSimApi.Preset("Perfect");                          // Perfect | LAN | Normal | Bad, or SetNetwork(ms, jitter, loss%)
NetSimApi.AddPlayer("Bob"); NetSimApi.AddPlayer("Ann"); // puppet attached immediately

NetSimApi.Interact("Counter");                        // click as the local player
NetSimApi.TakeOwnership("Bob", "Counter");            // may be denied by OnOwnershipRequest
NetSimApi.Flush();                                    // deliver everything in flight now (order kept)
NetSimApi.SendState("Bob", "Counter", "{\"_count\":5}"); // Bob must own it; keys = UdonSharp field names
NetSimApi.SendEvent("Bob", "Events", "All", "Ping", "[1,\"hi\"]"); // All | Others | Owner | Self
NetSimApi.SendEvent("Bob", "PlayerCard@You", "Owner", "Cheer"); // PlayerObject copy owned by "You"
NetSimApi.Grab("Bob", "Pickup"); NetSimApi.Use("Bob"); NetSimApi.Drop("Bob"); // OnPickup/Use/OnDrop run on Bob's client
NetSimApi.InteractAs("Bob", "Counter");               // Bob clicks: his Interact runs with LocalPlayer = Bob, results arrive with latency
NetSimApi.Jump("Bob");
NetSimApi.GoTo("Bob", new Vector3(0, 0, 5)); NetSimApi.SetMode("Ann", NetSimPuppet.Mode.Wander);
NetSimApi.Leave("Ann"); NetSimApi.MasterLeaves();
NetSimApi.View("Bob"); NetSimApi.View("You");          // Game view POV (Bob's own-client position); report.pov
NetSimApi.Flush();
return NetSimApi.ReportJson();                        // or WriteReport() -> Library/ClientSimNet/report.json
```

### unity-mcp tool `clientsim_net` (preferred over execute_code)

With com.coplaydev.unity-mcp installed, the package registers the MCP tool `clientsim_net` (same actions as
above; it returns the report JSON unless `report=false`). Enter Play mode (`manage_editor action=play`),
poll `clientsim_net action=status` until `ready`, then e.g.:

```
clientsim_net action=preset name=Perfect
clientsim_net action=add_player player=Bob
clientsim_net action=take_ownership player=Bob obj=Counter
clientsim_net action=send_state player=Bob obj=Counter json={"_count":5} flush=true
clientsim_net action=send_event player=Bob obj=Events target=All event=Ping args=[1,"hi"] flush=true
clientsim_net action=interact obj=Counter flush=true
clientsim_net action=view player=Bob third_person=true
```
Other actions: `debugger show=true|false`, `save_stats path=...`, `report`, `write_report`, `leave`, `rejoin`, `master_leaves`, `grab`, `drop`, `goto x y z`,
`use`, `jump`, `interact player=Bob obj=...` (Bob's client runs it), `mode name=Wander`, `network latency jitter loss`, `flush`. Errors come back as `success=false` with the reason.
In Claude Code the tool is `mcp__UnityMCP__clientsim_net` (Play mode: `mcp__UnityMCP__manage_editor action=play`).
Setup once: Unity `Window > MCP for Unity` > Start Server > Claude Code > **Configure** (runs
`claude mcp add --scope local --transport http UnityMCP http://127.0.0.1:8080/mcp`), then restart Claude Code / `/mcp` reconnect.
If the tool is missing, check the server's tool list or groups (`manage_tools`) and reconnect once after install.
Without an MCP client (or for scripted runs): `Tools~/mcp/` has stdlib Python scripts that talk to the bridge
directly, plus ready scenarios for the sample scene (see its README).

`Flush()` ignores latency, so checks give the same result every run. For real-timing behaviour (pacing,
races, interpolation) keep a preset, then wait a few frames (next call) instead of flushing.
Local-player names in ClientSim are usually not "You": read `players` in the report first.

### Report JSON (`NetSimApi.ReportJson()` / `GetReport()`)

| Field | Meaning |
|---|---|
| `ready`, `hooksInstalled` | Simulator running; false => nothing below is meaningful |
| `latencyMs`, `jitterMs`, `lossPercent`, `inFlight`, `outBytesPerSec`, `bandwidthLimit` | Network state; `inFlight > 0` => results not settled yet |
| `warnings`, `errors` | Counts in the simulator log; **any error is a bug or a hit limit** |
| `players[]` | `id, name, isLocal, isMaster, mode, holding, position` |
| `objects[]` | Every active synced UdonBehaviour: `path, behaviour, sync, owner, local, remote, inSync, bytes, limit` |
| `objects[].local` / `remote` | Synced vars as JSON on you / what remotes last received |
| `objects[].inSync` | false => remotes hold other values (non-owner write, failed/oversized serialization, or never sent) |
| `objects[].sentPerSec`, `lastSentAgo`, `lastReceivedAgo` | Network debugger stats; seconds, `-1` = never (a Continuous object that never sends isn't owned or isn't changing) |
| `log[]` | Last N console lines `t, player, kind, type, text, obj`. `player` = whose console (VRChat client) it's in; `kind` = `out` sent, `in` received, `lost`, `udon` (Debug.Log from scripts; always the local client), `sim` (simulator note) |

Typical check: `Flush()`, `GetReport()`, then assert `errors == 0`, every object you touched is
`inSync`, the `owner` is who you expect, and the `log` contains the events you expect.

In-game menu (ClientSim's Esc/Tab menu): **ClientSim Net ▶** at the top right opens a page with network status
(latency, loss, in flight, your B/s vs limit, warnings/errors), presets, the player list (View / Rejoin / Leave), + Add player,
Master leaves (when a remote is master), 3rd person, Control, Net Debugger, Save stats. Built at runtime from the
menu's own widgets; the SDK package is untouched.
POV for humans: 👁 per player in the window, Alt+1..9 / Alt+0 (you) / Alt+[ ] / Alt+T (3rd person). While viewing a remote player with Control on, **ClientSim's own player becomes them**: their world is moved into the main scene (yours waits in its own physics scene), so ClientSim's walking, jumping, click-to-interact, pickups, tooltips and menu all act in their world as them (their Udon sees `LocalPlayer` = them; a grab makes them owner). You stand still where you were (`GetPosition` of you returns that spot) and get your body back when you view yourself again; a held pickup is dropped on every switch. Alt+C / "Control" toggles this. Switching POV also switches the console tab and "Act as".
Network debugger: dockable window **Window > Analysis > ClientSim Net Debugger** (also VRChat SDK menu;
`clientsim_net action=debugger` opens it), laid out like Unity's Profiler: a 30 s timeline of stacked bars (bytes on the wire per 0.1 s, by kind: Sync,
Event, Owner, Pose; click a legend entry to hide it), **Rec** pauses/resumes recording, **Clear**, **◀ ▶** step,
**Live** follows the newest sample, **Player** filters to one player (red line = their ~11 KB/s limit).
Click or drag on the chart to select a sample; the tabs below show it: **Messages** (every message sent /
delivered / lost in it with sender → receiver, size, latency, payload; poses optional), **Players** (out/in bytes,
messages, lost in it) and **Objects** (now: owner, size/limit, sends/s, last send/receive, `DIFF` when remotes hold
other values). Red = over a limit, lost or out of sync; "CLOGGED" = your outbound is over ~11 KB/s.
Saving stats: **Save** (or `NetSimApi.SaveStats(path?)` / `clientsim_net action=save_stats path=...`) writes
`Library/ClientSimNet/netstats-<time>.json`: `players[]` / `kinds[]` totals (`outBytes, outMsgs, inBytes, inMsgs,
lost, outBytesPerSec, maxOutBytesPerSec`) plus the whole timeline (`samples[].Msgs[]`, last 30 s). The window's
**Load…** opens such a file any time (also outside Play mode) to browse it like a live recording; **Save as…** picks a path.
For AI: `save_stats` already returns the totals (`players[]`, `kinds[]`, ~1-2 KB); use them. The file itself is
compact JSON but large (30 s, 3 players ≈ 400 KB ≈ 100k tokens), so **never read the whole file**; if you need
messages, filter `samples[]` for the time range / kind you care about with a script (e.g. Python `json`). The same numbers are in the report
(`objects[].sentPerSec`, `lastSentAgo`, `lastReceivedAgo`).
Per-player view for humans: **VRChat SDK > ClientSim Net Console** (tab per player, Copy button gives plain text).
To read one player's story in code: `GetReport(500).log.Where(l => l.player == "Bob")`.

Console lines from the package start with `[ClientSimNet]`; warnings/errors there are real bugs in
the world's networking (or a limit you hit), not noise.

## Scenario checklist (run all that apply to the feature)

| # | Scenario | How | Pass when |
|---|---|---|---|
| 1 | Local write reaches remotes | You interact | Log shows `Sync ... sent`; `RemoteState[ub]` equals `LocalStateJson(ub)` |
| 2 | Non-owner write | Puppet owns; you interact without `SetOwner` | Warning "not the owner"; your value is overwritten by the next remote send. Fix: take ownership first |
| 3 | Remote write reaches you | Puppet takes ownership, `SendStateAsPuppet` | `OnDeserialization` runs, **visuals update** (not only the field) |
| 4 | Late join | Untick "Local Player Is Master", Play | State correct after snapshot; code doesn't rely on `Start` seeing synced values |
| 5 | Owner/Others events | Puppet owns; you send `Owner` and `Others` | Neither runs locally; `All` does |
| 6 | Remote event + CallingPlayer | `SendEventAsPuppet` | Handler runs; any `CallingPlayer` authorization check behaves |
| 7 | Event spam | Send >5 events in one frame | Code tolerates delayed remote delivery; no logic assumes instant arrival |
| 8 | Size limit | Grow strings/arrays to max realistic size | No `OnPostSerialization success=false`; continuous stays < 200 B |
| 9 | Ownership race | You and puppet both take ownership + write within latency | Final state is consistent; no stuck/duplicated state |
| 10 | Denied ownership | `OnOwnershipRequest` returns false | Puppet does not get ownership; your state unchanged. Reverse: puppet owns, you `SetOwner` → you own it briefly, then it reverts; your code must not assume `SetOwner` sticks |
| 11 | Master leaves | Remove `Networking.Master` | Objects migrate; master-only logic resumes on the new master |
| 12 | Player leaves while owning | Puppet owns, then leaves | Ownership falls back; nothing waits for the departed player |
| 13 | Pickup | Puppet grabs while you hold | Theft or `DisallowTheft` behaves as designed; follows puppet hand |
| 14 | Bad network | Preset **Bad** (300 ms, 5% loss) | Continuous visuals tolerate loss; manual state still converges |
| 15 | PlayerObjects | Add puppets; each gets `<name> [id]` copy owned by them | Your copy: you write + serialize. Puppet's copy: `SendStateAsPuppet(bob, bobsCopyUb, ...)` updates you; you clicking it must *ask* via an `Owner` event, never write |
| 16 | PlayerObject request-to-owner | `SendEventAsPuppet(bob, yourCopyUb, Owner, "Cheer", ...)` | Your owner-side handler validates `CallingPlayer` and writes; a self-call is rejected |
| 17 | PlayerObject leave | `RemovePlayer(bob.Player)` | Bob's copy is destroyed; anything aggregating copies (leaderboard) refreshes and doesn't hold a dead reference |
| 18 | Unsynced pickup | Grab `Unsynced pickup` yourself; `Grab("Bob", "Unsynced")` | Has no `VRCObjectSync`, so it moves only on the holder's client. Bob grabs his own copy: viewing as Bob shows it in his hand, your copy stays put (and vice versa). Compare with `Pickup (ObjectSync)` |

## PlayerObjects (per-player synced data)

Prefer a `VRCPlayerObject` over a shared object with ownership hand-offs when data belongs to one
player (score, loadout, settings). ClientSim clones the scene template once per player, names it
`<template> [playerId]`, assigns network IDs on Play, and makes that player the permanent owner.
- Find a player's copy: `Networking.FindComponentInPlayerObjects(player, templateComponent)` (Udon-exposed).
- In `execute_code`: `GameObject.Find("PlayerCard (...) [2]")`, or search `FindObjectsOfType<UdonBehaviour>()`
  where `Networking.GetOwner(ub.gameObject) == bob.Player`.
- Never `SetOwner` on a PlayerObject; others request changes through `Owner` events (sample: `NetSimTestPlayerCard`
  + `NetSimTestLeaderboard`).

## Known limits

Verified against the VRChat docs (SDK 3.10.x): sync limits (Continuous ~200 B at ~10 Hz, Manual 280,496 B),
~11 KB/s outbound, `[NetworkCallable]` default 5/s queued on the sender (local runs bypass it), underscore
events blocked unless `[NetworkCallable]`, OnOwnershipRequest on both sides, ownership races resolved by
arrival order, late joiners get synced vars (not past events), locomotion from the world's walk/run/strafe/jump.
Checked live in Play mode (test scene): manual sync + `[FieldChangeCallback]` both ways, Owner/All/Others
routing, 5/s pacing (20 events trickle out over ~4 s, local runs at once), OnOwnershipRequest allow/deny on
requester and owner, pickup by a puppet (owner on their client before OnPickup), owner → master on leave.
Gotchas the real SDK enforces (ClientSim Net keeps them): a behaviour with sync mode **None** can't send
network events ("Unable to send network event ... SyncType 'None'"), use `NoVariableSync`; changing
`[UdonBehaviourSyncMode]` in the script doesn't update UdonBehaviours already in the scene, so re-check them.
Not like the real game:
- Remote worlds step their own physics scene (rigidbodies, collisions, OnCollision/OnTrigger in their Udon),
  but Udon `Physics.Raycast` etc. there query your scene, and the puppet's body isn't in its world (no player
  collider/OnPlayerTrigger there). ObjectSync copies are kinematic and follow your scene's object (where its
  physics runs), without their own interpolation. No cameras/audio listeners in remote worlds.
- PlayerObjects are shared (only your scene has the spawned copies); `FindComponentInPlayerObjects` from a
  remote world returns yours. VRCInstantiate in a remote world spawns into your scene.
- Ownership changes are global at once (except a puppet's own pending takeover); `OnOwnershipTransferred`
  reaches remote worlds when it reaches you.
- Player APIs other than `TeleportTo` (Immobilize, SetWalkSpeed, ...) on a puppet do nothing (ClientSim warns).
- Each player's world costs a full scene copy (CPU/memory scale with players).
- While you control someone: ClientSim's events that carry the local player (OnPlayerTriggerEnter, pickup
  `currentPlayer`, station events) pass **you**, not them; objects your Udon spawns without a parent then land in
  their world's scene.
- Puppets have no wall collision (they only follow the floor); voice/avatars are not simulated.

- Sync sizes (yours and puppets') are a per-type estimate, not real wire bytes.
- No VRCObjectPool spawning by puppets (use Take ownership on the pool object).
- Puppets taking a pickup back from each other mid-hold can glitch.
- SDK internals (`NetworkCalling.SendCustomNetworkEventProxy`, `WithNetworkCallingContext`) are reached
  by reflection; after an SDK update, run the EditMode tests first; `Sdk_*` tests fail if they moved.
- Disable the simulator: window toggle **Enable** (PlayerPrefs `ClientSimNet.Enabled` = 0), applies next Play.

## Reporting

When you claim networked code works, say which scenarios (#) you ran, at what network preset, and
paste the relevant `[ClientSimNet]` log lines. Untested scenarios must be listed as untested.
