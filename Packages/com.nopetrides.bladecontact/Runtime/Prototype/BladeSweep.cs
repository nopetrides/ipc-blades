using UnityEngine;

namespace BladeContact.Prototype
{
    /// <summary>
    /// Continuous first-contact search between two registered blade shells, by conservative advancement.
    /// </summary>
    /// <remarks>
    /// Unity's sweep-based continuous collision detection cannot perform angular sweeps, so a shell that
    /// rotates through another between two simulation steps can exchange sides while both endpoint poses
    /// are separated. Conservative advancement is angular-capable: at each iterate it measures the true
    /// surface separation and advances time by only what an upper bound on the closure rate permits,
    /// so no contact earlier than the returned time of impact can exist along the interpolated path.
    ///
    /// The closure bound for the requested motion is
    /// <c>|dPositionA - dPositionB| + angleA * extentA + angleB * extentB</c>,
    /// which bounds the rate of change of the distance between any spine point of A and any of B.
    /// It is deliberately loose rather than tight: looseness costs iterations, never correctness.
    ///
    /// This type reads poses and returns a time of impact. It never writes a pose back onto a body;
    /// registered swords remain dynamic Rigidbodies owned by Unity.
    /// </remarks>
    public static class BladeSweep
    {
        /// <summary>
        /// Finds the earliest contact between shells A and B along the requested motion from their start
        /// poses to their end poses.
        /// </summary>
        public static BladeSweepResult FindFirstContact(
            BladeShellData shellA, in BladePose startA, in BladePose endA,
            BladeShellData shellB, in BladePose startB, in BladePose endB,
            in BladeSweepSettings settings)
        {
            float angleA = BladePose.AngleRadians(startA, endA);
            float angleB = BladePose.AngleRadians(startB, endB);
            Vector3 relativeTranslation = (endA.Position - startA.Position) - (endB.Position - startB.Position);

            float closureBound =
                relativeTranslation.magnitude +
                angleA * shellA.LocalExtent +
                angleB * shellB.LocalExtent;

            float time = 0f;
            float separation = float.MaxValue;

            for (int iteration = 0; iteration < settings.MaxIterations; iteration++)
            {
                BladePose poseA = BladePose.Interpolate(startA, endA, time);
                BladePose poseB = BladePose.Interpolate(startB, endB, time);

                separation = ClosestFeaturePair(
                    shellA, poseA, shellB, poseB,
                    out int featureA, out int featureB,
                    out Vector3 witnessA, out Vector3 witnessB);

                if (closureBound <= settings.MinimumClosureRate)
                    return BladeSweepResult.NoContact(separation);

                // The shells cannot close more than closureBound per unit time, so advancing by the
                // separation divided by that bound can never step over a contact.
                float advance = (separation - settings.ContactMargin) / closureBound;

                if (separation <= settings.ContactMargin || advance < settings.MinimumTimeAdvance)
                {
                    Vector3 delta = witnessB - witnessA;
                    Vector3 normal = delta.sqrMagnitude > 1e-18f ? delta.normalized : Vector3.zero;
                    return new BladeSweepResult(
                        BladeSweepStatus.Contact, time,
                        featureA, featureB, witnessA, witnessB, normal, separation);
                }

                time += advance;

                if (time >= 1f)
                    return BladeSweepResult.NoContact(separation);
            }

            // Did not converge. Block the motion at the last proven-safe time rather than accept a
            // step that could cross.
            return new BladeSweepResult(
                BladeSweepStatus.IterationLimit, time, -1, -1, Vector3.zero, Vector3.zero, Vector3.zero, separation);
        }

        /// <summary>
        /// Smallest surface separation between any feature of A and any feature of B at the given poses.
        /// Negative results indicate interpenetration.
        /// </summary>
        public static float ClosestFeaturePair(
            BladeShellData shellA, in BladePose poseA,
            BladeShellData shellB, in BladePose poseB,
            out int featureIndexA, out int featureIndexB,
            out Vector3 witnessA, out Vector3 witnessB)
        {
            float best = float.MaxValue;
            featureIndexA = -1;
            featureIndexB = -1;
            witnessA = Vector3.zero;
            witnessB = Vector3.zero;

            for (int i = 0; i < shellA.FeatureCount; i++)
            {
                BladeFeature a = shellA.GetFeature(i);
                Vector3 a0 = poseA.TransformPoint(a.LocalStart);
                Vector3 a1 = poseA.TransformPoint(a.LocalEnd);

                for (int j = 0; j < shellB.FeatureCount; j++)
                {
                    BladeFeature b = shellB.GetFeature(j);
                    Vector3 b0 = poseB.TransformPoint(b.LocalStart);
                    Vector3 b1 = poseB.TransformPoint(b.LocalEnd);

                    float spineDistance = SegmentGeometry.ClosestPointsBetweenSegments(
                        a0, a1, b0, b1, out Vector3 wa, out Vector3 wb);

                    float surfaceSeparation = spineDistance - a.Radius - b.Radius;
                    if (surfaceSeparation >= best)
                        continue;

                    best = surfaceSeparation;
                    featureIndexA = i;
                    featureIndexB = j;
                    witnessA = wa;
                    witnessB = wb;
                }
            }

            return best;
        }
    }
}
