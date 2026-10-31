using System.Collections.Generic;
using System.Text;
using FIMSpace.FProceduralAnimation;
using UnityEngine;

/// <summary>
///     The actuator settings this station ran under: what was authored on the handler, and what the
///     generated joints actually carry.
/// </summary>
/// <remarks>
///     <para>
///         <b>READ-ONLY. This component writes nothing</b> — no handler field, no joint, no drive, no
///         body. It reads and reports.
///     </para>
///     <para>
///         <b>Why both numbers.</b> <c>SpringsValue</c> is authored on the <c>RagdollHandler</c>, but
///         what acts on the blades is the <c>slerpDrive.positionSpring</c> on each generated
///         <c>ConfigurableJoint</c>, and the package is free to scale or override one into the other —
///         per-bone <c>OverrideSpringPower</c>, <c>MusclesBoost</c> and <c>ForceMultiplier</c> all exist.
///         Reporting only the authored figure states an intention; reporting only the joint figure loses
///         what was asked for. Both are sealed, and the joint values are reported as a spread across
///         bones rather than a single number, because a per-bone multiplier shows up as a spread and
///         nothing else would reveal it.
///     </para>
///     <para>
///         <b>The immediate reason this exists.</b> Measured 2026-08-26: <c>SpringsValue</c> is 5000 on
///         27 of the 29 dummies and <b>1500</b> on exactly two — both halves of
///         <c>Station_EdgeNoBind_OurSolution</c>, the station the spring sweep was run on. The sweep left
///         it off-nominal and nothing restored it, and no sealed file said so, because the spring was
///         never in a run header. A between-station difference in anything but the contact representation
///         is a defect rather than a treatment, and this is the instrument that makes it visible.
///     </para>
///     <para>
///         <b>Damping is reported and is not the pressure term.</b> At equilibrium a damper contributes
///         nothing, so steady-state contact pressure is set by the spring. Damping is still sealed
///         because it governs how the approach to that equilibrium behaves.
///     </para>
///     <para>
///         <b><c>HardMatching</c> is reported so it can be seen to be zero.</b> It is declared off across
///         the rig and must not be reasoned about as if it runs — an argument was once built on the
///         assumption that it did, and the argument was void. Its <c>AnimatePhysics</c> sibling is
///         ignored whenever a Mecanim Animator exists, so reading it from scene YAML says nothing; it is
///         read from the live handler here.
///     </para>
///     <para>
///         It sets nothing and declares nothing. Every value is read from the live object.
///     </para>
/// </remarks>
[DisallowMultipleComponent]
public sealed class RqActuatorProbe : MonoBehaviour
{
    /// <summary>Authored handler settings, plus the spread of what the generated joints carry.</summary>
    public struct Side
    {
        public string Label;
        public bool Resolved;

        // --- authored on the handler ---
        public float SpringsValue;
        public float DampingValue;
        public float MusclesPower;
        public float TargetMusclesPower;
        public float HardMatching;
        public float AnchorBoneSpring;
        public float JointLimitSpring;
        public float JointLimitDamper;
        public string ProjectionMode;
        public string DetectionMode;
        public int SolverIterations;
        public int SolverVelocityIterations;

        // --- what the generated joints actually carry ---
        public int Joints;

        /// <summary>Joints whose slerpDrive spring is exactly zero — bones carrying no drive.</summary>
        /// <remarks>
        ///     Measured 2026-08-26: exactly ONE of the 20 generated joints reads zero on every dummy,
        ///     which is why a naive min-equals-authored test reports a mismatch on a correctly configured
        ///     rig. Counted separately so the zero is visible rather than dragging the minimum down and
        ///     looking like a fault.
        /// </remarks>
        public int JointsAtZeroSpring;

        public float SpringMin, SpringMax, SpringMean;
        public float SpringMinNonZero;
        public float DamperMin, DamperMax, DamperMean;
        public float MaxForceMin, MaxForceMax;
        public string RotationDriveModes;

        /// <summary>
        ///     True when every DRIVEN joint carries exactly the authored spring.
        /// </summary>
        /// <remarks>
        ///     Zero-spring joints are excluded, because a bone with no drive is not a scaled bone. With
        ///     them included this read NO on a correctly configured rig, which is a useless check.
        /// </remarks>
        public bool SpringMatchesAuthored =>
            Joints - JointsAtZeroSpring > 0 &&
            Mathf.Approximately(SpringMinNonZero, SpringsValue) &&
            Mathf.Approximately(SpringMax, SpringsValue);

        /// <summary>
        ///     True when the DRIVEN joints do not all carry the same spring — a per-bone multiplier.
        /// </summary>
        public bool SpringVariesAcrossJoints =>
            Joints - JointsAtZeroSpring > 1 && !Mathf.Approximately(SpringMinNonZero, SpringMax);
    }

    [Header("The two dummies")]
    [Tooltip("Sword A's dummy. Left empty, both are resolved by the frozen naming from the station root " +
             "at Awake — Dummy_A_<station> and Dummy_B_<station>.")]
    [SerializeField] private RagdollAnimator2 ragdollA;

    [SerializeField] private RagdollAnimator2 ragdollB;

    [Tooltip("Log the declaration once when it first resolves. The lines are always readable from " +
             "DeclarationLines() regardless.")]
    [SerializeField] private bool logOnResolve = true;

    private Side a, b;
    private bool logged;

    /// <summary>Blade A's dummy actuator state as of the last sample.</summary>
    public Side A => a;

    /// <summary>Blade B's dummy actuator state as of the last sample.</summary>
    public Side B => b;

    /// <summary>True when both sides resolved.</summary>
    public bool Resolved => a.Resolved && b.Resolved;

    /// <summary>
    ///     True when the two dummies of this station do not carry the same authored spring.
    /// </summary>
    /// <remarks>
    ///     A station whose two arms are sprung differently is not measuring one apparatus, and nothing
    ///     else in the rig would say so.
    /// </remarks>
    public bool SidesDisagree =>
        a.Resolved && b.Resolved && !Mathf.Approximately(a.SpringsValue, b.SpringsValue);

    private void Awake()
    {
        Resolve();
    }

    private void Resolve()
    {
        if (ragdollA != null && ragdollB != null) return;

        Transform root = transform;
        while (root != null && !root.name.StartsWith("Station_")) root = root.parent;
        if (root == null) return;

        foreach (var ra in root.GetComponentsInChildren<RagdollAnimator2>(true))
        {
            if (ragdollA == null && ra.gameObject.name.StartsWith("Dummy_A")) ragdollA = ra;
            else if (ragdollB == null && ra.gameObject.name.StartsWith("Dummy_B")) ragdollB = ra;
        }
    }

    private void FixedUpdate()
    {
        Sample();

        if (!logOnResolve || logged || !Resolved) return;

        logged = true;
        foreach (string line in DeclarationLines()) Debug.Log("[Actuator] " + line, this);
    }

    /// <summary>Reads both handlers and both generated joint sets now.</summary>
    public void Sample()
    {
        Resolve();
        a = Read("A", ragdollA);
        b = Read("B", ragdollB);
    }

    private static Side Read(string label, RagdollAnimator2 ra)
    {
        var s = new Side { Label = label };
        if (ra == null) return s;

        RagdollHandler h = ra.Handler;
        if (h == null) return s;

        s.Resolved = true;
        s.SpringsValue = h.SpringsValue;
        s.DampingValue = h.DampingValue;
        s.MusclesPower = h.MusclesPower;
        s.TargetMusclesPower = h.targetMusclesPower;
        s.HardMatching = h.HardMatching;
        s.AnchorBoneSpring = h.AnchorBoneSpring;
        s.JointLimitSpring = h.JointLimitSpring;
        s.JointLimitDamper = h.JointLimitDamper;
        s.ProjectionMode = h.ProjectionMode.ToString();
        s.DetectionMode = h.RigidbodiesDetectionMode.ToString();
        s.SolverIterations = h.UnitySolverIterations;
        s.SolverVelocityIterations = h.UnityVelocitySolverIterations;

        // The generated joints. Absent in edit mode, and reported as zero rather than guessed.
        Transform container = h.Dummy_Container;
        if (container == null) return s;

        float sMin = float.MaxValue, sMax = float.MinValue, sSum = 0f;
        float sMinNonZero = float.MaxValue;
        int zeroSprings = 0;
        float dMin = float.MaxValue, dMax = float.MinValue, dSum = 0f;
        float fMin = float.MaxValue, fMax = float.MinValue;
        var modes = new HashSet<string>();
        int n = 0;

        foreach (var j in container.GetComponentsInChildren<ConfigurableJoint>(true))
        {
            JointDrive d = j.slerpDrive;
            n++;
            sSum += d.positionSpring;
            dSum += d.positionDamper;
            if (d.positionSpring == 0f) zeroSprings++;
            else if (d.positionSpring < sMinNonZero) sMinNonZero = d.positionSpring;
            if (d.positionSpring < sMin) sMin = d.positionSpring;
            if (d.positionSpring > sMax) sMax = d.positionSpring;
            if (d.positionDamper < dMin) dMin = d.positionDamper;
            if (d.positionDamper > dMax) dMax = d.positionDamper;
            if (d.maximumForce < fMin) fMin = d.maximumForce;
            if (d.maximumForce > fMax) fMax = d.maximumForce;
            modes.Add(j.rotationDriveMode.ToString());
        }

        s.Joints = n;
        if (n == 0) return s;

        s.JointsAtZeroSpring = zeroSprings;
        s.SpringMin = sMin;
        s.SpringMinNonZero = sMinNonZero == float.MaxValue ? 0f : sMinNonZero;
        s.SpringMax = sMax;
        s.SpringMean = sSum / n;
        s.DamperMin = dMin;
        s.DamperMax = dMax;
        s.DamperMean = dSum / n;
        s.MaxForceMin = fMin;
        s.MaxForceMax = fMax;

        var sb = new StringBuilder();
        foreach (string m in modes)
        {
            if (sb.Length > 0) sb.Append('+');
            sb.Append(m);
        }

        s.RotationDriveModes = sb.ToString();
        return s;
    }

    /// <summary>The actuator declaration for a run's sealed header. Authored AND live, both sides.</summary>
    public List<string> DeclarationLines()
    {
        var lines = new List<string>(8);

        if (!Resolved)
        {
            lines.Add("actuator,state,NOT RESOLVED,note,one or both RagdollAnimator2 handlers could not " +
                      "be reached, so this run does not state the actuator settings it ran under");
            return lines;
        }

        Line(lines, a);
        Line(lines, b);

        lines.Add("actuator,sidesAgreeOnSpring," + (SidesDisagree ? "NO" : "yes") +
                  ",springA," + a.SpringsValue.ToString("F1") +
                  ",springB," + b.SpringsValue.ToString("F1") +
                  ",note,a station whose two arms are sprung differently is not measuring one apparatus");

        lines.Add("actuator,NOTE,SpringsValue is AUTHORED on the handler; jointSpring is what the " +
                  "generated ConfigurableJoints carry and is what acts on the blades. Both are stated " +
                  "because the package may scale one into the other - per-bone OverrideSpringPower, " +
                  "MusclesBoost and ForceMultiplier all exist - and a per-bone multiplier shows up as a " +
                  "SPREAD between jointSpringMinNonZero and jointSpringMax");
        lines.Add("actuator,NOTE,measured 2026-08-26: the authored spring reaches the joints UNSCALED. " +
                  "19 of 20 generated joints carry exactly the authored value and ONE carries zero - a " +
                  "bone with no drive - which is why jointsAtZeroSpring is reported separately and why " +
                  "jointSpringMean sits slightly below the authored figure. maximumForce reads " +
                  "float.MaxValue on every joint: the drives are UNCAPPED, which is declared and is not " +
                  "a fault, but it means a servo can push arbitrarily hard against a constraint");
        lines.Add("actuator,NOTE,damping is not the pressure term. At equilibrium a damper contributes " +
                  "nothing, so steady-state contact pressure is set by the spring; damping governs the " +
                  "approach to it. hardMatching is stated so it can be seen to be zero - it is declared " +
                  "off across the rig and must not be reasoned about as if it runs");

        return lines;
    }

    private static void Line(List<string> lines, Side s)
    {
        lines.Add("actuatorSide,side," + s.Label +
                  ",springsValue," + s.SpringsValue.ToString("F1") +
                  ",dampingValue," + s.DampingValue.ToString("F1") +
                  ",musclesPower," + s.MusclesPower.ToString("F3") +
                  ",targetMusclesPower," + s.TargetMusclesPower.ToString("F3") +
                  ",hardMatching," + s.HardMatching.ToString("F3") +
                  ",anchorBoneSpring," + s.AnchorBoneSpring.ToString("F3") +
                  ",jointLimitSpring," + s.JointLimitSpring.ToString("F1") +
                  ",jointLimitDamper," + s.JointLimitDamper.ToString("F1") +
                  ",projectionMode," + s.ProjectionMode +
                  ",detectionMode," + s.DetectionMode +
                  ",handlerSolverIterations," + s.SolverIterations +
                  ",handlerSolverVelocityIterations," + s.SolverVelocityIterations +
                  ",joints," + s.Joints +
                  ",jointsAtZeroSpring," + s.JointsAtZeroSpring +
                  ",jointSpringMin," + s.SpringMin.ToString("F1") +
                  ",jointSpringMinNonZero," + s.SpringMinNonZero.ToString("F1") +
                  ",jointSpringMax," + s.SpringMax.ToString("F1") +
                  ",jointSpringMean," + s.SpringMean.ToString("F1") +
                  ",jointDamperMin," + s.DamperMin.ToString("F1") +
                  ",jointDamperMax," + s.DamperMax.ToString("F1") +
                  ",jointDamperMean," + s.DamperMean.ToString("F1") +
                  ",jointMaxForceMin," + s.MaxForceMin.ToString("F1") +
                  ",jointMaxForceMax," + s.MaxForceMax.ToString("F1") +
                  ",rotationDriveModes," + (string.IsNullOrEmpty(s.RotationDriveModes)
                      ? "(none)" : s.RotationDriveModes) +
                  ",jointSpringEqualsAuthored," + (s.SpringMatchesAuthored ? "yes" : "NO") +
                  ",jointSpringVaries," + (s.SpringVariesAcrossJoints ? "YES" : "no"));
    }

    /// <summary>One compact line for a screen readout.</summary>
    public string ShortLine()
    {
        if (!Resolved) return "Actuator: not resolved";

        return "Actuator A spring " + a.SpringsValue.ToString("F0") + " -> joints " +
               a.SpringMin.ToString("F0") + ".." + a.SpringMax.ToString("F0") +
               "   B spring " + b.SpringsValue.ToString("F0") + " -> joints " +
               b.SpringMin.ToString("F0") + ".." + b.SpringMax.ToString("F0") +
               (SidesDisagree ? "   [SIDES DISAGREE]" : "");
    }
}
