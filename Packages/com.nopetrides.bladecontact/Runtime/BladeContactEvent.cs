using UnityEngine;

namespace BladeContact
{
    /// <summary>What the manager did about one registered pair on one fixed step.</summary>
    /// <remarks>
    /// Recorded for every driven pair, including the ones that were rejected outright and the ones whose
    /// query was abandoned. A trace that only recorded contacts would make the cheap and the failed cases
    /// invisible, which are exactly the two that need watching: the first is where the runtime budget is
    /// won, and the second must never be mistaken for a contact.
    /// </remarks>
    public readonly struct BladeContactEvent
    {
        /// <summary>Fixed step this record belongs to, counted by the manager.</summary>
        public readonly int Step;

        public readonly string ShellA;
        public readonly string ShellB;

        /// <summary>True when the swept broad phase rejected the pair before any hierarchy work.</summary>
        public readonly bool BroadPhaseRejected;

        public readonly BladeContactStatus Status;

        /// <summary>Fraction of the step accepted before contact. Meaningless unless <see cref="IsUsable"/>.</summary>
        public readonly float TimeOfImpact;

        public readonly BladeFeatureType FeatureTypeA;
        public readonly BladeFeatureType FeatureTypeB;
        public readonly string FeatureIdA;
        public readonly string FeatureIdB;

        /// <summary>Contact normal from A toward B.</summary>
        public readonly Vector3 Normal;

        public readonly float Separation;

        /// <summary>Magnitude of the normal impulse actually applied, in newton-seconds. Zero when none was.</summary>
        public readonly float ImpulseApplied;

        /// <summary>Wall time of the sweep query itself, milliseconds.</summary>
        public readonly double QueryMs;

        public readonly int Iterations;
        public readonly int ExactFeatureTests;

        public BladeContactEvent(
            int step, string shellA, string shellB, bool broadPhaseRejected,
            BladeContactStatus status, float timeOfImpact,
            BladeFeatureType featureTypeA, BladeFeatureType featureTypeB,
            string featureIdA, string featureIdB,
            Vector3 normal, float separation, float impulseApplied,
            double queryMs, int iterations, int exactFeatureTests)
        {
            Step = step;
            ShellA = shellA;
            ShellB = shellB;
            BroadPhaseRejected = broadPhaseRejected;
            Status = status;
            TimeOfImpact = timeOfImpact;
            FeatureTypeA = featureTypeA;
            FeatureTypeB = featureTypeB;
            FeatureIdA = featureIdA;
            FeatureIdB = featureIdB;
            Normal = normal;
            Separation = separation;
            ImpulseApplied = impulseApplied;
            QueryMs = queryMs;
            Iterations = iterations;
            ExactFeatureTests = exactFeatureTests;
        }

        /// <summary>True only when this record carries contact data a response may act on.</summary>
        public bool IsUsable => Status == BladeContactStatus.Contact;

        /// <summary>
        /// Study classification of this contact, from the two participants' AUTHORED designations.
        /// </summary>
        public BladeContactScenario Scenario => BladeContactScenarios.Classify(FeatureTypeA, FeatureTypeB);

        public override string ToString()
        {
            if (BroadPhaseRejected)
                return $"[{Step}] {ShellA} x {ShellB}  REJECTED (broad phase)  {QueryMs:F4} ms";

            if (Status == BladeContactStatus.BudgetExceeded)
                return $"[{Step}] {ShellA} x {ShellB}  ABANDONED (budget) - no contact consumed  " +
                       $"{QueryMs:F3} ms, {Iterations} iters, {ExactFeatureTests} tests";

            if (Status != BladeContactStatus.Contact)
                return $"[{Step}] {ShellA} x {ShellB}  {Status}  {QueryMs:F4} ms, " +
                       $"{Iterations} iters, {ExactFeatureTests} tests";

            return $"[{Step}] {ShellA} x {ShellB}  CONTACT toi={TimeOfImpact:F4} [{Scenario}] " +
                   $"{FeatureTypeA}({FeatureIdA}) x {FeatureTypeB}({FeatureIdB}) " +
                   $"n=({Normal.x:F3},{Normal.y:F3},{Normal.z:F3}) sep={Separation:F6} " +
                   $"J={ImpulseApplied:F4} Ns  {QueryMs:F3} ms, {Iterations} iters, {ExactFeatureTests} tests";
        }
    }
}
