using UnityEngine;

/// <summary>
///     Draws the Hand↔Sword connection relationship as a coloured line.
///     <para>
///         It answers exactly one question: <i>which hand body and which sword body are being considered as a
///         constrained pair, and what is their current spatial relationship?</i>
///     </para>
///     <para>
///         <b>It does not answer</b> whether that is anatomically the correct grasp point, and it does not
///         measure grasp fidelity. The line length is visually informative because it exposes offset, but it is
///         <b>not an experimental metric</b> and no threshold may be derived from it.
///     </para>
///     <para>
///         <b>The visualiser observes; it never defines.</b> Endpoints and attachment status come from
///         <see cref="HandSwordConnection" />, which is also what creates the joint — so there is one mechanical
///         relationship and one picture of it. This component creates no attachment, moves no body, supplies no
///         anchor, does not touch <c>connectedAnchor</c>, never snaps the sword and never writes pose data. It
///         replaced a manually positioned <c>GripFrame_Sword</c> precisely so that no second, authored definition
///         of "where the grasp is" can exist.
///     </para>
/// </summary>
[ExecuteAlways]
[DisallowMultipleComponent]
[RequireComponent(typeof(LineRenderer))]
public class HandSwordConnectionVisualizer : MonoBehaviour
{
    /// <summary>What the colour means. Connection status only — never line length.</summary>
    public enum Display
    {
        /// <summary>RED — an explicitly unconnected lifecycle state, particularly SNAP. The actor is visually
        /// posed holding the sword, but the physics relationship has not been established.</summary>
        Unconnected,

        /// <summary>YELLOW — prospective. No attachment exists yet; the pair is being previewed at the current
        /// transforms. Nothing is snapped, solved toward, or moved to shorten this line.</summary>
        Prospective,

        /// <summary>GREEN — a real Hand↔Sword attachment exists <b>right now</b>, read from the live joint.</summary>
        Connected
    }

    [Tooltip("Hand side. There is no automatic resolution any more: the station protocol object that used to " +
             "declare the sword-to-rig pairing (PilotRunController) is deleted, and NO ActiveArmRagdoll " +
             "remains in P3T_SampleScene - the station dummies are driven by RagdollAnimator2 instead. Left " +
             "empty, HandSwordConnection.Resolve returns Valid=false and the line simply stays hidden, which " +
             "is the honest display for a pair this component cannot see.")]
    public ActiveArmRagdoll rig;

    [Tooltip("Sword side. Defaults to this GameObject.")]
    public Transform sword;

    public float lineWidth = 0.006f;

    [Tooltip("Draw in edit mode so the relationship can be inspected while reconstructing stations.")]
    public bool showInEditMode = true;

    public Color unconnectedColor = new Color(1f, 0.15f, 0.15f);
    public Color prospectiveColor = new Color(1f, 0.85f, 0.1f);
    public Color connectedColor = new Color(0.2f, 1f, 0.35f);

    /// <summary>Last resolved status, for inspection. Read-only output.</summary>
    public Display CurrentDisplay { get; private set; }

    /// <summary>Last resolved endpoint separation, millimetres. <b>Geometry of the line, not a metric.</b></summary>
    public float CurrentSeparationMm { get; private set; }

    private LineRenderer _lr;
    private Material _mat;

    private void Reset()
    {
        sword = transform;
        ResolveRefs();
        ConfigureLine();
    }

    private void OnEnable()
    {
        ResolveRefs();
        ConfigureLine();
    }

    private void LateUpdate()
    {
        if (!Application.isPlaying && !showInEditMode)
        {
            if (_lr != null) _lr.enabled = false;
            return;
        }

        Draw();
    }

    /// <summary>
    ///     Resolve the sword side only. The rig side is <b>declared, never guessed</b>: it used to come from a
    ///     station protocol object's <c>swordA/dummyA</c> wiring, and that object is deleted. Nothing here
    ///     falls back to proximity or name-matching, because a wrong pairing would draw a confident line
    ///     between two bodies that are not a pair.
    /// </summary>
    private void ResolveRefs()
    {
        if (sword == null) sword = transform;
    }

    private void ConfigureLine()
    {
        _lr = GetComponent<LineRenderer>();
        if (_lr == null) return;
        _lr.useWorldSpace = true;
        _lr.positionCount = 2;
        _lr.numCapVertices = 2;
        _lr.textureMode = LineTextureMode.Stretch;
        _lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        _lr.receiveShadows = false;
        _lr.alignment = LineAlignment.View;
        if (_lr.sharedMaterial == null) _lr.sharedMaterial = LineMaterial();
    }

    /// <summary>An unlit, vertex-coloured material so start/end colour actually shows. Created in memory and
    /// never saved as an asset — this is a debug overlay, not project content.</summary>
    private Material LineMaterial()
    {
        if (_mat != null) return _mat;
        var sh = Shader.Find("Universal Render Pipeline/Unlit");
        if (sh == null) sh = Shader.Find("Sprites/Default");
        if (sh == null) sh = Shader.Find("Unlit/Color");
        if (sh == null) return null;
        _mat = new Material(sh) {hideFlags = HideFlags.HideAndDontSave};
        return _mat;
    }

    private void Draw()
    {
        if (_lr == null) ConfigureLine();
        if (_lr == null) return;
        if (rig == null || sword == null) ResolveRefs();

        var st = HandSwordConnection.Resolve(rig, sword);
        if (!st.Valid)
        {
            _lr.enabled = false;
            return;
        }

        CurrentSeparationMm = st.SeparationMm;
        CurrentDisplay = ResolveDisplay(st);

        var c = CurrentDisplay == Display.Connected ? connectedColor
            : CurrentDisplay == Display.Unconnected ? unconnectedColor
            : prospectiveColor;

        _lr.enabled = true;
        _lr.startWidth = lineWidth;
        _lr.endWidth = lineWidth;
        _lr.startColor = c;
        _lr.endColor = c;
        if (_lr.sharedMaterial != null) _lr.sharedMaterial.color = c;
        _lr.SetPosition(0, st.HandPoint);
        _lr.SetPosition(1, st.SwordPoint);
    }

    /// <summary>
    ///     Colour from CONNECTION STATUS, never from line length.
    ///     <para>
    ///         GREEN is taken only from <see cref="HandSwordConnection.State.Attached" />, which reads the live
    ///         joint. It is deliberately <b>not</b> inferred from the lifecycle mode: if ARM-HOLD is entered but
    ///         attachment creation fails, the line must not turn green — and when the joint is destroyed the green
    ///         must vanish on the same frame rather than lingering as a stale picture.
    ///     </para>
    /// </summary>
    private Display ResolveDisplay(HandSwordConnection.State st)
    {
        // GREEN comes only from the live joint, and always did.
        if (st.Attached) return Display.Connected;

        // RED - "explicitly unconnected" - is retired with the lifecycle that defined it. It was read from
        // PilotRunController.Mode.Snapped; the animator's Snap state is NOT the same claim, because the
        // animator does not know whether an attachment exists. Inferring RED from an animator state would be
        // exactly the "GREEN is never inferred from the mode" rule broken in the other direction, so the
        // honest reading for any unattached pair is now "prospective".
        return Display.Prospective;
    }

    private void OnDisable()
    {
        if (_lr != null) _lr.enabled = false;
    }

    private void OnDestroy()
    {
        if (_mat == null) return;
        if (Application.isPlaying) Destroy(_mat);
        else DestroyImmediate(_mat);
    }
}
