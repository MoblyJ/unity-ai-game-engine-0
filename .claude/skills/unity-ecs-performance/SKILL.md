---
name: unity-ecs-performance
description: Run thousands of entities fast with Unity DOTS — ECS (Entities), the C# Job System, and Burst — for crowds, bullet-hell, and heavy parallel simulation. Use ONLY when scale genuinely demands it (thousands+ of things); for normal gameplay stay on plain MonoBehaviours, which the unity_* tools author live. ECS entities are NOT GameObjects, so they can't be edited with the live object tools.
---

# unity-ecs-performance — DOTS / ECS / Burst

Repo `repo/EntityComponentSystemSamples`. Unity Companion License. High effort — only when scale requires.

## Install
UPM id `com.unity.entities` (samples target the 1.4 line, Unity 6.2). Related: `com.unity.entities.graphics`,
`com.unity.physics`. Burst/Collections/Mathematics come transitively.

## Key API
- `World.DefaultGameObjectInjectionWorld`; systems as `ISystem` (Burst-friendly struct) or `SystemBase`
  (managed). Data in `IComponentData` structs. Iterate with `SystemAPI.Query<...>()`. Structural changes
  via `EntityManager` / `EntityCommandBuffer`. `LocalTransform` is the ECS transform (not `Transform`).
- Data enters ECS via **baking**: a `Baker<TAuthoring>` + authoring MonoBehaviours, usually in a SubScene.

```csharp
using Unity.Entities; using Unity.Mathematics; using Unity.Transforms; using Unity.Burst;
public struct MoveSpeed : IComponentData { public float3 Value; }
[BurstCompile]
public partial struct MoveSystem : ISystem {
    [BurstCompile] public void OnUpdate(ref SystemState state) {
        float dt = SystemAPI.Time.DeltaTime;
        foreach (var (t, s) in SystemAPI.Query<RefRW<LocalTransform>, RefRO<MoveSpeed>>())
            t.ValueRW.Position += s.ValueRO.Value * dt;
    }
}
```

## Why it's hard for a live-editor agent (be honest with the user)
- **No live authoring:** `unity_create_object`/`modify_object` can't touch entities — they come from a
  baked SubScene or a `Baker`, authored separately.
- `[BurstCompile]` code must be blittable (no managed types / captured refs) or it errors/falls back.
- Heavy API churn between Entities versions — pin to 1.x, ignore 0.x docs.

Recommendation: prefer MonoBehaviours + object pooling (see `unity-subsystems`) unless the user explicitly
needs thousands of entities. If they do: author the `IComponentData` + `ISystem` with `unity_create_script`,
add the package, and tell them a SubScene/baker is needed for data. Index on demand for samples:
`index_repo("repo/EntityComponentSystemSamples")`.
