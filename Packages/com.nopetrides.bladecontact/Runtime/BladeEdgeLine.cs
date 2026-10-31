using System;
using UnityEngine;

namespace BladeContact
{
    /// <summary>
    /// An authored line where two surface pieces of a blade shell meet.
    /// </summary>
    /// <remarks>
    /// An edge line is a real intersection of two authored surfaces, not a rounded rod. It carries no
    /// radius. <see cref="IncludedAngleDegrees"/> is measured from the two adjoining surfaces and is
    /// reported, never thresholded here: whether an edge counts as sharp is a study-declared rule the
    /// consumer supplies, applied on top of the authored <see cref="Type"/>. A geometrically acute
    /// corner on an undesignated feature is still undesignated.
    /// </remarks>
    [Serializable]
    public struct BladeEdgeLine
    {
        [SerializeField] private string id;
        [SerializeField] private BladeFeatureType type;
        [SerializeField] private Vector3 localStart;
        [SerializeField] private Vector3 localEnd;
        [SerializeField] private int surfaceIndexA;
        [SerializeField] private int surfaceIndexB;
        [SerializeField] private float includedAngleDegrees;

        public BladeEdgeLine(
            string id, BladeFeatureType type, Vector3 localStart, Vector3 localEnd,
            int surfaceIndexA, int surfaceIndexB, float includedAngleDegrees)
        {
            this.id = id;
            this.type = type;
            this.localStart = localStart;
            this.localEnd = localEnd;
            this.surfaceIndexA = surfaceIndexA;
            this.surfaceIndexB = surfaceIndexB;
            this.includedAngleDegrees = includedAngleDegrees;
        }

        public string Id => id;

        /// <summary>Authored semantic identity. Bind eligibility starts here, not at the angle.</summary>
        public BladeFeatureType Type => type;

        public Vector3 LocalStart => localStart;
        public Vector3 LocalEnd => localEnd;

        /// <summary>The two surfaces meeting at this line, or -1 where an adjoining surface is absent.</summary>
        public int SurfaceIndexA => surfaceIndexA;
        public int SurfaceIndexB => surfaceIndexB;

        /// <summary>
        /// Angle between the two adjoining surfaces, in degrees. Measured from authored geometry and
        /// reported for the consumer's declared sharpness rule to read. Not a threshold.
        /// </summary>
        public float IncludedAngleDegrees => includedAngleDegrees;

        public Vector3 LocalCentre => (localStart + localEnd) * 0.5f;

        public float LocalBoundingRadius => (localEnd - localStart).magnitude * 0.5f;

        public float LocalExtent => Mathf.Sqrt(Mathf.Max(localStart.sqrMagnitude, localEnd.sqrMagnitude));
    }
}
