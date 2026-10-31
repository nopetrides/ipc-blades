using UnityEngine;

namespace BladeContact
{
    public enum BladeContactStatus : byte
    {
        /// <summary>The requested motion completes without the shells reaching contact separation.</summary>
        NoContact,

        /// <summary>First contact was located at <see cref="BladeShellContact.TimeOfImpact"/>.</summary>
        Contact,

        /// <summary>Advancement did not converge within the iteration ceiling; motion is still blocked.</summary>
        IterationLimit,

        /// <summary>
        /// The query hit its diagnostic work or time ceiling and was abandoned.
        /// </summary>
        /// <remarks>
        /// This is NOT a contact result. Whatever the traversal had found so far is discarded, because a
        /// partially explored hierarchy can miss the true closest pair entirely and reporting it as a
        /// contact would be worse than reporting nothing. Callers must check
        /// <see cref="BladeShellContact.IsValid"/> and must not consume the pair or the time of impact.
        /// </remarks>
        BudgetExceeded
    }

    /// <summary>
    /// Closest authored feature pair between two shells at one pose, with the witness point on each.
    /// </summary>
    public readonly struct BladeFeaturePair
    {
        public readonly BladeFeatureRef FeatureA;
        public readonly BladeFeatureRef FeatureB;
        public readonly Vector3 WitnessA;
        public readonly Vector3 WitnessB;

        /// <summary>
        ///     Distance between <see cref="WitnessA" /> and <see cref="WitnessB" />, metres, unsigned.
        /// </summary>
        /// <remarks>
        ///     A property of THIS feature pair only — the two features the query selected. It is not the
        ///     distance between the shells, between the blades, or between any particular authored edge,
        ///     and it says nothing about a feature the query did not select. Surfaced to consumers as
        ///     <c>RqBladeTangentialSolver.Snapshot.ShellSeparation</c>, whose remarks record why it must not
        ///     be swapped for an independently measured edge gap.
        /// </remarks>
        public readonly float Separation;

        public BladeFeaturePair(
            BladeFeatureRef featureA, BladeFeatureRef featureB,
            Vector3 witnessA, Vector3 witnessB, float separation)
        {
            FeatureA = featureA;
            FeatureB = featureB;
            WitnessA = witnessA;
            WitnessB = witnessB;
            Separation = separation;
        }

        /// <summary>Unit normal from shell A toward shell B. Zero when the witnesses coincide.</summary>
        public Vector3 Normal
        {
            get
            {
                Vector3 delta = WitnessB - WitnessA;
                return delta.sqrMagnitude > 1e-18f ? delta.normalized : Vector3.zero;
            }
        }

        public static BladeFeaturePair None =>
            new BladeFeaturePair(BladeFeatureRef.None, BladeFeatureRef.None, Vector3.zero, Vector3.zero, float.MaxValue);
    }

    /// <summary>Outcome of one first-contact query between a registered shell pair.</summary>
    public readonly struct BladeShellContact
    {
        public readonly BladeContactStatus Status;

        /// <summary>Fraction of the requested motion that may be accepted, in [0, 1].</summary>
        public readonly float TimeOfImpact;

        public readonly BladeFeaturePair Pair;

        /// <summary>Conservative-advancement iterations consumed, for the event trace.</summary>
        public readonly int Iterations;

        public BladeShellContact(
            BladeContactStatus status, float timeOfImpact, BladeFeaturePair pair, int iterations)
        {
            Status = status;
            TimeOfImpact = timeOfImpact;
            Pair = pair;
            Iterations = iterations;
        }

        /// <summary>
        /// False only when the query was abandoned. An invalid result carries no usable contact data and
        /// must never be consumed as though the shells had been fully examined.
        /// </summary>
        public bool IsValid => Status != BladeContactStatus.BudgetExceeded;

        /// <summary>True when the requested motion must not be accepted in full.</summary>
        public bool BlocksMotion => Status == BladeContactStatus.Contact || Status == BladeContactStatus.IterationLimit;

        public static BladeShellContact Abandoned(int iterations) =>
            new BladeShellContact(BladeContactStatus.BudgetExceeded, 0f, BladeFeaturePair.None, iterations);
    }
}
