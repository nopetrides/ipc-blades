using UnityEngine;

/// <summary>
///     THE single declaration of the clearance policy.
///     <para>
///         It lives here, not on <see cref="ActiveArmRagdoll" />, so that the editor authoring gizmos do not have
///         to reach into a runtime component merely to discover the number. Sharing the *math* while leaving the
///         *policy* on the runtime component would still leave two policy sources, which is the same class of
///         problem one layer down.
///     </para>
/// </summary>
public static class JointLimitPolicy
{
    /// <summary>
    ///     Fraction of a declared angular range an authored or commanded pose may occupy. A pose sitting exactly
    ///     on a hard stop is not acceptable merely because the transform can be represented, so authoring and
    ///     verification both work inside this envelope rather than against the Unity hard limit.
    /// </summary>
    public const float DeclaredClearance = 0.85f;
}

/// <summary>
///     The declared constraint geometry of one <see cref="ConfigurableJoint" />, read out once so the evaluation
///     is a pure function of data rather than of live scene state. Twist is about the joint's X (its
///     <c>axis</c>); swing is the remainder, tested against the Y/Z ellipse.
/// </summary>
public struct JointLimitSpec
{
    public Quaternion Basis;
    public bool TwistLimited;
    public float TwistLow, TwistHigh;
    public bool YLocked, ZLocked;
    public float YLimit, ZLimit;

    /// <summary>
    ///     The axis is <c>ConfigurableJointMotion.Free</c>: it has <b>no angular stop at all</b>, so it has no
    ///     range, no clearance and no occupancy.
    ///     <para>
    ///         ⚠️ Distinct from <c>Locked</c> and from <c>Limited</c>, and the distinction is load-bearing. A Free
    ///         axis's serialized limit fields are <b>0</b> — Unity has nothing to store — so an evaluator that
    ///         assumes <c>Limited</c> divides by ~0 and reports the joint as sitting on a hard stop that does not
    ///         exist. That artefact appeared the moment the reference-validation topology went in: every
    ///         <c>RightHand</c> reported "hard limit carrying load = True" while all four of its limit fields
    ///         read 0.0. See DECISION_JointReferenceIsSnap.md [removed - see git history] §10 (Diagnostics must use the correct reference).
    ///     </para>
    /// </summary>
    public bool TwistFree, YFree, ZFree;

    /// <summary>The axes as they will be written to the joint. Kept so <see cref="Derive" /> is the one place
    /// that decides them, including the degenerate-input fallbacks.</summary>
    public Vector3 Axis, SecondaryAxis;

    /// <summary>
    ///     Which Unity humanoid DOF each joint axis carries, and the bone they belong to. Negative means the
    ///     humanoid defines no such DOF, i.e. the axis is Locked.
    ///     <para>
    ///         Carried through because the joint's X is <b>not</b> "twist" in the anatomical sense — on
    ///         <c>RightLowerArm</c> it is <i>Forearm Stretch</i>, the elbow hinge, while the twist lives on Y.
    ///         Presenting the swing/twist decomposition's own vocabulary to a human authoring a pose says
    ///         "you are twisting" when they are in fact bending. Tools should label axes with these names.
    ///     </para>
    /// </summary>
    public HumanBodyBones Bone;

    public int DofX, DofY, DofZ;

    /// <summary>Unity's muscle name for one of this joint's axes, or a Locked marker. <paramref name="slot" /> is
    /// 0 = X, 1 = Y, 2 = Z.</summary>
    public string DofName(int slot)
    {
        var dof = slot == 0 ? DofX : slot == 1 ? DofY : DofZ;
        if (dof < 0) return "LOCKED (no such humanoid DOF)";
        var mi = HumanTrait.MuscleFromBone((int) Bone, dof);
        return mi >= 0 ? HumanTrait.MuscleName[mi] : "LOCKED (no such humanoid DOF)";
    }

    /// <summary>
    ///     The joint-space basis a <c>ConfigurableJoint</c> reads <c>targetRotation</c> in, built from
    ///     <c>axis</c> (X) and the orthogonalised <c>secondaryAxis</c> (Y).
    /// </summary>
    public static Quaternion BasisOf(Vector3 axis, Vector3 secondaryAxis)
    {
        var jx = axis.normalized;
        var jy = Vector3.ProjectOnPlane(secondaryAxis, jx).normalized;
        if (jy.sqrMagnitude < 1e-6f) jy = Mathf.Abs(Vector3.Dot(jx, Vector3.up)) > 0.9f ? Vector3.forward : Vector3.up;
        return Quaternion.LookRotation(Vector3.Cross(jx, jy), jy);
    }

    public static Quaternion BasisOf(ConfigurableJoint j)
    {
        return BasisOf(j.axis, j.secondaryAxis);
    }

    /// <summary>
    ///     <b>THE SOURCE OF TRUTH.</b> Derives the joint we INTEND to build, from stable rig data only —
    ///     the chain's declared DOF assignment, the measured DOF axes, and Unity's <c>HumanTrait</c> defaults.
    ///     <para>
    ///         <b>It must never read an already-built joint.</b> Both the editor authoring gizmos (in edit mode,
    ///         where no <see cref="ConfigurableJoint" /> exists) and <c>ActiveArmRagdoll.Build</c> call this, so
    ///         the gizmo displays the joint that Build will actually create rather than an editor approximation
    ///         of it. Deriving from a built joint would only move the circular dependency.
    ///     </para>
    ///     <para>
    ///         Preferred over serializing Build's output: a serialized snapshot is a cache that can go stale
    ///         against the code that produced it — a second source of truth in time rather than in code.
    ///     </para>
    /// </summary>
    /// <param name="dofX">Muscle index for the joint's X (twist) DOF — the one carrying the asymmetric range.</param>
    /// <param name="dofY">Muscle index for Y, or negative if the humanoid defines none (then Y is Locked).</param>
    /// <param name="dofZ">Muscle index for Z, or negative if the humanoid defines none (then Z is Locked).</param>
    public static JointLimitSpec Derive(HumanBodyBones bone, int dofX, int dofY, int dofZ, Vector3 axisX,
        Vector3 axisY)
    {
        // Put the asymmetric DOF on X: only X has a low/high PAIR. Y and Z are symmetric magnitudes.
        var ax = axisX;
        var ay = dofY >= 0 ? axisY : Vector3.zero;
        if (ax == Vector3.zero) ax = Vector3.right;
        if (ay == Vector3.zero) ay = Mathf.Abs(Vector3.Dot(ax, Vector3.up)) > 0.9f ? Vector3.forward : Vector3.up;

        // X — asymmetric, straight from Unity's SIGNED min/max for that DOF. No truncation, no symmetrising.
        var miX = HumanTrait.MuscleFromBone((int) bone, dofX);
        return new JointLimitSpec
        {
            Axis = ax,
            SecondaryAxis = ay,
            Basis = BasisOf(ax, ay),
            Bone = bone,
            DofX = dofX,
            DofY = dofY,
            DofZ = dofZ,
            TwistLimited = true,
            TwistLow = HumanTrait.GetMuscleDefaultMin(miX),
            TwistHigh = HumanTrait.GetMuscleDefaultMax(miX),
            // Y/Z are LOCKED where Unity's humanoid defines no such DOF. A spurious rotational DOF is a spurious
            // way for the rig to answer the bind (E6), so it must not survive for implementation convenience.
            YLocked = dofY < 0,
            YLimit = dofY >= 0 ? SymmetricLimit(bone, dofY) : 0f,
            ZLocked = dofZ < 0,
            ZLimit = dofZ >= 0 ? SymmetricLimit(bone, dofZ) : 0f
        };
    }

    public static float SymmetricLimit(HumanBodyBones bone, int dof)
    {
        var mi = HumanTrait.MuscleFromBone((int) bone, dof);
        if (mi < 0) return 0f;
        return Mathf.Max(Mathf.Abs(HumanTrait.GetMuscleDefaultMin(mi)),
            Mathf.Abs(HumanTrait.GetMuscleDefaultMax(mi)));
    }

    /// <summary>Read the spec back off a built joint. For <see cref="VerifyBuilt" /> only — NOT a derivation.</summary>
    public static JointLimitSpec FromJoint(ConfigurableJoint j)
    {
        return new JointLimitSpec
        {
            Axis = j.axis,
            SecondaryAxis = j.secondaryAxis,
            Basis = BasisOf(j),
            TwistLimited = j.angularXMotion == ConfigurableJointMotion.Limited,
            TwistLow = j.lowAngularXLimit.limit,
            TwistHigh = j.highAngularXLimit.limit,
            YLocked = j.angularYMotion == ConfigurableJointMotion.Locked,
            ZLocked = j.angularZMotion == ConfigurableJointMotion.Locked,
            YLimit = j.angularYLimit.limit,
            ZLimit = j.angularZLimit.limit,
            // Read the THIRD state explicitly. Locked and Limited alone cannot describe this joint, and the
            // missing case silently became "Limited with a zero range".
            TwistFree = j.angularXMotion == ConfigurableJointMotion.Free,
            YFree = j.angularYMotion == ConfigurableJointMotion.Free,
            ZFree = j.angularZMotion == ConfigurableJointMotion.Free
        };
    }

    /// <summary>
    ///     Implementation-fidelity check: does the built joint actually match the spec it was built from?
    ///     Returns an empty string on agreement, otherwise a description of every field that differs.
    ///     <para>
    ///         This checks that <c>Build</c> applied the spec faithfully. It does <b>not</b> make the runtime
    ///         component the source of truth — the derivation remains that, and this only confirms the write.
    ///     </para>
    /// </summary>
    public string VerifyBuilt(ConfigurableJoint j)
    {
        var b = FromJoint(j);
        var sb = new System.Text.StringBuilder();
        if (b.TwistLimited != TwistLimited) sb.Append(" twistLimited " + b.TwistLimited + "!=" + TwistLimited);
        if (!Mathf.Approximately(b.TwistLow, TwistLow)) sb.Append(" twistLow " + b.TwistLow + "!=" + TwistLow);
        if (!Mathf.Approximately(b.TwistHigh, TwistHigh)) sb.Append(" twistHigh " + b.TwistHigh + "!=" + TwistHigh);
        if (b.YLocked != YLocked) sb.Append(" yLocked " + b.YLocked + "!=" + YLocked);
        if (b.ZLocked != ZLocked) sb.Append(" zLocked " + b.ZLocked + "!=" + ZLocked);
        if (!YLocked && !Mathf.Approximately(b.YLimit, YLimit)) sb.Append(" yLimit " + b.YLimit + "!=" + YLimit);
        if (!ZLocked && !Mathf.Approximately(b.ZLimit, ZLimit)) sb.Append(" zLimit " + b.ZLimit + "!=" + ZLimit);
        if (Quaternion.Angle(b.Basis, Basis) > 0.01f)
            sb.Append(" basis differs by " + Quaternion.Angle(b.Basis, Basis).ToString("F4") + " deg");
        return sb.ToString();
    }
}

/// <summary>
///     The evaluated state of one joint against its declared constraint geometry.
///     <para>
///         ⚠️ <b>Composite total angle is NOT the limit test</b> (Noah, 2026-08-07): a configured joint does not
///         enforce a single total-angle envelope. Using one is what let an earlier Flat run pass while
///         <c>UpperChest</c> sat at 20.07° composite against a 20° per-axis envelope.
///     </para>
///     <para>
///         This is still an <b>approximation</b> of PhysX's internal geometry, good enough to propose better
///         candidates and to warn an author while posing. <b>It certifies nothing</b> — only the closed-loop
///         settle test does.
///     </para>
/// </summary>
public struct JointLimitEvaluation
{
    public float TwistDeg, TwistClearanceDeg, SwingOccupancy, SwingClearanceDeg;

    /// <summary>
    ///     Rotation about an axis the joint has LOCKED. Should be ~0; a large value means the joint basis does not
    ///     match PhysX's, so the analytical verdict cannot be trusted for that joint.
    /// </summary>
    public float LockedResidualDeg;

    public bool Engaged;

    // Additional read-outs the authoring gizmos need to draw the envelope. They are derived from the same
    // decomposition and do not participate in any verdict, so they cannot change an existing result.
    public float SwingYDeg, SwingZDeg;

    /// <summary>Occupancy of the declared authoring envelope rather than the hard limit: 1.0 sits exactly on the
    /// <see cref="JointLimitPolicy.DeclaredClearance" /> boundary.</summary>
    public float SwingEnvelopeOccupancy;

    /// <summary>True when the pose is outside the declared authoring envelope, though possibly still inside the
    /// Unity hard limit. This is an AUTHORING verdict; <see cref="Engaged" /> is the hard-limit one.</summary>
    public bool OutsideDeclaredEnvelope;

    /// <summary>
    ///     At least one axis of this joint is <c>Free</c>, so a clearance of
    ///     <see cref="JointLimitEvaluator.NoLimit" /> on it means <b>there is no stop</b>, not "lots of room".
    ///     Reports should say so rather than printing a sentinel that reads like a measurement.
    /// </summary>
    public bool HasFreeAxis;
}

/// <summary>
///     Read-only evaluation of a joint's swing/twist state. <b>Pure math, no state.</b>
///     <para>
///         This exists as one shared implementation because the authoring gizmos must predict exactly what the
///         verification audit will later confirm. Two implementations of swing/twist decomposition, clearance and
///         locked-axis residual can disagree while each looks internally correct — and this project has already
///         been caught out several times by slightly different geometric interpretations of the same joint.
///     </para>
///     <para>
///         Deliberately NOT here: servo behaviour, solver behaviour, target generation, runtime joint
///         construction and lifecycle. This layer only answers "where does this rotation sit inside this joint's
///         declared limits".
///     </para>
/// </summary>
public static class JointLimitEvaluator
{
    /// <summary>
    ///     Sentinel clearance meaning <b>there is no angular stop on this axis</b> — not a large measured
    ///     clearance. Printed values equal to this must be rendered as "no limit", never as a number.
    /// </summary>
    public const float NoLimit = 999f;

    /// <summary>
    ///     Swing/twist limit state of a candidate local rotation against a joint's configured limits.
    ///     <paramref name="zero" /> is the joint's angular zero — since 2026-08-08 that is the <b>authored SNAP
    ///     reference</b> (<c>ActiveArmRagdoll.JointZeroLocalRotations</c>), not Humanoid muscle-zero; passing
    ///     muscle-zero here produces a Humanoid/Avatar diagnostic that must be labelled as such. See
    ///     DECISION_JointReferenceIsSnap.md [removed - see git history] §10 (Diagnostics must use the correct reference).
    ///     <paramref name="local" /> is the candidate local rotation and <paramref name="engageDeg" /> how close
    ///     to a hard stop counts as engaged.
    /// </summary>
    public static JointLimitEvaluation Evaluate(JointLimitSpec spec, Quaternion zero, Quaternion local,
        float engageDeg)
    {
        var st = new JointLimitEvaluation();
        // delta expressed in JOINT space, so +X is the twist axis
        var q = Quaternion.Inverse(spec.Basis) * (Quaternion.Inverse(zero) * local) * spec.Basis;
        if (q.w < 0f) q = new Quaternion(-q.x, -q.y, -q.z, -q.w);

        // twist about X
        var tw = new Quaternion(q.x, 0f, 0f, q.w);
        var twMag = Mathf.Sqrt(tw.x * tw.x + tw.w * tw.w);
        if (twMag < 1e-8f) tw = Quaternion.identity;
        else tw = new Quaternion(tw.x / twMag, 0f, 0f, tw.w / twMag);
        st.TwistDeg = 2f * Mathf.Atan2(tw.x, tw.w) * Mathf.Rad2Deg;
        if (st.TwistDeg > 180f) st.TwistDeg -= 360f;
        if (st.TwistDeg < -180f) st.TwistDeg += 360f;

        // A Free axis has no stop, so clearance is not merely large - it is undefined, and reported as the
        // no-limit sentinel. Same treatment as an unlimited X, deliberately, because they mean the same thing
        // mechanically.
        st.TwistClearanceDeg = spec.TwistLimited && !spec.TwistFree
            ? Mathf.Min(st.TwistDeg - spec.TwistLow, spec.TwistHigh - st.TwistDeg)
            : NoLimit;

        // swing = remainder after removing twist
        var sw = q * Quaternion.Inverse(tw);
        if (sw.w < 0f) sw = new Quaternion(-sw.x, -sw.y, -sw.z, -sw.w);
        var swY = 2f * Mathf.Atan2(sw.y, sw.w) * Mathf.Rad2Deg;
        var swZ = 2f * Mathf.Atan2(sw.z, sw.w) * Mathf.Rad2Deg;
        st.SwingYDeg = swY;
        st.SwingZDeg = swZ;

        // A LOCKED axis has no range, so it cannot have an "occupancy" - dividing by a tiny epsilon produced
        // absurd values (11.563 on the wrist) and false ENGAGED verdicts. Report locked axes as a residual angle
        // and compute the ellipse over the LIMITED axes only.
        //
        // A FREE axis is excluded for the same reason and a different cause: it has no stored range at all
        // (Unity leaves the limit field at 0), so including it divides by ~0 and manufactures a hard-stop
        // verdict out of an axis that cannot have one.
        st.LockedResidualDeg = Mathf.Max(spec.YLocked ? Mathf.Abs(swY) : 0f, spec.ZLocked ? Mathf.Abs(swZ) : 0f);

        var yBounded = !spec.YLocked && !spec.YFree;
        var zBounded = !spec.ZLocked && !spec.ZFree;
        var oy = yBounded ? swY / Mathf.Max(spec.YLimit, 1e-4f) : 0f;
        var oz = zBounded ? swZ / Mathf.Max(spec.ZLimit, 1e-4f) : 0f;
        st.SwingOccupancy = Mathf.Sqrt(oy * oy + oz * oz);
        var smallestLimited = NoLimit;
        if (yBounded) smallestLimited = Mathf.Min(smallestLimited, spec.YLimit);
        if (zBounded) smallestLimited = Mathf.Min(smallestLimited, spec.ZLimit);
        st.SwingClearanceDeg = smallestLimited > NoLimit - 1f
            ? NoLimit
            : (1f - Mathf.Min(st.SwingOccupancy, 1f)) * smallestLimited;
        st.Engaged = st.TwistClearanceDeg < engageDeg || st.SwingClearanceDeg < engageDeg;
        st.HasFreeAxis = spec.TwistFree || spec.YFree || spec.ZFree;

        // Authoring-envelope read-outs. Derived only; they take part in no existing verdict.
        st.SwingEnvelopeOccupancy = st.SwingOccupancy / JointLimitPolicy.DeclaredClearance;
        var twistOutside = spec.TwistLimited && !spec.TwistFree &&
                           (st.TwistDeg > spec.TwistHigh * JointLimitPolicy.DeclaredClearance ||
                            st.TwistDeg < spec.TwistLow * JointLimitPolicy.DeclaredClearance);
        st.OutsideDeclaredEnvelope = twistOutside || st.SwingOccupancy > JointLimitPolicy.DeclaredClearance;
        return st;
    }

    /// <summary>Convenience overload reading the spec straight off a live joint.</summary>
    public static JointLimitEvaluation Evaluate(ConfigurableJoint j, Quaternion zero, Quaternion local,
        float engageDeg)
    {
        return Evaluate(JointLimitSpec.FromJoint(j), zero, local, engageDeg);
    }

    /// <summary>
    ///     Project a desired local rotation onto the rotations this joint can <b>actually hold</b>: locked axes
    ///     zeroed, twist and swing clamped inside the declared clearance.
    ///     <para>
    ///         <b>Why this is now the authoring primitive.</b> We previously authored a Humanoid pose and then
    ///         demanded the physics articulation reproduce every resulting bone quaternion. Unity never promised
    ///         those are the same representation — <c>HumanDescription</c> builds an animation Avatar and its
    ///         twist parameters govern retargeting, while a <c>ConfigurableJoint</c> is a physical articulation
    ///         with explicit free/limited/locked axes. Reconciling them was the wrong goal, and it produced a
    ///         confirmed 57°/103° wrist failure at runtime.
    ///     </para>
    ///     <para>
    ///         Authoring through this projection means the pose is representable <b>by construction</b>: the
    ///         locked-axis residual is zero because the component is removed rather than measured, the clearance
    ///         holds because it is clamped, and the servo can hold exactly what was authored. There is nothing
    ///         left for a canonicaliser to translate.
    ///     </para>
    ///     <para>
    ///         Iterated, because the swing extraction is not exactly invertible; callers should still evaluate the
    ///         result rather than assume it.
    ///     </para>
    /// </summary>
    /// <summary>
    ///     Residual on a locked axis below which a joint is treated as already commandable. Absorbs quaternion
    ///     round-off only; it is not a tolerance anyone may tune to make a pose pass.
    /// </summary>
    public const float LockedEpsilonDeg = 1e-3f;

    /// <summary>
    ///     Remove <b>only</b> the rotation this ONE joint physically cannot command, and change nothing else.
    ///     <para>
    ///         <b>This is not <see cref="ProjectToFeasible" /> under a new name, and that function stays dead.</b>
    ///         The projector rebuilt swing as <c>AngleAxis(Y)·AngleAxis(Z)</c> from independently extracted
    ///         components, which does not invert its own extraction — measured, it would have moved RightUpperArm
    ///         43.9° on a joint reporting 0.000 locked residual. A guardrail that moves an already-fine joint is
    ///         worse than no guardrail.
    ///     </para>
    ///     <para>
    ///         <b>Only LOCKED axes are considered.</b> Under <c>referenceTestFreeDofs</c> the intended DOFs are
    ///         built <c>Free</c> and only absent DOFs are <c>Locked</c>, so the built joint has no angular limits
    ///         at all. The spec's Humanoid ranges are stated about muscle-zero and do not describe that joint —
    ///         clamping to them would be theatre, and is deliberately not done here.
    ///     </para>
    ///     <para>
    ///         <b>Method.</b> Swing–twist split about the joint's own X, then the swing is handled in
    ///         rotation-vector form, where the Y and Z contributions are independent coordinates of one vector.
    ///         Zeroing the locked coordinate provably leaves the other coordinate and the twist untouched — which
    ///         is the property the projector could not offer.
    ///     </para>
    ///     <para>
    ///         <b>Legal input is returned unchanged, not reconstructed.</b> The no-violation case short-circuits
    ///         before any recomposition, so the output is bit-identical to the input rather than merely close.
    ///     </para>
    /// </summary>
    /// <param name="reference">The joint's angular zero — the physical joint reference, never muscle-zero.</param>
    /// <param name="removedDeg">How much locked-axis rotation was removed. Zero means the input was returned as-is.</param>
    public static Quaternion ClampJointTarget(JointLimitSpec spec, Quaternion reference, Quaternion local,
        out float removedDeg)
    {
        removedDeg = 0f;
        if (!spec.YLocked && !spec.ZLocked) return local; // nothing is locked; nothing to remove

        // delta expressed in JOINT space, so +X is the twist axis
        var delta = Quaternion.Inverse(spec.Basis) * (Quaternion.Inverse(reference) * local) * spec.Basis;
        if (delta.w < 0f) delta = new Quaternion(-delta.x, -delta.y, -delta.z, -delta.w);

        // swing-twist: twist about X, swing about an axis in the YZ plane
        var tw = new Quaternion(delta.x, 0f, 0f, delta.w);
        var twMag = Mathf.Sqrt(tw.x * tw.x + tw.w * tw.w);
        tw = twMag < 1e-8f ? Quaternion.identity : new Quaternion(tw.x / twMag, 0f, 0f, tw.w / twMag);
        var swing = delta * Quaternion.Inverse(tw);

        // swing as a rotation vector: axis * angle. Y and Z are then independent coordinates.
        float ang;
        Vector3 axis;
        swing.ToAngleAxis(out ang, out axis);
        if (float.IsNaN(axis.x) || axis.sqrMagnitude < 1e-12f || ang < 1e-6f) return local;
        if (ang > 180f) ang -= 360f;
        var v = axis.normalized * ang;

        var removedY = spec.YLocked ? v.y : 0f;
        var removedZ = spec.ZLocked ? v.z : 0f;
        removedDeg = Mathf.Sqrt(removedY * removedY + removedZ * removedZ);
        if (removedDeg <= LockedEpsilonDeg)
        {
            removedDeg = 0f;
            return local; // ALREADY COMMANDABLE — returned untouched, never rebuilt
        }

        var kept = new Vector3(v.x, spec.YLocked ? 0f : v.y, spec.ZLocked ? 0f : v.z);
        var keptAng = kept.magnitude;
        var swingNew = keptAng < 1e-6f ? Quaternion.identity : Quaternion.AngleAxis(keptAng, kept / keptAng);
        var deltaNew = swingNew * tw;
        return reference * (spec.Basis * deltaNew * Quaternion.Inverse(spec.Basis));
    }

    public static Quaternion ProjectToFeasible(JointLimitSpec spec, Quaternion zero, Quaternion local,
        int iterations = 8)
    {
        var clear = JointLimitPolicy.DeclaredClearance;
        var result = local;
        for (var it = 0; it < iterations; it++)
        {
            // Same decomposition Evaluate performs, so the projection and the verdict cannot disagree.
            var q = Quaternion.Inverse(spec.Basis) * (Quaternion.Inverse(zero) * result) * spec.Basis;
            if (q.w < 0f) q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
            var tw = new Quaternion(q.x, 0f, 0f, q.w);
            var m = Mathf.Sqrt(tw.x * tw.x + tw.w * tw.w);
            tw = m < 1e-8f ? Quaternion.identity : new Quaternion(tw.x / m, 0f, 0f, tw.w / m);
            var sw = q * Quaternion.Inverse(tw);
            if (sw.w < 0f) sw = new Quaternion(-sw.x, -sw.y, -sw.z, -sw.w);

            var twDeg = 2f * Mathf.Atan2(tw.x, tw.w) * Mathf.Rad2Deg;
            if (twDeg > 180f) twDeg -= 360f;
            if (twDeg < -180f) twDeg += 360f;
            if (spec.TwistLimited) twDeg = Mathf.Clamp(twDeg, spec.TwistLow * clear, spec.TwistHigh * clear);
            else twDeg = 0f;

            var swY = 2f * Mathf.Atan2(sw.y, sw.w) * Mathf.Rad2Deg;
            var swZ = 2f * Mathf.Atan2(sw.z, sw.w) * Mathf.Rad2Deg;
            if (spec.YLocked) swY = 0f; // a locked axis holds NO rotation, not a small one
            if (spec.ZLocked) swZ = 0f;
            swY = Mathf.Clamp(swY, -spec.YLimit * clear, spec.YLimit * clear);
            swZ = Mathf.Clamp(swZ, -spec.ZLimit * clear, spec.ZLimit * clear);

            var qNew = Quaternion.AngleAxis(swY, Vector3.up) * Quaternion.AngleAxis(swZ, Vector3.forward) *
                       Quaternion.AngleAxis(twDeg, Vector3.right);
            result = zero * (spec.Basis * qNew * Quaternion.Inverse(spec.Basis));
        }

        return result;
    }
}
