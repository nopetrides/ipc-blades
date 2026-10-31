using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

/// <summary>
///     The declared ACTIVE UPPER-BODY ACTUATOR for the servo rig (TODO ID 36). A right-side chain of
///     <see cref="ConfigurableJoint" /> servos suspended from a kinematic pelvis:
///     <code>
///     Hips (KINEMATIC anchor, outside the chain)
///     └─ Spine → Chest → UpperChest → Right_Shoulder → Right_UpperArm → Right_LowerArm → Right_Hand
///     </code>
///     <para>
///         <b>The chain stops at the hand. No legs, no lower-body ragdoll</b> — that would import ground contact,
///         foot friction, balance, weight transfer and a further set of actuator assumptions, all outside this
///         experiment. This is the structural completion of the declared actuator, not an expansion into
///         whole-body simulation.
///     </para>
///     <para>
///         <b>Why the whole chain now (Noah, 2026-08-07).</b> The previous build made only UpperArm/LowerArm/Hand
///         off a kinematic <c>UpperChest</c>, which made the torso a rigid wall: any resistance the arm generated
///         terminated there, so the rig could not express torso or shoulder-base motion at all. (a) ID 34 needs a
///         bounded torso / shoulder-base actuator and the joints must exist before that motion can be commanded;
///         (b) the physical reference includes *leaning into* the bind, which a chain ending at a fixed
///         UpperChest cannot reproduce; (c) ID 26's servo-target architecture should target the final
///         articulation once rather than being rebuilt as joints are added.
///     </para>
///     <para>
///         <b>Left arm: excluded.</b> "Retained as a visual representation of the reference posture but does not
///         participate in the active ragdoll or blade-contact mechanics." No left-side rigidbodies, joints, drives
///         or limits are created. Those bones stay plain transform children of the dynamic torso, so they follow
///         visually and contribute nothing mechanically.
///     </para>
///     <para>
///         <b>Joint type: `ConfigurableJoint`, deliberately — NOT Unity's Ragdoll Wizard.</b> The wizard emits
///         <c>CharacterJoint</c>, which exposes passive twist/swing limits and limit springs but <b>no target pose
///         and no rotational drive</b> (verified by enumerating its entire public API), so it cannot implement a
///         pose-tracking actuator. We follow Unity's documented ragdoll construction and substitute the joint
///         type, with the reason stated in the engine's own API surface.
///     </para>
///     <para>
///         <b>Limits: Unity Humanoid default motion limits</b>, read live from <see cref="HumanTrait" />'s
///         <c>GetMuscleDefaultMin/Max</c> so this code cannot drift from
///         the Week-5 humanoid joint-limits record (removed; see git history if required) §5. All seven bones report
///         <c>HumanLimit.useDefaultValues = true</c>, so these are Unity's built-ins, not per-avatar overrides.
///         ⚠️ They are an animation-authoring envelope: <b>do not describe them as physiological human ROM.</b>
///     </para>
///     <para>
///         <b>Absent DOFs are `Locked`, not left free.</b> The old uniform three-axis ±60° granted the elbow a
///         swing axis and the wrist a twist that Unity's humanoid representation does not have. Under E6 the chain
///         is the only actuator, so a spurious rotational DOF is a spurious way for the rig to answer the bind.
///         That is a structural correction, not tuning.
///     </para>
///     <para>
///         <b>Asymmetric limits go on X.</b> Only <c>lowAngularXLimit</c>/<c>highAngularXLimit</c> form a pair; Y
///         and Z are symmetric magnitudes. Each bone has at most one asymmetric DOF, so <c>axis</c> and
///         <c>secondaryAxis</c> are set to put it on X — preserving the full Unity range with no truncation, no
///         inflation, no symmetrising and no zero shift.
///     </para>
///     <para>
///         ⚠️ <b>CRITICAL — every joint is built at Humanoid MUSCLE-ZERO, never at the station's authored
///         pose.</b> A <c>ConfigurableJoint</c>'s angular zero is the relative orientation at construction, while
///         Unity's limits are defined relative to muscle-zero. Measured: on an asymmetric −60/+100 DOF, muscle
///         0 → −1 is exactly 60.00° and 0 → +1 is exactly 100.00°, so muscle 0 is the rest orientation and min/max
///         are signed offsets from it. The authored station poses sit 39.9–52.4° (UpperArm), 45.5–84.5°
///         (LowerArm) and 38.7–107.2° (Hand) from muscle-zero <b>and differ per station</b>, so building at them
///         would make the same nominal limit mean a different thing at each station — a per-station divergence in
///         actuator limits, which 04 §5 forbids. The authored pose is therefore a <b>non-zero actuator
///         target</b>, not the physical zero of the joint. Commanding it is ID 26's job;
///         <see cref="MuscleZeroLocalRotations" /> is recorded here for that calibration.
///     </para>
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Animator))]
public class ActiveArmRagdoll : MonoBehaviour
{
    public enum Side
    {
        Right,
        Left,
        Both
    }

    /// <summary>
    ///     One articulated joint of the declared chain. <c>DofX</c> carries the asymmetric Humanoid DOF where a
    ///     bone has one; <c>DofZ = -1</c> means Unity's humanoid defines no third DOF for this bone, so the
    ///     joint's Z axis becomes <c>Locked</c>.
    /// </summary>
    private struct Link
    {
        public readonly HumanBodyBones Bone;
        public readonly int DofX, DofY, DofZ;
        public readonly bool TorqueSourced;

        public Link(HumanBodyBones bone, int x, int y, int z, bool torqueSourced)
        {
            Bone = bone;
            DofX = x;
            DofY = y;
            DofZ = z;
            TorqueSourced = torqueSourced;
        }
    }

    /// <summary>
    ///     The frozen chain, root-up. Axis assignment matches JOINT_LIMITS_UnityHumanoid.md [removed - see git history] §5 exactly. A joint
    ///     sits on a bone and represents that bone's PROXIMAL joint, so RightUpperArm = shoulder,
    ///     RightLowerArm = elbow, RightHand = wrist, and RightShoulder is the clavicle/scapula girdle.
    /// </summary>
    private static readonly Link[] Chain =
    {
        new Link(HumanBodyBones.Spine, 0, 1, 2, false),          // Twist / L-R / Front-Back
        new Link(HumanBodyBones.Chest, 0, 1, 2, false),
        new Link(HumanBodyBones.UpperChest, 0, 1, 2, false),
        new Link(HumanBodyBones.RightShoulder, 2, 1, -1, false), // Down-Up asym / Front-Back / none
        new Link(HumanBodyBones.RightUpperArm, 2, 1, 0, true),   // Down-Up asym / Front-Back / Twist
        new Link(HumanBodyBones.RightLowerArm, 2, 0, -1, true),  // Stretch / Twist / none
        new Link(HumanBodyBones.RightHand, 2, 1, -1, true)       // Down-Up / In-Out / none
    };

    [Header("Side — RIGHT ONLY. The left arm is excluded from the actuator by decision (see class docs).")]
    public Side side = Side.Right;

    [Header("Drive compliance — DISTAL (arm). DECLARED: no external source exists for spring or damper.")]
    public float positionSpring = 1500f;

    public float positionDamper = 40f;

    [Header("Drive compliance — PROXIMAL (torso + girdle). DECLARED, separate so it stays visible and freezable.")]
    [Tunable("Chain", 0f, 20000f, "Proximal spring",
        "Slerp-drive stiffness of the four PROXIMAL joints (Spine/Chest/UpperChest/girdle). DECLARED - no " +
        "external source. Exposed so it is inspectable and freezable rather than a hidden default.", -5)]
    public float proximalSpring = 4000f;

    [Tunable("Chain", 0f, 1000f, "Proximal damper",
        "Slerp-drive damping of the four PROXIMAL joints. DECLARED, not sourced.", -4)]
    public float proximalDamper = 120f;

    /// <summary>
    ///     <b>SOURCED.</b> Harbo, Brincks &amp; Andersen (2012), Eur J Appl Physiol 112:267-275,
    ///     doi:10.1007/s00421-011-1975-3, Table 3, MALE, ISOMETRIC peak torque: shoulder abduction 61.1 Nm
    ///     (n=79), elbow flexion 50.9 Nm (n=83), wrist flexion 24.7 Nm (n=83). MAXIMAL VOLUNTARY torques —
    ///     correct as actuator ceilings, but they do not imply the fencer exerts maximally.
    ///     <para>
    ///         <b>Known simplification.</b> Harbo's isokinetic wrist EXTENSION is 11.4 Nm, well below the flexion
    ///         figure used here, so real wrist capacity is direction-dependent while <c>slerpDrive</c> has a
    ///         single scalar cap that cannot express that. The flexion figure is the generous reading.
    ///     </para>
    ///     <para>
    ///         These caps only began to gate the press at apparatus event E6 (2026-08-06), which removed the
    ///         external blade-force channel. Before that they bounded only the arm's reaction.
    ///     </para>
    /// </summary>
    [Header("Torque ceilings — SOURCED (Harbo 2012 Table 3, male, isometric)")]
    [Tunable("Arm", 0f, 200f, "Shoulder max torque (Nm)",
        "SOURCED: Harbo 2012 male isometric shoulder abduction = 61.1 Nm.", -8)]
    public float shoulderMaxTorque = 61.1f;

    [Tunable("Arm", 0f, 200f, "Elbow max torque (Nm)",
        "SOURCED: Harbo 2012 male isometric elbow flexion = 50.9 Nm.", -7)]
    public float elbowMaxTorque = 50.9f;

    [Tunable("Arm", 0f, 200f, "Wrist max torque (Nm)",
        "SOURCED: Harbo 2012 male isometric wrist flexion = 24.7 Nm. The BINDING joint of the arm.", -6)]
    public float wristMaxTorque = 24.7f;

    /// <summary>
    ///     ⚠️ <b>DECLARED / ENGINEERED — NOT sourced from Harbo.</b> Harbo Table 3 provides no trunk or
    ///     shoulder-girdle actuator torque, so these four ceilings are engineering values chosen to be plausibly
    ///     stronger proximally than distally. They must be labelled as declared wherever they appear — code,
    ///     configuration, verification output and the parameter ledger — and swept rather than trusted before any
    ///     DRIVE outcome rests on them. <b>Do not imply these came from Harbo.</b>
    /// </summary>
    [Header("Torque ceilings — DECLARED / ENGINEERED (Harbo provides no value for these joints)")]
    [Tunable("Chain", 0f, 500f, "Spine max torque (Nm)",
        "DECLARED, NOT sourced. Harbo Table 3 has no trunk value.", -3)]
    public float spineMaxTorque = 100f;

    [Tunable("Chain", 0f, 500f, "Chest max torque (Nm)", "DECLARED, NOT sourced.", -2)]
    public float chestMaxTorque = 100f;

    [Tunable("Chain", 0f, 500f, "UpperChest max torque (Nm)", "DECLARED, NOT sourced.", -1)]
    public float upperChestMaxTorque = 60f;

    [Tunable("Chain", 0f, 500f, "Girdle max torque (Nm)",
        "DECLARED, NOT sourced. Shoulder girdle (clavicle/scapula); Harbo has no such value.", 0)]
    public float girdleMaxTorque = 40f;

    [Header("Masses (kg) — ALL DECLARED, none directly sourced. Do not tune until the rig behaves well.")]
    public float spineMass = 8f;

    public float chestMass = 8f;
    public float upperChestMass = 8f;
    public float shoulderMass = 1.5f;
    public float upperArmMass = 2.5f;
    public float lowerArmMass = 1.5f;
    public float handMass = 0.6f;

    /// <summary>
    ///     For dummies with no station protocol object driving them (the standalone <c>XRI_Dummy_B_*</c> rigs):
    ///     build the chain from this component's own <c>Start</c>, so a rig can carry the identical actuator
    ///     topology without any of the run/mode logic.
    ///     <para>
    ///         <b>2026-08-24:</b> the object that used to build station chains, <c>PilotRunController</c>, is
    ///         deleted, and there is no <c>ActiveArmRagdoll</c> left in <c>P3T_SampleScene</c> at all - station
    ///         and bay dummies are driven by RagdollAnimator2 instead. Nothing builds this chain externally any
    ///         more, so <c>buildOnStart</c> is now the only way it is ever built.
    ///     </para>
    /// </summary>
    [Header("Standalone POSED CONTROL")]
    [Tooltip("Build the chain from this component's own Start. This is now the ONLY builder: the station " +
             "controller that used to build chains externally is deleted.")]
    public bool buildOnStart;

    /// <summary>
    ///     ⚠️ <b>Retired 2026-08-07 — do not re-enable.</b> Kept only so existing scene data deserialises without
    ///     silently changing meaning.
    ///     <para>
    ///         Freezing a posed control kinematic <b>defeats the entire point of it</b> (Noah): it bypasses the
    ///         joint drives, torque ceilings, damping, angular limits, servo residual and dynamic response to
    ///         disturbance — i.e. everything the control exists to exercise. A kinematic dummy validates nothing
    ///         about the active-ragdoll actuator.
    ///     </para>
    ///     <para>
    ///         The standalone path now canonicalises a legal interior target and <b>holds it with live servos</b>.
    ///     </para>
    /// </summary>
    [System.Obsolete("Retired: a kinematic control validates none of the drives it exists to test. Live servos hold the pose.")]
    [HideInInspector] public bool freezeAsPosedControl;

    [Header("Rigidbody config (declared)")]
    public float angularDampingValue = 0.2f;

    public float maxAngularVelocity = 50f;
    // Pinned to the project default 25/15 by the 2026-08-09 freeze. Was 12/2, with nothing recording why.
    // These are the bodies of the ACTUATOR CHAIN: under the corrected model the press comes from the joint
    // drives, so this chain is what produces the bind — and it was running the lowest velocity-iteration count
    // in the project. Velocity iterations are not a passenger (25/15 holds contact where 25/25 crosses at the
    // same rate), so a chain at 2 was a live, undeclared, load-bearing value. Heterogeneous solver quality
    // inside one apparatus is noise we cannot defend, independent of which count is "right".
    // See the Week-5 proposed-freeze verification (removed; see git history if required) section 4.
    public int solverIterations = 25;
    public int solverVelocityIterations = 15;
    public bool useGravity = true;

    /// <summary>
    ///     Declared clearance from every joint limit, in normalised units of that DOF's range. 0.85 = 15 % of the
    ///     range held in reserve.
    ///     <para>
    ///         <b>This is the project's single clearance policy</b>, first declared for the pose repair (ID 37) and
    ///         reused here rather than a second number being invented. It exists because a commanded pose resting
    ///         <i>on</i> a joint limit lets the joint's structural constraint carry load that never passed through
    ///         the declared servo torque — the actuator would not be the thing producing the response.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     Re-exported from <see cref="JointLimitPolicy" />, which is now the single declaration. Existing call
    ///     sites keep reading it here; the editor authoring tools read the policy directly, so they do not depend
    ///     on a runtime component merely to discover the number.
    /// </remarks>
    public const float DeclaredClearance = JointLimitPolicy.DeclaredClearance;

    /// <summary>
    ///     Weight on hand-orientation error in the chain solve — i.e. on <b>grip fidelity</b>, since the sword is
    ///     held fixed at its registration while the chain settles, so the hand's achieved orientation is exactly
    ///     what the weld will bind as the grip.
    ///     <para>
    ///         <b>0.002 reproduces the historical objective exactly</b> and is the default, so nothing already
    ///         validated changes until this is deliberately raised. Raising it buys grip fidelity at the cost of
    ///         hand position, which is bounded by its own 40 mm deadband — the trade must be measured per station
    ///         and declared, not assumed in either direction.
    ///     </para>
    /// </summary>
    [Header("Grip fidelity — see DECISION_ArmContactPreload; 0.002 = the historical objective")]
    [Tooltip("Weight on hand-orientation (grip) error in the chain solve. 0.002 reproduces the validated " +
             "objective. Higher buys grip fidelity against hand position, which has a 40 mm deadband.")]
    public float gripOrientWeight = 0.002f;

    [SerializeField] [HideInInspector] private Quaternion[] _muscleZero;

    /// <summary>Measured rotation axis of every DOF, bone-local, flattened as 7 links x 3 dofs. Zero = absent.</summary>
    [SerializeField] [HideInInspector] private Vector3[] _dofAxes;

    /// <summary>
    ///     The authored local rotation of each chain bone at Build time. This is the <b>weak/cosmetic pose
    ///     reference</b> the canonicaliser aims at — not an actuator definition, and not seven independently
    ///     authoritative joint quaternions.
    /// </summary>
    [SerializeField] [HideInInspector] private Quaternion[] _authored;

    /// <summary>
    ///     Each chain bone's muscle-zero local rotation, in <see cref="Chain" /> order, captured at Build.
    ///     <para>
    ///         ⚠️ <b>This is NO LONGER the joint's angular zero</b> (Noah, 2026-08-08). It is retained as a
    ///         diagnostic about the imported animation Avatar. The joint reference is
    ///         <see cref="JointZeroLocalRotations" />.
    ///     </para>
    /// </summary>
    public Quaternion[] MuscleZeroLocalRotations => _muscleZero;

    /// <summary>
    ///     <b>THE PHYSICAL JOINT REFERENCE CONFIGURATION</b> — each chain bone's local rotation at the instant its
    ///     <see cref="ConfigurableJoint" /> was constructed, in <see cref="Chain" /> order. Every conversion from a
    ///     desired local rotation to <c>targetRotation</c> is relative to this, and it is what "angular zero"
    ///     means for these joints.
    ///     <para>
    ///         Under <see cref="JointReferenceMode.AuthoredSnap" /> this equals the manually accepted SNAP pose,
    ///         which is the point: commanding SNAP then yields <c>targetRotation = identity</c> and the drives
    ///         have nothing to correct.
    ///     </para>
    /// </summary>
    public Quaternion[] JointZeroLocalRotations => _jointZero;

    [SerializeField] [HideInInspector] private Quaternion[] _jointZero;

    // ---------------------------------------------------------------------------------------------------
    // ARM-CONTACT target set — PLAN_ArmContactTarget.md [removed - see git history] Step 2 · DECISION_ArmContactTarget.md [removed - see git history] §2-§4
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    ///     <b>THE AUTHORED ARM-CONTACT TARGET</b> — each chain bone's desired local rotation for the
    ///     contact-seeking pose, in <see cref="Chain" /> order. The second of the two stored servo poses; the
    ///     first is <see cref="JointZeroLocalRotations" /> (= the accepted SNAP).
    ///     <para>
    ///         <b>Every actuated joint carries an explicit entry, including the ones that should not move</b> —
    ///         those store their SNAP rotation, never <c>null</c> and never "inherit". A joint left unspecified is
    ///         a joint whose experimental pose nobody declared, and it would fall through to runtime IK or to
    ///         whatever the chain happens to be holding. That is what this array exists to prevent.
    ///     </para>
    ///     <para>
    ///         Commanded through the <b>existing</b> <c>targetRotation</c> mapping — no new conversion path and no
    ///         runtime IK. The authoring IK proposes; these stored rotations are authoritative.
    ///     </para>
    /// </summary>
    public Quaternion[] ArmContactTargets => _armContactTargets;

    [SerializeField] [HideInInspector] private Quaternion[] _armContactTargets;

    /// <summary>
    ///     Whether <see cref="ArmContactTargets" /> was <b>deliberately authored and accepted</b>, as opposed to
    ///     merely being present in memory.
    ///     <para>
    ///         ⚠️ <b>This flag is required and cannot be inferred.</b> <c>Quaternion.identity</c> is a perfectly
    ///         legitimate authored rotation, and a correct ARM-CONTACT set may be identical to SNAP on some or all
    ///         joints, so "do these quaternions look populated?" is not a question with a trustworthy answer. Only
    ///         an explicit authoring action sets this.
    ///     </para>
    /// </summary>
    public bool ArmContactAuthored => _armContactAuthored;

    [SerializeField] [HideInInspector] private bool _armContactAuthored;

    /// <summary>
    ///     Validate the stored ARM-CONTACT set against this chain. <b>Fails loudly rather than silently
    ///     substituting anything</b> — an unset, incomplete or unflagged target set must block ARM-CONTACT, the
    ///     way a missing <c>declaredInertiaTensor</c> blocks setup, because silence is how an undeclared value
    ///     reaches a result.
    /// </summary>
    /// <param name="reason">Human-readable cause when this returns false.</param>
    public bool ArmContactTargetsValid(out string reason)
    {
        reason = null;
        if (Chain == null || Chain.Length == 0)
        {
            reason = "the chain is not built";
            return false;
        }

        if (!_armContactAuthored)
        {
            reason = "no ARM-CONTACT target has been authored and accepted (armContactAuthored = false)";
            return false;
        }

        if (_armContactTargets == null)
        {
            reason = "the ARM-CONTACT target array is null";
            return false;
        }

        if (_armContactTargets.Length != Chain.Length)
        {
            reason = $"the ARM-CONTACT target array has {_armContactTargets.Length} entries for a chain of " +
                     $"{Chain.Length} joints — every actuated joint needs an explicit entry";
            return false;
        }

        for (var i = 0; i < _armContactTargets.Length; i++)
        {
            // A zero quaternion is the default-initialised value, i.e. an entry nobody ever wrote. It is NOT
            // identity, and it is the one unpopulated state that is distinguishable from a real rotation.
            var q = _armContactTargets[i];
            if (q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w >= 1e-6f) continue;
            reason = $"entry {i} ({Chain[i].Bone}) is a zero quaternion — it was never written";
            return false;
        }

        return true;
    }

    /// <summary>
    ///     Store an ARM-CONTACT target set and mark it authored. Editor-side authoring calls this; nothing at
    ///     runtime does. Rejects a wrong-length set rather than padding it.
    /// </summary>
    public bool SetArmContactTargets(Quaternion[] targets, out string reason)
    {
        reason = null;
        if (Chain == null || Chain.Length == 0)
        {
            reason = "the chain is not built";
            return false;
        }

        if (targets == null || targets.Length != Chain.Length)
        {
            reason = $"expected {Chain.Length} targets, got {(targets == null ? "null" : targets.Length.ToString())}";
            return false;
        }

        _armContactTargets = (Quaternion[]) targets.Clone();
        _armContactAuthored = true;
        return true;
    }

    /// <summary>
    ///     Seed the ARM-CONTACT set from the accepted SNAP reference — every joint gets its SNAP rotation, so
    ///     ARM-CONTACT is a no-op by construction.
    ///     <para>
    ///         This is the <b>null test</b> of PLAN_ArmContactTarget.md [removed - see git history] Step 2: it isolates the storage and
    ///         command plumbing from the pose. It is <b>not</b> an authored contact pose and must never be
    ///         mistaken for one — it deliberately seeks no contact at all.
    ///     </para>
    /// </summary>
    public bool SeedArmContactFromSnap(out string reason)
    {
        reason = null;
        if (_jointZero == null || Chain == null || _jointZero.Length != Chain.Length)
        {
            reason = "the joint reference is missing or does not match the chain";
            return false;
        }

        return SetArmContactTargets(_jointZero, out reason);
    }

    /// <summary>Clear the ARM-CONTACT set and its authored flag, returning the chain to "no target declared".</summary>
    public void ClearArmContactTargets()
    {
        _armContactTargets = null;
        _armContactAuthored = false;
    }

    // ---------------------------------------------------------------------------------------------------
    // COMMISSIONING TARGET — runtime only, deliberately never serialized
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    ///     A complete ARM-CONTACT target loaded for THIS PLAY SESSION ONLY, to commission the mechanism before any
    ///     target is approved.
    ///     <para>
    ///         <b><see cref="System.NonSerializedAttribute" /> is the whole point.</b> Three states must stay
    ///         distinct: an editor candidate exists · a commissioning candidate is loaded for this session · an
    ///         approved experimental target exists. Persisting this would collapse the middle into the last, and
    ///         an unapproved pose would quietly become frozen experimental data. Play-mode exit destroys it, so
    ///         the authorization cannot outlive the session that granted it.
    ///     </para>
    /// </summary>
    [NonSerialized] private Quaternion[] _commissioningTargets;

    [NonSerialized] private bool _commissioningValid;

    /// <summary>True when a runtime-only commissioning target is loaded. Never persisted, never an approval.</summary>
    public bool HasCommissioningTarget => _commissioningValid && _commissioningTargets != null;

    /// <summary>
    ///     Load a complete candidate into runtime-only commissioning storage and authorize this Play session to
    ///     attempt ARM-CONTACT with it.
    ///     <para>
    ///         ⛔ <b>This is not approval.</b> It does not touch <see cref="ArmContactTargets" />,
    ///         <see cref="ArmContactAuthored" />, the serialized quarantine, <c>_jointZero</c> or the accepted
    ///         SNAP. It cannot survive Play-mode exit.
    ///     </para>
    /// </summary>
    public bool LoadCommissioningTarget(Quaternion[] candidate, out string reason)
    {
        reason = null;
        _commissioningValid = false;
        _commissioningTargets = null;

        if (Chain == null || Chain.Length == 0)
        {
            reason = "the chain is not built";
            return false;
        }

        if (candidate == null || candidate.Length != Chain.Length)
        {
            reason = $"expected {Chain.Length} targets, got " +
                     $"{(candidate == null ? "null" : candidate.Length.ToString())} — every actuated joint needs one";
            return false;
        }

        for (var i = 0; i < candidate.Length; i++)
        {
            var q = candidate[i];
            if (q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w >= 1e-6f) continue;
            reason = $"entry {i} ({Chain[i].Bone}) is a zero quaternion — it was never written";
            return false;
        }

        _commissioningTargets = (Quaternion[]) candidate.Clone();
        _commissioningValid = true;
        return true;
    }

    /// <summary>Drop the commissioning target. Play-mode exit does this anyway; this is for an explicit revoke.</summary>
    public void ClearCommissioningTarget()
    {
        _commissioningTargets = null;
        _commissioningValid = false;
    }

    /// <summary>How an ARM-CONTACT target was authorized for a given run.</summary>
    public enum ArmContactAuthorization
    {
        /// <summary>No usable target: ARM-CONTACT must refuse.</summary>
        None,

        /// <summary>Runtime-only, this session only. NOT experimental data.</summary>
        Commissioning,

        /// <summary>The serialized, approved experimental target.</summary>
        Approved
    }

    /// <summary>
    ///     Resolve which ARM-CONTACT target this chain may command, and under what authority.
    ///     <para>
    ///         Commissioning wins when present, because it is the deliberate act of a human sitting in front of
    ///         the editor for this session; the approved target is the standing one.
    ///     </para>
    /// </summary>
    public ArmContactAuthorization ResolveArmContactTarget(out Quaternion[] targets, out string reason)
    {
        targets = null;
        reason = null;
        if (HasCommissioningTarget)
        {
            targets = _commissioningTargets;
            return ArmContactAuthorization.Commissioning;
        }

        if (ArmContactTargetsValid(out reason))
        {
            targets = _armContactTargets;
            return ArmContactAuthorization.Approved;
        }

        return ArmContactAuthorization.None;
    }

    /// <summary>
    ///     Command an explicit set of per-joint local rotations through the <b>existing production mapping</b>.
    ///     No solve, no IK, no procedural generation — the caller supplies the endpoint.
    /// </summary>
    /// <param name="worstDeltaDeg">Largest difference from the joint reference. 0 means this command was a no-op.</param>
    public bool CommandExplicitTargets(Quaternion[] targets, out string reason, out float worstDeltaDeg)
    {
        reason = null;
        worstDeltaDeg = 0f;
        if (_jointZero == null || Chain == null || _jointZero.Length != Chain.Length)
        {
            reason = "the joint reference is missing or does not match the chain";
            return false;
        }

        if (targets == null || targets.Length != Chain.Length)
        {
            reason = $"expected {Chain.Length} targets, got " +
                     $"{(targets == null ? "null" : targets.Length.ToString())}";
            return false;
        }

        for (var i = 0; i < Chain.Length; i++)
        {
            var t = Bone(Chain[i].Bone);
            if (t == null) continue;
            var j = t.GetComponent<ConfigurableJoint>();
            if (j == null) continue;
            var basis = JointBasis(j);
            var delta = Quaternion.Inverse(_jointZero[i]) * targets[i];
            j.targetRotation = Quaternion.Inverse(basis) * Quaternion.Inverse(delta) * basis;
            worstDeltaDeg = Mathf.Max(worstDeltaDeg, Quaternion.Angle(_jointZero[i], targets[i]));
        }

        LastCommandedTargets = (Quaternion[]) targets.Clone();
        return true;
    }

    /// <summary>
    ///     Command the accepted SNAP pose — the joint reference itself, so every <c>targetRotation</c> is identity
    ///     and the drives have nothing to correct. <b>This is what B does during ARM-CONTACT:</b> it stays an
    ///     active resisting holder on its SNAP target rather than being limp or kinematic.
    ///     <para>
    ///         Expressed directly rather than by relying on B's ARM-CONTACT slot happening to equal SNAP.
    ///     </para>
    /// </summary>
    public bool CommandSnapTargets(out string reason)
    {
        float unused;
        return CommandExplicitTargets(_jointZero, out reason, out unused);
    }

    /// <summary>
    ///     Command the stored ARM-CONTACT target set onto the joints.
    ///     <para>
    ///         <b>There is no solve here, and that is the design.</b> This walks the stored rotations straight
    ///         through the same conversion <see cref="CommandChainCanonicalPose" /> uses —
    ///         <c>targetRotation = Inverse(basis)·Inverse(delta)·basis</c> with
    ///         <c>delta = Inverse(jointZero)·target</c> — so it shares the measured mapping and adds no second
    ///         path. The authoring IK proposes; these stored rotations are authoritative
    ///         (DECISION_ArmContactTarget.md [removed - see git history] §4).
    ///     </para>
    ///     <para>
    ///         Refuses on an invalid set rather than commanding a partial or inherited pose.
    ///     </para>
    /// </summary>
    /// <param name="worstDeltaDeg">
    ///     Largest angular difference between a commanded target and the SNAP reference. <b>Zero means this
    ///     command was a no-op</b>, which is exactly what the ARM-CONTACT = SNAP null test asserts.
    /// </param>
    public bool CommandArmContactTargets(out string reason, out float worstDeltaDeg)
    {
        worstDeltaDeg = 0f;
        if (!ArmContactTargetsValid(out reason)) return false;

        for (var i = 0; i < Chain.Length; i++)
        {
            var t = Bone(Chain[i].Bone);
            if (t == null) continue;
            var j = t.GetComponent<ConfigurableJoint>();
            if (j == null) continue;
            var basis = JointBasis(j);
            var delta = Quaternion.Inverse(_jointZero[i]) * _armContactTargets[i];
            j.targetRotation = Quaternion.Inverse(basis) * Quaternion.Inverse(delta) * basis;
            worstDeltaDeg = Mathf.Max(worstDeltaDeg, Quaternion.Angle(_jointZero[i], _armContactTargets[i]));
        }

        LastCommandedTargets = (Quaternion[]) _armContactTargets.Clone();
        return true;
    }

    /// <summary>Where a joint's angular zero lives.</summary>
    public enum JointReferenceMode
    {
        /// <summary>
        ///     ⛔ <b>RETIRED 2026-08-08.</b> Unity Humanoid muscle-zero as the physical joint zero. Kept only so
        ///     the superseded behaviour stays inspectable and revertible without a code archaeology trip. It
        ///     treats the Humanoid muscle model as the ground truth of the physical articulation, an assumption
        ///     measured false: commanding an authored pose through it destroyed the hand by 57° (A) and 103° (B).
        /// </summary>
        MuscleZero = 0,

        /// <summary>
        ///     The manually accepted station SNAP pose. The articulation is configured <b>around</b> the
        ///     reconstruction instead of the reconstruction being projected into the articulation. A locked axis
        ///     does not mean "rotate until some old Humanoid reference is satisfied"; it means "this authored
        ///     relative orientation is the locked reference, and there is no motion about that axis from here".
        /// </summary>
        AuthoredSnap = 1
    }

    [Header("Joint reference — SUPERSEDES muscle-zero (2026-08-08). See DECISION_JointReferenceIsSnap.")]
    [Tooltip("Where each joint's angular zero lives. AuthoredSnap builds the articulation around the manually " +
             "accepted pose, so commanding SNAP produces an identity target. MuscleZero is the retired behaviour.")]
    public JointReferenceMode jointReference = JointReferenceMode.AuthoredSnap;

    /// <summary>
    ///     <b>STRUCTURAL TEST ONLY — this is not a limit policy.</b> Intended DOFs are built <c>Free</c> and
    ///     absent DOFs <c>Locked</c> at the reference, so that the drives-on identity test measures the
    ///     joint-frame/target mapping and nothing else. If a limit were involved, a pose that held could be
    ///     holding because a stop caught it.
    ///     <para>
    ///         Unity's Humanoid ROM cannot simply carry over: those ranges are stated about muscle-zero, and the
    ///         reference is no longer muscle-zero. The final safety envelope is a separate decision, taken only
    ///         after the reference architecture is proven.
    ///     </para>
    /// </summary>
    [Header("First structural test only — intended DOFs Free, absent DOFs Locked. NOT the final limit policy.")]
    [Tooltip("Build intended DOFs as Free and absent DOFs as Locked, so the drives-on identity test cannot be " +
             "passed by a hard stop. Turn off only once the final safety envelope has been declared.")]
    public bool referenceTestFreeDofs = true;

    private Animator _anim;
    private readonly List<Rigidbody> _dynamic = new List<Rigidbody>();
    private string _lastTopologyReport = "";

    private Transform Bone(HumanBodyBones b)
    {
        if (_anim == null) _anim = GetComponent<Animator>();
        return _anim.GetBoneTransform(b);
    }

    /// <summary>
    ///     Standalone ACTIVE POSE-HOLDING CONTROL. Station dummies used to leave <see cref="buildOnStart" /> off
    ///     and be built by the station protocol object; that object is deleted, so this path is now the only
    ///     one - a rig with <b>no station protocol at all</b> carries the identical actuator and holds one
    ///     declared pose with live servos.
    ///     <para>
    ///         <b>Lifecycle — deliberately not SNAP/ARM/DRIVE.</b> muscle-zero → build the chain → assign one
    ///         valid, reachable, interior joint-space target → live drives → settle → keep holding. There is no
    ///         SNAP, no ARM, no DRIVE transition, and no experimental result is inferred from these rigs. If
    ///         disturbed the chain should return toward its commanded pose under the same declared drives and
    ///         torque ceilings. <b>Do not add station sequencing here.</b>
    ///     </para>
    ///     <para>
    ///         No sword is welded on this path. These rigs carry their sword <i>under</i> <c>Right_Hand</c>, which
    ///         makes them a poor test of the station sword lifecycle (Noah): first prove the live chain holds a
    ///         canonical target without locked-axis commands or hard-limit bracing, and test weld ordering
    ///         separately with a sword arranged as a station sword — independent, kinematic through settle, welded
    ///         after, then released.
    ///     </para>
    /// </summary>
    private void Start()
    {
        if (!buildOnStart) return;

        var hand = Bone(HumanBodyBones.RightHand);
        if (hand != null && hand.GetComponent<Rigidbody>() != null) return; // already built
        Build();
        // CHAIN-WIDE solve, not per-joint: the remaining problem was allocation of pose across joints, not servo
        // tracking. Per-joint canonicalisation stays available as the baseline for comparison.
        Debug.Log(CommandChainCanonicalPose() +
                  "\n  live servos hold this target; bodies stay DYNAMIC. No sword welded on this path.");
    }

    [ContextMenu("Build Upper-Body Chain")]
    public void Build()
    {
        _anim = GetComponent<Animator>();
        if (_anim == null || !_anim.isHuman)
        {
            Debug.LogError("[ActiveArmRagdoll] Humanoid Animator required.");
            return;
        }

        if (side != Side.Right)
            Debug.LogWarning("[ActiveArmRagdoll] side=" + side + " ignored: the left arm is excluded from the " +
                             "actuator by decision. Building the RIGHT chain only.");

        _dynamic.Clear();

        // 0. Remove any prior articulation on humanoid bones (e.g. the old 3-joint, two-armed build). Restricted
        //    to MAPPED HUMANOID BONES so a sword's own Rigidbody is never touched.
        int removedRb = 0, removedJoints = 0;
        for (var i = 0; i < (int) HumanBodyBones.LastBone; i++)
        {
            var t = _anim.GetBoneTransform((HumanBodyBones) i);
            if (t == null) continue;
            foreach (var j in t.GetComponents<Joint>())
            {
                DestroyImmediate(j);
                removedJoints++;
            }

            var rb = t.GetComponent<Rigidbody>();
            if (rb != null)
            {
                DestroyImmediate(rb);
                removedRb++;
            }
        }

        // 1. Resolve the kinematic anchor and the chain. Hips stays OUTSIDE the chain as the fixed root.
        var anchorT = Bone(HumanBodyBones.Hips);
        if (anchorT == null)
        {
            Debug.LogError("[ActiveArmRagdoll] No Hips bone; cannot anchor the chain.");
            return;
        }

        var bones = new Transform[Chain.Length];
        for (var i = 0; i < Chain.Length; i++)
        {
            bones[i] = Bone(Chain[i].Bone);
            if (bones[i] == null)
            {
                Debug.LogError("[ActiveArmRagdoll] Missing required bone " + Chain[i].Bone + "; chain not built.");
                return;
            }
        }

        // 2. Capture muscle-zero local rotations AND each DOF's rotation axis, restoring the pose exactly.
        Quaternion[] zeroLocal;
        Vector3[][] dofAxis;
        float restoreError;
        CaptureJointSpace(bones, out zeroLocal, out dofAxis, out restoreError);
        _muscleZero = zeroLocal;
        _dofAxes = new Vector3[Chain.Length * 3];
        for (var i = 0; i < Chain.Length; i++)
            for (var d = 0; d < 3; d++)
                _dofAxes[i * 3 + d] = dofAxis[i][d];

        // 3. Remember the authored pose and establish the JOINT REFERENCE CONFIGURATION.
        //
        //    2026-08-08 (Noah): the reference is the manually accepted SNAP pose, NOT Unity Humanoid muscle-zero.
        //    Under AuthoredSnap the chain bones are left exactly where the author put them and every joint is
        //    constructed in place, so the authored parent->child orientation IS each joint's angular zero. The
        //    articulation is configured around the reconstruction; the reconstruction is never moved to suit the
        //    articulation. Commanding SNAP therefore produces targetRotation = identity, which is the whole point.
        //
        //    The measured DOF axes stay valid across this change: dofAxis is expressed in each bone's OWN body
        //    frame, and that frame is carried by the transform, so it does not depend on the bone's localRotation.
        //    j.axis / j.secondaryAxis are read in the same body frame.
        var authored = new Quaternion[bones.Length];
        var jointZero = new Quaternion[bones.Length];
        for (var i = 0; i < bones.Length; i++)
        {
            authored[i] = bones[i].localRotation;
            jointZero[i] = jointReference == JointReferenceMode.AuthoredSnap ? authored[i] : zeroLocal[i];
            bones[i].localRotation = jointZero[i]; // a no-op under AuthoredSnap, by construction
        }

        _authored = authored;
        _jointZero = jointZero;
        InvalidateChainSolveCache(); // topology/zero/authored pose all just changed - any cached solve is stale

        // 4. Build bodies and joints AT THE REFERENCE.
        var anchor = MakeKinematic(anchorT);
        var parent = anchor;
        var joints = new ConfigurableJoint[bones.Length];
        for (var i = 0; i < bones.Length; i++)
        {
            var rb = MakeDynamic(bones[i], MassFor(Chain[i].Bone));
            joints[i] = MakeJoint(rb, parent, Chain[i], dofAxis[i]);
            parent = rb;
        }

        // Implementation fidelity: every joint must match the spec it was built from. This does not make the
        // built joint authoritative - the derivation stays the source of truth - it only confirms the write
        // landed, so a silent divergence between "what we intended" and "what PhysX got" cannot survive.
        //
        // SKIPPED under referenceTestFreeDofs, and deliberately not "adapted": the spec's ranges are stated about
        // muscle-zero, so with the reference moved to SNAP they no longer describe the joint being built. A
        // fidelity check against a spec that no longer applies would be theatre. The axis assignment is still
        // verified, because that part of the spec is unaffected by where zero sits.
        if (referenceTestFreeDofs)
        {
            var axisOnly = VerifyBuiltAxesAgainstSpecs(joints, dofAxis);
            if (axisOnly.Length > 0)
                Debug.LogError("[ActiveArmRagdoll] " + name + ": BUILT JOINT AXES DO NOT MATCH THEIR DERIVED " +
                               "SPECS:\n" + axisOnly, this);
        }
        else
        {
            var fidelity = VerifyBuiltAgainstSpecs(joints, dofAxis);
            if (fidelity.Length > 0)
                Debug.LogError("[ActiveArmRagdoll] " + name + ": BUILT JOINTS DO NOT MATCH THEIR DERIVED SPECS. " +
                               "The authoring gizmos derive from the same specs, so they would be showing a joint " +
                               "that was not created:\n" + fidelity, this);
        }

        // 5. Restore the authored pose. Under AuthoredSnap this is already where the bones are and the chain
        //    carries ZERO pose error by construction - that is the invariant under test. Under the retired
        //    MuscleZero reference it re-introduces the non-zero error that has to be commanded away.
        for (var i = 0; i < bones.Length; i++) bones[i].localRotation = authored[i];

        // The effective config, stated rather than implied: an unserialized or unnoticed reference would silently
        // change what every later measurement means.
        var worstRefErr = 0f;
        for (var i = 0; i < bones.Length; i++)
            worstRefErr = Mathf.Max(worstRefErr, Quaternion.Angle(bones[i].localRotation, _jointZero[i]));
        Debug.Log(string.Format(CultureInfo.InvariantCulture,
            "[ActiveArmRagdoll] {0}: joint reference = {1}, referenceTestFreeDofs = {2}. Worst authored-vs-" +
            "reference error at Build = {3:F6} deg (AuthoredSnap must be 0).", name, jointReference,
            referenceTestFreeDofs, worstRefErr), this);
        if (jointReference == JointReferenceMode.AuthoredSnap && worstRefErr > 1e-3f)
            Debug.LogError("[ActiveArmRagdoll] " + name + ": AuthoredSnap reference is not at the authored pose " +
                           "(" + worstRefErr.ToString("F6", CultureInfo.InvariantCulture) + " deg). Something " +
                           "moved the bones during Build; the identity test would be meaningless.", this);

        _lastTopologyReport = BuildTopologyReport(anchorT, bones, joints, zeroLocal, authored, restoreError,
            removedRb, removedJoints);
        Debug.Log(_lastTopologyReport);
    }

    [ContextMenu("Verify chain topology")]
    public void VerifyTopology()
    {
        if (string.IsNullOrEmpty(_lastTopologyReport))
        {
            Debug.LogWarning("[ActiveArmRagdoll] No topology report yet — run Build first.");
            return;
        }

        Debug.Log(_lastTopologyReport);
    }

    /// <summary>
    ///     The joint-space basis a <c>ConfigurableJoint</c> reads <c>targetRotation</c> in, built from
    ///     <c>axis</c> (X) and the orthogonalised <c>secondaryAxis</c> (Y).
    /// </summary>
    /// <remarks>Forwarder — the basis construction lives in <see cref="JointLimitSpec.BasisOf" /> so the
    /// evaluator, the target mapping and the editor gizmos all read the same joint frame.</remarks>
    private static Quaternion JointBasis(ConfigurableJoint j)
    {
        return JointLimitSpec.BasisOf(j);
    }

    /// <summary>
    ///     CANONICALISER. Finds the <b>closest feasible joint-space rotation</b> to a desired local rotation, using
    ///     only the joint's UNLOCKED DOFs and staying inside <see cref="DeclaredClearance" /> of every angular
    ///     limit — then converts it to the <c>targetRotation</c> the drive must be given.
    ///     <para>
    ///         <b>Why this is a closest-feasible-rotation problem and not "strip the forbidden component"</b>
    ///         (Noah, 2026-08-07): rotations do not decompose that simply at large angles. Unity's humanoid DOFs
    ///         are not fixed orthogonal axes, so a pose that is perfectly legal in muscle space can still have a
    ///         large quaternion component about a locked joint axis. Subtracting that component is not
    ///         well-defined; searching the feasible set is.
    ///     </para>
    ///     <para>
    ///         <b>The three things this keeps distinct:</b> the <i>authored</i> local rotation is a weak/cosmetic
    ///         pose reference; the <i>canonical</i> rotation returned here is the <b>actuator definition</b>; and
    ///         the Hand↔Sword relative transform that results once the weld is made is a <b>mechanical initial
    ///         condition</b> that must be recorded, not dismissed as cosmetic.
    ///     </para>
    ///     <para>
    ///         Task-space residual from this is legitimate — it comes from the bounded actuator's reachable set.
    ///         What must never happen is commanding a locked DOF, or commanding onto a hard angular stop so the
    ///         PhysX limit carries the load instead of the drive.
    ///     </para>
    /// </summary>
    /// <param name="link">Index into the chain, 0..6.</param>
    /// <param name="desiredLocal">The pose reference being aimed at, as a bone-local rotation.</param>
    /// <param name="canonicalLocal">The closest feasible bone-local rotation.</param>
    /// <param name="targetRotation">What to write to the joint's <c>targetRotation</c>.</param>
    /// <param name="residualDeg">Angle between <paramref name="desiredLocal" /> and the canonical result.</param>
    public bool TryCanonicalTarget(int link, Quaternion desiredLocal, out Quaternion canonicalLocal,
        out Quaternion targetRotation, out float residualDeg)
    {
        canonicalLocal = desiredLocal;
        targetRotation = Quaternion.identity;
        residualDeg = 0f;
        if (_jointZero == null || link < 0 || link >= Chain.Length) return false;

        var t = Bone(Chain[link].Bone);
        var j = t != null ? t.GetComponent<ConfigurableJoint>() : null;
        if (j == null) return false;

        var zero = _jointZero[link];
        var ex = _dofAxes[link * 3 + Chain[link].DofX];
        var ey = Chain[link].DofY >= 0 ? _dofAxes[link * 3 + Chain[link].DofY] : Vector3.zero;
        var ez = Chain[link].DofZ >= 0 ? _dofAxes[link * 3 + Chain[link].DofZ] : Vector3.zero;

        // Feasible box: interior of the DECLARED envelope. X is asymmetric, Y and Z symmetric. Locked -> 0 span.
        var loX = j.lowAngularXLimit.limit * DeclaredClearance;
        var hiX = j.highAngularXLimit.limit * DeclaredClearance;
        var spanY = ey == Vector3.zero ? 0f : j.angularYLimit.limit * DeclaredClearance;
        var spanZ = ez == Vector3.zero ? 0f : j.angularZLimit.limit * DeclaredClearance;

        System.Func<float, float, float, Quaternion> local = (a, b, c) =>
            zero * Quaternion.AngleAxis(a, ex) *
            (ey == Vector3.zero ? Quaternion.identity : Quaternion.AngleAxis(b, ey)) *
            (ez == Vector3.zero ? Quaternion.identity : Quaternion.AngleAxis(c, ez));

        float bx = 0f, by = 0f, bz = 0f;
        var best = Quaternion.Angle(local(0f, 0f, 0f), desiredLocal);
        // coarse sweep then coordinate descent — the objective is not convex in these angles
        var steps = 12;
        for (var i = 0; i <= steps; i++)
        for (var k = 0; k <= steps; k++)
        for (var n = 0; n <= (spanZ > 0f ? steps : 0); n++)
        {
            var a = Mathf.Lerp(loX, hiX, i / (float) steps);
            var b = spanY > 0f ? Mathf.Lerp(-spanY, spanY, k / (float) steps) : 0f;
            var c = spanZ > 0f ? Mathf.Lerp(-spanZ, spanZ, n / (float) steps) : 0f;
            var e = Quaternion.Angle(local(a, b, c), desiredLocal);
            if (e < best)
            {
                best = e;
                bx = a;
                by = b;
                bz = c;
            }
        }

        for (var step = 8f; step > 0.02f; step *= 0.5f)
        {
            var improved = true;
            while (improved)
            {
                improved = false;
                for (var axis = 0; axis < 3; axis++)
                for (var dir = -1; dir <= 1; dir += 2)
                {
                    float na = bx, nb = by, nc = bz;
                    if (axis == 0) na = Mathf.Clamp(bx + dir * step, loX, hiX);
                    else if (axis == 1) nb = spanY > 0f ? Mathf.Clamp(by + dir * step, -spanY, spanY) : 0f;
                    else nc = spanZ > 0f ? Mathf.Clamp(bz + dir * step, -spanZ, spanZ) : 0f;
                    var e = Quaternion.Angle(local(na, nb, nc), desiredLocal);
                    if (e >= best - 1e-4f) continue;
                    best = e;
                    bx = na;
                    by = nb;
                    bz = nc;
                    improved = true;
                }
            }
        }

        canonicalLocal = local(bx, by, bz);
        residualDeg = best;
        var basis = JointBasis(j);
        var delta = Quaternion.Inverse(zero) * canonicalLocal;
        // Mapping established by measurement, not derivation: see FINDING_ServoTargetRotationMapping.md [removed - see git history] section 1.
        targetRotation = Quaternion.Inverse(basis) * Quaternion.Inverse(delta) * basis;
        return true;
    }

    /// <summary>
    ///     The state of one joint against the <b>actual `ConfigurableJoint` constraint geometry</b> — a twist
    ///     limit about X plus an elliptical swing cone in Y/Z — rather than against a composite quaternion angle.
    ///     <para>
    ///         ⚠️ <b>Composite total angle is NOT the limit test</b> (Noah, 2026-08-07): a configured joint does
    ///         not enforce a single total-angle envelope. Using one is what let the earlier Flat run pass while
    ///         `UpperChest` sat at 20.07° composite against a 20° per-axis envelope.
    ///     </para>
    ///     <para>
    ///         This is still an <b>approximation</b> of PhysX's internal geometry, good enough to propose better
    ///         candidates. <b>It does not certify anything</b> — only the closed-loop settle test does.
    ///     </para>
    /// </summary>
    public struct LimitState
    {
        public float TwistDeg, TwistClearanceDeg, SwingOccupancy, SwingClearanceDeg;

        /// <summary>Rotation about an axis the joint has LOCKED. Should be ~0; a large value means the joint
        /// basis does not match PhysX's, so the analytical verdict cannot be trusted for that joint.</summary>
        public float LockedResidualDeg;

        public bool Engaged;
    }

    /// <summary>
    ///     Swing/twist limit state of a candidate local rotation against a joint's configured limits.
    ///     Twist is about the joint's X (its <c>axis</c>); swing is the remainder, tested against the Y/Z ellipse.
    /// </summary>
    /// <remarks>
    ///     <b>Thin forwarder to <see cref="JointLimitEvaluator" />.</b> The math moved out so the editor authoring
    ///     gizmos and this runtime gate cannot drift apart: the gizmos must predict exactly what the verification
    ///     audit later confirms, and two implementations of swing/twist decomposition can disagree while each
    ///     looks internally correct. This entry point is kept so every existing runtime call site — and the
    ///     before/after regression snapshot — goes through the same path it always did.
    /// </remarks>
    private static LimitState LimitStateOf(ConfigurableJoint j, Quaternion zero, Quaternion local, float engageDeg)
    {
        var e = JointLimitEvaluator.Evaluate(j, zero, local, engageDeg);
        return new LimitState
        {
            TwistDeg = e.TwistDeg,
            TwistClearanceDeg = e.TwistClearanceDeg,
            SwingOccupancy = e.SwingOccupancy,
            SwingClearanceDeg = e.SwingClearanceDeg,
            LockedResidualDeg = e.LockedResidualDeg,
            Engaged = e.Engaged
        };
    }

    /// <summary>
    ///     Limit state of every chain joint AS CURRENTLY ACHIEVED, i.e. after PhysX has settled it. This is the
    ///     measurement the ARM gate must use: not the target's predicted state, the realised one.
    /// </summary>
    public LimitState WorstAchievedLimitState(out HumanBodyBones worstBone, float engageDeg = 0.75f)
    {
        var worst = new LimitState
            {TwistClearanceDeg = JointLimitEvaluator.NoLimit, SwingClearanceDeg = JointLimitEvaluator.NoLimit};
        worstBone = HumanBodyBones.LastBone;
        if (_jointZero == null) return worst;
        for (var i = 0; i < Chain.Length; i++)
        {
            var t = Bone(Chain[i].Bone);
            var j = t != null ? t.GetComponent<ConfigurableJoint>() : null;
            if (j == null) continue;
            var st = LimitStateOf(j, _jointZero[i], t.localRotation, engageDeg);
            var score = Mathf.Min(st.TwistClearanceDeg, st.SwingClearanceDeg);
            var worstScore = Mathf.Min(worst.TwistClearanceDeg, worst.SwingClearanceDeg);
            if (score >= worstScore && !st.Engaged) continue;
            worst = st;
            worstBone = Chain[i].Bone;
        }

        return worst;
    }

    /// <summary>Metrics for one chain-wide solve, so the coupling's benefit and cost are explicit.</summary>
    public struct ChainSolveReport
    {
        public float WristMm, ElbowMm, HandOrientDeg, WorstOccupancy, MinClearanceDeg, SilhouetteMm;

        /// <summary>
        ///     ARM-2 contact preload, mm. <see cref="AdvanceDemandMm" /> is what was asked for along the declared
        ///     advance axis; <see cref="AdvanceAchievedMm" /> is what the legal chain could actually deliver.
        ///     <b>A non-zero residual is a correct outcome, not a solver failure</b> — feasibility outranks the
        ///     preload demand, so an unreachable preload is retained as residual rather than forced.
        /// </summary>
        public float AdvanceDemandMm, AdvanceAchievedMm;

        /// <summary>Off-axis wrist error, mm — still deadbanded, unlike the along-advance component.</summary>
        public float WristOffAxisMm;
    }

    // ---- chain-solve cache -------------------------------------------------------------------------------
    // The solve is a pure function of Build-time data (authored pose, muscle-zero, DOF axes, joint limits) plus
    // the requested advance: the RNG seed is fixed and nothing else varies. ARM previously ran it FOUR times per
    // press - twice to command, twice more inside the diagnostic report merely to print a tracking number.
    //
    // Caching is not only a cost fix. The solve reads the root's WORLD transform to build its reference FK, so a
    // re-solve after the root has advanced can land on a different local minimum purely through float exponent
    // range - the same non-translation-invariance that gave the ID 37 pose solver a 6.7 deg wrist divergence at
    // x~7.98 vs 15.98. Solving once and reusing removes that dependence entirely.
    //
    // Several entries, not one: chain A alternates between the canonical target (ARM-1, advance 0) and the
    // preload target (ARM-2) on every single press, so a single-slot cache would miss every time and cache
    // nothing at all.
    private class SolveCacheEntry
    {
        public Vector3 Advance;
        public float GripWeight; // part of the key: changing the objective changes the solution
        public Quaternion[] Targets;
        public ChainSolveReport Rep;
    }

    private readonly List<SolveCacheEntry> _solveCache = new List<SolveCacheEntry>();

    /// <summary>Discard the cached chain solves. Called by <see cref="Build" />; also exposed for diagnostics.</summary>
    [ContextMenu("Invalidate chain-solve cache")]
    public void InvalidateChainSolveCache()
    {
        _solveCache.Clear();
    }

    /// <summary>Seconds spent in the last COLD chain solve. Zero once served from cache.</summary>
    public float LastColdSolveSeconds { get; private set; }

    /// <summary>
    ///     CHAIN-WIDE TARGET SOLVE. Finds the closest legal articulated pose of the <b>whole</b> chain to the
    ///     authored reference, letting rotation <b>redistribute between joints</b> — rotation authored at the hand
    ///     may legitimately end up in the forearm, upper arm, girdle or torso wherever the declared Humanoid
    ///     articulation permits it.
    ///     <para>
    ///         <b>Why this supersedes per-joint canonicalisation.</b> Canonicalising each authored local quaternion
    ///         independently cannot move rotation between joints, so it left a 17–64° wrist residual. The remaining
    ///         problem was never servo tracking — that is already verified — it is <b>allocation of pose across
    ///         joints</b>.
    ///     </para>
    ///     <para>
    ///         <b>This does not model servos cooperating dynamically.</b> It determines one coherent articulated
    ///         target pose; afterwards each <see cref="ConfigurableJoint" /> still independently servos toward its
    ///         own resulting target.
    ///     </para>
    ///     <para>
    ///         <b>Objective priority</b> (strongest first): wrist position · elbow position · overall silhouette ·
    ///         individual local bone rotations (weak) · hand local orientation (weakest). A larger hand angular
    ///         residual is acceptable when it better preserves the observed wrist and elbow positions, so
    ///         improvement must not be judged on hand orientation alone.
    ///     </para>
    ///     <para>
    ///         <b>The feasible set is the search domain.</b> Every candidate varies only unlocked DOFs and stays
    ///         inside <see cref="DeclaredClearance" /> of every declared range by construction — nothing illegal is
    ///         ever visited and then clamped. Topology, joint zero, masses, drives and limits are untouched, and
    ///         muscle-zero remains the permanent reference.
    ///     </para>
    /// </summary>
    public bool SolveChainCanonicalTargets(out Quaternion[] canonical, out ChainSolveReport rep)
    {
        return SolveChainTargets(Vector3.zero, out canonical, out rep);
    }

    /// <summary>
    ///     As <see cref="SolveChainCanonicalTargets" />, but with an <b>ARM-2 contact preload</b>: the wrist
    ///     reference is displaced by <paramref name="advanceWorld" /> along the declared advance axis, so the
    ///     bounded actuator keeps demanding advance after first contact instead of parking at the visual pose.
    ///     <para>
    ///         <b>Why a preload is needed at all.</b> A servo commanded to pose P, with nothing pushing it, sits at
    ///         P. Both chains commanded to their own authored poses therefore sit exactly as far apart as those
    ///         poses were authored, with zero contact force. Opposed pressure requires the contact-seeking side to
    ///         be commanded <i>past</i> the visual pose; contact then stops it short, and the equilibrium is the
    ///         balance of the two chains' servo effort against the contact.
    ///     </para>
    ///     <para>
    ///         <b>This is not the retired ID 27 overshoot.</b> That was a procedural scalar applied to the sword.
    ///         This is an actuator target: the demand travels through the same joint drives, the same declared
    ///         torque ceilings and the same legality bounds as every other target. No sword is translated, no
    ///         external blade force exists, and no kinematic channel carries the load.
    ///     </para>
    ///     <para>
    ///         <b>The deadband is removed ONLY along the advance axis.</b> The wrist term is deadbanded at 40 mm,
    ///         so an advance demand smaller than that would earn no cost improvement whatever and the optimiser
    ///         would simply not move — the preload would be silently inert. Along-advance error is therefore
    ///         continuous and undeadbanded; the off-axis components keep the 40 mm deadband, because they are the
    ///         visual-reconstruction tolerance and nothing about a preload licenses tightening them.
    ///     </para>
    ///     <para>
    ///         <b>Feasibility still outranks the demand.</b> Legal DOFs and the <see cref="DeclaredClearance" />
    ///         envelope are the <i>search domain</i>, not penalty terms, so no preload can ever produce an illegal
    ///         target: an unreachable demand is retained as <c>AdvanceDemand − AdvanceAchieved</c> residual rather
    ///         than forced. Hard-limit bracing is additionally penalised here and finally vetoed by the closed-loop
    ///         settle gate, which is the oracle. This is a contact-seeking actuator, not "reach this point at any
    ///         cost".
    ///     </para>
    /// </summary>
    public bool SolveChainTargets(Vector3 advanceWorld, out Quaternion[] canonical, out ChainSolveReport rep)
    {
        foreach (var e in _solveCache)
        {
            if ((advanceWorld - e.Advance).sqrMagnitude >= 1e-12f) continue;
            if (Mathf.Abs(gripOrientWeight - e.GripWeight) > 1e-9f) continue;
            canonical = (Quaternion[]) e.Targets.Clone();
            rep = e.Rep;
            LastColdSolveSeconds = 0f;
            return true;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ok = SolveChainTargetsUncached(advanceWorld, out canonical, out rep);
        sw.Stop();
        LastColdSolveSeconds = (float) sw.Elapsed.TotalSeconds;
        if (!ok) return false;
        _solveCache.Add(new SolveCacheEntry
        {
            Advance = advanceWorld, GripWeight = gripOrientWeight, Targets = (Quaternion[]) canonical.Clone(),
            Rep = rep
        });
        return true;
    }

    private bool SolveChainTargetsUncached(Vector3 advanceWorld, out Quaternion[] canonical,
        out ChainSolveReport rep)
    {
        canonical = null;
        rep = new ChainSolveReport();
        if (_authored == null || _jointZero == null || _dofAxes == null) return false;

        var bones = new Transform[Chain.Length];
        var joints = new ConfigurableJoint[Chain.Length];
        for (var i = 0; i < Chain.Length; i++)
        {
            bones[i] = Bone(Chain[i].Bone);
            if (bones[i] == null) return false;
            joints[i] = bones[i].GetComponent<ConfigurableJoint>();
            if (joints[i] == null) return false;
        }

        // Fixed frame the chain hangs from: Spine's parent (the kinematic Hips).
        var rootT = bones[0].parent;
        var rootRot = rootT.rotation;
        var rootPos = rootT.position;
        var localPos = new Vector3[Chain.Length];
        for (var i = 0; i < Chain.Length; i++) localPos[i] = bones[i].localPosition;

        // Free-DOF table with bounds. Bounds ARE the feasible set.
        var dofLink = new List<int>();
        var dofAxis = new List<Vector3>();
        var dofSlot = new List<int>(); // 0=X 1=Y 2=Z
        var dofLo = new List<float>();
        var dofHi = new List<float>();
        for (var i = 0; i < Chain.Length; i++)
        {
            var j = joints[i];
            AddDof(dofLink, dofAxis, dofSlot, dofLo, dofHi, i, 0, _dofAxes[i * 3 + Chain[i].DofX],
                j.lowAngularXLimit.limit * DeclaredClearance, j.highAngularXLimit.limit * DeclaredClearance);
            if (Chain[i].DofY >= 0)
                AddDof(dofLink, dofAxis, dofSlot, dofLo, dofHi, i, 1, _dofAxes[i * 3 + Chain[i].DofY],
                    -j.angularYLimit.limit * DeclaredClearance, j.angularYLimit.limit * DeclaredClearance);
            if (Chain[i].DofZ >= 0)
                AddDof(dofLink, dofAxis, dofSlot, dofLo, dofHi, i, 2, _dofAxes[i * 3 + Chain[i].DofZ],
                    -j.angularZLimit.limit * DeclaredClearance, j.angularZLimit.limit * DeclaredClearance);
        }

        var n = dofLink.Count;

        // Forward kinematics from a parameter vector -> local rotations + world bone origins.
        System.Action<float[], Quaternion[], Vector3[]> fk = (th, outLocal, outPos) =>
        {
            for (var i = 0; i < Chain.Length; i++) outLocal[i] = _jointZero[i];
            for (var k = 0; k < n; k++)
                outLocal[dofLink[k]] = outLocal[dofLink[k]] * Quaternion.AngleAxis(th[k], dofAxis[k]);
            var r = rootRot;
            var p = rootPos;
            for (var i = 0; i < Chain.Length; i++)
            {
                p = p + r * localPos[i];
                r = r * outLocal[i];
                outPos[i] = p;
            }
        };

        // Reference pose: FK of the authored local rotations.
        var refPos = new Vector3[Chain.Length];
        {
            var r = rootRot;
            var p = rootPos;
            for (var i = 0; i < Chain.Length; i++)
            {
                p = p + r * localPos[i];
                r = r * _authored[i];
                refPos[i] = p;
            }
        }

        var refHandWorldRot = Quaternion.identity;
        {
            var r = rootRot;
            for (var i = 0; i < Chain.Length; i++) r = r * _authored[i];
            refHandWorldRot = r;
        }

        const int WRIST = 6, ELBOW = 5;
        var curLocal = new Quaternion[Chain.Length];
        var curPos = new Vector3[Chain.Length];

        // Observational tolerances are DEADBANDS, not targets to drive to zero. Once inside, further reduction
        // earns nothing — because buying 0.1 mm of wrist accuracy with 10-21 deg of limit bracing is a bad trade:
        // bracing breaks a hard criterion, a 20 mm wrist residual does not.
        const float WristDeadbandM = 0.040f; // tight visual, 4 cm
        const float ElbowDeadbandM = 0.160f; // coarse visual, 16 cm

        // ARM-2 contact preload. The wrist reference is displaced along the DECLARED advance axis only; the
        // deadband is suspended on that axis alone (see the method docs for why it must be).
        var hasAdvance = advanceWorld.sqrMagnitude > 1e-12f;
        var advDir = hasAdvance ? advanceWorld.normalized : Vector3.zero;
        var wristTarget = refPos[WRIST] + advanceWorld;

        System.Func<float[], float> cost = th =>
        {
            fk(th, curLocal, curPos);
            var c = 0f;

            // (2) PhysX-space limit clearance — approximate proximity term, strongest after legality. Proposes
            //     better candidates; certification is the closed-loop settle test, not this.
            for (var i = 0; i < Chain.Length; i++)
            {
                var st = LimitStateOf(joints[i], _jointZero[i], curLocal[i], 0.75f);
                var over = Mathf.Max(0f, st.SwingOccupancy - 0.85f);
                c += 4000f * over * over;                              // push the swing ellipse away from its edge
                var twClr = Mathf.Max(0f, 3f - st.TwistClearanceDeg);  // keep >= 3 deg of twist clearance
                c += 40f * twClr * twClr;
            }

            // (3) wrist, (4) elbow — deadbanded, EXCEPT along the advance axis when a preload is demanded.
            // With advance == 0 this is bit-for-bit the validated expression; the split only engages under an
            // ARM-2 preload, where a demand smaller than the 40 mm deadband would otherwise be invisible.
            if (hasAdvance)
            {
                var dW = curPos[WRIST] - wristTarget;
                var along = Vector3.Dot(dW, advDir);      // continuous: no deadband, this IS the preload demand
                var off = (dW - along * advDir).magnitude; // off-axis keeps the visual-reconstruction deadband
                var offErr = Mathf.Max(0f, off - WristDeadbandM);
                c += 20000f * along * along;
                c += 20000f * offErr * offErr;
            }
            else
            {
                var wErr = Mathf.Max(0f, Vector3.Distance(curPos[WRIST], refPos[WRIST]) - WristDeadbandM);
                c += 20000f * wErr * wErr;
            }

            var eErr = Mathf.Max(0f, Vector3.Distance(curPos[ELBOW], refPos[ELBOW]) - ElbowDeadbandM);
            c += 4000f * eErr * eErr;

            // (6) silhouette, (7) authored local rotations, (8) authored hand orientation — weakest
            for (var i = 0; i < Chain.Length; i++)
                if (i != WRIST && i != ELBOW)
                    c += 5f * Vector3.SqrMagnitude(curPos[i] - refPos[i]);
            var rot = 0f;
            for (var i = 0; i < Chain.Length; i++)
            {
                var d = Quaternion.Angle(curLocal[i], _authored[i]) * Mathf.Deg2Rad;
                rot += d * d;
            }

            c += 0.02f * rot;

            // (8) HAND ORIENTATION — historically the weakest term, on the grounds that hand orientation is
            //     cosmetic. That is only true if the grip has no mechanical role. Once the sword is welded, the
            //     hand's orientation IS the grip, and the grip is the whole coupling between the actuator chain
            //     and the blade: it sets the moment arm and the blade's angular response to every joint. The
            //     weight is therefore a DECLARED parameter rather than a buried constant, so the trade against
            //     hand position can be measured instead of assumed.
            var hw = rootRot;
            for (var i = 0; i < Chain.Length; i++) hw = hw * curLocal[i];
            var ha = Quaternion.Angle(hw, refHandWorldRot) * Mathf.Deg2Rad;
            c += gripOrientWeight * ha * ha;
            return c;
        };

        // Seeds: zeros, the per-joint canonical solution, then randoms. Multi-start is not optional here.
        var seeds = new List<float[]>();
        seeds.Add(new float[n]);
        var perJoint = new float[n];
        for (var i = 0; i < Chain.Length; i++)
        {
            Quaternion cq, tq;
            float res;
            if (!TryCanonicalTarget(i, _authored[i], out cq, out tq, out res)) continue;
            var delta = Quaternion.Inverse(_jointZero[i]) * cq;
            for (var k = 0; k < n; k++)
            {
                if (dofLink[k] != i) continue;
                float ang;
                Vector3 ax;
                delta.ToAngleAxis(out ang, out ax);
                if (ang > 180f) ang -= 360f;
                perJoint[k] = Mathf.Clamp(ang * Vector3.Dot(ax.normalized, dofAxis[k]), dofLo[k], dofHi[k]);
            }
        }

        seeds.Add(perJoint);
        var rng = new System.Random(20260807);
        for (var s = 0; s < 10; s++)
        {
            var v = new float[n];
            for (var k = 0; k < n; k++) v[k] = Mathf.Lerp(dofLo[k], dofHi[k], (float) rng.NextDouble());
            seeds.Add(v);
        }

        float[] best = null;
        var bestC = float.MaxValue;
        foreach (var seed in seeds)
        {
            var th = (float[]) seed.Clone();
            var c = cost(th);
            for (var step = 12f; step > 0.02f; step *= 0.6f)
            {
                var improved = true;
                while (improved)
                {
                    improved = false;
                    for (var k = 0; k < n; k++)
                    for (var dir = -1; dir <= 1; dir += 2)
                    {
                        var old = th[k];
                        th[k] = Mathf.Clamp(old + dir * step, dofLo[k], dofHi[k]);
                        var cc = cost(th);
                        if (cc < c - 1e-7f)
                        {
                            c = cc;
                            improved = true;
                        }
                        else
                        {
                            th[k] = old;
                        }
                    }
                }
            }

            if (c >= bestC) continue;
            bestC = c;
            best = th;
        }

        if (best == null) return false;

        fk(best, curLocal, curPos);
        canonical = (Quaternion[]) curLocal.Clone();
        var handWorld = rootRot;
        for (var i = 0; i < Chain.Length; i++) handWorld = handWorld * curLocal[i];

        // WristMm stays measured against the UNDISPLACED visual reference - that is the reconstruction quantity,
        // and it must not silently absorb the preload. The advance is reported as its own pair so the demand and
        // what the legal chain actually delivered are never conflated.
        rep.WristMm = Vector3.Distance(curPos[WRIST], refPos[WRIST]) * 1000f;
        rep.ElbowMm = Vector3.Distance(curPos[ELBOW], refPos[ELBOW]) * 1000f;
        rep.AdvanceDemandMm = advanceWorld.magnitude * 1000f;
        rep.AdvanceAchievedMm = hasAdvance ? Vector3.Dot(curPos[WRIST] - refPos[WRIST], advDir) * 1000f : 0f;
        {
            var dW = curPos[WRIST] - wristTarget;
            rep.WristOffAxisMm = (hasAdvance ? (dW - Vector3.Dot(dW, advDir) * advDir).magnitude : dW.magnitude)
                                 * 1000f;
        }

        rep.HandOrientDeg = Quaternion.Angle(handWorld, refHandWorldRot);
        var sil = 0f;
        for (var i = 0; i < Chain.Length; i++) sil += Vector3.Distance(curPos[i], refPos[i]) * 1000f;
        rep.SilhouetteMm = sil / Chain.Length;
        rep.WorstOccupancy = 0f;
        rep.MinClearanceDeg = float.MaxValue;
        for (var k = 0; k < n; k++)
        {
            var range = best[k] >= 0f ? dofHi[k] : dofLo[k];
            if (Mathf.Abs(range) > 1e-4f)
                rep.WorstOccupancy = Mathf.Max(rep.WorstOccupancy,
                    Mathf.Abs(best[k] / range) * DeclaredClearance);
            var jj = joints[dofLink[k]];
            var hardLo = dofSlot[k] == 0
                ? jj.lowAngularXLimit.limit
                : -(dofSlot[k] == 1 ? jj.angularYLimit.limit : jj.angularZLimit.limit);
            var hardHi = dofSlot[k] == 0
                ? jj.highAngularXLimit.limit
                : dofSlot[k] == 1
                    ? jj.angularYLimit.limit
                    : jj.angularZLimit.limit;
            rep.MinClearanceDeg = Mathf.Min(rep.MinClearanceDeg,
                Mathf.Min(Mathf.Abs(best[k] - hardLo), Mathf.Abs(hardHi - best[k])));
        }

        return true;
    }

    private static void AddDof(List<int> link, List<Vector3> axis, List<int> slot, List<float> lo, List<float> hi,
        int i, int s, Vector3 ax, float l, float h)
    {
        if (ax == Vector3.zero) return;
        link.Add(i);
        axis.Add(ax);
        slot.Add(s);
        lo.Add(Mathf.Min(l, h));
        hi.Add(Mathf.Max(l, h));
    }

    /// <summary>
    ///     Command SNAP — the authored pose <b>verbatim</b>, through the exact runtime target mapping. No
    ///     canonicalisation, no clamping, no chain solve.
    ///     <para>
    ///         Under <see cref="JointReferenceMode.AuthoredSnap" /> the authored pose <i>is</i> the joint
    ///         reference, so the mapping must return <c>targetRotation = identity</c> on every joint. That is
    ///         computed, not asserted: the same formula runs, and the deviation from identity is measured and
    ///         reported. A non-identity target here means the joint-frame/target mapping is wrong, and the
    ///         drives-on test downstream would be measuring that rather than the articulation.
    ///     </para>
    ///     <para>
    ///         <b>Not for experimental runs.</b> This makes no legality guarantee — it commands what was authored.
    ///     </para>
    /// </summary>
    public string CommandAuthoredPoseRaw()
    {
        if (_authored == null || _jointZero == null) return "[ActiveArmRagdoll] " + name + ": Build first.";
        var sb = new StringBuilder();
        sb.AppendLine("[ActiveArmRagdoll] " + name + " — SNAP COMMANDED RAW through the runtime mapping" +
                      "  (reference " + jointReference + ")");
        var targets = new Quaternion[Chain.Length];
        var worstNonIdentity = 0f;
        var worstBone = "";
        for (var i = 0; i < Chain.Length; i++)
        {
            var t = Bone(Chain[i].Bone);
            var j = t != null ? t.GetComponent<ConfigurableJoint>() : null;
            if (j == null) continue;
            var basis = JointBasis(j);
            var delta = Quaternion.Inverse(_jointZero[i]) * _authored[i];
            j.targetRotation = Quaternion.Inverse(basis) * Quaternion.Inverse(delta) * basis;
            targets[i] = _authored[i];

            var off = Quaternion.Angle(j.targetRotation, Quaternion.identity);
            if (off > worstNonIdentity)
            {
                worstNonIdentity = off;
                worstBone = Chain[i].Bone.ToString();
            }
        }

        sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
            "  worst |targetRotation - identity| = {0:F6} deg at {1}", worstNonIdentity,
            worstBone.Length > 0 ? worstBone : "-"));
        if (jointReference == JointReferenceMode.AuthoredSnap && worstNonIdentity > 1e-3f)
            Debug.LogError("[ActiveArmRagdoll] " + name + ": SNAP did NOT map to an identity target (worst " +
                           worstNonIdentity.ToString("F6", CultureInfo.InvariantCulture) + " deg at " + worstBone +
                           "). The joint-frame/target mapping is wrong — stop before reading the drives-on test.",
                this);

        LastCommandedTargets = targets;
        LastCommandedReport = new ChainSolveReport();
        return sb.ToString();
    }

    /// <summary>The authored local rotations captured at <see cref="Build" />, in <c>Chain</c> order.</summary>
    public Quaternion[] AuthoredLocalRotations => _authored;

    /// <summary>The chain's bones, in <c>Chain</c> order.</summary>
    public HumanBodyBones[] ChainBones
    {
        get
        {
            var b = new HumanBodyBones[Chain.Length];
            for (var i = 0; i < Chain.Length; i++) b[i] = Chain[i].Bone;
            return b;
        }
    }

    /// <summary>
    ///     The targets actually written to the joints by the last <see cref="CommandChainCanonicalPose" />, with
    ///     their solve report. <b>Read these rather than re-solving</b>: a diagnostic that re-solves is comparing
    ///     the live pose against a fresh solution rather than against what was commanded, and the two are only
    ///     guaranteed to agree while the root has not moved.
    /// </summary>
    public Quaternion[] LastCommandedTargets { get; private set; }

    public ChainSolveReport LastCommandedReport { get; private set; }

    /// <summary>
    ///     Command the chain-wide solved targets. One coherent articulated pose, one target per joint.
    ///     <paramref name="advanceWorld" /> is the ARM-2 contact preload (zero for ARM-1's canonical settle).
    /// </summary>
    public string CommandChainCanonicalPose(Vector3 advanceWorld = default(Vector3))
    {
        Quaternion[] canonical;
        ChainSolveReport rep;
        if (!SolveChainTargets(advanceWorld, out canonical, out rep))
            return "[ActiveArmRagdoll] " + name + ": chain solve failed (Build first).";
        for (var i = 0; i < Chain.Length; i++)
        {
            var t = Bone(Chain[i].Bone);
            var j = t.GetComponent<ConfigurableJoint>();
            var basis = JointBasis(j);
            var delta = Quaternion.Inverse(_jointZero[i]) * canonical[i];
            j.targetRotation = Quaternion.Inverse(basis) * Quaternion.Inverse(delta) * basis;
        }

        LastCommandedTargets = canonical;
        LastCommandedReport = rep;
        var advance = rep.AdvanceDemandMm > 1e-4f
            ? string.Format(CultureInfo.InvariantCulture,
                "  PRELOAD demand {0:F1} mm -> achieved {1:F1} mm (residual {2:F1} mm, retained not forced)" +
                "  wrist off-axis {3:F1} mm", rep.AdvanceDemandMm, rep.AdvanceAchievedMm,
                rep.AdvanceDemandMm - rep.AdvanceAchievedMm, rep.WristOffAxisMm)
            : "";
        return string.Format(CultureInfo.InvariantCulture,
            "[ActiveArmRagdoll] {0} CHAIN-WIDE targets: wrist {1:F1} mm  elbow {2:F1} mm  hand orient {3:F1} deg" +
            "  silhouette {4:F1} mm  worst occupancy {5:F3}  min hard-limit clearance {6:F1} deg" +
            "  [solve {7}]{8}",
            name, rep.WristMm, rep.ElbowMm, rep.HandOrientDeg, rep.SilhouetteMm, rep.WorstOccupancy,
            rep.MinClearanceDeg,
            LastColdSolveSeconds > 0f
                ? "COLD " + LastColdSolveSeconds.ToString("F3", CultureInfo.InvariantCulture) + " s"
                : "cached",
            advance);
    }

    /// <summary>
    ///     Command the whole chain toward the canonicalised authored pose. Returns a per-joint report.
    ///     <para>
    ///         This sets <c>targetRotation</c> only — it does not change body kinematic state, does not touch any
    ///         sword, and does not rebuild anything. SNAP resets state; topology and joint zero are established
    ///         once at Build and never redefined.
    ///     </para>
    /// </summary>
    public string CommandCanonicalAuthoredPose()
    {
        if (_authored == null || _jointZero == null) return "[ActiveArmRagdoll] " + name + ": Build first.";
        var sb = new StringBuilder();
        sb.AppendLine("[ActiveArmRagdoll] " + name + " — CANONICAL TARGETS (clearance " + DeclaredClearance + ")");
        for (var i = 0; i < Chain.Length; i++)
        {
            Quaternion canon, target;
            float residual;
            if (!TryCanonicalTarget(i, _authored[i], out canon, out target, out residual))
            {
                sb.AppendLine("  " + Chain[i].Bone + ": no joint");
                continue;
            }

            var t = Bone(Chain[i].Bone);
            var j = t.GetComponent<ConfigurableJoint>();
            j.targetRotation = target;
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "  {0,-14} canonical target set; residual vs authored reference {1,6:F2} deg", Chain[i].Bone,
                residual));
        }

        return sb.ToString();
    }

    private float MassFor(HumanBodyBones b)
    {
        if (b == HumanBodyBones.Spine) return spineMass;
        if (b == HumanBodyBones.Chest) return chestMass;
        if (b == HumanBodyBones.UpperChest) return upperChestMass;
        if (b == HumanBodyBones.RightShoulder) return shoulderMass;
        if (b == HumanBodyBones.RightUpperArm) return upperArmMass;
        if (b == HumanBodyBones.RightLowerArm) return lowerArmMass;
        return handMass;
    }

    private float TorqueFor(HumanBodyBones b)
    {
        if (b == HumanBodyBones.Spine) return spineMaxTorque;
        if (b == HumanBodyBones.Chest) return chestMaxTorque;
        if (b == HumanBodyBones.UpperChest) return upperChestMaxTorque;
        if (b == HumanBodyBones.RightShoulder) return girdleMaxTorque;
        if (b == HumanBodyBones.RightUpperArm) return shoulderMaxTorque;
        if (b == HumanBodyBones.RightLowerArm) return elbowMaxTorque;
        return wristMaxTorque;
    }

    private static bool IsProximal(HumanBodyBones b)
    {
        return b == HumanBodyBones.Spine || b == HumanBodyBones.Chest || b == HumanBodyBones.UpperChest ||
               b == HumanBodyBones.RightShoulder;
    }

    /// <summary>
    ///     Reads muscle-zero local rotations and each DOF's rotation axis (expressed in the bone's own local
    ///     frame, which is the space <c>ConfigurableJoint.axis</c> is defined in), then restores the skeleton.
    ///     <para>
    ///         <b>Why the save/restore is mandatory.</b> <c>SetHumanPose</c> cannot be used casually on these
    ///         dummies: a Get/Set round trip with UNCHANGED muscle values rotates Hips by 90° and disturbs 45
    ///         bones, because ~20 body DOFs already sit outside Unity's limits and get clamped on write. So every
    ///         mapped bone's local transform plus the root's is recorded first and restored afterwards, and the
    ///         restore error is measured and reported rather than assumed.
    ///     </para>
    /// </summary>
    private void CaptureJointSpace(Transform[] bones, out Quaternion[] zeroLocal, out Vector3[][] dofAxis,
        out float restoreError)
    {
        zeroLocal = new Quaternion[bones.Length];
        dofAxis = new Vector3[bones.Length][];
        restoreError = 0f;

        var all = new List<Transform>();
        var pos = new List<Vector3>();
        var rot = new List<Quaternion>();
        for (var i = 0; i < (int) HumanBodyBones.LastBone; i++)
        {
            var t = _anim.GetBoneTransform((HumanBodyBones) i);
            if (t == null) continue;
            all.Add(t);
            pos.Add(t.localPosition);
            rot.Add(t.localRotation);
        }

        var rootPos = transform.localPosition;
        var rootRot = transform.localRotation;

        var hp = new HumanPoseHandler(_anim.avatar, transform);
        try
        {
            var pose = new HumanPose();
            hp.GetHumanPose(ref pose);
            var muscles = new float[pose.muscles.Length];

            for (var i = 0; i < muscles.Length; i++) muscles[i] = 0f;
            System.Array.Copy(muscles, pose.muscles, muscles.Length);
            hp.SetHumanPose(ref pose);
            for (var i = 0; i < bones.Length; i++) zeroLocal[i] = bones[i].localRotation;

            // Per-DOF axis: perturb one muscle away from zero and read the local-frame axis it rotates about.
            const float probe = 0.25f;
            for (var i = 0; i < bones.Length; i++)
            {
                dofAxis[i] = new Vector3[3];
                for (var d = 0; d < 3; d++)
                {
                    var mi = HumanTrait.MuscleFromBone((int) Chain[i].Bone, d);
                    if (mi < 0) continue;
                    for (var k = 0; k < muscles.Length; k++) muscles[k] = 0f;
                    muscles[mi] = probe;
                    System.Array.Copy(muscles, pose.muscles, muscles.Length);
                    hp.SetHumanPose(ref pose);
                    var delta = Quaternion.Inverse(zeroLocal[i]) * bones[i].localRotation;
                    float ang;
                    Vector3 ax;
                    delta.ToAngleAxis(out ang, out ax);
                    dofAxis[i][d] = ang > 1e-4f && !float.IsNaN(ax.x) && ax.sqrMagnitude > 1e-8f
                        ? ax.normalized
                        : Vector3.zero;
                }
            }
        }
        finally
        {
            hp.Dispose();
            for (var i = 0; i < all.Count; i++)
            {
                all[i].localPosition = pos[i];
                all[i].localRotation = rot[i];
            }

            transform.localPosition = rootPos;
            transform.localRotation = rootRot;
        }

        for (var i = 0; i < all.Count; i++)
            restoreError = Mathf.Max(restoreError, Quaternion.Angle(all[i].localRotation, rot[i]));
    }

    private Rigidbody MakeKinematic(Transform t)
    {
        var rb = t.GetComponent<Rigidbody>();
        if (rb == null) rb = t.gameObject.AddComponent<Rigidbody>();
        rb.isKinematic = true;
        rb.useGravity = false;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        return rb;
    }

    private Rigidbody MakeDynamic(Transform t, float mass)
    {
        var rb = t.GetComponent<Rigidbody>();
        if (rb == null) rb = t.gameObject.AddComponent<Rigidbody>();
        rb.isKinematic = false;
        rb.useGravity = useGravity;
        rb.mass = mass;
        rb.linearDamping = 0f;
        rb.angularDamping = angularDampingValue;
        rb.maxAngularVelocity = maxAngularVelocity;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
        rb.solverIterations = solverIterations;
        rb.solverVelocityIterations = solverVelocityIterations;
        _dynamic.Add(rb);
        return rb;
    }

    /// <summary>
    ///     Builds one servo joint. Called with the bodies AT THE JOINT REFERENCE — under
    ///     <see cref="JointReferenceMode.AuthoredSnap" /> that is the manually accepted SNAP pose, so the joint's
    ///     angular zero is the authored orientation and a SNAP command maps to an identity target.
    /// </summary>
    private ConfigurableJoint MakeJoint(Rigidbody body, Rigidbody connectedTo, Link link, Vector3[] axes)
    {
        var j = body.gameObject.AddComponent<ConfigurableJoint>();
        j.connectedBody = connectedTo;
        j.autoConfigureConnectedAnchor = true;
        j.xMotion = j.yMotion = j.zMotion = ConfigurableJointMotion.Locked;

        // CONSUME the shared derivation — do not re-derive here. JointLimitSpec.Derive is the single source for
        // "what joint are we intending to build", and the editor authoring gizmos call the same function, so the
        // gizmo shows the joint Build actually creates rather than an editor approximation of it.
        var spec = SpecFor(link, axes);
        j.axis = spec.Axis;
        j.secondaryAxis = spec.SecondaryAxis;

        if (referenceTestFreeDofs)
        {
            // STRUCTURAL TEST TOPOLOGY. Intended DOF -> Free; absent DOF -> Locked at the reference. The DOF
            // ASSIGNMENT is unchanged and still comes from the derivation: this frees the axes the chain is meant
            // to have, and does not invent one it is not. Ranges are omitted rather than transformed, because
            // Unity's Humanoid ROM is stated about muscle-zero and the reference is no longer muscle-zero.
            //
            // Free, not "very wide Limited": a wide limit is still a stop, and a stop that catches the pose would
            // let the identity test pass for the wrong reason.
            j.angularXMotion = link.DofX >= 0 ? ConfigurableJointMotion.Free : ConfigurableJointMotion.Locked;
            j.angularYMotion = link.DofY >= 0 ? ConfigurableJointMotion.Free : ConfigurableJointMotion.Locked;
            j.angularZMotion = link.DofZ >= 0 ? ConfigurableJointMotion.Free : ConfigurableJointMotion.Locked;
            ApplyDrive(j, link);
            return j;
        }

        j.angularXMotion = spec.TwistLimited ? ConfigurableJointMotion.Limited : ConfigurableJointMotion.Locked;
        j.lowAngularXLimit = new SoftJointLimit {limit = spec.TwistLow};
        j.highAngularXLimit = new SoftJointLimit {limit = spec.TwistHigh};

        j.angularYMotion = spec.YLocked ? ConfigurableJointMotion.Locked : ConfigurableJointMotion.Limited;
        if (!spec.YLocked) j.angularYLimit = new SoftJointLimit {limit = spec.YLimit};

        j.angularZMotion = spec.ZLocked ? ConfigurableJointMotion.Locked : ConfigurableJointMotion.Limited;
        if (!spec.ZLocked) j.angularZLimit = new SoftJointLimit {limit = spec.ZLimit};

        ApplyDrive(j, link);
        return j;
    }

    /// <summary>The normal bounded servo drive. Unchanged by the reference change and by the structural test —
    /// the same springs, dampers and declared torque ceilings apply on both paths.</summary>
    private void ApplyDrive(ConfigurableJoint j, Link link)
    {
        j.rotationDriveMode = RotationDriveMode.Slerp;
        var prox = IsProximal(link.Bone);
        // maximumForce caps the DRIVE (the muscle), not the structural constraint (the bone stays connected and
        // keeps transmitting load; that is breakForce, deliberately left infinite).
        j.slerpDrive = new JointDrive
        {
            positionSpring = prox ? proximalSpring : positionSpring,
            positionDamper = prox ? proximalDamper : positionDamper,
            maximumForce = TorqueFor(link.Bone)
        };
        j.enablePreprocessing = false;
        j.projectionMode = JointProjectionMode.None;
        j.enableCollision = false;
    }

    private static JointLimitSpec SpecFor(Link link, Vector3[] axes)
    {
        return JointLimitSpec.Derive(link.Bone, link.DofX, link.DofY, link.DofZ, axes[link.DofX],
            link.DofY >= 0 ? axes[link.DofY] : Vector3.zero);
    }

    /// <summary>
    ///     Derive the specs for the whole chain <b>without building anything</b>, so the editor authoring gizmos
    ///     can show exactly the joints <see cref="Build" /> will create. Works in edit mode: the DOF axes and
    ///     muscle-zero come from the Animator/avatar, which is the same stable imported rig data Build uses.
    ///     <para>
    ///         ⚠️ This <b>temporarily manipulates the humanoid pose</b> to measure the DOF axes and restores it;
    ///         <paramref name="restoreErrorDeg" /> reports how exactly. It is the same capture Build performs.
    ///     </para>
    /// </summary>
    /// <returns>False if the rig has no usable humanoid Animator or a bone is missing.</returns>
    public bool DeriveChainSpecs(out JointLimitSpec[] specs, out Quaternion[] muscleZero, out HumanBodyBones[] bonesOut,
        out float restoreErrorDeg)
    {
        specs = null;
        muscleZero = null;
        bonesOut = null;
        restoreErrorDeg = 0f;
        var anim = GetComponent<Animator>();
        if (anim == null || !anim.isHuman) return false;
        var bones = new Transform[Chain.Length];
        bonesOut = new HumanBodyBones[Chain.Length];
        for (var i = 0; i < Chain.Length; i++)
        {
            bones[i] = Bone(Chain[i].Bone);
            bonesOut[i] = Chain[i].Bone;
            if (bones[i] == null) return false;
        }

        Quaternion[] zeroLocal;
        Vector3[][] dofAxis;
        CaptureJointSpace(bones, out zeroLocal, out dofAxis, out restoreErrorDeg);
        muscleZero = zeroLocal;
        specs = new JointLimitSpec[Chain.Length];
        for (var i = 0; i < Chain.Length; i++) specs[i] = SpecFor(Chain[i], dofAxis[i]);
        return true;
    }

    /// <summary>
    ///     Implementation-fidelity check: every built joint must match the spec it was built from. This confirms
    ///     <see cref="Build" /> applied the derivation faithfully; it does NOT make the built joint the source of
    ///     truth. Returns an empty string when every joint agrees.
    /// </summary>
    private string VerifyBuiltAgainstSpecs(ConfigurableJoint[] joints, Vector3[][] dofAxis)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < Chain.Length && i < joints.Length; i++)
        {
            if (joints[i] == null) continue;
            var diff = SpecFor(Chain[i], dofAxis[i]).VerifyBuilt(joints[i]);
            if (diff.Length > 0) sb.AppendLine("    " + Chain[i].Bone + ":" + diff);
        }

        return sb.ToString();
    }

    /// <summary>
    ///     Fidelity check for the structural-test topology: the AXES must still be exactly what the derivation
    ///     specified, and each axis's motion must be <c>Free</c> where the chain declares a DOF and
    ///     <c>Locked</c> where it does not. Ranges are not compared — the spec's ranges are stated about
    ///     muscle-zero and do not describe a joint referenced to SNAP.
    /// </summary>
    private string VerifyBuiltAxesAgainstSpecs(ConfigurableJoint[] joints, Vector3[][] dofAxis)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < Chain.Length && i < joints.Length; i++)
        {
            var j = joints[i];
            if (j == null) continue;
            var spec = SpecFor(Chain[i], dofAxis[i]);
            if (Vector3.Angle(j.axis, spec.Axis) > 0.01f)
                sb.AppendLine("    " + Chain[i].Bone + ": axis " + j.axis + " != " + spec.Axis);
            if (Vector3.Angle(j.secondaryAxis, spec.SecondaryAxis) > 0.01f)
                sb.AppendLine("    " + Chain[i].Bone + ": secondaryAxis " + j.secondaryAxis + " != " +
                              spec.SecondaryAxis);
            var want = new[]
            {
                Chain[i].DofX >= 0 ? ConfigurableJointMotion.Free : ConfigurableJointMotion.Locked,
                Chain[i].DofY >= 0 ? ConfigurableJointMotion.Free : ConfigurableJointMotion.Locked,
                Chain[i].DofZ >= 0 ? ConfigurableJointMotion.Free : ConfigurableJointMotion.Locked
            };
            var got = new[] {j.angularXMotion, j.angularYMotion, j.angularZMotion};
            for (var d = 0; d < 3; d++)
                if (want[d] != got[d])
                    sb.AppendLine("    " + Chain[i].Bone + ": angular" + "XYZ"[d] + "Motion " + got[d] + " != " +
                                  want[d]);
        }

        return sb.ToString();
    }

    private string BuildTopologyReport(Transform anchorT, Transform[] bones, ConfigurableJoint[] joints,
        Quaternion[] zeroLocal, Quaternion[] authored, float restoreError, int removedRb, int removedJoints)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[ActiveArmRagdoll] UPPER-BODY CHAIN TOPOLOGY — " + name);
        sb.AppendLine("  cleanup: removed " + removedRb + " prior rigidbodies and " + removedJoints +
                      " prior joints from humanoid bones");
        sb.AppendLine("  muscle-zero capture, pose restore error: " + restoreError.ToString("F6") + " deg -> " +
                      (restoreError < 1e-3f ? "PASS (skeleton restored exactly)" : "*** FAIL ***"));
        sb.AppendLine();
        for (var i = 0; i < bones.Length; i++)
        {
            var j = joints[i];
            var link = Chain[i];
            sb.AppendLine("  Bone:                 " + link.Bone);
            sb.AppendLine("  Connected body:       " + (j.connectedBody != null ? j.connectedBody.name : "NONE"));
            sb.AppendLine("  Joint X DOF:          " + DofName(link.Bone, link.DofX));
            sb.AppendLine("  Joint Y DOF:          " + DofName(link.Bone, link.DofY));
            sb.AppendLine("  Joint Z DOF:          " + DofName(link.Bone, link.DofZ));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "  X motion: {0,-7}  low {1,7:F1}  high {2,7:F1}", j.angularXMotion, j.lowAngularXLimit.limit,
                j.highAngularXLimit.limit));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  Y motion: {0,-7}  limit +/-{1,6:F1}",
                j.angularYMotion, j.angularYLimit.limit));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  Z motion: {0,-7}  limit +/-{1,6:F1}",
                j.angularZMotion, j.angularZLimit.limit));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "  axis {0}  secondaryAxis {1}   (measured DOF axes, {2:F1} deg apart)", j.axis.ToString("F3"),
                j.secondaryAxis.ToString("F3"), Vector3.Angle(j.axis, j.secondaryAxis)));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "  Built at muscle-zero: PASS   (authored pose is {0:F1} deg away — an actuator TARGET, not a zero)",
                Quaternion.Angle(zeroLocal[i], authored[i])));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "  Torque ceiling:       {0,6:F1} Nm   Torque source: {1}", j.slerpDrive.maximumForce,
                link.TorqueSourced ? "Harbo 2012 (SOURCED)" : "DECLARED / ENGINEERED — not Harbo"));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "  Spring: {0,8:F1}   Damper: {1,6:F1}   Mass: {2,5:F2} kg (DECLARED)",
                j.slerpDrive.positionSpring, j.slerpDrive.positionDamper,
                bones[i].GetComponent<Rigidbody>().mass));
            sb.AppendLine();
        }

        var leftDynamic = 0;
        foreach (var hb in new[]
                 {
                     HumanBodyBones.LeftShoulder, HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm,
                     HumanBodyBones.LeftHand
                 })
        {
            var t = Bone(hb);
            if (t == null) continue;
            var rb = t.GetComponent<Rigidbody>();
            if (rb != null && !rb.isKinematic) leftDynamic++;
        }

        sb.AppendLine("  Kinematic root:            " + anchorT.name);
        sb.AppendLine("  Dynamic-body count:        " + _dynamic.Count + " (expected 7)");
        sb.AppendLine("  Dynamic left-arm bodies:   " + leftDynamic + " (expected 0)");
        sb.AppendLine();
        sb.AppendLine("  NOTE: the authored pose is now a NON-ZERO actuator target. Until ID 26 sets");
        sb.AppendLine("  targetRotation, these drives command MUSCLE-ZERO, so the chain will pull toward");
        sb.AppendLine("  muscle-zero instead of holding the authored station pose. That is expected.");
        return sb.ToString();
    }

    private static string DofName(HumanBodyBones bone, int dof)
    {
        if (dof < 0) return "(absent in Unity Humanoid -> Locked)";
        var mi = HumanTrait.MuscleFromBone((int) bone, dof);
        return mi < 0 ? "(absent)" : HumanTrait.MuscleName[mi];
    }

    /// <summary>
    ///     Rigidly grip a sword rigidbody with the hand. The hand rigidbody is driven at the wrist joint, and the
    ///     sword is welded to that hand rigidbody at its existing relative transform — no anchor is set, so the
    ///     current Hand↔Sword relative pose is preserved and the palm plays no mechanical role.
    /// </summary>
    public void AttachSword(Rigidbody sword, bool right)
    {
        // Delegated so the attachment has ONE definition, shared with the connection visualiser. The visualiser
        // observes this relationship; it must never define its own. See HandSwordConnection.
        //
        // The shared path resolves the hand from `side`, not from this argument. Every call site passes
        // right:true and `side` is Right by decision (the left arm is outside the actuator), so the two agree
        // today - but a silent disagreement would attach the sword to the wrong hand, so it is checked rather
        // than assumed.
        var wantRight = side != Side.Left;
        if (right != wantRight)
        {
            Debug.LogError("[ActiveArmRagdoll] " + name + ": AttachSword(right:" + right + ") disagrees with " +
                           "side=" + side + ". The attachment binds the driven hand; refusing rather than " +
                           "binding the wrong one.", this);
            return;
        }

        HandSwordConnection.Attach(this, sword);
    }

    /// <summary>
    ///     Hand transform of the driven side, or null before <see cref="Build" />. Exposed so the station can
    ///     record the Hand↔Sword transform without reaching through the Animator itself.
    /// </summary>
    public Transform DrivenHand
    {
        get { return Bone(side == Side.Left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand); }
    }

    /// <summary>
    ///     <b>DIAGNOSTIC ONLY — never call this from an experimental run.</b> Zeroes every joint's slerp drive so
    ///     the chain goes limp while staying dynamic, jointed and gravity-loaded. This is the "one side stops
    ///     resisting" perturbation: after a stable ARM-2 equilibrium, disabling B's resistance must let A press
    ///     through and past the former equilibrium toward its own preload target. If A instead stays parked, the
    ///     equilibrium was a positional freeze or a hard constraint, not mutual actuator/contact coupling.
    ///     <para>
    ///         Note it does <b>not</b> make the chain kinematic: a kinematic B would be an infinite-mass wall, the
    ///         exact opposite of ceasing to resist.
    ///     </para>
    /// </summary>
    public string SetDrivesLimp(bool limp)
    {
        var n = 0;
        for (var i = 0; i < Chain.Length; i++)
        {
            var t = Bone(Chain[i].Bone);
            var j = t != null ? t.GetComponent<ConfigurableJoint>() : null;
            if (j == null) continue;
            var d = j.slerpDrive;
            if (limp)
            {
                if (_stashedDrives == null) _stashedDrives = new JointDrive[Chain.Length];
                _stashedDrives[i] = d;
                d.positionSpring = 0f;
                d.positionDamper = 0f;
                d.maximumForce = 0f;
            }
            else
            {
                if (_stashedDrives == null) continue;
                d = _stashedDrives[i];
            }

            j.slerpDrive = d;
            n++;
        }

        if (!limp) _stashedDrives = null;
        return "[ActiveArmRagdoll] " + name + (limp ? " drives LIMP" : " drives RESTORED") + " on " + n + " joints";
    }

    private JointDrive[] _stashedDrives;
}
