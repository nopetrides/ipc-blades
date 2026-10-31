# Compound-collider sword prefabs

`V2_SW_A1_Compound` and `V2_SW_B1_Compound` are the collider-enabled swords. They are **generated**, not
hand-authored: run `Tools/P3T/Build compound-collider sword prefabs` to rebuild them from the mesh-only
prefabs after the specimen geometry changes. Hand-placed colliders on a measured specimen are an
undeclared approximation nobody can re-derive.

Each prefab is one physics object:

- a single `Rigidbody` on the root;
- the existing mesh-only prefab nested unmodified as `Visual`, still the source of truth for what the
  specimen looks like;
- a compound of convex colliders under `Colliders`;
- a `SwordCompoundPhysicsDeclaration` stating the mass and contact properties.

## What collides

| Section | Collider | Count | Contact bearing |
|---|---|---|---|
| Blade | convex `MeshCollider` | 1 | **yes** — carries `BladeShell` |
| Guard | convex `MeshCollider` (central span + one per quillon hook) | 3 | no |
| Grip | `BoxCollider` | 1 | no |
| Pommel | `BoxCollider` | 1 | no |
| Markers | `SphereCollider`, r = 20 mm | 3 | no |

**9 enabled physical colliders.** Every one is convex, which is a hard PhysX requirement for a
non-kinematic Rigidbody — not an XRI restriction, but it applies here regardless because the sword is
dynamic whenever it is not held.

Physical collision is deliberately separate from contact/damage. Only the blade carries `BladeShell`, so
guard, grip, pommel and markers block and transmit force but can never be read as a cutting or binding
contact.

## Why the guard is three convex pieces, not boxes and not one hull

The guard is a wedge — 15 mm thick at the centre tapering to 2 mm at the tips — whose quillons hook
forward, so its **footprint is concave while its cross-section is convex**. Measured against the real
solid:

| Approach | pieces | phantom volume |
|---|---|---|
| one convex hull over the whole guard | 1 | **113.8%** |
| axis-aligned boxes | 5 | 8.6% in footprint **plus ~61% in thickness** |
| **convex mesh pieces** (central + 2 hooks) | **3** | **2.6%** |
| convex mesh pieces (central + shank/tip ×2) | 5 | 0.7% |

A single hull bridges tip to tip and fills the entire bay between the quillons — that bay is the 113.8%.
Boxes are worse than their footprint number suggests, because a box has constant height while the guard
tapers, so a single box over the central span is ~61% too thick on its own and avoiding that needs enough
bands to visibly stair-step.

Cutting the footprint at the two hook roots avoids both. All the non-convexity is in the footprint, so
the pieces are near-convex, and because the top and bottom surfaces within any span are single planes,
the taper — including the central ridge — is reproduced **exactly** in thickness.

The residual 2.6% is one thing: the fore edge bows 0.31 mm toward the aft at the centre, so the central
span is very slightly concave.

## Markers are physical

The three tracking markers **carry sphere colliders**. They are real objects on the specimen and can
strike another sword there, so they collide here too.

This has a consequence that the declared inertia tensor exists to absorb. Unity distributes a body's mass
over its colliders **by volume**, and a 40 mm marker sphere is large by volume and negligible by mass —
the real markers weigh 8 g of 1582 g. Letting them into the auto tensor moved it by **+218% roll**. The
tensor is therefore declared, never computed.

## Mass properties

Declared per specimen, matching the values serialized on every station instance (verified identical
across Station_FlatFimpossible, Station_FlatFimpossible_Rolled and Station_EdgeBind):

| | mass | point of balance from guard | inertia tensor |
|---|---|---|---|
| A1 | 1.582 kg | 93.29 mm | (0.002991, 0.138475, 0.135493) |
| B1 | 1.630 kg | 91.44 mm | (0.003019, 0.148106, 0.145096) |

Contact material is `BladeGrip.physicMaterial` (μs 0.74 / μd 0.57) on every collider. Without it a
collider silently runs PhysX's built-in 0.6 / 0.6.

## Solver settings are stock, deliberately

Solver iterations **6 / 1** and contact offset **0.01 m** — Unity's stock values.

The station path runs an elevated 25 / 15 with a 0.005 m offset. Those were a way of suppressing
rotational pop-through by brute force: raising the solver and tick rate until ordinary overlap detection
happened to catch angular motion PhysX cannot sweep. That is the problem the custom blade-contact solver
exists to handle properly, and solver iterations and tick rate are among the most expensive things to
raise on a standalone Quest.

**Neither value serializes in Unity 6.** `Rigidbody` has no `m_SolverIterations` and `MeshCollider` has
no `m_ContactOffset` in its serialized property set; both fall back to the project settings. That is why
`SwordCompoundPhysicsDeclaration` applies them at runtime — without it the prefab silently runs whatever
the project happens to be set to, regardless of what the inspector showed when it was built.

Mass, centre of mass and inertia tensor **do** serialize, and are re-applied by the same component so one
place states what the sword is and can verify it.

## Nested visual physics

The mesh-only prefabs are not authored alike. A1's crossguard is a live ProBuilder mesh; B1's was a baked
asset until its geometry was replaced from A1. B1 also ships its own blade/crossguard colliders and a
`SwordBladeSetup`.

In the compound prefab those nested colliders are **disabled**, and `SwordBladeSetup` is **removed**.
Removed rather than disabled because Unity calls `Awake` on a component even when it is disabled, so long
as its GameObject is active — merely disabling it still let it add a second `Rigidbody` at runtime. The
source prefabs are untouched; both are recorded as prefab overrides.

## Validation

- **Scene:** `Assets/P3T/Scenes/P3T_CompoundSwordValidation.unity` — open it and press Play. Each sword
  drops one section onto an ordinary static PhysX pedestal; a negative control with no section over its
  spot must fall to the floor; two probe spheres show the guard blocks its real material but not its
  concave interior. An on-screen readout reports every check including the live mass properties.
- **Automated:** `P3T.Tests.PlayMode`, 27 tests. They run in play mode because the compound depends on
  nested physics staying disabled *after* `Awake`; an edit-mode check would inspect the serialized prefab
  and pass while the runtime object was wrong.
- **Scene view:** select a sword root to see the real collider outlines, colour-coded by section, drawn
  by `CompoundColliderOutline`. Every outline is an actual enabled collider — nothing is a proxy shape.

## Known gaps

- `BladeShell` on the blade has **no `BladeProfileAsset` assigned**. No such asset exists in the project
  yet, so blade contact is inert.
- The blade's own cross-section carries a fuller — the centre dips ~0.4 mm below the shoulders — so it is
  mildly concave too, and its single convex hull fills that in. Unmeasured.
- A1's physics is declared only as scene components on the station instances, while B1's lives on its
  prefab. The values above are restated in the builder because of that asymmetry; adding a
  `SwordBladeSetup` to A1's prefab would collide with the one the scene already adds, which is
  `[DisallowMultipleComponent]`.
- The three `Station_H4/H5/H8` swords are unlinked plain objects and inherit none of this.

## Footnote — grasping hand

Hand-to-hilt collision can later be suppressed selectively for the hand currently gripping the sword, via
`Physics.IgnoreCollision` against the grip, pommel and guard colliders on grab, or a layer pair. The
colliders are split by section so that is possible without rebuilding. Out of scope here.
