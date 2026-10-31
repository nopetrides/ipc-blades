using System.Collections.Generic;
using System.Text;
using FIMSpace.FProceduralAnimation;
using UnityEngine;

/// <summary>
///     Which <b>edge</b> and which <b>face</b> of each blade is engaged, derived from the sword's own
///     markers and its own collision mesh — with no contact model anywhere in the path.
/// </summary>
/// <remarks>
///     <para>
///         <b>READ-ONLY. This component writes nothing</b> — no joint, no drive, no body, no transform,
///         no material, no collider enabled state. It is an instrument and must stay one.
///     </para>
///     <para>
///         <b>Why it exists.</b> The contact-side readout that named the current EdgeBind pop-through is
///         computed by the BladeContact package, so it exists only on the three OurSolution stations and
///         it is the representation under evaluation. Using it as a run-validity signal is circular —
///         <c>CH4_MeasurementContract.md</c> §4, <i>the measurement apparatus must not change when the
///         contact representation changes</i>. This component answers the same question from passive
///         geometry that is present identically on all ten stations.
///     </para>
///     <para>
///         <b>What it reads, and nothing else.</b> Two quillon markers, one tip marker, and the long axis
///         of the declared contact collider's mesh. No profile asset, no shell, no solver, no classifier,
///         no latch. Per <c>DIAGNOSTIC_ContactSide_NonOurSolutionStations.md</c> §5 this file must not
///         name a <c>BladeContact.Runtime</c> type and must not <c>using BladeContact</c>. That boundary
///         is <b>review-enforced, not compiler-enforced</b> — <c>Assets/P3T/Scripts/</c> has no asmdef, so
///         the package is auto-referenced here and such a reference would compile silently.
///     </para>
///     <para>
///         <b>The evidence that the route works.</b> Measured live 2026-08-25 on all six OurSolution
///         swords: the marker-derived frame reproduces the authored profile at <c>edge·edge = 1.0000</c>
///         and <c>face·face = 0.9998</c>, both positive. The 0.0002 on face is ~1.1°, arising from the
///         mesh long axis differing slightly from the authored blade axis. It can only change a sign test
///         where the dot is already near zero, which is why <see cref="AFaceDot" /> is reported rather
///         than only its sign.
///     </para>
///     <para>
///         <b>Which edge is sharp is NOT geometrically recoverable and is never inferred.</b> The
///         <c>Blade</c> collider is a symmetric 5 x 45 mm slab and both QuickDirty hulls are centred on
///         it. <c>e+</c> is an authoring fact carried as a declaration: the quillon marker on the same
///         side as the sharp edge — Orange on A, Yellow on B (§3.2). <c>f+</c> is the side the tip marker
///         sits on (§3.3). Both arrive as serialized names, not as a table this component invents.
///     </para>
///     <para>
///         <b>Markers are selected by colour NAME, never by enumeration order.</b>
///         <c>GetComponentsInChildren</c> order is not guaranteed stable across a rebuild; if it changed,
///         <c>e+</c> would silently invert and the pop-through indicator would invert with it, which is
///         worse than not having one. A duplicate or missing marker name is a refusal, not a fallback.
///     </para>
///     <para>
///         <b>The blade axis must not come from the tip marker.</b> The tip marker is the thing carrying
///         the face offset, so deriving the axis from it drives that offset to zero by construction and
///         the face sign becomes undefined. The axis is the mesh's own long axis; the tip marker only
///         fixes its <i>sign</i>. Measured: taking the long axis unsigned yields the face offset as
///         -21.14 mm, and signing it toward the tip yields +21.14 mm and agreement with the profile.
///     </para>
///     <para>
///         <b>Pose comes from the PROXY BODY, never the authored transform.</b> On a worn sword Ragdoll
///         Animator disables the authored colliders, makes the authored body kinematic with
///         <c>detectCollisions = false</c>, and generates <c>&lt;name&gt;:Attachable Physics</c> copies
///         under the ragdoll. The authored object is a rendered follower. Marker positions are read from
///         the authored hierarchy — the proxy carries only colliders, verified live: the generated list
///         holds <c>Blade</c>, <c>Crossguard</c> and the QuickDirty hulls, and <b>no markers</b> — but
///         they are reduced to body-local offsets at resolve and re-posed by the proxy body every step.
///         That the two frames are interchangeable is <b>checked, not assumed</b>: see
///         <see cref="frameAgreementToleranceMm" />.
///     </para>
///     <para>
///         <b>One frame mismatch is inherited deliberately.</b> The contact point comes from
///         <c>Collider.ClosestPoint</c>, which evaluates at the collider's <i>transform</i> pose, while
///         the origin and axes come from the body pose. Under interpolation those differ — measured
///         1.5-1.7 mm on this rig. The existing readout has the same mismatch, and matching it is a
///         precondition of the agreement validation, so it is reproduced rather than corrected.
///         <see cref="ARenderLagMm" /> reports it every step so it is visible rather than hidden.
///     </para>
///     <para>
///         <b>Full inversion only.</b> Both sides flipping means each blade is engaging its opposite edge,
///         which is what a pass-through looks like. One side changing is the contact travelling around
///         the blade — ordinary slide, present in valid runs. Criterion inherited unchanged from MBC
///         <c>782c0d7</c>. <see cref="Inverted" /> is false until <see cref="declaredReference" /> is set,
///         because which state is correct is a presentation fact and not this component's to invent.
///     </para>
///     <para>
///         <b>Agreement with the package readout, and the one place it breaks.</b> Measured 2026-08-26
///         over 4338 physics steps at the three OurSolution stations, sampling both in the same instant:
///         <b>0 disagreements</b> at EdgeBind and Flat, and <b>14 at EdgeNoBind — every one of them with
///         the contact point within 0.0006 mm of the blade's own centreline</b>. Sub-micron from the
///         decision boundary the two routes land on opposite sides of a sign, because their edge axes
///         agree to 1.0000 but are not bit-identical. That is an <b>undetermined</b> reading, not a wrong
///         one, and the package readout is equally undetermined there. It is why
///         <see cref="AAcrossEdgeMetres" /> is exposed beside <see cref="AEdgePlus" />: a consumer that
///         cares must look at the margin. <b>No margin threshold is set here</b> — that is the
///         researcher's to declare.
///     </para>
///     <para>
///         <b>It is an INDICATOR, not a detector.</b> It reports that the engaged pairing departed from a
///         declared state. It measures no penetration and must never be turned into a claim about whether
///         the blades met. Whether it catches every pop-through is not established.
///     </para>
///     <para>
///         <b>THIS IS A RUN-VALIDITY GATE. Checking it is mandatory.</b> Declared 2026-08-26, and written
///         into <c>CH4_MeasurementContract.md</c> §4.1:
///     </para>
///     <para>
///         <i>Any tool that evaluates a station beyond the approach phase — HOLD dwell, Contact or Drive
///         — MUST read the contact-side value under normal circumstances. RED means an INVALID STATE, and
///         a red pass is NOT counted as a valid pass.</i>
///     </para>
///     <para>
///         "Red" is <see cref="Inverted" /> — <b>both</b> sides departed from
///         <see cref="declaredReference" />. <see cref="OneSideChanged" /> is NOT a gate: one side moving
///         is the contact travelling around the blade and happens in valid runs. Approach is exempt
///         because the blades are still closing and the pairing is not yet meaningful.
///     </para>
///     <para>
///         <b>The inverted state is a BOUND LIMITATION and is not to be investigated.</b> It occurs; which
///         arm it appears on varies between sessions rather than belonging to one station; the sign, the
///         Scene-view annotation and this probe agree in every observed case, so it is the rig and not the
///         instrument. <b>The only sanctioned response is additional passes to grow the valid set</b> — not
///         a root-cause hunt, not solver tuning against it, and not a mechanism to suppress it. Declared,
///         detected, excluded, reported.
///     </para>
///     <para>
///         This is the one component in <c>Probes/</c> that is a gate. The folder rule scoping everything
///         here as tier-2 still holds for the rest; this one is outside it because it is
///         representation-independent, which is the property that rule exists to protect.
///     </para>
/// </remarks>
[DisallowMultipleComponent]
public sealed class RqContactSideProbe : MonoBehaviour
{
    /// <summary>Which term the label leads with. A per-station presentation fact, never inferred here.</summary>
    public enum ContactTerm
    {
        Edge,
        Face
    }

    [Header("The two swords")]
    [Tooltip("Sword A's attachable. The probe reaches the generated proxy through it and reads the " +
             "markers from its authored hierarchy. Nothing else about the station is needed.")]
    [SerializeField] private RA2AttachableObject swordA;

    [Tooltip("Sword B's attachable.")]
    [SerializeField] private RA2AttachableObject swordB;

    [Header("Which collider carries the contact")]
    [Tooltip("DECLARED per station, not inferred. 'Blade' for PhysX and OurSolution; 'QuickEdgeHull' " +
             "for an edge QuickDirty station; 'QuickFlatHull' for a flat one — each Quick hull is the " +
             "surface that protrudes in its own presentation.\n\n" +
             "Matched against the AUTHORED collider name. The generated copy is found by list index, " +
             "the same correspondence Ragdoll Animator builds it with, so two shapes sharing a name " +
             "cannot cross-match.")]
    [SerializeField] private string contactColliderName = "Blade";

    [Tooltip("FURTHER colliders on the same blade whose closest approach is also evaluated. Leave EMPTY " +
             "to probe only the collider named above, which is the correct configuration for a PhysX or " +
             "OurSolution blade — they present one hull.\n\n" +
             "A QUICKDIRTY blade presents TWO live hulls and which one carries the contact is that " +
             "station's own semantic signal: it is the difference between 'an edge is involved' (20.6 N) " +
             "and 'no edge is involved' (11.4 N), which is the only distinction QuickDirty makes. " +
             "Declaring one hull makes the other invisible — a contact on the undeclared hull gets " +
             "reported at the declared hull's surface instead, which is fiction. So on QuickDirty declare " +
             "one hull above and the other here.\n\n" +
             "The collider named ABOVE is always the FRAME source: the axes come from its mesh long axis " +
             "and are not re-derived per hull. On this rig both Quick hulls are centred on the same Blade " +
             "slab, so the frame is common to them; the probe asserts that rather than assuming it.\n\n" +
             "Cost scales as the PRODUCT of the two sides' lists, since every cross pair is evaluated.")]
    [SerializeField] private List<string> additionalContactColliderNames = new List<string>();

    [Header("Marker names — the authoring facts this probe cannot see")]
    [Tooltip("A's quillon on the SHARP-EDGE side. This is what defines e+ on A; it is a declaration, " +
             "because a symmetric slab carries no geometric trace of which edge is sharp.")]
    [SerializeField] private string aQuillonPlus = "Marker_Quillon_Orange";

    [Tooltip("A's quillon on the opposite side, e-.")]
    [SerializeField] private string aQuillonMinus = "Marker_Quillon_Purple";

    [Tooltip("A's tip marker. Signs the blade axis, and through it the face normal. Sits ~18.6 mm proud " +
             "of the blade surface — a direction indicator, never a point on the blade.")]
    [SerializeField] private string aTip = "Marker_Tip_Pink";

    [Tooltip("B's quillon on the sharp-edge side, e+.")]
    [SerializeField] private string bQuillonPlus = "Marker_Quillon_Yellow";

    [Tooltip("B's quillon on the opposite side, e-.")]
    [SerializeField] private string bQuillonMinus = "Marker_Quillon_Beige";

    [Tooltip("B's tip marker.")]
    [SerializeField] private string bTip = "Marker_Tip_Blue";

    [Header("Reporting")]
    [Tooltip("Which term leads the reported line. Edge — which EDGE is engaged. Face — which broad FACE " +
             "is presented. BOTH are always computed and both are exposed; this only picks the lead.\n\n" +
             "IT DOES NOT DECIDE WHAT IS VALIDATED. The declared reference names its own term — 'Ae+' " +
             "checks the edge pair, 'Af-' checks the face pair — and the check uses that term whatever " +
             "leads here. A station may lead with the face and be validated on the edge.")]
    [SerializeField] private ContactTerm term = ContactTerm.Edge;

    [Tooltip("The DECLARED valid, non-crossed state for this station, e.g. \"Ae+ / Be+\" or \"Af- / Bf-\".\n\n" +
             "Left EMPTY the line is reported without a verdict and Inverted stays false. Nothing is " +
             "inferred and no default is supplied: which state is correct is a presentation fact and is " +
             "the researcher's to declare.")]
    [SerializeField] private string declaredReference = "";

    [Header("Self-checks — apparatus consistency, NOT experimental criteria")]
    [Tooltip("How far the proxy's collider may sit from the authored collider, expressed in each one's " +
             "own root frame, before the probe refuses. This asserts that the two frames are " +
             "interchangeable, which is what lets an authored-side marker offset be re-posed by the " +
             "proxy body. It is an internal consistency assert and is never a run criterion.")]
    [SerializeField] private float frameAgreementToleranceMm = 1f;

    [Tooltip("Degrees of orientation disagreement allowed by the same assert.")]
    [SerializeField] private float frameAgreementToleranceDeg = 1f;

    [Tooltip("Log the refusal reason once when the probe refuses. The refusal is always readable from " +
             "RefusalReason whether or not this is on.")]
    [SerializeField] private bool logRefusal = true;

    /// <summary>One side's resolved, pose-independent geometry and its live world frame.</summary>
    private sealed class Side
    {
        public RA2AttachableObject Attachable;
        public Rigidbody Body;
        public Transform BodyTransform;
        public Collider ProxyCollider;

        /// <summary>
        ///     Every proxy collider whose closest approach is evaluated on this side, with its name.
        /// </summary>
        /// <remarks>
        ///     <b>Why a list and not one.</b> A QuickDirty blade presents TWO live hulls, and which of
        ///     them carries the contact is that station's own semantic signal — it is the difference
        ///     between "an edge is involved" and "no edge is involved", which is the only thing
        ///     QuickDirty discriminates. Declaring one hull makes the other invisible, so a contact on
        ///     the undeclared hull is reported at the declared one's surface instead, which is fiction.
        ///     <see cref="ProxyCollider" /> remains the FRAME source and is always entry 0.
        /// </remarks>
        public readonly List<Collider> ProbeColliders = new List<Collider>(3);
        public readonly List<string> ProbeColliderNames = new List<string>(3);

        // Body-local and therefore pose-independent. Re-posed by the body every step.
        public Vector3 OriginLocal;
        public Vector3 EdgeLocal;
        public Vector3 BladeLocal;
        public Vector3 FaceLocal;

        // Recorded at resolve so a reader can check the frame rather than trust it.
        public float QuillonSpanMm;
        public float TipFaceOffsetMm;
        public float EdgeBladeOrthogonalityDot;
        public float TipAlongBladeMetres;
        public int EnabledProxyColliders;

        public Vector3 Origin;
        public Vector3 Edge;
        public Vector3 Blade;
        public Vector3 Face;
    }

    private readonly Side a = new Side();
    private readonly Side b = new Side();
    private readonly StringBuilder line = new StringBuilder(160);
    private readonly List<Transform> markerScratch = new List<Transform>(4);

    private bool resolved;
    private bool refused;
    private bool refusalLogged;
    private string refusalReason = "";
    private bool hasReading;

    /// <summary>True once both proxies exist and every self-check passed.</summary>
    public bool Resolved => resolved;

    /// <summary>True when an assumption did not hold. A refused probe reports nothing, ever.</summary>
    public bool Refused => refused;

    public string RefusalReason => refusalReason;

    /// <summary>True when a reading was produced on the most recent physics step.</summary>
    public bool HasReading => hasReading;

    /// <summary>Contact on A's declared sharp-edge side. Meaningless unless <see cref="HasReading" />.</summary>
    public bool AEdgePlus { get; private set; }

    public bool BEdgePlus { get; private set; }

    /// <summary>
    ///     A's face normal against the direction of B. The sign is the face side; the magnitude is how
    ///     well determined that side is, and a near-zero magnitude means the term is weakly determined.
    /// </summary>
    public float AFaceDot { get; private set; }

    public float BFaceDot { get; private set; }

    /// <summary>
    ///     Colour name of the quillon on the edge A is currently engaging, e.g. "Orange". Taken from the
    ///     declared marker names, so it needs no profile asset and reads the same on every station.
    /// </summary>
    public string AEngagedQuillon => Colour(AEdgePlus ? aQuillonPlus : aQuillonMinus);

    public string BEngagedQuillon => Colour(BEdgePlus ? bQuillonPlus : bQuillonMinus);

    private static string Colour(string markerName) =>
        string.IsNullOrEmpty(markerName) ? "?"
        : markerName.StartsWith(QuillonPrefix) ? markerName.Substring(QuillonPrefix.Length)
        : markerName;

    private const string QuillonPrefix = "Marker_Quillon_";

    /// <summary>e.g. "Ae+ / Be+".</summary>
    public string EdgePair { get; private set; } = "";

    /// <summary>e.g. "Af- / Bf-".</summary>
    public string FacePair { get; private set; } = "";

    /// <summary>
    ///     The pair for the DISPLAY term — what leads the line, the gizmo and the station sign.
    /// </summary>
    /// <remarks>
    ///     <b>Not what the verdict is taken against.</b> The verdict uses the term the declaration
    ///     names, which may be the other one — see <see cref="VerdictTerm" />. Both pairs are always
    ///     computed and both are always exposed; nothing downstream recomputes either.
    /// </remarks>
    public string LeadPair => term == ContactTerm.Edge ? EdgePair : FacePair;

    /// <summary>
    ///     Which term the DECLARATION validated on the most recent reading: "edge", "face" or "none".
    /// </summary>
    /// <remarks>
    ///     Taken from the declared reference's own letters, never from <see cref="term" />. Display and
    ///     validation are separate jobs: the term field chooses what leads the line, the declaration
    ///     chooses what is checked. A station may lead with the face and be validated on the edge.
    /// </remarks>
    public string VerdictTerm { get; private set; } = "none";

    /// <summary>Whether a reference was declared at all. False means <see cref="Inverted" /> says nothing.</summary>
    public bool HasDeclaredReference => !string.IsNullOrEmpty(declaredReference);

    public string DeclaredReference => declaredReference;

    /// <summary>
    ///     BOTH sides departed from the declared pair — the pass-through signature. False when no
    ///     reference is declared, and false for a one-sided change, which is ordinary slide.
    /// </summary>
    public bool Inverted { get; private set; }

    /// <summary>One side differs from the declared pair. Displayed, never treated as a pass-through.</summary>
    public bool OneSideChanged { get; private set; }

    /// <summary>
    ///     Contact position along A's OWN blade axis from its quillon midpoint, metres, signed toward the
    ///     tip. The second representation-independent validity criterion in
    ///     <c>CH4_MeasurementContract.md</c> §4 — whether contact is established in the intended region.
    /// </summary>
    public float AAlongBladeMetres { get; private set; }

    public float BAlongBladeMetres { get; private set; }

    /// <summary>
    ///     Where blade A's TIP sits along its own axis, metres, from the same origin
    ///     <see cref="AAlongBladeMetres" /> is measured from. Constant once resolved.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The blade occupies <c>[0, this]</c> along its own axis: zero is the quillon midpoint and
    ///         this is the tip. It exists so a contact coordinate can be placed against the blade's
    ///         EXTREMITIES instead of being read as a bare number.
    ///     </para>
    ///     <para>
    ///         <b>Why that matters and is not cosmetic.</b> "Contact was lost" is ambiguous between the
    ///         blades sliding off each other's ends and the blades passing through one another, and both
    ///         report the same release. Neither a release count nor a contact-side sign can separate
    ///         them — a sign taken after separation is meaningless, because the closest-approach query
    ///         still returns a pair when the blades are hundreds of millimetres apart. The only thing
    ///         that can distinguish them is WHERE along each blade the contact sat when it was lost, and
    ///         that needs the ends to be stated.
    ///     </para>
    ///     <para>
    ///         No threshold for "at the end" is defined here or anywhere. The extremity is reported; what
    ///         counts as near it is not this probe's to decide.
    ///     </para>
    /// </remarks>
    public float ATipAlongBladeMetres => a.TipAlongBladeMetres;

    /// <summary>Where blade B's tip sits along its own axis. See <see cref="ATipAlongBladeMetres" />.</summary>
    public float BTipAlongBladeMetres => b.TipAlongBladeMetres;

    /// <summary>
    ///     Which of A's declared hulls carried the closest approach on this step.
    /// </summary>
    /// <remarks>
    ///     Empty until a reading exists. On a single-hull blade this is always the declared frame
    ///     collider and carries no information. On a QUICKDIRTY blade it is the station's own semantic
    ///     signal: QuickDirty separates "an edge is involved" from "no edge is involved" and nothing
    ///     else, so which hull is engaged IS the classification that station makes. It is reported, and
    ///     it is not turned into a scenario label here.
    /// </remarks>
    public string AEngagedCollider { get; private set; } = "";

    /// <summary>Which of B's declared hulls carried the closest approach. See <see cref="AEngagedCollider" />.</summary>
    public string BEngagedCollider { get; private set; } = "";

    /// <summary>How many hulls are being probed on A. 1 on PhysX and OurSolution, 2 on QuickDirty.</summary>
    public int AProbedColliders => a.ProbeColliders.Count;

    /// <summary>How many hulls are being probed on B.</summary>
    public int BProbedColliders => b.ProbeColliders.Count;

    /// <summary>
    ///     Contact position ACROSS A's own edge axis from its quillon midpoint, metres. This is the
    ///     quantity <see cref="AEdgePlus" /> is the sign of, and it spans only the blade's half-width —
    ///     about 22.5 mm each way. Exposed for the same reason <see cref="AFaceDot" /> is: a reading a
    ///     few microns from the centreline is a coin toss, and hiding that behind a bare sign would let
    ///     an undetermined answer read as a determined one.
    /// </summary>
    public float AAcrossEdgeMetres { get; private set; }

    public float BAcrossEdgeMetres { get; private set; }

    /// <summary>
    ///     The closest-approach point found on A's declared collider, world space. Exposed so a display
    ///     can be placed where the reading was taken rather than somewhere convenient.
    /// </summary>
    /// <remarks>
    ///     <b>This is not a contact point and not a penetration.</b> It is where two
    ///     <c>Collider.ClosestPoint</c> queries converged, which is a surface pair whether or not the
    ///     blades touch. Never turn the distance between <see cref="AContactPoint" /> and
    ///     <see cref="BContactPoint" /> into a claim that the blades did or did not meet.
    /// </remarks>
    public Vector3 AContactPoint { get; private set; }

    public Vector3 BContactPoint { get; private set; }

    /// <summary>Midpoint of the closest-approach pair, world space. Where a label belongs.</summary>
    public Vector3 ContactMidpoint => (AContactPoint + BContactPoint) * 0.5f;

    /// <summary>Separation of the closest-approach pair, mm. Context for the reading, never a contact claim.</summary>
    public float ClosestApproachMm { get; private set; }

    /// <summary>Gap between A's rendered and simulated pose, mm. Reports the frame mismatch, never corrects it.</summary>
    public float ARenderLagMm { get; private set; }

    public float BRenderLagMm { get; private set; }

    /// <summary>
    ///     Enabled colliders the proxy presents, counting every generated shape and not only blade ones.
    ///     Measured live 2026-08-26 on a QuickDirty station sword: <b>3</b> — <c>QuickEdgeHull</c>,
    ///     <c>QuickFlatHull</c> and <c>Crossguard</c>, with the original <c>Blade</c> hull correctly
    ///     DISABLED. <b>4 would mean the original hull is live</b>, which collapses the two derived
    ///     hulls' friction back to one coefficient. A PhysX or OurSolution sword reads 2.
    /// </summary>
    public int AEnabledProxyColliders => a.EnabledProxyColliders;

    public int BEnabledProxyColliders => b.EnabledProxyColliders;

    /// <summary>
    ///     One blade's live, marker-derived orthonormal frame, exposed read-only so another instrument can
    ///     express a world point in blade-local terms WITHOUT rebuilding the frame.
    /// </summary>
    /// <remarks>
    ///     There is one frame calculation in this project and nothing recomputes it. A second derivation
    ///     would be a second number that can disagree with the first, which is the failure this probe's
    ///     whole design avoids. Consumers take the frame from here or they do not have it.
    /// </remarks>
    public readonly struct BladeFrame
    {
        /// <summary>True only when the owning probe has resolved and produced a reading this step.</summary>
        public readonly bool Valid;

        /// <summary>Frame origin in world space — the quillon-derived blade origin.</summary>
        public readonly Vector3 Origin;

        /// <summary>Unit vector across the blade, quillon to quillon. Positive toward the "+" quillon.</summary>
        public readonly Vector3 Edge;

        /// <summary>Unit vector along the blade, origin toward the tip.</summary>
        public readonly Vector3 Blade;

        /// <summary>Unit vector normal to the blade face.</summary>
        public readonly Vector3 Face;

        /// <summary>Distance from the frame origin to the tip marker, metres. The blade-axis extent.</summary>
        public readonly float TipAlongBladeMetres;

        internal BladeFrame(bool valid, Vector3 origin, Vector3 edge, Vector3 blade, Vector3 face, float tip)
        {
            Valid = valid;
            Origin = origin;
            Edge = edge;
            Blade = blade;
            Face = face;
            TipAlongBladeMetres = tip;
        }

        /// <summary>
        ///     A world point in this blade's own frame: x across the edge, y along the blade, z through the
        ///     face, all metres, all signed in the frame's declared directions.
        /// </summary>
        public Vector3 ToLocal(Vector3 world)
        {
            var d = world - Origin;
            return new Vector3(Vector3.Dot(d, Edge), Vector3.Dot(d, Blade), Vector3.Dot(d, Face));
        }
    }

    /// <summary>Blade A's live frame. <see cref="BladeFrame.Valid" /> is false unless a reading exists.</summary>
    public BladeFrame FrameA =>
        new BladeFrame(resolved && hasReading, a.Origin, a.Edge, a.Blade, a.Face, a.TipAlongBladeMetres);

    /// <summary>Blade B's live frame. <see cref="BladeFrame.Valid" /> is false unless a reading exists.</summary>
    public BladeFrame FrameB =>
        new BladeFrame(resolved && hasReading, b.Origin, b.Edge, b.Blade, b.Face, b.TipAlongBladeMetres);

    /// <summary>The formatted line, in the same shape the existing station readout prints.</summary>
    public string Line { get; private set; } = "";

    private void FixedUpdate()
    {
        Sample();
    }

    /// <summary>
    ///     Takes a reading NOW, at the caller's instant, and reports whether one was produced.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Why an instrument needs this and not only a cached value.</b> The pop-through pre-gate
    ///         asks for the contact side at one specific instant — the Drive transition, before the zero
    ///         is latched. Reading whatever this component's own <c>FixedUpdate</c> last stored answers at
    ///         whatever point in the script execution order that happened to run, which is not the
    ///         caller's instant and is not ordered against whatever moved the blades. Measured on this
    ///         rig: comparing a cached reading against a reference computed one phase later produced
    ///         alternating single-tick disagreements that were entirely an artefact of the sampling
    ///         phase.
    ///     </para>
    ///     <para>
    ///         Idempotent within a step and cheap — calling it more than once recomputes the same answer.
    ///         Still read-only.
    ///     </para>
    /// </remarks>
    public bool Sample()
    {
        if (refused) return false;

        if (!resolved)
        {
            // The proxy does not exist until the sword is worn, so this cannot be done in Awake. A
            // missing proxy is "not yet", not a failure; a broken assumption is a refusal.
            if (!TryResolve()) return false;
            resolved = true;
        }

        Read();
        return hasReading;
    }

    /// <summary>One line stating what this probe read its frame from, for a run's declaration block.</summary>
    public string DeclarationLine()
    {
        if (refused) return "ContactSideProbe REFUSED: " + refusalReason;
        if (!resolved) return "ContactSideProbe: not resolved (no proxy yet)";

        return "ContactSideProbe: collider '" + contactColliderName +
               "'  e+ A=" + aQuillonPlus + " B=" + bQuillonPlus +
               "  quillon span A=" + a.QuillonSpanMm.ToString("F1") + "mm B=" +
               b.QuillonSpanMm.ToString("F1") + "mm" +
               "  tip face offset A=" + a.TipFaceOffsetMm.ToString("F2") + "mm B=" +
               b.TipFaceOffsetMm.ToString("F2") + "mm" +
               "  |edge.blade| A=" + a.EdgeBladeOrthogonalityDot.ToString("F4") + " B=" +
               b.EdgeBladeOrthogonalityDot.ToString("F4") +
               "  hulls probed A=" + Join(a.ProbeColliderNames) + " B=" + Join(b.ProbeColliderNames) +
               "  blade span along axis A=[0," + (a.TipAlongBladeMetres * 1000f).ToString("F1") +
               "] B=[0," + (b.TipAlongBladeMetres * 1000f).ToString("F1") + "] mm" +
               "  enabled proxy colliders A=" + a.EnabledProxyColliders + " B=" + b.EnabledProxyColliders +
               "  reference " + (HasDeclaredReference ? declaredReference : "NOT DECLARED");
    }

    // ---------------------------------------------------------------------------------------------
    // Reading
    // ---------------------------------------------------------------------------------------------

    private void Read()
    {
        hasReading = false;

        if (a.Body == null || b.Body == null || a.ProxyCollider == null || b.ProxyCollider == null)
        {
            // The sword was put down, or the ragdoll was rebuilt. Re-resolve rather than report against
            // a destroyed proxy.
            resolved = false;
            return;
        }

        Pose(a);
        Pose(b);

        // Closest approach, over EVERY declared hull pair. ClosestPoint returns the query point unchanged
        // when it is inside, so each pair converges to a surface pair and CANNOT report overlap. It
        // locates the contact; it is never a separation claim.
        //
        // The pair reported is the CLOSEST one, and which hulls it was is reported with it. On a
        // single-hull blade there is one pair and this is identical to what it always did. On QuickDirty
        // there are four, and which hull pair wins is that station's own semantic signal.
        Vector3 pA = Vector3.zero, pB = Vector3.zero;
        float best = float.MaxValue;
        AEngagedCollider = "";
        BEngagedCollider = "";

        for (int i = 0; i < a.ProbeColliders.Count; i++)
        for (int j = 0; j < b.ProbeColliders.Count; j++)
        {
            Collider ca = a.ProbeColliders[i], cb = b.ProbeColliders[j];
            if (ca == null || cb == null) continue;

            Vector3 qA = ca.bounds.center, qB = cb.bounds.center;
            for (int k = 0; k < 10; k++)
            {
                qA = ca.ClosestPoint(qB);
                qB = cb.ClosestPoint(qA);
            }

            float d = Vector3.Distance(qA, qB);
            if (d >= best) continue;

            best = d;
            pA = qA;
            pB = qB;
            AEngagedCollider = a.ProbeColliderNames[i];
            BEngagedCollider = b.ProbeColliderNames[j];
        }

        if (best == float.MaxValue)
        {
            // Every declared hull went null between resolve and now, which means the proxy was rebuilt.
            resolved = false;
            return;
        }

        AContactPoint = pA;
        BContactPoint = pB;
        ClosestApproachMm = best * 1000f;

        // Edge: where on MY OWN blade, across its width, the contact sits.
        AAcrossEdgeMetres = Vector3.Dot(pA - a.Origin, a.Edge);
        BAcrossEdgeMetres = Vector3.Dot(pB - b.Origin, b.Edge);
        AEdgePlus = AAcrossEdgeMetres >= 0f;
        BEdgePlus = BAcrossEdgeMetres >= 0f;

        // Face: which way MY OWN face normal points relative to the other blade.
        AFaceDot = Vector3.Dot((pB - pA).normalized, a.Face);
        BFaceDot = Vector3.Dot((pA - pB).normalized, b.Face);

        AAlongBladeMetres = Vector3.Dot(pA - a.Origin, a.Blade);
        BAlongBladeMetres = Vector3.Dot(pB - b.Origin, b.Blade);

        ARenderLagMm = Vector3.Distance(a.Body.position, a.BodyTransform.position) * 1000f;
        BRenderLagMm = Vector3.Distance(b.Body.position, b.BodyTransform.position) * 1000f;

        EdgePair = "Ae" + Sign(AEdgePlus) + " / Be" + Sign(BEdgePlus);
        FacePair = "Af" + Sign(AFaceDot >= 0f) + " / Bf" + Sign(BFaceDot >= 0f);

        Verdict();
        if (refused) return;

        Format();
        hasReading = true;
    }

    private static void Pose(Side s)
    {
        Vector3 p = s.Body.position;
        Quaternion r = s.Body.rotation;

        s.Origin = p + r * s.OriginLocal;
        s.Edge = r * s.EdgeLocal;
        s.Blade = r * s.BladeLocal;
        s.Face = r * s.FaceLocal;
    }

    /// <summary>
    ///     Compares the leading pair against the declared one, per side. Only a full inversion is a
    ///     pass-through; a single side moving is slide along the blade.
    /// </summary>
    private void Verdict()
    {
        Inverted = false;
        OneSideChanged = false;

        VerdictTerm = "none";
        if (!HasDeclaredReference) return;

        string declared = Strip(declaredReference);

        int dSlash = declared.IndexOf('/');
        if (dSlash < 1 || declared.Length < dSlash + 3)
        {
            Refuse("declaredReference '" + declaredReference + "' is not of the form 'Ae+ / Be+'.");
            return;
        }

        // WHICH TERM IS BEING VALIDATED COMES FROM THE DECLARATION, NOT FROM THE DISPLAY.
        //
        // The declared reference names its own term — 'Ae+' validates the EDGE pair, 'Af-' the FACE
        // pair — and this compares against that term whatever `term` happens to lead with. Those are
        // two separate jobs and entangling them was a live trap: this used to compare the declared
        // string against LeadPair directly, so declaring an edge reference on a station whose display
        // leads with face made BOTH sides differ on every step and reported a permanent inversion. The
        // station would have read as popped-through from the moment it was declared.
        //
        // A declaration is only ever used to decide correct-or-incorrect. It must not decide what is
        // shown, and what is shown must not decide what is validated.
        char termA = declared[1], termB = declared[dSlash + 2];
        if (termA != termB || (termA != 'e' && termA != 'f'))
        {
            Refuse("declaredReference '" + declaredReference + "' does not name one term on both sides. " +
                   "Use 'Ae+ / Be+' for the edge pair or 'Af- / Bf-' for the face pair — not a mix.");
            return;
        }

        VerdictTerm = termA == 'e' ? "edge" : "face";
        string observed = Strip(termA == 'e' ? EdgePair : FacePair);

        // The two sides are split at the '/' and compared separately. Comparing the whole string cannot
        // tell a one-sided change from a full inversion, and only the latter is a pass-through.
        int oSlash = observed.IndexOf('/');
        if (oSlash < 0)
        {
            Refuse("the computed pair '" + observed + "' is not of the form 'Ae+ / Be+'.");
            return;
        }

        bool aDiffers = declared.Substring(0, dSlash) != observed.Substring(0, oSlash);
        bool bDiffers = declared.Substring(dSlash + 1) != observed.Substring(oSlash + 1);

        Inverted = aDiffers && bDiffers;
        OneSideChanged = aDiffers ^ bDiffers;
    }

    private void Format()
    {
        line.Length = 0;

        bool edgeLeads = term == ContactTerm.Edge;
        string lead = edgeLeads ? EdgePair : FacePair;
        string other = edgeLeads ? FacePair : EdgePair;

        if (Inverted)
            line.Append("<color=#FF4040><b>").Append(lead).Append("</b>  (declared ")
                .Append(declaredReference).Append(")</color>");
        else
            line.Append("<b>").Append(lead).Append("</b>");

        if (OneSideChanged)
            line.Append("  <size=75%>(one side, declared ").Append(declaredReference).Append(")</size>");

        line.Append("  <size=75%>[").Append(other).Append("  face|dot| ")
            .Append(Mathf.Abs(AFaceDot).ToString("F2")).Append('/')
            .Append(Mathf.Abs(BFaceDot).ToString("F2")).Append("]</size>");

        Line = line.ToString();
    }

    private static string Sign(bool plus) => plus ? "+" : "-";

    private static string Join(List<string> names)
    {
        if (names == null || names.Count == 0) return "(none)";

        return string.Join("+", names.ToArray());
    }

    private static string Strip(string s) => s.Replace(" ", string.Empty);

    // ---------------------------------------------------------------------------------------------
    // Resolve
    // ---------------------------------------------------------------------------------------------

    private bool TryResolve()
    {
        if (swordA == null || swordB == null)
        {
            Refuse("swordA and swordB must both be assigned.");
            return false;
        }

        a.Attachable = swordA;
        b.Attachable = swordB;

        // "Not worn yet" returns false without refusing. Only a broken assumption refuses.
        return TryResolveSide(a, "A", aQuillonPlus, aQuillonMinus, aTip) &&
               TryResolveSide(b, "B", bQuillonPlus, bQuillonMinus, bTip);
    }

    private bool TryResolveSide(Side s, string label, string plusName, string minusName, string tipName)
    {
        RA2AttachableObject att = s.Attachable;
        Transform root = att.transform;

        List<Collider> authored = att.AttachableColliders;
        List<Collider> generated = att.GeneratedPhysicsColliders;

        if (att.GeneratedPhysicsObject == null || generated == null) return false; // not worn yet

        if (authored == null || authored.Count != generated.Count)
        {
            Refuse(label + ": Ragdoll Animator's generated collider list does not correspond to the " +
                   "authored one (" + (authored == null ? 0 : authored.Count) + " authored vs " +
                   generated.Count + " generated), so no proxy shape can be matched to a declared one.");
            return false;
        }

        // Authored collider by declared name, index-mapped to its generated copy: Ragdoll Animator
        // appends one copy per authored entry in order. Name-matching the proxy directly would
        // cross-match two shapes that share a name.
        Collider authoredCollider = null;
        int index = -1;
        for (int i = 0; i < authored.Count; i++)
        {
            if (authored[i] == null || authored[i].name != contactColliderName) continue;

            if (authoredCollider != null)
            {
                Refuse(label + ": more than one authored collider is named '" + contactColliderName +
                       "', so the declared contact collider is ambiguous.");
                return false;
            }

            authoredCollider = authored[i];
            index = i;
        }

        if (authoredCollider == null)
        {
            Refuse(label + ": no authored collider named '" + contactColliderName + "' in this " +
                   "attachable's AttachableColliders. Declared contact colliders are 'Blade' for PhysX " +
                   "and OurSolution, 'QuickEdgeHull' or 'QuickFlatHull' for QuickDirty.");
            return false;
        }

        Collider proxyCollider = generated[index];
        if (proxyCollider == null)
        {
            Refuse(label + ": the generated copy of '" + contactColliderName + "' is missing.");
            return false;
        }

        if (!proxyCollider.enabled)
        {
            Refuse(label + ": the proxy's '" + contactColliderName + "' is DISABLED, so it carries no " +
                   "contact and a pairing read from it would be fiction. On a QuickDirty blade the " +
                   "original 'Blade' hull is disabled deliberately — declare a Quick hull there instead.");
            return false;
        }

        var mesh = proxyCollider as MeshCollider;
        if (mesh == null || mesh.sharedMesh == null)
        {
            Refuse(label + ": '" + contactColliderName + "' is not a MeshCollider with a mesh, so it has " +
                   "no long axis to read.");
            return false;
        }

        if (!mesh.convex)
        {
            Refuse(label + ": '" + contactColliderName + "' is a non-convex MeshCollider. " +
                   "Collider.ClosestPoint is undefined on one.");
            return false;
        }

        Rigidbody body = att.lastRigidbody != null
            ? att.lastRigidbody
            : att.GeneratedPhysicsObject.GetComponent<Rigidbody>();

        if (body == null)
        {
            Refuse(label + ": the generated physics object carries no Rigidbody, so there is no simulated " +
                   "pose to measure. The authored transform must not be substituted — it is a kinematic " +
                   "rendered follower.");
            return false;
        }

        // Authored-side geometry may only be re-posed by the proxy body if the two frames carry the
        // collider identically. Checked, not assumed.
        if (!FramesAgree(label, root, authoredCollider.transform, body.transform, proxyCollider.transform))
            return false;

        if (!TryBuildFrame(s, label, root, authoredCollider, plusName, minusName, tipName)) return false;

        s.Body = body;
        s.BodyTransform = body.transform;
        s.ProxyCollider = proxyCollider;

        // The frame collider is always entry 0, so a single-hull blade behaves exactly as before and the
        // ten already-serialized instances are unaffected by this field existing.
        s.ProbeColliders.Clear();
        s.ProbeColliderNames.Clear();
        s.ProbeColliders.Add(proxyCollider);
        s.ProbeColliderNames.Add(contactColliderName);

        for (int n = 0; n < additionalContactColliderNames.Count; n++)
        {
            string extra = additionalContactColliderNames[n];
            if (string.IsNullOrEmpty(extra)) continue;

            if (s.ProbeColliderNames.Contains(extra))
            {
                Refuse(label + ": '" + extra + "' is declared more than once, so one hull would be " +
                       "counted twice and the closest-pair report would name an arbitrary one of them.");
                return false;
            }

            Collider extraAuthored, extraProxy;
            if (!TryDeclaredCollider(label, authored, generated, extra, out extraAuthored, out extraProxy))
                return false;

            // The frame is taken from entry 0 and applied to every probed hull, so a hull that does NOT
            // sit in the same body-local place on the proxy as it does on the authored object would be
            // measured in the wrong frame. Asserted, not assumed — on this rig both Quick hulls are
            // centred on the same Blade slab, and that is the premise this list rests on.
            if (!FramesAgree(label + " (" + extra + ")", root, extraAuthored.transform, body.transform,
                             extraProxy.transform))
                return false;

            s.ProbeColliders.Add(extraProxy);
            s.ProbeColliderNames.Add(extra);
        }

        s.EnabledProxyColliders = 0;
        for (int i = 0; i < generated.Count; i++)
            if (generated[i] != null && generated[i].enabled)
                s.EnabledProxyColliders++;

        return true;
    }

    /// <summary>
    ///     One declared collider by name, as its authored entry and the generated copy that carries it.
    /// </summary>
    /// <remarks>
    ///     Index-mapped, never name-matched on the proxy: Ragdoll Animator appends one copy per authored
    ///     entry in order, and matching the proxy by name would cross-match two shapes that share one.
    ///     Every failure here refuses rather than falling back, because a fallback would report a
    ///     different hull than the one declared and nothing would say so.
    /// </remarks>
    private bool TryDeclaredCollider(string label, List<Collider> authored, List<Collider> generated,
                                     string name, out Collider authoredCollider, out Collider proxy)
    {
        authoredCollider = null;
        proxy = null;
        int index = -1;

        for (int i = 0; i < authored.Count; i++)
        {
            if (authored[i] == null || authored[i].name != name) continue;

            if (authoredCollider != null)
            {
                Refuse(label + ": more than one authored collider is named '" + name + "', so the " +
                       "declared collider is ambiguous.");
                return false;
            }

            authoredCollider = authored[i];
            index = i;
        }

        if (authoredCollider == null)
        {
            Refuse(label + ": no authored collider named '" + name + "' in this attachable's " +
                   "AttachableColliders. Declared contact colliders are 'Blade' for PhysX and " +
                   "OurSolution, 'QuickEdgeHull' and 'QuickFlatHull' for QuickDirty.");
            return false;
        }

        proxy = generated[index];
        if (proxy == null)
        {
            Refuse(label + ": the generated copy of '" + name + "' is missing.");
            return false;
        }

        if (!proxy.enabled)
        {
            Refuse(label + ": the proxy's '" + name + "' is DISABLED, so it carries no contact and a " +
                   "pairing read from it would be fiction. On a QuickDirty blade the original 'Blade' " +
                   "hull is disabled deliberately — declare the Quick hulls there instead.");
            return false;
        }

        var mesh = proxy as MeshCollider;
        if (mesh == null || mesh.sharedMesh == null)
        {
            Refuse(label + ": '" + name + "' is not a MeshCollider with a mesh.");
            return false;
        }

        if (!mesh.convex)
        {
            Refuse(label + ": '" + name + "' is a non-convex MeshCollider. Collider.ClosestPoint is " +
                   "undefined on one.");
            return false;
        }

        return true;
    }

    /// <summary>
    ///     Confirms the proxy holds the declared collider in the same place, relative to its own root,
    ///     that the authored object does. This is what makes an authored-side marker offset valid against
    ///     the proxy body.
    /// </summary>
    private bool FramesAgree(string label, Transform authoredRoot, Transform authoredCollider,
                             Transform proxyRoot, Transform proxyCollider)
    {
        Vector3 authoredLocal = Quaternion.Inverse(authoredRoot.rotation) *
                                (authoredCollider.position - authoredRoot.position);
        Vector3 proxyLocal = Quaternion.Inverse(proxyRoot.rotation) *
                             (proxyCollider.position - proxyRoot.position);

        float offsetMm = Vector3.Distance(authoredLocal, proxyLocal) * 1000f;

        Quaternion authoredRotation = Quaternion.Inverse(authoredRoot.rotation) * authoredCollider.rotation;
        Quaternion proxyRotation = Quaternion.Inverse(proxyRoot.rotation) * proxyCollider.rotation;
        float offsetDeg = Quaternion.Angle(authoredRotation, proxyRotation);

        if (offsetMm <= frameAgreementToleranceMm && offsetDeg <= frameAgreementToleranceDeg) return true;

        Refuse(label + ": the proxy does not carry '" + contactColliderName + "' in the same body-local " +
               "place as the authored object (" + offsetMm.ToString("F2") + " mm, " +
               offsetDeg.ToString("F2") + " deg). Marker offsets read from the authored hierarchy would " +
               "not be valid against the proxy body, so no reading is produced.");
        return false;
    }

    /// <summary>
    ///     Reduces the sword's markers and its mesh long axis to four pose-independent body-local
    ///     quantities. Everything that can be checked is checked here, once.
    /// </summary>
    private bool TryBuildFrame(Side s, string label, Transform root, Collider authoredCollider,
                               string plusName, string minusName, string tipName)
    {
        Transform qPlus, qMinus, tip;
        if (!TryFindMarker(root, plusName, label, out qPlus)) return false;
        if (!TryFindMarker(root, minusName, label, out qMinus)) return false;
        if (!TryFindMarker(root, tipName, label, out tip)) return false;

        Quaternion toLocal = Quaternion.Inverse(root.rotation);
        Vector3 qPlusLocal = toLocal * (qPlus.position - root.position);
        Vector3 qMinusLocal = toLocal * (qMinus.position - root.position);
        Vector3 tipLocal = toLocal * (tip.position - root.position);

        Vector3 span = qPlusLocal - qMinusLocal;
        s.QuillonSpanMm = span.magnitude * 1000f;
        if (s.QuillonSpanMm < 1f)
        {
            Refuse(label + ": the two quillon markers '" + plusName + "' and '" + minusName + "' are " +
                   s.QuillonSpanMm.ToString("F3") + " mm apart, which fixes no edge direction.");
            return false;
        }

        s.OriginLocal = (qPlusLocal + qMinusLocal) * 0.5f;
        s.EdgeLocal = span.normalized;

        // Long axis from the MESH, never from the tip marker: the tip marker carries the face offset, so
        // deriving the axis from it would drive that offset to zero by construction.
        Vector3 bladeLocal;
        if (!TryMeshLongAxis(authoredCollider, label, out bladeLocal)) return false;
        bladeLocal = (toLocal * bladeLocal).normalized;

        // The tip marker fixes the SIGN of the axis, and only the sign.
        Vector3 tipOffset = tipLocal - s.OriginLocal;
        if (Vector3.Dot(tipOffset, bladeLocal) < 0f) bladeLocal = -bladeLocal;
        s.BladeLocal = bladeLocal;

        s.EdgeBladeOrthogonalityDot = Mathf.Abs(Vector3.Dot(s.EdgeLocal, s.BladeLocal));
        if (s.EdgeBladeOrthogonalityDot > 0.05f)
        {
            Refuse(label + ": the marker edge axis is not orthogonal to the mesh long axis (|dot| = " +
                   s.EdgeBladeOrthogonalityDot.ToString("F4") + "), so the two do not describe the same " +
                   "blade. Measured 0.0000 on all twenty station swords.");
            return false;
        }

        s.FaceLocal = Vector3.Cross(s.BladeLocal, s.EdgeLocal).normalized;

        // f+ is DECLARED as the side the tip marker sits on. Signing the blade axis toward the tip should
        // already have put the face normal there; if it has not, a stated premise is false, and a silent
        // sign flip is exactly the failure this probe exists to avoid.
        s.TipFaceOffsetMm = Vector3.Dot(tipOffset, s.FaceLocal) * 1000f;
        if (s.TipFaceOffsetMm <= 0f)
        {
            Refuse(label + ": the tip marker sits on the NEGATIVE face side (" +
                   s.TipFaceOffsetMm.ToString("F2") + " mm), contradicting the declaration that f+ is the " +
                   "side the tip marker is on. Measured +21.14 mm (A) / +21.13 mm (B) on all twenty " +
                   "station swords, so this means the markers or the mesh are not what they were.");
            return false;
        }

        // WHERE THE BLADE ENDS ARE, along the blade's own axis, from the same origin AAlongBladeMetres
        // is measured from — so a contact coordinate can be placed against the extremities rather than
        // read as a bare number.
        //
        // This exists because "contact was lost" is ambiguous between the blades SLIDING OFF each other's
        // ends and the blades PASSING THROUGH one another, and a released contact reports the same
        // release count either way. The discriminator is WHERE the contact sat when it was lost: at an
        // extremity, or mid-blade. Neither the count nor the contact-side sign can tell those apart, so
        // the extremity has to be a stated quantity.
        //
        // Zero is the quillon midpoint, by construction of OriginLocal. The blade axis is signed toward
        // the tip, so the blade occupies [0, TipAlongBladeMetres] and both ends are named.
        s.TipAlongBladeMetres = Vector3.Dot(tipOffset, s.BladeLocal);

        return true;
    }

    private bool TryFindMarker(Transform root, string markerName, string label, out Transform found)
    {
        found = null;
        markerScratch.Clear();

        // By NAME, never by enumeration order — an order change would silently invert e+ and with it the
        // pop-through indicator.
        foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            if (t.name == markerName)
                markerScratch.Add(t);

        if (markerScratch.Count == 1)
        {
            found = markerScratch[0];
            return true;
        }

        Refuse(markerScratch.Count == 0
            ? label + ": no child named '" + markerName + "'. Markers are present on all twenty station " +
              "swords; a missing one means this is not a standard sword."
            : label + ": " + markerScratch.Count + " children named '" + markerName + "', so the marker " +
              "is ambiguous and the edge sign would depend on enumeration order.");

        return false;
    }

    /// <summary>
    ///     The mesh's longest local axis, in world space. The blade meshes are authored on their own
    ///     principal axes — measured 1016 x 5 x 45 mm on A and 1010 x 5 x 45 mm on B — so the local
    ///     extents ARE the OBB extents. That the longest is unambiguous is checked, not assumed.
    /// </summary>
    private bool TryMeshLongAxis(Collider collider, string label, out Vector3 worldAxis)
    {
        worldAxis = Vector3.zero;

        Transform t = collider.transform;
        Mesh mesh = ((MeshCollider)collider).sharedMesh;
        Vector3 extents = Vector3.Scale(mesh.bounds.extents, t.lossyScale);

        float[] length = { Mathf.Abs(extents.x), Mathf.Abs(extents.y), Mathf.Abs(extents.z) };
        int longest = 0;
        for (int i = 1; i < 3; i++)
            if (length[i] > length[longest])
                longest = i;

        float second = 0f;
        for (int i = 0; i < 3; i++)
            if (i != longest && length[i] > second)
                second = length[i];

        if (length[longest] <= second * 2f)
        {
            Refuse(label + ": the mesh of '" + collider.name + "' has no unambiguous long axis (extents " +
                   (length[0] * 2000f).ToString("F1") + " / " + (length[1] * 2000f).ToString("F1") + " / " +
                   (length[2] * 2000f).ToString("F1") + " mm), so the blade axis cannot be read from it.");
            return false;
        }

        Vector3 local = longest == 0 ? Vector3.right : longest == 1 ? Vector3.up : Vector3.forward;
        worldAxis = t.TransformDirection(local).normalized;
        return true;
    }

    private void Refuse(string reason)
    {
        refused = true;
        resolved = false;
        hasReading = false;
        refusalReason = reason;
        Line = "ContactSideProbe REFUSED: " + reason;

        if (!logRefusal || refusalLogged) return;

        refusalLogged = true;
        Debug.LogWarning("[ContactSideProbe] " + name + ": " + reason, this);
    }
}
