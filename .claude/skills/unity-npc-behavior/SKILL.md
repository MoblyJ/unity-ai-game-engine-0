---
name: unity-npc-behavior
description: Give a Unity NPC a decision-making brain with a behavior tree (patrol/chase/attack, states, conditions). Two options — Fluid Behavior Tree (simple, code+lambdas, polling) and NPBehave (event-driven + blackboard, cheaper at scale). Use when the user wants an enemy/NPC that "decides", reacts, patrols then chases, or switches behavior. Pair with unity-npc-movement for locomotion.
---

# unity-npc-behavior — behavior trees for NPC brains

Two proven repos. Pick by scale/reactivity. Both are MIT (you may copy their runtime into the project).

## Choose
- **Fluid Behavior Tree** (`repo/fluid-behavior-tree`) — fluent builder, inline lambdas, `.Tick()` each
  frame (polling). Best for a few NPCs and dead-simple authoring. Has a runtime visualizer.
- **NPBehave** (`repo/NPBehave`) — event-driven, Blackboard + `Stops` interrupts; only traverses when
  something changes. Best for many NPCs or reactive AI (perception/alarms/target switching). Code-only.

## Install
- Fluid: scoped registry in `Packages/manifest.json` (`"com.fluid.behavior-tree":"2.2.0"`, scope
  `com.fluid`, registry `https://registry.npmjs.org`) — OR copy
  `repo/fluid-behavior-tree/Assets/com.fluid.behavior-tree/Runtime` into `Assets/`.
- NPBehave: copy `repo/NPBehave/Scripts` → `Assets/NPBehave/Scripts` (no UPM, no asmdef → lands in
  Assembly-CSharp).

## Fluid — key API
Namespaces `CleverCrow.Fluid.BTs.Trees`, `…Tasks`. `new BehaviorTreeBuilder(gameObject)` →
`.Selector()/.Sequence("n")/.Condition("n",()=>bool)/.Do("n",()=>TaskStatus)` , close each composite
with `.End()`, finish `.Build()`. Tick in `Update()` with `_tree.Tick()`. Return `TaskStatus.Success/
Failure/Continue`.

```csharp
using UnityEngine;
using CleverCrow.Fluid.BTs.Tasks;
using CleverCrow.Fluid.BTs.Trees;
public class GuardAI : MonoBehaviour {
    BehaviorTree _tree; public Transform player; public float chaseRange = 5f;
    void Awake () {
        _tree = new BehaviorTreeBuilder(gameObject)
            .Selector()
                .Sequence("Chase")
                    .Condition("near", () => Vector3.Distance(transform.position, player.position) < chaseRange)
                    .Do("move", () => { transform.position = Vector3.MoveTowards(transform.position, player.position, 3f*Time.deltaTime); return TaskStatus.Success; })
                .End()
                .Do("Patrol", () => { transform.Rotate(0,30f*Time.deltaTime,0); return TaskStatus.Success; })
            .End().Build();
    }
    void Update () => _tree.Tick();
}
```
Gotchas: namespaces are `CleverCrow.Fluid.BTs.*`; every composite needs `.End()` + final `.Build()`;
if your script is in another asmdef, reference `Fluid.BehaviorTree.asmdef`.

## NPBehave — key API
Single namespace `NPBehave`. Build by nesting node ctors under `new Root(...)`; drive with
`tree.Start()` (NOT Tick — it runs off UnityContext's global clock); `tree.Stop()` in `OnDestroy`.
Blackboard: `tree.Blackboard["k"]=v`; interrupts via
`new BlackboardCondition("k", Operator.IS_EQUAL, val, Stops.IMMEDIATE_RESTART, child)`. Update the
blackboard in a `new Service(0.2f, UpdateBB, child)`.
Gotchas: `NPBehave.Action` collides with `System.Action` (fully-qualify if both used); always `Stop()`
on destroy or observers leak; custom `Task` must call `Stopped(result)` last; use `Stops` inside
`Selector` and return failure on abort; `NavMoveTo` needs a NavMeshAgent + baked NavMesh.

## Ground the exact API
`search_repo(path="repo/NPBehave", query="Stops rules blackboard interrupt")` or
`search_repo(path="repo/fluid-behavior-tree", query="BehaviorTreeBuilder condition do")`.

## Build + verify
Write the script with `unity_create_script`, attach with `unity_add_component`, set `player`/ranges via
`unity_set_component`, then `unity_console_logs` (clean compile) → `unity_play` + `unity_screenshot`.
