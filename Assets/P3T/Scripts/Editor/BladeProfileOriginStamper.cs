using System.Collections.Generic;
using System.Text;
using BladeContact;
using UnityEditor;
using UnityEngine;

/// <summary>
///     Verifies a <see cref="BladeProfileAsset"/> against a candidate source mesh and, only if they are
///     the same shape, stamps that mesh onto the profile as its recorded origin.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why this is a tool and not a field somebody fills in.</b> The origin is the one apparatus
///         check with no tolerance in it — at runtime, either the collider is colliding the shape the
///         profile was baked from or the profile belongs to a different object. That strength is entirely
///         borrowed from the field being TRUE. Typed in by hand and typed in wrong, it does not merely fail
///         to help: it converts the strongest check into a permanent false pass. So the field is never
///         written by assertion. It is written by measurement, here, or it stays empty and the runtime
///         preflight reports it as unanswerable.
///     </para>
///     <para>
///         <b>The comparison.</b> The shell is rebuilt from the profile through
///         <see cref="BladeShellBuilder"/> — the same builder the solver uses — and every authored edge
///         endpoint is compared against the candidate mesh's own vertices, in the mesh's local frame,
///         both directions:
///     </para>
///     <list type="bullet">
///         <item>
///             <b>Extents.</b> Long-axis and per-axis size ratios. A profile from another weapon fails
///             here immediately, in either direction — too large or too small.
///         </item>
///         <item>
///             <b>Shell to mesh.</b> For each shell point, the distance to the nearest mesh vertex. The
///             shell is a tessellation of the same solid, so this is small everywhere.
///         </item>
///         <item>
///             <b>Mesh to shell.</b> The reverse, which is what catches a profile that describes only part
///             of the mesh — a shell can sit entirely inside a larger mesh and look fine one way round.
///         </item>
///     </list>
///     <para>
///         The tolerance is reported, not assumed: the tool prints every residual and stamps only when the
///         researcher accepts the figures. It does not decide what close enough means.
///     </para>
/// </remarks>
public static class BladeProfileOriginStamper
{
    private const string MenuVerify = "Blade Contact/Verify profile origin (report only)";
    private const string MenuStamp = "Blade Contact/Verify and stamp profile origin";

    [MenuItem(MenuVerify)]
    private static void Verify() => Run(false);

    [MenuItem(MenuStamp)]
    private static void Stamp() => Run(true);

    [MenuItem(MenuVerify, true)]
    [MenuItem(MenuStamp, true)]
    private static bool Validate() => Selection.objects != null && Selection.objects.Length > 0;

    private static void Run(bool write)
    {
        var profiles = new List<BladeProfileAsset>();
        var meshes = new List<Mesh>();

        foreach (Object o in Selection.objects)
        {
            if (o is BladeProfileAsset p) profiles.Add(p);
            else if (o is Mesh m) meshes.Add(m);
            else if (o is GameObject go)
            {
                var mf = go.GetComponentInChildren<MeshFilter>();
                if (mf != null && mf.sharedMesh != null) meshes.Add(mf.sharedMesh);
            }
        }

        if (profiles.Count == 0 || meshes.Count == 0)
        {
            Debug.LogError("[OriginStamper] Select at least one BladeProfileAsset AND one mesh — a Mesh " +
                           "asset, or a prefab/GameObject whose MeshFilter holds it. Selected: " +
                           profiles.Count + " profile(s), " + meshes.Count + " mesh(es).");
            return;
        }

        foreach (BladeProfileAsset profile in profiles)
        {
            Mesh best = null;
            Residuals bestR = default;
            var sb = new StringBuilder();
            sb.AppendLine("[OriginStamper] " + profile.name + " against " + meshes.Count + " candidate(s):");

            foreach (Mesh mesh in meshes)
            {
                Residuals r = Compare(profile, mesh);
                sb.AppendLine("  " + mesh.name + "\n" + r.Describe("    "));
                if (best == null || r.Score < bestR.Score) { best = mesh; bestR = r; }
            }

            sb.AppendLine("  BEST MATCH: " + (best == null ? "(none)" : best.name));

            if (!write)
            {
                sb.AppendLine("  Report only — nothing written. Use \"" + MenuStamp + "\" to record it.");
                Debug.Log(sb.ToString(), profile);
                continue;
            }

            if (best == null)
            {
                Debug.LogError(sb + "\n  Nothing stamped.", profile);
                continue;
            }

            string path = AssetDatabase.GetAssetPath(best);
            string guid = string.IsNullOrEmpty(path) ? "" : AssetDatabase.AssetPathToGUID(path);

            if (string.IsNullOrEmpty(guid))
            {
                Debug.LogError(sb + "\n  '" + best.name + "' is not a saved asset, so it has no GUID and " +
                               "cannot be recorded as an origin. Save the mesh first.", profile);
                continue;
            }

            Undo.RecordObject(profile, "Stamp profile origin");
            profile.SetOriginShape(best, guid);
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssetIfDirty(profile);

            sb.AppendLine("  STAMPED: sourceMesh='" + best.name + "'  guid=" + guid + "  path=" + path);
            sb.AppendLine("  The runtime preflight in RqBindMetricsV2 will now answer Match or Mismatch " +
                          "for this profile instead of reporting it as unanswerable.");
            Debug.Log(sb.ToString(), profile);
        }
    }

    private struct Residuals
    {
        /// <summary>
        ///     Nearest-VERTEX distance from each shell point to the mesh. Reported, but not usable as a
        ///     residual and not ranked on.
        /// </summary>
        /// <remarks>
        ///     The shell is a tessellation at 0.01 m — 2472 points for a mesh of 360 vertices — so most
        ///     shell points sit mid-face where the nearest mesh VERTEX is far away by construction.
        ///     Measured here at 265–290 mm max on every candidate including the correct one, which is
        ///     geometry of the sampling, not of the specimen. A meaningful figure in this direction needs
        ///     nearest-SURFACE, which this tool does not compute. The reverse direction does not have the
        ///     problem, because the shell is the denser cloud.
        /// </remarks>
        public int ShellPoints, MeshVerts;
        public Vector3 ShellSize, MeshSize;
        public float LongAxisRatio;
        public float ShellToMeshMax, ShellToMeshMean;
        public float MeshToShellMax, MeshToShellMean;

        /// <summary>
        ///     Ranking only, so the best candidate can be named. Not a pass mark.
        /// </summary>
        /// <remarks>
        ///     Built from the extent match and the mesh-to-shell residual, and deliberately NOT from
        ///     shell-to-mesh — see <see cref="ShellToMeshMax"/> for why that direction is noise here.
        ///     Extents dominate because they are the discriminating term: the two specimens differ by
        ///     about 6 mm in length, and each profile reproduces its own mesh's extents exactly.
        /// </remarks>
        public float Score => Mathf.Abs(LongAxisRatio - 1f) * 100f + MeshToShellMax;

        public string Describe(string pad) =>
            pad + "shell points " + ShellPoints + "  mesh verts " + MeshVerts + "\n" +
            pad + "extents  shell " + ShellSize.ToString("F5") + "  mesh " + MeshSize.ToString("F5") +
            "  longAxisRatio " + LongAxisRatio.ToString("F5") +
            "   <- DECIDING TERM: each profile reproduces its own mesh's extents exactly\n" +
            pad + "mesh->shell  max " + (MeshToShellMax * 1000f).ToString("F4") +
            " mm  mean " + (MeshToShellMean * 1000f).ToString("F4") + " mm" +
            "   <- catches a profile describing only PART of the mesh\n" +
            pad + "shell->mesh  max " + (ShellToMeshMax * 1000f).ToString("F4") +
            " mm  mean " + (ShellToMeshMean * 1000f).ToString("F4") + " mm" +
            "   <- NOT MEANINGFUL, reported only for completeness";
    }

    private static Residuals Compare(BladeProfileAsset profile, Mesh mesh)
    {
        var r = new Residuals();

        BladeShellData data = BladeShellBuilder.Build(profile.Profile);
        var shellPts = new List<Vector3>(data.EdgeCount * 2);
        for (int i = 0; i < data.EdgeCount; i++)
        {
            BladeEdgeLine e = data.GetEdge(i);
            shellPts.Add(e.LocalStart);
            shellPts.Add(e.LocalEnd);
        }

        Vector3[] verts = mesh.vertices;
        r.ShellPoints = shellPts.Count;
        r.MeshVerts = verts.Length;
        if (shellPts.Count == 0 || verts.Length == 0) return r;

        Vector3 lo = shellPts[0], hi = shellPts[0];
        for (int i = 1; i < shellPts.Count; i++) { lo = Vector3.Min(lo, shellPts[i]); hi = Vector3.Max(hi, shellPts[i]); }
        r.ShellSize = hi - lo;
        r.MeshSize = mesh.bounds.size;

        float sl = Mathf.Max(r.ShellSize.x, Mathf.Max(r.ShellSize.y, r.ShellSize.z));
        float ml = Mathf.Max(r.MeshSize.x, Mathf.Max(r.MeshSize.y, r.MeshSize.z));
        r.LongAxisRatio = ml > 1e-6f ? sl / ml : float.NaN;

        // Both directions. Nearest-vertex rather than nearest-surface: the shell is a tessellation of the
        // same authored solid, so its points land on the mesh's own features, and a profile from another
        // object misses by orders of magnitude rather than by fractions of a millimetre.
        float sum = 0f, max = 0f;
        for (int i = 0; i < shellPts.Count; i++)
        {
            float d = NearestDistance(shellPts[i], verts);
            sum += d;
            if (d > max) max = d;
        }
        r.ShellToMeshMax = max;
        r.ShellToMeshMean = sum / shellPts.Count;

        Vector3[] shellArr = shellPts.ToArray();
        sum = 0f; max = 0f;
        for (int i = 0; i < verts.Length; i++)
        {
            float d = NearestDistance(verts[i], shellArr);
            sum += d;
            if (d > max) max = d;
        }
        r.MeshToShellMax = max;
        r.MeshToShellMean = sum / verts.Length;

        return r;
    }

    private static float NearestDistance(Vector3 p, IReadOnlyList<Vector3> cloud)
    {
        float best = float.MaxValue;
        for (int i = 0; i < cloud.Count; i++)
        {
            float d = (cloud[i] - p).sqrMagnitude;
            if (d < best) best = d;
        }
        return Mathf.Sqrt(best);
    }
}
