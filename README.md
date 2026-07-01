# unity-ai-game-engine

**Claude Code as a live Unity game developer.** You describe what you want in plain English —
*"add a red bouncing ball", "build a night forest", "give the player a jump script", "make a health
bar"* — and Claude **builds it live in your open Unity Editor**: GameObjects, materials, terrain,
lighting, UI, audio, physics, NavMesh, and behaviour scripts appear in the Scene view as it works.
Claude takes screenshots to see the result and self-corrects.

The bridge runs Claude Code in **WSL** and the Unity Editor on **Windows**, connected through an MCP
server. It ships with a one-command **npm CLI**, a one-click **Windows setup exe**, and 32 tools.

---

## Table of contents

- [What it does](#what-it-does)
- [Architecture](#architecture)
- [How a command flows](#how-a-command-flows)
- [Why this WSL ⇄ Windows split](#why-this-wsl--windows-split)
- [Repository layout](#repository-layout)
- [Tool catalog (32)](#tool-catalog-32)
- [Setup](#setup)
- [Using it](#using-it)
- [Verification](#verification)
- [Roadmap](#roadmap)
- [Troubleshooting](#troubleshooting)
- [Design notes & gotchas](#design-notes--gotchas)
- [License](#license)

---

## What it does

- **Authoring, live in the Editor.** Create/modify objects, materials, prefabs, scenes, terrain, UI,
  lighting, audio, physics materials, and full C# scripts — while you watch, without pressing Play.
- **Perception + self-correction.** Claude reads the scene hierarchy, inspects objects, reads the
  console (to catch compile errors), and takes **screenshots** to visually verify what it built.
- **Research-then-build.** The `/unity-build` skill web-searches the latest Unity API when needed and
  consults Unity's real source for exact signatures before writing C# (so generated code compiles).
- **Undoable.** Every change is wrapped in Unity's Undo — **Ctrl-Z undoes Claude**.
- **Turnkey install.** `unity-mcp-bridge` (npm) or `UnityMCPSetup.exe` wires up the whole thing.

---

## Architecture

```
┌──────────────────────────────── WSL2 (Ubuntu) ─────────────────────────────────┐
│                                                                                  │
│    You ──▶  Claude Code  ◀── the "game developer" brain (+ WebSearch/WebFetch)   │
│                  │                                                               │
│                  │  MCP over stdio, launched via WSL interop:                    │
│                  │      python.exe  C:\Users\<you>\unity-mcp\server\...py         │
└──────────────────┼───────────────────────────────────────────────────────────────┘
                   │   (the command is a Windows exe, so the process runs on Windows)
┌──────────────────▼───────────────────────── Windows ─────────────────────────────┐
│                                                                                   │
│   unity_editor_mcp   —  FastMCP server (Python)                                    │
│     • 32 tools: perception (read-only) + authoring (write)                         │
│     • Pydantic-validated args  ──▶  JSON op  { id, op, args }                      │
│                  │                                                                 │
│                  │   TCP  127.0.0.1:8765   (newline-delimited JSON)                │
│                  ▼                                                                 │
│   Unity Editor  ──  AgentBridge  (C# Editor extension, runs in Edit mode)          │
│     • TcpListener on a background thread                                           │
│     • main-thread command queue drained in EditorApplication.update               │
│       (UnityEditor APIs are main-thread only)                                      │
│     • CommandDispatcher:  op  ──▶  UnityEditor / AssetDatabase / EditorSceneMgr    │
│     • builds live in the Scene view; returns data / PNG screenshots to Claude      │
│     • "Agent Bridge" EditorWindow shows status + a live log of every command       │
│                                                                                   │
└───────────────────────────────────────────────────────────────────────────────────┘
```

**Three processes, one loop:** Claude Code (WSL) reasons and calls tools → the Python MCP server
(Windows) validates and relays them as symbolic ops → the C# bridge (inside Unity) executes them on
the Editor's main thread and returns results. The LLM never touches Unity APIs directly; it only
emits ops from a fixed, validated vocabulary — safe, debuggable, and deterministic.

---

## How a command flows

```
"create a red cube at origin"
        │
        ▼
Claude Code picks a tool:  unity_create_object { primitive:"cube", color:[1,0,0], position:[0,0,0] }
        │  (MCP stdio)
        ▼
unity_editor_mcp  validates args (Pydantic) → sends JSON line:
        {"id":"…","op":"create_object","args":{…}}          ──TCP 127.0.0.1:8765──▶
        │
        ▼
AgentBridge (Unity):  background thread reads the line → enqueues it →
        EditorApplication.update dequeues on the main thread →
        CommandDispatcher → AuthoringOps.CreateObject →
        GameObject.CreatePrimitive(Cube) + material + Undo + MarkSceneDirty + RepaintAllViews
        │
        ▼  {"id":"…","ok":true,"data":{"id":-1738,"name":"Cube","path":"Cube"}}
Claude sees the result, optionally calls unity_screenshot to look at it, and reports back.
```

Envelope (both directions):

```
request :  { "id": "<uuid>", "op": "create_object|perceive|…", "args": { … } }
response:  { "id": "<uuid>", "ok": true,  "data":  { … } }
        |  { "id": "<uuid>", "ok": false, "error": "actionable message" }
```

Errors are **soft**: unknown ops, bad targets, missing packages → structured error strings the agent
can read and retry, never a crash.

---

## Why this WSL ⇄ Windows split

Unity runs on Windows; Claude Code runs in WSL. WSL's `localhost` does **not** reach the Windows host
by default (NAT networking). Instead of configuring networking, the **MCP server runs on Windows**,
launched by Claude Code through WSL interop (`python.exe …`). Because that process is on Windows, it
shares `127.0.0.1` with Unity — the TCP bridge "just works" with **zero networking setup**.

The server code is staged to a real `C:\` path (not run over the `\\wsl.localhost` share) because a
Windows process launched from a WSL-share working directory can't reliably resolve those UNC paths.
The npm CLI and setup exe handle this automatically.

---

## Repository layout

```
unity-ai-game-engine/
├── bin/cli.js                       # npm CLI: `unity-mcp-bridge` (check / build / setup / run)
├── package.json                     # npm package manifest
├── server/                          # the MCP server (runs on Windows)
│   ├── unity_editor_mcp.py          #   FastMCP server, 32 tools, stdio + --http
│   ├── unity_client.py              #   async TCP client to the bridge (id-correlated, reconnect)
│   ├── schemas.py                   #   Pydantic v2 input models (one per tool)
│   └── requirements.txt             #   mcp[cli], pydantic
├── unity/Assets/AgentBridge/Editor/ # the C# Editor bridge — drop into your Unity project
│   ├── AgentBridgeServer.cs         #   TcpListener + main-thread queue + autostart
│   ├── CommandDispatcher.cs         #   op → handler routing; JSON envelope
│   ├── AuthoringOps.cs              #   create/modify/component/material/prefab/scene/script/
│   │                                #   navmesh/terrain/ui/audio/physics-material
│   ├── SceneQuery.cs                #   scene_info / find / get_object / console_logs / version
│   ├── Screenshot.cs                #   Scene/Game view → PNG
│   ├── BridgeUtil.cs                #   arg parsing, object/type resolution, reflection helpers
│   ├── Json.cs                      #   dependency-free JSON (MiniJSON)
│   ├── AgentBridgeWindow.cs         #   "Agent Bridge" EditorWindow (status + live log)
│   └── AgentBridge.Editor.asmdef    #   editor-only assembly definition
├── setup/
│   ├── UnityMCPSetup.cs             #   one-click Windows installer (C# source)
│   ├── UnityMCPSetup.exe            #   …compiled with the built-in .NET csc (no installs)
│   └── build_exe.sh                 #   rebuild the exe
├── scripts/
│   ├── setup_windows.ps1            #   stage server + install deps (PowerShell)
│   └── smoke_test.py                #   transport test (`--stub` needs no Unity)
├── .claude/skills/unity-build/      #   the research → build → screenshot-verify skill
├── Integration-plan.md              #   phased roadmap toward a full AI game builder
└── prompt.md                        #   original design brief
```

---

## Tool catalog (32)

**Read-only (perception)** — `readOnlyHint: true`
| Tool | Purpose |
|---|---|
| `unity_editor_version` | Unity version + render pipeline (URP/HDRP/Built-in) + bridge support check |
| `unity_scene_info` | Active scene + GameObject hierarchy |
| `unity_find` | Find objects by name / tag / component |
| `unity_get_object` | Full detail of one object (transform + components + properties) |
| `unity_query_assets` | Search project assets (AssetDatabase filter) |
| `unity_console_logs` | Recent console + compiler messages (catch compile errors) |
| `unity_screenshot` | Capture Scene or Game view as a PNG |

**Authoring (write)** — core
| Tool | Purpose |
|---|---|
| `unity_create_object` | Primitive/empty GameObject + transform + parent + color |
| `unity_modify_object` | Rename / move / rotate / scale / reparent / retag / active |
| `unity_delete_object` | Destroy (undoable) |
| `unity_add_component` / `unity_set_component` | Add/edit any component + serialized fields |
| `unity_create_material` | Material (shader + color + metallic/smoothness), optional assign |
| `unity_create_prefab` / `unity_instantiate_prefab` | Save/instantiate prefabs |
| `unity_create_script` | Write a C# script (+ optional attach after compile) |
| `unity_set_lighting` | Ambient / skybox / fog / sun (directional light) |
| `unity_create_scene` / `unity_open_scene` / `unity_save_scene` | Scene management |
| `unity_select` / `unity_focus` | Select / frame an object in the Editor |
| `unity_execute_menu` | Escape hatch: invoke any Editor menu item |
| `unity_undo` / `unity_redo` | Undo / redo |
| `unity_play` / `unity_stop` | Enter / exit Play mode |

**Authoring (write)** — Phase 1 richer authoring
| Tool | Purpose |
|---|---|
| `unity_bake_navmesh` | Bake a NavMesh over scene geometry (for NavMeshAgents) |
| `unity_create_terrain` | Terrain + TerrainData (size / resolution) |
| `unity_create_ui` | uGUI element: text / button / image / slider (health bars) / panel / canvas |
| `unity_add_audio` | AudioSource + AudioClip on an object |
| `unity_create_physics_material` | Friction + bounciness, assignable to a collider |

Every tool input is a Pydantic model with described, constrained fields, and carries MCP annotations
(`readOnlyHint`, `destructiveHint`, `idempotentHint`, `openWorldHint`).

---

## Setup

Prerequisites: **WSL2 + Ubuntu**, **Windows Python 3.11+**, **Node 16+** (in WSL), and **Unity Hub +
a Unity 6 (6000.x) Editor**. The CLI checks all of these for you.

### Option A — npm CLI (one command)

From **WSL**:

```bash
npm install -g .            # from a checkout of this repo (or: npm link)
unity-mcp-bridge check      # detect WSL, Windows Python, Unity Hub + Editors, Claude CLI
unity-mcp-bridge            # setup: check → ensure the exe → run it to wire everything up
```

Commands: `check` (detect, no changes) · `build` (compile the setup exe) · `setup` (default) ·
`run` · `help`.

### Option B — Windows setup exe

Run `C:\Users\<you>\unity-mcp\setup\UnityMCPSetup.exe` (or `setup/UnityMCPSetup.exe`). It: locates
Windows Python → stages the server to your profile → installs `mcp` + `pydantic` → **registers the
MCP server with Claude Code** → offers to install Unity Hub if missing → optionally **creates a Unity
project (adds uGUI) and injects the bridge**, launching Unity with the bridge listening automatically.

### Option C — Manual

1. Install Unity Hub + a 6000.x Editor; create a **3D** project on `C:\`.
2. Copy `unity/Assets/AgentBridge` into the project's `Assets/`. Open **Window ▸ Agent Bridge ▸ Start**
   (tick *Auto-start on load*). You should see `● LISTENING 127.0.0.1:8765`.
3. Stage the server + install deps on Windows:
   ```bash
   mkdir -p /mnt/c/Users/<you>/unity-mcp && cp -r server /mnt/c/Users/<you>/unity-mcp/
   python.exe -m pip install --user "mcp[cli]>=1.2.0" "pydantic>=2.6"
   ```
4. Register with Claude Code (run in WSL):
   ```bash
   claude mcp add unity-editor -- python.exe "C:\Users\<you>\unity-mcp\server\unity_editor_mcp.py"
   ```

**After any option: reload Claude Code** so the `unity_*` tools load into the session.

> **Port:** default `8765`. Change it in the Agent Bridge window **and** pass `-e UNITY_BRIDGE_PORT=…`
> when registering the server.

---

## Using it

Just ask — or invoke `/unity-build <request>` explicitly:

- *"Check the editor version, then create a red cube at origin and screenshot it."*
- *"Ground plane + a bouncing ball with a Rigidbody and a bouncy physics material."*
- *"Write a script that spins the selected object and attach it."*
- *"Make it night: dark ambient, blue fog, low sun."*
- *"Add a health-bar slider and a 'Play' button to a canvas."*
- *"Create a terrain and bake a navmesh over it."*

Claude builds via the tools, checks `unity_console_logs` for a clean compile, and `unity_screenshot`
to confirm the result — iterating until it matches your request.

---

## Verification

**Without Unity** (proves the Python transport + framing + error handling):

```bash
python -m py_compile server/*.py
python scripts/smoke_test.py --stub                 # → PASS: stub round-trip …
npx @modelcontextprotocol/inspector python server/unity_editor_mcp.py   # lists all 32 tools
```

**With Unity** (end-to-end): open the project + Start the bridge, then from WSL
`python scripts/smoke_test.py` returns real scene JSON. In Claude Code, *"create a red cube"* → the
cube appears in the Scene view and `unity_screenshot` returns the image.

---

## Roadmap

See **[Integration-plan.md](Integration-plan.md)** for the full plan. Summary:

| Phase | Adds | Status |
|---|---|---|
| 0 | Live authoring bridge | ✅ Done |
| 1 | Richer authoring (navmesh, terrain, UI, audio, physics) + API-accuracy lookup | ✅ Done |
| 2 | NPC brains + movement (Behavior Trees / GOAP + steering) | Planned |
| 3 | Smarter AI (GOAP, ML-Agents) | Planned |
| 4 | Generate content from text (textures / 3D / shaders) | Planned |
| 5 | Multiplayer (Netcode) + scale (DOTS/ECS) | Planned |
| 6 | One prompt → full playable game | Planned |

---

## Troubleshooting

- **`⚠️ Cannot reach the Unity bridge`** — Unity isn't open, or the Agent Bridge window isn't Started,
  or the port differs. Confirm it shows `● LISTENING`.
- **Tools not in Claude Code** — MCP servers load at startup; **reload Claude Code**. Or Windows Python
  is missing deps (re-run setup), or the registered path is wrong.
- **Unity is in "Safe Mode"** — a script didn't compile. Check `unity_console_logs` / the Console.
  Fix the script and re-copy; a fresh Unity launch forces a clean recompile.
- **UI tools say "menu unavailable"** — the project lacks `com.unity.ugui`. Add it to
  `Packages/manifest.json` (`"com.unity.ugui": "2.5.0"`). The setup exe does this for new projects.
- **Materials look pink** — shader mismatch. Pass `shader:"Standard"` for Built-in RP, or the URP Lit
  path for URP. `unity_editor_version` reports your render pipeline.
- **Testing the bridge from WSL fails but the MCP works** — WSL `python3` hits WSL's localhost, which
  can't reach the Windows bridge. Test with Windows `python.exe`, or just use the MCP tools.

---

## Design notes & gotchas

Hard-won lessons baked into the code (so they don't bite again):

- **Unity 6 renamed APIs to hard errors.** `Object.GetInstanceID()` and `PhysicMaterial` are
  `[Obsolete(error:true)]` in 6.x. The bridge calls them via **reflection** (`BridgeUtil.Iid`,
  `FindType`) so it compiles on 2022.3 LTS, 6.0 LTS, and 6.5 alike.
- **UI menu paths changed.** Unity 6.5's uGUI uses `GameObject/UI (Canvas)/…` (not `GameObject/UI/…`).
  `unity_create_ui` tries the new paths with fallbacks.
- **UNC from a WSL cwd.** A Windows process launched from a `\\wsl.localhost` working directory can't
  resolve those UNC paths — so the server is staged to `C:\` and the CLI spawns the exe with a `C:\`
  cwd.
- **Recompiles need focus.** Unity only re-imports changed scripts on Editor focus. To deploy bridge
  changes deterministically, restart Unity (fresh launch = clean compile) or trigger `Assets/Refresh`.
- **The bridge auto-starts** via an `agentbridge.autostart` marker file at the project root (dropped by
  the setup exe), so comms come up without clicking Start.

---

## License

MIT — see `package.json`. Third-party repositories studied during planning are **not** included in
this repository (see `.gitignore`); `UnityCsReference`, if used locally, is reference-only under
Unity's license and must never be shipped.

🤖 Built with [Claude Code](https://claude.com/claude-code).
