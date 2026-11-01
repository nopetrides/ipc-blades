using System;
using UnityEngine;

namespace IPCBlades.Gates
{
    /// <summary>Comparison helpers for the gate diagnostics.</summary>
    public static class GateMath
    {
        /// <summary>
        /// Angle in degrees between two rotations, computed in double. Quaternion.Angle returns 0 for anything
        /// below about 0.16 degrees (its dot-product epsilon), which hides the differences the gates look for.
        /// </summary>
        public static double AngleDeg(Quaternion a, Quaternion b)
        {
            double aw = a.w, ax = a.x, ay = a.y, az = a.z, bw = b.w, bx = b.x, by = b.y, bz = b.z;
            double na = Math.Sqrt(aw * aw + ax * ax + ay * ay + az * az), nb = Math.Sqrt(bw * bw + bx * bx + by * by + bz * bz);
            if (na == 0 || nb == 0) return double.NaN;
            aw /= na; ax /= na; ay /= na; az /= na; bw /= nb; bx /= nb; by /= nb; bz /= nb;
            // d = conj(a) * b
            double dw = aw * bw + ax * bx + ay * by + az * bz;
            double dx = aw * bx - ax * bw - ay * bz + az * by;
            double dy = aw * by + ax * bz - ay * bw - az * bx;
            double dz = aw * bz - ax * by + ay * bx - az * bw;
            return 2.0 * Math.Atan2(Math.Sqrt(dx * dx + dy * dy + dz * dz), Math.Abs(dw)) * (180.0 / Math.PI);
        }

        /// <summary>Angle in degrees between two directions, atan2-based in double (Vector3.Angle loses small angles to acos).</summary>
        public static double AngleDeg(Vector3 a, Vector3 b)
        {
            double cx = (double)a.y * b.z - (double)a.z * b.y, cy = (double)a.z * b.x - (double)a.x * b.z, cz = (double)a.x * b.y - (double)a.y * b.x;
            double dot = (double)a.x * b.x + (double)a.y * b.y + (double)a.z * b.z;
            return Math.Atan2(Math.Sqrt(cx * cx + cy * cy + cz * cz), dot) * (180.0 / Math.PI);
        }

        public static Quaternion Q(float[] q) => new Quaternion(q[0], q[1], q[2], q[3]);
        public static Vector3 V(float[] v) => new Vector3(v[0], v[1], v[2]);
    }
}
