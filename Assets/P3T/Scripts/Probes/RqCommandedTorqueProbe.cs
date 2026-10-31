using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;

/// <summary>
///     Reconstructs the <b>drive demand</b> of one servo chain, per joint, and reports the
///     drive parameters PhysX actually received.
/// </summary>
/// <remarks>
///     <para>
///         <b>READ-ONLY. This component writes nothing</b> — no joint, no drive, no body, no transform.
///         It is an instrument and must stay one.
///     </para>
///     <para>
///         <b>What this number IS and IS NOT.</b> It is <i>reconstructed drive demand</i>:
///         what the servo is <b>demanding</b>, computed from the drive law. It is <b>not</b> a
///         solver-measured actuator torque, because Unity exposes none for a
///         <see cref="ConfigurableJoint" />. Never label it "actual" torque.
///     </para>
///     <para>
///         <b>Why it must be reconstructed.</b> <c>Joint.currentTorque</c> is documented as "the torque
///         applied by the solver to satisfy all <b>constraints</b>" — a limit/lock reaction, not a drive.
///         Every angular DOF on this rig is <c>Free</c>, so it reads ~0.000 even on a loaded arm. The
///         observable that would do the job, <c>ArticulationBody.jointForce</c>, exists only on
///         <c>ArticulationBody</c>; Ragdoll Animator builds <c>ConfigurableJoint</c> on <c>Rigidbody</c>.
///     </para>
///     <para>
///         <b>THREE quantities are reported and they are deliberately NOT collapsed into one.</b> A
///         derivation says two of them must agree. A derivation is not a measurement, so both are
///         computed and their difference is recorded. What each one can establish:
///     </para>
///     <list type="number">
///         <item>
///             <b>Pose channel</b> — <c>Angle(animatorLocal, physicalLocal)</c>. No reflection, nothing to
///             break when the plugin updates.
///         </item>
///         <item>
///             <b>Reflected channel</b> — the same two poses mapped through RA2's own
///             <c>A = jointAxisConversion</c> and <c>B = initialAxisCorrection</c>, read by reflection.
///             <b>Its angle is forced equal to the pose channel by construction</b>, since
///             <c>Inv(A*Inv(q1)*B) * (A*Inv(q2)*B) == Inv(B) * (q1*Inv(q2)) * B</c> is a conjugation and
///             conjugation preserves rotation angle. Agreement therefore re-verifies quaternion algebra,
///             <b>not</b> RA2. Recorded anyway: an unexpected disagreement would mean a stated premise is
///             false.
///         </item>
///         <item>
///             <b>Target self-check</b> — <c>Angle(A*Inv(animatorLocal)*B, joint.targetRotation)</c>.
///             <b>The only independently informative one.</b> It compares a reconstruction against what
///             PhysX ACTUALLY RECEIVED, so non-zero means the target formula or the reflected fields are
///             being read wrongly.
///         </item>
///     </list>
///     <para>
///         <b>What none of them establishes.</b> Whether the mapped physical pose really is the joint's
///         current relative rotation as PhysX computes it internally. Unity exposes no read for that. It
///         stays an assumption and must be stated as one wherever these numbers are used.
///     </para>
///     <para>
///         <b>Pose convention, corrected by measurement.</b> The target is RA2's captured
///         <c>animatorLocalRotation</c>, NEVER the source bone transform: <c>RagdollBlend</c> is 100%, so
///         the SIMULATED pose is written back onto the source skeleton for rendering. Measured live,
///         <c>Angle(sourceBone.localRotation, dummyBone.localRotation)</c> is <b>0.0000</b> on every bone
///         — reading the source transform as the target compares the physics pose against itself. The
///         physical side is the dummy bone's own <c>localRotation</c>, in its own hierarchy.
///     </para>
///     <para>
///         <b>Drive-law semantics are CHECKED, not assumed.</b> The law is valid only for a Slerp drive in
///         force mode. If <c>rotationDriveMode</c> is not <c>Slerp</c>, or <c>useAcceleration</c> is true
///         (the drive then produces acceleration, not torque), this component <b>refuses to report</b>
///         rather than returning a plausible wrong figure.
///     </para>
///     <para>
///         <b>The damping term uses angular-velocity ERROR.</b> RA2 never writes
///         <c>targetAngularVelocity</c> today, so it is zero and the two forms coincide — but that is a
///         fact about the current controller, not the drive law. The field is READ every step so a future
///         controller that commands a target velocity cannot silently invalidate this.
///     </para>
///     <para>
///         <b>Modular.</b> It takes a ragdoll root and a list of bone names, resolving bones through
///         <c>RagdollAnimator2.Handler.Chains[].RuntimeBoneProcessors</c> — NOT through
///         <c>RagdollAnimator2BoneIndicator</c>, which is an optional extra feature and is absent on
///         these dummies. It knows nothing about stations, presentations, the blade solver or the
///         recorder, so it serves any arm, either side, and the free-play bays.
///     </para>
///     <para>
///         <b>SCOPE, per CH4_MeasurementContract.md §1 and §5.2.</b> This is a TIER-2 DIAGNOSTIC. It
///         characterises the apparatus; it is never a Chapter 5 match criterion and never a run-validity
///         gate. Delivered joint torque is unobservable here and is declared so — the reconstructed
///         demand is not to be compared against human joint maxima as though it were an output.
///     </para>
/// </remarks>
[DisallowMultipleComponent]
public sealed class RqCommandedTorqueProbe : MonoBehaviour
{
    /// <summary>One joint's reconstructed state for one step.</summary>
    public struct JointReading
    {
        public string Bone;

        /// <summary>Effective slerpDrive spring, read from the LIVE joint, not the handler inspector.</summary>
        public float Spring;

        public float Damper;

        /// <summary>Infinity means the drive is never clipped.</summary>
        public float MaximumForce;

        /// <summary>Channel 1 — drive-space error via poses only, degrees.</summary>
        public float ErrPoseDeg;

        /// <summary>Channel 2 — same error via RA2's reflected axis corrections, degrees.</summary>
        public float ErrReflectedDeg;

        /// <summary>Channel 2 minus channel 1. Algebra says 0; recorded rather than assumed.</summary>
        public float ErrDeltaDeg;

        /// <summary>Channel 3 — reconstruction vs the target PhysX actually received. The real check.</summary>
        public float TargetSelfCheckDeg;

        /// <summary>Magnitude of the angular-velocity ERROR (target minus current), rad/s.</summary>
        public float OmegaErrorRad;

        /// <summary>Reconstructed commanded torque from channel 1, N·m, after the clamp.</summary>
        public float CommandedNm;

        /// <summary>Same from channel 2, so the two torque figures travel together.</summary>
        public float CommandedReflectedNm;

        /// <summary>Before the clamp, so a clipping event is visible rather than hidden.</summary>
        public float CommandedUnclampedNm;

        public bool Clipped;

        /// <summary>Unloaded pose-holding cost, captured by <see cref="CaptureBaseline" />.</summary>
        public float BaselineNm;

        /// <summary>Largest <see cref="CommandedNm" /> since the last <see cref="ResetPeak" />.</summary>
        public float PeakNm;

        /// <summary>False when reflection was unavailable; channels 2 and 3 are then meaningless.</summary>
        public bool ReflectionOk;
    }

    [Header("Chain")]
    [Tooltip("The GENERATED ragdoll root, '<Dummy>-Ragdoll'. Left empty it is resolved by that name from " +
             "sourceRootName on the first step, because it does not exist at edit time.")]
    [SerializeField] private Transform ragdollRoot;

    [Tooltip("Name of the authored dummy, used to find '<name>-Ragdoll' at runtime.")]
    [SerializeField] private string sourceRootName = "";

    [Tooltip("Bone names to report, in order. Declared rather than auto-discovered so the reported chain " +
             "is a stated choice and the shoulder term is addressable by name, not by index.")]
    [SerializeField]
    private List<string> boneNames = new List<string>
        { "Right_Shoulder", "Right_UpperArm", "Right_LowerArm", "Right_Hand" };

    [Header("Measurement windows — optional, and inert when the animator is empty")]
    [Tooltip("Animator carrying the phase parameter. Left empty, baseline and peak are never captured " +
             "automatically and the public methods must be driven by something else. The probe stays " +
             "modular either way: it knows a parameter name and two values, not what a station is.")]
    [SerializeField] private Animator phaseAnimator;

    [Tooltip("Integer parameter naming the current phase.")]
    [SerializeField] private string phaseParameter = "State";

    [Tooltip("Phase during which the UNLOADED baseline is taken. The baseline is refreshed on every " +
             "fixed step of this phase, so what survives into the record is the value at its LAST step " +
             "— i.e. immediately before the next phase begins, which is the most meaningful reading of " +
             "'cost of merely holding the authored pose'.\n\n" +
             "Defaults to 2, which the controller maps to Hold. That mapping is a fact about the state " +
             "machine, not a criterion chosen here; WHICH phase the baseline should come from is the " +
             "researcher's to confirm.")]
    [SerializeField] private int baselineState = 2;

    [Tooltip("Entering this phase resets the tracked peak, so a peak belongs to one declared window " +
             "instead of accumulating from play start — which would fold in the SNAP reconstruction and " +
             "the initial ragdoll settle.\n\nDefaults to 3, which the controller maps to Contact.")]
    [SerializeField] private int peakWindowState = 3;

    [Header("Reporting")]
    [Tooltip("A refusal is a result and must be visible, not swallowed.")]
    [SerializeField] private bool logSemanticRefusal = true;

    private readonly List<JointReading> readings = new List<JointReading>();
    private readonly List<ConfigurableJoint> joints = new List<ConfigurableJoint>();
    private readonly List<Transform> sourceBones = new List<Transform>();
    private readonly List<Transform> dummyBones = new List<Transform>();
    private readonly List<object> processors = new List<object>();
    private FieldInfo fAxisConversion;
    private FieldInfo fInitialCorrection;

    /// <summary>
    ///     RA2's captured animation target. PUBLIC on the processor, unlike the two axis fields — and it
    ///     is the only correct source for the target, because RagdollBlend writes the physics pose back
    ///     onto the source skeleton.
    /// </summary>
    private PropertyInfo pAnimatorLocal;
    private bool resolved;
    private bool refused;
    private string refusalReason = "";
    private int lastPhaseState = -1;

    public IReadOnlyList<JointReading> Readings => readings;
    /// <summary>
    ///     The declared chain, in reported order. Exposed so another instrument can verify it is talking
    ///     about the same bones rather than declaring a parallel chain that could silently drift.
    /// </summary>
    public IList<string> BoneNames => boneNames;

    public bool Refused => refused;
    public string RefusalReason => refusalReason;

    /// <summary>Last phase value seen, or -1 when no phase animator is wired.</summary>
    public int CurrentPhase { get; private set; } = -1;

    private void FixedUpdate()
    {
        if (!resolved && !Resolve()) return;
        if (refused) return;

        for (int i = 0; i < joints.Count; i++)
        {
            ConfigurableJoint joint = joints[i];
            if (joint == null) continue;

            JointDrive drive = joint.slerpDrive;
            JointReading r = readings[i];

            r.Spring = drive.positionSpring;
            r.Damper = drive.positionDamper;
            r.MaximumForce = drive.maximumForce;

            Transform src = sourceBones[i];
            Transform dum = dummyBones[i];

            // THE TARGET MUST COME FROM RA2's animatorLocalRotation, NEVER from the source transform.
            //
            // RagdollBlend is 100%, so the SIMULATED pose is written back onto the source skeleton for
            // rendering. Measured live: Angle(sourceBone.localRotation, dummyBone.localRotation) is
            // 0.0000 on every bone — the source transform IS the physics pose. Reading it as the servo
            // target compares the physics pose against itself, which is why an earlier version reported
            // 28-92 deg errors and 2500-8000 N.m. That is precisely why RA2 keeps animatorLocalRotation
            // as a separate captured value.
            //
            // Verified: A * Inverse(animatorLocalRotation) * B reproduces joint.targetRotation to
            // 0.00000 deg on every bone. The targetSelfCheck channel is what established that, and it
            // exists only because the two angle channels were kept separate instead of being collapsed
            // on the strength of the algebra.
            Quaternion animatorLocal = pAnimatorLocal != null && processors[i] != null
                ? (Quaternion)pAnimatorLocal.GetValue(processors[i], null)
                : src.localRotation;
            Quaternion physicalLocal = dum.localRotation;

            // Channel 1 — poses only.
            r.ErrPoseDeg = Quaternion.Angle(animatorLocal, physicalLocal);

            // Channels 2 and 3 — through RA2's own axis corrections.
            if (r.ReflectionOk && processors[i] != null)
            {
                var a = (Quaternion)fAxisConversion.GetValue(processors[i]);
                var b = (Quaternion)fInitialCorrection.GetValue(processors[i]);

                Quaternion targetRebuilt = a * Quaternion.Inverse(animatorLocal) * b;
                Quaternion actualMapped = a * Quaternion.Inverse(physicalLocal) * b;

                r.ErrReflectedDeg = Quaternion.Angle(targetRebuilt, actualMapped);
                r.ErrDeltaDeg = r.ErrReflectedDeg - r.ErrPoseDeg;
                r.TargetSelfCheckDeg = Quaternion.Angle(targetRebuilt, joint.targetRotation);
            }

            // Angular-velocity ERROR, target READ rather than assumed zero.
            Rigidbody body = joint.GetComponent<Rigidbody>();
            Rigidbody connected = joint.connectedBody;
            Vector3 relativeOmega = body != null
                ? (connected != null ? body.angularVelocity - connected.angularVelocity : body.angularVelocity)
                : Vector3.zero;
            r.OmegaErrorRad = (joint.targetAngularVelocity - relativeOmega).magnitude;

            float unclamped = r.Spring * (r.ErrPoseDeg * Mathf.Deg2Rad) + r.Damper * r.OmegaErrorRad;
            r.CommandedUnclampedNm = unclamped;
            r.CommandedNm = Mathf.Min(unclamped, r.MaximumForce);
            r.Clipped = unclamped > r.MaximumForce;

            r.CommandedReflectedNm = Mathf.Min(
                r.Spring * (r.ErrReflectedDeg * Mathf.Deg2Rad) + r.Damper * r.OmegaErrorRad, r.MaximumForce);

            if (r.CommandedNm > r.PeakNm) r.PeakNm = r.CommandedNm;

            readings[i] = r;
        }

        DriveWindows();
    }

    /// <summary>
    ///     Advances the baseline / peak windows from the phase parameter. Inert with no animator.
    /// </summary>
    /// <remarks>
    ///     Order matters and is deliberate. The baseline is refreshed on EVERY step of the baseline
    ///     phase rather than once on entry, so the value that survives is the last one before the phase
    ///     changed — the settled pose-holding cost rather than whatever the transient looked like on
    ///     arrival. The peak is reset on ENTRY to its window, so it cannot inherit the startup spike:
    ///     an un-reset peak read 426-1235 N·m purely from the SNAP reconstruction and the initial
    ///     settle, which is not a bind peak and must never be reported as one.
    /// </remarks>
    private void DriveWindows()
    {
        if (phaseAnimator == null || string.IsNullOrEmpty(phaseParameter)) return;

        int state = phaseAnimator.GetInteger(phaseParameter);

        if (state == baselineState) CaptureBaseline();
        if (state == peakWindowState && lastPhaseState != peakWindowState) ResetPeak();

        lastPhaseState = state;
        CurrentPhase = state;
    }

    /// <summary>
    ///     Records the current commanded torque as the unloaded baseline — the cost of merely holding the
    ///     authored pose, before any blade interaction.
    /// </summary>
    /// <remarks>
    ///     Call while the chain holds its pose and is NOT in contact. A bind peak reported without it
    ///     cannot separate pose-holding demand from what the contact actually added.
    /// </remarks>
    public void CaptureBaseline()
    {
        for (int i = 0; i < readings.Count; i++)
        {
            JointReading r = readings[i];
            r.BaselineNm = r.CommandedNm;
            readings[i] = r;
        }
    }

    /// <summary>Clears tracked peaks so a peak belongs to one declared window.</summary>
    public void ResetPeak()
    {
        for (int i = 0; i < readings.Count; i++)
        {
            JointReading r = readings[i];
            r.PeakNm = 0f;
            readings[i] = r;
        }
    }

    /// <summary>Reads one bone's current reading. False when the chain has no such bone.</summary>
    public bool TryGet(string bone, out JointReading reading)
    {
        for (int i = 0; i < readings.Count; i++)
            if (readings[i].Bone == bone)
            {
                reading = readings[i];
                return true;
            }

        reading = default(JointReading);
        return false;
    }

    /// <summary>
    ///     Header lines for a sealed CSV, carrying the effective drive parameters, all three angle
    ///     channels and each joint's baseline / current / peak.
    /// </summary>
    /// <remarks>
    ///     Exists so the swept parameter is IN THE RECORD, read from the live joints rather than typed
    ///     into a filename. An unserialized experiment axis has already voided 36 runs on this project.
    /// </remarks>
    public string DeclarationLine()
    {
        if (refused) return "# servo,REFUSED," + refusalReason;
        if (readings.Count == 0) return "# servo,NOT RESOLVED,note,the generated ragdoll never appeared";

        var s = new StringBuilder(1024);
        s.Append("# servo,metric,reconstructedDriveDemand,note,");
        s.Append("COMMANDED torque from k*dTheta + c*dOmegaError clamped by maximumForce. NOT a ");
        s.Append("solver-measured actuator torque — Unity exposes none for ConfigurableJoint. ");
        s.Append("errPose and errReflected are forced equal by conjugation and agreement re-verifies ");
        s.Append("ALGEBRA not RA2; targetSelfCheck is the only independent check, comparing a ");
        s.Append("reconstruction against the target PhysX actually received");

        for (int i = 0; i < readings.Count; i++)
        {
            JointReading r = readings[i];
            s.Append(",bone,").Append(r.Bone);
            s.Append(",spring,").Append(r.Spring.ToString("F1"));
            s.Append(",damper,").Append(r.Damper.ToString("F1"));
            s.Append(",maximumForce,")
                .Append(float.IsInfinity(r.MaximumForce) ? "Infinity" : r.MaximumForce.ToString("G6"));
            s.Append(",errPoseDeg,").Append(r.ErrPoseDeg.ToString("F4"));
            s.Append(",errReflectedDeg,").Append(r.ReflectionOk ? r.ErrReflectedDeg.ToString("F4") : "NA");
            s.Append(",errDeltaDeg,").Append(r.ReflectionOk ? r.ErrDeltaDeg.ToString("F6") : "NA");
            s.Append(",targetSelfCheckDeg,").Append(r.ReflectionOk ? r.TargetSelfCheckDeg.ToString("F4") : "NA");
            s.Append(",baselineNm,").Append(r.BaselineNm.ToString("F2"));
            s.Append(",currentNm,").Append(r.CommandedNm.ToString("F2"));
            s.Append(",currentReflectedNm,").Append(r.ReflectionOk ? r.CommandedReflectedNm.ToString("F2") : "NA");
            s.Append(",peakNm,").Append(r.PeakNm.ToString("F2"));
            s.Append(",clipped,").Append(r.Clipped ? "true" : "false");
        }

        return s.ToString();
    }

    /// <summary>Resolves both skeletons via the plugin's own indicators and validates drive semantics.</summary>
    private bool Resolve()
    {
        // The handler lives on the AUTHORED dummy; the bodies it drives live under '<name>-Ragdoll'.
        GameObject sourceRootObject = string.IsNullOrEmpty(sourceRootName)
            ? gameObject
            : GameObject.Find(sourceRootName);
        if (sourceRootObject == null) return false;

        if (ragdollRoot == null)
        {
            GameObject found = GameObject.Find(sourceRootObject.name + "-Ragdoll");
            if (found == null) return false; // not generated yet; retry next step
            ragdollRoot = found.transform;
        }

        readings.Clear();
        joints.Clear();
        sourceBones.Clear();
        dummyBones.Clear();
        processors.Clear();

        // Bone pairing comes from the handler's own runtime bone processors.
        //
        // NOT from RagdollAnimator2BoneIndicator: that component is an OPTIONAL extra feature and is
        // absent on these dummies. A first version of this probe looked for it on every bone, found
        // none, and silently reported zero joints for a whole run. The processor list is always present
        // because it is what actually drives the rig.
        var procsByBone = new Dictionary<string, object>();
        foreach (MonoBehaviour mb in sourceRootObject.GetComponents<MonoBehaviour>())
        {
            if (mb == null || mb.GetType().Name != "RagdollAnimator2") continue;

            var handlerProp = mb.GetType().GetProperty("Handler");
            object handler = handlerProp != null ? handlerProp.GetValue(mb, null) : null;
            if (handler == null) continue;

            var chainsProp = handler.GetType().GetProperty("Chains");
            var chains = chainsProp != null ? chainsProp.GetValue(handler, null) as IEnumerable<object> : null;
            if (chains == null) continue;

            foreach (object chain in chains)
            {
                var listProp = chain.GetType().GetProperty("RuntimeBoneProcessors");
                var procs = listProp != null ? listProp.GetValue(chain, null) as IEnumerable<object> : null;
                if (procs == null) continue;

                foreach (object p in procs)
                {
                    if (p == null) continue;

                    var rbField = p.GetType().GetField("rigidbody");
                    var rb = rbField != null ? rbField.GetValue(p) as Rigidbody : null;
                    if (rb == null) continue;

                    if (!procsByBone.ContainsKey(rb.gameObject.name))
                        procsByBone.Add(rb.gameObject.name, p);
                }
            }
        }

        if (procsByBone.Count == 0) return false; // handler not built yet; retry next step

        for (int i = 0; i < boneNames.Count; i++)
        {
            object proc;
            if (!procsByBone.TryGetValue(boneNames[i], out proc)) continue;

            System.Type ptp = proc.GetType();

            // Physical bone from the PUBLIC rigidbody field.
            var rbF = ptp.GetField("rigidbody");
            var body = rbF != null ? rbF.GetValue(proc) as Rigidbody : null;
            Transform dum = body != null ? body.transform : null;

            // Source bone from RA2's own 'stransform' — the transform animatorLocalRotation is read from,
            // so the two sides are guaranteed to be the pair RA2 itself compares. Falls back to matching
            // by name in the source skeleton if the field is ever renamed.
            var stF = ptp.GetField("stransform", BindingFlags.Instance | BindingFlags.NonPublic);
            Transform src = stF != null ? stF.GetValue(proc) as Transform : null;
            if (src == null) src = FindByName(sourceRootObject.transform, boneNames[i]);
            if (src == null || dum == null) continue;

            ConfigurableJoint joint = dum.GetComponent<ConfigurableJoint>();
            if (joint == null) continue;

            // Semantics gate. A plausible wrong number is worse than a refusal.
            if (joint.rotationDriveMode != RotationDriveMode.Slerp)
            {
                Refuse(boneNames[i] + " rotationDriveMode is " + joint.rotationDriveMode +
                       ", but this reconstruction is valid only for Slerp");
                return true;
            }

            if (joint.slerpDrive.useAcceleration)
            {
                Refuse(boneNames[i] + " slerpDrive.useAcceleration is true, so the drive produces " +
                       "ACCELERATION not torque and k*dTheta is not N·m");
                return true;
            }

            if (pAnimatorLocal == null && proc != null)
                pAnimatorLocal = proc.GetType().GetProperty("animatorLocalRotation");

            if (fAxisConversion == null && proc != null)
            {
                System.Type pt = proc.GetType();
                fAxisConversion = pt.GetField("jointAxisConversion",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                fInitialCorrection = pt.GetField("initialAxisCorrection",
                    BindingFlags.Instance | BindingFlags.NonPublic);

                if (fAxisConversion == null || fInitialCorrection == null)
                    Debug.LogWarning("[CommandedTorque] RA2 private axis fields not found — the reflected " +
                                     "channel and the target self-check report NA. The pose channel is " +
                                     "unaffected. Likely a plugin update renamed them.", this);
            }

            sourceBones.Add(src);
            dummyBones.Add(dum);
            joints.Add(joint);
            processors.Add(proc);
            readings.Add(new JointReading
            {
                Bone = boneNames[i],
                ReflectionOk = proc != null && fAxisConversion != null && fInitialCorrection != null
            });
        }

        resolved = true;
        return readings.Count > 0;
    }

    /// <summary>Fallback pairing, used only if RA2's own 'stransform' field is ever renamed.</summary>
    private static Transform FindByName(Transform root, string name)
    {
        if (root == null) return null;
        if (root.name == name) return root;

        Transform[] all = root.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < all.Length; i++)
            if (all[i].name == name)
                return all[i];

        return null;
    }

    private void Refuse(string reason)
    {
        refused = true;
        resolved = true;
        refusalReason = reason;

        if (logSemanticRefusal)
            Debug.LogError("[CommandedTorque] REFUSED and reporting nothing: " + reason +
                           ". This is a result, not a failure to be worked around.", this);
    }
}
