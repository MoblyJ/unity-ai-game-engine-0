---
name: unity-game-builder
description: Master router + memory map for building ANY game live in Unity via the unity_* MCP tools. Maps a requested game feature (NPC AI, movement, multiplayer, performance/ECS, learning agents, niche subsystems, API lookups) to the right capability skill, the proven open-source repo under repo/, and a ready grounded-search query. Start here whenever a request is bigger than a few primitives (a fighting game, an RPG NPC, a multiplayer match, a crowd sim, "make the enemy smart").
---

# unity-game-builder — build any game without gaps, memory-efficiently

You build live in the user's Unity Editor with the `unity_*` MCP tools. This skill is the **memory
map**: instead of loading whole engine repos into context, you (1) pick the capability, (2) pull the
tight recipe from the matching skill, (3) **grounded-search** the indexed repo for the exact current
API, then (4) build + verify. That keeps context small and coverage complete.

## Router — feature → skill → repo → grounded search

| The user wants… | Use skill | Reference repo (`repo/…`) | Install | Grounded search (engine-ai) |
|---|---|---|---|---|
| NPC that decides (patrol/chase, states) | `unity-npc-behavior` | fluid-behavior-tree, NPBehave | copy folder / scoped UPM | `search_repo(repo/NPBehave, …)` · `repo/fluid-behavior-tree` |
| NPC that plans multi-step goals (gather→craft) | `unity-npc-goap` | GOAP | UPM git URL | `search_repo(repo/GOAP, …)` |
| Smooth movement: seek/flee/wander/flock/avoid | `unity-npc-movement` | unity-movement-ai | copy `Scripts/` | `search_repo(repo/unity-movement-ai, …)` |
| NPC that **learns** (reinforcement learning) | `unity-ml-agents` | ml-agents | `com.unity.ml-agents` + Python | grep `repo/ml-agents/docs` |
| Multiplayer (co-op, 1v1 online, host/client) | `unity-multiplayer` | com.unity.netcode.gameobjects | `com.unity.netcode.gameobjects` | grep repo |
| Thousands of entities / bullet-hell / crowds | `unity-ecs-performance` | EntityComponentSystemSamples | `com.unity.entities` | grep repo |
| Exact current Unity API (avoid deprecated) | `unity-api-lookup` | UnityCsReference | reference-only | grep `Modules/…`, `Runtime/Export/…` |
| Niche subsystem (pooling, save, inventory, procgen, dialogue) | `unity-subsystems` | awesome-* indexes | varies | grep the index READMEs |

If it's just primitives/materials/lighting/terrain/a spinning script → use **`unity-build`** directly; no capability skill needed.

## The efficient build loop (do this for any non-trivial game)

1. **Orient** — `unity_editor_version` (version + render pipeline), `unity_scene_info` (what exists). Reuse objects.
2. **Decompose** — break the game into features and map each row above. State the plan briefly.
3. **Per feature, pull the recipe** — open the matching skill (it has the license, install step, real
   API names, and a compile-ready C# sketch). Do **not** load the whole repo.
4. **Ground the exact API** — before writing C#, `search_repo(path=repo/<X>, query=…)` (repos are
   pre-indexed, see below) or grep `repo/UnityCsReference` for the precise signature. This is how you
   avoid deprecated/hallucinated APIs.
5. **Install deps if needed** — add the UPM package to `Packages/manifest.json`, or copy the library
   folder into `Assets/`. A domain reload follows; wait for a clean compile.
6. **Build via `unity_*`** — `unity_create_script` (full C#), `unity_add_component`/`unity_set_component`,
   `unity_create_object`, etc. Wire components in the same call where possible.
7. **Verify (mandatory)** — `unity_console_logs` (clean compile), then `unity_play` + `unity_screenshot`
   (game view) to confirm behavior, then `unity_stop`. Fix and repeat until it matches the ask.

## Pre-indexed repos (grounded memory — use `search_repo`, don't reload)

Already indexed with engine-ai `index_repo` (query with `search_repo`):
`repo/NPBehave`, `repo/fluid-behavior-tree`, `repo/unity-movement-ai`, `repo/GOAP`.

For the large repos (`UnityCsReference`, `com.unity.netcode.gameobjects`, `ml-agents`,
`EntityComponentSystemSamples`), index on demand with `index_repo(path)` the first time you need one,
or just `grep -rn <symbol> repo/<X>/<subfolder>` for a targeted lookup.

## Rules that keep it gap-free

- **License-aware:** MIT/Apache repos → you may copy their runtime into the project. `UnityCsReference`
  is **reference-only** — read it to verify APIs, never copy/ship it. Unity Companion License packages
  (netcode, entities) → install via UPM, don't vendor.
- **Decision layer ≠ movement layer.** A brain (behavior tree / GOAP / ML) decides *what*; steering or
  NavMesh does *how to move*. Compose them — don't make one do both.
- **Match effort to the ask.** Quick game → behavior tree + steering. Only reach for GOAP (emergent
  goals), ECS (massive scale), or ML-Agents (learned behavior) when the request truly needs it.
- **Always verify with a screenshot before claiming done.**
