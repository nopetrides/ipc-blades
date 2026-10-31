using FIMSpace.FProceduralAnimation;
using UnityEngine;

/// <summary>
///     The deliberately naive comparator: two enlarged blade hulls with two fixed PhysicsMaterials.
/// </summary>
/// <remarks>
///     <para>
///         <b>This is a collider/material heuristic, not a physical cutting-edge model.</b> It is here as
///         the third arm of the comparison, to show what the obvious cheap approach actually does. The
///         idea is the one most people reach for first: give the blade a slightly fatter hull on its edge
///         sides with a grippy material, another slightly fatter hull on its face sides with a slippery
///         one, leave both switched on, and let ordinary PhysX decide which one touches.
///     </para>
///     <para>
///         <b>Nothing here classifies anything.</b> There is no semantic query, no per-contact force, no
///         pair filtering and no collider switching. Whichever hull happens to protrude in the current
///         presentation engages first and its material governs the friction, and when both engage PhysX
///         combines whatever it finds. Its ambiguity and its artefacts are the point of including it; do
///         not "improve" them away.
///     </para>
///     <para>
///         <b>The hulls are derived, not modelled.</b> Both are the same cooked A1 blade hull with every
///         vertex pushed out 0.5 mm along one authored axis of the profile: the edge/width axis for
///         <see cref="edgeHull"/>, the face-normal/thickness axis for <see cref="flatHull"/>.
///     </para>
///     <para>
///         <b>The original hull is DISABLED, changed 2026-08-26.</b> This paragraph previously said it
///         "stays live alongside them", and the field said "never disabled". Both were wrong for a
///         reason that only became visible once the materials were split: a blade must present
///         <b>two</b> colliders, not three.
///     </para>
///     <para>
///         <b>Why it matters, and it is not cosmetic.</b> The original hull carries no explicit material,
///         so it inherits the project default — <c>BladeGrip</c>, whose <c>frictionCombine</c> is
///         <b>Maximum</b>. PhysX resolves mismatched combine modes by priority
///         (<c>Average &lt; Minimum &lt; Multiply &lt; Maximum</c>), so a live original hull
///         <b>overrides</b> the two derived hulls' <c>Minimum</c> and the pair collapses back to one
///         coefficient. That is the mechanism behind the recorded finding that QuickDirty "cannot
///         distinguish EdgeEdge from EdgeFlat at all" — measured as 20.55 N against 20.55 N. With the
///         original hull off, the two derived hulls decide the pair and the presentations separate.
///     </para>
///     <para>
///         <b>It must be disabled on the PROXY, not the authored object.</b> Verified live: at attach,
///         Ragdoll Animator disables every authored collider and sets the authored body
///         <c>isKinematic</c> with <c>detectCollisions = false</c>, then generates
///         <c>GeneratedPhysicsColliders</c> on <c>&lt;Sword&gt;:Attachable Physics</c> — and those copies
///         come through ENABLED. Disabling the authored collider therefore achieves nothing on a station
///         sword. It is still done, because on an XRI grabbable or a freestanding target there is no
///         proxy and the authored collider is the live one.
///     </para>
///     <para>
///         The component carries no station or rig knowledge, so it can be added to an XRI sword or a
///         freestanding target unchanged.
///     </para>
/// </remarks>
[DisallowMultipleComponent]
public sealed class RqQuickDirtyBlade : MonoBehaviour
{
    [Tooltip("The blade's ORIGINAL cooked hull. DISABLED at runtime, on the authored object and on the " +
             "generated proxy, so the blade presents TWO colliders and not three. Its material is never " +
             "written — only its enabled state. See the class remarks for why a live original hull " +
             "collapses the pair back to one friction coefficient.")]
    [SerializeField] private Collider originalHull;

    [Tooltip("Leave on. Off, the original hull stays live and the two derived hulls stop deciding the " +
             "pair's friction — which reproduces the recorded EdgeEdge/EdgeFlat indistinguishability. " +
             "Exposed so that failure can be demonstrated deliberately rather than only by accident.")]
    [SerializeField] private bool disableOriginalHull = true;

    [Tooltip("Attachable used to reach the generated proxy. Left empty it is taken from this object. " +
             "Absent entirely — an XRI grabbable or a freestanding target — only the authored collider " +
             "is disabled, which is correct there because no proxy exists.")]
    [SerializeField] private RA2AttachableObject attachable;

    private bool proxyHandled;

    [Tooltip("Hull expanded along the authored edge/width axis, so the cutting-edge sides protrude first. " +
             "Carries the high-friction material.")]
    [SerializeField] private Collider edgeHull;

    [Tooltip("Hull expanded along the authored face-normal/thickness axis, so the broad-face sides " +
             "protrude first. Carries the low-friction material.")]
    [SerializeField] private Collider flatHull;

    [Header("Gizmos")]
    [Tooltip("Draw the derived hulls in the Scene view. They have no renderer, so this is the only way " +
             "to see them.")]
    [SerializeField] private bool drawGizmos = true;

    [SerializeField] private Color edgeHullColour = new Color(1f, 0.45f, 0.1f, 1f);
    [SerializeField] private Color flatHullColour = new Color(0.2f, 0.7f, 1f, 1f);

    /// <summary>The blade's original cooked hull, unaltered.</summary>
    public Collider OriginalHull => originalHull;

    /// <summary>The edge-expanded hull and its high-friction material.</summary>
    public Collider EdgeHull => edgeHull;

    /// <summary>The face-expanded hull and its low-friction material.</summary>
    public Collider FlatHull => flatHull;

    /// <summary>One line naming every shape this blade presents to PhysX, and its material.</summary>
    public string Describe() =>
        Describe(originalHull) + "   " + Describe(edgeHull) + "   " + Describe(flatHull);

    private static string Describe(Collider collider)
    {
        if (collider == null) return "(missing)";

        PhysicsMaterial material = collider.sharedMaterial;
        string friction = material == null
            ? "no material (PhysX built-in 0.6/0.6)"
            : material.name + " s" + material.staticFriction.ToString("F2") +
              "/d" + material.dynamicFriction.ToString("F2");

        return "[" + collider.name + ": " + friction + "]";
    }

    private void Awake()
    {
        if (attachable == null) attachable = GetComponent<RA2AttachableObject>();
        if (!disableOriginalHull || originalHull == null) return;

        // Correct and sufficient where there is no proxy: XRI grabbable, freestanding target. On a
        // station sword RA2 disables this anyway at attach, so it is harmless there.
        originalHull.enabled = false;
    }

    // The proxy does not exist until the sword is worn, so this cannot be done in Awake. Stops
    // polling as soon as it has acted, or immediately if there is no attachable to wait for.
    private void Update()
    {
        if (proxyHandled || !disableOriginalHull || originalHull == null) return;

        if (attachable == null) { proxyHandled = true; return; }
        if (attachable.GeneratedPhysicsObject == null) return;

        var generated = attachable.GeneratedPhysicsColliders;
        if (generated == null) { proxyHandled = true; return; }

        string target = originalHull.gameObject.name;
        int disabled = 0;

        for (int i = 0; i < generated.Count; i++)
        {
            Collider c = generated[i];
            if (c == null) continue;

            // Proxy children are named "<original>:Attachable Physics". Match the exact name or that
            // prefix, so "Blade" cannot also capture a differently-named sibling.
            string n = c.gameObject.name;
            if (n != target && !n.StartsWith(target + ":")) continue;

            c.enabled = false;
            disabled++;
        }

        proxyHandled = true;

        if (disabled == 0)
            Debug.LogWarning("[QuickDirty] " + name + ": the original hull '" + target + "' was NOT found " +
                             "among GeneratedPhysicsColliders, so THREE colliders are still live on the " +
                             "proxy and the derived hulls do not decide the pair's friction. See the class " +
                             "remarks.", this);
    }

    private void OnDrawGizmos()
    {
        if (!drawGizmos) return;

        DrawHull(edgeHull, edgeHullColour);
        DrawHull(flatHull, flatHullColour);
    }

    private static void DrawHull(Collider collider, Color colour)
    {
        var mesh = collider as MeshCollider;
        if (mesh == null || mesh.sharedMesh == null) return;

        Gizmos.color = colour;
        Gizmos.DrawWireMesh(
            mesh.sharedMesh, mesh.transform.position, mesh.transform.rotation, mesh.transform.lossyScale);
    }
}
