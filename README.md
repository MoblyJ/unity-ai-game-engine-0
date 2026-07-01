# unity-editor-mcp — Claude Code as a live Unity game developer

Turn Claude Code (running in WSL) into a Unity game developer. You describe what you want —
*"add a red bouncing ball", "build a night forest", "give the player a jump script"* — Claude
researches the current Unity method when needed and **builds it live in your open Unity Editor**:
objects, materials, lighting, prefabs, scenes, and behaviour scripts appear in the Scene view as
it works. Claude takes screenshots to see the result and self-corrects.

```
Claude Code (WSL)  ──MCP stdio via WSL interop──►  python.exe  (runs on WINDOWS)
                                                        │  TCP 127.0.0.1:8765 (newline JSON)
                                                        ▼
                                            Unity Editor bridge (C#, Editor extension)
                                            builds live in the Scene view
```

The MCP server runs **on Windows** (launched by Claude Code through WSL interop), so it shares
`127.0.0.1` with Unity — **no WSL networking setup required**.

---

## What's in here

| Path | What it is |
|---|---|
| `server/` | Python FastMCP server: `unity_editor_mcp.py` + `unity_client.py` + `schemas.py` |
| `unity/Assets/AgentBridge/Editor/` | The C# Editor bridge — drop this folder into your Unity project |
| `.claude/skills/unity-build/` | The `/unity-build` skill: research → build → screenshot-verify loop |
| `setup/UnityMCPSetup.exe` | **One-click Windows installer** — does the whole setup below automatically |
| `scripts/setup_windows.ps1` | Installs deps into Windows Python + prints the register command |
| `scripts/smoke_test.py` | Transport test (`--stub` needs no Unity; real mode talks to the bridge) |

32 tools: read-only (`unity_editor_version`, `unity_scene_info`, `unity_find`, `unity_get_object`,
`unity_query_assets`, `unity_console_logs`, `unity_screenshot`) and authoring (`unity_create_object`, `unity_modify_object`,
`unity_delete_object`, `unity_add_component`, `unity_set_component`, `unity_create_material`,
`unity_create_prefab`, `unity_instantiate_prefab`, `unity_create_script`, `unity_set_lighting`,
`unity_create_scene`/`unity_open_scene`/`unity_save_scene`, `unity_select`, `unity_focus`,
`unity_execute_menu`, `unity_undo`/`unity_redo`, `unity_play`/`unity_stop`) — plus Phase 1 richer
authoring: `unity_bake_navmesh`, `unity_create_terrain`, `unity_create_ui`, `unity_add_audio`,
`unity_create_physics_material`.

---

## Setup

### Option A0 — npm CLI (one command)

From **WSL** (Node runs in WSL, drives Windows via interop):

```bash
# from a clone/checkout of this package:
npm install -g .            # or: npm link
unity-mcp-bridge check      # detect WSL, Windows Python, Unity Hub + Editors, Claude CLI
unity-mcp-bridge            # = setup: checks, ensures the exe, runs it to wire everything up
```

`unity-mcp-bridge` verifies Unity on Windows, then runs the setup exe (builds it first if needed via
the built-in .NET compiler) to stage the MCP server, install its deps, register it with Claude Code,
and wire up the live Editor bridge. Commands: `check`, `build`, `setup` (default), `run`, `help`.
After it finishes, **reload Claude Code** so the `unity_*` tools load.

> Once published to the registry this becomes `npx unity-mcp-bridge`. The CLI passes your package
> path + WSL distro to the exe automatically, so it works from any install location.

### Option A — One-click installer (the exe directly)

Run **`C:\Users\mjmob\unity-mcp\setup\UnityMCPSetup.exe`** (double-click it, or run from a Windows
terminal). It automates everything: locates Windows Python, stages the server to your profile,
installs `mcp` + `pydantic`, **registers the MCP server with Claude Code**, and — if Unity Hub is
missing — offers to download + launch the Hub installer. Once you have a Unity Editor, run it again
and give it your project path (or, when you leave the path blank, it asks **"Create a new Unity
project? [y/N]"** — press **Y** to have it generate a fresh project + `Assets` folder for you;
default is **N**). It then copies the bridge in, drops an autostart marker, and launches Unity with
the bridge **listening automatically** (comms open, no clicks).

> Run the copy at **`C:\Users\mjmob\unity-mcp\setup\`** (a local path), not the one on the
> `\\wsl.localhost\...` share — a program run from the WSL share can't read the WSL source paths.
> The only manual step is signing into your Unity account when the Hub/Editor installs (Unity
> requires it). Rebuild the exe after code changes with `bash setup/build_exe.sh`.

After it runs: **reload Claude Code** so the `unity_*` tools load, then jump to *Use it* below.

### Option B — Manual setup

### 1. Install Unity (Windows)
Not installed yet on this machine. Install **Unity Hub**, then a **2022 LTS or Unity 6** Editor,
and create a **3D** project (URP or Built-in both work). Keep the project on the Windows
filesystem (e.g. `C:\Users\<you>\UnityProjects\MyGame`) — Unity is slow on the WSL filesystem.

### 2. Add the bridge to your Unity project
Copy `unity/Assets/AgentBridge` into your project's `Assets/` folder. Unity will compile it.
Then open **Window > Agent Bridge**, optionally tick **Auto-start on load**, and click **Start**.
You should see `● LISTENING  127.0.0.1:8765`.

### 3. Copy the server to the Windows filesystem + install deps
> **Why copy?** The MCP server must run on **Windows** Python (so `127.0.0.1` reaches Unity).
> Claude Code launches it from the WSL project dir, and a Windows process started from inside the
> WSL share **cannot reliably resolve `\\wsl.localhost\...` UNC paths**. Running the server from a
> real `C:\` path avoids that entirely. (Source stays in WSL for editing; you re-copy on changes.)

From WSL (adjust the Windows user if not `mjmob`):
```bash
mkdir -p /mnt/c/Users/mjmob/unity-mcp
cp -r server /mnt/c/Users/mjmob/unity-mcp/                       # copy the 4 server files to C:
python.exe -m pip install --user "mcp[cli]>=1.2.0" "pydantic>=2.6"   # deps into Windows Python
```
Re-run the `cp` whenever you edit anything under `server/`.

### 4. Register the MCP server with Claude Code (run in WSL)
```bash
claude mcp add unity-editor -- python.exe "C:\Users\mjmob\unity-mcp\server\unity_editor_mcp.py"
```
`python.exe` runs via WSL interop, so the server process is on Windows (localhost = Unity).
Reload Claude Code (restart it, or `/mcp`) and confirm `unity-editor` shows **✔ Connected** and
the `unity_*` tools are listed.

> **Port:** default `8765`. To change it, set it in the Agent Bridge window **and** pass the env
> var to the server: `claude mcp add unity-editor -e UNITY_BRIDGE_PORT=9000 -- python.exe "C:\Users\mjmob\unity-mcp\server\unity_editor_mcp.py"`.

---

## Use it

Just ask, and Claude will run the build loop (or invoke `/unity-build <request>` explicitly):

- *"Create a red cube at the origin and point the camera at it."*
- *"Give me a ground plane, a bouncing ball with a Rigidbody and a bouncy physics material."*
- *"Write a script that spins the selected object and attach it."*
- *"Make it night: dark ambient, blue fog, low sun."*

Claude builds via the `unity_*` tools, calls `unity_console_logs` to confirm scripts compiled,
and `unity_screenshot` to show you the result. Everything is undoable with **Ctrl-Z** in Unity.

---

## Verify

**Without Unity** (proves the Python transport, framing, and error handling):
```bash
.venv/bin/python -m py_compile server/*.py     # or: python -m py_compile server/*.py
.venv/bin/python scripts/smoke_test.py --stub   # -> PASS: stub round-trip ...
npx @modelcontextprotocol/inspector .venv/bin/python server/unity_editor_mcp.py  # lists all 32 tools
```

**With Unity** (end-to-end): open the project, Start the Agent Bridge, then from WSL:
```bash
python scripts/smoke_test.py            # sends scene_info to the real bridge -> scene JSON
```
Then in Claude Code: *"create a red cube at origin"* → the cube appears in the Scene view,
`unity_screenshot` returns the image, `unity_console_logs` is clean.

---

## Troubleshooting

- **`⚠️ Cannot reach the Unity bridge`** — Unity isn't open, or you didn't click **Start** in the
  Agent Bridge window, or the port differs. Check the window shows `● LISTENING`.
- **Tools not showing in Claude Code** — the Windows `python.exe` is missing deps (re-run
  `setup_windows.ps1`) or the UNC path in the register command is wrong for your distro/user.
- **`No component type named ...`** — use the exact class name (`Rigidbody`, `BoxCollider`, `Light`,
  or your script's class name).
- **Materials look pink** — shader mismatch: pass `shader:"Standard"` for Built-in RP or the URP
  Lit path for URP. The tools fall back to a sensible default shader automatically.

## Notes
- This repo repurposes the plumbing described in `prompt.md` (TCP bridge + main-thread queue +
  FastMCP conventions) toward **authoring in Editor mode**. The two-agent *play* loop from
  `prompt.md` (personas / scenario matrix) is intentionally out of scope here.
