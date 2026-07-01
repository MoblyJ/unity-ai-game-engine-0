# Build Prompt: Unity ⇄ MCP Two-Agent Framework

> **For:** Claude Code
> **Goal:** Build an MCP server that turns a running Unity scene into a tool-driven world, then run **two independent Claude-driven agents** inside it that perceive, reason, and act in a loop. Include a wide parameter surface so every situation can be tested.

---

## 0. Mental model (read this first)

We are implementing the standard agentic loop against a live Unity scene:

```
prompt → Claude evaluates → tool call(s) → tool result → Claude evaluates → … → final action
```

- **Claude "evaluates"** = one agent brain deciding its next move.
- **Tool calls** = perception ("what do I see?") and action ("do this").
- **The world** = a running Unity scene that never talks to Claude directly.

Hard rule: **the LLM never touches Unity APIs directly.** It only emits *symbolic* actions from a fixed vocabulary. A C# dispatcher maps each symbolic action to real gameplay code. This keeps the system safe, debuggable, cheap, and deterministic to replay.

Two agents = two brains sharing one world. Each has its own persona, its own perception snapshot, and the **same** action vocabulary. Cooperation vs. adversarial behavior is decided entirely by the persona/goal prompt, not by code.

---

## 1. Architecture

```
┌─────────────────────────────────────────────────────────────┐
│  ORCHESTRATOR (Python)                                        │
│  - Runs tick loop for Agent A and Agent B                     │
│  - Holds per-agent system prompt (persona + goal + vocab)     │
│  - Calls Claude, parses chosen action, dispatches it          │
│  - Runs the scenario harness (spawns, difficulty, noise)      │
└───────────────┬───────────────────────────────────────────────┘
                │ MCP tool calls (perceive / act / scenario)
┌───────────────▼───────────────────────────────────────────────┐
│  MCP SERVER  unity_agent_mcp  (Python / FastMCP)              │
│  - Perception tools  (state in)                               │
│  - Action tools      (decisions out)                          │
│  - Scenario tools    (world setup / test knobs)               │
│  - Relays over a JSON-line TCP socket to Unity                │
└───────────────┬───────────────────────────────────────────────┘
                │ TCP  127.0.0.1:{PORT}  newline-delimited JSON
┌───────────────▼───────────────────────────────────────────────┐
│  UNITY BRIDGE (C# MonoBehaviour, runs inside Unity)          │
│  - TcpListener on a background thread                          │
│  - Main-thread command queue (Unity API is not thread-safe)   │
│  - WorldSnapshot serializer  → returns scene state as JSON    │
│  - ActionDispatcher          → maps symbolic action → method   │
└───────────────────────────────────────────────────────────────┘
```

**Why this split:** perception/action/scenario as separate tool groups mirrors the natural task subdivisions of an agent (sense → decide → act → reconfigure), which is exactly how MCP tools should be named and grouped.

---

## 2. Tech stack & environment

Target environment is **WSL2 + Windows** (dev already runs Claude Code in WSL2; Unity runs on Windows).

- **Unity:** 2022 LTS or newer, any 3D or 2D template. Editor Play Mode is fine — no build needed.
- **Unity bridge:** plain C#, `System.Net.Sockets.TcpListener`. No external Unity packages required.
- **MCP server + orchestrator:** Python 3.11+, `mcp` (FastMCP), `pydantic` v2, `anthropic` SDK. Use a venv.
- **Transport (MCP ⇄ client):** stdio for local Claude Code use; expose a `streamable_http` mode too.
- **Transport (MCP ⇄ Unity):** newline-delimited JSON over TCP on `127.0.0.1`.

> **WSL2 networking note (important, this bit us before):**
> Unity listens on Windows; the Python side may run in WSL2. WSL2 localhost does **not** reach the Windows host by default.
> Preferred fix: enable **mirrored networking mode** in `.wslconfig` (`[wsl2]\nnetworkingMode=mirrored`) then `wsl --shutdown`, so `127.0.0.1` is shared.
> Fallback: point the Python client at the Windows host IP (`cat /etc/resolv.conf` nameserver, or `hostname.local`), and have Unity bind to `0.0.0.0` instead of `127.0.0.1`.
> Simplest of all: run the Python MCP server **on Windows** alongside Unity, and only keep Claude Code in WSL2.

---

## 3. Repository layout

Create this structure:

```
unity-agent-mcp/
├── prompt.md                     # this file
├── README.md                     # setup + run instructions you generate
├── unity/
│   └── Assets/AgentBridge/
│       ├── AgentBridgeServer.cs   # TCP listener + main-thread queue
│       ├── WorldSnapshot.cs       # scene → JSON serializer
│       ├── ActionDispatcher.cs    # symbolic action → gameplay method
│       ├── AgentEntity.cs         # component marking a controllable agent
│       └── ScenarioController.cs  # spawn / reset / difficulty knobs
├── server/
│   ├── unity_agent_mcp.py         # FastMCP server (tools)
│   ├── unity_client.py            # TCP client to the Unity bridge
│   ├── schemas.py                 # Pydantic input/output models
│   └── requirements.txt
├── orchestrator/
│   ├── run_loop.py                # two-agent tick loop
│   ├── personas.py                # Agent A / Agent B system prompts
│   └── scenarios.py               # the test matrix (see §8)
└── logs/
    └── (jsonl transcripts per run)
```

---

## 4. Build phases

Work in order. After each phase, verify before moving on.

### Phase 1 — Unity bridge (C#)
1. `AgentBridgeServer.cs`: start a `TcpListener` on `START` (Play Mode), accept one client, read newline-delimited JSON requests. Because Unity APIs must run on the main thread, push each parsed request onto a `ConcurrentQueue` and drain it in `Update()`; write the JSON response back on the socket.
2. Request envelope: `{ "id": "<uuid>", "op": "perceive|act|scenario", "args": { ... } }`. Response: `{ "id": "<uuid>", "ok": true, "data": { ... } }` or `{ "ok": false, "error": "..." }`.
3. `WorldSnapshot.cs`: build a per-agent view — self state, visible entities (filtered by radius + FOV via raycasts/OverlapSphere), nearby cover/interactables, and global tick number. Use `JsonUtility` or a tiny hand-rolled serializer.
4. `ActionDispatcher.cs`: `Execute(agentId, action, args)` with a `switch` over the **exact** action names in §7. Unknown action → return an actionable error, never throw.
5. `AgentEntity.cs`: tag a GameObject as agent `A` or `B`, hold health/team/inventory.
6. **Verify:** with `nc`/a Python one-liner, send `{"op":"perceive","args":{"agent_id":"A"}}` and get JSON back.

### Phase 2 — MCP server (Python / FastMCP)
1. `unity_client.py`: async TCP client, one request → one response, correlated by `id`, with timeout + reconnect.
2. `unity_agent_mcp.py`: `mcp = FastMCP("unity_agent_mcp")`. Register the tools in §7 with `@mcp.tool(name=..., annotations={...})`, Pydantic models from `schemas.py` for every input, docstrings describing return schema.
3. Support both transports: `mcp.run()` (stdio, default) and `mcp.run(transport="streamable_http", port=8000)` behind a `--http` flag.
4. Centralize error formatting and JSON/Markdown response selection (don't duplicate per tool).
5. **Verify:** `python -m py_compile server/unity_agent_mcp.py`, then `npx @modelcontextprotocol/inspector` and call `unity_perceive`.

### Phase 3 — Perception & action tools
Implement the full tool catalog in §7 with the **wide parameter surface**. Every field gets a type, description, and constraints via Pydantic `Field(...)`.

### Phase 4 — Two-agent orchestration
1. `personas.py`: two system prompts (see §6 template). Each states identity, goal, the action vocabulary, and **"reply ONLY with a JSON action object, no prose."**
2. `run_loop.py`: for each tick, for each agent → `perceive` → build messages (persona + snapshot + short memory window) → call Claude → parse action JSON → `act`. Alternate or run both per tick (configurable). Log every step to `logs/<run>.jsonl`.
3. Stop conditions: goal reached, max ticks, or agent death.

### Phase 5 — Scenario / test harness
Implement `scenario` tools (§7.3) and `scenarios.py` (§8) so a single config dials difficulty, spawns, environment, and uncertainty. This is the "test every situation" surface.

### Phase 6 — Run & verify
Produce `README.md` with exact run commands. Do a smoke run: 1 scenario, 2 agents, 10 ticks, and confirm the jsonl transcript shows sensible perceive→decide→act cycles.

---

## 5. FastMCP patterns to follow (non-negotiable)

- Server name: `unity_agent_mcp` (format `{service}_mcp`, lowercase, no versions).
- Tool names: snake_case, service-prefixed, action-oriented (`unity_perceive`, `unity_move`, `unity_scenario_reset`).
- Every tool input is a Pydantic `BaseModel` with `model_config = ConfigDict(str_strip_whitespace=True, validate_assignment=True, extra='forbid')`.
- Every `Field(...)` has a description + constraints (`ge`, `le`, `min_length`, `max_items`, enums).
- Every tool sets `annotations`: `readOnlyHint`, `destructiveHint`, `idempotentHint`, `openWorldHint`.
  - Perception tools → `readOnlyHint: True`.
  - Action + scenario tools → `readOnlyHint: False` (scenario spawn/reset → `destructiveHint: True`).
- Async everywhere; actionable error strings (never a bare stack trace).
- Offer `response_format: "json" | "markdown"` on read tools.

---

## 6. Persona system-prompt template (for orchestrator)

```
You are Agent {A|B}, a {persona} operating in a live 3D arena.
GOAL: {goal — e.g., "reach the green zone" / "eliminate the rival" / "escort the payload"}.
TEAM: {team}. You may {cooperate with / must defeat} the other agent.

You perceive the world only through the state snapshot you are given each tick.
You act only by choosing ONE action from this vocabulary:
  move, look, attack, interact, take_cover, wait, communicate
(see the exact JSON schema below).

Behavioral dials for this run:
  aggression={0..1}  caution={0..1}  curiosity={0..1}

Reply with ONLY a JSON object, no prose, no markdown fences:
{ "action": "<name>", "args": { ... }, "reason": "<one short sentence>" }
```

Keep `reason` short — it's for the transcript log and debugging, not for the game.

---

## 7. Tool catalog (the wide parameter surface)

Implement all of these. The parameters are intentionally broad so you can reproduce edge cases.

### 7.1 Perception (readOnly)

**`unity_perceive`** — full situational snapshot for one agent.

| param | type | default | notes |
|---|---|---|---|
| `agent_id` | enum `A`/`B` | — | required |
| `radius` | float `0.5..200` | 30 | sight range |
| `fov_degrees` | float `10..360` | 110 | field of view cone |
| `include_occluded` | bool | false | if false, raycast-hidden entities are dropped |
| `entity_filter` | list[enum] | all | `agent`,`enemy`,`item`,`cover`,`hazard`,`objective` |
| `max_entities` | int `1..100` | 20 | cap + sort by distance |
| `include_self` | bool | true | self health/pos/inventory |
| `noise` | float `0..1` | 0 | inject positional/label uncertainty (test perception robustness) |
| `response_format` | enum | markdown | `json`/`markdown` |

**`unity_raycast`** — line-of-sight / distance probe from an agent toward a point or entity. Params: `agent_id`, `target` (entity id or `[x,y,z]`), `max_distance` `0.1..500`, `layer_mask` (list[str]).

**`unity_query_map`** — static/global info: bounds, spawn points, objective locations, cover graph. Params: `region` (`all`/bbox), `include_navmesh` bool.

### 7.2 Actions (not readOnly)

**`unity_move`** — move the agent.

| param | type | default | notes |
|---|---|---|---|
| `agent_id` | enum | — | required |
| `mode` | enum | `to_point` | `to_point`,`to_entity`,`direction`,`flee_from`,`patrol` |
| `target` | `[x,y,z]` \| entity id \| dir vector | — | meaning depends on `mode` |
| `speed` | float `0..10` | 3.5 | m/s clamp |
| `stance` | enum | `run` | `walk`,`run`,`crouch`,`sprint` |
| `pathfinding` | enum | `navmesh` | `navmesh`,`straight`,`avoid_hazards` |
| `stop_distance` | float `0..20` | 1.0 | arrival tolerance |
| `precision` | float `0..1` | 1.0 | 1=exact, <1 adds drift (test noisy control) |

**`unity_look`** — rotate/aim. Params: `agent_id`, `target` (entity/point/angle), `turn_speed` `0..720` deg/s, `snap` bool.

**`unity_attack`** — Params: `agent_id`, `target` (entity id), `weapon` (enum, scene-defined), `aim_variance` `0..1` (spread — dial accuracy), `burst` int `1..30`, `lead_target` bool (predict movement). Respect cooldowns server-side; return `hit`/`miss`/`cooldown`/`out_of_range`.

**`unity_interact`** — Params: `agent_id`, `target` (interactable id), `interaction` (enum: `use`,`pickup`,`open`,`toggle`,`plant`,`defuse`), `hold_seconds` `0..30`.

**`unity_take_cover`** — Params: `agent_id`, `from_entity` (threat), `prefer` (`nearest`/`best_angle`/`toward_objective`), `peek` bool.

**`unity_communicate`** — inter-agent message (for cooperation/coordination tests). Params: `from_agent`, `to_agent`, `message` (str ≤ 280), `broadcast` bool. Delivered into the other agent's next snapshot.

**`unity_wait`** — Params: `agent_id`, `ticks` int `1..10`. No-op / observe.

### 7.3 Scenario / test knobs (destructive where noted)

**`unity_scenario_reset`** *(destructive)* — reload the arena to a known state. Params: `scenario_id` (str), `seed` int (deterministic replays).

**`unity_scenario_configure`** — dial the whole situation in one call:

| param | type | range | tests |
|---|---|---|---|
| `difficulty` | float | `0..1` | overall enemy skill/HP scaling |
| `agent_a_health` / `agent_b_health` | int | `1..1000` | asymmetry |
| `agent_a_start` / `agent_b_start` | `[x,y,z]` | — | spawn placement |
| `time_scale` | float | `0.1..10` | slow-mo → fast-forward loop timing |
| `lighting` | enum | `day`/`dusk`/`night`/`flicker` | visibility stress |
| `cover_density` | float | `0..1` | open field ↔ dense cover |
| `hazard_count` | int | `0..50` | environmental danger |
| `item_spawns` | list[obj] | — | `{type, pos, qty}` |
| `objective` | enum | `capture`/`escort`/`survive`/`deathmatch`/`coop_reach` | win condition |
| `fog` | float | `0..1` | perception noise at world level |
| `perception_lag_ticks` | int | `0..5` | stale-snapshot handling |

**`unity_spawn`** *(destructive)* — Params: `entity_type` (enum), `position` `[x,y,z]`, `count` `1..100`, `team`, `overrides` (dict of stats).

**`unity_set_state`** — force an entity's health/position/status for edge-case reproduction. Params: `entity_id`, `health`, `position`, `status_effects` (list).

**`unity_tick`** — advance simulation N steps (for turn-based / deterministic testing). Params: `steps` `1..100`.

> Enumerate weapon/interactable/entity enums from the scene at server startup via a `unity_query_map` call so schemas stay in sync with the actual Unity scene.

---

## 8. Scenario matrix (`scenarios.py`) — "test every situation"

Define a list of named scenarios covering the axes below. Each is a dict of `unity_scenario_configure` args + persona overrides. Aim for full coverage of the corners, not just the happy path:

- **Objective:** deathmatch · coop_reach · escort · survive · capture
- **Symmetry:** balanced · A-advantaged (2× HP) · B-advantaged · A outnumbered
- **Visibility:** day/full · night/low · fog=1 · flicker
- **Terrain:** open (cover_density 0) · dense (cover_density 1) · hazard-heavy (hazard_count 30)
- **Control quality:** precision 1.0 · precision 0.5 (drift) · aim_variance 0.6 (spray)
- **Perception quality:** noise 0 · noise 0.5 · perception_lag_ticks 3 · include_occluded true
- **Timing:** time_scale 0.5 · 1 · 4 · turn-based via `unity_tick`
- **Behavior dials:** {aggression, caution, curiosity} sweep — e.g. rusher (agg 1, cau 0) vs. camper (agg 0, cau 1)

Provide a `run_matrix()` that iterates scenarios × seeds, runs the loop headless-ish, and writes one jsonl per (scenario, seed) with the full perceive/decide/act trace + outcome. That transcript is your eval data — it also feeds the QLoRA `finetune_data.jsonl` pipeline later if desired.

---

## 9. Guardrails

- **Symbolic actions only.** No tool ever passes free-form code or Unity API strings through to the engine.
- **Loop cadence, not per-frame.** Agents think on ticks (default ~1/sec or turn-based), never every frame — network + model latency (~1s) and API cost make per-frame impossible.
- **Deterministic replay.** Every scenario takes a `seed`; the same seed + same transcript must reproduce.
- **Fail soft.** Unknown actions, out-of-range targets, cooldowns → structured error results, never crashes. The agent should be able to read the error and retry.
- **Timeouts.** MCP↔Unity requests time out (e.g. 5s) and surface a clear message; the loop skips the tick rather than hanging.
- **Cost control.** Cap max ticks per run and log token usage per agent per run.

---

## 10. Done checklist

- [ ] Unity Play Mode + `AgentBridgeServer` accepts a TCP client and answers `perceive`/`act`/`scenario`.
- [ ] `python -m py_compile server/unity_agent_mcp.py` passes; MCP Inspector lists all §7 tools with correct schemas + annotations.
- [ ] All inputs are Pydantic models with described, constrained fields; all tools have annotations.
- [ ] Two agents run a full loop from `run_loop.py`, alternating perceive→decide→act, logging to jsonl.
- [ ] `unity_scenario_configure` visibly changes the world (lighting, cover, HP, objective).
- [ ] `run_matrix()` runs ≥1 scenario across ≥2 seeds and produces readable transcripts.
- [ ] `README.md` documents: WSL2 networking setup, how to start Unity bridge, how to register the MCP server with Claude Code, how to run a single scenario and the full matrix.

---

## 11. First message to Claude Code

> Read `prompt.md`. Confirm the architecture and the tool catalog back to me in 5 bullets, list any assumptions about my Unity scene, then start **Phase 1** (the C# Unity bridge). Stop after Phase 1 so I can drop the scripts into Unity and verify the TCP handshake before you build the MCP server.
