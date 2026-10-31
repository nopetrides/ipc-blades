using System;
using UnityEngine;

namespace BladeContact.Prototype
{
    /// <summary>
    /// Plain-C# snapshot of one shell's authored features. Deliberately free of <see cref="MonoBehaviour"/>
    /// and <see cref="Rigidbody"/> so the sweep can be exercised deterministically in edit-mode tests.
    /// </summary>
    public sealed class BladeShellData
    {
        private readonly BladeFeature[] features;

        public BladeShellData(BladeFeature[] features)
        {
            this.features = features ?? throw new ArgumentNullException(nameof(features));

            float extent = 0f;
            float maxRadius = 0f;
            for (int i = 0; i < features.Length; i++)
            {
                extent = Mathf.Max(extent, features[i].LocalExtent);
                maxRadius = Mathf.Max(maxRadius, features[i].Radius);
            }

            LocalExtent = extent;
            MaxFeatureRadius = maxRadius;
        }

        public int FeatureCount => features.Length;

        public BladeFeature GetFeature(int index) => features[index];

        /// <summary>
        /// Largest spine distance from the shell origin. Multiplied by angular speed, this bounds the
        /// linear speed of any spine point during conservative advancement.
        /// </summary>
        public float LocalExtent { get; }

        public float MaxFeatureRadius { get; }
    }
}
