using System.Collections.Generic;
using BladeContact;
using FIMSpace.FProceduralAnimation;
using UnityEngine;

/// <summary>
///     Measures the EXTENT of the contact patch between the two blades, per fixed step, from PhysX's own
///     contact points expressed in each blade's marker-derived frame.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why this exists.</b> Patch extent is the sharpness observable. An edge presents a near-point
///         or line patch; a broad face presents an area. Nothing else in the apparatus measures it —
///         <c>s_contacts</c> counts points, and a count is not an extent. Without this, the comparison can
///         say where contact sat and how long it persisted, but not how concentrated it was, which is the
///         geometric difference the three arms are built to express.
///     </para>
///     <para>
///         <b>Representation-independent, by construction.</b> No <c>BladeShell</c>, no profile asset, no
///         classifier. It reads PhysX contact points and the frame that
///         <see cref="RqContactSideProbe" /> already builds from markers and collider mesh. It therefore
///         reads the same way on Standard Unity, Coarse approximation and Augmented exploratory, which is
///         the property that lets it serve a between-arm comparison at all.
///     </para>
///     <para>
///         <b>It does not rebuild the frame.</b> There is one frame calculation in this project and this
///         probe consumes it through <see cref="RqContactSideProbe.FrameA" /> /
///         <see cref="RqContactSideProbe.FrameB" />. A second derivation would be a second number that can
///         disagree with the first.
///     </para>
///     <para>
///         <b>Sampling instant.</b> Unity runs every FixedUpdate, then simulates, then delivers collision
///         callbacks. So the callbacks accumulated during step N are reduced by this probe's FixedUpdate in
///         step N+1 — the same step on which <see cref="RqStationContactRecorder" /> (execution order 0)
///         reads them. This probe is +150: after that recorder, before the seal at +200. An observer
///         callback in the runner's −200 slot reads a step-stale value, and that defect once produced 2.08°
///         and 16.3 mm of phantom disagreement.
///     </para>
///     <para>
///         <b>Pair is not touch.</b> PhysX generates contact points inside the combined contact offset, so
///         a "pair" is reported at POSITIVE separation while the blades are apart — measured at +29.005 mm
///         on a Standard Unity station. Every quantity here is therefore reported twice: once over all
///         contact points, and once over the overlap subset (separation ≤ 0). A patch measured across the
///         speculative envelope is not a contact patch.
///     </para>
///     <para>
///         <b>No thresholds.</b> This probe sets none. It reports extents, counts and spreads; what counts
///         as "a point", "a line" or "an area" is the researcher's declaration, not this component's.
///     </para>
///     <para>
///         <b>It reports positions and states, never causes.</b> No value here classifies a mechanism.
///     </para>
///     <para>
///         <b>Known dependency.</b> <see cref="BladeCollisionRelay" /> lives in the
///         <c>BladeContact.Runtime</c> assembly — the treatment's own package — which Probes rule 3 exists
///         to prevent. It is a passive collision forwarder, present on 20/20 station proxies, and
///         <see cref="RqProximityDebug" /> already depends on it, so this follows precedent rather than
///         opening a new breach. Recorded in DECLARATION_StationObjectSets_LikeForLike.md §7.14.
///     </para>
///     <para>
///         This component writes nothing to the simulation. It applies no force, moves nothing, and touches
///         no collider, body or material.
///     </para>
/// </remarks>
[DisallowMultipleComponent]
[DefaultExecutionOrder(150)]
public sealed class RqContactPatchProbe : MonoBehaviour
{
    [Tooltip("The contact-side probe whose marker-derived blade frames this probe expresses contact " +
             "points in. Left empty, the one on this GameObject is used. This probe never builds its own " +
             "frame.")]
    [SerializeField] private RqContactSideProbe sideProbe;

    [Tooltip("Blade A's attachable. Left empty, it is resolved from this probe's own Station_ root by " +
             "name prefix, so no station name is hardcoded.")]
    [SerializeField] private RA2AttachableObject swordA;

    [Tooltip("Blade B's attachable. Resolved the same way.")]
    [SerializeField] private RA2AttachableObject swordB;

    [Tooltip("Log once, on the first step that produces a reading, what was resolved and what the first " +
             "patch measured. Provenance for a run; it never gates anything.")]
    [SerializeField] private bool logFirstReading = true;

    private readonly List<ContactPoint> buffer = new List<ContactPoint>(64);
    private readonly List<ContactPoint> scratch = new List<ContactPoint>(64);
    private readonly HashSet<(EntityId, EntityId)> shapePairs = new HashSet<(EntityId, EntityId)>();
    private readonly ContactPoint[] contactScratch = new ContactPoint[128];

    // Shape participation, accumulated per step alongside the points. Names, not IDs, because the
    // question a reviewer asks is "which hull carried this", and deepest_shape alone cannot answer it:
    // the deepest shape at peak penetration need not be the shape that established or sustained the pair.
    private readonly List<string> pairLabelsThisStep = new List<string>(8);
    private readonly List<string> pairLabelsReported = new List<string>(8);
    private string shapePairSummary = "";

    private BladeCollisionRelay relayA;
    private GameObject boundTo;
    private Transform proxyBRoot;
    private bool logged;

    /// <summary>True when both proxies, the relay and both blade frames are available this step.</summary>
    public bool Resolved { get; private set; }

    /// <summary>Why the probe is not reporting, when it is not. Empty when it is.</summary>
    public string RefusalReason { get; private set; } = "";

    /// <summary>True when at least one contact point was delivered for the step just reduced.</summary>
    public bool HasReading { get; private set; }

    // ---- counts -------------------------------------------------------------------------------

    /// <summary>Contact points delivered this step, at any separation. A PAIR count, not a touch count.</summary>
    public int PairPoints { get; private set; }

    /// <summary>Contact points this step with separation ≤ 0. The only count that implies surfaces met.</summary>
    public int OverlapPoints { get; private set; }

    /// <summary>Distinct collider-pair combinations contributing points this step.</summary>
    public int ShapePairs { get; private set; }

    // ---- extent, over ALL delivered points (pair) ---------------------------------------------

    /// <summary>Patch extent along blade A's own axis, millimetres, over all delivered points.</summary>
    public float PairExtentAlongAMm { get; private set; }

    /// <summary>Patch extent across blade A's width, millimetres, over all delivered points.</summary>
    public float PairExtentAcrossAMm { get; private set; }

    /// <summary>Patch extent along blade B's own axis, millimetres, over all delivered points.</summary>
    public float PairExtentAlongBMm { get; private set; }

    /// <summary>Patch extent across blade B's width, millimetres, over all delivered points.</summary>
    public float PairExtentAcrossBMm { get; private set; }

    // ---- extent, over the OVERLAP subset only --------------------------------------------------

    /// <summary>Patch extent along blade A's axis over points with separation ≤ 0, millimetres.</summary>
    public float OverlapExtentAlongAMm { get; private set; }

    /// <summary>Patch extent across blade A's width over points with separation ≤ 0, millimetres.</summary>
    public float OverlapExtentAcrossAMm { get; private set; }

    /// <summary>Patch extent along blade B's axis over points with separation ≤ 0, millimetres.</summary>
    public float OverlapExtentAlongBMm { get; private set; }

    /// <summary>Patch extent across blade B's width over points with separation ≤ 0, millimetres.</summary>
    public float OverlapExtentAcrossBMm { get; private set; }

    // ---- centroid -------------------------------------------------------------------------------

    /// <summary>Contact centroid in blade A's frame: x across, y along, z through the face. Metres.</summary>
    public Vector3 CentroidALocal { get; private set; }

    /// <summary>Contact centroid in blade B's frame, same axes. Metres.</summary>
    public Vector3 CentroidBLocal { get; private set; }

    /// <summary>Contact centroid in world space. The world original is kept beside the frame-local ones.</summary>
    public Vector3 CentroidWorld { get; private set; }

    // ---- normals ---------------------------------------------------------------------------------

    /// <summary>
    ///     Greatest angle between any delivered contact normal and the mean normal, degrees. A flat patch
    ///     reads near zero; a patch wrapping an edge reads high.
    /// </summary>
    public float NormalSpreadDeg { get; private set; }

    /// <summary>The mean contact normal this step, world space, normalised.</summary>
    public Vector3 MeanNormal { get; private set; }

    /// <summary>Most negative separation among delivered points this step, metres. Signed.</summary>
    public float MinSeparationM { get; private set; }

    /// <summary>Greatest separation among delivered points this step, metres. Signed.</summary>
    public float MaxSeparationM { get; private set; }

    private void Awake()
    {
        if (sideProbe == null) sideProbe = GetComponent<RqContactSideProbe>();
        ResolveSwords();
    }

    private void OnDisable()
    {
        Unsubscribe();
    }

    /// <summary>
    ///     Walks up to this probe's own <c>Station_</c> root and takes the two attachables from inside it by
    ///     name prefix. Station-local, so no cross-station leak is possible and no station name is baked in.
    /// </summary>
    private void ResolveSwords()
    {
        if (swordA != null && swordB != null) return;

        var root = transform;
        while (root != null && !root.name.StartsWith("Station_")) root = root.parent;
        if (root == null) return;

        foreach (var att in root.GetComponentsInChildren<RA2AttachableObject>(true))
        {
            if (swordA == null && att.gameObject.name.StartsWith("Sword_A")) swordA = att;
            else if (swordB == null && att.gameObject.name.StartsWith("Sword_B")) swordB = att;
        }
    }

    /// <summary>
    ///     Ragdoll Animator raises no signal when a sword is worn, so the generated root is watched here —
    ///     the same point <see cref="RqProximityDebug" /> and <see cref="RqProxyPhysicsDeclaration" /> bind at.
    /// </summary>
    private void LateUpdate()
    {
        ResolveSwords();
        if (swordA == null || swordB == null) return;

        var generatedA = swordA.GeneratedPhysicsObject;
        var generatedB = swordB.GeneratedPhysicsObject;
        proxyBRoot = generatedB == null ? null : generatedB.transform;

        if (generatedA == null)
        {
            Unsubscribe();
            return;
        }

        if (generatedA == boundTo && relayA != null) return;

        Unsubscribe();
        boundTo = generatedA;

        // Share the relay, never add a second. Both bodies report the same collision, so subscribing to
        // A's alone is the whole set; taking B's as well would double every count.
        relayA = generatedA.GetComponent<BladeCollisionRelay>();
        if (relayA == null) relayA = generatedA.AddComponent<BladeCollisionRelay>();
        relayA.Collided += OnCollided;
    }

    private void Unsubscribe()
    {
        if (relayA != null) relayA.Collided -= OnCollided;
        relayA = null;
        boundTo = null;
    }

    /// <summary>
    ///     Accumulates the step's contact points. Only contacts against blade B's own proxy are kept, so a
    ///     blade touching anything else in the scene cannot enter the patch.
    /// </summary>
    private void OnCollided(Collision collision)
    {
        if (proxyBRoot == null) return;

        var other = collision.collider;
        if (other == null || !other.transform.IsChildOf(proxyBRoot)) return;

        var n = collision.GetContacts(contactScratch);
        for (var i = 0; i < n; i++)
        {
            buffer.Add(contactScratch[i]);

            // Identity of the shape pair, order-independent, so A-vs-B and B-vs-A count once. Taken per
            // CONTACT, not once per collision: a single collision can carry points from more than one
            // shape pair, and counting only the first would under-report the route.
            var ca = contactScratch[i].thisCollider;
            var cb = contactScratch[i].otherCollider;
            if (ca == null || cb == null) continue;

            EntityId ia = ca.GetEntityId();
            EntityId ib = cb.GetEntityId();
            var key = ia.CompareTo(ib) < 0 ? (ia, ib) : (ib, ia);
            if (!shapePairs.Add(key)) continue;

            // Name it A-side first so the label reads the same way regardless of which body reported.
            string na = StripProxySuffix(ca.transform.name);
            string nb = StripProxySuffix(cb.transform.name);
            bool aIsOwn = proxyBRoot == null || !ca.transform.IsChildOf(proxyBRoot);
            pairLabelsThisStep.Add(aIsOwn ? na + "|" + nb : nb + "|" + na);
        }
    }

    /// <summary>
    ///     The shape pairs that carried contact THIS step, as "Aside|Bside" collider names, distinct and
    ///     order-normalised. Empty when there was no contact.
    /// </summary>
    /// <remarks>
    ///     Exposed because <c>deepest_shape</c> answers a different question. The deepest shape at peak
    ///     penetration need not be the shape that established the pair or the one that sustained it, and
    ///     on the coarse arm the route matters: its hulls stand 0.5 mm proud of the blade surface, so a
    ///     duration difference on that arm may coincide with a hull route rather than with behaviour.
    ///     Recording the route per step is what lets a reviewer tell those apart.
    /// </remarks>
    public IReadOnlyList<string> ShapePairLabels => pairLabelsReported;

    /// <summary>The step's shape pairs joined with ';', or empty. A CSV-safe single field.</summary>
    public string ShapePairSummary => shapePairSummary;

    private static string StripProxySuffix(string n)
    {
        const string suffix = ":Attachable Physics";
        return n.EndsWith(suffix) ? n.Substring(0, n.Length - suffix.Length) : n;
    }

    private void FixedUpdate()
    {
        Reduce();
        buffer.Clear();
        shapePairs.Clear();
        pairLabelsThisStep.Clear();
    }

    /// <summary>Reduces the accumulated points into this step's reading. Writes nothing to the simulation.</summary>
    private void Reduce()
    {
        HasReading = false;
        PairPoints = 0;
        OverlapPoints = 0;
        ShapePairs = 0;

        if (sideProbe == null)
        {
            Resolved = false;
            RefusalReason = "no RqContactSideProbe, so there is no blade frame to express a patch in.";
            return;
        }

        if (swordA == null || swordB == null)
        {
            Resolved = false;
            RefusalReason = "swordA and swordB did not resolve from this probe's own Station_ root.";
            return;
        }

        var fa = sideProbe.FrameA;
        var fb = sideProbe.FrameB;
        if (!fa.Valid || !fb.Valid)
        {
            Resolved = false;
            RefusalReason = "the contact-side probe has no reading this step, so its frames are not valid.";
            return;
        }

        Resolved = true;
        RefusalReason = "";

        PairPoints = buffer.Count;
        ShapePairs = shapePairs.Count;

        // Sorted so the label is stable step to step: an unsorted set would make an unchanged route look
        // like a changing one purely from callback ordering.
        pairLabelsReported.Clear();
        pairLabelsReported.AddRange(pairLabelsThisStep);
        pairLabelsReported.Sort(System.StringComparer.Ordinal);
        shapePairSummary = pairLabelsReported.Count == 0
            ? ""
            : string.Join(";", pairLabelsReported.ToArray());

        if (PairPoints == 0) return;

        HasReading = true;

        Measure(buffer, fa, fb, out var alongA, out var acrossA, out var alongB, out var acrossB,
                out var centroidW, out var centroidA, out var centroidB,
                out var meanN, out var spread, out var minSep, out var maxSep);

        PairExtentAlongAMm = alongA;
        PairExtentAcrossAMm = acrossA;
        PairExtentAlongBMm = alongB;
        PairExtentAcrossBMm = acrossB;
        CentroidWorld = centroidW;
        CentroidALocal = centroidA;
        CentroidBLocal = centroidB;
        MeanNormal = meanN;
        NormalSpreadDeg = spread;
        MinSeparationM = minSep;
        MaxSeparationM = maxSep;

        // The overlap subset, measured separately because a patch across the speculative envelope is not a
        // contact patch.
        scratch.Clear();
        for (var i = 0; i < buffer.Count; i++)
            if (buffer[i].separation <= 0f)
                scratch.Add(buffer[i]);

        OverlapPoints = scratch.Count;
        if (OverlapPoints > 0)
        {
            Measure(scratch, fa, fb, out var oAlongA, out var oAcrossA, out var oAlongB, out var oAcrossB,
                    out _, out _, out _, out _, out _, out _, out _);
            OverlapExtentAlongAMm = oAlongA;
            OverlapExtentAcrossAMm = oAcrossA;
            OverlapExtentAlongBMm = oAlongB;
            OverlapExtentAcrossBMm = oAcrossB;
        }
        else
        {
            OverlapExtentAlongAMm = 0f;
            OverlapExtentAcrossAMm = 0f;
            OverlapExtentAlongBMm = 0f;
            OverlapExtentAcrossBMm = 0f;
        }

        if (!logFirstReading || logged) return;
        logged = true;
        Debug.Log($"[ContactPatch] {name}: first reading. pairPoints={PairPoints} overlapPoints={OverlapPoints} " +
                  $"shapePairs={ShapePairs} extentAlongA={PairExtentAlongAMm:F3}mm acrossA={PairExtentAcrossAMm:F3}mm " +
                  $"alongB={PairExtentAlongBMm:F3}mm acrossB={PairExtentAcrossBMm:F3}mm " +
                  $"normalSpread={NormalSpreadDeg:F2}deg sep[{MinSeparationM * 1000f:F3}..{MaxSeparationM * 1000f:F3}]mm", this);
    }

    /// <summary>
    ///     Extent is max minus min of the frame-local coordinate over the supplied points — the span the
    ///     patch actually occupies on that blade, not a count and not a fitted shape.
    /// </summary>
    private static void Measure(List<ContactPoint> pts, RqContactSideProbe.BladeFrame fa,
                                RqContactSideProbe.BladeFrame fb,
                                out float alongAMm, out float acrossAMm,
                                out float alongBMm, out float acrossBMm,
                                out Vector3 centroidWorld, out Vector3 centroidA, out Vector3 centroidB,
                                out Vector3 meanNormal, out float normalSpreadDeg,
                                out float minSep, out float maxSep)
    {
        float minAlongA = float.MaxValue, maxAlongA = float.MinValue;
        float minAcrossA = float.MaxValue, maxAcrossA = float.MinValue;
        float minAlongB = float.MaxValue, maxAlongB = float.MinValue;
        float minAcrossB = float.MaxValue, maxAcrossB = float.MinValue;
        var sumWorld = Vector3.zero;
        var sumNormal = Vector3.zero;
        minSep = float.MaxValue;
        maxSep = float.MinValue;

        for (var i = 0; i < pts.Count; i++)
        {
            var p = pts[i].point;
            sumWorld += p;
            sumNormal += pts[i].normal;

            var s = pts[i].separation;
            if (s < minSep) minSep = s;
            if (s > maxSep) maxSep = s;

            var la = fa.ToLocal(p);
            if (la.y < minAlongA) minAlongA = la.y;
            if (la.y > maxAlongA) maxAlongA = la.y;
            if (la.x < minAcrossA) minAcrossA = la.x;
            if (la.x > maxAcrossA) maxAcrossA = la.x;

            var lb = fb.ToLocal(p);
            if (lb.y < minAlongB) minAlongB = lb.y;
            if (lb.y > maxAlongB) maxAlongB = lb.y;
            if (lb.x < minAcrossB) minAcrossB = lb.x;
            if (lb.x > maxAcrossB) maxAcrossB = lb.x;
        }

        alongAMm = (maxAlongA - minAlongA) * 1000f;
        acrossAMm = (maxAcrossA - minAcrossA) * 1000f;
        alongBMm = (maxAlongB - minAlongB) * 1000f;
        acrossBMm = (maxAcrossB - minAcrossB) * 1000f;

        centroidWorld = sumWorld / pts.Count;
        centroidA = fa.ToLocal(centroidWorld);
        centroidB = fb.ToLocal(centroidWorld);

        meanNormal = sumNormal.sqrMagnitude > 1e-12f ? sumNormal.normalized : Vector3.zero;

        normalSpreadDeg = 0f;
        if (meanNormal != Vector3.zero)
            for (var i = 0; i < pts.Count; i++)
            {
                var ang = Vector3.Angle(pts[i].normal, meanNormal);
                if (ang > normalSpreadDeg) normalSpreadDeg = ang;
            }
    }
}
