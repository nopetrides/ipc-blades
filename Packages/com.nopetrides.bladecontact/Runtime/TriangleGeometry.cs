using UnityEngine;

namespace BladeContact
{
    /// <summary>
    /// Exact closest-approach queries between the triangles and lines that make up an authored blade
    /// shell. No primitive here is inflated: distances are between the real authored surfaces.
    /// </summary>
    public static class TriangleGeometry
    {
        private const float Epsilon = 1e-12f;

        /// <summary>
        /// Distance between segment pq and triangle abc, with the witness point on each. Returns 0 when
        /// the segment pierces the triangle, so a surface passing through another is never missed.
        /// </summary>
        public static float SegmentTriangleDistance(
            Vector3 p, Vector3 q,
            Vector3 a, Vector3 b, Vector3 c,
            out Vector3 witnessSegment, out Vector3 witnessTriangle)
        {
            Vector3 normal = Vector3.Cross(b - a, c - a);
            float normalLengthSq = normal.sqrMagnitude;

            if (normalLengthSq > Epsilon)
            {
                // Does the segment cross the triangle's plane inside the triangle?
                float distP = Vector3.Dot(normal, p - a);
                float distQ = Vector3.Dot(normal, q - a);

                if (distP * distQ <= 0f && !Mathf.Approximately(distP, distQ))
                {
                    float t = distP / (distP - distQ);
                    Vector3 crossing = p + (q - p) * t;
                    if (PointInTriangle(crossing, a, b, c, normal))
                    {
                        witnessSegment = crossing;
                        witnessTriangle = crossing;
                        return 0f;
                    }
                }
            }

            float best = float.MaxValue;
            witnessSegment = p;
            witnessTriangle = a;

            // Segment against each triangle edge.
            ConsiderSegmentPair(p, q, a, b, ref best, ref witnessSegment, ref witnessTriangle);
            ConsiderSegmentPair(p, q, b, c, ref best, ref witnessSegment, ref witnessTriangle);
            ConsiderSegmentPair(p, q, c, a, ref best, ref witnessSegment, ref witnessTriangle);

            // Segment endpoints projecting into the triangle's interior.
            ConsiderPointInTriangle(p, a, b, c, normal, normalLengthSq, ref best, ref witnessSegment, ref witnessTriangle);
            ConsiderPointInTriangle(q, a, b, c, normal, normalLengthSq, ref best, ref witnessSegment, ref witnessTriangle);

            return best;
        }

        /// <summary>Distance between two triangles, with the witness point on each.</summary>
        public static float TriangleTriangleDistance(
            Vector3 a0, Vector3 a1, Vector3 a2,
            Vector3 b0, Vector3 b1, Vector3 b2,
            out Vector3 witnessA, out Vector3 witnessB)
        {
            float best = float.MaxValue;
            witnessA = a0;
            witnessB = b0;

            ConsiderEdgeAgainstTriangle(a0, a1, b0, b1, b2, false, ref best, ref witnessA, ref witnessB);
            ConsiderEdgeAgainstTriangle(a1, a2, b0, b1, b2, false, ref best, ref witnessA, ref witnessB);
            ConsiderEdgeAgainstTriangle(a2, a0, b0, b1, b2, false, ref best, ref witnessA, ref witnessB);

            if (best <= 0f) return 0f;

            ConsiderEdgeAgainstTriangle(b0, b1, a0, a1, a2, true, ref best, ref witnessA, ref witnessB);
            ConsiderEdgeAgainstTriangle(b1, b2, a0, a1, a2, true, ref best, ref witnessA, ref witnessB);
            ConsiderEdgeAgainstTriangle(b2, b0, a0, a1, a2, true, ref best, ref witnessA, ref witnessB);

            return best;
        }

        private static void ConsiderEdgeAgainstTriangle(
            Vector3 p, Vector3 q, Vector3 t0, Vector3 t1, Vector3 t2, bool edgeBelongsToB,
            ref float best, ref Vector3 witnessA, ref Vector3 witnessB)
        {
            Vector3 wSeg, wTri;
            float d = SegmentTriangleDistance(p, q, t0, t1, t2, out wSeg, out wTri);
            if (d >= best) return;

            best = d;
            if (edgeBelongsToB)
            {
                witnessA = wTri;
                witnessB = wSeg;
            }
            else
            {
                witnessA = wSeg;
                witnessB = wTri;
            }
        }

        private static void ConsiderSegmentPair(
            Vector3 p, Vector3 q, Vector3 r, Vector3 s,
            ref float best, ref Vector3 witnessSegment, ref Vector3 witnessTriangle)
        {
            Vector3 w1, w2;
            float d = SegmentGeometry.ClosestPointsBetweenSegments(p, q, r, s, out w1, out w2);
            if (d >= best) return;

            best = d;
            witnessSegment = w1;
            witnessTriangle = w2;
        }

        private static void ConsiderPointInTriangle(
            Vector3 point, Vector3 a, Vector3 b, Vector3 c, Vector3 normal, float normalLengthSq,
            ref float best, ref Vector3 witnessSegment, ref Vector3 witnessTriangle)
        {
            if (normalLengthSq <= Epsilon) return;

            float signedDistance = Vector3.Dot(normal, point - a) / normalLengthSq;
            Vector3 projected = point - normal * signedDistance;
            if (!PointInTriangle(projected, a, b, c, normal)) return;

            float d = (point - projected).magnitude;
            if (d >= best) return;

            best = d;
            witnessSegment = point;
            witnessTriangle = projected;
        }

        /// <summary>Is a point already on the triangle's plane inside the triangle?</summary>
        private static bool PointInTriangle(Vector3 point, Vector3 a, Vector3 b, Vector3 c, Vector3 normal)
        {
            return Vector3.Dot(Vector3.Cross(b - a, point - a), normal) >= 0f
                && Vector3.Dot(Vector3.Cross(c - b, point - b), normal) >= 0f
                && Vector3.Dot(Vector3.Cross(a - c, point - c), normal) >= 0f;
        }
    }
}
