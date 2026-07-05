---
name: unity-build
description: Build something in the live Unity Editor from a natural-language request. Researches the latest correct Unity API when needed, constructs it via the unity_* MCP tools, then verifies by reading the console and taking a screenshot. Use when the user asks to create/add/build objects, materials, environments, terrain, lighting, or behaviour scripts in Unity (e.g. "add a red bouncing ball", "make a night forest", "give the player a jump script").
---

# unity-build — Claude as a Unity game developer

You build directly in the user's open Unity Editor through the `unity-editor` MCP server
(tools prefixed `unity_`). Changes appear live in the Scene view. Work in this loop:

> **Bigger than primitives?** For NPC AI, movement, multiplayer, ECS/performance, learning agents, or
> niche subsystems, start from the **`unity-game-builder`** skill — it's the memory map that routes each
> feature to a capability skill + a proven repo under `repo/` + a grounded `search_repo` query, so you
> build from verified patterns instead of guessing. This `unity-build` loop still applies for the actual
> object/script/verify work.

## 1. Orient (cheap, always do this first)
- Call `unity_editor_version` once per session — it tells you the Unity version and the active
  render pipeline (URP / HDRP / Built-in) so you pick the right shaders and APIs.
- Call `unity_scene_info` to see the active scene + hierarchy. Reuse existing objects rather
  than duplicating them; `unity_find` locates things by name/tag/component.
- If the bridge is unreachable, the tool returns a `⚠️` message — tell the user to open Unity
  and click **Window > Agent Bridge > Start**, then retry. Do not guess object ids.

## 2. Research the latest method (only when it matters)
Unity APIs and render pipelines change between versions. Before writing C# or using an API you
are not certain is current, launch a research subagent:

> Use the Agent tool (subagent_type "general-purpose" or the deep-research skill) with WebSearch
> + WebFetch to find the CURRENT, non-deprecated Unity API/technique for `<feature>`, targeting
> Unity 6 / 2022 LTS. Return the exact API calls, required namespaces, and any URP-vs-Built-in
> differences.

Skip research for trivial primitives (cube, sphere, move, color). Use it for: physics setups,
particle/VFX, shaders, input system, NavMesh, animation, anything you'd otherwise guess at.

**Local ground-truth (use before writing C#):** Unity's real source is cloned at
`repo/UnityCsReference/` (reference-only license — read it, never copy/ship it). Grep it for the
EXACT current signature of any Unity API before you use it — this is how we catch APIs that Unity 6
turned into hard errors (e.g. `Object.GetInstanceID()` → obsolete-error, `PhysicMaterial` →
`PhysicsMaterial`). Example: `grep -rn "class PhysicsMaterial" repo/UnityCsReference/Modules/Physics`.
If a type is `[Obsolete(..., true)]`, call it via reflection instead (see `BridgeUtil.Iid` /
`BridgeUtil.FindType` for the pattern) so the assembly still compiles.

## 3. Build with the unity_* tools
Compose primitives — the tools are small on purpose:
- Objects: `unity_create_object` (primitive + transform + color), `unity_modify_object`.
- Look: `unity_create_material` (assign to a renderer), `unity_set_lighting` (sun/fog/ambient/skybox).
- Behaviour: `unity_create_script` (write full C#; class name must match `name`), then
  `unity_add_component` / the script's `attach_to` to put it on an object.
- Structure: `unity_add_component`, `unity_set_component`, prefabs, scenes.
Prefer setting values in the same call (e.g. `properties` on `unity_add_component`).

## 4. Verify — this is mandatory, it's how you "see live what happens"
After any `unity_create_script` or component work:
1. `unity_console_logs` (min_severity "warning") — confirm a CLEAN compile. If there are
   compile errors, fix the C# and re-write with `unity_create_script`; do not proceed.
2. `unity_focus` on the key object, then `unity_screenshot` — actually look at the result.
   If it doesn't match the request, adjust and repeat.
Report what you built with the screenshot, and only claim success after a clean compile + a
screenshot that matches the ask.

## Notes
- Everything is undoable in-editor (Ctrl-Z) — the tools wrap changes in Undo.
- Save when the user asks or a milestone is reached: `unity_save_scene`.
- To test runtime behaviour, `unity_play` then `unity_screenshot` (game view), then `unity_stop`.
