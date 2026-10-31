// =====================================================================================
// SCOPE — see PLAN_DriveWindowRecording.md §5.  NOT retired, and NOT the Chapter 4 instrument.
//
// This is the APPROACH / ATTRIBUTION recorder. captureInPhaseState = 3 (CONTACT), which is
// correct for its own question — what the classifier said before force was transmitted, the
// v1 latch, and the sealed Q1 evidence. All of that stands.
//
// It is NOT the physical-to-virtual comparison. The physical record measures from the onset of
// Sword A's forward step = DRIVE; this recorder's window ends where that one begins. The
// 27-run tranche of 2026-08-25 was taken with this recorder and is therefore approach-window
// data, not comparison data.
//
// Primary comparison instrument is RqStationContactRecorder over the DRIVE window.
// =====================================================================================
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BladeContact;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
///     Phase 1b. Answers ONE question: when production classifies this contact as <c>EdgeEdge</c> at some
///     steps and <c>EdgeFlat</c> at others, did the candidate geometry change, or did the same coincident
///     locus resolve differently?
/// </summary>
/// <remarks>
///     <para>
///         <b>Run 01's gap, and why this revision exists.</b> Run 01 established the mechanism but recorded
///         no phase and drove nothing, so there was no way to say whether its three events were in the
///         commanded CONTACT window or in the start-state settle. This version drives the animators on a
///         declared dwell schedule exactly as <c>RqAttributionPhase0Recorder</c> does, records
///         <c>phaseState</c>/<c>stepsInPhase</c> on every row, and refuses to capture outside a declared
///         window.
///     </para>
///     <para>
///         <b>Three measurement vectors, because one is not safe.</b> Distances alone cannot settle this: at
///         edge-on-edge contact the sharp ridge lies on the boundary of its own adjoining bevel facets, so
///         candidates are legitimately equidistant, and the sweep's distances are unsigned magnitudes
///         besides. So: (1) what production actually computed, via
///         <see cref="BladeClassificationTrace" />; (2) an unpruned re-measurement at the same pose; and
///         (3) authored adjacency, which involves no distance at all.
///     </para>
///     <para>
///         <b>Watchability is a requirement, not a nicety.</b> The numbers claim a phase and a contact; a
///         human has to be able to confirm independently that the animation was in that phase and that the
///         blades were visibly touching. So the exhaustive enumeration is deferred until after the dwell
///         schedule finishes — nothing competes with the window being watched — and the classifier's current
///         verdict is drawn live as full-blade-length lines plus an on-screen readout.
///     </para>
/// </remarks>
/// <remarks>
///     <see cref="DefaultExecutionOrder" /> +100 against the solver's -100, so this recorder's
///     <c>FixedUpdate</c> runs AFTER the solver has published its readout for this step. Without that it
///     read the previous step's values and every row mixed two instants — which was true of every run
///     recorded before 2026-08-23.
/// </remarks>
[DefaultExecutionOrder(100)]
public sealed class RqClassificationForkRecorder : MonoBehaviour
{
    /// <summary>Feature-kind categories the reference enumeration minimises over, independently.</summary>
    /// <remarks>
    ///     <see cref="SharpSharp" /> is NOT a duplicate of <see cref="EdgeEdge" />. Specificity in the sweep
    ///     is geometric KIND, so an edge-edge winner may be a non-cutting <c>ProfileFeatureEdge</c>, which
    ///     classifies as <c>EdgeFlat</c>. Minimising over authored SharpEdge-to-SharpEdge separately is the
    ///     only way to see whether an RQ-relevant candidate existed at all.
    /// </remarks>
    private enum Category
    {
        EdgeEdge,
        EdgeSurface,
        SurfaceEdge,
        SurfaceSurface,
        SharpSharp
    }

    private const int CategoryCount = 5;

    private const string AutoParam = "Auto";
    private const string StateParam = "State";

    private static readonly string[] PhaseNames = { "Pause", "SNAP", "HOLD", "CONTACT" };

    [Header("Identity — declared, written into every file")]
    [SerializeField] private string stationId = "";

    [SerializeField] private string referencePose = "";

    [Header("Instrument")]
    [Tooltip("The solver whose fresh classifications are captured. Tracing is enabled on it at Begin and " +
             "disabled at Seal, and is never serialized onto the prefab.")]
    [SerializeField] private RqBladeTangentialSolver solver;

    [Tooltip("Capture only traces whose pair labels match these, so another station's blade pair cannot " +
             "contribute a row. P3T_BladeTangentialSolver is ONE scene-wide solver serving every " +
             "OurSolution station, so leaving these empty is not safe.")]
    [SerializeField] private string requireLabelA = "";

    [SerializeField] private string requireLabelB = "";

    [Tooltip("The two blades. Used for the placement-integrity check and for the witness lines. " +
             "BladeShell.SimulatedPlacement is built from the SIMULATING RIGIDBODY, so the classification " +
             "is computed on the pose PhysX actually solved. It was previously built from the " +
             "ActiveTransform, which under interpolation is a rendering pose — that displaced the geometry " +
             "the classification ran on by 1.5-1.7 mm and moved an identical closest-feature query by 39%. " +
             "Left unset, the check is blank and the run cannot rule the effect out.")]
    [SerializeField] private RqBladeContact contactA;

    [SerializeField] private RqBladeContact contactB;

    [Header("Phase walk — Auto is forced false and State is driven directly")]
    [Tooltip("Both animators. Auto=false is what enables direct State addressing; the controller's " +
             "ANY-state transitions are gated on it. Both sides, or the two dummies desynchronise.")]
    [SerializeField] private Animator animatorA;

    [SerializeField] private Animator animatorB;

    [Tooltip("DWELL, Pause. Sample size, not a criterion. A State change takes a measured 180 steps " +
             "(2.0 s at 90 Hz) to blend, so this is set to 180: the dwell then ends no earlier than the " +
             "transition into the start pose completes.\n\n" +
             "Was 90, inherited from Phase 0 run 01, on the mistaken assumption that a 90-step dwell made " +
             "the transition take 90 steps. It does not — the dwell only decides when the NEXT phase is " +
             "commanded, so a 90-step Pause commanded SNAP while Pause was still blending. Runs before " +
             "2026-08-23 used 90 and are not comparable on phase timing.")]
    [Min(1)]
    [SerializeField] private int dwellPauseSteps = 180;

    [Min(1)]
    [SerializeField] private int dwellSnapSteps = 360;

    [Min(1)]
    [SerializeField] private int dwellHoldSteps = 720;

    [Min(1)]
    [SerializeField] private int dwellContactSteps = 900;

    [Header("Capture window — CRITERIA, pending ratification")]
    [Tooltip("Phase a capture is allowed in. 3 = CONTACT.")]
    [Range(0, 3)]
    [SerializeField] private int captureInPhaseState = 3;

    [Tooltip("Steps into that phase before a capture is allowed. DERIVED, not chosen: Phase 0 run 01 " +
             "measured every State transition completing at stepsInPhase = 180, so a smaller value would " +
             "capture during the blend rather than in a settled contact. Recorded in every output file.")]
    [Min(0)]
    [SerializeField] private int captureMinStepsInPhase = 180;

    [Tooltip("Capture the E1/E2/E3 contact-EVOLUTION events (first EdgeEdge, first EdgeFlat, return to " +
             "EdgeEdge) and run the exhaustive reference enumeration for each. OFF for a Q1 run.\n\n" +
             "Q1 asks what the FIRST attribution is, before load. That is answered entirely by the E4 " +
             "timeline, which records every step regardless of this setting. The E1/E2/E3 stages ask a " +
             "different and later question — whether an edge-on-edge contact is resolved into something " +
             "else and comes back — and their stages are hardcoded to EdgeEdge/EdgeFlat/EdgeEdge, so they " +
             "can never fire at a FlatFlat station.\n\n" +
             "Leaving them on does not merely waste effort, it breaks comparability: a capture starts the " +
             "deferred reference enumeration, so the two edge stations would do that heavy pass while Flat " +
             "never does, in a comparison meant to be like-for-like. Turn this back on for the evolution " +
             "question; leave it off while answering Q1.")]
    [SerializeField] private bool captureEvolutionEvents;

    [Tooltip("Re-run the authored-shell query here, independently, on each step the solver classifies, and " +
             "record verifyShellSepPhysics / verifyShellSepVisual / verifyDeltaMm.\n\n" +
             "OFF BY DEFAULT, because it is not free and this is the recorded path. It costs one extra " +
             "full shell query per classificationInterval inside the recorder's own FixedUpdate, which is " +
             "cost a plain play session does not carry. The recording path already differs from an " +
             "unrecorded run in three other ways — solver tracing is enabled (and the package documents a " +
             "traced query as marginally more likely to abort than an untraced one), nine station roots " +
             "are deactivated, and the dwell walk drives State directly instead of letting Auto run — so " +
             "this is one difference worth not adding unless it is being asked for.\n\n" +
             "Turn it on for an instrument-characterisation pass. Leave it off for physical results.")]
    [SerializeField] private bool verifyShellSeparation;

    [Tooltip("Measure the closest approach between the two blades' AUTHORED SHARP EDGES every step, " +
             "independent of the solver and of any contact manifold. This is what makes the approach " +
             "observable instead of inferred: it exists through Pause and SNAP when no pair exists. Costs " +
             "one brute-force pass over the sharp rings per step, which is why it is gated below.")]
    [SerializeField] private bool measureSharpEdgeGap = true;

    [Tooltip("PHASE GATE. The heavy pass will not arm before this phaseState is commanded. 2 = HOLD. " +
             "This exists because the A-pose->SNAP transition swings the swords transiently into reach: in " +
             "recorded runs that transient armed Flat and EdgeNoBind at steps 126 and 105, during the " +
             "blend, while EdgeBind's blend did not — so coverage of the approach depended on a transient " +
             "rather than on the geometry. Nothing before HOLD is commanded is part of the approach.")]
    [Range(0, 3)]
    [SerializeField] private int heavyGateRequiresPhase = 2;

    [Tooltip("Base proximity, metres, for arming the heavy measurement, AND-ed with the phase gate above. " +
             "NOT RATIFIED — this value has never been chosen by the researcher and must not be treated as " +
             "declared. Reference points, measured: all three OurSolution stations settle at SNAP with a " +
             "coarse bounding-sphere gap of 0.441 / 0.477 / 0.489 m, and first contact occurs around 190 " +
             "steps after HOLD is commanded. A base AT OR ABOVE that settle figure makes this gate " +
             "vestigial — it would already be open when HOLD is commanded and would save nothing, which " +
             "defeats its purpose. A base well BELOW it is what actually reduces load, at the cost of a " +
             "coverage hole early in HOLD; whether that hole matters is checkable, because coarseGap is a " +
             "LOWER BOUND recorded on every row and can prove no contact was possible there.")]
    [Min(0f)]
    [SerializeField] private float heavyGateBaseMetres = 0.25f;

    [Tooltip("Buffer multiplier on the closing terms. The blades may be accelerating, so the gate adds " +
             "speed and acceleration allowances and doubles them rather than trying to be exact.")]
    [Min(1f)]
    [SerializeField] private float heavyGateBuffer = 2f;

    [Tooltip("Once armed, stay armed for the rest of the run. On: no flapping, and an approach cannot be " +
             "half-recorded. Off: re-evaluated every step, cheaper but the record can have holes.")]
    [SerializeField] private bool heavyGateLatches = true;

    [Header("Isolation — runtime only, reverts when Play stops")]
    [Tooltip("Deactivates every other Station_* root for the duration of the run, so the watched station " +
             "is the only thing simulating. Applied at Begin and undone at Seal/OnDisable. Nothing is " +
             "saved, so stopping Play restores the scene regardless.")]
    [SerializeField] private bool isolateOtherStations = true;

    [Header("Visual confirmation")]
    [Tooltip("Draws one full-blade-length line per blade, through the winning feature's witness point and " +
             "along that blade's authored long axis, so the line visibly intersects the feature the " +
             "classifier is claiming. Green = the winning feature on that blade is an authored SharpEdge; " +
             "red = it is a face; yellow = anything else. An EdgeFlat verdict therefore shows one green " +
             "and one red line.")]
    [SerializeField] private bool drawWitnessLines = true;

    [Tooltip("Half-length of those lines, metres. Presentation only.")]
    [SerializeField] private float witnessLineHalfLength = 0.6f;

    [SerializeField] private bool drawScreenReadout = true;

    [Header("Reference enumeration")]
    [Tooltip("Exact feature-pair tests per RENDERED FRAME. Run 01 budgeted this per FixedUpdate, and Unity " +
             "runs several fixed steps per frame to catch up, so each frame spent an unbounded multiple of " +
             "the ceiling and the editor bogged. This is a COST CEILING ONLY: the enumeration is " +
             "exhaustive and its result cannot depend on it. It does not start until the dwell schedule " +
             "has finished, so it never competes with the window being watched.")]
    [Min(1000)]
    [SerializeField] private int referenceTestsPerFrame = 60000;

    [Header("Output")]
    /// <remarks>
    ///     BLANK by default, deliberately. It used to ship an absolute path from one machine, which
    ///     both leaked a folder structure into a public repository and silently wrote another
    ///     machine's runs into a directory that did not describe them. Blank cannot do either, and
    ///     the write path below refuses with a named error rather than throwing on
    ///     CreateDirectory("").
    /// </remarks>
    [SerializeField] private string outputDirectory = "";

    [Tooltip("FALSE for a controlled run. Arm with the hotkey below once the scene is up, so pressing Play " +
             "never starts an unattended capture.")]
    [SerializeField] private bool runOnStart;

    [Tooltip("Arms the run. The project is Input System only, so this reads Keyboard.current.\n\n" +
             "The press is only a REQUEST. Input is polled in Update, which is a rendered-frame boundary " +
             "and has no fixed relationship to the physics clock, so arming there starts the run at an " +
             "arbitrary sub-step phase. The request is therefore deferred and consumed in FixedUpdate — " +
             "see armAlignToFixedStepMultiple.")]
    [SerializeField] private Key armKey = Key.F9;

    [Tooltip("A deferred arm request is only consumed on a global fixed step that is an exact multiple " +
             "of this. 90 is one second at the declared 90 Hz, and is the researcher's chosen value.\n\n" +
             "This is what makes a keypress-armed run comparable to another keypress-armed run: both " +
             "start on the same phase of the physics clock instead of wherever the frame boundary fell. " +
             "1 would mean 'the very next fixed step', which is aligned to nothing.")]
    [Min(1)]
    [SerializeField] private int armAlignToFixedStepMultiple = 90;

    /// <summary>
    ///     The GLOBAL fixed-step index, counted from scene start — not this recorder's own step counter.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <see cref="Time.fixedTime" /> is the time at the beginning of the current fixed step, so
    ///         inside <c>FixedUpdate</c> this is the index of the step being simulated. Defined once, here,
    ///         because two components need the same clock and two copies of the definition could drift.
    ///     </para>
    ///     <para>
    ///         Read it in <c>FixedUpdate</c>. In <c>Update</c> it reports the LAST fixed step, which is
    ///         precisely the ambiguity this exists to remove.
    ///     </para>
    /// </remarks>
    public static int CurrentFixedStep =>
        Mathf.RoundToInt(Time.fixedTime / Mathf.Max(Time.fixedDeltaTime, 1e-9f));

    /// <summary>The global fixed step this run was armed on, or -1 if it has not been armed.</summary>
    /// <remarks>
    ///     Written into the sealed file's header. Two runs cannot be compared value-for-value unless this
    ///     matches — see the run-verification rule in HANDOFF_Q1_DONE_AND_Q2_START.md.
    /// </remarks>
    public int ArmedAtFixedStep => armedAtFixedStep;

    private int armedAtFixedStep = -1;
    private bool armRequested;
    private string armRequestSource;

    /// <summary>
    ///     Everything knowable about the instant the run was armed, for the sealed header.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Why the frame count is the important field.</b> The physics DRIVE is fixed-step, and this
    ///         field is not about that. <c>RagdollHandler.FixedUpdateTick</c> runs
    ///         <c>UpdatePhysicalAnimationMatching</c> (the muscle/joint drive), <c>FixedUpdateAttachables</c>
    ///         and the joint <c>connectedMassScale</c> transitions — all per fixed step. The
    ///         <c>LateUpdateTick</c> calls <c>ApplyPhysicalRotationsToTheSkeleton</c> and
    ///         <c>UpdateAttachables</c> are the VISUAL read-back, physics onto the animated skeleton, and do
    ///         not feed physics at all. Do not confuse the visual rig with the runtime ragdoll.
    ///     </para>
    ///     <para>
    ///         What IS frame-paced, and the reason this field exists: <c>CaptureAnimator</c> is in
    ///         <c>LateUpdateTick</c>, so the TARGET the muscles match toward is refreshed once per rendered
    ///         frame. Within a frame the order is FixedUpdate xN then LateUpdate, so N drive ticks chase a
    ///         target captured at the end of the PREVIOUS frame and the intermediate animator poses are
    ///         skipped. Frame pacing therefore reaches physics through the target-capture cadence, not
    ///         through the drive.
    ///     </para>
    ///     <para>
    ///         So two runs armed on the same fixed step have NOT necessarily had the same number of TARGET
    ///         REFRESHES to get there. <see cref="FrameCount" /> against <see cref="GlobalFixedStep" /> is
    ///         that ratio, and it is the first thing to compare when two matched-step runs disagree.
    ///         <b>This is a hypothesis the field exists to TEST, not an established cause.</b>
    ///     </para>
    ///     <para>
    ///         The velocities matter for the same reason: <c>Begin</c> does not reset them, so whatever the
    ///         bodies carry at the arm instant is the run's true initial condition. Nothing else in the file
    ///         records it.
    ///     </para>
    /// </remarks>
    private struct ArmSnapshot
    {
        public bool Captured;
        public int FrameCount, GlobalFixedStep;
        public float TimeSinceLevelLoad, FixedTime;
        public Vector3 PosA, PosB, LinVelA, LinVelB, AngVelA, AngVelB;
        public Quaternion RotA, RotB;
        public string BodyNameA, BodyNameB;
        public int AnimStateHashA, AnimStateHashB;
        public float AnimNormTimeA, AnimNormTimeB;
        public bool AnimInTransitionA, AnimInTransitionB;
    }

    private ArmSnapshot armSnapshot;

    /// <summary>Captures the arm instant. Called from <see cref="Begin" />, which runs in FixedUpdate.</summary>
    private void CaptureArmSnapshot()
    {
        armSnapshot = default(ArmSnapshot);
        armSnapshot.Captured = true;
        armSnapshot.FrameCount = Time.frameCount;
        armSnapshot.GlobalFixedStep = CurrentFixedStep;
        armSnapshot.TimeSinceLevelLoad = Time.timeSinceLevelLoad;
        armSnapshot.FixedTime = Time.fixedTime;

        if (contactA != null)
        {
            BladePose p = contactA.SimulatedPlacement;
            armSnapshot.PosA = p.Position;
            armSnapshot.RotA = p.Rotation;

            Rigidbody b = contactA.Body;
            if (b != null)
            {
                armSnapshot.LinVelA = b.linearVelocity;
                armSnapshot.AngVelA = b.angularVelocity;
                armSnapshot.BodyNameA = b.name;
            }
        }

        if (contactB != null)
        {
            BladePose p = contactB.SimulatedPlacement;
            armSnapshot.PosB = p.Position;
            armSnapshot.RotB = p.Rotation;

            Rigidbody b = contactB.Body;
            if (b != null)
            {
                armSnapshot.LinVelB = b.linearVelocity;
                armSnapshot.AngVelB = b.angularVelocity;
                armSnapshot.BodyNameB = b.name;
            }
        }

        if (animatorA != null)
        {
            AnimatorStateInfo s = animatorA.GetCurrentAnimatorStateInfo(0);
            armSnapshot.AnimStateHashA = s.shortNameHash;
            armSnapshot.AnimNormTimeA = s.normalizedTime;
            armSnapshot.AnimInTransitionA = animatorA.IsInTransition(0);
        }

        if (animatorB != null)
        {
            AnimatorStateInfo s = animatorB.GetCurrentAnimatorStateInfo(0);
            armSnapshot.AnimStateHashB = s.shortNameHash;
            armSnapshot.AnimNormTimeB = s.normalizedTime;
            armSnapshot.AnimInTransitionB = animatorB.IsInTransition(0);
        }

        Debug.Log("[Phase1b] " + stationId + ": ARM SNAPSHOT — frame " + armSnapshot.FrameCount +
                  ", global fixed step " + armSnapshot.GlobalFixedStep + " (" +
                  (armSnapshot.GlobalFixedStep > 0
                      ? (armSnapshot.FrameCount / (float)armSnapshot.GlobalFixedStep).ToString("F4")
                      : "n/a") + " frames per fixed step). " +
                  "|linVel| A " + armSnapshot.LinVelA.magnitude.ToString("F6") + " B " +
                  armSnapshot.LinVelB.magnitude.ToString("F6") + " m/s.", this);
    }

    // ----- captured events -------------------------------------------------------------------------

    private struct PoseIntegrity
    {
        public bool Captured;
        public string ActiveTransformName;
        public string BodyName;
        public RigidbodyInterpolation Interpolation;
        public CollisionDetectionMode CollisionDetection;
        public float PositionDelta;
        public float RotationDeltaDegrees;
    }

    private sealed class Event
    {
        public string Name;
        public BladeClassificationTrace Trace;
        public PoseIntegrity IntegrityA, IntegrityB;
        public int PhaseState, StepsInPhase;

        public readonly float[] MinDistance = new float[CategoryCount];
        public readonly int[] IndexA = new int[CategoryCount];
        public readonly int[] IndexB = new int[CategoryCount];
        public readonly Vector3[] WitnessA = new Vector3[CategoryCount];
        public readonly Vector3[] WitnessB = new Vector3[CategoryCount];

        /// <summary>Re-measurement of production's OWN winning pair. Must reproduce it exactly.</summary>
        public float ProductionPairRemeasured = float.NaN;

        public bool ReferenceComplete;
        public long ReferenceTests;
    }

    private readonly List<Event> events = new List<Event>();
    private readonly List<GameObject> isolated = new List<GameObject>();

    /// <summary>
    ///     One row per fixed step for the whole walk, whether or not anything is captured.
    /// </summary>
    /// <remarks>
    ///     Run 01 could not say which phase its events were in, and run 02 captured nothing at all — both
    ///     failures of the same kind: no record of what the rig was doing when. This timeline is that
    ///     record. It says, per step, what phase was commanded, whether the solver holds a live pair for
    ///     this blade pair at all (which is the only way a classification can happen), and what the current
    ///     verdict and separation are. It is written even when zero events are captured, because "the pair
    ///     was never in contact during the declared window" is itself the finding in that case.
    /// </remarks>
    private readonly List<string> timeline = new List<string>();

    /// <summary>
    ///     The four callback-sourced fields — signedOverlap, contactCount and the two deepest-contact
    ///     collider names — one pre-joined entry per timeline row, held out of the row string so they can be
    ///     shifted up one row when the file is written.
    /// </summary>
    /// <remarks>
    ///     Entry i was captured while writing row i, but it describes row i−1: collision callbacks for a
    ///     step only exist after that step is simulated. Written inline they would put one-step-old values
    ///     on a row where every other column is current.
    /// </remarks>
    private readonly List<string> callbackFieldsByRow = new List<string>();

    /// <summary>
    ///     The v1 latched-attribution and bind-gate fields, one pre-joined entry per timeline row.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Held out of the row string for a different reason than
    ///         <see cref="callbackFieldsByRow" />: these are NOT shifted, they are this row's own values.
    ///         They are kept separate purely so that the 46-column layout stays an exact PREFIX of this
    ///         one. The four shifted columns are appended last by <c>BuildTimeline</c>, so anything written
    ///         inline would land in front of them and move columns 43-46. Columns 1-46 of a file written
    ///         now are the same columns, in the same positions, as a file written before v1 existed.
    ///     </para>
    ///     <para>
    ///         Adding columns is what made the 43-column and 46-column files non-comparable. This layout
    ///         change is unavoidable — v1's whole output is the second fact — but it costs only the
    ///         columns it adds.
    ///     </para>
    /// </remarks>
    private readonly List<string> latchFieldsByRow = new List<string>();

    /// <summary>Empty cells for all fourteen v1 columns, for a row that has no solver pair.</summary>
    private const string EmptyLatchFields = ",,,,,,,,,,,,,";

    /// <summary>Makes a value safe to drop into a CSV cell, since collider names are not controlled here.</summary>
    private static string Csv(string v)
    {
        if (string.IsNullOrEmpty(v)) return "";
        return v.IndexOf(',') >= 0 || v.IndexOf('"') >= 0
            ? "\"" + v.Replace("\"", "\"\"") + "\""
            : v;
    }

    private bool running, sealedAlready, armedThisSession;

    /// <summary>The declared station label, as it appears in the output filename.</summary>
    public string StationId => stationId;

    /// <summary>True between <see cref="Begin" /> and <see cref="Seal" />.</summary>
    public bool IsRunning => running;

    /// <summary>True once this recorder has sealed a run.</summary>
    public bool HasSealed => sealedAlready;

    /// <summary>
    ///     True once this recorder has been armed in this Play session, whether or not it has sealed.
    /// </summary>
    /// <remarks>
    ///     Exposed so an automated arm can see the one-arm-per-session refusal BEFORE tripping it, rather
    ///     than discovering it as a LogError. It does not relax the refusal — <see cref="Begin" /> still
    ///     refuses on its own.
    /// </remarks>
    public bool HasArmedThisSession => armedThisSession;
    private int lastSeenTraceStep = -1;
    private int rejectedOtherPair, rejectedOutOfWindow;
    private int stage;

    private int step, phaseState = -1, phaseEnteredAtStep, sequenceCursor, cursorDeadline;
    private bool walkComplete;

    /// <summary>Set at <see cref="Begin"/>; makes the first fixed step after arming unrecorded.</summary>
    private bool skipFirstStep;

    private Event pending;
    private int cursorA, cursorB, lastEnumeratedFrame = -1;

    private LineRenderer lineA, lineB;
    private BladeClassificationTrace liveForVisual;

    private int StepsInPhase => step - phaseEnteredAtStep;

    private bool InCaptureWindow =>
        phaseState == captureInPhaseState && StepsInPhase >= captureMinStepsInPhase;

    /// <summary>
    ///     Refuses to operate while an enabled <see cref="RqStationRunner" /> shares this GameObject.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Two arming systems, one keyboard.</b> This recorder and <c>RqStationRunner</c> are separate
    ///         instruments that both watch <c>Keyboard.current</c> and both set
    ///         <c>isolateOtherStations</c>. The runner's static <c>Active</c> guard only refuses OTHER
    ///         RUNNERS — it cannot see this class — so nothing prevented one keypress arming both. Measured
    ///         2026-08-29: the ten runners carried F1–F10 while the three fork recorders carried F7/F8/F9,
    ///         so F7, F8 and F9 each armed two systems, two of them on DIFFERENT stations. Each would then
    ///         deactivate the station the other was measuring.
    ///     </para>
    ///     <para>
    ///         The keys are now deliberately IDENTICAL per station rather than merely distinct, and this
    ///         gate is what makes that safe: sharing a key is only sound if exactly one of the two can ever
    ///         respond to it. This class yields, because the runner drives the Chapter 4 comparison window
    ///         and this one does not (see the scope banner at the top of this file).
    ///     </para>
    ///     <para>
    ///         A LogError rather than a silent return. A recorder that quietly declined to arm would look
    ///         exactly like one that armed and produced nothing, and that ambiguity has cost runs before.
    ///     </para>
    /// </remarks>
    private bool BlockedByStationRunner()
    {
        var runner = GetComponent<RqStationRunner>();
        if (runner == null || !runner.enabled) return false;

        Debug.LogError("[Phase1b] " + stationId + ": an ENABLED RqStationRunner is on this same " +
                       "GameObject, so this recorder REFUSES to arm or run. They are two arming systems " +
                       "sharing one keyboard and both isolate other stations, and the runner's own guard " +
                       "cannot see this class. To use this recorder, disable the RqStationRunner on this " +
                       "station first — and remember that doing so removes the station from the Chapter 4 " +
                       "comparison, which is the runner's window, not this one's.", this);
        return true;
    }

    private void OnEnable()
    {
        // Fires the moment someone re-enables this component in the inspector, which is when the conflict
        // is worth hearing about — not later, at the keypress, with a run already half set up.
        BlockedByStationRunner();
    }

    private void Start()
    {
        // Routed through the same deferral as the hotkey. Start is not Update, but it is not a fixed-step
        // boundary either, and nothing here may begin a run off the physics clock.
        if (runOnStart) RequestArm("runOnStart");
    }

    private void Update()
    {
        Keyboard kb = Keyboard.current;
        if (kb == null || running || sealedAlready) return;
        if (!kb[armKey].wasPressedThisFrame) return;

        RequestArm("hotkey " + armKey);
    }

    /// <summary>
    ///     Records an intent to arm. The run itself begins in <c>FixedUpdate</c>, never here.
    /// </summary>
    private void RequestArm(string source)
    {
        if (armRequested || running || sealedAlready) return;
        if (BlockedByStationRunner()) return;

        armRequested = true;
        armRequestSource = source;

        Debug.Log("[Phase1b] " + stationId + ": arm REQUESTED by " + source + " at global fixed step " +
                  CurrentFixedStep + ". Deferred to the next fixed step that is a multiple of " +
                  armAlignToFixedStepMultiple + ", so the run starts on a known phase of the physics " +
                  "clock rather than on a frame boundary.", this);
    }

    /// <summary>
    ///     Consumes a deferred arm request, on an aligned fixed step. Called from the top of
    ///     <c>FixedUpdate</c>, before the running guard.
    /// </summary>
    private void ServiceArmRequest()
    {
        if (!armRequested || running || sealedAlready) return;
        if (CurrentFixedStep % armAlignToFixedStepMultiple != 0) return;

        armRequested = false;
        Begin();
    }

    /// <summary>
    ///     <see cref="Begin" />, recording WHO armed it so the sealed header carries the provenance.
    /// </summary>
    /// <remarks>
    ///     Callers must invoke this from <c>FixedUpdate</c>. Nothing in this apparatus may start a run
    ///     from a rendered-frame boundary.
    /// </remarks>
    public void BeginArmedBy(string source)
    {
        armRequestSource = source;
        Begin();
    }

    [ContextMenu("Begin")]
    public void Begin()
    {
        if (running) return;

        if (solver == null || animatorA == null || animatorB == null)
        {
            Debug.LogError("[Phase1b] solver and both animators are required. Nothing started.", this);
            return;
        }

        // A second run inside one Play session inherits the FIRST run's end state. The dwell walk
        // re-commands State 0, but the bodies stay where they finished, so the blades can already be in
        // contact at step 1 -- which is exactly what happened once, producing contact through Pause and
        // SNAP that read as a physical result and was an artefact of re-arming. Refused rather than warned:
        // the resulting file is not distinguishable from a clean one by inspection.
        if (armedThisSession)
        {
            Debug.LogError("[Phase1b] REFUSED: this recorder has already run once in this Play session. " +
                           "Its end state would contaminate the start state. Stop Play, enter Play again, " +
                           "and arm once.", this);
            return;
        }

        armedThisSession = true;
        armedAtFixedStep = CurrentFixedStep;
        CaptureArmSnapshot();

        events.Clear();
        timeline.Clear();
        callbackFieldsByRow.Clear();
        latchFieldsByRow.Clear();
        stage = 0;
        rejectedOtherPair = rejectedOutOfWindow = 0;
        pending = null;
        walkComplete = false;
        sealedAlready = false;
        lastSeenTraceStep = solver.LastClassificationTraceStep;

        CollectSharpRings(contactA, sharpRingsA, out sharpRingsAText);
        CollectSharpRings(contactB, sharpRingsB, out sharpRingsBText);
        CollectSharpSegments(contactA, segsA);
        CollectSharpSegments(contactB, segsB);
        heavyArmed = false;
        gateTrip = default(GateTrip);
        haveRelVelocity = false;
        prevRelVelocity = Vector3.zero;

        if (isolateOtherStations) Isolate();

        ClearCarriedContact();

        // Auto false is what enables direct State addressing; the controller's ANY-state transitions are
        // gated on it. Both sides, or the two dummies desynchronise.
        animatorA.SetBool(AutoParam, false);
        animatorB.SetBool(AutoParam, false);

        step = 0;
        skipFirstStep = true;
        sequenceCursor = 0;
        SetState(0);
        cursorDeadline = dwellPauseSteps;

        solver.TraceClassifications = true;
        running = true;

        Debug.Log("[Phase1b] " + stationId + " (" + referencePose + "): ARMED. Auto=false, walking " +
                  "Pause->SNAP->HOLD->CONTACT with dwells " + dwellPauseSteps + "/" + dwellSnapSteps + "/" +
                  dwellHoldSteps + "/" + dwellContactSteps + " at " +
                  (1f / Time.fixedDeltaTime).ToString("F1") + " Hz. Capturing only in phase " +
                  captureInPhaseState + " at stepsInPhase >= " + captureMinStepsInPhase + ".", this);
    }

    private void SetState(int value)
    {
        animatorA.SetInteger(StateParam, value);
        animatorB.SetInteger(StateParam, value);
        phaseState = value;
        phaseEnteredAtStep = step;

        // The approach begins when the gate phase is commanded. Anything the blades did before that
        // belongs to an earlier part of the cycle, and a Pair record carrying it would be attributed to
        // this run — including a breakaway that happened before the run's approach existed.
        if (value == heavyGateRequiresPhase) ClearCarriedContact();
    }

    /// <summary>
    ///     Drops any solver Pair these two blades are in, and the visual it was driving.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A <c>Pair</c> record outlives the contact that created it. The solver keeps the last RawId,
    ///         Scenario, ShellSeparation and ClassWrittenAtStep after the blades separate and re-publishes
    ///         them on every later row, so a run that began while the blades were touching reports an
    ///         attribution written before it started.
    ///     </para>
    ///     <para>
    ///         Called twice: at <see cref="Begin" />, and again when the gate phase is commanded. The
    ///         second call is the one that matters, because <c>Begin</c> cannot separate blades that are
    ///         already touching — it commands Pause, it does not place the bodies. The blades do come
    ///         apart on their own during the Pause and SNAP dwells, but the pair record does not, so
    ///         without this the record never registers a NEW pair at the approach and the
    ///         classify-on-first-step that Q1 reads simply never happens.
    ///     </para>
    ///     <para>
    ///         Nothing is fabricated and no physics changes: the solver rebuilds the pair with a fresh
    ///         Readout the next time PhysX reports these two colliders touching.
    ///     </para>
    /// </remarks>
    private void ClearCarriedContact()
    {
        if (solver == null) return;

        solver.Forget(contactA);
        solver.Forget(contactB);

        // Driven only by traces and read only by the witness lines and the screen readout, so it never
        // reaches a recorded value — but left standing it draws the PREVIOUS contact point while the
        // blades are visibly moving apart, which is exactly the signal a human uses to judge the run.
        liveForVisual = null;
    }

    /// <summary>Walks the declared dwell schedule. Dwells are sample sizes, not settling criteria.</summary>
    private void WalkPhases()
    {
        if (walkComplete || step < cursorDeadline) return;

        switch (sequenceCursor)
        {
            case 0:
                sequenceCursor = 1;
                SetState(1);
                cursorDeadline = step + dwellSnapSteps;
                break;
            case 1:
                sequenceCursor = 2;
                SetState(2);
                cursorDeadline = step + dwellHoldSteps;
                break;
            case 2:
                sequenceCursor = 3;
                SetState(3);
                cursorDeadline = step + dwellContactSteps;
                break;
            default:
                walkComplete = true;
                Debug.Log("[Phase1b] dwell schedule complete at step " + step + ". " +
                          (captureEvolutionEvents
                              ? "Captured " + events.Count + " of 3; out-of-window rejects " +
                                rejectedOutOfWindow + ". Starting the deferred reference enumeration."
                              : "Q1 run: E1/E2/E3 evolution capture is SUPPRESSED, so zero captures is " +
                                "the declared configuration and not a fault. Read the E4 timeline. " +
                                "Classifications seen this run: " +
                                (rejectedOutOfWindow + rejectedOtherPair) + " outside the declared " +
                                "window or pair."), this);
                break;
        }
    }

    private void FixedUpdate()
    {
        ServiceArmRequest();

        if (!running) return;

        // Wait a frame, so recording only starts once the expensive hooks are in.
        //
        // Begin does a lot on the step it is pressed: it isolates nine station roots, forgets the solver
        // pairs, flips both animators out of Auto and commands the first phase, and switches solver
        // tracing on. The very first fixed step after that is measuring the apparatus settling into the
        // run rather than the run itself, and it produced a first-row disagreement of 10 mm between the
        // solver's published separation and this recorder's independent reproduction of it — a step where
        // the two were plainly not looking at the same instant.
        //
        // That first step is never going to be a valid measurement, so it is skipped rather than
        // explained. Step 1 of the sealed file is the SECOND fixed step after Begin; the preflight says
        // so, and every dwell is counted from there, so the walk is shifted uniformly and nothing inside
        // it is distorted.
        if (skipFirstStep)
        {
            skipFirstStep = false;
            return;
        }

        step++;
        WalkPhases();
        PollForNewTrace();
        RecordTimelineRow();

        if (walkComplete)
        {
            if (pending == null) StartNextReference();
            if (stage >= 0 && pending == null && AllReferencesComplete()) Seal();
        }
    }

    private void OnDisable()
    {
        if (solver != null) solver.TraceClassifications = false;

        if (running && events.Count > 0 && !sealedAlready)
        {
            Debug.LogWarning("[Phase1b] disabled with " + events.Count + " event(s) captured; sealing.", this);
            Seal();
        }

        Restore();
        running = false;
    }

    // ----- isolation -------------------------------------------------------------------------------

    private void Isolate()
    {
        isolated.Clear();
        GameObject mine = gameObject;

        foreach (var root in gameObject.scene.GetRootGameObjects())
        {
            if (!root.name.StartsWith("Station_")) continue;
            if (root == mine || root.transform.IsChildOf(mine.transform) ||
                mine.transform.IsChildOf(root.transform)) continue;
            if (!root.activeSelf) continue;

            root.SetActive(false);
            isolated.Add(root);
        }

        Debug.Log("[Phase1b] isolated " + isolated.Count + " other station root(s) for this run. " +
                  "Runtime only — stopping Play restores them.", this);
    }

    private void Restore()
    {
        for (int i = 0; i < isolated.Count; i++)
            if (isolated[i] != null) isolated[i].SetActive(true);

        isolated.Clear();
    }

    // ----- capture ---------------------------------------------------------------------------------

    private static PoseIntegrity ReadIntegrity(RqBladeContact blade)
    {
        var p = new PoseIntegrity();
        if (blade == null) return p;

        Transform t = blade.ActiveTransform;
        if (t == null) return p;

        p.Captured = true;
        p.ActiveTransformName = t.name;

        Rigidbody rb = t.GetComponent<Rigidbody>();
        if (rb == null)
        {
            p.BodyName = "(none on ActiveTransform)";
            return p;
        }

        p.BodyName = rb.name;
        p.Interpolation = rb.interpolation;
        p.CollisionDetection = rb.collisionDetectionMode;
        p.PositionDelta = Vector3.Distance(t.position, rb.position);
        p.RotationDeltaDegrees = Quaternion.Angle(t.rotation, rb.rotation);
        return p;
    }

    private void PollForNewTrace()
    {
        int traceStep = solver.LastClassificationTraceStep;
        if (traceStep == lastSeenTraceStep) return;

        lastSeenTraceStep = traceStep;

        BladeClassificationTrace t = solver.LastClassificationTrace;
        if (t == null || !t.ClassificationValid) return;

        if (t.LabelA != requireLabelA || t.LabelB != requireLabelB)
        {
            rejectedOtherPair++;
            return;
        }

        // Independent second condition, written by a different code path than ClassificationValid. A
        // disagreement is an instrument fault, not a datum.
        if (t.Completion != BladeQueryCompletion.Completed)
        {
            Debug.LogError("[Phase1b] step " + t.Step + ": classification reported valid but query " +
                           "completion is " + t.Completion + ". Instrument fault — not captured.", this);
            return;
        }

        liveForVisual = t;

        if (!InCaptureWindow)
        {
            rejectedOutOfWindow++;
            return;
        }

        // Q1 is answered by the timeline, not by these. Suppressed rather than left to no-op, so that
        // every station does the same work: a capture starts the deferred reference enumeration, and an
        // edge station would run that heavy pass while a flat station never reaches stage 0 at all.
        if (!captureEvolutionEvents) return;

        BladeContactScenario s = t.Scenario;
        if (stage == 0 && s == BladeContactScenario.EdgeEdge) Capture("E1_first_EdgeEdge", t);
        else if (stage == 1 && s == BladeContactScenario.EdgeFlat) Capture("E2_first_EdgeFlat", t);
        else if (stage == 2 && s == BladeContactScenario.EdgeEdge) Capture("E3_return_EdgeEdge", t);
    }

    private void Capture(string name, BladeClassificationTrace live)
    {
        var e = new Event
        {
            Name = name,
            Trace = live.Clone(),
            IntegrityA = ReadIntegrity(contactA),
            IntegrityB = ReadIntegrity(contactB),
            PhaseState = phaseState,
            StepsInPhase = StepsInPhase
        };

        for (int i = 0; i < CategoryCount; i++)
        {
            e.MinDistance[i] = float.MaxValue;
            e.IndexA[i] = -1;
            e.IndexB[i] = -1;
        }

        events.Add(e);
        stage++;

        Debug.Log("[Phase1b] captured " + name + " at step " + e.Trace.Step + " (" +
                  PhaseName(phaseState) + " +" + e.StepsInPhase + "): " + e.Trace.Scenario + ", " +
                  e.Trace.Candidates.Count + " candidates, completion " + e.Trace.Completion +
                  (e.Trace.Overflow > 0 ? ", OVERFLOW " + e.Trace.Overflow : ""), this);
    }

    /// <summary>
    ///     Four separations are recorded per row and they are NOT interchangeable. Kept as distinct columns
    ///     on purpose, because the whole point is to be able to see them disagree.
    /// </summary>
    /// <remarks>
    ///     <list type="bullet">
    ///         <item>
    ///             <c>shellSeparation</c> — what the SOLVER published. Authored shell, no inflation, but
    ///             written only inside <c>Classify</c>, so HELD for up to <c>classificationInterval</c>
    ///             steps. This is the value OurSolution actually acted on.
    ///         </item>
    ///         <item>
    ///             <c>verifyShellSepPhysics</c> — the same authored-shell query, recomputed HERE at the
    ///             simulating body's pose, on the step the solver classified.
    ///             <b>This must equal <c>shellSeparation</c>.</b> They are the ground truth and the
    ///             instrument's independent reproduction of it; a disagreement means the solver is not
    ///             measuring what it claims to.
    ///         </item>
    ///         <item>
    ///             <c>verifyShellSepVisual</c> — the same query at the RENDERED (interpolated) transform
    ///             pose. Present only to show what the old, wrong pose source would have said. Never a
    ///             result. On 2026-08-23 this read 12.96 mm where the body pose read 7.90 mm on an
    ///             identical feature pair.
    ///         </item>
    ///         <item>
    ///             <c>signedOverlap</c> — PhysX's own contact separation, against the COOKED CONVEX HULL
    ///             inflated by <c>contactOffset</c>. Positive means a gap. It is systematically larger than
    ///             the shell figures and is reported for context only; it is not a blade separation and no
    ///             OurSolution number is derived from it.
    ///         </item>
    ///     </list>
    ///     Plus <c>poseLagMm</c> / <c>poseLagDeg</c>, how far the drawn pose sits from the simulated one,
    ///     so the size of the error that was previously being measured stays visible on every row.
    /// </remarks>
    private const string TimelineHeader =
        "step,phaseState,phaseName,stepsInPhase,inDeclaredWindow,pairExists,classificationValid," +
        "scenario,rawTypeA,rawTypeB,rawIdA,rawIdB,shellSeparation,classWrittenAtStep,classAgeSteps," +
        "tangentialState,normalForce,ringA,ringB,ringAIsAuthoredSharp,ringBIsAuthoredSharp," +
        "authoredSharpRingsA,authoredSharpRingsB,classifyAttemptedAtStep," +
        "coarseGap,heavyArmed,sharpEdgeGap,sharpGapRingA,sharpGapRingB," +
        "poseSourceA,poseSourceB,poseLagMmA,poseLagMmB,poseLagDegA,poseLagDegB," +
        // verifyDeltaMm is the raw difference. There is deliberately no verifyMatchesSolver pass/fail
        // column: what counts as agreement is a declared criterion and is not this recorder's to invent.
        // A column was previously declared here and never written, which shifted every field after
        // verifyFeatureB one to the left and made verifyDeltaMm read empty while its values sat under the
        // wrong name. Header and writer must be changed together.
        "verifyShellSepPhysics,verifyShellSepVisual,verifyFeatureA,verifyFeatureB," +
        "verifyDeltaMm," +
        // classificationStale=1 means this row's attribution was CARRIED FORWARD from an earlier step,
        // because a later query found no usable witness. classificationValid stays 1 through that, so
        // without this column a stale attribution is indistinguishable from a fresh one — which is how a
        // frozen readout was once reported as a 95.3 % occupancy figure and had to be retracted.
        "classificationStale,failedQueries," +
        // LAST FOUR COLUMNS, AND THEY ARE ALL SHIFTED TOGETHER. Every one of them is populated in
        // ReportCollision from PhysX collision callbacks, and callbacks for step N arrive only after step N
        // is simulated, so the solver consumes them at N+1 and publishes them one step old. Rather than
        // print stale numbers on a row that claims to be one instant, the values captured on row N+1 are
        // written onto row N — the row they actually describe. The last row therefore has none of them,
        // because the measurements that would fill it belong to a step after the walk ended.
        //
        // The solver-read lag that used to add a SECOND step is gone: DefaultExecutionOrder now puts this
        // recorder after the solver. Every other column on a row is that row's own step.
        //
        // contactCount and the two collider names are here to answer one specific question: whether
        // signedOverlap is still describing the same features the classifier is attributing. It agrees with
        // the authored gap to microns while the blades are apart and then contradicts it IN SIGN once
        // contact starts — -14.4308 mm against an authored gap of +6.1808 mm on one row of a good run. If
        // the deepest contact PhysX reports is on a different shape, or the count is jumping, that is
        // visible here rather than inferred. Nothing is claimed about it yet.
        "signedOverlapShifted,contactCountShifted,contactColliderAShifted,contactColliderBShifted," +
        // ---- v1: the LATCHED fact (columns 47-60) -----------------------------------------------------
        //
        // Columns 1-46 above are unchanged and in their old positions, so a 66-column file is a 46-column
        // file with twenty columns appended. Everything here is this row's own step; none of it is
        // shifted.
        //
        // WHY THESE EXIST. The columns above report what the classifier says NOW. It moves, and it is
        // supposed to: a contact that engages edge-on-edge becomes something else as the solver resolves
        // it, and at Flat a correct first attribution has been observed degrading into a spurious sharp
        // one about 21 steps later. Both facts are true and the design's rule is that the second may not
        // overwrite the first. So INITIAL APPROACH is latched at the pair's first successful
        // classification and published alongside CONTACT EVOLUTION for the rest of the pair's life.
        //
        // initialNormalForce is the load at the latch and is expected to read 0 — the sealed Q1 runs
        // latched at zero force at all three stations, ~20 mm apart. A non-zero value there means the
        // latch was taken after load arrived and the row is NOT the Q1 observation.
        //
        // latchGeneration is the LIVENESS figure. Two runs agreeing on initialScenario means nothing if
        // neither pair ever latched; 0 says nothing latched, 1 says one latch held, >1 says
        // relatchOnReEngagement fired and the latched fact is not the first-pair fact.
        //
        // RETIRED 2026-08-25: six further columns existed here — bindGateState, bindLatched,
        // bindLatchedAtStep, pressureFirstLoadN, pressurePeakSinceLoadN, stepsSinceFirstLoad — for a
        // pressure-gated bind. Removed with the mechanism; see the RETIRED block in
        // RqBladeTangentialSolver. Files sealed between 2026-08-25 00:26 and 01:02 are 66 columns and
        // carry those six; anything sealed after is 60. Columns 1-60 are identical across both.
        "initialLatched,initialScenario,initialRawTypeA,initialRawTypeB,initialRawIdA,initialRawIdB," +
        "initialRingA,initialRingB,initialLatchedAtStep,initialNormalForce,initialShellSeparation," +
        "latchGeneration,scenarioEvolved,governingScenario";

    /// <summary>
    ///     One authored sharp-edge segment, kept in local space for per-step transformation.
    /// </summary>
    private struct SharpSeg
    {
        public string Ring;
        public Vector3 A, B;
    }

    private readonly List<SharpSeg> segsA = new List<SharpSeg>();
    private readonly List<SharpSeg> segsB = new List<SharpSeg>();
    private readonly List<Vector3> worldA = new List<Vector3>();
    private readonly List<Vector3> worldB = new List<Vector3>();

    private static void CollectSharpSegments(RqBladeContact blade, List<SharpSeg> into)
    {
        into.Clear();
        if (blade == null) return;

        BladeShellData d = blade.Data;
        if (d == null) return;

        for (int i = 0; i < d.EdgeCount; i++)
        {
            var r = new BladeFeatureRef(BladeFeatureKind.Edge, i);
            if (d.TypeOf(r) != BladeFeatureType.SharpEdge) continue;

            BladeEdgeLine e = d.GetEdge(i);
            into.Add(new SharpSeg { Ring = RingOf(d.IdOf(r)), A = e.LocalStart, B = e.LocalEnd });
        }
    }

    /// <summary>
    ///     Closest approach between the two blades' AUTHORED SHARP EDGES, measured fresh every step.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Why this is separate from everything else in the row.</b> Every other geometric number
    ///         here comes through the solver, so it exists only once PhysX has reported a pair, and
    ///         <c>shellSeparation</c> is additionally HELD between classifications. That makes the moment
    ///         the cutting edges actually meet unmeasurable — it has to be inferred, and inferring it means
    ///         assuming it.
    ///     </para>
    ///     <para>
    ///         This is computed directly from the authored shells at the live pose, independent of the
    ///         solver, of any manifold, and of the classification cadence. It therefore exists through
    ///         Pause and SNAP when no pair exists at all, which is precisely the interval where the
    ///         approach happens. It says nothing about pressure or attribution, only how far apart the two
    ///         sets of authored cutting edges are, and which rings are closest.
    ///     </para>
    ///     <para>
    ///         Unsigned, like everything else the geometry routines return. It cannot show a crossing.
    ///     </para>
    ///     <para>
    ///         <b>A different quantity from
    ///         <see cref="RqBladeTangentialSolver.Snapshot.ShellSeparation" />, not a check on it.</b>
    ///         That one is the separation between the two features the classifier SELECTED, taken from
    ///         <see cref="BladeContact.BladeFeaturePair.Separation" />. This one is a segment-to-segment
    ///         minimum over every authored sharp edge, whether or not the classifier chose any of them.
    ///         On <c>Station_EdgeNoBind_OurSolution</c> at first contact the two read 20.5 mm and 12.6 mm
    ///         on the same step while both named ring <c>e6</c> on both blades. That difference is
    ///         recorded, not explained. Quote whichever answers the question being asked, say which, and
    ///         never present one as corroborating the other.
    ///     </para>
    /// </remarks>
    private bool heavyArmed;
    private Vector3 prevRelVelocity;
    private bool haveRelVelocity;

    /// <summary>
    ///     O(1) lower bound on the blades' separation, from their shells' bounding spheres.
    /// </summary>
    /// <remarks>
    ///     A bound, not a measurement: it is what the heavy pass is gated on, and it is recorded on every
    ///     row so a run can show whether the gate opened in time rather than being trusted to have.
    /// </remarks>
    private float CoarseGap()
    {
        if (contactA == null || contactB == null) return float.NaN;

        BladeShellData da = contactA.Data, db = contactB.Data;
        if (da == null || db == null) return float.NaN;

        Vector3 ca = contactA.SimulatedPlacement.TransformPoint(da.BoundingCentre);
        Vector3 cb = contactB.SimulatedPlacement.TransformPoint(db.BoundingCentre);
        return (ca - cb).magnitude - da.BoundingRadius - db.BoundingRadius;
    }

    /// <summary>
    ///     Whether the blades are close enough to be worth the brute-force sharp-edge pass.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Deliberately approximate. The reach allowance is the base proximity plus a step's worth of
    ///         closing at the current relative speed plus a step's worth of the current relative
    ///         acceleration, all scaled by the buffer — because the blades may still be accelerating and an
    ///         exact prediction is not the point. Being generous costs a few milliseconds; being tight
    ///         loses the approach.
    ///     </para>
    ///     <para>
    ///         Acceleration is differenced from the previous step's relative velocity, so the first step
    ///         after arming has no acceleration term. That is harmless — the base term dominates.
    ///     </para>
    /// </remarks>
    private bool HeavyGateOpen(float coarse)
    {
        if (heavyGateLatches && heavyArmed) return true;
        if (float.IsNaN(coarse)) return false;

        // PHASE GATE, checked before the distance term. The approach begins when HOLD is commanded;
        // anything earlier is the A-pose-to-SNAP swing, which passes transiently within reach and is not
        // an approach at all. Gating on distance alone let that transient decide whether the approach got
        // recorded, which is how one station lost the first 26 steps of it.
        if (phaseState < heavyGateRequiresPhase) return false;

        float dt = Time.fixedDeltaTime;
        Vector3 relVel = Vector3.zero;

        Rigidbody ra = contactA != null ? contactA.Body : null;
        Rigidbody rb = contactB != null ? contactB.Body : null;
        if (ra != null && rb != null) relVel = ra.linearVelocity - rb.linearVelocity;

        Vector3 relAcc = haveRelVelocity && dt > 0f ? (relVel - prevRelVelocity) / dt : Vector3.zero;
        prevRelVelocity = relVel;
        haveRelVelocity = true;

        float reach = heavyGateBaseMetres +
                      heavyGateBuffer * (relVel.magnitude * dt + relAcc.magnitude * dt * dt);

        bool open = coarse <= reach;

        if (open && !heavyArmed) CacheGateTrip(coarse, reach, relVel, relAcc);

        if (open) heavyArmed = true;
        return open;
    }

    /// <summary>
    ///     Captures the full state at the ONE step the heavy gate first opens, for the sealed header.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Why this exists.</b> <c>sharpEdgeGap</c> is gated, so the authored-shell geometry does not
    ///         exist on any row before the gate opens — observed at step 568/569. The first authored
    ///         measurement two runs can be compared on is therefore whatever the gate happens to admit,
    ///         and it already differs between runs when it arrives.
    ///     </para>
    ///     <para>
    ///         This does NOT extend the recording window and does not add a column. It caches the state at
    ///         the trip and writes it into the header, so the trip becomes a single precisely-located event
    ///         two runs can be compared on: the same gate condition, the same instant, full precision.
    ///     </para>
    ///     <para>
    ///         The body poses are the point. Every geometric column in the file is a DERIVED scalar; the
    ///         raw simulated placement of the two blades is not recorded anywhere, so two runs cannot
    ///         currently be compared on where the blades actually were. This records it, from
    ///         <c>SimulatedPlacement</c> — the generated ragdoll proxy's body, never the authored transform.
    ///     </para>
    ///     <para>
    ///         Written with round-trip float formatting, because the differences being chased are at float
    ///         epsilon: two runs armed on the same fixed step were measured differing by 0.0005 mm on a
    ///         1200 mm coarse gap at step 1.
    ///     </para>
    /// </remarks>
    private void CacheGateTrip(float coarse, float reach, Vector3 relVel, Vector3 relAcc)
    {
        gateTrip.Captured = true;
        gateTrip.RecorderStep = step;
        gateTrip.GlobalFixedStep = CurrentFixedStep;
        gateTrip.FrameCount = Time.frameCount;
        gateTrip.PhaseState = phaseState;
        gateTrip.StepsInPhase = StepsInPhase;
        gateTrip.CoarseGap = coarse;
        gateTrip.Reach = reach;
        gateTrip.RelVelocity = relVel;
        gateTrip.RelAcceleration = relAcc;

        // The first authored-shell measurement that exists in the run, taken on the trip step itself.
        string ra, rb;
        gateTrip.SharpEdgeGap = SharpEdgeGap(out ra, out rb);
        gateTrip.SharpRingA = ra;
        gateTrip.SharpRingB = rb;

        if (contactA != null)
        {
            BladePose pa = contactA.SimulatedPlacement;
            gateTrip.PosA = pa.Position;
            gateTrip.RotA = pa.Rotation;
        }

        if (contactB != null)
        {
            BladePose pb = contactB.SimulatedPlacement;
            gateTrip.PosB = pb.Position;
            gateTrip.RotB = pb.Rotation;
        }

        Debug.Log("[Phase1b] " + stationId + ": heavy gate TRIPPED at recorder step " +
                  gateTrip.RecorderStep + " (global fixed step " + gateTrip.GlobalFixedStep +
                  ", phase " + gateTrip.PhaseState + " +" + gateTrip.StepsInPhase +
                  "). coarseGap " + (coarse * 1000f).ToString("F4") + " mm against reach " +
                  (reach * 1000f).ToString("F4") + " mm. State cached for the header.", this);
    }

    private struct GateTrip
    {
        public bool Captured;
        public int RecorderStep, GlobalFixedStep, FrameCount, PhaseState, StepsInPhase;
        public float CoarseGap, Reach, SharpEdgeGap;
        public string SharpRingA, SharpRingB;
        public Vector3 RelVelocity, RelAcceleration;
        public Vector3 PosA, PosB;
        public Quaternion RotA, RotB;
    }

    private GateTrip gateTrip;

    private float SharpEdgeGap(out string ringA, out string ringB)
    {
        ringA = ringB = null;
        if (segsA.Count == 0 || segsB.Count == 0 || contactA == null || contactB == null)
            return float.NaN;

        BladePose pa = contactA.SimulatedPlacement, pb = contactB.SimulatedPlacement;

        worldA.Clear();
        for (int i = 0; i < segsA.Count; i++)
        {
            worldA.Add(pa.TransformPoint(segsA[i].A));
            worldA.Add(pa.TransformPoint(segsA[i].B));
        }

        worldB.Clear();
        for (int i = 0; i < segsB.Count; i++)
        {
            worldB.Add(pb.TransformPoint(segsB[i].A));
            worldB.Add(pb.TransformPoint(segsB[i].B));
        }

        float best = float.MaxValue;
        for (int i = 0; i < segsA.Count; i++)
        {
            Vector3 a0 = worldA[i * 2], a1 = worldA[i * 2 + 1];
            for (int j = 0; j < segsB.Count; j++)
            {
                Vector3 w1, w2;
                float d = SegmentGeometry.ClosestPointsBetweenSegments(
                    a0, a1, worldB[j * 2], worldB[j * 2 + 1], out w1, out w2);

                if (d >= best) continue;
                best = d;
                ringA = segsA[i].Ring;
                ringB = segsB[j].Ring;
            }
        }

        return best;
    }

    // Q1's subject. The set of rings the profile actually designates SharpEdge, resolved once from the
    // authored shell so every row can say whether the attributed ring is one of them without a reader
    // having to know the specimen. Both blades author TWO runs, so a single "the" cutting edge would be
    // wrong.
    private readonly HashSet<string> sharpRingsA = new HashSet<string>();
    private readonly HashSet<string> sharpRingsB = new HashSet<string>();
    private string sharpRingsAText = "", sharpRingsBText = "";

    private static void CollectSharpRings(RqBladeContact blade, HashSet<string> into, out string text)
    {
        into.Clear();
        text = "";
        if (blade == null) return;

        BladeShellData d = blade.Data;
        if (d == null) return;

        for (int i = 0; i < d.EdgeCount; i++)
        {
            var r = new BladeFeatureRef(BladeFeatureKind.Edge, i);
            if (d.TypeOf(r) != BladeFeatureType.SharpEdge) continue;
            string ring = RingOf(d.IdOf(r));
            if (ring != null) into.Add(ring);
        }

        var parts = new List<string>(into);
        parts.Sort(System.StringComparer.Ordinal);
        text = string.Join("|", parts.ToArray());
    }

    /// <summary>
    ///     The authored ring an id belongs to: <c>SW_A1_blade.s52.e6</c> -> <c>e6</c>, and
    ///     <c>SW_B1_blade.s68.f6.b</c> -> <c>f6</c>.
    /// </summary>
    /// <remarks>
    ///     <b>Not simply the id tail.</b> Taking the last dot-segment is right for an edge but wrong for a
    ///     facet, whose id carries a trailing <c>.a</c>/<c>.b</c> naming which half of the split quad it is.
    ///     That truncation reported facet rings as bare "a" and "b" in the first Q1 run — a column that
    ///     looked like a ring and was not one. The scan takes the last segment of the form e&lt;digits&gt; or
    ///     f&lt;digits&gt;, which is the authored ring in both cases, and falls back to the raw tail for cap
    ///     geometry that follows neither pattern.
    /// </remarks>
    // The implementation moved to BladeShellData.RingOf UNCHANGED, so the station label and this recorder
    // read a ring the same way. Two ring readers in this project already disagree with each other on the
    // same raw id — that is why pauseOnFirstSharpAttribution does not fire — and a third was not going to
    // be added just to put the latched ring on a label. Kept as a local name so every call site here is
    // untouched and nothing this recorder has already written moves.
    private static string RingOf(string id) => BladeShellData.RingOf(id);

    /// <summary>
    ///     A live pair existing is the precondition for any classification: the solver creates one only once
    ///     PhysX has reported a contact for these two colliders, so <c>pairExists = 0</c> across a phase
    ///     means the blades were not in reported contact during it.
    /// </summary>
    private void RecordTimelineRow()
    {
        // The timeline describes the DWELL WALK. Once the walk is done the recorder keeps running only to
        // finish the deferred enumeration, and those extra steps are not part of the commanded cycle —
        // logging them inflated run 02's timeline from 1710 rows to 9420 and made a window-bounded query
        // silently include post-walk steps, because phaseState stays 3 and stepsInPhase keeps climbing.
        if (walkComplete) return;

        RqBladeTangentialSolver.Snapshot s;
        bool has = solver.TryGetSnapshot(contactA, contactB, out s);

        var sb = new StringBuilder(160);
        sb.Append(step).Append(',').Append(phaseState).Append(',').Append(PhaseName(phaseState)).Append(',')
            .Append(StepsInPhase).Append(',').Append(InCaptureWindow ? 1 : 0).Append(',')
            .Append(has ? 1 : 0).Append(',');

        if (!has)
        {
            sb.Append(",,,,,,,,,,,,,,,,,");
        }
        else
        {
            string ringA = RingOf(s.RawIdA), ringB = RingOf(s.RawIdB);

            sb.Append(s.ClassificationValid ? 1 : 0).Append(',')
                .Append(s.ClassificationValid ? s.Scenario.ToString() : "").Append(',')
                .Append(s.RawTypeA).Append(',').Append(s.RawTypeB).Append(',')
                .Append(s.RawIdA).Append(',').Append(s.RawIdB).Append(',')
                .Append(R(s.ShellSeparation)).Append(',')
                .Append(s.ClassWrittenAtStep).Append(',')
                // Age is the difference of two SOLVER-clock stamps. It must NOT be measured against this
                // recorder's own step counter: the solver counts from its own enable, this recorder counts
                // from Begin, and subtracting one from the other produced a class age of -10921 steps when
                // the recorder was re-armed mid-session. Same clock, or no number.
                .Append(s.ClassWrittenAtStep > 0 && s.ClassifyAttemptedAtStep > 0
                    ? (s.ClassifyAttemptedAtStep - s.ClassWrittenAtStep).ToString()
                    : "").Append(',')
                .Append(s.State).Append(',').Append(R(s.NormalForce)).Append(',')
                .Append(ringA).Append(',').Append(ringB).Append(',')
                // Membership in the AUTHORED sharp set, not a judgement about the answer. A face's ring is
                // a facet ring and will read 0 here, which is correct and is the point.
                .Append(ringA != null && sharpRingsA.Contains(ringA) ? 1 : 0).Append(',')
                .Append(ringB != null && sharpRingsB.Contains(ringB) ? 1 : 0).Append(',')
                .Append(sharpRingsAText).Append(',').Append(sharpRingsBText).Append(',')
                .Append(s.ClassifyAttemptedAtStep);
        }

        // Appended for EVERY row, inside the pair branch or not. This is the one geometric quantity here
        // that does not depend on the solver, so it must not be lost in the rows where no pair exists —
        // those rows are the approach.
        float coarse = CoarseGap();
        bool armed = measureSharpEdgeGap && HeavyGateOpen(coarse);
        sb.Append(',').Append(R(coarse)).Append(',').Append(armed ? 1 : 0);

        if (armed)
        {
            string gRingA, gRingB;
            float gap = SharpEdgeGap(out gRingA, out gRingB);
            sb.Append(',').Append(R(gap)).Append(',').Append(gRingA).Append(',').Append(gRingB);
        }
        else
        {
            // Blank rather than zero: not measured is not the same fact as measured-and-touching.
            sb.Append(",,,");
        }

        AppendPlacementVerification(sb, has, s);
        AppendStaleness(sb, has, s);

        // Not appended to sb: kept parallel so the four shifted columns stay at 43-46. See
        // latchFieldsByRow.
        latchFieldsByRow.Add(BuildLatchFields(has, s));

        timeline.Add(sb.ToString());
    }

    /// <summary>
    ///     The fourteen v1 columns for one row: the latched INITIAL APPROACH fact, whether the current fact
    ///     has diverged from it, which of the two governed behaviour, and the bind gate.
    /// </summary>
    /// <remarks>
    ///     Rings are derived here from the LATCHED raw ids using this recorder's own
    ///     <see cref="RingOf" />, which is now <see cref="BladeShellData.RingOf" />. They are deliberately
    ///     not latched separately in the solver: a ring is a pure function of the raw id, so latching the
    ///     id latches the ring, and a second ring parser is exactly the defect that already exists between
    ///     <c>RqBindMetricsV2.RingIsAuthoredSharp</c> and the <c>ringAIsAuthoredSharp</c> column.
    /// </remarks>
    private string BuildLatchFields(bool has, RqBladeTangentialSolver.Snapshot s)
    {
        if (!has) return EmptyLatchFields;

        var sb = new StringBuilder(96);

        sb.Append(s.InitialLatched ? 1 : 0).Append(',');

        if (!s.InitialLatched)
        {
            // Nothing latched yet. Blank, not zero — EdgeEdge is the scenario enum's zero value and a
            // zero here would read as an EdgeEdge latch that never happened. Ten empty cells:
            // initialScenario through initialShellSeparation.
            sb.Append(",,,,,,,,,,");
        }
        else
        {
            sb.Append(s.InitialScenario).Append(',')
                .Append(s.InitialRawTypeA).Append(',').Append(s.InitialRawTypeB).Append(',')
                .Append(s.InitialRawIdA).Append(',').Append(s.InitialRawIdB).Append(',')
                .Append(RingOf(s.InitialRawIdA)).Append(',').Append(RingOf(s.InitialRawIdB)).Append(',')
                .Append(s.InitialLatchedAtStep).Append(',')
                .Append(R(s.InitialNormalForce)).Append(',')
                .Append(R(s.InitialShellSeparation)).Append(',');
        }

        sb.Append(s.LatchGeneration).Append(',')
            .Append(s.InitialLatched ? (s.ScenarioEvolved ? "1" : "0") : "").Append(',')
            .Append(s.ClassificationValid ? s.GoverningScenario.ToString() : "");

        return sb.ToString();
    }

    /// <summary>
    ///     The columns that prove the geometry was measured where it was supposed to be.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Why this exists.</b> Until 2026-08-23 every geometric number in this system was computed
    ///         at <c>ActiveTransform</c>, which under interpolation is a RENDERING pose, not the pose PhysX
    ///         solved. At first contact the two were 1.5-1.7 mm apart and the identical closest-feature
    ///         query returned 12.96 mm against 7.90 mm. Nothing errored, and the wrong number is not
    ///         distinguishable from the right one by inspection — so it is now checked on every row rather
    ///         than assumed.
    ///     </para>
    ///     <para>
    ///         <b>The check.</b> <c>verifyShellSepPhysics</c> re-runs the authored-shell closest-feature
    ///         query here, independently of the solver, at the simulating body's placement. On any row
    ///         where the solver's classification is FRESH — <c>ClassWrittenAtStep == step of this row's
    ///         solve</c> — it must equal the solver's own <c>shellSeparation</c>, and
    ///         <c>verifyMatchesSolver</c> says whether it did. On stale rows the solver's value is held and
    ///         a difference is expected, so the comparison is skipped rather than reported as a failure.
    ///     </para>
    ///     <para>
    ///         <b>Cost.</b> The query is only run when the solver classified on this step, so this adds one
    ///         shell query per <c>classificationInterval</c>, not one per step.
    ///     </para>
    /// </remarks>
    private void AppendPlacementVerification(StringBuilder sb, bool has, RqBladeTangentialSolver.Snapshot s)
    {
        BladeShell shellA = contactA != null ? contactA.Shell : null;
        BladeShell shellB = contactB != null ? contactB.Shell : null;

        if (shellA == null || shellB == null)
        {
            sb.Append(",,,,,,,,,,,,");
            return;
        }

        // Which source each side's placement actually came from, and how far the drawn pose is from it.
        sb.Append(',').Append(shellA.PlacementSource)
          .Append(',').Append(shellB.PlacementSource)
          .Append(',').Append(R(shellA.RenderLagMetres * 1000f))
          .Append(',').Append(R(shellB.RenderLagMetres * 1000f))
          .Append(',').Append(R(shellA.RenderLagDegrees))
          .Append(',').Append(R(shellB.RenderLagDegrees));

        // The callback-sourced fields are NOT written inline. They are one step old by construction — the
        // callbacks for step N only exist after step N is simulated — so they are held in a parallel list
        // and written by BuildTimeline onto the previous row, which is the row they describe. See the
        // header comment on the four *Shifted columns.
        callbackFieldsByRow.Add(has
            ? R(s.SignedOverlap) + "," + s.ContactCount + "," +
              Csv(s.ContactColliderA) + "," + Csv(s.ContactColliderB)
            : ",,,");

        // Only reproduce the query on a step the solver actually classified, and only when asked. The
        // comparison is skipped on stale rows because comparing against a held value manufactures a
        // disagreement that says nothing; it is skipped entirely when verifyShellSeparation is off
        // because the extra query is cost only the recorded path would carry.
        bool fresh = verifyShellSeparation && has && s.ClassificationValid &&
                     s.ClassWrittenAtStep == s.LastSolvedStep;
        if (!fresh)
        {
            sb.Append(",,,,,");
            return;
        }

        BladePose physA = shellA.SimulatedPlacement, physB = shellB.SimulatedPlacement;
        BladePose visA = shellA.RenderedPlacement, visB = shellB.RenderedPlacement;

        float sepPhysics, sepVisual;
        string featA, featB;
        bool okPhys = TryShellSeparation(shellA, shellB, physA, physB, out sepPhysics, out featA, out featB);
        string ignoredA, ignoredB;
        bool okVis = TryShellSeparation(shellA, shellB, visA, visB, out sepVisual, out ignoredA, out ignoredB);

        sb.Append(',').Append(okPhys ? R(sepPhysics) : "")
          .Append(',').Append(okVis ? R(sepVisual) : "")
          .Append(',').Append(featA).Append(',').Append(featB);

        if (okPhys)
        {
            float deltaMm = Mathf.Abs(sepPhysics - s.ShellSeparation) * 1000f;
            // Exact equality is not expected in floating point; this reports the difference and leaves the
            // question of what counts as agreement to the reader, who declares it.
            sb.Append(',').Append(deltaMm.ToString("F6", CultureInfo.InvariantCulture));
        }
        else
        {
            sb.Append(',');
        }
    }

    /// <summary>
    ///     Whether this row's attribution was carried forward from an earlier step, and how many queries
    ///     have failed on this pair. Written on EVERY row so a stale attribution is never silent.
    /// </summary>
    private static void AppendStaleness(StringBuilder sb, bool has, RqBladeTangentialSolver.Snapshot s)
    {
        if (!has)
        {
            sb.Append(",,");
            return;
        }

        sb.Append(',').Append(s.ClassificationStale ? 1 : 0)
          .Append(',').Append(s.FailedQueries);
    }

    /// <summary>
    ///     One authored-shell closest-feature query at a caller-supplied placement. No inflation, no skin.
    /// </summary>
    private bool TryShellSeparation(
        BladeShell a, BladeShell b, in BladePose poseA, in BladePose poseB,
        out float separation, out string featureA, out string featureB)
    {
        separation = float.NaN;
        featureA = featureB = "";

        BladeShellData da = a.Data, db = b.Data;
        if (da == null || db == null) return false;

        if (verifyScratch == null) verifyScratch = new BladeShellScratch();
        if (verifyClock == null) verifyClock = new System.Diagnostics.Stopwatch();
        verifyClock.Reset();
        verifyClock.Start();

        BladeSweepSettings settings = BladeSweepSettings.Default;
        BladeFeaturePair witness;
        bool ok = BladeShellSweep.TryClosestFeaturePair(
            da, poseA, db, poseB, settings, verifyScratch, null, verifyClock, null, out witness);

        if (!ok || !witness.FeatureA.IsValid || !witness.FeatureB.IsValid) return false;

        separation = witness.Separation;
        featureA = da.IdOf(witness.FeatureA);
        featureB = db.IdOf(witness.FeatureB);
        return true;
    }

    private BladeShellScratch verifyScratch;
    private System.Diagnostics.Stopwatch verifyClock;

    private static string PhaseName(int state) =>
        state >= 0 && state < PhaseNames.Length ? PhaseNames[state] : "?" + state;

    // ----- deferred unpruned reference enumeration -------------------------------------------------

    private void StartNextReference()
    {
        for (int i = 0; i < events.Count; i++)
        {
            if (events[i].ReferenceComplete) continue;

            Event e = events[i];
            pending = e;
            cursorA = cursorB = 0;

            BladeShellData a = e.Trace.ShellA, b = e.Trace.ShellB;
            if (a == null || b == null)
            {
                Debug.LogError("[Phase1b] " + e.Name + ": trace carries no shell data.", this);
                e.ReferenceComplete = true;
                pending = null;
                continue;
            }

            // Production's own winning pair, re-measured through this same code. If this does not
            // reproduce trace.Final.Separation exactly then the enumeration is not like-for-like and
            // nothing else it reports can be trusted, so it is computed first and reported beside the
            // original.
            if (e.Trace.Final.FeatureA.IsValid && e.Trace.Final.FeatureB.IsValid)
            {
                Vector3 wa, wb;
                int spec;
                e.ProductionPairRemeasured = Measure(
                    a, e.Trace.PoseA, e.Trace.Final.FeatureA,
                    b, e.Trace.PoseB, e.Trace.Final.FeatureB,
                    out wa, out wb, out spec);
            }

            long total = (long)(a.SurfaceCount + a.EdgeCount) * (b.SurfaceCount + b.EdgeCount);
            Debug.Log("[Phase1b] " + e.Name + ": enumerating " + total.ToString("N0") +
                      " pairs unpruned at " + referenceTestsPerFrame.ToString("N0") + " per frame.", this);
            return;
        }

        pending = null;
    }

    private void AdvanceReferenceEnumeration()
    {
        if (pending == null) return;

        // Budgeted per RENDERED FRAME. Budgeting per FixedUpdate is what bogged run 01: Unity runs
        // several fixed steps per frame to catch up, so each frame spent a multiple of the ceiling and
        // the catch-up fed itself.
        if (Time.frameCount == lastEnumeratedFrame) return;
        lastEnumeratedFrame = Time.frameCount;

        Event e = pending;
        BladeShellData a = e.Trace.ShellA, b = e.Trace.ShellB;
        int slotsA = a.SurfaceCount + a.EdgeCount;
        int slotsB = b.SurfaceCount + b.EdgeCount;
        int budget = referenceTestsPerFrame;

        while (budget > 0 && cursorA < slotsA)
        {
            BladeFeatureRef refA = SlotToRef(a, cursorA);

            while (budget > 0 && cursorB < slotsB)
            {
                BladeFeatureRef refB = SlotToRef(b, cursorB);

                Vector3 wa, wb;
                int spec;
                float d = Measure(a, e.Trace.PoseA, refA, b, e.Trace.PoseB, refB, out wa, out wb, out spec);

                Category cat = spec == 0
                    ? Category.SurfaceSurface
                    : spec == 2
                        ? Category.EdgeEdge
                        : refA.Kind == BladeFeatureKind.Edge
                            ? Category.EdgeSurface
                            : Category.SurfaceEdge;

                Offer(e, cat, d, refA.Index, refB.Index, wa, wb);

                if (spec == 2 &&
                    a.TypeOf(refA) == BladeFeatureType.SharpEdge &&
                    b.TypeOf(refB) == BladeFeatureType.SharpEdge)
                {
                    Offer(e, Category.SharpSharp, d, refA.Index, refB.Index, wa, wb);
                }

                e.ReferenceTests++;
                budget--;
                cursorB++;
            }

            if (cursorB >= slotsB)
            {
                cursorB = 0;
                cursorA++;
            }
        }

        if (cursorA < slotsA) return;

        e.ReferenceComplete = true;
        pending = null;

        Debug.Log("[Phase1b] " + e.Name + ": enumeration complete, " + e.ReferenceTests.ToString("N0") +
                  " tests. production pair re-measured " + R(e.ProductionPairRemeasured) +
                  " vs recorded " + R(e.Trace.Final.Separation), this);

        StartNextReference();
    }

    private static void Offer(
        Event e, Category cat, float d, int indexA, int indexB, Vector3 witnessA, Vector3 witnessB)
    {
        int i = (int)cat;

        // Strictly-less, so the FIRST achiever of a tied minimum is retained and the record is
        // deterministic. Ties are the phenomenon under study, so they are reported as distances rather
        // than resolved by a rule chosen here.
        if (d >= e.MinDistance[i]) return;

        e.MinDistance[i] = d;
        e.IndexA[i] = indexA;
        e.IndexB[i] = indexB;
        e.WitnessA[i] = witnessA;
        e.WitnessB[i] = witnessB;
    }

    private static BladeFeatureRef SlotToRef(BladeShellData shell, int slot) =>
        slot < shell.SurfaceCount
            ? new BladeFeatureRef(BladeFeatureKind.Surface, slot)
            : new BladeFeatureRef(BladeFeatureKind.Edge, slot - shell.SurfaceCount);

    /// <summary>
    ///     Exact separation of one feature pair, by the same routines and the SAME ARGUMENT ORDER the sweep
    ///     uses.
    /// </summary>
    /// <remarks>
    ///     The argument order matters and is not cosmetic:
    ///     <see cref="TriangleGeometry.SegmentTriangleDistance" /> returns its witnesses as (segment,
    ///     triangle), so calling it with A's edge against B's triangle and then assigning the witnesses as
    ///     though A were the triangle silently swaps the two blades. Production's assignments are mirrored
    ///     here exactly, and the reproduces-production check in <see cref="StartNextReference" /> is what
    ///     proves it.
    /// </remarks>
    private static float Measure(
        BladeShellData shellA, in BladePose poseA, BladeFeatureRef refA,
        BladeShellData shellB, in BladePose poseB, BladeFeatureRef refB,
        out Vector3 witnessA, out Vector3 witnessB, out int specificity)
    {
        bool aIsSurface = refA.Kind == BladeFeatureKind.Surface;
        bool bIsSurface = refB.Kind == BladeFeatureKind.Surface;

        Vector3 a0, a1, a2;
        if (aIsSurface)
        {
            BladeSurface f = shellA.GetSurface(refA.Index);
            a0 = poseA.TransformPoint(f.LocalA);
            a1 = poseA.TransformPoint(f.LocalB);
            a2 = poseA.TransformPoint(f.LocalC);
        }
        else
        {
            BladeEdgeLine l = shellA.GetEdge(refA.Index);
            a0 = poseA.TransformPoint(l.LocalStart);
            a1 = poseA.TransformPoint(l.LocalEnd);
            a2 = a0;
        }

        Vector3 b0, b1, b2;
        if (bIsSurface)
        {
            BladeSurface f = shellB.GetSurface(refB.Index);
            b0 = poseB.TransformPoint(f.LocalA);
            b1 = poseB.TransformPoint(f.LocalB);
            b2 = poseB.TransformPoint(f.LocalC);
        }
        else
        {
            BladeEdgeLine l = shellB.GetEdge(refB.Index);
            b0 = poseB.TransformPoint(l.LocalStart);
            b1 = poseB.TransformPoint(l.LocalEnd);
            b2 = b0;
        }

        if (aIsSurface && bIsSurface)
        {
            specificity = 0;
            return TriangleGeometry.TriangleTriangleDistance(
                a0, a1, a2, b0, b1, b2, out witnessA, out witnessB);
        }

        if (aIsSurface)
        {
            Vector3 wSeg, wTri;
            float d = TriangleGeometry.SegmentTriangleDistance(b0, b1, a0, a1, a2, out wSeg, out wTri);
            witnessA = wTri;
            witnessB = wSeg;
            specificity = 1;
            return d;
        }

        if (bIsSurface)
        {
            Vector3 wSeg, wTri;
            float d = TriangleGeometry.SegmentTriangleDistance(a0, a1, b0, b1, b2, out wSeg, out wTri);
            witnessA = wSeg;
            witnessB = wTri;
            specificity = 1;
            return d;
        }

        specificity = 2;
        return SegmentGeometry.ClosestPointsBetweenSegments(a0, a1, b0, b1, out witnessA, out witnessB);
    }

    private bool AllReferencesComplete()
    {
        for (int i = 0; i < events.Count; i++)
            if (!events[i].ReferenceComplete)
                return false;

        return true;
    }

    // ----- live visual confirmation ----------------------------------------------------------------

    private void LateUpdate()
    {
        if (pending != null) AdvanceReferenceEnumeration();
        if (drawWitnessLines) UpdateWitnessLines();
    }

    private LineRenderer MakeLine(string label)
    {
        var go = new GameObject(label);
        go.transform.SetParent(transform, false);

        var lr = go.AddComponent<LineRenderer>();
        lr.useWorldSpace = true;
        lr.positionCount = 2;
        lr.widthMultiplier = 0.004f;
        lr.numCapVertices = 2;
        lr.material = new Material(Shader.Find("Unlit/Color"));
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows = false;
        return lr;
    }

    /// <summary>
    ///     Draws the classifier's current claim as one full-blade-length line per blade.
    /// </summary>
    /// <remarks>
    ///     An individual ring feature is only a couple of millimetres long and invisible at working
    ///     distance, so the line is not the feature itself: it runs along the blade's authored long axis
    ///     THROUGH the winning feature's witness point, which makes it intersect the claimed feature while
    ///     staying long enough to see. Colour carries the authored designation, so the verdict is readable
    ///     without the numbers: green + green is EdgeEdge, green + red is EdgeFlat.
    /// </remarks>
    private void UpdateWitnessLines()
    {
        BladeClassificationTrace t = liveForVisual;
        bool show = running && t != null && t.ClassificationValid &&
                    t.Final.FeatureA.IsValid && t.Final.FeatureB.IsValid;

        if (lineA == null) lineA = MakeLine("Phase1b_WitnessLine_A");
        if (lineB == null) lineB = MakeLine("Phase1b_WitnessLine_B");

        lineA.enabled = show;
        lineB.enabled = show;
        if (!show) return;

        Place(lineA, contactA, t.Final.WitnessA, t.ShellA, t.Final.FeatureA);
        Place(lineB, contactB, t.Final.WitnessB, t.ShellB, t.Final.FeatureB);
    }

    private void Place(
        LineRenderer lr, RqBladeContact blade, Vector3 witness, BladeShellData shell, BladeFeatureRef f)
    {
        Vector3 axis = Vector3.forward;
        if (blade != null && blade.ActiveTransform != null)
        {
            Vector3 local = blade.LocalLongAxis;
            if (local.sqrMagnitude > 1e-12f)
                axis = blade.ActiveTransform.TransformDirection(local).normalized;
        }

        lr.SetPosition(0, witness - axis * witnessLineHalfLength);
        lr.SetPosition(1, witness + axis * witnessLineHalfLength);

        BladeFeatureType type = shell == null || !f.IsValid
            ? BladeFeatureType.BroadFace
            : shell.TypeOf(f);

        Color c = type == BladeFeatureType.SharpEdge
            ? new Color(0.1f, 1f, 0.2f)
            : type == BladeFeatureType.BevelFace || type == BladeFeatureType.BroadFace
                ? new Color(1f, 0.15f, 0.1f)
                : new Color(1f, 0.9f, 0.1f);

        lr.material.color = c;
        lr.startColor = lr.endColor = c;
    }

    private void OnGUI()
    {
        if (!drawScreenReadout || !running) return;

        BladeClassificationTrace t = liveForVisual;
        var sb = new StringBuilder();
        sb.AppendLine("PHASE 1b  " + stationId + "   step " + step);
        sb.AppendLine("phase " + PhaseName(phaseState) + " (+" + StepsInPhase + ")   window: " +
                      (InCaptureWindow ? "OPEN" : "closed, need phase " + captureInPhaseState + " +" +
                                                  captureMinStepsInPhase));
        sb.AppendLine("captured " + events.Count + "/3   out-of-window rejects " + rejectedOutOfWindow +
                      "   other-pair rejects " + rejectedOtherPair);

        if (t != null && t.ClassificationValid)
        {
            sb.AppendLine("VERDICT " + t.Scenario + "   sep " + R(t.Final.Separation) +
                          "   (classified at step " + t.Step + ")");
            sb.AppendLine("  A: " + Type(t.ShellA, t.Final.FeatureA) + "  " +
                          Id(t.ShellA, t.Final.FeatureA));
            sb.AppendLine("  B: " + Type(t.ShellB, t.Final.FeatureB) + "  " +
                          Id(t.ShellB, t.Final.FeatureB));
        }
        else
        {
            sb.AppendLine("VERDICT (no classification yet — the pair is created on first contact)");
        }

        if (pending != null)
            sb.AppendLine("enumerating " + pending.Name + ": " + pending.ReferenceTests.ToString("N0"));

        GUI.color = Color.black;
        GUI.Label(new Rect(13, 13, 900, 140), sb.ToString());
        GUI.color = InCaptureWindow ? new Color(0.4f, 1f, 0.4f) : Color.white;
        GUI.Label(new Rect(12, 12, 900, 140), sb.ToString());
        GUI.color = Color.white;
    }

    // ----- output ----------------------------------------------------------------------------------

    /// <summary>Round-trip float formatting. Anything lossier would erase the exact ties under study.</summary>
    /// <remarks>
    /// NaN and <see cref="float.MaxValue" /> are both written as empty, because both mean "no value here":
    /// MaxValue is the sentinel for an unset minimum, and printing it as 3.4028235E+38 invites it being
    /// read as a distance.
    /// </remarks>
    private static string R(float v) =>
        float.IsNaN(v) || v == float.MaxValue ? "" : v.ToString("R", CultureInfo.InvariantCulture);

    private static string V(Vector3 v) => R(v.x) + "," + R(v.y) + "," + R(v.z);

    [ContextMenu("Seal")]
    public void Seal()
    {
        if (sealedAlready) return;

        running = false;
        sealedAlready = true;
        if (solver != null) solver.TraceClassifications = false;

        if (string.IsNullOrEmpty(outputDirectory))
        {
            Debug.LogError("[ClassificationFork] please set directory on the RqClassificationForkRecorder component to record. "
                           + "outputDirectory is blank, so nothing was written. It is blank by "
                           + "default so that no machine's folder layout ships with the project; "
                           + "refusing here rather than letting CreateDirectory(\"\") throw, "
                           + "because an empty output folder looks exactly like a run that never "
                           + "armed.", this);
            return;
        }

        try
        {
            Directory.CreateDirectory(outputDirectory);
            string stamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");

            // The dwell schedule goes in the FILENAME, not only in the preflight header. Changing a dwell
            // moves every phase boundary and every step number in the file, so two runs on different
            // schedules are not comparable on timing at all — and that is not visible when a directory
            // listing shows only a station and a timestamp. Pause went 90 -> 180 on 2026-08-23 because a
            // 90-step dwell commanded the next phase while this one was still blending; every run before
            // that carries the old schedule and none of them says so in its name.
            string schedule = "d" + dwellPauseSteps + "-" + dwellSnapSteps + "-" +
                              dwellHoldSteps + "-" + dwellContactSteps;

            string stem = Path.Combine(
                outputDirectory,
                "phase1b_" + (string.IsNullOrEmpty(stationId) ? "station" : stationId) + "_" + stamp +
                "_" + schedule);

            // Always written, including a zero-capture run: whether the pair was ever in reported contact
            // inside the declared window is the question the timeline exists to answer, and a run that
            // captured nothing is precisely the run where that answer matters most.
            File.WriteAllText(stem + "_E4_timeline.csv", BuildTimeline());

            if (events.Count > 0)
            {
                File.WriteAllText(stem + "_E1_events.csv", BuildEvents());
                File.WriteAllText(stem + "_E2_candidates.csv", BuildCandidates());
                File.WriteAllText(stem + "_E3_reference.csv", BuildReference());
            }
            else if (!captureEvolutionEvents)
            {
                Debug.Log("[Phase1b] Q1 run: E1/E2/E3 evolution capture SUPPRESSED by configuration, so " +
                          "no event files are written and none are expected. The E4 timeline is the Q1 " +
                          "instrument and it is sealed.", this);
            }
            else
            {
                Debug.LogWarning("[Phase1b] NOTHING captured in the declared window (phase " +
                                 captureInPhaseState + " at +" + captureMinStepsInPhase + "). " +
                                 "out-of-window rejects " + rejectedOutOfWindow + ", other-pair rejects " +
                                 rejectedOtherPair + ". The timeline is still sealed and says where the " +
                                 "pair actually existed.", this);
            }

            Debug.Log("[Phase1b] SEALED " + events.Count + " events + " + timeline.Count +
                      " timeline rows to " + stem + "_E*.csv", this);
        }
        catch (System.Exception ex)
        {
            Debug.LogError("[Phase1b] seal failed: " + ex.GetType().Name + ": " + ex.Message, this);
        }

        Restore();
    }

    private string BuildTimeline()
    {
        var sb = new StringBuilder();
        AppendPreflight(sb);
        sb.AppendLine(TimelineHeader);

        // The callback-sourced fields are shifted UP one row: the values captured on row i+1 describe row
        // i, because PhysX cannot report a step's contacts until that step has been simulated. The last row
        // gets none of them — the measurements that would fill it belong to a step after the walk ended,
        // which was never taken.
        for (int i = 0; i < timeline.Count; i++)
        {
            string shifted = i + 1 < callbackFieldsByRow.Count ? callbackFieldsByRow[i + 1] : ",,,";

            // The v1 columns are NOT shifted — entry i belongs to row i. They go after the shifted block
            // so that columns 1-46 keep the positions every reading rule in the handoff refers to.
            string latched = i < latchFieldsByRow.Count ? latchFieldsByRow[i] : EmptyLatchFields;

            sb.AppendLine(timeline[i] + "," + shifted + "," + latched);
        }

        return sb.ToString();
    }

    private void AppendPreflight(StringBuilder h)
    {
        h.AppendLine("# Phase 1b — production classification fork. Read all three files together.");
        h.AppendLine("# stationId," + stationId + ",referencePose," + referencePose);
        h.AppendLine("# sealedAt," + System.DateTime.Now.ToString("o"));
        h.AppendLine("# fixedDeltaTime," + R(Time.fixedDeltaTime) + ",hz," + R(1f / Time.fixedDeltaTime));

        // Frame pacing is an apparatus condition, not a cosmetic one: Ragdoll Animator refreshes the
        // muscle target once per rendered FRAME (CaptureAnimator, in LateUpdateTick) while the drive runs
        // every fixed step, so how many steps share a target depends on the render rate. Recorded so a run
        // states the pacing it ran under instead of it having to be remembered.
        h.AppendLine("# framePacing,vSyncCount," + QualitySettings.vSyncCount +
                     ",targetFrameRate," + Application.targetFrameRate +
                     ",note,compare against armSnapshot framesPerFixedStep, which is the pacing that " +
                     "was actually achieved. A targetFrameRate cap above what the machine sustains does " +
                     "not bind, and vSyncCount above 0 makes targetFrameRate ineffective");
        // The one authored-shell observation that exists before the gated column does. Cached at the trip
        // rather than measured continuously, so the recording window is unchanged and no column is added:
        // adding columns is what made the 43-column and 46-column files non-comparable.
        if (gateTrip.Captured)
        {
            h.AppendLine("# gateTrip,recorderStep," + gateTrip.RecorderStep +
                         ",globalFixedStep," + gateTrip.GlobalFixedStep +
                         ",frameCount," + gateTrip.FrameCount +
                         ",phaseState," + gateTrip.PhaseState +
                         ",stepsInPhase," + gateTrip.StepsInPhase +
                         ",coarseGap," + R(gateTrip.CoarseGap) +
                         ",reach," + R(gateTrip.Reach) +
                         ",sharpEdgeGap," + R(gateTrip.SharpEdgeGap) +
                         ",sharpGapRingA," + (gateTrip.SharpRingA ?? "") +
                         ",sharpGapRingB," + (gateTrip.SharpRingB ?? "") +
                         ",relVelocity," + V(gateTrip.RelVelocity) +
                         ",relAcceleration," + V(gateTrip.RelAcceleration) +
                         ",posA," + V(gateTrip.PosA) +
                         ",rotA," + R(gateTrip.RotA.x) + "," + R(gateTrip.RotA.y) + "," +
                         R(gateTrip.RotA.z) + "," + R(gateTrip.RotA.w) +
                         ",posB," + V(gateTrip.PosB) +
                         ",rotB," + R(gateTrip.RotB.x) + "," + R(gateTrip.RotB.y) + "," +
                         R(gateTrip.RotB.z) + "," + R(gateTrip.RotB.w) +
                         ",note,state cached at the single step the heavy gate first opened. Metres and " +
                         "round-trip floats. The body poses are from SimulatedPlacement (the generated " +
                         "ragdoll proxy) and are the only raw pose in this file — every other geometric " +
                         "value is a derived scalar");
        }
        else
        {
            h.AppendLine("# gateTrip,NOT CAPTURED,note,the heavy gate never opened in this run — every " +
                         "sharpEdgeGap cell is empty and no authored-shell geometry was measured");
        }

        // The arm instant. The physics drive is fixed-step (UpdatePhysicalAnimationMatching and
        // FixedUpdateAttachables, in FixedUpdateTick). What is frame-paced is CaptureAnimator, in
        // LateUpdateTick, so the muscle TARGET is refreshed once per rendered frame while the drive runs
        // every fixed step. frameCount against globalFixedStep is that ratio — a hypothesis to test when
        // two matched-step runs disagree, not an established cause.
        if (armSnapshot.Captured)
        {
            h.AppendLine("# armSnapshot,frameCount," + armSnapshot.FrameCount +
                         ",globalFixedStep," + armSnapshot.GlobalFixedStep +
                         ",framesPerFixedStep," +
                         R(armSnapshot.GlobalFixedStep > 0
                             ? armSnapshot.FrameCount / (float)armSnapshot.GlobalFixedStep
                             : float.NaN) +
                         ",timeSinceLevelLoad," + R(armSnapshot.TimeSinceLevelLoad) +
                         ",fixedTime," + R(armSnapshot.FixedTime) +
                         ",bodyA," + (armSnapshot.BodyNameA ?? "<null>") +
                         ",posA," + V(armSnapshot.PosA) +
                         ",rotA," + R(armSnapshot.RotA.x) + "," + R(armSnapshot.RotA.y) + "," +
                         R(armSnapshot.RotA.z) + "," + R(armSnapshot.RotA.w) +
                         ",linVelA," + V(armSnapshot.LinVelA) +
                         ",angVelA," + V(armSnapshot.AngVelA) +
                         ",bodyB," + (armSnapshot.BodyNameB ?? "<null>") +
                         ",posB," + V(armSnapshot.PosB) +
                         ",rotB," + R(armSnapshot.RotB.x) + "," + R(armSnapshot.RotB.y) + "," +
                         R(armSnapshot.RotB.z) + "," + R(armSnapshot.RotB.w) +
                         ",linVelB," + V(armSnapshot.LinVelB) +
                         ",angVelB," + V(armSnapshot.AngVelB) +
                         ",animStateHashA," + armSnapshot.AnimStateHashA +
                         ",animNormTimeA," + R(armSnapshot.AnimNormTimeA) +
                         ",animInTransitionA," + (armSnapshot.AnimInTransitionA ? 1 : 0) +
                         ",animStateHashB," + armSnapshot.AnimStateHashB +
                         ",animNormTimeB," + R(armSnapshot.AnimNormTimeB) +
                         ",animInTransitionB," + (armSnapshot.AnimInTransitionB ? 1 : 0) +
                         ",note,state at the instant Begin ran. Begin does NOT reset rigidbody velocity " +
                         "or joint drive state, so these ARE the run's initial condition. Poses from " +
                         "SimulatedPlacement (the generated ragdoll proxy). Metres, m/s, rad/s, " +
                         "round-trip floats");
        }
        else
        {
            h.AppendLine("# armSnapshot,NOT CAPTURED");
        }

        h.AppendLine("# armedAtFixedStep," + armedAtFixedStep + ",alignMultiple," +
                     armAlignToFixedStepMultiple + ",requestedBy," + (armRequestSource ?? "direct Begin") +
                     ",RULE,value-for-value verification between two runs requires an IDENTICAL " +
                     "armedAtFixedStep; runs armed on different steps are a cross-comparison of overall " +
                     "behaviour only and their per-step numbers are not expected to match");
        h.AppendLine("# dwellSteps,pause," + dwellPauseSteps + ",snap," + dwellSnapSteps + ",hold," +
                     dwellHoldSteps + ",contact," + dwellContactSteps +
                     " (SAMPLE SIZES, not settling criteria)");
        h.AppendLine("# captureWindow,phaseState," + captureInPhaseState + ",minStepsInPhase," +
                     captureMinStepsInPhase +
                     ",derivation,a State transition completes at stepsInPhase 180 as measured in Phase 0 run 01");
        h.AppendLine("# isolation,otherStationRootsDeactivated," + isolated.Count + ",runtimeOnly,true");
        h.AppendLine("# captureEvolutionEvents," + (captureEvolutionEvents ? "true" : "false") +
                     (captureEvolutionEvents
                         ? ",note,E1/E2/E3 contact-evolution capture ACTIVE and the reference enumeration runs"
                         : ",note,Q1 RUN — E1/E2/E3 contact-evolution capture SUPPRESSED and no reference " +
                           "enumeration runs; zero events is the declared configuration and not a fault; " +
                           "the E4 timeline below is the Q1 instrument"));
        h.AppendLine("# solverPairCleared,atBegin,true,note,solver.Forget on both blades so no attribution " +
                     "is carried in from before the run was armed");

        // PART OF THE RUN'S CONDITION, READ FROM THE SOLVER ITSELF. The process rule this obeys was
        // earned: a condition read off the inspector instead of off the sealed file mislabelled three
        // runs, because editor runtime state survives a Play session. Every field below is queried from
        // the live component at seal time.
        //
        // GAP CLOSED 2026-08-25: ApplyTangentialBehaviour is now the FIRST field emitted below, so a
        // sealed file states which treatment it ran under. It had been absent, which meant a file could
        // not distinguish the OurSolution treatment from ordinary PhysX contact plus an observing
        // classifier — the single most consequential solver setting a run has.
        //
        // Files sealed BEFORE this change still cannot state it. Six of them carry it in the filename
        // instead (SWEEP_spring*_tangentialOFF_*); anything older must have its condition declared
        // alongside it and must NOT be treated as self-describing on this axis.
        if (solver != null)
        {
            // applyTangentialBehaviour FIRST, because it is the field that says WHICH TREATMENT this
            // run is. True = the OurSolution treatment, tangential force applied. False = ordinary
            // PhysX contact plus a classifier that only watches. Everything else in this line is
            // detail by comparison, and until 2026-08-25 this field was not recorded at all — six
            // sweep runs had to carry it in their FILENAMES because the header could not.
            h.AppendLine("# v1Solver,applyTangentialBehaviour," +
                         (solver.ApplyTangentialBehaviour ? "true" : "false") +
                         ",classificationInterval," + solver.ClassificationInterval +
                         ",clearAttributionOnQueryFailure," +
                         (solver.ClearAttributionOnQueryFailure ? "true" : "false") +
                         ",resolvePolicyFromLatchedScenario," +
                         (solver.ResolvePolicyFromLatchedScenario ? "true" : "false") +
                         ",relatchOnReEngagement," +
                         (solver.RelatchOnReEngagement ? "true" : "false") +
                         ",note,applyTangentialBehaviour is THE TREATMENT FLAG — false means this run " +
                         "is ordinary PhysX contact and the classifier is observing only, so it is NOT " +
                         "the OurSolution treatment however the station is named. " +
                         "resolvePolicyFromLatchedScenario FALSE is the pre-v1 behaviour and is the " +
                         "default — the policy is resolved from the CURRENT scenario and the latch is " +
                         "recording only. TRUE means the latched INITIAL APPROACH scenario steered the " +
                         "tangential term; governingScenario says which fact was in force per row. " +
                         "NOTE both together: with applyTangentialBehaviour false there is no " +
                         "tangential term for the latch to steer, so resolvePolicy is inert that run");

            // RETIRED 2026-08-25: a "# v1BindGate" line reported a pressure-gated bind criterion here.
            // The mechanism and the criterion are gone — see the RETIRED block in
            // RqBladeTangentialSolver and PHASE1_CLOSE_AND_PHASE2_OPEN.md. Nothing replaces it: Phase 2's
            // gate governs a constraint, not a friction coefficient, and does not exist yet.
        }
        else
        {
            h.AppendLine("# v1Solver,NOT AVAILABLE,note,no solver reference at seal");
        }

        h.AppendLine("# NOTE,twoFacts,columns 47-60 carry the LATCHED INITIAL APPROACH fact — what the " +
                     "classifier said at this pair's FIRST successful classification, which is its first " +
                     "reported step, at ~20 mm of separation and, on all three sealed Q1 runs, at zero " +
                     "normal force. Columns 6-24 carry CONTACT EVOLUTION, what it says now. Both are " +
                     "true and the second never overwrites the first: the solver's Initial* fields have " +
                     "exactly one writer and it runs only when nothing is latched. Read latchGeneration " +
                     "as the liveness figure — 0 means nothing ever latched and two runs agreeing on " +
                     "initialScenario would then be agreeing about nothing");
        h.AppendLine("# NOTE,columnLayout,60 columns. Columns 1-46 are UNCHANGED in name, order and " +
                     "meaning from the 46-column layout, including the four shifted callback columns at " +
                     "43-46, so a 46-column file is an exact prefix of this one. The fourteen v1 columns " +
                     "are appended at 47-60 and none of them is shifted. The three SEALED Q1 runs are 43 " +
                     "columns and are not comparable on layout with either");
        h.AppendLine("# NOTE,v1Scope,what a latched bind is and is not. IT IS a decision at engagement: " +
                     "the blade relationship identified before force is transmitted, gated on that plus a " +
                     "declared pressure criterion, falsifiable at Flat. IT IS NOT a simulated bind. After " +
                     "the gate fires, behaviour is PhysX plus the tangential term, and that is NOT " +
                     "validated during sustained contact. A run in which a bind visibly happens does not " +
                     "validate the chain after the gate");

        h.AppendLine("# declaredPair," + requireLabelA + " x " + requireLabelB +
                     ",rejectedOtherPairTraces," + rejectedOtherPair +
                     ",rejectedOutOfWindowTraces," + rejectedOutOfWindow +
                     ",eventsCaptured," + events.Count);

        // Written BEFORE the zero-event return below. These two rows describe the timeline, not the
        // captures, and a zero-capture run is exactly the run whose timeline has to be readable — the
        // first Q1 run lost both of them for that reason.
        h.AppendLine("# Q1,authoredSharpRingsA," + sharpRingsAText + ",authoredSharpRingsB," +
                     sharpRingsBText +
                     ",note,ring membership is reported per row; no pass/fail is computed here");
        h.AppendLine("# NOTE,shellSeparation is written only inside Classify and is therefore HELD for up " +
                     "to classificationInterval steps — it is not a per-step measurement");
        h.AppendLine("# heavyGate,requiresPhase," + heavyGateRequiresPhase + ",baseMetres," +
                     R(heavyGateBaseMetres) + ",buffer," +
                     R(heavyGateBuffer) + ",latches," + heavyGateLatches +
                     ",note,coarseGap and heavyArmed are on every row so the gate can be shown to have " +
                     "opened before the approach rather than assumed to have");

        // These describe the TIMELINE's own columns, so they must be written before the zero-event return
        // below. Putting them after it is the same defect section 12 item 2 already recorded and fixed
        // once for the Q1 rows: with evolution capture suppressed a Q1 run captures nothing by design, so
        // anything below the return never prints on exactly the runs that matter.
        h.AppendLine("# NOTE,placementSource,BladeShell.SimulatedPlacement reads Rigidbody.position/rotation " +
                     "— the pose PhysX solved. poseSourceA/B record which source was actually used per row; " +
                     "anything other than PhysicsBody invalidates that row's geometry");
        h.AppendLine("# NOTE,fourSeparations,shellSeparation=solver's published authored-shell value (HELD " +
                     "between classifications) | verifyShellSepPhysics=this recorder's independent " +
                     "authored-shell query at the body placement, expected to equal shellSeparation | " +
                     "verifyShellSepVisual=same query at the rendered transform placement, shown only to " +
                     "expose the old error, never a result | signedOverlap=PhysX against the COOKED CONVEX " +
                     "HULL inflated by contactOffset, context only and not a blade separation");
        h.AppendLine("# NOTE,verifyDeltaMm,raw difference between shellSeparation and verifyShellSepPhysics " +
                     "in mm, on rows where the solver classified fresh. No pass/fail is computed: what " +
                     "counts as agreement is a declared criterion and is not this recorder's to invent");
        h.AppendLine("# NOTE,cookedHull,the MeshCollider is convex so PhysX collides a cooked hull, not the " +
                     "mesh: 185 of 360 vertices of Blade_B1_Flat lie outside it, worst 2.5 mm at the tip. " +
                     "The authored shell is a THIRD geometry, built from the profile asset, and is the only " +
                     "one OurSolution measures against");
        h.AppendLine("# NOTE,firstStepSkipped,the first fixed step after arming is not recorded — see " +
                     "RqClassificationForkRecorder.FixedUpdate. Step 1 of this file is the SECOND fixed " +
                     "step after Begin");
        h.AppendLine("# NOTE,rowAlignment,A ROW IS ONE INSTANT. Every column on a row is that row's own " +
                     "step. Two things make that true and both are new on 2026-08-23: " +
                     "DefaultExecutionOrder puts this recorder (+100) after the solver (-100), so the " +
                     "solver readout read here is this step's and not the previous step's; and the four " +
                     "callback-sourced fields — signedOverlapShifted, contactCountShifted, " +
                     "contactColliderAShifted, contactColliderBShifted — are SHIFTED UP ONE ROW when the " +
                     "file is written, because PhysX cannot report a step's contacts until that step has " +
                     "been simulated. The last row therefore has none of those four");
        h.AppendLine("# NOTE,signSplitExplained,signedOverlap agrees with sharpEdgeGap to microns while the " +
                     "blades are APART and then diverges in sign once they are in contact — e.g. -14.4308 " +
                     "mm against an authored gap of +6.1808 mm. This is EXPECTED, not a defect. " +
                     "sharpEdgeGap is a kinematic evaluation of the authored shell at the body's pose, " +
                     "with no solver step applied to it; signedOverlap is a solver quantity taken while " +
                     "PhysX is actively depenetrating. While the blades are apart there is nothing to " +
                     "resolve and the two describe the same thing. Once contact starts they cannot, and " +
                     "should not, be read as the same measurement. Use sharpEdgeGap for where the authored " +
                     "shapes are; use signedOverlap only as a statement about what the solver is doing");
        h.AppendLine("# NOTE,previousRunsMisaligned,every run recorded BEFORE 2026-08-23 mixed instants on " +
                     "a single row — solver-derived columns were one step old and signedOverlap two. The " +
                     "values were correct measurements placed on the wrong rows. Do not compare a " +
                     "solver-derived distance from an older file against a recorder-computed one on the " +
                     "same row");

        // A zero-capture run still seals a timeline, so nothing below may assume an event exists.
        if (events.Count == 0)
        {
            h.AppendLine("# NOTE,ZERO EVENTS CAPTURED — trace-derived preflight rows are unavailable for " +
                         "this run; read the timeline for where the pair existed");
            return;
        }

        BladeClassificationTrace t = events[0].Trace;
        h.AppendLine("# specificityTieBand," + R(t.TieBand) +
                     ",maxNodePairVisits," + t.MaxNodePairVisits +
                     ",diagnosticTimeBudgetMs," + R(t.DiagnosticTimeBudgetMs) +
                     ",traceCapacity," + t.Capacity);
        h.AppendLine("# shellA,surfaces," + t.SurfaceCountA + ",edges," +
                     (t.ShellA == null ? -1 : t.ShellA.EdgeCount) +
                     ",shellB,surfaces," + t.SurfaceCountB + ",edges," +
                     (t.ShellB == null ? -1 : t.ShellB.EdgeCount));
        h.AppendLine("# shellProvenance,authored BladeProfileAsset via BladeShellBuilder — NO Unity runtime " +
                     "mesh; each authored facet is a quad split into .a and .b triangles");
        h.AppendLine("# NOTE,distances are UNSIGNED magnitudes; witnesses are recorded so side can be " +
                     "recovered without inventing a sign convention here");
        h.AppendLine("# NOTE,EdgeEdge is the scenario enum's ZERO value; only rows with " +
                     "classificationValid=1 carry a verdict");
        h.AppendLine("# NOTE,referenceTestsPerFrame," + referenceTestsPerFrame +
                     ",per RENDERED FRAME, cost ceiling only, deferred until after the dwell schedule");
    }

    private string BuildEvents()
    {
        var sb = new StringBuilder();
        AppendPreflight(sb);
        sb.AppendLine(
            "event,step,phaseState,phaseName,stepsInPhase,inDeclaredWindow,scenario,classificationValid," +
            "completion,candidatesRecorded,overflow,nodePairsVisited,warmStartUsed,warmStartKept," +
            "warmSlotA,warmSlotB," +
            "finalKindA,finalIndexA,finalIdA,finalTypeA,finalKindB,finalIndexB,finalIdB,finalTypeB," +
            "finalPairSeparation,finalBestScalar,finalSpecificity,bestScalarMinusPairSeparation," +
            "productionPairRemeasured,remeasuredMinusRecorded,referenceTests,referenceComplete," +
            "poseCheckA,activeTransformA,bodyA,interpolationA,collisionDetectionA,posDeltaA_m,rotDeltaA_deg," +
            "poseCheckB,activeTransformB,bodyB,interpolationB,collisionDetectionB,posDeltaB_m,rotDeltaB_deg");

        for (int i = 0; i < events.Count; i++)
        {
            Event e = events[i];
            BladeClassificationTrace t = e.Trace;
            BladeFeaturePair f = t.Final;

            sb.Append(e.Name).Append(',').Append(t.Step).Append(',')
                .Append(e.PhaseState).Append(',').Append(PhaseName(e.PhaseState)).Append(',')
                .Append(e.StepsInPhase).Append(',')
                .Append(e.PhaseState == captureInPhaseState && e.StepsInPhase >= captureMinStepsInPhase
                    ? 1
                    : 0).Append(',')
                .Append(t.Scenario).Append(',').Append(t.ClassificationValid ? 1 : 0).Append(',')
                .Append(t.Completion).Append(',')
                .Append(t.Candidates.Count).Append(',').Append(t.Overflow).Append(',')
                .Append(t.NodePairsVisited).Append(',')
                .Append(t.WarmStartUsed ? 1 : 0).Append(',').Append(t.WarmStartKept ? 1 : 0).Append(',')
                .Append(t.WarmFeatureA).Append(',').Append(t.WarmFeatureB).Append(',')
                .Append(f.FeatureA.Kind).Append(',').Append(f.FeatureA.Index).Append(',')
                .Append(Id(t.ShellA, f.FeatureA)).Append(',').Append(Type(t.ShellA, f.FeatureA)).Append(',')
                .Append(f.FeatureB.Kind).Append(',').Append(f.FeatureB.Index).Append(',')
                .Append(Id(t.ShellB, f.FeatureB)).Append(',').Append(Type(t.ShellB, f.FeatureB)).Append(',')
                .Append(R(f.Separation)).Append(',').Append(R(t.FinalBestScalar)).Append(',')
                .Append(t.FinalSpecificity).Append(',')
                .Append(R(t.FinalBestScalar - f.Separation)).Append(',')
                .Append(R(e.ProductionPairRemeasured)).Append(',')
                .Append(R(e.ProductionPairRemeasured - f.Separation)).Append(',')
                .Append(e.ReferenceTests).Append(',').Append(e.ReferenceComplete ? 1 : 0).Append(',')
                .Append(Integrity(e.IntegrityA)).Append(',')
                .Append(Integrity(e.IntegrityB))
                .AppendLine();
        }

        return sb.ToString();
    }

    private string BuildCandidates()
    {
        var sb = new StringBuilder();
        AppendPreflight(sb);
        sb.AppendLine(
            "event,step,scenario,ordinal,source,outcome,distance,distanceIsExact,specificity," +
            "bestBefore,bestSpecificityBefore,distanceMinusBestBefore," +
            "kindA,indexA,idA,typeA,kindB,indexB,idB,typeB," +
            "witnessAx,witnessAy,witnessAz,witnessBx,witnessBy,witnessBz");

        for (int i = 0; i < events.Count; i++)
        {
            Event e = events[i];
            BladeClassificationTrace t = e.Trace;

            for (int c = 0; c < t.Candidates.Count; c++)
            {
                BladeCandidateRecord r = t.Candidates[c];

                sb.Append(e.Name).Append(',').Append(t.Step).Append(',').Append(t.Scenario).Append(',')
                    .Append(c).Append(',').Append(r.Source).Append(',').Append(r.Outcome).Append(',')
                    .Append(R(r.Distance)).Append(',').Append(r.DistanceIsExact ? 1 : 0).Append(',')
                    .Append(r.Specificity).Append(',')
                    .Append(R(r.BestBefore)).Append(',').Append(r.BestSpecificityBefore).Append(',')
                    .Append(r.BestBefore == float.MaxValue ? "" : R(r.Distance - r.BestBefore)).Append(',')
                    .Append(r.FeatureA.Kind).Append(',').Append(r.FeatureA.Index).Append(',')
                    .Append(Id(t.ShellA, r.FeatureA)).Append(',')
                    .Append(Type(t.ShellA, r.FeatureA)).Append(',')
                    .Append(r.FeatureB.Kind).Append(',').Append(r.FeatureB.Index).Append(',')
                    .Append(Id(t.ShellB, r.FeatureB)).Append(',')
                    .Append(Type(t.ShellB, r.FeatureB)).Append(',')
                    .Append(V(r.WitnessA)).Append(',').Append(V(r.WitnessB))
                    .AppendLine();
            }
        }

        return sb.ToString();
    }

    private string BuildReference()
    {
        var sb = new StringBuilder();
        AppendPreflight(sb);
        sb.AppendLine(
            "event,step,phaseName,stepsInPhase,scenario,category,minDistance," +
            "indexA,idA,typeA,indexB,idB,typeB," +
            "reachedConsiderInProduction,sphereCulledInProduction,prunedBeforeEvaluation," +
            "isProductionWinner,minusEdgeEdgeMin,minusSharpSharpMin," +
            "facetSharesAuthoredFacetWithEdgeEdgeWinnerA,facetSharesAuthoredFacetWithEdgeEdgeWinnerB," +
            "witnessAx,witnessAy,witnessAz,witnessBx,witnessBy,witnessBz");

        for (int i = 0; i < events.Count; i++)
        {
            Event e = events[i];
            BladeClassificationTrace t = e.Trace;

            int eeA = e.IndexA[(int)Category.EdgeEdge];
            int eeB = e.IndexB[(int)Category.EdgeEdge];

            for (int c = 0; c < CategoryCount; c++)
            {
                var cat = (Category)c;
                if (e.IndexA[c] < 0) continue;

                BladeFeatureRef refA = RefFor(cat, e.IndexA[c], true);
                BladeFeatureRef refB = RefFor(cat, e.IndexB[c], false);

                bool considered = false, culled = false;
                for (int k = 0; k < t.Candidates.Count; k++)
                {
                    BladeCandidateRecord r = t.Candidates[k];
                    if (r.FeatureA.Kind != refA.Kind || r.FeatureA.Index != refA.Index) continue;
                    if (r.FeatureB.Kind != refB.Kind || r.FeatureB.Index != refB.Index) continue;

                    if (r.Outcome == BladeCandidateOutcome.SphereCulled) culled = true;
                    else considered = true;
                }

                bool isWinner =
                    t.Final.FeatureA.Kind == refA.Kind && t.Final.FeatureA.Index == refA.Index &&
                    t.Final.FeatureB.Kind == refB.Kind && t.Final.FeatureB.Index == refB.Index;

                string adjA = "", adjB = "";
                if (cat == Category.EdgeSurface && eeB >= 0)
                    adjB = SharesAuthoredFacet(t.ShellB, eeB, e.IndexB[c]) ? "1" : "0";
                if (cat == Category.SurfaceEdge && eeA >= 0)
                    adjA = SharesAuthoredFacet(t.ShellA, eeA, e.IndexA[c]) ? "1" : "0";

                float eeMin = e.MinDistance[(int)Category.EdgeEdge];
                float ssMin = e.MinDistance[(int)Category.SharpSharp];

                sb.Append(e.Name).Append(',').Append(t.Step).Append(',')
                    .Append(PhaseName(e.PhaseState)).Append(',').Append(e.StepsInPhase).Append(',')
                    .Append(t.Scenario).Append(',')
                    .Append(cat).Append(',').Append(R(e.MinDistance[c])).Append(',')
                    .Append(e.IndexA[c]).Append(',').Append(Id(t.ShellA, refA)).Append(',')
                    .Append(Type(t.ShellA, refA)).Append(',')
                    .Append(e.IndexB[c]).Append(',').Append(Id(t.ShellB, refB)).Append(',')
                    .Append(Type(t.ShellB, refB)).Append(',')
                    .Append(considered ? 1 : 0).Append(',').Append(culled ? 1 : 0).Append(',')
                    .Append(!considered && !culled ? 1 : 0).Append(',')
                    .Append(isWinner ? 1 : 0).Append(',')
                    .Append(eeMin == float.MaxValue ? "" : R(e.MinDistance[c] - eeMin)).Append(',')
                    .Append(ssMin == float.MaxValue ? "" : R(e.MinDistance[c] - ssMin)).Append(',')
                    .Append(adjA).Append(',').Append(adjB).Append(',')
                    .Append(V(e.WitnessA[c])).Append(',').Append(V(e.WitnessB[c]))
                    .AppendLine();
            }
        }

        return sb.ToString();
    }

    /// <summary>
    ///     True when <paramref name="surfaceIndex" /> belongs to one of the AUTHORED FACETS adjoining
    ///     <paramref name="edgeIndex" />, read from the profile rather than inferred from geometry.
    /// </summary>
    /// <remarks>
    ///     <b>Compares authored facets, not triangle slots.</b> BladeShellBuilder splits each authored quad
    ///     facet into two triangles, <c>.a</c> at slot <c>2i</c> and <c>.b</c> at <c>2i+1</c>, and an edge's
    ///     <see cref="BladeEdgeLine.SurfaceIndexA" />/<c>B</c> name only the <c>.a</c> halves. Comparing raw
    ///     slot indices therefore reports "not adjoining" whenever the winning triangle is a <c>.b</c> half
    ///     of an adjoining facet — a false negative that run 01 came within one row of hitting, since
    ///     production's own E2 winner was <c>s68.f6.b</c>. Halving both sides compares the facet each
    ///     triangle belongs to.
    /// </remarks>
    private static bool SharesAuthoredFacet(BladeShellData shell, int edgeIndex, int surfaceIndex)
    {
        if (shell == null || edgeIndex < 0 || edgeIndex >= shell.EdgeCount) return false;

        BladeEdgeLine l = shell.GetEdge(edgeIndex);
        int facet = surfaceIndex >> 1;
        return (l.SurfaceIndexA >> 1) == facet || (l.SurfaceIndexB >> 1) == facet;
    }

    private static BladeFeatureRef RefFor(Category cat, int index, bool sideA)
    {
        bool edge = cat == Category.EdgeEdge || cat == Category.SharpSharp ||
                    (sideA ? cat == Category.EdgeSurface : cat == Category.SurfaceEdge);
        return new BladeFeatureRef(edge ? BladeFeatureKind.Edge : BladeFeatureKind.Surface, index);
    }

    /// <summary>Seven columns. Blank when no blade was wired, so unchecked reads as unchecked.</summary>
    private static string Integrity(PoseIntegrity p) =>
        !p.Captured
            ? "0,,,,,,"
            : "1," + p.ActiveTransformName + "," + p.BodyName + "," + p.Interpolation + "," +
              p.CollisionDetection + "," + R(p.PositionDelta) + "," + R(p.RotationDeltaDegrees);

    private static string Id(BladeShellData shell, BladeFeatureRef f) =>
        shell == null || !f.IsValid ? "" : shell.IdOf(f);

    private static string Type(BladeShellData shell, BladeFeatureRef f) =>
        shell == null || !f.IsValid ? "" : shell.TypeOf(f).ToString();
}
