using System.Collections.Generic;
using BladeContact;
using UnityEngine;

/// <summary>
///     Independent, profile-derived closest-authored-feature checker. Shares the authored blade DEFINITION
///     with OurSolution and computes the geometric answer by its own calculation.
/// </summary>
/// <remarks>
///     <para>
///         <b>Shared semantics are intentional.</b> Authored identity comes from
///         <see cref="BladeProfileVertex.RidgeType"/> and <see cref="BladeProfileVertex.FacetType"/> — the
///         same source of truth OurSolution uses. This checker independently determines WHICH authored
///         feature is geometrically implicated; it does not reinvent what that feature means.
///     </para>
///     <para>
///         <b>Geometry validates semantics, it does not define them.</b> The cross-section included angle at
///         each ridge is recorded as a sanity check on the authored profile (~25 deg for a designated sharp
///         edge, ~160 deg for broad/flat), and is NEVER used in place of the authored type.
///     </para>
///     <para>
///         <b>Algorithmic independence.</b> Nothing here calls <c>BladeShellSweep</c>, the production
///         tie-break, <c>BladeShellData.TypeOf</c>/<c>IdOf</c>, <c>BladeContactScenarios.Classify</c>, or any
///         solver verdict. Shared: the profile asset, its geometry and semantics, the pose, and
///         <see cref="BladeProfileSection.ToLocal"/>, which is a pure axis mapping. Station resampling uses
///         this checker's OWN declared spacing, independent of the profile's <c>MaxStationSpacing</c>.
///     </para>
///     <para>
///         <b>Containment is handled explicitly, not assumed away.</b> Moving to the profile removes
///         artificial triangulation features; it does NOT remove genuine coincidence, because an authored
///         ridge lies exactly on the boundary of its adjoining facets. When the winning facet's closest point
///         sits on one of its bounding ridges, both candidates are reported with a coincidence flag and the
///         inter-category margin. No lowest-dimensional winner is chosen by fiat.
///     </para>
/// </remarks>
public sealed class RqProfileFeatureChecker
{
    /// <summary>One straight piece of an authored longitudinal ridge.</summary>
    public struct Ridge
    {
        public int Ring, Seg;
        public BladeFeatureType Type;
        public float IncludedAngleDeg;   // sanity only
        public Vector3 A, B;
        public Vector3 Centre; public float Radius;
    }

    /// <summary>One quad cell of an authored facet, between two rings and two resampled stations.</summary>
    public struct Facet
    {
        public int Ring, Seg;
        public BladeFeatureType Type;
        public Vector3 P00, P10, P11, P01;   // ring i / ring i+1 at station s / s+1
        public Vector3 Centre; public float Radius;
    }

    public struct Hit
    {
        public bool Valid;
        public float Dist;
        public bool RidgeA, RidgeB;
        public int RingA, SegA, RingB, SegB;
        public BladeFeatureType TypeA, TypeB;
        public float AngA, AngB;
        public Vector3 PA, PB;
    }

    private readonly List<Ridge> ridgesLocal = new List<Ridge>();
    private readonly List<Facet> facetsLocal = new List<Facet>();
    private Ridge[] ridges = new Ridge[0];
    private Facet[] facets = new Facet[0];

    public int RidgeCount => ridges.Length;
    public int FacetCount => facets.Length;
    public int StationCount { get; private set; }
    public string SectionId { get; private set; } = "";

    /// <summary>
    ///     Builds ridge pieces and facet cells from the authored profile, resampled at this checker's own
    ///     spacing. Inserted stations are linear interpolations of authored ones and lie on the same surface,
    ///     so the spacing changes tessellation only.
    /// </summary>
    public bool Build(BladeProfileAsset asset, float stationSpacing)
    {
        ridgesLocal.Clear(); facetsLocal.Clear();
        StationCount = 0; SectionId = "";
        if (asset == null || asset.Profile == null || asset.Profile.Sections == null) return false;

        for (int si = 0; si < asset.Profile.Sections.Length; si++)
        {
            BladeProfileSection sec = asset.Profile.Sections[si];
            if (sec.Stations == null || sec.Stations.Length < 2) continue;
            SectionId = sec.Id;

            // Own resampling: walk each authored interval and insert linear interpolations.
            var along = new List<float>();
            var rings = new List<BladeProfileVertex[]>();
            for (int k = 0; k < sec.Stations.Length - 1; k++)
            {
                BladeProfileStation s0 = sec.Stations[k], s1 = sec.Stations[k + 1];
                float span = Mathf.Abs(s1.Along - s0.Along);
                int div = Mathf.Max(1, Mathf.CeilToInt(span / Mathf.Max(1e-4f, stationSpacing)));
                for (int d = 0; d < div; d++)
                {
                    float t = d / (float)div;
                    along.Add(Mathf.Lerp(s0.Along, s1.Along, t));
                    rings.Add(Lerp(s0.Ring, s1.Ring, t));
                }
            }
            along.Add(sec.Stations[sec.Stations.Length - 1].Along);
            rings.Add(sec.Stations[sec.Stations.Length - 1].Ring);
            StationCount = along.Count;

            int ringN = rings[0].Length;
            for (int s = 0; s < along.Count - 1; s++)
            {
                BladeProfileVertex[] r0 = rings[s], r1 = rings[s + 1];
                if (r0.Length != ringN || r1.Length != ringN) continue;
                for (int i = 0; i < ringN; i++)
                {
                    int j = (i + 1) % ringN;
                    Vector3 a0 = sec.ToLocal(along[s], r0[i].Across, r0[i].Through);
                    Vector3 a1 = sec.ToLocal(along[s + 1], r1[i].Across, r1[i].Through);
                    Vector3 b0 = sec.ToLocal(along[s], r0[j].Across, r0[j].Through);
                    Vector3 b1 = sec.ToLocal(along[s + 1], r1[j].Across, r1[j].Through);

                    ridgesLocal.Add(new Ridge
                    {
                        Ring = i, Seg = s, Type = r0[i].RidgeType,
                        IncludedAngleDeg = IncludedAngle(r0, i),
                        A = a0, B = a1
                    });

                    facetsLocal.Add(new Facet
                    {
                        Ring = i, Seg = s, Type = r0[i].FacetType,
                        P00 = a0, P10 = a1, P11 = b1, P01 = b0
                    });
                }
            }
            break; // one section is the blade; further sections are other parts
        }

        ridges = ridgesLocal.ToArray();
        facets = facetsLocal.ToArray();
        return ridges.Length > 0;
    }

    private static BladeProfileVertex[] Lerp(BladeProfileVertex[] a, BladeProfileVertex[] b, float t)
    {
        int n = Mathf.Min(a.Length, b.Length);
        var outv = new BladeProfileVertex[n];
        for (int i = 0; i < n; i++)
            outv[i] = new BladeProfileVertex(
                Mathf.Lerp(a[i].Across, b[i].Across, t),
                Mathf.Lerp(a[i].Through, b[i].Through, t),
                a[i].RidgeType, a[i].FacetType);
        return outv;
    }

    /// <summary>
    ///     Cross-section included angle at one ring vertex. Sanity metric on the authored profile only —
    ///     around 25 degrees at a designated sharp edge, around 160 at a broad side. Never classification.
    /// </summary>
    private static float IncludedAngle(BladeProfileVertex[] ring, int i)
    {
        int n = ring.Length;
        int prev = (i - 1 + n) % n, next = (i + 1) % n;
        Vector2 p = new Vector2(ring[prev].Across, ring[prev].Through);
        Vector2 c = new Vector2(ring[i].Across, ring[i].Through);
        Vector2 q = new Vector2(ring[next].Across, ring[next].Through);
        return Vector2.Angle(p - c, q - c);
    }

    /// <summary>Transforms the built features into world space for the current pose.</summary>
    public void ToWorld(Matrix4x4 m, Ridge[] rOut, Facet[] fOut)
    {
        for (int i = 0; i < ridges.Length; i++)
        {
            Ridge r = ridgesLocal[i];
            r.A = m.MultiplyPoint3x4(r.A); r.B = m.MultiplyPoint3x4(r.B);
            r.Centre = (r.A + r.B) * 0.5f; r.Radius = Vector3.Distance(r.A, r.B) * 0.5f;
            rOut[i] = r;
        }
        for (int i = 0; i < facets.Length; i++)
        {
            Facet f = facetsLocal[i];
            f.P00 = m.MultiplyPoint3x4(f.P00); f.P10 = m.MultiplyPoint3x4(f.P10);
            f.P11 = m.MultiplyPoint3x4(f.P11); f.P01 = m.MultiplyPoint3x4(f.P01);
            f.Centre = (f.P00 + f.P10 + f.P11 + f.P01) * 0.25f;
            f.Radius = Mathf.Max(
                Mathf.Max(Vector3.Distance(f.Centre, f.P00), Vector3.Distance(f.Centre, f.P10)),
                Mathf.Max(Vector3.Distance(f.Centre, f.P11), Vector3.Distance(f.Centre, f.P01)));
            fOut[i] = f;
        }
    }

    public Ridge[] NewRidgeBuffer() => new Ridge[ridges.Length];
    public Facet[] NewFacetBuffer() => new Facet[facets.Length];

    /// <summary>
    ///     Best ridge-ridge, ridge-facet and facet-ridge pair between two posed blades. Bounding-sphere
    ///     pruning only; it cannot move the exact minimum because the bound is conservative.
    /// </summary>
    public static void Closest(
        Ridge[] rA, Facet[] fA, Ridge[] rB, Facet[] fB,
        out Hit rr, out Hit rf, out Hit fr)
    {
        rr = Blank(); rf = Blank(); fr = Blank();

        for (int i = 0; i < rA.Length; i++)
            for (int j = 0; j < rB.Length; j++)
            {
                if (Vector3.Distance(rA[i].Centre, rB[j].Centre) - rA[i].Radius - rB[j].Radius >= rr.Dist) continue;
                Vector3 pa, pb;
                float d = Geo.SegSeg(rA[i].A, rA[i].B, rB[j].A, rB[j].B, out pa, out pb);
                if (d >= rr.Dist) continue;
                rr = new Hit
                {
                    Valid = true, Dist = d, RidgeA = true, RidgeB = true,
                    RingA = rA[i].Ring, SegA = rA[i].Seg, RingB = rB[j].Ring, SegB = rB[j].Seg,
                    TypeA = rA[i].Type, TypeB = rB[j].Type,
                    AngA = rA[i].IncludedAngleDeg, AngB = rB[j].IncludedAngleDeg, PA = pa, PB = pb
                };
            }

        rf = RidgeVsFacet(rA, fB, true);
        fr = RidgeVsFacet(rB, fA, false);
    }

    private static Hit RidgeVsFacet(Ridge[] rs, Facet[] fs, bool ridgeIsA)
    {
        Hit best = Blank();
        for (int i = 0; i < rs.Length; i++)
            for (int j = 0; j < fs.Length; j++)
            {
                if (Vector3.Distance(rs[i].Centre, fs[j].Centre) - rs[i].Radius - fs[j].Radius >= best.Dist) continue;
                Vector3 pr, pf;
                float d = Geo.SegQuad(rs[i].A, rs[i].B, fs[j].P00, fs[j].P10, fs[j].P11, fs[j].P01, out pr, out pf);
                if (d >= best.Dist) continue;
                best = new Hit
                {
                    Valid = true, Dist = d,
                    RidgeA = ridgeIsA, RidgeB = !ridgeIsA,
                    RingA = ridgeIsA ? rs[i].Ring : fs[j].Ring, SegA = ridgeIsA ? rs[i].Seg : fs[j].Seg,
                    RingB = ridgeIsA ? fs[j].Ring : rs[i].Ring, SegB = ridgeIsA ? fs[j].Seg : rs[i].Seg,
                    TypeA = ridgeIsA ? rs[i].Type : fs[j].Type,
                    TypeB = ridgeIsA ? fs[j].Type : rs[i].Type,
                    AngA = ridgeIsA ? rs[i].IncludedAngleDeg : float.NaN,
                    AngB = ridgeIsA ? float.NaN : rs[i].IncludedAngleDeg,
                    PA = ridgeIsA ? pr : pf, PB = ridgeIsA ? pf : pr
                };
            }
        return best;
    }

    private static Hit Blank() => new Hit { Dist = float.MaxValue, Valid = false, AngA = float.NaN, AngB = float.NaN };

    /// <summary>Identity string used to detect GENUINE authored feature change, ignoring which ring segment.</summary>
    public static string Identity(Hit h) =>
        !h.Valid ? "" :
        (h.RidgeA ? "R" : "F") + h.RingA + ":" + h.TypeA + "|" + (h.RidgeB ? "R" : "F") + h.RingB + ":" + h.TypeB;

    public static class Geo
    {
        public static float SegSeg(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2, out Vector3 c1, out Vector3 c2)
        {
            Vector3 d1 = q1 - p1, d2 = q2 - p2, r = p1 - p2;
            float a = Vector3.Dot(d1, d1), e = Vector3.Dot(d2, d2), f = Vector3.Dot(d2, r);
            float s, t;
            if (a <= 1e-12f && e <= 1e-12f) { c1 = p1; c2 = p2; return Vector3.Distance(c1, c2); }
            if (a <= 1e-12f) { s = 0f; t = Mathf.Clamp01(f / e); }
            else
            {
                float c = Vector3.Dot(d1, r);
                if (e <= 1e-12f) { t = 0f; s = Mathf.Clamp01(-c / a); }
                else
                {
                    float b = Vector3.Dot(d1, d2);
                    float den = a * e - b * b;
                    s = den != 0f ? Mathf.Clamp01((b * f - c * e) / den) : 0f;
                    t = (b * s + f) / e;
                    if (t < 0f) { t = 0f; s = Mathf.Clamp01(-c / a); }
                    else if (t > 1f) { t = 1f; s = Mathf.Clamp01((b - c) / a); }
                }
            }
            c1 = p1 + d1 * s; c2 = p2 + d2 * t;
            return Vector3.Distance(c1, c2);
        }

        public static float PointTri(Vector3 p, Vector3 a, Vector3 b, Vector3 c, out Vector3 cp)
        {
            Vector3 ab = b - a, ac = c - a, ap = p - a;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f) { cp = a; return Vector3.Distance(p, cp); }
            Vector3 bp = p - b; float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3) { cp = b; return Vector3.Distance(p, cp); }
            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f) { cp = a + ab * (d1 / (d1 - d3)); return Vector3.Distance(p, cp); }
            Vector3 cq = p - c; float d5 = Vector3.Dot(ab, cq), d6 = Vector3.Dot(ac, cq);
            if (d6 >= 0f && d5 <= d6) { cp = c; return Vector3.Distance(p, cp); }
            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f) { cp = a + ac * (d2 / (d2 - d6)); return Vector3.Distance(p, cp); }
            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f)
            { cp = b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6))); return Vector3.Distance(p, cp); }
            float den = 1f / (va + vb + vc);
            cp = a + ab * (vb * den) + ac * (vc * den);
            return Vector3.Distance(p, cp);
        }

        public static float SegTri(Vector3 s0, Vector3 s1, Vector3 t0, Vector3 t1, Vector3 t2,
            out Vector3 ps, out Vector3 pt)
        {
            Vector3 c1, c2;
            float best = SegSeg(s0, s1, t0, t1, out c1, out c2); ps = c1; pt = c2;
            float d = SegSeg(s0, s1, t1, t2, out c1, out c2); if (d < best) { best = d; ps = c1; pt = c2; }
            d = SegSeg(s0, s1, t2, t0, out c1, out c2); if (d < best) { best = d; ps = c1; pt = c2; }
            Vector3 cp;
            d = PointTri(s0, t0, t1, t2, out cp); if (d < best) { best = d; ps = s0; pt = cp; }
            d = PointTri(s1, t0, t1, t2, out cp); if (d < best) { best = d; ps = s1; pt = cp; }
            return best;
        }

        /// <summary>A facet cell is a bilinear quad; both triangulations are tested so the split direction
        /// cannot bias the result.</summary>
        public static float SegQuad(Vector3 s0, Vector3 s1,
            Vector3 p00, Vector3 p10, Vector3 p11, Vector3 p01, out Vector3 ps, out Vector3 pf)
        {
            Vector3 a1, a2, b1, b2;
            float d1 = SegTri(s0, s1, p00, p10, p11, out a1, out a2);
            float d2 = SegTri(s0, s1, p00, p11, p01, out b1, out b2);
            float d3 = SegTri(s0, s1, p00, p10, p01, out _, out _);
            float d4 = SegTri(s0, s1, p10, p11, p01, out _, out _);
            float best = Mathf.Min(Mathf.Min(d1, d2), Mathf.Min(d3, d4));
            if (best == d1) { ps = a1; pf = a2; }
            else if (best == d2) { ps = b1; pf = b2; }
            else { SegTri(s0, s1, p00, p10, p01, out ps, out pf); if (best == d4) SegTri(s0, s1, p10, p11, p01, out ps, out pf); }
            return best;
        }
    }
}
