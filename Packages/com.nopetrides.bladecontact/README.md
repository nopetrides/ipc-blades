# BladeContact

Reusable Unity package for feature-aware blade-to-blade contact between dynamic `Rigidbody` swords.

## Boundary

The package owns registered BladeShell pairing, authored feature classification, continuous blade-shell contact, ordinary blade slip, and bind eligibility. It returns contact impulses/velocity constraints to dynamic swords.

Unity/PhysX remains responsible for arm/servo dynamics, mass and inertia, gravity, environment contact, and all non-registered-sword interaction.

This package has no thesis-station, physical-reference, or MBC dependencies.

## Status

**Functional, pre-release.** Two contact paths exist and both run:

- **Owned contact** — `BladeContactManager` takes ownership of one registered blade pair, suppresses PhysX collision for exactly those two colliders, and resolves non-penetration itself from a swept time of impact. Normal response only.
- **Augmenting contact** — `RqBladeTangentialSolver` leaves PhysX alone and adds a tangential term per contact, chosen from the authored feature the contact actually landed on. This is the path currently exercised in the author's thesis scene.

Supporting: `BladeShell` + `BladeProfileAsset` (authored cross-section geometry), `BladeShellSweep` (BVH broad phase, conservative advancement, closest authored feature pair), `BladeTangentialPolicy` (semantic scenario to parameters), and `BladeShell.BindPhysicsProxy` + `BladeCollisionRelay` for the case where an attachment system re-hosts a blade's physics on a rigid copy. Nine editor test fixtures under `Tests/`.

## Known gaps before this is publishable

**There is nothing runnable in the box.** A downloader gets scripts and tests and no way to see the thing work:

- `Samples~/MinimumBladeContactSample/` is a reserved placeholder containing only a README. No scene, no prefab, no sword.
- **No example `BladeProfileAsset` ships.** This is the sharpest gap, because a null profile is a *silent* failure: the shell builds empty, no witness is found, classification never validates, and the solver applies nothing without logging an error. That exact mistake was hit three separate times during development. A user authoring their first profile from nothing will hit it too.
- No blade mesh, so there is nothing for a profile to correspond to.
- No profile authoring tool. The one used by the author lives in the consuming project, not here.
- `RqBladeContact` and `RqBladeTangentialSolver` are still in the **global namespace** under an inherited `Rq` prefix, unlike every other type here. They need renaming into `BladeContact` before release, in a step that also patches consuming scenes.
- No API documentation beyond source comments, and no note on how to author a profile for a given blade.

See `Samples~/MinimumBladeContactSample/README.md` for what the sample has to contain.
