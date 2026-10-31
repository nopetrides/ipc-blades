using UnityEngine;

namespace BladeContact
{
    /// <summary>
    /// The once-per-query setup that lets two shells' node boxes be compared as oriented boxes at
    /// roughly the cost of comparing spheres.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every node box of one shell is axis-aligned in that shell's own local space, so under a rigid pose
    /// every node box of that shell shares one orientation. Two shells therefore contribute only six
    /// distinct axes for an entire query, and the rotation between their frames is fixed for the whole
    /// query. Both are built once here rather than per node pair.
    /// </para>
    /// <para>
    /// The bound is the separating-axis lower bound over those six axes. Projected onto any single axis,
    /// the gap between the two boxes' shadows is a lower bound on their true distance, so the largest such
    /// gap is also a lower bound — never an over-estimate, and therefore never able to prune away a pair
    /// that could have been closer. The nine edge-cross axes are deliberately omitted: including them
    /// would only tighten the bound further, and omitting them costs conservatism, not soundness.
    /// </para>
    /// </remarks>
    internal struct BladeObbBasis
    {
        private Vector3 ax, ay, az;
        private Vector3 bx, by, bz;

        // AbsR[i,j] = |dot(a_i, b_j)|, the absolute rotation from B's frame into A's.
        private float r00, r01, r02;
        private float r10, r11, r12;
        private float r20, r21, r22;

        /// <summary>
        /// Guards the degenerate case of two near-parallel frames, where a projected extent can otherwise
        /// come out fractionally small and make the bound optimistic. Biasing the absolute rotation upward
        /// only ever grows a projected extent, which shrinks the bound and stays conservative.
        /// </summary>
        private const float ParallelBias = 1e-6f;

        internal static BladeObbBasis Build(in BladePose poseA, in BladePose poseB)
        {
            BladeObbBasis basis;

            basis.ax = poseA.Rotation * Vector3.right;
            basis.ay = poseA.Rotation * Vector3.up;
            basis.az = poseA.Rotation * Vector3.forward;

            basis.bx = poseB.Rotation * Vector3.right;
            basis.by = poseB.Rotation * Vector3.up;
            basis.bz = poseB.Rotation * Vector3.forward;

            basis.r00 = Mathf.Abs(Vector3.Dot(basis.ax, basis.bx)) + ParallelBias;
            basis.r01 = Mathf.Abs(Vector3.Dot(basis.ax, basis.by)) + ParallelBias;
            basis.r02 = Mathf.Abs(Vector3.Dot(basis.ax, basis.bz)) + ParallelBias;
            basis.r10 = Mathf.Abs(Vector3.Dot(basis.ay, basis.bx)) + ParallelBias;
            basis.r11 = Mathf.Abs(Vector3.Dot(basis.ay, basis.by)) + ParallelBias;
            basis.r12 = Mathf.Abs(Vector3.Dot(basis.ay, basis.bz)) + ParallelBias;
            basis.r20 = Mathf.Abs(Vector3.Dot(basis.az, basis.bx)) + ParallelBias;
            basis.r21 = Mathf.Abs(Vector3.Dot(basis.az, basis.by)) + ParallelBias;
            basis.r22 = Mathf.Abs(Vector3.Dot(basis.az, basis.bz)) + ParallelBias;

            return basis;
        }

        /// <summary>
        /// Lower bound on the distance between two node boxes, given the world vector from A's box centre
        /// to B's box centre and each box's half-extents in its own shell's local space. A negative result
        /// means no tested axis separates them, i.e. they may overlap.
        /// </summary>
        internal float Separation(Vector3 delta, Vector3 ea, Vector3 eb)
        {
            // Centre offset expressed in each frame.
            float tax = Vector3.Dot(delta, ax);
            float tay = Vector3.Dot(delta, ay);
            float taz = Vector3.Dot(delta, az);

            float best = Mathf.Abs(tax) - (ea.x + eb.x * r00 + eb.y * r01 + eb.z * r02);

            float d = Mathf.Abs(tay) - (ea.y + eb.x * r10 + eb.y * r11 + eb.z * r12);
            if (d > best) best = d;

            d = Mathf.Abs(taz) - (ea.z + eb.x * r20 + eb.y * r21 + eb.z * r22);
            if (d > best) best = d;

            d = Mathf.Abs(Vector3.Dot(delta, bx)) - (eb.x + ea.x * r00 + ea.y * r10 + ea.z * r20);
            if (d > best) best = d;

            d = Mathf.Abs(Vector3.Dot(delta, by)) - (eb.y + ea.x * r01 + ea.y * r11 + ea.z * r21);
            if (d > best) best = d;

            d = Mathf.Abs(Vector3.Dot(delta, bz)) - (eb.z + ea.x * r02 + ea.y * r12 + ea.z * r22);
            if (d > best) best = d;

            return best;
        }
    }
}
