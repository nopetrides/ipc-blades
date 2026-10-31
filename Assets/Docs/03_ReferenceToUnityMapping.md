# Reference → Unity Mapping

> **Living document.** Current design intent, edit as the project evolves.
> **Last reviewed:** 2026-07-27 · **Status:** ACTIVE — validated physical mapping frozen for the
> Unity pilot.

How the recorded physical reference feeds the Unity comparison: what is *commanded*, what remains
an *emergent target to match*, and what is *measured*. The physical data and its provenance live in
the parent Thesis repo (`Workspace/Week3/Recording/Tracker_Exports/`); the supervisor's
instructions for what to reproduce are in `Workspace/Week3/Checkpoint_3_Notes.md` — "firstly
calculate contact point," focus on "motion of driving sword A, deflection or lack thereof of
contact sword B," and aim to "match the 'average' of our successful bind case and non-bind case."

## The mechanical picture we are reproducing

The validated counted passes preserve three different observable responses under maintained contact.
Flat (`n=7`) and primary Edge-no-bind (`n=3`) allow Blade A to continue its advance while Blade B
shows limited carry/rotation. Edge-bind (`n=5`) shows stronger coupled carry/rotation and greater
interruption/redirection of Blade A. PASS-11 is a terminal bind-to-slip transition retained
descriptively but excluded from the two primary Edge station groups.

This revises the earlier picture. Blade A's realised trajectory cannot be the fixed Unity input.
The controlled input is the same **starting state plus commanded drive/actuation**. Blade A and
Blade B trajectories are both outcomes. This is precisely the two-way mechanical coupling named in
the RQ.

## Input vs. emergent target

- **Controlled input (identical within a comparison):** starting blade poses, target drive
  direction/timing, actuator limits, Blade B holder boundary, and all physics settings.
- **Calibration evidence:** Blade A's pre-contact motion and a declared low-coupling/flat case,
  used to identify a bounded actuator profile. Force was not measured physically; do not describe
  the Unity command as a known-force replay.
- **Emergent targets to match:** Blade A's realised advance, interruption and deflection; Blade B's
  translation and rotation; contact persistence; and the contact-migration pattern.

### Core test logic

Apply one frozen **actuator command profile** through the model twice—once with blades in flat
presentation and once in edge—changing only the declared presentation geometry. A faithful model
allows contact to change both realised trajectories: the flat case should permit continued A
advance with migration and limited B displacement, while a bind-positive edge case should produce
temporary coupling, substantially greater B carry/rotation, and interruption/redirection of A.
Run the same command and starting state through every contact model.

Model 1 is intentionally unable to satisfy this because transform-follow prevents contact from
redirecting A. Model 2 asks whether standard rigidbody contact and shared physics-material rules can
produce the positive/negative divergence. Model 3 is tested only if Model 2 exposes a specific
predeclared failure.

## Completed Tracker reduction consumed by Unity

The parent repository contains sixteen counted-pass exports with six stable color-identified tracks:
tip and two physical quillon markers on each blade. `analyze_tracker_exports.mjs` is the calculation
source of truth and regenerates tidy frame data, derived frames, pass summaries, station summaries,
and the pooled presentation summary.

Noah, the primary researcher, validated every counted-pass classification and action window against
the two camera views on 2026-07-27. Every exported row is part of the maintained-contact action; the
window starts at action onset and ends when motion ends, not at touch/separation. Do not search for
or invent a contact-onset sub-window.

Missing markers remain missing. Endpoint-complete measures may use observed first/last rows;
continuous paths require complete trajectories; the projected contact-location target requires all
six marker series. These rules are encoded in the generated CSVs. PASS-16 retains endpoint measures
but not full-series tip paths/contact targets because of its interior tip occlusion.

The metric-eligible physical means and observed ranges are frozen as initial Unity-pilot targets,
not final acceptance thresholds or thesis findings. The strongest separation in the current
descriptive target table is:

| Physical group | A guard displacement mean | B guard displacement mean | B projected rotation mean | Guard coupling B/A mean |
|---|---:|---:|---:|---:|
| Flat (`n=7`) | 87.27 cm | 5.62 cm | 9.64° | 0.06 |
| Edge no bind (`n=3`) | 91.11 cm | 6.16 cm | 3.45° | 0.07 |
| Edge bind (`n=5`) | 83.87 cm | 15.19 cm | 35.10° | 0.18 |

Always read `metric_n`, minimum, maximum, and completeness flags from
`Workspace/Week3/Recording/Tracker_Exports/Tracker_Case_Summary.csv`; small and unequal groups make
the mean alone insufficient. The Edge-bind projected contact-location summary has only one
fully marker-complete supporting pass, so it remains a secondary diagnostic rather than a frozen
pass/fail target.

## Metrics

| Metric | Role | Definition sketch |
|---|---|---|
| Contact-point migration | Primary descriptor | displacement/rate along each blade during engagement; interpret with persistence and two-body response, not as a zero-migration bind rule |
| Blade A completion ratio | **Primary** | realised drive-axis advance divided by no-contact/commanded advance |
| Blade A deflection | **Primary** | angular and lateral deviation from its pre-contact/commanded direction |
| Blade B angular change | **Primary** | change in B chord/pose angle across the action window |
| Blade B translation/carry | Primary | guard midpoint and eligible tip displacement across the action window |
| Coupling relation | Derived | B response relative to A realised motion and/or command; use the physical guard-coupling target first |
| Contact duration / persistence | Secondary | Frames of maintained contact through the window |
| Failure mode | Qualitative | free slide / separation / jitter / tunnelling / over-coupling / carried motion |

All are compared against the physical reference and reported per model and per compliance setting.

## Remaining Unity dependencies (resolve before analysed runs)

- **Actuator identification.** Decide which pre-contact/low-coupling data calibrate the command and
  which bind/non-bind cases remain held out for evaluation. Do not tune the actuator or contact
  parameters on the same case later presented as validation.
- **Force boundary.** Physical force was not measured. Freeze a bounded actuator-strength sweep or
  one calibration-derived setting and report this limitation.
- **Configuration and run validity.** Freeze Unity/XRI versions, timestep, solver, colliders, masses,
  material/joint values, reset state, logger schema, settling/repeatability criteria, and invalid-run
  rules before analysed runs.

## Claim boundaries

- **Within the measured range:** the model's parameters are identified against the reference and
  fidelity is reported (edge−flat difference, comparison to the repeatability band).
- **Outside the measured range (interactive mode):** demonstration only — the same fixed parameters
  respond plausibly to novel input. No accuracy claim; there is no ground truth there.

## Changelog

- 2026-07-22 — Initial draft: input/emergent-target split, core test logic, metric table, open data
  dependencies, and claim boundaries.
- 2026-07-22 — Cited `Checkpoint_3_Notes.md` for the supervisor's reproduction targets (contact
  point, motion of A / deflection of B, match the average bind and non-bind case).
- 2026-07-24 — Corrected the controlled input from a fixed Blade A path to a finite commanded
  actuation. Added the six-marker Tracker plan, provisional flat/strong-edge readings, two-body
  metrics, calibration/validation split, and the single-camera limit on precise roll recovery.
- 2026-07-27 — Replaced the extraction plan and pilot readings with the completed, video-validated
  sixteen-pass reduction; recorded the frozen Unity-pilot targets, missing-data boundary, and
  remaining Unity gate.
