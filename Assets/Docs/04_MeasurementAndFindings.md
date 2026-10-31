# Measurement & Findings Plan

> **Living document.** Measurement-first build and analysis plan for the Unity comparison.
> **Last reviewed:** 2026-07-27 · **Status:** ACTIVE — physical reference frozen for pilot;
> Unity measurement scaffold is next.
> **Authority:** [DR4 verification and research gate](../../../../Workspace/Week3/DR4_VerificationAndDecision.md),
> [Checkpoint 3 review](../../../../Workspace/Week3/Checkpoint_3_Post_Recording_Review.md), and
> [Bill's notes](../../../../Workspace/Week3/Checkpoint_3_Notes.md).

Docs [01](01_ComparisonOverview.md)–[03](03_ReferenceToUnityMapping.md) define the RQ, models,
mechanical boundary, and physical-to-Unity mapping. This document defines how the artifact produces
auditable findings. “Stations” remain a useful scene layout, but they are test cells for one
controlled experiment—not separate game experiences.

## 1. Correction that controls the design

Blade A's recorded path is **not** imposed on the virtual blade. The physical strong bind appears
to have interrupted and redirected Blade A's expected advance while carrying and rotating Blade B.
Imposing Blade A's recorded end pose would hide whether the contact model can generate that
two-way response.

The shared experimental input is therefore:

- the same initial blade poses and presentation definition;
- the same target drive direction and timing;
- the same finite actuator force/torque or velocity/acceleration limits;
- the same Blade B holder boundary; and
- the same frozen simulation and logging settings.

The realised trajectories of **both Blade A and Blade B are outputs**.

## 2. Physical reference — completed and validated

The physical-reference reduction is complete for all sixteen counted passes. Noah, the primary
researcher, confirmed every preliminary outcome and every action window against the video on
2026-07-27. The analysis source and generated tables live in
`Workspace/Week3/Recording/Tracker_Exports/`; do not duplicate calculations inside Unity.

Primary groups are Flat (`n=7`), Edge no bind (`n=3`), and Edge bind (`n=5`). PASS-11 is retained as
one terminal bind-to-slip transition in pooled/descriptive reporting but excluded from both primary
Edge station targets. All exported rows are part of maintained blade contact and run from action
start through end of motion.

Missing observations remain blank. Endpoint and continuous-trajectory eligibility are separate,
and the analysis script enforces those rules per metric. The Edge-bind projected contact-location
summary has only one fully marker-complete supporting pass; use it as a secondary diagnostic, not a
frozen acceptance target.

## 3. Frozen physical targets for the Unity pilot

The metric-eligible means and observed ranges in `Tracker_Case_Summary.csv` are frozen as initial
pilot targets. They are descriptive anchors for validating the Unity measurement system, not final
acceptance thresholds or thesis findings.

| Physical group | A guard displacement mean | B guard displacement mean | B projected rotation mean | Guard coupling B/A mean |
|---|---:|---:|---:|---:|
| Flat (`n=7`) | 87.27 cm | 5.62 cm | 9.64° | 0.06 |
| Edge no bind (`n=3`) | 91.11 cm | 6.16 cm | 3.45° | 0.07 |
| Edge bind (`n=5`) | 83.87 cm | 15.19 cm | 35.10° | 0.18 |

The pilot must first reproduce the measurement semantics and preserve the qualitative ordering:
bind cases should interrupt/redirect A more and carry/rotate B more than the two negative controls.
Every comparison must also report metric-specific `n`, observed range, completeness, and failure
mode. Do not tune a model only to the displayed means.

## 4. Shared actuator, not shared realised path

The scripted measurement driver is a force/torque-limited target actuator:

```text
target command
      ↓
finite actuator / compliant grip boundary
      ↓
dynamic Blade A ↔ contact model ↔ compliant Blade B holder
      ↓
realised A trace + realised B trace + contact trace
```

Physical force was not measured. The actuator must therefore be identified transparently:

- calibrate target timing/direction and bounded actuator strength using pre-contact motion and a
  declared low-coupling/flat case;
- freeze that setting or a small predeclared sensitivity sweep;
- keep bind-positive and edge-non-bind cases held out from actuator/contact tuning where possible;
- never call the command a replay of measured force.

Model 1 remains the transform-follow floor and is expected to ignore feedback. Models 2–3 must use a
dynamic driver that allows contact to interrupt and redirect Blade A.

## 5. Stations as controlled test cells

All test cells use the same geometry, starting state, actuation command, Blade B holder, logging,
and physics configuration except for the declared model parameter.

| Test cell | Contact response | Research purpose |
|---|---|---|
| **M1 — transform-follow floor** | Blade A follows the target regardless of contact | Establish the behavior when the interaction stack cannot feed contact back to the driver |
| **M2 — rigidbody + physics material** | standard collider contact; identical material rules in edge and flat | Test whether exposed rigidbody/friction behavior produces bind-positive coupling without false binding in controls |
| **M3 — gated augmentation** | one compliant/breakable contact constraint or state transition | Test whether a known bounded augmentation reduces a specific M2 failure |

The scene may present these as side-by-side stations for inspection. That layout is an artifact
choice. Analysed runs are executed one test cell at a time from the scripted actuator.

## 6. Parameter tests and claim rules

The pilot may identify stable, meaningful ranges. Before analysed runs, freeze a bounded matrix for:

- Physics Material static/dynamic friction and combine rule;
- friction type;
- solver type and iteration counts;
- contact offset and collision-detection mode;
- Blade A actuator cap/compliance;
- Blade B holder compliance; and
- Model 3 constraint compliance/release threshold, only if gated in.

Edge and flat presentations receive the **same material rules**. Presentation geometry is the
changed condition; an “edge material” with special friction would pre-label the expected result.

Example defensible conclusion if supported:

> Across the declared Unity build, geometry, actuator, holder boundary, and tested
> physics-material range, standard rigidbody contact did not reproduce maintained edge coupling
> without also producing unacceptable coupling in the flat or edge-non-bind controls.

This supports “friction-only was insufficient **within the tested configuration/range**.” It does
not support “PhysX cannot model blade binds” or “physics materials never work.”

If M3 improves the bind-positive correspondence, it must also be tested against the flat and
edge-non-bind controls. Closing one gap by making every contact stick is a failure, not a solution.

## 7. Run outputs

Every run logs:

- configuration/run ID and reproducibility hash;
- Unity/editor/XRI version and all frozen physics settings;
- target command and actuator state;
- Blade A and Blade B position, rotation, linear/angular velocity;
- contact start/end, contact count, normals/points available from Unity, and relative motion;
- realised Blade A completion ratio and deflection;
- Blade B translation and angular change;
- contact migration/persistence descriptors;
- failure classification and any numerical warning/invalid-run reason.

Primary comparisons:

1. Does the model distinguish bind-positive edge from flat and edge-non-bind controls?
2. Does Blade A show comparable interrupted advance/redirection?
3. Does Blade B show comparable direction and magnitude of carry/rotation?
4. Does coupling persist and release without tunnelling, jitter, or indiscriminate sticking?
5. Which observable elements are preserved, reduced, exaggerated, or absent?

## 8. Calibration, pilot, and analysed-run boundary

- **Physical gate — complete:** counted-pass classifications, action windows, missing-data rules,
  and metric-eligible pilot targets are validated and frozen.
- **Unity pilot:** establish stable actuation, visibility, logger correctness, and empirical
  settling/repeatability rules. Pilot output is not a finding.
- **Calibration cases:** identify actuator/holder parameters; list them explicitly.
- **Held-out evaluation cases:** assess correspondence after settings are frozen.
- **Analysed runs:** begin only after the remaining Unity-specific items in the seven-item DR4 gate
  are recorded as closed.

Repeated identical Unity reruns verify repeatability; they do not increase inferential sample size.

## 9. Interactive VR boundary

Interactive VR may feed a controller target into the same actuator. If a person's command lies
within the measured/calibrated envelope, the model should be capable of producing the same class of
response. That is a valuable application demonstration.

The person's attempts remain demo-only unless a separately approved protocol makes their tracked
motion and outcomes analysed data. The controlled scripted actuator—not the person—is the source of
the thesis comparison.

## 10. Build order

- [x] SW-A1 specimen model and XR interactable.
- [x] Complete counted-pass six-marker Tracker extraction for both blades.
- [x] Validate pass classifications/windows and freeze metric-eligible physical pilot targets.
- [ ] Create a separate scripted measurement scene/test cell; preserve the XR scene as demo-only.
- [ ] Bake specimen scale and document collider/mass representations for both blades.
- [ ] Build dynamic Blade A command/actuator and deterministic run reset.
- [ ] Build Blade B compliant holder.
- [ ] Build the configuration manifest and fixed-step CSV logger before contact tuning.
- [ ] Implement M1 and M2 test cells.
- [ ] Declare calibration versus held-out cases.
- [ ] Validate CSV schema, units, reset, settling, and repeatability.
- [ ] Freeze the parameter matrix and execute M1/M2 comparisons.
- [ ] Decide once whether the measured M2 failure warrants M3.
- [ ] If warranted, implement and evaluate M3 against positive and negative cases.
- [ ] Produce physical-versus-virtual traces, difference tables, failure-mode figures, and the
      bounded answer to the RQ.

### Immediate implementation slices

Keep each slice buildable and reviewable:

1. **Measurement scene + rig representation:** scene, scale bake, colliders, masses, starting-pose
   transforms, and a written settings baseline.
2. **Shared runner:** command profile, finite actuator, Blade-B holder, reset/state machine, and
   configuration manifest—without model-specific tuning.
3. **Measurement output:** per-fixed-step logger, run ID/hash, failure/invalid-run events, units,
   and a small parser/sanity check for emitted CSVs.
4. **M1/M2 pilot:** same runner and initial state; verify that only the declared contact mechanism
   differs; derive settling and repeatability rules from repeated identical runs.
5. **Gate freeze:** record calibration/held-out roles, final parameter matrix, Unity estimands,
   invalid-run/exclusion rules, and exact software/settings before analysed runs.

## Changelog

- 2026-07-24 — Replaced the paused stations roadmap with a measurement-first plan after DR4.
  Corrected the input from a copied Blade A path to a shared finite actuator command; added the
  six-marker Tracker schema, provisional observations, calibration/validation boundary, bounded
  parameter tests, two-body outputs, claim rules, and the station-as-test-cell layout.
- 2026-07-27 — Recorded completion and primary-researcher validation of the sixteen-pass physical
  reduction; froze the metric-eligible Unity-pilot targets; replaced Tracker tasks with the
  measurement-scene, shared-runner, logger, M1/M2 pilot, and remaining-gate workflow.
