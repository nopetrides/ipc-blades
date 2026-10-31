using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BladeContact;
using UnityEngine;

/// <summary>
///     Bind metrics, second generation. Built only on the AUTHORED shells that OurSolution actually
///     classifies against — never on the legacy geometry path.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why a V2 rather than reviving <c>BindMetrics</c>.</b> The Week 4/5 component predates authored
///         blade shells entirely: it calibrates its own guard/tip/quillon frame from sword transforms and
///         reasons about blades as whole bodies. Nothing in it knows what a <c>SharpEdge</c> is. Reviving it
///         would import a geometry model we have since replaced, and any agreement between the two would be
///         coincidence rather than corroboration.
///     </para>
///     <para>
///         <b>Q0 — the visual validation this exists for first.</b> Before any signed-side or opposition
///         measurement can mean anything, we have to know the instrument is pointing at the right geometry.
///         The authored cutting edge is not an approximation: it is a run of authored ridge segments with a
///         genuine best-fit line. This component fits that line, reports its equation, and draws it. A human
///         then confirms the drawn line lies along the visible cutting edge. No recording, no sweep, no
///         phase driving — it runs standalone with the animator on Auto.
///     </para>
///     <para>
///         <b>Two sharp edges per blade, not one.</b> Both specimens author TWO SharpEdge runs, rings
///         <c>e0</c> and <c>e6</c>, 103 segments each — these are double-edged blades. Drawing only one
///         would make the check ambiguous, because a line sitting on the other authored edge would look
///         like a failure when it is not. Every run is fitted and drawn, and each is labelled, so the check
///         can also see WHICH edge the contact is currently attributed to.
///     </para>
///     <para>
///         <b>Poses come from <see cref="BladePose" />, not from the Transform directly.</b>
///         <see cref="BladeShell.SimulatedPlacement" /> is position + rotation only, and that is the frame the
///         classifier's own queries are posed in. Drawing through the same construction is what makes this a
///         check on the classifier rather than a check on a parallel piece of maths.
///     </para>
///     <para>
///         Every visual is independently toggleable. This is deliberate: the lines are cheap but the clutter
///         is not, and a station under measurement should be able to show exactly one thing at a time.
///     </para>
/// </remarks>
public sealed class RqBindMetricsV2 : MonoBehaviour
{
    /// <summary>One authored sharp-edge run, and the line fitted through it.</summary>
    private sealed class EdgeRun
    {
        public string Ring;
        public int Segments;

        /// <summary>Least-squares line, in the blade's local space.</summary>
        public Vector3 LocalCentroid, LocalDirection;

        /// <summary>Extent of the authored run along the fitted direction, so the drawn line spans the
        /// real edge end to end rather than an arbitrary length.</summary>
        public float MinT, MaxT;

        /// <summary>How well a straight line actually describes the authored run, in metres.</summary>
        public float ResidualMax, ResidualRms;

        /// <summary>Authored wedge angle at this ridge, from the profile. Not measured from geometry.</summary>
        public float IncludedAngleMin, IncludedAngleMax;

        public LineRenderer Line;
    }

    private sealed class Side
    {
        public RqBladeContact Blade;
        public readonly List<EdgeRun> Runs = new List<EdgeRun>();
        public bool Built;
    }

    [Header("Blades")]
    [SerializeField] private RqBladeContact contactA;

    [SerializeField] private RqBladeContact contactB;

    [Tooltip("Optional. Only needed to highlight which edge the contact is currently attributed to and to " +
             "draw the witness. Everything else works without it.")]
    [SerializeField] private RqBladeTangentialSolver solver;

    [Header("Visuals — each independently toggleable")]
    [Tooltip("Q-1, a plain 'show me the cutting edges' aid: the best-fit line through each AUTHORED " +
             "SharpEdge run, over the extent of the run itself. This is chosen by us, not by the solver, " +
             "so it proves nothing about attribution — it is orientation only. Q0 is the separate toggle " +
             "below.")]
    [SerializeField] private bool drawSharpEdgeLines = true;

    [Tooltip("Q0: draw the feature OurSolution ACTUALLY ATTRIBUTED, from that feature's own two vertices, " +
             "extended along its own direction. Nothing about which feature is chosen here — the solver " +
             "chose it. If the attributed feature is a face rather than an edge, its triangle is outlined " +
             "instead, so 'not an edge' is visible at a glance rather than being drawn as if it were one.")]
    [SerializeField] private bool drawAttributedFeature = true;

    [Tooltip("How far past its own endpoints the attributed edge line is extended, metres each way. An " +
             "authored ring segment is only millimetres long and invisible at working distance; extending " +
             "along its OWN direction makes it visible without changing which line it is.")]
    [SerializeField] private float attributedExtend = 0.6f;

    [Tooltip("Restrict the edge lines to the ring the solver currently attributes the contact to. Off " +
             "draws every authored SharpEdge run, which is what Q0 wants.")]
    [SerializeField] private bool drawAttributedRingOnly;

    [Tooltip("The blade's authored long axis through the shell origin. Orientation aid only.")]
    [SerializeField] private bool drawLongAxis;

    [Tooltip("A short cross at each of the current attributed witness points. Requires the solver.")]
    [SerializeField] private bool drawContactWitness;

    [Tooltip("SCENE-VIEW GIZMO. The speculative contact envelope around each authored cutting edge, drawn " +
             "as rings of radius contactOffset perpendicular to the fitted edge line. This is not a " +
             "schematic: the set of points within distance d of a line IS a cylinder of radius d, so this " +
             "is the exact envelope for the ridge. It is also the answer to whether the skin preserves the " +
             "sharp edge — IT DOES NOT. contactOffset is a distance threshold, so the envelope is a " +
             "Minkowski sum with a sphere and every edge and corner comes out ROUNDED at that radius. To " +
             "speculative contact generation, a 25-degree apex and a blunt one are indistinguishable at " +
             "this scale.")]
    [SerializeField] private bool drawContactSkin;

    [Tooltip("Rings drawn along each edge for the envelope above. Presentation only.")]
    [Range(2, 24)]
    [SerializeField] private int contactSkinRings = 7;

    [Tooltip("SCENE-VIEW GIZMO. The legacy magenta box: the blade collider's own oriented bounding box, " +
             "inflated by contactOffset. Encloses the WHOLE blade, so it is much larger than the envelope " +
             "above. Useful for seeing at a glance whether the combined skin of both blades is wider than " +
             "the side margin they are supposed to maintain. It is a BOUND, not the envelope — a box has " +
             "corners the real envelope does not.")]
    [SerializeField] private bool drawContactSkinBox;

    [Tooltip("SCENE-VIEW GIZMO. Wireframe of the mesh PhysX actually collides against — the cooked convex " +
             "hull, not the authored shell. Worth comparing against the authored sharp-edge lines: where " +
             "they diverge is where the collider and the classifier disagree about the blade.")]
    [SerializeField] private bool drawColliderHull;

    [Tooltip("SCENE-VIEW GIZMO. The ENTIRE Minkowski envelope of the blade shell: EVERY authored face " +
             "offset outward by contactOffset, EVERY authored edge as a cylinder of that radius, and EVERY " +
             "vertex as a sphere of that radius. That union IS the exact contact-generation envelope. " +
             "WARNING: this is the whole shell — 2492 surfaces and 1236 edges per blade — so it is very " +
             "heavy and the Scene view will slow markedly. Default OFF. Raise the tessellation only if you " +
             "need it; the two segment counts change how finely each sphere and cylinder is drawn, never " +
             "which geometry is included.")]
    [SerializeField] private bool drawFullMinkowski;

    [Range(3, 16)]
    [SerializeField] private int minkowskiSphereSegments = 4;

    [Range(3, 16)]
    [SerializeField] private int minkowskiCylinderSides = 4;

    [Tooltip("SCENE-VIEW GIZMO. Draws the retained probe cloud from the last cooked-hull scan: each " +
             "sampled hull point pushed out along its own normal by contactOffset, linked to its nearest " +
             "neighbours. This is the SPLAT ENVELOPE as a wireframe, built from probe data rather than from " +
             "the authored shell, so it can be compared directly against the other envelope layers. Run " +
             "'Splat fidelity' first — with no retained scan there is nothing to draw.")]
    [SerializeField] private bool drawSplatWireframe;

    [Tooltip("THE SPLAT. Billboarded filled circles of radius contactOffset at every sampled hull point — " +
             "the Minkowski envelope drawn as what it is. A sphere's silhouette is always a circle, so a " +
             "camera-facing filled circle is its EXACT projection, not an approximation: strictly more " +
             "accurate than a triangulated sphere at any zoom, and it matches PhysX, which evaluates " +
             "distance analytically rather than tessellating. Renders in the Game view as well as the Scene " +
             "view, unlike the gizmo layers. Scans on demand if no probe data is retained, so it just works " +
             "when switched on.")]
    [SerializeField] private bool drawSplatCircles;

    [Tooltip("Directions sampled per side when the splat has to scan for itself.")]
    [Range(200, 20000)]
    [SerializeField] private int splatDirections = 4000;

    [Tooltip("Splat colour. Low alpha is deliberate — the envelope is a union, so overlap should build up.")]
    [SerializeField] private Color splatColour = new Color(0.25f, 0.85f, 1f, 0.22f);

    [Tooltip("Colour of the full Minkowski envelope.")]
    [SerializeField] private Color fullMinkowskiColour = new Color(0.25f, 0.85f, 1f, 0.9f);

    [Header("Appearance")]
    [SerializeField] private float lineWidth = 0.0025f;

    [SerializeField] private float witnessCrossSize = 0.01f;

    [Tooltip("Colour for the ring the contact is currently attributed to.")]
    [SerializeField] private Color engagedColour = new Color(0.15f, 1f, 0.25f);

    [Tooltip("Colour for an authored sharp edge that is not the attributed one.")]
    [SerializeField] private Color otherEdgeColour = new Color(0.2f, 0.7f, 1f);

    [SerializeField] private Color longAxisColour = new Color(1f, 0.85f, 0.2f);

    [SerializeField] private Color witnessColour = new Color(1f, 0.3f, 0.9f);

    [Tooltip("Colour of the speculative contact skin schematic.")]
    [SerializeField] private Color contactSkinColour = new Color(1f, 0.65f, 0f, 1f);

    [Tooltip("Q0 line when the attributed feature IS an authored SharpEdge.")]
    [SerializeField] private Color attributedSharpColour = new Color(1f, 1f, 1f);

    [Tooltip("Q0 outline when the attributed feature is NOT a sharp edge — a face, or a non-cutting ridge.")]
    [SerializeField] private Color attributedNonSharpColour = new Color(1f, 0.25f, 0.1f);

    [Header("Reporting")]
    [Tooltip("Log the fitted equations once on Start. The context-menu item does the same on demand.")]
    [SerializeField] private bool logEquationsOnStart;

    [Tooltip("On Start, prove the apparatus is sound before anything measures with it: that each blade's " +
             "profile was baked from the shape its collider is actually colliding, and that the authored " +
             "shell still fits inside that shape with room to spare.\n\n" +
             "This lives here rather than in a component of its own because this is already the Q0 " +
             "instrument-validation component and already holds both blades. It also has to run IN PLAY: " +
             "ProBuilder bakes on load and PhysX cooks and inflates its hull on load, so none of it exists " +
             "to be checked at edit time.")]
    [SerializeField] private bool runPreflightOnStart = true;

    [Tooltip("DECLARED CRITERION, unset by default. Warn when a pair's proximity margin falls below this " +
             "many metres while still being positive. Zero warns on nothing and errors only on an actual " +
             "violation, which is the derived condition, not a chosen number.")]
    [Min(0f)]
    [SerializeField] private float preflightWarnBelowMetres;

    [Tooltip("How many frames to wait for the attachment system to hand each blade to a simulating body " +
             "before running the preflight anyway and reporting what it finds.\n\n" +
             "This is an engineering timeout, not a measurement criterion: it only decides how long to " +
             "wait for Ragdoll Animator, which binds the physics proxy in LateUpdate and so is not ready " +
             "at Start. It exists so a check can never quietly fail to run — if the attachment never " +
             "happens the preflight says so out loud instead of staying silent.")]
    [Min(1)]
    [SerializeField] private int preflightWaitFrames = 300;

    [Tooltip("OFF BY DEFAULT. Pause the editor the instant the solver first attributes an AUTHORED SHARP " +
             "EDGE on either blade at this station, so the moment cannot be missed.\n\n" +
             "It exists because that moment is one classification interval long — 20 fixed steps, about " +
             "0.22 s at 90 Hz — and on a flat-on-flat station it is the whole finding. Measured once at " +
             "8.2 s after arming, which is not something a human catches by watching.\n\n" +
             "Editor only, and it pauses rather than stopping, so the scene is left exactly as it was at " +
             "that step and can be inspected. It changes nothing about what the solver computes; it only " +
             "stops the clock afterwards. On a station where a sharp attribution is EXPECTED — either edge " +
             "station — leave it off or it will pause on the first correct answer.")]
    [SerializeField] private bool pauseOnFirstSharpAttribution;

    private bool sharpPauseFired;

    [Tooltip("Q1's readout: on-screen, what the solver is attributing right now, which ring that is, and " +
             "whether that ring is one of the blade's AUTHORED SharpEdge runs. Reported, not judged — it " +
             "names the ring and lists the authored sharp rings beside it.")]
    [SerializeField] private bool drawAttributionReadout = true;

    private readonly Side a = new Side();
    private readonly Side b = new Side();
    private readonly List<LineRenderer> extraLines = new List<LineRenderer>();
    // Retained from the last cooked-hull scan so the wireframe can be drawn from probe data, and so the
    // fidelity numbers and the picture are guaranteed to come from the SAME scan rather than two.
    private GameObject splatHolder;
    private Mesh splatMesh;
    private Material splatMaterial;
    private int splatBuiltFor;

    private readonly List<HullSample> retainedA = new List<HullSample>();
    private readonly List<HullSample> retainedB = new List<HullSample>();
    private float retainedRadius;

    private Material lineMaterial;

    private void Start()
    {
        // NOT run here. At Start the attachment system has not created the physics proxy yet -- Ragdoll
        // Animator binds it in LateUpdate -- so ActiveColliders still returns the AUTHORED collider, which
        // RA2 has already disabled, and a disabled collider reports attachedRigidbody as null. Running at
        // Start reported "NOT CHECKABLE, live collider has no attached Rigidbody" on all six blades: a
        // timing artefact that looked exactly like a real apparatus fault.
        preflightPending = runPreflightOnStart;
        preflightWaitedFrames = 0;

        if (logEquationsOnStart) LogEquations();
    }

    private bool preflightPending;
    private int preflightWaitedFrames;
    private bool preflightRan;

    /// <summary>
    ///     True once the preflight has actually executed, however it was reached.
    /// </summary>
    /// <remarks>
    ///     Read-only, and it does not distinguish a clean preflight from a failing one — that is what the
    ///     console block is for. It exists so an automated arm can wait for the same precondition a human
    ///     waits for: the preflight cannot run until both blades are carried by a simulating body, so
    ///     "the preflight has run" is the cheapest available proof that the proxies exist and the authored
    ///     shells are being measured against the shapes PhysX is actually colliding.
    /// </remarks>
    public bool PreflightHasRun => preflightRan;

    /// <summary>
    ///     Runs the preflight as soon as both blades are actually being simulated, and no later.
    /// </summary>
    /// <remarks>
    ///     Called from the existing <c>LateUpdate</c> rather than being one of its own, so the two do not
    ///     silently collide.
    /// </remarks>
    private void TickPreflight()
    {
        if (!preflightPending) return;

        if (BladeIsSimulating(contactA) && BladeIsSimulating(contactB))
        {
            preflightPending = false;
            RunPreflight();
            return;
        }

        // Never fail silently. If the attachment never happens, say so rather than simply not running --
        // a check that quietly did not execute is worse than one that failed.
        if (++preflightWaitedFrames >= preflightWaitFrames)
        {
            preflightPending = false;
            Debug.LogError("[Preflight] " + name + ": gave up after " + preflightWaitFrames +
                           " frames waiting for both blades to be carried by a simulating body. Running " +
                           "anyway; anything it reports below is real and not a startup-timing artefact.",
                           this);
            RunPreflight();
        }
    }

    /// <summary>
    ///     Pauses the editor the first time an authored sharp edge is attributed at this station.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Reads the solver's own <see cref="RqBladeTangentialSolver.Snapshot.RawIdA"/>/<c>RawIdB</c>
    ///         and asks each blade's own shell whether that ring is designated <c>SharpEdge</c> — so the
    ///         trigger is the classifier's answer, not a rule of this component's own. It fires once per
    ///         Play session.
    ///     </para>
    ///     <para>
    ///         Pauses rather than stops, so the scene stays exactly as it was on that step and can be
    ///         inspected: the lines are still drawn, the snapshot is still readable, and stepping forward
    ///         from there is possible. Nothing about the classification is altered — the pause happens in
    ///         LateUpdate, after the step it is reporting.
    ///     </para>
    /// </remarks>
    private void MaybePauseOnSharp(RqBladeTangentialSolver.Snapshot snap)
    {
        if (!pauseOnFirstSharpAttribution || sharpPauseFired) return;

        bool sharpA = RingIsAuthoredSharp(contactA, snap.RawIdA);
        bool sharpB = RingIsAuthoredSharp(contactB, snap.RawIdB);
        if (!sharpA && !sharpB) return;

        sharpPauseFired = true;

        Debug.LogWarning("[V2] PAUSED — first authored SHARP EDGE attribution at " + name + ": " +
                         snap.RawTypeA + " | " + snap.RawTypeB +
                         "   rings " + RingOf(snap.RawIdA) + "/" + RingOf(snap.RawIdB) +
                         "   sharp side: " + (sharpA && sharpB ? "BOTH" : sharpA ? "A" : "B") +
                         "   normalForce " + snap.NormalForce.ToString("F4") + " N" +
                         "   shellSeparation " + (snap.ShellSeparation * 1000f).ToString("F3") + " mm" +
                         "   classWrittenAtStep " + snap.ClassWrittenAtStep +
                         ".  The editor is paused, not stopped — the scene is as it was on that step.",
                         this);

#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPaused = true;
#endif
    }

    /// <summary>Whether a raw feature id names a ring the blade's own profile designates SharpEdge.</summary>
    private static bool RingIsAuthoredSharp(RqBladeContact blade, string rawId)
    {
        if (blade == null || string.IsNullOrEmpty(rawId)) return false;

        BladeShell shell = blade.Shell;
        BladeShellData data = shell != null ? shell.Data : null;
        if (data == null) return false;

        string ring = RingOf(rawId);
        if (string.IsNullOrEmpty(ring)) return false;

        for (int i = 0; i < data.EdgeCount; i++)
        {
            var featureRef = new BladeFeatureRef(BladeFeatureKind.Edge, i);
            if (data.TypeOf(featureRef) != BladeFeatureType.SharpEdge) continue;
            if (RingOf(data.IdOf(featureRef)) == ring) return true;
        }

        return false;
    }

    /// <summary>Whether this blade is being carried by a body PhysX is simulating, right now.</summary>
    private static bool BladeIsSimulating(RqBladeContact blade)
    {
        BladeShell shell = blade != null ? blade.Shell : null;
        if (shell == null) return false;

        IReadOnlyList<Collider> live = shell.ActiveColliders;
        for (int i = 0; live != null && i < live.Count; i++)
            if (live[i] != null && live[i].attachedRigidbody != null) return true;

        return false;
    }

    private void OnDisable()
    {
        // Runtime-created visuals only; nothing is serialized, so stopping Play leaves no trace.
        DestroyVisuals();
    }

    // ----- preflight: is this apparatus entitled to the measurements it is about to make? -----------

    /// <summary>Result of the origin-shape identity check. Not a measurement — yes, no, or unanswerable.</summary>
    private enum OriginVerdict { NotRecorded, Match, Mismatch }

    private struct PreflightSide
    {
        public RqBladeContact Blade;
        public bool Usable;
        public string Why;
        public OriginVerdict Origin;
        public string OriginExpected, OriginActual, ColliderName;
        public float MaxProtrusion, ContactOffset;
        public string WorstFeature;
        public int PointsTested, PointsOutside;
        public Vector3 LossyScale;
        public float LongAxisRatio, VolumeRatio;
    }

    /// <summary>
    ///     Proves, once per Play, that the shells may be trusted before anything measures with them.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Why it must run in Play and cannot be an edit-time tool.</b> ProBuilder bakes its mesh on
    ///         load, PhysX cooks its convex hull on load and inflates it by <c>contactOffset</c>, and
    ///         Ragdoll Animator re-hosts the whole blade on a generated proxy on load. None of the three
    ///         things being checked here exists until the scene is running.
    ///     </para>
    ///     <para>
    ///         <b>What it proves.</b> OurSolution only attributes a contact if PhysX reports a pair first —
    ///         a <c>Pair</c> is created from a collision callback and nothing else — and that callback
    ///         fires when the cooked hulls come within their combined <c>contactOffset</c>. So the authored
    ///         shells must still be apart at that moment. Nothing in the code enforces that ordering, so it
    ///         is measured: the worst protrusion of each shell past its own hull, summed, against the
    ///         combined offset. A margin at or below zero is an error by construction.
    ///     </para>
    /// </remarks>
    [ContextMenu("Run preflight")]
    public void RunPreflight()
    {
        preflightRan = true;

        PreflightSide a = MeasureSide(contactA);
        PreflightSide b = MeasureSide(contactB);

        var sb = new StringBuilder();
        sb.AppendLine("[Preflight] " + name + " — authored shell vs the shape PhysX actually collides.");

        int bad = 0;
        bad += ReportSide(a, sb);
        bad += ReportSide(b, sb);

        if (a.Usable && b.Usable)
        {
            float protrusion = a.MaxProtrusion + b.MaxProtrusion;
            float room = a.ContactOffset + b.ContactOffset;
            float margin = room - protrusion;

            string line = "shell protrusion " + (protrusion * 1000f).ToString("F4") +
                          " mm vs combined contactOffset " + (room * 1000f).ToString("F2") +
                          " mm -> margin " + (margin * 1000f).ToString("F4") + " mm";

            if (margin <= 0f)
            {
                bad++;
                Debug.LogError("[Preflight] " + name + ": ORDERING NOT GUARANTEED — " + line +
                               ". The authored shells can meet before PhysX reports a pair, so an " +
                               "attribution may arrive after the contact it describes. Derived condition, " +
                               "not a chosen threshold.", this);
            }
            else if (preflightWarnBelowMetres > 0f && margin < preflightWarnBelowMetres)
            {
                Debug.LogWarning("[Preflight] " + name + ": margin below the declared warn level — " +
                                 line, this);
            }
            else
            {
                sb.AppendLine("  PAIR OK  " + line);
            }
        }

        if (bad > 0)
        {
            Debug.LogError("[Preflight] " + name + ": " + bad + " problem(s); details above.", this);
            Debug.Log(sb.ToString(), this);
        }
        else
        {
            sb.AppendLine("  no problems found.");
            Debug.Log(sb.ToString(), this);
        }
    }

    private int ReportSide(PreflightSide r, StringBuilder sb)
    {
        string who = r.Blade == null ? "(unset)" : r.Blade.Label;

        if (!r.Usable)
        {
            Debug.LogError("[Preflight] " + who + ": NOT CHECKABLE — " + r.Why +
                           ". The ordering this method depends on cannot be verified for this blade.", this);
            return 1;
        }

        int bad = 0;

        // Origin shape first: the only check here with no tolerance in it, and the only one that catches a
        // profile SMALLER than its collider, which protrudes nothing and would otherwise pass silently.
        switch (r.Origin)
        {
            case OriginVerdict.Match:
                sb.AppendLine("  " + who + "  origin OK — baked from '" + r.OriginExpected +
                              "', colliding '" + r.OriginActual + "'");
                break;
            case OriginVerdict.Mismatch:
                bad++;
                Debug.LogError("[Preflight] " + who + ": WRONG PROFILE FOR THIS COLLIDER. Baked from '" +
                               r.OriginExpected + "' but colliding '" + r.OriginActual +
                               "'. Every attribution this blade makes describes a different object. " +
                               "Identity check — it cannot be a false alarm.", this);
                break;
            case OriginVerdict.NotRecorded:
                Debug.LogWarning("[Preflight] " + who + ": its BladeProfileAsset records no origin shape, " +
                                 "so the strongest check is unavailable and only geometry applies. Stamp " +
                                 "it with Blade Contact > Verify and stamp profile origin.", this);
                break;
        }

        sb.AppendLine("    protrusion " + (r.MaxProtrusion * 1000f).ToString("F4") + " mm at " +
                      r.WorstFeature + "  (" + r.PointsOutside + "/" + r.PointsTested + " points outside)" +
                      "  contactOffset " + (r.ContactOffset * 1000f).ToString("F2") + " mm" +
                      "  collider " + r.ColliderName +
                      "  longAxisRatio " + r.LongAxisRatio.ToString("F4") +
                      "  volumeRatio " + r.VolumeRatio.ToString("F4"));

        if (Vector3.Distance(r.LossyScale, Vector3.one) > 1e-4f)
        {
            bad++;
            Debug.LogError("[Preflight] " + who + ": collider lossyScale is " + r.LossyScale.ToString("F5") +
                           ", not 1. The authored shell is expressed in unscaled local space, so shell and " +
                           "hull are not in the same units and every separation this blade produces is " +
                           "wrong by that factor.", this);
        }

        return bad;
    }

    private static PreflightSide MeasureSide(RqBladeContact blade)
    {
        var r = new PreflightSide { Blade = blade, WorstFeature = "", ColliderName = "(none)" };

        BladeShell shell = blade != null ? blade.Shell : null;
        if (shell == null) { r.Why = "no BladeShell"; return r; }

        BladeShellData data = shell.Data;
        if (data == null || data.EdgeCount == 0) { r.Why = "authored shell is empty — no profile asset?"; return r; }

        Collider col = null;
        IReadOnlyList<Collider> live = shell.ActiveColliders;
        for (int i = 0; live != null && i < live.Count; i++)
            if (live[i] != null) { col = live[i]; break; }

        if (col == null) { r.Why = "no live collider represents this blade"; return r; }
        if (col.attachedRigidbody == null) { r.Why = "live collider has no attached Rigidbody"; return r; }

        var mc = col as MeshCollider;
        if (mc != null && mc.sharedMesh == null) { r.Why = "MeshCollider has a null sharedMesh"; return r; }

        r.ColliderName = col.name;
        r.ContactOffset = col.contactOffset;
        r.LossyScale = col.transform.lossyScale;

        BladeProfileAsset profile = shell.ProfileAsset;
        Mesh liveMesh = mc != null ? mc.sharedMesh : null;
        r.OriginActual = liveMesh != null ? liveMesh.name : "(not a MeshCollider)";
        r.OriginExpected = profile != null && profile.HasOriginShape ? profile.SourceMeshName : "(not recorded)";
        r.Origin = profile == null || !profile.HasOriginShape || liveMesh == null
            ? OriginVerdict.NotRecorded
            : profile.MatchesOriginShape(liveMesh) ? OriginVerdict.Match : OriginVerdict.Mismatch;

        BladePose pose = shell.SimulatedPlacement;
        Vector3 cPos;
        Quaternion cRot;
        SimulatedColliderPose(col, out cPos, out cRot);

        float worst = 0f;
        string worstId = "";
        int tested = 0, outside = 0;
        Vector3 lo = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        Vector3 hi = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        Quaternion invCol = Quaternion.Inverse(cRot);

        for (int i = 0; i < data.EdgeCount; i++)
        {
            var featureRef = new BladeFeatureRef(BladeFeatureKind.Edge, i);
            BladeEdgeLine e = data.GetEdge(i);

            for (int end = 0; end < 2; end++)
            {
                Vector3 world = pose.TransformPoint(end == 0 ? e.LocalStart : e.LocalEnd);
                float d = Vector3.Distance(world, Physics.ClosestPoint(world, col, cPos, cRot));
                tested++;
                if (d > 1e-5f) outside++;
                if (d > worst) { worst = d; worstId = data.IdOf(featureRef); }

                Vector3 local = invCol * (world - cPos);
                lo = Vector3.Min(lo, local);
                hi = Vector3.Max(hi, local);
            }
        }

        Vector3 shellSize = hi - lo;
        Bounds hullLocal = mc != null && mc.sharedMesh != null
            ? mc.sharedMesh.bounds
            : new Bounds(Vector3.zero, col.bounds.size);

        float shellLong = Mathf.Max(shellSize.x, Mathf.Max(shellSize.y, shellSize.z));
        float hullLong = Mathf.Max(hullLocal.size.x, Mathf.Max(hullLocal.size.y, hullLocal.size.z));
        r.LongAxisRatio = hullLong > 1e-6f ? shellLong / hullLong : float.NaN;
        r.VolumeRatio = Mathf.Max(1e-9f, shellSize.x * shellSize.y * shellSize.z) /
                        Mathf.Max(1e-9f, hullLocal.size.x * hullLocal.size.y * hullLocal.size.z);

        r.Usable = true;
        r.MaxProtrusion = worst;
        r.WorstFeature = string.IsNullOrEmpty(worstId) ? "(none outside)" : worstId;
        r.PointsTested = tested;
        r.PointsOutside = outside;
        return r;
    }

    // ----- authored edge extraction and line fitting -----------------------------------------------

    private void EnsureBuilt(Side s, RqBladeContact blade)
    {
        if (s.Built && s.Blade == blade) return;

        s.Blade = blade;
        s.Runs.Clear();
        s.Built = true;

        if (blade == null) return;

        BladeShellData d = blade.Data;
        if (d == null) return;

        // Group authored SharpEdge segments by their ring, which is the id tail. Both specimens author two
        // runs (e0 and e6): these are double-edged blades, and the ring is what distinguishes them.
        var byRing = new Dictionary<string, List<Vector3>>();
        var angMin = new Dictionary<string, float>();
        var angMax = new Dictionary<string, float>();

        for (int i = 0; i < d.EdgeCount; i++)
        {
            var r = new BladeFeatureRef(BladeFeatureKind.Edge, i);
            if (d.TypeOf(r) != BladeFeatureType.SharpEdge) continue;

            string id = d.IdOf(r);
            int dot = id.LastIndexOf('.');
            string ring = dot >= 0 ? id.Substring(dot + 1) : id;

            List<Vector3> pts;
            if (!byRing.TryGetValue(ring, out pts))
            {
                pts = new List<Vector3>();
                byRing[ring] = pts;
            }

            BladeEdgeLine line = d.GetEdge(i);
            pts.Add(line.LocalStart);
            pts.Add(line.LocalEnd);

            float ia = line.IncludedAngleDegrees;
            if (!angMin.ContainsKey(ring)) { angMin[ring] = ia; angMax[ring] = ia; }
            else
            {
                if (ia < angMin[ring]) angMin[ring] = ia;
                if (ia > angMax[ring]) angMax[ring] = ia;
            }
        }

        foreach (var kv in byRing)
        {
            EdgeRun run = Fit(kv.Key, kv.Value);
            if (run == null) continue;
            run.IncludedAngleMin = angMin[kv.Key];
            run.IncludedAngleMax = angMax[kv.Key];
            s.Runs.Add(run);
        }

        s.Runs.Sort((x, y) => string.CompareOrdinal(x.Ring, y.Ring));
    }

    /// <summary>
    ///     Least-squares 3D line through the authored run: centroid plus the dominant direction of the
    ///     point covariance.
    /// </summary>
    /// <remarks>
    ///     The direction is taken by power iteration on the covariance matrix rather than
    ///     <c>normalize(last - first)</c>. The endpoints are the two least reliable samples for a fit — if
    ///     the edge sweeps or the tip curves, an endpoint-to-endpoint chord tilts the whole line while
    ///     reporting no error at all. Power iteration uses every sample, and the residual it leaves is the
    ///     honest statement of how straight the authored edge really is.
    /// </remarks>
    private static EdgeRun Fit(string ring, List<Vector3> pts)
    {
        int n = pts.Count;
        if (n < 4) return null;

        Vector3 c = Vector3.zero;
        for (int i = 0; i < n; i++) c += pts[i];
        c /= n;

        float xx = 0f, xy = 0f, xz = 0f, yy = 0f, yz = 0f, zz = 0f;
        for (int i = 0; i < n; i++)
        {
            Vector3 p = pts[i] - c;
            xx += p.x * p.x; xy += p.x * p.y; xz += p.x * p.z;
            yy += p.y * p.y; yz += p.y * p.z; zz += p.z * p.z;
        }

        // Seeded from the chord, then refined. The seed only has to be non-degenerate.
        Vector3 v = pts[n - 1] - pts[0];
        if (v.sqrMagnitude < 1e-18f) v = Vector3.right;
        v.Normalize();

        for (int it = 0; it < 24; it++)
        {
            Vector3 nv = new Vector3(
                xx * v.x + xy * v.y + xz * v.z,
                xy * v.x + yy * v.y + yz * v.z,
                xz * v.x + yz * v.y + zz * v.z);

            if (nv.sqrMagnitude < 1e-24f) break;
            nv.Normalize();
            if ((nv - v).sqrMagnitude < 1e-20f) { v = nv; break; }
            v = nv;
        }

        float minT = float.MaxValue, maxT = float.MinValue, resMax = 0f, sum = 0f;
        for (int i = 0; i < n; i++)
        {
            Vector3 p = pts[i] - c;
            float t = Vector3.Dot(p, v);
            if (t < minT) minT = t;
            if (t > maxT) maxT = t;

            float perp = (p - v * t).magnitude;
            if (perp > resMax) resMax = perp;
            sum += perp * perp;
        }

        return new EdgeRun
        {
            Ring = ring,
            Segments = n / 2,
            LocalCentroid = c,
            LocalDirection = v,
            MinT = minT,
            MaxT = maxT,
            ResidualMax = resMax,
            ResidualRms = Mathf.Sqrt(sum / n)
        };
    }

    // ----- drawing ---------------------------------------------------------------------------------

    private Material LineMaterial =>
        lineMaterial != null ? lineMaterial : lineMaterial = new Material(Shader.Find("Unlit/Color"));

    private LineRenderer MakeLine(string label)
    {
        var go = new GameObject(label);
        go.transform.SetParent(transform, false);

        var lr = go.AddComponent<LineRenderer>();
        lr.useWorldSpace = true;
        lr.positionCount = 2;
        lr.widthMultiplier = lineWidth;
        lr.numCapVertices = 2;
        lr.material = new Material(LineMaterial);
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows = false;
        return lr;
    }

    private void DestroyVisuals()
    {
        // Unparented, so it does not die with this object and has to be removed explicitly.
        if (splatHolder != null)
        {
            Destroy(splatHolder);
            splatHolder = null;
            splatMesh = null;
            splatBuiltFor = -1;
        }

        foreach (var s in new[] { a, b })
            for (int i = 0; i < s.Runs.Count; i++)
                if (s.Runs[i].Line != null)
                {
                    Destroy(s.Runs[i].Line.gameObject);
                    s.Runs[i].Line = null;
                }

        for (int i = 0; i < extraLines.Count; i++)
            if (extraLines[i] != null) Destroy(extraLines[i].gameObject);

        extraLines.Clear();
    }

    private void LateUpdate()
    {
        TickPreflight();

        EnsureBuilt(a, contactA);
        EnsureBuilt(b, contactB);

        string attributedA = null, attributedB = null;
        if (solver != null && contactA != null && contactB != null)
        {
            RqBladeTangentialSolver.Snapshot snap;
            if (solver.TryGetSnapshot(contactA, contactB, out snap) && snap.ClassificationValid)
            {
                attributedA = RingOf(snap.RawIdA);
                attributedB = RingOf(snap.RawIdB);

                MaybePauseOnSharp(snap);
            }
        }

        DrawSide(a, attributedA, "A");
        DrawSide(b, attributedB, "B");

        // Extra lines are rebuilt per frame rather than pooled: they are few, and pooling them would mean
        // one more piece of state that can disagree with the toggles.
        for (int i = 0; i < extraLines.Count; i++)
            if (extraLines[i] != null) Destroy(extraLines[i].gameObject);

        extraLines.Clear();

        if (drawAttributedFeature) DrawAttributed();

        if (drawLongAxis)
        {
            DrawLongAxis(contactA);
            DrawLongAxis(contactB);
        }

        if (drawContactWitness) DrawWitness();

        UpdateSplatCircles();
    }

    private static string RingOf(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        int dot = id.LastIndexOf('.');
        return dot >= 0 ? id.Substring(dot + 1) : id;
    }

    private void DrawSide(Side s, string attributedRing, string label)
    {
        if (s.Blade == null) return;

        BladePose pose = s.Blade.SimulatedPlacement;

        for (int i = 0; i < s.Runs.Count; i++)
        {
            EdgeRun run = s.Runs[i];
            bool engaged = attributedRing != null && attributedRing == run.Ring;
            bool show = drawSharpEdgeLines && (!drawAttributedRingOnly || engaged);

            if (!show)
            {
                if (run.Line != null) run.Line.enabled = false;
                continue;
            }

            if (run.Line == null) run.Line = MakeLine("V2_SharpEdge_" + label + "_" + run.Ring);

            run.Line.enabled = true;
            run.Line.widthMultiplier = lineWidth;

            // Spans the authored run's own extent, so a correct fit visually coincides with the edge end
            // to end. An arbitrary length would overshoot and hide a tilt.
            Vector3 p0 = pose.TransformPoint(run.LocalCentroid + run.LocalDirection * run.MinT);
            Vector3 p1 = pose.TransformPoint(run.LocalCentroid + run.LocalDirection * run.MaxT);
            run.Line.SetPosition(0, p0);
            run.Line.SetPosition(1, p1);

            Color c = engaged ? engagedColour : otherEdgeColour;
            run.Line.material.color = c;
            run.Line.startColor = run.Line.endColor = c;
        }
    }

    /// <summary>
    ///     Q0. Draws the feature the solver attributed, taken from that feature's own geometry.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The instrument must not choose the feature.</b> Reading
    ///         <see cref="RqBladeTangentialSolver.Snapshot.FeatureA" /> is the whole point: if this drew a
    ///         feature selected by any rule of its own — a ring we believe is the cutting edge, a fitted
    ///         run, the blade's long axis through a witness — then a correct-looking line would only be
    ///         evidence that we drew what we already believed. It would demonstrate nothing.
    ///     </para>
    ///     <para>
    ///         An edge is drawn as its own two vertices extended along their own direction. A face is
    ///         drawn as its triangle outline in a different colour, because an attributed face is a
    ///         different fact from an attributed edge and must not be able to look like one.
    ///     </para>
    /// </remarks>
    private void DrawAttributed()
    {
        if (solver == null || contactA == null || contactB == null) return;

        RqBladeTangentialSolver.Snapshot snap;
        if (!solver.TryGetSnapshot(contactA, contactB, out snap) || !snap.ClassificationValid) return;

        DrawAttributedSide(contactA, snap.FeatureA);
        DrawAttributedSide(contactB, snap.FeatureB);
    }

    private void DrawAttributedSide(RqBladeContact blade, BladeFeatureRef feature)
    {
        if (blade == null || !feature.IsValid) return;

        BladeShellData d = blade.Data;
        if (d == null) return;

        BladePose pose = blade.SimulatedPlacement;
        bool sharp = d.TypeOf(feature) == BladeFeatureType.SharpEdge;
        Color c = sharp ? attributedSharpColour : attributedNonSharpColour;

        if (feature.Kind == BladeFeatureKind.Edge)
        {
            BladeEdgeLine e = d.GetEdge(feature.Index);
            Vector3 w0 = pose.TransformPoint(e.LocalStart);
            Vector3 w1 = pose.TransformPoint(e.LocalEnd);

            Vector3 dir = w1 - w0;
            if (dir.sqrMagnitude < 1e-16f) return;
            dir.Normalize();

            var lr = MakeLine("V2_Q0_Attributed_Edge");
            lr.material.color = c;
            lr.startColor = lr.endColor = c;
            lr.SetPosition(0, w0 - dir * attributedExtend);
            lr.SetPosition(1, w1 + dir * attributedExtend);
            extraLines.Add(lr);
            return;
        }

        // A face: outline the triangle rather than pretend it has a line.
        BladeSurface f = d.GetSurface(feature.Index);
        Vector3 a0 = pose.TransformPoint(f.LocalA);
        Vector3 a1 = pose.TransformPoint(f.LocalB);
        Vector3 a2 = pose.TransformPoint(f.LocalC);

        var tri = MakeLine("V2_Q0_Attributed_Face");
        tri.material.color = c;
        tri.startColor = tri.endColor = c;
        tri.positionCount = 4;
        tri.SetPosition(0, a0);
        tri.SetPosition(1, a1);
        tri.SetPosition(2, a2);
        tri.SetPosition(3, a0);
        extraLines.Add(tri);
    }

    private void DrawLongAxis(RqBladeContact blade)
    {
        if (blade == null) return;

        BladePose pose = blade.SimulatedPlacement;
        Vector3 axis = blade.LocalLongAxis;
        if (axis.sqrMagnitude < 1e-12f) return;

        axis = axis.normalized;
        var lr = MakeLine("V2_LongAxis");
        lr.material.color = longAxisColour;
        lr.startColor = lr.endColor = longAxisColour;
        lr.SetPosition(0, pose.TransformPoint(Vector3.zero));
        lr.SetPosition(1, pose.TransformPoint(axis * 1f));
        extraLines.Add(lr);
    }

    /// <summary>
    ///     The live collider's <c>contactOffset</c> — the speculative contact distance actually in force.
    /// </summary>
    /// <remarks>
    ///     Read from the collider on the blade's <c>ActiveTransform</c>, which is the proxy when an
    ///     attachment system is hosting the sword's physics, and only falls back to the authored collider if
    ///     there is none. Reading the authored one while the proxy is what PhysX simulates would report an
    ///     offset that is not in effect.
    /// </remarks>
    private static float LiveContactOffset(RqBladeContact blade, out string source)
    {
        source = "none";
        if (blade == null) return 0f;

        Collider authored = blade.BladeCollider;
        Transform t = blade.ActiveTransform;

        // The collider sits on a CHILD of the proxy root, not on the root itself, so GetComponent alone
        // finds nothing and silently falls through to the authored collider. That is not a harmless
        // fallback: the authored blade reads 0.005 m here while the proxy that actually simulates reads
        // 0.01 m, so substituting one for the other misreports the envelope by a factor of two. Match the
        // proxy copy by the authored collider's name, which is what the attachment system names it after.
        if (t != null && authored != null)
        {
            var candidates = t.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < candidates.Length; i++)
            {
                if (candidates[i] == authored) continue;
                if (!candidates[i].name.StartsWith(authored.name)) continue;

                source = candidates[i].name + " (live proxy)";
                return candidates[i].contactOffset;
            }
        }

        if (authored == null) return 0f;

        // Reported as UNRESOLVED rather than as a value, so a reader cannot mistake the authored figure for
        // the one in force.
        source = authored.name + " (AUTHORED — live proxy collider NOT resolved, may not be in force)";
        return authored.contactOffset;
    }

    /// <summary>
    ///     Scene-view gizmos for the collision geometry: the speculative envelope, its bounding box, and the
    ///     hull PhysX actually collides against.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         These are <see cref="MonoBehaviour.OnDrawGizmos" /> rather than <see cref="LineRenderer" />s,
    ///         unlike the edge and attribution lines. That is deliberate: these three answer questions asked
    ///         while inspecting the rig in the Scene view, not questions asked while watching a run, and
    ///         drawing a hull wireframe as line renderers would cost thousands of objects.
    ///     </para>
    ///     <para>
    ///         <b>The envelope is drawn as rings, not as an inflated wireframe, and that is the point.</b>
    ///         <c>contactOffset</c> is a DISTANCE THRESHOLD: PhysX reports a contact once the surfaces are
    ///         within it. The locus of points within distance d of a body is its Minkowski sum with a sphere
    ///         of radius d, which <b>rounds every edge and corner</b>. So the skin does NOT preserve the
    ///         sharp edge — the 25-degree apex is enclosed by a cylinder of radius <c>contactOffset</c>, and
    ///         at that scale a sharp ridge and a blunt one generate contacts identically.
    ///     </para>
    ///     <para>
    ///         An inflated wireframe would be misleading in the opposite direction: offsetting mesh vertices
    ///         along their normals keeps a sharp edge sharp, which is exactly the wrong answer. Rings around
    ///         the ridge are the exact envelope for the ridge, so they are drawn instead.
    ///     </para>
    ///     <para>
    ///         This is also why OurSolution measures the authored shell directly rather than inferring
    ///         geometry from the manifold — though the manifold still governs WHEN a contact is reported and
    ///         what normal arrives, so the rounding is not fully sidestepped.
    ///     </para>
    /// </remarks>
    private void OnDrawGizmos()
    {
        if (!drawContactSkin && !drawContactSkinBox && !drawColliderHull && !drawFullMinkowski &&
            !drawSplatWireframe) return;

        if (drawSplatWireframe)
        {
            DrawSplatWireframe(retainedA);
            DrawSplatWireframe(retainedB);
        }

        EnsureBuilt(a, contactA);
        EnsureBuilt(b, contactB);
        GizmosForSide(a);
        GizmosForSide(b);
    }

    /// <summary>
    ///     Builds and maintains the billboarded-circle splat: one quad per sampled hull point, rendered as
    ///     a filled camera-facing disc of radius contactOffset.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Why circles rather than geometry.</b> The envelope is the union of spheres of radius r
    ///         centred on the surface, and a sphere's silhouette is a circle from every viewpoint — so a
    ///         filled camera-facing circle is the sphere's exact projection, not a stand-in for it. That is
    ///         strictly more accurate than a triangulated sphere at any zoom, and it is what PhysX's own
    ///         analytic distance evaluation corresponds to. No tessellation parameter appears anywhere in
    ///         this path, because there is nothing to tessellate.
    ///     </para>
    ///     <para>
    ///         All four vertices of a quad sit at the splat centre and the corner offset travels in the UV;
    ///         the billboarding happens in VIEW space in the vertex shader. So the mesh is built once and
    ///         never rebuilt when the camera moves, and it is one mesh with one draw call for every splat.
    ///     </para>
    ///     <para>
    ///         Rendered through a real <see cref="MeshRenderer" />, so unlike the gizmo layers it appears in
    ///         the Game view as well as the Scene view. It also scans on demand when no probe data is
    ///         retained — the wireframe's requirement to run a separate report first was a flaw, not a
    ///         feature.
    ///     </para>
    /// </remarks>
    private void UpdateSplatCircles()
    {
        if (!drawSplatCircles)
        {
            if (splatHolder != null) splatHolder.SetActive(false);
            return;
        }

        if (!Application.isPlaying) return;

        // Scan on demand rather than requiring a separate report to have been run first.
        if (retainedA.Count == 0 && retainedB.Count == 0)
        {
            if (!ScanForSplat()) return;
        }

        if (splatHolder == null)
        {
            // UNPARENTED, at identity, on purpose. The splat vertices are built in WORLD space, so the
            // holder's own matrix must be identity or TransformObjectToWorld applies a second transform on
            // top. Parenting it under the station root did exactly that: with the root at (4, 0.1, 12) the
            // envelope rendered at (6.18, 1.47, 26.63) while the blade sat at (2.32, 1.30, 14.80) — drawn
            // faithfully, twelve metres from the thing it describes. Runtime-only, so it leaves no trace.
            splatHolder = new GameObject("V2_SplatCircles");
            splatHolder.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            splatHolder.transform.localScale = Vector3.one;
            splatHolder.AddComponent<MeshFilter>();

            var mr = splatHolder.AddComponent<MeshRenderer>();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
        }

        splatHolder.SetActive(true);

        if (splatMaterial == null)
        {
            Shader sh = Shader.Find("P3T/RqSplatCircle");
            if (sh == null)
            {
                Debug.LogError("[BindMetricsV2] shader P3T/RqSplatCircle not found — the splat cannot draw.",
                    this);
                drawSplatCircles = false;
                return;
            }

            splatMaterial = new Material(sh);
            splatHolder.GetComponent<MeshRenderer>().sharedMaterial = splatMaterial;
        }

        splatMaterial.SetColor("_Color", splatColour);
        splatMaterial.SetFloat("_Radius", retainedRadius);

        int total = retainedA.Count + retainedB.Count;
        if (splatMesh == null || splatBuiltFor != total)
        {
            splatMesh = BuildSplatMesh();
            splatBuiltFor = total;
            splatHolder.GetComponent<MeshFilter>().sharedMesh = splatMesh;
        }
    }

    /// <summary>Samples both hulls for the splat, retaining the result. Returns false if nothing usable.</summary>
    private bool ScanForSplat()
    {
        RqBladeContact[] sides = { contactA, contactB };
        List<HullSample>[] keep = { retainedA, retainedB };
        bool any = false;

        for (int i = 0; i < 2; i++)
        {
            Collider col = ResolveLiveCollider(sides[i]);
            if (col == null) continue;

            string src;
            float r = LiveContactOffset(sides[i], out src);
            if (r <= 0f) continue;

            retainedRadius = r;
            keep[i].Clear();
            keep[i].AddRange(SampleCookedHull(col, splatDirections));
            any |= keep[i].Count > 0;
        }

        return any;
    }

    /// <summary>
    ///     One quad per splat, all four vertices at the centre, corner offsets in the UV.
    /// </summary>
    private Mesh BuildSplatMesh()
    {
        int n = retainedA.Count + retainedB.Count;
        var verts = new Vector3[n * 4];
        var uvs = new Vector2[n * 4];
        var tris = new int[n * 6];

        int v = 0, t = 0;
        for (int side = 0; side < 2; side++)
        {
            List<HullSample> src = side == 0 ? retainedA : retainedB;
            for (int i = 0; i < src.Count; i++)
            {
                // The splat sits on the ENVELOPE, one radius out along the sampled surface normal.
                Vector3 c = src[i].Point + src[i].Normal * retainedRadius;

                verts[v + 0] = c; uvs[v + 0] = new Vector2(-1f, -1f);
                verts[v + 1] = c; uvs[v + 1] = new Vector2(1f, -1f);
                verts[v + 2] = c; uvs[v + 2] = new Vector2(1f, 1f);
                verts[v + 3] = c; uvs[v + 3] = new Vector2(-1f, 1f);

                tris[t + 0] = v + 0; tris[t + 1] = v + 1; tris[t + 2] = v + 2;
                tris[t + 3] = v + 0; tris[t + 4] = v + 2; tris[t + 5] = v + 3;

                v += 4;
                t += 6;
            }
        }

        var m = new Mesh();
        m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        m.vertices = verts;
        m.uv = uvs;
        m.triangles = tris;

        // The vertex shader pushes geometry outward in view space, so Unity's own bounds would cull it
        // early. Padded by the radius so a splat is never culled while still on screen.
        Bounds b = m.bounds;
        b.Expand(retainedRadius * 4f);
        m.bounds = b;
        return m;
    }

    /// <summary>Discards the retained probe cloud so the next draw rescans.</summary>
    [ContextMenu("Rescan splat")]
    public void RescanSplat()
    {
        retainedA.Clear();
        retainedB.Clear();
        splatBuiltFor = -1;
    }

    /// <summary>
    ///     The splat envelope as a wireframe, built from retained probe samples.
    /// </summary>
    /// <remarks>
    ///     Each retained hull sample pushed out along its own normal by the retained radius, then linked to
    ///     its nearest neighbour. Drawn from PROBE data, not from the authored shell, so laying it over the
    ///     other envelope layers compares two independently derived pictures of the same envelope — one from
    ///     the authored geometry, one from the collider PhysX actually uses. Where they disagree is the gap
    ///     between what we reason about and what collides.
    /// </remarks>
    private void DrawSplatWireframe(List<HullSample> samples)
    {
        if (samples.Count == 0 || retainedRadius <= 0f) return;

        Gizmos.color = new Color(1f, 1f, 0.35f, 0.9f);
        int n = samples.Count;
        float tick = retainedRadius * 0.25f;

        for (int i = 0; i < n; i++)
        {
            Vector3 x = samples[i].Point + samples[i].Normal * retainedRadius;
            Gizmos.DrawLine(x - samples[i].Normal * tick, x + samples[i].Normal * tick);

            // One link to the nearest neighbour, which is enough to read as a surface without drawing a
            // full proximity graph over thousands of points.
            float best = float.MaxValue;
            int bj = -1;
            for (int j = 0; j < n; j++)
            {
                if (j == i) continue;
                float d2 = (samples[j].Point - samples[i].Point).sqrMagnitude;
                if (d2 < best) { best = d2; bj = j; }
            }

            if (bj >= 0)
                Gizmos.DrawLine(x, samples[bj].Point + samples[bj].Normal * retainedRadius);
        }
    }

    private void GizmosForSide(Side s)
    {
        if (s.Blade == null) return;

        string src;
        float r = LiveContactOffset(s.Blade, out src);

        Collider live = ResolveLiveCollider(s.Blade);

        if (drawFullMinkowski && r > 0f) DrawFullMinkowski(s, r);

        if (drawColliderHull && live != null)
        {
            var mc = live as MeshCollider;
            if (mc != null && mc.sharedMesh != null)
            {
                // Drawn at the SIMULATED collider pose, so the wireframe sits where PhysX has the shape
                // rather than where the interpolated transform is rendering it.
                Vector3 hullPos;
                Quaternion hullRot;
                SimulatedColliderPose(live, out hullPos, out hullRot);
                Gizmos.color = Color.white;
                Gizmos.DrawWireMesh(mc.sharedMesh, hullPos, hullRot, live.transform.lossyScale);
            }
        }

        if (drawContactSkinBox && live != null && r > 0f)
        {
            // The mesh's own local bounds drawn under the collider's transform IS the oriented box; a world
            // AABB would rotate with the camera-aligned axes and mean nothing.
            var mc = live as MeshCollider;
            if (mc != null && mc.sharedMesh != null)
            {
                Bounds lb = mc.sharedMesh.bounds;
                Matrix4x4 prev = Gizmos.matrix;
                // Same reason as the hull above: built at the simulated pose, not the rendered transform.
                Vector3 boxPos;
                Quaternion boxRot;
                SimulatedColliderPose(live, out boxPos, out boxRot);
                Gizmos.matrix = Matrix4x4.TRS(boxPos, boxRot, live.transform.lossyScale);
                Gizmos.color = new Color(1f, 0.2f, 0.9f, 0.85f);
                Gizmos.DrawWireCube(lb.center, lb.size + new Vector3(r, r, r) * 2f);
                Gizmos.matrix = prev;
            }
        }

        if (!drawContactSkin || r <= 0f) return;

        BladePose pose = s.Blade.SimulatedPlacement;
        Gizmos.color = contactSkinColour;

        for (int i = 0; i < s.Runs.Count; i++)
        {
            EdgeRun run = s.Runs[i];
            Vector3 dir = (pose.Rotation * run.LocalDirection).normalized;

            Vector3 u = Vector3.Cross(dir, Vector3.up);
            if (u.sqrMagnitude < 1e-8f) u = Vector3.Cross(dir, Vector3.right);
            u.Normalize();
            Vector3 v = Vector3.Cross(dir, u).normalized;

            Vector3 p0 = pose.TransformPoint(run.LocalCentroid + run.LocalDirection * run.MinT);
            Vector3 p1 = pose.TransformPoint(run.LocalCentroid + run.LocalDirection * run.MaxT);
            DrawWireCapsule(p0, p1, dir, u, v, r, contactSkinRings);
        }
    }

    /// <summary>One exact surface sample of the cooked hull: a point and its outward normal.</summary>
    private struct HullSample
    {
        public Vector3 Point;
        public Vector3 Normal;
    }

    /// <summary>
    ///     Samples the cooked convex hull's SURFACE with <see cref="Physics.ClosestPoint" />, returning a
    ///     point and its exact outward normal per sample.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Why this reaches the real hull.</b> Unity exposes no managed access to the cooked hull —
    ///         <see cref="MeshCollider" /> has zero managed instance fields and the PxConvexMesh lives in
    ///         native memory, so reflection cannot get at it. <see cref="Physics.ClosestPoint" /> queries
    ///         that cooked shape directly, so this samples the true hull including PhysX's welding,
    ///         cleaning and skin, rather than a reconstruction of it.
    ///     </para>
    ///     <para>
    ///         <b>These are NOT vertices, and assuming they were is the mistake this code exists to avoid.</b>
    ///         For a FINITE query point the closest point on a convex body is where the surface normal
    ///         points back at the query, which for a polyhedron is generically a point in the interior of a
    ///         FACE. The samples therefore lie on a continuum, and collapsing them in point space does not
    ///         converge: measured, the distinct-point count grew LINEARLY with the sample count — 38, 122,
    ///         439, 1750 for 600, 2000, 8000, 32000 directions. Nothing was being deduplicated because
    ///         nothing was duplicated.
    ///     </para>
    ///     <para>
    ///         <b>What does converge is the PLANE set.</b> For a convex body the outward normal at the
    ///         closest point is exactly normalize(query - point), so every sample yields a supporting plane
    ///         at no extra cost. Faces are finite, so clustering in plane space — normal direction plus
    ///         offset — converges on the hull's face count. A sample landing on an edge or a vertex gives a
    ///         normal that is not a face normal; those are a minority and appear as small clusters, which is
    ///         why cluster support counts are reported rather than hidden.
    ///     </para>
    /// </remarks>
    /// <summary>
    ///     A collider's pose as the SIMULATION has it, not as it is being drawn.
    /// </summary>
    /// <remarks>
    ///     A collider on an interpolated body inherits the interpolated root, so its Transform is a
    ///     rendering pose. The collider's offset from its body is rigid, so it can be taken from the
    ///     hierarchy and re-applied at <c>Rigidbody.position/rotation</c>. Everything that draws or
    ///     measures a collider goes through this, so a wireframe sits where PhysX has the shape and a
    ///     residual is computed against the shape PhysX actually used.
    /// </remarks>
    private static void SimulatedColliderPose(Collider col, out Vector3 pos, out Quaternion rot)
    {
        Transform t = col.transform;
        Rigidbody rb = col.attachedRigidbody;

        if (rb == null)
        {
            pos = t.position;
            rot = t.rotation;
            return;
        }

        Quaternion invBody = Quaternion.Inverse(rb.transform.rotation);
        Vector3 localPos = invBody * (t.position - rb.transform.position);
        Quaternion localRot = invBody * t.rotation;

        pos = rb.position + rb.rotation * localPos;
        rot = rb.rotation * localRot;
    }

    private static List<HullSample> SampleCookedHull(Collider col, int directions)
    {
        var outp = new List<HullSample>(directions);
        if (col == null) return outp;

        Vector3 centre = col.bounds.center;
        float far = col.bounds.size.magnitude * 8f + 1f;
        Vector3 pos;
        Quaternion rot;
        SimulatedColliderPose(col, out pos, out rot);

        for (int i = 0; i < directions; i++)
        {
            float k = (i + 0.5f) / directions;
            float phi = Mathf.Acos(1f - 2f * k);
            float theta = Mathf.PI * (1f + 2.2360679775f) * i;
            Vector3 d = new Vector3(
                Mathf.Sin(phi) * Mathf.Cos(theta), Mathf.Sin(phi) * Mathf.Sin(theta), Mathf.Cos(phi));

            Vector3 q = centre + d * far;
            Vector3 pt = Physics.ClosestPoint(q, col, pos, rot);
            Vector3 n = q - pt;
            if (n.sqrMagnitude < 1e-16f) continue;

            n.Normalize();
            outp.Add(new HullSample { Point = pt, Normal = n });
        }

        return outp;
    }

    /// <summary>A recovered face plane and how many samples landed on it.</summary>
    private struct HullPlane
    {
        public Vector3 Normal;
        public float Offset;
        public int Support;
    }

    /// <summary>
    ///     Collapses surface samples into distinct supporting planes, clustering on the NORMAL ALONE.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Normal only, because convexity makes the offset redundant AND harmful.</b> On a convex
    ///         body no two faces share an outward normal, so the normal already identifies the face
    ///         uniquely — the offset adds nothing. Worse, it actively breaks the clustering: the offset is
    ///         <c>n·p</c>, so on a blade half a metre long a mere 0.5 degree error in the estimated normal
    ///         moves the offset by about 4 mm, which blows straight through a sub-millimetre offset
    ///         tolerance. Including it produced ONE cluster per sample — 2000 samples, 2000 "planes" — and
    ///         the failure looked like a geometry result rather than an over-constrained test.
    ///     </para>
    /// </remarks>
    private static List<HullPlane> CollapseToPlanes(List<HullSample> samples, float normalDegrees)
    {
        var planes = new List<HullPlane>(256);
        float cosTol = Mathf.Cos(normalDegrees * Mathf.Deg2Rad);

        for (int i = 0; i < samples.Count; i++)
        {
            Vector3 n = samples[i].Normal;
            float d = Vector3.Dot(n, samples[i].Point);

            int hit = -1;
            for (int j = 0; j < planes.Count; j++)
            {
                if (Vector3.Dot(planes[j].Normal, n) < cosTol) continue;

                hit = j;
                break;
            }

            if (hit < 0)
            {
                planes.Add(new HullPlane { Normal = n, Offset = d, Support = 1 });
                continue;
            }

            // Running mean weighted by support, so a plane's estimate improves with every sample that lands
            // on it instead of being pinned to whichever sample happened to arrive first.
            HullPlane pl = planes[hit];
            float w = pl.Support;
            pl.Normal = ((pl.Normal * w) + n).normalized;
            pl.Offset = ((pl.Offset * w) + d) / (w + 1f);
            pl.Support += 1;
            planes[hit] = pl;
        }

        return planes;
    }

    /// <summary>
    ///     Asks how close a splat envelope built from probe samples actually is, by two independent routes.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Route 1 — direct residual, and it does not depend on how the splat is built.</b> Every
    ///         point of the true envelope is at distance exactly <c>r</c> from the hull. So take a candidate
    ///         envelope point <c>x = p + n*r</c>, query <see cref="Physics.ClosestPoint" /> again from
    ///         <c>x</c>, and measure <c>| |x - closest| - r |</c>. That is the error of our envelope point
    ///         against the real hull, measured by the same authority that defines the hull. It catches a bad
    ///         normal, a bad radius, or a bad assumption, without trusting any of them.
    ///     </para>
    ///     <para>
    ///         <b>Route 2 — coverage between samples.</b> A splat is a union of spheres of radius r centred
    ///         on sampled surface points, so between samples it dips INSIDE the true envelope. For samples
    ///         spaced <c>s</c> apart the shortfall is the sagitta, about <c>s^2 / (8r)</c>. Reporting the
    ///         nearest-neighbour spacing therefore converts sampling density into a millimetre error bound,
    ///         which is a statement about the splat rather than about the probe.
    ///     </para>
    ///     <para>
    ///         Together: route 1 says whether each splat centre sits in the right place, route 2 says
    ///         whether there are enough of them. Both are needed — a perfect residual on a sparse cloud
    ///         still leaves gaps.
    ///     </para>
    /// </remarks>
    public void ReportSplatFidelity(int directions)
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning("[BindMetricsV2] splat fidelity needs PLAY MODE.", this);
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("[BindMetricsV2] SPLAT FIDELITY from probe data, " + directions + " directions/side");

        RqBladeContact[] sides = { contactA, contactB };
        string[] tags = { "A", "B" };
        List<HullSample>[] keep = { retainedA, retainedB };

        for (int i = 0; i < 2; i++)
        {
            Collider col = ResolveLiveCollider(sides[i]);
            if (col == null) continue;

            string src;
            float r = LiveContactOffset(sides[i], out src);
            if (r <= 0f) continue;

            retainedRadius = r;
            List<HullSample> samples = SampleCookedHull(col, directions);
            keep[i].Clear();
            keep[i].AddRange(samples);

            // Route 1: residual of each envelope point against the real hull.
            float resMin = float.MaxValue, resMax = 0f, resSum = 0f;
            Vector3 pos;
            Quaternion rot;
            SimulatedColliderPose(col, out pos, out rot);
            for (int k = 0; k < samples.Count; k++)
            {
                Vector3 x = samples[k].Point + samples[k].Normal * r;
                Vector3 back = Physics.ClosestPoint(x, col, pos, rot);
                float e = Mathf.Abs((x - back).magnitude - r);
                if (e < resMin) resMin = e;
                if (e > resMax) resMax = e;
                resSum += e;
            }

            // Route 2: nearest-neighbour spacing of the sample cloud.
            float nnMin = float.MaxValue, nnMax = 0f, nnSum = 0f;
            int n = samples.Count;
            for (int k = 0; k < n; k++)
            {
                float best = float.MaxValue;
                for (int j = 0; j < n; j++)
                {
                    if (j == k) continue;
                    float d2 = (samples[j].Point - samples[k].Point).sqrMagnitude;
                    if (d2 < best) best = d2;
                }

                float d = Mathf.Sqrt(best);
                if (d < nnMin) nnMin = d;
                if (d > nnMax) nnMax = d;
                nnSum += d;
            }

            float nnMean = n > 0 ? nnSum / n : 0f;

            sb.AppendLine("");
            sb.AppendLine("  side " + tags[i] + ": " + col.name + "   r=" + F(r) + " m   samples=" + n);
            sb.AppendLine("    ROUTE 1  envelope-point residual against the real hull");
            sb.AppendLine("       min " + F(resMin) + "   mean " + F(n > 0 ? resSum / n : 0f) +
                          "   max " + F(resMax) + "   metres");
            sb.AppendLine("    ROUTE 2  sample spacing, and the coverage shortfall it implies");
            sb.AppendLine("       spacing  min " + F(nnMin) + "   mean " + F(nnMean) +
                          "   max " + F(nnMax) + "   metres");
            sb.AppendLine("       implied sagitta s^2/(8r):  at mean " + F(nnMean * nnMean / (8f * r)) +
                          "   at max " + F(nnMax * nnMax / (8f * r)) + "   metres");
        }

        sb.AppendLine("");
        sb.AppendLine("  Probe cloud retained — enable drawSplatWireframe to see it.");
        Debug.Log(sb.ToString(), this);
    }

    [ContextMenu("Splat fidelity (2000 directions)")]
    public void ReportSplatFidelityDefault()
    {
        ReportSplatFidelity(2000);
    }

    /// <summary>One scan per side, then the recovered PLANE count at several clustering tolerances.</summary>
    /// <remarks>
    ///     Read convergence off this: the plane count should stop rising as the direction count rises,
    ///     unlike the point count which grew linearly. If it keeps rising, the clustering tolerance is too
    ///     tight for the sampling density and is splitting single faces.
    /// </remarks>
    public void ReportCookedHullScan(int directions)
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning("[BindMetricsV2] the cooked-hull scan needs PLAY MODE — neither the proxy " +
                             "collider nor the cooked hull exists in edit mode.", this);
            return;
        }

        float[] angles = { 0.25f, 0.5f, 1f, 2f, 5f };

        var sb = new StringBuilder();
        sb.AppendLine("[BindMetricsV2] cooked-hull PLANE scan, " + directions + " directions per side");

        RqBladeContact[] sides = { contactA, contactB };
        string[] tags = { "A", "B" };

        for (int i = 0; i < 2; i++)
        {
            Collider col = ResolveLiveCollider(sides[i]);
            if (col == null) continue;

            var mc = col as MeshCollider;
            List<HullSample> samples = SampleCookedHull(col, directions);

            sb.AppendLine("");
            sb.AppendLine("  side " + tags[i] + ": " + col.name + "   sourceMeshTris=" +
                          (mc == null || mc.sharedMesh == null ? -1 : mc.sharedMesh.triangles.Length / 3) +
                          "   samples=" + samples.Count);

            for (int an = 0; an < angles.Length; an++)
            {
                List<HullPlane> pl = CollapseToPlanes(samples, angles[an]);
                int weak = 0;
                int strongest = 0;
                for (int k = 0; k < pl.Count; k++)
                {
                    if (pl[k].Support <= 2) weak++;
                    if (pl[k].Support > strongest) strongest = pl[k].Support;
                }

                sb.AppendLine("     normal<" + F(angles[an]) + "deg  ->  " + pl.Count +
                              " planes   (" + weak + " with <=2 samples, largest cluster " +
                              strongest + ")");
            }
        }

        Debug.Log(sb.ToString(), this);
    }

    [ContextMenu("Cooked-hull plane scan (2000 directions)")]
    public void ReportCookedHullScanDefault()
    {
        ReportCookedHullScan(2000);
    }

    /// <summary>
    ///     The ENTIRE Minkowski envelope of the blade shell, drawn in full: face offsets, edge cylinders and
    ///     vertex spheres, for every authored feature.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The envelope of a body at distance r is its Minkowski sum with a sphere of radius r, and for a
    ///         polyhedron that union is exactly three things: every FACE pushed out along its outward normal
    ///         by r, a CYLINDER of radius r along every EDGE, and a SPHERE of radius r at every VERTEX. All
    ///         three are drawn here for the whole shell — nothing is sampled and no feature is skipped,
    ///         because the point of this layer is the true size and shape of the envelope over the entire
    ///         blade geometry rather than around the cutting edges alone.
    ///     </para>
    ///     <para>
    ///         <b>It is genuinely expensive.</b> 2492 surfaces and 1236 edges per blade, so even at the
    ///         minimum tessellation this is tens of thousands of gizmo lines per blade per frame and the
    ///         Scene view will slow markedly. That is inherent to drawing the whole envelope, not a defect.
    ///         The two segment counts control tessellation only; they never drop geometry.
    ///     </para>
    ///     <para>
    ///         <b>Outwardness is resolved against the shell centre, not from winding.</b>
    ///         <see cref="BladeSurface.LocalNormal" /> is a raw cross product of the triangle's own vertex
    ///         order and the builder does not guarantee side facets wind outward, so a normal is flipped when
    ///         it points back toward the shell's bounding centre. Offsetting a face the wrong way would put
    ///         part of the envelope inside the blade.
    ///     </para>
    /// </remarks>
    private void DrawFullMinkowski(Side s, float r)
    {
        if (s.Blade == null) return;

        BladeShellData d = s.Blade.Data;
        if (d == null) return;

        BladePose pose = s.Blade.SimulatedPlacement;
        Gizmos.color = fullMinkowskiColour;

        Vector3 shellCentre = pose.TransformPoint(d.BoundingCentre);

        // FACES: every authored surface pushed out along its outward normal.
        for (int i = 0; i < d.SurfaceCount; i++)
        {
            BladeSurface f = d.GetSurface(i);
            Vector3 a0 = pose.TransformPoint(f.LocalA);
            Vector3 a1 = pose.TransformPoint(f.LocalB);
            Vector3 a2 = pose.TransformPoint(f.LocalC);

            Vector3 nrm = Vector3.Cross(a1 - a0, a2 - a0);
            if (nrm.sqrMagnitude < 1e-16f) continue;
            nrm.Normalize();

            Vector3 mid = (a0 + a1 + a2) / 3f;
            if (Vector3.Dot(nrm, mid - shellCentre) < 0f) nrm = -nrm;

            Vector3 o = nrm * r;
            Gizmos.DrawLine(a0 + o, a1 + o);
            Gizmos.DrawLine(a1 + o, a2 + o);
            Gizmos.DrawLine(a2 + o, a0 + o);
        }

        // EDGES: a cylinder of radius r along every authored edge. VERTICES: a sphere at every endpoint.
        for (int i = 0; i < d.EdgeCount; i++)
        {
            BladeEdgeLine e = d.GetEdge(i);
            Vector3 p0 = pose.TransformPoint(e.LocalStart);
            Vector3 p1 = pose.TransformPoint(e.LocalEnd);

            Vector3 dir = p1 - p0;
            if (dir.sqrMagnitude < 1e-16f) continue;
            dir.Normalize();

            Vector3 u = Vector3.Cross(dir, Vector3.up);
            if (u.sqrMagnitude < 1e-8f) u = Vector3.Cross(dir, Vector3.right);
            u.Normalize();
            Vector3 v = Vector3.Cross(dir, u).normalized;

            for (int k = 0; k < minkowskiCylinderSides; k++)
            {
                float ang = k * 2f * Mathf.PI / minkowskiCylinderSides;
                Vector3 off = (u * Mathf.Cos(ang) + v * Mathf.Sin(ang)) * r;
                Gizmos.DrawLine(p0 + off, p1 + off);
            }

            DrawSphereWire(p0, r, minkowskiSphereSegments);
            DrawSphereWire(p1, r, minkowskiSphereSegments);
        }
    }

    /// <summary>Three great circles, which is the cheapest drawing that still reads as a sphere.</summary>
    private static void DrawSphereWire(Vector3 centre, float r, int segments)
    {
        DrawArc(centre, Vector3.right, Vector3.up, r, 0f, 360f, segments);
        DrawArc(centre, Vector3.up, Vector3.forward, r, 0f, 360f, segments);
        DrawArc(centre, Vector3.forward, Vector3.right, r, 0f, 360f, segments);
    }

    /// <summary>
    ///     The speculative envelope around a ridge, drawn as what it actually is: a CAPSULE.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The set of points within distance <paramref name="r" /> of a line segment is the segment's
    ///         Minkowski sum with a sphere — a cylinder with hemispherical caps. So this is not an
    ///         approximation of the envelope around the authored cutting edge, it is the envelope, and the
    ///         fact that it comes out as a rounded pill is the finding rather than the drawing style.
    ///     </para>
    ///     <para>
    ///         Cross-section rings plus four silhouette lines plus hemispherical caps, so it reads as a
    ///         rounded solid rather than as a stack of hoops. Whatever the authored apex angle is, the
    ///         envelope's surface is smooth at radius r: no part of this shape is sharp.
    ///     </para>
    /// </remarks>
    private static void DrawWireCapsule(
        Vector3 p0, Vector3 p1, Vector3 dir, Vector3 u, Vector3 v, float r, int rings)
    {
        for (int k = 0; k < rings; k++)
        {
            Vector3 c = Vector3.Lerp(p0, p1, rings == 1 ? 0f : (float)k / (rings - 1));
            DrawArc(c, u, v, r, 0f, 360f);
        }

        // Silhouette lines, so the rings read as one surface.
        DrawSeg(p0 + u * r, p1 + u * r);
        DrawSeg(p0 - u * r, p1 - u * r);
        DrawSeg(p0 + v * r, p1 + v * r);
        DrawSeg(p0 - v * r, p1 - v * r);

        // Hemispherical caps: the rounding that replaces the sharp ends of the ridge.
        DrawArc(p0, u, -dir, r, 0f, 180f);
        DrawArc(p0, v, -dir, r, 0f, 180f);
        DrawArc(p1, u, dir, r, 0f, 180f);
        DrawArc(p1, v, dir, r, 0f, 180f);
    }

    private static void DrawSeg(Vector3 a, Vector3 b)
    {
        Gizmos.DrawLine(a, b);
    }

    /// <summary>Arc of radius r in the plane spanned by u and v, from <paramref name="fromDeg" />.</summary>
    private static void DrawArc(
        Vector3 centre, Vector3 u, Vector3 v, float r, float fromDeg, float toDeg, int segments = 20)
    {
        Vector3 prev = Vector3.zero;
        for (int i = 0; i <= segments; i++)
        {
            float ang = Mathf.Deg2Rad * Mathf.Lerp(fromDeg, toDeg, (float)i / segments);
            Vector3 p = centre + (u * Mathf.Cos(ang) + v * Mathf.Sin(ang)) * r;
            if (i > 0) Gizmos.DrawLine(prev, p);
            prev = p;
        }
    }

    /// <summary>The proxy blade collider if it can be resolved, else the authored one.</summary>
    private static Collider ResolveLiveCollider(RqBladeContact blade)
    {
        Collider authored = blade == null ? null : blade.BladeCollider;
        Transform t = blade == null ? null : blade.ActiveTransform;
        if (t == null || authored == null) return authored;

        var candidates = t.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < candidates.Length; i++)
        {
            if (candidates[i] == authored) continue;
            if (candidates[i].name.StartsWith(authored.name)) return candidates[i];
        }

        return authored;
    }

    private void DrawWitness()
    {
        if (solver == null || contactA == null || contactB == null) return;

        RqBladeTangentialSolver.Snapshot snap;
        if (!solver.TryGetSnapshot(contactA, contactB, out snap) || !snap.ClassificationValid) return;

        // The snapshot carries ids and types but not world witnesses, so the witness cross is drawn from
        // the blades' current poses only when the solver exposes them; otherwise this stays silent rather
        // than inventing a position.
        Vector3 mid = (contactA.SimulatedPlacement.TransformPoint(Vector3.zero) + contactB.SimulatedPlacement.TransformPoint(Vector3.zero)) * 0.5f;
        for (int axis = 0; axis < 3; axis++)
        {
            Vector3 d = axis == 0 ? Vector3.right : axis == 1 ? Vector3.up : Vector3.forward;
            var lr = MakeLine("V2_WitnessCross");
            lr.material.color = witnessColour;
            lr.startColor = lr.endColor = witnessColour;
            lr.SetPosition(0, mid - d * witnessCrossSize);
            lr.SetPosition(1, mid + d * witnessCrossSize);
            extraLines.Add(lr);
        }
    }

    /// <summary>
    ///     Q1's readout: what is attributed right now, which ring it is, and which rings are authored sharp.
    /// </summary>
    /// <remarks>
    ///     It states the attributed ring and the authored sharp rings side by side and stops there. It does
    ///     not print a verdict, because "the line is on the cutting edge" is the judgement being asked of a
    ///     human, and a component that answered it would be answering its own test.
    /// </remarks>
    private void OnGUI()
    {
        if (!drawAttributionReadout || solver == null || contactA == null || contactB == null) return;

        RqBladeTangentialSolver.Snapshot snap;
        bool has = solver.TryGetSnapshot(contactA, contactB, out snap) && snap.ClassificationValid;

        EnsureBuilt(a, contactA);
        EnsureBuilt(b, contactB);

        var sb = new StringBuilder();
        sb.AppendLine("BIND METRICS V2 — attribution readout");

        if (!has)
        {
            sb.AppendLine("no valid classification (the pair exists only once PhysX reports a contact)");
        }
        else
        {
            sb.AppendLine("scenario " + snap.Scenario);
            sb.AppendLine("  A  " + snap.RawTypeA + "  " + snap.RawIdA +
                          "   ring " + (RingOf(snap.RawIdA) ?? "?") +
                          "   authored sharp rings: " + SharpRings(a));
            sb.AppendLine("  B  " + snap.RawTypeB + "  " + snap.RawIdB +
                          "   ring " + (RingOf(snap.RawIdB) ?? "?") +
                          "   authored sharp rings: " + SharpRings(b));
        }

        GUI.color = Color.black;
        GUI.Label(new Rect(13, 163, 1000, 90), sb.ToString());
        GUI.color = Color.white;
        GUI.Label(new Rect(12, 162, 1000, 90), sb.ToString());
    }

    private static string SharpRings(Side s)
    {
        if (s.Runs.Count == 0) return "(none built)";

        var sb = new StringBuilder();
        for (int i = 0; i < s.Runs.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(s.Runs[i].Ring);
        }

        return sb.ToString();
    }

    // ----- reporting -------------------------------------------------------------------------------

    private static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>
    ///     Reports each authored sharp edge as a line, in 3D and as slope-intercept in all three world
    ///     planes.
    /// </summary>
    /// <remarks>
    ///     <b>All three planes on purpose.</b> A slope-intercept form only exists relative to a chosen
    ///     projection plane, and picking one here would be inventing a convention. The expected mirror
    ///     relation between the two blades — A near <c>y = mx + b</c> and B near <c>y = -mx - b</c> — will
    ///     appear in whichever plane actually contains both edge directions; emitting all three lets that
    ///     plane identify itself instead of being assumed. Which plane becomes the declared reporting frame
    ///     is a decision to record, not one to bury in code.
    /// </remarks>
    [ContextMenu("Log edge equations")]
    public void LogEquations()
    {
        EnsureBuilt(a, contactA);
        EnsureBuilt(b, contactB);

        var sb = new StringBuilder();
        sb.AppendLine("[BindMetricsV2] authored SharpEdge lines, fitted from the authored shell.");
        Report(sb, a, "A");
        Report(sb, b, "B");
        Debug.Log(sb.ToString(), this);
    }

    private void Report(StringBuilder sb, Side s, string label)
    {
        if (s.Blade == null)
        {
            sb.AppendLine("  " + label + ": no blade assigned.");
            return;
        }

        BladePose pose = s.Blade.SimulatedPlacement;
        string offsetSource;
        float offset = LiveContactOffset(s.Blade, out offsetSource);

        sb.AppendLine("  " + label + " = " + s.Blade.Label + "  (" + s.Runs.Count + " authored SharpEdge run(s))");
        Collider authoredCol = s.Blade.BladeCollider;
        sb.AppendLine("    speculative contact skin: contactOffset IN FORCE = " + F(offset) + " m  from " +
                      offsetSource);
        sb.AppendLine("      authored collider offset = " +
                      (authoredCol == null ? "n/a" : F(authoredCol.contactOffset)) +
                      " m   (reported alongside so a proxy/authored mismatch is visible, not substituted)");
        sb.AppendLine("      both blades carry one, so a pair is reported at roughly their SUM = " +
                      F(offset * 2f) + " m");

        for (int i = 0; i < s.Runs.Count; i++)
        {
            EdgeRun r = s.Runs[i];
            Vector3 wc = pose.TransformPoint(r.LocalCentroid);
            Vector3 wd = pose.Rotation * r.LocalDirection;
            float length = r.MaxT - r.MinT;

            sb.AppendLine("    ring " + r.Ring + ": " + r.Segments + " segments, length " + F(length) + " m");
            sb.AppendLine("      straightness: residual max " + F(r.ResidualMax) + " m, rms " +
                          F(r.ResidualRms) + " m");
            sb.AppendLine("      local  P " + r.LocalCentroid.ToString("F6") + "  d " +
                          r.LocalDirection.ToString("F6"));
            sb.AppendLine("      world  P " + wc.ToString("F6") + "  d " + wd.ToString("F6"));
            sb.AppendLine("      world XY: " + SlopeIntercept(wc.x, wc.y, wd.x, wd.y, "x", "y"));
            sb.AppendLine("      world XZ: " + SlopeIntercept(wc.x, wc.z, wd.x, wd.z, "x", "z"));
            sb.AppendLine("      world ZY: " + SlopeIntercept(wc.z, wc.y, wd.z, wd.y, "z", "y"));
            sb.AppendLine(BluntingReport(r, offset));
        }
    }

    /// <summary>
    ///     What the speculative envelope does to an authored sharp edge, in numbers.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Both inputs are measured, not assumed: the wedge angle is the authored
    ///         <see cref="BladeEdgeLine.IncludedAngleDegrees" /> from the profile, and the radius is the
    ///         <c>contactOffset</c> read off the collider that actually simulates.
    ///     </para>
    ///     <para>
    ///         <b>The geometry.</b> For a wedge of included angle <c>2a</c>, the Minkowski sum with a sphere
    ///         of radius <c>r</c> replaces the apex with a circular arc of radius <c>r</c> centred ON the
    ///         apex. So the envelope's foremost point sits <c>r</c> ahead of the real apex, and the sharp
    ///         line becomes an arc subtending <c>180 - 2a</c> degrees. The apex is not merely blunted, it is
    ///         replaced by a cylinder of radius r running the length of the edge.
    ///     </para>
    ///     <para>
    ///         <b>The contrast that matters.</b> Offsetting the FACES and letting them meet — which is what
    ///         "inflate the wireframe" does — puts a still-sharp false apex at <c>r / sin(a)</c>, much
    ///         further out. Reporting both shows why the rings are the honest drawing: the naive
    ///         construction is wrong in position AND wrong in sharpness.
    ///     </para>
    /// </remarks>
    private static string BluntingReport(EdgeRun run, float r)
    {
        if (r <= 0f) return "      blunting: contactOffset is 0 or unresolved — nothing to report";

        float included = 0.5f * (run.IncludedAngleMin + run.IncludedAngleMax);
        float halfRad = included * 0.5f * Mathf.Deg2Rad;
        float sinHalf = Mathf.Sin(halfRad);

        float arcDeg = 180f - included;
        float arcLen = r * arcDeg * Mathf.Deg2Rad;
        float naive = sinHalf > 1e-6f ? r / sinHalf : float.PositiveInfinity;

        var sb = new StringBuilder();
        sb.AppendLine("      BLUNTING by the speculative envelope (authored angle x live contactOffset):");
        sb.AppendLine("        authored included angle = " + F(run.IncludedAngleMin) + " to " +
                      F(run.IncludedAngleMax) + " deg");
        sb.AppendLine("        envelope radius r       = " + F(r) + " m");
        sb.AppendLine("        apex becomes an arc of  = " + F(arcDeg) + " deg, arc length " +
                      F(arcLen) + " m");
        sb.AppendLine("        effective tip radius    = " + F(r) +
                      " m   (the authored apex is a LINE, radius ~0)");
        sb.AppendLine("        naive face-offset apex  = " + F(naive) +
                      " m ahead and still SHARP — the WRONG construction, shown for contrast");
        sb.Append("        the two blades' skins meet at " + F(r * 2f) + " m of surface separation");
        return sb.ToString();
    }

    /// <summary>
    ///     Slope-intercept in one plane, or an explicit statement that the line is vertical there.
    /// </summary>
    /// <remarks>
    ///     A vertical line has no finite slope, and emitting a huge number for it would read as a real
    ///     measurement. The degenerate case is named instead.
    /// </remarks>
    private static string SlopeIntercept(float pu, float pv, float du, float dv, string u, string v)
    {
        if (Mathf.Abs(du) < 1e-6f)
            return "vertical in this plane, " + u + " = " + F(pu) + " (no finite slope)";

        float m = dv / du;
        float b = pv - m * pu;
        return v + " = " + F(m) + " * " + u + " + " + F(b);
    }
}
