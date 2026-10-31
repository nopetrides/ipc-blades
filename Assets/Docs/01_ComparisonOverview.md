# Comparison Overview

> **Living document.** Current design intent, edit as the project evolves.
> **Last reviewed:** 2026-07-24 · **Status:** DRAFT — pre-implementation design.

## Research question this serves

> To what extent do representative real-time VR contact models reproduce the observable
> mechanical coupling of a controlled, maintained edge-on-edge blade bind?

This is a **reproduction/fidelity** question. The Unity artifact answers it by applying the same
controlled **drive command** to Blade A under several contact models and measuring what each
preserves or loses. The realised Blade A path is an output, not a prescribed track: in the physical
reference, a maintained bind interrupted Blade A's expected advance and redirected it. Forcing
Blade A through its recorded end pose would erase the phenomenon under test. The study does not ask
the engine to reproduce every underlying material process, and it is not a study of human users.

## The artifact: one contact model, two run modes

There is a single contact model (two blades and their attachments — see
[02_AttachmentSpec.md](02_AttachmentSpec.md)). It is driven two different ways:

1. **Measurement mode — scripted actuation.** A target motion/actuation profile drives a dynamic
   Blade A through a force/torque-limited actuator at a frozen timestep. The command is shared; the
   realised motion of **both** blades remains free to diverge under contact. This is the **only
   source of analysed Unity data**: presentation, model, and physical-reference comparisons.
2. **Interactive VR mode — live controller.** A person wields Blade A with a VR controller. This
   is for the supervisor demo, parameter exploration, and as the substrate for a *future* "feel"
   study. It is a **demonstration and sandbox only — never a data source.**

The interactive half is not an afterthought: the supervisor's Checkpoint-3 direction
(`Workspace/Week3/Checkpoint_3_Notes.md`) is explicitly generative and hands-on — "adding force to
the rigid body," "what is the formula to achieve our reference results… play around and see which
way makes sense," and "the project should allow for both." The proposal also commits to VR. The
two-mode split honours that while keeping every analysed number in the scripted replay.

**Why the split is not optional:** the moment a person's wielding becomes analysed data (how well
they bind, how it felt, their performance), the project would be collecting human-subject data and
would break the thesis's mechanical-only, non-human-subjects determination. Keeping all data in the
scripted replay protects that determination *and* leaves the demo unrestricted. Measurement runs
also require a frozen `Time.fixedDeltaTime`; a separate mode keeps that isolated from headset
frame rates.

## The three contact models

Evaluated in this order; each shares identical blade geometry, mass, starting state, and commanded
actuation — only the drive/contact response changes.

1. **Model 1 — transform-follow (floor).** Blade A is welded to its driver target; contact cannot
   interrupt or redirect it. Fails to couple by construction. Establishes the
   low-contact-responsiveness baseline.
2. **Model 2 — rigidbody + friction.** Standard collider contact with Coulomb friction. Material
   rules are kept **consistent** between edge and flat; only geometry/orientation changes. Whether
   contact produces sustained two-way coupling, and how its migration differs, is the empirical
   question.
3. **Model 3 — augmented, gated.** Adds one **breakable/compliant contact constraint** that limits
   relative motion and releases above a threshold. It is a candidate abstraction, not a claim that
   the physical contact point was perfectly fixed. Built **only if** Models 1–2 expose a specific,
   measurable failure. Not built pre-emptively.

A predeclared compliance sweep (low/med/high on the relevant joint drive) is run rather than tuning
parameters until one clip matches.

## What we measure

See [03_ReferenceToUnityMapping.md](03_ReferenceToUnityMapping.md) for definitions. Primary and
model-agnostic:

- **Contact-point migration** along the blades — its rate, direction, and persistence during
  coupling. The first Tracker pass suggests the visible contact location can change even in the
  strong bind, so “near-zero migration = bind” is not a valid classification rule by itself.
- **Blade B rotation / carry** — the coupling transferred to the held blade.
- **Blade A realised trajectory** — interrupted advance, deflection from the commanded direction,
  and angular change when contact redirects its momentum.
- **Blade B realised trajectory** — translation, rotation, and carry relative to its neutral hold.
- **Contact duration / persistence** and the **visible failure mode** (free slide, separation,
  jitter, tunnelling, over-coupling, plausible carried motion).

## Scope guards (non-goals)

- **No VR user study.** People appear only as demo drivers; their behaviour is never analysed data.
- **No beyond-range fidelity claim.** The reference is ground truth only inside the measured range.
  Model parameters are identified against it there; the interactive mode then demonstrates that the
  *same fixed parameters* respond plausibly to novel input. That is a **capability demonstration**
  ("not a canned replay"), not a claim of accuracy outside the measured range.
- **No per-model tuning-to-match.** Success is not "make one preferred clip look right." Parameters
  are predeclared or swept; the evaluation asks what each model preserves or loses.
- **RQ stays a reproduction question.** The interactive mode is the delivery medium and a
  generalisation demonstration; the perceived-correctness / "feel" question is future work.

## Third-party provenance

Academic honesty requirement: disclose everything used, own the object of study.

- **Base project:** Unity **VR Template** (Unity 6000.4.4f1) — provides the XR rig, XR Interaction
  Toolkit, and Meta OpenXR / Quest support used for the interactive mode. Used as-is for the rig;
  project logic lives under `Assets/MBC/`.
- **Ragdoll Animator 2 (FImpossible Creations):** **studied, not imported.** Its source (available
  in a separate project) confirmed our primitive choice and gave us a parameter/stability recipe
  and the runtime-weld pattern reused for Model 3. See
  [02_AttachmentSpec.md](02_AttachmentSpec.md#grounding-in-existing-implementations). The contact
  model — the object of study — is hand-built from Unity primitives so it can be fully described and
  defended, not delegated to a vendor solver.
- **FimVRatar (an earlier VR project):** **considered as a rig source and declined as a base.** Its
  XR-rig plumbing could have been lifted to save controller-setup time, but its interaction stack
  (BIMOS/VRFPS, 144 Hz, locomotion) is measurement baggage the frozen-timestep measurement scene
  must not inherit. Decision: start from the clean VR Template rig and keep the contact model
  hand-built. At most, isolated rig plumbing would be borrowed as a disclosed convenience — never
  the project as a foundation, and never into the measurement scene.
- **Any future third-party contact package** would be added only as a clearly-labelled,
  gated "representative off-the-shelf" comparison model, never as the foundation.

## Changelog

- 2026-07-22 — Initial draft capturing the two-mode architecture, three models, scope guards, and
  provenance agreed in the Week-3 planning discussion.
- 2026-07-22 — Cited `Checkpoint_3_Notes.md` as the source of the interactive/generative steer;
  recorded the FimVRatar "considered and declined" decision in provenance.
- 2026-07-24 — Corrected the experimental input from a prescribed Blade A pose path to a shared,
  force/torque-limited drive command. Blade A and Blade B trajectories are both measured outputs;
  this preserves the observed bind outcome in which A's expected advance is interrupted and
  redirected.
