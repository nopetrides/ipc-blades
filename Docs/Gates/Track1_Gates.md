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
| D | see Gate D below | |
| E | not run yet | |

Observations:
- 6000.6.7f1 defers backend selection until after native plugins load ("deferred post-plugins"), which is the fix the
  6000.4.4f1 run lacked. The odd id 1562845264 from run 1 does not appear.
- In 6000.6 the Editor log is written to the project's `Logs/Editor.log`, not `%LOCALAPPDATA%/Unity/Editor`.
- Deviation: the probe's `[InitializeOnLoad]` + `delayCall` entry did not fire on these launches, so each restart was
  read with **IPC/Gate/Log Backend Status** (same code path) instead.
- `EditorApplication.OpenProject` on the already-open project does not restart the Editor in 6000.6.7f1.
- The project is now saved with RigidIPC as the GameObject SDK. Roll back with
  **IPC/Gate/Save PhysX As GameObject SDK** and a restart.

## Gate D — minimal physics scene (Unity 6000.6.7f1, 2026-11-01)

Scene `Assets/IPC/Scenes/IPC_GateD.unity`: static Floor; Box_A (plain cube); Box_B (rotated 20/35/10 deg, scale
1 x 0.5 x 2, mass 2); Box_C (Rigidbody on a parent rotated 25 deg, collider on a child offset 0.5 m, rotated 45 deg,
scaled 0.8, collider center (0, 0.1, 0)), joined to Box_B by a ConfigurableJoint (linear axes locked). The gate stepper
integrates gravity only (no contact, no joint solve), so everything falls through the floor by design.

Evidence comes from `RigidIPCGateDiagnostics` (`Assets/IPC/Runtime`), which after every physics step compares Unity
against the backend's own state (`RigidIPC_GetShapeSnapshots` / `RigidIPC_GetShapeWorldVertices`):
body pose vs Rigidbody and Transform; each step vs the stepper's update `v' = (v + g dt)(1 - dt damping), p' = p + v' dt`;
and, only while armed (first 3 steps, 4 steps after a collider-center shift at step 30, or **IPC/Diagnostics** menu),
every shape vertex the backend holds vs the same vertex computed from the Unity collider. Tolerance: 1e-5 m or 4 float
ulps at the value's magnitude, whichever is larger; 1e-3 deg.

| Run | Result | Evidence |
|---|---|---|
| Editor run 1 (gate build as of Gate C) | Body poses exact (0 m, 0 deg) every step; gravity per step 0 m early, <= 1 float ulp later. **Vertices of all three Rigidbody shapes off by 3.9-5.8 m** (static floor exact). | `evidence/gateD_editor_diag.txt` (first block) |
| Fix 1 | `AttachShape` without `forceLocalPose`: pose is body-relative for a Rigidbody, world for a static body (the header says the frame depends on body type; the log showed (0,0,0) / (0.5,0.08,0) for Rigidbody shapes and the world pose for the floor). | `Native/RigidIPCBackend/src/RigidIPCBackend.cpp` |
| Editor run 2 | All vertices <= 7.2e-7 m. Changing collider centers in Play: Rigidbody shapes <= 5.4e-7 m, **static floor 0.100 m off** (`Shape.SetPose` for a static body is also world). | second block |
| Fix 2 | `Shape.SetPose` uses the same frame rule. | |
| Editor cycle 1 | 1791 steps, **0 flagged**. After the step-30 center shift, all four shapes <= 6.8e-7 m (floor 3.7e-9 m). | third block |
| Editor cycle 2 | 711 steps, **0 flagged**. Alive backend objects back to the pre-Play baseline after each Stop (4 bodies, 4 shapes, 1 joint, 2 materials, 3 worlds). Console: no errors or warnings except the OpenXR loader line. | last block; probe log |
| Win64 player (IL2CPP) | Backend selected at startup (`Selected backend (deferred post-plugins). Name: RigidIPC`), 300 steps with `-ipcGateQuitAfterSteps 300`, **0 flagged**, vertices <= 7.2e-7 m before and after the center shift, clean `ShutdownSdk` and exit code 0. | `evidence/gateD_player_*.txt` |

Command stream (both): CreateWorld, CreateBody, CreateShape, AttachShape, D6 CreateJoint with
Set6DofAxisLock / Limit / SpringLimit / AxisMotorConfiguration / MotorTarget*, Simulate, GetPose every step,
Shape.SetPose on collider-center changes, DestroyBody/DestroyJoint/DestroyWorld on Stop.

Open items (not blocking D):
- Read-back frames `Shape.GetPose` (returns world) and `Shape.GetLocalPose` (returns body-relative) are unverified; no
  Gate D path called them. Watch for them in Gate E.
- Destroyed backend objects are kept as tombstones by design (stale-handle safety), so the backend's total object
  count grows by ~21 per Play cycle; alive counts do not grow.
- The OpenXR "Uncaught Exception" reports in Editor and player logs are XR start-up without a headset
  (`XR_ERROR_FORM_FACTOR_UNAVAILABLE`), logged at Info level on every Play; unrelated to the backend.
- `EditorApplication.OpenProject` cannot restart the Editor in place, and MCP's code tool blocks
  `EditorApplication.Exit`; restarts use **File/Exit** and a relaunch.
