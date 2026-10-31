// =====================================================================================
// PRIMARY COMPARISON INSTRUMENT — see PLAN_DriveWindowRecording.md §4.
//
// Its columns map onto the Chapter 3 observables almost one-for-one: migration along A and B,
// relative tip and rotation gain, slip path, release count, coupling duration.
//
// WINDOW DEFECT — CLOSED 2026-08-26 (plan §6 item 2). autoLatchOnFirstContact defaulted TRUE, so
// the zero latched on the FIRST TOUCH, during Hold/Contact and before A moves, while the physical
// record aligns at A's forward onset. The default is now FALSE, and because a changed default does
// not reach ten already-serialized instances, RqDriveWindowRecorder additionally calls
// SuppressAutoLatch at arm. ZeroSource states in the sealed header which instant a file's zero
// actually came from, so a file can no longer be silently mis-windowed.
//
// THE POP-THROUGH PRE-GATE — CLOSED 2026-08-26 (plan §3 / §6 item 3), and NOT in this component.
// It lives in RqDriveWindowRecorder, gated on RqContactSideProbe, because this recorder is
// measurement-only by design: it decides nothing, and a validity decision taken here would make
// the instrument part of what it measures. Six pass-through runs from 2026-08-25 are the reason
// the gate exists — release and pass-through must never occupy the same column.
// =====================================================================================
using System.Collections.Generic;
using BladeContact;
using FIMSpace.FProceduralAnimation;
using UnityEngine;

/// <summary>
///     One measurement-only record of what happened between a station's two blades. Identical on all
///     nine stations, whatever treatment the station is running.
/// </summary>
/// <remarks>
///     <para>
///         <b>This applies nothing and decides nothing.</b> It adds no force, writes no material, touches
///         no collider, and never classifies a contact. It exists so that the three treatments can be
///         compared through one instrument rather than through each treatment's own reporting, which
///         would make the instrument a confound.
///     </para>
///     <para>
///         <b>No semantics on the controls.</b> Nothing here needs a <see cref="BladeShell" />, an
///         authored profile, or a feature designation. The Baseline and QuickDirty rows are read purely
///         through the contacts PhysX generated. Where a treatment adds its own term the record notes it
///         separately, so the shared columns stay shared.
///     </para>
///     <para>
///         <b>The primary outputs are kinematic.</b> Contact migration along each blade, tangential
///         sliding, how long contact persisted, when it released, and whether the shells crossed. Forces
///         are recorded too, but as secondary diagnostics only, and one of them is a negative result:
///         PhysX's own tangential response is NOT observable through <see cref="Collision.impulse" />,
///         which comes back parallel to the contact normal. See
///         <see cref="Record.OffNormalImpulseForce" />. Where the contact sat on the blade, by contrast,
///         is the same question asked the same way for every row.
///     </para>
///     <para>
///         <b>Migration is blade-local on purpose.</b> A world-space displacement cannot separate a
///         contact that is held on both blades while the blades themselves move from one that is running
///         down them; the second is slip and the first is not.
///     </para>
///     <para>
///         <b>What is deliberately NOT reported.</b> There is no gap, clearance or separation reading
///         when the pair is not touching. A scalar distance from an instrument that saturates or goes
///         stale is not evidence about whether two blades meet, and treating it as such has previously
///         voided a matrix of answers. Absence of contact is reported only as counted steps.
///     </para>
/// </remarks>
[DisallowMultipleComponent]
public sealed class RqStationContactRecorder : MonoBehaviour
{
    /// <summary>One side of the pair: which shapes are this blade, and where they currently live.</summary>
    [System.Serializable]
    public sealed class Side
    {
        [Tooltip("Label used in the record. Cosmetic.")]
        public string label = "";

        [Tooltip("The authored colliders that ARE this blade. For QuickDirty that is all three live " +
                 "hulls. Crossguard, grip, pommel and marker colliders must never be listed.")]
        public List<Collider> bladeColliders = new List<Collider>();

        [Tooltip("The attachable this sword is worn by, when it is worn. Ragdoll Animator disables the " +
                 "authored colliders and rebuilds them on a generated body, so the shapes actually in " +
                 "contact are not the ones listed above.")]
        public RA2AttachableObject attachable;

        [Tooltip("Blade long axis in the sword's own local space, and the direction counted POSITIVE. " +
                 "Both specimens sweep along local X (BladeProfileSection.Axis = X). The DIRECTION is not " +
                 "in the profile and had to be measured: the tip sits at local x = -1.011 m and the " +
                 "quillon at -0.031 m, so local -X is TIP-ward. This default therefore counts migration " +
                 "TOWARD THE TIP as positive, which is the direction a contact runs when a blade is " +
                 "driven through it. A geometric fact about the model, not a study parameter.")]
        public Vector3 localLongAxis = Vector3.left;

        [Tooltip("The blade tip in the sword's own local space, taken from that sword's own tip marker. " +
                 "Used only for tip travel, which is the physical record's coupling signature.")]
        public Vector3 localTip = new Vector3(-1.011f, 0.021f, 0f);

        [System.NonSerialized] public readonly List<Collider> live = new List<Collider>();
        [System.NonSerialized] public Rigidbody body;
        [System.NonSerialized] public Transform frame;
        [System.NonSerialized] public GameObject boundTo;
        [System.NonSerialized] public BladeCollisionRelay relay;
        [System.NonSerialized] public bool zeroPose;
        [System.NonSerialized] public Quaternion zeroRotation;
        [System.NonSerialized] public Vector3 zeroTipWorld;

        public bool Owns(Collider collider)
        {
            for (int i = 0; i < live.Count; i++)
                if (live[i] == collider) return true;

            return false;
        }
    }

    /// <summary>Everything the record holds about this station's blade pair.</summary>
    public struct Record
    {
        public bool EverTouched;

        // --- primary: persistence ---------------------------------------------------------------
        /// <summary>
        ///     Fixed steps in which PhysX reported a CONTACT PAIR for these two blades, since the zero.
        /// </summary>
        /// <remarks>
        ///     <b>A pair is not a touch.</b> PhysX generates contact points inside the combined contact
        ///     offset, so a pair exists — with POSITIVE separation — while the blades are still apart.
        ///     At the project value of 0.01 m per collider that begins at about 20 mm, and
        ///     ContinuousSpeculative extends it further by closing speed. This was previously named
        ///     TouchingSteps, which put a claim into every column that the apparatus had not made.
        ///     <see cref="OverlapSteps" /> is the count that requires the surfaces to have met.
        /// </remarks>
        public int PairSteps;

        /// <summary>
        ///     Fixed steps on which the closest reported contact had separation at or below zero.
        /// </summary>
        /// <remarks>
        ///     This is the count that means the surfaces actually met. It is a subset of
        ///     <see cref="PairSteps" /> and the two are reported side by side deliberately: their
        ///     difference is the width of the speculative envelope in steps.
        /// </remarks>
        public int OverlapSteps;

        /// <summary>Steps with no contact after the pair had first touched and before it ended.</summary>
        public int DropoutSteps;

        /// <summary>Longest unbroken run of PAIR steps, in steps and in seconds. Not of overlap.</summary>
        public int LongestRunSteps;

        public float LongestRunSeconds;

        /// <summary>True while PhysX is reporting a contact PAIR right now. May be apart — see
        /// <see cref="PairSteps" />.</summary>
        public bool PairPresent;

        /// <summary>True while the closest reported contact has separation at or below zero.</summary>
        public bool Overlapping;

        /// <summary>
        ///     Smallest SIGNED separation reported this run, metres. Positive means never met.
        /// </summary>
        /// <remarks>
        ///     Unlike <see cref="DeepestPenetration" />, which starts at zero and therefore only ever
        ///     records negatives, this keeps the true minimum including the pre-contact approach. Without
        ///     it a run that never overlapped reported 0.000 and looked like a run that touched exactly.
        /// </remarks>
        public float MinSeparation;

        // --- primary: release -------------------------------------------------------------------
        /// <summary>Seconds after the zero at which a contact PAIR first existed. Not first touch.</summary>
        public float FirstPairSeconds;

        /// <summary>Seconds after the zero at which separation first reached zero. Negative if never.</summary>
        public float FirstOverlapSeconds;

        /// <summary>Seconds after the zero at which contact was last lost. Negative when never lost.</summary>
        public float LastReleaseSeconds;

        /// <summary>How many times contact went from touching to not touching.</summary>
        public int ReleaseCount;

        // --- primary: contact migration (the blade-local bind statement) ------------------------
        /// <summary>How far the contact has travelled ALONG blade A since the zero, metres.</summary>
        public float MigrationAlongA;

        /// <summary>How far the contact has travelled ALONG blade B since the zero, metres.</summary>
        public float MigrationAlongB;

        /// <summary>Largest magnitude either migration reached, metres.</summary>
        public float PeakMigration;

        // --- primary: sliding -------------------------------------------------------------------
        /// <summary>Relative tangential speed at the contact right now, m/s.</summary>
        public float TangentialSpeed;

        /// <summary>Path length of tangential relative sliding accumulated since the zero, metres.</summary>
        public float TangentialSlipPath;

        // --- primary: coupling (the physical record's own bind signature) ------------------------
        /// <summary>Angle each blade body has turned since the zero, degrees.</summary>
        /// <remarks>
        ///     The physical trial's primary dependent variable is B's rotation response: in a bind, B
        ///     pivots in the holder's grip because the two blades move as one body. A driving blade that
        ///     turns while the contacted blade does not is a slide, whatever the contact force says.
        /// </remarks>
        public float RotationADeg;

        public float RotationBDeg;

        /// <summary>B's turn as a fraction of A's. Near 1 is coupled motion; near 0 is A moving alone.</summary>
        public float RotationGainBA;

        /// <summary>Net displacement of each blade tip since the zero, metres.</summary>
        public float TipTravelA;

        public float TipTravelB;

        /// <summary>
        ///     B's tip travel as a fraction of A's. The physical record reads 1.01 on the bind pass
        ///     against 0.30 flat and 0.34 edge-no-bind.
        /// </summary>
        public float TipGainBA;

        // --- primary: crossing ------------------------------------------------------------------
        /// <summary>Most negative contact separation PhysX reported, metres. Negative is interpenetration.</summary>
        public float DeepestPenetration;

        /// <summary>True if the contact normal ever reversed past 90 degrees from its first direction.</summary>
        public bool NormalReversed;

        /// <summary>Largest angle the contact normal reached away from its first direction, degrees.</summary>
        public float PeakNormalSwing;

        // --- secondary: forces ------------------------------------------------------------------
        /// <summary>Normal force PhysX resolved this step, newtons.</summary>
        public float NormalForce;

        /// <summary>
        ///     Off-normal part of the impulse PhysX reported this step, newtons.
        ///     <b>This is NOT PhysX friction.</b>
        /// </summary>
        /// <remarks>
        ///     Kept, and named for what it actually is, because what it is worth is a negative result.
        ///     Measured 2026-08-19 over the DRIVE window: this reads EXACTLY 0.00 at all three Baseline
        ///     stations and at both touching OurSolution stations, over hundreds of touching steps at
        ///     55-163 N of normal load and a 0.45 friction material. A real friction force cannot be
        ///     identically zero there. <see cref="Collision.impulse" /> on a single collider pair comes
        ///     back parallel to the contact normal, so PhysX's own tangential response is not observable
        ///     through it.
        ///
        ///     The QuickDirty row does read non-zero (17-57 N) and that is an ARTEFACT, not a measurement:
        ///     each blade there carries three shapes, so several collider pairs report in the same step,
        ///     each with its own normal, and summing their impulses then projecting on the MEAN normal
        ///     leaves a residual. Reporting that as friction would have credited QuickDirty with a
        ///     tangential response the number does not evidence.
        ///
        ///     This is why the primary comparison is kinematic. Do not cite this column as friction.
        /// </remarks>
        public float OffNormalImpulseForce;

        /// <summary>Tangential force an augmenting layer added this step, newtons. Zero on the controls.</summary>
        public float AddedTangentialForce;

        /// <summary>Mean of each force over the touching steps since the zero, newtons.</summary>
        public float MeanNormalForce;

        /// <summary>Mean <see cref="OffNormalImpulseForce" />. Not friction; see that field.</summary>
        public float MeanOffNormalImpulseForce;
        public float MeanAddedTangentialForce;

        /// <summary>PhysX contact points in the pair this step.</summary>
        public int ContactCount;

        /// <summary>Which shapes actually produced the deepest contact. Never inferred.</summary>
        public string DeepestShapeA;

        public string DeepestShapeB;
    }

    [Header("Identity")]
    [Tooltip("Station label used in the record. Cosmetic; nothing here behaves differently because of it.")]
    [SerializeField] private string station = "";

    [Tooltip("Treatment label used in the record. Cosmetic. The recorder is identical for all three and " +
             "reads nothing from this.")]
    [SerializeField] private string treatment = "";

    [Header("Pair")]
    [SerializeField] private Side a = new Side();
    [SerializeField] private Side b = new Side();

    /// <summary>
    ///     The transform each side's coupling terms are measured on — the generated proxy once the sword is
    ///     worn. Exposed read-only so a consumer can recompute a coupling angle from the same body this
    ///     recorder uses, rather than resolving its own and risking a different one. Null before attachment.
    /// </summary>
    public Transform FrameA
    {
        get { return a == null ? null : a.frame; }
    }

    /// <inheritdoc cref="FrameA" />
    public Transform FrameB
    {
        get { return b == null ? null : b.frame; }
    }

    [Header("Augmenting layer, if any")]
    [Tooltip("Optional. When a treatment adds its own tangential term on top of PhysX, its applied " +
             "force is recorded in a SEPARATE column so the shared columns stay shared. Left empty, " +
             "that column reads zero, which is the correct reading for an untreated station.")]
    [SerializeField] private RqBladeTangentialSolver augmentingSolver;

    [SerializeField] private RqBladeContact augmentedA;
    [SerializeField] private RqBladeContact augmentedB;

    [Header("Zero")]
    [Tooltip("Latch the measurement zero automatically the first time the blades touch. Off, the zero " +
             "is set only by an explicit LatchZero() from whatever is running the protocol.\n\n" +
             "DEFAULT MOVED TO FALSE 2026-08-26, per PLAN_DriveWindowRecording.md §6 item 2. First " +
             "touch happens during Hold or Contact, before A moves, while the physical record aligns its " +
             "window at the onset of A's forward passing step. A zero latched on first touch therefore " +
             "measures a different window from the one the comparison is against.\n\n" +
             "Changing this default does NOT change instances already serialized under the old one, " +
             "which is why RqDriveWindowRecorder calls SuppressAutoLatch at arm rather than trusting it.")]
    [SerializeField] private bool autoLatchOnFirstContact;

    private Record record;
    private bool zeroLatched;
    private float zeroTime;
    private bool autoLatchSuppressed;
    private string zeroSource = "no zero latched";

    private readonly List<ContactPoint> contactBuffer = new List<ContactPoint>(32);
    private readonly HashSet<int> countedShapePairs = new HashSet<int>();

    private int step;
    private int reportedStep = -1;
    private int contactCount;
    private Vector3 pointSum;
    private Vector3 normalSum;
    private Vector3 impulse;
    private float deepestThisStep;
    private Collider deepestA;
    private Collider deepestB;

    private bool haveFirstNormal;
    private Vector3 firstNormal;
    private bool haveAnchor;
    private Vector3 anchorLocalA;
    private Vector3 anchorLocalB;
    private int runSteps;
    private bool wasTouching;

    private double normalSumForce;
    private double offNormalSumForce;
    private double addedTangentialSumForce;
    private int forceSamples;

    /// <summary>The station label, for a readout.</summary>
    public string Station => station;

    /// <summary>The treatment label, for a readout.</summary>
    public string Treatment => treatment;

    /// <summary>Everything measured since the zero.</summary>
    public Record Current => record;

    /// <summary>True once a measurement zero has been established.</summary>
    public bool ZeroLatched => zeroLatched;

    /// <summary>
    ///     Where this record's zero came from, in words, for the sealed header of whatever writes it out.
    /// </summary>
    /// <remarks>
    ///     A window is only comparable to another window if both were differenced from the same instant
    ///     of the protocol, and "first touch" and "the Drive transition" are not the same instant. Stated
    ///     in the file rather than left to be inferred from which component happened to be enabled.
    /// </remarks>
    public string ZeroSource => zeroSource;

    /// <summary>The live proxy body carrying blade A right now, or null while the sword is not worn.</summary>
    /// <remarks>
    ///     Exposed so a sealed header can state the mass properties of the body that ACTUALLY simulated.
    ///     The authored sword is kinematic with <c>detectCollisions = false</c> and its values are not
    ///     the ones the run was solved under — see <see cref="RqProxyPhysicsDeclaration" />.
    /// </remarks>
    public Rigidbody SideABody => a.body;

    /// <summary>The live proxy body carrying blade B right now, or null while the sword is not worn.</summary>
    public Rigidbody SideBBody => b.body;

    /// <summary>
    ///     The raw poses the coupling terms are computed from, so another instrument can recompute them
    ///     independently in its own frame.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Exposed rather than only the finished figures, because an expected invariance is only
    ///         demonstrated by a SECOND computation from the same inputs. Handing out the answer and
    ///         adding zero to it proves nothing — two runs agreeing can mean the rig was frozen, and the
    ///         same trap applies to two numbers agreeing because one was derived from the other.
    ///     </para>
    ///     <para>
    ///         Must be read at the SAME INSTANT as <see cref="Current" />, or the disagreement reported
    ///         will be the time between the two reads rather than anything about the frame.
    ///     </para>
    /// </remarks>
    public bool TryPose(bool sideA, out Quaternion zeroRotation, out Vector3 zeroTipWorld,
                        out Quaternion nowRotation, out Vector3 nowTipWorld)
    {
        Side s = sideA ? a : b;

        zeroRotation = Quaternion.identity;
        nowRotation = Quaternion.identity;
        zeroTipWorld = Vector3.zero;
        nowTipWorld = Vector3.zero;

        if (!s.zeroPose || s.frame == null) return false;

        zeroRotation = s.zeroRotation;
        zeroTipWorld = s.zeroTipWorld;
        nowRotation = s.frame.rotation;
        nowTipWorld = s.frame.TransformPoint(s.localTip);
        return true;
    }

    /// <summary>
    ///     Takes ownership of the zero away from first-touch, for the rest of this Play session.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Called by whatever is running the protocol, because the serialized default cannot be
    ///         trusted: flipping a <c>[SerializeField]</c> default does not change instances already
    ///         serialized under the old one, and there are ten of those in the scene. A driver that needs
    ///         the zero at a declared instant must therefore say so at runtime.
    ///     </para>
    ///     <para>
    ///         Deliberately one-way. There is no un-suppress, because a run in which the zero's ownership
    ///         changed half way through would have two different windows in one file and no column saying
    ///         which was which.
    ///     </para>
    /// </remarks>
    public void SuppressAutoLatch(string because)
    {
        if (autoLatchSuppressed) return;

        autoLatchSuppressed = true;

        if (autoLatchOnFirstContact)
            Debug.Log("[Recorder] " + station + ": autoLatchOnFirstContact was SERIALIZED TRUE and is " +
                      "now suppressed for this session. " + because, this);
    }

    /// <summary>
    ///     Sets the instant every accumulated quantity is measured from, and clears them.
    /// </summary>
    /// <remarks>
    ///     Migration and slip path are differences, so they mean nothing without a declared instant to
    ///     difference against. Latching at the start of the loaded phase means every station's numbers
    ///     describe the same window of the protocol.
    /// </remarks>
    public void LatchZero()
    {
        LatchZero("explicit LatchZero() from the protocol driver");
    }

    /// <summary>As <see cref="LatchZero()" />, recording where the zero came from.</summary>
    public void LatchZero(string source)
    {
        zeroSource = source + " at t=" + Time.time.ToString("F4") + "s";

        record = default(Record);
        zeroLatched = true;
        zeroTime = Time.time;

        haveAnchor = false;
        haveFirstNormal = false;
        LatchPose(a);
        LatchPose(b);
        runSteps = 0;
        wasTouching = false;
        record.LastReleaseSeconds = -1f;
        record.FirstPairSeconds = -1f;
        record.FirstOverlapSeconds = -1f;
        record.MinSeparation = float.MaxValue;

        normalSumForce = 0d;
        offNormalSumForce = 0d;
        addedTangentialSumForce = 0d;
        forceSamples = 0;
    }

    private static void LatchPose(Side side)
    {
        if (side.frame == null) { side.zeroPose = false; return; }

        side.zeroRotation = side.frame.rotation;
        side.zeroTipWorld = side.frame.TransformPoint(side.localTip);
        side.zeroPose = true;
    }

    /// <summary>
    ///     How much each blade moved, and how much of A's motion B took up.
    /// </summary>
    /// <remarks>
    ///     Updated every step, touching or not. Coupling is a statement about the two bodies, and it does
    ///     not stop being measurable the moment PhysX drops a contact point.
    /// </remarks>
    private void UpdateCoupling()
    {
        if (a.zeroPose && a.frame != null)
        {
            record.RotationADeg = Quaternion.Angle(a.zeroRotation, a.frame.rotation);
            record.TipTravelA = Vector3.Distance(a.zeroTipWorld, a.frame.TransformPoint(a.localTip));
        }

        if (b.zeroPose && b.frame != null)
        {
            record.RotationBDeg = Quaternion.Angle(b.zeroRotation, b.frame.rotation);
            record.TipTravelB = Vector3.Distance(b.zeroTipWorld, b.frame.TransformPoint(b.localTip));
        }

        record.RotationGainBA = record.RotationADeg > 1e-4f ? record.RotationBDeg / record.RotationADeg : 0f;
        record.TipGainBA = record.TipTravelA > 1e-6f ? record.TipTravelB / record.TipTravelA : 0f;
    }

    /// <summary>Declares the pair this recorder watches, so the configuration is serialized, not inferred.</summary>
    public void Configure(
        string stationLabel, string treatmentLabel,
        string labelA, IEnumerable<Collider> bladesA, RA2AttachableObject attachableA,
        string labelB, IEnumerable<Collider> bladesB, RA2AttachableObject attachableB)
    {
        station = stationLabel;
        treatment = treatmentLabel;

        a.label = labelA;
        a.bladeColliders.Clear();
        a.bladeColliders.AddRange(bladesA);
        a.attachable = attachableA;

        b.label = labelB;
        b.bladeColliders.Clear();
        b.bladeColliders.AddRange(bladesB);
        b.attachable = attachableB;
    }

    /// <summary>Names the augmenting layer, when the station is running one.</summary>
    public void ConfigureAugmentation(
        RqBladeTangentialSolver solver, RqBladeContact contactA, RqBladeContact contactB)
    {
        augmentingSolver = solver;
        augmentedA = contactA;
        augmentedB = contactB;
    }

    private void OnEnable()
    {
        record.LastReleaseSeconds = -1f;
        record.FirstPairSeconds = -1f;
        record.FirstOverlapSeconds = -1f;
        record.MinSeparation = float.MaxValue;
    }

    private void OnDisable()
    {
        Release(a);
        Release(b);
    }

    private void LateUpdate()
    {
        Rebind(a);
        Rebind(b);
    }

    /// <summary>
    ///     Points at whichever shapes are carrying each blade right now.
    /// </summary>
    /// <remarks>
    ///     Ragdoll Animator does not move a worn sword's own body: it makes that body kinematic, switches
    ///     the authored colliders off, and rebuilds equivalent shapes on a generated body. A recorder
    ///     holding the authored collider would sit through the whole run reporting nothing at all, and
    ///     nothing would say so. The generated list corresponds one for one, in order, to
    ///     <see cref="RA2AttachableObject.AttachableColliders" />.
    /// </remarks>
    private void Rebind(Side side)
    {
        GameObject generated = side.attachable != null ? side.attachable.GeneratedPhysicsObject : null;
        if (generated == side.boundTo && side.live.Count > 0) return;

        Release(side);
        side.boundTo = generated;

        if (generated == null)
        {
            for (int i = 0; i < side.bladeColliders.Count; i++)
                if (side.bladeColliders[i] != null) side.live.Add(side.bladeColliders[i]);

            if (side.live.Count > 0)
            {
                side.body = side.live[0].attachedRigidbody;
                side.frame = side.body != null ? side.body.transform : side.live[0].transform;
            }
        }
        else
        {
            List<Collider> authored = side.attachable.AttachableColliders;
            List<Collider> copies = side.attachable.GeneratedPhysicsColliders;

            if (authored == null || copies == null || authored.Count != copies.Count)
            {
                Debug.LogWarning(
                    "[Recorder] " + station + "/" + side.label + ": Ragdoll Animator's generated collider " +
                    "list does not correspond to the authored one, so this blade cannot be followed onto " +
                    "the shapes now carrying it. This station will record nothing.", this);
                return;
            }

            for (int i = 0; i < authored.Count; i++)
                if (side.bladeColliders.Contains(authored[i]) && copies[i] != null) side.live.Add(copies[i]);

            side.body = side.attachable.lastRigidbody != null
                ? side.attachable.lastRigidbody
                : generated.GetComponent<Rigidbody>();
            side.frame = generated.transform;
        }

        if (side.live.Count == 0 || side.body == null)
        {
            Debug.LogWarning(
                "[Recorder] " + station + "/" + side.label + ": no live blade shape or no body, so this " +
                "station will record nothing.", this);
            return;
        }

        side.relay = side.body.GetComponent<BladeCollisionRelay>();
        if (side.relay == null) side.relay = side.body.gameObject.AddComponent<BladeCollisionRelay>();

        if (side == a) side.relay.Collided += OnCollisionFromA;
        else side.relay.Collided += OnCollisionFromB;

        // The frame only exists once the blade is bound, so a zero latched before that has no pose in it.
        if (zeroLatched && !side.zeroPose) LatchPose(side);
    }

    private void Release(Side side)
    {
        if (side.relay != null)
        {
            if (side == a) side.relay.Collided -= OnCollisionFromA;
            else side.relay.Collided -= OnCollisionFromB;
        }

        side.relay = null;
        side.live.Clear();
        side.body = null;
        side.frame = null;
    }

    // Both sides receive a callback for the same contact. Only A's view is kept, so the contact count
    // and the impulse are not counted twice.
    private void OnCollisionFromA(Collision collision) => Accumulate(collision);

    private void OnCollisionFromB(Collision collision)
    {
    }

    private void Accumulate(Collision collision)
    {
        int count = collision.GetContacts(contactBuffer);
        if (count == 0) return;

        for (int i = 0; i < count; i++)
        {
            ContactPoint contact = contactBuffer[i];
            if (!a.Owns(contact.thisCollider)) continue;
            if (!b.Owns(contact.otherCollider)) continue;

            if (reportedStep != step)
            {
                reportedStep = step;
                contactCount = 0;
                pointSum = Vector3.zero;
                normalSum = Vector3.zero;
                impulse = Vector3.zero;
                deepestThisStep = float.MaxValue;
                deepestA = null;
                deepestB = null;
                countedShapePairs.Clear();
            }

            // One blade can carry several shapes, so PhysX delivers a separate Collision per COLLIDER
            // pair in the same step, each with its own impulse. Summed once per collider pair.
            int shapePair = contact.thisCollider.GetHashCode() * 397 ^ contact.otherCollider.GetHashCode();
            if (countedShapePairs.Add(shapePair)) impulse += collision.impulse;

            pointSum += contact.point;
            normalSum += contact.normal;
            contactCount++;

            if (contact.separation < deepestThisStep)
            {
                deepestThisStep = contact.separation;
                deepestA = contact.thisCollider;
                deepestB = contact.otherCollider;
            }
        }
    }

    private void FixedUpdate()
    {
        step++;

        // The callbacks for step N arrive after step N is simulated, so they are consumed at the top of
        // step N+1, which is also when the poses they refer to are the current ones.
        bool touching = reportedStep == step - 1 && contactCount > 0;

        if (touching && !zeroLatched && autoLatchOnFirstContact && !autoLatchSuppressed)
            LatchZero("FIRST TOUCH (autoLatchOnFirstContact) — NOT the Drive transition, so this window " +
                      "is not aligned with the physical record's");
        if (!zeroLatched)
        {
            wasTouching = touching;
            return;
        }

        UpdateCoupling();

        record.PairPresent = touching;
        record.ContactCount = touching ? contactCount : 0;

        if (!touching)
        {
            if (wasTouching)
            {
                record.ReleaseCount++;
                record.LastReleaseSeconds = Time.time - zeroTime;
                runSteps = 0;
            }

            if (record.EverTouched) record.DropoutSteps++;

            record.NormalForce = 0f;
            record.OffNormalImpulseForce = 0f;
            record.AddedTangentialForce = 0f;
            record.Overlapping = false;
            wasTouching = false;
            return;
        }

        if (!record.EverTouched)
        {
            record.EverTouched = true;
            record.FirstPairSeconds = Time.time - zeroTime;
        }

        record.PairSteps++;
        runSteps++;
        if (runSteps > record.LongestRunSteps)
        {
            record.LongestRunSteps = runSteps;
            record.LongestRunSeconds = runSteps * Time.fixedDeltaTime;
        }

        wasTouching = true;

        Vector3 point = pointSum / contactCount;
        Vector3 normal = normalSum.sqrMagnitude > 1e-12f ? normalSum.normalized : Vector3.up;

        // --- crossing --------------------------------------------------------------------------
        if (!haveFirstNormal)
        {
            firstNormal = normal;
            haveFirstNormal = true;
        }

        float swing = Vector3.Angle(firstNormal, normal);
        if (swing > record.PeakNormalSwing) record.PeakNormalSwing = swing;
        if (swing > 90f) record.NormalReversed = true;

        if (deepestThisStep < record.DeepestPenetration) record.DeepestPenetration = deepestThisStep;
        if (deepestThisStep < record.MinSeparation) record.MinSeparation = deepestThisStep;

        // OVERLAP, as distinct from a pair existing. A pair is reported inside the contact offset while
        // the surfaces are still apart; only a separation at or below zero means they met.
        record.Overlapping = deepestThisStep <= 0f;
        if (record.Overlapping)
        {
            record.OverlapSteps++;
            if (record.FirstOverlapSeconds < 0f) record.FirstOverlapSeconds = Time.time - zeroTime;
        }

        // --- contact migration, in each blade's own frame ----------------------------------------
        if (a.frame != null && b.frame != null)
        {
            Vector3 localA = a.frame.InverseTransformPoint(point);
            Vector3 localB = b.frame.InverseTransformPoint(point);

            if (!haveAnchor)
            {
                anchorLocalA = localA;
                anchorLocalB = localB;
                haveAnchor = true;
            }

            record.MigrationAlongA = Vector3.Dot(localA - anchorLocalA, a.localLongAxis);
            record.MigrationAlongB = Vector3.Dot(localB - anchorLocalB, b.localLongAxis);

            float peak = Mathf.Max(Mathf.Abs(record.MigrationAlongA), Mathf.Abs(record.MigrationAlongB));
            if (peak > record.PeakMigration) record.PeakMigration = peak;
        }

        // --- sliding -----------------------------------------------------------------------------
        if (a.body != null && b.body != null)
        {
            Vector3 relative = b.body.GetPointVelocity(point) - a.body.GetPointVelocity(point);
            Vector3 tangential = relative - normal * Vector3.Dot(relative, normal);

            record.TangentialSpeed = tangential.magnitude;
            record.TangentialSlipPath += tangential.magnitude * Time.fixedDeltaTime;
        }

        // --- secondary: forces --------------------------------------------------------------------
        float dt = Mathf.Max(Time.fixedDeltaTime, 1e-6f);
        float alongNormal = Vector3.Dot(impulse, normal);

        record.NormalForce = Mathf.Abs(alongNormal) / dt;
        record.OffNormalImpulseForce = (impulse - normal * alongNormal).magnitude / dt;
        record.AddedTangentialForce = ReadAddedTangentialForce();

        normalSumForce += record.NormalForce;
        offNormalSumForce += record.OffNormalImpulseForce;
        addedTangentialSumForce += record.AddedTangentialForce;
        forceSamples++;

        record.MeanNormalForce = (float)(normalSumForce / forceSamples);
        record.MeanOffNormalImpulseForce = (float)(offNormalSumForce / forceSamples);
        record.MeanAddedTangentialForce = (float)(addedTangentialSumForce / forceSamples);

        record.DeepestShapeA = deepestA != null ? deepestA.name : "(none)";
        record.DeepestShapeB = deepestB != null ? deepestB.name : "(none)";
    }

    /// <summary>
    ///     What an augmenting layer added this step, or zero when the station is not running one.
    /// </summary>
    /// <remarks>
    ///     Read out of the layer's own record rather than recomputed, because recomputing it here would
    ///     be a second implementation of the same model and the two would drift.
    /// </remarks>
    private float ReadAddedTangentialForce()
    {
        if (augmentingSolver == null || augmentedA == null || augmentedB == null) return 0f;

        RqBladeTangentialSolver.Snapshot snapshot;
        if (!augmentingSolver.TryGetSnapshot(augmentedA, augmentedB, out snapshot)) return 0f;

        return snapshot.AppliedTangentialForce;
    }
}
