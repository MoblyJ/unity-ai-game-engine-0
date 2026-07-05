---
name: unity-npc-goap
description: Give NPCs goal-oriented action planning (GOAP) — you declare goals + actions with conditions/effects and the planner chains the steps itself (e.g. "hungry" → gather → craft → eat). Use for emergent, simulation-style AI (survival, colony sims, utility AI) with many agents. Overkill for simple scripted states — use unity-npc-behavior for those.
---

# unity-npc-goap — goal-oriented action planning (crashkonijn GOAP)

Repo `repo/GOAP`. Apache-2.0 (you may use/copy with attribution). Multi-threaded planner, scales to
~2000 agents.

## Install
UPM git URL (Package Manager → Add from git URL):
`https://github.com/crashkonijn/GOAP.git?path=/Package#3.1.2` (id `com.crashkonijn.goap`, min Unity
2022.3, works on 6000.x). Dependency `com.unity.collections` auto-resolves — the project MUST have it
or compilation fails.

## Key API
Namespaces `CrashKonijn.Goap.Runtime/.Core`, `CrashKonijn.Agent.Runtime/.Core`.
- **Scene:** exactly one `GoapBehaviour` runner in the scene. Each agent GameObject needs
  `AgentBehaviour` + `GoapActionProvider` + an `IAgentMoveBehaviour` (moves toward
  `provider.CurrentActionRequest.Target`).
- **Declare:** goals subclass `GoalBase` (`[GoapId("…")]`); actions subclass `GoapActionBase<TData>`
  (override `Perform` returning `ActionRunState.Continue/Completed/WaitThenComplete(t)`, and `Complete`);
  `TData : IActionData` holds `ITarget Target` + `[GetComponent]`-injected refs. World keys are classes
  used as effects/conditions.
- **Wire:** a `CapabilityFactoryBase` with `CapabilityBuilder`:
  `b.AddGoal<G>().AddCondition<Key>(Comparison.GreaterThanOrEqual, n)`,
  `b.AddAction<A>().AddEffect<Key>(EffectType.Increase).SetTarget<T>()`. Register in an
  `AgentTypeFactoryBase`: `this.CreateBuilder("DemoAgent").AddCapability<F>().Build()`.
- **Drive:** `provider.AgentType = goap.GetAgentType("DemoAgent")`; call
  `provider.RequestGoal<GoalA,GoalB>()`; react to `agent.Events.OnActionEnd += …` to request the next.

```csharp
using CrashKonijn.Agent.Core; using CrashKonijn.Goap.Core; using CrashKonijn.Goap.Runtime; using UnityEngine;
public class IsIdle : IWorldKey { }
[GoapId("Idle-Goal")] public class IdleGoal : GoalBase { }
[GoapId("Idle-Action")] public class IdleAction : GoapActionBase<IdleAction.Data> {
    public override IActionRunState Perform(IMonoAgent a, Data d, IActionContext c) => ActionRunState.WaitThenComplete(2f);
    public override void Complete(IMonoAgent a, Data d) { }
    public class Data : IActionData { public ITarget Target { get; set; } }
}
public class IdleCapability : CapabilityFactoryBase {
    public override ICapabilityConfig Create() {
        var b = new CapabilityBuilder("IdleCapability");
        b.AddGoal<IdleGoal>().AddCondition<IsIdle>(Comparison.GreaterThanOrEqual, 1).SetBaseCost(2);
        b.AddAction<IdleAction>().AddEffect<IsIdle>(EffectType.Increase).SetRequiresTarget(false);
        return b.Build();
    }
}
public class DemoAgentType : AgentTypeFactoryBase {
    public override IAgentTypeConfig Create() => this.CreateBuilder("DemoAgent").AddCapability<IdleCapability>().Build();
}
```

## Gotchas
Actions must be **stateless** (all state in `IActionData`; `[GetComponent]` auto-injects). Goals need
conditions, actions need effects — the planner only chains when an effect matches a goal/condition key.
Movement is YOUR job: implement `AgentMoveBehaviour`/`IActionMoveBehaviour` (often delegate to
`unity-npc-movement` or a NavMeshAgent). `[GoapId]` values must be unique + stable.

Ground it: `search_repo(path="repo/GOAP", query="CapabilityBuilder AddGoal AddAction effect condition")`.
Verify with a clean compile + play-mode screenshot; log the agent's chosen action to confirm planning.
