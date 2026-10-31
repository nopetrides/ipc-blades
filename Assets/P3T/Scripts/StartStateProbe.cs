using UnityEngine;

/// <summary>
///     The declared BLADE CROSS-SECTION CONVENTION: how a blade's frame is built from its markers, and where a
///     contact sits within that frame. <b>Pure geometry</b> - every method reads its arguments and writes
///     nothing back, so calling it cannot change an outcome.
/// </summary>
/// <remarks>
///     <para>
///         <b>2026-08-24.</b> This file used to also emit a wide DRIVE-start CSV row
///         (<c>Header</c> / <c>FieldCount</c> / <c>Row(PilotRunController, BindMetrics)</c>). Both of those
///         types are deleted and nothing called <c>Row</c>, so it is gone; the current DRIVE-start record is
///         <c>RqStationContactRecorder</c> and <c>RqClassificationForkRecorder</c>. What remains is the part
///         that is still consumed - <see cref="StationDebugGizmos" /> draws the SAME edge direction these
///         methods define, rather than deriving a second one. The withdrawn crossing angles that the old row
///         header quoted (flat 11 deg, no-bind 40 deg, bind 47 deg) went with it and must not be cited.
///     </para>
///     <b>Declared conventions</b> (quote these with any number derived from them):
///     <list type="bullet">
///         <item>
///             <b>Blade frame.</b> Origin = guard (midpoint of the two crossguard reference points). Axis
///             <c>a</c> = guard -> tip, normalised. Quillon <c>q</c> = the Q2 -> Q1 direction, orthogonalised
///             against <c>a</c>. Face normal <c>f</c> = <c>a x q</c>. On these specimens the quillons lie in the
///             plane of the edges, so <c>q</c> points across the blade toward an edge and <c>f</c> is normal to
///             the flat. <b>That correspondence is an assumption about how the markers were placed</b>, not a
///             measurement; it is what makes the presentation angle below interpretable, so it should be checked
///             against the specimens before the number is leaned on.
///         </item>
///         <item>
///             <b>Presentation angle</b> (per blade, 0-90 deg). Take the contact point, remove its component
///             along the blade axis, and ask where the remainder sits in the cross-section:
///             <c>0 deg = on the edge</c> (along +/-q), <c>90 deg = on the flat</c> (along +/-f). This is the
///             realised edge-vs-flat presentation, as opposed to the authored one.
///             <b>The components must be normalised by the local half-extents first.</b> A blade is wide and
///             thin, so a contact sitting squarely on the flat still has a large edge-component - up to the full
///             half-width - while its face-component can never exceed the half-thickness. A raw
///             <c>atan2(face, edge)</c> therefore reads "near the edge" for every contact and discriminates
///             nothing (measured 2026-08-02: it returned 1-27 deg for edge and flat presentations alike). The
///             half-extents are measured from the convex hull's own vertices in a window around the contact's
///             axial station, so a tapering blade is handled, and both are reported so the normalisation is
///             auditable.
///         </item>
///     </list>
/// </remarks>
public static class StartStateProbe
{
	/// <summary>
	///     Edge direction (quillon, orthogonalised to the axis) and flat normal for one blade.
	///     <para>
	///         <b>This is the single physical edge convention.</b> Public so <see cref="StationDebugGizmos" /> can
	///         draw the same edge direction this probe measures against, rather than deriving a second one — the
	///         rig already carries two deliberately independent conventions (this one, from the quillon markers,
	///         and the geometric one from mesh extents in <c>StationDebugGizmos.Frame</c>). A third would have to
	///         be reconciled with both.
	///     </para>
	/// </summary>
	public static void CrossSection(Vector3 axis, Vector3 quillon, out Vector3 edge, out Vector3 face)
	{
		edge = quillon - Vector3.Dot(quillon, axis) * axis;
		edge = edge.sqrMagnitude > 1e-10f ? edge.normalized : Vector3.Cross(axis, Vector3.up).normalized;
		face = Vector3.Cross(axis, edge).normalized;
	}

	/// <summary>
	///     Half-width (edge-to-edge) and half-thickness (flat-to-flat) of the blade hull at the contact's axial
	///     station, measured from the collider's own vertices. The window around that station is grown until it
	///     catches vertices, because these hulls are coarse (50 verts over a metre) and a fixed narrow window
	///     would often be empty on a tapering blade. Returns 0 if the collider has no readable mesh, which makes
	///     the caller fall back to the raw components rather than divide by zero.
	/// </summary>
	public static void CrossSectionExtents(Collider col, Vector3 guard, Vector3 axis, Vector3 edge, Vector3 face,
		float alongM, out float halfWidthMm, out float halfThickMm)
	{
		halfWidthMm = 0f;
		halfThickMm = 0f;
		var mc = col as MeshCollider;
		if (mc == null || mc.sharedMesh == null) return;

		var l2w = col.transform.localToWorldMatrix;
		Vector3[] verts;
		try
		{
			// a non-readable mesh throws rather than returning empty; treat that as "cannot normalise" and let
			// the caller fall back to the raw components
			verts = mc.sharedMesh.vertices;
		}
		catch (System.Exception)
		{
			return;
		}

		if (verts == null || verts.Length < 3) return;
		foreach (var window in new[] {0.03f, 0.06f, 0.12f, 0.25f, 1e9f})
		{
			float w = 0f, t = 0f;
			var hit = 0;
			foreach (var v in verts)
			{
				var r = l2w.MultiplyPoint3x4(v) - guard;
				var s = Vector3.Dot(r, axis);
				if (Mathf.Abs(s - alongM) > window) continue;
				r -= s * axis;
				w = Mathf.Max(w, Mathf.Abs(Vector3.Dot(r, edge)));
				t = Mathf.Max(t, Mathf.Abs(Vector3.Dot(r, face)));
				hit++;
			}

			if (hit < 3) continue;
			halfWidthMm = w * 1000f;
			halfThickMm = t * 1000f;
			return;
		}
	}

	/// <summary>
	///     Where the contact sits in a blade's cross-section. 0° = on the edge, 90° = on the flat. The two
	///     components are normalised by the local half-extents first — see the class docs; without that a blade's
	///     width swamps its thickness and every contact reads as "near the edge". Raw components are returned in
	///     mm as well, so a near-axis contact (where the angle is ill-conditioned because the radial offset is
	///     tiny) stays visible instead of hiding behind an angle.
	/// </summary>
	public static void Presentation(Vector3 contact, Vector3 guard, Vector3 axis, Vector3 edge, Vector3 face,
		float halfWidthMm, float halfThickMm, out float deg, out float edgeMm, out float faceMm)
	{
		var r = contact - guard;
		r -= Vector3.Dot(r, axis) * axis;
		edgeMm = Vector3.Dot(r, edge) * 1000f;
		faceMm = Vector3.Dot(r, face) * 1000f;
		var e = halfWidthMm > 1e-3f ? Mathf.Abs(edgeMm) / halfWidthMm : Mathf.Abs(edgeMm);
		var f = halfThickMm > 1e-3f ? Mathf.Abs(faceMm) / halfThickMm : Mathf.Abs(faceMm);
		deg = Mathf.Atan2(f, e) * Mathf.Rad2Deg;
	}
}
