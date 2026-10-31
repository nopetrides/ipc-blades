using System;
using UnityEngine;

namespace BladeContact
{
    /// <summary>
    /// One authored surface piece of a blade shell: a triangle carrying a semantic identity.
    /// </summary>
    /// <remarks>
    /// Surfaces are stored as triangles rather than quads or polygons because a triangle is always
    /// planar and always convex. A quad spanning two cross-section stations of a tapering blade is
    /// neither, and the distance queries would silently lose accuracy on exactly the thin, angled
    /// geometry this system exists to handle.
    ///
    /// A surface has real area and no inflation radius. Blade thickness comes from the authored
    /// positions of the opposing faces, which converge to zero at a designated sharp edge; it is never
    /// produced by sweeping a radius around a centre line.
    /// </remarks>
    [Serializable]
    public struct BladeSurface
    {
        [SerializeField] private string id;
        [SerializeField] private BladeFeatureType type;
        [SerializeField] private Vector3 localA;
        [SerializeField] private Vector3 localB;
        [SerializeField] private Vector3 localC;

        public BladeSurface(string id, BladeFeatureType type, Vector3 localA, Vector3 localB, Vector3 localC)
        {
            this.id = id;
            this.type = type;
            this.localA = localA;
            this.localB = localB;
            this.localC = localC;
        }

        public string Id => id;

        /// <summary>Authored semantic identity. Never inferred from the geometry.</summary>
        public BladeFeatureType Type => type;

        public Vector3 LocalA => localA;
        public Vector3 LocalB => localB;
        public Vector3 LocalC => localC;

        public Vector3 LocalCentre => (localA + localB + localC) / 3f;

        /// <summary>
        /// Unit outward normal of this triangle, normalised by hand.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Deliberately does NOT use <see cref="Vector3.normalized"/>. Unity returns
        /// <see cref="Vector3.zero"/> from that property whenever the vector's magnitude is below 1e-5,
        /// and blade facets near the tip are small enough to fall under it: on SW-A1, the facet
        /// <c>s99.f0.b</c> has a cross-product magnitude of 9.607e-06 and normalises to exactly zero.
        /// </para>
        /// <para>
        /// That silent zero is dangerous rather than merely wrong. Any code measuring an angle between two
        /// face normals gets 0 degrees against a zero vector, which reads as a 180 degree dihedral — a
        /// perfectly flat surface — for what is in fact a 25 degree cutting edge. That produced a false
        /// report that SW-A1's last ~33 mm of edge was coplanar tip geometry; it is not, and the wedge
        /// holds at 25.00-25.02 degrees over the full 206-segment run.
        /// </para>
        /// <para>
        /// The solver itself was never affected: <see cref="TriangleGeometry"/> keeps cross products
        /// unnormalised and scales by their squared magnitude against a 1e-12 epsilon. This property
        /// exists so that DIAGNOSTIC and authoring code has a safe normal to reach for.
        /// </para>
        /// </remarks>
        public Vector3 LocalNormal
        {
            get
            {
                Vector3 cross = Vector3.Cross(localB - localA, localC - localA);
                float magnitude = cross.magnitude;
                return magnitude > 0f ? cross / magnitude : Vector3.zero;
            }
        }

        /// <summary>Twice the triangle's area. Zero only for a genuinely degenerate triangle.</summary>
        public float LocalDoubleArea => Vector3.Cross(localB - localA, localC - localA).magnitude;

        /// <summary>Radius of a bounding sphere about <see cref="LocalCentre"/>, for broadphase rejection.</summary>
        public float LocalBoundingRadius
        {
            get
            {
                Vector3 centre = LocalCentre;
                return Mathf.Sqrt(Mathf.Max(
                    (localA - centre).sqrMagnitude,
                    Mathf.Max((localB - centre).sqrMagnitude, (localC - centre).sqrMagnitude)));
            }
        }

        /// <summary>Largest distance from the shell origin reached by this surface.</summary>
        public float LocalExtent => Mathf.Sqrt(Mathf.Max(
            localA.sqrMagnitude, Mathf.Max(localB.sqrMagnitude, localC.sqrMagnitude)));
    }
}
