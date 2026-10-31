using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
///     Rebuilds a station's <c>PilotModePanel_*</c> in place: two vertical lists side by side — STATE and
///     TRAILS — with an Auto Cycle row at the top of the state list, and an <see cref="RqStationModePanel" />
///     wired to the station's two dummy animators, its <see cref="DriveTrailRecorder" /> and, where one
///     exists, its <see cref="RqClassificationForkRecorder" /> as the run guard.
///
///     Every row is a Button and the row background IS its state: dark green when on. The numbers in the
///     labels are KEYBOARD keys — 1-6 for STATE, Ctrl+1-6 for TRAILS — so the panel is operable without a
///     headset. They are pulled from <see cref="RqStationModePanel" /> rather than typed here, so the
///     label, the key and the action cannot drift apart.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why a builder and not hand-authored UI.</b> The panel lives inside
///         <c>Station_FlatFimpossible.prefab</c> and is inherited by both station variants and all ten scene
///         instances, so it is authored exactly once — but it also has to be re-derivable when a row is added
///         or a station is re-instanced. Hand-placing eleven anchored RectTransforms is where a panel silently
///         drifts out of alignment with the component that drives it.
///     </para>
///     <para>
///         <b>The panel sizes itself.</b> Both columns are <see cref="VerticalLayoutGroup" /> +
///         <see cref="ContentSizeFitter" />, and the panel root is a <see cref="HorizontalLayoutGroup" /> +
///         <see cref="ContentSizeFitter" /> over them. Add a row and the column grows; add a column and the
///         panel grows. Nothing is positioned by hand except the panel's own world placement, which is left
///         exactly as it was found.
///     </para>
///     <para>
///         The panel's Canvas, CanvasScaler, GraphicRaycaster, TrackedDeviceGraphicRaycaster, world position,
///         rotation and scale are <b>never touched</b> — only its children are replaced. The world placement
///         was registered against the station and re-deriving it is not this tool's job.
///     </para>
/// </remarks>
public static class StationModePanelBuilder
{
    // Layout, in canvas pixels. The panel's own localScale (0.0016 on the stations) turns these into metres.
    private const float ColumnWidth = 320f;
    private const float RowHeight = 78f;
    private const float HeaderHeight = 52f;
    private const float StatusHeight = 52f;
    private const float Spacing = 8f;
    private const float Pad = 14f;
    private const float LabelSize = 27f;
    private const float HeaderSize = 30f;

    private static readonly Color PanelBg = new Color(0.080f, 0.080f, 0.100f, 0.920f);
    private static readonly Color ColumnBg = new Color(0.125f, 0.140f, 0.170f, 0.950f);
    private static readonly Color IdleState = new Color(0.16f, 0.19f, 0.24f, 1f);

    // Kept in step with the defaults on RqStationModePanel. These are the values a REBUILD stamps onto the
    // driver, so if they drift from the component's own field initialisers a rebuild silently reverts the
    // panel's colours.
    private static readonly Color ActiveState = new Color(0.10f, 0.40f, 0.20f, 1f);
    private static readonly Color TransitioningState = new Color(0.55f, 0.28f, 0.06f, 1f);
    private static readonly Color ActionBg = new Color(0.34f, 0.20f, 0.20f, 1f);
    private static readonly Color TextColor = new Color(0.93f, 0.95f, 0.98f, 1f);
    private static readonly Color HeaderColor = new Color(0.62f, 0.72f, 0.86f, 1f);

    [MenuItem("P3T/Rebuild station mode panels (base prefab)")]
    public static void RebuildBasePrefab()
    {
        const string path = "Assets/P3T/Prefabs/Station_FlatFimpossible.prefab";
        var root = PrefabUtility.LoadPrefabContents(path);
        try
        {
            var panel = FindChildStartingWith(root.transform, "PilotModePanel_");
            if (panel == null)
            {
                Debug.LogError("[PanelBuilder] no PilotModePanel_* under " + path);
                return;
            }

            Build(panel, root.transform);
            panel.gameObject.SetActive(true);
            PrefabUtility.SaveAsPrefabAsset(root, path);
            Debug.Log("[PanelBuilder] rebuilt " + panel.name + " in " + path +
                      ". Both station variants and all ten scene instances inherit it.");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    /// <summary>
    ///     Replace every child of <paramref name="panel" /> with the two-column layout and wire the driver.
    ///     <paramref name="stationRoot" /> supplies the animators, the trail recorder and the reference still.
    /// </summary>
    public static RqStationModePanel Build(Transform panel, Transform stationRoot)
    {
        for (var i = panel.childCount - 1; i >= 0; i--) Object.DestroyImmediate(panel.GetChild(i).gameObject);

        var driver = panel.GetComponent<RqStationModePanel>();
        if (driver == null) driver = panel.gameObject.AddComponent<RqStationModePanel>();

        // ---- panel root: horizontal list of columns, sized by its content ----
        var hl = Ensure<HorizontalLayoutGroup>(panel.gameObject);
        hl.spacing = Spacing;
        hl.padding = new RectOffset((int) Pad, (int) Pad, (int) Pad, (int) Pad);
        hl.childAlignment = TextAnchor.UpperCenter;
        hl.childControlWidth = true;
        hl.childControlHeight = true;
        hl.childForceExpandWidth = false;
        hl.childForceExpandHeight = false;

        var fit = Ensure<ContentSizeFitter>(panel.gameObject);
        fit.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        // Background sits UNDER the layout and is excluded from it, so it stretches to whatever size the
        // fitter settles on rather than being laid out as a third column.
        var bg = NewRect("BG", panel);
        var bgImg = bg.gameObject.AddComponent<Image>();
        bgImg.color = PanelBg;
        bgImg.raycastTarget = true;
        Stretch(bg);
        Ensure<LayoutElement>(bg.gameObject).ignoreLayout = true;
        bg.SetAsFirstSibling();

        // ---- STATES column: keys 1-6 ----
        //
        // Every row is a Button, including Auto. It used to be a Toggle, which meant one row on an
        // eleven-row panel read differently from the rest and carried a swatch nothing else had. The row
        // background IS the state now: dark green when the thing it names is on.
        var states = Column("Col_States", panel);
        Header(states, "STATE");
        var autoButton = StateButton(states, "Btn_Auto", RqStationModePanel.AutoLabel);

        var buttons = new Button[RqStationModePanel.RowCount];
        for (var i = 0; i < RqStationModePanel.RowCount; i++)
            buttons[i] = StateButton(states, "Btn_State_" + RqStationModePanel.ValueAt(i),
                RqStationModePanel.LabelAt(i));

        var status = Text(states, "Status", "", LabelSize - 3f, FontStyles.Italic);
        Ensure<LayoutElement>(status.gameObject).preferredHeight = StatusHeight;
        status.color = HeaderColor;

        // ---- TRAILS column: Ctrl + keys 1-6 ----
        //
        // The digits repeat 1-6 here, so the sub-header carries the modifier once rather than every row
        // spelling out "ctrl+". It is a plain label with no Button and no raycast target — a non-button
        // row in a column of buttons has to be unmistakably not pressable.
        var trailsCol = Column("Col_Trails", panel);
        Header(trailsCol, "TRAILS");
        SubHeader(trailsCol, RqStationModePanel.TrailsSubHeader);

        var tRecord = StateButton(trailsCol, "Btn_Record", RqStationModePanel.TrailLabelAt(0));
        var t3D = StateButton(trailsCol, "Btn_3D", RqStationModePanel.TrailLabelAt(1));
        var tPlane = StateButton(trailsCol, "Btn_Plane", RqStationModePanel.TrailLabelAt(2));
        var tCsv = StateButton(trailsCol, "Btn_Csv", RqStationModePanel.TrailLabelAt(3));
        var tStill = StateButton(trailsCol, "Btn_Still", RqStationModePanel.TrailLabelAt(4));

        // Momentary, so it keeps the action styling: there is no state for it to sit in and it must not
        // look like a row that could be "on".
        var bClear = ActionButton(trailsCol, "Btn_Clear", RqStationModePanel.TrailLabelAt(5));

        // ---- wire ----
        var so = new SerializedObject(driver);
        so.FindProperty("autoButton").objectReferenceValue = autoButton;
        var arr = so.FindProperty("stateButtons");
        arr.arraySize = buttons.Length;
        for (var i = 0; i < buttons.Length; i++) arr.GetArrayElementAtIndex(i).objectReferenceValue = buttons[i];
        so.FindProperty("activeStateColor").colorValue = ActiveState;
        so.FindProperty("transitioningStateColor").colorValue = TransitioningState;
        so.FindProperty("idleStateColor").colorValue = IdleState;
        so.FindProperty("trailRecordButton").objectReferenceValue = tRecord;
        so.FindProperty("trailDraw3DButton").objectReferenceValue = t3D;
        so.FindProperty("trailDrawPlaneButton").objectReferenceValue = tPlane;
        so.FindProperty("trailWriteCsvButton").objectReferenceValue = tCsv;
        so.FindProperty("referenceStillButton").objectReferenceValue = tStill;
        so.FindProperty("trailClearButton").objectReferenceValue = bClear;
        so.FindProperty("statusLabel").objectReferenceValue = status;

        if (stationRoot != null)
        {
            var da = FindChildStartingWith(stationRoot, "Dummy_A_");
            var db = FindChildStartingWith(stationRoot, "Dummy_B_");
            if (da != null) so.FindProperty("animatorA").objectReferenceValue = da.GetComponent<Animator>();
            if (db != null) so.FindProperty("animatorB").objectReferenceValue = db.GetComponent<Animator>();
            so.FindProperty("trails").objectReferenceValue = stationRoot.GetComponent<DriveTrailRecorder>();

            // BOTH arming systems, so the lock exists on all ten stations rather than only the three
            // carrying a fork recorder. A station missing one simply contributes no lock from it.
            var guards = so.FindProperty("runGuards");
            guards.ClearArray();
            foreach (var guard in new MonoBehaviour[]
                     {
                         stationRoot.GetComponent<RqStationRunner>(),
                         stationRoot.GetComponent<RqClassificationForkRecorder>()
                     })
            {
                if (guard == null) continue;
                guards.InsertArrayElementAtIndex(guards.arraySize);
                guards.GetArrayElementAtIndex(guards.arraySize - 1).objectReferenceValue = guard;
            }

            var clips = FindChildStartingWith(stationRoot, "MotionClips_");
            if (clips != null)
            {
                var still = FindChildStartingWith(clips, "Still_");
                if (still != null) so.FindProperty("referenceStill").objectReferenceValue = still.gameObject;
            }
        }

        so.ApplyModifiedPropertiesWithoutUndo();

        // Rows start showing the state the components they drive are actually in, so the panel looks right
        // in the prefab and in edit mode rather than only after the first runtime Refresh. RECORD is off
        // because DriveTrailRecorder.recordEnabled is off, the still is off because Still_* ships inactive,
        // and the three draw/write options default on.
        Lit(autoButton, false);
        Lit(tRecord, false);
        Lit(t3D, true);
        Lit(tPlane, true);
        Lit(tCsv, true);
        Lit(tStill, false);

        LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform) panel);
        return driver;
    }

    // ------------------------------------------------------------------ pieces

    private static Transform Column(string name, Transform parent)
    {
        var rt = NewRect(name, parent);
        var img = rt.gameObject.AddComponent<Image>();
        img.color = ColumnBg;
        img.raycastTarget = false;

        var v = rt.gameObject.AddComponent<VerticalLayoutGroup>();
        v.spacing = Spacing;
        v.padding = new RectOffset((int) Pad, (int) Pad, (int) Pad, (int) Pad);
        v.childAlignment = TextAnchor.UpperCenter;
        v.childControlWidth = true;
        v.childControlHeight = true;
        v.childForceExpandWidth = true;
        v.childForceExpandHeight = false;

        var f = rt.gameObject.AddComponent<ContentSizeFitter>();
        f.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        f.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        var le = rt.gameObject.AddComponent<LayoutElement>();
        le.preferredWidth = ColumnWidth;
        return rt;
    }

    private static TextMeshProUGUI Header(Transform parent, string text)
    {
        var t = Text(parent, "Header", text, HeaderSize, FontStyles.Bold);
        t.color = HeaderColor;
        Ensure<LayoutElement>(t.gameObject).preferredHeight = HeaderHeight;
        return t;
    }

    /// <summary>
    ///     A non-interactive line under a column header — here, the <c>ctrl+num</c> hint on TRAILS.
    /// </summary>
    /// <remarks>
    ///     No Button, no Image and <c>raycastTarget = false</c>. In a column where every other row is
    ///     pressable, a row that is not has to be unmistakably not pressable, including to a controller
    ///     ray that would otherwise stop on it.
    /// </remarks>
    private static TextMeshProUGUI SubHeader(Transform parent, string text)
    {
        var t = Text(parent, "SubHeader", text, LabelSize - 4f, FontStyles.Italic);
        t.color = HeaderColor;
        t.raycastTarget = false;
        Ensure<LayoutElement>(t.gameObject).preferredHeight = HeaderHeight * 0.6f;
        return t;
    }

    private static Button StateButton(Transform parent, string name, string label)
    {
        return MakeButton(parent, name, label, IdleState, ActiveState);
    }

    /// <summary>
    ///     Sets a row's resting colour to match the state it will report at runtime.
    /// </summary>
    /// <remarks>
    ///     Writes the Button's ColorBlock rather than the Image, for the same reason
    ///     <see cref="RqStationModePanel" /> does: under a ColorTint transition the Button overwrites
    ///     <c>targetGraphic.color</c> with <c>normalColor</c> on every hover exit, so an Image tint would
    ///     be wiped the first time a ray passed over the row.
    /// </remarks>
    private static void Lit(Button b, bool on)
    {
        if (b == null) return;

        var c = b.colors;
        c.normalColor = on ? ActiveState : IdleState;
        c.selectedColor = c.normalColor;
        c.highlightedColor = Lighten(c.normalColor, 0.18f);
        b.colors = c;
    }

    private static Button ActionButton(Transform parent, string name, string label)
    {
        return MakeButton(parent, name, label, ActionBg, new Color(0.62f, 0.28f, 0.28f, 1f));
    }

    private static Button MakeButton(Transform parent, string name, string label, Color normal, Color pressed)
    {
        var rt = NewRect(name, parent);
        var img = rt.gameObject.AddComponent<Image>();
        img.color = Color.white; // tinted by the Button's ColorBlock, never written directly
        var b = rt.gameObject.AddComponent<Button>();
        b.targetGraphic = img;
        b.transition = Selectable.Transition.ColorTint;
        var c = b.colors;
        c.normalColor = normal;
        c.selectedColor = normal;
        c.highlightedColor = Lighten(normal, 0.18f);
        c.pressedColor = pressed;
        c.disabledColor = new Color(0.2f, 0.2f, 0.2f, 0.5f);
        c.fadeDuration = 0.08f;
        c.colorMultiplier = 1f;
        b.colors = c;
        Ensure<LayoutElement>(rt.gameObject).preferredHeight = RowHeight;

        var t = Text(rt, "Label", label, LabelSize, FontStyles.Bold);
        Stretch((RectTransform) t.transform);
        return b;
    }

    private static TextMeshProUGUI Text(Transform parent, string name, string text, float size, FontStyles style)
    {
        var rt = NewRect(name, parent);
        var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
        t.text = text;
        t.fontSize = size;
        t.fontStyle = style;
        t.color = TextColor;
        t.alignment = TextAlignmentOptions.Center;
        t.enableAutoSizing = false;
        t.raycastTarget = false;
        t.enableWordWrapping = false;
        t.overflowMode = TextOverflowModes.Overflow;
        return t;
    }

    // ------------------------------------------------------------------ helpers

    private static RectTransform NewRect(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.layer = parent.gameObject.layer;
        var rt = (RectTransform) go.transform;
        rt.SetParent(parent, false);
        rt.localScale = Vector3.one;
        rt.localRotation = Quaternion.identity;
        return rt;
    }

    private static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    private static T Ensure<T>(GameObject go) where T : Component
    {
        var c = go.GetComponent<T>();
        return c != null ? c : go.AddComponent<T>();
    }

    private static Color Lighten(Color c, float k)
    {
        return new Color(Mathf.Min(1f, c.r + k), Mathf.Min(1f, c.g + k), Mathf.Min(1f, c.b + k), c.a);
    }

    /// <summary>Set the serialized start state without firing the listener that is not registered yet.</summary>
    private static Transform FindChildStartingWith(Transform parent, string prefix)
    {
        for (var i = 0; i < parent.childCount; i++)
        {
            var c = parent.GetChild(i);
            if (c.name.StartsWith(prefix)) return c;
        }

        return null;
    }
}
