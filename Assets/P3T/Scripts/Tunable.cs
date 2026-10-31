using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

/// <summary>
///     Marks a field as a runtime-tunable experiment parameter. <see cref="TunerPanel" /> discovers these by
///     reflection and builds its list from them, so adding a parameter is one attribute on one field — there is
///     no registry to keep in sync.
///     Reflection rather than a ScriptableObject registry is deliberate. An SO listing the parameters would have
///     to be edited every time a field is added, which is the opposite of auto-populating, and it cannot carry a
///     getter/setter for an arbitrary field without per-parameter subclasses. The SO earns its place elsewhere —
///     see <see cref="TunerProfile" />, which snapshots values as a declared, citable configuration.
///     Why this exists (Bill, Checkpoint 4): the parameters are otherwise *"smoke-screened by the inspector"*.
///     Anything that could influence the outcome should be adjustable at runtime by a reviewer, **even if it
///     turns out to change nothing** — a factor whose whole range changes nothing is itself a reportable result,
///     and exposing it pre-empts the "but did you try…" objection.
/// </summary>
/// <summary>
///     Bill's three-way disposition rule (Checkpoint 4). Every parameter that could influence the outcome must
///     land in exactly one of these, and the panel shows all three so a reviewer who cannot open the Unity
///     inspector still sees the whole accounting.
///     <para>
///         This is NOT the outcome taxonomy. Bill has a second three-way rule — category 1 (whole range changes
///         nothing) / 2 (changes every station) / 3 (changes ONE station) — which is a <i>result</i> produced by
///         SweepRunner (DELETED 2026-08-21), never authored here. Do not conflate them.
///     </para>
/// </summary>
public enum Disposition
{
	/// <summary>Slider. Exposed because we could neither justify nor fix it — a reviewer moves it themselves.</summary>
	Exposed = 0,

	/// <summary>Tested, one value works, now locked. Read-only row: the value, why, and what fixed it.</summary>
	TestedAndFixed = 1,

	/// <summary>No test needed; a sentence suffices. Read-only row: the value and why.</summary>
	Justified = 2
}

[AttributeUsage(AttributeTargets.Field)]
public class TunableAttribute : Attribute
{
	public TunableAttribute(string group, float min, float max, string label = null, string note = null,
		int order = 0, Disposition disposition = Disposition.Exposed, float declared = float.NaN)
	{
		Group = group;
		Min = min;
		Max = max;
		Label = label;
		Note = note;
		Order = order;
		Disposition = disposition;
		Declared = declared;
	}

	public string Group { get; }
	public string Label { get; }
	public float Min { get; }
	public float Max { get; }

	/// <summary>One line on what this does or why it is bounded. Shown under the row.</summary>
	public string Note { get; }

	public int Order { get; }

	/// <summary>Which of Bill's three dispositions this parameter is in.</summary>
	public Disposition Disposition { get; }

	/// <summary>
	///     The value <see cref="Note" /> was written about, or NaN to opt out of the staleness check.
	///     <para>
	///         This is the whole anti-rot mechanism, and it needs no bookkeeping: if the field no longer holds the
	///         declared value then the explanation beside it is, by definition, describing something that is no
	///         longer true. <see cref="TunableRegistry.Scan" /> raises a console error naming the field, both
	///         values and the note, so the next person to open the scene sees an error rather than a stale
	///         sentence. Mandatory for <see cref="Disposition.Justified" /> and
	///         <see cref="Disposition.TestedAndFixed" /> — for those the fixed value IS the claim.
	///     </para>
	/// </summary>
	public float Declared { get; }
}

/// <summary>
///     Implement when one component type fills two different experimental roles, so its tunables do not collapse
///     into a single row. <c>BladeResistActor</c> (removed at E6) was the case that forced this: the same class was A's
///     line-keeper (12 N) and B's bind-resist (30 N), and moving those together would be wrong — A must stay well
///     below B or the bind can no longer deflect A.
/// </summary>
public interface ITunableRole
{
	string TunableRole { get; }
}

/// <summary>One discovered field, bound to its owning component, with the baseline it started at.</summary>
public class TunableParam
{
	public readonly TunableAttribute Attr;
	public readonly object Baseline;
	public readonly FieldInfo Field;
	public readonly string Label;

	/// <summary>Callback the owner may need after a write (e.g. re-applying joint drives).</summary>
	public readonly Action<MonoBehaviour> PostSet;

	public readonly string Station;
	public readonly MonoBehaviour Target;

	public TunableParam(MonoBehaviour target, FieldInfo field, TunableAttribute attr, string station,
		Action<MonoBehaviour> postSet)
	{
		Target = target;
		Field = field;
		Attr = attr;
		Station = station;
		PostSet = postSet;
		var role = target is ITunableRole r ? r.TunableRole : null;
		Label = (attr.Label ?? Nicify(field.Name)) + (string.IsNullOrEmpty(role) ? "" : " — " + role);
		Baseline = field.GetValue(target);
	}

	public bool IsBool => Field.FieldType == typeof(bool);

	public bool IsEnum => Field.FieldType.IsEnum;

	/// <summary>Whole-number parameter: the slider should snap and the value should print as a name, not 1.003.</summary>
	public bool IsDiscrete => IsEnum || Field.FieldType == typeof(int);

	/// <summary>Display text for the current value — enum member name where there is one.</summary>
	public string ValueText()
	{
		if (IsBool) return GetFloat() > 0.5f ? "on" : "off";
		if (IsEnum) return System.Enum.GetName(Field.FieldType, Field.GetValue(Target)) ?? GetFloat().ToString();
		return GetFloat().ToString("0.###");
	}

	public string Key => Attr.Group + "/" + Label;

	public Disposition Disposition => Attr.Disposition;

	/// <summary>
	///     True when the field has drifted from <see cref="TunableAttribute.Declared" />, which means the note
	///     beside it no longer describes reality. Compared against the value the field was found with
	///     (<see cref="Baseline" />) rather than the live value, so a reviewer dragging a slider does not trip it —
	///     what we are catching is a change someone made and did not write down.
	/// </summary>
	public bool IsStale
	{
		get
		{
			if (float.IsNaN(Attr.Declared)) return false;
			var found = BaselineFloat();
			// relative tolerance: contact offset lives at 5e-3 and torque caps at 6e1, so one absolute epsilon
			// cannot serve both.
			var tol = Mathf.Max(1e-6f, Mathf.Abs(Attr.Declared) * 1e-4f);
			return Mathf.Abs(found - Attr.Declared) > tol;
		}
	}

	/// <summary>The value this field held when the scene was scanned, as a float.</summary>
	public float BaselineFloat()
	{
		if (Baseline is float f) return f;
		if (Baseline is int i) return i;
		if (Baseline is bool b) return b ? 1f : 0f;
		if (Baseline != null && Field.FieldType.IsEnum) return System.Convert.ToInt32(Baseline);
		return float.NaN;
	}

	/// <summary>Baseline rendered the way <see cref="ValueText" /> renders the live value.</summary>
	public string BaselineText()
	{
		if (IsBool) return BaselineFloat() > 0.5f ? "on" : "off";
		if (IsEnum) return System.Enum.GetName(Field.FieldType, Baseline) ?? BaselineFloat().ToString();
		return BaselineFloat().ToString("0.#####");
	}

	public float GetFloat()
	{
		if (Field.FieldType == typeof(float)) return (float) Field.GetValue(Target);
		if (Field.FieldType == typeof(int)) return (int) Field.GetValue(Target);
		if (Field.FieldType == typeof(bool)) return (bool) Field.GetValue(Target) ? 1f : 0f;
		if (IsEnum) return System.Convert.ToInt32(Field.GetValue(Target));
		return 0f;
	}

	public void SetFloat(float v)
	{
		if (Field.FieldType == typeof(float)) Field.SetValue(Target, v);
		else if (Field.FieldType == typeof(int)) Field.SetValue(Target, Mathf.RoundToInt(v));
		else if (Field.FieldType == typeof(bool)) Field.SetValue(Target, v > 0.5f);
		else if (IsEnum) Field.SetValue(Target, System.Enum.ToObject(Field.FieldType, Mathf.RoundToInt(v)));
		PostSet?.Invoke(Target);
	}

	public void ResetToBaseline()
	{
		Field.SetValue(Target, Baseline);
		PostSet?.Invoke(Target);
	}

	private static string Nicify(string n)
	{
		var s = "";
		for (var i = 0; i < n.Length; i++)
		{
			if (i > 0 && char.IsUpper(n[i]) && !char.IsUpper(n[i - 1])) s += " ";
			s += i == 0 ? char.ToUpper(n[i]) : n[i];
		}

		return s;
	}
}

/// <summary>
///     Scans the loaded scene for <see cref="TunableAttribute" /> fields and groups them.
///     Parameters are keyed by group + label rather than by component, so the same knob across the three
///     stations collapses to ONE row that writes to all of them. That matches how the determination is run —
///     identical rigs by construction (04 §5), swept together — and keeps "I changed one thing" true. A scope
///     selector narrows writes to a single station when a per-station difference is being chased.
/// </summary>
public static class TunableRegistry
{
	private static readonly Dictionary<MonoBehaviour, Action<MonoBehaviour>> PostSetters = new();

	/// <summary>Group -> rows, where each row is every station's copy of that parameter.</summary>
	public static List<KeyValuePair<string, List<List<TunableParam>>>> Scan()
	{
		var found = new List<TunableParam>();
		foreach (var mb in UnityEngine.Object.FindObjectsByType<MonoBehaviour>())
		{
			if (mb == null) continue;
			var type = mb.GetType();
			FieldInfo[] fields;
			try
			{
				fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
			}
			catch
			{
				continue;
			}

			foreach (var f in fields)
			{
				var attr = (TunableAttribute) Attribute.GetCustomAttribute(f, typeof(TunableAttribute));
				if (attr == null) continue;
				found.Add(new TunableParam(mb, f, attr, StationOf(mb.transform), PostSetterFor(mb)));
			}
		}

		var byGroup = new Dictionary<string, Dictionary<string, List<TunableParam>>>();
		var groupOrder = new List<string>();
		foreach (var p in found)
		{
			if (!byGroup.TryGetValue(p.Attr.Group, out var rows))
			{
				rows = new Dictionary<string, List<TunableParam>>();
				byGroup[p.Attr.Group] = rows;
				groupOrder.Add(p.Attr.Group);
			}

			if (!rows.TryGetValue(p.Label, out var list)) rows[p.Label] = list = new List<TunableParam>();
			list.Add(p);
		}

		var result = new List<KeyValuePair<string, List<List<TunableParam>>>>();
		foreach (var g in groupOrder)
		{
			var rows = new List<List<TunableParam>>(byGroup[g].Values);
			rows.Sort((a, b) =>
			{
				var c = a[0].Attr.Order.CompareTo(b[0].Attr.Order);
				return c != 0 ? c : string.CompareOrdinal(a[0].Label, b[0].Label);
			});
			result.Add(new KeyValuePair<string, List<List<TunableParam>>>(g, rows));
		}

		Audit(result);
		return result;
	}

	/// <summary>
	///     Two checks, run on every scan, that turn written rules into enforced ones.
	///     <para>
	///         <b>Staleness.</b> A parameter whose value has drifted from its declared value has an explanation
	///         that no longer describes reality. That is worse than no explanation, because it reads as verified.
	///         Raised as an error so the next person to open the scene cannot miss it.
	///     </para>
	///     <para>
	///         <b>Boundary identity (04 §5).</b> A controlled variable must hold ONE value across all three
	///         stations, or a per-station difference in it becomes an alternative explanation for the
	///         between-station outcome difference and the comparison is void. The panel already collapses a
	///         parameter to one row that writes every station, but the scope selector can narrow a write to a
	///         single station — useful while chasing a difference, and silently invalidating if left that way.
	///         This names any parameter currently divergent across stations.
	///     </para>
	/// </summary>
	private static void Audit(List<KeyValuePair<string, List<List<TunableParam>>>> groups)
	{
		var stale = 0;
		var divergent = 0;
		foreach (var g in groups)
		foreach (var row in g.Value)
		{
			foreach (var p in row)
			{
				if (!p.IsStale) continue;
				stale++;
				Debug.LogError(
					$"[Tuner] STALE DECLARATION — {p.Key}" +
					(string.IsNullOrEmpty(p.Station) ? "" : $" (station {p.Station})") +
					$" is {p.BaselineText()} but was declared as {p.Attr.Declared:0.#####}. " +
					"The note beside it therefore describes a value the scene no longer has. Either restore the " +
					"declared value or update BOTH the declared value and the note in the same edit. " +
					$"Note: \"{p.Attr.Note}\"", p.Target);
			}

			// divergence across stations, scene-global parameters excluded (they have no station)
			var stationed = row.FindAll(p => !string.IsNullOrEmpty(p.Station));
			if (stationed.Count < 2) continue;
			var first = stationed[0].GetFloat();
			var spread = 0f;
			foreach (var p in stationed) spread = Mathf.Max(spread, Mathf.Abs(p.GetFloat() - first));
			if (spread <= Mathf.Max(1e-6f, Mathf.Abs(first) * 1e-4f)) continue;
			divergent++;
			var detail = "";
			foreach (var p in stationed) detail += $"  {p.Station}={p.ValueText()}";
			Debug.LogError(
				$"[Tuner] BOUNDARY IDENTITY VIOLATED (04 §5) — {row[0].Key} is not the same across stations:" +
				detail + ". A controlled variable must hold one value for every station, or a per-station " +
				"difference in it is an alternative explanation for the outcome difference and the comparison " +
				"is void. Reset to baseline, or declare this parameter as the one thing under test.",
				stationed[0].Target);
		}

		if (stale == 0 && divergent == 0) return;
		Debug.LogError($"[Tuner] audit failed: {stale} stale declaration(s), {divergent} boundary-identity " +
		               "violation(s). Results recorded in this state are not citable until both are zero.");
	}

	/// <summary>Nearest ancestor named Station_* — the parameter's station, or "" for scene-global ones.</summary>
	private static string StationOf(Transform t)
	{
		while (t != null)
		{
			if (t.name.StartsWith("Station_")) return t.name.Substring("Station_".Length);
			t = t.parent;
		}

		return "";
	}

	// Some fields need a call after being written or the change does not reach PhysX.
	private static Action<MonoBehaviour> PostSetterFor(MonoBehaviour mb)
	{
		if (PostSetters.TryGetValue(mb, out var a)) return a;
		Action<MonoBehaviour> setter = null;
		if (mb is SwordBladeSetup)
			setter = m =>
			{
				// ApplyContactOffset is gone with the retired bladeContactOffset tunable; contactOffset is
				// now the project default and nothing writes it per-sword.
				var s = (SwordBladeSetup) m;
				s.ApplyMaterial();
			};
		PostSetters[mb] = setter;
		return setter;
	}
}
