using NUnit.Framework;
using UnityEngine;

namespace BladeContact.Tests
{
    /// <summary>
    /// Authoring gate: the built shell must reproduce the real blade's shape and carry the authored
    /// identities, because everything downstream classifies from those and not from the geometry.
    /// </summary>
    public sealed class BladeShellBuilderTests
    {
        private static BladeShellData Blade() => BladeShellBuilder.Build(SampleBladeProfiles.Blade());

        [Test]
        public void BuildsSurfacesAndEdgesForEveryAuthoredFacet()
        {
            BladeShellData shell = BladeShellBuilder.Build(SampleBladeProfiles.Blade(0f));

            // 4 station intervals x 12 facets x 2 triangles, plus two ear-clipped 12-gon caps.
            Assert.AreEqual(4 * 12 * 2 + 2 * 10, shell.SurfaceCount);
            Assert.AreEqual(4 * 12, shell.EdgeCount);
        }

        [Test]
        public void TessellationRefinesTheShellWithoutMovingItsSurface()
        {
            BladeShellData coarse = BladeShellBuilder.Build(SampleBladeProfiles.Blade(0f));
            BladeShellData fine = BladeShellBuilder.Build(SampleBladeProfiles.Blade(0.05f));

            Assert.Greater(fine.SurfaceCount, coarse.SurfaceCount, "tessellation must actually subdivide");

            // Same query, both shells: an inserted station is a linear interpolation of the authored
            // ones and lies exactly on the same surface, so the measured separation must not move.
            var poseA = new BladePose(new Vector3(0.5f, 0f, 0f), Quaternion.AngleAxis(-90f, Vector3.forward));
            var poseB = new BladePose(new Vector3(0f, 0.5f, 0f), Quaternion.AngleAxis(200f, Vector3.up));

            float coarseSeparation = BladeShellSweep.ClosestFeaturePair(coarse, poseA, coarse, poseB).Separation;
            float fineSeparation = BladeShellSweep.ClosestFeaturePair(fine, poseA, fine, poseB).Separation;

            Assert.That(fineSeparation, Is.EqualTo(coarseSeparation).Within(1e-5f));
        }

        [Test]
        public void BladeThicknessComesFromOpposingFacesNotFromARadius()
        {
            BladeShellData shell = Blade();

            float baseThickness = 0f;
            float tipThickness = 0f;
            for (int i = 0; i < shell.SurfaceCount; i++)
            {
                BladeSurface s = shell.GetSurface(i);
                foreach (Vector3 v in new[] { s.LocalA, s.LocalB, s.LocalC })
                {
                    float fromMid = Mathf.Abs(v.y - SampleBladeProfiles.MidThickness);
                    if (Mathf.Abs(v.x - (-0.010693f)) < 1e-4f) baseThickness = Mathf.Max(baseThickness, fromMid);
                    if (Mathf.Abs(v.x - (-1.026693f)) < 1e-4f) tipThickness = Mathf.Max(tipThickness, fromMid);
                }
            }

            // 5.00 mm at the guard tapering to 2.00 mm at the point.
            Assert.That(baseThickness * 2f, Is.EqualTo(0.0050f).Within(0.0001f));
            Assert.That(tipThickness * 2f, Is.EqualTo(0.0020f).Within(0.0001f));
        }

        [Test]
        public void DesignatedSharpEdgesReportTheAuthoredIncludedAngle()
        {
            BladeProfile profile = SampleBladeProfiles.Blade();
            BladeProfileStation station = profile.Sections[0].Stations[0];

            float negative = BladeShellBuilder.IncludedAngleDegrees(station.Ring, SampleBladeProfiles.NegativeEdgeIndex);
            float positive = BladeShellBuilder.IncludedAngleDegrees(station.Ring, SampleBladeProfiles.PositiveEdgeIndex);

            // The specimen is ground to a constant included angle at both edges.
            Assert.That(negative, Is.EqualTo(25.0f).Within(0.5f));
            Assert.That(positive, Is.EqualTo(25.0f).Within(0.5f));
        }

        [Test]
        public void TheIncludedAngleIsConstantAlongTheWholeBlade()
        {
            BladeProfileSection section = SampleBladeProfiles.Blade().Sections[0];

            foreach (BladeProfileStation station in section.Stations)
            {
                float angle = BladeShellBuilder.IncludedAngleDegrees(station.Ring, SampleBladeProfiles.NegativeEdgeIndex);
                Assert.That(angle, Is.EqualTo(25.0f).Within(0.5f),
                    $"station at x={station.Along} reported {angle} degrees");
            }
        }

        [Test]
        public void BroadFaceRidgesAreFarBlunterThanTheSharpEdges()
        {
            BladeProfileStation station = SampleBladeProfiles.Blade().Sections[0].Stations[0];

            // The shoulder where the bevel meets the broad face is a real ridge, but it is not an edge.
            float shoulder = BladeShellBuilder.IncludedAngleDegrees(station.Ring, 1);
            Assert.Greater(shoulder, 150f, "the bevel/broad-face shoulder must not read as acute");
        }

        [Test]
        public void EdgeIdentityIsAuthoredNotInferredFromTheAngle()
        {
            BladeShellData shell = BladeShellBuilder.Build(SampleBladeProfiles.Blade(0f));

            int sharp = 0;
            int blunt = 0;
            for (int i = 0; i < shell.EdgeCount; i++)
            {
                BladeFeatureType type = shell.GetEdge(i).Type;
                if (type == BladeFeatureType.SharpEdge) sharp++;
                if (type == BladeFeatureType.ProfileFeatureEdge) blunt++;
            }

            // Two designated edges per station interval; the other ten ridges stay undesignated.
            Assert.AreEqual(4 * 2, sharp);
            Assert.AreEqual(4 * 10, blunt);
        }

        [Test]
        public void ASquareCornerReportsNinetyDegreesAndIsNotDesignatedSharp()
        {
            // A box-like ring: geometrically a corner, authored as undesignated.
            var ring = new[]
            {
                new BladeProfileVertex(-0.01f, -0.01f, BladeFeatureType.Unresolved, BladeFeatureType.BroadFace),
                new BladeProfileVertex(-0.01f, 0.01f, BladeFeatureType.Unresolved, BladeFeatureType.BroadFace),
                new BladeProfileVertex(0.01f, 0.01f, BladeFeatureType.Unresolved, BladeFeatureType.BroadFace),
                new BladeProfileVertex(0.01f, -0.01f, BladeFeatureType.Unresolved, BladeFeatureType.BroadFace)
            };

            for (int i = 0; i < ring.Length; i++)
                Assert.That(BladeShellBuilder.IncludedAngleDegrees(ring, i), Is.EqualTo(90f).Within(0.01f));
        }

        [Test]
        public void AReflexCornerReportsMoreThanOneEightyRatherThanReadingAsAcute()
        {
            // An L-shaped footprint of the kind a crossguard has. The concave corner must not be
            // mistaken for a sharp edge by a declared angle rule.
            var ring = new[]
            {
                new BladeProfileVertex(0f, 0f, BladeFeatureType.Unresolved, BladeFeatureType.BroadFace),
                new BladeProfileVertex(0f, 0.03f, BladeFeatureType.Unresolved, BladeFeatureType.BroadFace),
                new BladeProfileVertex(0.01f, 0.03f, BladeFeatureType.Unresolved, BladeFeatureType.BroadFace),
                new BladeProfileVertex(0.01f, 0.01f, BladeFeatureType.Unresolved, BladeFeatureType.BroadFace),
                new BladeProfileVertex(0.03f, 0.01f, BladeFeatureType.Unresolved, BladeFeatureType.BroadFace),
                new BladeProfileVertex(0.03f, 0f, BladeFeatureType.Unresolved, BladeFeatureType.BroadFace)
            };

            Assert.That(BladeShellBuilder.IncludedAngleDegrees(ring, 3), Is.EqualTo(270f).Within(0.01f));
        }
    }
}
