using System;
using UnityEngine;

namespace BladeContact
{
    /// <summary>Which authored list a contact feature came from.</summary>
    public enum BladeFeatureKind : byte
    {
        Surface,
        Edge
    }

    /// <summary>Identifies one authored feature of a shell.</summary>
    public readonly struct BladeFeatureRef
    {
        public readonly BladeFeatureKind Kind;
        public readonly int Index;

        public BladeFeatureRef(BladeFeatureKind kind, int index)
        {
            Kind = kind;
            Index = index;
        }

        public static BladeFeatureRef None => new BladeFeatureRef(BladeFeatureKind.Surface, -1);

        public bool IsValid => Index >= 0;
    }

    /// <summary>
    /// A contiguous block of features sharing one bounding sphere, used to reject most of the shell
    /// before any exact triangle work. Groups follow the authoring: one per swept station interval, one
    /// per cap. Without them a full shell pair costs tens of thousands of triangle tests per iterate.
    /// </summary>
    public readonly struct BladeShellGroup
    {
        public readonly int SurfaceStart;
        public readonly int SurfaceCount;
        public readonly int EdgeStart;
        public readonly int EdgeCount;
        public readonly Vector3 LocalCentre;
        public readonly float LocalRadius;

        public BladeShellGroup(
            int surfaceStart, int surfaceCount, int edgeStart, int edgeCount,
            Vector3 localCentre, float localRadius)
        {
            SurfaceStart = surfaceStart;
            SurfaceCount = surfaceCount;
            EdgeStart = edgeStart;
            EdgeCount = edgeCount;
            LocalCentre = localCentre;
            LocalRadius = localRadius;
        }
    }

    /// <summary>
    /// Plain-C# snapshot of one shell's authored geometry: real surface pieces and the edge lines where
    /// they meet. Free of <see cref="MonoBehaviour"/> and <see cref="Rigidbody"/> so the solver can be
    /// exercised deterministically in edit-mode tests.
    /// </summary>
    public sealed class BladeShellData
    {
        private readonly BladeSurface[] surfaces;
        private readonly BladeEdgeLine[] edges;
        private readonly Vector3[] surfaceCentres;
        private readonly float[] surfaceRadii;
        private readonly Vector3[] edgeCentres;
        private readonly float[] edgeRadii;
        private readonly BladeShellGroup[] groups;

        public BladeShellData(BladeSurface[] surfaces, BladeEdgeLine[] edges)
            : this(surfaces, edges, null) { }

        public BladeShellData(BladeSurface[] surfaces, BladeEdgeLine[] edges, BladeShellGroup[] groups)
        {
            this.surfaces = surfaces ?? throw new ArgumentNullException(nameof(surfaces));
            this.edges = edges ?? throw new ArgumentNullException(nameof(edges));
            this.groups = groups ?? BuildSingleGroup(surfaces, edges);

            surfaceCentres = new Vector3[surfaces.Length];
            surfaceRadii = new float[surfaces.Length];
            edgeCentres = new Vector3[edges.Length];
            edgeRadii = new float[edges.Length];

            float extent = 0f;

            for (int i = 0; i < surfaces.Length; i++)
            {
                surfaceCentres[i] = surfaces[i].LocalCentre;
                surfaceRadii[i] = surfaces[i].LocalBoundingRadius;
                extent = Mathf.Max(extent, surfaces[i].LocalExtent);
            }

            for (int i = 0; i < edges.Length; i++)
            {
                edgeCentres[i] = edges[i].LocalCentre;
                edgeRadii[i] = edges[i].LocalBoundingRadius;
                extent = Mathf.Max(extent, edges[i].LocalExtent);
            }

            LocalExtent = extent;

            // Shell-level bounding sphere, for the pair-level broad phase.
            Vector3 centre = Vector3.zero;
            int total = surfaces.Length + edges.Length;
            foreach (BladeSurface s in surfaces) centre += s.LocalCentre;
            foreach (BladeEdgeLine e in edges) centre += e.LocalCentre;
            if (total > 0) centre /= total;

            float bound = 0f;
            foreach (BladeSurface s in surfaces)
                bound = Mathf.Max(bound, (s.LocalCentre - centre).magnitude + s.LocalBoundingRadius);
            foreach (BladeEdgeLine e in edges)
                bound = Mathf.Max(bound, (e.LocalCentre - centre).magnitude + e.LocalBoundingRadius);

            BoundingCentre = centre;
            BoundingRadius = bound;
        }

        private BladeShellBvh bvh;

        /// <summary>Acceleration structure over the authored features, built once on first use.</summary>
        public BladeShellBvh Bvh => bvh ?? (bvh = new BladeShellBvh(this));

        /// <summary>Centre of the shell's bounding sphere, in local space.</summary>
        public Vector3 BoundingCentre { get; }

        /// <summary>Radius of the shell's bounding sphere about <see cref="BoundingCentre"/>.</summary>
        public float BoundingRadius { get; }

        /// <summary>Bounds every feature in one group, so a group pair can be rejected as a unit.</summary>
        private static BladeShellGroup[] BuildSingleGroup(BladeSurface[] surfaces, BladeEdgeLine[] edges)
        {
            Vector3 centre = Vector3.zero;
            int count = 0;
            foreach (BladeSurface s in surfaces) { centre += s.LocalCentre; count++; }
            foreach (BladeEdgeLine e in edges) { centre += e.LocalCentre; count++; }
            if (count > 0) centre /= count;

            float radius = 0f;
            foreach (BladeSurface s in surfaces)
                radius = Mathf.Max(radius, (s.LocalCentre - centre).magnitude + s.LocalBoundingRadius);
            foreach (BladeEdgeLine e in edges)
                radius = Mathf.Max(radius, (e.LocalCentre - centre).magnitude + e.LocalBoundingRadius);

            return new[] { new BladeShellGroup(0, surfaces.Length, 0, edges.Length, centre, radius) };
        }

        public int SurfaceCount => surfaces.Length;
        public int EdgeCount => edges.Length;
        public int GroupCount => groups.Length;

        public BladeShellGroup GetGroup(int index) => groups[index];

        public BladeSurface GetSurface(int index) => surfaces[index];
        public BladeEdgeLine GetEdge(int index) => edges[index];

        /// <summary>
        /// Largest distance from the shell origin reached by any authored feature. Multiplied by angular
        /// speed, this bounds the linear speed of any shell point during conservative advancement.
        /// </summary>
        public float LocalExtent { get; }

        internal Vector3 SurfaceCentre(int index) => surfaceCentres[index];
        internal float SurfaceRadius(int index) => surfaceRadii[index];
        internal Vector3 EdgeCentre(int index) => edgeCentres[index];
        internal float EdgeRadius(int index) => edgeRadii[index];

        /// <summary>The authored type of any feature, whichever list it came from.</summary>
        public BladeFeatureType TypeOf(BladeFeatureRef feature)
        {
            if (!feature.IsValid) return BladeFeatureType.Unresolved;
            return feature.Kind == BladeFeatureKind.Surface
                ? surfaces[feature.Index].Type
                : edges[feature.Index].Type;
        }

        /// <summary>The authored identifier of any feature, for contact traces.</summary>
        public string IdOf(BladeFeatureRef feature)
        {
            if (!feature.IsValid) return "(none)";
            return feature.Kind == BladeFeatureKind.Surface
                ? surfaces[feature.Index].Id
                : edges[feature.Index].Id;
        }

        /// <summary>
        ///     The authored RING an id belongs to — <c>e6</c>, <c>f5</c> — from an id <see cref="IdOf" />
        ///     produced, e.g. <c>SW_A1_blade.s64.e6</c>.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Not the id's tail: a facet id carries a trailing <c>.a</c> / <c>.b</c>, so the last
        ///         segment is not the ring. This walks back to the last segment that is a letter
        ///         <c>e</c> or <c>f</c> followed only by digits.
        ///     </para>
        ///     <para>
        ///         <b>Public here so there is ONE parser.</b> Two independent ring readers already exist in
        ///         this project and they disagree with each other on the same raw id — the reason
        ///         <c>pauseOnFirstSharpAttribution</c> does not fire. Every new consumer calls this rather
        ///         than writing a third. This is the recorder's implementation unchanged, so nothing it
        ///         has already written moves.
        ///     </para>
        /// </remarks>
        public static string RingOf(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;

            string[] parts = id.Split('.');
            for (int i = parts.Length - 1; i >= 0; i--)
            {
                string p = parts[i];
                if (p.Length < 2) continue;
                if (p[0] != 'e' && p[0] != 'f') continue;

                bool digits = true;
                for (int c = 1; c < p.Length; c++)
                    if (p[c] < '0' || p[c] > '9') { digits = false; break; }

                if (digits) return p;
            }

            return parts[parts.Length - 1];
        }
    }
}
