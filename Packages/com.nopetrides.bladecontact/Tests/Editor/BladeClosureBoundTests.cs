using NUnit.Framework;
using UnityEngine;

namespace BladeContact.Tests
{
    /// <summary>
    /// Equivalence of the localized closure bound against the solver as it behaved before tightening.
    /// </summary>
    /// <remarks>
    /// Tightening the closure bound changes how far each conservative-advancement step may reach. It must
    /// not change what the sweep concludes. The reference values below were measured from the solver
    /// immediately before the change, with the same rig, profile and settings, and are quoted per case.
    ///
    /// Time of impact is deliberately NOT asserted equal. Conservative advancement approaches contact
    /// asymptotically, so a tighter bound stops nearer the true contact time: the correct expectation is
    /// that the new time of impact is no EARLIER than the old one and still strictly non-penetrating.
    /// Asserting equality would forbid the improvement; asserting only "close" would miss a bound that
    /// overshoots. Both directions are checked separately.
    /// </remarks>
    public sealed class BladeClosureBoundTests
    {
        private static readonly Vector3 Pivot = new Vector3(0f, 0.5f, 0f);

        private static BladeShellData Shell() => BladeShellBuilder.Build(SampleBladeProfiles.Blade());

        private static BladePose PostPose() =>
            new BladePose(new Vector3(0.5f, 0f, 0f), Quaternion.AngleAxis(-90f, Vector3.forward));

        private static BladePose Swing(float degrees) => SwingAt(degrees, Pivot);

        private static BladePose SwingAt(float degrees, Vector3 pivot) =>
            new BladePose(pivot, Quaternion.AngleAxis(180f + degrees, Vector3.up));

        private static BladeShellContact Sweep(BladePose start, BladePose end) =>
            BladeShellSweep.FindFirstContact(
                Shell(), PostPose(), PostPose(), Shell(), start, end, BladeSweepSettings.Default);

        /// <summary>Exact separation at a sampled instant, through the unaccelerated entry point.</summary>
        private static float SeparationAt(BladePose start, BladePose end, float t)
        {
            BladePose pose = BladePose.Interpolate(start, end, t);
            return BladeShellSweep.ClosestFeaturePair(Shell(), PostPose(), Shell(), pose).Separation;
        }

        /// <summary>
        /// The shared obligation for every case: same verdict, same named features, a time of impact no
        /// earlier than the reference, and a pose that still does not penetrate.
        /// </summary>
        private static void AssertMatchesReference(
            BladePose start, BladePose end,
            float referenceToi, BladeFeatureKind kindA, int indexA, BladeFeatureKind kindB, int indexB)
        {
            BladeShellContact contact = Sweep(start, end);

            Assert.AreEqual(BladeContactStatus.Contact, contact.Status);
            Assert.AreEqual(kindA, contact.Pair.FeatureA.Kind);
            Assert.AreEqual(indexA, contact.Pair.FeatureA.Index, "named feature on A changed");
            Assert.AreEqual(kindB, contact.Pair.FeatureB.Kind);
            Assert.AreEqual(indexB, contact.Pair.FeatureB.Index, "named feature on B changed");

            Assert.GreaterOrEqual(contact.TimeOfImpact, referenceToi - 1e-4f,
                "a tighter bound must not stop EARLIER than the untightened solver did");
            Assert.Less(contact.TimeOfImpact, 1f);

            // The whole point of conservativeness: the accepted pose is still clear of penetration.
            Assert.Greater(contact.Pair.Separation, 0f, "accepted pose must stay strictly separated");
            Assert.Greater(SeparationAt(start, end, contact.TimeOfImpact), 0f);
        }

        [Test]
        public void ExistingAngularFixture_ReachesTheSameContact()
        {
            // Reference before tightening: Contact, toi 0.4718667, Edge:138 / Edge:138, 22 iterations.
            AssertMatchesReference(
                Swing(-60f), Swing(60f), 0.4718667f, BladeFeatureKind.Edge, 138, BladeFeatureKind.Edge, 138);
        }

        [Test]
        public void ClosestFeatureIdentityChangesDuringTheSweep_StillReachesTheSameContact()
        {
            // Along this step the closest pair walks the blade -- Edge:66, 90, 114, 126, then 138 -- so the
            // bound may not assume the current witness stays the contact pair.
            // Reference before tightening: Contact, toi 0.6618587, Edge:138 / Edge:66, 45 iterations.
            AssertMatchesReference(
                SwingAt(-60f, Pivot), SwingAt(20f, Pivot + new Vector3(0.35f, 0f, 0f)),
                0.6618587f, BladeFeatureKind.Edge, 138, BladeFeatureKind.Edge, 66);
        }

        [Test]
        public void TranslationPlusRotation_ReachesTheSameContact()
        {
            // Same case, asserted for the combined-motion property rather than the identity change: the
            // translation and rotation terms must both survive localization.
            BladeShellContact contact = Sweep(
                SwingAt(-60f, Pivot), SwingAt(20f, Pivot + new Vector3(0.35f, 0f, 0f)));

            Assert.AreEqual(BladeContactStatus.Contact, contact.Status);
            Assert.Greater(contact.TimeOfImpact, 0f);
            Assert.Less(contact.TimeOfImpact, 1f);
            Assert.Greater(contact.Pair.Separation, 0f);
        }

        [Test]
        public void PureTranslation_IsUnaffectedByLocalization()
        {
            // With no rotation there is no lever arm to localize, so this must reproduce the global bound
            // exactly. Reference before tightening: Contact, toi 0.5320150, Surface:279 / Edge:6.
            BladeShellContact contact = Sweep(
                SwingAt(-90f, Pivot), SwingAt(-90f, Pivot + new Vector3(0.9f, 0f, 0f)));

            Assert.AreEqual(BladeContactStatus.Contact, contact.Status);
            Assert.AreEqual(0.5320150f, contact.TimeOfImpact, 1e-5f, "pure translation must be unchanged");
            Assert.AreEqual(BladeFeatureKind.Surface, contact.Pair.FeatureA.Kind);
            Assert.AreEqual(279, contact.Pair.FeatureA.Index);
            Assert.AreEqual(BladeFeatureKind.Edge, contact.Pair.FeatureB.Kind);
            Assert.AreEqual(6, contact.Pair.FeatureB.Index);
        }

        [Test]
        public void HighAngularVelocity_DoesNotTunnel()
        {
            // A 160 degree shortest-arc sweep straight through the post in a single step. The endpoints are
            // both clear, so only the angular sweep can catch it.
            BladePose start = Swing(-80f);
            BladePose end = Swing(80f);

            Assert.Greater(SeparationAt(start, end, 0f), 0.3f, "start pose is clear");
            Assert.Greater(SeparationAt(start, end, 1f), 0.3f, "end pose is clear");

            BladeShellContact contact = Sweep(start, end);

            Assert.AreEqual(BladeContactStatus.Contact, contact.Status, "a fast angular sweep must not tunnel");
            Assert.Greater(contact.Pair.Separation, 0f);
            Assert.Greater(SeparationAt(start, end, contact.TimeOfImpact), 0f,
                "accepted pose must not penetrate");
        }

        [Test]
        public void ShallowGraze_ReachesTheSameContact()
        {
            // Reference before tightening: Contact, toi 0.4156021, Edge:138 / Edge:138, 21 iterations.
            AssertMatchesReference(
                Swing(-20f), Swing(20f), 0.4156021f, BladeFeatureKind.Edge, 138, BladeFeatureKind.Edge, 138);
        }

        [Test]
        public void ClearStep_StillReportsNoContact()
        {
            // Reference before tightening: NoContact. A tighter bound takes larger steps, so this is where
            // an unsound bound would leap past the geometry and wrongly report contact -- or miss one.
            BladeShellContact contact = Sweep(Swing(-5.4f), Swing(-3.4f));

            Assert.AreEqual(BladeContactStatus.NoContact, contact.Status);
            Assert.IsFalse(contact.BlocksMotion);
        }

        [Test]
        public void AcceptedPoseNeverPenetrates_AcrossTheWholeApproach()
        {
            // Every 4 degree step over the approach: wherever contact is reported, the accepted pose must
            // still be strictly separated. This is the property a too-large step would break first.
            for (int i = 0; i < 15; i++)
            {
                float from = -60f + i * 4f;
                BladePose start = Swing(from);
                BladePose end = Swing(from + 4f);

                BladeShellContact contact = Sweep(start, end);
                if (contact.Status != BladeContactStatus.Contact) continue;

                Assert.Greater(SeparationAt(start, end, contact.TimeOfImpact), 0f,
                    $"step [{from}, {from + 4f}] accepted a penetrating pose");
            }
        }

        [Test]
        public void IsStillDeterministic()
        {
            BladeShellContact first = Sweep(Swing(-60f), Swing(60f));
            BladeShellContact second = Sweep(Swing(-60f), Swing(60f));

            Assert.AreEqual(first.Status, second.Status);
            Assert.AreEqual(first.TimeOfImpact, second.TimeOfImpact);
            Assert.AreEqual(first.Pair.FeatureA.Index, second.Pair.FeatureA.Index);
            Assert.AreEqual(first.Pair.FeatureB.Index, second.Pair.FeatureB.Index);
        }
    }
}
