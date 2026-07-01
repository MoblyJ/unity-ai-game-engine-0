"""Pydantic v2 input models for the unity_editor_mcp authoring tools.

Every model forbids extra fields, strips whitespace, and constrains every field so the
LLM gets a tight, self-documenting schema. These models are the single source of truth
for the tool parameter surface; the C# `CommandDispatcher` reads the same `op`/`args`.
"""
from __future__ import annotations

from enum import Enum
from typing import Literal, Optional

from pydantic import BaseModel, ConfigDict, Field

# ---------------------------------------------------------------------------
# Shared config + reusable field types
# ---------------------------------------------------------------------------

_CONFIG = ConfigDict(
    str_strip_whitespace=True,
    validate_assignment=True,
    extra="forbid",
)

Vector3 = tuple[float, float, float]


class _Model(BaseModel):
    model_config = _CONFIG


class ResponseFormat(str, Enum):
    json = "json"
    markdown = "markdown"


class Primitive(str, Enum):
    empty = "empty"
    cube = "cube"
    sphere = "sphere"
    capsule = "capsule"
    cylinder = "cylinder"
    plane = "plane"
    quad = "quad"


class ViewKind(str, Enum):
    scene = "scene"
    game = "game"


# ---------------------------------------------------------------------------
# Query / read-only tools
# ---------------------------------------------------------------------------


class SceneInfoInput(_Model):
    include_hierarchy: bool = Field(
        True, description="Include the full GameObject hierarchy tree of the active scene."
    )
    max_objects: int = Field(
        200, ge=1, le=5000, description="Cap on hierarchy nodes returned (breadth-first)."
    )
    response_format: ResponseFormat = Field(
        ResponseFormat.markdown, description="`markdown` for humans, `json` for exact data."
    )


class FindInput(_Model):
    name: Optional[str] = Field(
        None, description="Substring match against GameObject names (case-insensitive)."
    )
    tag: Optional[str] = Field(None, description="Exact Unity tag to match, e.g. 'Player'.")
    component: Optional[str] = Field(
        None, description="Component type name to match, e.g. 'Rigidbody' or 'Light'."
    )
    max_results: int = Field(50, ge=1, le=500, description="Maximum matches to return.")
    response_format: ResponseFormat = Field(ResponseFormat.markdown)


class GetObjectInput(_Model):
    target: str = Field(
        ...,
        min_length=1,
        description="GameObject identity: instance id (numeric string) or hierarchy path 'Parent/Child'.",
    )
    include_components: bool = Field(
        True, description="Include each component and its serialized properties."
    )
    response_format: ResponseFormat = Field(ResponseFormat.markdown)


class QueryAssetsInput(_Model):
    filter: str = Field(
        "",
        description="AssetDatabase search filter, e.g. 't:Material', 't:Prefab', 't:Script name'.",
    )
    max_results: int = Field(100, ge=1, le=1000)
    response_format: ResponseFormat = Field(ResponseFormat.markdown)


class ConsoleLogsInput(_Model):
    max_entries: int = Field(50, ge=1, le=500, description="Most recent console/compile messages.")
    min_severity: Literal["log", "warning", "error"] = Field(
        "log", description="Drop messages below this severity."
    )
    response_format: ResponseFormat = Field(ResponseFormat.markdown)


class ScreenshotInput(_Model):
    view: ViewKind = Field(ViewKind.scene, description="Capture the Scene view or the Game view.")
    width: int = Field(1280, ge=64, le=4096, description="Output width in pixels.")
    height: int = Field(720, ge=64, le=4096, description="Output height in pixels.")


# ---------------------------------------------------------------------------
# Authoring tools
# ---------------------------------------------------------------------------


class CreateObjectInput(_Model):
    primitive: Primitive = Field(
        Primitive.empty, description="Primitive to spawn, or 'empty' for a bare GameObject."
    )
    name: Optional[str] = Field(
        None, max_length=128, description="Name for the new object (defaults to the primitive name)."
    )
    position: Vector3 = Field((0.0, 0.0, 0.0), description="World position [x,y,z].")
    rotation: Vector3 = Field((0.0, 0.0, 0.0), description="Euler rotation in degrees [x,y,z].")
    scale: Vector3 = Field((1.0, 1.0, 1.0), description="Local scale [x,y,z].")
    parent: Optional[str] = Field(
        None, description="Parent identity (instance id or hierarchy path); root if omitted."
    )
    color: Optional[Vector3] = Field(
        None, description="Optional RGB 0..1; creates+assigns a material on the renderer."
    )


class ModifyObjectInput(_Model):
    target: str = Field(..., min_length=1, description="Object identity (instance id or hierarchy path).")
    name: Optional[str] = Field(None, max_length=128, description="Rename the object.")
    position: Optional[Vector3] = Field(None, description="New world position [x,y,z].")
    rotation: Optional[Vector3] = Field(None, description="New euler rotation degrees [x,y,z].")
    scale: Optional[Vector3] = Field(None, description="New local scale [x,y,z].")
    parent: Optional[str] = Field(None, description="Reparent under this identity; '' detaches to root.")
    tag: Optional[str] = Field(None, description="Set the Unity tag (must already exist in the project).")
    layer: Optional[int] = Field(None, ge=0, le=31, description="Set the layer index 0..31.")
    active: Optional[bool] = Field(None, description="Set GameObject active state.")


class DeleteObjectInput(_Model):
    target: str = Field(..., min_length=1, description="Object identity to destroy (undoable).")


class AddComponentInput(_Model):
    target: str = Field(..., min_length=1, description="Object to add the component to.")
    component: str = Field(
        ..., min_length=1, description="Component type name, e.g. 'Rigidbody', 'BoxCollider', or a user script."
    )
    properties: dict[str, object] = Field(
        default_factory=dict,
        description="Optional serialized field values to set immediately, e.g. {'mass': 2.0}.",
    )


class SetComponentInput(_Model):
    target: str = Field(..., min_length=1, description="Object that owns the component.")
    component: str = Field(..., min_length=1, description="Component type name to edit.")
    properties: dict[str, object] = Field(
        ..., description="Serialized field values to set, e.g. {'useGravity': false, 'mass': 5}."
    )


class CreateMaterialInput(_Model):
    name: str = Field(..., min_length=1, max_length=128, description="Material asset name.")
    color: Vector3 = Field((0.8, 0.8, 0.8), description="Base/albedo color RGB 0..1.")
    shader: str = Field(
        "Universal Render Pipeline/Lit",
        description="Shader name. Use 'Standard' for Built-in RP, URP/HDRP Lit otherwise.",
    )
    metallic: float = Field(0.0, ge=0.0, le=1.0, description="Metallic 0..1 (if the shader supports it).")
    smoothness: float = Field(0.5, ge=0.0, le=1.0, description="Smoothness/glossiness 0..1.")
    assign_to: Optional[str] = Field(
        None, description="Optional object identity to assign this material's renderer to."
    )
    save_path: str = Field(
        "Assets/Materials", description="Folder under the project to save the .mat asset."
    )


class CreatePrefabInput(_Model):
    target: str = Field(..., min_length=1, description="Scene object to turn into a prefab asset.")
    save_path: str = Field(
        "Assets/Prefabs", description="Folder to write the .prefab asset into."
    )
    connect: bool = Field(
        True, description="Keep the scene object linked to the new prefab (a prefab instance)."
    )


class InstantiatePrefabInput(_Model):
    prefab_path: str = Field(
        ..., min_length=1, description="Asset path to the prefab, e.g. 'Assets/Prefabs/Enemy.prefab'."
    )
    position: Vector3 = Field((0.0, 0.0, 0.0), description="World position for the instance.")
    rotation: Vector3 = Field((0.0, 0.0, 0.0), description="Euler rotation degrees.")
    parent: Optional[str] = Field(None, description="Optional parent identity.")


class CreateScriptInput(_Model):
    name: str = Field(
        ..., min_length=1, max_length=128, description="Class + file name (without .cs), e.g. 'Spinner'."
    )
    content: str = Field(
        ...,
        min_length=1,
        description="Full C# source for the script. Class name must match `name`. Triggers a compile.",
    )
    save_path: str = Field("Assets/Scripts", description="Folder to write the .cs file into.")
    attach_to: Optional[str] = Field(
        None,
        description="Optional object to add the component to AFTER compilation completes.",
    )


class SetLightingInput(_Model):
    ambient_color: Optional[Vector3] = Field(None, description="Ambient/environment color RGB 0..1.")
    skybox_material: Optional[str] = Field(None, description="Asset path to a skybox material.")
    fog: Optional[bool] = Field(None, description="Enable/disable fog.")
    fog_color: Optional[Vector3] = Field(None, description="Fog color RGB 0..1.")
    fog_density: Optional[float] = Field(None, ge=0.0, le=1.0, description="Exponential fog density.")
    sun_rotation: Optional[Vector3] = Field(
        None, description="Euler rotation of the main directional light (sun angle)."
    )
    sun_intensity: Optional[float] = Field(None, ge=0.0, le=8.0, description="Directional light intensity.")


class SceneFileInput(_Model):
    path: str = Field(
        ..., min_length=1, description="Scene asset path, e.g. 'Assets/Scenes/Main.unity'."
    )


class CreateSceneInput(_Model):
    path: str = Field(..., min_length=1, description="Path for the new .unity scene asset.")
    additive: bool = Field(False, description="Open additively instead of replacing the current scene.")


class SaveSceneInput(_Model):
    path: Optional[str] = Field(
        None, description="Optional 'save as' path; omit to save the active scene in place."
    )


class SelectInput(_Model):
    target: str = Field(..., min_length=1, description="Object identity to select in the editor.")


class FocusInput(_Model):
    target: str = Field(..., min_length=1, description="Object identity to frame in the Scene view camera.")


class ExecuteMenuInput(_Model):
    menu_path: str = Field(
        ..., min_length=1, description="Unity menu item path, e.g. 'GameObject/Align With View'."
    )


# ---------------------------------------------------------------------------
# Phase 1: richer authoring
# ---------------------------------------------------------------------------


class UIElement(str, Enum):
    text = "text"
    button = "button"
    image = "image"
    raw_image = "raw_image"
    slider = "slider"
    panel = "panel"
    canvas = "canvas"


class BakeNavMeshInput(_Model):
    agent_radius: float = Field(0.5, ge=0.05, le=10.0, description="NavMesh agent radius (m).")
    agent_height: float = Field(2.0, ge=0.1, le=20.0, description="NavMesh agent height (m).")
    agent_slope: float = Field(45.0, ge=0.0, le=60.0, description="Max walkable slope (degrees).")
    agent_climb: float = Field(0.4, ge=0.0, le=5.0, description="Max step/ledge height the agent can climb (m).")
    size: float = Field(500.0, ge=10.0, le=5000.0, description="Cubic world bounds to collect geometry within (m).")


class CreateTerrainInput(_Model):
    name: Optional[str] = Field(None, max_length=128, description="Name for the terrain GameObject.")
    resolution: int = Field(513, description="Heightmap resolution; must be 2^n + 1 (e.g. 129, 257, 513, 1025).")
    width: float = Field(100.0, ge=1.0, le=10000.0, description="Terrain width in meters (X).")
    length: float = Field(100.0, ge=1.0, le=10000.0, description="Terrain length in meters (Z).")
    height: float = Field(30.0, ge=1.0, le=5000.0, description="Max terrain height in meters (Y).")
    position: Vector3 = Field((0.0, 0.0, 0.0), description="World position of the terrain corner.")


class CreateUIInput(_Model):
    element: UIElement = Field(UIElement.text, description="Which UI element to create.")
    name: Optional[str] = Field(None, max_length=128, description="Name for the new UI object.")
    text: Optional[str] = Field(
        None, max_length=500, description="Label text (applies to text/button elements)."
    )
    position: Optional[Vector3] = Field(
        None, description="Anchored position [x,y,_] within its Canvas (z ignored)."
    )


class AddAudioInput(_Model):
    target: str = Field(..., min_length=1, description="Object to attach the AudioSource to.")
    clip_path: Optional[str] = Field(
        None, description="Asset path to an AudioClip, e.g. 'Assets/Audio/step.wav'."
    )
    play_on_awake: Optional[bool] = Field(None, description="Play automatically when the scene starts.")
    loop: Optional[bool] = Field(None, description="Loop the clip.")
    spatial_blend: Optional[float] = Field(
        None, ge=0.0, le=1.0, description="0 = 2D sound, 1 = fully 3D/positional."
    )
    volume: Optional[float] = Field(None, ge=0.0, le=1.0, description="Playback volume 0..1.")


class CreatePhysicsMaterialInput(_Model):
    name: str = Field(..., min_length=1, max_length=128, description="Physics material asset name.")
    dynamic_friction: float = Field(0.6, ge=0.0, le=1.0, description="Friction while moving.")
    static_friction: float = Field(0.6, ge=0.0, le=1.0, description="Friction when at rest.")
    bounciness: float = Field(0.0, ge=0.0, le=1.0, description="0 = no bounce, 1 = full bounce.")
    assign_to: Optional[str] = Field(
        None, description="Optional object with a Collider to assign this material to."
    )


class EmptyInput(_Model):
    """Tools that take no arguments (undo/redo/play/stop)."""
