# Track 1 gates — custom GameObject SDK backend (Unity 6000.4.4f1)

Date: 2026-10-31. Backend: `Native/RigidIPCBackend` (gate build: gravity/forces/kinematic only, no contact).
Integration id `0x52495043` (1380536387), name `RigidIPC`, registered through `IUnityPhysicsBackendSystem::Register`
from `UnityPluginLoad`. Plugin: `Assets/Plugins/x86_64/RigidIPCBackend.dll`, *Load on startup* (`isPreloaded: 1`),
Editor + Win64. Probe: `Assets/IPC/Editor/RigidIPCGateProbe.cs` (menu **IPC/Gate/...**). All runs are batch-mode
Editor launches via `Tools/Gates/Invoke-UnityBatch.ps1`; raw excerpts are in `evidence/`.

| Gate | Result | Evidence |
|---|---|---|
| A — DLL loads, registration succeeds | **PASS** | `RigidIPC_commands.log`: `UnityPluginLoad` then `Register(...) == true` on every launch; probe `native diagnostics: registered=1` |
| B — third option listed | **PASS (programmatic)** | `Physics.GetIntegrationInfos` (the dropdown's source) lists None, PhysX, **RigidIPC (0x52495043)** — `RigidIPC_gate_probe.log`. Dropdown screenshot not taken (batch mode). |
| C — stays selected after restart | **FAIL (3/3)** | `m_CurrentBackendId: 1380536387` saved; every restart logs `BackendSystem::Select was unable to find SDK integration with ID: 1562845264. Falling back to Null backend implementation.` → current = None (0xDECAFBAD). Plugin registers in the same second, after Select. |
| D, E | not run | Stop rule: C failed. |

Observations:
- Matches the public report for 6000.3.25f1 / 6000.6.0f1 (Select runs before native plugins preload in the Editor).
- The ID Select reports (1562845264 = 0x5D272050) is not the saved id, is stable across restarts here, is not a CRC32 of
  the backend name, and appears in no settings file. Unknown whether this is a log quirk or a wrong read; a Win64 player
  (plugins load before modules) would discriminate.
- No managed re-select hook exists in `UnityEngine/UnityEditor.PhysicsModule` (6000.4.4f1); `GetIntegrationInfos` is
  internal, selection is native-only.
- Rollback verified: saving PhysX and restarting selects PhysX again (`evidence/gateC_rollback_verify.physics-module.txt`).
  The project is committed with PhysX selected.
