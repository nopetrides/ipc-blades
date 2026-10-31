using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
///     Scripted checks on the apparatus that a reviewer can run and a future session can re-run without
///     re-deriving what "correct" means.
///     <para>
///         These are written as plain static methods returning a pass/fail report rather than as NUnit tests
///         because the project has no test framework installed (no <c>com.unity.test-framework</c>, no test
///         asmdef, no test files). The logic lives here so it can be wrapped in NUnit unchanged the day the
///         framework is added — the assertions are the hard part, not the harness.
///     </para>
/// </summary>
public static class ApparatusChecks
{
	private const string MaterialPath = "Assets/P3T/Materials/BladeGrip.physicMaterial";

	[MenuItem("P3T/Checks/Material clone isolation (requires Play mode)")]
	private static void MenuMaterialCloneIsolation()
	{
		string report;
		var ok = MaterialCloneIsolation(out report);
		if (ok) Debug.Log("[Check] PASS — material clone isolation\n" + report);
		else Debug.LogError("[Check] FAIL — material clone isolation\n" + report);
	}

	/// <summary>
	///     Proves the ownership split actually holds under mutation: moving the friction slider during play must
	///     reach <b>every collider on every sword</b>, and must leave the declared project asset untouched.
	///     <para>
	///         Both halves matter and they fail in opposite directions. If the mutation does not propagate, a
	///         sweep silently tests fewer surfaces than it claims — that is how a crossguard ended up running
	///         PhysX's 0.6/0.6 beside a blade on the declared 0.3. If the mutation reaches the asset instead, a
	///         sweep permanently rewrites the declared baseline it was supposed to be measured against, and the
	///         corruption survives into the next session and into git.
	///     </para>
	///     <para>
	///         Restores the original friction on every sword before returning, including on failure.
	///     </para>
	/// </summary>
	public static bool MaterialCloneIsolation(out string report)
	{
		var log = new List<string>();
		var problems = new List<string>();

		if (!Application.isPlaying)
		{
			report = "must be run in Play mode — the runtime clone does not exist in edit mode";
			return false;
		}

		var asset = AssetDatabase.LoadAssetAtPath<PhysicsMaterial>(MaterialPath);
		if (asset == null)
		{
			report = "declared asset not found at " + MaterialPath;
			return false;
		}

		var setups = Object.FindObjectsByType<SwordBladeSetup>(FindObjectsInactive.Include);
		if (setups.Length == 0)
		{
			report = "no SwordBladeSetup components in the scene";
			return false;
		}

		var assetBefore = asset.staticFriction;
		var originals = new Dictionary<SwordBladeSetup, float>();
		foreach (var s in setups) originals[s] = s.friction;

		// a value nothing else in the project uses, so a stale read is obvious rather than coincidentally equal
		const float probe = 0.7654321f;

		try
		{
			foreach (var s in setups)
			{
				s.friction = probe;
				s.ApplyMaterial();
			}

			// 1. the mutation reached EVERY non-trigger collider on every sword
			var colliders = 0;
			var missed = 0;
			foreach (var s in setups)
			foreach (var c in s.ContactColliders())
			{
				colliders++;
				if (c.sharedMaterial == null)
				{
					problems.Add(Path(c.transform) + " has no material at all");
					missed++;
					continue;
				}

				if (!Mathf.Approximately(c.sharedMaterial.staticFriction, probe)
				    || !Mathf.Approximately(c.sharedMaterial.dynamicFriction, probe))
				{
					problems.Add(Path(c.transform) + " did NOT receive the slider change (mu_s " +
					             c.sharedMaterial.staticFriction + ")");
					missed++;
				}
			}

			log.Add($"mutation reached {colliders - missed}/{colliders} non-trigger colliders across " +
			        $"{setups.Length} swords");

			// 2. one clone per sword, not one per collider — otherwise a slider moves only part of a sword
			foreach (var s in setups)
			{
				var distinct = new HashSet<EntityId>();
				foreach (var c in s.ContactColliders())
					if (c.sharedMaterial != null)
						distinct.Add(c.sharedMaterial.GetEntityId());
				if (distinct.Count > 1)
					problems.Add(s.name + " has " + distinct.Count + " distinct materials across its colliders");
			}

			// 3. no collider is pointing at the project asset itself
			foreach (var s in setups)
			foreach (var c in s.ContactColliders())
				if (c.sharedMaterial != null && c.sharedMaterial.GetEntityId() == asset.GetEntityId())
					problems.Add(Path(c.transform) + " references the PROJECT ASSET directly — a sweep would " +
					             "rewrite the declared baseline");

			// 4. the declared asset is untouched
			if (!Mathf.Approximately(asset.staticFriction, assetBefore))
				problems.Add($"THE DECLARED ASSET WAS MUTATED: {assetBefore} -> {asset.staticFriction}");
			else
				log.Add($"declared asset still mu_s {asset.staticFriction} — untouched by the slider");

			// 5. and the divergence is reported rather than silent
			var reported = 0;
			foreach (var s in setups)
			{
				string d;
				if (!s.MatchesDeclared(out d)) reported++;
			}

			if (reported != setups.Length)
				problems.Add($"only {reported}/{setups.Length} swords reported divergence from the declared " +
				             "material while holding a swept value — divergence must never be silent");
			else
				log.Add($"all {reported} swords reported divergence while swept, as they should");
		}
		finally
		{
			foreach (var kv in originals)
			{
				kv.Key.friction = kv.Value;
				kv.Key.ApplyMaterial();
			}

			log.Add("restored original friction on all " + originals.Count + " swords");
		}

		report = string.Join("\n  ", log.ToArray());
		if (problems.Count > 0) report += "\nPROBLEMS:\n  " + string.Join("\n  ", problems.ToArray());
		return problems.Count == 0;
	}

	private static string Path(Transform t)
	{
		var p = "";
		while (t != null)
		{
			p = "/" + t.name + p;
			t = t.parent;
		}

		return p;
	}
}
