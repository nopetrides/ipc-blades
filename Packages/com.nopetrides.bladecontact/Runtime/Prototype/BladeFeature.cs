using System;
using UnityEngine;

namespace BladeContact.Prototype
{
    /// <summary>
    /// One authored contact feature on a <see cref="BladeShell"/>: a local-space spine segment with a
    /// thickness radius and a semantic identity. Faces, bevels, edges, and tips share one primitive so
    /// the sweep can treat them uniformly while classification reads the authored <see cref="Type"/>
    /// rather than inferring meaning from a generic contact manifold.
    /// </summary>
    [Serializable]
    public struct BladeFeature
    {
        [SerializeField] private string id;
        [SerializeField] private BladeFeatureType type;
        [SerializeField] private Vector3 localStart;
        [SerializeField] private Vector3 localEnd;
        [SerializeField] private float radius;

        public BladeFeature(string id, BladeFeatureType type, Vector3 localStart, Vector3 localEnd, float radius)
        {
            this.id = id;
            this.type = type;
            this.localStart = localStart;
            this.localEnd = localEnd;
            this.radius = radius;
        }

        /// <summary>Author-supplied identifier, reported in contact traces.</summary>
        public string Id => id;

        /// <summary>Authored semantic identity. Never inferred from geometry.</summary>
        public BladeFeatureType Type => type;

        public Vector3 LocalStart => localStart;
        public Vector3 LocalEnd => localEnd;

        /// <summary>Half-thickness swept around the spine segment.</summary>
        public float Radius => radius;

        /// <summary>
        /// Largest distance from the shell origin reached by this feature's spine. Bounds the linear
        /// speed contributed by the shell's angular motion during conservative advancement.
        /// </summary>
        public float LocalExtent => Mathf.Max(localStart.magnitude, localEnd.magnitude);
    }
}
