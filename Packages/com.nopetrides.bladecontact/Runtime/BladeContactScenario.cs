using UnityEngine;

namespace BladeContact
{
    /// <summary>
    /// What a contacting feature MEANS for the study, as distinct from what it is geometrically.
    /// </summary>
    /// <remarks>
    /// Two layers, deliberately kept apart. The RAW layer — <see cref="BladeFeatureType"/> and the feature
    /// ids — stays authoritative for time of impact, distance, collision geometry and the non-crossing
    /// invariant, and is always reported unchanged. This SEMANTIC layer is derived from it and used only
    /// for scenario naming and tangential response. Deriving it never alters the raw witness.
    /// </remarks>
    public enum BladeSemanticRegion
    {
        /// <summary>
        /// One of the two explicitly authored long cutting-edge runs — the consistent ~25 degree convex
        /// wedge. Nothing else qualifies, however creased it happens to be.
        /// </summary>
        CuttingEdge,

        /// <summary>
        /// Every other blade region: broad faces, bevel faces, fuller surfaces, medial and centre ridges,
        /// shoulders, profile seams, and tip-cap geometry.
        /// </summary>
        NonCuttingRegion
    }

    /// <summary>How a contact is classified for the station cases.</summary>
    public enum BladeContactScenario
    {
        /// <summary>Both participants are authored cutting edges.</summary>
        EdgeEdge,

        /// <summary>
        /// Neither participant is an authored cutting edge. The station-facing shorthand for
        /// NonCuttingRegion against NonCuttingRegion.
        /// </summary>
        /// <remarks>
        /// "Flat" is shorthand, not a geometric claim. It does NOT require mathematically planar faces: a
        /// fuller, a medial ridge, a bevel or a profile seam all belong here, because what defines the case
        /// is that no authored cutting edge is taking part.
        /// </remarks>
        FlatFlat,

        /// <summary>One participant is an authored cutting edge, the other is not.</summary>
        EdgeFlat
    }

    /// <summary>Maps raw authored feature types onto study semantics, and names the scenario.</summary>
    /// <remarks>
    /// <para>
    /// The mapping is by authored DESIGNATION alone, never by geometry measured at runtime. That is the
    /// whole point of separating the layers: a medial ridge can be a genuine crease and still not be a
    /// cutting edge, and a runtime angle test would silently relabel geometry the author never designated.
    /// </para>
    /// <para>
    /// Measured on SW-A1 for scale: the authored cutting edges hold a ~25.00 degree wedge along their
    /// length, the centreline ridge down each broad face sits at a median 166.23 degrees — about 14 degrees
    /// off flat — and the remaining profile seams at a median 176.82 degrees. The separation between a real
    /// edge and everything else is wide, but it is the designation that is authoritative here.
    /// </para>
    /// </remarks>
    public static class BladeContactScenarios
    {
        /// <summary>
        /// The semantic region a raw authored feature type belongs to.
        /// </summary>
        /// <remarks>
        /// Only <see cref="BladeFeatureType.SharpEdge"/> maps to <see cref="BladeSemanticRegion.CuttingEdge"/>.
        /// <see cref="BladeFeatureType.ProfileFeatureEdge"/> is a non-designated line of the authored
        /// profile — an internal seam, typically bounded by broad or bevel faces — and belongs to the
        /// non-cutting region regardless of how sharp its crease happens to be.
        /// <see cref="BladeFeatureType.Tip"/> is likewise non-cutting.
        /// </remarks>
        public static BladeSemanticRegion RegionOf(BladeFeatureType type) =>
            type == BladeFeatureType.SharpEdge
                ? BladeSemanticRegion.CuttingEdge
                : BladeSemanticRegion.NonCuttingRegion;

        /// <summary>True when this feature is an authored cutting edge.</summary>
        public static bool IsCuttingEdge(BladeFeatureType type) =>
            RegionOf(type) == BladeSemanticRegion.CuttingEdge;

        /// <summary>Classifies a contact from the two participants' semantic regions.</summary>
        public static BladeContactScenario Classify(BladeFeatureType typeA, BladeFeatureType typeB) =>
            Classify(RegionOf(typeA), RegionOf(typeB));

        /// <summary>Classifies a contact from two already-resolved semantic regions.</summary>
        public static BladeContactScenario Classify(BladeSemanticRegion a, BladeSemanticRegion b)
        {
            bool cuttingA = a == BladeSemanticRegion.CuttingEdge;
            bool cuttingB = b == BladeSemanticRegion.CuttingEdge;

            if (cuttingA && cuttingB) return BladeContactScenario.EdgeEdge;
            if (!cuttingA && !cuttingB) return BladeContactScenario.FlatFlat;
            return BladeContactScenario.EdgeFlat;
        }

        /// <summary>
        /// Raw and semantic together, as a single trace fragment, so neither can be read without the other.
        /// </summary>
        public static string Describe(
            BladeFeatureType typeA, string idA, BladeFeatureType typeB, string idB)
        {
            BladeSemanticRegion regionA = RegionOf(typeA);
            BladeSemanticRegion regionB = RegionOf(typeB);

            return $"[{Classify(regionA, regionB)}] {regionA} x {regionB}  " +
                   $"raw: {typeA}({idA}) x {typeB}({idB})";
        }
    }
}
