---
name: unity-npc-movement
description: Give NPCs smooth, physics-based locomotion with classic steering behaviors — Seek, Arrive, Flee, Evade, Pursue, Wander, Separation/Cohesion (flocking), CollisionAvoidance, WallAvoidance, FollowPath. Use for chasing/fleeing, patrol, wandering, flocks, and obstacle avoidance without NavMesh. This is the "legs" — pair it with a brain (unity-npc-behavior / unity-npc-goap) that decides where to go.
---

# unity-npc-movement — steering behaviors (sturdyspoon unity-movement-ai)

Repo `repo/unity-movement-ai`. MIT (copy freely). It only handles *how to move*, not *what to do*.

## Install
No UPM — copy `repo/unity-movement-ai/Assets/UnityMovementAI/Scripts/` (especially
`Scripts/Units/Movement/`) into `Assets/`, or import the repo-root `UnityMovementAI.unitypackage`.

## Key API (namespace `UnityMovementAI`)
- Every steering GameObject needs a `Rigidbody` (or `Rigidbody2D`) **plus** a `MovementAIRigidbody`
  wrapper (auto-detects 2D/3D; exposes `Velocity`, `Position`, `Radius`).
- `SteeringBasics` (`[RequireComponent(MovementAIRigidbody)]`) is the hub: `Seek(Vector3 target)`,
  `Arrive(target)` return acceleration; apply with `Steer(accel)` and orient with
  `LookWhereYoureGoing()`. Tunables: `maxVelocity`, `maxAcceleration`, `turnSpeed`, `slowRadius`.
- Other behaviors expose `GetSteering(...)`: `Wander1`, `Flee`, `Evade`, `Pursue(MovementAIRigidbody)`,
  `Separation`, `Cohesion`, `CollisionAvoidance`, `WallAvoidance`, `FollowPath`.
- Tick in **`FixedUpdate`** (physics): gather accel → `steering.Steer(accel)` → `LookWhereYoureGoing()`.
  See `Units/*Unit.cs` (`SeekUnit`, `Wander1Unit`, `PursueUnit`) for canonical usage.

```csharp
using UnityEngine; using UnityMovementAI;
[RequireComponent(typeof(SteeringBasics), typeof(Wander1))]
public class WanderAgent : MonoBehaviour {
    SteeringBasics steering; Wander1 wander;
    void Start () { steering = GetComponent<SteeringBasics>(); wander = GetComponent<Wander1>(); }
    void FixedUpdate () {
        Vector3 accel = wander.GetSteering();     // or steering.Seek(target.position) / flee.GetSteering(threat.position)
        steering.Steer(accel);
        steering.LookWhereYoureGoing();
    }
}
```

## Gotchas
- Needs `Rigidbody` + `MovementAIRigidbody` on the same object; behaviors read/write velocity through
  the wrapper. For top-down, turn gravity off / freeze rotation (the lib controls velocity).
- Flocking behaviors (`Separation`/`Cohesion`/`CollisionAvoidance`) need a `NearSensor` trigger collider
  to find neighbors.
- **Unity 6 warning:** code uses `Rigidbody.velocity` (renamed `linearVelocity` in 6000.x) — compiles
  with a deprecation *warning*. If a clean-compile gate treats warnings as errors, patch the `Velocity`
  getter/setter in `MovementAIRigidbody.cs` to use `linearVelocity`.

Ground it: `search_repo(path="repo/unity-movement-ai", query="SteeringBasics Seek Steer LookWhereYoureGoing")`.
Build the script + wire components with `unity_add_component`, then play + screenshot to watch it move.
