using FIMSpace.FProceduralAnimation;
using UnityEngine;

/// <summary>
///     While a sword is WORN, forces the authored (visible) body's rigidbody interpolation to
///     <see cref="RigidbodyInterpolation.None" /> and restores the original value when it is unworn. This
///     removes the cause of the worn-sword visual strobe. It changes nothing else.
/// </summary>
/// <remarks>
///     <para>
///         <b>The cause, measured 2026-08-21.</b> The authored sword carries a kinematic Rigidbody with
///         <c>interpolation = Interpolate</c>. Unity applies an interpolated pose to an interpolated
///         rigidbody's transform <b>between the fixed loop and <c>Update</c></b>. Ragdoll Animator writes the
///         visual pose to the TRANSFORM and never to <c>Rigidbody.position</c>, so that interpolation runs
///         from stale poses and displaced the sword by a median <b>100-490 mm on every frame</b> —
///         <c>rbVsTransform</c> tracked the displacement to three digits, which identifies the writer as the
///         physics-to-transform sync rather than any script. Ragdoll Animator's late pass corrected it, which
///         is why it was invisible; on a render frame with no new fixed step that pass returns early at
///         <c>RagdollHandler.Update.cs:209</c> and the displaced pose is drawn. The gate is the EXPOSURE
///         mechanism, not the cause.
///     </para>
///     <para>
///         <b>Why the authored body must not interpolate, while the proxy must.</b> While worn the authored
///         body is a visual stand-in: kinematic, <c>detectCollisions = false</c>, colliders disabled, and its
///         transform authored by Ragdoll Animator. There is nothing for interpolation to smooth, and because
///         interpolation never touches the simulation this cannot perturb any physics result. The GENERATED
///         PROXY is the body that actually simulates and that every measurement reads; it keeps the handler's
///         <c>RigidbodiesInterpolation</c> and is never touched here. Verified 29/29 on both sides.
///     </para>
///     <para>
///         <b>Ordering.</b> Suppression is applied only once <c>GeneratedPhysicsObject</c> exists, so it can
///         never run before the proxy has been generated. That requirement is met structurally rather than by
///         sequencing.
///     </para>
///     <para>
///         Result: zero-step divergence 101.4 -> 0.029 mm and 121.0 -> 0.013 mm, established by an
///         interpolation A/B and a crossover. See <c>Workspace/Week7/FIX_WornSwordVisualDesync_MinSpec.md</c>
///         and <b>N11</b> in <c>DECLARATION_Draft9_RagdollHandler.md</c>, which predicted the hazard.
///     </para>
///     <para>
///         A local-pose mirror was tried first and is <b>deliberately gone</b>. It corrected the symptom every
///         frame and was measured redundant once the cause was removed (0.29-0.59 mm residual against
///         0.013-0.029 mm). It is not kept as a fallback: a standing corrective write would hide the next
///         writer exactly as Ragdoll Animator's correction hid this one. It remains in git history.
///     </para>
/// </remarks>
[DisallowMultipleComponent]
public sealed class RqAttachableVisualSync : MonoBehaviour
{
    [Tooltip("The worn attachable this belongs to. Left empty, the one on this object is used.")]
    [SerializeField] private RA2AttachableObject attachable;

    [Tooltip("While worn, force the authored body's interpolation to None and restore it on unwear. OFF " +
             "restores the pre-fix behaviour, which is the A/B switch: it is read every LateUpdate, so it " +
             "can be flipped mid-run without restarting.")]
    [SerializeField] private bool suppressAuthoredInterpolation = true;

    [Tooltip("Log once per wear and unwear what was changed. One line per sword, not per frame.")]
    [SerializeField] private bool logOnce = true;

    private Rigidbody authoredBody;
    private bool interpolationCaptured;
    private RigidbodyInterpolation originalInterpolation;
    private bool warnedNotKinematic;

    /// <summary>True while the authored body is being held at <c>None</c>.</summary>
    public bool Suppressing => interpolationCaptured;

    /// <summary>What the authored body's interpolation was before it was worn, restored on unwear.</summary>
    public RigidbodyInterpolation OriginalInterpolation => originalInterpolation;

    private void Awake()
    {
        if (attachable == null) attachable = GetComponent<RA2AttachableObject>();
        authoredBody = GetComponent<Rigidbody>();

        if (attachable == null)
            Debug.LogWarning("[VisualSync] " + name + ": no RA2AttachableObject, so interpolation is never " +
                             "suppressed. This sword keeps the zero-step visual strobe.", this);
        if (authoredBody == null)
            Debug.LogWarning("[VisualSync] " + name + ": no Rigidbody, so there is no interpolation to " +
                             "suppress.", this);
    }

    /// <remarks>
    ///     <c>LateUpdate</c> because Ragdoll Animator raises no public signal when a sword is worn or removed,
    ///     so the generated root has to be polled — the same point <see cref="RqProxyPhysicsDeclaration" />
    ///     rebinds at.
    /// </remarks>
    private void LateUpdate()
    {
        if (attachable == null) { RestoreInterpolation(); return; }

        // The same condition Ragdoll Animator uses: membership in the handler's attachables list. AttachedTo
        // is nulled synchronously in RemoveFromCurrentDummy, so a released sword is restored promptly.
        RagdollHandler wornBy = attachable.AttachedTo;
        if (wornBy == null || !wornBy.IsWearingAttachable(attachable)) { RestoreInterpolation(); return; }
        if (attachable.GeneratedPhysicsObject == null) { RestoreInterpolation(); return; }

        if (suppressAuthoredInterpolation) SuppressInterpolation();
        else RestoreInterpolation();
    }

    private void SuppressInterpolation()
    {
        if (authoredBody == null) authoredBody = GetComponent<Rigidbody>();
        if (authoredBody == null) return;

        if (!interpolationCaptured)
        {
            originalInterpolation = authoredBody.interpolation;
            interpolationCaptured = true;
            if (logOnce)
                Debug.Log("[VisualSync] " + name + ": authored body interpolation " + originalInterpolation +
                          " -> None while worn. The generated proxy is untouched.", this);
        }

        if (authoredBody.interpolation != RigidbodyInterpolation.None)
            authoredBody.interpolation = RigidbodyInterpolation.None;

        // Not a gate — interpolation cannot affect the simulation either way. But a worn attachable is
        // supposed to be kinematic with collisions off, and if it is not, that is worth knowing about
        // regardless of this component.
        if (!warnedNotKinematic && (!authoredBody.isKinematic || authoredBody.detectCollisions))
        {
            warnedNotKinematic = true;
            Debug.LogWarning("[VisualSync] " + name + ": worn, but the authored body is isKinematic=" +
                             authoredBody.isKinematic + ", detectCollisions=" + authoredBody.detectCollisions +
                             ". Ragdoll Animator normally sets kinematic with collisions off on wear, so " +
                             "something else is changing it. Not caused by this component.", this);
        }
    }

    /// <summary>Puts back whatever the authored body's interpolation was before it was worn.</summary>
    private void RestoreInterpolation()
    {
        if (!interpolationCaptured) return;
        interpolationCaptured = false;
        warnedNotKinematic = false;
        if (authoredBody == null) authoredBody = GetComponent<Rigidbody>();
        if (authoredBody == null) return;
        authoredBody.interpolation = originalInterpolation;
        if (logOnce)
            Debug.Log("[VisualSync] " + name + ": unworn, authored body interpolation restored to " +
                      originalInterpolation + ".", this);
    }

    private void OnDisable() => RestoreInterpolation();
}
