using NUnit.Framework;
using UnityEngine;


namespace BladeContact.Prototype.Tests
{
    /// <summary>
    /// ARCHIVED PROTOTYPE. Disposable proof that endpoint-only checks miss rotation; superseded by
    /// BladeShellSweepTests, which runs the same question against real authored blade geometry. Kept
    /// because it is the smallest reproduction of the tunnelling problem, NOT as production behaviour.
    /// The rig is a rotation-dominant tunnelling case: a rod pivots through a
    /// stationary post while both endpoint poses are far apart, so any endpoint-only test misses the
    /// contact entirely. Analytic contact geometry for the rig is derived in <see cref="AnalyticSeparation"/>.
    /// </summary>
    public sealed class BladeSweepTests
    {
        private const float FeatureRadius = 0.01f;
        private const float SweptAngleDegrees = 60f;

        /// <summary>Stationary post: a vertical spine at x = 0.5, spanning y in [-0.3, 0.3].</summary>
        private static BladeShellData Post() => new BladeShellData(new[]
        {
            new BladeFeature("post", BladeFeatureType.SharpEdge,
                new Vector3(0.5f, -0.3f, 0f), new Vector3(0.5f, 0.3f, 0f), FeatureRadius)
        });

        /// <summary>Rotating rod: a radial spine from 0.2 to 0.9 along local +X, pivoting about its origin.</summary>
        private static BladeShellData Rod() => new BladeShellData(new[]
        {
            new BladeFeature("rod", BladeFeatureType.SharpEdge,
                new Vector3(0.2f, 0f, 0f), new Vector3(0.9f, 0f, 0f), FeatureRadius)
        });

        private static BladePose RodPose(float degrees) =>
            new BladePose(Vector3.zero, Quaternion.AngleAxis(degrees, Vector3.up));

        /// <summary>World position of the rod tip at a given rotation. Its z sign identifies which side of the post it is on.</summary>
        private static Vector3 RodTip(float degrees) => RodPose(degrees).TransformPoint(new Vector3(0.9f, 0f, 0f));

        /// <summary>
        /// Surface separation for the rig at rod angle theta: the post's closest point is (0.5, 0, 0),
        /// whose perpendicular distance to the rod's radial spine is 0.5*|sin theta|, less both radii.
        /// </summary>
        private static float AnalyticSeparation(float degrees) =>
            0.5f * Mathf.Abs(Mathf.Sin(degrees * Mathf.Deg2Rad)) - 2f * FeatureRadius;

        private static BladeSweepResult Sweep(BladeSweepSettings settings)
        {
            return BladeSweep.FindFirstContact(
                Post(), BladePose.Identity, BladePose.Identity,
                Rod(), RodPose(-SweptAngleDegrees), RodPose(SweptAngleDegrees),
                settings);
        }

        private static float SeparationAt(float normalizedTime)
        {
            float degrees = Mathf.Lerp(-SweptAngleDegrees, SweptAngleDegrees, normalizedTime);
            return BladeSweep.ClosestFeaturePair(
                Post(), BladePose.Identity,
                Rod(), RodPose(degrees),
                out _, out _, out _, out _);
        }

        [Test]
        public void Rig_EndpointsAreSeparated_SoEndpointTestingAloneWouldMissTheContact()
        {
            // Guards the test itself: if either endpoint were already in contact, the sweep could pass
            // without ever exercising the angular case.
            Assert.Greater(SeparationAt(0f), 0.4f, "start pose must be clearly separated");
            Assert.Greater(SeparationAt(1f), 0.4f, "end pose must be clearly separated");
            Assert.Less(AnalyticSeparation(0f), 0f, "the requested path must pass through the post");
        }

        [Test]
        public void AngularSweep_FindsFirstContactStrictlyInsideTheRequestedMotion()
        {
            BladeSweepResult result = Sweep(BladeSweepSettings.Default);

            Assert.AreEqual(BladeSweepStatus.Contact, result.Status);
            Assert.Greater(result.TimeOfImpact, 0f);
            Assert.Less(result.TimeOfImpact, 1f);

            // Contact where 0.5*|sin theta| equals both radii plus the margin, i.e. theta ~ 2.350 deg,
            // which the path reaches at t ~ 0.4804.
            Assert.That(result.TimeOfImpact, Is.EqualTo(0.4804f).Within(0.002f));
        }

        [Test]
        public void AngularSweep_AcceptedPoseDoesNotPenetrate()
        {
            BladeSweepResult result = Sweep(BladeSweepSettings.Default);

            Assert.GreaterOrEqual(result.Separation, 0f, "accepted pose must not interpenetrate");
            Assert.GreaterOrEqual(SeparationAt(result.TimeOfImpact), 0f);
        }

        [Test]
        public void AngularSweep_ShellsDoNotExchangeSides()
        {
            BladeSweepResult result = Sweep(BladeSweepSettings.Default);

            float acceptedDegrees = Mathf.Lerp(-SweptAngleDegrees, SweptAngleDegrees, result.TimeOfImpact);

            // The rod starts on the +z side of the post and the requested end pose is on the -z side.
            Assert.Greater(RodTip(-SweptAngleDegrees).z, 0f);
            Assert.Less(RodTip(SweptAngleDegrees).z, 0f, "requested motion does cross to the other side");
            Assert.Greater(RodTip(acceptedDegrees).z, 0f, "accepted motion must stay on the starting side");
        }

        [Test]
        public void AngularSweep_ReportsTheAuthoredFeaturesThatContacted()
        {
            BladeSweepResult result = Sweep(BladeSweepSettings.Default);

            Assert.AreEqual(0, result.FeatureIndexA);
            Assert.AreEqual(0, result.FeatureIndexB);
            Assert.AreEqual(BladeFeatureType.SharpEdge, Post().GetFeature(result.FeatureIndexA).Type);
            Assert.AreNotEqual(Vector3.zero, result.Normal);
        }

        [Test]
        public void AngularSweep_IsDeterministic()
        {
            BladeSweepResult first = Sweep(BladeSweepSettings.Default);
            BladeSweepResult second = Sweep(BladeSweepSettings.Default);

            Assert.AreEqual(first.Status, second.Status);
            Assert.AreEqual(first.TimeOfImpact, second.TimeOfImpact, "same inputs must give the same time of impact");
            Assert.AreEqual(first.FeatureIndexA, second.FeatureIndexA);
            Assert.AreEqual(first.FeatureIndexB, second.FeatureIndexB);
        }

        [Test]
        public void AngularSweep_NearbyContactMarginsDoNotChangeTheOutcome()
        {
            // The contact margin is a numerical tolerance, not a physical claim. Halving or doubling it
            // must not change whether contact is found, only where by a negligible amount.
            BladeSweepResult tight = Sweep(BladeSweepSettings.Default.WithContactMargin(0.00025f));
            BladeSweepResult loose = Sweep(BladeSweepSettings.Default.WithContactMargin(0.001f));

            Assert.AreEqual(BladeSweepStatus.Contact, tight.Status);
            Assert.AreEqual(BladeSweepStatus.Contact, loose.Status);
            Assert.GreaterOrEqual(tight.Separation, 0f);
            Assert.GreaterOrEqual(loose.Separation, 0f);
            Assert.That(loose.TimeOfImpact, Is.EqualTo(tight.TimeOfImpact).Within(0.005f));
        }

        [Test]
        public void ClearOfThePost_ReportsNoContact()
        {
            BladeSweepResult result = BladeSweep.FindFirstContact(
                Post(), BladePose.Identity, BladePose.Identity,
                Rod(), RodPose(-SweptAngleDegrees), RodPose(-30f),
                BladeSweepSettings.Default);

            Assert.AreEqual(BladeSweepStatus.NoContact, result.Status);
            Assert.IsFalse(result.BlocksMotion);
        }

        [Test]
        public void StationaryShells_ReportNoContactRatherThanSpinning()
        {
            BladePose held = RodPose(-SweptAngleDegrees);

            BladeSweepResult result = BladeSweep.FindFirstContact(
                Post(), BladePose.Identity, BladePose.Identity,
                Rod(), held, held,
                BladeSweepSettings.Default);

            Assert.AreEqual(BladeSweepStatus.NoContact, result.Status);
        }
    }
}
