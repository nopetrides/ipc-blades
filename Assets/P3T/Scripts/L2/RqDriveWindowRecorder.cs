// =====================================================================================
// THE CHAPTER 5 COMPARISON DRIVER — see PLAN_DriveWindowRecording.md §6 items 2-4.
//
// It exists because no instrument on this rig recorded the DRIVE window. The fork recorder
// (RqClassificationForkRecorder) walks Pause->SNAP->HOLD->CONTACT and stops: it never commands
// State 4, and its capture window is phase 3. That is correct for its own question — approach,
// attribution, latch — and it is not the Chapter 4/5 window.
//
// WHAT MAKES THIS LIKE-FOR-LIKE, and it is the whole point:
//   * it reads NOTHING from the contact representation under test. No BladeShell, no
//     BladeProfileAsset, no classifier scenario. The solver reference below is metadata only and
//     is written to the header, never read into a decision.
//   * run validity comes from RqContactSideProbe, which is marker-derived and identical on all
//     ten stations (CH4_MeasurementContract.md §4).
//   * the measurement itself comes from RqStationContactRecorder, which is already identical on
//     every station.
//
// So the same three components produce the same file on PhysX, QuickDirty and OurSolution, and
// the only thing that differs between the three is the contact representation.
//
// WHAT IT DOES NOT DO. It applies no force, writes no material, touches no collider, and never
// sets ApplyTangentialBehaviour or any other treatment field. It drives State and Auto on the two
// station animators — which is the protocol, not the treatment — and it deactivates other station
// roots for the duration, runtime only.
// =====================================================================================
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
///     Walks one station through the declared protocol, gates the run on the representation-independent
///     contact-side reading, and seals the DRIVE window as the Chapter 5 comparison record.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why the window starts at DRIVE.</b> The physical record measures from the onset of Sword
///         A's forward passing step. In this apparatus that onset is DRIVE; Snap, Hold and Contact are
///         the setup equivalents and the procedural record treats them as setup. See
///         PLAN_DriveWindowRecording.md §1.
///     </para>
///     <para>
///         <b>Why the window's LENGTH is not a field here.</b> It is the length of the drive clip,
///         declared 2026-08-26. Read off the clip, not stated as a number: any figure written into this
///         component would go stale the moment a clip is re-authored, and a reader would then have a
///         declared duration that disagreed with what actually ran. The window closes when both
///         animators' drive state has played through once. The achieved length is REPORTED on every row.
///     </para>
///     <para>
///         <b>The pre-gate, and why it is a pre-gate.</b> A pass-through that happened during setup
///         produces a Drive segment that reads as a clean terminal bind-to-release history. Recorded
///         without the gate it is indistinguishable from an observed release, which is a Chapter 3
///         observable. Release and pass-through must never occupy the same column, so a run whose
///         relationship has already failed is refused rather than recorded. A refusal is a RESULT: it is
///         logged, counted and sealed as a refusal file.
///     </para>
///     <para>
///         <b>What this component deliberately does not decide.</b> It records a mid-Drive inversion as
///         counts and a step index; it does not invalidate the window on its own authority. The entry
///         gate is a declared criterion — the probe's <c>Inverted</c> at the Drive transition — while
///         whether an inversion appearing part-way through Drive invalidates the window is the
///         researcher's call. The columns are there so it can be made from the file.
///     </para>
///     <para>
///         <b>Every column is a measured quantity or a state, and none is a conclusion.</b> Contact
///         positions, counts, forces and geometry are recorded; nothing here labels a mechanism or
///         attributes a cause. Interpretation belongs to whatever reads the file, on evidence stated
///         there — a column that pre-judged it would make the instrument part of the argument.
///     </para>
/// </remarks>
// +200, BEHIND everything it reads. RqStationContactRecorder carries no DefaultExecutionOrder and so
// runs at 0, alongside Ragdoll Animator; the solver is -100 and the fork recorder +100. This driver
// reads the station recorder's per-step Touching flag to locate a release, and reading an order-0
// script from another order-0 script is undefined ordering — which is precisely the defect that
// produced 110 phantom disagreements in the probe's own agreement harness, where a cached value was
// compared against a reference computed later in the same step. Ordering last makes every value this
// driver reads belong to the step it is reporting.
[DefaultExecutionOrder(200)]
[DisallowMultipleComponent]
public sealed class RqDriveWindowRecorder : RqRunObserver
{
    private const string AutoParam = "Auto";
    private const string StateParam = "State";

    /// <summary>How the entry gate resolved. Both Refused values stop the run before Drive is commanded.</summary>
    public enum Gate
    {
        /// <summary>Not evaluated yet.</summary>
        Pending,

        /// <summary>Read, a reference was declared, and it was not inverted.</summary>
        Pass,

        /// <summary>Read, a reference was declared, and BOTH sides had departed it. A pass-through.</summary>
        RefusedInverted,

        /// <summary>
        ///     The probe could not produce a reading. Not a pass: the declared rule is that a run
        ///     evaluated beyond approach MUST read the contact-side value, so a run that cannot be read
        ///     cannot be gated.
        /// </summary>
        RefusedUnreadable,

        /// <summary>
        ///     Read, but the probe carries no declared reference, so the criterion cannot fire. The run
        ///     is recorded and flagged UNGATED. Applies to Flat_Rolled_PhysX, which has no counterpart to
        ///     inherit a reference from.
        /// </summary>
        PassUngated
    }

    /// <summary>One sealed segment of one run.</summary>
    private struct Segment
    {
        public string Label;
        public int StartStep;
        public int Steps;
        public RqStationContactRecorder.Record Record;

        // Contact-side at the instant this segment was opened.
        public bool EntryRead;
        public string EntryLeadPair;
        public bool EntryInverted;
        public bool EntryOneSideChanged;
        public float EntryClosestMm;
        public float EntryAcrossEdgeAMm;
        public float EntryAcrossEdgeBMm;
        public float EntryAlongBladeAMm;
        public float EntryAlongBladeBMm;

        // WORLD contact points, kept as the original math, plus the station-relative secondary.
        // See the AppendWorldPair remarks: the world value is never removed or replaced.
        public Vector3 EntryContactAWorld;
        public Vector3 EntryContactBWorld;

        // BOTH pairs, not only the declared term's. A declaration names one term, and the verdict
        // compares against that term alone. Recording only its pair cannot separate an evolution of
        // contact (a face pair giving way to an edge pair) from a pass-through (the same term
        // flipping sign on both sides). Both are computed every step by the probe regardless of what
        // is declared, so carrying both costs nothing and keeps the two results distinguishable.
        public string EntryEdgePair;
        public string EntryFacePair;

        // And at the instant it was closed.
        public bool ExitRead;
        public string ExitLeadPair;
        public bool ExitInverted;
        public string ExitEdgePair;
        public string ExitFacePair;

        // Steps on which each pair differed from its own value at segment entry. Descriptive only:
        // no threshold, no claim about which term is "engaged" — that criterion is the researcher's.
        public int EdgePairChangedSteps;
        public int FacePairChangedSteps;

        // Accumulated over the segment. Recorded, never acted on — see the class remarks.
        public int InvertedSteps;
        public int OneSideChangedSteps;
        public int UnreadableSteps;
        public int FirstInversionStep;

        public int ReleaseEvents;
        public int ReEngageEvents;

        // Station-frame RECOMPUTATION of the coupling terms, from the same raw poses at the same
        // instant. Expected to equal the originals; printed so that is shown rather than assumed.
        // Proxy rotations at the FIRST in-window step, and the blade rotation recomputed from them at seal.
        //
        // rot_a_deg is Quaternion.Angle(zeroRotation, frame.rotation), where zeroRotation is latched by
        // LatchZero() at the segment transition. These recompute the same angle from the first step the
        // window actually SAMPLED. The two agree only while those instants coincide, so the delta is a
        // standing per-run check on that and not a restatement of the same arithmetic. Measured 0.0001 deg
        // on A and 0.0000 deg on B when the endpoints were taken from the logged step-trace quaternions.
        public bool RotCheckComputed;
        public Quaternion QFirstA;
        public Quaternion QFirstB;
        public Quaternion QLastA;
        public Quaternion QLastB;
        public float RotACheckDeg;
        public float RotBCheckDeg;
        public float RotADeltaDeg;
        public float RotBDeltaDeg;

        public bool InvarianceComputed;
        public float StnRotADeg;
        public float StnRotBDeg;
        public float StnRotGainBA;
        public float StnTipAMm;
        public float StnTipBMm;
        public float StnTipGainBA;
        public float InvarianceWorstDelta;

        // Per-step statistics for this window, built by RqSeries at close. Held as the finished row
        // because the series objects are reused for the next segment and a struct copy of them would
        // alias the same lists.
        /// <summary>The series file for this segment, built at close while the samples still exist.</summary>
        public string SeriesRows;

        /// <summary>The per-step file for this segment, built at close from the same samples.</summary>
        public string StepRows;
    }

    /// <summary>
    ///     One contact-state change, with the contact position on each blade at that step.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Records POSITIONS AND STATES, not causes. It captures the reading on the last sample the
    ///         pair was touching and on the first sample it was not, on both blades, in each blade's own
    ///         frame, together with each blade's own tip coordinate on the same axis so a position can be
    ///         placed without a second lookup.
    ///     </para>
    ///     <para>
    ///         <b>It does not classify why contact ended and carries no column that does.</b> The contact
    ///         point is recorded because it is the observable; a release count on its own is a bare
    ///         integer. Any reading of these numbers as a mechanism is a separate argument, made
    ///         elsewhere, on evidence this file does not contain.
    ///     </para>
    ///     <para>
    ///         One limit worth stating because it bounds what the after_* columns can mean: the
    ///         closest-approach query returns a surface pair whatever the separation, so an after_*
    ///         reading exists even when the blades are far apart. It is a position, not a contact.
    ///     </para>
    /// </remarks>
    private struct BoundaryEvent
    {
        public string Segment;
        public string Kind;
        public int StepInSegment;

        // The last step the contact existed. This is the discriminating instant.
        public bool LastTouchRead;
        public string LastTouchLeadPair;
        public float LastTouchAlongAMm;
        public float LastTouchAlongBMm;
        public float LastTouchAcrossAMm;
        public float LastTouchAcrossBMm;
        public float LastTouchClosestMm;
        public Vector3 LastTouchContactAWorld;
        public Vector3 LastTouchContactBWorld;

        // The step after, so both instants are on the record rather than one being inferred.
        public bool AfterRead;
        public string AfterLeadPair;
        public bool AfterInverted;
        public float AfterClosestMm;
        public float AfterAlongAMm;
        public float AfterAlongBMm;

        // Each blade's own span, on the row, so it is self-contained.
        public float TipAlongAMm;
        public float TipAlongBMm;
    }

    /// <summary>One step's contact-side reading, held so the step BEFORE a release can be reported.</summary>
    private struct Reading
    {
        public bool Read;
        public bool PairPresent;
        public string LeadPair;
        public float AlongAMm;
        public float AlongBMm;
        public float AcrossAMm;
        public float AcrossBMm;
        public float ClosestMm;
        public Vector3 ContactAWorld;
        public Vector3 ContactBWorld;
    }

    [Header("Identity — declared, written into every file")]
    [Tooltip("Station label. Written into the file name and the header. Nothing behaves differently " +
             "because of it.")]
    [SerializeField] private string stationId = "";

    [Tooltip("Treatment label — PhysX, QuickDirty or OurSolution. HEADER ONLY. This component reads no " +
             "behaviour from it, which is the property that makes the three arms comparable through it.\n\n" +
             "This is the SCENE's name for the arm. The thesis reporting name is armName below, and both " +
             "are sealed: the scene name is what a reader matches against the GameObject, and the thesis " +
             "name is what the chapter calls it.")]
    [SerializeField] private string treatment = "";

    [Header("Thesis reporting names — declared per station, never inferred")]
    [Tooltip("The presentation as the THESIS names it. Broad-face control / Edge-presented non-bind / " +
             "Edge-presented bind-like / Rolled broad-face exploration.\n\n" +
             "Declared here rather than derived from the station name, because a mapping in code would " +
             "be station-specific logic in the instrument and the scene naming is frozen for other " +
             "reasons. Both names are sealed into every file so neither has to be looked up.")]
    [SerializeField] private string presentationName = "";

    [Tooltip("The contact representation as the THESIS names it. Standard Unity / Coarse approximation / " +
             "Augmented exploratory.")]
    [SerializeField] private string armName = "";

    [Tooltip("The physical-reference pass this station was built from — PASS-09, PASS-03, PASS-08, or " +
             "'PASS-09 pose, rolled'.\n\n" +
             "Sealed because a virtual run is only comparable to the physical record through the pass it " +
             "was reconstructed from, and Chapter 5 has to be able to state that per run rather than by " +
             "recalling which station came from which pass.")]
    [SerializeField] private string sourcePass = "";

    [Tooltip("WHY that pass was selected, in the thesis's own words. Recorded so the selection basis " +
             "travels with the data instead of living only in a table: 'closest to the selected central " +
             "flat-motion measure' and 'strongest observed bind-like reference selected for " +
             "reconstruction' are different kinds of selection and license different claims.")]
    [SerializeField] private string selectionBasis = "";

    [Header("Instruments — both references are required")]
    [Tooltip("The measurement. Identical on every station and reads nothing from any treatment.")]
    [SerializeField] private RqStationContactRecorder recorder;

    [Tooltip("Run validity. Marker-derived and representation-independent, which is the whole reason it " +
             "and not the classifier gates the run — CH4_MeasurementContract.md §4.")]
    [SerializeField] private RqContactSideProbe contactSide;

    [Header("Metadata only — NEVER read into a decision")]
    [Tooltip("The augmenting layer, when the station runs one. Present ONLY so the sealed header can " +
             "state applyTangentialBehaviour, which is the field that says which treatment a run was.\n\n" +
             "Left empty on a PhysX or QuickDirty station, and the header then says NO LAYER PRESENT — " +
             "which is a different fact from a layer present and switched off, and the two must not be " +
             "confusable in a file. This component never writes to it.")]
    [SerializeField] private RqBladeTangentialSolver metadataSolver;

    [Tooltip("Optional but strongly wanted. The friction actually in force on the live shapes, sealed " +
             "into the header. Every one of QuickDirty's three silent failure modes was found by reading " +
             "the configuration rather than by a measurement noticing anything, and a run whose file " +
             "states only a DECLARED coefficient cannot show which one it used.")]
    [SerializeField] private RqFrictionProbe friction;

    [Tooltip("Optional but strongly wanted. The actuator settings in force - the authored handler spring " +
             "AND the spring the generated joints actually carry. Measured 2026-08-26: SpringsValue is " +
             "5000 on 27 of 29 dummies and 1500 on the two halves of Station_EdgeNoBind_OurSolution, " +
             "which the spring sweep left off-nominal. No sealed file said so, because the spring was " +
             "never in a run header.")]
    [SerializeField] private RqActuatorProbe actuator;

    [Tooltip("Optional. When present, its declared-vs-live verification is invoked at arm and the fact " +
             "that it ran goes in the header, so a run states that its physics settings were checked " +
             "against the declared ones rather than leaving it to be remembered.")]
    [SerializeField] private DeclaredPhysicsSettings declaredPhysics;

    [Tooltip("Optional but strongly wanted. Contact-patch EXTENT, which is the sharpness observable and " +
             "the one RQ quantity no other instrument reports. s_contacts counts points; a count is not " +
             "an extent. Without this the file can say where contact sat and how long it lasted, but not " +
             "how concentrated it was - which is the geometric difference the three arms are built to " +
             "express.")]
    [SerializeField] private RqContactPatchProbe patch;

    [Tooltip("Optional. The cross-boundary marker capture. It writes its OWN csv and contributes no " +
             "columns here - only a header line naming its output, so a sealed run can be tied to the " +
             "physical-reference file taken alongside it. Its values are NOT EQUIVALENT to physical " +
             "measurements; thesis 4.1 governs and its own file says so.")]
    [SerializeField] private RqPhysicalReferenceProbe physRef;


    [Header("Windows")]
    [Tooltip("Record SNAP, HOLD and CONTACT as their own labelled segments as well.\n\n" +
             "OFF by default. They are SETUP CHARACTERISATION and answer 'did the apparatus reach a " +
             "valid starting relationship', never 'did the representation reproduce the behaviour'. " +
             "Every segment carries its phase label so no downstream reader can pool them by accident.\n\n" +
             "Turning this on does not move the Drive segment: the zero is re-latched at each phase " +
             "boundary either way, so the Drive segment of a run with these on is the same window as the " +
             "Drive segment of a run with them off.")]
    [SerializeField] private bool recordSetupSegments;


    [Header("Output")]
    [Tooltip("Root for sealed runs. DELIBERATELY NOT Workspace/Week7/Phase1.\n\n" +
             "Phase1 holds the approach/latch tranche and the INVALIDATED_DO_NOT_CITE files — a " +
             "different question, a different window and a different validity regime. Writing the Drive " +
             "window into the same directory would put two incompatible record types behind one glob, " +
             "and anything picking files out of a directory listing could pool them.")]
    /// <remarks>
    ///     BLANK by default, deliberately. It used to ship an absolute path from one machine, which
    ///     both leaked a folder structure into a public repository and silently wrote another
    ///     machine's runs into a directory that did not describe them. Blank cannot do either, and
    ///     the write path below refuses with a named error rather than throwing on
    ///     CreateDirectory("").
    /// </remarks>
    [SerializeField] private string outputDirectory = "";

    [Tooltip("The SET this run belongs to. Every run is written into outputDirectory/<setName>/, so one " +
             "declared configuration's runs sit together and cannot be swept up with another's.\n\n" +
             "A set is whatever the researcher declares it to be — normally one frozen configuration. " +
             "Left empty the folder is named set_UNDECLARED and a warning is logged, because an " +
             "undeclared set is a real problem with the record and should not look tidy.")]
    [SerializeField] private string setName = "";

    // ---- DRIVE-WINDOW FRAME CAPTURE -------------------------------------------------------------
    // Two frames per run: the Contact->Drive transition and the seal. Enough to see what the window
    // started from and what it ended in, which is what a reader of the numbers keeps asking for.
    //
    // NOTHING IS WRITTEN TO DISK DURING THE WINDOW. The start frame is held in memory as a Texture2D
    // and both PNGs are encoded and written in Seal(), after the window is over. A PNG encode plus a
    // file write inside the drive window would stall a fixed step, and a stall during simulation is
    // exactly what voided a previous run. The cost inside the window is one camera render plus one
    // ReadPixels on a single step; it is timed and logged so the cost is auditable rather than assumed.
    //
    // Mid-window frames are deliberately NOT captured. They are cheap to add on this design - the
    // frames are held in memory, so N frames cost memory rather than disk - but each still carries a
    // ReadPixels stall on its step, and no one has declared how many are wanted or where.
    [Header("Drive-window frame capture")]
    [Tooltip("Capture one frame at the Contact->Drive transition and one at the seal. Written to disk " +
             "only at seal time, never during the window.")]
    [SerializeField] private bool captureDriveFrames = true;

    [Tooltip("Vertical resolution of the captured frames. Width follows the camera aspect. 720 is the " +
             "declared default.")]
    [SerializeField] private int driveFrameHeight = 720;

    [Tooltip("Optional. Leave empty to resolve the station's own ViewCam from its DriveTrailRecorder, " +
             "which is the same camera the PhysicalReference channel projects through.")]
    [SerializeField] private Camera driveFrameCamera;

    private Texture2D driveStartFrame;
    private Texture2D driveMidFrame;
    private Texture2D driveEndFrame;
    private double driveStartCaptureMs;
    private double driveMidCaptureMs;
    private double driveEndCaptureMs;

    // The recorder's own step at the Contact->Drive transition. Only ever used to turn a recorder step
    // into an IN-WINDOW index; it is never written to a file.
    private int driveWindowZeroStep = -1;

    // In-window step indices: 0 at the Drive transition, the same basis as first_inversion_step and the
    // boundaries file's step_in_segment. An earlier version named frames with the recorder's own counter,
    // which is zero at ARMING - a third scale that matched neither the step trace nor a global count, and
    // made a frame impossible to line up against anything.
    private int driveStartWindowStep = -1;
    private int driveMidWindowStep = -1;
    private int driveEndWindowStep = -1;
    private float driveMidNormalizedTime = -1f;

    // Capture is REQUESTED from the physics step and PERFORMED at end of frame. See CaptureAtEndOfFrame.
    private int pendingCaptureSlot;   // 0 none, 1 start, 2 mid, 3 end
    private string pendingFrameStem;
    private bool framesWritten;


    // ----- state -----------------------------------------------------------------------------------

    private readonly List<Segment> segments = new List<Segment>();
    private readonly List<BoundaryEvent> boundaries = new List<BoundaryEvent>();

    // ----- per-step series ------------------------------------------------------------------------
    //
    // Every observable that exists per step, accumulated across the window and summarised in code
    // before the seal. The end-of-window value each of these used to be sealed as is KEPT as well and
    // is not replaced: a final value and a distribution are different facts.
    private readonly List<RqSeries> series = new List<RqSeries>(32);

    private RqSeries sPair, sOverlap, sSeparation, sContacts, sNormalN, sOffNormalN, sAddedTanN, sTanSpeed;
    private RqSeries sMigA, sMigB, sSlip, sRotA, sRotB, sRotGain, sTipA, sTipB, sTipGain;
    private RqSeries sDeepestPen, sNormalSwing;
    private RqSeries sClosest, sAlongA, sAlongB, sAcrossA, sAcrossB;
    private RqSeries sFaceDotA, sFaceDotB, sLagA, sLagB;
    private RqSeries sPatchPoints, sPatchOverlapPoints, sPatchShapePairs;
    private RqSeries sPatchAcrossA, sPatchAcrossB, sPatchAlongA, sPatchAlongB;
    private RqSeries sPatchOvlAcrossA, sPatchOvlAcrossB, sPatchOvlAlongA, sPatchOvlAlongB;
    private RqSeries sPatchNormalSpread;

    private void BuildSeries()
    {
        if (series.Count > 0) return;

        // Contact state and load, per step.
        // PAIR and OVERLAP are separate series on purpose. A pair exists inside the contact offset
        // while the blades are still apart; only an overlap means the surfaces met. Their difference
        // per window is the width of the speculative envelope in steps.
        series.Add(sPair = new RqSeries("s_pair", "0/1"));
        series.Add(sOverlap = new RqSeries("s_overlap", "0/1"));
        // RENAMED FOR CLARITY. Previously serialized as "s_min_separation_mm". The underlying field is a
        // RUN-CUMULATIVE running minimum, so these ten statistics describe a monotone staircase, not a
        // distribution of separations. Mean and sd in particular measure WHEN the minimum latched.
        series.Add(sSeparation = new RqSeries("s_run_min_separation_mm", "mm"));
        series.Add(sContacts = new RqSeries("s_contacts", "count"));
        series.Add(sNormalN = new RqSeries("s_normal_n", "N"));
        series.Add(sOffNormalN = new RqSeries("s_off_normal_n", "N"));
        series.Add(sAddedTanN = new RqSeries("s_added_tan_n", "N"));
        series.Add(sTanSpeed = new RqSeries("s_tan_speed_mps", "m/s"));

        // The primary kinematics. These are the Chapter 3 observables, per step rather than at the end.
        series.Add(sMigA = new RqSeries("s_mig_a_mm", "mm"));
        series.Add(sMigB = new RqSeries("s_mig_b_mm", "mm"));
        series.Add(sSlip = new RqSeries("s_slip_mm", "mm"));
        series.Add(sRotA = new RqSeries("s_rot_a_deg", "deg"));
        series.Add(sRotB = new RqSeries("s_rot_b_deg", "deg"));
        series.Add(sRotGain = new RqSeries("s_rot_gain_ba", "-"));
        series.Add(sTipA = new RqSeries("s_tip_a_mm", "mm"));
        series.Add(sTipB = new RqSeries("s_tip_b_mm", "mm"));
        series.Add(sTipGain = new RqSeries("s_tip_gain_ba", "-"));

        // Already-cumulative terms. Their series is monotone by construction and is emitted anyway, so
        // the monotonicity is visible in the file rather than assumed from the field's name.
        // RENAMED FOR CLARITY. Previously serialized as "s_deepest_pen_mm". Same running minimum as
        // s_run_min_separation_mm above, differing only in seed, so the two are identical once a run
        // penetrates at all.
        series.Add(sDeepestPen = new RqSeries("s_run_deepest_pen_mm", "mm"));
        series.Add(sNormalSwing = new RqSeries("s_normal_swing_deg", "deg"));

        // The representation-INDEPENDENT geometry, from the marker-derived probe.
        series.Add(sClosest = new RqSeries("s_closest_mm", "mm"));
        series.Add(sAlongA = new RqSeries("s_along_a_mm", "mm"));
        series.Add(sAlongB = new RqSeries("s_along_b_mm", "mm"));
        series.Add(sAcrossA = new RqSeries("s_across_a_mm", "mm"));
        series.Add(sAcrossB = new RqSeries("s_across_b_mm", "mm"));
        series.Add(sFaceDotA = new RqSeries("s_face_dot_a", "-"));
        series.Add(sFaceDotB = new RqSeries("s_face_dot_b", "-"));

        // Render lag, so the known interpolation frame mismatch is quantified per run instead of cited.
        series.Add(sLagA = new RqSeries("s_render_lag_a_mm", "mm"));
        series.Add(sLagB = new RqSeries("s_render_lag_b_mm", "mm"));

        // CONTACT PATCH — the sharpness observable. Extent, not a count. Reported for the whole
        // delivered set AND for the overlap subset separately, because a patch measured across the
        // speculative envelope is not a contact patch.
        series.Add(sPatchPoints = new RqSeries("s_patch_points", "count"));
        series.Add(sPatchOverlapPoints = new RqSeries("s_patch_overlap_points", "count"));
        series.Add(sPatchShapePairs = new RqSeries("s_patch_shape_pairs", "count"));
        series.Add(sPatchAcrossA = new RqSeries("s_patch_across_a_mm", "mm"));
        series.Add(sPatchAcrossB = new RqSeries("s_patch_across_b_mm", "mm"));
        series.Add(sPatchAlongA = new RqSeries("s_patch_along_a_mm", "mm"));
        series.Add(sPatchAlongB = new RqSeries("s_patch_along_b_mm", "mm"));
        series.Add(sPatchOvlAcrossA = new RqSeries("s_patch_ovl_across_a_mm", "mm"));
        series.Add(sPatchOvlAcrossB = new RqSeries("s_patch_ovl_across_b_mm", "mm"));
        series.Add(sPatchOvlAlongA = new RqSeries("s_patch_ovl_along_a_mm", "mm"));
        series.Add(sPatchOvlAlongB = new RqSeries("s_patch_ovl_along_b_mm", "mm"));
        series.Add(sPatchNormalSpread = new RqSeries("s_patch_normal_spread_deg", "deg"));
    }

    private void ResetSeries()
    {
        BuildSeries();
        for (int i = 0; i < series.Count; i++) series[i].Reset();
    }

    /// <summary>Takes one sample of every series. Called once per step of an open segment.</summary>
    private void AccumulateSeries(Reading now)
    {
        // One assignment locates the maximum of EVERY series. Zero at the segment's first step, which is the
        // same basis first_inversion_step uses - so an argmax and an inversion step read against each other
        // without a translation.
        //
        // Computed live from StartStep rather than read from open.Steps: that field is assigned ONLY in
        // CloseSegment, so during the window it is still 0 and every series would locate its maximum at
        // step 0. Caught on the verification run - all 40 argmax columns read 0, which is a plausible
        // number and a wrong one, exactly the class of failure this instrument exists to avoid.
        RqSeries.SampleStep = step - open.StartStep;

        // Proxy rotations at the first and most-recent sampled step, for the rot_*_deg check at seal.
        // BOTH endpoints come from sampled steps so the check differs from rot_*_deg in its ZERO ONLY.
        // Reading the end pose at CloseSegment instead mixed an end-instant mismatch into the delta and
        // reported -0.1603 deg on A for a blade rotation that is demonstrably correct to 0.0001 deg.
        {
            Transform fa = recorder.FrameA, fb = recorder.FrameB;
            if (fa != null && fb != null)
            {
                if (!open.RotCheckComputed)
                {
                    open.QFirstA = fa.rotation;
                    open.QFirstB = fb.rotation;
                    open.RotCheckComputed = true;
                }

                open.QLastA = fa.rotation;
                open.QLastB = fb.rotation;
            }
        }

        RqStationContactRecorder.Record c = recorder.Current;

        // Whether the pair delivered contact on this step, latched alongside the step index and for the
        // same reason: one assignment labels every sample taken below. PairPresent is the recorder's own
        // touching flag - it is set from `reportedStep == step - 1 && contactCount > 0`, and it is what
        // gates the force accumulator that produces the undiluted means.
        //
        // Without this a series can only produce ONE mean, over every step it was given, and the force
        // series are explicitly ZEROED on a step with no contact. That mean is then the quantity
        // multiplied by the duty cycle: on Station_Flat_QuickDirty, touching on 83 of 234 steps, it
        // understates the normal force by a factor of 2.8.
        RqSeries.SampleTouching = c.PairPresent;

        // Whether the contact side was inverted on this step, latched the same way. Read here rather
        // than counted later because contact_drive_window MASKS inverted steps: truncating at the
        // first one is right only for a state that never reverses, and this set contains both a
        // sustained crossing that never does and a one-or-two-step excursion that always does.
        // Gated on now.Read for the same reason inverted_steps is - an unreadable step is not an
        // inverted one.
        RqSeries.SampleInverted = now.Read && contactSide != null && contactSide.Inverted;

        sPair.Add(now.PairPresent ? 1f : 0f);
        sOverlap.Add(c.Overlapping ? 1f : 0f);
        sSeparation.Add(c.MinSeparation == float.MaxValue ? 0f : c.MinSeparation * 1000f);
        sContacts.Add(c.ContactCount);
        sNormalN.Add(c.NormalForce);
        sOffNormalN.Add(c.OffNormalImpulseForce);
        sAddedTanN.Add(c.AddedTangentialForce);
        sTanSpeed.Add(c.TangentialSpeed);

        sMigA.Add(c.MigrationAlongA * 1000f);
        sMigB.Add(c.MigrationAlongB * 1000f);
        sSlip.Add(c.TangentialSlipPath * 1000f);
        sRotA.Add(c.RotationADeg);
        sRotB.Add(c.RotationBDeg);
        sRotGain.Add(c.RotationGainBA);
        sTipA.Add(c.TipTravelA * 1000f);
        sTipB.Add(c.TipTravelB * 1000f);
        sTipGain.Add(c.TipGainBA);

        sDeepestPen.Add(c.DeepestPenetration * 1000f);
        sNormalSwing.Add(c.PeakNormalSwing);

        // CONTACT PATCH, sampled BEFORE the contact-side readability gate below. The patch probe has its
        // own resolve state and its own reason to be silent; suppressing it because the contact-side
        // probe could not read this step would conflate two independent instruments.
        //
        // A step with no delivered contact points contributes NO sample rather than a zero. Zero extent
        // and no contact are different facts, and folding them together would drag every mean toward
        // zero in proportion to how often the blades were apart. The gap shows as a lower _n.
        if (patch != null && patch.Resolved && patch.HasReading)
        {
            sPatchPoints.Add(patch.PairPoints);
            sPatchOverlapPoints.Add(patch.OverlapPoints);
            sPatchShapePairs.Add(patch.ShapePairs);
            sPatchAcrossA.Add(patch.PairExtentAcrossAMm);
            sPatchAcrossB.Add(patch.PairExtentAcrossBMm);
            sPatchAlongA.Add(patch.PairExtentAlongAMm);
            sPatchAlongB.Add(patch.PairExtentAlongBMm);
            sPatchNormalSpread.Add(patch.NormalSpreadDeg);

            // The overlap subset only when there IS one, for the same reason.
            if (patch.OverlapPoints > 0)
            {
                sPatchOvlAcrossA.Add(patch.OverlapExtentAcrossAMm);
                sPatchOvlAcrossB.Add(patch.OverlapExtentAcrossBMm);
                sPatchOvlAlongA.Add(patch.OverlapExtentAlongAMm);
                sPatchOvlAlongB.Add(patch.OverlapExtentAlongBMm);
            }
        }

        // Only when the probe actually read. A skipped sample is visible as a lower _n than the
        // recorder-sourced series, which is how an unreadable stretch shows up rather than being
        // smuggled in as a zero.
        if (!now.Read) return;

        sClosest.Add(now.ClosestMm);
        sAlongA.Add(now.AlongAMm);
        sAlongB.Add(now.AlongBMm);
        sAcrossA.Add(now.AcrossAMm);
        sAcrossB.Add(now.AcrossBMm);
        sFaceDotA.Add(contactSide.AFaceDot);
        sFaceDotB.Add(contactSide.BFaceDot);
        sLagA.Add(contactSide.ARenderLagMm);
        sLagB.Add(contactSide.BRenderLagMm);
    }

    /// <summary>
    ///     The series file: every observable summarised over all FOUR combinations of span and
    ///     denominator, one row each. Long format on purpose - span and denominator are VALUES, so a
    ///     reader filters on them rather than parsing them out of a column name.
    /// </summary>
    /// <remarks>
    ///     This left the drive file in REPEATABILITY4. Wide, it would have been forty series times
    ///     eleven statistics times four cells; and the one prefix-encoded variant that already existed -
    ///     mean_normal_n, whose trailing _n is NEWTONS and not the sample count every neighbouring
    ///     column uses that suffix for - had already been misread as a sample count.
    /// </remarks>
    private string BuildSeriesRows(int firstInversionStep)
    {
        BuildSeries();

        var sb = new StringBuilder(16384);
        sb.AppendLine("# Series statistics for the DRIVE window. One row per series per span per denominator.");
        sb.AppendLine("# span,full_drive_window,every sampled step,contact_drive_window,every step the " +
                      "contact side was NOT inverted");
        sb.AppendLine("# note,contact_drive_window MASKS inverted steps rather than truncating at the " +
                      "first one. A sustained crossing never reverses - its inverted span runs to the " +
                      "last step of the window - so for it the two are identical. An excursion " +
                      "reverses within one or two steps and truncating would discard every valid step " +
                      "after it.");
        sb.AppendLine("# denominator,all_steps,every step in the span,touching_only,only steps the pair delivered contact");
        sb.AppendLine("# first_inversion_step," + firstInversionStep.ToString(CultureInfo.InvariantCulture) +
                      ",note,negative means the run never inverted and the two spans are identical");

        // TWO DIFFERENT n. The column named n is the SAMPLE COUNT for that row. The trailing _n on
        // s_normal_n, s_off_normal_n and s_added_tan_n is the UNIT, newtons. That collision is what
        // made mean_normal_n readable as a sample count for as long as it was; the unit column now
        // states the unit on every row, and this line says so in the file rather than only in code.
        sb.AppendLine("# n,the SAMPLE COUNT for this row,note,the trailing _n on s_normal_n and " +
                      "s_off_normal_n and s_added_tan_n is the UNIT newtons and is not a count - " +
                      "read the unit column, which states it for every row");
        sb.AppendLine(RqSeries.Header());

        for (int i = 0; i < series.Count; i++)
        {
            sb.AppendLine(series[i].Row("F4", RqSeries.Span.FullDriveWindow,
                RqSeries.Denominator.AllSteps));
            sb.AppendLine(series[i].Row("F4", RqSeries.Span.FullDriveWindow,
                RqSeries.Denominator.TouchingOnly));
            sb.AppendLine(series[i].Row("F4", RqSeries.Span.ContactDriveWindow,
                RqSeries.Denominator.AllSteps));
            sb.AppendLine(series[i].Row("F4", RqSeries.Span.ContactDriveWindow,
                RqSeries.Denominator.TouchingOnly));
        }

        return sb.ToString();
    }

    /// <summary>
    ///     The per-step file: every sample of every series, at the step it was taken, from the SAME
    ///     retained samples the statistics above are computed from.
    /// </summary>
    /// <remarks>
    ///     Written by this recorder rather than by a probe of its own, deliberately. A separate component
    ///     re-reading the contact recorder and the patch probe would sample at its own point in the
    ///     physics step and could disagree with the aggregates carrying the same name. Here the two
    ///     cannot disagree, because there is one sampling and this is a dump of it.
    ///     <para>
    ///         A blank cell means the series took NO sample on that step, which is a different fact from
    ///         a zero: the patch family contributes nothing on a step that delivered no contact points,
    ///         and folding that into a zero would drag its mean toward zero in proportion to time apart.
    ///     </para>
    /// </remarks>
    private string BuildStepRows()
    {
        BuildSeries();

        int maxStep = -1;
        for (int i = 0; i < series.Count; i++)
        for (int k = 0; k < series[i].Count; k++)
        {
            float v;
            int st;
            bool t;
            bool inv;
            series[i].SampleAt(k, out v, out st, out t, out inv);
            if (st > maxStep) maxStep = st;
        }

        var sb = new StringBuilder(65536);
        sb.AppendLine("# Per-step samples of every DRIVE-window series, at the step each was taken.");
        sb.AppendLine("# A BLANK cell is no sample on that step, which is not the same as a zero.");
        sb.AppendLine("# touching,1,the pair delivered contact on this step,0,it did not");
        sb.AppendLine("# inverted,1,the contact side was inverted on this step,0,it was not");
        sb.AppendLine("# note,touching and inverted are the two filters the series file uses. With both " +
                      "present every one of its four span/denominator cells is reproducible from this " +
                      "file, which is the reason this file exists.");
        sb.AppendLine("# step is the IN-WINDOW step, zero at the Drive transition - the same basis as " +
                      "first_inversion_step and every argmax_step.");
        sb.AppendLine("# column names are series names. A trailing _n on s_normal_n and s_off_normal_n " +
                      "and s_added_tan_n is the UNIT newtons, never a count. Units are stated per " +
                      "series in the accompanying _series.csv.");

        sb.Append("step,touching,inverted");
        for (int i = 0; i < series.Count; i++) sb.Append(',').Append(series[i].Name);
        sb.AppendLine();

        if (maxStep < 0) return sb.ToString();

        int width = maxStep + 1;
        var have = new bool[series.Count][];
        var vals = new float[series.Count][];
        var touch = new bool[width];
        var invert = new bool[width];

        for (int i = 0; i < series.Count; i++)
        {
            have[i] = new bool[width];
            vals[i] = new float[width];
            for (int k = 0; k < series[i].Count; k++)
            {
                float v;
                int st;
                bool t;
                bool inv;
                series[i].SampleAt(k, out v, out st, out t, out inv);
                if (st < 0 || st >= width) continue;
                have[i][st] = true;
                vals[i][st] = v;
                if (t) touch[st] = true;
                if (inv) invert[st] = true;
            }
        }

        for (int st = 0; st < width; st++)
        {
            sb.Append(st.ToString(CultureInfo.InvariantCulture))
              .Append(',').Append(touch[st] ? "1" : "0")
              .Append(',').Append(invert[st] ? "1" : "0");
            for (int i = 0; i < series.Count; i++)
            {
                sb.Append(',');
                if (have[i][st]) sb.Append(vals[i][st].ToString("F6"));
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    // EmptyStatsRow() was removed with the series block. It padded the drive file's row when a segment
    // never accumulated one, and the drive file no longer carries series statistics. The case it existed
    // for is now answered by the series file: a refused run gets that file with its headers and no data
    // rows, which says "no window opened" without a row of zero-width statistics to be misread.

    // Latest station-frame recomputation, refreshed every step at THIS component's execution order so
    // it is taken at the same instant as the recorder's own figures. See ComputeInvariance.
    private RqStationContactRecorder.Record lastRecord;
    private bool haveLastRecord;

    private bool invHave;
    private float invRotA, invRotB, invRotGain, invTipA, invTipB, invTipGain, invWorst;

    private Reading previous;
    private bool havePrevious;
    private Reading lastTouching;
    private bool haveLastTouching;

    /// <summary>The runner driving this observation, set at <see cref="OnRunBegin" />.</summary>
    /// <remarks>
    ///     Every protocol fact in the sealed header — the dwell schedule, the arm step, the isolation
    ///     count, the settle allowance — is read from here at seal time rather than duplicated onto this
    ///     component. Two copies of the schedule are two things to keep in step, and the file would
    ///     eventually state one while the run used the other.
    /// </remarks>
    private RqStationRunner runner;

    private bool running, sealedAlready;
    private int step;
    private string endReason = "";

    private Segment open;
    private bool segmentOpen;

    private Gate gate = Gate.Pending;
    private string gateNote = "";

    /// <summary>True between the run beginning and sealing.</summary>
    public bool Running => running;

    /// <summary>True once this recorder has sealed a run.</summary>
    public bool HasSealed => sealedAlready;

    /// <summary>How the entry gate resolved. <see cref="Gate.Pending" /> until the Drive transition.</summary>
    public Gate EntryGate => gate;

    /// <summary>The station label, for a readout.</summary>
    public string StationId => stationId;

    /// <summary>The phase currently commanded, or -1 when no runner is driving one.</summary>
    public int PhaseState => runner != null ? runner.PhaseState : -1;

    // ----- the observer contract -------------------------------------------------------------------

    /// <summary>
    ///     Ready when both instruments can actually measure — which is not true until the swords are worn.
    /// </summary>
    /// <remarks>
    ///     Ragdoll Animator generates the sword proxies at runtime, and until it has, the probe cannot
    ///     resolve and the contact recorder has no body to follow. Arming before that produces a run
    ///     whose early phases were watched by instruments that did not yet exist, and nothing in the file
    ///     would say so.
    /// </remarks>
    public override bool ReadyToArm
    {
        get
        {
            if (recorder == null || contactSide == null) return false;
            if (contactSide.Refused) return false;
            if (!contactSide.Sample()) return false;

            return recorder.SideABody != null && recorder.SideBBody != null;
        }
    }

    public override string ReadinessReason
    {
        get
        {
            if (recorder == null) return "no RqStationContactRecorder assigned";
            if (contactSide == null) return "no RqContactSideProbe assigned";
            if (contactSide.Refused) return "the probe REFUSED: " + contactSide.RefusalReason;
            if (!contactSide.Resolved) return "the probe has not resolved — the swords are not worn yet";
            if (recorder.SideABody == null || recorder.SideBBody == null)
                return "the contact recorder has not bound both proxy bodies yet";

            return "ready";
        }
    }

    public override void OnRunBegin(RqStationRunner startedBy)
    {
        runner = startedBy;

        if (declaredPhysics != null) declaredPhysics.Verify();

        // The recorder must not latch its own zero on first touch. That zero lands during Hold or
        // Contact, before A moves, while the physical record aligns at A's forward onset. Suppressed here
        // rather than trusted to the serialized default, because changing a [SerializeField] default does
        // NOT change ten scene instances that were serialized under the old one.
        recorder.SuppressAutoLatch(
            "RqDriveWindowRecorder owns the zero: latched at the Contact->Drive transition, which is " +
            "where the physical record's window begins");

        segments.Clear();
        boundaries.Clear();
        previous = default(Reading);
        lastTouching = default(Reading);
        haveLastTouching = false;
        segmentOpen = false;
        gate = Gate.Pending;
        gateNote = "";
        endReason = "";
        step = 0;
        sealedAlready = false;
        running = true;

        // Per-run frame state. Reset explicitly rather than relying on field initialisers, which run once
        // at construction and would carry a previous run's step indices into this one.
        driveStartFrame = driveMidFrame = driveEndFrame = null;
        driveWindowZeroStep = -1;
        driveStartWindowStep = driveMidWindowStep = driveEndWindowStep = -1;
        driveMidNormalizedTime = -1f;
        driveStartCaptureMs = driveMidCaptureMs = driveEndCaptureMs = 0d;
        pendingCaptureSlot = 0;
        pendingFrameStem = "";
        framesWritten = false;

        if (captureDriveFrames)
        {
            StopAllCoroutines();
            StartCoroutine(CaptureAtEndOfFrame());
        }

        Debug.Log("[DriveWindow] " + Label() + ": observing " + startedBy.Label() + ". Setup segments " +
                  (recordSetupSegments ? "ON" : "OFF") + ". " + contactSide.DeclarationLine(), this);
    }

    /// <summary>
    ///     A phase was just commanded. Closes the open segment, gates at Drive, opens the next.
    /// </summary>
    /// <remarks>
    ///     The gate is evaluated HERE and not one step later because it asks about a single instant — the
    ///     Drive transition, before the zero is latched. A step later the blades have already been driven.
    /// </remarks>
    public override void OnPhase(int state, int atStep)
    {
        if (!running) return;

        step = atStep;

        // Close whatever segment was open BEFORE opening the next, so a segment can never span a phase
        // boundary. The setup segments are opened only when they are wanted; the Drive one always is.
        CloseSegment();

        if (state == 4)
        {
            EvaluateEntryGate();

            // A refusal does not open the window. The runner sees VetoRun and ends the run, and this
            // observer seals the refusal — which is a result, not a lost run.
            if (gate == Gate.RefusedInverted || gate == Gate.RefusedUnreadable) return;

            OpenSegment("Drive");

            // The window's opening state. Requested here, taken at end of frame; no disk write either way.
            driveWindowZeroStep = atStep;
            driveStartWindowStep = 0;
            pendingCaptureSlot = 1;
            return;
        }

        if (!recordSetupSegments) return;

        if (state == 1) OpenSegment("Snap");
        else if (state == 2) OpenSegment("Hold");
        else if (state == 3) OpenSegment("Contact");
    }

    public override void OnStep(int atStep)
    {
        if (!running) return;

        // ONLY the step index. The sampling happens in this component's own FixedUpdate.
        //
        // WHY, and this was a measured defect: an observer callback is invoked synchronously BY THE
        // RUNNER, so it executes in the RUNNER's execution slot at -200 - not at this component's
        // declared +200. At -200 the station recorder has not run for this step, so recorder.Current
        // holds what it computed at order 0 of the PREVIOUS step, while a transform read in the same
        // breath is the pose physics has since moved. Two instants, one call.
        //
        // Measured: the invariance check read 2.08 deg and 16.3 mm of "disagreement" over the Drive
        // window, on quantities that cannot depend on the frame at all.
        step = atStep;
    }

    public override void OnRunEnd(string reason)
    {
        endReason = reason;
        Seal();
    }

    /// <summary>A refused gate stops the run. The runner does not need to know what a gate is.</summary>
    public override bool VetoRun =>
        gate == Gate.RefusedInverted || gate == Gate.RefusedUnreadable;

    public override string VetoReason => gateNote;

    /// <summary>
    ///     All per-step sampling, at this component's own execution order.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>+200 is the whole point of doing it here.</b> The station recorder runs at 0 and PhysX
    ///         simulates after every <c>FixedUpdate</c> callback, so at +200 of step N the recorder has
    ///         published step N and no body has moved since. <c>recorder.Current</c>, the live proxy pose
    ///         and <see cref="RqContactSideProbe.Sample" /> are then all describing one instant.
    ///     </para>
    ///     <para>
    ///         <b>This is the same fix the contact-side probe already carries, for the same reason.</b>
    ///         That probe's agreement harness reported 110 phantom disagreements because it compared a
    ///         cached value against a reference computed later in the step; <c>Sample()</c> exists so a
    ///         caller can take the reading at ITS instant. Caching a comparison and reading it later
    ///         reintroduces exactly that defect, and the first version of this recorder did.
    ///     </para>
    /// </remarks>
    private void FixedUpdate()
    {
        if (!running || !segmentOpen) return;

        WatchContactSide();

        // Mid-window frame, taken the first step the SLOWER animator is half way through the drive clip.
        // Half of the clip, not half of a step count: the recorder states no window duration, and the
        // runner already closes the window on normalizedTime rather than on a number of steps, so this
        // uses the same basis. It therefore lands correctly whatever the authored clip length is.
        //
        // NOT frame-rate dependent. cam.Render() draws on demand inside this FixedUpdate, so the frame
        // is the physics state of THIS step - it does not wait for the next rendered frame. The one
        // caveat is that interpolated rigidbodies are drawn at their interpolated pose, so a captured
        // frame can differ slightly from what a viewer sees at the same instant.
        if (captureDriveFrames && driveMidWindowStep < 0 && driveWindowZeroStep >= 0 && runner != null)
        {
            float t = runner.DriveClipNormalizedTime;
            if (t >= 0.5f)
            {
                driveMidWindowStep = step - driveWindowZeroStep;
                driveMidNormalizedTime = t;
                pendingCaptureSlot = 2;
            }
        }

        // The record as of THIS step, so a segment closing at a phase boundary - which happens at the
        // runner's -200, before the recorder has run - seals the step it actually ended on rather than
        // the one before.
        lastRecord = recorder.Current;
        haveLastRecord = true;
    }

    private void OnDisable()
    {
        if (running && !sealedAlready)
        {
            Debug.LogWarning("[DriveWindow] " + Label() + ": disabled mid-run; sealing what exists so " +
                             "the run is not lost silently.", this);
            endReason = "the observer was disabled mid-run";
            Seal();
        }

        // Last resort for the frames. If Play is exited between the seal and the next end of frame, the
        // END frame was never taken - write the ones that exist rather than losing all three silently.
        if (captureDriveFrames && !framesWritten && !string.IsNullOrEmpty(pendingFrameStem))
        {
            Debug.LogWarning("[DriveWindow] " + Label() + ": disabled before the end-of-frame capture " +
                             "ran, so the END frame is missing. Writing the frames that exist.", this);
            FlushFrames();
        }

        running = false;
    }

    // ----- the gate --------------------------------------------------------------------------------

    /// <summary>
    ///     The pop-through pre-gate, taken at the Drive transition BEFORE the zero is latched.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <see cref="RqContactSideProbe.Sample" /> and not the cached value, because the question is
    ///         about ONE instant. Reading whatever the probe's own <c>FixedUpdate</c> last stored answers
    ///         at whatever point in the script execution order that happened to run, which is not this
    ///         caller's instant and is not ordered against whatever moved the blades.
    ///     </para>
    ///     <para>
    ///         The criterion is the probe's <c>Inverted</c> — BOTH sides departed the declared reference.
    ///         One side changing is the contact travelling around the blade, happens in valid runs, and
    ///         does not gate. Declared 2026-08-26. No threshold is set here.
    ///     </para>
    /// </remarks>
    private void EvaluateEntryGate()
    {
        bool read = contactSide.Sample() && contactSide.HasReading;

        if (!read)
        {
            gate = Gate.RefusedUnreadable;
            gateNote = contactSide.Refused
                ? "probe REFUSED: " + contactSide.RefusalReason
                : "probe produced no reading at the Drive transition (proxy not resolved?)";

            Debug.LogError("[DriveWindow] " + Label() + ": RUN REFUSED — the contact-side value could " +
                           "not be read at the Drive transition, and the declared rule is that a run " +
                           "evaluated beyond approach MUST read it. This is a REFUSAL, not a pass. " +
                           gateNote, this);
            return;
        }

        if (!contactSide.HasDeclaredReference)
        {
            gate = Gate.PassUngated;
            gateNote = "no declaredReference on the probe, so the inversion criterion cannot fire";

            Debug.LogWarning("[DriveWindow] " + Label() + ": recording UNGATED. The probe read " +
                             contactSide.LeadPair + " but carries no declared reference, so this run " +
                             "cannot be gated on pop-through and must not be pooled with gated runs. " +
                             contactSide.Line, this);
            return;
        }

        if (contactSide.Inverted)
        {
            gate = Gate.RefusedInverted;
            gateNote = "declared " + contactSide.DeclaredReference + ", read " + contactSide.LeadPair +
                       " — BOTH sides departed [edge " + contactSide.EdgePair +
                       " | face " + contactSide.FacePair + "]";

            Debug.LogError("[DriveWindow] " + Label() + ": RUN REFUSED at the Drive transition. " +
                           gateNote + ". The relationship has already failed, so a Drive segment " +
                           "recorded from here would read as a clean terminal bind-to-release history, " +
                           "indistinguishable from an observed release. Refusals are counted, not " +
                           "hidden. " + contactSide.Line, this);
            return;
        }

        gate = Gate.Pass;
        // Both pairs are named here, not only the declared term's. The note is the line a reader
        // trusts, and quoting one term made a station look face-engaged while the contact had moved
        // to the edge — the reading was correct for the term it named and wrong about the apparatus.
        gateNote = "declared " + contactSide.DeclaredReference + ", read " + contactSide.LeadPair +
                   " [edge " + contactSide.EdgePair + " | face " + contactSide.FacePair + "]" +
                   (contactSide.OneSideChanged ? " — ONE side changed, which does not gate" : "");

        Debug.Log("[DriveWindow] " + Label() + ": gate PASS. " + gateNote + ". " + contactSide.Line, this);
    }

    // ----- segments --------------------------------------------------------------------------------

    private void OpenSegment(string label)
    {
        var s = new Segment
        {
            Label = label,
            StartStep = step,
            FirstInversionStep = -1
        };

        bool read = contactSide.Sample() && contactSide.HasReading;
        s.EntryRead = read;

        if (read)
        {
            s.EntryLeadPair = contactSide.LeadPair;
            s.EntryEdgePair = contactSide.EdgePair;
            s.EntryFacePair = contactSide.FacePair;
            s.EntryInverted = contactSide.Inverted;
            s.EntryOneSideChanged = contactSide.OneSideChanged;
            s.EntryClosestMm = contactSide.ClosestApproachMm;
            s.EntryAcrossEdgeAMm = contactSide.AAcrossEdgeMetres * 1000f;
            s.EntryAcrossEdgeBMm = contactSide.BAcrossEdgeMetres * 1000f;
            s.EntryAlongBladeAMm = contactSide.AAlongBladeMetres * 1000f;
            s.EntryAlongBladeBMm = contactSide.BAlongBladeMetres * 1000f;
            s.EntryContactAWorld = contactSide.AContactPoint;
            s.EntryContactBWorld = contactSide.BContactPoint;
        }
        else
        {
            s.EntryLeadPair = "(unread)";
            s.EntryEdgePair = "(unread)";
            s.EntryFacePair = "(unread)";
        }

        open = s;
        segmentOpen = true;

        // Boundary detection restarts with the segment, and the baseline is deliberately NOT taken
        // here.
        //
        // MEASURED DEFECT 2026-08-26. This used to seed `previous` with a Take() at this point, which
        // runs in the RUNNER's execution slot at -200 where recorder.Current.Touching still holds the
        // previous step's value — while the first real sample arrives at +200 carrying this step's. The
        // mismatch manufactured a Release on step 0 of EVERY segment, followed by a ReEngage on step 1.
        // It was caught by cross-checking against the recorder's own ReleaseCount, which read 0
        // throughout, and it would otherwise have corrupted a primary observable.
        //
        // The first sample now establishes the baseline and emits no boundary.
        ResetSeries();
        haveLastRecord = false;
        invHave = false;

        havePrevious = false;
        lastTouching = default(Reading);
        haveLastTouching = false;

        // The zero is what every accumulated quantity is differenced against, so it is latched here and
        // nowhere else. Migration and slip path are differences and have no meaning without a declared
        // instant to difference against.
        recorder.LatchZero();
    }

    private void CloseSegment()
    {
        if (!segmentOpen) return;

        open.Steps = step - open.StartStep;

        // Recompute the blade rotation from the window's own first sampled step and compare. Independent of
        // the primary only in its ZERO: rot_*_deg differences against LatchZero()'s instant, this against the
        // first step the window sampled. A non-trivial delta means those two instants have separated, which
        // is a fact about the window rather than about the blade.
        if (open.RotCheckComputed)
        {
            open.RotACheckDeg = Quaternion.Angle(open.QFirstA, open.QLastA);
            open.RotBCheckDeg = Quaternion.Angle(open.QFirstB, open.QLastB);
            RqStationContactRecorder.Record rc = haveLastRecord ? lastRecord : recorder.Current;
            open.RotADeltaDeg = open.RotACheckDeg - rc.RotationADeg;
            open.RotBDeltaDeg = open.RotBCheckDeg - rc.RotationBDeg;
        }

        open.Record = haveLastRecord ? lastRecord : recorder.Current;
        // Built here, while the samples still exist: Reset() clears them for the next window.
        open.SeriesRows = BuildSeriesRows(open.FirstInversionStep);
        open.StepRows = BuildStepRows();

        // The invariance figures are the ones cached on the LAST step of this segment, not ones computed
        // now: CloseSegment can be reached from the runner's phase callback at execution order -200,
        // before the station recorder has updated for this step, and comparing across that boundary
        // measures one step of blade motion rather than the frame change. That is exactly what the first
        // version of this did - it reported 2.43 mm of "disagreement" at SNAP, scaling with blade speed
        // and vanishing at CONTACT, which is the signature of a time offset and not of frame maths.
        open.InvarianceComputed = invHave;
        open.StnRotADeg = invRotA;
        open.StnRotBDeg = invRotB;
        open.StnRotGainBA = invRotGain;
        open.StnTipAMm = invTipA;
        open.StnTipBMm = invTipB;
        open.StnTipGainBA = invTipGain;
        open.InvarianceWorstDelta = invWorst;

        bool read = contactSide.Sample() && contactSide.HasReading;
        open.ExitRead = read;
        open.ExitLeadPair = read ? contactSide.LeadPair : "(unread)";
        open.ExitEdgePair = read ? contactSide.EdgePair : "(unread)";
        open.ExitFacePair = read ? contactSide.FacePair : "(unread)";
        open.ExitInverted = read && contactSide.Inverted;

        segments.Add(open);
        segmentOpen = false;
    }

    /// <summary>
    ///     Watches the contact side every step of an open segment.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Counted and located, never acted on.
    ///     </para>
    ///     <para>
    ///         <b>RULED 2026-08-29 by the researcher, and this is no longer an open question.</b> A red
    ///         reading that appears AFTER the Drive transition is <b>VALID, and it is itself the
    ///         observable</b> — it is SRQ1 point 3 evidence, not a validity failure. So
    ///         <c>inverted_steps</c>, <c>first_inversion_step</c>, <c>one_side_changed_steps</c> and the
    ///         face-dot series are read as behaviour.
    ///     </para>
    ///     <para>
    ///         The <c>entry_gate</c> is untouched by this and still refuses at the Drive transition: a
    ///         relationship that has ALREADY failed before the window opens would record as a clean terminal
    ///         bind-to-release history and be indistinguishable from an observed release. Entry is a gate;
    ///         what happens after the window opens is data. The two were being conflated, which is what made
    ///         this look like a rule conflict at PILOT runs 5 and 10.
    ///     </para>
    ///     <para>
    ///         Read the located step against the series locators: <c>first_inversion_step</c> and every
    ///         <c>*_argmax_step</c> share one basis, zero at the segment's first step, so "did the force peak
    ///         precede or follow the inversion" is answerable within a run without a translation.
    ///     </para>
    /// </remarks>
    private void WatchContactSide()
    {
        if (!segmentOpen) return;

        Reading now = Take();
        AccumulateSeries(now);
        ComputeInvariance();

        if (!now.Read) open.UnreadableSteps++;
        else if (contactSide.Inverted)
        {
            open.InvertedSteps++;
            if (open.FirstInversionStep < 0) open.FirstInversionStep = step - open.StartStep;
        }
        else if (contactSide.OneSideChanged) { open.OneSideChangedSteps++; }

        // Watch BOTH pairs against their own entry value. inverted_steps counts only the declared
        // term, so on a station declared Af- / Bf- it is blind to the edge pair moving. These two
        // counters are what let an evolution of contact be told apart from a pass-through downstream.
        if (now.Read)
        {
            if (!string.IsNullOrEmpty(open.EntryEdgePair) && open.EntryEdgePair != "(unread)" &&
                contactSide.EdgePair != open.EntryEdgePair)
            {
                open.EdgePairChangedSteps++;
            }

            if (!string.IsNullOrEmpty(open.EntryFacePair) && open.EntryFacePair != "(unread)" &&
                contactSide.FacePair != open.EntryFacePair)
            {
                open.FacePairChangedSteps++;
            }
        }

        // Contact boundaries. Touching comes from RqStationContactRecorder, which is the one place
        // PhysX's own contact report for this pair is consumed — this driver never counts contacts
        // itself, because two counters would drift.
        // The first sample of a segment only establishes the baseline. A boundary needs two samples
        // taken the same way, and there is no earlier one taken the same way.
        if (!havePrevious) havePrevious = true;
        else if (previous.PairPresent && !now.PairPresent) Boundary("Release", now);
        else if (!previous.PairPresent && now.PairPresent) Boundary("ReEngage", now);

        if (now.PairPresent)
        {
            lastTouching = now;
            haveLastTouching = true;
        }

        previous = now;
    }

    /// <summary>This step's reading, plus whether the pair is touching on this step.</summary>
    private Reading Take()
    {
        var r = new Reading { PairPresent = recorder.Current.PairPresent };

        if (!contactSide.Sample() || !contactSide.HasReading)
        {
            r.LeadPair = "(unread)";
            return r;
        }

        r.Read = true;
        r.LeadPair = contactSide.LeadPair;
        r.AlongAMm = contactSide.AAlongBladeMetres * 1000f;
        r.AlongBMm = contactSide.BAlongBladeMetres * 1000f;
        r.AcrossAMm = contactSide.AAcrossEdgeMetres * 1000f;
        r.AcrossBMm = contactSide.BAcrossEdgeMetres * 1000f;
        r.ClosestMm = contactSide.ClosestApproachMm;
        r.ContactAWorld = contactSide.AContactPoint;
        r.ContactBWorld = contactSide.BContactPoint;
        return r;
    }

    /// <summary>
    ///     Records a contact loss or re-engagement, and where on each blade the contact sat.
    /// </summary>
    /// <remarks>
    ///     The discriminating instant is the LAST step the contact existed, not the step it was noticed
    ///     gone: by then the blades have already separated and the reading no longer describes an
    ///     engagement. Both are written, so neither has to be inferred.
    /// </remarks>
    private void Boundary(string kind, Reading now)
    {
        var e = new BoundaryEvent
        {
            Segment = open.Label,
            Kind = kind,
            StepInSegment = step - open.StartStep,

            AfterRead = now.Read,
            AfterLeadPair = now.LeadPair,
            AfterInverted = now.Read && contactSide.Inverted,
            AfterClosestMm = now.ClosestMm,
            AfterAlongAMm = now.AlongAMm,
            AfterAlongBMm = now.AlongBMm,

            TipAlongAMm = contactSide.ATipAlongBladeMetres * 1000f,
            TipAlongBMm = contactSide.BTipAlongBladeMetres * 1000f,
        };

        // On a Release the previous step was touching, so lastTouching IS that step. On a ReEngage there
        // may be no earlier touch at all, and the fields are left unread rather than filled with the
        // nearest thing to hand.
        Reading source = kind == "Release" ? previous : (haveLastTouching ? lastTouching : default(Reading));

        e.LastTouchRead = source.Read;
        e.LastTouchLeadPair = string.IsNullOrEmpty(source.LeadPair) ? "(none)" : source.LeadPair;
        e.LastTouchAlongAMm = source.AlongAMm;
        e.LastTouchAlongBMm = source.AlongBMm;
        e.LastTouchAcrossAMm = source.AcrossAMm;
        e.LastTouchAcrossBMm = source.AcrossBMm;
        e.LastTouchClosestMm = source.ClosestMm;
        e.LastTouchContactAWorld = source.ContactAWorld;
        e.LastTouchContactBWorld = source.ContactBWorld;

        boundaries.Add(e);

        if (kind == "Release") open.ReleaseEvents++;
        else open.ReEngageEvents++;
    }

    // ----- seal -----------------------------------------------------------------------------------

    [ContextMenu("Seal")]
    /// <summary>
    ///     The station's own ViewCam, resolved from its <c>DriveTrailRecorder</c> unless one is wired
    ///     explicitly. This is the same camera the PhysicalReference channel projects through, so a
    ///     captured frame and a <c>plane_*</c> coordinate are the same viewpoint.
    /// </summary>
    private Camera ResolveDriveFrameCamera()
    {
        if (driveFrameCamera != null) return driveFrameCamera;

        Transform root = transform;
        while (root.parent != null) root = root.parent;

        var trail = root.GetComponentInChildren<DriveTrailRecorder>(true);
        if (trail == null || trail.viewCam == null) return null;

        return trail.viewCam.GetComponent<Camera>();
    }

    /// <summary>
    ///     Renders the ViewCam into an off-screen target and reads it back. Returns null rather than
    ///     throwing: a missing camera is a reason to have no picture, never a reason to lose a run.
    /// </summary>
    private Texture2D CaptureDriveFrame(out double elapsedMs)
    {
        elapsedMs = 0d;
        if (!captureDriveFrames) return null;

        Camera cam = ResolveDriveFrameCamera();
        if (cam == null)
        {
            Debug.LogWarning("[DriveWindow] " + Label() + ": no ViewCam resolved, so no frame was " +
                             "captured. The run is unaffected.", this);
            return null;
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();

        int h = Mathf.Max(64, driveFrameHeight);
        int w = Mathf.Max(64, Mathf.RoundToInt(h * (cam.aspect <= 0f ? 16f / 9f : cam.aspect)));

        RenderTexture rt = RenderTexture.GetTemporary(w, h, 24);
        RenderTexture previousActive = RenderTexture.active;
        RenderTexture previousTarget = cam.targetTexture;
        bool wasEnabled = cam.enabled;

        Texture2D shot = null;
        try
        {
            cam.targetTexture = rt;
            cam.Render();

            RenderTexture.active = rt;
            shot = new Texture2D(w, h, TextureFormat.RGB24, false);
            shot.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            shot.Apply(false);
        }
        finally
        {
            // Restored in a finally so a throw mid-capture cannot leave a station camera rendering into
            // a released target for the rest of the session.
            cam.targetTexture = previousTarget;
            cam.enabled = wasEnabled;
            RenderTexture.active = previousActive;
            RenderTexture.ReleaseTemporary(rt);
        }

        clock.Stop();
        elapsedMs = clock.Elapsed.TotalMilliseconds;
        return shot;
    }

    /// <summary>Encodes and writes one held frame. False when there was nothing to write.</summary>
    private bool WriteFrame(Texture2D frame, string path)
    {
        if (frame == null) return false;
        File.WriteAllBytes(path, frame.EncodeToPNG());
        return true;
    }

    /// <summary>
    ///     Takes requested frames at end of frame, and writes all three once the run has sealed.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         WHY END OF FRAME, and this was a measured defect. An earlier version rendered inside
    ///         FixedUpdate. The mid frame of the verification run then showed both dummies flung wide and
    ///         much closer to the camera, while that run's own step trace showed a smooth monotonic
    ///         closure with contact on all 234 steps and zero dropouts — the picture contradicted its own
    ///         data. The video plane behind them was pixel-identical across all three frames, so the
    ///         camera had not moved.
    ///     </para>
    ///     <para>
    ///         A render issued from FixedUpdate happens before the Animator and Ragdoll Animator's late
    ///         correction have run for that visual frame, so it draws an uncorrected pose. This is the
    ///         same mechanism as the worn-sword artefact, where interpolation displaced the authored body
    ///         by 100-490 mm every frame and the late correction masked it.
    ///     </para>
    ///     <para>
    ///         CONSEQUENCE FOR THE LABEL: the visual pose only exists at render time, so a frame showing
    ///         what a viewer sees IS frame-timing dependent. Each file is named with the in-window step
    ///         that REQUESTED it, and the image is the first render after that step. At a render rate
    ///         below the 90 Hz physics rate, more than one step may have elapsed by then.
    ///     </para>
    /// </remarks>
    private System.Collections.IEnumerator CaptureAtEndOfFrame()
    {
        var wait = new WaitForEndOfFrame();

        while (true)
        {
            yield return wait;

            if (pendingCaptureSlot != 0)
            {
                int slot = pendingCaptureSlot;
                pendingCaptureSlot = 0;

                if (slot == 1) driveStartFrame = CaptureDriveFrame(out driveStartCaptureMs);
                else if (slot == 2) driveMidFrame = CaptureDriveFrame(out driveMidCaptureMs);
                else if (slot == 3) driveEndFrame = CaptureDriveFrame(out driveEndCaptureMs);

                if (slot == 3) FlushFrames();
            }

            if (framesWritten) yield break;
        }
    }

    /// <summary>Writes whatever frames exist. Safe to call twice; the second call does nothing.</summary>
    private void FlushFrames()
    {
        if (framesWritten || string.IsNullOrEmpty(pendingFrameStem)) return;
        framesWritten = true;

        try
        {
            int n = 0;
            if (WriteFrame(driveStartFrame, pendingFrameStem + "_frame_drive_start_step" +
                           driveStartWindowStep + ".png")) n++;
            if (WriteFrame(driveMidFrame, pendingFrameStem + "_frame_drive_mid_step" +
                           driveMidWindowStep + ".png")) n++;
            if (WriteFrame(driveEndFrame, pendingFrameStem + "_frame_drive_end_step" +
                           driveEndWindowStep + ".png")) n++;

            Debug.Log("[DriveWindow] " + Label() + ": frames " + n + "/3 — start step " +
                      driveStartWindowStep + " (" + driveStartCaptureMs.ToString("F2") + " ms), mid step " +
                      driveMidWindowStep + " at clip normalizedTime " +
                      driveMidNormalizedTime.ToString("F4") + " (" + driveMidCaptureMs.ToString("F2") +
                      " ms), end step " + driveEndWindowStep + " (" + driveEndCaptureMs.ToString("F2") +
                      " ms). Steps are IN-WINDOW indices, 0 at the Drive transition — the same basis as " +
                      "first_inversion_step and the boundaries file's step_in_segment. Each image is the " +
                      "FIRST RENDER AFTER that step, so it is frame-timing dependent.", this);

            if (driveMidFrame == null)
                Debug.LogWarning("[DriveWindow] " + Label() + ": NO MID FRAME — the drive clip never " +
                                 "reported normalizedTime >= 0.5 on both animators while the window was " +
                                 "open. The run is unaffected.", this);
        }
        catch (System.Exception fx)
        {
            Debug.LogWarning("[DriveWindow] " + Label() + ": frame write failed, the RUN IS UNAFFECTED — " +
                             fx.GetType().Name + ": " + fx.Message, this);
        }
        finally
        {
            if (driveStartFrame != null) DestroyImmediate(driveStartFrame);
            if (driveMidFrame != null) DestroyImmediate(driveMidFrame);
            if (driveEndFrame != null) DestroyImmediate(driveEndFrame);
            driveStartFrame = driveMidFrame = driveEndFrame = null;
        }
    }


    public void Seal()
    {
        if (sealedAlready) return;

        running = false;
        sealedAlready = true;

        CloseSegment();

        if (string.IsNullOrEmpty(outputDirectory))
        {
            Debug.LogError("[DriveWindow] please set directory on the RqDriveWindowRecorder component to record. "
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

            // The dwell schedule and the settle allowance go in the FILENAME, not only the header.
            // Changing a dwell moves every phase boundary and every step number in the file, and that is
            // invisible in a directory listing showing only a station and a timestamp.
            int settle = runner != null ? runner.SettleStepsAfterClip : 0;
            string schedule = runner == null
                ? "dNORUNNER"
                : "d" + runner.DwellPauseSteps + "-" + runner.DwellSnapSteps + "-" +
                  runner.DwellHoldSteps + "-" + runner.DwellContactSteps +
                              (settle > 0 ? "+s" + settle : "");

            // ---- FAILURE FLAGGED IN THE FILE NAME, so a failed pass cannot be picked out of a
            // directory listing and pooled with the valid set by accident. Every cause the recorder can
            // determine at seal time gets its own prefix; the ones only the analysis can determine
            // (a mid-window inversion, a malformed file) are flagged there instead.
            //
            // FAILED_ is the common marker. Anything starting FAILED_ is not a result about behaviour.
            string prefix;
            if (gate == Gate.RefusedInverted) prefix = "FAILED_refused-inverted_";
            else if (gate == Gate.RefusedUnreadable) prefix = "FAILED_refused-unreadable_";
            else if (gate == Gate.PassUngated) prefix = "UNGATED_";
            else if (DriveWindowEmpty()) prefix = "FAILED_no-drive-window_";
            else prefix = "";

            // One folder per SET. An undeclared set is named as such rather than silently folded in
            // with whatever else is in the root.
            string set = string.IsNullOrEmpty(setName) ? "set_UNDECLARED" : setName;
            if (string.IsNullOrEmpty(setName))
                Debug.LogWarning("[DriveWindow] " + Label() + ": setName is EMPTY, so this run is being " +
                                 "written to '" + set + "'. Declare the set before collecting data that " +
                                 "has to be pooled.", this);

            string folder = Path.Combine(outputDirectory, set);
            Directory.CreateDirectory(folder);

            string stem = Path.Combine(
                folder,
                prefix + "drive_" + (string.IsNullOrEmpty(stationId) ? "station" : stationId) + "_" +
                (string.IsNullOrEmpty(treatment) ? "treatment" : treatment) + "_" + stamp + "_" +
                schedule);

            File.WriteAllText(stem + ".csv", Build());

            // Always written, including with zero events: whether contact was ever lost is exactly the
            // question this file exists to answer, and an empty one says it was not. A run whose
            // boundary file is missing is indistinguishable from a run that never had one.
            File.WriteAllText(stem + "_boundaries.csv", BuildBoundaries());

            // The series statistics and the per-step samples they were computed from. Both come from the
            // segment, captured at close while the samples still existed.
            //
            // Written even when no segment opened - a refused run gets the headers and nothing else - for
            // the same reason the boundaries file always is: an absent file and a run that produced no
            // window must not look the same on disk.
            File.WriteAllText(stem + "_series.csv",
                segments.Count > 0 && !string.IsNullOrEmpty(segments[0].SeriesRows)
                    ? segments[0].SeriesRows
                    : BuildSeriesRows(-1));

            File.WriteAllText(stem + "_steps.csv",
                segments.Count > 0 && !string.IsNullOrEmpty(segments[0].StepRows)
                    ? segments[0].StepRows
                    : BuildStepRows());

            // Frames last. The window is over, so a PNG encode and a file write here stall nothing that
            // is still being simulated. A capture failure must never cost the run, so it is caught
            // separately and reported rather than allowed to reach the outer handler.
            // Frames are NOT written here. The end frame has not been taken yet: it is requested now and
            // taken at the next end of frame, along with writing all three. See CaptureAtEndOfFrame.
            if (captureDriveFrames)
            {
                driveEndWindowStep = driveWindowZeroStep >= 0 ? step - driveWindowZeroStep : step;
                pendingFrameStem = stem;
                pendingCaptureSlot = 3;
            }

            Debug.Log("[DriveWindow] " + Label() + ": SEALED gate=" + gate + ", " + segments.Count +
                      " segment(s) and " + boundaries.Count + " boundary event(s) to " + stem +
                      "{,_boundaries}.csv", this);
        }
        catch (System.Exception ex)
        {
            // The stack trace is included deliberately. A seal that fails silently and leaves an EMPTY
            // OUTPUT FOLDER is indistinguishable from a run that was never armed, and that cost a pilot
            // attempt: the folder existed, held zero files, and the only clue was one console line with
            // no location in it. If a seal ever fails again the file and line must be in the log.
            Debug.LogError("[DriveWindow] seal failed: " + ex.GetType().Name + ": " + ex.Message +
                           "\n" + ex.StackTrace, this);
        }
    }

    private const string RowHeader =
        // identity + window
        "segment,start_step,steps,seconds," +
        // validity — representation-independent, and the reason this file is comparable across arms
        "entry_gate,entry_gate_note,entry_read,entry_lead_pair,entry_inverted,entry_one_side_changed," +
        "entry_closest_mm,entry_across_edge_a_mm,entry_across_edge_b_mm," +
        "entry_along_blade_a_mm,entry_along_blade_b_mm," +
        // WORLD, then the station-relative secondary. Both, always — see the header's worldFrame note.
        "entry_contact_a_world_x,entry_contact_a_world_y,entry_contact_a_world_z," +
        "entry_contact_b_world_x,entry_contact_b_world_y,entry_contact_b_world_z," +
        "entry_contact_a_stn_x,entry_contact_a_stn_y,entry_contact_a_stn_z," +
        "entry_contact_b_stn_x,entry_contact_b_stn_y,entry_contact_b_stn_z," +
        "exit_read,exit_lead_pair,exit_inverted," +
        // Both pairs at both boundaries, plus how many steps each moved. The declared term's own
        // columns above are unchanged; these sit beside them and are never a substitute.
        "entry_edge_pair,entry_face_pair,exit_edge_pair,exit_face_pair," +
        "edge_pair_changed_steps,face_pair_changed_steps," +
        "inverted_steps,one_side_changed_steps,unreadable_steps,first_inversion_step," +
        "release_events,reengage_events," +
        // PRIMARY — CH4_MeasurementContract.md §5.1
        "pair_steps,overlap_steps,dropout_steps,longest_pair_run_steps,longest_pair_run_s,releases," +
        "first_pair_s,first_overlap_s,run_min_separation_mm,last_release_s," +
        "mig_a_mm,mig_b_mm,peak_mig_mm,slip_path_mm," +
        "rot_a_deg,rot_b_deg,rot_gain_ba,tip_a_mm,tip_b_mm,tip_gain_ba," +
        "rot_a_deg_check,rot_b_deg_check,rot_a_deg_delta,rot_b_deg_delta," +
        // SECONDARY — §5.2. Diagnostics. These explain the primary block, never substitute for it.
        "run_deepest_pen_mm,normal_reversed,peak_normal_swing_deg," +
        // mean_normal_n, mean_off_normal_impulse_n and mean_added_tangential_n were REMOVED here.
        // They were means over TOUCHING steps only, sitting beside s_*_mean which averaged every
        // step, with nothing in either name saying which - and a trailing _n that reads as the
        // sample count it means on every neighbouring column while actually meaning NEWTONS. The
        // series file states the same three numbers unambiguously as
        // <series>/full_drive_window/touching_only.
        "deepest_shape_a,deepest_shape_b," +
        // The station-frame secondaries for the quantities EXPECTED to be invariant to the station
        // offset. Printed so the expectation is demonstrated per run instead of asserted in a comment:
        // if any of these ever disagrees with its original, the file says so.
        "tip_a_stn_mm,tip_b_stn_mm,tip_gain_ba_stn," +
        "rot_a_stn_deg,rot_b_stn_deg,rot_gain_ba_stn," +
        "entry_closest_stn_mm,entry_along_blade_a_stn_mm,entry_along_blade_b_stn_mm," +
        "entry_across_edge_a_stn_mm,entry_across_edge_b_stn_mm," +
        "invariance_max_abs_delta";

    /// <summary>
    ///     The per-step statistics block appended to every segment row.
    /// </summary>
    /// <remarks>
    ///     Ten statistics per observable — n, first, last, min, max, mean, median, sd, range, sum — all
    ///     computed by <see cref="RqSeries" /> from the samples taken during the window, before the seal.
    ///     Nothing is derived from the file afterwards, and no statistic is dropped because it is
    ///     expected to duplicate another: on a monotonic series first equals min and last equals max, and
    ///     THAT is the finding.
    /// </remarks>
    private string FullRowHeader => RowHeader;

    private string Build()
    {
        var sb = new StringBuilder(8192);
        AppendPreflight(sb);
        sb.AppendLine(FullRowHeader);

        for (int i = 0; i < segments.Count; i++)
        {
            Segment s = segments[i];
            RqStationContactRecorder.Record c = s.Record;

            sb.AppendLine(
                s.Label + "," + s.StartStep + "," + s.Steps + "," +
                (s.Steps * Time.fixedDeltaTime).ToString("F4") + "," +

                gate + ",\"" + gateNote + "\"," +
                B(s.EntryRead) + "," + s.EntryLeadPair + "," + B(s.EntryInverted) + "," +
                B(s.EntryOneSideChanged) + "," +
                F(s.EntryClosestMm) + "," + F(s.EntryAcrossEdgeAMm) + "," + F(s.EntryAcrossEdgeBMm) + "," +
                F(s.EntryAlongBladeAMm) + "," + F(s.EntryAlongBladeBMm) + "," +
                XYZ(s.EntryContactAWorld) + "," + XYZ(s.EntryContactBWorld) + "," +
                XYZ(ToStation(s.EntryContactAWorld)) + "," + XYZ(ToStation(s.EntryContactBWorld)) + "," +
                B(s.ExitRead) + "," + s.ExitLeadPair + "," + B(s.ExitInverted) + "," +
                s.EntryEdgePair + "," + s.EntryFacePair + "," +
                s.ExitEdgePair + "," + s.ExitFacePair + "," +
                s.EdgePairChangedSteps + "," + s.FacePairChangedSteps + "," +
                s.InvertedSteps + "," + s.OneSideChangedSteps + "," + s.UnreadableSteps + "," +
                s.FirstInversionStep + "," +
                s.ReleaseEvents + "," + s.ReEngageEvents + "," +

                c.PairSteps + "," + c.OverlapSteps + "," + c.DropoutSteps + "," + c.LongestRunSteps + "," +
                c.LongestRunSeconds.ToString("F4") + "," + c.ReleaseCount + "," +
                c.FirstPairSeconds.ToString("F4") + "," + c.FirstOverlapSeconds.ToString("F4") + "," +
                F((c.MinSeparation == float.MaxValue ? 0f : c.MinSeparation) * 1000f) + "," +
                c.LastReleaseSeconds.ToString("F4") + "," +

                F(c.MigrationAlongA * 1000f) + "," + F(c.MigrationAlongB * 1000f) + "," +
                F(c.PeakMigration * 1000f) + "," + F(c.TangentialSlipPath * 1000f) + "," +

                c.RotationADeg.ToString("F4") + "," + c.RotationBDeg.ToString("F4") + "," +
                c.RotationGainBA.ToString("F5") + "," +
                F(c.TipTravelA * 1000f) + "," + F(c.TipTravelB * 1000f) + "," +
                c.TipGainBA.ToString("F5") + "," +

                (s.RotCheckComputed ? s.RotACheckDeg.ToString("F4") : "") + "," +
                (s.RotCheckComputed ? s.RotBCheckDeg.ToString("F4") : "") + "," +
                (s.RotCheckComputed ? s.RotADeltaDeg.ToString("F4") : "") + "," +
                (s.RotCheckComputed ? s.RotBDeltaDeg.ToString("F4") : "") + "," +

                F(c.DeepestPenetration * 1000f) + "," + B(c.NormalReversed) + "," +
                c.PeakNormalSwing.ToString("F2") + "," +
                (string.IsNullOrEmpty(c.DeepestShapeA) ? "(none)" : c.DeepestShapeA) + "," +
                (string.IsNullOrEmpty(c.DeepestShapeB) ? "(none)" : c.DeepestShapeB) + "," +
                InvarianceColumns(s, c));
        }

        if (segments.Count == 0)
            sb.AppendLine("# NO SEGMENT — the run was refused before the Drive window opened. The " +
                          "refusal IS the result; see the gate lines above.");

        return sb.ToString();
    }

    /// <summary>
    ///     The sealed apparatus metadata. Every field is queried from the LIVE object at seal time.
    /// </summary>
    /// <remarks>
    ///     A condition read off the inspector instead of off the sealed file has already mislabelled
    ///     three runs on this project, because editor runtime state survives a Play session.
    /// </remarks>
    private void AppendPreflight(StringBuilder h)
    {
        // HEADER FORMAT, and it is uniform on purpose so the analysis can parse it without guessing:
        //
        //     # <section>,<key>,<value>[,<key>,<value>...][,note,<free text, always LAST>]
        //
        // The section is always the first token and is never itself a key/value pair, so a reader can
        // split on commas and take strict pairs from index 1. Free prose goes after a key literally
        // named note or NOTE and is always the last thing on the line, because prose contains commas
        // and a reader must be able to stop before it. Nothing above that point contains a comma.
        h.AppendLine("# Chapter 5 comparison — DRIVE window. One file per run per station.");
        h.AppendLine("# instrument,name,RqDriveWindowRecorder,plan,PLAN_DriveWindowRecording.md," +
                     "contract,CH4_MeasurementContract.md,headerFormat," +
                     "section then strict key/value pairs; a note key is always last");
        // The station is identified by its Station_* name and nothing else. The thesis reporting names
        // are on the next line; they label the row in a chapter table, they do not identify the object.
        h.AppendLine("# identity,station," + stationId +
                     ",note,the Station_* name is THE object identifier. treatment below is a LABEL and " +
                     "this recorder reads no behaviour from it, which is what makes the three arms " +
                     "comparable through it");
        h.AppendLine("# thesis,presentation," +
                     (string.IsNullOrEmpty(presentationName) ? "NOT DECLARED" : presentationName) +
                     ",arm," + (string.IsNullOrEmpty(armName) ? "NOT DECLARED" : armName) +
                     ",sourcePass," + (string.IsNullOrEmpty(sourcePass) ? "NOT DECLARED" : sourcePass) +
                     ",note,the physical-reference presentation and the contact representation as the " +
                     "thesis names them, and the pass this station was reconstructed from. A virtual run " +
                     "is comparable to the physical record only through that pass");
        h.AppendLine("# thesis,selectionBasis," +
                     (string.IsNullOrEmpty(selectionBasis) ? "NOT DECLARED" : selectionBasis) +
                     ",note,why that pass was selected. Closest-to-a-central-measure and " +
                     "strongest-observed-reference are different kinds of selection and license " +
                     "different claims, so the basis travels with the data rather than living only in a " +
                     "table");
        h.AppendLine("# meta,sealedAt," + System.DateTime.Now.ToString("o") +
                     ",set," + (string.IsNullOrEmpty(setName) ? "UNDECLARED" : setName) +
                     ",note,runs are written one folder per set. A file whose name begins FAILED_ is " +
                     "a failed pass and is not a result about behaviour");

        // ---- THE TREATMENT FLAG. CH4_MeasurementContract.md §5.3 named its absence as the gap that had
        // to close before Chapter 5 runs: without it a sealed file cannot distinguish the OurSolution
        // treatment from ordinary PhysX contact plus an observing classifier.
        //
        // NO LAYER PRESENT and a layer present-and-false are DIFFERENT FACTS and are written differently.
        // A PhysX or QuickDirty station has no layer at all; an OurSolution station with
        // applyTangentialBehaviour false is the ABLATION arm. Collapsing the two would make the ablation
        // arm indistinguishable from a control.
        // BRACES ARE LOAD-BEARING HERE. This was a braceless if/else and a second statement inserted
        // between the first branch and the else silently became its OWN if/else: the ApplyTangentialBehaviour
        // read lost its null guard and threw on every no-layer station, and the NO LAYER PRESENT line
        // re-bound to the wrong condition. It compiled. Do not remove the braces.
        if (metadataSolver != null)
        {
            h.AppendLine("# treatmentFlag,applyTangentialBehaviour," +
                         (metadataSolver.ApplyTangentialBehaviour ? "true" : "false") +
                         ",layer," + metadataSolver.name +
                         ",classificationInterval," + metadataSolver.ClassificationInterval +
                         ",resolvePolicyFromLatchedScenario," +
                         (metadataSolver.ResolvePolicyFromLatchedScenario ? "true" : "false") +
                         ",relatchOnReEngagement," +
                         (metadataSolver.RelatchOnReEngagement ? "true" : "false") +
                         ",note,applyTangentialBehaviour is THE TREATMENT FLAG. false means this run is " +
                         "ordinary PhysX contact with the classifier observing only — the ABLATION arm — " +
                         "and it is NOT the OurSolution treatment however the station is named");

            if (!metadataSolver.ApplyTangentialBehaviour)
                h.AppendLine("# treatmentFlag,NOTE,offIsNotAControl,an augmented station with the layer " +
                             "PRESENT and switched OFF is an ABLATION OFF ARM. It is NOT a Standard Unity " +
                             "control and must not be reported as one, pooled with the PhysX row, or used " +
                             "as the baseline a treatment is compared against. The Standard Unity control " +
                             "is a station with NO LAYER PRESENT. The two differ in what is attached even " +
                             "when the flag is false, and only a no-layer station evidences ordinary " +
                             "Unity/PhysX contact.");
        }
        else
        {
            h.AppendLine("# treatmentFlag,applyTangentialBehaviour,NO LAYER PRESENT" +
                         ",note,no RqBladeTangentialSolver is wired to this recorder, which is the " +
                         "correct reading for a PhysX or QuickDirty station. This is NOT the same fact " +
                         "as a layer present and switched off; do not pool the two");
        }

        h.AppendLine("# window,primary,Drive,zero,Contact->Drive transition,length,THE DRIVE CLIP" +
                     ",settleStepsAfterClip," + (runner != null ? runner.SettleStepsAfterClip : 0) +
                     ",note,the window length is the clip and is deliberately not declared as a number " +
                     "here. The achieved length is the steps and seconds columns on the Drive row");
        h.AppendLine("# window,setupSegmentsRecorded," + (recordSetupSegments ? "true" : "false") +
                     ",note,Snap/Hold/Contact are SETUP CHARACTERISATION only. They answer whether the " +
                     "apparatus reached a valid starting relationship, never whether the representation " +
                     "reproduced the behaviour. Never pool them with the Drive row");

        h.AppendLine("# gate,entry," + gate + ",criterion,RqContactSideProbe.Inverted at the Drive " +
                     "transition — BOTH sides departed the declared reference,note,\"" + gateNote + "\"");
        h.AppendLine("# gate,source,RqContactSideProbe,representationIndependent,true,note,marker-derived " +
                     "— two quillon markers, the tip marker and the collider mesh long axis. No contact " +
                     "model in the path, which is why this and not the classifier gates the run (CH4 §4)");
        h.AppendLine("# gate,probeDeclaration," + contactSide.DeclarationLine().Replace(",", ";"));
        h.AppendLine("# gate,hullsProbedA," + contactSide.AProbedColliders +
                     ",hullsProbedB," + contactSide.BProbedColliders +
                     ",engagedA," + Blank(contactSide.AEngagedCollider) +
                     ",engagedB," + Blank(contactSide.BEngagedCollider) +
                     ",note,1 hull per side is a PhysX or OurSolution blade. 2 is QuickDirty, where " +
                     "WHICH hull carries the contact is that station's own semantic signal — it is the " +
                     "difference between an edge being involved and no edge being involved, which is the " +
                     "only distinction QuickDirty makes. Probing one hull there makes the other invisible");
        h.AppendLine("# gate,NOTE,a red reading beyond approach is an INVALID STATE and a red pass is " +
                     "not a valid pass. One side changing is the contact travelling around the blade, " +
                     "happens in valid runs, and does NOT gate. Mid-window inversions are COUNTED on " +
                     "each row and are not acted on by this instrument");
        h.AppendLine("# gate,NOTE,no sign-margin threshold is applied. The measured indeterminate band is " +
                     "0.0000-0.0006 mm from the centreline; entry_across_edge_a_mm and " +
                     "entry_across_edge_b_mm are on every row so a margin can be applied downstream once " +
                     "one is DECLARED. None is declared, so none is in force");

        h.AppendLine("# arm,armedAtFixedStep," + (runner != null ? runner.ArmedAtFixedStep : -1) +
                     ",alignMultiple," + (runner != null ? runner.ArmAlignMultiple : 0) +
                     ",requestedBy," + (runner != null ? runner.ArmRequestSource : "no runner") +
                     ",note,RULE — value-for-value verification between two runs requires an IDENTICAL " +
                     "armedAtFixedStep; runs armed on different steps are a cross-comparison of overall " +
                     "behaviour only");
        h.AppendLine("# walk,runner," + (runner != null ? runner.Label() : "NONE") +
                     ",dwellPause," + (runner != null ? runner.DwellPauseSteps : 0) +
                     ",dwellSnap," + (runner != null ? runner.DwellSnapSteps : 0) +
                     ",dwellHold," + (runner != null ? runner.DwellHoldSteps : 0) +
                     ",dwellContact," + (runner != null ? runner.DwellContactSteps : 0) +
                     ",note,dwells are SAMPLE SIZES, not settling criteria. They are read from the " +
                     "RUNNER at seal time, not duplicated onto this recorder, so the file cannot state " +
                     "one schedule while the run used another");
        h.AppendLine("# walk,endReason," + (string.IsNullOrEmpty(endReason) ? "not ended" : endReason));
        int frames = runner != null ? runner.FramesThisRun : 0;
        h.AppendLine("# walk,stepsThisRun," + step + ",framesThisRun," + frames +
                     ",framesPerFixedStep," +
                     (step > 0 ? ((float)frames / step).ToString("F3") : "n/a") +
                     ",note,Ragdoll Animator refreshes the muscle TARGET once per rendered FRAME while " +
                     "the drive runs every fixed step, so how many steps share a target depends on the " +
                     "render rate. This is why two Play sessions of the same station differ more than a " +
                     "treatment does, and why cross-session comparison is not the working standard");
        h.AppendLine("# isolation,otherStationRootsDeactivated," +
                     (runner != null ? runner.IsolatedRoots : 0) + ",runtimeOnly,true");

        // ---- WORLD POSITION, and it is here because it is the one thing the measurement contract's
        // controlled-inputs list does not hold constant.
        //
        // The nine-station matrix separates the three representations in world SPACE, so an arm-to-arm
        // comparison across stations moves the rig as well as changing the treatment. This project has
        // already measured a single-point knife-edge contact shifting ~13 % over an 8 m move in world
        // position — the same order as the effects under discussion — and localised that sensitivity to
        // the single-point knife-edge case, which is what the edge presentations are. So treatment is
        // confounded with position across stations, and RESULT_Ablation_TangentialOffOn.md §6 states
        // plainly that nothing licenses a quantitative between-arm claim on that basis.
        //
        // It is recorded on every run so the confound is AUDITABLE from the files rather than remembered,
        // and so that a downstream reader can see whether two pooled runs sat at the same place.
        // The station frame, so the station-relative secondary columns are reproducible from the file
        // alone rather than requiring the scene.
        h.AppendLine("# station,worldPosition," + V(transform.position) +
                     ",worldRotation," + Q(transform.rotation) +
                     ",lossyScale," + V(transform.lossyScale) +
                     ",note,NOT a controlled input. The three representations sit at DIFFERENT world " +
                     "positions, so an arm-to-arm comparison across stations changes position and " +
                     "treatment together. Recorded so the confound is auditable per run. Two runs pooled " +
                     "for a between-arm claim and NOT sharing this value are a cross-comparison of " +
                     "overall behaviour, not a treatment measurement");

        // ---- THE WORLD-FRAME POLICY. Stated in the file, because a reader has to know which columns
        // were adjusted and which were not, and has to be able to trust that nothing was replaced.
        h.AppendLine("# worldFrame,policy,world value AND station-relative secondary,originalRetained,true" +
                     ",secondarySuffix,_stn,originalSuffix,_world" +
                     ",note,every world-space quantity is written TWICE — the original world math and the " +
                     "station-relative adjustment beside it. The world math is never removed and never " +
                     "overwritten. A corrected number that has replaced its original cannot be audited, " +
                     "and if the adjustment is ever found wrong the original is the only way back. The " +
                     "adjustment is the station root's full inverse transform, not a subtraction, so a " +
                     "rotated or scaled root is handled; the root's position and rotation are above");
        h.AppendLine("# worldFrame,expectedInvariant," +
                     "mig_a_mm mig_b_mm peak_mig_mm entry_along_blade_* entry_across_edge_* " +
                     "last_touch_along_* last_touch_across_* tip_a_mm tip_b_mm slip_path_mm " +
                     "rot_a_deg rot_b_deg peak_normal_swing_deg deepest_pen_mm entry_closest_mm" +
                     ",secondaryAdded,true" +
                     ",note,these are EXPECTED to be invariant to the station offset — migration and the " +
                     "along/across-blade coordinates are computed in each blade's own frame; tip travel, " +
                     "slip path and the separation scalars are differences or magnitudes; the rotations " +
                     "and the normal swing are angles. They get a station-relative secondary anyway. An " +
                     "expected equality is PROVEN by printing both and letting them agree, not asserted " +
                     "by omitting one: a file that omitted it could never show that the invariance " +
                     "actually held for this run");
        h.AppendLine("# worldFrame,genuinelyAdjusted," +
                     "entry_contact_*_world -> entry_contact_*_stn; " +
                     "last_touch_contact_*_world -> last_touch_contact_*_stn" +
                     ",note,these are absolute world positions, so the secondary differs from the " +
                     "original by the station offset. Both are present, and the world original is never " +
                     "removed");

        // ---- apparatus, contract §5.3. Read from the live engine, not from a declaration asset.
        h.AppendLine("# apparatus,fixedDeltaTime," + F6(Time.fixedDeltaTime) + ",hz," +
                     F(1f / Time.fixedDeltaTime) +
                     ",gravityY," + F6(Physics.gravity.y) +
                     ",solverIterations," + Physics.defaultSolverIterations +
                     ",solverVelocityIterations," + Physics.defaultSolverVelocityIterations +
                     ",defaultContactOffset," + F6(Physics.defaultContactOffset) +
                     ",maxDepenetrationVelocity," + F6(Physics.defaultMaxDepenetrationVelocity) +
                     ",sleepThreshold," + F6(Physics.sleepThreshold) +
                     ",bounceThreshold," + F6(Physics.bounceThreshold) +
                     ",improvedPatchFriction," + Physics.improvedPatchFriction +
                     ",defaultMaxAngularSpeed," + F(Physics.defaultMaxAngularSpeed));
        h.AppendLine("# apparatus,NOTE,solver TYPE and friction TYPE have no runtime getter and cannot be " +
                     "read back here. They are declared on DeclaredPhysicsSettings, and a run is " +
                     "attributable to them only through that component's verification");
        h.AppendLine("# apparatus,declaredPhysicsSettings," +
                     (declaredPhysics != null
                         ? declaredPhysics.name + ",verifiedAtArm,true"
                         : "NOT WIRED,verifiedAtArm,false,note,nothing checked the live settings against " +
                           "the declared ones for this run"));

        AppendBody(h, "A", recorder.SideABody);
        AppendBody(h, "B", recorder.SideBBody);

        // FRICTION, read live at seal time. Every line, including the ones that are expected to
        // agree with each other: the failure modes here are silent, and a record that omitted a
        // shape because it "should not matter" is the record that could not show it did.
        // ACTUATOR, read live. Both the authored handler value and what the joints carry, because the
        // package may scale one into the other and only the spread across joints would reveal it.
        if (actuator != null)
            foreach (string line in actuator.DeclarationLines()) h.AppendLine("# " + line);
        else
            h.AppendLine("# actuator,probe,NOT WIRED,note,no RqActuatorProbe on this station, so this " +
                         "run does not state the spring, damping or muscle settings it ran under");

        if (friction != null)
            foreach (string line in friction.DeclarationLines()) h.AppendLine("# " + line);
        else
            h.AppendLine("# friction,probe,NOT WIRED,note,no RqFrictionProbe on this station, so this run does not state the friction it actually ran under");

        if (patch != null)
        {
            h.AppendLine("# patch,probe,RqContactPatchProbe,executionOrder,150,resolved," + patch.Resolved +
                         ",note,contact-patch EXTENT in each blade's marker-derived frame. This is the " +
                         "SHARPNESS observable: an edge presents a near-point or line patch and a broad " +
                         "face presents an area. s_contacts counts points and a count is not an extent.");
            h.AppendLine("# patch,NOTE,pairVsOverlap,the s_patch_* series cover EVERY delivered contact " +
                         "point and the s_patch_ovl_* series cover only points at separation <= 0. PhysX " +
                         "reports points inside the contact offset, so a patch measured over the pair set " +
                         "includes the speculative envelope and is NOT a contact patch. Compare like with " +
                         "like.");
            h.AppendLine("# patch,NOTE,noSample,a step with no delivered contact points contributes NO " +
                         "sample rather than a zero, so _n is lower than the recorder-sourced series by " +
                         "the number of steps with no patch. Zero extent and no contact are different " +
                         "facts. No threshold is set here: what counts as a point, a line or an area is " +
                         "the researcher's declaration.");
        }
        else
        {
            h.AppendLine("# patch,probe,NOT WIRED,note,no RqContactPatchProbe on this station, so this " +
                         "run does not state contact-patch extent and cannot speak to sharpness");
        }

        if (physRef != null)
            h.AppendLine("# physref,probe,RqPhysicalReferenceProbe,resolved," + physRef.Resolved +
                         ",note,the six Chapter 3 marker series were captured alongside this run into " +
                         "their own NOT_EQUIVALENT_*.csv. Those values are NOT EQUIVALENT to physical " +
                         "measurements - thesis 4.1 governs - and contribute no column here. They exist " +
                         "so a trace SHAPE can be compared, never a magnitude.");
        else
            h.AppendLine("# physref,probe,NOT WIRED,note,no RqPhysicalReferenceProbe on this station, so " +
                         "no cross-boundary marker capture accompanies this run");

        h.AppendLine("# NOTE,tiers,the PRIMARY block is CH4_MeasurementContract.md §5.1 and is KINEMATIC. " +
                     "The SECONDARY block is §5.2 and explains the primary — it never substitutes for it. " +
                     "In particular s_off_normal_n IS NOT FRICTION: Collision.impulse on a " +
                     "single collider pair comes back parallel to the contact normal, so it reads exactly " +
                     "0.00 on single-hull blades, and the non-zero QuickDirty reading is a multi-hull " +
                     "summing artefact. Do not cite it as friction");
        h.AppendLine("# NOTE,contactVocabulary,PAIR is not TOUCH,note,PhysX generates contact points " +
                     "inside the combined contact offset, so a contact PAIR exists with POSITIVE " +
                     "separation while the blades are still apart. At the project value of 0.01 m per " +
                     "collider that begins at about 20 mm and ContinuousSpeculative extends it further " +
                     "by closing speed. pair_steps counts the pair; overlap_steps counts steps whose " +
                     "closest reported separation was at or below zero, which is the count that requires " +
                     "the surfaces to have met. Their difference is the speculative envelope in steps");
        h.AppendLine("# NOTE,forceBeforeContact,normal force is NOT zero before the surfaces meet" +
                     ",note,the force in the envelope is ContinuousSpeculative acting against a " +
                     "penetration that has not happened. Measured on this project at first non-zero: " +
                     "7.99 N Flat / 6.88 N EdgeNoBind / 7.49 N EdgeBind - three presentations that are " +
                     "supposed to differ, agreeing to about 1 N - and it is the LARGEST force in the " +
                     "onset sequence before it decays. Do not read a non-zero normal force as evidence " +
                     "of contact");
        h.AppendLine("# NOTE,closestApproachLimit,s_closest_mm is an upper bound only while the blades " +
                     "are APART,note,Collider.ClosestPoint returns the query point unchanged when it is " +
                     "inside, so once the hulls overlap this value does not fall to zero - it becomes " +
                     "meaningless. Measured on one run: the probe never reported closer than 1.87 mm " +
                     "over a window in which PhysX reported 15.34 mm of penetration. " +
                     "run_min_separation_mm and run_deepest_pen_mm are the signed quantities, but see " +
                     "the renamedForClarity note below before reading either");
        h.AppendLine("# NOTE,renamedForClarity,run_min_separation_mm was previously serialized as " +
                     "min_separation_mm; run_deepest_pen_mm as deepest_pen_mm; s_run_min_separation_mm " +
                     "as s_min_separation_mm; s_run_deepest_pen_mm as s_deepest_pen_mm" +
                     ",note,the old names promised a per-step or per-window measurement and delivered a " +
                     "RUN-CUMULATIVE running minimum. RqStationContactRecorder keeps a running min and " +
                     "resets it only once per run, so the value latches at the deepest point reached and " +
                     "never recovers. Across 36 archived step traces the column never increased in any " +
                     "file. Consequences: the ten s_ statistics describe a monotone staircase rather " +
                     "than a distribution - mean and sd measure WHEN the minimum latched, not how deep " +
                     "penetration typically was - and a single-step outlier pins the column for the rest " +
                     "of the run. On one archived run it jumped -0.48 to -113.91 mm in one 11.1 ms tick " +
                     "while the independent live instrument read 0.734 mm. For a per-step separation " +
                     "read step_min_separation_mm in the step trace, which is sourced from " +
                     "RqContactPatchProbe over the contact points actually delivered that step");
        h.AppendLine("# NOTE,zeroProvenance," + recorder.ZeroSource);
    }

    // Each side gets its OWN section, so a reader keying on the section name does not have B silently
    // overwrite A.
    private static void AppendBody(StringBuilder h, string side, Rigidbody body)
    {
        if (body == null)
        {
            h.AppendLine("# body" + side + ",name,NOT BOUND,note,no live proxy body at seal, so this " +
                         "side's mass properties are not stated and its columns cannot be trusted");
            return;
        }

        h.AppendLine("# body" + side + ",name," + body.name +
                     ",mass," + F6(body.mass) +
                     ",centreOfMass," + V(body.centerOfMass) +
                     ",inertiaTensor," + V(body.inertiaTensor) +
                     ",collisionDetection," + body.collisionDetectionMode +
                     ",interpolation," + body.interpolation +
                     ",solverIterations," + body.solverIterations +
                     ",solverVelocityIterations," + body.solverVelocityIterations +
                     ",useGravity," + body.useGravity +
                     ",isKinematic," + body.isKinematic);
    }

    private string Label() =>
        (string.IsNullOrEmpty(stationId) ? name : stationId) +
        (string.IsNullOrEmpty(treatment) ? "" : "/" + treatment);

    /// <summary>
    ///     A world point expressed in the station root's own frame — the SECONDARY, never a replacement.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Every world-space quantity in these files is written twice: the original world value, and
    ///         this station-relative adjustment beside it. <b>The world math is never removed and never
    ///         overwritten.</b> A corrected number that has replaced its original cannot be audited, and
    ///         if the adjustment is ever found to be wrong the original is the only way back.
    ///     </para>
    ///     <para>
    ///         The adjustment exists because the ten stations sit at different world positions, so an
    ///         absolute contact point is not comparable across them while a station-relative one is. It
    ///         is the full inverse transform rather than a subtraction, so a rotated or scaled station
    ///         root is handled; the root's own position and rotation are in the header so the mapping is
    ///         reproducible from the file alone.
    ///     </para>
    ///     <para>
    ///         <b>Quantities expected to be invariant get a secondary anyway.</b> Contact migration and
    ///         the along/across-blade coordinates are computed in each blade's own frame; tip travel,
    ///         slip path and the separation scalars are differences or magnitudes; the rotations and the
    ///         normal swing are angles. None of them should change. They are still emitted, and the
    ///         angles and distances are RECOMPUTED rather than copied — because an expected equality that
    ///         is never printed is an assertion, and a file that omitted the column could never show the
    ///         invariance held for that run. See <see cref="ComputeInvariance" />.
    ///     </para>
    /// </remarks>
    private Vector3 ToStation(Vector3 world) => transform.InverseTransformPoint(world);

    /// <summary>
    ///     Recomputes the coupling terms in the STATION's frame, from the recorder's raw poses, every
    ///     step.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>A genuinely independent second computation.</b> It takes the same zero and current
    ///         poses the recorder used, maps them into the station frame, and derives the angles and
    ///         distances again. It does not start from the recorder's answers, because a figure derived
    ///         from an answer agrees with it by construction and demonstrates nothing. An earlier version
    ///         added zero to the original and was replaced for exactly that reason.
    ///     </para>
    ///     <para>
    ///         <b>Called from <see cref="OnStep" />, at this component's own execution order of +200, and
    ///         that placement is the whole correctness argument.</b> The station recorder runs at 0 and
    ///         physics simulates after every <c>FixedUpdate</c> callback, so between order 0 and order
    ///         200 of one step no body has moved: <c>recorder.Current</c> and the live frame transform
    ///         describe the same instant.
    ///     </para>
    ///     <para>
    ///         <b>Measured, when it was computed in the wrong place.</b> The first version ran from
    ///         <see cref="CloseSegment" />, which the runner reaches from its phase callback at order
    ///         -200 — before the recorder has updated for that step. It reported 2.43 mm of
    ///         "disagreement" at SNAP, 0.22 at HOLD and 0.005 at CONTACT: scaling with blade speed and
    ///         vanishing when the blades were nearly still, which is the signature of a one-step time
    ///         offset and not of frame arithmetic. The delta is only a frame check if both reads are in
    ///         the same instant.
    ///     </para>
    /// </remarks>
    private void ComputeInvariance()
    {
        invHave = false;
        if (recorder == null) return;

        Quaternion inv = Quaternion.Inverse(transform.rotation);

        Quaternion zRotA, nRotA, zRotB, nRotB;
        Vector3 zTipA, nTipA, zTipB, nTipB;

        if (!recorder.TryPose(true, out zRotA, out zTipA, out nRotA, out nTipA)) return;
        if (!recorder.TryPose(false, out zRotB, out zTipB, out nRotB, out nTipB)) return;

        // Rotations, between the same two orientations after both are carried into the station frame.
        // An angle between two orientations cannot depend on the frame it is expressed in, so this must
        // reproduce the recorder's figure.
        invRotA = Quaternion.Angle(inv * zRotA, inv * nRotA);
        invRotB = Quaternion.Angle(inv * zRotB, inv * nRotB);
        invRotGain = invRotA > 1e-4f ? invRotB / invRotA : 0f;

        // Tip travel, as the distance between the same two points expressed station-locally.
        invTipA = Vector3.Distance(ToStation(zTipA), ToStation(nTipA)) * 1000f;
        invTipB = Vector3.Distance(ToStation(zTipB), ToStation(nTipB)) * 1000f;
        invTipGain = invTipA > 1e-6f ? invTipB / invTipA : 0f;

        RqStationContactRecorder.Record c = recorder.Current;

        float worst = 0f;
        worst = Mathf.Max(worst, Mathf.Abs(invRotA - c.RotationADeg));
        worst = Mathf.Max(worst, Mathf.Abs(invRotB - c.RotationBDeg));
        worst = Mathf.Max(worst, Mathf.Abs(invRotGain - c.RotationGainBA));
        worst = Mathf.Max(worst, Mathf.Abs(invTipA - c.TipTravelA * 1000f));
        worst = Mathf.Max(worst, Mathf.Abs(invTipB - c.TipTravelB * 1000f));
        worst = Mathf.Max(worst, Mathf.Abs(invTipGain - c.TipGainBA));
        invWorst = worst;
        invHave = true;
    }

    /// <summary>
    ///     The station-frame columns, and the largest disagreement with their originals.
    /// </summary>
    /// <remarks>
    ///     The along/across-blade and closest-approach terms are already taken in a blade's own frame, so
    ///     the station-frame value IS the same number and is emitted unchanged. It is emitted rather than
    ///     omitted for the same reason as the rest: an equality that is never printed is an assertion,
    ///     and a file that omitted it could not show the invariance held for this run.
    /// </remarks>
    private string InvarianceColumns(Segment s, RqStationContactRecorder.Record c)
    {
        if (!s.InvarianceComputed)
            return "n/a,n/a,n/a,n/a,n/a,n/a," +
                   F(s.EntryClosestMm) + "," + F(s.EntryAlongBladeAMm) + "," + F(s.EntryAlongBladeBMm) +
                   "," + F(s.EntryAcrossEdgeAMm) + "," + F(s.EntryAcrossEdgeBMm) + ",not computed";

        return F(s.StnTipAMm) + "," + F(s.StnTipBMm) + "," + s.StnTipGainBA.ToString("F5") + "," +
               s.StnRotADeg.ToString("F4") + "," + s.StnRotBDeg.ToString("F4") + "," +
               s.StnRotGainBA.ToString("F5") + "," +
               F(s.EntryClosestMm) + "," + F(s.EntryAlongBladeAMm) + "," + F(s.EntryAlongBladeBMm) + "," +
               F(s.EntryAcrossEdgeAMm) + "," + F(s.EntryAcrossEdgeBMm) + "," +
               s.InvarianceWorstDelta.ToString("F6");
    }

    /// <summary>
    ///     True when a Drive segment exists but spans no steps — a known failure mode, flagged at seal.
    /// </summary>
    /// <remarks>
    ///     Measured 2026-08-26: the clip-completion test fired on the still-current source state, whose
    ///     <c>normalizedTime</c> was already past 1 after the 900-step Contact dwell, so the window
    ///     closed before the drive began. The cause is fixed, but a run that produces an empty Drive
    ///     window for any reason is a failed pass and must be named as one rather than shipping a file
    ///     whose columns are all zero and which reads like a completed run.
    /// </remarks>
    private bool DriveWindowEmpty()
    {
        for (int i = 0; i < segments.Count; i++)
            if (segments[i].Label == "Drive") return segments[i].Steps <= 0;

        return true;
    }

    private static string Blank(string v) => string.IsNullOrEmpty(v) ? "(none)" : v;

    private static string B(bool v) => v ? "1" : "0";
    private static string F(float v) => v.ToString("F3");
    private static string F6(float v) => v.ToString("F6");
    private static string V(Vector3 v) => F6(v.x) + " " + F6(v.y) + " " + F6(v.z);
    private static string Q(Quaternion q) =>
        F6(q.x) + " " + F6(q.y) + " " + F6(q.z) + " " + F6(q.w);

    private static string XYZ(Vector3 v) => F6(v.x) + "," + F6(v.y) + "," + F6(v.z);

    private const string BoundaryHeader =
        "event_index,segment,kind,step_in_segment," +
        // THE DISCRIMINATING INSTANT — the last step the contact existed.
        "last_touch_read,last_touch_lead_pair," +
        "last_touch_along_a_mm,last_touch_along_b_mm," +
        "last_touch_frac_along_a,last_touch_frac_along_b," +
        "last_touch_across_a_mm,last_touch_across_b_mm,last_touch_closest_mm," +
        "last_touch_contact_a_world_x,last_touch_contact_a_world_y,last_touch_contact_a_world_z," +
        "last_touch_contact_b_world_x,last_touch_contact_b_world_y,last_touch_contact_b_world_z," +
        "last_touch_contact_a_stn_x,last_touch_contact_a_stn_y,last_touch_contact_a_stn_z," +
        "last_touch_contact_b_stn_x,last_touch_contact_b_stn_y,last_touch_contact_b_stn_z," +
        // the step after, so both instants are on the record
        "after_read,after_lead_pair,after_inverted,after_closest_mm," +
        "after_along_a_mm,after_along_b_mm," +
        // each blade's own span, so the row is self-contained
        "tip_along_a_mm,tip_along_b_mm";

    /// <summary>
    ///     The boundary-event file: every contact loss and re-engagement, and where it happened.
    /// </summary>
    /// <remarks>
    ///     A separate file rather than more columns on the segment row, because the number of events per
    ///     run varies and the fork recorder already establishes the several-files-per-run idiom. The
    ///     segment row carries the counts so a reader is told the events exist.
    /// </remarks>
    private string BuildBoundaries()
    {
        var sb = new StringBuilder(4096);
        AppendPreflight(sb);

        sb.AppendLine("# Contact-boundary events: every step on which PhysX's contact report for this "
                      + "pair changed state.");
        sb.AppendLine("# kind,Release,note,the pair was touching on the previous sample and is not on "
                      + "this one. ReEngage is the reverse. The count of these is reported per segment "
                      + "in the main file alongside the recorder's own ReleaseCount, which is derived "
                      + "independently");
        sb.AppendLine("# columns,last_touch_*,note,the reading on the LAST sample the pair was "
                      + "touching. along is the contact position measured along that blade's own axis "
                      + "from its quillon midpoint; tip_along is where that blade's tip sits on the same "
                      + "axis, so along and tip_along share an origin and a direction. frac_along is "
                      + "along divided by tip_along");
        sb.AppendLine("# columns,after_*,note,the same reading on the first sample the pair was not "
                      + "touching");
        sb.AppendLine("# columns,contact_*_world and contact_*_stn,note,the contact point in world "
                      + "space and in the station's own frame. Both are present and the world value is "
                      + "never removed");
        sb.AppendLine("# NOTE,scope,these are positions and states, not causes. This file does not "
                      + "classify why contact ended and no column here should be read as doing so");

        sb.AppendLine(BoundaryHeader);

        for (int i = 0; i < boundaries.Count; i++)
        {
            BoundaryEvent e = boundaries[i];

            sb.AppendLine(
                i + "," + e.Segment + "," + e.Kind + "," + e.StepInSegment + "," +
                B(e.LastTouchRead) + "," + e.LastTouchLeadPair + "," +
                F(e.LastTouchAlongAMm) + "," + F(e.LastTouchAlongBMm) + "," +
                Frac(e.LastTouchAlongAMm, e.TipAlongAMm) + "," +
                Frac(e.LastTouchAlongBMm, e.TipAlongBMm) + "," +
                F(e.LastTouchAcrossAMm) + "," + F(e.LastTouchAcrossBMm) + "," +
                F(e.LastTouchClosestMm) + "," +
                XYZ(e.LastTouchContactAWorld) + "," + XYZ(e.LastTouchContactBWorld) + "," +
                XYZ(ToStation(e.LastTouchContactAWorld)) + "," +
                XYZ(ToStation(e.LastTouchContactBWorld)) + "," +
                B(e.AfterRead) + "," + e.AfterLeadPair + "," + B(e.AfterInverted) + "," +
                F(e.AfterClosestMm) + "," +
                F(e.AfterAlongAMm) + "," + F(e.AfterAlongBMm) + "," +
                F(e.TipAlongAMm) + "," + F(e.TipAlongBMm));
        }

        if (boundaries.Count == 0)
            sb.AppendLine("# NO BOUNDARY EVENT — contact was never lost and never re-established inside "
                          + "any recorded segment. That is a statement about this run, not a missing "
                          + "measurement");

        return sb.ToString();
    }

    /// <summary>
    ///     Where along the blade, as a fraction of that blade's own span. 0 is the quillon midpoint, 1
    ///     the tip. Reported so the position can be read against the extremities; it classifies nothing.
    /// </summary>
    private static string Frac(float alongMm, float tipMm)
    {
        if (Mathf.Abs(tipMm) < 1e-6f) return "n/a";

        return (alongMm / tipMm).ToString("F4");
    }
}
