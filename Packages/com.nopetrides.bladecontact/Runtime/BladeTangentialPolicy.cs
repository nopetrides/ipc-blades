using System;
using UnityEngine;

namespace BladeContact
{
    /// <summary>Tangential response parameters for one contact scenario.</summary>
    [Serializable]
    public struct BladeTangentialParameters
    {
        [Tooltip("Coulomb friction coefficient once sliding.")]
        public float DynamicFriction;

        [Tooltip("Coefficient below which the contact is treated as stuck rather than sliding.")]
        public float StaticBindThreshold;

        [Tooltip("Tangential speed, m/s, above which a stuck contact is treated as having broken free.")]
        public float ReleaseThreshold;

        public BladeTangentialParameters(float dynamicFriction, float staticBindThreshold, float releaseThreshold)
        {
            DynamicFriction = dynamicFriction;
            StaticBindThreshold = staticBindThreshold;
            ReleaseThreshold = releaseThreshold;
        }

        public static BladeTangentialParameters Lerp(
            in BladeTangentialParameters a, in BladeTangentialParameters b, float t)
        {
            t = Mathf.Clamp01(t);
            return new BladeTangentialParameters(
                Mathf.Lerp(a.DynamicFriction, b.DynamicFriction, t),
                Mathf.Lerp(a.StaticBindThreshold, b.StaticBindThreshold, t),
                Mathf.Lerp(a.ReleaseThreshold, b.ReleaseThreshold, t));
        }

        public override string ToString() =>
            $"dyn {DynamicFriction:F3}, bind {StaticBindThreshold:F3}, release {ReleaseThreshold:F3} m/s";
    }

    /// <summary>
    /// How tangential behaviour is chosen from a contact's SEMANTIC scenario.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This never touches admissibility.</b> It has no influence on the sweep, the time of impact, the
    /// separation, or the non-crossing invariant. Those are decided from raw geometry before any of this is
    /// consulted. This policy only shapes how a contact behaves TANGENTIALLY once it has already been
    /// established as valid.
    /// </para>
    /// <para>
    /// <b>Why the edge-against-flat case is a dial rather than a value.</b> Neither the literature nor the
    /// current physical trial establishes that a cutting edge resting on a non-cutting region should bind
    /// harder than two non-cutting regions do. Picking either answer would smuggle an unevidenced physical
    /// claim into the simulation, and it would be invisible in the results. So the case is exposed as
    /// <see cref="EdgeNonCuttingBind"/> and defaults to 0 — behave exactly like non-cutting against
    /// non-cutting — until specimen-specific trial evidence says otherwise.
    /// </para>
    /// <para>
    /// The dial interpolates the tangential parameters ONLY. It cannot make contact happen sooner, later,
    /// or at a different place.
    /// </para>
    /// </remarks>
    [Serializable]
    public struct BladeTangentialPolicy
    {
        [Tooltip("Tangential behaviour when neither participant is an authored cutting edge.")]
        public BladeTangentialParameters NonCuttingPair;

        [Tooltip("Tangential behaviour when both participants are authored cutting edges.")]
        public BladeTangentialParameters CuttingPair;

        [Tooltip("Where CuttingEdge-vs-NonCuttingRegion contact sits between the two.\n\n" +
                 "0 = behave exactly like NonCuttingRegion vs NonCuttingRegion.\n" +
                 "1 = behave exactly like the configured CuttingEdge vs CuttingEdge binding.\n\n" +
                 "Defaults to 0. Raising it asserts that an edge on a flat binds harder than a flat on a " +
                 "flat, which is not currently evidenced for this specimen.")]
        [Range(0f, 1f)]
        public float EdgeNonCuttingBind;

        /// <summary>
        /// Defaults with the edge-against-flat dial at zero. The two endpoint parameter sets are
        /// placeholders to be replaced by measured values; they are not a claim about this specimen.
        /// </summary>
        public static BladeTangentialPolicy Default => new BladeTangentialPolicy
        {
            NonCuttingPair = new BladeTangentialParameters(0.35f, 0.45f, 0.05f),
            CuttingPair = new BladeTangentialParameters(0.60f, 0.80f, 0.02f),
            EdgeNonCuttingBind = 0f
        };

        /// <summary>Tangential parameters for a scenario.</summary>
        public BladeTangentialParameters Resolve(BladeContactScenario scenario)
        {
            switch (scenario)
            {
                case BladeContactScenario.EdgeEdge:
                    return CuttingPair;
                case BladeContactScenario.EdgeFlat:
                    return BladeTangentialParameters.Lerp(NonCuttingPair, CuttingPair, EdgeNonCuttingBind);
                default:
                    return NonCuttingPair;
            }
        }

        /// <summary>Tangential parameters for a raw feature pair, via its semantic regions.</summary>
        public BladeTangentialParameters Resolve(BladeFeatureType typeA, BladeFeatureType typeB) =>
            Resolve(BladeContactScenarios.Classify(typeA, typeB));

        /// <summary>One-line statement of what this policy will do, for traces and provenance.</summary>
        public string Describe(BladeContactScenario scenario)
        {
            string dial = scenario == BladeContactScenario.EdgeFlat
                ? $" (edgeNonCuttingBind {EdgeNonCuttingBind:F2}"
                  + (EdgeNonCuttingBind <= 0f ? ", i.e. same as FlatFlat)" : ")")
                : string.Empty;

            return $"{scenario}: {Resolve(scenario)}{dial}";
        }
    }
}
