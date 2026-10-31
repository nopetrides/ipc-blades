using FIMSpace.FProceduralAnimation;
using UnityEngine;

/// <summary>
///     Makes Ragdoll Animator's generated sword proxy carry the same rigidbody values as the authored
///     sword it stands in for, instead of silently reverting to Unity and project defaults.
/// </summary>
/// <remarks>
///     <para>
///         <b>The problem.</b> When <see cref="RA2AttachableObject" /> is worn, the body that actually
///         simulates is a generated copy, and Ragdoll Animator writes exactly THREE properties onto it
///         (<c>RagdollHandler.Attachables.cs:157-159</c>): <c>interpolation</c>,
///         <c>collisionDetectionMode</c> and <c>mass</c>. Everything else on that body is a Unity or
///         project default. Measured at runtime on this rig, the proxy therefore diverged from the
///         authored sword on five counts: solver iterations 16/24 against the sword's 25/15, linear
///         damping 0.000 against 0.050, <c>useGravity</c> true while all twenty ragdoll bone bodies are
///         false, and centre of mass and inertia tensor recomputed AUTOMATICALLY instead of using the
///         frozen values <c>SwordBladeSetup</c> deliberately set.
///     </para>
///     <para>
///         The mass-properties case is the one that matters most. <c>SwordBladeSetup</c> freezes centre of
///         mass and inertia specifically to exclude the marker spheres; the proxy discards that and derives
///         both from its own collider VOLUME, which put the balance point 131 mm further along the blade.
///         Nothing in Ragdoll Animator can prevent this: <c>DoNotChangeInertiaTensor</c> protects the HAND
///         BONE's tensor, not the proxy's, and only when <c>Mass == 0</c>, whereas these swords are
///         1.582 kg and 1.630 kg.
///     </para>
///     <para>
///         <b>Why copy rather than declare new numbers.</b> Every value here is taken FROM THE AUTHORED
///         SWORD, whose values are already declared per sword. Nothing invents a figure, so aligning the
///         proxy cannot smuggle in an undeclared setting — it makes the simulating body run the
///         configuration the declaration already describes.
///     </para>
///     <para>
///         <b>Why a separate component.</b> <see cref="RqRagdollAttachableBinding" /> already polls for the
///         proxy, but it requires a <c>BladeShell</c> and so exists only on the OurSolution swords. This
///         divergence affects every sword at every station, so it needs a component that does not depend on
///         the treatment. It is added alongside, never replacing anything, so no existing serialized state
///         is disturbed.
///     </para>
///     <para>
///         <b>Polling, for the same reason the sibling does it.</b> Ragdoll Animator raises no public
///         signal when a sword is worn or removed, so the generated root is watched in
///         <c>LateUpdate</c> — the same point <see cref="RqRagdollAttachableBinding" /> rebinds at.
///     </para>
///     <para>
///         This writes rigidbody values on the generated proxy and nothing else. It applies no force, moves
///         nothing, touches no collider, and never modifies the authored sword.
///     </para>
/// </remarks>
[DisallowMultipleComponent]
public sealed class RqProxyPhysicsDeclaration : MonoBehaviour
{
    [Tooltip("The attachable whose generated proxy is corrected. Left empty, the one on this object is used.")]
    [SerializeField] private RA2AttachableObject attachable;

    [Tooltip("The authored sword rigidbody the proxy should match. Left empty, the one on this object is " +
             "used. Every value applied is READ FROM THIS BODY, so nothing here introduces a new number.")]
    [SerializeField] private Rigidbody authored;

    [Header("What to align — each is a declared choice")]
    [Tooltip("Copy solverIterations and solverVelocityIterations from the authored sword. OFF leaves the " +
             "proxy on the project default, which is the current, measured behaviour.")]
    [SerializeField] private bool alignSolverIterations = true;

    [Tooltip("Copy linearDamping and angularDamping from the authored sword.")]
    [SerializeField] private bool alignDamping = true;

    [Tooltip("Copy centreOfMass, inertiaTensor and inertiaTensorRotation from the authored sword, which " +
             "turns OFF the proxy's automatic recomputation. This is the frozen distribution " +
             "SwordBladeSetup authored, excluding the markers.")]
    [SerializeField] private bool alignMassProperties = true;

    [Tooltip("Set useGravity on the proxy. All twenty ragdoll bone bodies run false because the handler " +
             "declares NoGravityOnStanding, while the proxy defaults to true — the only gravity-loaded " +
             "body in the ragdoll, at 15.5 N against a measured contact force of about 13.6 N. Turn this " +
             "OFF if gravity is being handled elsewhere.")]
    [SerializeField] private bool alignGravity = true;

    [Tooltip("Value written when Align Gravity is on.")]
    [SerializeField] private bool proxyUseGravity;

    [Tooltip("Re-apply if something later changes a value back. Counts how often that happens, which is " +
             "the evidence for whether anything is fighting these writes.")]
    [SerializeField] private bool reassertIfChanged = true;

    [Tooltip("Log once per bind what was changed, old value to new. Provenance for a measurement run.")]
    [SerializeField] private bool logOnBind = true;

    private GameObject boundTo;
    private int reassertions;

    /// <summary>The generated physics root currently corrected, or null while the sword is not worn.</summary>
    public GameObject BoundTo => boundTo;

    /// <summary>How many times a value had to be re-applied after something else changed it.</summary>
    public int Reassertions => reassertions;

    private void Awake()
    {
        if (attachable == null) attachable = GetComponent<RA2AttachableObject>();
        if (authored == null) authored = GetComponent<Rigidbody>();

        if (attachable == null)
            Debug.LogWarning("[ProxyPhysics] " + name + ": no RA2AttachableObject, so no proxy will ever " +
                             "be corrected. This sword's generated body keeps Unity/project defaults.", this);
        if (authored == null)
            Debug.LogWarning("[ProxyPhysics] " + name + ": no authored Rigidbody to copy from, so there is " +
                             "nothing to align to.", this);
    }

    private void LateUpdate()
    {
        if (attachable == null || authored == null) return;

        GameObject generated = attachable.GeneratedPhysicsObject;
        if (generated == null) { boundTo = null; return; }

        Rigidbody proxy = attachable.lastRigidbody;
        if (proxy == null) proxy = generated.GetComponent<Rigidbody>();
        if (proxy == null) return;

        bool firstBind = generated != boundTo;
        if (firstBind) { boundTo = generated; Apply(proxy, true); return; }
        if (reassertIfChanged) Apply(proxy, false);
    }

    /// <summary>
    ///     Writes the authored values onto the proxy. On the first bind it reports what changed; afterwards
    ///     it only counts corrections, so a fight shows up as a rising <see cref="Reassertions" /> count
    ///     rather than as log spam.
    /// </summary>
    private void Apply(Rigidbody proxy, bool announce)
    {
        var log = announce && logOnBind ? new System.Text.StringBuilder() : null;
        int changed = 0;

        if (alignSolverIterations)
        {
            if (proxy.solverIterations != authored.solverIterations)
            {
                if (log != null) log.Append("  solverIterations " + proxy.solverIterations + " -> " + authored.solverIterations);
                proxy.solverIterations = authored.solverIterations; changed++;
            }
            if (proxy.solverVelocityIterations != authored.solverVelocityIterations)
            {
                if (log != null) log.Append("  solverVelocityIterations " + proxy.solverVelocityIterations + " -> " + authored.solverVelocityIterations);
                proxy.solverVelocityIterations = authored.solverVelocityIterations; changed++;
            }
        }

        if (alignDamping)
        {
            if (!Mathf.Approximately(proxy.linearDamping, authored.linearDamping))
            {
                if (log != null) log.Append("  linearDamping " + proxy.linearDamping.ToString("F4") + " -> " + authored.linearDamping.ToString("F4"));
                proxy.linearDamping = authored.linearDamping; changed++;
            }
            if (!Mathf.Approximately(proxy.angularDamping, authored.angularDamping))
            {
                if (log != null) log.Append("  angularDamping " + proxy.angularDamping.ToString("F4") + " -> " + authored.angularDamping.ToString("F4"));
                proxy.angularDamping = authored.angularDamping; changed++;
            }
        }

        if (alignGravity && proxy.useGravity != proxyUseGravity)
        {
            if (log != null) log.Append("  useGravity " + proxy.useGravity + " -> " + proxyUseGravity);
            proxy.useGravity = proxyUseGravity; changed++;
        }

        if (alignMassProperties)
        {
            // Assigning either of these switches the proxy's automatic recomputation off, which is the
            // point: the authored distribution is frozen deliberately and must not be re-derived from
            // collider volume. A non-positive inertia tensor would throw, so it is guarded.
            Vector3 tensor = authored.inertiaTensor;
            if (tensor.x > 0f && tensor.y > 0f && tensor.z > 0f)
            {
                if (proxy.centerOfMass != authored.centerOfMass)
                {
                    if (log != null) log.Append("  centerOfMass " + proxy.centerOfMass.ToString("F5") + " -> " + authored.centerOfMass.ToString("F5"));
                    proxy.centerOfMass = authored.centerOfMass; changed++;
                }
                if (proxy.inertiaTensor != tensor)
                {
                    if (log != null) log.Append("  inertiaTensor " + proxy.inertiaTensor.ToString("F5") + " -> " + tensor.ToString("F5"));
                    proxy.inertiaTensor = tensor; changed++;
                }
                if (proxy.inertiaTensorRotation != authored.inertiaTensorRotation)
                {
                    proxy.inertiaTensorRotation = authored.inertiaTensorRotation; changed++;
                }
            }
            else if (log != null) log.Append("  mass properties SKIPPED (authored inertia tensor not strictly positive)");
        }

        if (!announce) { reassertions += changed; return; }

        if (log != null)
            Debug.Log("[ProxyPhysics] " + name + ": aligned generated proxy to the authored sword." +
                      (changed == 0 ? "  nothing needed changing." : log.ToString()), this);
    }
}
