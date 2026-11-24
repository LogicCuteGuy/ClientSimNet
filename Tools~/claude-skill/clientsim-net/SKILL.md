---
name: clientsim-net
description: Test and debug networked VRChat UdonSharp (synced variables, RequestSerialization/OnDeserialization, ownership, network events, late join, pickups/ObjectSync, master/player leave) with the ClientSim Net multiplayer simulator in one Unity editor. Use whenever writing, reviewing or debugging networked Udon in a project that has Packages/com.logiccuteguy.clientnetsim, before claiming networked code works.
---

Read `Packages/com.logiccuteguy.clientnetsim/AGENTS.md` in full and follow it: the mental model,
the `clientsim_net` unity-mcp tool (or `NetSimApi` via `execute_code`) for driving remote players, the scenario checklist, and the reporting rule.
For general UdonSharp rules also load `unity-vrc-udon-sharp`; for driving the editor load `unity-mcp-skill`.
