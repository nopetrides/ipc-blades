using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

/// <summary>
///     Runs the tangential layer OFF against ON at the SAME station, repeatedly, in a balanced order.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why in place.</b> The nine-station matrix separates the three treatments in world space, so
///         a Baseline-vs-OurSolution comparison across rows moves the rig twelve metres and changes the
///         treatment at the same time. This project already has evidence that a single-point knife-edge
///         contact's resistance shifts with absolute world position by an amount comparable to the effects
///         being discussed. Treatment and position are therefore confounded across rows, and no row-to-row
///         difference can be attributed to the treatment. Holding everything fixed and toggling only the
///         layer removes the confound entirely: same station, same dummies, same blade geometry, same
///         authored motion, same physics settings, same world position.
///     </para>
///     <para>
///         <b>Why repeated and balanced.</b> There is no repeatability bound for this rig. The only one
///         the project ever measured belongs to the retired kinematic apparatus and does not carry. Until
///         a scatter band exists, no difference between two single runs can be called real in either
///         direction. Conditions alternate rather than running all OFF then all ON, so any run-order drift
///         is spread across both arms instead of loading onto one.
///     </para>
///     <para>
///         <b>What this deliberately does not do.</b> It does not touch the contact solver, the collision
///         layer, the authored poses, the servos or the timestep. It sets one boolean and runs the
///         declared protocol.
///     </para>
///     <para>
///         The sequencer is a coroutine that yields on simulation time. It never blocks the editor main
///         thread, which is a hard rule here: a synchronous sweep loop has previously hung Unity with no
///         cancel path.
///     </para>
/// </remarks>
[DisallowMultipleComponent]
public sealed class RqAblationRunner : MonoBehaviour
{
    /// <summary>One completed run of one condition at one station.</summary>
    public struct Row
    {
        public int Run;
        public bool LayerOn;
        public string Treatment;
        public string Station;
        public RqStationContactRecorder.Record Record;
        public string Scenario;
        public string RawPair;
    }

    [Header("What is toggled")]
    [Tooltip("The layer under test. Its ApplyTangentialBehaviour is the ONLY thing this runner changes.")]
    [SerializeField] private RqBladeTangentialSolver solver;

    [Header("Stations observed")]
    [Tooltip("Recorders read at the end of every run. Every station listed is run under both conditions " +
             "in the same play session, at its own fixed world position.")]
    [SerializeField] private List<RqStationContactRecorder> recorders = new List<RqStationContactRecorder>();

    [Tooltip("The two blades of each observed station, in the same order as the recorders, so the " +
             "classified feature pair can be reported alongside its own run.")]
    [SerializeField] private List<RqBladeContact> bladesA = new List<RqBladeContact>();

    [SerializeField] private List<RqBladeContact> bladesB = new List<RqBladeContact>();

    [Header("Condition sequence")]
    [Tooltip("Balanced order, one character per run: F = layer OFF, N = layer ON. Alternating rather " +
             "than blocked, so run-order drift does not load onto one arm.")]
    [SerializeField] private string sequence = "FNNFFN";

    [Header("Protocol, simulation seconds")]
    [Tooltip("Held at SNAP before HOLD. Generous on purpose: rushing the start chain has previously " +
             "produced a 48x scatter artefact that looked like physics.")]
    [SerializeField] private float snapSeconds = 8f;

    [SerializeField] private float holdSeconds = 6f;
    [SerializeField] private float contactSeconds = 8f;

    [Tooltip("The declared drive is 0.657 m over 2.5833 s. This is that plus settling, and it is the " +
             "window every accumulated quantity is measured over.")]
    [SerializeField] private float driveSeconds = 6f;

    [Header("Output")]
    [SerializeField] private bool logEachRun = true;

    private readonly List<Row> rows = new List<Row>();
    private Coroutine running;

    /// <summary>Every completed run, in the order they were run.</summary>
    public IReadOnlyList<Row> Rows => rows;

    /// <summary>True while the sequence is executing.</summary>
    public bool Running => running != null;

    /// <summary>Runs completed so far, out of the declared sequence length.</summary>
    public int Completed => rows.Count / Mathf.Max(1, recorders.Count);

    public int Planned => sequence != null ? sequence.Length : 0;

    /// <summary>The phase the sequencer is currently in, for a readout.</summary>
    public string Phase { get; private set; } = "idle";

    /// <summary>Declares the apparatus, so the configuration is serialized rather than typed each session.</summary>
    public void Configure(
        RqBladeTangentialSolver tangentialSolver,
        List<RqStationContactRecorder> stationRecorders,
        List<RqBladeContact> a, List<RqBladeContact> b,
        string conditionSequence)
    {
        solver = tangentialSolver;
        recorders = stationRecorders;
        bladesA = a;
        bladesB = b;
        sequence = conditionSequence;
    }

    /// <summary>Starts the declared sequence. Ignored if one is already running.</summary>
    public void Begin()
    {
        if (running != null) return;

        rows.Clear();
        running = StartCoroutine(Sequence());
    }

    /// <summary>Stops after the current run finishes nothing further; the rows already collected stay.</summary>
    public void Abort()
    {
        if (running == null) return;

        StopCoroutine(running);
        running = null;
        Phase = "aborted";
    }

    private IEnumerator Sequence()
    {
        if (solver == null || recorders.Count == 0)
        {
            Debug.LogWarning("[Ablation] nothing to run: no solver or no recorders.", this);
            running = null;
            yield break;
        }

        for (int run = 0; run < sequence.Length; run++)
        {
            bool layerOn = sequence[run] == 'N' || sequence[run] == 'n';
            solver.ApplyTangentialBehaviour = layerOn;

            // Back to SNAP and hold there. Setting a body's state and re-arming in the same breath leaves
            // PhysX holding stale solver state; the whole point of the spaced chain is to let the reset
            // propagate before anything is measured.
            Phase = "run " + (run + 1) + "/" + sequence.Length + " " + (layerOn ? "ON" : "OFF") + " SNAP";
            SetState(1);
            ReAnchorAll();
            yield return Wait(snapSeconds);

            Phase = Phase.Replace("SNAP", "HOLD");
            SetState(2);
            yield return Wait(holdSeconds);

            Phase = Phase.Replace("HOLD", "CONTACT");
            SetState(3);
            yield return Wait(contactSeconds);

            // Zero and drive on the same frame, so every station's window is the same window.
            Phase = Phase.Replace("CONTACT", "DRIVE");
            for (int i = 0; i < recorders.Count; i++)
                if (recorders[i] != null) recorders[i].LatchZero();

            SetState(4);
            yield return Wait(driveSeconds);

            Collect(run, layerOn);
        }

        SetState(1);
        Phase = "done: " + rows.Count + " rows";
        running = null;
    }

    private IEnumerator Wait(float seconds)
    {
        float until = Time.time + seconds;
        while (Time.time < until) yield return new WaitForFixedUpdate();
    }

    /// <summary>Drives every animator that carries the protocol parameters. No station is addressed by name.</summary>
    private static void SetState(int state)
    {
        Animator[] animators = FindObjectsByType<Animator>(FindObjectsInactive.Exclude);

        for (int i = 0; i < animators.Length; i++)
        {
            Animator animator = animators[i];
            if (animator.runtimeAnimatorController == null) continue;

            bool hasState = false;
            for (int p = 0; p < animator.parameterCount; p++)
                if (animator.GetParameter(p).name == "State") hasState = true;

            if (!hasState) continue;

            animator.SetBool("Auto", false);
            animator.SetInteger("State", state);
        }
    }

    /// <summary>
    ///     Clears each pair's held anchor and its engagement history between runs.
    /// </summary>
    /// <remarks>
    ///     A retained anchor would carry the previous run's stick point into the next one, and a retained
    ///     touch count would make the next run's persistence figures a running total across conditions.
    /// </remarks>
    private void ReAnchorAll()
    {
        for (int i = 0; i < bladesA.Count && i < bladesB.Count; i++)
            if (bladesA[i] != null && bladesB[i] != null) solver.ReAnchor(bladesA[i], bladesB[i]);
    }

    private void Collect(int run, bool layerOn)
    {
        for (int i = 0; i < recorders.Count; i++)
        {
            RqStationContactRecorder recorder = recorders[i];
            if (recorder == null) continue;

            var row = new Row
            {
                Run = run + 1,
                LayerOn = layerOn,
                Treatment = recorder.Treatment,
                Station = recorder.Station,
                Record = recorder.Current,
                Scenario = "(unclassified)",
                RawPair = "(none)"
            };

            if (i < bladesA.Count && i < bladesB.Count && bladesA[i] != null && bladesB[i] != null)
            {
                RqBladeTangentialSolver.Snapshot snapshot;
                if (solver.TryGetSnapshot(bladesA[i], bladesB[i], out snapshot) && snapshot.ClassificationValid)
                {
                    row.Scenario = snapshot.Scenario.ToString();
                    row.RawPair = snapshot.RawTypeA + "(" + snapshot.RawIdA + ") x " +
                                  snapshot.RawTypeB + "(" + snapshot.RawIdB + ")";
                }
            }

            rows.Add(row);

            if (logEachRun)
                Debug.Log(
                    "[Ablation] run " + row.Run + " " + (layerOn ? "ON " : "OFF") + " " + row.Station +
                    "  migA " + (row.Record.MigrationAlongA * 1000f).ToString("F1") +
                    " migB " + (row.Record.MigrationAlongB * 1000f).ToString("F1") +
                    " slip " + (row.Record.TangentialSlipPath * 1000f).ToString("F1") +
                    " rotGain " + row.Record.RotationGainBA.ToString("F3") +
                    " tipGain " + row.Record.TipGainBA.ToString("F3") +
                    " pair " + row.Record.PairSteps + "/" +
                    (row.Record.PairSteps + row.Record.DropoutSteps) +
                    " overlap " + row.Record.OverlapSteps +
                    "  " + row.Scenario, this);
        }
    }

    /// <summary>The collected rows as CSV, one line per station per run.</summary>
    public string ToCsv()
    {
        var text = new StringBuilder(4096);
        text.AppendLine(
            "run,layer,treatment,station,scenario,pair_steps,overlap_steps,dropout_steps,releases," +
            "first_pair_s,first_overlap_s," +
            "mig_a_mm,mig_b_mm,slip_path_mm,rot_a_deg,rot_b_deg,rot_gain_ba,tip_a_mm,tip_b_mm,tip_gain_ba," +
            "deepest_pen_mm,peak_normal_swing_deg,mean_normal_n,mean_added_tangential_n,raw_pair");

        for (int i = 0; i < rows.Count; i++)
        {
            Row r = rows[i];
            RqStationContactRecorder.Record c = r.Record;

            text.AppendLine(
                r.Run + "," + (r.LayerOn ? "ON" : "OFF") + "," + r.Treatment + "," + r.Station + "," +
                r.Scenario + "," +
                c.PairSteps + "," + c.OverlapSteps + "," + c.DropoutSteps + "," + c.ReleaseCount + "," +
                c.FirstPairSeconds.ToString("F3") + "," + c.FirstOverlapSeconds.ToString("F3") + "," +
                (c.MigrationAlongA * 1000f).ToString("F2") + "," +
                (c.MigrationAlongB * 1000f).ToString("F2") + "," +
                (c.TangentialSlipPath * 1000f).ToString("F2") + "," +
                c.RotationADeg.ToString("F3") + "," + c.RotationBDeg.ToString("F3") + "," +
                c.RotationGainBA.ToString("F4") + "," +
                (c.TipTravelA * 1000f).ToString("F2") + "," + (c.TipTravelB * 1000f).ToString("F2") + "," +
                c.TipGainBA.ToString("F4") + "," +
                (c.DeepestPenetration * 1000f).ToString("F3") + "," +
                c.PeakNormalSwing.ToString("F1") + "," +
                c.MeanNormalForce.ToString("F2") + "," +
                c.MeanAddedTangentialForce.ToString("F2") + "," +
                "\"" + r.RawPair + "\"");
        }

        return text.ToString();
    }
}
