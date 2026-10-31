using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
///     Draws the quantities this rig is argued about but cannot normally be seen: the collider's speculative
///     contact skin, each blade's mid-plane, the signed side margin that decides a crossing, the blade frame,
///     and the per-step travel budget that governs tunnelling.
///
///     <para>
///         <b>Pure visualisation. Touches nothing.</b> Every method here reads transforms, colliders and
///         rigidbodies inside <c>OnDrawGizmos</c>; nothing is written, so enabling any of these cannot change a
///         run. Safe to leave attached during measured runs.
///     </para>
///     <para>
///         Everything defaults to OFF. Turn on only the layer being discussed — all of them at once is
///         unreadable, and the skin shells in particular hide the geometry they surround.
///     </para>
///     Put one on each Station root. It resolves both blades from the station's own <c>Sword_A__*</c> /
///     <c>Sword_B__*</c> children, by the frozen naming convention.
/// </summary>
[DisallowMultipleComponent]
[ExecuteAlways]
public class StationDebugGizmos : MonoBehaviour
{
    [Header("Wiring (empty = resolved by name from this station root)")]
    [Tooltip("The station's two authored swords. Empty, they are found as Sword_A__<station> / " +
             "Sword_B__<station>. In play mode these are kinematic visual followers of the generated " +
             ":Attachable Physics proxies, tracking them to 0.013-0.029 mm - near enough for a drawn gizmo, " +
             "and NOT to be read as rigidbodies: the authored body reports no contacts by design.")]
    public Transform swordA;

    public Transform swordB;

    [Header("Contact skin — Collider.contactOffset")]
    [Tooltip("Draw each blade's true oriented box and the same box inflated by contactOffset. The inflated " +
             "shell is where PhysX BEGINS generating contacts, so if the combined skin (both blades) is wider " +
             "than the side margin the blades must maintain, the side geometry sits inside the speculative " +
             "margin. Box is an OBB approximation of the convex hull, not the hull itself.")]
    public bool showContactSkin;

    [Header("Crossing geometry")]
    [Tooltip("Each blade's mid-plane, i.e. the plane a crossing is measured against (normal = the blade's " +
             "thin axis).")]
    public bool showMidPlanes;

    [Tooltip("The signed side margin: the line between the two blade CENTRELINES at their closest approach. " +
             "Green while both blades are on their original side, red once the sign has flipped (crossed).")]
    public bool showSideMargin;

    [Tooltip("Guard, tip, long axis (blue), edge direction (red), face normal (green) per blade.")]
    public bool showBladeFrames;

    [Header("Tunnelling budget (play mode only)")]
    [Tooltip("Ray from each blade's contact-relevant point along its MATERIAL-POINT velocity (v + omega x r), " +
             "scaled by one fixed timestep. Yellow under half a blade thickness, red over — red means the " +
             "blade can cross its own thickness within a single step, which linear CCD cannot catch.")]
    public bool showStepTravel;

    [Header("Edge candidates — READS an existing convention, defines none")]
    [Tooltip("Which of the rig's two EXISTING edge conventions to draw. They are deliberately independent and " +
             "are NOT reconciled here — drawing them is how you see whether they agree at a given pose.\n\n" +
             "PhysicalQuillon: StartStateProbe.CrossSection() applied to the quillon marker direction, i.e. the " +
             "registered physical edge. This is the convention every measured column is expressed in.\n\n" +
             "GeometricExtents: this component's own Frame(), i.e. the middle mesh extent. Available with no " +
             "markers and no play mode, but it is a mesh property, not a registered one.")]
    public EdgeConvention edgeConvention = EdgeConvention.PhysicalQuillon;

    [Tooltip("Both long edges of both blades, as polylines walked along the blade. Sampled from the COOKED HULL " +
             "via Collider.ClosestPoint — the same ruler the interpenetration audit trusts — not from mesh " +
             "vertices: the source blade mesh is non-convex (the fuller, bulging to 8.5/17.7 mm) so its vertices " +
             "are not hull points, and its two extreme-|edge| vertices both sit at the guard.")]
    public bool showEdgePolylines;

    [Tooltip("Closest approach between A's edge polylines and B's, over all four (A edge, B edge) pairings. " +
             "Draws the winning pairing and labels its separation plus the position along each blade. THIS is " +
             "the edge-on-edge readout: it says which pair of edges is engaged and where, which a hull-wide " +
             "closest-point cannot (that pair can sit on a flat).")]
    public bool showEdgePairClosest;

    [Tooltip("At the contact's axial station, the blade's local half-width x half-thickness rectangle from " +
             "StartStateProbe.CrossSectionExtents(), with the contact plotted inside it and the normalised " +
             "presentation angle. Draw this before proposing any new edge metric: the rectangle is 4-8x wider " +
             "than it is thick at real contact stations (measured on these hulls 2026-08-06), which is why a RAW " +
             "atan2(face, edge) read 1-27 deg for edge and flat alike (measured 2026-08-02, retired) and why the " +
             "half-extent normalisation is not optional. Checked against synthetic points on these hulls: a point " +
             "on the edge extreme reads 0.00 deg and one on the flat reads 90.00 deg.")]
    public bool showContactCrossSection;

    [Tooltip("Marks in red any edge-polyline point that lies INSIDE the opposing cooked hull " +
             "(ClosestPoint(p) == p). This is the state an unsigned hull closest-pair cannot represent — the retired " +
             "BindMetrics reported 6.092 mm of gap for the EdgeBind SNAP pose whose edges were mutually buried " +
             "1.10 mm, because such a measure cannot go negative.")]
    public bool showEdgeContainment;

    [Tooltip("Stations sampled along each blade. The hull is coarse (~50 verts over a metre), so a high count " +
             "does not buy real detail — it resamples the same few facets.")]
    [Range(4, 64)] public int edgeStations = 24;

    [Tooltip("How far along +/-edge the hull is probed from the blade axis (m). Must exceed the blade half-width; " +
             "the returned point is on the hull, so this only has to be generous, not accurate.")]
    public float edgeProbeM = 0.5f;

    [Header("Wiring for the physical convention (empty = resolved by name from this station root)")]
    [Tooltip("The station's six SwordRef leaf points - SwordRef_{A,B}_*/{Tip,Q1,Q2}. They are STATIC station " +
             "children: in EDIT mode they are read directly, and in PLAY mode they are converted once at " +
             "Start() into fixed local offsets on each sword so the frame rides the blade. That calibration " +
             "is a private cache, never a write to the scene, which this class must never do.")]
    public Transform refATip;

    public Transform refAQ1;
    public Transform refAQ2;
    public Transform refBTip;
    public Transform refBQ1;
    public Transform refBQ2;

    [Header("Appearance")]
    public float planeSize = 0.25f;

    public float skinExaggeration = 1f;

    [Tooltip("Colours avoid the 20 permanent RefCurve_* marker-trajectory renderers already in the scene " +
             "(azure / yellow / magenta / orange). A collision there once turned 'yes those are the ones' into " +
             "an ambiguous confirmation that got recorded as visual validation.")]
    public Color edgePlusColour = Color.white;

    public Color edgeMinusColour = new Color(0.15f, 0.95f, 0.45f);

    /// <summary>
    ///     Draw the edge polylines as SOLID LineRenderers instead of gizmo lines. Same geometry as
    ///     <see cref="showEdgePolylines" /> — same conventions, same cooked-hull sampling — but rendered as real
    ///     unlit tubes, so they are visible in the GAME view and in a build, at a width you can actually see, and
    ///     they ignore every gizmo toggle.
    ///     <para>
    ///         <b>Why both exist.</b> Gizmo lines are hairline and Scene-view only, which is right for a dense
    ///         diagnostic layer. When the question is "am I looking at the right edge?" a 4 mm bright tube answers
    ///         it in one glance and a hairline does not. Noah asked for this specifically after the throwaway
    ///         play-mode version was lost on every stop.
    ///     </para>
    ///     <para>
    ///         <b>Still touches nothing.</b> The renderers are created with
    ///         <see cref="HideFlags.DontSave" />, so they are never serialized into the scene, never dirty it and
    ///         never appear in a diff. They are rebuilt each frame from the same lists the gizmo path uses and
    ///         destroyed the moment this is switched off or the component is disabled.
    ///     </para>
    /// </summary>
    [Tooltip("Solid LineRenderer version of the edge polylines - visible in the GAME view too, unlike gizmos. " +
             "Same geometry and same conventions as showEdgePolylines; only the rendering differs. Created with " +
             "HideFlags.DontSave so they never enter the scene file. Also draws the winning edge pair in white.")]
    public bool showEdgeRenderers;

    [Tooltip("Width of the solid edge lines, metres. The blade is 5 mm thick and resting gaps are ~0.02 mm, so " +
             "do NOT judge contact by eye from these - they are for identifying which edge, not measuring it.")]
    [Range(0.0005f, 0.02f)] public float edgeRendererWidth = 0.004f;

    private readonly List<Vector3> _eAPlus = new List<Vector3>();
    private readonly List<Vector3> _eAMinus = new List<Vector3>();
    private readonly List<Vector3> _eBPlus = new List<Vector3>();
    private readonly List<Vector3> _eBMinus = new List<Vector3>();

    /// <summary>The rig's two existing edge conventions. Adding a third is not a fix — see the field tooltip.</summary>
    public enum EdgeConvention
    {
        PhysicalQuillon,
        GeometricExtents
    }

    private readonly Vector3[] _localGuard = new Vector3[2];
    private readonly Vector3[] _localTip = new Vector3[2];
    private readonly Vector3[] _localQuillon = new Vector3[2];
    private bool _frameCalibrated;

    /// <summary>True once the marker frame has been calibrated onto the blades (play mode only).</summary>
    public bool FrameCalibrated => _frameCalibrated;

    private void Reset()
    {
        ResolveRefs();
    }

    /// <remarks>
    ///     <c>[ExecuteAlways]</c> runs <c>Start</c> in EDIT mode too, so the serialized write in
    ///     <see cref="ResolveRefs" /> is gated on play mode: a write from edit mode would dirty the scene, which
    ///     this class promises never to do. In edit mode the lazy accessors below resolve the same objects
    ///     without storing anything.
    /// </remarks>
    private void Start()
    {
        if (!Application.isPlaying) return;
        ResolveRefs();
        CalibrateBladeFrames();
    }

    /// <summary>
    ///     Fill anything left empty from the frozen station naming convention. Called from <c>Reset</c> (a user
    ///     action) and from <c>Start</c> in PLAY mode only, where a write is discarded on exit.
    /// </summary>
    private void ResolveRefs()
    {
        if (!name.StartsWith("Station_")) return;
        if (swordA == null) swordA = transform.Find("Sword_A__" + name);
        if (swordB == null) swordB = transform.Find("Sword_B__" + name);

        // The SwordRef children are deliberately not arm-suffixed (SwordRef_A_Bind under an OurSolution
        // station), so they are matched by prefix rather than by a name built from the root.
        var sra = ChildStartingWith("SwordRef_A_");
        var srb = ChildStartingWith("SwordRef_B_");
        if (sra != null)
        {
            if (refATip == null) refATip = sra.Find("Tip");
            if (refAQ1 == null) refAQ1 = sra.Find("Q1");
            if (refAQ2 == null) refAQ2 = sra.Find("Q2");
        }

        if (srb == null) return;
        if (refBTip == null) refBTip = srb.Find("Tip");
        if (refBQ1 == null) refBQ1 = srb.Find("Q1");
        if (refBQ2 == null) refBQ2 = srb.Find("Q2");
    }

    private Transform ChildStartingWith(string prefix)
    {
        for (var i = 0; i < transform.childCount; i++)
        {
            var c = transform.GetChild(i);
            if (c.name.StartsWith(prefix)) return c;
        }

        return null;
    }

    /// <summary>
    ///     Every reference, resolved lazily and WITHOUT caching back into the serialized field. That assignment
    ///     would be a write, and a write from an edit-mode gizmo pass dirties the scene.
    /// </summary>
    private Transform SwordA => swordA != null ? swordA : transform.Find("Sword_A__" + name);

    private Transform SwordB => swordB != null ? swordB : transform.Find("Sword_B__" + name);

    private Transform RefATip => refATip != null ? refATip : Leaf("SwordRef_A_", "Tip");
    private Transform RefAQ1 => refAQ1 != null ? refAQ1 : Leaf("SwordRef_A_", "Q1");
    private Transform RefAQ2 => refAQ2 != null ? refAQ2 : Leaf("SwordRef_A_", "Q2");
    private Transform RefBTip => refBTip != null ? refBTip : Leaf("SwordRef_B_", "Tip");
    private Transform RefBQ1 => refBQ1 != null ? refBQ1 : Leaf("SwordRef_B_", "Q1");
    private Transform RefBQ2 => refBQ2 != null ? refBQ2 : Leaf("SwordRef_B_", "Q2");

    private Transform Leaf(string parentPrefix, string leaf)
    {
        var p = ChildStartingWith(parentPrefix);
        return p != null ? p.Find(leaf) : null;
    }

    /// <summary>
    ///     Turn the static SwordRef leaf positions into fixed local offsets on each sword, once, from the
    ///     authored pose at <c>Start</c>. This is what <c>BindMetrics</c> used to carry; it lives here now
    ///     because that component is deleted and the marker convention is not.
    /// </summary>
    private bool CalibrateBladeFrames()
    {
        _frameCalibrated = false;
        for (var i = 0; i < 2; i++)
        {
            var isA = i == 0;
            var sw = isA ? SwordA : SwordB;
            var q1 = isA ? RefAQ1 : RefBQ1;
            var q2 = isA ? RefAQ2 : RefBQ2;
            var tp = isA ? RefATip : RefBTip;
            if (sw == null || q1 == null || q2 == null || tp == null) return false;
            var quillon = (q1.position - q2.position).normalized;
            if (quillon.sqrMagnitude < 1e-10f) return false;
            _localGuard[i] = sw.InverseTransformPoint((q1.position + q2.position) * 0.5f);
            _localTip[i] = sw.InverseTransformPoint(tp.position);
            _localQuillon[i] = sw.InverseTransformDirection(quillon);
        }

        _frameCalibrated = true;
        return true;
    }

    /// <summary>
    ///     Blade frame straight from the collider transform and its mesh extents, so this works in EDIT mode as
    ///     well. The thinnest local extent is the face normal, the longest is the blade axis, the remaining one
    ///     is the edge direction. Deliberately independent of the MARKER frame, which is only calibrated onto
    ///     the blades at <c>Start()</c> and so would give nothing outside play mode.
    /// </summary>
    private static bool Frame(Collider col, out Vector3 centre, out Vector3 axis, out Vector3 edge,
        out Vector3 face, out Vector3 half)
    {
        centre = axis = edge = face = half = Vector3.zero;
        var mc = col as MeshCollider;
        if (mc == null || mc.sharedMesh == null) return false;
        var b = mc.sharedMesh.bounds;
        var t = col.transform;
        var ext = b.extents;

        int iLong = ext.x >= ext.y && ext.x >= ext.z ? 0 : ext.y >= ext.z ? 1 : 2;
        int iThin = ext.x <= ext.y && ext.x <= ext.z ? 0 : ext.y <= ext.z ? 1 : 2;
        if (iThin == iLong) return false;
        int iMid = 3 - iLong - iThin;

        Vector3 Local(int i) => i == 0 ? Vector3.right : i == 1 ? Vector3.up : Vector3.forward;
        axis = t.TransformDirection(Local(iLong)).normalized;
        face = t.TransformDirection(Local(iThin)).normalized;
        edge = t.TransformDirection(Local(iMid)).normalized;
        centre = t.TransformPoint(b.center);
        half = new Vector3(ext[iLong], ext[iMid], ext[iThin]); // length, width, thickness (local units)
        var s = t.lossyScale;
        half = new Vector3(half.x * Mathf.Abs(s[iLong]), half.y * Mathf.Abs(s[iMid]), half.z * Mathf.Abs(s[iThin]));
        return true;
    }

    private void OnDrawGizmos()
    {
        Transform swA = SwordA, swB = SwordB;
        if (swA == null || swB == null) return;
        var colA = Blade(swA);
        var colB = Blade(swB);
        if (colA == null || colB == null) return;

        Vector3 cA, axA, edA, faA, hA, cB, axB, edB, faB, hB;
        if (!Frame(colA, out cA, out axA, out edA, out faA, out hA)) return;
        if (!Frame(colB, out cB, out axB, out edB, out faB, out hB)) return;

        if (showContactSkin)
        {
            Skin(colA, cA, axA, edA, faA, hA);
            Skin(colB, cB, axB, edB, faB, hB);
        }

        if (showBladeFrames)
        {
            BladeFrame(cA, axA, edA, faA, hA);
            BladeFrame(cB, axB, edB, faB, hB);
        }

        if (showMidPlanes)
        {
            Gizmos.color = new Color(0.3f, 0.8f, 1f, 0.9f);
            Plane(cA, axA, edA);
            Gizmos.color = new Color(1f, 0.6f, 0.2f, 0.9f);
            Plane(cB, axB, edB);
        }

        if (showSideMargin) SideMargin(cA, axA, faB, cB, axB);
        if (showStepTravel && Application.isPlaying)
        {
            StepTravel(swA, colA, hA.z * 2f);
            StepTravel(swB, colB, hB.z * 2f);
        }

        if (showEdgePolylines || showEdgePairClosest || showContactCrossSection || showEdgeContainment)
            EdgeLayers(colA, colB);
    }

    /// <summary>
    ///     The edge-candidate layers. Everything here is drawn from a convention that already exists —
    ///     <see cref="StartStateProbe.CrossSection" /> for the physical one, <see cref="Frame" /> for the
    ///     geometric one — and from the cooked hulls via <c>Collider.ClosestPoint</c>. Nothing new is defined and
    ///     nothing is written, so a layer being on cannot change a run.
    /// </summary>
    private void EdgeLayers(Collider colA, Collider colB)
    {
        Vector3 gA, axA, edA, faA, gB, axB, edB, faB;
        if (!BuildEdgeLists(colA, colB, out gA, out axA, out edA, out faA, out gB, out axB, out edB, out faB))
            return;

        if (showEdgePolylines)
        {
            Polyline(_eAPlus, edgePlusColour);
            Polyline(_eAMinus, edgeMinusColour);
            Polyline(_eBPlus, edgePlusColour);
            Polyline(_eBMinus, edgeMinusColour);
        }

        if (showEdgeContainment)
        {
            Containment(_eAPlus, colB);
            Containment(_eAMinus, colB);
            Containment(_eBPlus, colA);
            Containment(_eBMinus, colA);
        }

        // Winning (A edge, B edge) pairing. Computed whenever any edge layer is on, because the cross-section
        // layer needs a contact point in edit mode, where the marker frame has not been calibrated onto the blades.
        Vector3 pa, pb;
        string label;
        var best = BestEdgePair(out pa, out pb, out label);
        if (best == float.MaxValue) return;

        if (showEdgePairClosest)
        {
            Gizmos.color = new Color(1f, 0.25f, 0.25f);
            Gizmos.DrawLine(pa, pb);
            Gizmos.DrawWireSphere(pa, 0.006f);
            Gizmos.DrawWireSphere(pb, 0.006f);
#if UNITY_EDITOR
            Handles.color = Color.white;
            Handles.Label((pa + pb) * 0.5f + Vector3.up * 0.03f, string.Format(
                "edge pair {0}\n{1:F2} mm\nalong A {2:F1} cm\nalong B {3:F1} cm",
                label, best * 1000f,
                Vector3.Dot(pa - gA, axA) * 100f, Vector3.Dot(pb - gB, axB) * 100f));
#endif
        }

        if (!showContactCrossSection) return;

        // Always the engaged edge pair's midpoint, and always labelled as such. BindMetrics' hull
        // closest-pair used to be preferred here and is deleted with it: it could not go negative, so it
        // reported 6.092 mm of gap for the EdgeBind SNAP pose whose edges were mutually buried 1.10 mm.
        var contact = (pa + pb) * 0.5f;

        CrossSectionBox(colA, gA, axA, edA, faA, contact, "A", false);
        CrossSectionBox(colB, gB, axB, edB, faB, contact, "B", false);
    }

    /// <summary>
    ///     Guard, axis, edge and flat normal for one blade, from whichever existing convention is selected.
    ///     <para>
    ///         The physical branch reads the quillon markers and hands them to
    ///         <see cref="StartStateProbe.CrossSection" />, so the edge direction drawn is bit-for-bit the one
    ///         every <c>present_*</c> and <c>edge_edge_deg</c> column was measured against. In play mode the frame
    ///         comes from <see cref="PhysicalMarkers" /> — the leaf positions converted once at <c>Start()</c>
    ///         into fixed local offsets and thereafter carried by the sword. In edit mode it is read from the marker transforms directly, which is valid there and
    ///         only there: the markers are static siblings, so once the sword has moved they no longer describe it.
    ///     </para>
    ///     The geometric branch is this component's own <see cref="Frame" />, whose guard end is the
    ///     <c>-axis</c> end; when markers are present the axis is flipped to agree with them rather than
    ///     introducing a second guess about which end is the guard.
    /// </summary>
    private bool PrescribedFrame(bool isA, Collider col, out Vector3 guard, out Vector3 axis, out Vector3 edge,
        out Vector3 face)
    {
        guard = axis = edge = face = Vector3.zero;
        Vector3 mg, mt, mq;
        var haveMarkers = PhysicalMarkers(isA, out mg, out mt, out mq);

        if (edgeConvention == EdgeConvention.PhysicalQuillon)
        {
            if (!haveMarkers || (mt - mg).sqrMagnitude < 1e-8f) return false;
            guard = mg;
            axis = (mt - mg).normalized;
            StartStateProbe.CrossSection(axis, mq, out edge, out face);
            return true;
        }

        Vector3 c, gAxis, gEdge, gFace, half;
        if (!Frame(col, out c, out gAxis, out gEdge, out gFace, out half)) return false;
        if (haveMarkers && Vector3.Dot(gAxis, mt - mg) < 0f)
        {
            gAxis = -gAxis;
            gEdge = -gEdge;
        }

        guard = c - gAxis * half.x;
        axis = gAxis;
        edge = gEdge;
        face = gFace;
        return true;
    }

    private bool PhysicalMarkers(bool isA, out Vector3 guard, out Vector3 tip, out Vector3 quillon)
    {
        guard = tip = quillon = Vector3.zero;

        // PLAY: the calibrated frame, riding the blade. The SwordRef leaves are static station children and do
        // not move with the sword, so reading them directly here would draw the authored pose over a blade
        // that has since moved.
        if (Application.isPlaying && _frameCalibrated)
        {
            var sw = isA ? SwordA : SwordB;
            if (sw == null) return false;
            var i = isA ? 0 : 1;
            guard = sw.TransformPoint(_localGuard[i]);
            tip = sw.TransformPoint(_localTip[i]);
            quillon = sw.TransformDirection(_localQuillon[i]).normalized;
            return quillon.sqrMagnitude > 1e-10f;
        }

        // EDIT: the leaves ARE the authored pose, so read them straight.
        var q1 = isA ? RefAQ1 : RefBQ1;
        var q2 = isA ? RefAQ2 : RefBQ2;
        var tp = isA ? RefATip : RefBTip;
        if (q1 == null || q2 == null || tp == null) return false;
        guard = (q1.position + q2.position) * 0.5f;
        tip = tp.position;
        quillon = (q1.position - q2.position).normalized;
        return quillon.sqrMagnitude > 1e-10f;
    }

    /// <summary>
    ///     One long edge, as points on the COOKED hull. At each axial station the hull is queried from far out
    ///     along +/-edge; <c>Collider.ClosestPoint</c> then returns the hull point facing that direction, so the
    ///     polyline is the hull's own edge rather than a selection of source-mesh vertices.
    /// </summary>
    /// <summary>
    ///     Fill the four edge polylines from the prescribed frame. Extracted so the gizmo path
    ///     (<see cref="EdgeLayers" />), the readout (<see cref="LogEdgeReadout" />) and the solid-renderer path
    ///     (<see cref="SyncEdgeRenderers" />) all draw the SAME points from the SAME convention. Read-only.
    /// </summary>
    private bool BuildEdgeLists(Collider colA, Collider colB,
        out Vector3 gA, out Vector3 axA, out Vector3 edA, out Vector3 faA,
        out Vector3 gB, out Vector3 axB, out Vector3 edB, out Vector3 faB)
    {
        gA = axA = edA = faA = gB = axB = edB = faB = Vector3.zero;
        if (!PrescribedFrame(true, colA, out gA, out axA, out edA, out faA)) return false;
        if (!PrescribedFrame(false, colB, out gB, out axB, out edB, out faB)) return false;

        EdgePolyline(colA, gA, axA, edA, 1f, _eAPlus);
        EdgePolyline(colA, gA, axA, edA, -1f, _eAMinus);
        EdgePolyline(colB, gB, axB, edB, 1f, _eBPlus);
        EdgePolyline(colB, gB, axB, edB, -1f, _eBMinus);
        return true;
    }

    // ---- solid LineRenderer version of the edge polylines (showEdgeRenderers) ----
    // Runtime-only visual objects. HideFlags.DontSave keeps them out of the scene file entirely, so this
    // still writes nothing persistent. Rebuilt every frame from the lists above; destroyed on toggle-off,
    // disable and destroy.

    private const string RendererPrefix = "__StationEdge_";

    private readonly List<LineRenderer> _edgeRenderers = new List<LineRenderer>();

    /// <summary>
    ///     ONE MATERIAL PER RENDERER, not a shared one. <c>LineRenderer.startColor</c>/<c>endColor</c> are vertex
    ///     colours, and URP/Unlit ignores vertex colour — it reads <c>_BaseColor</c>. Sharing a single material
    ///     and only setting startColor draws every line in the material's default white, which is exactly what
    ///     the first version of this did.
    /// </summary>
    private readonly List<Material> _edgeMats = new List<Material>();

    private void Update()
    {
        SyncEdgeRenderers();
    }

    private void OnDisable()
    {
        ClearEdgeRenderers();
    }

    private void OnDestroy()
    {
        ClearEdgeRenderers();
    }

    private void SyncEdgeRenderers()
    {
        if (!showEdgeRenderers)
        {
            if (_edgeRenderers.Count > 0) ClearEdgeRenderers();
            return;
        }

        Transform swA = SwordA, swB = SwordB;
        if (swA == null || swB == null) return;
        var colA = Blade(swA);
        var colB = Blade(swB);
        if (colA == null || colB == null) return;
        Vector3 gA, axA, edA, faA, gB, axB, edB, faB;
        if (!BuildEdgeLists(colA, colB, out gA, out axA, out edA, out faA, out gB, out axB, out edB, out faB))
            return;

        if (_edgeRenderers.Count != 5) RebuildEdgeRenderers();

        Apply(0, _eAPlus, edgePlusColour);
        Apply(1, _eAMinus, edgeMinusColour);
        Apply(2, _eBPlus, edgePlusColour);
        Apply(3, _eBMinus, edgeMinusColour);

        // 5th: the winning edge pair, i.e. WHICH two edges are actually engaged
        Vector3 pa, pb;
        string label;
        var sep = BestEdgePair(out pa, out pb, out label);
        var pair = _edgeRenderers[4];
        if (pair != null)
        {
            pair.widthMultiplier = edgeRendererWidth * 1.5f;
            pair.positionCount = 2;
            pair.SetPosition(0, pa);
            pair.SetPosition(1, pb);
            Tint(4, sep <= 0f ? Color.red : Color.cyan);
        }
    }

    private void Apply(int i, List<Vector3> pts, Color col)
    {
        if (i >= _edgeRenderers.Count) return;
        var lr = _edgeRenderers[i];
        if (lr == null) return;
        lr.widthMultiplier = edgeRendererWidth;
        lr.positionCount = pts.Count;
        for (var k = 0; k < pts.Count; k++) lr.SetPosition(k, pts[k]);
        Tint(i, col);
    }

    /// <summary>Set the colour on BOTH channels: the material (URP reads _BaseColor) and the vertex colours.</summary>
    private void Tint(int i, Color col)
    {
        var lr = _edgeRenderers[i];
        if (lr != null)
        {
            lr.startColor = col;
            lr.endColor = col;
        }

        if (i >= _edgeMats.Count) return;
        var m = _edgeMats[i];
        if (m == null) return;
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", col);
        if (m.HasProperty("_Color")) m.SetColor("_Color", col);
    }

    private void RebuildEdgeRenderers()
    {
        ClearEdgeRenderers();
        var sh = Shader.Find("Universal Render Pipeline/Unlit");
        if (sh == null) sh = Shader.Find("Sprites/Default");
        if (sh == null) sh = Shader.Find("Hidden/Internal-Colored");

        string[] names = { "A_plus", "A_minus", "B_plus", "B_minus", "PairClosest" };
        for (var i = 0; i < names.Length; i++)
        {
            var go = new GameObject(RendererPrefix + names[i]) { hideFlags = HideFlags.DontSave };
            go.transform.SetParent(transform, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = true; // the polyline points are already world-space hull samples
            lr.numCapVertices = 2;
            var mat = new Material(sh) { hideFlags = HideFlags.DontSave };
            lr.sharedMaterial = mat; // sharedMaterial: assigning .material would instantiate a second copy
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            lr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            lr.positionCount = 0;
            _edgeRenderers.Add(lr);
            _edgeMats.Add(mat);
        }
    }

    private void ClearEdgeRenderers()
    {
        for (var i = 0; i < _edgeRenderers.Count; i++)
        {
            var lr = _edgeRenderers[i];
            if (lr == null) continue;
            if (Application.isPlaying) Destroy(lr.gameObject);
            else DestroyImmediate(lr.gameObject);
        }

        _edgeRenderers.Clear();

        for (var i = 0; i < _edgeMats.Count; i++)
        {
            var m = _edgeMats[i];
            if (m == null) continue;
            if (Application.isPlaying) Destroy(m);
            else DestroyImmediate(m);
        }

        _edgeMats.Clear();

        // Catch any strays a domain reload orphaned - the list does not survive but the objects can.
        var stray = new List<GameObject>();
        foreach (var t in GetComponentsInChildren<Transform>(true))
            if (t != null && t != transform && t.name.StartsWith(RendererPrefix))
                stray.Add(t.gameObject);
        for (var i = 0; i < stray.Count; i++)
        {
            if (Application.isPlaying) Destroy(stray[i]);
            else DestroyImmediate(stray[i]);
        }
    }

    private void EdgePolyline(Collider col, Vector3 guard, Vector3 axis, Vector3 edge, float sign,
        List<Vector3> pts)
    {
        pts.Clear();
        float s0, s1;
        if (!AxialSpan(col, guard, axis, out s0, out s1)) return;
        var inset = (s1 - s0) * 0.002f;
        s0 += inset;
        s1 -= inset;
        var n = Mathf.Max(2, edgeStations);
        for (var i = 0; i < n; i++)
        {
            var s = Mathf.Lerp(s0, s1, i / (float) (n - 1));
            var onAxis = guard + axis * s;
            pts.Add(col.ClosestPoint(onAxis + edge * (sign * Mathf.Max(0.05f, edgeProbeM))));
        }
    }

    /// <summary>Extent of the collider along the blade axis, measured from its mesh bounds corners.</summary>
    private static bool AxialSpan(Collider col, Vector3 guard, Vector3 axis, out float s0, out float s1)
    {
        s0 = 0f;
        s1 = 0f;
        var mc = col as MeshCollider;
        if (mc == null || mc.sharedMesh == null) return false;
        var b = mc.sharedMesh.bounds;
        // Built at the simulated pose, not the rendered Transform, for the same reason as everything else
        // in this file that touches a collider.
        Vector3 sPos;
        Quaternion sRot;
        SimulatedColliderPose(col, out sPos, out sRot);
        var l2w = Matrix4x4.TRS(sPos, sRot, col.transform.lossyScale);
        var lo = float.MaxValue;
        var hi = float.MinValue;
        for (var i = 0; i < 8; i++)
        {
            var corner = b.center + new Vector3(
                (i & 1) == 0 ? -b.extents.x : b.extents.x,
                (i & 2) == 0 ? -b.extents.y : b.extents.y,
                (i & 4) == 0 ? -b.extents.z : b.extents.z);
            var s = Vector3.Dot(l2w.MultiplyPoint3x4(corner) - guard, axis);
            lo = Mathf.Min(lo, s);
            hi = Mathf.Max(hi, s);
        }

        s0 = lo;
        s1 = hi;
        return hi - lo > 1e-4f;
    }

    private static void Polyline(List<Vector3> pts, Color c)
    {
        if (pts.Count < 2) return;
        Gizmos.color = c;
        for (var i = 1; i < pts.Count; i++) Gizmos.DrawLine(pts[i - 1], pts[i]);
    }

    /// <summary>
    ///     Marks edge points buried in the opposing hull. <c>Collider.ClosestPoint</c> returns the query point
    ///     unchanged when it is inside, which is exactly the property that makes the seeded closest-pair search
    ///     blind to overlap — used deliberately here as the containment test.
    /// </summary>
    private static void Containment(List<Vector3> pts, Collider other)
    {
        Gizmos.color = Color.red;
        foreach (var p in pts)
            if ((other.ClosestPoint(p) - p).sqrMagnitude < 1e-12f)
                Gizmos.DrawWireSphere(p, 0.005f);
    }

    /// <summary>
    ///     The engaged (A edge, B edge) pairing: whichever of the four edge-to-edge combinations is closest.
    ///     Assumes the four polylines are already built. Returns <c>float.MaxValue</c> if they are not.
    /// </summary>
    /// <summary>
    ///     Public accessor for the engaged edge pair's closest points — the SAME code path the cyan
    ///     <c>__StationEdge_PairClosest</c> gizmo draws, so a follower and the viewport cannot disagree.
    ///     <para>
    ///         Kept as the single public closest-point path rather than a fourth private implementation.
    ///         Returns the separation in metres; <paramref name="pa" />
    ///         is the point on blade A's engaged edge, <paramref name="pb" /> the point on blade B's, and
    ///         <paramref name="label" /> which pairing won (e.g. "A+ / B-").
    ///     </para>
    /// </summary>
    public bool TryEdgePairClosest(out Vector3 pa, out Vector3 pb, out string label, out float sepM)
    {
        pa = pb = Vector3.zero;
        label = "";
        sepM = float.PositiveInfinity;
        Transform swA = SwordA, swB = SwordB;
        var colA = swA != null ? Blade(swA) : null;
        var colB = swB != null ? Blade(swB) : null;
        if (colA == null || colB == null) return false;

        Vector3 gA, axA, edA, faA, gB, axB, edB, faB;
        if (!PrescribedFrame(true, colA, out gA, out axA, out edA, out faA)) return false;
        if (!PrescribedFrame(false, colB, out gB, out axB, out edB, out faB)) return false;

        EdgePolyline(colA, gA, axA, edA, 1f, _eAPlus);
        EdgePolyline(colA, gA, axA, edA, -1f, _eAMinus);
        EdgePolyline(colB, gB, axB, edB, 1f, _eBPlus);
        EdgePolyline(colB, gB, axB, edB, -1f, _eBMinus);

        sepM = BestEdgePair(out pa, out pb, out label);
        return sepM < float.MaxValue;
    }

    /// <summary>
    ///     The same data <see cref="LogEdgeReadout" /> prints, returned instead of logged, so an automated
    ///     suite can record it per cell. Read-only. False if the prescribed frame is unavailable.
    ///     <para>
    ///         Reports ALL FOUR pairing separations, not just the winner, because "which edges are engaged" is
    ///         only meaningful against how close the alternatives are — and the buried-point counts, which are
    ///         the local penetration read (<c>Physics.ComputePenetration</c> is banned on these hulls).
    ///     </para>
    /// </summary>
    public bool TryEdgeReadout(out string winner, out float sepApBp, out float sepApBm,
        out float sepAmBp, out float sepAmBm, out int buriedA, out int buriedB, out int perEdge)
    {
        winner = "";
        sepApBp = sepApBm = sepAmBp = sepAmBm = float.NaN;
        buriedA = buriedB = perEdge = 0;

        Transform swA = SwordA, swB = SwordB;
        var colA = swA != null ? Blade(swA) : null;
        var colB = swB != null ? Blade(swB) : null;
        if (colA == null || colB == null) return false;

        Vector3 gA, axA, edA, faA, gB, axB, edB, faB;
        if (!PrescribedFrame(true, colA, out gA, out axA, out edA, out faA)) return false;
        if (!PrescribedFrame(false, colB, out gB, out axB, out edB, out faB)) return false;

        EdgePolyline(colA, gA, axA, edA, 1f, _eAPlus);
        EdgePolyline(colA, gA, axA, edA, -1f, _eAMinus);
        EdgePolyline(colB, gB, axB, edB, 1f, _eBPlus);
        EdgePolyline(colB, gB, axB, edB, -1f, _eBMinus);

        Vector3 ca, cb;
        sepApBp = PolylineClosest(_eAPlus, _eBPlus, out ca, out cb) * 1000f;
        sepApBm = PolylineClosest(_eAPlus, _eBMinus, out ca, out cb) * 1000f;
        sepAmBp = PolylineClosest(_eAMinus, _eBPlus, out ca, out cb) * 1000f;
        sepAmBm = PolylineClosest(_eAMinus, _eBMinus, out ca, out cb) * 1000f;

        buriedA = Buried(_eAPlus, colB) + Buried(_eAMinus, colB);
        buriedB = Buried(_eBPlus, colA) + Buried(_eBMinus, colA);
        perEdge = _eAPlus.Count;

        Vector3 pa, pb;
        BestEdgePair(out pa, out pb, out winner);
        return true;
    }

    /// <summary>
    ///     Where along the blade a world point sits, measured from the guard, and how WIDE the blade is at
    ///     that station - the perpendicular distance between its two edges there.
    ///     <para>
    ///         This exists so blade interpenetration can be DERIVED rather than read off a deflection. Given
    ///         the contact station's distance from the grip and the angle the blade turned through, the swept
    ///         distance at the contact is r·θ, and expressing that as a fraction of the local width says how
    ///         far into the opposing blade the geometry moved.
    ///     </para>
    /// </summary>
    public bool TryBladeStation(bool isA, Vector3 worldPoint, out float alongMm, out float widthMm,
        out float thickMm)
    {
        alongMm = widthMm = thickMm = float.NaN;
        var sw = isA ? SwordA : SwordB;
        var col = sw == null ? null : Blade(sw);
        if (col == null) return false;

        Vector3 g, ax, ed, fa;
        if (!PrescribedFrame(isA, col, out g, out ax, out ed, out fa)) return false;

        var plus = isA ? _eAPlus : _eBPlus;
        var minus = isA ? _eAMinus : _eBMinus;
        EdgePolyline(col, g, ax, ed, 1f, plus);
        EdgePolyline(col, g, ax, ed, -1f, minus);
        if (plus.Count == 0 || minus.Count == 0) return false;

        var s = Vector3.Dot(worldPoint - g, ax);
        alongMm = s * 1000f;

        Vector3 onPlus, onMinus;
        if (!NearestAtStation(plus, g, ax, s, out onPlus)) return false;
        if (!NearestAtStation(minus, g, ax, s, out onMinus)) return false;

        widthMm = Vector3.Distance(onPlus, onMinus) * 1000f;

        // Thickness = the blade's extent along its FACE NORMAL at this station, probed from the mid-width
        // point outwards. This is the blade at its THICKEST there (the centre of a tapered cross-section),
        // so it is a conservative denominator - the steel is thinner than this everywhere nearer the edge.
        var mid = (onPlus + onMinus) * 0.5f;
        var faceOut = col.ClosestPoint(mid + fa * 0.5f);
        var faceIn = col.ClosestPoint(mid - fa * 0.5f);
        thickMm = Vector3.Distance(faceOut, faceIn) * 1000f;
        return true;
    }

    /// <summary>Point on a walked edge whose along-blade station is closest to <paramref name="s" />.</summary>
    private static bool NearestAtStation(List<Vector3> line, Vector3 guard, Vector3 axis, float s,
        out Vector3 best)
    {
        best = Vector3.zero;
        var bd = float.MaxValue;
        foreach (var v in line)
        {
            var d = Mathf.Abs(Vector3.Dot(v - guard, axis) - s);
            if (d >= bd) continue;
            bd = d;
            best = v;
        }

        return bd < float.MaxValue;
    }

    private float BestEdgePair(out Vector3 pa, out Vector3 pb, out string label)
    {
        pa = pb = Vector3.zero;
        label = "";
        var best = float.MaxValue;
        for (var i = 0; i < 4; i++)
        {
            var la = i < 2 ? _eAPlus : _eAMinus;
            var lb = i % 2 == 0 ? _eBPlus : _eBMinus;
            Vector3 ca, cb;
            var d = PolylineClosest(la, lb, out ca, out cb);
            if (d >= best) continue;
            best = d;
            pa = ca;
            pb = cb;
            label = (i < 2 ? "A+" : "A-") + " / " + (i % 2 == 0 ? "B+" : "B-");
        }

        return best;
    }

    /// <summary>
    ///     The same readout as text. Deliberately the same code path the gizmos draw, so a number in the console
    ///     and a line in the viewport cannot disagree, and a reading can be recorded without a screenshot.
    ///     Reports all four pairings, not just the winner, because "which edges are engaged" is only meaningful
    ///     against how close the alternatives are.
    /// </summary>
    [ContextMenu("Log edge readout")]
    public void LogEdgeReadout()
    {
        Transform swA = SwordA, swB = SwordB;
        var colA = swA != null ? Blade(swA) : null;
        var colB = swB != null ? Blade(swB) : null;
        if (colA == null || colB == null)
        {
            Debug.LogWarning("[StationDebugGizmos] " + name + ": no blade colliders. In EDIT mode the collider " +
                             "only exists if it was authored — SwordBladeSetup adds it at Awake.", this);
            return;
        }

        Vector3 gA, axA, edA, faA, gB, axB, edB, faB;
        if (!PrescribedFrame(true, colA, out gA, out axA, out edA, out faA) ||
            !PrescribedFrame(false, colB, out gB, out axB, out edB, out faB))
        {
            Debug.LogWarning("[StationDebugGizmos] " + name + ": cannot resolve the " + edgeConvention +
                             " frame. The physical one needs the six SwordRef leaves assigned, and in play mode " +
                             "needs them to have calibrated onto the blades at Start().", this);
            return;
        }

        EdgePolyline(colA, gA, axA, edA, 1f, _eAPlus);
        EdgePolyline(colA, gA, axA, edA, -1f, _eAMinus);
        EdgePolyline(colB, gB, axB, edB, 1f, _eBPlus);
        EdgePolyline(colB, gB, axB, edB, -1f, _eBMinus);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("[StationDebugGizmos] " + name + " edge readout   convention=" + edgeConvention +
                      "   mode=" + (Application.isPlaying ? "PLAY" : "EDIT") + "   stations=" + edgeStations);
        sb.AppendLine(string.Format("   edge dirs: A {0}  B {1}   edge-edge angle {2:F2} deg (acute)",
            Fmt(edA), Fmt(edB), Acute(Vector3.Angle(edA, edB))));
        sb.AppendLine(string.Format("   polyline lengths mm: A+ {0:F1}  A- {1:F1}  B+ {2:F1}  B- {3:F1}" +
                                    "   (blade is ~1016 mm; a much shorter figure means the walk failed)",
            Len(_eAPlus), Len(_eAMinus), Len(_eBPlus), Len(_eBMinus)));

        for (var i = 0; i < 4; i++)
        {
            var la = i < 2 ? _eAPlus : _eAMinus;
            var lb = i % 2 == 0 ? _eBPlus : _eBMinus;
            Vector3 ca, cb;
            var d = PolylineClosest(la, lb, out ca, out cb);
            sb.AppendLine(string.Format("   pair {0} / {1} : {2,8:F3} mm   along A {3,6:F1} cm   along B {4,6:F1} cm",
                i < 2 ? "A+" : "A-", i % 2 == 0 ? "B+" : "B-", d * 1000f,
                Vector3.Dot(ca - gA, axA) * 100f, Vector3.Dot(cb - gB, axB) * 100f));
        }

        sb.AppendLine(string.Format("   edge points buried in the opposing hull: A {0}  B {1}   (of {2} per edge)",
            Buried(_eAPlus, colB) + Buried(_eAMinus, colB), Buried(_eBPlus, colA) + Buried(_eBMinus, colA),
            _eAPlus.Count));

        Vector3 pa, pb;
        string label;
        var best = BestEdgePair(out pa, out pb, out label);
        if (best < float.MaxValue)
        {
            var contact = (pa + pb) * 0.5f;
            sb.AppendLine("   engaged pairing " + label + string.Format(" at {0:F3} mm", best * 1000f));
            sb.Append(Section(colA, gA, axA, edA, faA, contact, "A"));
            sb.Append(Section(colB, gB, axB, edB, faB, contact, "B"));
        }

        Debug.Log(sb.ToString(), this);
    }

    private string Section(Collider col, Vector3 guard, Vector3 axis, Vector3 edge, Vector3 face, Vector3 contact,
        string tag)
    {
        var along = Vector3.Dot(contact - guard, axis);
        float hwMm, htMm;
        StartStateProbe.CrossSectionExtents(col, guard, axis, edge, face, along, out hwMm, out htMm);
        if (hwMm <= 1e-3f || htMm <= 1e-3f) return "   " + tag + " cross-section: unreadable mesh\n";
        float deg, edgeMm, faceMm;
        StartStateProbe.Presentation(contact, guard, axis, edge, face, hwMm, htMm, out deg, out edgeMm,
            out faceMm);
        return string.Format(
            "   {0} @ {1:F1} cm: half-width {2:F2} mm  half-thick {3:F2} mm   edge {4:F2}  face {5:F2} mm   " +
            "presentation {6:F1} deg\n", tag, along * 100f, hwMm, htMm, edgeMm, faceMm, deg);
    }

    private static int Buried(List<Vector3> pts, Collider other)
    {
        var n = 0;
        foreach (var q in pts)
            if ((other.ClosestPoint(q) - q).sqrMagnitude < 1e-12f)
                n++;
        return n;
    }

    private static float Len(List<Vector3> pts)
    {
        var d = 0f;
        for (var i = 1; i < pts.Count; i++) d += Vector3.Distance(pts[i - 1], pts[i]);
        return d * 1000f;
    }

    private static float Acute(float deg)
    {
        return deg > 90f ? 180f - deg : deg;
    }

    private static string Fmt(Vector3 v)
    {
        return string.Format("({0:F3},{1:F3},{2:F3})", v.x, v.y, v.z);
    }

    /// <summary>Closest approach between two polylines, segment against segment.</summary>
    private static float PolylineClosest(List<Vector3> a, List<Vector3> b, out Vector3 ca, out Vector3 cb)
    {
        ca = cb = Vector3.zero;
        var best = float.MaxValue;
        if (a.Count < 2 || b.Count < 2) return best;
        for (var i = 1; i < a.Count; i++)
        for (var j = 1; j < b.Count; j++)
        {
            Vector3 p, q;
            SegSeg(a[i - 1], a[i], b[j - 1], b[j], out p, out q);
            var d = (p - q).magnitude;
            if (d >= best) continue;
            best = d;
            ca = p;
            cb = q;
        }

        return best;
    }

    /// <summary>Closest points of two line SEGMENTS, clamped to both, degenerate cases included.</summary>
    private static void SegSeg(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2, out Vector3 c1, out Vector3 c2)
    {
        var d1 = q1 - p1;
        var d2 = q2 - p2;
        var r = p1 - p2;
        var a = Vector3.Dot(d1, d1);
        var e = Vector3.Dot(d2, d2);
        var f = Vector3.Dot(d2, r);
        float s, t;
        if (a <= 1e-12f && e <= 1e-12f)
        {
            c1 = p1;
            c2 = p2;
            return;
        }

        if (a <= 1e-12f)
        {
            s = 0f;
            t = Mathf.Clamp01(f / e);
        }
        else
        {
            var c = Vector3.Dot(d1, r);
            if (e <= 1e-12f)
            {
                t = 0f;
                s = Mathf.Clamp01(-c / a);
            }
            else
            {
                var bb = Vector3.Dot(d1, d2);
                var den = a * e - bb * bb;
                s = den > 1e-12f ? Mathf.Clamp01((bb * f - c * e) / den) : 0f;
                t = bb * s + f;
                if (t < 0f)
                {
                    t = 0f;
                    s = Mathf.Clamp01(-c / a);
                }
                else if (t > e)
                {
                    t = 1f;
                    s = Mathf.Clamp01((bb - c) / a);
                }
                else
                {
                    t /= e;
                }
            }
        }

        c1 = p1 + d1 * s;
        c2 = p2 + d2 * t;
    }

    /// <summary>
    ///     The blade's cross-section at the contact's axial station — half-width across the edges, half-thickness
    ///     across the flats — with the contact plotted in it. Both come from <see cref="StartStateProbe" />, so
    ///     what is drawn is the same rectangle the presentation angle is normalised by.
    /// </summary>
    private void CrossSectionBox(Collider col, Vector3 guard, Vector3 axis, Vector3 edge, Vector3 face,
        Vector3 contact, string tag, bool measuredContact)
    {
        var along = Vector3.Dot(contact - guard, axis);
        float hwMm, htMm;
        StartStateProbe.CrossSectionExtents(col, guard, axis, edge, face, along, out hwMm, out htMm);
        if (hwMm <= 1e-3f || htMm <= 1e-3f) return;

        float deg, edgeMm, faceMm;
        StartStateProbe.Presentation(contact, guard, axis, edge, face, hwMm, htMm, out deg, out edgeMm,
            out faceMm);

        var o = guard + axis * along;
        var w = edge * (hwMm * 0.001f);
        var t = face * (htMm * 0.001f);
        Gizmos.color = new Color(0.4f, 0.85f, 1f);
        Gizmos.DrawLine(o - w - t, o + w - t);
        Gizmos.DrawLine(o + w - t, o + w + t);
        Gizmos.DrawLine(o + w + t, o - w + t);
        Gizmos.DrawLine(o - w + t, o - w - t);

        var at = o + edge * (edgeMm * 0.001f) + face * (faceMm * 0.001f);
        Gizmos.color = new Color(1f, 0.55f, 0f);
        Gizmos.DrawWireSphere(at, 0.0025f);
        Gizmos.DrawLine(o, at);
#if UNITY_EDITOR
        Handles.color = Color.white;
        Handles.Label(o - t * 2.5f, string.Format(
            "{0} @ {1:F1} cm{2}\nhalf-width {3:F2} mm  half-thick {4:F2} mm\nedge {5:F2}  face {6:F2} mm\n" +
            "presentation {7:F1} deg (0 = edge, 90 = flat)",
            tag, along * 100f, measuredContact ? "" : "  [edge-pair midpoint, NOT a measured contact]",
            hwMm, htMm, edgeMm, faceMm, deg));
#endif
    }

    private static Collider Blade(Transform sword)
    {
        var bt = sword.Find("Blade");
        return bt != null ? bt.GetComponent<Collider>() : null;
    }

    /// <summary>
    ///     Separates the two things that are constantly confused here, per the resolution used in the
    ///     contact-offset construct-validity screen (removed; see git history if required):
    ///     <list type="bullet">
    ///         <item>
    ///             <b>WHITE — the actual collider.</b> The convex hull mesh PhysX collides, drawn as the mesh
    ///             itself rather than a bounding box, so no approximation stands in for the geometry. This is
    ///             what every gap and margin number must be derived from — the screen's ruler
    ///             (<c>Collider.ClosestPoint</c>) reads this and never consults <c>contactOffset</c>.
    ///         </item>
    ///         <item>
    ///             <b>MAGENTA — the speculative skin envelope.</b> <c>contactOffset</c> is a predictive contact
    ///             GENERATION distance, not a rest shell and not geometry: PhysX starts emitting contacts while
    ///             the hulls are still <c>offsetA + offsetB</c> apart. It is drawn as a box because the true
    ///             envelope is a Minkowski sum, not a scaled hull — so read it as a bound on reach, never as a
    ///             shape. <b>No measurement may be taken from this.</b>
    ///         </item>
    ///     </list>
    ///     The offset is read from the LIVE collider, not from <c>SwordBladeSetup.bladeContactOffset</c> — the
    ///     same reason the screen logs <c>live_offset_a_mm</c>/<c>live_offset_b_mm</c> on every row.
    /// </summary>
    private void Skin(Collider col, Vector3 c, Vector3 axis, Vector3 edge, Vector3 face, Vector3 half)
    {
        // WHITE: the real hull, drawn as the mesh PhysX actually collides -- and drawn at the pose PhysX
        // has it, which is NOT the collider's Transform while the body is interpolating.
        var mc = col as MeshCollider;
        if (mc != null && mc.sharedMesh != null)
        {
            Vector3 hullPos;
            Quaternion hullRot;
            SimulatedColliderPose(col, out hullPos, out hullRot);
            Gizmos.color = Color.white;
            Gizmos.DrawWireMesh(mc.sharedMesh, hullPos, hullRot, col.transform.lossyScale);
        }

        // MAGENTA: the speculative generation envelope, as a bound only
        var prev = Gizmos.matrix;
        Gizmos.matrix = Matrix4x4.TRS(c, Quaternion.LookRotation(axis, face), Vector3.one);
        var size = new Vector3(half.y * 2f, half.z * 2f, half.x * 2f); // axis->z, face->y, edge->x
        var off = col.contactOffset * skinExaggeration * 2f;
        Gizmos.color = new Color(1f, 0.2f, 0.9f, 0.85f);
        Gizmos.DrawWireCube(Vector3.zero, size + new Vector3(off, off, off));
        Gizmos.matrix = prev;
    }

    private void BladeFrame(Vector3 c, Vector3 axis, Vector3 edge, Vector3 face, Vector3 half)
    {
        var guard = c - axis * half.x;
        var tip = c + axis * half.x;
        Gizmos.color = Color.blue;
        Gizmos.DrawLine(guard, tip);
        Gizmos.DrawWireSphere(guard, 0.012f);
        Gizmos.color = Color.red;
        Gizmos.DrawRay(c, edge * 0.08f);
        Gizmos.color = Color.green;
        Gizmos.DrawRay(c, face * 0.08f);
    }

    private void Plane(Vector3 c, Vector3 axis, Vector3 edge)
    {
        var a = axis * planeSize;
        var e = edge * planeSize * 0.5f;
        Gizmos.DrawLine(c - a - e, c + a - e);
        Gizmos.DrawLine(c - a + e, c + a + e);
        Gizmos.DrawLine(c - a - e, c - a + e);
        Gizmos.DrawLine(c + a - e, c + a + e);
        Gizmos.DrawLine(c - a, c + a);
    }

    /// <summary>Closest approach of the two blade CENTRELINES, and the signed offset across B's mid-plane.</summary>
    private void SideMargin(Vector3 cA, Vector3 axA, Vector3 faceB, Vector3 cB, Vector3 axB)
    {
        var w0 = cA - cB;
        float b = Vector3.Dot(axA, axB), d = Vector3.Dot(axA, w0), e = Vector3.Dot(axB, w0);
        float den = 1f - b * b;
        if (Mathf.Abs(den) < 1e-6f) return;
        var pA = cA + axA * ((b * e - d) / den);
        var pB = cB + axB * ((e - b * d) / den);
        float margin = Vector3.Dot(pA - pB, faceB);
        Gizmos.color = Mathf.Abs(margin) < 0.0005f ? Color.yellow : margin > 0f ? Color.green : Color.red;
        Gizmos.DrawLine(pA, pB);
        Gizmos.DrawWireSphere(pA, 0.008f);
        Gizmos.DrawWireSphere(pB, 0.008f);
    }


    /// <summary>
    ///     Per-step travel of the material point nearest the opposing blade. This is the quantity that decides
    ///     tunnelling — and it is NOT the centre-of-mass velocity: rotation makes the contact point and the tip
    ///     move considerably faster.
    /// </summary>
    /// <summary>
    ///     A collider's pose as the SIMULATION has it, not as it is being drawn.
    /// </summary>
    /// <remarks>
    ///     A collider on an interpolated body inherits the interpolated root, so its Transform is a
    ///     rendering pose and anything drawn or measured through it describes the catch-up rather than the
    ///     physics. The collider's offset from its body is rigid, so it is taken from the hierarchy and
    ///     re-applied at <c>Rigidbody.position/rotation</c>. Twin of the method of the same name in
    ///     <c>RqBindMetricsV2</c>; kept separate because this file does not reference the BladeContact
    ///     package.
    /// </remarks>
    private static void SimulatedColliderPose(Collider col, out Vector3 pos, out Quaternion rot)
    {
        var t = col.transform;
        var rb = col.attachedRigidbody;

        if (rb == null)
        {
            pos = t.position;
            rot = t.rotation;
            return;
        }

        var invBody = Quaternion.Inverse(rb.transform.rotation);
        var localPos = invBody * (t.position - rb.transform.position);
        var localRot = invBody * t.rotation;

        pos = rb.position + rb.rotation * localPos;
        rot = rb.rotation * localRot;
    }

    private void StepTravel(Transform sword, Collider blade, float thickness)
    {
        var rb = sword.GetComponent<Rigidbody>();
        if (rb == null || rb.isKinematic) return;

        // Collider.bounds is a world AABB derived from the Transform, so it carries the rendered pose.
        // The travel ray is a physics statement, so its origin is taken at the simulated pose.
        Vector3 bodyPos;
        Quaternion bodyRot;
        SimulatedColliderPose(blade, out bodyPos, out bodyRot);
        var bmc = blade as MeshCollider;
        var pt = bmc != null && bmc.sharedMesh != null
            ? bodyPos + bodyRot * bmc.sharedMesh.bounds.center
            : blade.bounds.center;
        var v = rb.linearVelocity + Vector3.Cross(rb.angularVelocity, pt - rb.worldCenterOfMass);
        var travel = v * Time.fixedDeltaTime;
        Gizmos.color = travel.magnitude > thickness * 0.5f ? Color.red : Color.yellow;
        Gizmos.DrawRay(pt, travel * 20f); // x20 so a few mm is visible at rig scale
    }
}
