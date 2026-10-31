using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using FIMSpace.FProceduralAnimation;
using UnityEngine;

/// <summary>
///     Writes one row per fixed step of the CONTACT and DRIVE window: where each body was, where the
///     contact sat on each blade, whether the surfaces were sliding, and which shapes carried the pair.
/// </summary>
/// <remarks>
///     <para>
///         <b>The gap this closes.</b> Every <c>s_*</c> quantity in the sealed record is kept as ten
///         summary statistics and the per-step samples are discarded. That is enough to say the contact
///         travelled 547 mm along blade A end to end, and not enough to say whether it did so steadily,
///         in a few jumps between stationary dwells, or with reversals that never passed the endpoints.
///         <c>sd</c> is consistent with all three. This probe keeps the samples.
///     </para>
///     <para>
///         <b>Two questions it exists to answer.</b> First, how much of the driven root motion the sword
///         does NOT follow — the lag. The chain is: animated root to the Hips anchor, which is KINEMATIC
///         and therefore exact; Hips through eight spring-driven joints at 5000/40 to the hand, which is
///         where all the compliance lives; hand to sword by a rigid <c>FixedJoint</c>. Recording the root,
///         the hand and the sword separately is what separates arm compliance from anything at the weld.
///         Second, whether the contact dwells or keeps migrating, which needs the per-step contact
///         position in each blade's own frame rather than in world space.
///     </para>
///     <para>
///         <b>Lag is not attributable on its own.</b> Root minus sword displacement is arm compliance and
///         contact resistance combined. A 5000-spring chain accelerating a 1.58 kg blade lags with no
///         contact at all. This probe records the quantity; what to compare it against is a design
///         decision, not something the number settles.
///     </para>
///     <para>
///         <b>Migration and sliding are different quantities.</b> Migration is where along each blade the
///         touch is. Sliding is the relative tangential motion of the two surfaces, which is what friction
///         acts on. They coincide when the blades move as one body and diverge otherwise, so both are
///         written and neither is derived from the other.
///     </para>
///     <para>
///         <b>No thresholds, and no mechanism named.</b> Every column is a position, a speed, a force or a
///         name. Nothing here decides what counts as a dwell, and nothing labels a friction regime — the
///         project has separate static and dynamic coefficients but no event-level instrument that can say
///         which regime produced a given interval. Dwell detection and interval classification happen in
///         analysis, against a rule the researcher declares.
///     </para>
///     <para>
///         <b>Nothing is recomputed.</b> Contact position comes from <see cref="RqContactSideProbe" />,
///         sliding and force from <see cref="RqStationContactRecorder" />, shape participation from
///         <see cref="RqContactPatchProbe" />. This probe reads and writes; it derives no measurement of
///         its own, so it cannot disagree with the sealed record.
///     </para>
///     <para>This component writes nothing to the simulation.</para>
/// </remarks>
[DisallowMultipleComponent]
[DefaultExecutionOrder(170)]
public sealed class RqStepTraceProbe : MonoBehaviour
{
    [Tooltip("Contact position source. Left empty, the one on this GameObject is used.")]
    [SerializeField] private RqContactSideProbe sideProbe;

    [Tooltip("Sliding, force and separation source. Left empty, the one on this GameObject is used.")]
    [SerializeField] private RqStationContactRecorder recorder;

    [Tooltip("Shape-participation source. Left empty, the one on this GameObject is used.")]
    [SerializeField] private RqContactPatchProbe patch;

    [Header("Window")]
    [Tooltip("Animator State that begins CONTACT. Recording starts here so DRIVE onset has a pre-roll.")]
    [SerializeField] private int contactState = 3;

    [Tooltip("Animator State that is DRIVE. t_since_drive_s is zeroed on the first step at this state.")]
    [SerializeField] private int driveState = 4;

    [Tooltip("Steps to keep recording after the window closes. ALSO closes the window when the runner " +
             "ends and leaves the animator parked in DRIVE, which is what happens on a runner-driven run.")]
    [SerializeField] private int endGraceSteps = 90;

    [Tooltip("Close the window when the station runner reports Finished, rather than waiting for the " +
             "animator to leave DRIVE. The runner ends parked at State 4, so without this the window " +
             "never closes and no file is written.")]
    [SerializeField] private bool closeOnRunnerFinished = true;

    [Tooltip("Hard cap on rows, so a forgotten Play session cannot fill the disk.")]
    [SerializeField] private int maxRows = 20000;

    [Header("Output")]
    /// <remarks>
    ///     BLANK by default, deliberately. It used to ship an absolute path from one machine, which
    ///     both leaked a folder structure into a public repository and silently wrote another
    ///     machine's runs into a directory that did not describe them. Blank cannot do either, and
    ///     the write path below refuses with a named error rather than throwing on
    ///     CreateDirectory("").
    /// </remarks>
    [SerializeField] private string outputDirectory = "";

    [Tooltip("Mirrors the sealed record's setName so a trace can be tied to the run tranche.")]
    [SerializeField] private string setName = "PILOT_AllStations_90Hz";

    [SerializeField] private bool writeCsvOnWindowEnd = true;

    private readonly List<string> rows = new List<string>(4096);
    private RqStationRunner runner;

    /// <summary>
    ///     Whether an ARMED run was in progress when this window opened, latched at <c>Begin</c>.
    ///     <para>
    ///         Capture is not recording. This probe samples whenever the animator is in the Contact or Drive
    ///         window, which happens on an Auto cycle, on a hand-driven State from the panel, and in free
    ///         play — none of which is a run. Before 2026-08-29 every one of those wrote a CSV, so pressing
    ///         Play produced files in the channel root that no set had declared and no ledger row covered.
    ///     </para>
    ///     <para>
    ///         There are exactly two arming paths, and both are <c>RqStationRunner</c>: <c>armKey</c>
    ///         (manual, mid-play) and <c>armWhenReady</c> (automatic, once initialization completes). It is
    ///         latched at <c>Begin</c> rather than read at <c>End</c> because the runner has already
    ///         reported <c>Finished</c> by the time the window closes, so reading it late would refuse every
    ///         armed run.
    ///     </para>
    /// </summary>
    private bool armedForThisWindow;
    private RA2AttachableObject swordA, swordB;
    private Animator animA, animB;
    private Transform rootA, rootB;
    private bool recording, sawDrive;
    private float driveStartTime;
    private int offSteps, runIndex;

    /// <summary>True while a window is open.</summary>
    public bool Recording => recording;

    /// <summary>Rows captured in the open window, or the last closed one.</summary>
    public int Rows => rows.Count;

    /// <summary>Why the probe is not recording, when it is not.</summary>
    public string RefusalReason { get; private set; } = "";

    private void Awake()
    {
        if (sideProbe == null) sideProbe = GetComponent<RqContactSideProbe>();
        if (recorder == null) recorder = GetComponent<RqStationContactRecorder>();
        if (patch == null) patch = GetComponent<RqContactPatchProbe>();
        runner = GetComponent<RqStationRunner>();
        Resolve();
    }

    /// <summary>Everything from this probe's own Station_ root, so no station name is baked in.</summary>
    private void Resolve()
    {
        var root = transform;
        while (root != null && !root.name.StartsWith("Station_")) root = root.parent;
        if (root == null) return;

        foreach (var att in root.GetComponentsInChildren<RA2AttachableObject>(true))
        {
            if (swordA == null && att.gameObject.name.StartsWith("Sword_A")) swordA = att;
            else if (swordB == null && att.gameObject.name.StartsWith("Sword_B")) swordB = att;
        }

        foreach (var an in root.GetComponentsInChildren<Animator>(true))
        {
            if (animA == null && an.gameObject.name.StartsWith("Dummy_A")) { animA = an; rootA = an.transform; }
            else if (animB == null && an.gameObject.name.StartsWith("Dummy_B")) { animB = an; rootB = an.transform; }
        }
    }

    /// <summary>The generated proxy body when it exists, else the authored sword.</summary>
    private Transform Proxy(RA2AttachableObject att)
    {
        if (att == null) return null;
        var g = att.GeneratedPhysicsObject;
        return g != null ? g.transform : att.transform;
    }

    /// <summary>The hand bone the proxy is welded to — the last DRIVEN body before the rigid weld.</summary>
    private static Transform HandOf(Transform proxy)
    {
        return proxy == null ? null : proxy.parent;
    }

    /// <summary>
    ///     The ANIMATED Hips — the servo target the clip actually writes, and the real driver of the run.
    /// </summary>
    /// <remarks>
    ///     <b>Not the dummy root.</b> IR-4 measured the dummy root transform as CONSTANT across a whole
    ///     drive window, at both step 0 and step 1223, because every Animator here runs
    ///     <c>applyRootMotion = false</c>. The root GameObject does not translate; the clip animates the
    ///     Hips bone inside <c>Skeleton</c>, and the kinematic ragdoll anchor follows that. A lag measured
    ///     against the root would be measured against something that never moved.
    /// </remarks>
    private static Transform AnimHipsOf(Transform dummyRoot)
    {
        return dummyRoot == null ? null : dummyRoot.Find("Skeleton/Hips");
    }

    /// <summary>
    ///     The ANIMATED twin of a simulated bone: the same-named transform under the clip-driven
    ///     <c>Skeleton</c> hierarchy.
    ///     <para>
    ///         Added 2026-08-29 so the lag chain is a lag chain. Only <c>animHips</c> was logged, so
    ///         <c>anchor - hand</c> spanned 63-67 mm and read like arm compliance when most of it is simply the
    ///         clip articulating the arm: the animated HAND target had moved and nothing recorded where to.
    ///         With this column, <c>animHand - hand</c> is the servo's actual tracking error and
    ///         <c>anchor - hand</c> can stop being mistaken for one.
    ///     </para>
    ///     <para>
    ///         Matched BY NAME against the physics bone rather than by a hard-coded bone path, so the pair is
    ///         guaranteed to be the same bone on both skeletons. Null when no twin is found, which writes
    ///         blanks rather than substituting anything.
    ///     </para>
    /// </summary>
    private static Transform AnimTwinOf(Transform dummyRoot, Transform physicsBone)
    {
        if (dummyRoot == null || physicsBone == null) return null;
        var skeleton = dummyRoot.Find("Skeleton");
        if (skeleton == null) return null;

        foreach (var t in skeleton.GetComponentsInChildren<Transform>(true))
            if (t.name == physicsBone.name)
                return t;

        return null;
    }

    /// <summary>
    ///     The ragdoll anchor body — the KINEMATIC Hips at the top of the simulated chain. It follows the
    ///     animated Hips exactly, so anchor-minus-hand is the compliance of the eight spring joints.
    /// </summary>
    private static Transform AnchorHipsOf(Transform proxy)
    {
        if (proxy == null) return null;
        var container = proxy.root;
        return container == null ? null : container.Find("Hips");
    }

    private int CurrentState()
    {
        if (animA != null) return animA.GetInteger("State");
        if (animB != null) return animB.GetInteger("State");
        return -1;
    }

    private void FixedUpdate()
    {
        if (swordA == null || swordB == null || animA == null) Resolve();
        if (sideProbe == null || recorder == null)
        {
            RefusalReason = "no RqContactSideProbe or RqStationContactRecorder on this station.";
            return;
        }

        var pa = Proxy(swordA);
        var pb = Proxy(swordB);
        if (pa == null || pb == null) { RefusalReason = "a generated proxy is not present yet."; return; }
        RefusalReason = "";

        int state = CurrentState();
        bool inWindow = state == contactState || state == driveState;
        bool runnerDone = closeOnRunnerFinished && runner != null && runner.Finished;

        if (inWindow && !recording && !runnerDone) Begin();
        if (!recording) return;

        if (state == driveState && !sawDrive) { sawDrive = true; driveStartTime = Time.time; }

        // The runner ends parked in DRIVE, so leaving the window is not a reliable close signal on a
        // runner-driven run. Finished is.
        if (!inWindow || runnerDone)
        {
            offSteps++;
            if (offSteps > endGraceSteps) { End(); return; }
        }
        else offSteps = 0;

        if (rows.Count >= maxRows) return;

        var fa = sideProbe.FrameA;
        var fb = sideProbe.FrameB;
        RqStationContactRecorder.Record c = recorder.Current;

        var handA = HandOf(pa);
        var handB = HandOf(pb);

        var sb = new StringBuilder(320);
        sb.Append(rows.Count).Append(',')
          .Append(F(Time.time)).Append(',')
          .Append(F(sawDrive ? Time.time - driveStartTime : float.NaN)).Append(',')
          .Append(state).Append(',');

        // The lag chain, four points, because the compliance is not where it looks.
        // root: does NOT move - applyRootMotion is false. Kept as evidence of that, not as a driver.
        // animHips: the clip's actual output, and the real driver.
        // anchor: the kinematic ragdoll Hips, which follows animHips exactly.
        // hand: after eight spring joints at 5000/40 - this is where the give is.
        // sword: after the rigid FixedJoint - should track hand to sub-millimetre.
        Append(sb, rootA); Append(sb, rootB);
        Append(sb, AnimHipsOf(rootA)); Append(sb, AnimHipsOf(rootB));
        Append(sb, AnchorHipsOf(pa)); Append(sb, AnchorHipsOf(pb));
        Append(sb, handA); Append(sb, handB);
        Append(sb, pa); Append(sb, pb);

        // The weld is a FixedJoint with all six degrees of freedom locked, so its constraint error has a
        // translational half AND a rotational half. The position columns above give the first; without these
        // the second is not computable from any sealed channel. Rotations are of the SAME bodies the position
        // columns use - hand, then the generated proxy - so a hand-to-sword deviation is one pose convention
        // throughout and not a mixture of two.
        AppendRot(sb, handA); AppendRot(sb, handB);
        AppendRot(sb, pa); AppendRot(sb, pb);
        AppendWeld(sb, pa); AppendWeld(sb, pb);

        // The clip's HAND target. animHips alone made anchor-minus-hand look like compliance when most of
        // that 63-67 mm is the clip articulating the arm. animHand minus hand is the tracking error.
        Append(sb, AnimTwinOf(rootA, handA)); Append(sb, AnimTwinOf(rootB, handB));

        // Contact position in each blade's own frame. Metres -> millimetres at write time only.
        bool haveSide = sideProbe.Resolved && sideProbe.HasReading && fa.Valid && fb.Valid;
        sb.Append(haveSide ? "1" : "0").Append(',')
          .Append(haveSide ? sideProbe.LeadPair : "").Append(',')
          .Append(haveSide && sideProbe.Inverted ? "1" : "0").Append(',');

        // step_min_separation_mm is the LIVE per-step value, from RqContactPatchProbe: a signed minimum
        // over the contact points actually delivered this step. It is the column to read when asking how
        // far apart the blades were at a given step, and it is the one contact-geometry quantity that
        // lives ONLY here.
        //
        // TWELVE columns were removed from this file in REPEATABILITY4 - along_a/b_mm, across_a/b_mm,
        // closest_mm, pair, overlap, run_min_separation_mm, tangential_speed_mps, slip_path_mm,
        // normal_force_n and contact_count. They were duplicates: the drive recorder samples the same
        // quantities into its own series, and the two agreed to within 7.3e-5 across all fifty runs of
        // REPEATABILITY3 - the whole residual being the print precision of the file that rounds to four
        // decimals. Read them from <stem>_steps.csv, which carries them per step from that one sampling.
        //
        // Among them run_min_separation_mm was a RUN-CUMULATIVE running minimum, not a per-step reading,
        // and it sat on a per-step row where it invited exactly the statistic it cannot support. Its
        // successor in the series file is named s_run_min_separation_mm and carries the same warning.
        sb.Append(patch != null && patch.Resolved ? F(patch.MinSeparationM * 1000f) : "").Append(',');

        // Shape participation, so a duration difference can be checked against the hull route.
        sb.Append(patch != null ? patch.ShapePairs.ToString() : "")
          .Append(",\"").Append(patch != null ? patch.ShapePairSummary : "").Append('"');

        rows.Add(sb.ToString());
    }

    private static string F(float v) =>
        float.IsNaN(v) ? "NaN" : v.ToString("R", CultureInfo.InvariantCulture);

    private static void Append(StringBuilder sb, Transform t)
    {
        if (t == null) { sb.Append(",,"); sb.Append(','); return; }
        var p = t.position;
        sb.Append(F(p.x)).Append(',').Append(F(p.y)).Append(',').Append(F(p.z)).Append(',');
    }

    /// <summary>
    ///     World rotation as a raw quaternion, four columns. Written RAW and never as a relative or delta
    ///     rotation: the hand-to-sword relative rotation is the grip orientation, which is NOT zero — measured
    ///     80.87 deg on A and 89.23 deg on B — so the weld constraint error is the deviation of that relative
    ///     pose from its value at the window's first step, not the angle itself. Computing it here would put a
    ///     derived number where the original belongs, and a derived number that has replaced its original
    ///     cannot be audited. Every fixed step is present, so the t0 row to difference against is always there.
    /// </summary>
    private static void AppendRot(StringBuilder sb, Transform t)
    {
        if (t == null) { sb.Append(",,,"); sb.Append(','); return; }
        var q = t.rotation;
        sb.Append(F(q.x)).Append(',').Append(F(q.y)).Append(',')
          .Append(F(q.z)).Append(',').Append(F(q.w)).Append(',');
    }

    /// <summary>
    ///     The weld's constraint reaction, force then torque magnitude.
    ///     <para>
    ///         Readable on THIS joint and only this joint. The arm's <c>ConfigurableJoint</c>s have every
    ///         angular degree of freedom set to Free, so their <c>currentTorque</c> reports a limit reaction and
    ///         delivered servo torque stays the closed, unobservable branch. A <c>FixedJoint</c> locks all six
    ///         degrees of freedom, so a constraint reaction is exactly the right quantity and it is the one
    ///         actuator-boundary value on this rig that is measured rather than computed.
    ///     </para>
    ///     <para>
    ///         BLANK, never zero, when no <c>FixedJoint</c> is found on the body passed in. Zero is a reading;
    ///         blank is the absence of one, and a wrong-but-plausible zero in a constraint column is precisely
    ///         the failure this project keeps paying for.
    ///     </para>
    /// </summary>
    private static void AppendWeld(StringBuilder sb, Transform body)
    {
        var fj = body == null ? null : body.GetComponent<FixedJoint>();
        if (fj == null) { sb.Append(','); sb.Append(','); return; }
        sb.Append(F(fj.currentForce.magnitude)).Append(',')
          .Append(F(fj.currentTorque.magnitude)).Append(',');
    }

    private void Begin()
    {
        recording = true;
        armedForThisWindow = runner != null && runner.Running;
        sawDrive = false;
        offSteps = 0;
        rows.Clear();
    }

    private void End()
    {
        recording = false;
        runIndex++;

        // Captured, deliberately not written. The rows stay in memory for the readout; only an ARMED run
        // earns a file. Logged rather than silent, because a window that produced nothing and a window
        // that was never opened must not look the same in the console.
        if (!armedForThisWindow)
        {
            Debug.Log("[StepTrace] " + transform.name + ": window closed with " + rows.Count +
                      " sample(s) but NO ARMED RUN, so no CSV was written. Capture is not recording — arm " +
                      "the station with its armKey or armWhenReady to write one.", this);
            return;
        }

        if (writeCsvOnWindowEnd && rows.Count > 0) Write();
    }

    private void OnDisable()
    {
        if (recording) End();
    }

    private void Write()
    {
        if (string.IsNullOrEmpty(outputDirectory))
        {
            Debug.LogError("[StepTrace] please set directory on the RqStepTraceProbe component to record. "
                           + "outputDirectory is blank, so nothing was written. It is blank by "
                           + "default so that no machine's folder layout ships with the project; "
                           + "refusing here rather than letting CreateDirectory(\"\") throw, "
                           + "because an empty output folder looks exactly like a run that never "
                           + "armed.", this);
            return;
        }

        try
        {
            var dir = Path.Combine(outputDirectory, setName);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir,
                $"steptrace_{transform.name}_run{runIndex:D2}_{System.DateTime.Now:yyyyMMdd_HHmmss}.csv");

            var h = new StringBuilder(1 << 14);
            h.AppendLine("# Per-step trace of the CONTACT and DRIVE window. One row per fixed step, no");
            h.AppendLine("# decimation, so row index and time stay consistent.");
            h.AppendLine("# WHY THIS EXISTS: the sealed record keeps only ten summary statistics per");
            h.AppendLine("# observable and discards the samples, which cannot distinguish a steady slide");
            h.AppendLine("# from dwells separated by jumps. This file keeps the samples.");
            h.AppendLine("# NO THRESHOLDS. Every column is a position, speed, force or name. What counts as");
            h.AppendLine("# a dwell, and what counts as a maintained-coupling interval, are declared by the");
            h.AppendLine("# researcher and applied in analysis - not here.");
            h.AppendLine("# NO MECHANISM IS NAMED. The project has separate static and dynamic friction");
            h.AppendLine("# coefficients but no event-level instrument that can say which regime produced a");
            h.AppendLine("# given interval, so no column asserts one.");
            h.AppendLine("# THE LAG CHAIN, four points, because the compliance is not where it looks:");
            h.AppendLine("#   root_*     DOES NOT MOVE. applyRootMotion is false on every Animator here, so");
            h.AppendLine("#              the dummy root GameObject never translates. Measured constant across");
            h.AppendLine("#              a full drive window. Kept as EVIDENCE of that, never as a driver.");
            h.AppendLine("#   animHips_* the clip's actual output and the REAL driver of the run.");
            h.AppendLine("#   anchor_*   the kinematic ragdoll Hips body, which follows animHips exactly.");
            h.AppendLine("#   hand_*     after eight spring joints at 5000/40 - ALL the compliance is here.");
            h.AppendLine("#   sword_*    after the rigid FixedJoint - tracks hand to sub-millimetre.");
            h.AppendLine("# ADDED 2026-08-29 - the weld, in full. hand_q* and sword_q* are RAW world rotations");
            h.AppendLine("# as quaternions, of the SAME bodies the position columns use. A FixedJoint locks all");
            h.AppendLine("# SIX degrees of freedom, so its constraint error has a translational half and a");
            h.AppendLine("# rotational half; the position columns alone give only the first. No relative or");
            h.AppendLine("# delta rotation is written: the hand-to-sword relative rotation is the GRIP");
            h.AppendLine("# ORIENTATION and is not zero, so the error is the deviation of that relative pose");
            h.AppendLine("# from its value at the first in-window step, never the angle itself. Difference");
            h.AppendLine("# against the t0 row, which is always present because every step is written.");
            h.AppendLine("# weld*_force_n and weld*_torque_nm are FixedJoint.currentForce/currentTorque");
            h.AppendLine("# magnitudes - the constraint reaction. Readable on THIS joint only: the arm's");
            h.AppendLine("# ConfigurableJoints run every angular DOF Free, so their currentTorque reports a");
            h.AppendLine("# limit reaction and delivered servo torque remains the closed unobservable branch.");
            h.AppendLine("# BLANK, never zero, when no FixedJoint is found on that body. Blank is the absence");
            h.AppendLine("# of a reading; zero would be a reading, and a plausible wrong one.");
            h.AppendLine("# So anchor minus hand is arm compliance, and hand minus sword is a zero-check on");
            h.AppendLine("# the weld. animHips minus sword is compliance AND contact resistance together and");
            h.AppendLine("# is NOT attributable to contact on its own.");
            h.AppendLine("# MIGRATION IS NOT SLIDING. along/across are where the touch sits on each blade;");
            h.AppendLine("# tangential_speed is the relative motion of the two surfaces. Both are written.");
            h.AppendLine("# Nothing here is recomputed: contact position comes from RqContactSideProbe,");
            h.AppendLine("# sliding and force from RqStationContactRecorder, shapes from RqContactPatchProbe.");
            h.AppendLine($"# station={transform.name} run={runIndex} set={setName}");
            h.AppendLine($"# fixedDeltaTime={Time.fixedDeltaTime.ToString("R", CultureInfo.InvariantCulture)} " +
                         $"hz={(1f / Time.fixedDeltaTime).ToString("F3", CultureInfo.InvariantCulture)}");
            h.AppendLine($"# stationOrigin={transform.position.ToString("F6")}");
            h.AppendLine("# phase: 3=CONTACT 4=DRIVE. t_since_drive_s is zeroed at the first DRIVE step and");
            h.AppendLine("# is NaN before it. side_read=0 means the contact-side probe had no reading that");
            h.AppendLine("# step, and the along/across fields are then EMPTY rather than zero.");
            h.AppendLine("# shape_pairs_summary lists the distinct 'Aside|Bside' collider pairs that carried");
            h.AppendLine("# contact that step. deepest_shape in the sealed record answers a DIFFERENT");
            h.AppendLine("# question: the deepest shape at peak penetration need not be the shape that");
            h.AppendLine("# established or sustained the pair.");
            h.AppendLine("# RELEASE EVENTS ARE NOT IN THIS FILE, BY DECLARATION. A release is defined ONLY");
            h.AppendLine("# as the END of a maintained-coupling interval - two or more consecutive pair");
            h.AppendLine("# steps ending - and is derived in analysis from the pair column here, against the");
            h.AppendLine("# rule in StationMeasurementSets.md 2.7. At each release the analysis records the");
            h.AppendLine("# last contact positions, separation, overlap status, and any later");
            h.AppendLine("# re-engagement. It MUST NOT classify the release as 'slid off', 'pass-through',");
            h.AppendLine("# or a friction transition: this apparatus cannot distinguish those, and no column");
            h.AppendLine("# in this file supports that distinction.");

            h.AppendLine("step,t_s,t_since_drive_s,phase," +
                         "rootA_x,rootA_y,rootA_z,rootB_x,rootB_y,rootB_z," +
                         "animHipsA_x,animHipsA_y,animHipsA_z,animHipsB_x,animHipsB_y,animHipsB_z," +
                         "anchorA_x,anchorA_y,anchorA_z,anchorB_x,anchorB_y,anchorB_z," +
                         "handA_x,handA_y,handA_z,handB_x,handB_y,handB_z," +
                         "swordA_x,swordA_y,swordA_z,swordB_x,swordB_y,swordB_z," +
                         "handA_qx,handA_qy,handA_qz,handA_qw,handB_qx,handB_qy,handB_qz,handB_qw," +
                         "swordA_qx,swordA_qy,swordA_qz,swordA_qw," +
                         "swordB_qx,swordB_qy,swordB_qz,swordB_qw," +
                         "weldA_force_n,weldA_torque_nm,weldB_force_n,weldB_torque_nm," +
                         "animHandA_x,animHandA_y,animHandA_z,animHandB_x,animHandB_y,animHandB_z," +
                         "side_read,lead_pair,inverted," +
                         "step_min_separation_mm,shape_pairs,shape_pairs_summary");

            for (var i = 0; i < rows.Count; i++) h.AppendLine(rows[i]);

            File.WriteAllText(path, h.ToString());
            Debug.Log($"[StepTrace] wrote {path} ({rows.Count} rows)", this);
        }
        catch (System.Exception e)
        {
            Debug.LogError("[StepTrace] " + name + ": CSV write failed — " + e.GetType().Name + ": " +
                           e.Message + "\n" + e.StackTrace, this);
        }
    }
}
