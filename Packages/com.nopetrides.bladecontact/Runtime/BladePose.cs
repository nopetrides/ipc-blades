using System;
using UnityEngine;

namespace BladeContact
{
    /// <summary>
    /// Rigid pose of a shell. The sweep interpolates between a start and an end pose to describe the
    /// requested motion; it does not write poses back onto a <see cref="Rigidbody"/>.
    /// </summary>
    [Serializable]
    public struct BladePose
    {
        [SerializeField] private Vector3 position;
        [SerializeField] private Quaternion rotation;

        public BladePose(Vector3 position, Quaternion rotation)
        {
            this.position = position;
            this.rotation = rotation;
        }

        public Vector3 Position => position;
        public Quaternion Rotation => rotation;

        public static BladePose Identity => new BladePose(Vector3.zero, Quaternion.identity);

        public Vector3 TransformPoint(Vector3 local) => position + rotation * local;

        /// <summary>
        /// Constant-rate interpolation of the requested motion. Rotation uses the shortest-arc slerp,
        /// so angular speed along the path is constant and equal to <see cref="AngleRadians"/>.
        /// </summary>
        public static BladePose Interpolate(in BladePose from, in BladePose to, float t)
        {
            return new BladePose(
                Vector3.Lerp(from.position, to.position, t),
                Quaternion.Slerp(from.rotation, to.rotation, t));
        }

        /// <summary>Shortest-arc angle between two poses' rotations, in radians.</summary>
        public static float AngleRadians(in BladePose from, in BladePose to)
        {
            return Quaternion.Angle(from.rotation, to.rotation) * Mathf.Deg2Rad;
        }
    }
}
