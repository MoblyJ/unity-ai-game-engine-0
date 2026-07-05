---
name: unity-ml-agents
description: Make an NPC that LEARNS behavior via reinforcement learning (Unity ML-Agents). Use only when the behavior is genuinely hard to script and emergent learning is the point (locomotion, adaptive opponents, self-play, automated playtesting). High effort and two-process (a separate Python training loop) — for 95% of NPCs use unity-npc-behavior instead. A live-editor agent can scaffold + wire it, but cannot run the training itself.
---

# unity-ml-agents — reinforcement-learning NPCs

Repo `repo/ml-agents`. Apache-2.0. Trained policy runs in-game as a lightweight `.onnx` (no Python at runtime).

## Install
- Unity: UPM `com.unity.ml-agents` (repo = v4.0.3 / Release 23, Unity 6000.0+; pulls `com.unity.ai.inference`).
- Python (training only): `pip install mlagents` (1.1.0 for Release 23) in a dedicated Python 3.10 venv.
  Keep the Unity package release and pip release **paired** or the handshake fails.

## Key API (namespaces `Unity.MLAgents`, `.Sensors`, `.Actuators`)
Subclass `Agent`: `Initialize()`, `OnEpisodeBegin()`, `CollectObservations(VectorSensor sensor)` →
`sensor.AddObservation(...)`, `OnActionReceived(ActionBuffers a)` → read
`a.ContinuousActions[i]`/`a.DiscreteActions[i]`, `Heuristic(in ActionBuffers)` for manual test.
Rewards: `AddReward`/`SetReward`; end with `EndEpisode()`. Required components: **Behavior Parameters**
(behavior name, obs/action sizes, holds the `.onnx`) + **Decision Requester**.

```csharp
using UnityEngine; using Unity.MLAgents; using Unity.MLAgents.Actuators; using Unity.MLAgents.Sensors;
public class ReachTargetAgent : Agent {
    public Transform target; Rigidbody rb;
    public override void Initialize() => rb = GetComponent<Rigidbody>();
    public override void OnEpisodeBegin() {
        transform.localPosition = new Vector3(Random.Range(-4f,4f),0.5f,Random.Range(-4f,4f));
        rb.linearVelocity = Vector3.zero;
        target.localPosition = new Vector3(Random.Range(-4f,4f),0.5f,Random.Range(-4f,4f));
    }
    public override void CollectObservations(VectorSensor s) { s.AddObservation(transform.localPosition); s.AddObservation(target.localPosition); }
    public override void OnActionReceived(ActionBuffers a) {
        rb.AddForce(new Vector3(a.ContinuousActions[0],0,a.ContinuousActions[1]) * 10f);
        AddReward(-0.001f);
        if (Vector3.Distance(transform.localPosition, target.localPosition) < 1.2f) { SetReward(1f); EndEpisode(); }
    }
    public override void Heuristic(in ActionBuffers o) { var c=o.ContinuousActions; c[0]=Input.GetAxis("Horizontal"); c[1]=Input.GetAxis("Vertical"); }
}
```
(Add Behavior Parameters: Vector Obs = 6, Continuous Actions = 2, plus Decision Requester + Rigidbody.)

## Training workflow (hand this to the user — the agent can't do it in-editor)
1. Write `config/<name>.yaml` (`behaviors:` keyed by the exact Behavior Name, `trainer_type: ppo`, `max_steps`).
2. In the venv: `mlagents-learn config/<name>.yaml --run-id=run1`, then press **Play** in Unity when prompted.
3. Train to convergence (watch reward / tensorboard); policy exports to `results/run1/<Behavior>.onnx`.
4. Assign the `.onnx` to Behavior Parameters → Model; set Behavior Type = Inference Only.
5. Press Play (no Python) — the NPC runs the trained policy.

## Gotchas
Fundamentally two-process (Editor ⇄ external Python over a socket) — the MCP bridge can only author the
`Agent` C#, add Behavior Parameters + Decision Requester, install the package, and later assign the `.onnx`.
Obs/action sizes in Behavior Parameters must exactly match the C#. Training is slow + nondeterministic.
Bottom line: **scaffold and wire it, then hand off the training loop.**
