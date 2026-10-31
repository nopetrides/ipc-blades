using UnityEngine;

namespace BladeContact
{
    /// <summary>
    /// World-space vertices of one shell's features at one fixed pose, filled on first touch and reused
    /// for the rest of that query.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A feature reached by hierarchy traversal is normally reached again: the same surface or edge takes
    /// part in many leaf pairs within a single closest-feature query. Transforming its vertices once per
    /// participation is pure repetition, because the pose is fixed for the whole query.
    /// </para>
    /// <para>
    /// The fill is <b>lazy</b>, not eager. Pruning means most of a 3728-feature shell is never touched
    /// near contact, so transforming every feature up front would cost more than it saves — a cache that
    /// only pays for what traversal actually reaches cannot lose to the uncached path. Occupancy is
    /// tracked by a stamp per feature compared against a per-query stamp, so invalidating the whole cache
    /// between queries is a single integer increment and never a clear of the arrays.
    /// </para>
    /// <para>
    /// This is a cost-only structure. It stores the same numbers <see cref="BladePose.TransformPoint"/>
    /// would have produced at the moment of use, so no measurement or classification can change.
    /// </para>
    /// </remarks>
    internal sealed class BladeShellPoseCache
    {
        private Vector3[] v0;
        private Vector3[] v1;
        private Vector3[] v2;
        private Vector3[] centre;
        private float[] radius;
        private bool[] isSurface;
        private int[] index;
        private int[] stamp;

        private int currentStamp;
        private BladeShellData boundShell;

        /// <summary>Features transformed for the current query, i.e. cache misses.</summary>
        internal int Fills;

        /// <summary>Feature lookups served from an already-filled slot.</summary>
        internal int Hits;

        /// <summary>
        /// Rebinds to a shell and invalidates every slot. Reallocates only when the feature count changes,
        /// so a steady pair of shells allocates once for the lifetime of the scratch buffer.
        /// </summary>
        internal void Begin(BladeShellData shell)
        {
            int total = shell.SurfaceCount + shell.EdgeCount;

            if (v0 == null || v0.Length < total)
            {
                v0 = new Vector3[total];
                v1 = new Vector3[total];
                v2 = new Vector3[total];
                centre = new Vector3[total];
                radius = new float[total];
                isSurface = new bool[total];
                index = new int[total];
                stamp = new int[total];
                currentStamp = 0;
            }

            if (!ReferenceEquals(boundShell, shell))
            {
                boundShell = shell;
                currentStamp = 0;
                System.Array.Clear(stamp, 0, stamp.Length);
            }

            currentStamp++;

            // A wrapped stamp would alias stale slots as live. Clearing on wrap keeps that impossible.
            if (currentStamp == int.MaxValue)
            {
                System.Array.Clear(stamp, 0, stamp.Length);
                currentStamp = 1;
            }
        }

        /// <summary>
        /// World-space geometry of feature <paramref name="feature"/> (surfaces first, then edges offset
        /// by the surface count), transforming it only if this query has not already done so.
        /// </summary>
        internal void Fetch(
            BladeShellData shell, BladeShellBvh bvh, in BladePose pose, int feature,
            out Vector3 p0, out Vector3 p1, out Vector3 p2,
            out Vector3 c, out float r, out bool surface, out int featureIndex)
        {
            if (stamp[feature] == currentStamp)
            {
                Hits++;
                p0 = v0[feature];
                p1 = v1[feature];
                p2 = v2[feature];
                c = centre[feature];
                r = radius[feature];
                surface = isSurface[feature];
                featureIndex = index[feature];
                return;
            }

            Fills++;

            bool s = bvh.IsSurface(feature);
            if (s)
            {
                BladeSurface face = shell.GetSurface(feature);
                p0 = pose.TransformPoint(face.LocalA);
                p1 = pose.TransformPoint(face.LocalB);
                p2 = pose.TransformPoint(face.LocalC);
                c = pose.TransformPoint(shell.SurfaceCentre(feature));
                r = shell.SurfaceRadius(feature);
                featureIndex = feature;
            }
            else
            {
                int e = bvh.EdgeIndex(feature);
                BladeEdgeLine line = shell.GetEdge(e);
                p0 = pose.TransformPoint(line.LocalStart);
                p1 = pose.TransformPoint(line.LocalEnd);
                p2 = p0;
                c = pose.TransformPoint(shell.EdgeCentre(e));
                r = shell.EdgeRadius(e);
                featureIndex = e;
            }

            surface = s;

            v0[feature] = p0;
            v1[feature] = p1;
            v2[feature] = p2;
            centre[feature] = c;
            radius[feature] = r;
            isSurface[feature] = s;
            index[feature] = featureIndex;
            stamp[feature] = currentStamp;
        }
    }
}
