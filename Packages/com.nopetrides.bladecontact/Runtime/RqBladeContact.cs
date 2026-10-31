using System.Collections.Generic;
using BladeContact;
using UnityEngine;

/// <summary>
///     Marks one sword's blade as eligible for semantic per-contact tangential behaviour (L2).
/// </summary>
/// <remarks>
///     <para>
///         Eligibility is a property of the SWORD, declared where the sword is. Nothing here knows about
///         bays, stations, test pairs or which swords are expected to meet: a contact is handled when
///         BOTH colliders in it belong to registered blades, whoever they are. That is what lets the same
///         component serve a servo-driven station sword, an XRI-grabbable sword, a freestanding target,
///         and ordinary sword-vs-sword play without configuration.
///     </para>
///     <para>
///         PhysX is NOT suppressed. The blade colliders collide normally; the solver adds a tangential
///         term on top of whatever PhysX already does. Guard, grip, pommel, world and any contact where
///         one side is unregistered stay entirely with ordinary PhysX.
///     </para>
///     <para>
///         Collider <see cref="PhysicsMaterial"/> values are never written by this system. A sword can be
///         touching several things at once, and a collider-wide material cannot express one behaviour per
///         contact; writing it would silently change every other contact that collider is in.
///     </para>
///     <para>
///         <b>The blade is not always where it was authored.</b> An attachment system can re-host a
///         sword's physics on a rigid copy, leaving the authored collider disabled and the authored body
///         kinematic. Everything here therefore goes through <see cref="BladeShell.ActiveColliders"/>,
///         <see cref="BladeShell.ActiveBody"/> and <see cref="BladeShell.ActiveTransform"/>, and the
///         registration is rebuilt whenever that binding changes. Holding the authored collider instead
///         fails silently: contacts arrive on shapes nothing recognises, no pair is ever formed, and the
///         blade behaves as ordinary PhysX with no error anywhere.
///     </para>
/// </remarks>
[DisallowMultipleComponent]
[RequireComponent(typeof(BladeShell))]
public sealed class RqBladeContact : MonoBehaviour
{
    /// <summary>Every registered blade collider in the scene, so a contact can be resolved both ways.</summary>
    private static readonly Dictionary<Collider, RqBladeContact> ByCollider =
        new Dictionary<Collider, RqBladeContact>();

    [Tooltip("Solver to report contacts to. Left empty, the first one in the scene is used.")]
    [SerializeField] private RqBladeTangentialSolver solver;

    [Tooltip("Optional label shown in readouts. Purely cosmetic; behaviour never depends on it.")]
    [SerializeField] private string displayName;

    [Tooltip("Further colliders that are also THIS BLADE, when a blade is represented by more than one " +
             "shape. Contacts on any of them are matched to this blade, and to this blade's authored " +
             "shell. Guard, grip and pommel colliders must never be listed here.")]
    [SerializeField] private Collider[] additionalColliders = new Collider[0];

    private BladeShell shell;
    private Rigidbody body;
    private Vector3 localLongAxis = Vector3.right;

    /// <summary>Colliders this blade is currently registered under, so they can be unregistered exactly.</summary>
    private readonly List<Collider> registered = new List<Collider>();

    /// <summary>Callback forwarder living on the proxy body, when one is carrying this blade.</summary>
    private BladeCollisionRelay relay;

    private bool live;

    /// <summary>The authored shell this blade contacts with.</summary>
    public BladeShell Shell => shell != null ? shell : shell = GetComponent<BladeShell>();

    /// <summary>Built shell geometry: surfaces, edge lines and their authored feature types.</summary>
    public BladeShellData Data => Shell.Data;

    /// <summary>The authored collider that counts as "this blade". Nothing else on the sword qualifies.</summary>
    public Collider BladeCollider => Shell.BladeCollider;

    /// <summary>
    ///     True when a collider is one of the shapes representing this blade.
    /// </summary>
    /// <remarks>
    ///     A blade is usually one collider, but it need not be: the QuickDirty comparator represents the
    ///     same blade with the cooked hull plus two derived hulls, all live at once, and an attachment
    ///     system can replace all of them with rigid copies. Which shape PhysX happened to generate the
    ///     contact on does not change which BLADE it is, nor which authored shell answers for it.
    /// </remarks>
    public bool Owns(Collider collider)
    {
        if (collider == null) return false;

        for (int i = 0; i < registered.Count; i++)
            if (registered[i] == collider) return true;

        return false;
    }

    /// <summary>The dynamic body tangential force is applied to.</summary>
    /// <remarks>
    ///     The body currently CARRYING the blade, which is not always the one the shell was authored on.
    ///     Applying force to a body an attachment system has made kinematic does nothing, and does it
    ///     without complaint.
    /// </remarks>
    public Rigidbody Body => Shell.ActiveBody;

    /// <summary>The transform the authored local geometry is currently expressed against.</summary>
    public Transform ActiveTransform => Shell.ActiveTransform;

    /// <summary>Current world pose of the authored geometry.</summary>
    public BladePose SimulatedPlacement => Shell.SimulatedPlacement;

    public string Label => string.IsNullOrEmpty(displayName) ? name : displayName;

    /// <summary>The solver this blade reports to, resolved at enable. Null means ordinary PhysX only.</summary>
    public RqBladeTangentialSolver Solver => solver;

    /// <summary>
    ///     The blade's own long axis in shell-local space, taken from the authored profile's sweep axis.
    /// </summary>
    /// <remarks>
    ///     Used only to report contact drift ALONG each blade, which is the blade-local statement of the
    ///     bind: a bound contact stays put on both blades, a slipping one travels along them. A world-space
    ///     displacement cannot distinguish the two.
    /// </remarks>
    public Vector3 LocalLongAxis => localLongAxis;

    /// <summary>Resolves the blade behind a collider, or false when that collider is not a registered blade.</summary>
    public static bool TryResolve(Collider collider, out RqBladeContact blade) =>
        ByCollider.TryGetValue(collider, out blade) && blade != null;

    private void OnEnable()
    {
        localLongAxis = ResolveLongAxis();

        Shell.PhysicsBindingChanged += OnPhysicsBindingChanged;
        live = true;
        Rebind();

        if (solver == null) solver = FindFirstObjectByType<RqBladeTangentialSolver>();
        if (solver == null)
            Debug.LogWarning(
                "[L2] " + name + ": no RqBladeTangentialSolver in the scene, so this blade behaves as " +
                "ordinary PhysX.", this);
    }

    private void OnDisable()
    {
        live = false;
        Shell.PhysicsBindingChanged -= OnPhysicsBindingChanged;

        Unregister();
        DetachRelay();

        if (solver != null) solver.Forget(this);
    }

    private void OnPhysicsBindingChanged(BladeShell changed)
    {
        if (live) Rebind();
    }

    /// <summary>
    ///     Points the collider map and the callback path at whatever is carrying this blade right now.
    /// </summary>
    private void Rebind()
    {
        Unregister();
        DetachRelay();

        body = Shell.ActiveBody;

        IReadOnlyList<Collider> active = Shell.ActiveColliders;
        for (int i = 0; i < active.Count; i++) Register(active[i]);

        // Extra authored shapes belong to the authored blade only. Under a proxy the proxy's own list is
        // the complete answer, and an authored extra would name a collider that is switched off.
        if (!Shell.HasPhysicsProxy)
            for (int i = 0; i < additionalColliders.Length; i++) Register(additionalColliders[i]);

        if (registered.Count == 0)
        {
            Debug.LogWarning(
                "[L2] " + name + ": no live collider represents this blade, so it cannot be matched to a " +
                "PhysX contact. It stays entirely with ordinary PhysX.", this);
            return;
        }

        if (body == null)
        {
            Debug.LogWarning("[L2] " + name + ": no Rigidbody, so no tangential force can be applied.", this);
            return;
        }

        // Callbacks are delivered to the body's own GameObject. When that is a proxy, this component is
        // not on it and would never hear about the contact.
        if (body.gameObject != gameObject) AttachRelay(body.gameObject);
    }

    private void Register(Collider collider)
    {
        if (collider == null) return;
        ByCollider[collider] = this;
        registered.Add(collider);
    }

    private void Unregister()
    {
        for (int i = 0; i < registered.Count; i++)
        {
            Collider collider = registered[i];
            RqBladeContact owner;
            if (collider != null && ByCollider.TryGetValue(collider, out owner) && owner == this)
                ByCollider.Remove(collider);
        }

        registered.Clear();
    }

    private void AttachRelay(GameObject host)
    {
        relay = host.GetComponent<BladeCollisionRelay>();
        if (relay == null) relay = host.AddComponent<BladeCollisionRelay>();
        relay.Collided += OnRelayCollision;
    }

    private void DetachRelay()
    {
        if (relay != null) relay.Collided -= OnRelayCollision;
        relay = null;
    }

    private Vector3 ResolveLongAxis()
    {
        BladeProfileAsset asset = Shell.ProfileAsset;
        if (asset == null || asset.Profile == null || asset.Profile.Sections == null ||
            asset.Profile.Sections.Length == 0)
            return Vector3.right;

        switch (asset.Profile.Sections[0].Axis)
        {
            case BladeSweepAxis.Y: return Vector3.up;
            case BladeSweepAxis.Z: return Vector3.forward;
            default: return Vector3.right;
        }
    }

    private void OnCollisionEnter(Collision collision) => Report(collision);
    private void OnCollisionStay(Collision collision) => Report(collision);
    private void OnRelayCollision(Collision collision) => Report(collision);

    private void Report(Collision collision)
    {
        if (solver == null || registered.Count == 0 || Body == null) return;
        solver.ReportCollision(this, collision);
    }
}
