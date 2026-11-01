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

Correction found during Gate E: the rotation checks above used `Quaternion.Angle`, which returns 0 for anything below
about 0.16 deg (its dot-product epsilon), so the "0 deg" body-rotation figures only bound the error by that floor. The
vertex comparisons were not affected and would have caught such an error (0.16 deg is ~1.4 mm at 0.5 m). The
diagnostics now use `GateMath.AngleDeg` (atan2 in double). Re-run with it (2026-11-01, 32 steps including the step-30
center shift): body rotation d <= 2.4e-15 deg, vertices <= 7.2e-7 m, **0 flagged**
(`evidence/gateD_editor_rerun_gatemath.txt`).

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

## Gate E — Ragdoll Animator 2 on the backend (Unity 6000.6.7f1, 2026-11-01)

Scene `Assets/IPC/Scenes/IPC_GateE.unity`: static Floor plus an unmodified instance of `Assets/P3T/Prefabs/Dummy_RA2_Base`
with the station's one override (Animator controller `Motions_Flat_B`), i.e. the thesis station's dummy configuration.
No RA2 file was changed. The prefab ships a **pre-generated** physics dummy (`Dummy_RA2_Base-Ragdoll`: 20 Rigidbodies,
20 ConfigurableJoints, 11 box + 9 capsule colliders), so RA2 does not build the clone at Play; it takes over the existing
one. The gate stepper still has no contact and no joint solve.

Evidence comes from `RigidIPCRagdollDiagnostics` (new, `Assets/IPC/Runtime`) plus the Gate D component in shape-only mode
(`checkGravity` off, vertex compare for the first 3 steps). After every physics step it reads the backend through new
diagnostics exports (`RigidIPC_GetBodySnapshots`, `RigidIPC_GetJointSnapshots`, `RigidIPC_GetCommandCounts`) and compares:
- commands: the backend's histogram differenced per step, excluding the component's own reads;
- bodies: pose, linear/angular velocity, kinematic and gravity flags, mass, damping, max velocities, centre of mass,
  `worldCenterOfMass`, inertia tensor and rotation, sleep state, against the Rigidbody;
- joints (matched by `userData` = ConfigurableJoint): type, which backend actor is the own/connected body, own anchor
  position and frame axes vs `anchor`/`axis`/`secondaryAxis`, connected anchor vs `connectedAnchor`, linear and angular
  axis locks, the drive of the active `rotationDriveMode` and the linear drives, and `targetRotation` (bitwise, plus angle
  as-is and inverted), with the per-joint count of `Set6DofMotorTargetRotation` calls;
- any error or exception logged during Play.

| Run | Result | Evidence |
|---|---|---|
| Editor run 1 | 1861 steps. Bodies: pose, velocities and all properties match every step (pos 0 m, rot <= 3e-15 deg). Joints: anchors <= 6.6e-7 m, frame axes <= 5.9e-5 deg, target rotation equal as-is. No logged errors. **9 capsules: tips off by up to 0.28 m** (radial points <= 1.2e-7 m). Two diagnostic artifacts flagged every step (below). | `evidence/gateE_editor_diag.txt` (run 1), `gateE_editor_shapes.txt` |
| Fix 3 (backend) | Capsule `height` is the PhysX **half-height** (half the cylinder length, caps excluded), not the total height the header comment states. Measured on all 9 capsules: `CapsuleCollider.height * scale == 2 * (height + radius)` within 3e-8 m (total-height reading off by up to 0.30 m). Fixed in `LocalExtents` (bounds) and `LocalShapePoints` (vertex diagnostic). | `RigidIPCBackend.cpp` |
| Diagnostic fixes | (a) Drives: in Slerp mode Unity never sends AngularX/AngularYZ drive configuration, so those rows keep the backend defaults (`maxForce` Infinity vs Unity's `float.MaxValue`); now listed as "not sent", not compared. (b) "Target changed but not sent": `AngleDeg(q, q)` is ~1e-22 deg, not 0, so the change test is now component-wise. | `RigidIPCRagdollDiagnostics.cs` |
| Editor run 2 | 1960 steps, **0 flagged, 0 errors/exceptions**. Target rotation bitwise equal on 20/20 joints at every step. Vertices: all shapes <= 3.6e-7 m, except the head capsule at 5.0e-6 m: it is a sphere-shaped capsule (`height = 2 r`) and Unity sends half-height 5e-6 rather than 0. After Stop, alive backend objects are back to the edit-scene baseline (2 worlds, 21 bodies, 21 shapes, 20 joints, 2 materials). | run 2 blocks; `gateE_editor_commands.txt` |
| Target forwarding test | During run 2, between two steps: assigning an unchanged `targetRotation` sends nothing; a changed value is sent once, synchronously and verbatim; restoring sends once more. | `evidence/gateE_target_dedupe.txt` |

What RA2 sends and reads (run 2):
- Creation: CreateBody/CreateShape/AttachShape for 20 bones, 20 D6 `CreateJoint`s (type 6, flags 0x18 =
  DriveLimitsAreForces | LegacyLimitRanges), `Set6DofAxisMotorConfiguration` Slerp spring 5000 / damper 40 on the 19 bone
  joints (RA2 `SpringsValue` 5000, `DampingValue` 40) and spring 0 on the hips-to-world anchor joint.
- Per step: `Set6DofMotorTargetRotation` (+ `Joint.WakeUpActors`) only for joints whose target changed. RA2 writes every
  non-kinematic bone's target every FixedUpdate (`RagdollBoneProcessor.ApplyJointRotation`); Unity drops unchanged values.
  With this Animator (no Avatar, as in the station prefab) the targets stay within ~1e-8 of identity, so 0-19 joints
  receive a new target per step.
- Hips anchor: switched to kinematic at start (`Body.SetFlags`, `SetCollisionDetectionMode`), then 2 `SetKinematicTarget`
  per step (`MovePosition` + `MoveRotation`); plus one `SetPose`, `SetAngularVelocity`, `SetInertiaTensor` per step.
- Reads RA2 depends on (`linearVelocity`, `angularVelocity`, `position`, `worldCenterOfMass`, `IsSleeping`) return the
  backend's values exactly; no command reached an unhandled path (`UNHANDLED` count 0).
- Joint mapping read off the raw values: twist limits are sent in radians, same sign and order
  (`lowAngularXLimit -60, highAngularXLimit 10` -> AngularX -1.0472..0.1745); the swing pair is sent on the AngularYZ row
  (min = `angularYLimit`, max = `angularZLimit`, radians). Backend `localPose` Parent = the joint's own body,
  Child = `connectedBody`.

**H-RA2 verdict (for this configuration): confirmed.** RA2 runs unmodified; every API it used reached the backend as a
command and read back the backend's own state; no API lacked a command.

Not exercised by this gate (no claim either way):
- RA2 falling / get-up modes, hard matching (its `AddForce` / velocity-lerp paths), attachables (`FixedJoint` sword proxy),
  dismemberment, collision-ignore with attachables. These use the same command families already seen, except FixedJoint.
- Real animation: the Animator has no Avatar, so drive targets barely change. A rig with an Avatar would exercise
  per-step targets at full rate.
- `Shape.GetPose` / `Shape.GetLocalPose` read-back frames: still never called (Gate D open item stands).
- Joint limits are not enforced in this dummy anyway: all 19 bone joints are authored with angular X/Y/Z **Free**
  (linear Locked) and the hips anchor is Free on all six axes, so the authored limits are inactive under PhysX too.

Other observations:
- Asset-import worker processes also load the plugin and select RigidIPC (each `AssetImportWorker*.log` shows it), and
  they append to the same `Logs/RigidIPC_commands.log` (world serials restarting at #2, `workers=7`). The C# diagnostics
  read in-process state and are unaffected; the shared native log needs a process tag before it is used as evidence of
  ordering.
- Run 1 diagnostics flagged 94,158 items in total; all were the two artifacts or the capsule tips listed above.
