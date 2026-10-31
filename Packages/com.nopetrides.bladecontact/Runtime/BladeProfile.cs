using System;
using UnityEngine;

namespace BladeContact
{
    /// <summary>Local axis a profile section sweeps along.</summary>
    public enum BladeSweepAxis : byte
    {
        X,
        Y,
        Z
    }

    /// <summary>
    /// One outline vertex of a cross-section ring, in the section's two cross-axes.
    /// </summary>
    /// <remarks>
    /// Each vertex carries two authored identities, so nothing about a shell's semantics is inferred
    /// from geometry: <see cref="RidgeType"/> names the longitudinal line running through this vertex,
    /// and <see cref="FacetType"/> names the surface leaving it toward the next vertex in the ring.
    /// </remarks>
    [Serializable]
    public struct BladeProfileVertex
    {
        [Tooltip("First cross-axis coordinate. For a section sweeping along X this is local Z (across the blade's width).")]
        public float Across;

        [Tooltip("Second cross-axis coordinate. For a section sweeping along X this is local Y (through the blade's thickness).")]
        public float Through;

        [Tooltip("Authored identity of the longitudinal line at this vertex.")]
        public BladeFeatureType RidgeType;

        [Tooltip("Authored identity of the surface running from this vertex to the next.")]
        public BladeFeatureType FacetType;

        public BladeProfileVertex(float across, float through, BladeFeatureType ridgeType, BladeFeatureType facetType)
        {
            Across = across;
            Through = through;
            RidgeType = ridgeType;
            FacetType = facetType;
        }
    }

    /// <summary>One authored cross-section: a closed outline ring at a position along the sweep axis.</summary>
    [Serializable]
    public sealed class BladeProfileStation
    {
        public float Along;
        public BladeProfileVertex[] Ring;

        public BladeProfileStation(float along, BladeProfileVertex[] ring)
        {
            Along = along;
            Ring = ring;
        }
    }

    /// <summary>
    /// A swept part of a shell: an ordered set of cross-sections along one local axis. A blade sweeps
    /// along its length; a crossguard is a prism sweeping through its thickness. Both are the same shape.
    /// </summary>
    [Serializable]
    public sealed class BladeProfileSection
    {
        public string Id = "section";
        public BladeSweepAxis Axis = BladeSweepAxis.X;
        public BladeProfileStation[] Stations = new BladeProfileStation[0];

        [Tooltip("Close the section at its first station, and the authored identity of that cap.")]
        public bool CapStart = true;
        public BladeFeatureType StartCapType = BladeFeatureType.Unresolved;

        [Tooltip("Close the section at its last station, and the authored identity of that cap.")]
        public bool CapEnd = true;
        public BladeFeatureType EndCapType = BladeFeatureType.Tip;

        [Tooltip("Largest gap between built cross-sections, in metres. Zero keeps only the authored " +
                 "stations. This is a tessellation setting, not a shape change: inserted stations are " +
                 "linear interpolations of the authored ones and lie exactly on the same surface. It " +
                 "exists because a metre-long blade authored with five stations produces half-metre " +
                 "surface pieces whose bounding volumes overlap everywhere, defeating rejection and " +
                 "leaving every query to test the whole shell against the whole shell.")]
        public float MaxStationSpacing = 0.05f;

        /// <summary>Maps a section-local (along, across, through) triple into shell-local space.</summary>
        public Vector3 ToLocal(float along, float across, float through)
        {
            switch (Axis)
            {
                case BladeSweepAxis.X: return new Vector3(along, through, across);
                case BladeSweepAxis.Y: return new Vector3(across, along, through);
                default: return new Vector3(through, across, along);
            }
        }
    }

    /// <summary>
    /// Authored shell geometry for one weapon, as swept sections. The package defines the representation;
    /// the specimen's actual dimensions are supplied by the consumer.
    /// </summary>
    [Serializable]
    public sealed class BladeProfile
    {
        public BladeProfileSection[] Sections = new BladeProfileSection[0];

        public BladeProfile() { }

        public BladeProfile(params BladeProfileSection[] sections)
        {
            Sections = sections;
        }
    }

}
