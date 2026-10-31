using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
///     Draws the contact-side pair in the Scene view, at the point the reading was taken.
/// </summary>
/// <remarks>
///     <para>
///         The station sign says which pair is engaged; it does not say <i>where</i>. Standing in front of
///         two crossed blades that is the harder half of the question, and a sign reading <c>Ae- / Be-</c>
///         in red is much easier to trust once the annotation is sitting on the place the blades meet.
///     </para>
///     <para>
///         <b>A Scene-view gizmo, not a billboarded world-space text.</b> The first version of this was a
///         <c>TextMeshPro</c> turned to face the active camera; it was rejected because billboarding to
///         the game camera makes it unreadable while working in the editor. <c>Handles.Label</c> always
///         faces the Scene view camera, draws at a fixed screen size regardless of distance, and costs
///         nothing at runtime.
///     </para>
///     <para>
///         <b>KNOWN LIMIT, stated rather than discovered later.</b> Gizmos draw in the Scene view only.
///         This annotation does <b>not</b> appear in the Game view and does <b>not</b> exist in a
///         standalone Quest build. In the headset the station sign is what carries the pairing, and it
///         carries the same string from the same probe. If the pairing is ever wanted in-headset at the
///         blades, that is a world-space text and a separate decision.
///     </para>
///     <para>
///         <b>It draws the geometry, not only the text.</b> A line between the two closest-approach points
///         with a marker at each end, so the annotation shows which two places on the two blades the
///         reading is about. That is the part a floating string could not convey.
///     </para>
///     <para>
///         <b>It reads the probe and recomputes nothing</b>, so it cannot drift from the station sign or
///         from any gate built on the same probe. Red on a full inversion only, matching both — one side
///         changing is the contact travelling around the blade, which happens in valid runs.
///     </para>
///     <para>
///         <b>Nothing here is a contact claim.</b> The two points are where two <c>Collider.ClosestPoint</c>
///         queries converged, which is a surface pair whether or not the blades touch. The separation is
///         printed as context and must never be turned into a statement that the blades did or did not
///         meet.
///     </para>
/// </remarks>
[DisallowMultipleComponent]
public sealed class RqContactSideAnnotation : MonoBehaviour
{
    [Tooltip("Where the pairing is read from. This component never computes it, so the two cannot " +
             "disagree. Left empty it takes the probe on this same GameObject.")]
    [SerializeField] private RqContactSideProbe probe;

    [Tooltip("Draw whenever the Scene view is open. Off, it draws only while this station is selected — " +
             "useful when ten of these at once is too much.")]
    [SerializeField] private bool drawAlways = true;

    [Tooltip("Print the closest-approach separation under the pair. CONTEXT ONLY — a closest-approach " +
             "pair exists whether or not the blades touch, and this is never a contact claim.")]
    [SerializeField] private bool showSeparation = true;

    [Tooltip("Draw the line and end markers between the two closest-approach points, so the annotation " +
             "shows WHICH two places on the two blades the reading is about.")]
    [SerializeField] private bool showGeometry = true;

    [Tooltip("Metres above the midpoint to float the text, so it does not sit inside the steel.")]
    [SerializeField] private float textOffset = 0.05f;

    [Tooltip("Radius of the end markers, metres.")]
    [Min(0f)]
    [SerializeField] private float markerRadius = 0.004f;

    [Tooltip("Stop drawing beyond this distance from the Scene camera, metres. Zero draws at any " +
             "distance. Keeps ten stations from becoming ten overlapping labels when zoomed out.")]
    [Min(0f)]
    [SerializeField] private float maxDrawDistance = 6f;

    private static readonly Color Normal = new Color(0.55f, 0.9f, 1f);
    private static readonly Color Departed = new Color(1f, 0.25f, 0.25f);

    private void Reset() => probe = GetComponent<RqContactSideProbe>();

    private void OnValidate()
    {
        if (probe == null) probe = GetComponent<RqContactSideProbe>();
    }

#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        if (drawAlways) Draw();
    }

    private void OnDrawGizmosSelected()
    {
        if (!drawAlways) Draw();
    }

    private void Draw()
    {
        RqContactSideProbe p = probe != null ? probe : GetComponent<RqContactSideProbe>();
        if (p == null) return;

        // Nothing exists to annotate before Play: the proxy the probe measures is generated at runtime.
        if (!p.HasReading && !p.Refused) return;

        Vector3 mid = p.ContactMidpoint;

        Camera cam = SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.camera : null;
        if (maxDrawDistance > 0f && cam != null &&
            Vector3.Distance(cam.transform.position, mid) > maxDrawDistance) return;

        if (p.Refused)
        {
            Handles.color = Departed;
            Handles.Label(transform.position, "contact side unavailable\n" + p.RefusalReason, Style(true));
            return;
        }

        bool departed = p.Inverted;
        Color c = departed ? Departed : Normal;

        if (showGeometry)
        {
            Gizmos.color = c;
            Gizmos.DrawLine(p.AContactPoint, p.BContactPoint);
            Gizmos.DrawSphere(p.AContactPoint, markerRadius);
            Gizmos.DrawSphere(p.BContactPoint, markerRadius);
        }

        // BOTH pairs are drawn, with their magnitudes, and the validated one is marked rather than
        // being the only one shown.
        //
        // This used to draw p.LeadPair alone. LeadPair follows the station's authored `term`, so on a
        // Flat station it always showed the face pair - including at frames where the face term had no
        // determinate value at all and the edge pair was well determined. The gizmo then disagreed with
        // the apparatus while being internally correct, which is the worst kind of readout.
        //
        // The magnitudes are what make this threshold-free: a face dot is a direction cosine, so it
        // reads 0 when the other blade lies edge-on, and a reader who can see 0.00 does not need a
        // near-zero rule to know the sign beside it is a tiebreak. No criterion for "which term is
        // engaged" is applied here - that is the researcher's, and the gizmo must not pre-empt it.
        string edgeMark = p.VerdictTerm == "edge" ? "  <- validated" : "";
        string faceMark = p.VerdictTerm == "face" ? "  <- validated" : "";

        string body =
            "edge " + p.EdgePair + "   " +
            (p.AAcrossEdgeMetres * 1000f).ToString("F1") + " / " +
            (p.BAcrossEdgeMetres * 1000f).ToString("F1") + " mm" + edgeMark + "\n" +
            "face " + p.FacePair + "   " +
            p.AFaceDot.ToString("F2") + " / " + p.BFaceDot.ToString("F2") + faceMark;

        // Exactly zero is stated because it is exact. Anything short of that would need a threshold.
        if (p.AFaceDot == 0f || p.BFaceDot == 0f)
        {
            body += "\nface dot is exactly 0 - that sign is a tiebreak, not a reading";
        }

        if (p.HasDeclaredReference)
        {
            body += "\ndeclared " + p.DeclaredReference;
            if (departed)
            {
                body += "   BOTH SIDES DEPARTED";
            }
            else if (p.OneSideChanged)
            {
                body += "   (one side changed - does not gate)";
            }
        }

        if (showSeparation) body += "\n" + p.ClosestApproachMm.ToString("F1") + " mm apart";

        Handles.color = c;
        Handles.Label(mid + Vector3.up * textOffset, body, Style(departed));
    }

    /// <summary>
    ///     Built per draw rather than cached: a cached <c>GUIStyle</c> made in a field initialiser is
    ///     constructed outside the editor GUI's skin and comes out with no font.
    /// </summary>
    private static GUIStyle Style(bool departed)
    {
        var s = new GUIStyle(EditorStyles.boldLabel)
        {
            alignment = TextAnchor.UpperLeft,
            fontSize = 11,
            richText = false
        };

        s.normal.textColor = departed ? Departed : Normal;
        return s;
    }
#endif

    /// <summary>Declares which probe this annotation reads. Serialized, not discovered.</summary>
    public void Configure(RqContactSideProbe source) => probe = source;
}
