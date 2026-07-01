# Integration Plan — Toward a Fully AI-Powered Unity Game Builder

*Plain-language plan. Read it, edit anything you want, then approve — and we integrate phase by phase.
Studied 13 open-source repos (in `repo/`) to decide what to add.*

---

## 1. What you already have (working today)

The **unity-editor MCP bridge** lets Claude Code build **live** in your open Unity Editor:
create objects, materials, lights, prefabs, scenes, and **write C# scripts** — plus read the
scene, read the console, and take screenshots to check its own work. (Proven: the red `ClaudeCube`.)

So today Claude can **BUILD** things. What it can't do yet:
make them **THINK** (AI behavior), **MOVE** well, run **FAST** at big scale, play **MULTIPLAYER**,
or **GENERATE art/worlds** from text. That's what the plan below adds.

---

## 2. What the studied tools give us (in simple words)

**A. Make NPCs think (decision-making)**
| Tool | What it gives us | Effort | License |
|---|---|---|---|
| **Fluid Behavior Tree** | Simple "if this, do that" brains written in C#. Easiest, and Claude is great at writing C#. | Low | not stated (verify) |
| **NPBehave** | Same idea but *event-driven* + a "blackboard" memory — efficient for many NPCs. | Low | not stated (verify) |
| **GOAP** | Smarter: you give goals + actions, and it *plans* the steps itself. Handles ~2000 agents. | Medium | MIT |
| **ML-Agents** | NPCs that **learn** by training (reinforcement learning). Most powerful, but needs a separate Python training step. | High | Apache-2.0 |

**B. Make NPCs move (locomotion)**
| Tool | What it gives us | Effort | License |
|---|---|---|---|
| **Unity Movement AI** | Steering: seek, flee, wander, pursue, flocking, obstacle avoidance — the "legs" under any brain. | Low | not stated (verify) |

**C. Performance & multiplayer**
| Tool | What it gives us | Effort | License |
|---|---|---|---|
| **DOTS/ECS Samples** | Patterns to run thousands of things fast (crowds, bullet-hell). New coding style to learn. | High | Unity Companion (usable) |
| **Netcode for GameObjects** | Turn a game **multiplayer** — fits our current GameObject style well. | Medium | Unity Companion (usable) |

**D. Make Claude's code correct**
| Tool | What it gives us | Effort | License |
|---|---|---|---|
| **UnityCsReference** | Unity's real source code — Claude reads it to write **accurate** Unity code (fewer bugs like the 6.5 one). | Low | **Reference-only — read, never ship** |

**E. Generate art & worlds from text** (from the AI-tools list — external AI backends)
- **AI Shader / Dream Textures / Blockade Labs** → generate materials, textures, skyboxes from a text prompt.
- **MeshAnything / TripoSR / BlenderMCP** → generate 3D models from text.
- **World models (Genie, Oasis, GameNGen…)** → frontier "generate a whole playable level" (experimental, watch-list).

**F. Precedents / rivals to learn from**
- **IvanMurzak Unity-MCP** — a bigger rival bridge (70+ tools): "call any method" via reflection, run C# at
  runtime, drive AI **inside a running game**, profiler capture, ready-made packs (NavMesh, Cinemachine,
  Terrain, Timeline…). We shouldn't run it *alongside* ours (they'd clash), but we should **copy its best ideas**.
- **unity-cli-plugin, AICommand** — same concept as ours; good sources of tool ideas.
- **Prowl** — an MIT, Unity-like engine we could fully own/modify. Interesting long-term, but early-stage; ignore for now.

---

## 3. The gap — what a "fully AI-powered game builder" still needs

1. **NPC brains** (decision-making) — none yet.
2. **NPC movement** — none yet.
3. **Auto art/assets** (textures, 3D models, shaders from text) — none yet.
4. **Multiplayer** — none yet.
5. **Big-scale performance** — none yet.
6. **More authoring tools** — we have ~27; rivals have 70+ (missing: NavMesh bake, terrain, UI, animation, audio, input).
7. **Code accuracy** — give Claude the Unity reference so scripts compile first try.
8. **"Whole game" orchestration** — one prompt → a full playable game (player + enemies + UI + win/lose + save).

---

## 4. Capability map

| Capability | Status |
|---|---|
| Build objects/materials/scenes/scripts live | ✅ Have |
| See results (screenshot / console) | ✅ Have |
| Research latest Unity API before building | ✅ Have (`/unity-build` skill) |
| Rich authoring (NavMesh, terrain, UI, animation, audio) | 🟡 Partial → **Phase 1** |
| Accurate code via engine reference | 🟡 Partial → **Phase 1** |
| NPC decision-making (BT/GOAP) | ❌ → **Phase 2–3** |
| NPC movement (steering) | ❌ → **Phase 2** |
| Learned behavior (ML-Agents) | ❌ → **Phase 3 (optional)** |
| Generate textures/3D/shaders from text | ❌ → **Phase 4** |
| Multiplayer (Netcode) | ❌ → **Phase 5** |
| Massive scale (DOTS/ECS) | ❌ → **Phase 5 (optional)** |
| One prompt → full playable game | ❌ → **Phase 6** |

---

## 5. Phased roadmap (safest, highest-value first)

**Phase 0 — Live authoring bridge.** ✅ Done and working.

**Phase 1 — Accurate + richer building.** Add authoring tools (NavMesh bake, terrain, UI/Canvas,
animation, audio, input, physics materials). Give Claude the **UnityCsReference** as read-only lookup so
generated scripts are correct. *Low risk. Unlocks:* "add a health bar," "bake navmesh," "add footstep audio."

**Phase 2 — Give NPCs brains + legs.** Integrate **Fluid Behavior Tree** (or NPBehave) + **Unity Movement
AI**, and add a skill so Claude wires them up. *Unlocks:* "make an enemy that chases the player and flees when hurt."

**Phase 3 — Smarter AI (optional deeper).** Add **GOAP** for goal-based agents; optionally **ML-Agents** for
learned behavior (separate training pipeline). *Unlocks:* planning NPCs, trainable bots.

**Phase 4 — Generate content from text.** Add MCP tools that call **AI Shader / Dream Textures** (materials,
textures) and **MeshAnything / BlenderMCP** (3D models). *Unlocks:* "give me a rusty metal texture," "make a low-poly barrel."

**Phase 5 — Multiplayer & scale.** Add **Netcode for GameObjects** (multiplayer); **DOTS/ECS** for heavy
simulations. *Unlocks:* "make this 2-player," "spawn 5000 zombies."

**Phase 6 — "Whole game" orchestration.** Build game **templates** + a build-a-game workflow so a single prompt
scaffolds a playable game (genre → scene, player controller, enemies+AI, spawner, UI/health, win/lose, save).

---

## 6. Licensing cheat-sheet (before we ship anything)

- **Safe to integrate/ship:** GOAP (MIT), ML-Agents (Apache-2.0), Prowl (MIT).
- **Usable inside Unity projects:** Netcode, DOTS/ECS Samples (Unity Companion License).
- **Read-only, NEVER ship:** UnityCsReference (Unity Reference-Only License).
- **Verify first (README didn't state):** Fluid Behavior Tree, NPBehave, Unity Movement AI.
- **External AI services** (AI Shader, Dream Textures, world models): check API terms + cost per call.

---

## 7. What "done" looks like

You type: **"make a 3D zombie-survival game."** Claude scaffolds the scene, a player controller, zombies that
chase with AI, a spawner, a health/UI system, win/lose logic, and generates the textures — **playable in the
Editor, built live while you watch.**

---

## Next step

Read this over and edit anything. When you approve, we start **Phase 1** (richer authoring tools + the
Unity reference for accurate code). We do one phase at a time so you can see it working before moving on.
