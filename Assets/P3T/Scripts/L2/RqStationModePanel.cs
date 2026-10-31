using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
///     The in-world control surface for one station: which phase it is in, and whether it is drawing drive
///     trails. One per station, on <c>PilotModePanel_&lt;Treatment&gt;</c>.
/// </summary>
/// <remarks>
///     <para>
///         <b>It drives the ANIMATOR, and nothing else.</b> Every button writes the two declared parameters
///         on <c>Motions_Controller</c> — <c>Auto</c> (bool) and <c>State</c> (int) — on both of the
///         station's dummy animators, using exactly the values
///         <see cref="RqClassificationForkRecorder" /> and <see cref="RqAutoArmSequencer" /> use. It holds
///         no lifecycle of its own, applies no forces, and touches no joint. If this component is removed
///         the station still cycles; it just cannot be commanded from inside the headset.
///     </para>
///     <para>
///         <b>State values are the controller's, not this class's.</b>
///         <c>0 Pause · 1 Snap · 2 Hold · 3 Contact · 4 Drive</c>. They come from the Any-State transitions
///         in <c>Motions_Controller</c>, which are all guarded by <c>Auto == false</c> — so a State press
///         must clear <c>Auto</c> first or it is silently ignored. The four reverse-playback states
///         (<c>SR</c>, <c>HR</c>, <c>CR</c>, <c>DR</c>) have no State transition into them and are
///         deliberately not exposed: they are reachable only through the Auto cycle.
///     </para>
///     <para>
///         <b>The numbers on the labels are KEYBOARD keys, not State values.</b> They used to be State
///         values, which made the label and the key disagree by one from Snap onwards. The panel is now
///         operable without a headset: <c>1</c> Auto Cycle, <c>2</c>–<c>6</c> the five states, and
///         <c>Ctrl</c> + the same digits for the Trails column. The mapping lives in <see cref="Rows" />
///         and <see cref="TrailRows" /> so the label, the key and the action cannot drift apart.
///     </para>
///     <para>
///         <b>Every ACTIVE station responds to the same key.</b> There is no focus and no "selected"
///         station — each panel reads the keyboard itself, so one press commands every station that is
///         active in the hierarchy. That is deliberate: pressing <c>1</c> stops the whole rig cycling
///         rather than whichever station happens to be nearest. Stations deactivated by
///         <c>RqClassificationForkRecorder.Isolate</c> do not run <c>Update</c>, so they correctly do not
///         respond.
///     </para>
///     <para>
///         <b>Both animators or neither.</b> A press that reached only one dummy would desynchronise the
///         pair, which is the one failure this panel could introduce. Both references are required and the
///         component says so at Awake rather than half-working.
///     </para>
///     <para>
///         <b>Dwell is the operator's problem, not this panel's.</b> A State change takes a measured 180
///         fixed steps (2.0 s at 90 Hz) to blend, and the transitions have no interruption source, so a
///         second press during a blend is deferred rather than dropped. The highlight reads the animator's
///         own state, so it shows where the station really is, not the last button pressed.
///     </para>
///     <para>
///         Replaces the panel's previous wiring to <c>PilotRunController</c>, which commanded joint
///         targets directly and never touched the animator at all. That class is deleted; the two
///         mechanisms were confused once before and cost an attribution run.
///     </para>
/// </remarks>
[DisallowMultipleComponent]
public sealed class RqStationModePanel : MonoBehaviour
{
    private const string AutoParam = "Auto";
    private const string StateParam = "State";

    /// <summary>
    ///     Frames to re-poll the animator after a command, before falling back to the idle cadence.
    /// </summary>
    /// <remarks>
    ///     A command writes an animator PARAMETER. The transition it opens does not exist until the
    ///     Animator next evaluates, which is not guaranteed to be the same frame the button was clicked —
    ///     the click is dispatched from the EventSystem's own Update and the Animator has its own update
    ///     phase. Polling a few frames is what makes the highlight look instant. A single next-frame check
    ///     would usually work and would occasionally miss.
    /// </remarks>
    private const int CommandPollFrames = 4;

    /// <summary>
    ///     The six digit keys, in panel order. Index is the row; <c>Ctrl</c> selects the Trails column.
    /// </summary>
    private static readonly Key[] DigitKeys =
    {
        Key.Digit1, Key.Digit2, Key.Digit3, Key.Digit4, Key.Digit5, Key.Digit6
    };

    /// <summary>
    ///     Button label and the <c>State</c> value it writes, in panel order. Index is NOT the State value
    ///     and it is NOT the key digit either — the states occupy keys 2–6 because key 1 is Auto Cycle.
    ///     Read <see cref="StateRow.Value" /> for the animator parameter and <see cref="StateRow.Label" />
    ///     for what the operator sees.
    /// </summary>
    private static readonly StateRow[] Rows =
    {
        new StateRow(0, "2 - Pause"),
        new StateRow(1, "3 - Snap"),
        new StateRow(2, "4 - Hold"),
        new StateRow(3, "5 - Contact"),
        new StateRow(4, "6 - Drive")
    };

    /// <summary>Trails column labels, in panel order. Reached with <c>Ctrl</c> + the digit.</summary>
    private static readonly string[] TrailRows =
    {
        "1 - Record Drives",
        "2 - Draw 3D Path",
        "3 - Draw On Plane",
        "4 - Write CSV",
        "5 - Reference Still",
        "6 - Clear Trails"
    };

    /// <summary>The Auto Cycle row's label. Key 1, and the only row in the States column that is not a State.</summary>
    public const string AutoLabel = "1 - Auto Cycle";

    /// <summary>The non-interactive sub-header under the Trails header.</summary>
    public const string TrailsSubHeader = "ctrl+num";

    [Header("The two dummies this panel commands (both required)")]
    [Tooltip("Dummy_A_<station>'s Animator. Commanded together with B — a State written to only one of " +
             "them desynchronises the pair.")]
    [SerializeField] private Animator animatorA;

    [Tooltip("Dummy_B_<station>'s Animator.")]
    [SerializeField] private Animator animatorB;

    [Header("States column — keys 1-6")]
    [Tooltip("Key 1. Auto = true hands the station to the controller's own chained exit-time cycle and " +
             "disables every State transition. Auto = false is what makes the State buttons do anything.\n\n" +
             "A Button rather than a Toggle so every row on the panel reads the same way: the row itself " +
             "goes dark green when the thing it names is ON.")]
    [SerializeField] private Button autoButton;

    [Tooltip("The five state buttons, in the same order as the labels: Pause, Snap, Hold, Contact, Drive — " +
             "keys 2 to 6. Wired at Awake, so no persistent UnityEvent listener is needed or wanted: a " +
             "listener bound by index can be silently re-pointed.")]
    [SerializeField] private Button[] stateButtons = new Button[5];

    [Tooltip("Colour applied to the button whose State the animator has SETTLED in, and to any Trails row " +
             "that is currently ON. Deliberately dark: for a state it only ever appears once a blend has " +
             "finished, so it reads as \"this is where the station is\" rather than \"this is where it is " +
             "going\".")]
    [SerializeField] private Color activeStateColor = new Color(0.10f, 0.40f, 0.20f);

    [Tooltip("Colour applied to the button whose State the animator is currently blending INTO. While a " +
             "blend is running NO button shows the settled colour — the destination shows this instead, so " +
             "the panel cannot claim the station has arrived somewhere it is still travelling to.\n\n" +
             "A State change takes a measured 180 fixed steps (2.0 s at 90 Hz) and the transitions have no " +
             "interruption source, so this is on screen for a full two seconds and is the honest reading " +
             "for all of it.")]
    [SerializeField] private Color transitioningStateColor = new Color(0.55f, 0.28f, 0.06f);

    [SerializeField] private Color idleStateColor = new Color(0.16f, 0.19f, 0.24f);

    [Header("Trails column — Ctrl + keys 1-6")]
    [Tooltip("This station's DriveTrailRecorder. Empty, the one on the station root is used.")]
    [SerializeField] private DriveTrailRecorder trails;

    [SerializeField] private Button trailRecordButton;
    [SerializeField] private Button trailDraw3DButton;
    [SerializeField] private Button trailDrawPlaneButton;
    [SerializeField] private Button trailWriteCsvButton;

    [Tooltip("The station's reference still image (Still_Bind / Still_NoBind / Still_Flat). Toggling it is " +
             "the only surviving function of the old panel's '6 Toggle Video' button.")]
    [SerializeField] private GameObject referenceStill;

    [SerializeField] private Button referenceStillButton;

    [Tooltip("Momentary, not a state — it never shows the ON colour because there is nothing to be on.")]
    [SerializeField] private Button trailClearButton;

    [Header("Run guards")]
    [Tooltip("Every system on this station that can be mid-run: its RqStationRunner, and on the three " +
             "OurSolution stations its RqClassificationForkRecorder. Empty, both are resolved from the " +
             "station root.\n\n" +
             "While ANY of them is running the panel is INERT — keyboard and buttons both. Isolate() " +
             "deactivates the OTHER station roots but leaves the armed one live, so without this a stray " +
             "keypress writes State straight onto the animators the run is driving and voids the walk in a " +
             "way the sealed CSV cannot show.\n\n" +
             "WAS A SINGLE FORK-RECORDER FIELD until 2026-08-29, which meant the lock existed on only the " +
             "three stations carrying one. The other seven ran the whole protocol with a LIVE panel: the " +
             "runner drives the same animators and nothing stopped a keypress overwriting State mid-run.")]
    [SerializeField] private List<MonoBehaviour> runGuards = new List<MonoBehaviour>();

    [Header("Readout")]
    [Tooltip("Optional. Shows the animator's live state name and the trail count, so the panel reports " +
             "what the station is doing rather than what was last asked of it.")]
    [SerializeField] private TMP_Text statusLabel;

    [Min(0.05f)]
    [SerializeField] private float refreshSeconds = 0.2f;

    private int _lastHighlighted = -1;
    private int _lastPending = -1;
    private bool _lastBlending;
    private int _lastTrailMask = -1;
    private bool _warnedUnknownGuard;
    private int _commandPollFrames;
    private float _timer;

    /// <summary>
    ///     True while this station's recorder is mid-run, during which the panel accepts nothing.
    /// </summary>
    /// <remarks>
    ///     Enforced inside every command rather than by clearing <c>Button.interactable</c>. Disabling the
    ///     buttons would swap their ColorBlock for <c>disabledColor</c> and take the state highlight with
    ///     it — losing the readout during exactly the window it is most worth having. The buttons stay
    ///     lit and honest; they simply do nothing, and the status label says why.
    /// </remarks>
    private bool Locked
    {
        get
        {
            if (runGuards == null) return false;

            for (var i = 0; i < runGuards.Count; i++)
            {
                MonoBehaviour g = runGuards[i];
                if (g == null || !g.enabled) continue;

                // Explicit per type rather than through a shared interface: the two systems are deliberately
                // unrelated, and naming them here is what makes it obvious that adding a third is a
                // decision rather than an accident.
                if (g is RqStationRunner runner)
                {
                    if (runner.Running) return true;
                }
                else if (g is RqClassificationForkRecorder fork)
                {
                    if (fork.IsRunning) return true;
                }
                else if (!_warnedUnknownGuard)
                {
                    _warnedUnknownGuard = true;
                    Debug.LogError("[ModePanel] " + name + ": '" + g.GetType().Name + "' is in runGuards " +
                                   "but this panel does not know how to ask it whether it is running, so " +
                                   "it CANNOT lock the panel. A guard that silently never locks is worse " +
                                   "than no guard. Remove it, or teach Locked about it.", this);
                }
            }

            return false;
        }
    }

    /// <summary>Adds a guard if it exists and is not already listed. Order does not matter.</summary>
    private void AddGuard(MonoBehaviour guard)
    {
        if (guard == null) return;
        if (runGuards == null) runGuards = new List<MonoBehaviour>();
        if (!runGuards.Contains(guard)) runGuards.Add(guard);
    }

    private void Awake()
    {
        ResolveRefs();

        if (animatorA == null || animatorB == null)
            Debug.LogError("[ModePanel] " + StationName() + ": both dummy animators must be assigned. " +
                           "Commanding one and not the other desynchronises the pair, so this panel " +
                           "commands neither.", this);

        for (var i = 0; i < stateButtons.Length && i < Rows.Length; i++)
        {
            if (stateButtons[i] == null) continue;
            var value = Rows[i].Value;
            stateButtons[i].onClick.AddListener(() => CommandState(value));
        }

        if (autoButton != null) autoButton.onClick.AddListener(ToggleAuto);
        if (trailRecordButton != null) trailRecordButton.onClick.AddListener(ToggleTrailRecording);
        if (trailDraw3DButton != null) trailDraw3DButton.onClick.AddListener(ToggleTrailDraw3D);
        if (trailDrawPlaneButton != null) trailDrawPlaneButton.onClick.AddListener(ToggleTrailDrawPlane);
        if (trailWriteCsvButton != null) trailWriteCsvButton.onClick.AddListener(ToggleTrailWriteCsv);
        if (referenceStillButton != null) referenceStillButton.onClick.AddListener(ToggleReferenceStill);
        if (trailClearButton != null) trailClearButton.onClick.AddListener(ClearTrails);
    }

    private void Update()
    {
        ReadKeyboard();

        // Straight after a command, poll every frame for a few frames so the highlight moves as soon as
        // the animator actually opens the transition, instead of waiting out the refresh interval. This
        // changes WHEN the panel looks, never WHAT it concludes — see CommandState.
        if (_commandPollFrames > 0)
        {
            _commandPollFrames--;
            _timer = 0f;
            Refresh();
            return;
        }

        _timer += Time.unscaledDeltaTime;
        if (_timer < refreshSeconds) return;
        _timer = 0f;
        Refresh();
    }

    private void Reset()
    {
        ResolveRefs();
    }

    // ---- keyboard ----

    /// <summary>
    ///     Digits 1-6 drive the States column, <c>Ctrl</c> + the same digits drive Trails.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Read per panel rather than by any central dispatcher, which is what makes every ACTIVE
    ///         station respond to one press. There is no notion of a focused station and deliberately so.
    ///     </para>
    ///     <para>
    ///         The project is Input System only (<c>activeInputHandler: 1</c>), so this reads
    ///         <c>Keyboard.current</c> and never <c>UnityEngine.Input</c>. A null keyboard is normal — a
    ///         headset build with no keyboard attached — and is not an error.
    ///     </para>
    /// </remarks>
    private void ReadKeyboard()
    {
        if (Locked) return;

        var kb = Keyboard.current;
        if (kb == null) return;

        var ctrl = kb.ctrlKey.isPressed;

        for (var i = 0; i < DigitKeys.Length; i++)
        {
            if (!kb[DigitKeys[i]].wasPressedThisFrame) continue;

            if (ctrl) TrailsRow(i);
            else StatesRow(i);
        }
    }

    /// <summary>Row <paramref name="i" /> of the States column: 0 is Auto Cycle, 1-5 are the five states.</summary>
    private void StatesRow(int i)
    {
        if (i == 0)
        {
            ToggleAuto();
            return;
        }

        var row = i - 1;
        if (row >= 0 && row < Rows.Length) CommandState(Rows[row].Value);
    }

    /// <summary>Row <paramref name="i" /> of the Trails column, in the order the labels are built.</summary>
    private void TrailsRow(int i)
    {
        switch (i)
        {
            case 0: ToggleTrailRecording(); break;
            case 1: ToggleTrailDraw3D(); break;
            case 2: ToggleTrailDrawPlane(); break;
            case 3: ToggleTrailWriteCsv(); break;
            case 4: ToggleReferenceStill(); break;
            case 5: ClearTrails(); break;
        }
    }

    // ---- commands ----

    /// <summary>
    ///     Writes one <c>State</c> value to both animators, clearing <c>Auto</c> first because every
    ///     State-driven transition is guarded by <c>Auto == false</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The press changes when the panel LOOKS, not what it concludes.</b> It never paints a
    ///         button from the fact that the button was pressed — it only shortens the interval before the
    ///         animator is asked again. So a command the state machine declines to act on produces no
    ///         highlight at all, which is the honest outcome and the one that matters: transitions have no
    ///         interruption source, so a press landing mid-blend is DEFERRED by the controller, and
    ///         painting it orange on press would assert a transition that has not been accepted and will
    ///         not begin for up to two seconds.
    ///     </para>
    /// </remarks>
    public void CommandState(int value)
    {
        if (Locked) return;

        SetAutoParam(false);
        Write(animatorA, value);
        Write(animatorB, value);
        _commandPollFrames = CommandPollFrames;
    }

    private static void Write(Animator a, int value)
    {
        if (a == null) return;
        a.SetBool(AutoParam, false);
        a.SetInteger(StateParam, value);
    }

    /// <summary>
    ///     Flips <c>Auto</c> from whatever the ANIMATOR currently reports, not from a cached UI value.
    /// </summary>
    /// <remarks>
    ///     Reading the animator means the key and the button agree with the station even when something
    ///     else set the parameter — the auto-arm sequencer and the fork recorder both write it directly.
    /// </remarks>
    public void ToggleAuto()
    {
        if (Locked) return;
        SetAuto(!AutoIsOn());
    }

    /// <summary>
    ///     Auto ON hands the station to the controller's chained exit-time cycle. The current <c>State</c>
    ///     integer is deliberately left where it is: it is inert while Auto is set, and clearing it would
    ///     make the station jump when Auto is switched back off.
    /// </summary>
    public void SetAuto(bool on)
    {
        if (Locked) return;
        SetAutoParam(on);
        _commandPollFrames = CommandPollFrames;
    }

    private void SetAutoParam(bool on)
    {
        if (animatorA != null) animatorA.SetBool(AutoParam, on);
        if (animatorB != null) animatorB.SetBool(AutoParam, on);
    }

    private bool AutoIsOn()
    {
        var a = animatorA;
        return a != null && a.runtimeAnimatorController != null && a.GetBool(AutoParam);
    }

    public void ToggleTrailRecording()
    {
        if (Locked || trails == null) return;
        trails.SetRecordEnabled(!trails.recordEnabled);
    }

    public void ToggleTrailDraw3D()
    {
        if (Locked || trails == null) return;
        trails.SetDraw3D(!trails.draw3D);
    }

    public void ToggleTrailDrawPlane()
    {
        if (Locked || trails == null) return;
        trails.SetDrawProjected(!trails.drawProjected);
    }

    public void ToggleTrailWriteCsv()
    {
        if (Locked || trails == null) return;
        trails.SetWriteCsv(!trails.writeCsvOnDriveEnd);
    }

    public void ClearTrails()
    {
        if (Locked || trails == null) return;
        trails.ClearTrails();
    }

    public void ToggleReferenceStill()
    {
        if (Locked || referenceStill == null) return;
        referenceStill.SetActive(!referenceStill.activeSelf);
    }

    // ---- readout ----

    /// <summary>
    ///     Highlight and status come from the ANIMATOR, never from the last button pressed. A command issued
    ///     during a 2 s blend is deferred by the controller, so the two can legitimately disagree for two
    ///     seconds and the honest picture is the one the state machine is in.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Three colours, and the settled one is exclusive.</b> While the animator is blending, the
    ///         DESTINATION button takes <see cref="transitioningStateColor" /> and every other button —
    ///         including the one being left — goes idle. <see cref="activeStateColor" /> appears only when
    ///         no transition is running. So the panel cannot show "arrived" during the two seconds the
    ///         station is still travelling, which is exactly the window an operator is most likely to
    ///         misread.
    ///     </para>
    ///     <para>
    ///         <b>The repaint guard has to watch the transition, not just the state.</b> It previously
    ///         compared only the current state index — but <c>GetCurrentAnimatorStateInfo</c> keeps
    ///         reporting the state being LEFT for the whole blend, so that index does not move when a
    ///         transition begins or ends. A guard on it alone would never repaint for a transition at all.
    ///     </para>
    ///     <para>
    ///         <b>The Trails rows use the same dark green, and it means the same thing.</b> The row is
    ///         green when the thing it names is ON. Clear Trails is momentary and is never green, because
    ///         there is no state for it to be in.
    ///     </para>
    /// </remarks>
    private void Refresh()
    {
        var current = CurrentStateIndex();
        var blending = IsBlending();
        var pending = blending ? PendingStateIndex() : -1;

        if (current != _lastHighlighted || pending != _lastPending || blending != _lastBlending)
        {
            _lastHighlighted = current;
            _lastPending = pending;
            _lastBlending = blending;

            for (var i = 0; i < stateButtons.Length; i++)
            {
                // blending is tracked separately from pending because a transition into one of the four
                // reverse-playback states (SR/HR/CR/DR) matches no row and yields pending -1. That is still a
                // transition, and showing the settled colour through it would be a lie.
                var want = blending
                    ? i == pending ? transitioningStateColor : idleStateColor
                    : i == current ? activeStateColor : idleStateColor;

                Tint(stateButtons[i], want);
            }
        }

        RefreshTrailRows();

        if (statusLabel == null) return;

        if (Locked)
        {
            // Says why the panel is not responding. Without it a locked panel is indistinguishable from a
            // broken one, and the operator's next move is to press harder.
            statusLabel.text = "RUN ARMED — panel locked";
            return;
        }

        var a = animatorA;
        var state = "no animator";
        if (a != null && a.isActiveAndEnabled && a.runtimeAnimatorController != null)
        {
            // Names the DESTINATION rather than saying only "blending", so the label and the orange button
            // are reporting the same fact rather than two halves of it.
            state = blending
                ? "→ " + (pending >= 0 ? StateName(pending) : "…")
                : StateName(current);
            if (a.GetBool(AutoParam)) state += "   AUTO";
        }

        if (trails != null && trails.recordEnabled)
            state += trails.IsRecording ? "   ● REC" : "   trails " + trails.DriveCount;

        statusLabel.text = state;
    }

    /// <summary>
    ///     Paints Auto and the four stateful Trails rows green-when-on, behind one bitmask comparison so
    ///     the common case costs nothing.
    /// </summary>
    private void RefreshTrailRows()
    {
        var mask = 0;
        if (AutoIsOn()) mask |= 1 << 0;
        if (trails != null)
        {
            if (trails.recordEnabled) mask |= 1 << 1;
            if (trails.draw3D) mask |= 1 << 2;
            if (trails.drawProjected) mask |= 1 << 3;
            if (trails.writeCsvOnDriveEnd) mask |= 1 << 4;
        }

        if (referenceStill != null && referenceStill.activeSelf) mask |= 1 << 5;

        if (mask == _lastTrailMask) return;
        _lastTrailMask = mask;

        Tint(autoButton, (mask & (1 << 0)) != 0 ? activeStateColor : idleStateColor);
        Tint(trailRecordButton, (mask & (1 << 1)) != 0 ? activeStateColor : idleStateColor);
        Tint(trailDraw3DButton, (mask & (1 << 2)) != 0 ? activeStateColor : idleStateColor);
        Tint(trailDrawPlaneButton, (mask & (1 << 3)) != 0 ? activeStateColor : idleStateColor);
        Tint(trailWriteCsvButton, (mask & (1 << 4)) != 0 ? activeStateColor : idleStateColor);
        Tint(referenceStillButton, (mask & (1 << 5)) != 0 ? activeStateColor : idleStateColor);
    }

    /// <summary>
    ///     Writes a Button's resting colour.
    /// </summary>
    /// <remarks>
    ///     The highlight goes on the Button's ColorBlock, NOT on the target Image's colour. Under a
    ///     ColorTint transition the Button writes <c>targetGraphic.color = normalColor</c> on every hover
    ///     exit, so an Image tint set from here would be wiped the first time a controller ray passed
    ///     over it.
    /// </remarks>
    private static void Tint(Button button, Color want)
    {
        if (button == null) return;

        var c = button.colors;
        if (c.normalColor == want && c.selectedColor == want) return;
        c.normalColor = want;
        c.selectedColor = want;
        button.colors = c;
    }

    /// <summary>Row index of the animator's current state, or -1 if it is in none of the five.</summary>
    private int CurrentStateIndex()
    {
        var a = animatorA;
        if (a == null || !a.isActiveAndEnabled || a.runtimeAnimatorController == null) return -1;
        var info = a.GetCurrentAnimatorStateInfo(0);
        for (var i = 0; i < Rows.Length; i++)
            if (info.IsName(Rows[i].AnimatorState))
                return i;
        return -1;
    }

    /// <summary>
    ///     Whether layer 0 is mid-blend, asked independently of where it is blending TO.
    /// </summary>
    /// <remarks>
    ///     Separate from <see cref="PendingStateIndex" /> on purpose. A transition into one of the four
    ///     reverse-playback states matches no row and returns -1, which is indistinguishable from "not
    ///     transitioning" if the two questions share one answer. They do not share one here.
    /// </remarks>
    private bool IsBlending()
    {
        var a = animatorA;
        return a != null && a.isActiveAndEnabled && a.runtimeAnimatorController != null &&
               a.IsInTransition(0);
    }

    /// <summary>
    ///     Row index of the state the animator is blending INTO, or -1 when it is not blending or the
    ///     destination is none of the five.
    /// </summary>
    private int PendingStateIndex()
    {
        var a = animatorA;
        if (a == null || !a.isActiveAndEnabled || a.runtimeAnimatorController == null) return -1;
        if (!a.IsInTransition(0)) return -1;

        var info = a.GetNextAnimatorStateInfo(0);
        for (var i = 0; i < Rows.Length; i++)
            if (info.IsName(Rows[i].AnimatorState))
                return i;
        return -1;
    }

    private static string StateName(int rowIndex)
    {
        return rowIndex >= 0 && rowIndex < Rows.Length ? Rows[rowIndex].AnimatorState : "—";
    }

    // ---- wiring ----

    /// <summary>
    ///     Resolve what can be resolved from the frozen station naming convention, so a re-instanced station
    ///     is not silently half-wired. Anything already assigned is left alone.
    /// </summary>
    private void ResolveRefs()
    {
        var root = StationRoot();
        if (root == null) return;

        if (animatorA == null)
        {
            var d = root.Find("Dummy_A_" + root.name);
            if (d != null) animatorA = d.GetComponent<Animator>();
        }

        if (animatorB == null)
        {
            var d = root.Find("Dummy_B_" + root.name);
            if (d != null) animatorB = d.GetComponent<Animator>();
        }

        if (trails == null) trails = root.GetComponent<DriveTrailRecorder>();

        // Both systems, on every station. The runner is present on all ten; the fork recorder only on the
        // three OurSolution stations, and a missing one simply contributes no lock.
        AddGuard(root.GetComponent<RqStationRunner>());
        AddGuard(root.GetComponent<RqClassificationForkRecorder>());
    }

    private Transform StationRoot()
    {
        var t = transform;
        while (t != null && !t.name.StartsWith("Station_")) t = t.parent;
        return t;
    }

    private string StationName()
    {
        var r = StationRoot();
        return r != null ? r.name : name;
    }

    private readonly struct StateRow
    {
        public readonly int Value;
        public readonly string Label;

        /// <summary>
        ///     The animator state the value lands in, per <c>Motions_Controller</c>'s Any-State transitions.
        ///     Used only to highlight the live state; nothing is commanded by name.
        /// </summary>
        public string AnimatorState => Value == 0 ? "Pause"
            : Value == 1 ? "Snap"
            : Value == 2 ? "Hold"
            : Value == 3 ? "Contact"
            : "Drive";

        public StateRow(int value, string label)
        {
            Value = value;
            Label = label;
        }
    }

    /// <summary>Panel-order labels for the States column, so the builder and the runtime cannot drift apart.</summary>
    public static string LabelAt(int rowIndex)
    {
        return rowIndex >= 0 && rowIndex < Rows.Length ? Rows[rowIndex].Label : "";
    }

    /// <summary>Panel-order State values.</summary>
    public static int ValueAt(int rowIndex)
    {
        return rowIndex >= 0 && rowIndex < Rows.Length ? Rows[rowIndex].Value : 0;
    }

    /// <summary>Number of state rows the panel is built with.</summary>
    public static int RowCount => Rows.Length;

    /// <summary>Panel-order labels for the Trails column.</summary>
    public static string TrailLabelAt(int rowIndex)
    {
        return rowIndex >= 0 && rowIndex < TrailRows.Length ? TrailRows[rowIndex] : "";
    }

    /// <summary>Number of Trails rows the panel is built with.</summary>
    public static int TrailRowCount => TrailRows.Length;
}
