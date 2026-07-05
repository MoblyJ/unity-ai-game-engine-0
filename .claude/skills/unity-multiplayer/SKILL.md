---
name: unity-multiplayer
description: Make a Unity game multiplayer (co-op, 1v1 online, host/client) with Netcode for GameObjects — synced state, RPCs, and networked object spawning on the normal GameObject/component model. Use when the user wants online/networked play. Moderate effort; needs a NetworkManager + transport and host/client bootstrapping before anything syncs.
---

# unity-multiplayer — Netcode for GameObjects

Repo `repo/com.unity.netcode.gameobjects`. Unity Companion License (install via UPM; don't vendor).
Fits our GameObject+component workflow (unlike ECS netcode).

## Install
UPM id `com.unity.netcode.gameobjects` (repo pins `2.13.1`, `unity: 6000.0`). Pulls in
`com.unity.transport`. Add to `Packages/manifest.json` or via Package Manager.

## Key API (namespace `Unity.Netcode`)
- `NetworkManager` — scene singleton; `StartHost()/StartServer()/StartClient()`. Needs a transport
  assigned (Unity Transport) and a registered prefab list.
- `NetworkObject` — component required on any networked prefab; registered in NetworkManager.
- `NetworkBehaviour` — base class for networked scripts.
- `NetworkVariable<T>` — auto-synced state with read/write permissions (server-write by default).
- `[ServerRpc]` / `[ClientRpc]` — method names MUST end in `ServerRpc`/`ClientRpc` (mono-cecil weaving)
  or compilation fails.
- Spawn server-authoritatively with `NetworkObject.Spawn()`, not `Instantiate`.

```csharp
using Unity.Netcode;
public class PlayerHealth : NetworkBehaviour {
    public NetworkVariable<int> Health = new NetworkVariable<int>(
        100, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    [ServerRpc(RequireOwnership = false)]
    public void DamageServerRpc(int amount) { Health.Value -= amount; }  // server writes, all clients see it
}
```

## Bootstrapping (the part that's easy to miss)
Nothing syncs until: (1) a `NetworkManager` exists in the scene with a transport, (2) networked prefabs
have `NetworkObject` AND are in the NetworkManager prefab list, and (3) code/UI actually calls
`StartHost`/`StartClient`. For a quick local test, add a tiny OnGUI with Host/Client buttons.

## Gotchas / verification
`NetworkVariable` writes are permission-gated (server-only default). Hard to verify "live" — needs two
connected peers; use a Host + a second Client (build + editor, or ParrelSync). Confirm a clean compile
first (`unity_console_logs`), then test host/client connect and a synced value change.
Index on demand for exact API: `index_repo("repo/com.unity.netcode.gameobjects")` then `search_repo`.
