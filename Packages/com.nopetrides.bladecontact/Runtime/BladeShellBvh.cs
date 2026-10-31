using System.Collections.Generic;
using UnityEngine;

namespace BladeContact
{
    /// <summary>
    /// A bounding-volume hierarchy over one shell's authored features, built once in local space.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is an acceleration structure only. It changes which feature pairs are *examined*, never how
    /// a pair is measured or classified, so the exact narrow phase and its result are untouched.
    /// </para>
    /// <para>
    /// Nodes are boxes, axis-aligned in the shell's own local space. Under a rigid pose such a box becomes
    /// an oriented box in the world with no re-fitting and no inflation, so it is exactly as pose-stable
    /// as a sphere while being far tighter on a blade: a node spanning 100 mm of a 5 mm-thick blade gets a
    /// box 100 x 5 x 25 mm, where the enclosing sphere has a ~52 mm radius in every direction. That
    /// looseness is what a sphere hierarchy cannot fix, because the geometry it bounds is not round.
    /// </para>
    /// <para>
    /// A box hierarchy also makes the traversal's early termination sound. A node's box is the union of
    /// its children's boxes, so a child box is always contained in its parent's and the lower bound is
    /// monotone non-decreasing on the way down. That containment does not hold for the enclosing-sphere
    /// construction, where a child sphere fitted about its own centroid can protrude outside its parent.
    /// </para>
    /// <para>
    /// The build is deterministic — a median split on the widest axis of the centroid spread, with ties
    /// broken by feature index — so the same profile always yields the same tree and the same traversal
    /// order.
    /// </para>
    /// </remarks>
    public sealed class BladeShellBvh
    {
        /// <summary>Maximum features held in a leaf before it is split.</summary>
        private const int LeafSize = 4;

        internal struct Node
        {
            /// <summary>Box centre in shell-local space.</summary>
            public Vector3 Centre;

            /// <summary>Half-extents of the local box. Exact over the enclosed features, never inflated.</summary>
            public Vector3 HalfExtents;

            /// <summary>Squared box diagonal, used only to pick which side of a pair to descend.</summary>
            public float DiagonalSq;

            /// <summary>
            /// Greatest distance from the SHELL ORIGIN reached by any vertex of any feature in this node.
            /// Pose interpolation rotates a shell about its own origin, so this is the lever arm that
            /// converts the node's angular travel into linear travel.
            /// </summary>
            public float MaxLocalRadius;

            /// <summary>First child index, or -1 for a leaf.</summary>
            public int Left;

            public int Right;

            /// <summary>Range into <see cref="order"/> for a leaf.</summary>
            public int Start;

            public int Count;
        }

        private readonly Node[] nodes;
        private readonly int[] order;
        private readonly int surfaceCount;
        private readonly int[] cover;

        /// <summary>
        /// Depth of the frontier used as a closure-bound cover. Shallow on purpose: the cover is crossed
        /// pairwise every advancement step, so its cost is quadratic in its size while its benefit is only
        /// as good as how much the blade's radius varies across it.
        /// </summary>
        private const int CoverDepth = 3;

        public int NodeCount => nodes.Length;
        public int LeafSizeLimit => LeafSize;

        /// <summary>
        /// A frontier of nodes that together contain every feature exactly once, so a bound taken as the
        /// minimum over the cross product of two shells' covers is a bound over all feature pairs.
        /// </summary>
        internal int[] Cover => cover;

        /// <summary>Feature index as stored: surfaces first, then edges offset by the surface count.</summary>
        internal int FeatureAt(int slot) => order[slot];

        internal bool IsSurface(int feature) => feature < surfaceCount;

        internal int EdgeIndex(int feature) => feature - surfaceCount;

        internal Node GetNode(int index) => nodes[index];

        public BladeShellBvh(BladeShellData shell)
        {
            surfaceCount = shell.SurfaceCount;
            int total = shell.SurfaceCount + shell.EdgeCount;

            var centres = new Vector3[total];
            var lows = new Vector3[total];
            var highs = new Vector3[total];
            var originRadii = new float[total];

            for (int i = 0; i < shell.SurfaceCount; i++)
            {
                BladeSurface s = shell.GetSurface(i);
                centres[i] = shell.SurfaceCentre(i);
                lows[i] = Vector3.Min(s.LocalA, Vector3.Min(s.LocalB, s.LocalC));
                highs[i] = Vector3.Max(s.LocalA, Vector3.Max(s.LocalB, s.LocalC));
                originRadii[i] = Mathf.Sqrt(Mathf.Max(
                    s.LocalA.sqrMagnitude, Mathf.Max(s.LocalB.sqrMagnitude, s.LocalC.sqrMagnitude)));
            }

            for (int i = 0; i < shell.EdgeCount; i++)
            {
                BladeEdgeLine e = shell.GetEdge(i);
                int f = surfaceCount + i;
                centres[f] = shell.EdgeCentre(i);
                lows[f] = Vector3.Min(e.LocalStart, e.LocalEnd);
                highs[f] = Vector3.Max(e.LocalStart, e.LocalEnd);
                originRadii[f] = Mathf.Sqrt(Mathf.Max(e.LocalStart.sqrMagnitude, e.LocalEnd.sqrMagnitude));
            }

            order = new int[total];
            for (int i = 0; i < total; i++) order[i] = i;

            var built = new List<Node>(Mathf.Max(1, total * 2));
            Build(built, centres, lows, highs, originRadii, 0, total);
            nodes = built.ToArray();
            cover = BuildCover();
        }

        /// <summary>Recursively builds a subtree over order[start..start+count) and returns its node index.</summary>
        private int Build(
            List<Node> built, Vector3[] centres, Vector3[] lows, Vector3[] highs, float[] originRadii,
            int start, int count)
        {
            var node = new Node { Left = -1, Right = -1, Start = start, Count = count };

            // Bound every feature in the range exactly: the union of their own local boxes.
            Vector3 low = Vector3.one * float.MaxValue;
            Vector3 high = Vector3.one * float.MinValue;
            float maxRadius = 0f;
            for (int i = start; i < start + count; i++)
            {
                int f = order[i];
                low = Vector3.Min(low, lows[f]);
                high = Vector3.Max(high, highs[f]);
                maxRadius = Mathf.Max(maxRadius, originRadii[f]);
            }

            node.MaxLocalRadius = maxRadius;

            node.Centre = (low + high) * 0.5f;
            node.HalfExtents = (high - low) * 0.5f;
            node.DiagonalSq = (high - low).sqrMagnitude;

            int index = built.Count;
            built.Add(node);

            if (count <= LeafSize) return index;

            // Widest axis of the centroid spread.
            Vector3 lo = Vector3.one * float.MaxValue;
            Vector3 hi = Vector3.one * float.MinValue;
            for (int i = start; i < start + count; i++)
            {
                Vector3 c = centres[order[i]];
                lo = Vector3.Min(lo, c);
                hi = Vector3.Max(hi, c);
            }

            Vector3 span = hi - lo;
            int axis = span.x >= span.y && span.x >= span.z ? 0 : span.y >= span.z ? 1 : 2;

            // Deterministic: sort by the chosen axis, ties broken by feature index.
            var slice = new int[count];
            System.Array.Copy(order, start, slice, 0, count);
            System.Array.Sort(slice, (a, b) =>
            {
                float ca = centres[a][axis];
                float cb = centres[b][axis];
                int cmp = ca.CompareTo(cb);
                return cmp != 0 ? cmp : a.CompareTo(b);
            });
            System.Array.Copy(slice, 0, order, start, count);

            int half = count / 2;
            int left = Build(built, centres, lows, highs, originRadii, start, half);
            int right = Build(built, centres, lows, highs, originRadii, start + half, count - half);

            Node self = built[index];
            self.Left = left;
            self.Right = right;
            built[index] = self;
            return index;
        }

        /// <summary>
        /// Collects the frontier at <see cref="CoverDepth"/>, stopping early at a leaf. Every feature lies
        /// under exactly one frontier node, which is what makes a minimum over the cover sound.
        /// </summary>
        private int[] BuildCover()
        {
            var frontier = new List<int>(1 << CoverDepth);
            Descend(frontier, 0, 0);
            return frontier.ToArray();
        }

        private void Descend(List<int> frontier, int index, int depth)
        {
            Node node = nodes[index];
            if (depth >= CoverDepth || node.Left < 0)
            {
                frontier.Add(index);
                return;
            }

            Descend(frontier, node.Left, depth + 1);
            Descend(frontier, node.Right, depth + 1);
        }
    }
}
