using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace IPCBlades.Gates
{
    /// <summary>
    /// Gate E evidence. After every physics step (WaitForFixedUpdate) it compares the scene's Rigidbodies and
    /// ConfigurableJoints, as Unity and Ragdoll Animator 2 see them, with what the RigidIPC backend holds, and
    /// writes Logs/RigidIPC_gateE_diag.log:
    /// <list type="bullet">
    /// <item>commands: the backend's per-command histogram, differenced per step (our own reads are excluded);</item>
    /// <item>bodies: pose, velocities, flags, mass, damping, centre of mass and inertia vs the Rigidbody;</item>
    /// <item>joints: type, actors, both joint frames, axis locks, limits, drives and the drive targets vs the
    ///   ConfigurableJoint, including how many Set6DofMotorTargetRotation calls each joint received per step;</item>
    /// <item>errors and exceptions logged during Play, and the RA2 dummy container ("*-Ragdoll").</item>
    /// </list>
    /// Like the Gate D component it reports numbers and flags values for review; it does not decide pass or fail.
    /// Where the mapping between a Unity property and the backend value is not documented (limits, target rotation),
    /// it prints both and the candidate relations instead of assuming one.
    /// </summary>
    public class RigidIPCRagdollDiagnostics : MonoBehaviour
    {
        [Tooltip("Full per-body and per-joint dump for every step up to this many steps after Play, then every fullDumpEvery steps.")]
        public int detailedSteps = 10;
        public int fullDumpEvery = 90;
        [Tooltip("Position/velocity differences above this, or above 4 float ulps at the value's magnitude if larger, are flagged.")]
        public float tolerance = 1e-5f;
        [Tooltip("Rotation and direction differences above this (degrees) are flagged.")]
        public float angleToleranceDegrees = 1e-3f;
        public string logPath = "Logs/RigidIPC_gateE_diag.log";

        static readonly FieldInfo CachedPtr =
            typeof(UnityEngine.Object).GetField("m_CachedPtr", BindingFlags.Instance | BindingFlags.NonPublic);

        static readonly string[] AxisNames = { "AngularX", "AngularY", "AngularZ", "AngularYZ", "LinearX", "LinearY", "LinearZ", "Distance", "Slerp" };

        class JointState
        {
            public uint lastSets;
            public Quaternion lastTarget;
            public bool seen;
        }

        class BodyState
        {
            public string props;
        }

        StreamWriter log;
        int step;
        int flaggedTotal;
        Dictionary<uint, ulong> lastCounts;
        readonly HashSet<uint> seenCommands = new HashSet<uint>();
        readonly Dictionary<ConfigurableJoint, JointState> joints = new Dictionary<ConfigurableJoint, JointState>();
        readonly Dictionary<Rigidbody, BodyState> bodies = new Dictionary<Rigidbody, BodyState>();
        readonly List<string> newErrors = new List<string>();
        int errorCount;
        int targetBitwiseEqual;
        string lastInventory, lastContainers;

        void OnEnable()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath));
            log = new StreamWriter(logPath, append: true) { AutoFlush = true };
            Write($"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} Play: Unity {Application.unityVersion}, scene '{gameObject.scene.name}', " +
                  $"fixedDeltaTime={Time.fixedDeltaTime:R}, gravity={V(Physics.gravity)}, backend={Physics.GetCurrentIntegrationInfo().name}");
            Write($"native at Play: {SafeNativeDiagnostics()}");
            Application.logMessageReceived += OnLog;
        }

        void OnDisable()
        {
            Application.logMessageReceived -= OnLog;
            Write($"==== Stop after {step} step(s), {flaggedTotal} item(s) flagged, {errorCount} error/exception log(s) during Play; " +
                  $"native at Stop: {SafeNativeDiagnostics()}");
            log?.Dispose();
            log = null;
        }

        void OnLog(string condition, string stackTrace, LogType type)
        {
            if (type == LogType.Log || type == LogType.Warning) return;
            errorCount++;
            if (newErrors.Count < 20)
            {
                string where = stackTrace?.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
                newErrors.Add($"{type}: {condition.Split('\n')[0]} @ {where}");
            }
        }

        void Start()
        {
            Check();   // step 0: before any Simulate
            StartCoroutine(AfterEachStep());
        }

        IEnumerator AfterEachStep()
        {
            var wait = new WaitForFixedUpdate();
            while (true)
            {
                yield return wait;
                step++;
                Check();
            }
        }

        void Check()
        {
            bool detailed = step <= detailedSteps || (fullDumpEvery > 0 && step % fullDumpEvery == 0);
            var lines = new List<string>();
            int flagged = 0;

            // 1. Commands since the previous check ended (that is: what Unity and RA2 sent for this step).
            var counts = RigidIPCNative.CommandCounts();
            string mix = CommandMix(counts, lines);

            // 2. Inventory and RA2 dummy container.
            var scene = gameObject.scene;
            var rbs = FindObjectsByType<Rigidbody>(FindObjectsInactive.Exclude).Where(r => r.gameObject.scene == scene).ToList();
            var cjs = FindObjectsByType<ConfigurableJoint>(FindObjectsInactive.Exclude).Where(j => j.gameObject.scene == scene).ToList();
            var otherJoints = FindObjectsByType<Joint>(FindObjectsInactive.Exclude).Where(j => j.gameObject.scene == scene && !(j is ConfigurableJoint)).ToList();
            var containers = string.Join("; ", FindObjectsByType<Transform>(FindObjectsInactive.Include)
                .Where(t => t.gameObject.scene == scene && t.name.EndsWith("-Ragdoll"))
                .Select(t => $"'{PathOf(t)}' active={t.gameObject.activeInHierarchy} children={t.childCount} pos={V(t.position)}"));
            if (containers != lastContainers)
            {
                lines.Add($"  RA2 dummy container(s): {(containers.Length > 0 ? containers : "none")}");
                lastContainers = containers;
            }

            var bodySnaps = RigidIPCNative.Bodies();
            var jointSnaps = RigidIPCNative.Joints();
            var bodyByPtr = new Dictionary<IntPtr, RigidIPCNative.BodySnapshot>();
            foreach (var b in bodySnaps) bodyByPtr[b.userData] = b;
            var jointByPtr = new Dictionary<IntPtr, RigidIPCNative.JointSnapshot>();
            foreach (var j in jointSnaps) jointByPtr[j.userData] = j;

            // 3. Bodies.
            int bodiesMatched = 0;
            float maxPos = 0, maxVel = 0, maxAngVel = 0; double maxRot = 0;
            foreach (var rb in rbs)
            {
                if (!bodyByPtr.TryGetValue(Ptr(rb), out var b))
                {
                    flagged++;
                    lines.Add($"  body '{PathOf(rb.transform)}' has NO backend body  <-- REVIEW");
                    continue;
                }
                bodiesMatched++;
                flagged += CheckBody(rb, b, detailed, lines, ref maxPos, ref maxRot, ref maxVel, ref maxAngVel);
            }

            // 4. Joints.
            int jointsMatched = 0, targetSetsThisStep = 0, targetChangedNotSent = 0;
            targetBitwiseEqual = 0;
            double maxTargetSame = 0, maxTargetInverse = 0, maxFrame = 0; float maxAnchor = 0;
            foreach (var cj in cjs)
            {
                if (!jointByPtr.TryGetValue(Ptr(cj), out var j))
                {
                    flagged++;
                    lines.Add($"  joint '{PathOf(cj.transform)}' has NO backend joint (userData match)  <-- REVIEW");
                    continue;
                }
                jointsMatched++;
                flagged += CheckJoint(cj, j, bodyByPtr, detailed, lines, ref targetSetsThisStep, ref targetChangedNotSent,
                    ref maxTargetSame, ref maxTargetInverse, ref maxFrame, ref maxAnchor);
            }
            foreach (var oj in otherJoints)
                if (!jointByPtr.ContainsKey(Ptr(oj)))
                {
                    flagged++;
                    lines.Add($"  {oj.GetType().Name} '{PathOf(oj.transform)}' has NO backend joint  <-- REVIEW");
                }
                else if (detailed)
                {
                    var j = jointByPtr[Ptr(oj)];
                    lines.Add($"  {oj.GetType().Name} '{PathOf(oj.transform)}' -> joint#{j.serial} type={j.type} parent#{j.parentSerial} child#{j.childSerial}");
                }

            string inventory = $"rigidbodies={rbs.Count} (matched {bodiesMatched}), backend bodies={bodySnaps.Length}; " +
                               $"configurable joints={cjs.Count} (matched {jointsMatched}), other joints={otherJoints.Count}, backend joints={jointSnaps.Length}";
            if (inventory != lastInventory || detailed)
            {
                lines.Insert(0, $"  inventory: {inventory}");
                lastInventory = inventory;
            }

            // 5. Errors logged since the last check.
            if (newErrors.Count > 0)
            {
                foreach (var e in newErrors) lines.Add($"  LOGGED {e}  <-- REVIEW");
                flagged += newErrors.Count;
                newErrors.Clear();
            }

            flaggedTotal += flagged;
            Write($"step {step}: bodies d max: pos {maxPos:E3} m, rot {maxRot:E3} deg, linVel {maxVel:E3}, angVel {maxAngVel:E3} | " +
                  $"joints: anchor {maxAnchor:E3} m, frame axes {maxFrame:E3} deg, target vs backend: bitwise equal {targetBitwiseEqual}/{jointsMatched}, same {maxTargetSame:E3} deg / inverse {maxTargetInverse:E3} deg, " +
                  $"TargetRotation sets {targetSetsThisStep}/{jointsMatched}" + (targetChangedNotSent > 0 ? $", {targetChangedNotSent} changed but not sent" : "") +
                  $" | cmds: {mix}" + (flagged > 0 ? $" | {flagged} item(s) flagged" : ""));
            foreach (var l in lines) Write(l);

            // Our own reads above also go through the backend; start the next step's count after them.
            lastCounts = RigidIPCNative.CommandCounts();
        }

        string CommandMix(Dictionary<uint, ulong> counts, List<string> lines)
        {
            if (lastCounts == null) { lastCounts = counts; return "(baseline)"; }
            var parts = new List<string>();
            foreach (var kv in counts.OrderBy(k => k.Key))
            {
                lastCounts.TryGetValue(kv.Key, out var before);
                ulong d = kv.Value - before;
                if (d == 0) continue;
                string name = RigidIPCNative.CommandName(kv.Key);
                parts.Add($"{Short(name)}={d}");
                if (seenCommands.Add(kv.Key))
                    lines.Add($"  first use this Play: {name} x{d}" + (name.Contains("?") || name.StartsWith("Articulation") || name.StartsWith("Unknown") ? "  <-- REVIEW (not handled by the gate build)" : ""));
            }
            return parts.Count > 0 ? string.Join(" ", parts) : "none";
        }

        int CheckBody(Rigidbody rb, RigidIPCNative.BodySnapshot b, bool detailed, List<string> lines,
            ref float maxPos, ref double maxRot, ref float maxVel, ref float maxAngVel)
        {
            int flagged = 0;
            var notes = new List<string>();
            var bPos = GateMath.V(b.pos); var bRot = GateMath.Q(b.rot);
            float dPos = (bPos - rb.position).magnitude; double dRot = GateMath.AngleDeg(bRot, rb.rotation);
            float dVel = (GateMath.V(b.linVel) - rb.linearVelocity).magnitude, dAng = (GateMath.V(b.angVel) - rb.angularVelocity).magnitude;
            maxPos = Mathf.Max(maxPos, dPos); maxRot = Math.Max(maxRot, dRot); maxVel = Mathf.Max(maxVel, dVel); maxAngVel = Mathf.Max(maxAngVel, dAng);
            if (dPos > Tol(bPos)) notes.Add($"pos d={dPos:E3}");
            if (dRot > angleToleranceDegrees) notes.Add($"rot d={dRot:E3} deg");
            if (dVel > Tol(rb.linearVelocity)) notes.Add($"linVel d={dVel:E3} (backend {V(GateMath.V(b.linVel))} unity {V(rb.linearVelocity)})");
            if (dAng > Tol(rb.angularVelocity)) notes.Add($"angVel d={dAng:E3} (backend {V(GateMath.V(b.angVel))} unity {V(rb.angularVelocity)})");

            bool kin = (b.flags & RigidIPCNative.BodyKinematic) != 0, grav = (b.flags & RigidIPCNative.BodyGravity) != 0;
            if (kin != rb.isKinematic) notes.Add($"kinematic backend={kin} unity={rb.isKinematic}");
            if (grav != rb.useGravity) notes.Add($"gravity backend={grav} unity={rb.useGravity}");
            if (b.mass != rb.mass) notes.Add($"mass backend={b.mass:R} unity={rb.mass:R}");
            if (b.linearDamping != rb.linearDamping || b.angularDamping != rb.angularDamping)
                notes.Add($"damping backend=({b.linearDamping:R}, {b.angularDamping:R}) unity=({rb.linearDamping:R}, {rb.angularDamping:R})");
            if (b.maxAngVel != rb.maxAngularVelocity) notes.Add($"maxAngularVelocity backend={b.maxAngVel:R} unity={rb.maxAngularVelocity:R}");
            if (b.maxDepenetration != rb.maxDepenetrationVelocity) notes.Add($"maxDepenetration backend={b.maxDepenetration:R} unity={rb.maxDepenetrationVelocity:R}");
            var com = GateMath.V(b.com);
            if ((com - rb.centerOfMass).magnitude > Tol(com)) notes.Add($"centerOfMass backend={V(com)} unity={V(rb.centerOfMass)}");
            var wcom = bPos + bRot * com;
            if ((wcom - rb.worldCenterOfMass).magnitude > Tol(wcom)) notes.Add($"worldCenterOfMass backend pose*com={V(wcom)} unity={V(rb.worldCenterOfMass)}");
            var inertia = GateMath.V(b.inertia);
            if ((inertia - rb.inertiaTensor).magnitude > Tol(inertia)) notes.Add($"inertiaTensor backend={V(inertia)} unity={V(rb.inertiaTensor)}");
            double dIr = GateMath.AngleDeg(GateMath.Q(b.inertiaRot), rb.inertiaTensorRotation);
            if (dIr > angleToleranceDegrees) notes.Add($"inertiaTensorRotation d={dIr:E3} deg");
            if ((b.sleeping != 0) != rb.IsSleeping()) notes.Add($"sleeping backend={b.sleeping != 0} unity={rb.IsSleeping()}");
            flagged += notes.Count;

            // RA2 writes: record when Unity-side properties change during Play.
            string props = $"kinematic={rb.isKinematic} gravity={rb.useGravity} mass={rb.mass:R} damping=({rb.linearDamping:R}, {rb.angularDamping:R}) " +
                           $"maxAngVel={rb.maxAngularVelocity:R} maxDepen={rb.maxDepenetrationVelocity:R} ccd={rb.collisionDetectionMode}(backend {b.ccd}) " +
                           $"interp={rb.interpolation} solverIters={rb.solverIterations}/{rb.solverVelocityIterations}";
            if (!bodies.TryGetValue(rb, out var st)) bodies[rb] = st = new BodyState();
            if (st.props != props)
            {
                lines.Add($"  body#{b.serial} '{PathOf(rb.transform)}' {(st.props == null ? "properties" : "properties CHANGED")}: {props}");
                st.props = props;
            }

            if (detailed || notes.Count > 0)
                lines.Add($"  body#{b.serial} '{PathOf(rb.transform)}': pos={V(bPos)} rot={Q(bRot)} linVel={V(GateMath.V(b.linVel))} angVel={V(GateMath.V(b.angVel))} " +
                          $"d pos {dPos:E3} rot {dRot:E3} vel {dVel:E3} angVel {dAng:E3} | lastImpulse={V(GateMath.V(b.lastImpulse))} lastAngImpulse={V(GateMath.V(b.lastAngImpulse))} " +
                          $"lastForce={V(GateMath.V(b.lastForce))} lastTorque={V(GateMath.V(b.lastTorque))}" +
                          (b.hasKinematicTarget != 0 ? $" pendingKinematicTarget={V(GateMath.V(b.kinematicTargetPos))}" : "") +
                          (notes.Count > 0 ? $" | {string.Join("; ", notes)}  <-- REVIEW" : ""));
            return flagged;
        }

        int CheckJoint(ConfigurableJoint cj, RigidIPCNative.JointSnapshot j, Dictionary<IntPtr, RigidIPCNative.BodySnapshot> bodyByPtr,
            bool detailed, List<string> lines, ref int targetSets, ref int changedNotSent,
            ref double maxTargetSame, ref double maxTargetInverse, ref double maxFrame, ref float maxAnchor)
        {
            int flagged = 0;
            var notes = new List<string>();
            var own = cj.GetComponent<Rigidbody>();
            var other = cj.connectedBody;
            IntPtr ownPtr = Ptr(own), otherPtr = other ? Ptr(other) : IntPtr.Zero;

            // Which backend side holds which Unity body.
            int ownSide = j.parentUserData == ownPtr ? 0 : j.childUserData == ownPtr ? 1 : -1;
            int otherSide = other ? (j.parentUserData == otherPtr ? 0 : j.childUserData == otherPtr ? 1 : -1)
                                  : (j.parentSerial == 0 ? 0 : j.childSerial == 0 ? 1 : -1);
            if (ownSide < 0 || otherSide < 0 || ownSide == otherSide)
                notes.Add($"actors: own side={ownSide} connected side={otherSide} (parent#{j.parentSerial} child#{j.childSerial})");

            // Frames. Unity: anchor/axis/secondaryAxis on the joint's own body; connectedAnchor on the connected body (or world).
            float dAnchor = 0, dConnected = 0; double dX = 0, dY = 0;
            if (ownSide >= 0 && bodyByPtr.TryGetValue(ownPtr, out var ob))
            {
                var bodyPose = new Pose(GateMath.V(ob.pos), GateMath.Q(ob.rot));
                var local = ownSide == 0 ? new Pose(GateMath.V(j.localPos0), GateMath.Q(j.localRot0)) : new Pose(GateMath.V(j.localPos1), GateMath.Q(j.localRot1));
                var world = local.GetTransformedBy(bodyPose);
                var t = cj.transform;
                var unityAnchor = t.TransformPoint(cj.anchor);
                var ux = t.TransformDirection(cj.axis).normalized;
                var uz = Vector3.Cross(ux, t.TransformDirection(cj.secondaryAxis)).normalized;
                var uy = Vector3.Cross(uz, ux);
                dAnchor = (world.position - unityAnchor).magnitude;
                dX = GateMath.AngleDeg(world.rotation * Vector3.right, ux);
                dY = GateMath.AngleDeg(world.rotation * Vector3.up, uy);
                if (dAnchor > Tol(unityAnchor)) notes.Add($"own anchor backend={V(world.position)} unity={V(unityAnchor)} d={dAnchor:E3}");
                if (dX > angleToleranceDegrees || dY > angleToleranceDegrees) notes.Add($"own frame axes d x={dX:E3} y={dY:E3} deg");
            }
            if (otherSide >= 0)
            {
                var local = otherSide == 0 ? new Pose(GateMath.V(j.localPos0), GateMath.Q(j.localRot0)) : new Pose(GateMath.V(j.localPos1), GateMath.Q(j.localRot1));
                Vector3 backendWorld, unityWorld;
                if (other && bodyByPtr.TryGetValue(otherPtr, out var cb))
                {
                    backendWorld = local.GetTransformedBy(new Pose(GateMath.V(cb.pos), GateMath.Q(cb.rot))).position;
                    unityWorld = other.transform.TransformPoint(cj.connectedAnchor);
                }
                else { backendWorld = local.position; unityWorld = cj.connectedAnchor; }
                dConnected = (backendWorld - unityWorld).magnitude;
                if (dConnected > Tol(unityWorld)) notes.Add($"connected anchor backend={V(backendWorld)} unity={V(unityWorld)} d={dConnected:E3}");
            }
            maxAnchor = Mathf.Max(maxAnchor, Mathf.Max(dAnchor, dConnected));
            maxFrame = Math.Max(maxFrame, Math.Max(dX, dY));

            // Axis locks: ConfigurableJointMotion and Physics::JointDofLock are both Locked=0, Limited=1, Free=2.
            var motions = new (int axis, ConfigurableJointMotion m)[]
            {
                (RigidIPCNative.AxLinearX, cj.xMotion), (RigidIPCNative.AxLinearY, cj.yMotion), (RigidIPCNative.AxLinearZ, cj.zMotion),
                (RigidIPCNative.AxAngularX, cj.angularXMotion), (RigidIPCNative.AxAngularY, cj.angularYMotion), (RigidIPCNative.AxAngularZ, cj.angularZMotion),
            };
            foreach (var (axis, m) in motions)
                if ((int)j.Axis(axis, RigidIPCNative.FLock) != (int)m) notes.Add($"{AxisNames[axis]} lock backend={(int)j.Axis(axis, RigidIPCNative.FLock)} unity={m}");

            // Drives that have a direct counterpart. Only the angular drives of the active rotationDriveMode are sent
            // (Gate E run 1: in Slerp mode AngularX/AngularYZ keep the backend defaults), so the others are listed, not compared.
            var unsentDrives = new List<string>();
            bool slerp = cj.rotationDriveMode == RotationDriveMode.Slerp;
            if (slerp)
            {
                CompareDrive(notes, j, RigidIPCNative.AxSlerp, "slerpDrive", cj.slerpDrive);
                UnsentDrive(unsentDrives, j, RigidIPCNative.AxAngularX, "angularXDrive", cj.angularXDrive);
                UnsentDrive(unsentDrives, j, RigidIPCNative.AxAngularYZ, "angularYZDrive", cj.angularYZDrive);
            }
            else
            {
                CompareDrive(notes, j, RigidIPCNative.AxAngularX, "angularXDrive", cj.angularXDrive);
                CompareDrive(notes, j, RigidIPCNative.AxAngularYZ, "angularYZDrive", cj.angularYZDrive);
                UnsentDrive(unsentDrives, j, RigidIPCNative.AxSlerp, "slerpDrive", cj.slerpDrive);
            }
            CompareDrive(notes, j, RigidIPCNative.AxLinearX, "xDrive", cj.xDrive);
            CompareDrive(notes, j, RigidIPCNative.AxLinearY, "yDrive", cj.yDrive);
            CompareDrive(notes, j, RigidIPCNative.AxLinearZ, "zDrive", cj.zDrive);

            // Target rotation: the protocol does not say how ConfigurableJoint.targetRotation maps to the D6 drive
            // target, so report the angle to the backend value both as-is and inverted.
            var backendTarget = GateMath.Q(j.targetRotation);
            double same = GateMath.AngleDeg(backendTarget, cj.targetRotation), inverse = GateMath.AngleDeg(backendTarget, Quaternion.Inverse(cj.targetRotation));
            bool bitwiseSame = Same(backendTarget, cj.targetRotation);
            if (bitwiseSame) targetBitwiseEqual++;
            maxTargetSame = Math.Max(maxTargetSame, same); maxTargetInverse = Math.Max(maxTargetInverse, inverse);
            if (!joints.TryGetValue(cj, out var st)) joints[cj] = st = new JointState();
            uint sets = st.seen ? j.targetRotationSets - st.lastSets : 0;
            bool unityChanged = st.seen && !Same(st.lastTarget, cj.targetRotation);
            if (sets > 0) targetSets++;
            if (unityChanged && sets == 0) { changedNotSent++; notes.Add("targetRotation changed in Unity but no Set6DofMotorTargetRotation reached the backend"); }

            flagged += notes.Count;
            if (detailed || notes.Count > 0 || !st.seen)
            {
                lines.Add($"  joint#{j.serial} '{PathOf(cj.transform)}' -> {(other ? PathOf(other.transform) : "world")}: type={j.type} flags=0x{j.flags:x} " +
                          $"parent#{j.parentSerial}{(ownSide == 0 ? "=own" : otherSide == 0 ? "=connected" : "")} child#{j.childSerial}{(ownSide == 1 ? "=own" : otherSide == 1 ? "=connected" : "")} | " +
                          $"anchor d {dAnchor:E3} connected d {dConnected:E3} axes d {dX:E3}/{dY:E3} deg | " +
                          $"targetRotation unity={Q(cj.targetRotation)} backend={Q(backendTarget)} {(bitwiseSame ? "bitwise equal" : "DIFFERENT")} angle same={same:E3} inverse={inverse:E3} deg, " +
                          $"sets +{sets} (total {j.targetRotationSets}){(unityChanged ? " changed" : "")}" +
                          (notes.Count > 0 ? $" | {string.Join("; ", notes)}  <-- REVIEW" : ""));
            }
            if (!st.seen || (detailed && step == 0))
            {
                // Static configuration, printed raw once per joint so the limit mapping can be read off directly.
                var rows = new List<string>();
                for (int a = 0; a < 9; a++)
                    rows.Add($"{AxisNames[a]}[lock {j.Axis(a, 0)} lim {j.Axis(a, 1):R}..{j.Axis(a, 2):R} spr {j.Axis(a, 3):R}/{j.Axis(a, 4):R} " +
                             $"drive {j.Axis(a, 5):R}/{j.Axis(a, 6):R}/{j.Axis(a, 7):R} acc {j.Axis(a, 8)}]");
                lines.Add($"    backend axes: {string.Join(" ", rows)}");
                lines.Add($"    unity: lowAngX={cj.lowAngularXLimit.limit:R} highAngX={cj.highAngularXLimit.limit:R} angY={cj.angularYLimit.limit:R} angZ={cj.angularZLimit.limit:R} " +
                          $"linear={cj.linearLimit.limit:R} angXLimitSpring={cj.angularXLimitSpring.spring:R}/{cj.angularXLimitSpring.damper:R} " +
                          $"angYZLimitSpring={cj.angularYZLimitSpring.spring:R}/{cj.angularYZLimitSpring.damper:R} rotationDriveMode={cj.rotationDriveMode} " +
                          $"projection={cj.projectionMode} preprocessing={cj.enablePreprocessing} enableCollision={cj.enableCollision} " +
                          $"massScale={cj.massScale:R}/{cj.connectedMassScale:R} (backend invMassScale {j.parentInvMassScale:R}/{j.childInvMassScale:R}) " +
                          $"break={cj.breakForce:R}/{cj.breakTorque:R} (backend {j.breakForce:R}/{j.breakTorque:R}) " +
                          $"anchor={V(cj.anchor)} connectedAnchor={V(cj.connectedAnchor)} autoConfigure={cj.autoConfigureConnectedAnchor} axis={V(cj.axis)} secondaryAxis={V(cj.secondaryAxis)} " +
                          $"swapBodies={cj.swapBodies} configuredInWorldSpace={cj.configuredInWorldSpace}");
                if (unsentDrives.Count > 0) lines.Add($"    not sent for rotationDriveMode={cj.rotationDriveMode}: {string.Join("; ", unsentDrives)}");
                lines.Add($"    backend local poses: parent pos={V(GateMath.V(j.localPos0))} rot={Q(GateMath.Q(j.localRot0))} child pos={V(GateMath.V(j.localPos1))} rot={Q(GateMath.Q(j.localRot1))}");
            }
            st.seen = true;
            st.lastSets = j.targetRotationSets;
            st.lastTarget = cj.targetRotation;
            return flagged;
        }

        static void UnsentDrive(List<string> list, RigidIPCNative.JointSnapshot j, int axis, string name, JointDrive d) =>
            list.Add($"{name} unity=({d.positionSpring:R}, {d.positionDamper:R}, {d.maximumForce:R}) backend {AxisNames[axis]}=" +
                     $"({j.Axis(axis, RigidIPCNative.FDriveSpring):R}, {j.Axis(axis, RigidIPCNative.FDriveDamper):R}, {j.Axis(axis, RigidIPCNative.FDriveMaxForce):R})");

        static bool Same(Quaternion a, Quaternion b) => a.x == b.x && a.y == b.y && a.z == b.z && a.w == b.w;

        static void CompareDrive(List<string> notes, RigidIPCNative.JointSnapshot j, int axis, string name, JointDrive d)
        {
            float spring = j.Axis(axis, RigidIPCNative.FDriveSpring), damper = j.Axis(axis, RigidIPCNative.FDriveDamper), max = j.Axis(axis, RigidIPCNative.FDriveMaxForce);
            if (spring != d.positionSpring || damper != d.positionDamper || max != d.maximumForce || (j.Axis(axis, RigidIPCNative.FDriveAccel) != 0) != d.useAcceleration)
                notes.Add($"{name} unity=({d.positionSpring:R}, {d.positionDamper:R}, {d.maximumForce:R}, accel {d.useAcceleration}) " +
                          $"backend {AxisNames[axis]}=({spring:R}, {damper:R}, {max:R}, accel {j.Axis(axis, RigidIPCNative.FDriveAccel)})");
        }

        float Tol(Vector3 p) =>
            Mathf.Max(tolerance, 4f * 1.1920929e-7f * Mathf.Max(Mathf.Abs(p.x), Mathf.Max(Mathf.Abs(p.y), Mathf.Abs(p.z))));

        static IntPtr Ptr(UnityEngine.Object o) => o ? (IntPtr)CachedPtr.GetValue(o) : IntPtr.Zero;

        static string Short(string commandName)
        {
            int paren = commandName.IndexOf('(');
            return paren > 0 ? commandName.Substring(0, paren) : commandName;
        }

        string SafeNativeDiagnostics()
        {
            try { return RigidIPCNative.Diagnostics(); }
            catch (Exception e) { return $"unavailable ({e.GetType().Name})"; }
        }

        void Write(string line) => log?.WriteLine(line);

        static string PathOf(Transform t) => t.parent ? $"{t.parent.name}/{t.name}" : t.name;
        static string V(Vector3 v) => $"({v.x:R}, {v.y:R}, {v.z:R})";
        static string Q(Quaternion q) => $"({q.x:R}, {q.y:R}, {q.z:R}, {q.w:R})";
    }
}
