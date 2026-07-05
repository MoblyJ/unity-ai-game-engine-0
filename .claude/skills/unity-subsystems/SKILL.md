---
name: unity-subsystems
description: Find a proven open-source implementation for a niche game subsystem (object pooling, save/serialization, inventory, procedural generation, dialogue, tweening, character controllers, AI-content tools) by searching the curated meta-indexes in repo/. Use when the game needs a supporting system that isn't one of the core AI/movement/multiplayer/ECS capabilities — so you reuse a known-good approach instead of inventing one (no gaps).
---

# unity-subsystems — meta-index lookup for niche systems

Three curated indexes list hundreds of open-source Unity repos by subsystem. Grep them to find a proven
option, then read its README and adapt (respect each project's license).

## The index files (grep these)
- `repo/awesome-unity3d/README.md` — flat, per-subsystem: AI, Animation, Character Controllers 2D/3D,
  DOTS/ECS, Editor, Effects/Shaders, Networking, Node Graph, **Pooling System**, **Procedural Generation**,
  Scriptable Object, Serializer.
- `repo/AwesomeUnityCommunity/README.md` — larger/tagged: AI/ML, Audio, Animation/Tweening, AR/VR, Physics,
  Character Controllers, Networking, and a deep **Scripting** tree (Algorithms, **Patterns**, **Pooling**,
  **Serialization**, Coroutines/Threading), Rendering, Voxel, UI.
- `repo/ai-game-devtools/README.md` — Yuan-ManX's AI tools for game dev: **LLM & Tool** (AICommand,
  ChatGPT-API-unity, HuggingFace unity-api), Code, Image, Texture, Shader, Avatar, Animation, Audio, Voice.

## How to search
```
grep -iE 'pool|save|serial|procgen|procedural|inventory|dialogue|tween|navmesh|shader|voxel' \
  repo/awesome-unity3d/README.md repo/AwesomeUnityCommunity/README.md repo/ai-game-devtools/README.md
```
Swap keywords per need. First two indexes = engine/gameplay subsystems; third = AI-content generation.

## Highest-value picks for an AI game-builder
- **Object pooling** — reuse instead of Instantiate/Destroy (perf) → search `pool`. Prefer this over ECS
  for most "lots of bullets/enemies" cases.
- **Save/serialization** — persist progress → search `save`, `serial`.
- **Procedural generation** — levels/terrain/dungeons from a seed → search `procedural`, `procgen`.
- **Character controllers 3D** — ready-made FPS/third-person movement → search `controller`.

Cross-check any picked API against `unity-api-lookup` before writing C#, then build + verify as usual.
