# P3T — Phase 3 Thesis workspace

All Phase-3 (Unity build-out) work for the thesis lives under this folder. Keeping it in one
place isolates project-owned work from the Unity **VR Template** and other third-party/sample
assets, which stay where their importers put them and are **not edited in place** (see the design
docs in `Assets/Docs/`).

> **Status (2026-08-21):** `Scenes/P3T_SampleScene.unity` is the deliverable and it is built. It
> carries **two parallel systems** — ten `Station_*` measurement roots and three free-play bays under
> `P3T_FreePlay_XRI` — across three arms: **PhysX** (baseline/control), **QuickDirty**, and
> **OurSolution**. The older status line here claimed the measurement scene, actuator, holder and
> logger were "not built yet"; that has been untrue for some time.
>
> **Before touching the scene, read
> `Workspace/Week7/FREEZE_SceneNaming_And_Hierarchy.md`.** It freezes the naming convention, gives
> the component lookup tables, and records two traps that have each cost a wrong conclusion:
> (1) the scene `.unity` YAML **cannot** be walked to answer hierarchy questions, because prefab
> instances serialise as `PrefabInstance` modification lists and a walker silently skips their
> children; and (2) at runtime the authored sword on each dummy is **kinematic** — PhysX solves a
> generated proxy named `<Sword>:Attachable Physics` under `<Dummy>-Ragdoll`. Measure the proxy,
> never the station dummy.
>
> **Discipline:** every build/method choice here must be recorded, validated against the research
> question, and **justified with a verified-corpus citation, documented primary-researcher
> decision, or Bill's supervisory steer** — not game-dev habit (see
> `Assets/Docs/README.md` → *Research discipline*). Artifact/demo work is not a finding; anything
> feeding analysed results must be justified + cited first.

## Layout

| Path | Purpose |
|---|---|
| `Scenes/` | P3T scenes. `P3T_SampleScene.unity` is a copy of the project `SampleScene` (the VR Template rig) used as the working sandbox so the original is untouched. Holds `P3T_References` (photo + video planes) and the live `SW_A1_Interactable` demo root, with `SW_A1_MeshOnly` nested as its visual specimen. |
| `References/Images/` | Still references imported as textures. `SONY_SWA1_FULL-Perspective_Corrected.JPG` is the perspective-corrected specimen photo, used as a grid-aligned modelling reference (see below). |
| `References/Video/` | **Local-only** raw capture clips for visual reference (see *Media policy*). |
| `Materials/` | Project material palette for the specimen model: `Mat_Steel`, `Mat_SteelDark`, `Mat_GripWrap`, `Mat_BlueTape`. (`References/Materials/` holds separate reference-visualisation materials for the photo/video planes.) |
| `Scripts/` | Pilot-station behaviour. `PilotRunController` (4-mode state machine + trapezoidal drive; exposes `DriveCount`/`DriveActive`), `BladeResistActor`, `SwordBladeSetup`, `ActiveArmRagdoll`, `InteractableReturnHome`, **`DriveTrailRecorder`** (motion curves — see below), `BladeContactStabilizer` (**inert**, kept for provenance). |
| `Prefabs/` | Specimen prefabs. **`SW_A1_MeshOnly`** = model geometry only — **no colliders, rigidbodies, or scripts**. **`SW_A1_MeshOnly_WithReference`** nests that mesh plus the aligned specimen photo for shaping. **`SW_A1_Interactable`** is the live interactive-demo wrapper: it contains the mesh-only visual and adds colliders, Rigidbody/XRI grab behavior, grip anchors, predicted visuals, and the return-home tether. |

## Reference media manifest

Every reference asset traces back to a specific physical recording, so provenance is never
ambiguous (ties to the pass log in
`Workspace/Week3/Checkpoint_3_Post_Recording_Review.md`). Source media lives in the parent Thesis
repo under `Workspace/Week3/Recording/Recording_Day/03_ARTIFACTS/`.

### Motion clips — the reference trio

Chain: `Import_Pixel/<source>` → trimmed to `Edited/<name>` (ffmpeg) → copied to `References/Video/`.

| Asset (`References/Video/`) | Source pass — outcome | Source file (`Import_Pixel/`) | Trim (s) | Frames |
|---|---|---|---|---|
| `PIXEL_08_Edge_Bind_1080p60.mp4` | PASS-08 · edge — **bind** (positive) | `PIXEL_08_EDGE-PXL_20260718_235818992.mp4` | 11.46–14.05 | 155 |
| `PIXEL_09_Flat_Control_1080p60.mp4` | PASS-09 · flat — non-bind (control) | `PIXEL_09_FLAT-PXL_20260719_000152171.mp4` | 6.37–8.96 | 155 |
| `PIXEL_03_Edge_NoBind_1080p60.mp4` | PASS-03 · edge — non-bind (negative) | `PIXEL_03_EDGE-PXL_20260718_235154867.mp4` | 4.87–6.96 | 125 |

All clips normalised identically for Unity: **1080p, CFR 60 fps, H.264 yuv420p (limited range),
audio stripped, first frame at t=0.** Recipe (per clip, output-side seek for a clean zero start):

```
ffmpeg -i <source> -ss <in> -t <dur> \
  -vf "scale=-2:1080:in_range=full:out_range=limited,fps=60,setpts=PTS-STARTPTS" \
  -color_range tv -c:v libx264 -bf 0 -pix_fmt yuv420p -crf 20 -an <out>
```

### Still references

| Asset (`References/Images/`) | Source | Represents |
|---|---|---|
| `SONY_SWA1_FULL-Perspective_Corrected.JPG` (2048×512) | `Import_Sony/` | SW-A1 specimen on grid paper — modelling/scale reference for `SW_A1_MeshOnly`. Dimensions from `Workspace/Week3/Recording/Recording_Day/01_SETUP/Specimen_Measurements.md` (SW-A1: total 131 cm, blade 101.6 cm, 45→10 mm, guard ~24.5 cm). |

## Specimen model (SW-A1)

The specimen geometry is stored as **`SW_A1_MeshOnly`** and nested under the live
**`SW_A1_Interactable`** root in `P3T_SampleScene`. The mesh contains five independent, editable
ProBuilder pieces, each with its own material, originally laid over the reference photo:

| Piece | Material | From measurements |
|---|---|---|
| `Blade` | `Mat_Steel` | 101.6 cm, tapered 45 → 10 mm |
| `Crossguard` | `Mat_SteelDark` | ~24.5 cm span; curved quillons hooked toward the blade, mirror-symmetric |
| `Grip` | `Mat_GripWrap` | part of the 29.4 cm hilt |
| `TapeBand_Base` | `Mat_BlueTape` | blue tape/marker at the blade base |
| `Pommel` | `Mat_SteelDark` | hilt end |

- **Scale:** 1 Unity unit = 1 m (real world). `SW_A1_MeshOnly` is scaled so total length = 1.31 units
  (131 cm, SW-A1 spec); the specimen photo was scaled and repositioned by the same factor to stay
  aligned. The model's transform carries a ~0.0253 uniform scale — **freeze/bake it** (ProBuilder →
  Freeze Transform) before building the physics rig so pieces sit at unit transform scale.
- **Origin:** the `SW_A1_MeshOnly` pivot is at the **top of the handle** (grip/guard junction), on the
  long axis; all pieces are centred on that axis. This is the intended rotation/drive point for the rig.
- Still a **blockout** — refine by hand (ProBuilder vertex editing) against the top-down ortho view.
  Editing one guard arm and re-mirroring is a manual re-run, not live symmetry.

## Media policy

Raw 4K capture is large (~120 MB/clip), so the **4K originals stay out of the MBC submodule** —
canonical copies live in the parent Thesis repo's `03_ARTIFACTS`. The trimmed 1080p60 clips above
are small (~2 MB each) but `References/Video/` is currently **gitignored** as a blanket rule, so on
a fresh clone any scene referencing a clip shows a missing clip until the media is re-copied. (The
trimmed clips are now small enough to commit if we prefer scenes to survive a clean clone — flip
the `.gitignore` if so.) Still images are small and committed normally.

## Third-party note

`P3T_SampleScene` is a copy of the VR Template sample scene (rig used as-is; disclosed in
`Assets/Docs/01_ComparisonOverview.md`). Project-owned work added on top goes under `Assets/P3T/`.

## VR interaction setup (interactive mode) — don't casually change these

The `SW_A1_Interactable` prefab is grabbable in VR. Getting it to hold smoothly and orient like a
sword took several **non-obvious, interacting settings**. If the sword starts jittering or holding
wrong again, check these first:

**Smooth hold (anti-jitter) — the fix that mattered:**
- **Project physics rate:** `ProjectSettings` Fixed Timestep = **0.0111 s (90 Hz)** (was 0.02 = 50 Hz).
  Velocity-tracked held objects jitter when physics steps slower than the headset refresh. Match the
  headset (set to `1/120` if running 120 Hz). Solver is **TGS, 25/15 iterations** (matches VR_FPS_Kit).
- **Rigidbody Interpolate = None.** Interpolation makes a held object *swim* (renders a frame behind
  the VR head). The kit's weapons confirm interpolate-off.
- **XRGrabInteractable → Predicted Visuals Transform = `SW_A1_MeshOnly`.** Unity's sanctioned VR fix:
  the visual mesh is predicted per-frame (smooth) while the rigidbody/colliders run raw physics.
- These are **interactive-mode only.** Measurement mode uses its own frozen timestep and will *not*
  use predicted visuals (it needs physics-accurate poses).

**Grab config:** VelocityTracking, SelectMode = Multiple (two-handed), UseDynamicAttach = off, fixed
attach points `Grip_Primary` (near guard) + `Grip_Secondary` (near pommel). Rigidbody mass = 1.574 kg
(real SW-A1), gravity off.

**Grip orientation (HEMA handshake):** the grip anchors are rotated so the attach *up* = the blade
width/quillon axis → **quillons vertical, edges up/down**, plus ~**35° tip-up** pitch. Adjust the
anchor rotations to tune; positions along the handle set the two hand spacings (match recorded
references).

**Safety tether:** `InteractableReturnHome` on the prefab records the spawn pose as "home", gently
drifts the sword back when released, and hard-resets (kills momentum + teleports home) if it ends up
>3 m from **both** home and the player. Player is resolved via `Camera.main` so it survives rig swaps.

## Motion curves — physical reference vs simulated run

Each station carries both halves of the curve comparison, in one space.

**`RefCurves_<Suffix>`** — the *physical* tip/guard curves from the Tracker export, drawn on that station's
video plane at **Z = 2.66** as four LineRenderers (`RefCurve_{TipA,TipB,GuardA,GuardB}_<Suffix>`), coloured to
match the physical marker colours. Generated by `Workspace/Week4/StationTools/trk_curves.py`. The Tracker metre
frame → plane mapping is a **fitted** 2D similarity (frame-0 residual rms 0.11–0.16 mm across all three passes),
not an assumed one. `ViewCam_*` is 42.99° vertical = **70.00° horizontal**, which is exactly what framing the
3.184 × 1.791 plane from z = 0.4264 requires — so the camera used to project is the one that registered the refs.

**`DriveTrailRecorder`** — the *simulated* curves, drawn live. Sits on each `PilotRig_*` next to the controller
and **polls** it (never drives it). Traces the same four points the Tracker export carries; each is locked to its
sword as a fixed local offset calibrated from that station's `SwordRef_*` leaves at the first DRIVE, so a trace
begins where its reference curve begins. Every DRIVE press spawns `DriveTrail_NNN_<Suffix>` containing, per point:

- `_3D` — the true world path. This is the physical result.
- `_Plane` — that path projected through `ViewCam_*` onto Z = 2.66. **Only this one is dimensionally comparable**
  with the reference curves, which are 2D primary-camera projections.

Runtime LineRenderers do not survive Play mode, so each completed run is also written to
`Workspace/Week4/StationTools/Curves/DriveTrails/`. That CSV is the record; the lines are the demonstration.
Set `draw3D = false` to leave exactly four trail lines against four reference lines when runs accumulate.

`PilotRunController.DriveCount` / `.DriveActive` exist for this: the controller **stays in `Driving` after the
sweep arrives**, so mode alone cannot distinguish a second DRIVE press from the first.

Curves and trails are **apparatus/demonstration, not findings**. Measured data remain blade rigidbody poses (D5),
and the analysed comparison stays gated behind the DR4 freeze. Known limits (blade A's magnitudes inflated
~20–30 % by the planar calibration; PASS-08 tip truncation; static overlay drift against the playing video) are
recorded in `Workspace/Week4/Pilot_Build_Log.md` Session 8 — read them before quoting any number off these curves.

**`SwordRef_A/B_Flat_tmp`** (cyan `Mat_SwordRef_Tmp`) are *staged proposals, not live apparatus*. The flat
station's live `SwordRef`s do not match a clean P3P solve — their six points project onto their pixel-mapped
surface points at mean 26.0 mm / max 51.5 mm, against 0.6–20.3 mm on the edge stations. The `_tmp` copies sit at
the clean solve (surface points on the true plane size 3.1840 × 1.7910 at Z = 2.70) and reach **mean 2.3 / max
5.6 mm**. Nothing was moved; adopting them changes validated apparatus and is Noah's call. Delete the `_tmp`
objects once decided.

## Next steps

Follow `Assets/Docs/04_MeasurementAndFindings.md`; do not restart Tracker extraction.

1. Create a separate scripted measurement scene/test cell. Preserve `P3T_SampleScene` and
   `SW_A1_Interactable` as interactive demo assets.
2. Bake the SW-A1 scale, confirm measured proportions, add documented collider/mass properties,
   and prepare the same physical rig representation for Blade B.
3. Implement one scripted target command, a finite dynamic Blade-A actuator, and one compliant
   Blade-B holder shared by all models. Both realised blade paths are outputs.
4. Before contact tuning, implement deterministic reset, configuration manifest, fixed-step CSV
   logger, run ID/hash, and invalid-run warnings.
5. Implement M1 and M2 behind the same runner. Pilot Flat, Edge-no-bind, and Edge-bind with declared
   calibration cases; derive settling/repeatability rules and freeze the remaining research gate.
6. Only after that freeze, run the analysed comparison. Consider M3 once, and only if M2 exposes a
   specific recorded gap against both positive and negative controls.

Interactive XR may later feed the same actuator for demonstration, but it never contributes
analysed thesis data.
