using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
///     A scrollable list of every runtime-tunable experiment parameter, built from whatever carries a
///     <see cref="TunableAttribute" />. Deliberately separate from the per-station `PilotModePanel_*` panels:
///     those drive one station's mode, this drives the experiment's parameters across all of them.
///     <b>Auto-populating.</b> The rows come from reflection at Start, so adding a parameter is one attribute on
///     one field. Nothing here needs editing, and there is no registry asset to fall out of sync.
///     <b>Rows are per parameter, not per station.</b> The three stations are identical rigs by construction
///     (04 §5) and are swept together, so one row writes to all three by default. The scope selector narrows to
///     a single station when a per-station difference is being chased. A row whose stations disagree is marked,
///     so a divergence can never hide.
///     <b>One world-space board, authored in the scene.</b> The canvas is a real scene object —
///     `P3T_TunerPanel/TunerCanvas` — carrying the same component set as a `PilotModePanel_*`: Canvas,
///     CanvasScaler, GraphicRaycaster and `TrackedDeviceGraphicRaycaster`. It is moved and resized with the
///     ordinary transform tools and <b>nothing here writes its position, rotation, scale or sizeDelta</b>.
///     Only the contents are built at Start, because the rows come from reflection and cannot be baked.
///     `Tab` shows and hides it. The scene's existing EventSystem serves it; no second one is needed, and
///     both XR ray and mouse reach it.
///     <b>There was a ScreenSpaceOverlay mode on `F2`; it is gone.</b> Overlay handed the canvas
///     RectTransform to Unity, which drove it to screen size, and returning to WorldSpace left it wherever
///     Unity had put it — so an authored placement could not survive a single round trip.
///     <b>Placement knowledge that the transform cannot carry.</b> The board sits directly above the centre
///     station's video plane. `z = 2.72` rather than 2.70 on purpose: the station's `Affordance Callout` tag
///     sits at z = 2.70 inside this board's y range and exactly coplanar geometry z-fights; 2 cm puts the
///     board behind the tag, imperceptible at ~2.5 m. The height is CONSTRAINED, not chosen — the video
///     plane's top edge is y = 2.0455 and the template environment's Ceiling slab starts at y = 2.835,
///     leaving 0.79 m of clear headroom, so a taller board punches through the ceiling. Disable
///     `Environment/Template Environment/Ceiling` if a taller one is wanted; it is scenery, not apparatus.
///     The default width matches the video plane's 3.184 m exactly. And a canvas is single-sided: its
///     VISIBLE face is the one its forward points AWAY from, so the board wants yaw 0, not 180 — the player
///     stands at low z looking toward the stations.
///     Purpose (Bill, Checkpoint 4): the parameters are otherwise *"smoke-screened by the inspector"*. A reviewer
///     should be able to move anything that might influence the outcome and watch what happens — including
///     parameters that turn out to change nothing, since that is itself a reportable result.
/// </summary>
[DisallowMultipleComponent]
public class TunerPanel : MonoBehaviour
{
	[Header("Authored canvas — placement and size live on these transforms, not here")]
	[Tooltip("P3T_TunerPanel/TunerCanvas. The board's position, rotation, scale and sizeDelta are AUTHORED on " +
	         "this RectTransform and nothing in this class writes them — move and resize it with the ordinary " +
	         "transform tools.\n\n" +
	         "It sits directly above the centre station's video plane. z = 2.72 rather than 2.70 on purpose: " +
	         "the station's 'Affordance Callout' tag sits at z = 2.70 inside this board's y range and exactly " +
	         "coplanar geometry z-fights.\n\n" +
	         "The height is CONSTRAINED, not chosen: the video plane's top edge is y = 2.0455 and the template " +
	         "environment's Ceiling slab starts at y = 2.835, leaving 0.79 m of clear headroom. A taller board " +
	         "punches through the ceiling — disable Environment/Template Environment/Ceiling if a taller one is " +
	         "wanted, it is scenery not apparatus.\n\n" +
	         "The default 3.184 m width matches the video plane exactly. A canvas is single-sided and its " +
	         "VISIBLE face is the one forward points AWAY from, so it wants yaw 0, not 180.\n\n" +
	         "A serialized reference rather than a name lookup, so renaming the object breaks here at author " +
	         "time instead of silently at Play.")]
	[SerializeField] private RectTransform canvasRoot;

	[Tooltip("TunerCanvas/Frame. Carries the background image and hosts every built row. Authored so the board " +
	         "is visible in the Scene view before Play — a bare canvas draws nothing, which leaves nothing to " +
	         "aim at when positioning it.")]
	[SerializeField] private RectTransform frameRoot;

	[Header("Presentation")]
	public bool startVisible = true;

	[Tooltip("Hide the per-row explanatory notes. They roughly double row height, and the headroom above the " +
	         "video plane is tight.")]
	public bool hideNotes = true;

	[Header("Live readout")]
	[Tooltip("A per-station line showing whether that station is recording drive trails and how many " +
	         "it has taken. It used to show BindMetrics' last-drive migration/tip-ratio summary; that " +
	         "component is deleted, and by then it was reading ZERO instances because every one of them " +
	         "sat on an inactive PilotRig_* object, so the block had been silently empty for weeks.")]
	public bool showLastRunReadout = true;

	private readonly List<Row> _rows = new();
	private Canvas _canvas;
	private RectTransform _frame;
	private Transform _content;
	private RectTransform _scrollRt, _readoutRt, _headerRt, _scopeRt, _resetRt;

	// the two read-only disposition lists and the three list captions
	private Transform _fixedContent, _justifiedContent;
	private RectTransform _fixedScrollRt, _justifiedScrollRt;
	private RectTransform _exposedHdrRt, _fixedHdrRt, _justifiedHdrRt;
	private TextMeshProUGUI _readout;
	private DriveTrailRecorder[] _trails;
	private string _scope = "All";
	private TextMeshProUGUI _scopeLabel;
	private List<string> _stations = new();
	private float _readoutTimer;

	private void Start()
	{
		Build();
		if (_canvas == null) return;

		_trails = FindObjectsByType<DriveTrailRecorder>();
		ApplyLayout();
		_canvas.gameObject.SetActive(startVisible);
	}

	private void Update()
	{
		if (_canvas == null) return;

#if ENABLE_INPUT_SYSTEM
		var kb = Keyboard.current;
		if (kb != null && kb.tabKey.wasPressedThisFrame)
			_canvas.gameObject.SetActive(!_canvas.gameObject.activeSelf);
#endif

		if (!showLastRunReadout || _readout == null) return;
		_readoutTimer += Time.deltaTime;
		if (_readoutTimer < 0.25f) return;
		_readoutTimer = 0f;
		RefreshReadout();
		foreach (var r in _rows) r.RefreshIfExternallyChanged();
	}

	/// <summary>
	///     Lays the chrome and the three lists out inside the AUTHORED canvas.
	/// </summary>
	/// <remarks>
	///     <para>
	///         <b>Reads the canvas, never writes it.</b> Position, rotation, scale and sizeDelta belong to the
	///         scene object and are the whole point of it being authored. This used to set all four from
	///         serialized fields, which is why the panel could not be moved by hand.
	///     </para>
	///     <para>
	///         Replaces <c>SetOverlay(bool)</c>. The ScreenSpaceOverlay mode is gone: it handed the canvas
	///         RectTransform to Unity, so an authored placement did not survive one round trip through it.
	///         Every dimension that used to be chosen per mode is now the former "compact" value, which is what
	///         the world board always used.
	///     </para>
	/// </remarks>
	public void ApplyLayout()
	{
		if (_canvas == null || _frame == null) return;

		_canvas.renderMode = RenderMode.WorldSpace;

		_frame.anchorMin = Vector2.zero;
		_frame.anchorMax = Vector2.one;
		_frame.pivot = new Vector2(0.5f, 0.5f);
		_frame.offsetMin = Vector2.zero;
		_frame.offsetMax = Vector2.zero;

		foreach (var r in _rows)
			if (r.NoteObject != null)
				r.NoteObject.SetActive(!hideNotes);

		// The board is only ~0.74 m tall (the video plane's top edge and the environment ceiling leave 0.79 m),
		// so the header is small and the readout tight — otherwise the chrome eats the list and barely two rows
		// show.
		const float headH = 34f;
		const float rowH = 30f;
		const float readH = 62f;
		var y = -8f;

		Place(_headerRt, 0, 1, 1, 1, new Vector4(18, -(y + headH), -18, y));
		y += headH;

		Place(_scopeRt, 0, 1, 0.42f, 1, new Vector4(18, -(y + rowH), -6, -y));
		Place(_resetRt, 0.42f, 1, 1, 1, new Vector4(6, -(y + rowH), -18, -y));
		y += rowH + 6f;

		var bottom = showLastRunReadout ? readH + 8f : 10f;
		const float capH = 22f;
		const float gap = 6f;

		// Two layouts, chosen by shape rather than by mode.
		//
		// COLUMNS when the board is wide (the world board now spans the three RQ stations). Each list keeps its
		// full height, which is what a vertical list actually wants — stacking three of them in one short board
		// gave every list a third of the height and none of them enough to read.
		//
		// STACKED when it is narrow, where three side-by-side columns would be ~300 px each and unreadable.
		//
		// Read from the AUTHORED canvas, which is what makes resizing TunerCanvas in the editor actually reflow
		// the lists rather than just stretching a fixed layout.
		//
		// Deliberately canvasRoot.sizeDelta and NOT _frame.rect: this runs from Start before the child's rect
		// has been rebuilt, so _frame.rect reports the previous frame's size. The authored value is both correct
		// and one layout pass earlier.
		var w = canvasRoot.sizeDelta.x;
		var h = canvasRoot.sizeDelta.y;
		if (w > h * 1.6f)
		{
			var listTop = y;
			var listH = Mathf.Max(120f, h - listTop - bottom - capH - gap);
			var colGap = 14f;
			// exposed is the one a reviewer drives, so it gets the widest column
			var usable = w - 24f - 2f * colGap;
			var wExposed = usable * 0.42f;
			var wFixed = usable * 0.31f;
			var wJustified = usable - wExposed - wFixed;
			var x = 12f;
			PlaceColumn(_exposedHdrRt, _scrollRt, x, wExposed, listTop, capH, listH, gap);
			x += wExposed + colGap;
			PlaceColumn(_fixedHdrRt, _fixedScrollRt, x, wFixed, listTop, capH, listH, gap);
			x += wFixed + colGap;
			PlaceColumn(_justifiedHdrRt, _justifiedScrollRt, x, wJustified, listTop, capH, listH, gap);
			y = listTop + capH + gap + listH;
		}
		else
		{
			var avail = Mathf.Max(120f, h - y - bottom - 3f * (capH + gap) - 2f * gap);
			var hExposed = avail * 0.50f;
			var hFixed = avail * 0.28f;
			var hJustified = avail - hExposed - hFixed;
			PlaceCaptionedList(_exposedHdrRt, _scrollRt, ref y, capH, hExposed, gap);
			PlaceCaptionedList(_fixedHdrRt, _fixedScrollRt, ref y, capH, hFixed, gap);
			PlaceCaptionedList(_justifiedHdrRt, _justifiedScrollRt, ref y, capH, hJustified, gap);
		}

		if (_readoutRt != null)
		{
			_readout.fontSize = 14f;
			Place(_readoutRt, 0, 0, 1, 0, new Vector4(18, 6, -18, readH));
		}

		// NOTHING is written to the EventSystem's XRUIInputModule here any more. This used to set
		// enableMouseInput = overlay, which at Start meant FALSE — turning the mouse off for every UI in the
		// scene, not just this panel, because the input module is scene-wide. Mouse input is now authored ON in
		// the scene and both pointer kinds reach this board: XR ray via TrackedDeviceGraphicRaycaster and mouse
		// via the plain GraphicRaycaster.
	}

	// ---------- construction ----------

	private void Build()
	{
		// The canvas and its frame are AUTHORED scene objects, not created here. That is what lets the board be
		// moved and resized with the ordinary transform tools. Refuse rather than half-build: a panel missing
		// its own root is a wiring mistake, and quietly conjuring a replacement at the origin would hide it.
		if (canvasRoot == null || frameRoot == null)
		{
			Debug.LogError(
				"[TunerPanel] " + name + ": canvasRoot and frameRoot must both be assigned. Expected the " +
				"authored objects P3T_TunerPanel/TunerCanvas (Canvas, CanvasScaler, GraphicRaycaster, " +
				"TrackedDeviceGraphicRaycaster) and TunerCanvas/Frame (Image). Nothing was built.", this);
			return;
		}

		_canvas = canvasRoot.GetComponent<Canvas>();
		if (_canvas == null)
		{
			Debug.LogError("[TunerPanel] " + name + ": canvasRoot has no Canvas component. Nothing was built.",
				this);
			return;
		}

		// Everything lives in a frame rather than directly on the canvas, so the rows lay out against a rect
		// that is independent of the canvas's own anchoring.
		_frame = frameRoot;
		var rt = _frame;

		// Authored children are replaced, so a second Build (or a domain reload with the panel already
		// populated) cannot stack two sets of rows on top of each other.
		for (var i = rt.childCount - 1; i >= 0; i--) Destroy(rt.GetChild(i).gameObject);

		var header = Text(rt, "EXPERIMENT PARAMETERS", 30, FontStyles.Bold, TextAlignmentOptions.Left);
		_headerRt = header.rectTransform;

		// scope selector
		_stations = new List<string> {"All"};
		// Station scopes come from the scene roots carrying the frozen Station_ prefix. This used to
		// enumerate PilotRunController, which by 2026-08-21 sat only on INACTIVE objects - and
		// FindObjectsByType excludes inactive - so the scope selector had silently collapsed to "All".
		foreach (var r in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
			if (r.name.StartsWith("Station_"))
				_stations.Add(r.name.Substring("Station_".Length));

		_stations.Sort((a, b) => a == "All" ? -1 : b == "All" ? 1 : string.CompareOrdinal(a, b));

		var scopeBtn = Button(rt, "Scope: All", () =>
		{
			var i = (_stations.IndexOf(_scope) + 1) % _stations.Count;
			_scope = _stations[i];
			_scopeLabel.text = "Scope: " + _scope;
			foreach (var r in _rows) r.SetScope(_scope);
		});
		_scopeRt = (RectTransform) scopeBtn.transform;
		_scopeLabel = scopeBtn.GetComponentInChildren<TextMeshProUGUI>();

		var resetBtn = Button(rt, "Reset all to baseline", () =>
		{
			foreach (var r in _rows) r.ResetAll();
		});
		_resetRt = (RectTransform) resetBtn.transform;

		// Three independently scrolling lists, one per disposition (Bill, Checkpoint 4). All three are visible at
		// once on purpose: the point is that a reviewer who cannot open the inspector sees the WHOLE accounting —
		// what we justified, what we tested and locked, and what they can move themselves — rather than only the
		// movable part. Keeping them separate also stops a locked value looking like a slider someone forgot.
		_scrollRt = MakeScrollList(rt, "ScrollExposed", out _content);
		_fixedScrollRt = MakeScrollList(rt, "ScrollFixed", out _fixedContent);
		_justifiedScrollRt = MakeScrollList(rt, "ScrollJustified", out _justifiedContent);

		// rows, straight from reflection
		var groups = TunableRegistry.Scan();
		var count = 0;
		var fixedCount = 0;
		var justifiedCount = 0;

		foreach (var d in new[] {Disposition.Exposed, Disposition.TestedAndFixed, Disposition.Justified})
		{
			var target = d == Disposition.Exposed ? _content
				: d == Disposition.TestedAndFixed ? _fixedContent
				: _justifiedContent;

			foreach (var g in groups)
			{
				var matching = g.Value.FindAll(r => r[0].Disposition == d);
				if (matching.Count == 0) continue;
				GroupHeader(target, g.Key);
				foreach (var row in matching)
					if (d == Disposition.Exposed)
					{
						_rows.Add(new Row(this, target, row));
						count++;
					}
					else
					{
						ReadOnlyRow(target, row[0]);
						if (d == Disposition.TestedAndFixed) fixedCount++;
						else justifiedCount++;
					}
			}
		}

		ListHeader(rt, "EXPOSED — move these", out _exposedHdrRt,
			count == 0 ? "no [Tunable] fields found in the scene" : count + " sliders");
		ListHeader(rt, "TESTED AND FIXED — locked, with what fixed it", out _fixedHdrRt,
			fixedCount == 0 ? "none declared" : fixedCount + " values");
		ListHeader(rt, "JUSTIFIED — value and why", out _justifiedHdrRt,
			justifiedCount == 0 ? "none declared" : justifiedCount + " values");

		if (showLastRunReadout)
		{
			_readout = Text(rt, "", 17, FontStyles.Normal, TextAlignmentOptions.TopLeft);
			_readout.color = new Color(0.75f, 0.82f, 0.9f);
			_readoutRt = _readout.rectTransform;
		}

		Debug.Log($"[Tuner] built {count} exposed rows, {fixedCount} tested-and-fixed, {justifiedCount} justified, " +
		          $"across {groups.Count} groups.");
	}

	/// <summary>One masked, vertically scrolling list. Extracted so the three disposition lists are identical.</summary>
	private static RectTransform MakeScrollList(RectTransform parent, string name, out Transform content)
	{
		var scrollGo = new GameObject(name, typeof(RectTransform), typeof(ScrollRect), typeof(Image), typeof(Mask));
		scrollGo.transform.SetParent(parent, false);
		var scrollRt = (RectTransform) scrollGo.transform;
		scrollGo.GetComponent<Image>().color = new Color(0.10f, 0.11f, 0.14f, 0.9f);
		scrollGo.GetComponent<Mask>().showMaskGraphic = true;

		var contentGo = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup),
			typeof(ContentSizeFitter));
		contentGo.transform.SetParent(scrollRt, false);
		var contentRt = (RectTransform) contentGo.transform;
		contentRt.anchorMin = new Vector2(0, 1);
		contentRt.anchorMax = new Vector2(1, 1);
		contentRt.pivot = new Vector2(0.5f, 1);
		// a fresh RectTransform defaults to sizeDelta (100,100); with stretched X that makes the content 100px
		// WIDER than the viewport, overhanging 50px each side and clipping every label. Zero the width delta.
		contentRt.sizeDelta = new Vector2(0f, 0f);
		contentRt.anchoredPosition = Vector2.zero;
		var vlg = contentGo.GetComponent<VerticalLayoutGroup>();
		vlg.padding = new RectOffset(12, 12, 12, 12);
		vlg.spacing = 6;
		vlg.childForceExpandHeight = false;
		vlg.childControlHeight = true;
		vlg.childControlWidth = true;
		contentGo.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

		var sr = scrollGo.GetComponent<ScrollRect>();
		sr.content = contentRt;
		sr.viewport = scrollRt;
		sr.horizontal = false;
		sr.scrollSensitivity = 40f;
		sr.movementType = ScrollRect.MovementType.Clamped;

		content = contentRt;
		return scrollRt;
	}

	/// <summary>One captioned list as a full-height column at <paramref name="x" />, for the wide board.</summary>
	private static void PlaceColumn(RectTransform caption, RectTransform list, float x, float width, float top,
		float capH, float listH, float gap)
	{
		if (caption == null || list == null) return;
		Place(caption, 0, 1, 0, 1, new Vector4(x, -(top + capH), x + width, -top));
		Place(list, 0, 1, 0, 1, new Vector4(x, -(top + capH + gap + listH), x + width, -(top + capH + gap)));
	}

	/// <summary>Caption above its list, both anchored from the top, advancing the running <paramref name="y" />.</summary>
	private static void PlaceCaptionedList(RectTransform caption, RectTransform list, ref float y, float capH,
		float listH, float gap)
	{
		if (caption == null || list == null) return;
		Place(caption, 0, 1, 1, 1, new Vector4(18, -(y + capH), -18, -y));
		y += capH;
		Place(list, 0, 1, 1, 1, new Vector4(12, -(y + listH), -12, -y));
		y += listH + gap;
	}

	private void ListHeader(RectTransform parent, string title, out RectTransform outRt, string suffix)
	{
		var t = Text(parent, title + "   ·   " + suffix, 19, FontStyles.Bold, TextAlignmentOptions.Left);
		t.color = new Color(0.58f, 0.70f, 0.85f);
		outRt = t.rectTransform;
	}

	/// <summary>
	///     A justified or tested-and-fixed parameter: the value and the reason, not a control. Rendered as a row so
	///     it reads as part of the same register as the sliders, because it is — it just isn't movable.
	///     A stale row is marked, since an explanation that no longer matches its value is worse than none.
	/// </summary>
	private void ReadOnlyRow(Transform parent, TunableParam p)
	{
		var host = new GameObject("Fixed_" + p.Label, typeof(RectTransform), typeof(VerticalLayoutGroup),
			typeof(ContentSizeFitter));
		host.transform.SetParent(parent, false);
		var vlg = host.GetComponent<VerticalLayoutGroup>();
		vlg.spacing = 1;
		vlg.childControlHeight = true;
		vlg.childControlWidth = true;
		vlg.childForceExpandHeight = false;
		host.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

		var stale = p.IsStale;
		var head = Text(host.transform, (stale ? "⚠ " : "") + p.Label + "   =   " + p.BaselineText(), 18,
			FontStyles.Bold, TextAlignmentOptions.Left);
		head.color = stale ? new Color(1f, 0.45f, 0.4f) : new Color(0.86f, 0.9f, 0.95f);

		var why = stale
			? $"STALE — declared {p.Attr.Declared:0.#####}, scene has {p.BaselineText()}. " +
			  "The note below no longer describes this value. " + p.Attr.Note
			: p.Attr.Note;
		if (string.IsNullOrEmpty(why)) return;
		var note = Text(host.transform, why, 15, FontStyles.Italic, TextAlignmentOptions.TopLeft);
		note.color = stale ? new Color(1f, 0.6f, 0.55f) : new Color(0.62f, 0.68f, 0.76f);
	}

	/// <summary>
	///     Per-station drive-trail state. Reports what each recorder is doing; it decides nothing and
	///     summarises no result.
	/// </summary>
	private void RefreshReadout()
	{
		var sb = new System.Text.StringBuilder("DRIVE TRAILS   (per station: recording, drives taken)\n");
		if (_trails == null || _trails.Length == 0)
			sb.AppendLine("  no DriveTrailRecorder in the scene");
		else
			foreach (var t in _trails)
			{
				if (t == null) continue;
				var label = string.IsNullOrEmpty(t.StationSuffix) ? t.name : t.StationSuffix;
				var state = t.recordEnabled ? t.IsRecording ? "REC  " : "on   " : "off  ";
				sb.AppendLine($"  {label,-24} {state}   {t.DriveCount} drive(s)");
			}

		_readout.text = sb.ToString();
	}

	// ---------- one parameter, all its stations ----------

	private class Row
	{
		private readonly List<TunableParam> _all;
		private readonly TextMeshProUGUI _label;
		private readonly Slider _slider;
		private readonly Toggle _toggle;
		private readonly TextMeshProUGUI _value;
		private List<TunableParam> _active;
		private bool _suppress;

		/// <summary>The explanatory note line, hidden in world mode where vertical space is scarce.</summary>
		public GameObject NoteObject;

		public Row(TunerPanel panel, Transform parent, List<TunableParam> all)
		{
			_all = all;
			_active = all;
			var p = all[0];

			var go = new GameObject("Row_" + p.Label, typeof(RectTransform), typeof(VerticalLayoutGroup));
			go.transform.SetParent(parent, false);
			var vl = go.GetComponent<VerticalLayoutGroup>();
			vl.spacing = 2;
			vl.childControlHeight = true;
			vl.childControlWidth = true;
			vl.childForceExpandHeight = false;

			var top = new GameObject("Top", typeof(RectTransform), typeof(HorizontalLayoutGroup),
				typeof(LayoutElement));
			top.transform.SetParent(go.transform, false);
			top.GetComponent<LayoutElement>().minHeight = 34;
			var hl = top.GetComponent<HorizontalLayoutGroup>();
			hl.spacing = 8;
			hl.childControlWidth = true;
			hl.childForceExpandWidth = false;

			_label = Text(top.transform, p.Label, 19, FontStyles.Normal, TextAlignmentOptions.Left);
			_label.GetComponent<LayoutElement>().preferredWidth = 250;

			if (p.IsBool)
			{
				_toggle = MakeToggle(top.transform, p.GetFloat() > 0.5f, v =>
				{
					if (_suppress) return;
					foreach (var q in _active) q.SetFloat(v ? 1f : 0f);
				});
				_value = Text(top.transform, "", 18, FontStyles.Normal, TextAlignmentOptions.Right);
				_value.GetComponent<LayoutElement>().preferredWidth = 230;
			}
			else
			{
				_slider = MakeSlider(top.transform, p.Attr.Min, p.Attr.Max, p.GetFloat(), v =>
				{
					if (_suppress) return;
					foreach (var q in _active) q.SetFloat(v);
					UpdateValueText();
				});
				_slider.wholeNumbers = p.IsDiscrete;
				var sle = _slider.GetComponent<LayoutElement>();
				sle.preferredWidth = 300;
				sle.flexibleWidth = 1; // take the slack: the board is much wider than a row needs
				_value = Text(top.transform, "", 18, FontStyles.Normal, TextAlignmentOptions.Right);
				_value.GetComponent<LayoutElement>().preferredWidth = 130;
			}

			var rb = Button(top.transform, "reset", ResetAll);
			rb.GetComponent<LayoutElement>().preferredWidth = 78;

			if (!string.IsNullOrEmpty(p.Attr.Note))
			{
				var note = Text(go.transform, p.Attr.Note, 14, FontStyles.Italic, TextAlignmentOptions.TopLeft);
				note.color = new Color(0.55f, 0.6f, 0.68f);
				note.GetComponent<LayoutElement>().preferredHeight = 34;
				note.textWrappingMode = TextWrappingModes.Normal;
				NoteObject = note.gameObject;
			}

			UpdateValueText();
		}

		public void SetScope(string scope)
		{
			if (scope == "All") _active = _all;
			else
			{
				_active = new List<TunableParam>();
				foreach (var q in _all)
					if (q.Station == scope)
						_active.Add(q);
				if (_active.Count == 0) _active = _all; // scene-global parameter: always writable
			}

			UpdateValueText();
		}

		public void ResetAll()
		{
			foreach (var q in _active) q.ResetToBaseline();
			RefreshIfExternallyChanged();
		}

		/// <summary>Pull the widget back in line if something else moved the value (a sweep, a reset, code).</summary>
		public void RefreshIfExternallyChanged()
		{
			var v = _active[0].GetFloat();
			_suppress = true;
			if (_slider != null && !Mathf.Approximately(_slider.value, v)) _slider.SetValueWithoutNotify(v);
			if (_toggle != null && _toggle.isOn != v > 0.5f) _toggle.SetIsOnWithoutNotify(v > 0.5f);
			_suppress = false;
			UpdateValueText();
		}

		private void UpdateValueText()
		{
			var v = _active[0].GetFloat();
			var diverged = false;
			foreach (var q in _active)
				if (!Mathf.Approximately(q.GetFloat(), v))
					diverged = true;
			var baseline = System.Convert.ToSingle(_active[0].Baseline);
			var offBaseline = !Mathf.Approximately(v, baseline);

			_value.text = _active[0].ValueText()
			              + (diverged ? "  (stations differ)" : "")
			              + (offBaseline ? "  *" : "");
			_value.color = diverged ? new Color(1f, 0.55f, 0.3f)
				: offBaseline ? new Color(1f, 0.85f, 0.35f)
				: new Color(0.8f, 0.85f, 0.9f);
		}
	}

	// ---------- tiny uGUI helpers ----------

	private void GroupHeader(Transform parent, string title)
	{
		var t = Text(parent, title.ToUpperInvariant(), 20, FontStyles.Bold, TextAlignmentOptions.Left);
		t.color = new Color(0.45f, 0.78f, 1f);
		t.GetComponent<LayoutElement>().preferredHeight = 38;
	}

	private static TextMeshProUGUI Text(Transform parent, string s, float size, FontStyles style,
		TextAlignmentOptions align)
	{
		var go = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI), typeof(LayoutElement));
		go.transform.SetParent(parent, false);
		var t = go.GetComponent<TextMeshProUGUI>();
		t.text = s;
		t.fontSize = size;
		t.fontStyle = style;
		t.alignment = align;
		t.color = Color.white;
		t.raycastTarget = false;
		return t;
	}

	private static Button Button(Transform parent, string label, UnityEngine.Events.UnityAction onClick)
	{
		var go = new GameObject("Button", typeof(RectTransform), typeof(Image), typeof(Button),
			typeof(LayoutElement));
		go.transform.SetParent(parent, false);
		go.GetComponent<Image>().color = new Color(0.20f, 0.24f, 0.32f, 1f);
		var b = go.GetComponent<Button>();
		b.onClick.AddListener(onClick);
		var t = Text(go.transform, label, 17, FontStyles.Normal, TextAlignmentOptions.Center);
		var trt = t.rectTransform;
		trt.anchorMin = Vector2.zero;
		trt.anchorMax = Vector2.one;
		trt.offsetMin = Vector2.zero;
		trt.offsetMax = Vector2.zero;
		return b;
	}

	// Standard Unity slider hierarchy, built by hand. The anchors matter: Slider DRIVES fillRect's and
	// handleRect's anchorMin/anchorMax along its direction, so both must start anchored to the left edge and
	// stretched vertically, with size carried by sizeDelta. Getting this wrong renders the fill as a giant
	// block covering the row (which is exactly what the first version did).
	private static Slider MakeSlider(Transform parent, float min, float max, float value,
		UnityEngine.Events.UnityAction<float> onChanged)
	{
		var go = new GameObject("Slider", typeof(RectTransform), typeof(Slider), typeof(LayoutElement));
		go.transform.SetParent(parent, false);
		((RectTransform) go.transform).sizeDelta = new Vector2(0, 26);
		var s = go.GetComponent<Slider>();

		var bg = new GameObject("Background", typeof(RectTransform), typeof(Image));
		bg.transform.SetParent(go.transform, false);
		var bgRt = (RectTransform) bg.transform;
		bgRt.anchorMin = new Vector2(0f, 0.35f);
		bgRt.anchorMax = new Vector2(1f, 0.65f);
		bgRt.offsetMin = Vector2.zero;
		bgRt.offsetMax = Vector2.zero;
		bg.GetComponent<Image>().color = new Color(0.17f, 0.19f, 0.24f);

		var fillArea = new GameObject("Fill Area", typeof(RectTransform));
		fillArea.transform.SetParent(go.transform, false);
		var faRt = (RectTransform) fillArea.transform;
		faRt.anchorMin = new Vector2(0f, 0.35f);
		faRt.anchorMax = new Vector2(1f, 0.65f);
		faRt.offsetMin = new Vector2(9f, 0f);
		faRt.offsetMax = new Vector2(-9f, 0f);

		var fill = new GameObject("Fill", typeof(RectTransform), typeof(Image));
		fill.transform.SetParent(faRt, false);
		var fRt = (RectTransform) fill.transform;
		fRt.anchorMin = Vector2.zero;
		fRt.anchorMax = new Vector2(0f, 1f);
		fRt.offsetMin = Vector2.zero;
		fRt.offsetMax = Vector2.zero;
		fRt.sizeDelta = new Vector2(18f, 0f);
		fill.GetComponent<Image>().color = new Color(0.30f, 0.62f, 0.95f);

		var handleArea = new GameObject("Handle Slide Area", typeof(RectTransform));
		handleArea.transform.SetParent(go.transform, false);
		var haRt = (RectTransform) handleArea.transform;
		haRt.anchorMin = Vector2.zero;
		haRt.anchorMax = Vector2.one;
		haRt.offsetMin = new Vector2(9f, 0f);
		haRt.offsetMax = new Vector2(-9f, 0f);

		var handle = new GameObject("Handle", typeof(RectTransform), typeof(Image));
		handle.transform.SetParent(haRt, false);
		var hRt = (RectTransform) handle.transform;
		hRt.anchorMin = Vector2.zero;
		hRt.anchorMax = new Vector2(0f, 1f);
		hRt.offsetMin = Vector2.zero;
		hRt.offsetMax = Vector2.zero;
		hRt.sizeDelta = new Vector2(18f, 0f);
		handle.GetComponent<Image>().color = new Color(0.88f, 0.92f, 1f);

		s.fillRect = fRt;
		s.handleRect = hRt;
		s.targetGraphic = handle.GetComponent<Image>();
		s.direction = Slider.Direction.LeftToRight;
		s.minValue = min;
		s.maxValue = max;
		s.SetValueWithoutNotify(value);
		s.onValueChanged.AddListener(onChanged);
		return s;
	}

	private static Toggle MakeToggle(Transform parent, bool on, UnityEngine.Events.UnityAction<bool> onChanged)
	{
		var go = new GameObject("Toggle", typeof(RectTransform), typeof(Toggle), typeof(LayoutElement));
		go.transform.SetParent(parent, false);
		go.GetComponent<LayoutElement>().preferredWidth = 70;
		var bg = new GameObject("BG", typeof(RectTransform), typeof(Image));
		bg.transform.SetParent(go.transform, false);
		((RectTransform) bg.transform).sizeDelta = new Vector2(30, 30);
		bg.GetComponent<Image>().color = new Color(0.18f, 0.2f, 0.25f);
		var check = new GameObject("Check", typeof(RectTransform), typeof(Image));
		check.transform.SetParent(bg.transform, false);
		((RectTransform) check.transform).sizeDelta = new Vector2(20, 20);
		check.GetComponent<Image>().color = new Color(0.35f, 0.85f, 0.5f);
		var t = go.GetComponent<Toggle>();
		t.targetGraphic = bg.GetComponent<Image>();
		t.graphic = check.GetComponent<Image>();
		t.SetIsOnWithoutNotify(on);
		t.onValueChanged.AddListener(onChanged);
		return t;
	}

	private static void Stretch(RectTransform rt, float padY)
	{
		rt.anchorMin = new Vector2(0, 0.5f);
		rt.anchorMax = new Vector2(1, 0.5f);
		rt.sizeDelta = new Vector2(0, padY > 0 ? padY * 2 : 30);
		rt.anchoredPosition = Vector2.zero;
	}

	private static void Place(RectTransform rt, float aMinX, float aMinY, float aMaxX, float aMaxY, Vector4 off)
	{
		rt.anchorMin = new Vector2(aMinX, aMinY);
		rt.anchorMax = new Vector2(aMaxX, aMaxY);
		rt.offsetMin = new Vector2(off.x, off.y);
		rt.offsetMax = new Vector2(off.z, off.w);
	}
}
