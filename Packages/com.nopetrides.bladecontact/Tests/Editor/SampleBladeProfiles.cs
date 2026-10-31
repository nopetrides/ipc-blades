using UnityEngine;

namespace BladeContact.Tests
{
    /// <summary>
    /// Stripped specimen geometry used only to exercise the package in isolation.
    /// </summary>
    /// <remarks>
    /// These are the authored cross-sections of a real double-edged training sword blade, copied as plain
    /// numbers so the package's tests never reference a consuming project's prefabs, stations, or
    /// experiment assets. They are sample data, not a declared study parameter: nothing in the solver
    /// reads a constant from here, and the consuming project supplies its own profile and its own
    /// sharpness criterion.
    ///
    /// Local frame: X runs along the blade (negative, away from the guard), Z across its width, Y through
    /// its thickness. The ring is authored clockwise in the (Across, Through) plane, starting at the
    /// negative-Z sharp edge and running over the upper faces to the positive-Z sharp edge.
    /// </remarks>
    public static class SampleBladeProfiles
    {
        public const float MidThickness = 0.002533f;

        private static BladeProfileVertex Edge(float across, float through) =>
            new BladeProfileVertex(across, through, BladeFeatureType.SharpEdge, BladeFeatureType.BevelFace);

        private static BladeProfileVertex Bevel(float across, float through) =>
            new BladeProfileVertex(across, through, BladeFeatureType.ProfileFeatureEdge, BladeFeatureType.BroadFace);

        private static BladeProfileVertex Broad(float across, float through) =>
            new BladeProfileVertex(across, through, BladeFeatureType.ProfileFeatureEdge, BladeFeatureType.BroadFace);

        private static BladeProfileVertex BroadToBevel(float across, float through) =>
            new BladeProfileVertex(across, through, BladeFeatureType.ProfileFeatureEdge, BladeFeatureType.BevelFace);

        /// <summary>One 12-vertex cross-section ring. Vertex 0 and 6 are the two designated sharp edges.</summary>
        private static BladeProfileStation Station(
            float along,
            float halfWidth, float bevelAcross, float bevelUpper, float bevelLower,
            float shoulderAcross, float shoulderUpper, float shoulderLower,
            float centreUpper, float centreLower)
        {
            return new BladeProfileStation(along, new[]
            {
                Edge(-halfWidth, MidThickness),
                Bevel(-bevelAcross, bevelUpper),
                Broad(-shoulderAcross, shoulderUpper),
                Broad(0f, centreUpper),
                Broad(shoulderAcross, shoulderUpper),
                BroadToBevel(bevelAcross, bevelUpper),
                Edge(halfWidth, MidThickness),
                Bevel(bevelAcross, bevelLower),
                Broad(shoulderAcross, shoulderLower),
                Broad(0f, centreLower),
                Broad(-shoulderAcross, shoulderLower),
                BroadToBevel(-bevelAcross, bevelLower)
            });
        }

        /// <summary>
        /// The blade section: five authored stations from the guard to the tip. Pass zero spacing to
        /// build only the authored stations, without tessellation.
        /// </summary>
        public static BladeProfile Blade(float maxStationSpacing = 0.05f)
        {
            var section = new BladeProfileSection
            {
                Id = "blade",
                Axis = BladeSweepAxis.X,
                CapStart = true,
                StartCapType = BladeFeatureType.Unresolved,
                CapEnd = true,
                EndCapType = BladeFeatureType.Tip,
                MaxStationSpacing = maxStationSpacing,
                Stations = new[]
                {
                    Station(-0.010693f, 0.022500f, 0.019783f, 0.003136f, 0.001931f,
                        0.005777f, 0.005033f, 0.000033f, 0.004633f, 0.000433f),
                    Station(-0.324693f, 0.017430f, 0.014696f, 0.003140f, 0.001927f,
                        0.006624f, 0.004706f, 0.000360f, 0.004706f, 0.000360f),
                    Station(-0.391693f, 0.016348f, 0.013610f, 0.003140f, 0.001926f,
                        0.006805f, 0.003888f, 0.001178f, 0.004636f, 0.000430f),
                    Station(-0.970693f, 0.007000f, 0.005828f, 0.002793f, 0.002273f,
                        0.002914f, 0.003413f, 0.001653f, 0.004033f, 0.001033f),
                    Station(-1.026693f, 0.005000f, 0.004163f, 0.002719f, 0.002348f,
                        0.002081f, 0.003126f, 0.001941f, 0.003533f, 0.001533f)
                }
            };

            return new BladeProfile(section);
        }

        /// <summary>Ring indices of the two designated sharp edges.</summary>
        public const int NegativeEdgeIndex = 0;
        public const int PositiveEdgeIndex = 6;
    }
}
