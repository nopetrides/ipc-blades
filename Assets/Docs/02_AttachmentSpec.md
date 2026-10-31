# Contact Model — Attachment Spec

> **Living document.** Current design intent, edit as the project evolves. Parameter values below
> are **starting points to tune**, not frozen constants.
> **Last reviewed:** 2026-07-24 · **Status:** DRAFT — pre-implementation design.

The contact model is three physics "attachments" (joints). Blade A is the driver; Blade B is the
held/contacted blade. Coordinates and drivers differ by [run mode](01_ComparisonOverview.md).

```
 [driver grip anchor] --A1--> (Blade A)  ...contact...  (Blade B) <--A2-- [holder anchor]
                                        \___________A3 (the bind)___________/
```

## A1 — Blade A ↔ driver grip

Represents the wrist/arm compliance between the hand and the blade. The blade is **not** pinned to
a path; the *grip anchor* is driven, and the blade responds through this joint plus contact.

- **Component:** `ConfigurableJoint` on Blade A, `connectedBody` = driver grip anchor rigidbody.
- **Linear motion:** `Locked` (blade does not translate relative to grip). Optionally `Limited`
  with a stiff `linearLimitSpring` if a little give is wanted later.
- **Rotation:** `rotationDriveMode = Slerp`; `slerpDrive` toward a rest orientation.
- **Anchor drive (mode-dependent):**
  - *Measurement mode:* a **dynamic actuator body** is driven toward a target motion by capped
    force and torque (or an equivalent capped velocity/acceleration servo). The target command is
    repeatable, but contact may slow, stop, or redirect the realised Blade A motion.
  - *Interactive mode:* the tracked controller supplies the target pose to the same actuator
    abstraction. The blade must not be teleported to the controller transform if contact response
    is meant to remain physically active.

The recorded Blade A trajectory is therefore a **physical output/reference**, not a pose track that
the simulated blade is forced to complete. The actuator command is calibrated from the pre-contact
approach and a declared low-coupling/flat case, then frozen or swept over a predeclared bounded
range. Physical force was not measured, so the thesis must call this a commanded actuation profile,
not a replay of known Newtons.

| Parameter | Start | Notes |
|---|---|---|
| `slerpDrive.positionSpring` | 2000 | Grip stiffness; higher = blade tracks hand more rigidly |
| `slerpDrive.positionDamper` | 20 | Suppresses oscillation |
| `slerpDrive.maximumForce` | **Pilot variable; must be finite** | If effectively infinite, the actuator can erase bind-induced interruption |
| linear actuator force/acceleration cap | **Pilot variable** | Shared across presentations/models after calibration |
| `slerpDrive.useAcceleration` | **Record explicitly** | Changes drive semantics; it is not a determinism guarantee |

## A2 — Blade B ↔ holder anchor

Bill's "resistant actor" — his Checkpoint-3 phrasing (`Workspace/Week3/Checkpoint_3_Notes.md`):
Blade B "needs to be held by a resistant actor with enough rigidity to overcome gravity and hold
the sword in place, but enough flexibility of the rig to allow for the motion of A to cause the
rotation." So: rigid enough to hold Blade B against gravity, compliant enough that Blade A's drive
can rotate it. The anchor is **static** (the neutral hold); the compliance lives in this joint.

- **Component:** `ConfigurableJoint` on Blade B, `connectedBody` = holder anchor rigidbody
  (kinematic, fixed pose).
- **Linear motion:** `Locked`.
- **Rotation:** `Slerp` drive toward Blade B's neutral orientation. This spring is the main knob
  for "how hard is B held" and is the primary target of the **compliance sweep** (low/med/high).

| Parameter | Start | Notes |
|---|---|---|
| `slerpDrive.positionSpring` | 400 | **Compliance sweep variable.** Lower = B yields more |
| `slerpDrive.positionDamper` | 15 | |
| `angular*Limit` (X/Y/Z) | wide | Allow the observed carry range; not a hard stop unless needed |
| `useAcceleration` | true | Reproducibility |

## A3 — Blade A ↔ Blade B contact (the bind)

The crux, and the only attachment that differs across the three models. In the provisional strong
physical bind, the contact temporarily coupled the blades: Blade B was carried through a large
translation/rotation while Blade A failed to complete its expected advance and was redirected.
The shared contact response must therefore be allowed to act back on Blade A, not merely push B
while A follows an inviolable path. See
[03_ReferenceToUnityMapping.md](03_ReferenceToUnityMapping.md).

The first Tracker pass also suggests the visible contact location changed during the bind. Model 3's
“pin” is therefore a candidate abstraction with finite compliance/sliding and a release rule, not a
claim that the physical contact point was perfectly fixed.

- **Model 1 (transform-follow):** no contact response. No A3. Blades interpenetrate or shove
  trivially; never binds.
- **Model 2 (rigidbody + friction):** **no explicit joint.** A3 is emergent from collider contact
  + `PhysicsMaterial` friction. Whether it sustains two-way coupling, and with what migration, is
  what we are testing. Keep edge and flat material rules identical; only the collider
  geometry/orientation differs.
- **Model 3 (augmented, gated):** a **runtime-created, threshold-released joint** at the contact
  point. Pattern (see grounding below): on qualifying contact, create a joint whose anchor is the
  contact point connecting the two blade rigidbodies; release when load exceeds a threshold
  (`breakForce`/`breakTorque`, or manual monitoring) — this is stick→slip.

| Parameter (Model 3) | Start | Notes |
|---|---|---|
| Constraint type | `ConfigurableJoint` (anchored at contact) | `FixedJoint` is the rigid limit case |
| Position drive / lock | limit relative contact motion | Allow finite compliance/migration rather than a perfect weld |
| `breakForce` / `breakTorque` | tuned per sweep | The stick→slip threshold; predeclared, not fit-to-clip |
| Re-arm rule | on separation + re-contact | Models bind→slip→bind sequences seen in the footage |

## Stability checklist (learned from Ragdoll Animator 2)

- Drive rotation via **`slerpDrive` only** (not separate angular X / YZ drives) for a single muscle.
- Use **`connectedMassScale` / `massScale`** to stabilise joints when blade and anchor masses
  differ — expect to need this.
- Expose and freeze: `solverIterations`, `solverVelocityIterations`, `enablePreprocessing`,
  `projectionMode`, `maxAngularVelocity`, rigidbody `interpolation`, `collisionDetectionMode`.
- **Settling/warm-up:** do not assume two `FixedUpdate` ticks. Define an empirical settling rule
  during the pilot (for example, pose/velocity remaining below declared thresholds for a declared
  interval), then freeze it before analysed runs.
- Unity joints **do not support runtime scaling** — set blade scale before play.
- Freeze `Time.fixedDeltaTime` for all measurement runs.

### Concrete baseline (from RA2 source study, 2026-07-27)

RA2 confirmed to be a **management layer over stock `Rigidbody` + `ConfigurableJoint`** (no custom solver);
pose held via `joint.targetRotation` + `slerpDrive`. Values it uses, as our starting baseline:

- Per bone: `ConfigurableJoint`, `rotationDriveMode = Slerp`, **`slerpDrive` only** — `positionSpring ≈ 1500`,
  `positionDamper ≈ 40`, `maximumForce` unbounded; scale spring by `musclesPower²`. Set `joint.targetRotation`
  **every FixedUpdate** from the target's *local* rotation mapped into joint-axis space.
- Motion: linear **Locked**; angular **Limited** (anatomical limits), limit springs **0** (hard limits);
  `autoConfigureConnectedAnchor = false`.
- Rigidbody: `solverIterations = 12`; velocity iters = project default (→ ~6 only if jittery);
  `enablePreprocessing = false`; `projectionMode = None`; `joint.enableCollision = false`; `angularDamping ≈ 0.2`;
  `linearDamping = 0`; **`maxAngularVelocity ≈ 50`** (up from Unity's 7).
- Masses: ~50 reference distributed anatomically (chain ×0.5); `connectedMassScale ≈ 0.5` standing, `massScale = 1`;
  Unity auto-inertia (box tensor only for long/unstable chains); COM untouched.
- Ragdoll-wide: `Physics.IgnoreCollision` between adjacent bones (+ bounds-overlap auto-ignore). Ordering: capture
  target pose in **LateUpdate** → write `targetRotation` in **FixedUpdate** → copy sim back in **LateUpdate**;
  physical bones in a **detached hierarchy**. Anchor/root: joint `Free`, `freezeRotation = true` standing (kills
  anchor jitter), or kinematic `MovePosition/MoveRotation`.
- **Held blade:** attach the sword rigidbody to the hand-bone rigidbody with a **`FixedJoint`** (tune
  `connectedMassScale`/`massScale`). So the A1 "grip" compliance is the **arm chain** with the blade rigidly
  gripped — not a separate blade↔anchor spring.

**Where our blades diverge from RA2 (declared/swept, not inherited blindly):**
- `collisionDetectionMode`: RA2 = **Discrete** (chunky, slow bones); our thin, fast blades in contact will likely
  need **Continuous** to avoid tunnelling — swept in `04` §6.
- Solver: RA2 = 12 iters (project default); the VR *interactable* = TGS 25/15 (demo path). The **measurement**
  solver type + iteration counts are declared/swept, inherited from neither blindly.
- `interpolation`: RA2 uses Interpolate (visual only); harmless for the ragdoll bones since the logger reads the
  FixedUpdate physics pose.

## Grounding in existing implementations

The above is a hand-built subset of the pattern Ragdoll Animator 2 uses (studied, not imported):
its "muscle" is exactly a `ConfigurableJoint.slerpDrive` toward a `targetRotation` with linear
motion locked; its anchor supports both a kinematic-teleport mode and a force-toward-target mode
(our two A1 drive modes); and its `WearAttachable` creates a runtime `Rigidbody`+joint between a
prop and a body with mass-scale tuning and collision-ignore management — the template for the
Model 3 bind, except ours is transient and threshold-released. We build our own minimal version so
the contact model (the object of study) is fully describable in the thesis Methods.

## Changelog

- 2026-07-22 — Initial draft: three attachments, starting parameters, per-model A3 behaviour,
  stability checklist, and grounding.
- 2026-07-22 — Attributed the A2 "resistant actor" requirement to its source in
  `Checkpoint_3_Notes.md`.
- 2026-07-24 — Replaced kinematic pose replay with a finite, force/torque-limited actuator target so
  contact can interrupt and redirect Blade A. Removed the unsupported two-tick warm-up rule and
  marked settling as an empirical pre-run criterion.
- 2026-07-27 — Folded a concrete active-ragdoll config baseline from an RA2 source study into the
  stability checklist (verdict: RA2 = management layer over stock `Rigidbody` + `ConfigurableJoint`);
  flagged blade-specific divergences (Continuous collision, measurement solver, interpolation) as
  declared/swept; noted the held-blade `FixedJoint`-to-hand mapping.
