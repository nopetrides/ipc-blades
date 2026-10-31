using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
///     Verifies that every authored pose is REACHABLE under the station-independent Unity Humanoid default
///     motion limits, before those limits are frozen into the active ragdoll (TODO ID 36).
///     <para>
///         <b>Per-DOF, never the total quaternion angle.</b> A rotation composed across several permitted DOFs
///         can exceed the numerical limit of any single axis without violating any of them, so a combined
///         angle cannot decide PASS/FAIL. This audit evaluates each humanoid DOF independently in the muscle
///         basis — the same basis the <c>ConfigurableJoint</c> axes are assigned from in
///         the Week-5 humanoid joint-limits record (removed; see git history if required) §5.
///     </para>
///     <para>
///         <b>Reference is muscle-zero</b>, the station-independent joint reference. An authored station pose
///         is a TARGET relative to that reference; it never redefines the joint zero or its limits.
///     </para>
///     <para>
///         <b>Instrument validity.</b> <c>HumanPoseHandler.GetHumanPose</c> returns each DOF as a normalised
///         muscle value where ±1 IS the limit, and it does <b>not</b> clamp: verified 2026-08-07 by forcing a
///         bone to 130% and 70% of a limit and reading back exactly 1.3000 and 0.7000. So <c>|v| &gt; 1</c> is
///         a sound out-of-range test, and degrees convert as <c>v&lt;0 ? -v*min : v*max</c>. Had it clamped,
///         every pose would have falsely passed.
///     </para>
///     <para>
///         ⚠️ <b>Read-only by construction.</b> This audit never calls <c>SetHumanPose</c> and never writes a
///         bone rotation — the authored poses are Noah's and must not be touched by an instrument.
///     </para>
///     <para>
///         ⚠️ <b>Never widen a limit to make an authored pose pass.</b> A FAIL means the pose is unreachable
///         under the declared actuator specification. Changing either the authored target or the declared
///         limits is a separate design decision requiring its own justification.
///     </para>
/// </summary>
public static class HumanoidLimitAudit
{
    /// <summary>
    ///     The seven upper-body joints of the ID 36 chain, root-up. A joint sits on a bone and represents that
    ///     bone's PROXIMAL joint, so RightUpperArm = shoulder, RightLowerArm = elbow, RightHand = wrist, and
    ///     RightShoulder is the clavicle/scapula girdle above the shoulder proper.
    /// </summary>
    private static readonly HumanBodyBones[] Chain =
    {
        HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.UpperChest,
        HumanBodyBones.RightShoulder, HumanBodyBones.RightUpperArm,
        HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand
    };

    // Application.dataPath is <repo>/Unity/MBC/Assets, so the thesis root is three levels up.
    private const string OutDir = "../../../Workspace/Week5";

    [MenuItem("Tools/P3T/Audit humanoid joint limits (per-DOF)")]
    public static void Run()
    {
        var rows = new List<string>
        {
            "station,dummy,bone,dof_index,dof_name,authored_deg,unity_min_deg,unity_max_deg,muscle_value,locked,result"
        };
        var report = new StringBuilder();
        int pass = 0, fail = 0, locked = 0;
        var failures = new List<string>();
        var stationFailed = new SortedDictionary<string, List<string>>();
        var seen = new HashSet<string>();

        foreach (var r in Object.FindObjectsByType<ActiveArmRagdoll>(FindObjectsInactive.Include))
        {
            var an = r.GetComponent<Animator>();
            if (an == null || !an.isHuman || an.avatar == null) continue;
            var station = r.transform.parent != null ? r.transform.parent.name : "(scene root)";
            if (!seen.Add(station + "/" + r.name)) continue;

            var hp = new HumanPoseHandler(an.avatar, r.transform);
            var pose = new HumanPose();
            hp.GetHumanPose(ref pose); // READ ONLY — never SetHumanPose in an audit
            hp.Dispose();

            report.AppendLine();
            report.AppendLine(station + " / " + r.name);
            foreach (var b in Chain)
            for (var dof = 0; dof < 3; dof++)
            {
                var mi = HumanTrait.MuscleFromBone((int) b, dof);
                if (mi < 0)
                {
                    locked++;
                    rows.Add(string.Join(",", station, r.name, b.ToString(), dof.ToString(),
                        "(absent)", "", "", "", "TRUE", "PASS"));
                    report.AppendLine(string.Format("  {0,-14} dof{1}  {2,-32} {3}", b, dof,
                        "absent in Unity Humanoid", "Locked   PASS"));
                    continue;
                }

                var v = pose.muscles[mi];
                var mn = HumanTrait.GetMuscleDefaultMin(mi);
                var mx = HumanTrait.GetMuscleDefaultMax(mi);
                var deg = v < 0f ? -v * mn : v * mx;
                var ok = Mathf.Abs(v) <= 1f;
                if (ok)
                {
                    pass++;
                }
                else
                {
                    fail++;
                    failures.Add(station + " / " + r.name + " / " + b + " / " + HumanTrait.MuscleName[mi] +
                                 string.Format(CultureInfo.InvariantCulture,
                                     "  authored {0:F1} deg, allowed [{1},{2}], v={3:F3}", deg, mn, mx, v));
                    if (!stationFailed.ContainsKey(station)) stationFailed[station] = new List<string>();
                    stationFailed[station].Add(r.name + " / " + b + " / " + HumanTrait.MuscleName[mi]);
                }

                rows.Add(string.Join(",", station, r.name, b.ToString(), dof.ToString(),
                    HumanTrait.MuscleName[mi].Replace(",", ""),
                    F(deg), F(mn), F(mx), F(v), "FALSE", ok ? "PASS" : "FAIL"));
                report.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "  {0,-14} dof{1}  {2,-32} {3,8:F1} deg  allowed [{4,4},{5,5}]  v={6,6:F3}  {7}",
                    b, dof, HumanTrait.MuscleName[mi], deg, mn, mx, v, ok ? "PASS" : "**FAIL**"));
            }
        }

        var header = new StringBuilder();
        header.AppendLine("PER-DOF HUMANOID LIMIT AUDIT — " + System.DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
        header.AppendLine("Reference: Unity Humanoid muscle-zero. FAIL when |muscleValue| > 1.");
        header.AppendLine("VERDICT: " + (fail == 0 ? "PASS" : "FAIL") +
                          "   (" + pass + " PASS, " + fail + " FAIL, " + locked + " absent/Locked)");
        if (fail > 0)
        {
            header.AppendLine();
            header.AppendLine("STATIONS FAILING (a station passes only if EVERY authored DOF is in range):");
            foreach (var kv in stationFailed)
                header.AppendLine("  " + kv.Key + "  — " + kv.Value.Count + " failing DOF(s)");
            header.AppendLine();
            header.AppendLine("FAILURES:");
            foreach (var f in failures) header.AppendLine("  " + f);
        }

        var text = header + report.ToString();
        Debug.Log("[HumanoidLimitAudit]\n" + text);

        try
        {
            var dir = Path.GetFullPath(Path.Combine(Application.dataPath, OutDir));
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "HumanoidLimitAudit.csv"), string.Join("\n", rows));
            File.WriteAllText(Path.Combine(dir, "HumanoidLimitAudit.txt"), text);
            Debug.Log("[HumanoidLimitAudit] wrote HumanoidLimitAudit.csv + .txt to " + dir);
        }
        catch (System.Exception e)
        {
            Debug.LogError("[HumanoidLimitAudit] could not write the record: " + e.Message);
        }
    }

    private static string F(float f)
    {
        return f.ToString("R", CultureInfo.InvariantCulture);
    }
}
