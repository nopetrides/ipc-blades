# Track 1 gates — custom GameObject SDK backend

## Run 1 — Unity 6000.4.4f1 (2026-10-31): Gate C FAIL

Backend: `Native/RigidIPCBackend` (gate build: gravity/forces/kinematic only, no contact).
Integration id `0x52495043` (1380536387), name `RigidIPC`, registered through `IUnityPhysicsBackendSystem::Register`
from `UnityPluginLoad`. Plugin: `Assets/Plugins/x86_64/RigidIPCBackend.dll`, *Load on startup* (`isPreloaded: 1`),
Editor + Win64. Probe: `Assets/IPC/Editor/RigidIPCGateProbe.cs` (menu **IPC/Gate/...**). All runs are batch-mode
Editor launches via `Tools/Gates/Invoke-UnityBatch.ps1`; raw excerpts are in `evidence/`.

| Gate | Result | Evidence |
|---|---|---|
| A — DLL loads, registration succeeds | **PASS** | `evidence/RigidIPC_commands.txt`: `UnityPluginLoad` then `Register(...) == true` on every launch; probe `native diagnostics: registered=1` |
| B — third option listed | **PASS (programmatic)** | `Physics.GetIntegrationInfos` (the dropdown's source) lists None, PhysX, **RigidIPC (0x52495043)** — `evidence/RigidIPC_gate_probe.txt`. Dropdown screenshot not taken (batch mode). |
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

## Run 2 — Unity 6000.6.7f1 (2026-11-01): Gate C PASS (3/3)

Only this duplicate was upgraded (commit `ed2fb42`). The backend was rebuilt against the 6000.6.7f1 `PluginAPI`
headers: the command enums are unchanged, but `Vec3`/`Vec4` are structs, `Pose` uses them, `EngineObjectId` is 8 bytes,
ignore pairs are keyed by a 16-bit ignore id, and `IntegrationInfo` gained per-joint-type limit ranges (left at the
unbounded defaults). All runs were in the interactive GUI Editor, restarted with File/Exit and a relaunch.

| Gate | Result | Evidence |
|---|---|---|
| A | **PASS** | `evidence/RigidIPC_commands_6000.6.txt`: `UnityPluginLoad`, `Register(...) == true`, then `InitializeSdk` and `CreateWorld` on our backend |
| B | **PASS** | Probe lists None, PhysX, RigidIPC (`evidence/RigidIPC_gate_probe_6000.6.txt`); Project Settings → Physics opened in the Editor |
| C | **PASS (3/3)** | Each restart: Editor.log `Selected backend (deferred post-plugins). Name: RigidIPC, Id: 0x52495043` with no "unable to find"/Null-backend line (`evidence/gateC66_restart{1,2,3}.physics-module.txt`); probe `current integration` = RigidIPC, `saved backend id` = 1380536387, native `initialized=1` |
| D, E | not run yet | |

Observations:
- 6000.6.7f1 defers backend selection until after native plugins load ("deferred post-plugins"), which is the fix the
  6000.4.4f1 run lacked. The odd id 1562845264 from run 1 does not appear.
- In 6000.6 the Editor log is written to the project's `Logs/Editor.log`, not `%LOCALAPPDATA%/Unity/Editor`.
- Deviation: the probe's `[InitializeOnLoad]` + `delayCall` entry did not fire on these launches, so each restart was
  read with **IPC/Gate/Log Backend Status** (same code path) instead.
- `EditorApplication.OpenProject` on the already-open project does not restart the Editor in 6000.6.7f1.
- The project is now saved with RigidIPC as the GameObject SDK. Roll back with
  **IPC/Gate/Save PhysX As GameObject SDK** and a restart.
