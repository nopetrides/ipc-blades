using UnityEngine;

/// <summary>
///     Arms ONE <see cref="RqClassificationForkRecorder" /> without a keypress, from a declared animator
///     state, on a declared fixed step, once the apparatus is demonstrably ready.
/// </summary>
/// <remarks>
///     <para>
///         <b>Everything here runs in FixedUpdate.</b> Nothing in this apparatus may start a run from a
///         rendered-frame boundary. <c>Update</c> fires once per rendered frame and has no fixed
///         relationship to the physics clock, so arming there begins the walk at an arbitrary sub-step
///         phase — measured at 11 ms of spread across four runs, about one whole fixed step at 90 Hz,
///         which is a different initial condition every time.
///     </para>
///     <para>
///         <see cref="DefaultExecutionOrder" /> is -200, ahead of the solver's -100 and the recorder's
///         +100, so when this arms on a step, the solver and recorder both see that step already armed.
///     </para>
///     <para>
///         <b>Why this is not <c>runOnStart</c>.</b> That calls <c>Begin</c> from <c>Start</c>, on frame 0,
///         before Ragdoll Animator has bound the physics proxies and before the preflight can run.
///     </para>
///     <para>
///         <b>What it waits for.</b> Both conditions are defined elsewhere in the apparatus rather than
///         invented here: the station's preflight has run, which cannot happen until both blades are
///         carried by a simulating body; and both animators are IN the declared arm-from state and NOT in
///         a transition. Every <c>State</c>-driven transition in <c>Motions_Controller</c> is an Any-State
///         transition with <c>HasExitTime = 0</c>, a 2 s duration and <c>InterruptionSource = None</c>, so
///         a command issued during a blend is deferred until that blend finishes and arming without the
///         transition check would arm from an unknown pose.
///     </para>
///     <para>
///         <b>Why the arm-from state is SNAP and not Pause.</b> Researcher's decision. Pause is the
///         A-pose: the swords are oriented in a completely different way and it is the least
///         representative point in the cycle, which is the same reason the heavy gate does not arm before
///         HOLD. Arming at SNAP does not skip the settle — the recorder's own dwell walk still commands
///         Pause and then SNAP, so every run traverses the full Any -> Pause -> SNAP path under command.
///         That traversal IS the settle check, and it has caught a real fault before: colliders on the
///         markers once caught the swords on each other and they never reached their SNAP positions.
///     </para>
///     <para>
///         <b>Nothing here changes what is measured.</b> It calls the recorder's own public entry point,
///         and sets <c>Auto</c> and <c>State</c> using the same parameters and values the recorder itself
///         uses. It does not touch dwells, the solver, the gate, isolation, or the output path. With
///         <see cref="armWhenReady" /> off it does nothing at all.
///     </para>
/// </remarks>
[DisallowMultipleComponent]
[DefaultExecutionOrder(-200)]
public sealed class RqAutoArmSequencer : MonoBehaviour
{
    private const string AutoParam = "Auto";
    private const string StateParam = "State";

    private enum Stage
    {
        WaitingForPreflight,
        DrivingToArmState,
        WaitingForArmState,
        WaitingForExactStep,
        Armed,
        GaveUp,
        Idle
    }

    [Header("What is armed")]
    [Tooltip("The one recorder this sequencer arms. One station per Play session: the recorder refuses a " +
             "second arm, and running several stations back to back has never been verified as " +
             "non-interfering.")]
    [SerializeField] private RqClassificationForkRecorder recorder;

    [Tooltip("The same station's metrics component, which owns the preflight. Its PreflightHasRun is the " +
             "readiness signal.")]
    [SerializeField] private RqBindMetricsV2 metrics;

    [Tooltip("Both dummies' animators, same two the recorder drives. Both, or the dummies desynchronise.")]
    [SerializeField] private Animator animatorA;

    [SerializeField] private Animator animatorB;

    [Header("Arm condition")]
    [Tooltip("OFF BY DEFAULT, and off means this component does nothing whatsoever — the hotkey path is " +
             "untouched. On means this Play session will arm itself once the conditions below are met.")]
    [SerializeField] private bool armWhenReady;

    [Tooltip("The State value driven before arming, and the animator state expected to result.\n\n" +
             "Motions_Controller, State-driven (Auto = false) reachable states only:\n" +
             "  0 -> Pause    (A-pose)\n" +
             "  1 -> SR       (SNAP)\n" +
             "  2 -> Hold\n" +
             "  3 -> Contact\n" +
             "  4 -> Drive\n\n" +
             "The four reverse-playback states — Snap 0, HR, CR, DR — have NO State transition into them " +
             "and cannot be commanded here at all; they are reachable only by the Auto cycle.\n\n" +
             "1 (SNAP) is the researcher's declared choice. Drive is excluded because its advance/retreat " +
             "motion would muddy the approach Q1 measures.")]
    [Range(0, 4)]
    [SerializeField] private int armFromState = 1;

    [Tooltip("The animator state name expected for the State value above. Checked by name so a controller " +
             "edit that repoints a State value cannot silently arm from a different pose.")]
    [SerializeField] private string armFromStateName = "SR";

    [Header("Exact-step arming")]
    [Tooltip("The EXACT global fixed step to arm on, counted from scene start. Not restricted to a " +
             "multiple of anything — any step is accepted.\n\n" +
             "0 = disabled: arm on the first fixed step where the readiness conditions hold, which is the " +
             "ordinary 'when ready' behaviour.\n\n" +
             "Set to a step N: the run begins on exactly step N, so two runs given the same N share an " +
             "identical starting instant. This is the ONLY way two runs can be compared value-for-value; " +
             "runs armed on different steps are a cross-comparison of overall behaviour, and their " +
             "per-step numbers are not expected to match.\n\n" +
             "If the readiness conditions are NOT met when step N arrives, the run is REFUSED rather than " +
             "armed late or armed from an unverified pose. Pick an N comfortably after the preflight — " +
             "observed arming naturally at 102-118 rendered frames, which is the figure to size it " +
             "against, not a value chosen here.\n\n" +
             "REJECTED if armWhenReady is false: an exact step with nothing enabled to act on it is a " +
             "silently dead setting.")]
    [Min(0)]
    [SerializeField] private int armAtExactFixedStep;

    [Tooltip("DIAGNOSTIC CEILING in FIXED STEPS, not a declared threshold and not a pass/fail criterion. " +
             "If the arm condition is still unmet after this many fixed steps the sequencer says so and " +
             "REFUSES to arm, rather than arming from an unknown state or hanging silently. Mirrors the " +
             "preflight's own give-up-and-shout behaviour. Ignored once an exact step is set, because the " +
             "exact step is its own deadline.")]
    [Min(1)]
    [SerializeField] private int giveUpAfterFixedSteps = 1800;

    [Header("Frame pacing (DIAGNOSTIC — applied in Awake, before anything simulates)")]
    [Tooltip("Applied regardless of armWhenReady, because the divergence being chased accumulates from " +
             "SCENE LOAD, not from the arm.\n\n" +
             "WHY THIS EXISTS. Ragdoll Animator refreshes the muscle TARGET in CaptureAnimator, which is " +
             "in LateUpdateTick and therefore runs once per rendered frame, while the drive runs every " +
             "fixed step. Measured on this machine: 2.7 to 3.3 fixed steps per frame, wandering run to " +
             "run, so three runs armed on the identical fixed step reached it after 270, 332 and 338 " +
             "frames.\n\n" +
             "The fix tested here does NOT remove the batching. It makes it CONSTANT, by capping the " +
             "render rate to a value the machine always exceeds and that divides the physics rate evenly. " +
             "At 90 Hz physics: 90 gives one step per frame (ideal, but unreachable here at ~30 fps), 30 " +
             "gives three, 15 gives six. The cap must actually bind — a cap above what the machine can " +
             "sustain does nothing.\n\n" +
             "0 or below leaves Application.targetFrameRate untouched.")]
    [SerializeField] private int diagnosticTargetFrameRate;

    [Tooltip("-1 leaves QualitySettings.vSyncCount untouched. 0 disables vsync, which is required for " +
             "targetFrameRate to take effect at all — with vsync on, the cap is ignored.")]
    [SerializeField] private int diagnosticVSyncCount = -1;

    private Stage stage = Stage.WaitingForPreflight;
    private int waitedSteps;
    private int armFromStateHash;

    // Awake, not Start: the frame pacing has to be in force before the first frame simulates, because
    // what is being controlled accumulates from scene load.
    private void Awake()
    {
        if (diagnosticVSyncCount >= 0)
        {
            QualitySettings.vSyncCount = diagnosticVSyncCount;
            Debug.Log("[AutoArm] DIAGNOSTIC: QualitySettings.vSyncCount set to " + diagnosticVSyncCount +
                      ". This is an apparatus change and is recorded in the sealed header.", this);
        }

        // ALWAYS applied, never "left alone". Application.targetFrameRate SURVIVES a Play session in the
        // editor, so a previous session's cap is still in force at Awake. Treating 0 as "leave untouched"
        // silently inherited a 15 fps cap into a run that was labelled uncapped, and produced three
        // conclusions that had to be withdrawn. 0 or below now means EXPLICITLY UNCAPPED.
        int previousCap = Application.targetFrameRate;
        Application.targetFrameRate = diagnosticTargetFrameRate > 0 ? diagnosticTargetFrameRate : -1;

        Debug.Log("[AutoArm] DIAGNOSTIC frame pacing: targetFrameRate was " + previousCap +
                  ", now " + Application.targetFrameRate + "; vSyncCount now " +
                  QualitySettings.vSyncCount + ". Physics " +
                  (1f / Time.fixedDeltaTime).ToString("F1") + " Hz. Both effective values are written to " +
                  "the sealed header — read them, do not assume the condition from the inspector.", this);
    }

    private void Start()
    {
        armFromStateHash = Animator.StringToHash(armFromStateName);

        if (armAtExactFixedStep > 0 && !armWhenReady)
        {
            Debug.LogError("[AutoArm] REJECTED: armAtExactFixedStep is " + armAtExactFixedStep +
                           " but armWhenReady is false, so nothing would ever act on it. Either enable " +
                           "armWhenReady or set the step back to 0. Nothing will be armed.", this);
            stage = Stage.GaveUp;
            return;
        }

        if (!armWhenReady)
        {
            stage = Stage.Idle;
            return;
        }

        if (recorder == null || metrics == null || animatorA == null || animatorB == null)
        {
            Debug.LogError("[AutoArm] recorder, metrics and both animators are required. Nothing will be " +
                           "armed; use the hotkey.", this);
            stage = Stage.GaveUp;
            return;
        }

        Debug.Log("[AutoArm] " + recorder.StationId + ": enabled. Waiting for preflight, then driving " +
                  "State=" + armFromState + " (" + armFromStateName + "). Arm target: " +
                  (armAtExactFixedStep > 0
                      ? "EXACT global fixed step " + armAtExactFixedStep
                      : "first ready fixed step (no exact step set)") + ".", this);
    }

    // FixedUpdate, never Update: a run may only begin on the physics clock.
    private void FixedUpdate()
    {
        switch (stage)
        {
            case Stage.Idle:
            case Stage.Armed:
            case Stage.GaveUp:
                return;
        }

        int now = RqClassificationForkRecorder.CurrentFixedStep;

        // The exact step is its own deadline, so the generic ceiling only guards the open-ended case.
        if (armAtExactFixedStep == 0 && ++waitedSteps > giveUpAfterFixedSteps)
        {
            Stage reached = stage;
            stage = Stage.GaveUp;
            Debug.LogError("[AutoArm] " + recorder.StationId + ": GAVE UP after " + giveUpAfterFixedSteps +
                           " fixed steps at stage " + reached + ". NOTHING WAS ARMED — arming from an " +
                           "unverified state is worse than not arming. " + Describe(), this);
            return;
        }

        switch (stage)
        {
            case Stage.WaitingForPreflight:
                if (!metrics.PreflightHasRun) return;
                stage = Stage.DrivingToArmState;
                return;

            case Stage.DrivingToArmState:
                // The same two parameters and values the recorder itself writes. Auto must go false
                // first: with Auto true the cycle keeps advancing on clip end, and the State-driven
                // transitions are all conditioned on Auto being false.
                animatorA.SetBool(AutoParam, false);
                animatorB.SetBool(AutoParam, false);
                animatorA.SetInteger(StateParam, armFromState);
                animatorB.SetInteger(StateParam, armFromState);
                stage = Stage.WaitingForArmState;
                return;

            case Stage.WaitingForArmState:
                if (!Ready()) return;

                if (armAtExactFixedStep == 0)
                {
                    Arm(now, "AutoArm when-ready");
                    return;
                }

                if (now > armAtExactFixedStep)
                {
                    stage = Stage.GaveUp;
                    Debug.LogError("[AutoArm] " + recorder.StationId + ": REFUSED. The declared arm step " +
                                   armAtExactFixedStep + " has already passed — the apparatus only became " +
                                   "ready at step " + now + ". Nothing was armed. Set a later step; the " +
                                   "readiness conditions cannot be rushed.", this);
                    return;
                }

                stage = Stage.WaitingForExactStep;
                Debug.Log("[AutoArm] " + recorder.StationId + ": ready at fixed step " + now +
                          ", holding for declared arm step " + armAtExactFixedStep + ".", this);
                return;

            case Stage.WaitingForExactStep:
                if (now < armAtExactFixedStep) return;

                if (now > armAtExactFixedStep)
                {
                    stage = Stage.GaveUp;
                    Debug.LogError("[AutoArm] " + recorder.StationId + ": REFUSED. Overshot the declared " +
                                   "arm step " + armAtExactFixedStep + " (now " + now + "). Nothing was " +
                                   "armed.", this);
                    return;
                }

                // Re-checked ON the declared step. Being ready earlier is not being ready now, and the
                // whole point of the exact step is that the state on THAT step is what is reproduced.
                if (!Ready())
                {
                    stage = Stage.GaveUp;
                    Debug.LogError("[AutoArm] " + recorder.StationId + ": REFUSED on the declared arm " +
                                   "step " + armAtExactFixedStep + " — the readiness conditions do not " +
                                   "hold on this step. Arming from an unverified pose would make the run " +
                                   "uncomparable, which is the thing the exact step exists to prevent. " +
                                   Describe(), this);
                    return;
                }

                Arm(now, "AutoArm exact step " + armAtExactFixedStep);
                return;
        }
    }

    private void Arm(int now, string source)
    {
        stage = Stage.Armed;

        // Recorded per run, because the arm condition is a property OF the run and must not be assumed
        // constant across runs.
        Debug.Log("[AutoArm] " + recorder.StationId + ": arm condition met on global fixed step " + now +
                  ". " + Describe() + " Arming (" + source + ").", this);

        if (recorder.HasArmedThisSession)
        {
            Debug.LogError("[AutoArm] " + recorder.StationId + ": recorder was ALREADY armed this Play " +
                           "session. Not arming — a second run inherits the first run's end state.", this);
            return;
        }

        recorder.BeginArmedBy(source);
    }

    private bool Ready()
    {
        return InArmState(animatorA) && InArmState(animatorB);
    }

    private bool InArmState(Animator animator)
    {
        if (animator.IsInTransition(0)) return false;

        return animator.GetCurrentAnimatorStateInfo(0).shortNameHash == armFromStateHash;
    }

    private string Describe()
    {
        return "A: " + StateOf(animatorA) + " | B: " + StateOf(animatorB);
    }

    private string StateOf(Animator animator)
    {
        if (animator == null) return "<null>";

        if (animator.IsInTransition(0))
        {
            AnimatorTransitionInfo t = animator.GetAnimatorTransitionInfo(0);
            return "IN TRANSITION (" + t.normalizedTime.ToString("F3") + ")";
        }

        AnimatorStateInfo s = animator.GetCurrentAnimatorStateInfo(0);
        string label = s.shortNameHash == armFromStateHash ? armFromStateName : "hash " + s.shortNameHash;

        return label + " normalizedTime " + s.normalizedTime.ToString("F3");
    }
}
