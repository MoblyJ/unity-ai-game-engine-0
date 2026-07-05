---
name: unity-api-lookup
description: Verify the EXACT current Unity C# API (signatures, enums, property names, overloads) before writing C#, using the official engine source in repo/UnityCsReference. Use whenever you're about to call a Unity API you're not 100% sure is current for Unity 6000.x — this is how you avoid deprecated/hardened calls (e.g. GetInstanceID, PhysicMaterial) and hallucinated signatures. Reference-only: read to verify, never copy/ship this code.
---

# unity-api-lookup — ground your C# in Unity's real source

Repo `repo/UnityCsReference` (Unity 6000.x). **Unity Reference-Only License** — read/verify only, never
copy or compile into shipped code. Grep it, read the real declaration, then generate code against it.

## Where to look (paths under `repo/UnityCsReference/`)
- `Runtime/Export/` — public runtime API root. Subfolders: `Animation/`, `Audio/`, `Camera/`, `Graphics/`,
  `Input/`, `SceneManager/`, `Transform/`, `Director/` (Timeline/Playables).
- `Modules/Physics/` — `Rigidbody`, `Collider`, `Physics.Raycast`, joints, `ForceMode`. `Modules/Physics2D/` for 2D.
- `Modules/AI/` — NavMesh: `NavMeshAgent`, `NavMesh.SamplePosition`, `NavMesh.CalculatePath`.
- `Modules/Input/` + `Runtime/Export/Input/` — legacy `Input` class.
- `Modules/ParticleSystem/` — `ParticleSystem` + emission/shape/velocity modules.
- `Modules/UI/` (uGUI: `Canvas`, `Image`, `Button.onClick`) + `Modules/UIElements/` (UI Toolkit).
- `Modules/Animation/` + `Runtime/Export/Animation/` — `Animator`, `AnimationClip`, `AnimationCurve`.
- `Modules/Terrain/` — `Terrain`, `TerrainData`.
- `Editor/Mono/` — editor APIs: `AssetDatabase`, `EditorUtility`, `MenuItem`, `PrefabUtility`.

## Example lookups
- `Rigidbody.AddForce` overloads / `ForceMode` → `grep -rn "AddForce" repo/UnityCsReference/Modules/Physics`
- `NavMeshAgent.SetDestination` → `grep -rn "SetDestination" repo/UnityCsReference/Modules/AI`
- `Animator.SetTrigger` → `grep -rn "public void SetTrigger" repo/UnityCsReference/Modules/Animation`
- `PrefabUtility.SaveAsPrefabAsset` → `grep -rn "SaveAsPrefabAsset" repo/UnityCsReference/Editor/Mono`

## The deprecation trap (why this matters)
Unity 6 turned some APIs into hard compile errors. If a type/member is `[Obsolete(..., true)]` in the
source, do NOT call it — either use the replacement it names, or (if you must, for cross-version code)
call it via reflection like the bridge's `BridgeUtil.Iid`/`BridgeUtil.FindType` pattern. Known ones:
`Object.GetInstanceID()`→`GetEntityId`, `PhysicMaterial`→`PhysicsMaterial`,
`EditorUtility.InstanceIDToObject`→`EntityIdToObject`, `Rigidbody.velocity`→`linearVelocity`.

Optional: `index_repo("repo/UnityCsReference")` once, then `search_repo` for symbols — but it's huge, so a
targeted `grep -rn <symbol> repo/UnityCsReference/<module>` is usually faster.
