using System;
using System.Collections.Generic;
using UnityEngine;

namespace BladeContact
{
    /// <summary>
    /// Registers authored blade-contact geometry on a dynamic sword. The profile is supplied by the
    /// consumer; the package contains no specimen dimensions and no study-declared sharpness criterion.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BladeShell : MonoBehaviour
    {
        [Tooltip("The dynamic body this shell belongs to. The solver returns impulses to it; it never " +
                 "takes pose authority over it.")]
        [SerializeField] private Rigidbody body;

        [Tooltip("Authored cross-section geometry for this specimen, in this transform's local space.")]
        [SerializeField] private BladeProfileAsset profileAsset;

        [Tooltip("The PhysX collider representing THIS BLADE ONLY. When two shells are registered as a " +
                 "pair, the manager suppresses PhysX collision between exactly these two colliders, so " +
                 "one owner reports blade-vs-blade. Guard, grip, pommel and world collision are untouched.")]
        [SerializeField] private Collider bladeCollider;

        private BladeShellData cachedData;

        private Rigidbody proxyBody;
        private Transform proxyTransform;
        private readonly List<Collider> proxyColliders = new List<Collider>();
        private bool proxyBound;

        /// <summary>
        /// Raised whenever the shapes and body that carry this blade in the simulation change.
        /// </summary>
        /// <remarks>
        /// Anything that keeps a collider-to-blade map has to rebuild it here. A map built at enable is
        /// stale the moment the blade is picked up by something that re-hosts its physics.
        /// </remarks>
        public event Action<BladeShell> PhysicsBindingChanged;

        /// <summary>The dynamic body receiving custom sword-to-sword response.</summary>
        /// <remarks>
        /// The AUTHORED body. When something else is currently hosting this blade's physics, the body that
        /// actually carries it is <see cref="ActiveBody"/>, and that is the one a response must be applied
        /// to. Applying to a body that has been made kinematic does nothing at all, silently.
        /// </remarks>
        public Rigidbody Body => body;

        /// <summary>
        /// The body that carries this blade in the simulation right now.
        /// </summary>
        public Rigidbody ActiveBody => proxyBound && proxyBody != null ? proxyBody : body;

        /// <summary>
        /// The transform the authored local geometry is currently expressed against.
        /// </summary>
        /// <remarks>
        /// A proxy is a rigid copy of this blade's collider hierarchy, so the same authored local geometry
        /// is valid against it. Using the authored transform while the proxy is what PhysX moves would put
        /// the shell query one host behind the contact it is meant to describe.
        /// </remarks>
        public Transform ActiveTransform => proxyBound && proxyTransform != null ? proxyTransform : transform;

        /// <summary>True while another host is carrying this blade's physics.</summary>
        public bool HasPhysicsProxy => proxyBound;

        /// <summary>
        /// Every collider that counts as THIS BLADE right now, authored or proxied.
        /// </summary>
        public IReadOnlyList<Collider> ActiveColliders
        {
            get
            {
                if (proxyBound) return proxyColliders;

                singleCollider.Clear();
                if (bladeCollider != null) singleCollider.Add(bladeCollider);
                return singleCollider;
            }
        }

        private readonly List<Collider> singleCollider = new List<Collider>(1);

        /// <summary>Built surfaces and edge lines for this shell, constructed once and reused.</summary>
        public BladeShellData Data =>
            cachedData ??= profileAsset != null
                ? BladeShellBuilder.Build(profileAsset.Profile)
                : new BladeShellData(new BladeSurface[0], new BladeEdgeLine[0]);

        /// <summary>
        /// The blade's own PhysX collider, the single relationship handed over to the custom solver.
        /// </summary>
        /// <remarks>
        /// Named explicitly rather than discovered by convention. Suppressing the wrong collider would
        /// silently drop guard or grip collision, which is a change to the physical model rather than to
        /// who owns blade contact, and it would not be obvious from watching the scene.
        /// </remarks>
        public Collider BladeCollider => bladeCollider;

        /// <summary>Where a <see cref="BladePose"/> was taken from, so a measurement can say so.</summary>
        public enum PlacementSourceKind
        {
            /// <summary>From the simulating Rigidbody. The pose PhysX solved. What everything must use.</summary>
            PhysicsBody,

            /// <summary>
            ///     From the Transform, because no Rigidbody governs it. Visual only when interpolation is on.
            /// </summary>
            Transform
        }

        /// <summary>
        ///     Current world pose of this shell's local geometry, taken from the SIMULATING BODY.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         <b>Rigidbody, not Transform, and the distinction is not cosmetic.</b> A simulating body on
        ///         <c>RigidbodyInterpolation.Interpolate</c> writes a Transform that is deliberately NOT the
        ///         pose PhysX solved — it is a smoothed pose for rendering, produced between fixed steps.
        ///         Posing the shell query from it asks the authored geometry a question about where the blade
        ///         is being drawn rather than where it was simulated.
        ///     </para>
        ///     <para>
        ///         Measured on this rig, 2026-08-23, at the step a contact pair first appeared: the two poses
        ///         were 1.5–1.7 mm apart, and the SAME closest-feature query returned 12.96 mm from the
        ///         Transform pose against 7.90 mm from the body pose — a 39 % difference on an identical
        ///         feature pair, at the sub-millimetre separations where a cutting edge and the profile edge
        ///         beside it are being told apart.
        ///     </para>
        ///     <para>
        ///         <b>Why this shell exists at all.</b> PhysX collides a cooked convex hull inflated by
        ///         <c>contactOffset</c>; the authored shell is here to recover the precision that costs. That
        ///         only works if the shell is evaluated at the pose the simulation actually used. There is no
        ///         skin, margin or speculative shell in this geometry and none is ever to be added.
        ///     </para>
        ///     <para>
        ///         Falls back to the Transform only when no Rigidbody governs this blade — at edit time, or
        ///         for an unsimulated prop. <see cref="PlacementSource"/> reports which was used, so a
        ///         recorder can log it rather than assume it.
        ///     </para>
        /// </remarks>
        public BladePose SimulatedPlacement
        {
            get
            {
                Rigidbody rb = ActiveBody;
                if (rb != null) return new BladePose(rb.position, rb.rotation);

                Transform t = ActiveTransform;
                return new BladePose(t.position, t.rotation);
            }
        }

        /// <summary>Which source <see cref="SimulatedPlacement"/> just used. For verification, never for logic.</summary>
        public PlacementSourceKind PlacementSource => ActiveBody != null ? PlacementSourceKind.PhysicsBody : PlacementSourceKind.Transform;

        /// <summary>
        ///     The Transform pose — the RENDERED pose, not the simulated one.
        /// </summary>
        /// <remarks>
        ///     Exposed for verification and for drawing only, so a diagnostic can show the gap between what
        ///     is drawn and what was simulated. Nothing that produces a measurement may use it. Under
        ///     interpolation it lags the body by design; that lag is the thing being measured, not an error
        ///     to be corrected here.
        /// </remarks>
        public BladePose RenderedPlacement
        {
            get
            {
                Transform t = ActiveTransform;
                return new BladePose(t.position, t.rotation);
            }
        }

        /// <summary>
        ///     How far the rendered pose currently sits from the simulated one, metres. Verification only.
        /// </summary>
        public float RenderLagMetres
        {
            get
            {
                Rigidbody rb = ActiveBody;
                if (rb == null) return 0f;
                return Vector3.Distance(rb.position, ActiveTransform.position);
            }
        }

        /// <summary>
        ///     Angle between the rendered pose and the simulated one, degrees. Verification only.
        /// </summary>
        /// <remarks>
        ///     Computed from the quaternion dot product rather than <c>Quaternion.Angle</c>, which returned a
        ///     flat 0 on this rig for rotations whose Euler angles differed by more than a tenth of a degree.
        /// </remarks>
        public float RenderLagDegrees
        {
            get
            {
                Rigidbody rb = ActiveBody;
                if (rb == null) return 0f;
                float dot = Mathf.Abs(Quaternion.Dot(rb.rotation, ActiveTransform.rotation));
                return 2f * Mathf.Acos(Mathf.Min(1f, dot)) * Mathf.Rad2Deg;
            }
        }

        /// <summary>
        /// Declares the body and shapes that are carrying this blade's physics instead of the authored
        /// ones, for as long as that is true.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A blade does not always collide through the collider it was authored with. Attachment systems
        /// commonly make the source object kinematic, switch its colliders off, and rebuild an equivalent
        /// rigid copy on the body that is going to carry it. Contacts then arrive on shapes this component
        /// has never heard of, and every consumer that resolves a contact back to a blade silently stops
        /// matching. Nothing errors; the blade simply behaves as ordinary physics.
        /// </para>
        /// <para>
        /// This is deliberately a declaration made from OUTSIDE. The package cannot know which attachment
        /// system a project uses, and guessing — by name, by mesh, by proximity — would be a rule that is
        /// wrong somewhere. The consumer that owns the attachment knows the answer exactly.
        /// </para>
        /// <para>
        /// Only shapes that are this BLADE belong here. A guard, grip or pommel copy passed in would make
        /// a hilt contact classify as a blade contact.
        /// </para>
        /// </remarks>
        public void BindPhysicsProxy(Rigidbody proxy, Transform pose, IReadOnlyList<Collider> colliders)
        {
            proxyBody = proxy;
            proxyTransform = pose != null ? pose : (proxy != null ? proxy.transform : null);

            proxyColliders.Clear();
            if (colliders != null)
                for (int i = 0; i < colliders.Count; i++)
                    if (colliders[i] != null) proxyColliders.Add(colliders[i]);

            proxyBound = proxyColliders.Count > 0 || proxyBody != null;

            Action<BladeShell> changed = PhysicsBindingChanged;
            if (changed != null) changed(this);
        }

        /// <summary>Returns this blade to its authored body and collider.</summary>
        public void UnbindPhysicsProxy()
        {
            if (!proxyBound) return;

            proxyBound = false;
            proxyBody = null;
            proxyTransform = null;
            proxyColliders.Clear();

            Action<BladeShell> changed = PhysicsBindingChanged;
            if (changed != null) changed(this);
        }

        /// <summary>Reusable world-space buffers, so a per-step sweep does not allocate.</summary>
        public BladeShellScratch Scratch { get; } = new BladeShellScratch();

        /// <summary>Discards the built shell after the authored profile changes.</summary>
        public void InvalidateData() => cachedData = null;

        /// <summary>
        /// Authoring entry point for consumer tooling that generates prefabs.
        /// </summary>
        /// <remarks>
        /// A profile is specimen-specific: it carries that specimen's own edge basis and feature
        /// identities, so passing another specimen's profile is a modelling error, not a shortcut.
        /// </remarks>
        public void SetAuthoring(BladeProfileAsset profile, Rigidbody dynamicBody, Collider blade = null)
        {
            profileAsset = profile;
            body = dynamicBody;
            if (blade != null) bladeCollider = blade;
            cachedData = null;
        }

        /// <summary>The profile currently driving this shell, or null when none has been assigned.</summary>
        public BladeProfileAsset ProfileAsset => profileAsset;

        private void Reset()
        {
            body = GetComponentInParent<Rigidbody>();
        }

        private void OnValidate()
        {
            if (body == null)
                body = GetComponentInParent<Rigidbody>();

            cachedData = null;
        }
    }
}
