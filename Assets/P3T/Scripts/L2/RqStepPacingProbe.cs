using System.Globalization;
using System.Text;
using BladeContact;
using UnityEngine;

/// <summary>
///     Lightweight probe: records the first N fixed steps from SCENE LOAD, to find out whether the
///     frame-paced animator capture is what makes two runs diverge.
/// </summary>
/// <remarks>
///     <para>
///         <b>The question.</b> Ragdoll Animator refreshes the muscle target in <c>CaptureAnimator</c>,
///         which sits in <c>LateUpdateTick</c> and so runs once per rendered frame, while the drive runs
///         every fixed step. If that cadence is what carries divergence, then between two runs the FRAME
///         COUNT per fixed step must diverge at or before the step where the POSE diverges. If the pose
///         diverges while the frame counts are still identical, the capture cadence is not the carrier and
///         the hypothesis is wrong.
///     </para>
///     <para>
///         <b>Why it is separate from the recorder.</b> The recorder starts at the arm, which is hundreds
///         of steps in. This starts at step 1 of the session, which is where the divergence has to begin.
///         It also does no shell queries, no classification and no isolation — it reads four transforms and
///         two integers per step into a preallocated buffer, so it does not perturb what it measures.
///     </para>
///     <para>
///         <b>What "captureRan" means.</b> Whether a rendered frame boundary occurred since the previous
///         fixed step, inferred from <c>Time.frameCount</c> changing. Under
///         <c>AnimatorUpdateMode.Fixed</c>, RA2's <c>LateUpdateTick</c> is gated so it does its capture at
///         most once per frame and only on frames where a fixed step ran — so a frame boundary is a capture
///         opportunity, and steps with no frame boundary got no fresh target.
///     </para>
/// </remarks>
[DisallowMultipleComponent]
[DefaultExecutionOrder(-300)]
public sealed class RqStepPacingProbe : MonoBehaviour
{
    [Tooltip("OFF BY DEFAULT. On, this writes one CSV per Play session, from scene load.")]
    [SerializeField] private bool probeEnabled;

    [Tooltip("How many fixed steps to record from scene load. 200 covers the interval where divergence " +
             "first becomes measurable; the buffer is preallocated at this size.")]
    [Min(1)]
    [SerializeField] private int steps = 200;

    [Tooltip("The two blades to sample. Poses are read from SimulatedPlacement — the generated ragdoll " +
             "proxy body, never the authored transform.")]
    [SerializeField] private RqBladeContact contactA;

    [SerializeField] private RqBladeContact contactB;

    [SerializeField] private string label = "probe";

    /// <remarks>
    ///     BLANK by default, deliberately. It used to ship an absolute path from one machine, which
    ///     both leaked a folder structure into a public repository and silently wrote another
    ///     machine's runs into a directory that did not describe them. Blank cannot do either, and
    ///     the write path below refuses with a named error rather than throwing on
    ///     CreateDirectory("").
    /// </remarks>
    [SerializeField] private string outputDirectory = "";

    private struct Row
    {
        public int Step, Frame;
        public bool CaptureRan;
        public Vector3 PosA, PosB;
        public Quaternion RotA, RotB;
        public bool HaveA, HaveB;
    }

    private Row[] rows;
    private int count;
    private int lastFrame = -1;
    private bool written;

    private void Start()
    {
        if (!probeEnabled) return;
        rows = new Row[steps];
        Debug.Log("[Probe] " + label + ": recording the first " + steps + " fixed steps from scene load.",
                  this);
    }

    private void FixedUpdate()
    {
        if (!probeEnabled || rows == null || count >= rows.Length) return;

        var r = new Row();
        r.Step = RqClassificationForkRecorder.CurrentFixedStep;
        r.Frame = Time.frameCount;
        r.CaptureRan = lastFrame >= 0 && Time.frameCount != lastFrame;
        lastFrame = Time.frameCount;

        if (contactA != null && contactA.Body != null)
        {
            BladePose p = contactA.SimulatedPlacement;
            r.PosA = p.Position;
            r.RotA = p.Rotation;
            r.HaveA = true;
        }

        if (contactB != null && contactB.Body != null)
        {
            BladePose p = contactB.SimulatedPlacement;
            r.PosB = p.Position;
            r.RotB = p.Rotation;
            r.HaveB = true;
        }

        rows[count++] = r;

        if (count >= rows.Length) Write();
    }

    private void OnDisable()
    {
        if (probeEnabled && !written && count > 0) Write();
    }

    private void Write()
    {
        if (written) return;
        written = true;

        var sb = new StringBuilder();
        sb.AppendLine("# RqStepPacingProbe — first " + count + " fixed steps from scene load");
        sb.AppendLine("# label," + label + ",fixedDeltaTime," + R(Time.fixedDeltaTime) +
                      ",vSyncCount," + QualitySettings.vSyncCount +
                      ",targetFrameRate," + Application.targetFrameRate);
        sb.AppendLine("# captureRan,1 when a rendered frame boundary occurred since the previous fixed " +
                      "step, so RA2 had an opportunity to refresh the muscle target. 0 means this step " +
                      "drove toward a target captured on an earlier step");
        sb.AppendLine("# poses from SimulatedPlacement (generated ragdoll proxy). Metres, round-trip floats");
        sb.AppendLine("step,frame,captureRan,haveA,posAx,posAy,posAz,rotAx,rotAy,rotAz,rotAw," +
                      "haveB,posBx,posBy,posBz,rotBx,rotBy,rotBz,rotBw");

        for (int i = 0; i < count; i++)
        {
            Row r = rows[i];
            sb.Append(r.Step).Append(',').Append(r.Frame).Append(',').Append(r.CaptureRan ? 1 : 0)
              .Append(',').Append(r.HaveA ? 1 : 0).Append(',').Append(V(r.PosA)).Append(',').Append(Q(r.RotA))
              .Append(',').Append(r.HaveB ? 1 : 0).Append(',').Append(V(r.PosB)).Append(',').Append(Q(r.RotB))
              .AppendLine();
        }

        if (string.IsNullOrEmpty(outputDirectory))
        {
            Debug.LogError("[StepPacing] please set directory on the RqStepPacingProbe component to record. "
                           + "outputDirectory is blank, so nothing was written. It is blank by "
                           + "default so that no machine's folder layout ships with the project; "
                           + "refusing here rather than letting CreateDirectory(\"\") throw, "
                           + "because an empty output folder looks exactly like a run that never "
                           + "armed.", this);
            return;
        }

        string path = System.IO.Path.Combine(
            outputDirectory,
            "probe_" + label + "_" + System.DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv");

        System.IO.File.WriteAllText(path, sb.ToString());
        Debug.Log("[Probe] " + label + ": wrote " + count + " steps to " + path, this);
    }

    private static string R(float v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static string V(Vector3 v) => R(v.x) + "," + R(v.y) + "," + R(v.z);

    private static string Q(Quaternion q) => R(q.x) + "," + R(q.y) + "," + R(q.z) + "," + R(q.w);
}
