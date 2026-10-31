using UnityEngine;

namespace BladeContact.Prototype
{
    public enum BladeSweepStatus : byte
    {
        /// <summary>The requested motion completes without the shells reaching contact separation.</summary>
        NoContact,

        /// <summary>First contact was located at <see cref="BladeSweepResult.TimeOfImpact"/>.</summary>
        Contact,

        /// <summary>Advancement did not converge within the iteration ceiling; motion is still blocked.</summary>
        IterationLimit
    }

    /// <summary>Outcome of one first-contact query between a registered shell pair.</summary>
    public readonly struct BladeSweepResult
    {
        public readonly BladeSweepStatus Status;

        /// <summary>Fraction of the requested motion that may be accepted, in [0, 1].</summary>
        public readonly float TimeOfImpact;

        public readonly int FeatureIndexA;
        public readonly int FeatureIndexB;

        /// <summary>Witness point on each shell's spine at <see cref="TimeOfImpact"/>, in world space.</summary>
        public readonly Vector3 WitnessA;
        public readonly Vector3 WitnessB;

        /// <summary>Unit contact normal pointing from shell A toward shell B. Zero if degenerate.</summary>
        public readonly Vector3 Normal;

        /// <summary>Surface separation at <see cref="TimeOfImpact"/>; negative means interpenetration.</summary>
        public readonly float Separation;

        public BladeSweepResult(
            BladeSweepStatus status, float timeOfImpact,
            int featureIndexA, int featureIndexB,
            Vector3 witnessA, Vector3 witnessB, Vector3 normal, float separation)
        {
            Status = status;
            TimeOfImpact = timeOfImpact;
            FeatureIndexA = featureIndexA;
            FeatureIndexB = featureIndexB;
            WitnessA = witnessA;
            WitnessB = witnessB;
            Normal = normal;
            Separation = separation;
        }

        /// <summary>True when the requested motion must not be accepted in full.</summary>
        public bool BlocksMotion => Status != BladeSweepStatus.NoContact;

        public static BladeSweepResult NoContact(float separation) => new BladeSweepResult(
            BladeSweepStatus.NoContact, 1f, -1, -1, Vector3.zero, Vector3.zero, Vector3.zero, separation);
    }
}
