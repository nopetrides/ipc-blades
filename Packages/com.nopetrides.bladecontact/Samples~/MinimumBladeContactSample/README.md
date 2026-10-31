# Minimum Blade Contact Sample

Reserved for the standalone two-dynamic-sword sample used to demonstrate L1 angular contact. It must not reproduce thesis stations or depend on thesis classes.

---

## TODO — this folder is still empty, and that is the package's largest gap

**Status 2026-08-19: placeholder only.** Anyone who downloads the package gets scripts and tests and no way to
run anything. Written down here so it is picked up when the package is wrapped up.

### Why this matters more than it looks

A missing `BladeProfileAsset` is a **silent** failure, not an error. With a null profile the shell builds
empty, `TryClosestFeaturePair` finds no valid witness, `ClassificationValid` never becomes true, and the
solver applies zero force — with nothing written to the console. The station reads as ordinary PhysX *by
design* rather than *by omission*.

During development that same mistake was made three separate times: on the three station B swords, on the
free-play `XRI_B3`, and in the `SW_B3_OurSolution` prefab itself. Every one of them was found only by
inspecting the profile field directly. A first-time user authoring a profile from nothing will hit it, and
they will have no working example to diff against.

### What the sample must contain

- **A blade mesh.** Simple, flat-shaded, and clearly showing the two long cutting edges. It does not need
  to be a real specimen; it needs to make the cross-section legible.
- **A `BladeProfileAsset` that matches that mesh**, with the two long edge polylines designated
  `SharpEdge` and everything else left non-designated. This is the reference artefact — the thing a user
  copies and edits.
- **Two dynamic sword prefabs**, each with `Rigidbody`, one convex blade `MeshCollider`, and a
  `BladeShell` wired to the profile and that collider.
- **A scene** that brings the two blades into maintained contact under some simple drive, with either
  path selectable: `BladeContactManager` for owned contact, or `RqBladeTangentialSolver` for the
  augmenting one.
- **A readout** naming the classified scenario, the raw witness feature pair, and the applied tangential
  force — so a user can see the classification working rather than infer it.
- **A profile authoring path.** Either ship the editor tool or document the procedure. Right now the tool
  lives in the author's consuming project and does not travel with the package.

### Constraints, unchanged

No thesis stations, no physical-reference registration, no MBC or Ragdoll Animator dependency. If the
sample needs an attachment system to demonstrate `BladeShell.BindPhysicsProxy`, fake it with a kinematic
parent rather than pulling in a third-party package.

### Also outstanding at release

`RqBladeContact` and `RqBladeTangentialSolver` are in the global namespace under an inherited `Rq` prefix.
Rename into `BladeContact` before publishing; the rename has to patch consuming scenes in the same step,
since Unity resolves these by script GUID and the file name must follow the class.
