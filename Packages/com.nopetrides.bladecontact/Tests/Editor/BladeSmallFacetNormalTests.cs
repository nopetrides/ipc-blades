using NUnit.Framework;
using UnityEngine;

namespace BladeContact.Tests
{
    /// <summary>
    /// Regression: a small facet near the tip must never report as a flat surface.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The bug this pins down.</b> <see cref="Vector3.normalized"/> returns
    /// <see cref="Vector3.zero"/> whenever the vector's magnitude is below 1e-5. A blade tapers, so its
    /// facets shrink toward the tip, and their cross products drop under that threshold while the geometry
    /// remains perfectly valid. Diagnostic code that normalised face normals then measured 0 degrees
    /// between a real normal and a zero vector, and reported the resulting 180 degree "dihedral" as
    /// coplanar tip geometry.
    /// </para>
    /// <para>
    /// On SW-A1 that produced a false claim that the final ~33 mm of cutting edge had gone flat and should
    /// be demoted from SharpEdge. The edge is a consistent 25 degree wedge over its whole length; nothing
    /// needed demoting. The failure mode is silent, produces a plausible-looking number, and points the
    /// wrong way — toward removing designation from real geometry — so it is worth a test of its own.
    /// </para>
    /// <para>
    /// The solver is not implicated: <see cref="TriangleGeometry"/> never normalises, keeping cross
    /// products raw and scaling by squared magnitude against a 1e-12 epsilon.
    /// </para>
    /// </remarks>
    public sealed class BladeSmallFacetNormalTests
    {
        private static BladeShellData Shell() => BladeShellBuilder.Build(SampleBladeProfiles.Blade(0.01f));

        [Test]
        public void TaperedBladeActuallyProducesFacetsBelowUnitysNormalizeEpsilon()
        {
            // Without such facets the rest of these tests would pass vacuously.
            BladeShellData shell = Shell();

            int belowEpsilon = 0;
            for (int i = 0; i < shell.SurfaceCount; i++)
                if (shell.GetSurface(i).LocalDoubleArea < 1e-5f)
                    belowEpsilon++;

            Assert.Greater(belowEpsilon, 0,
                "this blade has no facets under Unity's 1e-5 normalize epsilon, so the regression it " +
                "guards against cannot be exercised; pick a finer tessellation or a sharper taper");
        }

        [Test]
        public void SmallFacetsStillYieldUnitNormals()
        {
            BladeShellData shell = Shell();

            for (int i = 0; i < shell.SurfaceCount; i++)
            {
                BladeSurface surface = shell.GetSurface(i);
                if (surface.LocalDoubleArea <= 0f) continue;

                Assert.AreEqual(1f, surface.LocalNormal.magnitude, 1e-3f,
                    $"surface {surface.Id} has non-zero area but did not produce a unit normal");
            }
        }

        [Test]
        public void UnityNormalizedIsTheOneThatFails_SoTheSafeAccessorIsNotRedundant()
        {
            BladeShellData shell = Shell();

            int unityZeroed = 0;
            for (int i = 0; i < shell.SurfaceCount; i++)
            {
                BladeSurface surface = shell.GetSurface(i);
                if (surface.LocalDoubleArea <= 0f) continue;

                Vector3 raw = Vector3.Cross(surface.LocalB - surface.LocalA, surface.LocalC - surface.LocalA);
                if (raw.normalized == Vector3.zero) unityZeroed++;
            }

            Assert.Greater(unityZeroed, 0,
                "Vector3.normalized no longer zeroes any facet on this blade; if Unity changed that " +
                "behaviour the accessor is still correct, but this regression's premise needs revisiting");
        }

        [Test]
        public void NearTipCuttingEdgeKeepsTheSameWedgeAsMidBlade()
        {
            // The actual claim: the wedge does not open out toward the tip. Compared against mid-blade
            // rather than a hard-coded angle, so this stays true if the specimen's geometry is re-authored.
            BladeShellData shell = Shell();

            float tipMost = 0f;
            float guardMost = 0f;
            for (int i = 0; i < shell.EdgeCount; i++)
            {
                float along = shell.GetEdge(i).LocalCentre.x;
                if (along < tipMost) tipMost = along;
                if (along > guardMost) guardMost = along;
            }

            float span = guardMost - tipMost;
            float midBlade = MedianWedge(shell, tipMost + span * 0.4f, tipMost + span * 0.6f);
            float nearTip = MedianWedge(shell, tipMost, tipMost + span * 0.05f);

            Assert.Greater(midBlade, 0f, "no mid-blade cutting edge sampled");
            Assert.Greater(nearTip, 0f, "no near-tip cutting edge sampled");

            Assert.AreEqual(midBlade, nearTip, 2f,
                $"near-tip wedge ({nearTip:F2} deg) differs from mid-blade ({midBlade:F2} deg); a value " +
                "near 180 here is the normalization regression, not flattened geometry");

            Assert.Less(nearTip, 90f,
                $"near-tip cutting edge reports {nearTip:F2} deg, i.e. effectively flat");
        }

        /// <summary>Median wedge angle of the designated cutting edges whose centre lies in a span.</summary>
        private static float MedianWedge(BladeShellData shell, float fromAlong, float toAlong)
        {
            var angles = new System.Collections.Generic.List<float>();

            for (int i = 0; i < shell.EdgeCount; i++)
            {
                BladeEdgeLine edge = shell.GetEdge(i);
                if (edge.Type != BladeFeatureType.SharpEdge) continue;

                float along = edge.LocalCentre.x;
                if (along < fromAlong || along > toAlong) continue;

                var normals = new System.Collections.Generic.List<Vector3>();
                for (int s = 0; s < shell.SurfaceCount; s++)
                {
                    BladeSurface surface = shell.GetSurface(s);
                    int shared = 0;
                    if ((surface.LocalA - edge.LocalStart).sqrMagnitude < 1e-12f
                        || (surface.LocalA - edge.LocalEnd).sqrMagnitude < 1e-12f) shared++;
                    if ((surface.LocalB - edge.LocalStart).sqrMagnitude < 1e-12f
                        || (surface.LocalB - edge.LocalEnd).sqrMagnitude < 1e-12f) shared++;
                    if ((surface.LocalC - edge.LocalStart).sqrMagnitude < 1e-12f
                        || (surface.LocalC - edge.LocalEnd).sqrMagnitude < 1e-12f) shared++;
                    if (shared < 2) continue;

                    Vector3 n = surface.LocalNormal;
                    if (n != Vector3.zero) normals.Add(n);
                }

                if (normals.Count < 2) continue;

                float dihedral = 180f;
                for (int a = 0; a < normals.Count; a++)
                for (int b = a + 1; b < normals.Count; b++)
                    dihedral = Mathf.Min(dihedral, 180f - Vector3.Angle(normals[a], normals[b]));

                angles.Add(dihedral);
            }

            if (angles.Count == 0) return -1f;
            angles.Sort();
            return angles[angles.Count / 2];
        }
    }
}
