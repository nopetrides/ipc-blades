using UnityEngine;

namespace BladeContact
{
    /// <summary>Project asset wrapper so a consumer can author and version a profile outside code.</summary>
    /// <remarks>
    /// A profile is specimen-specific. It carries that specimen's own edge basis and its own feature
    /// identities, so one specimen's profile must never be assigned to another.
    /// </remarks>
    [CreateAssetMenu(menuName = "Blade Contact/Blade Profile", fileName = "BladeProfile")]
    public sealed class BladeProfileAsset : ScriptableObject
    {
        [SerializeField] private BladeProfile profile = new BladeProfile();

        [Header("Authored basis (consumer's convention, expressed in shell-local space)")]
        [Tooltip("Direction along the blade, from guard toward tip.")]
        [SerializeField] private Vector3 bladeAxis = new Vector3(-1f, 0f, 0f);

        [Tooltip("Signed direction across the blade's width, toward the +edge. The opposite edge is -this.")]
        [SerializeField] private Vector3 edgeAxis = new Vector3(0f, 0f, 1f);

        [Tooltip("Normal of the blade's flat, completing the basis.")]
        [SerializeField] private Vector3 faceNormal = new Vector3(0f, 1f, 0f);

        [Tooltip("Where this basis and these features came from, and what is provisional about them.")]
        [TextArea(4, 20)]
        [SerializeField] private string provenance = string.Empty;

        [Header("Origin shape — what this profile was baked FROM")]
        [Tooltip("The mesh asset this profile was authored against. Recorded at bake time so a consumer " +
                 "can prove the collider it is classifying is cut from the same source. This is an " +
                 "IDENTITY check and needs no tolerance: either the collider's mesh is this one, or the " +
                 "profile belongs to a different object.")]
        [SerializeField] private Mesh sourceMesh;

        [Tooltip("The source mesh's asset name, kept as text as well as a reference. A runtime-generated " +
                 "mesh — ProBuilder baking on load, for instance — is a DIFFERENT Mesh instance from the " +
                 "asset even though it was built from it, so an instance comparison alone would report a " +
                 "false mismatch. The name survives that.")]
        [SerializeField] private string sourceMeshName = string.Empty;

        [Tooltip("The source mesh asset's GUID at bake time. Survives renaming, which the name does not. " +
                 "Written by the authoring tool; blank on profiles baked before this was recorded.")]
        [SerializeField] private string sourceMeshGuid = string.Empty;

        public BladeProfile Profile => profile;

        /// <summary>Direction along the blade, guard toward tip, in shell-local space.</summary>
        public Vector3 BladeAxis => bladeAxis;

        /// <summary>
        /// Signed across-width direction toward the +edge. The package never infers this; it is supplied so a
        /// consumer's own edge convention and the solver's geometry cannot silently disagree in orientation.
        /// </summary>
        public Vector3 EdgeAxis => edgeAxis;

        public Vector3 FaceNormal => faceNormal;

        /// <summary>Human-readable record of where the basis and feature identities came from.</summary>
        public string Provenance => provenance;

        /// <summary>The mesh asset this profile was baked from, or null on a profile baked before this existed.</summary>
        public Mesh SourceMesh => sourceMesh;

        /// <summary>Name of that mesh asset, which survives runtime re-baking into a new Mesh instance.</summary>
        public string SourceMeshName => sourceMeshName;

        /// <summary>GUID of that mesh asset, which survives renaming.</summary>
        public string SourceMeshGuid => sourceMeshGuid;

        /// <summary>True when this profile records what it was baked from at all.</summary>
        public bool HasOriginShape => sourceMesh != null || !string.IsNullOrEmpty(sourceMeshName);

        /// <summary>
        ///     Whether a live mesh is the shape this profile was baked from.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Checked three ways, cheapest first, because a runtime-generated mesh is a different
        ///         <c>Mesh</c> instance from the asset it was built from even though it IS that shape.
        ///         ProBuilder baking on load is the case that forced this: instance equality alone would
        ///         report a mismatch for a mesh that is correct.
        ///     </para>
        ///     <para>
        ///         Unity also decorates generated instances — "Blade_A1_Flat (Instance)", "... Instance" —
        ///         so the name comparison is a prefix match on the recorded name rather than equality.
        ///     </para>
        ///     <para>
        ///         Returns false only when the profile HAS an origin recorded and the live mesh is not it.
        ///         A profile with nothing recorded cannot answer, and says so through
        ///         <see cref="HasOriginShape"/> rather than by passing.
        ///     </para>
        /// </remarks>
        public bool MatchesOriginShape(Mesh live)
        {
            if (live == null || !HasOriginShape) return false;
            if (sourceMesh != null && live == sourceMesh) return true;
            if (string.IsNullOrEmpty(sourceMeshName)) return false;

            string n = live.name;
            if (n == sourceMeshName) return true;

            // "<asset> (Instance)" and similar decorations of a runtime copy of the same shape.
            return n.StartsWith(sourceMeshName) &&
                   n.Length > sourceMeshName.Length &&
                   (n[sourceMeshName.Length] == ' ' || n[sourceMeshName.Length] == '(');
        }

        /// <summary>Authoring entry point for consumer-side tooling that generates a profile.</summary>
        public void SetAuthored(
            BladeProfile authoredProfile, Vector3 axis, Vector3 edge, Vector3 face, string provenanceNotes)
        {
            profile = authoredProfile;
            bladeAxis = axis;
            edgeAxis = edge;
            faceNormal = face;
            provenance = provenanceNotes;
        }

        /// <summary>
        ///     Records the shape this profile was baked from. Called by the authoring tool at bake time.
        /// </summary>
        /// <remarks>
        ///     Separate from <see cref="SetAuthored"/> so an existing profile can be given its origin
        ///     without rewriting the geometry it already carries.
        /// </remarks>
        public void SetOriginShape(Mesh mesh, string guid)
        {
            sourceMesh = mesh;
            sourceMeshName = mesh != null ? mesh.name : string.Empty;
            sourceMeshGuid = guid ?? string.Empty;
        }
    }
}
