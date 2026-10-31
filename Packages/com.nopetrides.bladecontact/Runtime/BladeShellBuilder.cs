using System.Collections.Generic;
using UnityEngine;

namespace BladeContact
{
    /// <summary>
    /// Turns an authored <see cref="BladeProfile"/> into the surface pieces and edge lines the solver
    /// tests against.
    /// </summary>
    /// <remarks>
    /// Every surface and edge produced here traces back to an authored cross-section vertex, and every
    /// semantic identity is copied from the profile rather than derived. The one quantity the builder
    /// computes is <see cref="BladeEdgeLine.IncludedAngleDegrees"/>, measured from the two adjoining
    /// facets — reported for a consumer's declared sharpness rule to read, never thresholded here.
    /// </remarks>
    public static class BladeShellBuilder
    {
        public static BladeShellData Build(BladeProfile profile)
        {
            var surfaces = new List<BladeSurface>();
            var edges = new List<BladeEdgeLine>();
            var groups = new List<BladeShellGroup>();

            foreach (BladeProfileSection section in profile.Sections)
                BuildSection(section, surfaces, edges, groups);

            return new BladeShellData(surfaces.ToArray(), edges.ToArray(), groups.ToArray());
        }

        /// <summary>
        /// Bounds one contiguous block of features. Grouping follows the authoring rather than a spatial
        /// split: a station interval is already a compact, contiguous piece of the shell.
        /// </summary>
        private static BladeShellGroup MakeGroup(
            List<BladeSurface> surfaces, List<BladeEdgeLine> edges,
            int surfaceStart, int edgeStart)
        {
            int surfaceCount = surfaces.Count - surfaceStart;
            int edgeCount = edges.Count - edgeStart;

            Vector3 centre = Vector3.zero;
            int n = 0;
            for (int i = surfaceStart; i < surfaces.Count; i++) { centre += surfaces[i].LocalCentre; n++; }
            for (int i = edgeStart; i < edges.Count; i++) { centre += edges[i].LocalCentre; n++; }
            if (n > 0) centre /= n;

            float radius = 0f;
            for (int i = surfaceStart; i < surfaces.Count; i++)
                radius = Mathf.Max(radius, (surfaces[i].LocalCentre - centre).magnitude + surfaces[i].LocalBoundingRadius);
            for (int i = edgeStart; i < edges.Count; i++)
                radius = Mathf.Max(radius, (edges[i].LocalCentre - centre).magnitude + edges[i].LocalBoundingRadius);

            return new BladeShellGroup(surfaceStart, surfaceCount, edgeStart, edgeCount, centre, radius);
        }

        private static void BuildSection(
            BladeProfileSection section, List<BladeSurface> surfaces, List<BladeEdgeLine> edges,
            List<BladeShellGroup> groups)
        {
            BladeProfileStation[] stations = Resample(section);
            if (stations == null || stations.Length < 2) return;

            int ringLength = stations[0].Ring.Length;

            for (int s = 0; s < stations.Length - 1; s++)
            {
                BladeProfileStation near = stations[s];
                BladeProfileStation far = stations[s + 1];

                // Facet i of this station pair occupies surfaces [facetBase + 2i, facetBase + 2i + 1].
                int facetBase = surfaces.Count;
                int groupEdgeStart = edges.Count;

                for (int i = 0; i < ringLength; i++)
                {
                    int next = (i + 1) % ringLength;

                    Vector3 p00 = Local(section, near, i);
                    Vector3 p01 = Local(section, near, next);
                    Vector3 p10 = Local(section, far, i);
                    Vector3 p11 = Local(section, far, next);

                    BladeFeatureType facetType = near.Ring[i].FacetType;
                    string facetId = $"{section.Id}.s{s}.f{i}";

                    // Two triangles rather than one quad: a quad spanning a tapering section is not
                    // planar, and a non-planar "surface" would quietly corrupt the distance queries.
                    surfaces.Add(new BladeSurface(facetId + ".a", facetType, p00, p01, p11));
                    surfaces.Add(new BladeSurface(facetId + ".b", facetType, p00, p11, p10));
                }

                for (int i = 0; i < ringLength; i++)
                {
                    int previousFacet = (i - 1 + ringLength) % ringLength;

                    edges.Add(new BladeEdgeLine(
                        $"{section.Id}.s{s}.e{i}",
                        near.Ring[i].RidgeType,
                        Local(section, near, i), Local(section, far, i),
                        facetBase + 2 * previousFacet,
                        facetBase + 2 * i,
                        IncludedAngleDegrees(near.Ring, i)));
                }

                groups.Add(MakeGroup(surfaces, edges, facetBase, groupEdgeStart));
            }

            if (section.CapStart)
            {
                int capBase = surfaces.Count;
                AddCap(section, stations[0], section.StartCapType, "capStart", surfaces);
                groups.Add(MakeGroup(surfaces, edges, capBase, edges.Count));
            }

            if (section.CapEnd)
            {
                int capBase = surfaces.Count;
                AddCap(section, stations[stations.Length - 1], section.EndCapType, "capEnd", surfaces);
                groups.Add(MakeGroup(surfaces, edges, capBase, edges.Count));
            }
        }

        /// <summary>
        /// Inserts interpolated cross-sections so no interval exceeds the section's maximum spacing.
        /// </summary>
        /// <remarks>
        /// This does not change the shape. Between two authored stations the surface is already the
        /// linear interpolation of their rings, so an inserted station lies exactly on it. What changes
        /// is the size of the resulting surface pieces, and therefore whether their bounding volumes can
        /// reject anything.
        /// </remarks>
        public static BladeProfileStation[] Resample(BladeProfileSection section)
        {
            BladeProfileStation[] authored = section.Stations;
            if (authored == null || authored.Length < 2 || section.MaxStationSpacing <= 0f) return authored;

            var result = new List<BladeProfileStation>(authored.Length);

            for (int s = 0; s < authored.Length - 1; s++)
            {
                BladeProfileStation near = authored[s];
                BladeProfileStation far = authored[s + 1];
                result.Add(near);

                float span = Mathf.Abs(far.Along - near.Along);
                int steps = Mathf.CeilToInt(span / section.MaxStationSpacing);
                for (int k = 1; k < steps; k++)
                    result.Add(Interpolate(near, far, (float)k / steps));
            }

            result.Add(authored[authored.Length - 1]);
            return result.ToArray();
        }

        private static BladeProfileStation Interpolate(BladeProfileStation near, BladeProfileStation far, float t)
        {
            var ring = new BladeProfileVertex[near.Ring.Length];
            for (int i = 0; i < ring.Length; i++)
            {
                BladeProfileVertex a = near.Ring[i];
                BladeProfileVertex b = far.Ring[i];
                ring[i] = new BladeProfileVertex(
                    Mathf.Lerp(a.Across, b.Across, t),
                    Mathf.Lerp(a.Through, b.Through, t),
                    a.RidgeType,
                    a.FacetType);
            }

            return new BladeProfileStation(Mathf.Lerp(near.Along, far.Along, t), ring);
        }

        private static Vector3 Local(BladeProfileSection section, BladeProfileStation station, int index)
        {
            BladeProfileVertex v = station.Ring[index];
            return section.ToLocal(station.Along, v.Across, v.Through);
        }

        /// <summary>
        /// Interior wedge angle at a ring vertex, between the two facets meeting there. A designated
        /// sharp edge reports its authored included angle; a square corner reports 90; a vertex on a flat
        /// run reports 180; a reflex corner reports more than 180.
        /// </summary>
        /// <remarks>
        /// The reflex case is not academic: a crossguard footprint has concave corners, and reporting one
        /// of those as an acute angle would hand a declared sharpness rule a false positive on a feature
        /// that is not an edge at all.
        /// </remarks>
        public static float IncludedAngleDegrees(BladeProfileVertex[] ring, int index)
        {
            int n = ring.Length;
            BladeProfileVertex previous = ring[(index - 1 + n) % n];
            BladeProfileVertex current = ring[index];
            BladeProfileVertex next = ring[(index + 1) % n];

            Vector2 toPrevious = new Vector2(previous.Across - current.Across, previous.Through - current.Through);
            Vector2 toNext = new Vector2(next.Across - current.Across, next.Through - current.Through);

            if (toPrevious.sqrMagnitude <= 1e-18f || toNext.sqrMagnitude <= 1e-18f) return 180f;

            float angle = Vector2.Angle(toPrevious, toNext);

            // Convexity is only meaningful relative to the ring's winding, so resolve that first.
            float area = 0f;
            for (int k = 0; k < n; k++)
            {
                BladeProfileVertex a = ring[k];
                BladeProfileVertex b = ring[(k + 1) % n];
                area += a.Across * b.Through - b.Across * a.Through;
            }

            Vector2 incoming = new Vector2(current.Across - previous.Across, current.Through - previous.Through);
            Vector2 outgoing = new Vector2(next.Across - current.Across, next.Through - current.Through);
            float turn = incoming.x * outgoing.y - incoming.y * outgoing.x;
            if (area < 0f) turn = -turn;

            return turn >= 0f ? angle : 360f - angle;
        }

        /// <summary>
        /// Closes a section with an ear-clipped fan. Ear clipping rather than a triangle fan because a
        /// crossguard footprint is not convex, and a fan across a concave ring produces inverted
        /// triangles that would report contact on the wrong side.
        /// </summary>
        private static void AddCap(
            BladeProfileSection section, BladeProfileStation station, BladeFeatureType capType,
            string label, List<BladeSurface> surfaces)
        {
            int n = station.Ring.Length;
            if (n < 3) return;

            var remaining = new List<int>(n);
            for (int i = 0; i < n; i++) remaining.Add(i);

            if (SignedArea(station.Ring, remaining) < 0f) remaining.Reverse();

            int guard = 0;
            int triangle = 0;
            while (remaining.Count > 3 && guard++ < n * n)
            {
                bool clipped = false;
                for (int k = 0; k < remaining.Count; k++)
                {
                    int i0 = remaining[(k - 1 + remaining.Count) % remaining.Count];
                    int i1 = remaining[k];
                    int i2 = remaining[(k + 1) % remaining.Count];

                    if (!IsEar(station.Ring, remaining, i0, i1, i2)) continue;

                    surfaces.Add(new BladeSurface(
                        $"{section.Id}.{label}.t{triangle++}", capType,
                        Local(section, station, i0), Local(section, station, i1), Local(section, station, i2)));
                    remaining.RemoveAt(k);
                    clipped = true;
                    break;
                }

                if (!clipped) break;
            }

            if (remaining.Count == 3)
                surfaces.Add(new BladeSurface(
                    $"{section.Id}.{label}.t{triangle}", capType,
                    Local(section, station, remaining[0]),
                    Local(section, station, remaining[1]),
                    Local(section, station, remaining[2])));
        }

        private static float SignedArea(BladeProfileVertex[] ring, List<int> indices)
        {
            float area = 0f;
            for (int k = 0; k < indices.Count; k++)
            {
                BladeProfileVertex a = ring[indices[k]];
                BladeProfileVertex b = ring[indices[(k + 1) % indices.Count]];
                area += a.Across * b.Through - b.Across * a.Through;
            }

            return area * 0.5f;
        }

        private static bool IsEar(BladeProfileVertex[] ring, List<int> remaining, int i0, int i1, int i2)
        {
            Vector2 a = new Vector2(ring[i0].Across, ring[i0].Through);
            Vector2 b = new Vector2(ring[i1].Across, ring[i1].Through);
            Vector2 c = new Vector2(ring[i2].Across, ring[i2].Through);

            float cross = (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
            if (cross <= 0f) return false; // reflex vertex

            foreach (int index in remaining)
            {
                if (index == i0 || index == i1 || index == i2) continue;
                Vector2 p = new Vector2(ring[index].Across, ring[index].Through);
                if (PointInTriangle2D(p, a, b, c)) return false;
            }

            return true;
        }

        private static bool PointInTriangle2D(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x);
            float d2 = (c.x - b.x) * (p.y - b.y) - (c.y - b.y) * (p.x - b.x);
            float d3 = (a.x - c.x) * (p.y - c.y) - (a.y - c.y) * (p.x - c.x);
            return d1 >= 0f && d2 >= 0f && d3 >= 0f;
        }
    }
}
