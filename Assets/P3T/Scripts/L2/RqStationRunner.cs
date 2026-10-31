// =====================================================================================
// THE PROTOCOL, AND NOTHING ELSE.
//
// This drives one station through Pause -> SNAP -> HOLD -> CONTACT -> DRIVE at the declared
// dwell schedule with Auto = false, and tells its observers when each phase begins and when
// every step passes. It records nothing, measures nothing, gates nothing, and writes no file.
//
// IT CONTAINS NO STATION-SPECIFIC LOGIC AND MUST NOT ACQUIRE ANY. It never asks which station
// it is on, never names a collider, a marker, a treatment or a presentation, and holds no
// reference to a probe, a solver or a recorder. Everything that differs between the ten
// stations is a serialized declaration on the instrument that needs it.
//
// One station at a time, enforced: arming refuses while another runner is running.
// =====================================================================================
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
///     Walks one station through the declared phase protocol and reports it to its observers.
/// </summary>
/// <remarks>
///     <para>
///         <b>Auto = false is what makes the walk addressable.</b> The controller's transitions into the
///         State-driven poses are all gated on it; with Auto true the cycle keeps advancing on clip end
///         and a commanded State is ignored. Both animators, or the two dummies desynchronise.
///     </para>
///     <para>
///         <b>Dwells are sample sizes, not settling criteria.</b> The defaults are inherited from
///         <see cref="RqClassificationForkRecorder" />'s declared schedule rather than chosen here, so a
///         Drive run's setup is comparable with an approach run's. A State change takes a measured 180
///         steps to blend, so a dwell shorter than that commands the next phase mid-blend.
///     </para>
///     <para>
///         <b>The DRIVE window's length is the drive clip.</b> The runner ends the walk when that clip
///         has played through once on both animators. No duration is stated here, because a duration
///         written into a component goes stale the moment a clip is re-authored and a reader is then left
///         with a declared figure that disagrees with what ran.
///     </para>
/// </remarks>
[DefaultExecutionOrder(-200)]
[DisallowMultipleComponent]
public sealed class RqStationRunner : MonoBehaviour
{
    private const string AutoParam = "Auto";
    private const string StateParam = "State";

    /// <summary>The runner currently executing, or null. One station at a time.</summary>
    /// <remarks>
    ///     Static because the constraint is global, not per-station: the isolation below deactivates the
    ///     other station roots, so two runners arming together would each disable the other's station
    ///     and both runs would be measuring a rig that had been switched off underneath them. Cleared in
    ///     <c>OnDisable</c>, which a Play-mode stop always reaches.
    /// </remarks>
    public static RqStationRunner Active { get; private set; }

    [Header("Identity — a label, never behaviour")]
    [Tooltip("Station label, used in this runner's log lines only. Nothing here branches on it and " +
             "nothing may be added that does.")]
    [SerializeField] private string stationId = "";

    [Header("The two animators — both, or the dummies desynchronise")]
    [SerializeField] private Animator animatorA;

    [SerializeField] private Animator animatorB;

    [Header("Observers — whatever wants to watch this run")]
    [Tooltip("Told when each phase begins and when every step passes, and polled for readiness before " +
             "arming and for a veto after each callback. The runner does not know or care what any of " +
             "them do; an empty list is a valid walk that records nothing.")]
    [SerializeField] private List<RqRunObserver> observers = new List<RqRunObserver>();

    [Header("Dwell schedule — SAMPLE SIZES, in fixed steps")]
    [Tooltip("Pause. 180 is inherited from RqClassificationForkRecorder's declared schedule, not chosen " +
             "here: a State change takes a measured 180 steps to blend, so a shorter dwell commands the " +
             "next phase while this one is still blending.")]
    [Min(1)]
    [SerializeField] private int dwellPauseSteps = 180;

    [Min(1)]
    [SerializeField] private int dwellSnapSteps = 360;

    [Tooltip("Hold. 720 as of 2026-08-28, doubled from 360 because BOTH transitions into Hold were " +
             "lengthened 2 s -> 4 s in the controller (Snap->Hold on the Auto path, AnyState->Hold on the " +
             "State path). A dwell is the wait AFTER commanding its state, so this one has to cover the " +
             "blend into Hold: at 360 against a 360-step blend, CONTACT was commanded on the exact step " +
             "the blend completed, leaving zero settled dwell. 720 restores 360 settled steps.\n\n" +
             "If either into-Hold transition duration changes again, this field changes with it. The two " +
             "are coupled and nothing checks the coupling.")]
    [Min(1)]
    [SerializeField] private int dwellHoldSteps = 720;

    [Min(1)]
    [SerializeField] private int dwellContactSteps = 900;

    [Tooltip("Fixed steps to hold after the drive clip has played through once, before the run ends. " +
             "0 = end as soon as the clip completes.\n\n" +
             "NOT a criterion and NOT the window length — the window length is the clip. It exists only " +
             "because a contact can still be resolving on the step the clip ends, and it is reported by " +
             "every observer that seals a file so it can never be mistaken for part of the window.")]
    [Min(0)]
    [SerializeField] private int settleStepsAfterClip;

    [Tooltip("The animator state that IS the drive. Motions_Controller calls it 'Drive' (State = 4). " +
             "MEASURED DEFECT 2026-08-26, which is why this is checked by name at all. Without it the " +
             "window closed with ZERO steps: on the step State=4 is commanded the animator has not " +
             "transitioned yet, so GetCurrentAnimatorStateInfo still reports the SOURCE state - and that " +
             "state had been held for 900 dwell steps, so its normalizedTime was already past 1 and the " +
             "clip read as complete before the drive had begun. The same one-step reporting lag is " +
             "documented on DriveTrailRecorder's endGraceSteps. The window now cannot close until both " +
             "animators actually report THIS state.")]
    [SerializeField] private string driveStateName = "Drive";

    [Header("Arming")]
    [Tooltip("Arm as soon as every observer reports ready.\n\n" +
             "FALSE on every station by default, and it has to be: ten runners all set to arm when ready " +
             "would all fire at once, and each one's isolation would switch off the stations the others " +
             "were measuring. Enable it on the ONE station being run, or arm that station with the " +
             "hotkey. Arming is refused outright while another runner is running.")]
    [SerializeField] private bool armWhenReady;

    [Tooltip("Arms this station. Input System only. The press is a REQUEST: input is polled in Update, a " +
             "rendered-frame boundary with no fixed relationship to the physics clock, so it is deferred " +
             "and consumed in FixedUpdate.")]
    [SerializeField] private Key armKey = Key.F10;

    [Tooltip("A deferred arm request is consumed only on a global fixed step that is an exact multiple " +
             "of this. 90 is one second at the declared 90 Hz and is the value the fork recorder already " +
             "runs; kept identical so a Drive run and an approach run start on the same phase of the " +
             "physics clock.")]
    [Min(1)]
    [SerializeField] private int armAlignToFixedStepMultiple = 90;

    [Tooltip("Log the readiness reason while waiting to arm, once per this many fixed steps. 0 = never. " +
             "A run that never arms because one instrument never resolved is otherwise silent.")]
    [Min(0)]
    [SerializeField] private int readinessLogEverySteps = 180;

    [Header("Isolation — runtime only, reverts when Play stops")]
    [Tooltip("Deactivates every other Station_* root for the duration, so the watched station is the " +
             "only thing simulating and the arms all carry the same load. Nothing is saved, so stopping " +
             "Play restores the scene regardless.")]
    [SerializeField] private bool isolateOtherStations = true;

    // ----- state -----------------------------------------------------------------------------------

    private readonly List<GameObject> isolated = new List<GameObject>();

    private bool running, finished, armRequested;
    private string armRequestSource;
    private int armedAtFixedStep = -1;

    private int step;
    private bool skipFirstStep;
    private int sequenceCursor;
    private int cursorDeadline;
    private int phaseState = -1;
    private int phaseEnteredAtStep;

    private bool clipComplete;
    private int clipCompleteAtStep = -1;

    private int framesThisRun;
    private int driveStateHash;
    private bool driveStateSeen;

    /// <summary>True between arming and the end of the run.</summary>
    public bool Running => running;

    /// <summary>True once this runner has completed or ended a run. One run per Play session.</summary>
    public bool Finished => finished;

    /// <summary>This runner's own step index. Step 1 is the SECOND fixed step after arming.</summary>
    public int Step => step;

    /// <summary>The phase currently commanded, or -1 before the walk starts.</summary>
    public int PhaseState => phaseState;

    /// <summary>Steps since the current phase was commanded.</summary>
    public int StepsInPhase => step - phaseEnteredAtStep;

    /// <summary>The global fixed step this run was armed on, or -1.</summary>
    public int ArmedAtFixedStep => armedAtFixedStep;

    /// <summary>What asked for the arm, for a sealed header.</summary>
    public string ArmRequestSource => armRequestSource ?? "direct Arm";

    /// <summary>The declared dwell schedule, for a sealed header.</summary>
    public int DwellPauseSteps => dwellPauseSteps;

    public int DwellSnapSteps => dwellSnapSteps;
    public int DwellHoldSteps => dwellHoldSteps;
    public int DwellContactSteps => dwellContactSteps;

    /// <summary>The settle allowance. Not a criterion — see its tooltip.</summary>
    public int SettleStepsAfterClip => settleStepsAfterClip;

    /// <summary>How many other station roots this run deactivated.</summary>
    public int IsolatedRoots => isolated.Count;

    /// <summary>Rendered frames since arming, so an observer can report the achieved frame pacing.</summary>
    public int FramesThisRun => framesThisRun;

    /// <summary>Alignment multiple, for a sealed header.</summary>
    public int ArmAlignMultiple => armAlignToFixedStepMultiple;

    // ----- arming ----------------------------------------------------------------------------------

    private void Update()
    {
        if (running) framesThisRun++;

        Keyboard kb = Keyboard.current;
        if (kb == null || running || finished) return;
        if (kb[armKey].wasPressedThisFrame) Request("key " + armKey);
    }

    /// <summary>Requests an arm. Consumed in <c>FixedUpdate</c> on an aligned global fixed step.</summary>
    public void Request(string source)
    {
        if (armRequested || running || finished) return;

        armRequested = true;
        armRequestSource = source;

        Debug.Log("[Runner] " + Label() + ": arm REQUESTED by " + source + ". Deferred to the next " +
                  "global fixed step that is a multiple of " + armAlignToFixedStepMultiple +
                  ", and held until every observer reports ready.", this);
    }

    private void ServiceArmRequest()
    {
        if (running || finished) return;

        if (!armRequested)
        {
            if (!armWhenReady) return;
            if (!Ready()) return;

            armRequested = true;
            armRequestSource = "armWhenReady";
        }

        if (!Ready()) return;

        int now = RqClassificationForkRecorder.CurrentFixedStep;
        if (now % armAlignToFixedStepMultiple != 0) return;

        armedAtFixedStep = now;
        armRequested = false;
        Begin();
    }

    /// <summary>Every observer's own readiness, with the first refusal logged periodically.</summary>
    private bool Ready()
    {
        for (int i = 0; i < observers.Count; i++)
        {
            RqRunObserver observer = observers[i];
            if (observer == null || observer.ReadyToArm) continue;

            if (readinessLogEverySteps > 0 &&
                RqClassificationForkRecorder.CurrentFixedStep % readinessLogEverySteps == 0)
                Debug.Log("[Runner] " + Label() + ": WAITING to arm — " + observer.GetType().Name +
                          " is not ready: " + observer.ReadinessReason, this);

            return false;
        }

        return true;
    }

    /// <summary>Starts the walk. Refuses rather than half-starting.</summary>
    [ContextMenu("Arm")]
    public void Begin()
    {
        if (running || finished) return;

        if (animatorA == null || animatorB == null)
        {
            Debug.LogError("[Runner] " + Label() + ": REFUSED to start. Both animators are required — " +
                           "one alone desynchronises the two dummies.", this);
            return;
        }

        if (Active != null && Active != this)
        {
            Debug.LogError("[Runner] " + Label() + ": REFUSED to start. " + Active.Label() + " is " +
                           "already running, and this runner would deactivate the station that one is " +
                           "measuring. One station at a time.", this);
            return;
        }

        if (!Ready())
        {
            Debug.LogWarning("[Runner] " + Label() + ": not armed — an observer is not ready. Set " +
                             "armWhenReady, or press the hotkey once the instruments have resolved.", this);
            return;
        }

        Active = this;

        if (isolateOtherStations) Isolate();

        animatorA.SetBool(AutoParam, false);
        animatorB.SetBool(AutoParam, false);

        step = 0;
        skipFirstStep = true;
        sequenceCursor = 0;
        clipComplete = false;
        clipCompleteAtStep = -1;
        driveStateHash = Animator.StringToHash(driveStateName);
        driveStateSeen = false;
        framesThisRun = 0;
        running = true;

        for (int i = 0; i < observers.Count; i++)
            if (observers[i] != null) observers[i].OnRunBegin(this);

        Debug.Log("[Runner] " + Label() + ": ARMED at global fixed step " + armedAtFixedStep +
                  ", requested by " + ArmRequestSource + ". Auto=false, walking " +
                  "Pause->SNAP->HOLD->CONTACT->DRIVE with dwells " + dwellPauseSteps + "/" +
                  dwellSnapSteps + "/" + dwellHoldSteps + "/" + dwellContactSteps + " at " +
                  (1f / Time.fixedDeltaTime).ToString("F1") + " Hz. The Drive window ends when the drive " +
                  "clip has played through" +
                  (settleStepsAfterClip > 0 ? " plus " + settleStepsAfterClip + " settle step(s)" : "") +
                  ". Observers: " + observers.Count + ".", this);

        SetState(0);
        cursorDeadline = dwellPauseSteps;
    }

    // ----- the walk --------------------------------------------------------------------------------

    private void SetState(int value)
    {
        animatorA.SetInteger(StateParam, value);
        animatorB.SetInteger(StateParam, value);

        phaseState = value;
        phaseEnteredAtStep = step;

        for (int i = 0; i < observers.Count; i++)
            if (observers[i] != null) observers[i].OnPhase(value, step);
    }

    private void WalkPhases()
    {
        if (step < cursorDeadline || sequenceCursor > 3) return;

        switch (sequenceCursor)
        {
            case 0:
                sequenceCursor = 1;
                SetState(1);
                cursorDeadline = step + dwellSnapSteps;
                break;
            case 1:
                sequenceCursor = 2;
                SetState(2);
                cursorDeadline = step + dwellHoldSteps;
                break;
            case 2:
                sequenceCursor = 3;
                SetState(3);
                cursorDeadline = step + dwellContactSteps;
                break;
            default:
                sequenceCursor = 4;
                SetState(4);
                break;
        }
    }

    /// <summary>
    ///     Has the drive clip played through once on BOTH animators?
    /// </summary>
    /// <remarks>
    ///     This is what "the window is the length of the clip" means operationally. A state still
    ///     blending is not the clip playing, so a transition does not count; and normalizedTime is a
    ///     fraction of the clip, so 1 is one full pass whatever the authored duration happens to be.
    ///     Nothing here states a duration.
    /// </remarks>
    private bool DriveClipPlayedThrough()
    {
        if (InDriveState(animatorA) && InDriveState(animatorB) && !driveStateSeen)
        {
            driveStateSeen = true;
            Debug.Log("[Runner] " + Label() + ": both animators entered '" + driveStateName + "' at step " +
                      step + " (" + StepsInPhase + " steps after State=4 was commanded). The window is " +
                      "open and closes when this clip has played through.", this);
        }

        return PlayedThrough(animatorA) && PlayedThrough(animatorB);
    }

    /// <summary>True when this animator is settled in the declared drive state.</summary>
    /// <remarks>
    ///     A state still blending is not the clip playing, and a state that merely has State=4 commanded
    ///     is not yet the drive state: the animator reports the SOURCE state for a further step.
    /// </remarks>
    private bool InDriveState(Animator animator)
    {
        if (animator.IsInTransition(0)) return false;

        return animator.GetCurrentAnimatorStateInfo(0).shortNameHash == driveStateHash;
    }

    /// <summary>
    ///     Has the DRIVE clip played through once on this animator?
    /// </summary>
    /// <remarks>
    ///     The state identity check is load-bearing, not defensive. Without it this returned true on the
    ///     step State=4 was commanded, because the still-current CONTACT state had been held for 900
    ///     dwell steps and its normalizedTime was long past 1 - so the window closed with zero steps and
    ///     the file carried an empty Drive segment that looked like a completed run.
    /// </remarks>
    private bool PlayedThrough(Animator animator)
    {
        if (!InDriveState(animator)) return false;

        return animator.GetCurrentAnimatorStateInfo(0).normalizedTime >= 1f;
    }

    /// <summary>
    ///     How far through the drive clip the SLOWER of the two animators is, as a fraction. Negative
    ///     when either is not yet settled in the drive state.
    /// </summary>
    /// <remarks>
    ///     The minimum of the two, deliberately: the window closes when BOTH have played through, so the
    ///     slower one is what the window's progress actually is. This states no duration - it is a
    ///     fraction of whatever the authored clip happens to be, which is the same basis
    ///     <see cref="PlayedThrough" /> uses to close the window.
    /// </remarks>
    public float DriveClipNormalizedTime
    {
        get
        {
            float a = DriveClipProgress(animatorA);
            float b = DriveClipProgress(animatorB);
            if (a < 0f || b < 0f) return -1f;

            return Mathf.Min(a, b);
        }
    }

    /// <summary>
    ///     How far through the drive clip one animator is, counting THROUGH the transition into it.
    ///     Negative when the drive clip is neither current nor pending.
    /// </summary>
    /// <remarks>
    ///     Deliberately NOT gated on <see cref="InDriveState" />. That helper returns false for the whole
    ///     transition, and the blend into Drive was measured at 181 of a 234-step window - so a caller
    ///     asking "are we half way through the clip" would find the question unanswerable until the clip
    ///     was already 77% done. During a transition the destination state is the one whose clip is
    ///     playing, so it is read from GetNextAnimatorStateInfo.
    /// </remarks>
    private float DriveClipProgress(Animator animator)
    {
        if (animator == null) return -1f;

        if (animator.IsInTransition(0))
        {
            AnimatorStateInfo next = animator.GetNextAnimatorStateInfo(0);
            return next.shortNameHash == driveStateHash ? next.normalizedTime : -1f;
        }

        AnimatorStateInfo cur = animator.GetCurrentAnimatorStateInfo(0);
        return cur.shortNameHash == driveStateHash ? cur.normalizedTime : -1f;
    }

    private void FixedUpdate()
    {
        ServiceArmRequest();

        if (!running) return;

        // The first step after arming measures the apparatus settling into the run rather than the run:
        // arming isolates station roots, flips both animators out of Auto and commands the first phase.
        // Skipped rather than explained, exactly as the fork recorder does, so step 1 is the SECOND
        // fixed step after arming and every dwell is counted from there.
        if (skipFirstStep)
        {
            skipFirstStep = false;
            return;
        }

        step++;
        WalkPhases();

        if (CheckVeto()) return;

        for (int i = 0; i < observers.Count; i++)
            if (observers[i] != null) observers[i].OnStep(step);

        if (CheckVeto()) return;

        if (sequenceCursor < 4) return;

        if (!clipComplete && DriveClipPlayedThrough())
        {
            clipComplete = true;
            clipCompleteAtStep = step;

            Debug.Log("[Runner] " + Label() + ": drive clip played through at step " + step + " (" +
                      StepsInPhase + " steps after DRIVE was commanded)." +
                      (settleStepsAfterClip > 0
                          ? " Holding " + settleStepsAfterClip + " settle step(s)."
                          : " Ending the run."), this);
        }

        if (clipComplete && step >= clipCompleteAtStep + settleStepsAfterClip)
            End("the drive clip played through" +
                (settleStepsAfterClip > 0 ? " plus " + settleStepsAfterClip + " settle step(s)" : ""));
    }

    /// <summary>Ends the run if any observer says it must not continue. A veto is a result.</summary>
    private bool CheckVeto()
    {
        for (int i = 0; i < observers.Count; i++)
        {
            RqRunObserver observer = observers[i];
            if (observer == null || !observer.VetoRun) continue;

            End("VETOED by " + observer.GetType().Name + ": " + observer.VetoReason);
            return true;
        }

        return false;
    }

    /// <summary>Ends the walk and tells every observer why. Idempotent.</summary>
    public void End(string reason)
    {
        if (!running) return;

        running = false;
        finished = true;

        Debug.Log("[Runner] " + Label() + ": run ENDED at step " + step + " — " + reason, this);

        if (sequenceCursor >= 4 && !driveStateSeen)
            Debug.LogError("[Runner] " + Label() + ": State=4 was commanded but neither animator ever " +
                           "reported a state named '" + driveStateName + "', so NO DRIVE WINDOW EXISTS " +
                           "in this run. Check the name against the controller - a wrong name here " +
                           "produces a file whose Drive segment is empty rather than an error.", this);

        for (int i = 0; i < observers.Count; i++)
            if (observers[i] != null) observers[i].OnRunEnd(reason);

        Restore();
        if (Active == this) Active = null;
    }

    private void OnDisable()
    {
        if (running) End("the runner was disabled mid-run");

        Restore();
        if (Active == this) Active = null;
    }

    // ----- isolation ------------------------------------------------------------------------------

    private void Isolate()
    {
        isolated.Clear();
        GameObject mine = gameObject;

        foreach (var root in gameObject.scene.GetRootGameObjects())
        {
            if (!root.name.StartsWith("Station_")) continue;
            if (root == mine || root.transform.IsChildOf(mine.transform) ||
                mine.transform.IsChildOf(root.transform)) continue;
            if (!root.activeSelf) continue;

            root.SetActive(false);
            isolated.Add(root);
        }

        Debug.Log("[Runner] " + Label() + ": isolated " + isolated.Count + " other station root(s). " +
                  "Runtime only — stopping Play restores them.", this);
    }

    private void Restore()
    {
        for (int i = 0; i < isolated.Count; i++)
            if (isolated[i] != null) isolated[i].SetActive(true);

        isolated.Clear();
    }

    /// <summary>This runner's label, for a log line.</summary>
    public string Label() => string.IsNullOrEmpty(stationId) ? name : stationId;
}
