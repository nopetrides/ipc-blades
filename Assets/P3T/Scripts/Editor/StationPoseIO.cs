using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
///     Capture and restore a station's complete pose as JSON, so a hand-made rig state survives leaving play
///     mode. Written 2026-08-05 for the EdgeBind pop-through investigation, where three hand-posed states
///     (snap 1 / 2 / 3) existed ONLY in play mode and would otherwise have been lost on stop.
///
///     <para>
///         <b>Why this exists and why it is not optional.</b> The resist targets were posed during play, and
///         play-mode transform edits are discarded on stop. The authored edit-mode pose differs from the posed
///         one in Y and Z, not just X, so no single-axis edit reproduces a snapshot. The JSON is the record;
///         this is the reader.
///     </para>
///     <para>
///         <b>A restore is a STATE INJECTION, not a physically reached state.</b> Transforms are written
///         directly and rigidbody velocities are zeroed — the solver never integrated its way there. Anything
///         measured immediately after a restore inherits that. <see cref="Restore" /> therefore prints a
///         verification block (blade-Z, gap, penetration) so the restored state can be checked against the
///         values recorded in the file before it is trusted. On the EdgeBind snap 2 file this reproduced the
///         recorded geometry to within 0.2 mm.
///     </para>
///     <para>
///         <b>Format.</b> One JSON object per line under "transforms", each with a station-relative path and
///         local TRS at round-trip float precision. Paths are captured parent-first, which is the order a
///         restore must apply them in. Files written by the investigation live in
///         <c>Workspace/Week5/EdgeBind_Snapshots/</c> (contents removed; see git history if required) and are readable by this loader unchanged.
///     </para>
/// </summary>
public static class StationPoseIO
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    private const string DefaultDir = "Workspace/Week5/EdgeBind_Snapshots";

    [MenuItem("P3T/Station Pose/Capture Selected Station...", false, 100)]
    private static void CaptureMenu()
    {
        var go = Selection.activeGameObject;
        if (go == null)
        {
            EditorUtility.DisplayDialog("Station Pose", "Select the station root first.", "OK");
            return;
        }

        var root = go.transform.root.gameObject;
        var path = EditorUtility.SaveFilePanel("Capture station pose", AbsDefaultDir(),
            root.name + "_pose.json", "json");
        if (string.IsNullOrEmpty(path)) return;
        Capture(root, path, Path.GetFileNameWithoutExtension(path));
        Debug.Log("[StationPoseIO] captured '" + root.name + "' -> " + path);
    }

    [MenuItem("P3T/Station Pose/Restore Into Selected Station...", false, 101)]
    private static void RestoreMenu()
    {
        var go = Selection.activeGameObject;
        if (go == null)
        {
            EditorUtility.DisplayDialog("Station Pose", "Select the station root first.", "OK");
            return;
        }

        var root = go.transform.root.gameObject;
        var path = EditorUtility.OpenFilePanel("Restore station pose", AbsDefaultDir(), "json");
        if (string.IsNullOrEmpty(path)) return;
        Restore(root, path);
    }

    private static string AbsDefaultDir()
    {
        // Application.dataPath is <project>/Assets; the workspace sits beside the Unity folder.
        var dir = Path.GetFullPath(Path.Combine(Application.dataPath, "../../../" + DefaultDir));
        return Directory.Exists(dir) ? dir : Application.dataPath;
    }

    /// <summary>Write every descendant's local TRS, plus rigidbody state and derived contact geometry.</summary>
    public static void Capture(GameObject station, string file, string label)
    {
        var all = station.GetComponentsInChildren<Transform>(true);
        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendFormat(Ci, "  \"label\": \"{0}\",\n", label);
        sb.AppendFormat(Ci, "  \"station\": \"{0}\",\n", station.name);
        sb.AppendFormat(Ci, "  \"capturedUtc\": \"{0}\",\n", System.DateTime.UtcNow.ToString("o"));
        sb.AppendFormat(Ci, "  \"isPlaying\": {0},\n", Application.isPlaying ? "true" : "false");
        sb.AppendLine("  \"transforms\": [");
        for (var i = 0; i < all.Length; i++)
        {
            var t = all[i];
            var p = RelPath(station.transform, t);
            sb.AppendFormat(Ci,
                "    {{\"path\":\"{0}\",\"active\":{1},\"lp\":[{2},{3},{4}],\"lr\":[{5},{6},{7},{8}],\"ls\":[{9},{10},{11}]}}{12}\n",
                p, t.gameObject.activeSelf ? "true" : "false",
                R(t.localPosition.x), R(t.localPosition.y), R(t.localPosition.z),
                R(t.localRotation.x), R(t.localRotation.y), R(t.localRotation.z), R(t.localRotation.w),
                R(t.localScale.x), R(t.localScale.y), R(t.localScale.z),
                i == all.Length - 1 ? "" : ",");
        }

        sb.AppendLine("  ],");
        var geo = Describe(station);
        sb.AppendLine("  \"contactGeometry\": {");
        sb.AppendFormat(Ci, "    \"bladeZ_B_minus_A_mm\": {0},\n", R(geo.x));
        sb.AppendFormat(Ci, "    \"gap_mm\": {0},\n", R(geo.y));
        sb.AppendFormat(Ci, "    \"penetration_mm\": {0}\n", R(geo.z));
        sb.AppendLine("  }");
        sb.AppendLine("}");
        File.WriteAllText(file, sb.ToString());
    }

    /// <summary>
    ///     Apply a captured pose. Transforms are written in file order (parent-first), then every rigidbody is
    ///     re-seated on its transform and its velocities zeroed, so the solver does not inherit momentum from
    ///     whatever state the rig was in. Prints a verification block — compare it against the file's own
    ///     recorded geometry before trusting a measurement taken from the restored state.
    /// </summary>
    public static void Restore(GameObject station, string file)
    {
        var live = new Dictionary<string, Transform>();
        foreach (var t in station.GetComponentsInChildren<Transform>(true))
        {
            var p = RelPath(station.transform, t);
            if (!live.ContainsKey(p)) live[p] = t;
        }

        int applied = 0, missing = 0;
        if (!Application.isPlaying) Undo.RegisterFullObjectHierarchyUndo(station, "Restore station pose");
        foreach (var raw in File.ReadAllLines(file))
        {
            var line = raw.Trim();
            if (!line.StartsWith("{\"path\"") || line.IndexOf("\"lp\":") < 0) continue;
            var a = line.IndexOf("\"path\":\"") + 8;
            var b = line.IndexOf("\"", a);
            var p = line.Substring(a, b - a);
            if (!live.ContainsKey(p))
            {
                missing++;
                continue;
            }

            var lp = Arr(line, "lp");
            var lr = Arr(line, "lr");
            var ls = Arr(line, "ls");
            var t = live[p];
            t.localPosition = new Vector3(lp[0], lp[1], lp[2]);
            t.localRotation = new Quaternion(lr[0], lr[1], lr[2], lr[3]);
            t.localScale = new Vector3(ls[0], ls[1], ls[2]);
            applied++;
        }

        // In EDIT mode there are no rigidbodies at all - SwordBladeSetup builds them on Awake - so this loop
        // is a play-mode-only concern. Kinematic bodies must be skipped: PhysX rejects a velocity write on
        // them and logs "Setting linear velocity of a kinematic body is not supported" once per body.
        foreach (var rb in station.GetComponentsInChildren<Rigidbody>(true))
        {
            rb.position = rb.transform.position;
            rb.rotation = rb.transform.rotation;
            if (rb.isKinematic) continue;
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }

        var geo = Describe(station);
        Debug.Log(string.Format(Ci,
            "[StationPoseIO] RESTORED (STATE INJECTION, not a simulated state) '{0}' from {1}\n" +
            "  transforms applied={2} missing={3}\n" +
            "  bladeZ B-A = {4:F3} mm   gap = {5:F4} mm   penetration = {6:F4} mm\n" +
            "  Compare these against the file's own contactGeometry before trusting any measurement.",
            station.name, Path.GetFileName(file), applied, missing, geo.x, geo.y, geo.z));
    }

    /// <summary>blade-Z (B-A, mm), hull gap (mm), penetration depth (mm). Zeros if the blades are not found.</summary>
    private static Vector3 Describe(GameObject station)
    {
        MeshCollider ca = null, cb = null;
        foreach (var setup in station.GetComponentsInChildren<SwordBladeSetup>(true))
        {
            var bt = setup.transform.Find("Blade");
            var mc = bt != null ? bt.GetComponent<MeshCollider>() : null;
            if (mc == null) continue;
            // "_A" is tested first: "Sword_A_Bind" also contains "_B" via "_Bind".
            if (setup.gameObject.name.Contains("_A") && ca == null) ca = mc;
            else if (setup.gameObject.name.Contains("_B") && cb == null) cb = mc;
        }

        if (ca == null || cb == null || ca.sharedMesh == null || cb.sharedMesh == null) return Vector3.zero;
        var ta = ca.transform;
        var tb = cb.transform;
        var cenA = ta.TransformPoint(ca.sharedMesh.bounds.center);
        var cenB = tb.TransformPoint(cb.sharedMesh.bounds.center);

        // Alternating projection converges to the GLOBAL closest pair for two convex hulls, including the
        // edge-on-edge case a vertex sample would miss. Saturates at 0 once penetrating - read penetration then.
        Vector3 qa = cenA, qb = cenB;
        for (var i = 0; i < 24; i++)
        {
            qa = ca.ClosestPoint(qb);
            qb = cb.ClosestPoint(qa);
        }

        Vector3 mtd;
        float pen;
        if (!Physics.ComputePenetration(ca, ta.position, ta.rotation, cb, tb.position, tb.rotation, out mtd, out pen))
            pen = 0f;
        return new Vector3((cenB.z - cenA.z) * 1000f, (qa - qb).magnitude * 1000f, pen * 1000f);
    }

    private static string RelPath(Transform root, Transform t)
    {
        if (t == root) return "";
        var p = t.name;
        var q = t.parent;
        while (q != null && q != root)
        {
            p = q.name + "/" + p;
            q = q.parent;
        }

        return p;
    }

    private static float[] Arr(string line, string key)
    {
        var i = line.IndexOf("\"" + key + "\":[") + key.Length + 4;
        var j = line.IndexOf("]", i);
        var parts = line.Substring(i, j - i).Split(',');
        var v = new float[parts.Length];
        for (var k = 0; k < parts.Length; k++) v[k] = float.Parse(parts[k], Ci);
        return v;
    }

    private static string R(float f)
    {
        return f.ToString("R", Ci);
    }
}
