using NUnit.Framework;
using UnityEngine;

namespace BladeContact.Tests
{
    /// <summary>
    /// The warm-start pruning hint must be invisible in every result it touches.
    /// </summary>
    /// <remarks>
    /// Carrying the previous query's winning pair into the next query is only safe because the hint is
    /// used as a bound and never as an answer. These tests attack that claim from the side that would
    /// actually break: not whether a good hint helps, but whether a BAD one can change what the solver
    /// concludes. A hint that is stale, that belongs to a wildly different pose, or that names a pair which
    /// is nowhere near closest, must all produce bit-identical results to a cold solver.
    /// </remarks>
    public sealed class BladeWarmStartTests
    {
        private static readonly Vector3 Pivot = new Vector3(0f, 0.5f, 0f);

        private static BladeShellData Shell() => BladeShellBuilder.Build(SampleBladeProfiles.Blade());

        private static BladePose PostPose() =>
            new BladePose(new Vector3(0.5f, 0f, 0f), Quaternion.AngleAxis(-90f, Vector3.forward));

        private static BladePose Swing(float degrees) =>
            new BladePose(Pivot, Quaternion.AngleAxis(180f + degrees, Vector3.up));

        private static void AssertIdentical(BladeFeaturePair expected, BladeFeaturePair actual, string what)
        {
            Assert.AreEqual(expected.Separation, actual.Separation, $"{what}: separation");
            Assert.AreEqual(expected.FeatureA.Kind, actual.FeatureA.Kind, $"{what}: feature A kind");
            Assert.AreEqual(expected.FeatureA.Index, actual.FeatureA.Index, $"{what}: feature A index");
            Assert.AreEqual(expected.FeatureB.Kind, actual.FeatureB.Kind, $"{what}: feature B kind");
            Assert.AreEqual(expected.FeatureB.Index, actual.FeatureB.Index, $"{what}: feature B index");
        }

        [Test]
        public void ColdAndWarmQueries_AgreeExactly()
        {
            BladeShellData a = Shell();
            BladeShellData b = Shell();
            BladePose target = Swing(-12f);

            BladeFeaturePair cold = BladeShellSweep.ClosestFeaturePair(
                a, PostPose(), b, target, new BladeShellScratch());

            // Prime the hint somewhere else entirely, then query the target through the same scratch.
            var shared = new BladeShellScratch();
            BladeShellSweep.ClosestFeaturePair(a, PostPose(), b, Swing(-55f), shared);
            BladeFeaturePair warm = BladeShellSweep.ClosestFeaturePair(a, PostPose(), b, target, shared);

            AssertIdentical(cold, warm, "warm start changed the closest pair");
        }

        [Test]
        public void HintFromTheOppositeSideOfTheSweep_ChangesNothing()
        {
            BladeShellData a = Shell();
            BladeShellData b = Shell();
            BladePose target = Swing(-12f);

            BladeFeaturePair cold = BladeShellSweep.ClosestFeaturePair(
                a, PostPose(), b, target, new BladeShellScratch());

            // The blades swap sides across 0 degrees, so a hint taken at +50 names a pair on the far side.
            var shared = new BladeShellScratch();
            BladeShellSweep.ClosestFeaturePair(a, PostPose(), b, Swing(50f), shared);
            BladeFeaturePair warm = BladeShellSweep.ClosestFeaturePair(a, PostPose(), b, target, shared);

            AssertIdentical(cold, warm, "a hint from the far side of the sweep leaked into the result");
        }

        [Test]
        public void ForgettingTheHint_ChangesNothing()
        {
            BladeShellData a = Shell();
            BladeShellData b = Shell();
            BladePose target = Swing(-20f);

            var shared = new BladeShellScratch();
            BladeShellSweep.ClosestFeaturePair(a, PostPose(), b, Swing(-55f), shared);

            BladeFeaturePair withHint = BladeShellSweep.ClosestFeaturePair(a, PostPose(), b, target, shared);

            shared.ForgetWarmStart();
            BladeFeaturePair withoutHint = BladeShellSweep.ClosestFeaturePair(a, PostPose(), b, target, shared);

            AssertIdentical(withHint, withoutHint, "dropping the hint changed the answer");
        }

        [Test]
        public void ScratchReusedAcrossDifferentShells_IsNotConfused()
        {
            // The hint stores feature indices, which mean different things for different shells. Reusing one
            // scratch across two shell pairs must not carry indices from one into the other.
            BladeShellData a = Shell();
            BladeShellData b = Shell();
            BladeShellData other = BladeShellBuilder.Build(SampleBladeProfiles.Blade(0f));

            var shared = new BladeShellScratch();
            BladeShellSweep.ClosestFeaturePair(a, PostPose(), other, Swing(-30f), shared);

            BladeFeaturePair cold = BladeShellSweep.ClosestFeaturePair(
                a, PostPose(), b, Swing(-12f), new BladeShellScratch());
            BladeFeaturePair warm = BladeShellSweep.ClosestFeaturePair(a, PostPose(), b, Swing(-12f), shared);

            AssertIdentical(cold, warm, "a hint from a different shell pair leaked in");
        }

        [Test]
        public void SweepWithAReusedScratch_MatchesAFreshOne()
        {
            BladeShellContact fresh = BladeShellSweep.FindFirstContact(
                Shell(), PostPose(), PostPose(), Shell(), Swing(-60f), Swing(60f),
                BladeSweepSettings.Default, new BladeShellScratch());

            var shared = new BladeShellScratch();
            BladeShellSweep.FindFirstContact(
                Shell(), PostPose(), PostPose(), Shell(), Swing(-40f), Swing(-20f),
                BladeSweepSettings.Default, shared);

            BladeShellContact reused = BladeShellSweep.FindFirstContact(
                Shell(), PostPose(), PostPose(), Shell(), Swing(-60f), Swing(60f),
                BladeSweepSettings.Default, shared);

            Assert.AreEqual(fresh.Status, reused.Status);
            Assert.AreEqual(fresh.TimeOfImpact, reused.TimeOfImpact, "warm start moved the time of impact");
            Assert.AreEqual(fresh.Iterations, reused.Iterations);
            AssertIdentical(fresh.Pair, reused.Pair, "warm start changed the contact pair");
        }

        [Test]
        public void RepeatedSweepsThroughOneScratch_StayDeterministic()
        {
            var shared = new BladeShellScratch();

            BladeShellContact first = BladeShellSweep.FindFirstContact(
                Shell(), PostPose(), PostPose(), Shell(), Swing(-60f), Swing(60f),
                BladeSweepSettings.Default, shared);

            BladeShellContact second = BladeShellSweep.FindFirstContact(
                Shell(), PostPose(), PostPose(), Shell(), Swing(-60f), Swing(60f),
                BladeSweepSettings.Default, shared);

            BladeShellContact third = BladeShellSweep.FindFirstContact(
                Shell(), PostPose(), PostPose(), Shell(), Swing(-60f), Swing(60f),
                BladeSweepSettings.Default, shared);

            Assert.AreEqual(first.TimeOfImpact, second.TimeOfImpact);
            Assert.AreEqual(second.TimeOfImpact, third.TimeOfImpact);
            AssertIdentical(first.Pair, third.Pair, "repeated warm sweeps drifted");
        }

        [Test]
        public void WarmStartedSweep_StillDoesNotPenetrate()
        {
            var shared = new BladeShellScratch();
            BladeShellSweep.FindFirstContact(
                Shell(), PostPose(), PostPose(), Shell(), Swing(-50f), Swing(-30f),
                BladeSweepSettings.Default, shared);

            BladeShellContact contact = BladeShellSweep.FindFirstContact(
                Shell(), PostPose(), PostPose(), Shell(), Swing(-60f), Swing(60f),
                BladeSweepSettings.Default, shared);

            Assert.AreEqual(BladeContactStatus.Contact, contact.Status);
            Assert.Greater(contact.Pair.Separation, 0f);

            BladePose accepted = BladePose.Interpolate(Swing(-60f), Swing(60f), contact.TimeOfImpact);
            Assert.Greater(
                BladeShellSweep.ClosestFeaturePair(Shell(), PostPose(), Shell(), accepted).Separation, 0f,
                "warm-started sweep accepted a penetrating pose");
        }

        [Test]
        public void ClearStepThroughAWarmScratch_StillReportsNoContact()
        {
            var shared = new BladeShellScratch();
            BladeShellSweep.FindFirstContact(
                Shell(), PostPose(), PostPose(), Shell(), Swing(-10f), Swing(-5f),
                BladeSweepSettings.Default, shared);

            BladeShellContact contact = BladeShellSweep.FindFirstContact(
                Shell(), PostPose(), PostPose(), Shell(), Swing(-40f), Swing(-38f),
                BladeSweepSettings.Default, shared);

            Assert.AreEqual(BladeContactStatus.NoContact, contact.Status);
        }
    }
}
