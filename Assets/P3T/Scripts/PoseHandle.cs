using UnityEngine;

/// <summary>
///     A pose-authoring handle that is a real scene object, built like the tracking markers: a bright unlit
///     sphere with a runtime label — but <b>deliberately non-collidable</b>.
///     <para>
///         The authoring handles were previously editor-only <c>Handles</c>, which meant they existed nowhere,
///         could not be selected with the normal transform tools, were invisible in play mode, and could not be
///         inspected or version-controlled. As scene objects they are movable with the ordinary Unity gizmo,
///         survive into play mode where the drives can be watched against them, and serialize with the station.
///     </para>
///     <para>
///         <b>No collider, ever.</b> The tracking markers carry colliders on purpose — they are physical
///         ping-pong balls that participate in contact. These do not: an authoring aid must never enter the
///         mechanics it is used to set up. <see cref="OnValidate" /> removes any collider that appears.
///     </para>
///     <para>
///         The two grasp roles are <b>declarations, not measurements</b>: they say where this reconstructed hand
///         is intended to hold this sword, and claim nothing about the physical actor's grasp — the ±2 cm marker
///         floor could not support such a claim. Every implicit alternative was tried and each misled (sword
///         transform origin, hand-bone origin, grip <c>Renderer.bounds</c>, palm proxy, cylinder centre), so the
///         grip is declared rather than inferred.
///     </para>
/// </summary>
[DisallowMultipleComponent]
public class PoseHandle : MonoBehaviour
{
    public enum Kind
    {
        /// <summary>Where the wrist should end up. An authoring control, never snapped to anything.</summary>
        WristTarget,

        /// <summary>Which way the elbow points — its swivel about the shoulder→wrist axis.</summary>
        ElbowHint,

        /// <summary>
        ///     On a sword: the declared grasp frame for this station — where this reconstructed hand is intended
        ///     to hold it.
        ///     <para>
        ///         There is deliberately <b>no matching frame on the hand</b>. Two declared frames produce an
        ///         offset between them, and that offset is functionally the scalar grip error we prohibited: it
        ///         reads as a measurement while actually being the distance between two points we chose
        ///         ourselves. Today already contains one retracted finding that began exactly that way.
        ///     </para>
        ///     <para>
        ///         The grasp is judged <b>visually</b> — does the rendered hand wrap the real grip geometry — and
        ///         the Hand↔Sword relationship is <b>recorded afterwards</b>, at the weld, as the reproducible
        ///         mechanical initial condition of the accepted reconstruction. It is never optimised toward.
        ///     </para>
        /// </summary>
        /// <remarks>
        ///     ⛔ <b>RETIRED 2026-08-08. Nothing creates or draws these any more.</b> A hand-placed grasp frame is
        ///     a second, authored definition of "where the grasp is", competing with the attachment that actually
        ///     binds the sword. The Hand↔Sword relationship is now derived by <c>HandSwordConnection</c> — the
        ///     same type that creates the joint — and drawn by <c>HandSwordConnectionVisualizer</c>.
        ///     <para>
        ///         <b>The member is kept, and kept at this index, on purpose.</b> Removing an enum member silently
        ///         re-points every serialized field of that type by index; that has already produced one run under
        ///         a factor nobody chose. Deleting it buys nothing and risks that again.
        ///     </para>
        /// </remarks>
        SwordGrasp
    }

    public Kind kind = Kind.WristTarget;

    [Tooltip("Shown on the runtime label after the kind. Use it for live context, e.g. which station this is.")]
    public string note = "";

    [Tooltip("Why this handle is where it is. A declaration needs its justification attached — especially for " +
             "the grasp frames, which are chosen, not measured.")]
    [TextArea(2, 4)]
    public string rationale = "";

    [Tooltip("Sphere diameter in metres. The tracking markers are 0.04 (40 mm); handles default smaller so they " +
             "are not mistaken for markers.")]
    public float diameter = 0.025f;

    public bool showLabel = true;

    private TextMesh _label;

    public Color ColorFor()
    {
        switch (kind)
        {
            case Kind.WristTarget: return new Color(0.25f, 1f, 0.45f);
            case Kind.ElbowHint: return new Color(0.35f, 0.75f, 1f);
            default: return new Color(1f, 0.62f, 0.1f);
        }
    }

    private void OnValidate()
    {
        StripColliders();
        Rescale();
        UpdateLabel();
    }

    private void Start()
    {
        StripColliders();
        UpdateLabel();
    }

    // Only in PLAY mode. The label is a real serialized child, so its text does not need regenerating every
    // frame - and per-frame transform writes in edit mode would dirty the scene continuously.
    private void LateUpdate()
    {
        if (!Application.isPlaying) return;
        var cam = Camera.main;
        if (cam != null && _label != null) _label.transform.rotation = cam.transform.rotation;
    }

    /// <summary>An authoring aid must not participate in the mechanics it exists to set up.</summary>
    private void StripColliders()
    {
        foreach (var c in GetComponents<Collider>())
            if (c != null)
            {
                c.enabled = false;
                if (Application.isPlaying) Destroy(c);
                else DestroyImmediate(c, true);
            }
    }

    private void Rescale()
    {
        if (diameter <= 0f) diameter = 0.025f;
        transform.localScale = Vector3.one * diameter;
    }

    /// <summary>
    ///     Keep the serialized label in step with the handle's declared identity. The label is a real scene child
    ///     (a <c>TextMesh</c> named "Label") created by the builder — not produced at runtime — so this only
    ///     refreshes its text and colour when the declaration changes.
    /// </summary>
    private void UpdateLabel()
    {
        if (_label == null)
        {
            var t = transform.Find("Label");
            if (t != null) _label = t.GetComponent<TextMesh>();
        }

        if (_label == null) return;
        if (_label.gameObject.activeSelf != showLabel) _label.gameObject.SetActive(showLabel);
        if (!showLabel) return;
        var want = kind + (string.IsNullOrEmpty(note) ? "" : "\n" + note);
        if (_label.text != want) _label.text = want;
        _label.color = ColorFor();
    }
}
