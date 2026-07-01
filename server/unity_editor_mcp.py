"""unity_editor_mcp — a FastMCP server that lets Claude Code act as a Unity game developer.

Claude calls these tools; each is relayed as a JSON-line `op` to the Unity Editor bridge
(a C# extension) over TCP 127.0.0.1, which executes it live in the open Editor and returns
structured data. Because this server runs on Windows next to Unity, localhost just works.

Run:
    python unity_editor_mcp.py            # stdio (for Claude Code / MCP Inspector)
    python unity_editor_mcp.py --http     # streamable-http on :8000

The wide, constrained parameter surface lives in schemas.py (one Pydantic model per tool).
"""
from __future__ import annotations

import argparse
import json
from typing import Any

from mcp.server.fastmcp import FastMCP, Image
from mcp.types import ToolAnnotations

from schemas import (
    AddAudioInput,
    AddComponentInput,
    BakeNavMeshInput,
    ConsoleLogsInput,
    CreatePhysicsMaterialInput,
    CreateTerrainInput,
    CreateUIInput,
    CreateMaterialInput,
    CreateObjectInput,
    CreatePrefabInput,
    CreateSceneInput,
    CreateScriptInput,
    DeleteObjectInput,
    EmptyInput,
    ExecuteMenuInput,
    FindInput,
    FocusInput,
    GetObjectInput,
    InstantiatePrefabInput,
    ModifyObjectInput,
    QueryAssetsInput,
    SaveSceneInput,
    SceneFileInput,
    SceneInfoInput,
    ScreenshotInput,
    SelectInput,
    SetComponentInput,
    SetLightingInput,
)
from unity_client import UnityBridgeError, get_client

mcp = FastMCP("unity_editor_mcp")

# Annotation presets (prompt.md §5).
READ = ToolAnnotations(readOnlyHint=True, openWorldHint=True)
WRITE = ToolAnnotations(readOnlyHint=False, destructiveHint=False, openWorldHint=True)
WRITE_IDEMPOTENT = ToolAnnotations(
    readOnlyHint=False, destructiveHint=False, idempotentHint=True, openWorldHint=True
)
DESTRUCTIVE = ToolAnnotations(readOnlyHint=False, destructiveHint=True, openWorldHint=True)


# ---------------------------------------------------------------------------
# Shared relay + formatting helpers (centralized, not per-tool)
# ---------------------------------------------------------------------------


async def _send(op: str, model: Any = None) -> dict[str, Any]:
    """Relay one op to the Unity bridge; raise UnityBridgeError as a clean message."""
    args = model.model_dump(exclude_none=True) if model is not None else {}
    return await get_client().request(op, args)


def _render(data: dict[str, Any], fmt) -> str:
    """Format bridge data as pretty JSON or readable markdown, per the tool's response_format."""
    fmt_value = getattr(fmt, "value", fmt)
    if fmt_value == "json":
        return json.dumps(data, indent=2, ensure_ascii=False)
    return _to_markdown(data)


def _to_markdown(value: Any, depth: int = 0) -> str:
    pad = "  " * depth
    if isinstance(value, dict):
        if not value:
            return f"{pad}(empty)"
        lines = []
        for key, val in value.items():
            if isinstance(val, (dict, list)) and val:
                lines.append(f"{pad}- **{key}**:")
                lines.append(_to_markdown(val, depth + 1))
            else:
                lines.append(f"{pad}- **{key}**: {_scalar(val)}")
        return "\n".join(lines)
    if isinstance(value, list):
        if not value:
            return f"{pad}(none)"
        lines = []
        for item in value:
            if isinstance(item, (dict, list)):
                lines.append(f"{pad}-")
                lines.append(_to_markdown(item, depth + 1))
            else:
                lines.append(f"{pad}- {_scalar(item)}")
        return "\n".join(lines)
    return f"{pad}{_scalar(value)}"


def _scalar(value: Any) -> str:
    if isinstance(value, float):
        return f"{value:.4g}"
    return "(none)" if value is None else str(value)


def _errmsg(exc: Exception) -> str:
    return f"⚠️ {exc}" if isinstance(exc, UnityBridgeError) else f"⚠️ Unexpected error: {exc}"


# ---------------------------------------------------------------------------
# Query / read-only tools
# ---------------------------------------------------------------------------


@mcp.tool(name="unity_editor_version", annotations=READ)
async def unity_editor_version(params: EmptyInput) -> str:
    """Report the running Unity version, active render pipeline (URP/HDRP/Built-in), and whether
    this bridge supports it. Call this once at the start of a session to pick the right shaders/APIs."""
    try:
        return _render(await _send("editor_version", None), "markdown")
    except Exception as exc:  # noqa: BLE001
        return _errmsg(exc)


@mcp.tool(name="unity_scene_info", annotations=READ)
async def unity_scene_info(params: SceneInfoInput) -> str:
    """Return the active scene name, path, dirty flag, and (optionally) its GameObject hierarchy.
    Start here to see what exists before building."""
    try:
        return _render(await _send("scene_info", params), params.response_format)
    except Exception as exc:  # noqa: BLE001 - fail soft, return a readable message
        return _errmsg(exc)


@mcp.tool(name="unity_find", annotations=READ)
async def unity_find(params: FindInput) -> str:
    """Find GameObjects by name substring, tag, and/or component type. Returns instance ids +
    hierarchy paths you can pass as `target` to other tools."""
    try:
        return _render(await _send("find", params), params.response_format)
    except Exception as exc:  # noqa: BLE001
        return _errmsg(exc)


@mcp.tool(name="unity_get_object", annotations=READ)
async def unity_get_object(params: GetObjectInput) -> str:
    """Full detail of one GameObject: transform, tag/layer, active state, and each component with
    its serialized properties."""
    try:
        return _render(await _send("get_object", params), params.response_format)
    except Exception as exc:  # noqa: BLE001
        return _errmsg(exc)


@mcp.tool(name="unity_query_assets", annotations=READ)
async def unity_query_assets(params: QueryAssetsInput) -> str:
    """Search project assets via an AssetDatabase filter (e.g. 't:Material', 't:Prefab')."""
    try:
        return _render(await _send("query_assets", params), params.response_format)
    except Exception as exc:  # noqa: BLE001
        return _errmsg(exc)


@mcp.tool(name="unity_console_logs", annotations=READ)
async def unity_console_logs(params: ConsoleLogsInput) -> str:
    """Recent Unity console + compiler messages. Call this after create_script/add_component to
    confirm a clean compile before continuing."""
    try:
        return _render(await _send("console_logs", params), params.response_format)
    except Exception as exc:  # noqa: BLE001
        return _errmsg(exc)


@mcp.tool(name="unity_screenshot", annotations=READ)
async def unity_screenshot(params: ScreenshotInput) -> Image:
    """Capture the Scene or Game view as a PNG so you can visually confirm what you built.
    This is how you 'see live what happens' and self-correct."""
    data = await _send("screenshot", params)  # let errors propagate: Image return needs bytes
    import base64

    png = base64.b64decode(data["png_base64"])
    return Image(data=png, format="png")


# ---------------------------------------------------------------------------
# Authoring tools
# ---------------------------------------------------------------------------


@mcp.tool(name="unity_create_object", annotations=WRITE)
async def unity_create_object(params: CreateObjectInput) -> str:
    """Create a primitive (cube/sphere/plane/...) or empty GameObject with transform, parent, and
    optional color. Returns the new instance id + path. Appears in the Scene view immediately."""
    try:
        return _render(await _send("create_object", params), "markdown")
    except Exception as exc:  # noqa: BLE001
        return _errmsg(exc)


@mcp.tool(name="unity_modify_object", annotations=WRITE_IDEMPOTENT)
async def unity_modify_object(params: ModifyObjectInput) -> str:
    """Rename / move / rotate / scale / reparent / retag / set active state of an existing object."""
    try:
        return _render(await _send("modify_object", params), "markdown")
    except Exception as exc:  # noqa: BLE001
        return _errmsg(exc)


@mcp.tool(name="unity_delete_object", annotations=DESTRUCTIVE)
async def unity_delete_object(params: DeleteObjectInput) -> str:
    """Destroy a GameObject (undoable with Ctrl-Z in the Editor)."""
    try:
        return _render(await _send("delete_object", params), "markdown")
    except Exception as exc:  # noqa: BLE001
        return _errmsg(exc)


@mcp.tool(name="unity_add_component", annotations=WRITE)
async def unity_add_component(params: AddComponentInput) -> str:
    """Add a component (built-in like Rigidbody/Light, or a user script) to an object, optionally
    setting serialized field values in the same call."""
    try:
        return _render(await _send("add_component", params), "markdown")
    except Exception as exc:  # noqa: BLE001
        return _errmsg(exc)


@mcp.tool(name="unity_set_component", annotations=WRITE_IDEMPOTENT)
async def unity_set_component(params: SetComponentInput) -> str:
    """Set serialized field values on an existing component (e.g. Rigidbody.mass, Light.intensity)."""
    try:
        return _render(await _send("set_component", params), "markdown")
    except Exception as exc:  # noqa: BLE001
        return _errmsg(exc)


@mcp.tool(name="unity_create_material", annotations=WRITE)
async def unity_create_material(params: CreateMaterialInput) -> str:
    """Create a material asset (shader + color + metallic/smoothness) and optionally assign it to
    an object's renderer."""
    try:
        return _render(await _send("create_material", params), "markdown")
    except Exception as exc:  # noqa: BLE001
        return _errmsg(exc)


@mcp.tool(name="unity_create_prefab", annotations=WRITE)
async def unity_create_prefab(params: CreatePrefabInput) -> str:
    """Save a scene GameObject as a reusable .prefab asset."""
    try:
        return _render(await _send("create_prefab", params), "markdown")
    except Exception as exc:  # noqa: BLE001
        return _errmsg(exc)


@mcp.tool(name="unity_instantiate_prefab", annotations=WRITE)
async def unity_instantiate_prefab(params: InstantiatePrefabInput) -> str:
    """Instantiate a prefab asset into the active scene at a position/rotation/parent."""
    try:
        return _render(await _send("instantiate_prefab", params), "markdown")
    except Exception as exc:  # noqa: BLE001
        return _errmsg(exc)


@mcp.tool(name="unity_create_script", annotations=WRITE)
async def unity_create_script(params: CreateScriptInput) -> str:
    """Write a C# script asset and refresh AssetDatabase to compile it. Optionally attach it to an
    object AFTER the compile finishes. The class name must match `name`.
    IMPORTANT: after this, call unity_console_logs to confirm the script compiled cleanly."""
    try:
        return _render(await _send("create_script", params), "markdown")
    except Exception as exc:  # noqa: BLE001
        return _errmsg(exc)


@mcp.tool(name="unity_set_lighting", annotations=WRITE_IDEMPOTENT)
async def unity_set_lighting(params: SetLightingInput) -> str:
    """Set environment lighting: ambient color, skybox, fog, and the main directional light (sun)."""
    try:
        return _render(await _send("set_lighting", params), "markdown")
    except Exception as exc:  # noqa: BLE001
        return _errmsg(exc)


@mcp.tool(name="unity_bake_navmesh", annotations=WRITE)
async def unity_bake_navmesh(params: BakeNavMeshInput) -> str:
    """Bake a NavMesh over the current scene geometry so NavMeshAgents can path-find. Add a
    NavMeshAgent component (via unity_add_component) to any object you want to move on it."""
    try:
        return _render(await _send("bake_navmesh", params), "markdown")
    except Exception as exc:  # noqa: BLE001
        return _errmsg(exc)


@mcp.tool(name="unity_create_terrain", annotations=WRITE)
async def unity_create_terrain(params: CreateTerrainInput) -> str:
    """Create a Terrain (with TerrainData asset) of a given size/resolution at a position."""
    try:
        return _render(await _send("create_terrain", params), "markdown")
    except Exception as exc:  # noqa: BLE001
        return _errmsg(exc)


@mcp.tool(name="unity_create_ui", annotations=WRITE)
async def unity_create_ui(params: CreateUIInput) -> str:
    """Create a uGUI element (text/button/image/slider/panel/canvas). Unity auto-creates the Canvas
    and EventSystem as needed. Use for HUDs, health bars (slider), menus. Returns the new object."""
    try:
        return _render(await _send("create_ui", params), "markdown")
    except Exception as exc:  # noqa: BLE001
        return _errmsg(exc)


@mcp.tool(name="unity_add_audio", annotations=WRITE)
async def unity_add_audio(params: AddAudioInput) -> str:
    """Add/configure an AudioSource on an object and optionally assign an AudioClip asset."""
    try:
        return _render(await _send("add_audio", params), "markdown")
    except Exception as exc:  # noqa: BLE001
        return _errmsg(exc)


@mcp.tool(name="unity_create_physics_material", annotations=WRITE)
async def unity_create_physics_material(params: CreatePhysicsMaterialInput) -> str:
    """Create a physics material (friction + bounciness) and optionally assign it to an object's
    Collider. Use for bouncy balls, icy floors, etc."""
    try:
        return _render(await _send("create_physics_material", params), "markdown")
    except Exception as exc:  # noqa: BLE001
        return _errmsg(exc)


@mcp.tool(name="unity_create_scene", annotations=WRITE)
async def unity_create_scene(params: CreateSceneInput) -> str:
    """Create a new .unity scene asset and open it (replacing the current scene, or additively)."""
    try:
        return _render(await _send("create_scene", params), "markdown")
    except Exception as exc:  # noqa: BLE001
        return _errmsg(exc)


@mcp.tool(name="unity_open_scene", annotations=WRITE)
async def unity_open_scene(params: SceneFileInput) -> str:
    """Open an existing .unity scene by asset path."""
    try:
        return _render(await _send("open_scene", params), "markdown")
    except Exception as exc:  # noqa: BLE001
        return _errmsg(exc)


@mcp.tool(name="unity_save_scene", annotations=WRITE_IDEMPOTENT)
async def unity_save_scene(params: SaveSceneInput) -> str:
    """Save the active scene (in place, or 'save as' to a new path)."""
    try:
        return _render(await _send("save_scene", params), "markdown")
    except Exception as exc:  # noqa: BLE001
        return _errmsg(exc)


@mcp.tool(name="unity_select", annotations=WRITE_IDEMPOTENT)
async def unity_select(params: SelectInput) -> str:
    """Select an object in the Editor (highlights it in the Hierarchy + Inspector)."""
    try:
        return _render(await _send("select", params), "markdown")
    except Exception as exc:  # noqa: BLE001
        return _errmsg(exc)


@mcp.tool(name="unity_focus", annotations=WRITE_IDEMPOTENT)
async def unity_focus(params: FocusInput) -> str:
    """Frame the Scene view camera on an object so it's centered in the next screenshot."""
    try:
        return _render(await _send("focus", params), "markdown")
    except Exception as exc:  # noqa: BLE001
        return _errmsg(exc)


@mcp.tool(name="unity_execute_menu", annotations=WRITE)
async def unity_execute_menu(params: ExecuteMenuInput) -> str:
    """Escape hatch: invoke any Unity Editor menu item by path (e.g. 'GameObject/Align With View')."""
    try:
        return _render(await _send("execute_menu", params), "markdown")
    except Exception as exc:  # noqa: BLE001
        return _errmsg(exc)


@mcp.tool(name="unity_undo", annotations=WRITE)
async def unity_undo(params: EmptyInput) -> str:
    """Undo the last editor operation."""
    try:
        return _render(await _send("undo"), "markdown")
    except Exception as exc:  # noqa: BLE001
        return _errmsg(exc)


@mcp.tool(name="unity_redo", annotations=WRITE)
async def unity_redo(params: EmptyInput) -> str:
    """Redo the last undone editor operation."""
    try:
        return _render(await _send("redo"), "markdown")
    except Exception as exc:  # noqa: BLE001
        return _errmsg(exc)


@mcp.tool(name="unity_play", annotations=WRITE)
async def unity_play(params: EmptyInput) -> str:
    """Enter Play Mode to test runtime behavior."""
    try:
        return _render(await _send("play"), "markdown")
    except Exception as exc:  # noqa: BLE001
        return _errmsg(exc)


@mcp.tool(name="unity_stop", annotations=WRITE)
async def unity_stop(params: EmptyInput) -> str:
    """Exit Play Mode back to edit mode."""
    try:
        return _render(await _send("stop"), "markdown")
    except Exception as exc:  # noqa: BLE001
        return _errmsg(exc)


def main() -> None:
    parser = argparse.ArgumentParser(description="unity_editor_mcp server")
    parser.add_argument("--http", action="store_true", help="Serve over streamable-http on :8000")
    args = parser.parse_args()
    if args.http:
        mcp.settings.port = 8000
        mcp.run(transport="streamable-http")
    else:
        mcp.run()


if __name__ == "__main__":
    main()
