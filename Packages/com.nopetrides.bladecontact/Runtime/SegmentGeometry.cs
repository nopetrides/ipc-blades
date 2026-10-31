using UnityEngine;

namespace BladeContact
{
    /// <summary>Closest-approach queries between the spine segments that represent blade features.</summary>
    public static class SegmentGeometry
    {
        private const float Epsilon = 1e-12f;

        /// <summary>
        /// Closest points between segments p1q1 and p2q2. Returns the distance between them and, via
        /// the out parameters, the witness point on each segment.
        /// </summary>
        public static float ClosestPointsBetweenSegments(
            Vector3 p1, Vector3 q1,
            Vector3 p2, Vector3 q2,
            out Vector3 witness1, out Vector3 witness2)
        {
            Vector3 d1 = q1 - p1;
            Vector3 d2 = q2 - p2;
            Vector3 r = p1 - p2;
            float a = Vector3.Dot(d1, d1);
            float e = Vector3.Dot(d2, d2);
            float f = Vector3.Dot(d2, r);

            float s;
            float t;

            if (a <= Epsilon && e <= Epsilon)
            {
                witness1 = p1;
                witness2 = p2;
                return (witness1 - witness2).magnitude;
            }

            if (a <= Epsilon)
            {
                s = 0f;
                t = Mathf.Clamp01(f / e);
            }
            else
            {
                float c = Vector3.Dot(d1, r);
                if (e <= Epsilon)
                {
                    t = 0f;
                    s = Mathf.Clamp01(-c / a);
                }
                else
                {
                    float b = Vector3.Dot(d1, d2);
                    float denom = a * e - b * b;
                    s = denom > Epsilon ? Mathf.Clamp01((b * f - c * e) / denom) : 0f;
                    t = (b * s + f) / e;

                    if (t < 0f)
                    {
                        t = 0f;
                        s = Mathf.Clamp01(-c / a);
                    }
                    else if (t > 1f)
                    {
                        t = 1f;
                        s = Mathf.Clamp01((b - c) / a);
                    }
                }
            }

            witness1 = p1 + d1 * s;
            witness2 = p2 + d2 * t;
            return (witness1 - witness2).magnitude;
        }
    }
}
