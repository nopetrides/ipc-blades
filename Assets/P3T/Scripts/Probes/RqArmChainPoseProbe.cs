using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
///     Records the <b>pose state</b> of the simulated arm chain on both sides — world position and
///     rotation per bone — beside the <b>servo state</b> read from <see cref="RqCommandedTorqueProbe" />,
///     into its own CSV channel.
/// </summary>
/// <remarks>
///     <para>
///         <b>Its own file, and OFF by default, on purpose.</b> The first version of this bolted ~130
///         columns onto the step trace. That would have broken column compatibility between every run
///         recorded before it and every run after, for data most runs have no use for. The three existing
///         channels are already separated by concern for exactly this reason, and
///         <c>RqPhysicalReferenceProbe</c> is the precedent: a probe that owns its own output cannot
///         confound anybody else's. Turning this on is a per-run decision, not a schema change.
///     </para>
///     <para>
///         <b>What it measures and what it only carries.</b> Pose is measured here. Servo state is
///         <i>read</i> from <see cref="RqCommandedTorqueProbe" /> and never reconstructed — a second
///         implementation of the drive law would be a second number that can disagree with the first.
///     </para>
///     <para>
///         <b>The chain is the PHYSICAL one.</b> Bones are reached by walking parents up from the physics
///         hand, so a bone can only match if it actually carries that hand. A name search from the dummy
///         root can land on the authored skeleton or on a generated <c>:Attachable Physics</c> copy, which
///         is a mistake this project has already paid for once.
///     </para>
///     <para>
///         <b>The animator's target pose is deliberately not duplicated.</b> The deviation between target
///         and physical is precisely the servo pose-channel error that
///         <see cref="RqCommandedTorqueProbe" /> already reports, and a second copy of a difference is a
///         second number that can disagree.
///     </para>
///     <para>
///         <b>Rotation is written twice.</b> The quaternion is canonical — unambiguous, no axis-order
///         convention, no gimbal. The Euler triple is for human inspection and is explicitly not the value
///         to compute with: it carries Unity's ZXY order and will hinge and wrap near the poles, which on
///         a shoulder is a real possibility rather than a theoretical one. Seeing that happen is part of
///         why it is here.
///     </para>
///     <para>
///         <b>What is NOT here.</b> No contact force, no impulse decomposition, no endpoint-force
///         reconstruction, no Jacobian. Servo demand is <i>not</i> blade load and must never be read as
///         it: delivered joint torque is unobservable on a <c>ConfigurableJoint</c>, and the contact
///         channel already lives in the sealed record and the step trace.
///     </para>
/// </remarks>
[DefaultExecutionOrder(165)]
public sealed class RqArmChainPoseProbe : MonoBehaviour
{
    /// <summary>One bone's pose at the sampled instant. World space.</summary>
    public struct BoneReading
    {
        /// <summary>Declared bone name this reading is for.</summary>
        public string Bone;

        /// <summary>False when the bone was not found on this side; every other field is then undefined.</summary>
        public bool Resolved;

        /// <summary>World position, metres.</summary>
        public Vector3 Position;

        /// <summary>World rotation. CANONICAL — compute with this one.</summary>
        public Quaternion Rotation;

        /// <summary>World rotation as Euler degrees. Inspection only; Unity ZXY, and it wraps.</summary>
        public Vector3 EulerDeg;
    }

    [Header("Capture — OFF by default. This is an optional channel.")]
    [Tooltip("Nothing is sampled or written unless this is on. Off, the component is inert and costs " +
             "nothing, which is the point: turning the arm chain on is a per-run decision and must never " +
             "silently change what other channels contain.")]
    [SerializeField] private bool captureEnabled;

    [Header("Chain — declared, and cross-checked against the servo probe")]
    [Tooltip("Bone names, shoulder first, matching RqCommandedTorqueProbe's declared order so the two " +
             "read the same way in the file. They are FOUND by walking parents up from the physics hand.")]
    [SerializeField]
    private List<string> boneNames = new List<string>
        { "Right_Shoulder", "Right_UpperArm", "Right_LowerArm", "Right_Hand" };

    [Tooltip("Servo probe for the A side. Its chain is verified against the list above and a mismatch " +
             "REFUSES, so pose columns and servo columns cannot describe different bones in one row.")]
    [SerializeField] private RqCommandedTorqueProbe servoA;

    [Tooltip("The same, for B.")]
    [SerializeField] private RqCommandedTorqueProbe servoB;

    [Header("Sides — assigned by hierarchy, never by array order")]
    [Tooltip("Dummy root for the A side. The physics hand is found beneath it. Assigning sides by " +
             "hierarchy matters because a swapped side produces a completely plausible file.")]
    [SerializeField] private Transform dummyA;

    [Tooltip("Dummy root for the B side.")]
    [SerializeField] private Transform dummyB;

    [Header("Window")]
    [SerializeField] private Animator animA;
    [SerializeField] private int contactState = 3;
    [SerializeField] private int driveState = 4;

    [Tooltip("Steps to keep recording after DRIVE ends.")]
    [SerializeField] private int endGraceSteps = 30;

    [Tooltip("Close when the station runner reports Finished. The runner ends PARKED at State 4, so " +
             "without this the window never closes on a runner-driven run.")]
    [SerializeField] private bool closeOnRunnerFinished = true;

    [SerializeField] private int maxRows = 20000;

    [Header("Output")]
    [Tooltip("Its own directory. NOT the DriveWindow folder and NOT the StepTrace folder: this channel " +
             "is optional and must not be mistaken for either of those records.")]
    /// <remarks>
    ///     BLANK by default, deliberately. It used to ship an absolute path from one machine, which
    ///     both leaked a folder structure into a public repository and silently wrote another
    ///     machine's runs into a directory that did not describe them. Blank cannot do either, and
    ///     the write path below refuses with a named error rather than throwing on
    ///     CreateDirectory("").
    /// </remarks>
    [SerializeField] private string outputDirectory = "";

    [Tooltip("Set name, mirroring the other writers so a file can be tied to its run tranche.")]
    [SerializeField] private string setName = "";

    [SerializeField] private bool writeCsvOnWindowEnd = true;

    private readonly List<BoneReading> a = new List<BoneReading>(4);
    private readonly List<BoneReading> b = new List<BoneReading>(4);
    private readonly List<string> rows = new List<string>(4096);

    private RqStationRunner runner;
    private Transform handA, handB;
    private bool recording, sawDrive;
    private int servoRead, poseRead;
    private float driveStartTime;
    private int offSteps, runIndex, step;

    /// <summary>True when the last sample could not produce a trustworthy reading.</summary>
    public bool Refused { get; private set; }

    /// <summary>Why, in a form that names the fix. Empty when not refused.</summary>
    public string RefusalReason { get; private set; } = "";

    /// <summary>True once a sample has produced readings.</summary>
    public bool HasReading { get; private set; }

    /// <summary>Whether this optional channel is capturing at all.</summary>
    public bool CaptureEnabled => captureEnabled;

    /// <summary>The declared chain, shoulder first.</summary>
    public IList<string> BoneNames => boneNames;

    /// <summary>Readings for the A side, in <see cref="BoneNames" /> order.</summary>
    public IList<BoneReading> A => a;

    /// <summary>Readings for the B side, in <see cref="BoneNames" /> order.</summary>
    public IList<BoneReading> B => b;

    private void Awake()
    {
        runner = GetComponent<RqStationRunner>();
    }

    private void FixedUpdate()
    {
        if (!captureEnabled) return;

        if (!Resolve()) return;

        int state = CurrentState();
        bool inWindow = state == contactState || state == driveState;
        bool runnerDone = closeOnRunnerFinished && runner != null && runner.Finished;

        if (inWindow && !recording && !runnerDone) Begin();
        if (!recording) return;

        if (state == driveState && !sawDrive)
        {
            sawDrive = true;
            driveStartTime = Time.time;
        }

        if (!inWindow || runnerDone)
        {
            offSteps++;
            if (offSteps > endGraceSteps)
            {
                End();
                return;
            }
        }
        else
        {
            offSteps = 0;
        }

        if (rows.Count >= maxRows) return;

        Sample();
        rows.Add(Row(state));
        step++;
    }

    /// <summary>
    ///     Samples both chains at the caller's instant. Public so a consumer can read chain pose live
    ///     without waiting for a file.
    /// </summary>
    public bool Sample()
    {
        Refused = false;
        RefusalReason = "";
        HasReading = false;
        a.Clear();
        b.Clear();

        if (boneNames == null || boneNames.Count == 0)
        {
            Refuse("no bone names are declared, so there is no chain to report.");
            return false;
        }

        if (!ChainMatches(servoA, "A") || !ChainMatches(servoB, "B")) return false;

        if (handA == null || handB == null)
        {
            Refuse("a physics hand is not present. The proxy is generated at runtime, so this is " +
                   "expected in edit mode and a problem during Play.");
            return false;
        }

        bool okA = Walk(handA, a);
        bool okB = Walk(handB, b);
        HasReading = true;
        return okA && okB;
    }

    /// <summary>Reads one bone on one side. False when that bone did not resolve.</summary>
    public bool TryGet(bool sideA, string bone, out BoneReading reading)
    {
        List<BoneReading> list = sideA ? a : b;
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].Bone != bone) continue;
            reading = list[i];
            return list[i].Resolved;
        }

        reading = default(BoneReading);
        return false;
    }

    /// <summary>
    ///     Walks from the physics hand up through its ancestors, one reading per declared bone.
    /// </summary>
    /// <remarks>
    ///     Ancestors only, so a bone can match only if it actually carries this hand — which is what it
    ///     means to be on this arm.
    /// </remarks>
    private bool Walk(Transform physicsHand, List<BoneReading> into)
    {
        bool all = true;

        for (int i = 0; i < boneNames.Count; i++)
        {
            string want = boneNames[i];
            Transform found = null;

            for (Transform t = physicsHand; t != null; t = t.parent)
            {
                if (t.name != want) continue;
                found = t;
                break;
            }

            var r = new BoneReading { Bone = want, Resolved = found != null };
            if (found != null)
            {
                r.Position = found.position;
                r.Rotation = found.rotation;
                r.EulerDeg = found.rotation.eulerAngles;
            }
            else
            {
                all = false;
            }

            into.Add(r);
        }

        return all;
    }

    private bool ChainMatches(RqCommandedTorqueProbe other, string label)
    {
        if (other == null) return true;

        IList<string> theirs = other.BoneNames;
        if (theirs == null) return true;

        if (theirs.Count != boneNames.Count)
        {
            Refuse("the " + label + " servo probe declares " + theirs.Count + " bones and this probe " +
                   boneNames.Count + ". The pose and servo columns would be about different chains.");
            return false;
        }

        for (int i = 0; i < theirs.Count; i++)
        {
            if (theirs[i] == boneNames[i]) continue;
            Refuse("chain mismatch at index " + i + ": servo probe " + label + " says '" + theirs[i] +
                   "', this probe says '" + boneNames[i] + "'. Make one the source of truth.");
            return false;
        }

        return true;
    }

    private bool Resolve()
    {
        if (handA != null && handB != null) return true;

        handA = HandUnder(dummyA);
        handB = HandUnder(dummyB);
        return handA != null && handB != null;
    }

    /// <summary>The physics hand beneath a dummy: the parent of the generated sword proxy.</summary>
    private static Transform HandUnder(Transform dummy)
    {
        if (dummy == null) return null;

        var atts = dummy.GetComponentsInChildren<FIMSpace.FProceduralAnimation.RA2AttachableObject>(true);
        for (int i = 0; i < atts.Length; i++)
        {
            var g = atts[i].GeneratedPhysicsObject;
            if (g != null) return g.transform.parent;
        }

        return null;
    }

    private int CurrentState()
    {
        return animA != null ? animA.GetInteger("State") : -1;
    }

    private void Begin()
    {
        recording = true;
        sawDrive = false;
        offSteps = 0;
        step = 0;
        rows.Clear();
    }

    private void End()
    {
        recording = false;
        runIndex++;
        if (writeCsvOnWindowEnd && rows.Count > 0) Write();
    }

    private void OnDisable()
    {
        if (recording) End();
    }

    private string Row(int state)
    {
        var sb = new StringBuilder(768);
        sb.Append(step).Append(',')
          .Append(F(Time.time)).Append(',')
          .Append(F(sawDrive ? Time.time - driveStartTime : float.NaN)).Append(',')
          .Append(state);

        servoRead = 0;
        poseRead = 0;
        AppendSide(sb, true, servoA);
        AppendSide(sb, false, servoB);

        // THREE states, not one boolean. The first version of this wrote servo_refused=0 beside 1,164
        // rows of entirely empty servo columns, because "not refused" is not the same as "produced a
        // reading" — the probe was resolving nothing and reporting no complaint. A flag that cannot
        // distinguish refused from absent will certify a hole as valid, which is the whole failure mode
        // this project keeps meeting.
        int expected = boneNames.Count * 2;
        string servoState;
        if (servoA == null || servoB == null) servoState = "absent";
        else if (servoA.Refused || servoB.Refused) servoState = "refused";
        else if (servoRead == 0) servoState = "absent";
        else if (servoRead < expected) servoState = "partial";
        else servoState = "ok";

        string poseState;
        if (Refused) poseState = "refused";
        else if (poseRead == 0) poseState = "absent";
        else if (poseRead < expected) poseState = "partial";
        else poseState = "ok";

        sb.Append(',').Append(servoState).Append(',').Append(servoRead).Append('/').Append(expected);
        sb.Append(',').Append(poseState).Append(',').Append(poseRead).Append('/').Append(expected);
        return sb.ToString();
    }

    private void AppendSide(StringBuilder sb, bool sideA, RqCommandedTorqueProbe servo)
    {
        for (int i = 0; i < boneNames.Count; i++)
        {
            string bone = boneNames[i];

            BoneReading pose;
            if (TryGet(sideA, bone, out pose))
            {
                poseRead++;
                sb.Append(',').Append(F(pose.Position.x)).Append(',').Append(F(pose.Position.y))
                  .Append(',').Append(F(pose.Position.z))
                  .Append(',').Append(F(pose.Rotation.x)).Append(',').Append(F(pose.Rotation.y))
                  .Append(',').Append(F(pose.Rotation.z)).Append(',').Append(F(pose.Rotation.w))
                  .Append(',').Append(F(pose.EulerDeg.x)).Append(',').Append(F(pose.EulerDeg.y))
                  .Append(',').Append(F(pose.EulerDeg.z));
            }
            else
            {
                sb.Append(",,,,,,,,,,");
            }

            // Initialised because the short-circuit can leave the out parameter unassigned, and a
            // half-written struct would serialise as a plausible zero.
            RqCommandedTorqueProbe.JointReading j = default(RqCommandedTorqueProbe.JointReading);
            bool haveServo = servo != null && !servo.Refused && servo.TryGet(bone, out j);

            if (haveServo)
            {
                servoRead++;
                sb.Append(',').Append(F(j.ErrPoseDeg)).Append(',').Append(F(j.CommandedNm))
                  .Append(',').Append(F(j.ErrReflectedDeg)).Append(',').Append(F(j.CommandedReflectedNm))
                  .Append(',').Append(j.Clipped ? "1" : "0")
                  .Append(',').Append(j.ReflectionOk ? "1" : "0");
            }
            else
            {
                sb.Append(",,,,,,");
            }
        }
    }

    private string Columns()
    {
        var sb = new StringBuilder(1024);
        sb.Append("step,t_s,t_since_drive_s,phase");
        for (int side = 0; side < 2; side++)
        {
            string s = side == 0 ? "A" : "B";
            for (int i = 0; i < boneNames.Count; i++)
            {
                string p = s + "_" + boneNames[i];
                sb.Append(',').Append(p).Append("_pos_x,").Append(p).Append("_pos_y,").Append(p).Append("_pos_z");
                sb.Append(',').Append(p).Append("_qx,").Append(p).Append("_qy,").Append(p).Append("_qz,").Append(p).Append("_qw");
                sb.Append(',').Append(p).Append("_eux,").Append(p).Append("_euy,").Append(p).Append("_euz");
                sb.Append(',').Append(p).Append("_err_pose_deg,").Append(p).Append("_demand_nm");
                sb.Append(',').Append(p).Append("_err_reflected_deg,").Append(p).Append("_demand_reflected_nm");
                sb.Append(',').Append(p).Append("_clipped,").Append(p).Append("_reflection_ok");
            }
        }

        sb.Append(",servo_state,servo_bones_read,arm_pose_state,arm_pose_bones_read");
        return sb.ToString();
    }

    private void Write()
    {
        if (string.IsNullOrEmpty(outputDirectory))
        {
            Debug.LogError("[ArmChainPose] please set directory on the RqArmChainPoseProbe component to record. "
                           + "outputDirectory is blank, so nothing was written. It is blank by "
                           + "default so that no machine's folder layout ships with the project; "
                           + "refusing here rather than letting CreateDirectory(\"\") throw, "
                           + "because an empty output folder looks exactly like a run that never "
                           + "armed.", this);
            return;
        }

        try
        {
            string stationName = transform.name;
            string dir = Path.Combine(outputDirectory, string.IsNullOrEmpty(setName) ? "set_UNDECLARED" : setName);
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir,
                "armchain_" + stationName + "_run" + runIndex.ToString("D2") + "_" +
                System.DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv");

            if (string.IsNullOrEmpty(setName))
                Debug.LogWarning("[ArmChain] " + stationName + ": setName is EMPTY, so this run is being " +
                                 "written to 'set_UNDECLARED'. Declare the set before collecting data " +
                                 "that has to be pooled.", this);

            var sb = new StringBuilder(1 << 16);
            sb.AppendLine("# ============================================================================");
            sb.AppendLine("# ARM CHAIN — pose state and servo state. AN OPTIONAL CHANNEL.");
            sb.AppendLine("# This file is not the sealed record and not the step trace. It exists so the");
            sb.AppendLine("# arm chain can be inspected WITHOUT changing the schema of either of those.");
            sb.AppendLine("# ============================================================================");
            sb.AppendLine("# WHAT THESE NUMBERS ARE NOT:");
            sb.AppendLine("#   *_demand_nm is RECONSTRUCTED DRIVE DEMAND, from k*dTheta + c*dOmega clamped");
            sb.AppendLine("#   by maximumForce. It is NOT a solver-measured actuator torque — Unity exposes");
            sb.AppendLine("#   none for ConfigurableJoint, every angular DOF on this rig is Free so");
            sb.AppendLine("#   Joint.currentTorque reads ~0 even on a loaded arm, and delivered joint");
            sb.AppendLine("#   torque is unobservable here. NEVER label it 'actual' torque.");
            sb.AppendLine("#   DEMAND IS NOT BLADE LOAD. How hard the servo is trying and how hard the");
            sb.AppendLine("#   blades are pushing are different quantities. Contact load lives in the");
            sb.AppendLine("#   sealed record and the step trace, and is not duplicated here.");
            sb.AppendLine("#   servo_state is ok | partial | absent | refused, and servo_bones_read is");
            sb.AppendLine("#   read/expected. THESE ARE NOT THE SAME THING. 'refused' means the");
            sb.AppendLine("#   reconstruction was not valid — the law is only defined for a Slerp drive in");
            sb.AppendLine("#   force mode. 'absent' means the probe produced no reading at all and never");
            sb.AppendLine("#   complained. An earlier version of this file reported a single refused=0 flag");
            sb.AppendLine("#   beside entirely empty servo columns, which certified a hole as valid.");
            sb.AppendLine("#   Unless servo_state is 'ok', treat every demand figure in the row as absent");
            sb.AppendLine("#   rather than zero. arm_pose_state works the same way for the pose block.");
            sb.AppendLine("#");
            sb.AppendLine("# *_err_pose_deg is the servo tracking error — the deflection of the virtual");
            sb.AppendLine("# spring, i.e. how far contact has held this joint from where its actuator");
            sb.AppendLine("# commanded it. That is a DISPLACEMENT, and turning it into a force needs the");
            sb.AppendLine("# drive law, damping, the chain and the geometry. Read it as compliance.");
            sb.AppendLine("#");
            sb.AppendLine("# *_qx/qy/qz/qw is the CANONICAL rotation — compute with it.");
            sb.AppendLine("# *_eux/euy/euz is Euler degrees, Unity ZXY, INSPECTION ONLY. It hinges and");
            sb.AppendLine("# wraps, and on a shoulder that is a real possibility, not a theoretical one.");
            sb.AppendLine("#");
            sb.AppendLine("# Pose is the PHYSICAL chain, reached by walking parents up from the physics");
            sb.AppendLine("# hand. The animator's TARGET pose is deliberately not duplicated: the");
            sb.AppendLine("# deviation between target and physical is *_err_pose_deg above.");
            sb.AppendLine("#");
            sb.AppendLine("# Join to the other channels on 'step' within a run, and align on");
            sb.AppendLine("# t_since_drive_s, which is zeroed at the first DRIVE step and NaN before it.");
            sb.AppendLine("# EVERY fixed step in the window is written. No decimation.");
            sb.AppendLine("# station=" + stationName + " run=" + runIndex + " set=" + setName);
            sb.AppendLine("# fixedDeltaTime=" + Time.fixedDeltaTime.ToString("R", CultureInfo.InvariantCulture));
            sb.Append("# armChain,bones,");
            for (int i = 0; i < boneNames.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(boneNames[i]);
            }

            sb.AppendLine(",frame,world");
            if (servoA != null) sb.AppendLine("# servoA," + servoA.DeclarationLine().TrimStart('#', ' '));
            if (servoB != null) sb.AppendLine("# servoB," + servoB.DeclarationLine().TrimStart('#', ' '));

            sb.AppendLine(Columns());
            for (int i = 0; i < rows.Count; i++) sb.AppendLine(rows[i]);

            File.WriteAllText(path, sb.ToString());
            Debug.Log("[ArmChain] " + stationName + ": wrote " + rows.Count + " rows to " + path, this);
        }
        catch (System.Exception ex)
        {
            Debug.LogError("[ArmChain] " + transform.name + ": write FAILED. " + ex.Message + "\n" +
                           ex.StackTrace, this);
        }
    }

    private void Refuse(string why)
    {
        Refused = true;
        RefusalReason = why;
    }

    private static string F(float v)
    {
        return float.IsNaN(v) ? "NaN" : v.ToString("R", CultureInfo.InvariantCulture);
    }
}
