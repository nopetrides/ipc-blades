using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
///     Re-solves an authored right-arm pose so every Unity Humanoid DOF sits <b>comfortably interior</b> to its
///     limit, while honouring the declared observational tolerances (TODO ID 37).
///     <para>
///         <b>Tolerance hierarchy — Noah, 2026-08-07.</b> This is the missing rule-4 number that the earlier
///         escalation lacked, and it is what makes the problem feasible without touching the declared envelope:
///         <list type="bullet">
///             <item><b>Sword</b> — registered. Derived from calibrated physical geometry. Must not move at all.</item>
///             <item><b>Hand</b> — tight visual. Clearly visible landmark: preserve within ~4 cm.</item>
///             <item><b>Lower arm / elbow</b> — coarse visual. Perspective-estimated: preserve within ~16 cm.</item>
///             <item>
///                 <b>Upper arm, shoulder</b> — reconstructed. Insufficiently observed for a meaningful
///                 positional tolerance; determined by the articulated solution.
///             </item>
///         </list>
///     </para>
///     <para>
///         <b>Why every DOF must be interior, not merely legal.</b> A commanded pose resting <i>on</i> a joint
///         limit lets the joint's structural constraint carry load that never passed through the declared servo
///         torque. Under the servo rig (ID 26/36) the chain is the only actuator, so that is a measurement
///         defect of the same class as E6 — not a cosmetic issue. <see cref="Margin" /> is the required slack.
///     </para>
///     <para>
///         <b>Method.</b> Coordinate descent in Unity's muscle space, where limits are trivially satisfiable by
///         clamping, with <b>multi-start</b>. Multi-start is not optional: a single descent from the clamped
///         authored pose stalls in a local minimum that looks like "the envelope cannot reproduce this pose",
///         which nearly triggered an unnecessary limit deviation. Candidate poses are realised on a throwaway
///         clone and only the four right-arm local rotations are harvested, because
///         <c>SetHumanPose</c> cannot be used to author these dummies at all — a Get/Set round trip with
///         unchanged values rotates Hips by 90° and disturbs 45 bones, since ~20 body DOFs are already
///         out of range and get clamped on write.
///     </para>
///     <para>
///         Elbow and wrist are scored in <b>screen space</b> against the <c>BodyMarkers_*</c> landmarks, because
///         those markers all lie on the video plane (z = 2.67) and are 2D placements, not 3D measurements. The
///         <b>anatomical wrist</b> is additionally held in 3D at its authored position, which preserves the
///         wrist-to-sword geometry that actually carries the load.
///     </para>
///     <para>
///         <b>Palm orientation is deliberately NOT constrained</b> (Noah, 2026-08-07). The
///         <c>*_R_Palm_Hilt_Connection</c> marker records where the palm visually rests on the hilt, but it is
///         neither the wrist nor the hilt, so it really only indicates which way the palm should face. And the
///         palm plays no mechanical role: <b>the hand rigidbody is driven at the wrist joint, and the sword is
///         rigidly welded to that hand rigidbody at its existing relative transform</b> (<c>AttachSword</c> adds a
///         <c>FixedJoint</c> with no anchor set). Constraining palm facing would therefore let a purely cosmetic
///         reference dictate the wrist servo's target orientation — an unmeasured visual driving an actuator, the
///         same class of error as E6. Hand orientation is left to the articulated solution. <b>Do not "fix" this
///         by adding a palm or grip-frame term.</b>
///     </para>
/// </summary>
public static class PoseRepairSolver
{
    /// <summary>Required slack from every joint limit, in normalised muscle units. 0.85 = 15 % of range spare.</summary>
    private const float Margin = 0.85f;

    private const float HandTolMm = 40f;   // tight visual
    private const float ElbowTolMm = 160f; // coarse visual
    private const float WristTolMm = 40f;  // tight visual
    private const float ReachMax = 0.94f;  // keep off the kinematic singularity
    private const float ShoulderHtTolMm = 20f;

    private static readonly HumanBodyBones[] Arm =
    {
        HumanBodyBones.RightShoulder, HumanBodyBones.RightUpperArm,
        HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand
    };

    /// <summary>
    ///     Runs the same solve over <b>every</b> humanoid dummy whose right arm exceeds <see cref="Margin" /> —
    ///     not merely those that break a limit.
    ///     <para>
    ///         <b>Why the gate is the margin and not the limit</b> (Noah, 2026-08-07): the XRI dummies were
    ///         derived from the B-side rigs, so every dummy should pass through the same tooling. Gating on
    ///         "currently illegal" would leave the B-side poses at their authored values — measured as high as
    ///         <c>|v| = 0.949</c>, legal but only 5 % from a limit — while the repaired A-side poses sat at
    ///         ≤ 0.850. That asymmetry is exactly the kind of per-station divergence 04 §5 forbids for actuator
    ///         state, and under the servo rig a joint 5 % from its stop can still let the limit take load during
    ///         DRIVE. One rule, applied to all: every DOF ends inside the margin.
    ///     </para>
    /// </summary>
    [MenuItem("Tools/P3T/Solve pose repair (all dummies, margin sweep)")]
    public static void SolveAll()
    {
        var report = new StringBuilder();
        var seen = new HashSet<string>();
        foreach (var rig in Object.FindObjectsByType<ActiveArmRagdoll>(FindObjectsInactive.Include))
        {
            var an = rig.GetComponent<Animator>();
            if (an == null || !an.isHuman || an.avatar == null) continue;
            var station = rig.transform.parent != null ? rig.transform.parent.name : "(scene root)";
            if (!seen.Add(station + "/" + rig.name)) continue;
            if (!NeedsRepair(rig, an)) continue;
            report.AppendLine(Solve(rig, an, station));
        }

        if (report.Length == 0)
            report.AppendLine("Every dummy already holds all right-arm DOFs within the margin. Nothing to do.");
        Debug.Log("[PoseRepairSolver]\n" + report);
    }

    /// <summary>True when any right-arm DOF exceeds <see cref="Margin" /> — see <see cref="SolveAll" /> for why
    /// the threshold is the margin rather than the hard limit.</summary>
    private static bool NeedsRepair(ActiveArmRagdoll rig, Animator an)
    {
        var hp = new HumanPoseHandler(an.avatar, rig.transform);
        var pose = new HumanPose();
        hp.GetHumanPose(ref pose);
        hp.Dispose();
        foreach (var b in Arm)
            for (var d = 0; d < 3; d++)
            {
                var mi = HumanTrait.MuscleFromBone((int) b, d);
                if (mi >= 0 && Mathf.Abs(pose.muscles[mi]) > Margin) return true;
            }

        return false;
    }

    private static string Solve(ActiveArmRagdoll rig, Animator an, string station)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== " + station + " / " + rig.name + " ===");

        // Swords parented UNDER the hand bone would be dragged by the repair. Unparent, repair, reparent.
        var reparent = new List<(Transform t, Transform parent)>();
        var handBone = an.GetBoneTransform(HumanBodyBones.RightHand);
        foreach (var setup in Object.FindObjectsByType<SwordBladeSetup>(FindObjectsInactive.Include))
        {
            var t = setup.transform;
            if (!t.IsChildOf(handBone)) continue;
            reparent.Add((t, t.parent));
            Undo.SetTransformParent(t, rig.transform.parent, "Pose repair: detach sword");
            sb.AppendLine("  sword '" + t.name + "' temporarily unparented from the hand bone");
        }

        // record every sword's world transform to prove none moved
        var swordPos = new Dictionary<Transform, Vector3>();
        var swordRot = new Dictionary<Transform, Quaternion>();
        foreach (var setup in Object.FindObjectsByType<SwordBladeSetup>(FindObjectsInactive.Include))
        {
            swordPos[setup.transform] = setup.transform.position;
            swordRot[setup.transform] = setup.transform.rotation;
        }

        // The authored RIGHT_HAND position — i.e. the ANATOMICAL WRIST, not the grip.
        // Noah, 2026-08-07: the hand rigidbody is driven at the wrist joint, and the sword is rigidly welded
        // to that hand rigidbody at its existing relative transform (AttachSword adds a FixedJoint with no
        // anchor set). So the palm plays no mechanical role, and holding the wrist is what preserves the
        // load path into the contact.
        var wristTarget = handBone.position;
        var shoulderT = an.GetBoneTransform(HumanBodyBones.RightUpperArm);
        var elbowT = an.GetBoneTransform(HumanBodyBones.RightLowerArm);
        var lShoulderT = an.GetBoneTransform(HumanBodyBones.LeftUpperArm);
        var authoredHt = (shoulderT.position.y - lShoulderT.position.y) * 1000f;
        var armLen = Vector3.Distance(shoulderT.position, elbowT.position) +
                     Vector3.Distance(elbowT.position, handBone.position);

        var side = rig.name.Contains("_A_") ? "A_" : "B_";
        Transform mkElbow = null, mkWrist = null;
        Camera cam = null;
        if (rig.transform.parent != null)
        {
            foreach (Transform c in rig.transform.parent)
            {
                if (c.name.StartsWith("BodyMarkers"))
                    foreach (Transform m in c)
                    {
                        if (m.name == side + "R_Elbow") mkElbow = m;
                        else if (m.name == side + "R_Wrist") mkWrist = m;
                    }

                var cc = c.GetComponent<Camera>();
                if (cc != null && c.name.StartsWith("ViewCam")) cam = cc;
            }
        }

        var haveScreen = cam != null && mkElbow != null && mkWrist != null;
        sb.AppendLine(haveScreen
            ? "  screen landmarks: " + side + "R_Elbow / " + side + "R_Wrist via " + cam.name
            : "  no markers or view camera for this rig — solving on grip + limits only");

        var tanV = Mathf.Tan(42.99059f * 0.5f * Mathf.Deg2Rad);
        var tanH = tanV * 16f / 9f;
        var cp = cam != null ? cam.transform.position : Vector3.zero;

        System.Func<Vector3, Vector3, float> screenMm = (bone, marker) =>
        {
            var dz1 = bone.z - cp.z;
            var dz2 = marker.z - cp.z;
            var a = new Vector2((bone.x - cp.x) / dz1 / tanH, (bone.y - cp.y) / dz1 / tanV);
            var b = new Vector2((marker.x - cp.x) / dz2 / tanH, (marker.y - cp.y) / dz2 / tanV);
            var d = a - b;
            return Mathf.Sqrt(Mathf.Pow(d.x * tanH * dz1 * 1000f, 2) + Mathf.Pow(d.y * tanV * dz1 * 1000f, 2));
        };

        // realiser + evaluator clones, parented so their WORLD transforms match the original
        var realizer = Object.Instantiate(rig.gameObject, rig.transform.parent);
        realizer.name = "__PRS_R";
        realizer.transform.SetPositionAndRotation(rig.transform.position, rig.transform.rotation);
        var evaluator = Object.Instantiate(rig.gameObject, rig.transform.parent);
        evaluator.name = "__PRS_E";
        evaluator.transform.SetPositionAndRotation(rig.transform.position, rig.transform.rotation);

        var result = new Quaternion[4];
        var summary = "";
        try
        {
            var rAn = realizer.GetComponent<Animator>();
            var eAn = evaluator.GetComponent<Animator>();
            var hp = new HumanPoseHandler(rAn.avatar, realizer.transform);
            var pose = new HumanPose();
            hp.GetHumanPose(ref pose);
            var authored = new HumanPose();
            hp.GetHumanPose(ref authored);

            var rB = new Transform[4];
            var eB = new Transform[4];
            for (var i = 0; i < 4; i++)
            {
                rB[i] = rAn.GetBoneTransform(Arm[i]);
                eB[i] = eAn.GetBoneTransform(Arm[i]);
            }

            var eSh = eAn.GetBoneTransform(HumanBodyBones.RightUpperArm);
            var eEl = eAn.GetBoneTransform(HumanBodyBones.RightLowerArm);
            var eWr = eAn.GetBoneTransform(HumanBodyBones.RightHand);
            var eLSh = eAn.GetBoneTransform(HumanBodyBones.LeftUpperArm);

            var idx = new List<int>();
            foreach (var b in Arm)
                for (var d = 0; d < 3; d++)
                {
                    var mi = HumanTrait.MuscleFromBone((int) b, d);
                    if (mi >= 0) idx.Add(mi);
                }

            var v0 = new Dictionary<int, float>();
            foreach (var mi in idx) v0[mi] = Mathf.Clamp(authored.muscles[mi], -1f, 1f);

            System.Func<float[], float> cost = mv =>
            {
                System.Array.Copy(mv, pose.muscles, mv.Length);
                hp.SetHumanPose(ref pose);
                for (var i = 0; i < 4; i++) eB[i].localRotation = rB[i].localRotation;
                var hand = Vector3.Distance(eWr.position, wristTarget) * 1000f;
                var c = hand;
                if (haveScreen)
                {
                    var elb = screenMm(eEl.position, mkElbow.position);
                    var wri = screenMm(eWr.position, mkWrist.position);
                    c += 0.05f * elb;
                    if (elb > ElbowTolMm) c += (elb - ElbowTolMm) * 50f;
                    if (wri > WristTolMm) c += (wri - WristTolMm) * 50f;
                }

                var reach = Vector3.Distance(eSh.position, wristTarget) / armLen;
                if (reach > ReachMax) c += (reach - ReachMax) * 50000f;
                var ht = Mathf.Abs((eSh.position.y - eLSh.position.y) * 1000f - authoredHt);
                if (ht > ShoulderHtTolMm) c += (ht - ShoulderHtTolMm) * 50f;
                foreach (var mi in idx)
                    if (Mathf.Abs(mv[mi]) > Margin)
                        c += (Mathf.Abs(mv[mi]) - Margin) * 50000f;
                return c;
            };

            var rng = new System.Random(1234567);
            var best = float.MaxValue;
            float[] bm = null;
            for (var rs = 0; rs < 120; rs++)
            {
                var m = new float[pose.muscles.Length];
                System.Array.Copy(authored.muscles, m, m.Length);
                foreach (var mi in idx)
                    m[mi] = rs == 0
                        ? Mathf.Clamp(v0[mi], -Margin, Margin)
                        : Mathf.Clamp(v0[mi] + (float) (rng.NextDouble() * 1.6 - 0.8), -Margin, Margin);
                var c = cost(m);
                for (var st = 0.45f; st > 0.0005f; st *= 0.72f)
                    for (var p = 0; p < 3; p++)
                        foreach (var mi in idx)
                            foreach (var s in new[] {st, -st})
                            {
                                var o = m[mi];
                                m[mi] = Mathf.Clamp(o + s, -1f, 1f);
                                var cc = cost(m);
                                if (cc < c - 1e-5f) c = cc;
                                else m[mi] = o;
                            }

                if (c < best)
                {
                    best = c;
                    bm = (float[]) m.Clone();
                }
            }

            cost(bm);
            for (var i = 0; i < 4; i++)
            {
                eB[i].localRotation = rB[i].localRotation;
                result[i] = rB[i].localRotation;
            }

            var worst = 0f;
            foreach (var mi in idx) worst = Mathf.Max(worst, Mathf.Abs(bm[mi]));
            summary = string.Format(CultureInfo.InvariantCulture,
                "  hand {0:F1} mm/{1:F0}   elbow {2}   reach {3:P1}/{4:P0}   largest |v| {5:F3}/{6:F2}",
                Vector3.Distance(eWr.position, wristTarget) * 1000f, HandTolMm,
                haveScreen
                    ? screenMm(eEl.position, mkElbow.position).ToString("F1") + " mm/" + ElbowTolMm.ToString("F0")
                    : "n/a",
                Vector3.Distance(eSh.position, wristTarget) / armLen, ReachMax, worst, Margin);
            foreach (var b in Arm)
                for (var d = 0; d < 3; d++)
                {
                    var mi = HumanTrait.MuscleFromBone((int) b, d);
                    if (mi < 0) continue;
                    var mn = HumanTrait.GetMuscleDefaultMin(mi);
                    var mx = HumanTrait.GetMuscleDefaultMax(mi);
                    summary += string.Format(CultureInfo.InvariantCulture, "\n    {0,-14} {1,-30} {2,7:F1} -> {3,7:F1} deg  v={4,6:F3}",
                        b, HumanTrait.MuscleName[mi],
                        v0[mi] < 0 ? -v0[mi] * mn : v0[mi] * mx,
                        bm[mi] < 0 ? -bm[mi] * mn : bm[mi] * mx, bm[mi]);
                }

            hp.Dispose();
        }
        finally
        {
            Object.DestroyImmediate(realizer);
            Object.DestroyImmediate(evaluator);
        }

        // apply
        for (var i = 0; i < 4; i++)
        {
            var t = an.GetBoneTransform(Arm[i]);
            Undo.RecordObject(t, "Pose repair " + rig.name);
            t.localRotation = Quaternion.Normalize(result[i]);
            EditorUtility.SetDirty(t);
        }

        sb.AppendLine(summary);

        // reparent swords, then prove nothing moved
        foreach (var (t, parent) in reparent)
        {
            Undo.SetTransformParent(t, parent, "Pose repair: reattach sword");
            sb.AppendLine("  sword '" + t.name + "' reparented to " + parent.name);
        }

        float maxP = 0f, maxR = 0f;
        foreach (var kv in swordPos)
        {
            if (kv.Key == null) continue;
            maxP = Mathf.Max(maxP, Vector3.Distance(kv.Key.position, kv.Value) * 1000f);
            maxR = Mathf.Max(maxR, Quaternion.Angle(kv.Key.rotation, swordRot[kv.Key]));
        }

        sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
            "  sword check: MAX dPos {0:F6} mm, MAX dRot {1:F6} deg -> {2}",
            maxP, maxR, maxP < 1e-3f && maxR < 1e-3f ? "PASS" : "*** FAIL ***"));
        return sb.ToString();
    }
}
