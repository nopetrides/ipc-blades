# Probes — station-agnostic, read-only instruments

Drop-in diagnostics for any station whose dummy carries the standard **Ragdoll Animator 2** setup.

## Rules for anything in this folder

1. **Read-only.** A probe writes no joint, drive, body, transform or material. If it can change the
   simulation it does not belong here.
2. **No station coupling.** No hardcoded station, dummy, presentation or arm names. Whatever a probe
   needs is a serialized field or is resolved from its own GameObject.
3. **No contact-model coupling.** A probe must not reference the contact representation under
   evaluation, or it stops being comparable across arms. See
   `Workspace/Week8/CH4_MeasurementContract.md` §4 — *the measurement apparatus must not change when
   the contact representation changes.*
4. **Fail loudly.** If a probe's assumptions do not hold, it refuses and says so rather than returning
   a plausible wrong number.

## Scope

These are **tier-2 diagnostics** (`CH4_MeasurementContract.md` §5.2). They characterise the apparatus
and explain failures. They are **never** Chapter 5 match criteria and **never** run-validity gates.

**One declared exception, resolved 2026-08-26.** `RqContactSideProbe` **IS a run-validity gate**, by
Noah's declaration and by `CH4_MeasurementContract.md` §4.1:

> Any tool that evaluates a station beyond the approach phase — HOLD dwell, Contact or Drive — MUST
> read the contact-side value. **RED means an INVALID STATE, and a red pass is not counted as a valid
> pass.**

It qualifies because it is representation-independent: markers and mesh only, no contact model in the
path, so it reads the same way on all ten stations and does not change when the representation does.
That is the property the tier-2 rule above exists to protect, and it is why this one probe sits
outside it. Nothing else in this folder does.

## Usage

Per-arm probes go on the **authored dummy** — the GameObject carrying `RagdollAnimator2`. Leave
`sourceRootName` empty and the probe resolves its own GameObject, then finds the generated
`<name>-Ragdoll` at runtime.

A probe about the *relationship between two swords* instead takes both swords as serialized fields and
can sit on any object; `RqContactSideProbe` is one, and the station root is the natural host.

Either way nothing exists to inspect until Play: these read the generated proxy, never the authored
hierarchy.

## Contents

| probe | reports |
|---|---|
| `RqCommandedTorqueProbe` | **reconstructed drive demand** per joint (`k·Δθ + c·Δω_err`, clamped by `maximumForce`), the effective drive parameters read from the live joints, tracking error, and baseline → peak over declared phase windows. Delivered joint torque is **unobservable** on a `ConfigurableJoint` and is declared so — this is servo *demand*, never an actuator output, and it must not be set against human joint maxima as though it were one. |
| `RqContactSideProbe` | **which edge and which face of each blade is engaged** — `Ae±/Be±` and `Af±/Bf±` — from the two quillon markers, the tip marker and the contact collider's own mesh long axis. No profile asset, no shell, no solver, no classifier, so it reads the same way on all ten stations. Also reports the contact position along each blade's own axis, the enabled collider count on each proxy, and the rendered-vs-simulated pose gap. Flags **only a full inversion** against a declared reference; a one-sided change is ordinary slide. It is an **indicator, not a detector** — it never claims the blades met. `declaredReference` ships empty and no default is supplied. |
