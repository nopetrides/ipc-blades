using NUnit.Framework;
using UnityEngine;

namespace BladeContact.Tests
{
    /// <summary>
    /// L1 — angular contact between two real authored blade shells.
    /// </summary>
    /// <remarks>
    /// Rig: blade A stands vertically as a post at x = 0.5; blade B pivots about the world Y axis at
    /// (0, 0.5, 0) and sweeps 120 degrees through it. A is thin in X and wide in Z; B is thin in Y and
    /// sweeps horizontally, so the crossing is a real face/edge crossing rather than two coplanar plates.
    /// Both endpoint poses are far clear of one another, so an endpoint-only test misses the crossing.
    /// </remarks>
    public sealed class BladeShellSweepTests
    {
        private const float SweptAngleDegrees = 60f;
        private static readonly Vector3 Pivot = new Vector3(0f, 0.5f, 0f);
        private static readonly Vector3 BladeTipLocal = new Vector3(-1.026693f, 0f, 0f);

        private static BladeShellData Shell() => BladeShellBuilder.Build(SampleBladeProfiles.Blade());

        /// <summary>Stationary blade, stood on end so its length runs up the world Y axis.</summary>
        private static BladePose PostPose() =>
            new BladePose(new Vector3(0.5f, 0f, 0f), Quaternion.AngleAxis(-90f, Vector3.forward));

        /// <summary>Rotating blade: length swings in the horizontal plane about the pivot.</summary>
        private static BladePose SwingPose(float degrees) =>
            new BladePose(Pivot, Quaternion.AngleAxis(180f + degrees, Vector3.up));

        private static Vector3 SwingTip(float degrees) => SwingPose(degrees).TransformPoint(BladeTipLocal);

        private static BladeShellContact Sweep(BladeSweepSettings settings)
        {
            return BladeShellSweep.FindFirstContact(
                Shell(), PostPose(), PostPose(),
                Shell(), SwingPose(-SweptAngleDegrees), SwingPose(SweptAngleDegrees),
                settings);
        }

        private static float SeparationAt(float normalizedTime)
        {
            float degrees = Mathf.Lerp(-SweptAngleDegrees, SweptAngleDegrees, normalizedTime);
            return BladeShellSweep.ClosestFeaturePair(
                Shell(), PostPose(), Shell(), SwingPose(degrees)).Separation;
        }

        [Test]
        public void Rig_EndpointsAreSeparated_SoEndpointTestingAloneWouldMissTheContact()
        {
            Assert.Greater(SeparationAt(0f), 0.3f, "start pose must be clearly separated");
            Assert.Greater(SeparationAt(1f), 0.3f, "end pose must be clearly separated");

            // The distance query saturates at zero for intersecting geometry: it reports separation, not
            // penetration depth. So an overlapping midpoint reads as zero rather than as a negative
            // number, and "in contact" is the strongest statement available here.
            Assert.LessOrEqual(SeparationAt(0.5f), 1e-6f,
                "the requested path must drive the shells into one another");
        }

        [Test]
        public void AngularSweep_FindsFirstContactStrictlyInsideTheRequestedMotion()
        {
            BladeShellContact contact = Sweep(BladeSweepSettings.Default);

            Assert.AreEqual(BladeContactStatus.Contact, contact.Status);
            Assert.Greater(contact.TimeOfImpact, 0f);
            Assert.Less(contact.TimeOfImpact, 1f);
        }

        [Test]
        public void AngularSweep_AcceptedPoseDoesNotPenetrate()
        {
            BladeShellContact contact = Sweep(BladeSweepSettings.Default);

            // Strictly greater than zero, not merely non-negative: because the distance query saturates
            // at zero on intersection, any penetration would read as exactly zero and fail here.
            Assert.Greater(contact.Pair.Separation, 0f, "accepted pose must stay strictly separated");
            Assert.Greater(SeparationAt(contact.TimeOfImpact), 0f);
        }

        [Test]
        public void AngularSweep_ShellsDoNotExchangeSides()
        {
            BladeShellContact contact = Sweep(BladeSweepSettings.Default);
            float acceptedDegrees = Mathf.Lerp(-SweptAngleDegrees, SweptAngleDegrees, contact.TimeOfImpact);

            Assert.Greater(SwingTip(-SweptAngleDegrees).z, 0f);
            Assert.Less(SwingTip(SweptAngleDegrees).z, 0f, "requested motion does cross to the other side");
            Assert.Greater(SwingTip(acceptedDegrees).z, 0f, "accepted motion must stay on the starting side");
        }

        [Test]
        public void AngularSweep_NamesTheAuthoredFeaturesThatContacted()
        {
            BladeShellData shell = Shell();
            BladeShellContact contact = Sweep(BladeSweepSettings.Default);

            Assert.IsTrue(contact.Pair.FeatureA.IsValid);
            Assert.IsTrue(contact.Pair.FeatureB.IsValid);
            Assert.AreNotEqual(BladeFeatureType.Unresolved, shell.TypeOf(contact.Pair.FeatureA));
            Assert.AreNotEqual(BladeFeatureType.Unresolved, shell.TypeOf(contact.Pair.FeatureB));
            Assert.IsNotEmpty(shell.IdOf(contact.Pair.FeatureA));
            Assert.AreNotEqual(Vector3.zero, contact.Pair.Normal);
        }

        [Test]
        public void AngularSweep_IsDeterministic()
        {
            BladeShellContact first = Sweep(BladeSweepSettings.Default);
            BladeShellContact second = Sweep(BladeSweepSettings.Default);

            Assert.AreEqual(first.Status, second.Status);
            Assert.AreEqual(first.TimeOfImpact, second.TimeOfImpact);
            Assert.AreEqual(first.Pair.FeatureA.Index, second.Pair.FeatureA.Index);
            Assert.AreEqual(first.Pair.FeatureB.Index, second.Pair.FeatureB.Index);
        }

        [Test]
        public void AngularSweep_NearbyContactMarginsDoNotChangeTheOutcome()
        {
            BladeShellContact tight = Sweep(BladeSweepSettings.Default.WithContactMargin(0.00025f));
            BladeShellContact loose = Sweep(BladeSweepSettings.Default.WithContactMargin(0.001f));

            Assert.AreEqual(BladeContactStatus.Contact, tight.Status);
            Assert.AreEqual(BladeContactStatus.Contact, loose.Status);
            Assert.Greater(tight.Pair.Separation, 0f);
            Assert.Greater(loose.Pair.Separation, 0f);
            Assert.That(loose.TimeOfImpact, Is.EqualTo(tight.TimeOfImpact).Within(0.01f));
        }

        [Test]
        public void ClearOfThePost_ReportsNoContact()
        {
            BladeShellContact contact = BladeShellSweep.FindFirstContact(
                Shell(), PostPose(), PostPose(),
                Shell(), SwingPose(-SweptAngleDegrees), SwingPose(-30f),
                BladeSweepSettings.Default);

            Assert.AreEqual(BladeContactStatus.NoContact, contact.Status);
            Assert.IsFalse(contact.BlocksMotion);
        }

        [Test]
        public void AngularSweep_ConvergesWellInsideTheIterationCeiling()
        {
            BladeShellContact contact = Sweep(BladeSweepSettings.Default);

            Assert.AreNotEqual(BladeContactStatus.IterationLimit, contact.Status);
            Assert.Less(contact.Iterations, BladeSweepSettings.Default.MaxIterations);
        }
    }
}
