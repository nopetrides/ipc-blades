using NUnit.Framework;
using UnityEngine;

namespace BladeContact.Tests
{
    /// <summary>
    /// The swept root-box broad-phase gate: what it may reject, and what it must never reject.
    /// </summary>
    /// <remarks>
    /// The gate is an optimisation, so its only correctness obligation is one-sided. Rejecting a pair that
    /// could have reached contact would silently lose a contact, which is the failure this whole system
    /// exists to prevent; admitting a pair that turns out to be clear merely costs time. Every test here
    /// is therefore written against that asymmetry: rejections are checked against the exact solver along
    /// the step, and anything reachable is required to survive the gate.
    ///
    /// Same rig as <see cref="BladeShellSweepTests"/>: blade A stands as a vertical post at x = 0.5, blade
    /// B pivots about the world Y axis at (0, 0.5, 0) and swings through it.
    /// </remarks>
    public sealed class BladeShellGateTests
    {
        private static readonly Vector3 Pivot = new Vector3(0f, 0.5f, 0f);

        private static BladeShellData Shell() => BladeShellBuilder.Build(SampleBladeProfiles.Blade());

        private static BladePose PostPose() =>
            new BladePose(new Vector3(0.5f, 0f, 0f), Quaternion.AngleAxis(-90f, Vector3.forward));

        private static BladePose SwingPose(float degrees) =>
            new BladePose(Pivot, Quaternion.AngleAxis(180f + degrees, Vector3.up));

        private static float Margin => BladeSweepSettings.Default.ContactMargin;

        private static bool Gated(float fromDegrees, float toDegrees)
        {
            return BladeShellSweep.CanSkipPair(
                Shell(), PostPose(), PostPose(),
                Shell(), SwingPose(fromDegrees), SwingPose(toDegrees),
                Margin);
        }

        private static BladeShellContact Sweep(float fromDegrees, float toDegrees)
        {
            return BladeShellSweep.FindFirstContact(
                Shell(), PostPose(), PostPose(),
                Shell(), SwingPose(fromDegrees), SwingPose(toDegrees),
                BladeSweepSettings.Default);
        }

        /// <summary>Exact separation at one sampled instant of the step, via the unaccelerated entry point.</summary>
        private static float SeparationAt(float degrees) =>
            BladeShellSweep.ClosestFeaturePair(Shell(), PostPose(), Shell(), SwingPose(degrees)).Separation;

        [Test]
        public void ClearlySeparatedPair_IsRejectedBeforeAnyHierarchyWork()
        {
            // A 2 degree step with the blades roughly 40 cm apart: the motion cannot close that gap.
            Assert.IsTrue(Gated(-40f, -38f), "a pair this far apart must not reach the narrow phase");
        }

        [Test]
        public void RejectedPair_ReallyIsClearOfContactThroughoutTheStep()
        {
            Assume.That(Gated(-40f, -38f));

            // The rejection is only sound if the exact solver agrees along the whole step, not merely at
            // its endpoints. Contact anywhere here would mean the gate discarded a real contact.
            for (int i = 0; i <= 8; i++)
            {
                float degrees = Mathf.Lerp(-40f, -38f, i / 8f);
                Assert.Greater(SeparationAt(degrees), Margin,
                    $"gate rejected the pair but the shells are within the contact margin at {degrees} deg");
            }
        }

        [Test]
        public void RejectedPair_AgreesWithTheExactPath()
        {
            Assume.That(Gated(-40f, -38f));

            // Equivalence: running the full solver over the same step must reach the same verdict.
            BladeShellContact contact = Sweep(-40f, -38f);

            Assert.AreEqual(BladeContactStatus.NoContact, contact.Status);
            Assert.IsFalse(contact.BlocksMotion);
        }

        [Test]
        public void SeparatedAtBothEndpoints_ButRotationSweepsIntoContact_IsNotRejected()
        {
            // The case a static overlap test would get wrong. Both endpoint poses are well clear of the
            // post, yet the arc between them passes straight through it.
            Assert.Greater(SeparationAt(-60f), 0.3f, "start pose is clearly separated");
            Assert.Greater(SeparationAt(60f), 0.3f, "end pose is clearly separated");

            Assert.IsFalse(Gated(-60f, 60f),
                "rotational travel reaches contact during the step, so the pair must survive the gate");
        }

        [Test]
        public void SweptContactPair_StillReportsContactThroughTheGate()
        {
            BladeShellContact contact = Sweep(-60f, 60f);

            Assert.AreEqual(BladeContactStatus.Contact, contact.Status);
            Assert.Greater(contact.TimeOfImpact, 0f);
            Assert.Less(contact.TimeOfImpact, 1f);
        }

        [Test]
        public void NearContactPair_EntersTheNarrowPhase()
        {
            // Barely separated and barely moving: nothing about this pair may be skipped.
            Assert.IsFalse(Gated(-3.5f, -3.4f), "a near-contact pair must reach the exact solver");
        }

        [Test]
        public void NearContactPair_IsMeasuredExactly()
        {
            // Measured on this rig: separation is 0.010347 at -4.5 deg, 0.000711 at -3.4 deg, and first
            // reaches the 0.0005 contact margin at about -3.376 deg. The step must therefore end past
            // -3.376 to contain a contact at all; -3.4 stops fractionally short of one.
            BladeShellContact contact = Sweep(-4.5f, -3.2f);

            Assert.AreEqual(BladeContactStatus.Contact, contact.Status);
            Assert.Greater(contact.Pair.Separation, 0f, "accepted pose must stay strictly separated");
            Assert.IsTrue(contact.Pair.FeatureA.IsValid);
            Assert.IsTrue(contact.Pair.FeatureB.IsValid);
        }

        [Test]
        public void GateNeverRejectsAPairTheExactSolverFindsInContact()
        {
            // Sweeps the whole approach in 2 degree steps. Wherever the gate rejects, the exact solver must
            // report no contact for that same step; the two may never disagree in that direction.
            for (int i = 0; i < 30; i++)
            {
                float from = -60f + i * 2f;
                float to = from + 2f;

                if (!Gated(from, to)) continue;

                BladeShellContact contact = Sweep(from, to);
                Assert.AreEqual(BladeContactStatus.NoContact, contact.Status,
                    $"gate rejected [{from}, {to}] but the exact solver reports {contact.Status}");
            }
        }

        [Test]
        public void StationaryDistantPair_IsRejected()
        {
            // No motion at all: the gate reduces to a pure separation test and must reject.
            Assert.IsTrue(Gated(-40f, -40f));
        }

        [Test]
        public void TranslationTowardTheOtherShell_DefeatsTheRejection()
        {
            // Same distant angular pose, but now the pivot is driven bodily into the post. The rejection
            // must fall away purely because of the translation term.
            BladeShellData a = Shell();
            BladeShellData b = Shell();
            BladePose start = SwingPose(-40f);
            BladePose end = new BladePose(Pivot + new Vector3(1.5f, 0f, 0f), start.Rotation);

            Assert.IsTrue(
                BladeShellSweep.CanSkipPair(a, PostPose(), PostPose(), b, start, start, Margin),
                "without translation this pair is rejected");

            Assert.IsFalse(
                BladeShellSweep.CanSkipPair(a, PostPose(), PostPose(), b, start, end, Margin),
                "translation large enough to close the gap must defeat the rejection");
        }
    }
}
