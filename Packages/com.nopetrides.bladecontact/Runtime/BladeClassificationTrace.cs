using System.Collections.Generic;
using UnityEngine;

namespace BladeContact
{
    /// <summary>Where in the query a candidate feature pair was measured.</summary>
    /// <remarks>
    /// Zero is <see cref="None" /> in every enum on this instrument, so a default-initialised or
    /// zero-filled record is detectable as unset rather than masquerading as a real reading.
    /// </remarks>
    public enum BladeCandidateSource : byte
    {
        /// <summary>Never written. A record carrying this was not filled in.</summary>
        None = 0,

        /// <summary>Re-measurement of the previous query's winner, before traversal.</summary>
        WarmStart = 1,

        /// <summary>The greedy descent that opens a cold query with a finite bound.</summary>
        Seed = 2,

        /// <summary>A leaf pair reached by the nearest-first traversal.</summary>
        Leaf = 3
    }

    /// <summary>What the selection rules did with a candidate.</summary>
    public enum BladeCandidateOutcome : byte
    {
        /// <summary>Never written. A record carrying this was not filled in.</summary>
        None = 0,

        /// <summary>
        /// Rejected on bounding spheres before any exact distance was computed. Its recorded distance is a
        /// LOWER BOUND, not a measurement. Never compare it against an exact distance as though it were one.
        /// </summary>
        SphereCulled = 1,

        /// <summary>Measured exactly, and beaten by the incumbent.</summary>
        RejectedNotCloser = 2,

        /// <summary>Took the lead by being closer than the incumbent by more than the tie band.</summary>
        TookAsCloser = 3,

        /// <summary>Took the lead by tying the incumbent within the tie band while being more specific.</summary>
        TookAsTiedMoreSpecific = 4
    }

    /// <summary>How a query ended.</summary>
    /// <remarks>
    /// <see cref="None" /> is zero deliberately. "Completed" must be written by the code path that actually
    /// completed, so a trace that was reset and never run cannot read as a successful query.
    /// </remarks>
    public enum BladeQueryCompletion : byte
    {
        /// <summary>Never written — the query did not run, or the trace was reset and not reused.</summary>
        None = 0,

        /// <summary>Traversal exhausted the heap or pruned everything remaining.</summary>
        Completed = 1,

        /// <summary>Abandoned on the node-pair visit ceiling. No usable witness.</summary>
        AbortedNodeVisits = 2,

        /// <summary>Abandoned on the wall-clock budget. No usable witness.</summary>
        AbortedTimeBudget = 3
    }

    /// <summary>One candidate feature pair as the production query actually saw it.</summary>
    /// <remarks>
    /// <para>
    /// Feature identity is kept as a <see cref="BladeFeatureRef" /> rather than a resolved id/type string,
    /// so recording costs no allocation and the authored designation is read back from the same
    /// <see cref="BladeShellData" /> the query used.
    /// </para>
    /// <para>
    /// <b>The witnesses are recorded because the distance is unsigned.</b>
    /// <see cref="SegmentGeometry.ClosestPointsBetweenSegments" /> and its triangle counterparts return a
    /// magnitude, so a feature that has passed THROUGH the other blade reads the same as one that far
    /// short of it. Two candidates at identical unsigned distance on opposite sides are not the same
    /// locus, and only the witness points can tell them apart.
    /// </para>
    /// </remarks>
    public struct BladeCandidateRecord
    {
        public BladeCandidateSource Source;
        public BladeCandidateOutcome Outcome;
        public BladeFeatureRef FeatureA;
        public BladeFeatureRef FeatureB;

        /// <summary>
        /// Exact separation for every outcome except <see cref="BladeCandidateOutcome.SphereCulled" />,
        /// where it is the sphere-gap lower bound the cull actually tested.
        /// </summary>
        public float Distance;

        /// <summary>False for a sphere cull, whose distance is a bound rather than a measurement.</summary>
        public bool DistanceIsExact;

        /// <summary>0 surface-surface, 1 surface-edge, 2 edge-edge. Geometric kind, NOT authored semantics.</summary>
        public int Specificity;

        /// <summary>The incumbent this candidate was compared against.</summary>
        public float BestBefore;

        /// <summary>The incumbent's specificity at the moment of comparison.</summary>
        public int BestSpecificityBefore;

        /// <summary>Closest point on shell A. Zero for a sphere cull, which computes no witness.</summary>
        public Vector3 WitnessA;

        /// <summary>Closest point on shell B. Zero for a sphere cull, which computes no witness.</summary>
        public Vector3 WitnessB;
    }

    /// <summary>
    /// Read-only record of one production classification query: which candidate feature pairs it measured,
    /// which it discarded before measuring, and which rule selected the winner.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is an instrument, not part of the contact model.</b> Nothing here is read back by the sweep
    /// or the solver, and a null trace is the production path. It exists to answer one question: when the
    /// classifier reports <c>EdgeEdge</c> at one step and <c>EdgeFlat</c> at another, did the candidate
    /// geometry change, or did the same coincident locus resolve differently?
    /// </para>
    /// <para>
    /// <b>Absence is evidence.</b> A candidate that appears in neither <see cref="Candidates" /> nor as a
    /// sphere cull was eliminated by hierarchy pruning before it was ever measured — the node-pair prune
    /// applies no tie band, so an exactly-tied pair in a losing node is discarded unseen. That is why the
    /// capture records culls as well as measurements: the three states are distinguishable only together.
    /// </para>
    /// <para>
    /// <b>Two readings of the winner, on purpose.</b> <see cref="Final" /> carries the winning pair's own
    /// separation, while <see cref="FinalBestScalar" /> carries the traversal's running bound. These are
    /// NOT redundant: the selection lowers the running bound only when a candidate is strictly closer, but
    /// replaces the winning pair unconditionally, so a tie-and-more-specific win leaves the pair's
    /// separation ABOVE the bound. Recording one alone would silently misattribute that case.
    /// </para>
    /// <para>
    /// <see cref="Capacity" /> bounds the record so a traced query cannot grow without limit;
    /// <see cref="Overflow" /> counts what was dropped, so a truncated capture can never be mistaken for a
    /// complete one.
    /// </para>
    /// </remarks>
    public sealed class BladeClassificationTrace
    {
        public const int DefaultCapacity = 2048;

        private readonly List<BladeCandidateRecord> candidates;

        public BladeClassificationTrace(int capacity = DefaultCapacity)
        {
            Capacity = capacity < 1 ? 1 : capacity;
            candidates = new List<BladeCandidateRecord>(Capacity);
        }

        public int Capacity { get; private set; }

        /// <summary>Candidates in the order the query handled them.</summary>
        public List<BladeCandidateRecord> Candidates => candidates;

        /// <summary>Candidates dropped for want of capacity. Non-zero means this capture is incomplete.</summary>
        public int Overflow { get; private set; }

        /// <summary>The tie band compiled into the sweep that produced this trace.</summary>
        public float TieBand;

        /// <summary>
        /// The EFFECTIVE query ceilings, read from the settings actually passed in rather than assumed from
        /// <see cref="BladeSweepSettings.Default" />, because the solver serialises its own copy.
        /// </summary>
        public int MaxNodePairVisits;

        public float DiagnosticTimeBudgetMs;

        /// <summary>True when a previous winner existed for these shells and was re-measured.</summary>
        public bool WarmStartUsed;

        /// <summary>
        /// Hierarchy feature SLOTS the warm start opened with, or -1. A slot is not a
        /// <see cref="BladeFeatureRef.Index" />: slots run surfaces first and then edges, so comparing a
        /// slot against a ref index is only valid for surfaces. Use <see cref="SurfaceCountA" /> to convert.
        /// </summary>
        public int WarmFeatureA, WarmFeatureB;

        /// <summary>
        /// True when the warm-started pair was still the winner at the end of traversal. Set by the sweep
        /// using its own slot arithmetic, never recomputed by a consumer.
        /// </summary>
        public bool WarmStartKept;

        /// <summary>Surface counts, so slot/index conversion is reproducible at analysis time.</summary>
        public int SurfaceCountA, SurfaceCountB;

        public int NodePairsVisited;
        public BladeQueryCompletion Completion;

        /// <summary>
        /// The winning pair. Valid only when <see cref="Completion" /> is
        /// <see cref="BladeQueryCompletion.Completed" />.
        /// </summary>
        public BladeFeaturePair Final;

        /// <summary>The traversal's running bound at the end. See the type remarks: not the same quantity
        /// as <c>Final.Separation</c>.</summary>
        public float FinalBestScalar;

        /// <summary>The winning pair's specificity at the end.</summary>
        public int FinalSpecificity;

        // Context the reference enumeration needs to reproduce this query's geometry exactly. The shells
        // are the same instances the query used, and the poses are the ones it was posed with, so an
        // unpruned re-measurement is like-for-like rather than a second representation. The poses must be
        // read from here and never re-read from a Transform, because the bodies keep moving after capture.
        public BladeShellData ShellA, ShellB;
        public BladePose PoseA, PoseB;

        // Stamped by the solver so a trace can never be attributed to the wrong pair, step or verdict.
        public string LabelA, LabelB;
        public int Step;

        /// <summary>
        /// The verdict, VALID ONLY WHEN <see cref="ClassificationValid" /> IS TRUE.
        /// <see cref="BladeContactScenario.EdgeEdge" /> is the enum's zero value, so a reset or abandoned
        /// trace reads as EdgeEdge on this field alone. Never select or count on it without the flag.
        /// </summary>
        public BladeContactScenario Scenario;

        public bool ClassificationValid;

        public void Reset(float tieBand, in BladeSweepSettings settings)
        {
            candidates.Clear();
            Overflow = 0;
            TieBand = tieBand;
            MaxNodePairVisits = settings.MaxNodePairVisits;
            DiagnosticTimeBudgetMs = settings.DiagnosticTimeBudgetMs;
            WarmStartUsed = false;
            WarmFeatureA = WarmFeatureB = -1;
            WarmStartKept = false;
            SurfaceCountA = SurfaceCountB = -1;
            NodePairsVisited = 0;

            // NOT Completed: only the code path that actually finished traversal may write that, so a trace
            // that was reset and never run cannot be read as a successful query.
            Completion = BladeQueryCompletion.None;
            Final = BladeFeaturePair.None;
            FinalBestScalar = float.MaxValue;
            FinalSpecificity = -1;
            ShellA = ShellB = null;
            PoseA = PoseB = BladePose.Identity;
            LabelA = LabelB = null;
            Step = -1;
            Scenario = default(BladeContactScenario);
            ClassificationValid = false;
        }

        public void Add(
            BladeCandidateSource source, BladeCandidateOutcome outcome,
            BladeFeatureRef featureA, BladeFeatureRef featureB,
            float distance, bool distanceIsExact, int specificity,
            float bestBefore, int bestSpecificityBefore,
            Vector3 witnessA, Vector3 witnessB)
        {
            if (candidates.Count >= Capacity)
            {
                Overflow++;
                return;
            }

            candidates.Add(new BladeCandidateRecord
            {
                Source = source,
                Outcome = outcome,
                FeatureA = featureA,
                FeatureB = featureB,
                Distance = distance,
                DistanceIsExact = distanceIsExact,
                Specificity = specificity,
                BestBefore = bestBefore,
                BestSpecificityBefore = bestSpecificityBefore,
                WitnessA = witnessA,
                WitnessB = witnessB
            });
        }

        /// <summary>
        /// Deep copy, so a captured event survives the next query overwriting the live trace.
        /// </summary>
        public BladeClassificationTrace Clone()
        {
            var copy = new BladeClassificationTrace(Capacity);
            copy.candidates.AddRange(candidates);
            copy.Overflow = Overflow;
            copy.TieBand = TieBand;
            copy.MaxNodePairVisits = MaxNodePairVisits;
            copy.DiagnosticTimeBudgetMs = DiagnosticTimeBudgetMs;
            copy.WarmStartUsed = WarmStartUsed;
            copy.WarmFeatureA = WarmFeatureA;
            copy.WarmFeatureB = WarmFeatureB;
            copy.WarmStartKept = WarmStartKept;
            copy.SurfaceCountA = SurfaceCountA;
            copy.SurfaceCountB = SurfaceCountB;
            copy.NodePairsVisited = NodePairsVisited;
            copy.Completion = Completion;
            copy.Final = Final;
            copy.FinalBestScalar = FinalBestScalar;
            copy.FinalSpecificity = FinalSpecificity;
            copy.ShellA = ShellA;
            copy.ShellB = ShellB;
            copy.PoseA = PoseA;
            copy.PoseB = PoseB;
            copy.LabelA = LabelA;
            copy.LabelB = LabelB;
            copy.Step = Step;
            copy.Scenario = Scenario;
            copy.ClassificationValid = ClassificationValid;
            return copy;
        }
    }
}
