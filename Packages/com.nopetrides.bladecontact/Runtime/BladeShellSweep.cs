using System.Diagnostics;
using UnityEngine;

namespace BladeContact
{
    /// <summary>Instrumentation for one sweep, so cost can be attributed rather than guessed.</summary>
    public sealed class BladeContactStats
    {
        public int BroadPhaseTests;
        public int BroadPhaseRejections;
        public int Iterations;
        public int CoarseIterations;
        public int ExactQueries;
        public int NodePairsVisited;
        public int LeafPairsVisited;
        public int ExactFeatureTests;
        public int FeaturePairsCulled;

        /// <summary>Feature vertices actually transformed to world (cache misses).</summary>
        public int FeatureTransforms;

        /// <summary>Feature lookups served from the per-query pose cache.</summary>
        public int FeatureCacheHits;

        /// <summary>Queries that opened with a prior iteration's pair instead of a greedy descent.</summary>
        public int WarmStarts;

        /// <summary>Warm-started queries whose prior pair survived traversal as the closest pair.</summary>
        public int WarmStartsKept;

        /// <summary>Wall time inside the transform/cache path, milliseconds.</summary>
        public double TransformMs;

        /// <summary>Wall time inside the exact distance arithmetic, milliseconds.</summary>
        public double DistanceMs;

        /// <summary>
        /// Exact feature tests split by the authored classification of the two participants, so feature
        /// participation can be argued from measurement rather than assumed.
        /// </summary>
        public int TestsSharpSharp;
        public int TestsSharpSurface;
        public int TestsSharpBlunt;
        public int TestsSurfaceSurface;
        public int TestsSurfaceBlunt;
        public int TestsBluntBlunt;

        public double ElapsedMs;
        public bool BudgetExceeded;

        public void Reset()
        {
            BroadPhaseTests = 0;
            BroadPhaseRejections = 0;
            Iterations = 0;
            CoarseIterations = 0;
            ExactQueries = 0;
            NodePairsVisited = 0;
            LeafPairsVisited = 0;
            ExactFeatureTests = 0;
            FeaturePairsCulled = 0;
            FeatureTransforms = 0;
            FeatureCacheHits = 0;
            WarmStarts = 0;
            WarmStartsKept = 0;
            TransformMs = 0d;
            DistanceMs = 0d;
            TestsSharpSharp = 0;
            TestsSharpSurface = 0;
            TestsSharpBlunt = 0;
            TestsSurfaceSurface = 0;
            TestsSurfaceBlunt = 0;
            TestsBluntBlunt = 0;
            ElapsedMs = 0d;
            BudgetExceeded = false;
        }

        public override string ToString() =>
            $"broad {BroadPhaseTests}/{BroadPhaseRejections}rej, iters {Iterations} " +
            $"(coarse {CoarseIterations}, exact {ExactQueries}), nodePairs {NodePairsVisited}, " +
            $"leafPairs {LeafPairsVisited}, featureTests {ExactFeatureTests} " +
            $"({FeaturePairsCulled} culled), xform {FeatureTransforms}/{FeatureCacheHits}hit, " +
            $"warm {WarmStartsKept}/{WarmStarts}kept, " +
            $"{ElapsedMs:F3} ms (xform {TransformMs:F3}, dist {DistanceMs:F3})" +
            (BudgetExceeded ? "  [BUDGET EXCEEDED]" : string.Empty);

        /// <summary>Exact-feature tests broken down by authored participant classification.</summary>
        public string Classification() =>
            $"sharp-sharp {TestsSharpSharp}, sharp-surface {TestsSharpSurface}, " +
            $"sharp-blunt {TestsSharpBlunt}, surface-surface {TestsSurfaceSurface}, " +
            $"surface-blunt {TestsSurfaceBlunt}, blunt-blunt {TestsBluntBlunt}";
    }

    /// <summary>
    /// Reusable per-query buffers: the nearest-first traversal heap. Feature vertices are transformed
    /// lazily inside leaf tests, because pruning means most are never touched.
    /// </summary>
    public sealed class BladeShellScratch
    {
        internal float[] HeapBound = new float[256];
        internal int[] HeapA = new int[256];
        internal int[] HeapB = new int[256];
        internal int HeapCount;

        /// <summary>
        /// World-space feature geometry for the two shells at the current query's poses. Each feature is
        /// transformed at most once per query however many leaf pairs it takes part in.
        /// </summary>
        internal readonly BladeShellPoseCache CacheA = new BladeShellPoseCache();

        internal readonly BladeShellPoseCache CacheB = new BladeShellPoseCache();

        /// <summary>
        /// The feature pair that won the previous query, in hierarchy feature indices, together with the
        /// shells it belonged to. Re-measuring it at the new pose is a cheap way to open a query with a
        /// tight finite bound, because consecutive iterates of one sweep move the shells very little.
        /// </summary>
        /// <remarks>
        /// This is a PRUNING HINT and nothing more. It seeds the running best with a real measured
        /// separation of a real pair, which is exactly what the traversal's own seed descent produces; the
        /// traversal then runs unchanged and replaces it whenever it finds anything closer. A stale or
        /// simply wrong hint costs a wasted measurement and cannot change the answer, so it is never
        /// invalidated on pose change -- only when the shells themselves differ.
        /// </remarks>
        internal BladeShellData WarmShellA;

        internal BladeShellData WarmShellB;
        internal int WarmFeatureA = -1;
        internal int WarmFeatureB = -1;

        /// <summary>Drops the pruning hint, forcing the next query back to a greedy seed descent.</summary>
        public void ForgetWarmStart()
        {
            WarmShellA = null;
            WarmShellB = null;
            WarmFeatureA = -1;
            WarmFeatureB = -1;
        }

        internal void ClearHeap() => HeapCount = 0;

        internal void Push(float bound, int a, int b)
        {
            if (HeapCount == HeapBound.Length) Grow();

            int i = HeapCount++;
            HeapBound[i] = bound;
            HeapA[i] = a;
            HeapB[i] = b;

            while (i > 0)
            {
                int parent = (i - 1) >> 1;
                if (HeapBound[parent] <= HeapBound[i]) break;
                Swap(parent, i);
                i = parent;
            }
        }

        internal bool TryPop(out float bound, out int a, out int b)
        {
            bound = 0f;
            a = b = 0;
            if (HeapCount == 0) return false;

            bound = HeapBound[0];
            a = HeapA[0];
            b = HeapB[0];

            HeapCount--;
            if (HeapCount > 0)
            {
                HeapBound[0] = HeapBound[HeapCount];
                HeapA[0] = HeapA[HeapCount];
                HeapB[0] = HeapB[HeapCount];

                int i = 0;
                while (true)
                {
                    int left = 2 * i + 1;
                    if (left >= HeapCount) break;
                    int right = left + 1;
                    int smallest = right < HeapCount && HeapBound[right] < HeapBound[left] ? right : left;
                    if (HeapBound[i] <= HeapBound[smallest]) break;
                    Swap(i, smallest);
                    i = smallest;
                }
            }

            return true;
        }

        private void Swap(int x, int y)
        {
            float fb = HeapBound[x]; HeapBound[x] = HeapBound[y]; HeapBound[y] = fb;
            int ia = HeapA[x]; HeapA[x] = HeapA[y]; HeapA[y] = ia;
            int ib = HeapB[x]; HeapB[x] = HeapB[y]; HeapB[y] = ib;
        }

        private void Grow()
        {
            int n = HeapBound.Length * 2;
            var nb = new float[n];
            var na = new int[n];
            var nc = new int[n];
            System.Array.Copy(HeapBound, nb, HeapCount);
            System.Array.Copy(HeapA, na, HeapCount);
            System.Array.Copy(HeapB, nc, HeapCount);
            HeapBound = nb;
            HeapA = na;
            HeapB = nc;
        }
    }

    /// <summary>
    /// Continuous first-contact search between two authored blade shells, by conservative advancement
    /// over their real surface and edge pieces.
    /// </summary>
    /// <remarks>
    /// Unity's sweep-based continuous collision detection cannot perform angular sweeps, so a shell that
    /// rotates through another between two simulation steps can exchange sides while both endpoint poses
    /// are separated. Conservative advancement is angular-capable: each iterate measures the true
    /// separation between authored surfaces and advances time by only what an upper bound on the closure
    /// rate permits, so no contact earlier than the returned time of impact can exist along the path.
    ///
    /// The hierarchy is an acceleration structure: it changes which pairs are examined, never how a pair
    /// is measured or classified. Traversal is nearest-first against a finite seed bound, so it stops as
    /// soon as the best remaining node pair cannot beat what has already been found.
    ///
    /// This type reads poses and returns a time of impact. It never writes a pose back onto a body.
    /// </remarks>
    public static class BladeShellSweep
    {
        /// <summary>
        /// Separation band within which two candidate feature pairs count as tied. An edge line lies on
        /// the boundary of its adjoining surfaces, so the two genuinely coincide; the tie is broken toward
        /// the edge because it is the more specific description of the same contact.
        /// </summary>
        /// <remarks>
        ///     <b>Public so a measurement run can record it, and a COMPILE-TIME constant, not a runtime
        ///     parameter.</b> It cannot be varied from the inspector or by a runner, so any pass that
        ///     characterises the classifier's sensitivity to it requires a code change and its own build
        ///     identity. Preflight records this value beside the module version so a row can never be
        ///     attributed to the wrong tie band.
        /// </remarks>
        public const float SpecificityTieBand = 1e-6f;

        /// <summary>How often the wall-clock budget is checked, in node-pair pops.</summary>
        private const int TimeCheckInterval = 512;

        /// <summary>
        /// Rejects a shell pair that cannot reach contact during the requested motion, before any
        /// hierarchy work.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is a SWEPT test, not a current-pose overlap test. It compares a conservative lower bound
        /// on the shells' separation at the START pose against everything the step's motion is able to
        /// close:
        /// </para>
        /// <code>
        /// reject when   separation(start)  &gt;   |relative translation|
        ///                                    + angleA x extentA
        ///                                    + angleB x extentB
        ///                                    + contact margin
        /// </code>
        /// <para>
        /// Each term is an upper bound on its own contribution, so their sum is an upper bound on the
        /// total closing the step can achieve, and a pair rejected here provably cannot reach contact at
        /// any time during the step. Translation enters as the RELATIVE displacement because a common
        /// translation closes nothing. Rotation enters per shell as angle times
        /// <see cref="BladeShellData.LocalExtent"/>: the pose interpolation rotates each shell about its
        /// own origin, and a point at distance r from that origin traverses an arc of at most
        /// <c>angle x r</c>, whose chord is shorter still.
        /// </para>
        /// <para>
        /// The separation term is the root node's oriented box bound, not a bounding sphere. A sphere
        /// around a 1 m blade has a ~0.54 m radius in every direction, so two swords 40 cm apart have
        /// overlapping spheres and every such pair became a candidate — which is exactly the cost this
        /// gate exists to avoid. The root box is the same bound the traversal already uses, evaluated once.
        /// </para>
        /// </remarks>
        public static bool CanSkipPair(
            BladeShellData shellA, in BladePose startA, in BladePose endA,
            BladeShellData shellB, in BladePose startB, in BladePose endB,
            float margin, BladeContactStats stats = null)
        {
            if (stats != null) stats.BroadPhaseTests++;

            BladeObbBasis basis = BladeObbBasis.Build(startA, startB);
            float separation = NodeBound(
                basis, startA, shellA.Bvh.GetNode(0), startB, shellB.Bvh.GetNode(0));

            float relativeTranslation =
                ((endA.Position - startA.Position) - (endB.Position - startB.Position)).magnitude;

            float rotationalA = BladePose.AngleRadians(startA, endA) * shellA.LocalExtent;
            float rotationalB = BladePose.AngleRadians(startB, endB) * shellB.LocalExtent;

            float reach = relativeTranslation + rotationalA + rotationalB + margin;
            bool skip = separation > reach;

            if (skip && stats != null) stats.BroadPhaseRejections++;
            return skip;
        }

        public static BladeShellContact FindFirstContact(
            BladeShellData shellA, in BladePose startA, in BladePose endA,
            BladeShellData shellB, in BladePose startB, in BladePose endB,
            in BladeSweepSettings settings,
            BladeShellScratch scratch = null, BladeContactStats stats = null)
        {
            scratch = scratch ?? new BladeShellScratch();

            Stopwatch clock = null;
            if (stats != null || settings.DiagnosticTimeBudgetMs > 0f) clock = Stopwatch.StartNew();

            // Swept broad phase first: a pair that cannot reach contact this step never enters traversal.
            if (CanSkipPair(shellA, startA, endA, shellB, startB, endB, settings.ContactMargin, stats))
            {
                Finish(stats, clock);
                return new BladeShellContact(BladeContactStatus.NoContact, 1f, BladeFeaturePair.None, 0);
            }

            float angleA = BladePose.AngleRadians(startA, endA);
            float angleB = BladePose.AngleRadians(startB, endB);
            Vector3 relativeTranslation = (endA.Position - startA.Position) - (endB.Position - startB.Position);

            // Global closure rate: the fastest any point of either shell can close, per unit of normalized
            // time. Retained in full as the fallback whenever the localized bound is unavailable or weaker.
            float translationRate = relativeTranslation.magnitude;
            float closureBound =
                translationRate +
                angleA * shellA.LocalExtent +
                angleB * shellB.LocalExtent;

            float time = 0f;
            BladeFeaturePair pair = BladeFeaturePair.None;

            for (int iteration = 1; iteration <= settings.MaxIterations; iteration++)
            {
                if (stats != null) stats.Iterations++;

                BladePose poseA = BladePose.Interpolate(startA, endA, time);
                BladePose poseB = BladePose.Interpolate(startB, endB, time);

                if (closureBound <= settings.MinimumClosureRate)
                {
                    Finish(stats, clock);
                    return new BladeShellContact(BladeContactStatus.NoContact, 1f, pair, iteration);
                }

                // Same substitution as the gate: the root box bound, not a bounding sphere. This is a
                // lower bound on separation, which is all conservative advancement needs to take a safe
                // step; a tighter one merely permits a larger step.
                BladeObbBasis coarseBasis = BladeObbBasis.Build(poseA, poseB);
                float coarse = NodeBound(
                    coarseBasis, poseA, shellA.Bvh.GetNode(0), poseB, shellB.Bvh.GetNode(0));

                if (coarse > settings.CoarseRefinementBand)
                {
                    if (stats != null) stats.CoarseIterations++;
                    time += SafeAdvance(
                        coarse, shellA, poseA, angleA, shellB, poseB, angleB,
                        translationRate, closureBound, settings.ContactMargin);
                    if (time >= 1f)
                    {
                        Finish(stats, clock);
                        return new BladeShellContact(BladeContactStatus.NoContact, 1f, pair, iteration);
                    }

                    continue;
                }

                if (stats != null) stats.ExactQueries++;

                if (!TryClosestFeaturePair(shellA, poseA, shellB, poseB, settings, scratch, stats, clock, out pair))
                {
                    if (stats != null) stats.BudgetExceeded = true;
                    Finish(stats, clock);
                    return BladeShellContact.Abandoned(iteration);
                }

                float advance = SafeAdvance(
                    pair.Separation, shellA, poseA, angleA, shellB, poseB, angleB,
                    translationRate, closureBound, settings.ContactMargin);

                if (pair.Separation <= settings.ContactMargin || advance < settings.MinimumTimeAdvance)
                {
                    Finish(stats, clock);
                    return new BladeShellContact(BladeContactStatus.Contact, time, pair, iteration);
                }

                time += advance;

                if (time >= 1f)
                {
                    Finish(stats, clock);
                    return new BladeShellContact(BladeContactStatus.NoContact, 1f, pair, iteration);
                }
            }

            Finish(stats, clock);
            return new BladeShellContact(BladeContactStatus.IterationLimit, time, pair, settings.MaxIterations);
        }

        private static void Finish(BladeContactStats stats, Stopwatch clock)
        {
            if (stats != null && clock != null) stats.ElapsedMs = clock.Elapsed.TotalMilliseconds;
        }

        /// <summary>
        /// Largest time step that provably cannot overshoot first contact, given a lower bound on the
        /// shells' current separation.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The global bound divides the separation by the fastest closing rate ANY point of either shell
        /// can achieve, which on a blade is set by the tip: the lever arm is the whole shell's
        /// <see cref="BladeShellData.LocalExtent"/> even when contact is developing near the guard, where
        /// the true rate is a fraction of that. The step is then far shorter than it needs to be, and the
        /// iteration count pays for it.
        /// </para>
        /// <para>
        /// The refinement keeps the same argument but applies it piecewise. Each shell's hierarchy carries
        /// a shallow frontier whose nodes contain every feature exactly once, and each node knows the
        /// greatest distance from the shell origin reached by its own features. For one pair of frontier
        /// nodes (i, j):
        /// </para>
        /// <code>
        /// separation(i,j)  &gt;=  max( boxBound(i,j), globalSeparation )
        /// closingRate(i,j) &lt;=  |relative translation| + angleA x maxRadius(i) + angleB x maxRadius(j)
        /// </code>
        /// <para>
        /// so no feature pair drawn from (i, j) can reach the contact margin before
        /// <c>(separation - margin) / closingRate</c>. Because the two covers together contain every
        /// feature pair, the MINIMUM of that ratio over the cross product bounds first contact over the
        /// whole shell pair. Both inputs are worst cases within their node, so this is an upper bound on
        /// the rate and a lower bound on the separation — never an optimistic estimate of either, and
        /// never a claim that the current witness stays the contact pair. A pair whose closest feature
        /// changes identity mid-sweep is covered, because the bound is quantified over all pairs and not
        /// over the witness.
        /// </para>
        /// <para>
        /// The global result is kept as an explicit floor. The refinement is provably no weaker, since the
        /// root's own rate is the global rate; the floor guards the arithmetic rather than the argument,
        /// and means an unusable cover degrades to exactly the previous behaviour.
        /// </para>
        /// </remarks>
        private static float SafeAdvance(
            float separationLowerBound,
            BladeShellData shellA, in BladePose poseA, float angleA,
            BladeShellData shellB, in BladePose poseB, float angleB,
            float translationRate, float globalRate, float margin)
        {
            float gap = separationLowerBound - margin;
            float global = gap / globalRate;

            // Rotation is what makes the global rate pessimistic. With neither shell turning there is
            // nothing to localize and the cover cannot improve on it.
            if (angleA <= 0f && angleB <= 0f) return global;

            BladeShellBvh bvhA = shellA.Bvh;
            BladeShellBvh bvhB = shellB.Bvh;
            int[] coverA = bvhA.Cover;
            int[] coverB = bvhB.Cover;

            BladeObbBasis basis = BladeObbBasis.Build(poseA, poseB);
            float localized = float.MaxValue;

            for (int i = 0; i < coverA.Length; i++)
            {
                BladeShellBvh.Node na = bvhA.GetNode(coverA[i]);
                float rateA = angleA * na.MaxLocalRadius;

                for (int j = 0; j < coverB.Length; j++)
                {
                    BladeShellBvh.Node nb = bvhB.GetNode(coverB[j]);

                    float rate = translationRate + rateA + angleB * nb.MaxLocalRadius;
                    if (rate <= 0f) continue;

                    // Both are valid lower bounds on this group's separation, so the larger one holds.
                    float separation = NodeBound(basis, poseA, na, poseB, nb);
                    if (separation < separationLowerBound) separation = separationLowerBound;

                    float candidate = (separation - margin) / rate;
                    if (candidate < localized) localized = candidate;
                }
            }

            return localized > global ? localized : global;
        }

        /// <summary>
        /// Closest authored feature pair between two shells at fixed poses, by nearest-first hierarchy
        /// traversal. Returns false when the diagnostic budget was hit, in which case no usable result
        /// exists and the out pair must not be consumed.
        /// </summary>
        public static bool TryClosestFeaturePair(
            BladeShellData shellA, in BladePose poseA,
            BladeShellData shellB, in BladePose poseB,
            in BladeSweepSettings settings,
            BladeShellScratch scratch, BladeContactStats stats, Stopwatch clock,
            out BladeFeaturePair pair)
        {
            return TryClosestFeaturePair(
                shellA, poseA, shellB, poseB, settings, scratch, stats, clock, null, out pair);
        }

        /// <summary>
        /// As <see cref="TryClosestFeaturePair(BladeShellData,in BladePose,BladeShellData,in BladePose,in BladeSweepSettings,BladeShellScratch,BladeContactStats,Stopwatch,out BladeFeaturePair)" />,
        /// additionally recording into <paramref name="trace" /> which candidates were measured, which were
        /// discarded unmeasured, and which rule selected the winner.
        /// </summary>
        /// <remarks>
        /// A null <paramref name="trace" /> is the production path and costs nothing. When non-null the
        /// recording is append-only into a pre-sized buffer and never read back, so it cannot change which
        /// pair is selected. It does consume wall time inside the query, which is checked against
        /// <see cref="BladeSweepSettings.DiagnosticTimeBudgetMs" /> — so a traced query is marginally more
        /// likely to abort than an untraced one, and <see cref="BladeClassificationTrace.Completion" />
        /// records that rather than hiding it.
        /// </remarks>
        public static bool TryClosestFeaturePair(
            BladeShellData shellA, in BladePose poseA,
            BladeShellData shellB, in BladePose poseB,
            in BladeSweepSettings settings,
            BladeShellScratch scratch, BladeContactStats stats, Stopwatch clock,
            BladeClassificationTrace trace,
            out BladeFeaturePair pair)
        {
            BladeShellBvh bvhA = shellA.Bvh;
            BladeShellBvh bvhB = shellB.Bvh;

            float best = float.MaxValue;
            int bestSpecificity = -1;
            BladeFeaturePair bestPair = BladeFeaturePair.None;

            // Both frames are fixed for this query, so the six separating axes and the rotation between
            // them are built once here rather than at every node pair.
            BladeObbBasis basis = BladeObbBasis.Build(poseA, poseB);

            // One pose per shell for this whole query, so the caches are valid for its entire duration.
            // Begun before the seed descent, which already measures leaves.
            scratch.CacheA.Begin(shellA);
            scratch.CacheB.Begin(shellB);

            if (trace != null)
            {
                // Captured BEFORE the warm start consumes them, because TryWarmStart's own bookkeeping
                // overwrites scratch.WarmFeature* at the end of the query.
                trace.WarmFeatureA = scratch.WarmFeatureA;
                trace.WarmFeatureB = scratch.WarmFeatureB;
                trace.SurfaceCountA = shellA.SurfaceCount;
                trace.SurfaceCountB = shellB.SurfaceCount;
                trace.ShellA = shellA;
                trace.ShellB = shellB;
                trace.PoseA = poseA;
                trace.PoseB = poseB;
            }

            // Seed a FINITE bound before traversing. Without it the first descent has nothing to prune
            // against and can expand across the whole hierarchy. The previous query's winner is tried
            // first because consecutive iterates barely move; failing that, descend greedily as before.
            bool warmed = TryWarmStart(
                shellA, poseA, bvhA, shellB, poseB, bvhB, scratch,
                ref best, ref bestSpecificity, ref bestPair, stats, trace);

            if (trace != null) trace.WarmStartUsed = warmed;

            if (!warmed)
            {
                SeedBest(shellA, poseA, bvhA, shellB, poseB, bvhB, scratch, basis,
                    ref best, ref bestSpecificity, ref bestPair, stats, trace);
            }

            int warmFeatureA = scratch.WarmFeatureA;
            int warmFeatureB = scratch.WarmFeatureB;

            scratch.ClearHeap();
            scratch.Push(NodeBound(basis, poseA, bvhA.GetNode(0), poseB, bvhB.GetNode(0)), 0, 0);

            int visits = 0;
            float bound;
            int ia, ib;

            while (scratch.TryPop(out bound, out ia, out ib))
            {
                // Nearest-first: a child box is contained in its parent's, so bounds only grow as we
                // descend. Once the best remaining pair cannot beat what we have, nothing else can.
                if (bound >= best) break;

                visits++;
                if (stats != null) stats.NodePairsVisited++;

                if (settings.MaxNodePairVisits > 0 && visits > settings.MaxNodePairVisits)
                {
                    PublishCacheCounters(scratch, stats);
                    if (trace != null)
                    {
                        trace.NodePairsVisited = visits;
                        trace.Completion = BladeQueryCompletion.AbortedNodeVisits;
                    }

                    pair = BladeFeaturePair.None;
                    return false;
                }

                if (clock != null && settings.DiagnosticTimeBudgetMs > 0f &&
                    visits % TimeCheckInterval == 0 &&
                    clock.Elapsed.TotalMilliseconds > settings.DiagnosticTimeBudgetMs)
                {
                    PublishCacheCounters(scratch, stats);
                    if (trace != null)
                    {
                        trace.NodePairsVisited = visits;
                        trace.Completion = BladeQueryCompletion.AbortedTimeBudget;
                    }

                    pair = BladeFeaturePair.None;
                    return false;
                }

                BladeShellBvh.Node na = bvhA.GetNode(ia);
                BladeShellBvh.Node nb = bvhB.GetNode(ib);

                bool leafA = na.Left < 0;
                bool leafB = nb.Left < 0;

                if (leafA && leafB)
                {
                    if (stats != null) stats.LeafPairsVisited++;
                    TestLeaves(shellA, poseA, bvhA, na, shellB, poseB, bvhB, nb, scratch,
                        ref best, ref bestSpecificity, ref bestPair, stats, trace,
                        BladeCandidateSource.Leaf);
                    continue;
                }

                if (leafB || (!leafA && na.DiagonalSq >= nb.DiagonalSq))
                {
                    PushPair(scratch, basis, poseA, bvhA, na.Left, poseB, bvhB, ib, best);
                    PushPair(scratch, basis, poseA, bvhA, na.Right, poseB, bvhB, ib, best);
                }
                else
                {
                    PushPair(scratch, basis, poseA, bvhA, ia, poseB, bvhB, nb.Left, best);
                    PushPair(scratch, basis, poseA, bvhA, ia, poseB, bvhB, nb.Right, best);
                }
            }

            PublishCacheCounters(scratch, stats);

            if (bestPair.FeatureA.IsValid && bestPair.FeatureB.IsValid)
            {
                int keptA = FeatureSlot(shellA, bestPair.FeatureA);
                int keptB = FeatureSlot(shellB, bestPair.FeatureB);

                bool kept = warmed && keptA == warmFeatureA && keptB == warmFeatureB;
                if (stats != null && kept) stats.WarmStartsKept++;

                // The sweep's own slot arithmetic, so a consumer never has to reconstruct it from a ref
                // index — which would be wrong for edges, whose slots are offset by the surface count.
                if (trace != null) trace.WarmStartKept = kept;

                scratch.WarmShellA = shellA;
                scratch.WarmShellB = shellB;
                scratch.WarmFeatureA = keptA;
                scratch.WarmFeatureB = keptB;
            }

            if (trace != null)
            {
                trace.NodePairsVisited = visits;
                trace.Final = bestPair;
                trace.FinalBestScalar = best;
                trace.FinalSpecificity = bestSpecificity;

                // Written here and nowhere else, so only a query that reached this line reads as completed.
                trace.Completion = BladeQueryCompletion.Completed;
            }

            pair = bestPair;
            return true;
        }

        /// <summary>Hierarchy feature index of a resolved feature: surfaces first, then edges.</summary>
        private static int FeatureSlot(BladeShellData shell, BladeFeatureRef feature) =>
            feature.Kind == BladeFeatureKind.Surface ? feature.Index : shell.SurfaceCount + feature.Index;

        /// <summary>
        /// Re-measures the previous query's winning pair at the current poses, purely to open with a finite
        /// pruning bound.
        /// </summary>
        /// <remarks>
        /// The value seeded here is a genuine measured separation of a genuine feature pair, which is the
        /// same kind of value <see cref="SeedBest"/> produces and the same kind the traversal compares
        /// against. Nothing downstream treats it as the answer: the heap is still seeded from the root, the
        /// nearest-first traversal still runs in full, and <see cref="Consider"/> replaces this pair the
        /// moment anything closer appears. Its only effect is that pairs which cannot beat it are skipped
        /// sooner. Returns false when there is no hint, or it belongs to different shells.
        /// </remarks>
        private static bool TryWarmStart(
            BladeShellData shellA, in BladePose poseA, BladeShellBvh bvhA,
            BladeShellData shellB, in BladePose poseB, BladeShellBvh bvhB,
            BladeShellScratch scratch,
            ref float best, ref int bestSpecificity, ref BladeFeaturePair bestPair, BladeContactStats stats,
            BladeClassificationTrace trace)
        {
            if (scratch.WarmFeatureA < 0 || scratch.WarmFeatureB < 0) return false;
            if (!ReferenceEquals(scratch.WarmShellA, shellA)) return false;
            if (!ReferenceEquals(scratch.WarmShellB, shellB)) return false;

            int featureA = scratch.WarmFeatureA;
            int featureB = scratch.WarmFeatureB;
            if (featureA >= shellA.SurfaceCount + shellA.EdgeCount) return false;
            if (featureB >= shellB.SurfaceCount + shellB.EdgeCount) return false;

            Vector3 a0, a1, a2, centreA;
            float radiusA;
            bool aIsSurface;
            int indexA;
            scratch.CacheA.Fetch(shellA, bvhA, poseA, featureA,
                out a0, out a1, out a2, out centreA, out radiusA, out aIsSurface, out indexA);

            Vector3 b0, b1, b2, centreB;
            float radiusB;
            bool bIsSurface;
            int indexB;
            scratch.CacheB.Fetch(shellB, bvhB, poseB, featureB,
                out b0, out b1, out b2, out centreB, out radiusB, out bIsSurface, out indexB);

            Vector3 wa, wb;
            float d;
            int specificity;

            if (aIsSurface && bIsSurface)
            {
                d = TriangleGeometry.TriangleTriangleDistance(a0, a1, a2, b0, b1, b2, out wa, out wb);
                specificity = 0;
            }
            else if (aIsSurface)
            {
                Vector3 wSeg, wTri;
                d = TriangleGeometry.SegmentTriangleDistance(b0, b1, a0, a1, a2, out wSeg, out wTri);
                wa = wTri;
                wb = wSeg;
                specificity = 1;
            }
            else if (bIsSurface)
            {
                Vector3 wSeg, wTri;
                d = TriangleGeometry.SegmentTriangleDistance(a0, a1, b0, b1, b2, out wSeg, out wTri);
                wa = wSeg;
                wb = wTri;
                specificity = 1;
            }
            else
            {
                d = SegmentGeometry.ClosestPointsBetweenSegments(a0, a1, b0, b1, out wa, out wb);
                specificity = 2;
            }

            if (stats != null)
            {
                stats.ExactFeatureTests++;
                stats.WarmStarts++;
            }

            BladeFeatureRef refA = new BladeFeatureRef(
                aIsSurface ? BladeFeatureKind.Surface : BladeFeatureKind.Edge, indexA);
            BladeFeatureRef refB = new BladeFeatureRef(
                bIsSurface ? BladeFeatureKind.Surface : BladeFeatureKind.Edge, indexB);

            Consider(d, specificity, refA, refB, wa, wb, ref best, ref bestSpecificity, ref bestPair,
                trace, BladeCandidateSource.WarmStart);
            return true;
        }

        /// <summary>Rolls this query's cache occupancy into the sweep's running totals.</summary>
        private static void PublishCacheCounters(BladeShellScratch scratch, BladeContactStats stats)
        {
            if (stats == null) return;

            stats.FeatureTransforms += scratch.CacheA.Fills + scratch.CacheB.Fills;
            stats.FeatureCacheHits += scratch.CacheA.Hits + scratch.CacheB.Hits;
            scratch.CacheA.Fills = scratch.CacheA.Hits = 0;
            scratch.CacheB.Fills = scratch.CacheB.Hits = 0;
        }

        /// <summary>
        /// Convenience form of <see cref="TryClosestFeaturePair"/> for tests and one-off queries.
        /// </summary>
        /// <remarks>
        /// If the diagnostic budget is hit this returns <see cref="BladeFeaturePair.None"/>, whose
        /// separation is <c>float.MaxValue</c>. That reads as "nothing found", never as a contact, so an
        /// abandoned query cannot be mistaken for a valid measurement.
        /// </remarks>
        public static BladeFeaturePair ClosestFeaturePair(
            BladeShellData shellA, in BladePose poseA,
            BladeShellData shellB, in BladePose poseB,
            BladeShellScratch scratch = null, BladeContactStats stats = null)
        {
            scratch = scratch ?? new BladeShellScratch();
            BladeFeaturePair pair;
            TryClosestFeaturePair(
                shellA, poseA, shellB, poseB, BladeSweepSettings.Default, scratch, stats, null, out pair);
            return pair;
        }

        private static void PushPair(
            BladeShellScratch scratch, in BladeObbBasis basis,
            in BladePose poseA, BladeShellBvh bvhA, int ia,
            in BladePose poseB, BladeShellBvh bvhB, int ib, float best)
        {
            float b = NodeBound(basis, poseA, bvhA.GetNode(ia), poseB, bvhB.GetNode(ib));
            if (b < best) scratch.Push(b, ia, ib);
        }

        private static float NodeBound(
            in BladeObbBasis basis,
            in BladePose poseA, in BladeShellBvh.Node na, in BladePose poseB, in BladeShellBvh.Node nb)
        {
            Vector3 delta = poseB.TransformPoint(nb.Centre) - poseA.TransformPoint(na.Centre);
            return basis.Separation(delta, na.HalfExtents, nb.HalfExtents);
        }

        /// <summary>
        /// Descends greedily to one leaf pair and measures it, purely to obtain a finite starting bound.
        /// </summary>
        private static void SeedBest(
            BladeShellData shellA, in BladePose poseA, BladeShellBvh bvhA,
            BladeShellData shellB, in BladePose poseB, BladeShellBvh bvhB, BladeShellScratch scratch,
            in BladeObbBasis basis,
            ref float best, ref int bestSpecificity, ref BladeFeaturePair bestPair, BladeContactStats stats,
            BladeClassificationTrace trace)
        {
            int ia = 0, ib = 0;
            for (int guard = 0; guard < 256; guard++)
            {
                BladeShellBvh.Node na = bvhA.GetNode(ia);
                BladeShellBvh.Node nb = bvhB.GetNode(ib);
                bool leafA = na.Left < 0;
                bool leafB = nb.Left < 0;

                if (leafA && leafB)
                {
                    if (stats != null)
                    {
                        stats.NodePairsVisited++;
                        stats.LeafPairsVisited++;
                    }

                    TestLeaves(shellA, poseA, bvhA, na, shellB, poseB, bvhB, nb, scratch,
                        ref best, ref bestSpecificity, ref bestPair, stats, trace,
                        BladeCandidateSource.Seed);
                    return;
                }

                if (leafB || (!leafA && na.DiagonalSq >= nb.DiagonalSq))
                {
                    float l = NodeBound(basis, poseA, bvhA.GetNode(na.Left), poseB, nb);
                    float r = NodeBound(basis, poseA, bvhA.GetNode(na.Right), poseB, nb);
                    ia = l <= r ? na.Left : na.Right;
                }
                else
                {
                    float l = NodeBound(basis, poseA, na, poseB, bvhB.GetNode(nb.Left));
                    float r = NodeBound(basis, poseA, na, poseB, bvhB.GetNode(nb.Right));
                    ib = l <= r ? nb.Left : nb.Right;
                }
            }
        }

        /// <summary>Exact narrow phase over two leaves. Identical measurement to the pre-hierarchy solver.</summary>
        /// <remarks>
        /// Two things here are cost-only and cannot move the answer. Feature vertices come from the
        /// per-query pose cache, so a feature taking part in many leaf pairs is transformed once rather
        /// than once per participation. And each candidate pair is first rejected by its own bounding
        /// spheres: <c>|ca - cb| - ra - rb</c> is a lower bound on the pair's true separation, so a pair
        /// failing it cannot beat <c>best</c> and its exact measurement would be discarded anyway. The cull
        /// keeps the specificity tie band, so a pair that ties the best and is more specific still reaches
        /// <see cref="Consider"/> and can still win the tie-break.
        /// </remarks>
        private static void TestLeaves(
            BladeShellData shellA, in BladePose poseA, BladeShellBvh bvhA, in BladeShellBvh.Node na,
            BladeShellData shellB, in BladePose poseB, BladeShellBvh bvhB, in BladeShellBvh.Node nb,
            BladeShellScratch scratch,
            ref float best, ref int bestSpecificity, ref BladeFeaturePair bestPair, BladeContactStats stats,
            BladeClassificationTrace trace, BladeCandidateSource source)
        {
            bool timed = stats != null;
            long mark;

            for (int x = na.Start; x < na.Start + na.Count; x++)
            {
                Vector3 a0, a1, a2, centreA;
                float radiusA;
                bool aIsSurface;
                int indexA;

                mark = timed ? Stopwatch.GetTimestamp() : 0L;
                scratch.CacheA.Fetch(shellA, bvhA, poseA, bvhA.FeatureAt(x),
                    out a0, out a1, out a2, out centreA, out radiusA, out aIsSurface, out indexA);
                if (timed) stats.TransformMs += Ticks(mark);

                BladeFeatureRef refA = new BladeFeatureRef(
                    aIsSurface ? BladeFeatureKind.Surface : BladeFeatureKind.Edge, indexA);

                for (int y = nb.Start; y < nb.Start + nb.Count; y++)
                {
                    Vector3 b0, b1, b2, centreB;
                    float radiusB;
                    bool bIsSurface;
                    int indexB;

                    mark = timed ? Stopwatch.GetTimestamp() : 0L;
                    scratch.CacheB.Fetch(shellB, bvhB, poseB, bvhB.FeatureAt(y),
                        out b0, out b1, out b2, out centreB, out radiusB, out bIsSurface, out indexB);
                    if (timed) stats.TransformMs += Ticks(mark);

                    // Feature-level sphere reject. Skipped while best is still infinite, where it cannot bite.
                    float reach = radiusA + radiusB + best + SpecificityTieBand;
                    if (reach < float.MaxValue && (centreA - centreB).sqrMagnitude > reach * reach)
                    {
                        if (timed) stats.FeaturePairsCulled++;

                        // Recorded so a candidate's absence from the trace means "pruned at the HIERARCHY,
                        // never reached this leaf" and nothing else. The distance is the sphere-gap lower
                        // bound the cull actually tested, not a measurement of the pair.
                        if (trace != null)
                        {
                            trace.Add(
                                source, BladeCandidateOutcome.SphereCulled,
                                refA,
                                new BladeFeatureRef(
                                    bIsSurface ? BladeFeatureKind.Surface : BladeFeatureKind.Edge, indexB),
                                (centreA - centreB).magnitude - radiusA - radiusB, false, -1,
                                best, bestSpecificity, Vector3.zero, Vector3.zero);
                        }

                        continue;
                    }

                    BladeFeatureRef refB = new BladeFeatureRef(
                        bIsSurface ? BladeFeatureKind.Surface : BladeFeatureKind.Edge, indexB);

                    if (timed)
                    {
                        stats.ExactFeatureTests++;
                        Classify(shellA.TypeOf(refA), shellB.TypeOf(refB), stats);
                    }

                    Vector3 wa, wb;
                    float d;
                    int specificity;

                    mark = timed ? Stopwatch.GetTimestamp() : 0L;

                    if (aIsSurface && bIsSurface)
                    {
                        d = TriangleGeometry.TriangleTriangleDistance(a0, a1, a2, b0, b1, b2, out wa, out wb);
                        specificity = 0;
                    }
                    else if (aIsSurface)
                    {
                        Vector3 wSeg, wTri;
                        d = TriangleGeometry.SegmentTriangleDistance(b0, b1, a0, a1, a2, out wSeg, out wTri);
                        wa = wTri;
                        wb = wSeg;
                        specificity = 1;
                    }
                    else if (bIsSurface)
                    {
                        Vector3 wSeg, wTri;
                        d = TriangleGeometry.SegmentTriangleDistance(a0, a1, b0, b1, b2, out wSeg, out wTri);
                        wa = wSeg;
                        wb = wTri;
                        specificity = 1;
                    }
                    else
                    {
                        d = SegmentGeometry.ClosestPointsBetweenSegments(a0, a1, b0, b1, out wa, out wb);
                        specificity = 2;
                    }

                    if (timed) stats.DistanceMs += Ticks(mark);

                    Consider(d, specificity, refA, refB, wa, wb, ref best, ref bestSpecificity, ref bestPair,
                        trace, source);
                }
            }
        }

        /// <summary>Milliseconds elapsed since a <see cref="Stopwatch"/> timestamp.</summary>
        private static double Ticks(long since) =>
            (Stopwatch.GetTimestamp() - since) * 1000d / Stopwatch.Frequency;

        /// <summary>Buckets one exact test by the authored classification of its two participants.</summary>
        private static void Classify(BladeFeatureType typeA, BladeFeatureType typeB, BladeContactStats stats)
        {
            bool sharpA = typeA == BladeFeatureType.SharpEdge;
            bool sharpB = typeB == BladeFeatureType.SharpEdge;
            bool faceA = typeA == BladeFeatureType.BroadFace || typeA == BladeFeatureType.BevelFace;
            bool faceB = typeB == BladeFeatureType.BroadFace || typeB == BladeFeatureType.BevelFace;

            if (sharpA && sharpB) stats.TestsSharpSharp++;
            else if ((sharpA && faceB) || (sharpB && faceA)) stats.TestsSharpSurface++;
            else if (sharpA || sharpB) stats.TestsSharpBlunt++;
            else if (faceA && faceB) stats.TestsSurfaceSurface++;
            else if (faceA || faceB) stats.TestsSurfaceBlunt++;
            else stats.TestsBluntBlunt++;
        }

        private static void Consider(
            float separation, int specificity,
            BladeFeatureRef featureA, BladeFeatureRef featureB,
            Vector3 witnessA, Vector3 witnessB,
            ref float best, ref int bestSpecificity, ref BladeFeaturePair bestPair,
            BladeClassificationTrace trace, BladeCandidateSource source)
        {
            bool closer = separation < best - SpecificityTieBand;
            bool tiedButMoreSpecific = separation < best + SpecificityTieBand && specificity > bestSpecificity;

            // Recorded against the incumbent AS IT STOOD, before either is updated below, so the trace
            // shows the comparison the rule actually made rather than its aftermath.
            if (trace != null)
            {
                trace.Add(
                    source,
                    closer
                        ? BladeCandidateOutcome.TookAsCloser
                        : tiedButMoreSpecific
                            ? BladeCandidateOutcome.TookAsTiedMoreSpecific
                            : BladeCandidateOutcome.RejectedNotCloser,
                    featureA, featureB, separation, true, specificity,
                    best, bestSpecificity, witnessA, witnessB);
            }

            if (!closer && !tiedButMoreSpecific) return;

            if (separation < best) best = separation;
            bestSpecificity = specificity;
            bestPair = new BladeFeaturePair(featureA, featureB, witnessA, witnessB, separation);
        }
    }
}
