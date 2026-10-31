// =====================================================================================
// RETIRED — see PLAN_DriveWindowRecording.md §5.  Do not extend, do not enable.
//
// A Phase 0 artefact. Superseded function for function by RqClassificationForkRecorder's
// per-row scenario / rawId / classWrittenAtStep and then by the v1 initialScenario latch.
// Present on Station_EdgeNoBind_OurSolution only, and DISABLED there.
//
// It also records the WRONG WINDOW for the Chapter 4 comparison: the physical record measures
// from the onset of Sword A's forward step, which is DRIVE. Anything gated earlier is setup.
// =====================================================================================
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BladeContact;
using FIMSpace.FProceduralAnimation;
using UnityEngine;

/// <summary>
///     Phase 0 calibration recorder for the apex-attribution work. Establishes what "stable enough under
///     maintained load" looks like on this rig, so gates can be locked from observation instead of guessed.
/// </summary>
/// <remarks>
///     <para>
///         <b>This decides nothing and applies nothing.</b> It adds no force, writes no material, sets no
///         threshold and declares no pass or fail. It drives the animation State it is told to drive and
///         writes down raw per-step values. Whether a hold counts as settled is a decision taken later,
///         from this data, by a human.
///     </para>
///     <para>
///         <b>Deliberately independent of <see cref="RqStationContactRecorder" />.</b> That recorder
///         latches a zero on first contact and reports migration, rotation gain and tip travel RELATIVE to
///         it. Servos overshoot on the way into HOLD and the blades can briefly kiss, so a first-contact
///         latch can land on that kiss rather than on the loaded state. Nothing here reads that latch or
///         any baseline-relative quantity: every column is a raw per-step reading, plus explicit phase
///         entry and contact-episode boundaries, so a HOLD kiss cannot silently become the reference for a
///         later CONTACT measurement. Re-purposing <c>autoLatchOnFirstContact</c> is expressly not done.
///         If baseline-relative migration during CONTACT is wanted later, that needs its own declared
///         CONTACT-entry reference and its own validation.
///     </para>
///     <para>
///         <b>Phase semantics, as authored.</b> <c>Auto</c> false enables direct State addressing; the
///         controller's ANY-state transitions are gated on it. State 1 = SNAP (swords deliberately apart),
///         2 = HOLD (servos target the tracked reference pose; the approach may overshoot and kiss, and any
///         contact here is CIRCUMSTANTIAL, never maintained contact), 3 = CONTACT (servos target beyond
///         HOLD, pressing the blades together). CONTACT is the only state with a designed load.
///     </para>
///     <para>
///         <b>Why dwell is counted in steps and transitions are observed.</b> The SNAP, HOLD and CONTACT
///         clips are single-keyframe authored poses of zero length, so elapsed clip time says nothing about
///         whether the rig has arrived. What moves the bodies is the servos chasing a target that itself
///         blends in over the transition. Settling therefore cannot be inferred from clip time, and is not
///         assumed from the transition's authored duration either: <c>IsInTransition</c> and the
///         transition's normalized time are recorded per step so blend completion is observed.
///     </para>
///     <para>
///         <b>Timing is measured, not assumed.</b> Collision callbacks and <c>FixedUpdate</c> do not
///         interleave in an order this component takes on faith. Every callback is tagged with the step
///         counter current at arrival, and every row records the lag between that and the step which
///         consumed it, so the actual relationship on this rig comes out in the data.
///     </para>
///     <para>
///         <b>Solver staleness is exposed.</b> The classifier recomputes on its own interval and, when a
///         query yields no usable witness, keeps whatever the pair was last called. Each row therefore
///         carries the solver's own <c>LastSolvedStep</c> beside the step it was read at, so a stale class
///         is visible as stale rather than reported as a fresh attribution.
///     </para>
///     <para>
///         <b>Not in Phase 0.</b> The independent vertex/topology attribution route is not run here —
///         Phase 0's declared purpose is speeds, contact-point movement, timing and classification cadence.
///         A mesh/topology checksum IS recorded, as provenance for the reconstruction that comes later.
///         <c>SignedOverlap</c> is recorded verbatim but is meaningless when ContactCount is zero and must
///         never be read as a separation in that case.
///     </para>
/// </remarks>
[DisallowMultipleComponent]
public sealed class RqAttributionPhase0Recorder : MonoBehaviour
{
    [Header("Identity — declared, not inferred")]
    [Tooltip("Station ID as it appears on this station's RqStationContactRecorder, e.g. EdgeNoBind. " +
             "Recorded verbatim. This is a historical station identifier, NOT a predicted outcome.")]
    [SerializeField] private string stationId = "";

    [Tooltip("The station's declared physical reference pose in words, e.g. 'edge-on-edge, no-bind'. " +
             "Recorded verbatim so the row cannot be mistaken for a result.")]
    [SerializeField] private string referencePose = "";

    [Header("Animation control — both sides or neither")]
    [SerializeField] private Animator animatorA;
    [SerializeField] private Animator animatorB;

    [Header("Blades")]
    [Tooltip("Ragdoll Animator re-hosts a worn sword's physics on a generated copy. These are needed to " +
             "follow the blade onto the shapes actually carrying it.")]
    [SerializeField] private RA2AttachableObject attachableA;
    [SerializeField] private RA2AttachableObject attachableB;

    [Tooltip("The AUTHORED blade collider on each sword. Used only to pick the corresponding generated " +
             "copy out of Ragdoll Animator's list. Guard, grip and marker colliders must not be listed.")]
    [SerializeField] private Collider authoredBladeA;
    [SerializeField] private Collider authoredBladeB;

    [Header("Solver readout (read-only)")]
    [SerializeField] private RqBladeTangentialSolver solver;
    [SerializeField] private RqBladeContact contactA;
    [SerializeField] private RqBladeContact contactB;

    [Header("Dwell — SAMPLE SIZES, not criteria")]
    [Tooltip("Steps to record in Pause before State 1, to capture an unloaded baseline.")]
    [SerializeField] private int dwellPauseSteps = 90;

    [Tooltip("Steps to hold State 1 (SNAP).")]
    [SerializeField] private int dwellSnapSteps = 360;

    [Tooltip("Steps to hold State 2 (HOLD).")]
    [SerializeField] private int dwellHoldSteps = 720;

    [Tooltip("Steps to hold State 3 (CONTACT). Longest, because this is the state being calibrated and it " +
             "may never fully quiet: two servo sets push against each other, so ongoing motion under " +
             "maintained load is an expected observation rather than a failure.")]
    [SerializeField] private int dwellContactSteps = 900;

    [Header("Output")]
    /// <remarks>
    ///     BLANK by default, deliberately. It used to ship an absolute path from one machine, which
    ///     both leaked a folder structure into a public repository and silently wrote another
    ///     machine's runs into a directory that did not describe them. Blank cannot do either, and
    ///     the write path below refuses with a named error rather than throwing on
    ///     CreateDirectory("").
    /// </remarks>
    [SerializeField] private string outputDirectory = "";

    [Tooltip("Begin the sequence automatically on play. Left off, call Begin() yourself.")]
    [SerializeField] private bool runOnStart = true;

    private const string AutoParam = "Auto";
    private const string StateParam = "State";

    private sealed class Side
    {
        public string label = "";
        public RA2AttachableObject attachable;
        public Collider authored;

        public Collider live;
        public Rigidbody body;
        public Transform frame;
        public BladeCollisionRelay relay;
        public GameObject boundTo;

        public int meshVertexCount;
        public int meshTriangleCount;
        public int topologyChecksum;
        public string meshName = "";
    }

    private readonly Side a = new Side();
    private readonly Side b = new Side();

    /// <summary>Monotonic physics-step counter. Not Time.frameCount, which counts render frames.</summary>
    private int step;

    // Contact accumulation for the step whose solve produced it.
    private readonly ContactPoint[] contactBuffer = new ContactPoint[64];
    private int reportedStep = -1;
    private int arrivalStep = -1;
    private int contactCount;
    private Vector3 pointSum;
    private Vector3 normalSum;
    private Vector3 impulse;
    private float deepestSeparation;
    private string deepestShapeA = "";
    private string deepestShapeB = "";
    private bool pairVerified;
    private int callbacksThisStep;

    private int phaseState = int.MinValue;
    private int phaseEnteredAtStep;
    private int sequenceCursor;
    private int cursorDeadline;
    private bool running;
    private bool sealed_;

    private bool wasTouching;
    private int episodeIndex;
    private int episodeStartStep = -1;

    private readonly List<string> rows = new List<string>();

    private void Start()
    {
        if (runOnStart) Begin();
    }

    /// <summary>Freezes both animators and starts the declared SNAP -> HOLD -> CONTACT walk.</summary>
    public void Begin()
    {
        if (animatorA == null || animatorB == null)
        {
            Debug.LogError("[Phase0] " + stationId + ": both animators must be assigned. Nothing recorded.", this);
            return;
        }

        // Auto false is what enables direct State addressing; the controller's ANY-state transitions are
        // gated on it. Both sides or the two dummies desynchronise.
        animatorA.SetBool(AutoParam, false);
        animatorB.SetBool(AutoParam, false);
        SetState(0);

        rows.Clear();
        rows.Add(Header());
        step = 0;
        sequenceCursor = 0;
        cursorDeadline = dwellPauseSteps;
        running = true;
        sealed_ = false;
        episodeIndex = 0;
        episodeStartStep = -1;
        wasTouching = false;

        Debug.Log("[Phase0] " + stationId + " (" + referencePose + "): Auto=false, walking Pause -> 1 -> 2 -> 3. " +
                  "Dwells " + dwellPauseSteps + "/" + dwellSnapSteps + "/" + dwellHoldSteps + "/" +
                  dwellContactSteps + " steps at " + (1f / Time.fixedDeltaTime).ToString("F1") + " Hz.", this);
    }

    private void SetState(int value)
    {
        animatorA.SetInteger(StateParam, value);
        animatorB.SetInteger(StateParam, value);
        phaseState = value;
        phaseEnteredAtStep = step;
    }

    private void LateUpdate()
    {
        Rebind(a, attachableA, authoredBladeA);
        Rebind(b, attachableB, authoredBladeB);
    }

    /// <summary>
    ///     Points at whichever shape is carrying each blade right now. Holding the authored collider would
    ///     record nothing at all once Ragdoll Animator has re-hosted the sword, and would not say so.
    /// </summary>
    private void Rebind(Side side, RA2AttachableObject attachable, Collider authored)
    {
        side.attachable = attachable;
        side.authored = authored;

        GameObject generated = attachable != null ? attachable.GeneratedPhysicsObject : null;
        if (generated == side.boundTo && side.live != null) return;

        Release(side);
        side.boundTo = generated;

        if (generated == null)
        {
            side.live = authored;
        }
        else
        {
            List<Collider> src = attachable.AttachableColliders;
            List<Collider> copies = attachable.GeneratedPhysicsColliders;
            if (src == null || copies == null || src.Count != copies.Count)
            {
                Debug.LogWarning("[Phase0] " + stationId + "/" + side.label + ": generated collider list does " +
                                 "not correspond to the authored one. This side records nothing.", this);
                return;
            }

            for (int i = 0; i < src.Count; i++)
                if (src[i] == authored && copies[i] != null) side.live = copies[i];
        }

        if (side.live == null) return;

        side.body = side.live.attachedRigidbody;
        side.frame = side.body != null ? side.body.transform : side.live.transform;
        side.label = side.live.transform.name;

        CacheMeshProvenance(side);

        if (side.body == null) return;

        side.relay = side.body.GetComponent<BladeCollisionRelay>();
        if (side.relay == null) side.relay = side.body.gameObject.AddComponent<BladeCollisionRelay>();
        side.relay.Collided += OnCollided;
    }

    /// <summary>
    ///     Mesh identity and a topology checksum, taken once per binding. Provenance for the independent
    ///     reconstruction that runs in a later phase: it proves which geometry a row was taken against.
    /// </summary>
    private void CacheMeshProvenance(Side side)
    {
        side.meshVertexCount = 0;
        side.meshTriangleCount = 0;
        side.topologyChecksum = 0;
        side.meshName = "";

        MeshCollider mc = side.live as MeshCollider;
        if (mc == null || mc.sharedMesh == null) return;

        Mesh m = mc.sharedMesh;
        side.meshName = m.name;
        side.meshVertexCount = m.vertexCount;

        int[] tris = m.triangles;
        side.meshTriangleCount = tris.Length / 3;

        unchecked
        {
            int h = 17;
            h = h * 31 + m.vertexCount;
            h = h * 31 + tris.Length;
            for (int i = 0; i < tris.Length; i++) h = h * 31 + tris[i];
            Vector3[] verts = m.vertices;
            for (int i = 0; i < verts.Length; i++)
            {
                h = h * 31 + verts[i].x.GetHashCode();
                h = h * 31 + verts[i].y.GetHashCode();
                h = h * 31 + verts[i].z.GetHashCode();
            }
            side.topologyChecksum = h;
        }
    }

    private void Release(Side side)
    {
        if (side.relay != null) side.relay.Collided -= OnCollided;
        side.relay = null;
        side.live = null;
        side.body = null;
        side.frame = null;
    }

    private void OnDisable()
    {
        Release(a);
        Release(b);
    }

    /// <summary>
    ///     Accumulates only manifolds that are this station's blade proxy against the other's. Marker,
    ///     grip, crossguard and any other shape are excluded, and whether the pair was verified is written
    ///     into the row rather than assumed.
    /// </summary>
    private void OnCollided(Collision collision)
    {
        if (a.live == null || b.live == null) return;

        int count = collision.GetContacts(contactBuffer);
        if (count == 0) return;

        for (int i = 0; i < count; i++)
        {
            ContactPoint cp = contactBuffer[i];

            bool forward = cp.thisCollider == a.live && cp.otherCollider == b.live;
            bool reverse = cp.thisCollider == b.live && cp.otherCollider == a.live;
            if (!forward && !reverse) continue;

            if (reportedStep != step)
            {
                reportedStep = step;
                arrivalStep = step;
                contactCount = 0;
                callbacksThisStep = 0;
                pointSum = Vector3.zero;
                normalSum = Vector3.zero;
                impulse = collision.impulse;
                deepestSeparation = float.MaxValue;
                deepestShapeA = "";
                deepestShapeB = "";
                pairVerified = true;
            }

            callbacksThisStep++;
            pointSum += cp.point;
            normalSum += forward ? cp.normal : -cp.normal;
            contactCount++;

            if (cp.separation < deepestSeparation)
            {
                deepestSeparation = cp.separation;
                deepestShapeA = forward ? cp.thisCollider.name : cp.otherCollider.name;
                deepestShapeB = forward ? cp.otherCollider.name : cp.thisCollider.name;
            }
        }
    }

    private void FixedUpdate()
    {
        if (!running || sealed_) return;

        step++;

        // Consumed here rather than in the callback, and the lag is written down rather than presumed: the
        // interleaving of callbacks and FixedUpdate on this rig is a Phase 0 question, not a premise.
        bool touching = reportedStep >= 0 && reportedStep == step - 1 && contactCount > 0;
        int consumeLag = reportedStep >= 0 ? step - reportedStep : -1;

        if (touching && episodeStartStep < 0)
        {
            episodeIndex++;
            episodeStartStep = step;
        }

        bool episodeClosed = !touching && wasTouching;
        if (episodeClosed) episodeStartStep = -1;

        rows.Add(Row(touching, consumeLag, episodeClosed));
        wasTouching = touching;

        Advance();
    }

    /// <summary>Walks the declared dwell schedule. No settling test is applied; that is what this measures.</summary>
    private void Advance()
    {
        if (step < cursorDeadline) return;

        switch (sequenceCursor)
        {
            case 0:
                SetState(1);
                cursorDeadline = step + dwellSnapSteps;
                break;
            case 1:
                SetState(2);
                cursorDeadline = step + dwellHoldSteps;
                break;
            case 2:
                SetState(3);
                cursorDeadline = step + dwellContactSteps;
                break;
            default:
                Seal();
                return;
        }

        sequenceCursor++;
    }

    /// <summary>Closes the buffer and writes it. Nothing is read out of this component before this runs.</summary>
    public void Seal()
    {
        if (sealed_) return;
        sealed_ = true;
        running = false;

        if (string.IsNullOrEmpty(outputDirectory))
        {
            Debug.LogError("[AttributionPhase0] please set directory on the RqAttributionPhase0Recorder component to record. "
                           + "outputDirectory is blank, so nothing was written. It is blank by "
                           + "default so that no machine's folder layout ships with the project; "
                           + "refusing here rather than letting CreateDirectory(\"\") throw, "
                           + "because an empty output folder looks exactly like a run that never "
                           + "armed.", this);
            return;
        }

        string dir = outputDirectory;
        try
        {
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string file = Path.Combine(dir,
                "phase0_" + (string.IsNullOrEmpty(stationId) ? "station" : stationId) + "_" +
                System.DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv");

            var sb = new StringBuilder();
            sb.Append(Preflight());
            for (int i = 0; i < rows.Count; i++) sb.AppendLine(rows[i]);
            File.WriteAllText(file, sb.ToString());

            Debug.Log("[Phase0] " + stationId + " SEALED. " + (rows.Count - 1) + " step rows -> " + file, this);
        }
        catch (System.Exception e)
        {
            Debug.LogError("[Phase0] " + stationId + ": seal failed to write. " + e.GetType().Name + ": " +
                           e.Message, this);
        }
    }

    /// <summary>
    ///     Recorded per session rather than assumed constant: the solver's own interval and policy, the
    ///     engine configuration, and each blade's mass properties and effective material.
    /// </summary>
    private string Preflight()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Phase 0 calibration — RAW observations. No gate, no threshold, no pass/fail.");
        sb.AppendLine("# stationId," + stationId);
        sb.AppendLine("# referencePose," + referencePose);
        sb.AppendLine("# sealedAt," + System.DateTime.Now.ToString("o"));
        sb.AppendLine("# fixedDeltaTime," + F(Time.fixedDeltaTime) + ",hz," + F(1f / Time.fixedDeltaTime));
        sb.AppendLine("# dwellSteps,pause," + dwellPauseSteps + ",snap," + dwellSnapSteps + ",hold," +
                      dwellHoldSteps + ",contact," + dwellContactSteps);

        sb.AppendLine("# gravityY," + F(Physics.gravity.y) +
                      ",solverIterations," + Physics.defaultSolverIterations +
                      ",solverVelocityIterations," + Physics.defaultSolverVelocityIterations +
                      ",sleepThreshold," + F(Physics.sleepThreshold) +
                      ",maxDepenetrationVelocity," + F(Physics.defaultMaxDepenetrationVelocity) +
                      ",defaultContactOffset," + F(Physics.defaultContactOffset) +
                      ",defaultMaxAngularSpeed," + F(Physics.defaultMaxAngularSpeed) +
                      ",improvedPatchFriction," + Physics.improvedPatchFriction +
                      ",queriesHitBackfaces," + Physics.queriesHitBackfaces);

        if (solver != null)
        {
            RqBladeTangentialSolver.Snapshot s;
            bool have = contactA != null && contactB != null && solver.TryGetSnapshot(contactA, contactB, out s);
            sb.AppendLine("# solver," + solver.name + ",snapshotAvailableAtSeal," + have);
        }
        else sb.AppendLine("# solver,<none assigned>");

        // Classifier identity. SpecificityTieBand is a COMPILE-TIME constant, so it cannot be read back
        // from a runtime parameter and a run can only be attributed to it via the build it ran on. The
        // module version id changes on every recompile of the package, which makes it a code fingerprint
        // that cannot go stale the way a hand-maintained version string would.
        sb.AppendLine("# classifier,specificityTieBand," + F(BladeShellSweep.SpecificityTieBand) +
                      ",sweepModuleVersionId," + typeof(BladeShellSweep).Module.ModuleVersionId +
                      ",solverModuleVersionId," + typeof(RqBladeTangentialSolver).Module.ModuleVersionId +
                      ",sweepAssembly," + typeof(BladeShellSweep).Assembly.GetName().Name);

        AppendSidePreflight(sb, "A", a);
        AppendSidePreflight(sb, "B", b);
        return sb.ToString();
    }

    private void AppendSidePreflight(StringBuilder sb, string tag, Side side)
    {
        sb.Append("# side" + tag + ",label," + side.label +
                  ",mesh," + side.meshName +
                  ",verts," + side.meshVertexCount +
                  ",tris," + side.meshTriangleCount +
                  ",topologyChecksum," + side.topologyChecksum);

        if (side.body != null)
            sb.Append(",mass," + F(side.body.mass) +
                      ",comLocal," + V(side.body.centerOfMass) +
                      ",inertiaTensor," + V(side.body.inertiaTensor) +
                      ",maxAngularVelocity," + F(side.body.maxAngularVelocity) +
                      ",collisionDetection," + side.body.collisionDetectionMode);
        else sb.Append(",body,<null>");

        if (side.live != null)
            sb.Append(",contactOffset," + F(side.live.contactOffset) +
                      ",material," + (side.live.sharedMaterial == null ? "<null>" : side.live.sharedMaterial.name) +
                      ",staticFriction," + (side.live.sharedMaterial == null ? "" : F(side.live.sharedMaterial.staticFriction)) +
                      ",dynamicFriction," + (side.live.sharedMaterial == null ? "" : F(side.live.sharedMaterial.dynamicFriction)) +
                      ",frictionCombine," + (side.live.sharedMaterial == null ? "" : side.live.sharedMaterial.frictionCombine.ToString()));

        sb.AppendLine();
    }

    private static string Header()
    {
        return string.Join(",", new[]
        {
            "step", "phaseState", "stepsInPhase",
            "animAInTransition", "animATransitionNorm", "animAStateNorm",
            "animBInTransition", "animBTransitionNorm", "animBStateNorm",
            "touching", "consumeLag", "callbacksThisStep", "contactCount", "pairVerified",
            "episodeIndex", "episodeStartStep", "episodeClosed",
            "contactPointX", "contactPointY", "contactPointZ",
            "contactNormalX", "contactNormalY", "contactNormalZ",
            "deepestSeparation", "deepestShapeA", "deepestShapeB",
            "impulseMag",
            "aPosX", "aPosY", "aPosZ", "aRotX", "aRotY", "aRotZ", "aRotW",
            "aLinSpeed", "aAngSpeed",
            "bPosX", "bPosY", "bPosZ", "bRotX", "bRotY", "bRotZ", "bRotW",
            "bLinSpeed", "bAngSpeed",
            "aTopologyChecksum", "bTopologyChecksum",
            "snapValid", "snapScenario", "snapRawTypeA", "snapRawTypeB", "snapRawIdA", "snapRawIdB",
            "snapClassificationValid", "snapLastSolvedStep", "snapSolveAgeSteps",
            "snapClassifyAttemptedAtStep", "snapClassWrittenAtStep", "snapClassAgeSteps",
            "snapClassCarriedForward",
            "snapState", "snapNormalForce", "snapTangentialDemand", "snapBindLimit",
            "snapAppliedTangentialForce", "snapTangentialSpeed", "snapShellSeparation",
            "snapSignedOverlap_INVALID_IF_NO_CONTACT", "snapContactCount"
        });
    }

    private string Row(bool touching, int consumeLag, bool episodeClosed)
    {
        var sb = new StringBuilder(512);

        sb.Append(step).Append(',').Append(phaseState).Append(',').Append(step - phaseEnteredAtStep).Append(',');

        AppendAnimator(sb, animatorA);
        AppendAnimator(sb, animatorB);

        sb.Append(touching ? 1 : 0).Append(',').Append(consumeLag).Append(',')
          .Append(touching ? callbacksThisStep : 0).Append(',')
          .Append(touching ? contactCount : 0).Append(',')
          .Append(touching && pairVerified ? 1 : 0).Append(',')
          .Append(episodeIndex).Append(',').Append(episodeStartStep).Append(',')
          .Append(episodeClosed ? 1 : 0).Append(',');

        if (touching && contactCount > 0)
        {
            Vector3 p = pointSum / contactCount;
            Vector3 n = normalSum / contactCount;
            sb.Append(V(p)).Append(',').Append(V(n)).Append(',')
              .Append(F(deepestSeparation)).Append(',').Append(deepestShapeA).Append(',')
              .Append(deepestShapeB).Append(',').Append(F(impulse.magnitude)).Append(',');
        }
        else sb.Append(",,,,,,,,,,");

        AppendBody(sb, a);
        AppendBody(sb, b);

        sb.Append(a.topologyChecksum).Append(',').Append(b.topologyChecksum).Append(',');

        RqBladeTangentialSolver.Snapshot s;
        if (solver != null && contactA != null && contactB != null &&
            solver.TryGetSnapshot(contactA, contactB, out s))
        {
            sb.Append(s.Valid ? 1 : 0).Append(',').Append(s.Scenario).Append(',')
              .Append(s.RawTypeA).Append(',').Append(s.RawTypeB).Append(',')
              .Append(Clean(s.RawIdA)).Append(',').Append(Clean(s.RawIdB)).Append(',')
              .Append(s.ClassificationValid ? 1 : 0).Append(',')
              // LastSolvedStep is a SOLVE timestamp. Its age is recorded for completeness but it is not
              // classification freshness, which is what the next three columns carry.
              .Append(s.LastSolvedStep).Append(',')
              .Append(s.LastSolvedStep > 0 ? (step - s.LastSolvedStep).ToString(CultureInfo.InvariantCulture) : "")
              .Append(',')
              .Append(s.ClassifyAttemptedAtStep).Append(',')
              .Append(s.ClassWrittenAtStep).Append(',')
              .Append(s.ClassWrittenAtStep > 0 ? (step - s.ClassWrittenAtStep).ToString(CultureInfo.InvariantCulture) : "")
              .Append(',')
              // An attempt newer than the last successful write means the pair is carrying a class forward
              // from an earlier step rather than having been re-classified.
              .Append(s.ClassifyAttemptedAtStep > s.ClassWrittenAtStep ? 1 : 0).Append(',')
              .Append(s.State).Append(',').Append(F(s.NormalForce)).Append(',')
              .Append(F(s.TangentialDemand)).Append(',').Append(F(s.BindLimit)).Append(',')
              .Append(F(s.AppliedTangentialForce)).Append(',').Append(F(s.TangentialSpeed)).Append(',')
              .Append(F(s.ShellSeparation)).Append(',').Append(F(s.SignedOverlap)).Append(',')
              .Append(s.ContactCount);
        }
        else sb.Append(",,,,,,,,,,,,,,,,,,,,,");

        return sb.ToString();
    }

    private static void AppendAnimator(StringBuilder sb, Animator an)
    {
        if (an == null) { sb.Append(",,,"); return; }
        bool inTransition = an.IsInTransition(0);
        float tNorm = inTransition ? an.GetAnimatorTransitionInfo(0).normalizedTime : 0f;
        float sNorm = an.GetCurrentAnimatorStateInfo(0).normalizedTime;
        sb.Append(inTransition ? 1 : 0).Append(',').Append(F(tNorm)).Append(',').Append(F(sNorm)).Append(',');
    }

    private static void AppendBody(StringBuilder sb, Side side)
    {
        if (side.frame == null || side.body == null) { sb.Append(",,,,,,,,,"); return; }
        Vector3 p = side.frame.position;
        Quaternion q = side.frame.rotation;
        sb.Append(F(p.x)).Append(',').Append(F(p.y)).Append(',').Append(F(p.z)).Append(',')
          .Append(F(q.x)).Append(',').Append(F(q.y)).Append(',').Append(F(q.z)).Append(',').Append(F(q.w)).Append(',')
          .Append(F(side.body.linearVelocity.magnitude)).Append(',')
          .Append(F(side.body.angularVelocity.magnitude)).Append(',');
    }

    private static string Clean(string s) => string.IsNullOrEmpty(s) ? "" : s.Replace(',', ';');
    private static string F(float v) => v.ToString("G9", CultureInfo.InvariantCulture);
    private static string V(Vector3 v) => F(v.x) + "," + F(v.y) + "," + F(v.z);
}
