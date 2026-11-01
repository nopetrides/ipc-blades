using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace IPCBlades.Gates
{
    /// <summary>
    /// Gate D evidence. After every physics step (WaitForFixedUpdate) it compares what Unity reports
    /// against what the RigidIPC backend holds, per collider, and writes Logs/RigidIPC_gateD_diag.log:
    /// <list type="bullet">
    /// <item>body pose: backend pose vs Rigidbody.position/rotation and vs the Transform;</item>
    /// <item>gravity: each dynamic body's step vs the gate stepper's own update
    ///   (v' = (v + g dt)(1 - dt damping), p' = p + v' dt), one step at a time and accumulated from Play;</item>
    /// <item>vertices (only while armed, then it switches itself off): the backend's world-space shape points
    ///   vs the same points computed from the Unity collider, in index order and as nearest-point sets.</item>
    /// </list>
    /// It reports numbers and flags values over tolerance for review; it does not decide pass or fail.
    /// </summary>
    public class RigidIPCGateDiagnostics : MonoBehaviour
    {
        [Tooltip("Write a summary for every step up to this many steps after Play, then once per second.")]
        public int detailedSteps = 10;
        [Tooltip("Vertex-by-vertex comparison for this many steps after Play (step 0 = before the first step), then off.")]
        public int vertexCompareStepsOnStart = 3;
        [Tooltip("Native per-step pose trace in Logs/RigidIPC_commands.log for this many steps after Play.")]
        public int nativeTraceStepsOnStart = 5;
        [Tooltip("Position differences above this (m), or above 4 float ulps at the value's magnitude if larger, are flagged for review.")]
        public float positionTolerance = 1e-5f;
        [Tooltip("Rotation differences above this (degrees) are flagged for review.")]
        public float angleToleranceDegrees = 1e-3f;
        [Tooltip("At this step, shift every scene collider's center by centerShift (Unity then sends Shape.SetPose) " +
                 "and compare vertices for the next 3 steps. 0 = off.")]
        public int shiftCollidersAtStep;
        public Vector3 centerShift = new Vector3(0.1f, 0.2f, -0.15f);
        [Tooltip("Check each dynamic body against the gravity-only stepper. Off for scenes where scripts also push bodies.")]
        public bool checkGravity = true;
        [Tooltip("Log file under the project (Editor) or player folder.")]
        public string logPath = "Logs/RigidIPC_gateD_diag.log";

        static readonly FieldInfo CachedPtr =
            typeof(UnityEngine.Object).GetField("m_CachedPtr", BindingFlags.Instance | BindingFlags.NonPublic);

        static int s_vertexCompareSteps;

        /// <summary>Compare vertices for the next <paramref name="steps"/> physics steps, then switch off again.</summary>
        public static void ArmVertexCompare(int steps) => s_vertexCompareSteps = Mathf.Max(0, steps);

        class BodyState
        {
            public Vector3 position, velocity;
            public Quaternion rotation;
            public double px, py, pz, vx, vy, vz;   // accumulated prediction from Play, in double
        }

        readonly Dictionary<Rigidbody, BodyState> bodies = new Dictionary<Rigidbody, BodyState>();
        StreamWriter log;
        int step;
        long lastSimulate = -1;
        int flaggedTotal;
        int quitAfterSteps;   // player runs: -ipcGateQuitAfterSteps N quits after N physics steps

        void OnEnable()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath));
            log = new StreamWriter(logPath, append: true) { AutoFlush = true };
            Write($"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} Play: Unity {Application.unityVersion}, scene '{gameObject.scene.name}', " +
                  $"fixedDeltaTime={Time.fixedDeltaTime:R}, gravity={V(Physics.gravity)}, backend={Physics.GetCurrentIntegrationInfo().name}");
            Write($"native at Play: {SafeNativeDiagnostics()}");
            s_vertexCompareSteps = vertexCompareStepsOnStart;
            if (nativeTraceStepsOnStart > 0) RigidIPCNative.RigidIPC_TraceNextSteps(nativeTraceStepsOnStart);
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i] == "-ipcGateQuitAfterSteps") int.TryParse(args[i + 1], out quitAfterSteps);
        }

        void Start()
        {
            Check();   // step 0: the state Unity handed the backend, before any Simulate
            StartCoroutine(AfterEachStep());
        }

        void OnDisable()
        {
            Write($"==== Stop after {step} step(s), {flaggedTotal} item(s) flagged in total; native at Stop: {SafeNativeDiagnostics()}");
            log?.Dispose();
            log = null;
        }

        IEnumerator AfterEachStep()
        {
            var wait = new WaitForFixedUpdate();
            while (true)
            {
                yield return wait;
                step++;
                if (shiftCollidersAtStep > 0 && step == shiftCollidersAtStep) ShiftColliderCenters();
                Check();
                if (quitAfterSteps > 0 && step >= quitAfterSteps)
                {
                    Write($"quitting after {step} steps (-ipcGateQuitAfterSteps)");
                    Application.Quit();
                    yield break;
                }
            }
        }

        // Setting a center sends Shape.SetPose right away (Gate D log), so this step's Check already sees it.
        void ShiftColliderCenters()
        {
            var shifted = new List<string>();
            foreach (var c in FindObjectsByType<Collider>(FindObjectsInactive.Exclude))
            {
                if (c.gameObject.scene != gameObject.scene) continue;
                switch (c)
                {
                    case BoxCollider box: box.center += centerShift; break;
                    case SphereCollider sphere: sphere.center += centerShift; break;
                    case CapsuleCollider capsule: capsule.center += centerShift; break;
                    default: continue;
                }
                shifted.Add(Label(c));
            }
            Write($"step {step}: shifted collider centers by {V(centerShift)}: {string.Join(", ", shifted)}");
            ArmVertexCompare(4);
        }

        void Check()
        {
            long simulate = RigidIPCNative.SimulateCount();
            long simulateDelta = lastSimulate < 0 ? 0 : simulate - lastSimulate;
            lastSimulate = simulate;
            bool compareVertices = s_vertexCompareSteps > 0;
            if (compareVertices) s_vertexCompareSteps--;
            bool detailed = step <= detailedSteps || compareVertices;

            var colliders = new Dictionary<IntPtr, Collider>();
            foreach (var c in FindObjectsByType<Collider>(FindObjectsInactive.Exclude))
                if (c.gameObject.scene == gameObject.scene) colliders[(IntPtr)CachedPtr.GetValue(c)] = c;

            var lines = new List<string>();
            float maxRbPos = 0, maxRbAngle = 0, maxTfPos = 0, maxTfAngle = 0, maxOrdered = 0, maxSet = 0;
            int matched = 0, unmatchedShapes = 0, flagged = 0;
            var seen = new HashSet<Collider>();

            foreach (var s in RigidIPCNative.Shapes())
            {
                if (!colliders.TryGetValue(s.shapeUserData, out var c)) { unmatchedShapes++; continue; }
                matched++;
                seen.Add(c);
                var rb = c.attachedRigidbody;
                var backendPos = new Vector3(s.bodyPosX, s.bodyPosY, s.bodyPosZ);
                var backendRot = new Quaternion(s.bodyRotX, s.bodyRotY, s.bodyRotZ, s.bodyRotW);
                var bodyTransform = rb ? rb.transform : c.transform;
                var rbPos = rb ? rb.position : bodyTransform.position;
                var rbRot = rb ? rb.rotation : bodyTransform.rotation;
                float dRbPos = (backendPos - rbPos).magnitude, dRbAngle = (float)GateMath.AngleDeg(backendRot, rbRot);
                float dTfPos = (backendPos - bodyTransform.position).magnitude, dTfAngle = (float)GateMath.AngleDeg(backendRot, bodyTransform.rotation);
                maxRbPos = Mathf.Max(maxRbPos, dRbPos); maxRbAngle = Mathf.Max(maxRbAngle, dRbAngle);
                maxTfPos = Mathf.Max(maxTfPos, dTfPos); maxTfAngle = Mathf.Max(maxTfAngle, dTfAngle);
                float tol = Tolerance(backendPos);
                bool bad = dRbPos > tol || dTfPos > tol || dRbAngle > angleToleranceDegrees || dTfAngle > angleToleranceDegrees;
                if (bad) flagged++;
                if (detailed || bad)
                    lines.Add($"  shape#{s.shapeSerial} '{Label(c)}' {c.GetType().Name} body#{s.bodySerial}{(s.bodyIsStatic != 0 ? " static" : "")}: " +
                              $"backend pos={V(backendPos)} rot={Q(backendRot)} | rb pos={V(rbPos)} d={dRbPos:E3} m, {dRbAngle:E3} deg | " +
                              $"transform d={dTfPos:E3} m, {dTfAngle:E3} deg{(bad ? "  <-- REVIEW" : "")}");

                if (compareVertices)
                    CompareVertices(s, c, lines, ref maxOrdered, ref maxSet, ref flagged);
            }

            int missing = 0;
            foreach (var c in colliders.Values)
                if (!seen.Contains(c)) { missing++; lines.Add($"  collider '{Label(c)}' has NO backend shape  <-- REVIEW"); }
            flagged += missing;

            float maxStep = 0, maxAccum = 0;
            if (checkGravity) CheckGravity(simulateDelta, detailed, lines, ref maxStep, ref maxAccum, ref flagged);

            flaggedTotal += flagged;
            if (detailed || flagged > 0 || step % 90 == 0)
            {
                Write($"step {step} (simulate={simulate}, delta={simulateDelta}): shapes matched={matched}, backend-only={unmatchedShapes}, " +
                      $"collider-only={missing} | max body d: rb {maxRbPos:E3} m {maxRbAngle:E3} deg, transform {maxTfPos:E3} m {maxTfAngle:E3} deg | " +
                      $"gravity d: step {maxStep:E3} m, accumulated {maxAccum:E3} m" +
                      (compareVertices ? $" | vertices d: ordered {maxOrdered:E3} m, nearest {maxSet:E3} m" : "") +
                      (flagged > 0 ? $" | {flagged} item(s) flagged for review" : ""));
                foreach (var l in lines) Write(l);
            }
        }

        void CompareVertices(RigidIPCNative.ShapeSnapshot s, Collider c, List<string> lines, ref float maxOrdered, ref float maxSet, ref int flagged)
        {
            var backend = RigidIPCNative.WorldVertices(s.shapeSerial);
            var unity = UnityPoints(c, out string note);
            float ordered = 0, set = 0;
            int n = Mathf.Min(backend.Length, unity.Count);
            for (int i = 0; i < n; i++) ordered = Mathf.Max(ordered, (backend[i] - unity[i]).magnitude);
            foreach (var u in unity) set = Mathf.Max(set, Nearest(u, backend));
            foreach (var b in backend) set = Mathf.Max(set, Nearest(b, unity));
            float tol = positionTolerance;
            foreach (var u in unity) tol = Mathf.Max(tol, Tolerance(u));
            bool bad = backend.Length != unity.Count || set > tol;
            if (bad) flagged++;
            maxOrdered = Mathf.Max(maxOrdered, ordered);
            maxSet = Mathf.Max(maxSet, set);
            lines.Add($"    vertices shape#{s.shapeSerial} geom={s.geometryType} params=({s.geom0:R}, {s.geom1:R}, {s.geom2:R}) " +
                      $"local pos={V(new Vector3(s.localPosX, s.localPosY, s.localPosZ))} rot={Q(new Quaternion(s.localRotX, s.localRotY, s.localRotZ, s.localRotW))}: " +
                      $"backend={backend.Length} unity={unity.Count} ordered max={ordered:E3} nearest max={set:E3}{note}{(bad ? "  <-- REVIEW" : "")}");
            int dump = Mathf.Max(backend.Length, unity.Count);
            for (int i = 0; i < dump && i < 64; i++)
            {
                string b = i < backend.Length ? V(backend[i]) : "-";
                string u = i < unity.Count ? V(unity[i]) : "-";
                string d = i < n ? $"{(backend[i] - unity[i]).magnitude:E3}" : "-";
                lines.Add($"      [{i}] backend {b} unity {u} d={d}");
            }
            if (dump > 64) lines.Add($"      ... {dump - 64} more not listed");
        }

        void CheckGravity(long simulateDelta, bool detailed, List<string> lines, ref float maxStep, ref float maxAccum, ref int flagged)
        {
            float dt = Time.fixedDeltaTime;
            foreach (var rb in FindObjectsByType<Rigidbody>(FindObjectsInactive.Exclude))
            {
                if (rb.gameObject.scene != gameObject.scene || rb.isKinematic) continue;
                var p = rb.position;
                var v = rb.linearVelocity;
                if (!bodies.TryGetValue(rb, out var st))
                {
                    bodies[rb] = new BodyState { position = p, velocity = v, rotation = rb.rotation, px = p.x, py = p.y, pz = p.z, vx = v.x, vy = v.y, vz = v.z };
                    lines.Add($"  gravity baseline '{Label(rb)}' pos={V(p)} vel={V(v)} damping={rb.linearDamping:R} useGravity={rb.useGravity}");
                    continue;
                }
                var g = rb.useGravity ? Physics.gravity : Vector3.zero;
                float keep = Mathf.Max(0f, 1f - dt * rb.linearDamping);
                Vector3 ep = st.position, ev = st.velocity;
                for (long k = 0; k < simulateDelta; k++)
                {
                    ev = (ev + g * dt) * keep;
                    ep += ev * dt;
                    st.vx = (st.vx + g.x * dt) * keep; st.vy = (st.vy + g.y * dt) * keep; st.vz = (st.vz + g.z * dt) * keep;
                    st.px += st.vx * dt; st.py += st.vy * dt; st.pz += st.vz * dt;
                }
                float dStep = (p - ep).magnitude;
                float dAccum = (float)Math.Sqrt((p.x - st.px) * (p.x - st.px) + (p.y - st.py) * (p.y - st.py) + (p.z - st.pz) * (p.z - st.pz));
                float dRot = (float)GateMath.AngleDeg(st.rotation, rb.rotation);
                maxStep = Mathf.Max(maxStep, dStep);
                maxAccum = Mathf.Max(maxAccum, dAccum);
                bool bad = dStep > Tolerance(p) || dRot > angleToleranceDegrees;
                if (bad) flagged++;
                if (detailed || bad)
                    lines.Add($"  gravity '{Label(rb)}': pos={V(p)} predicted={V(ep)} step d={dStep:E3} m, accumulated d={dAccum:E3} m, " +
                              $"vel={V(v)} predicted={V(ev)}, rotation change={dRot:E3} deg{(bad ? "  <-- REVIEW" : "")}");
                st.position = p;
                st.velocity = v;
                st.rotation = rb.rotation;
            }
        }

        /// <summary>The same characteristic points LocalShapePoints emits natively, computed from the Unity collider.</summary>
        static List<Vector3> UnityPoints(Collider c, out string note)
        {
            note = "";
            var t = c.transform;
            var points = new List<Vector3>();
            var scale = Abs(t.lossyScale);
            switch (c)
            {
                case BoxCollider box:
                {
                    var h = box.size * 0.5f;
                    for (int i = 0; i < 8; i++)
                        points.Add(t.TransformPoint(box.center + new Vector3((i & 1) != 0 ? h.x : -h.x, (i & 2) != 0 ? h.y : -h.y, (i & 4) != 0 ? h.z : -h.z)));
                    break;
                }
                case SphereCollider sphere:
                {
                    var w = t.TransformPoint(sphere.center);
                    float r = sphere.radius * Mathf.Max(scale.x, Mathf.Max(scale.y, scale.z));
                    points.Add(w);
                    foreach (var axis in new[] { Vector3.right, Vector3.up, Vector3.forward })
                    {
                        points.Add(w + t.rotation * axis * r);
                        points.Add(w - t.rotation * axis * r);
                    }
                    break;
                }
                case CapsuleCollider capsule:
                {
                    int d = capsule.direction;
                    var w = t.TransformPoint(capsule.center);
                    float radial = d == 0 ? Mathf.Max(scale.y, scale.z) : d == 1 ? Mathf.Max(scale.x, scale.z) : Mathf.Max(scale.x, scale.y);
                    float r = capsule.radius * radial;
                    float half = 0.5f * Mathf.Max(capsule.height * scale[d], 2f * r);
                    var axes = new[] { Vector3.right, Vector3.up, Vector3.forward };
                    var a = t.rotation * axes[d];
                    points.Add(w + a * half);
                    points.Add(w - a * half);
                    for (int k = 0; k < 3; k++)
                        if (k != d)
                        {
                            points.Add(w + t.rotation * axes[k] * r);
                            points.Add(w - t.rotation * axes[k] * r);
                        }
                    note = " (capsule radial points compare as a set; their order depends on the axis rotation)";
                    break;
                }
                case MeshCollider mesh when mesh.sharedMesh != null:
                    foreach (var v in mesh.sharedMesh.vertices) points.Add(t.TransformPoint(v));
                    note = mesh.convex ? " (convex: backend keeps the source vertices, not a hull)" : "";
                    break;
                default:
                    note = $" (no Unity point model for {c.GetType().Name})";
                    break;
            }
            return points;
        }

        static float Nearest(Vector3 p, IList<Vector3> set)
        {
            float best = float.PositiveInfinity;
            foreach (var q in set) best = Mathf.Min(best, (p - q).magnitude);
            return set.Count == 0 ? float.PositiveInfinity : best;
        }

        string SafeNativeDiagnostics()
        {
            try { return RigidIPCNative.Diagnostics(); }
            catch (Exception e) { return $"unavailable ({e.GetType().Name})"; }
        }

        void Write(string line)
        {
            log?.WriteLine(line);
        }

        /// <summary>positionTolerance, or 4 float ulps at the point's largest coordinate when that is coarser.</summary>
        float Tolerance(Vector3 p) =>
            Mathf.Max(positionTolerance, 4f * 1.1920929e-7f * Mathf.Max(Mathf.Abs(p.x), Mathf.Max(Mathf.Abs(p.y), Mathf.Abs(p.z))));

        static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));
        static string V(Vector3 v) => $"({v.x:R}, {v.y:R}, {v.z:R})";
        static string Q(Quaternion q) => $"({q.x:R}, {q.y:R}, {q.z:R}, {q.w:R})";
        static string Label(Component c) => c.transform.parent ? $"{c.transform.parent.name}/{c.name}" : c.name;
    }
}
