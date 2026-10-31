# IPC Blades

Fork of the MBC thesis project (`nopetrides/MBC` @ `39432f7`) for replacing PhysX with a custom
Rigid-IPC-style backend registered through Unity's `IUnityPhysicsBackendSystem` native plugin API.
BladeContact is embedded under `Packages/com.nopetrides.bladecontact` (from `nopetrides/blade-contact` @ `18b6f9f`).
The original MBC README follows unchanged.

---

# MBC — Maintained Blade Contact (Unity)

Phase-2 Unity artifact for the MS thesis *"To what extent do representative real-time VR contact
models reproduce the observable mechanical coupling of a controlled, maintained edge-on-edge blade
bind?"*

This is a controlled comparison of physics contact models against a recorded physical reference —
**not** a game and **not** a VR user study. It is delivered as a VR-capable project so the same
contact model can also be wielded in a headset for demonstration.

- **Unity version:** 6000.4.4f1
- **Base:** Unity VR Template (XR Interaction Toolkit + Meta OpenXR / Quest support)
- **Origin repo:** https://github.com/nopetrides/MBC
- **Parent:** included as a git submodule of the private **Thesis** repository, which holds the
  physical reference, analysis, and manuscript.

## Two run modes

1. **Measurement mode (scripted replay)** — the *only* source of analysed data. Blade A's grip
   replays a recorded path at a frozen timestep; the contact model's behaviour is measured.
2. **Interactive VR mode (live controller)** — demonstration and parameter sandbox **only, never a
   data source** (this protects the thesis's mechanical-only, non-human-subjects determination).

## Layout

- `Assets/Docs/` — **living design & spec docs. Start here:**
  [Assets/Docs/README.md](Assets/Docs/README.md).
- `Assets/MBC/` — project-owned code, scenes, prefabs (new work goes here).
- Unity VR Template / XR assets — used as the rig; not edited in place.

## Status

Pre-implementation. The contact model, the three comparison models, and the reference-to-Unity
mapping are specified in `Assets/Docs/` and are expected to change as the project is built — treat
those docs as living, and update them alongside the code.

## Third-party disclosure

Everything used is disclosed in
[Assets/Docs/01_ComparisonOverview.md](Assets/Docs/01_ComparisonOverview.md#third-party-provenance).
The contact model — the object of study — is hand-built from Unity primitives so it can be fully
described and defended; third-party physical-animation packages were studied for their approach but
not imported as the model.
