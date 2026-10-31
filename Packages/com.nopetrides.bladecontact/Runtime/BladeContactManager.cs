using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace BladeContact
{
    /// <summary>
    /// Sole owner of contact between registered blade shells: drives one swept query per pair per fixed
    /// step and turns a valid time of impact into an impulse on two dynamic bodies.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Ownership.</b> For a registered pair, and only for that pair's two blade colliders, PhysX
    /// collision is suppressed. Everything else a sword can touch — guard, grip, pommel, the world, and
    /// any blade against any UNregistered collider — stays with PhysX exactly as before. The point is one
    /// owner per relationship, not replacing the physics engine.
    /// </para>
    /// <para>
    /// <b>No pose authority.</b> Registered swords stay dynamic Rigidbodies. This component reads their
    /// poses and velocities and returns impulses; it never writes a transform, never makes a body
    /// kinematic, and never teleports anything to the time of impact. A solver that moved bodies directly
    /// would be easier to make look right and impossible to reconcile with the rest of the simulation.
    /// </para>
    /// <para>
    /// <b>Abandoned queries are not contacts.</b> A <see cref="BladeContactStatus.BudgetExceeded"/> result
    /// carries no usable data. It is traced, counted, and otherwise ignored — never fed to the response.
    /// The diagnostic rails stay enabled at their stock values in play mode for exactly this reason.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class BladeContactManager : MonoBehaviour
    {
        /// <summary>One registered blade-vs-blade relationship.</summary>
        private sealed class Pair
        {
            public BladeShell A;
            public BladeShell B;
            public bool Suppressed;
            public int LastDrivenStep = -1;

            /// <summary>
            /// True once contact for this pair has been handed to PhysX at a validated boundary. While
            /// set, this manager stops applying its own response, so exactly one system is resolving the
            /// contact at any moment.
            /// </summary>
            public bool HandedOff;

            /// <summary>Last usable contact normal, kept for the frame where the witnesses coincide.</summary>
            public Vector3 LastNormal;

            // Per-relationship totals for the current run, so a consumer can verify that a distant
            // relationship really did cost nothing beyond its broad-phase test.
            public int BroadPhaseTests;
            public int BroadPhaseRejections;
            public int ExactSweeps;
            public int Contacts;
            public int Responses;

            public void ResetCounters()
            {
                BroadPhaseTests = 0;
                BroadPhaseRejections = 0;
                ExactSweeps = 0;
                Contacts = 0;
                Responses = 0;
            }
            public readonly BladeShellScratch Scratch = new BladeShellScratch();
            public readonly BladeContactStats Stats = new BladeContactStats();
        }

        /// <summary>Read-only view of one cached relationship, for consumers that need to audit cost.</summary>
        public readonly struct PairReport
        {
            public readonly BladeShell A;
            public readonly BladeShell B;
            public readonly bool Suppressed;
            public readonly bool HandedOff;
            public readonly int BroadPhaseTests;
            public readonly int BroadPhaseRejections;
            public readonly int ExactSweeps;
            public readonly int Contacts;
            public readonly int Responses;

            public PairReport(
                BladeShell a, BladeShell b, bool suppressed, bool handedOff,
                int broadPhaseTests, int broadPhaseRejections, int exactSweeps, int contacts, int responses)
            {
                A = a;
                B = b;
                Suppressed = suppressed;
                HandedOff = handedOff;
                BroadPhaseTests = broadPhaseTests;
                BroadPhaseRejections = broadPhaseRejections;
                ExactSweeps = exactSweeps;
                Contacts = contacts;
                Responses = responses;
            }
        }

        [Header("Solver")]
        [Tooltip("Numerical settings for conservative advancement. Diagnostic ceilings stay enabled: an " +
                 "abandoned query is reported, never consumed as contact.")]
        [SerializeField] private BladeSweepSettings settings = BladeSweepSettings.Default;

        [Header("Response")]
        [Tooltip("Fraction of the computed normal impulse actually applied. 1 removes exactly the closing " +
                 "velocity the time of impact forbids.")]
        [Range(0f, 1f)]
        [SerializeField] private float responseScale = 1f;

        [Tooltip("Separation, in metres, below which a residual overlap is pushed apart over one step. " +
                 "Recovery only; the sweep is what prevents penetration in the first place.")]
        [SerializeField] private float recoverySeparation = 0.0002f;

        [Tooltip("Largest recovery speed, m/s, so a deep residual cannot fling the swords apart.")]
        [SerializeField] private float maxRecoverySpeed = 0.25f;

        [Header("Tangential response policy")]
        [Tooltip("How tangential behaviour is chosen from a contact's SEMANTIC scenario. This never " +
                 "affects admissibility, time of impact, separation, or the non-crossing invariant.")]
        [SerializeField] private BladeTangentialPolicy tangentialPolicy = BladeTangentialPolicy.Default;

        [Header("Trace")]
        [SerializeField] private bool traceEnabled = true;

        [Tooltip("Records kept in the ring buffer.")]
        [SerializeField] private int traceCapacity = 512;

        [Tooltip("Also write every contact and every abandoned query to the Unity console.")]
        [SerializeField] private bool logContacts;

        private readonly List<BladeShell> participants = new List<BladeShell>();
        private readonly List<Pair> pairs = new List<Pair>();
        private readonly Queue<BladeContactEvent> trace = new Queue<BladeContactEvent>();
        private readonly Stopwatch queryClock = new Stopwatch();
        private readonly Stopwatch stepClock = new Stopwatch();
        private readonly Stopwatch responseClock = new Stopwatch();

        private int step;

        /// <summary>Fixed steps driven since this manager woke.</summary>
        public int Step => step;

        /// <summary>Total wall time of the last fixed step's driving, milliseconds.</summary>
        public double LastStepMs { get; private set; }

        /// <summary>Of <see cref="LastStepMs"/>, the part spent inside sweep queries.</summary>
        public double LastSolverMs { get; private set; }

        /// <summary>Of <see cref="LastStepMs"/>, the part spent computing and applying impulses.</summary>
        public double LastResponseMs { get; private set; }

        /// <summary>Pairs driven on the last step, and how many the broad phase rejected outright.</summary>
        public int LastPairsDriven { get; private set; }

        public int LastPairsRejected { get; private set; }

        /// <summary>Contacts responded to on the last step.</summary>
        public int LastContacts { get; private set; }

        /// <summary>Queries abandoned on the diagnostic ceiling since this manager woke. Should stay 0.</summary>
        public int AbandonedQueries { get; private set; }

        public IEnumerable<BladeContactEvent> Trace => trace;

        public int RegisteredPairCount => pairs.Count;

        /// <summary>Every shell currently participating in custom blade contact.</summary>
        public IEnumerable<BladeShell> Participants => participants;

        public int ParticipantCount => participants.Count;

        /// <summary>
        /// Adds a shell to the participating set. Every unordered relationship with the shells already
        /// registered becomes a candidate.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Registration is by PARTICIPANT, not by pair. The manager is told which swords take part and
        /// derives the relationships itself, so it never holds a list of which specimens are "supposed" to
        /// meet. It has no concept of a bay, a rig, or a scenario, and cannot be configured into ignoring a
        /// relationship that physically exists.
        /// </para>
        /// <para>
        /// That means N registered swords yield N(N-1)/2 relationships, including ones currently far apart.
        /// Those are not a problem to be filtered out: the swept broad phase rejects a separated pair for
        /// almost nothing, and if two swords from opposite ends of the scene are brought together they
        /// become live candidates on their own, which is the correct behaviour rather than a special case.
        /// </para>
        /// </remarks>
        public bool RegisterShell(BladeShell shell)
        {
            if (shell == null || shell.Body == null) return false;
            if (participants.Contains(shell)) return false;

            foreach (BladeShell other in participants)
            {
                var pair = new Pair { A = other, B = shell };
                pairs.Add(pair);
                ApplySuppression(pair, true);
            }

            participants.Add(shell);
            return true;
        }

        /// <summary>Removes a shell and every relationship it took part in, returning them to PhysX.</summary>
        public bool UnregisterShell(BladeShell shell)
        {
            if (shell == null || !participants.Remove(shell)) return false;

            for (int i = pairs.Count - 1; i >= 0; i--)
            {
                Pair pair = pairs[i];
                if (pair.A != shell && pair.B != shell) continue;

                if (pair.Suppressed) ApplySuppression(pair, false);
                pairs.RemoveAt(i);
            }

            return true;
        }

        /// <summary>Per-relationship cost and ownership, for auditing.</summary>
        public IEnumerable<PairReport> PairReports()
        {
            foreach (Pair pair in pairs)
                yield return new PairReport(
                    pair.A, pair.B, pair.Suppressed, pair.HandedOff,
                    pair.BroadPhaseTests, pair.BroadPhaseRejections,
                    pair.ExactSweeps, pair.Contacts, pair.Responses);
        }

        /// <summary>
        /// Hands the registered pair's contact to PhysX, or takes it back, at a boundary the caller has
        /// already validated.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This touches exactly the two registered blade colliders and nothing else. Guard, grip, pommel,
        /// markers and every world relationship keep whatever ownership they already had — handing over
        /// blade-vs-blade must not quietly hand over anything adjacent to it.
        /// </para>
        /// <para>
        /// Handing off also stops this manager applying its own impulse for the pair. Two systems both
        /// resolving one contact would double the response and neither trace would describe what actually
        /// happened.
        /// </para>
        /// </remarks>
        public bool SetPairPhysXContact(
            BladeShell a, BladeShell b, bool physXOwnsContact,
            BladeContactScenario scenario = BladeContactScenario.FlatFlat)
        {
            foreach (Pair pair in pairs)
            {
                bool same = (pair.A == a && pair.B == b) || (pair.A == b && pair.B == a);
                if (!same) continue;

                if (physXOwnsContact) ApplyTangentialPolicy(pair, scenario);

                ApplySuppression(pair, !physXOwnsContact);
                pair.HandedOff = physXOwnsContact;
                return true;
            }

            return false;
        }

        /// <summary>The tangential policy this manager will apply, for inspection and provenance.</summary>
        public BladeTangentialPolicy TangentialPolicy => tangentialPolicy;

        /// <summary>
        /// Applies the scenario's tangential parameters to the two registered blade colliders, at the
        /// moment contact is handed to PhysX.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Applied here and nowhere else, because here is the only point at which a contact has already
        /// been established as valid. Nothing upstream — the sweep, the time of impact, the separation, the
        /// non-crossing invariant — can see these values, which is what keeps a response setting from
        /// quietly changing whether a contact is admitted.
        /// </para>
        /// <para>
        /// The material is written onto the two blade colliders only. Guard, grip, pommel and every world
        /// relationship keep their own materials.
        /// </para>
        /// </remarks>
        private void ApplyTangentialPolicy(Pair pair, BladeContactScenario scenario)
        {
            BladeTangentialParameters parameters = tangentialPolicy.Resolve(scenario);

            ApplyMaterial(pair.A, parameters);
            ApplyMaterial(pair.B, parameters);

            if (logContacts)
                UnityEngine.Debug.Log(
                    $"[BladeContact] tangential policy at handoff -> {tangentialPolicy.Describe(scenario)}",
                    this);
        }

        private static void ApplyMaterial(BladeShell shell, in BladeTangentialParameters parameters)
        {
            Collider blade = shell != null ? shell.BladeCollider : null;
            if (blade == null) return;

            PhysicsMaterial material = blade.sharedMaterial;
            if (material == null)
            {
                material = new PhysicsMaterial($"BladeContact_{shell.name}") { hideFlags = HideFlags.DontSave };
                blade.sharedMaterial = material;
            }

            material.dynamicFriction = parameters.DynamicFriction;
            material.staticFriction = parameters.StaticBindThreshold;
        }

        /// <summary>True when PhysX currently owns the registered pair's contact.</summary>
        public bool IsHandedOff(BladeShell a, BladeShell b)
        {
            foreach (Pair pair in pairs)
            {
                bool same = (pair.A == a && pair.B == b) || (pair.A == b && pair.B == a);
                if (same) return pair.HandedOff;
            }

            return false;
        }

        /// <summary>Releases a relationship and returns it to PhysX.</summary>
        public bool UnregisterPair(BladeShell a, BladeShell b)
        {
            for (int i = 0; i < pairs.Count; i++)
            {
                Pair pair = pairs[i];
                bool same = (pair.A == a && pair.B == b) || (pair.A == b && pair.B == a);
                if (!same) continue;

                ApplySuppression(pair, false);
                pairs.RemoveAt(i);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Suppresses or restores PhysX collision for one registered pair's blade colliders.
        /// </summary>
        /// <remarks>
        /// Deliberately narrow. This touches one collider on each sword, so guard, grip, pommel and every
        /// world collider keep their PhysX relationships. Layer-based filtering would have been less code
        /// and would have silently taken the blade out of world collision too.
        /// </remarks>
        private void ApplySuppression(Pair pair, bool suppress)
        {
            Collider bladeA = pair.A != null ? pair.A.BladeCollider : null;
            Collider bladeB = pair.B != null ? pair.B.BladeCollider : null;

            if (bladeA == null || bladeB == null)
            {
                UnityEngine.Debug.LogWarning(
                    $"[BladeContact] {name}: a registered pair is missing a blade collider, so PhysX still " +
                    "owns blade-vs-blade for it. Two owners will fight. Assign BladeShell.bladeCollider.",
                    this);
                return;
            }

            Physics.IgnoreCollision(bladeA, bladeB, suppress);
            pair.Suppressed = suppress;
        }

        private void OnDisable()
        {
            // Hand every relationship back rather than leaving PhysX permanently suppressed.
            foreach (Pair pair in pairs)
                if (pair.Suppressed)
                    ApplySuppression(pair, false);
        }

        private void FixedUpdate()
        {
            step++;
            stepClock.Restart();

            LastSolverMs = 0d;
            LastResponseMs = 0d;
            LastPairsDriven = 0;
            LastPairsRejected = 0;
            LastContacts = 0;

            float dt = Time.fixedDeltaTime;

            for (int i = 0; i < pairs.Count; i++)
            {
                Pair pair = pairs[i];

                // Exactly once per fixed step, whatever else calls in.
                if (pair.LastDrivenStep == step) continue;
                pair.LastDrivenStep = step;

                Drive(pair, dt);
            }

            stepClock.Stop();
            LastStepMs = stepClock.Elapsed.TotalMilliseconds;
        }

        private void Drive(Pair pair, float dt)
        {
            BladeShell a = pair.A;
            BladeShell b = pair.B;
            if (a == null || b == null || a.Body == null || b.Body == null) return;

            LastPairsDriven++;

            BladePose startA = a.SimulatedPlacement;
            BladePose startB = b.SimulatedPlacement;
            BladePose endA = Predict(a.Body, a.transform, dt);
            BladePose endB = Predict(b.Body, b.transform, dt);

            BladeShellData dataA = a.Data;
            BladeShellData dataB = b.Data;

            pair.Stats.Reset();

            queryClock.Restart();

            // The broad phase is the whole reason a scene of swords is affordable: a pair that cannot
            // reach contact this step must cost almost nothing, so it is asked first and separately.
            bool rejected = BladeShellSweep.CanSkipPair(
                dataA, startA, endA, dataB, startB, endB, settings.ContactMargin, pair.Stats);

            pair.BroadPhaseTests++;
            if (rejected) pair.BroadPhaseRejections++;

            BladeShellContact contact = default;
            if (!rejected)
            {
                pair.ExactSweeps++;
                contact = BladeShellSweep.FindFirstContact(
                    dataA, startA, endA, dataB, startB, endB, settings, pair.Scratch, pair.Stats);
            }

            queryClock.Stop();
            double queryMs = queryClock.Elapsed.TotalMilliseconds;
            LastSolverMs += queryMs;

            if (rejected)
            {
                LastPairsRejected++;
                Record(pair, true, default, Vector3.zero, 0f, queryMs, dataA, dataB);
                return;
            }

            if (!contact.IsValid)
            {
                // Abandoned. Traced and counted; never consumed. PhysX is suppressed for this pair, so
                // this is a step with no owner reporting - which is why the counter is surfaced.
                AbandonedQueries++;
                Record(pair, false, contact, Vector3.zero, 0f, queryMs, dataA, dataB);

                if (logContacts)
                    UnityEngine.Debug.LogWarning(
                        $"[BladeContact] step {step}: query abandoned on the diagnostic ceiling for " +
                        $"{a.name} x {b.name}. No contact was consumed.", this);
                return;
            }

            float impulse = 0f;

            // While handed off, PhysX is resolving this contact. The sweep still runs and still traces,
            // because the guard needs its verdict; only the response steps aside.
            if (contact.Status == BladeContactStatus.Contact && !pair.HandedOff)
            {
                Vector3 normal = ResolveNormal(pair, contact, startA, startB);
                responseClock.Restart();
                impulse = Respond(a.Body, b.Body, contact, normal);
                pair.Contacts++;
                if (impulse != 0f) pair.Responses++;
                responseClock.Stop();
                LastResponseMs += responseClock.Elapsed.TotalMilliseconds;
                LastContacts++;
            }

            Record(pair, false, contact, pair.LastNormal, impulse, queryMs, dataA, dataB);
        }

        /// <summary>
        /// A usable contact direction even on the step where the two witness points coincide.
        /// </summary>
        /// <remarks>
        /// The narrow phase reports SEPARATION, not penetration depth, so it saturates at zero the moment
        /// the shells actually touch — and at exactly zero the two witnesses are the same point, whose
        /// difference has no direction. Taken literally that yields a zero normal, no impulse, and a blade
        /// that passes straight through on the one step where the response mattered most. The last usable
        /// direction is therefore carried forward; it is at most one step stale, and over one step at these
        /// rates the contact direction barely turns. Falling back to the root-box centres covers the case
        /// where contact begins already touching and no previous normal exists.
        /// </remarks>
        private static Vector3 ResolveNormal(
            Pair pair, in BladeShellContact contact, in BladePose poseA, in BladePose poseB)
        {
            Vector3 normal = contact.Pair.Normal;

            if (normal.sqrMagnitude > 1e-12f)
            {
                pair.LastNormal = normal;
                return normal;
            }

            if (pair.LastNormal.sqrMagnitude > 1e-12f) return pair.LastNormal;

            Vector3 centreA = poseA.TransformPoint(pair.A.Data.Bvh.GetNode(0).Centre);
            Vector3 centreB = poseB.TransformPoint(pair.B.Data.Bvh.GetNode(0).Centre);
            Vector3 fallback = centreB - centreA;

            pair.LastNormal = fallback.sqrMagnitude > 1e-12f ? fallback.normalized : Vector3.up;
            return pair.LastNormal;
        }

        /// <summary>
        /// Where a body's transform will be after one step if nothing interferes, from its CURRENT
        /// velocities.
        /// </summary>
        /// <remarks>
        /// FixedUpdate runs before the physics step, so these velocities are the ones about to be
        /// integrated. Rotation is taken about the centre of mass, which is what the solver integrates,
        /// not about the transform origin — on a sword those are ~93 mm apart and using the wrong one puts
        /// the predicted tip in the wrong place.
        /// </remarks>
        private static BladePose Predict(Rigidbody body, Transform t, float dt)
        {
            Vector3 com = body.worldCenterOfMass;
            Vector3 omega = body.angularVelocity;

            float angle = omega.magnitude * dt;
            Quaternion spin = angle > 1e-8f
                ? Quaternion.AngleAxis(angle * Mathf.Rad2Deg, omega / omega.magnitude)
                : Quaternion.identity;

            Vector3 newCom = com + body.linearVelocity * dt;
            Vector3 offset = t.position - com;

            return new BladePose(newCom + spin * offset, spin * t.rotation);
        }

        /// <summary>
        /// Turns a time of impact into a normal impulse that removes exactly the closing velocity the
        /// step is not allowed to have.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The sweep says the pair may travel a fraction <c>toi</c> of this step before touching. Left
        /// alone, the bodies would travel all of it. Removing <c>(1 - toi)</c> of the closing normal
        /// velocity makes the step's normal displacement equal to what was permitted, so a grazing
        /// approach that only just reaches contact is barely altered while a step that would drive deep
        /// has almost all of its closing velocity taken away.
        /// </para>
        /// <para>
        /// The impulse is scaled by the pair's effective mass along the normal, including both bodies'
        /// rotational inertia about the contact point. Using linear mass alone would over-apply on a
        /// struck blade tip, where most of the response should become rotation, and the swords would jump.
        /// </para>
        /// <para>
        /// Restitution is zero. A bouncing blade is not what the non-crossing contract is about, and any
        /// energy return here would be an invented physical claim.
        /// </para>
        /// </remarks>
        private float Respond(
            Rigidbody bodyA, Rigidbody bodyB, in BladeShellContact contact, Vector3 normal)
        {
            if (normal.sqrMagnitude < 1e-12f) return 0f;

            Vector3 point = (contact.Pair.WitnessA + contact.Pair.WitnessB) * 0.5f;
            Vector3 rA = point - bodyA.worldCenterOfMass;
            Vector3 rB = point - bodyB.worldCenterOfMass;

            Vector3 velA = bodyA.linearVelocity + Vector3.Cross(bodyA.angularVelocity, rA);
            Vector3 velB = bodyB.linearVelocity + Vector3.Cross(bodyB.angularVelocity, rB);

            // Normal runs A -> B, so a negative normal speed means the shells are closing.
            float closing = Vector3.Dot(velB - velA, normal);

            float forbidden = closing < 0f ? -closing * (1f - Mathf.Clamp01(contact.TimeOfImpact)) : 0f;

            // Residual overlap recovery, capped so a deep result cannot become a launch.
            float deficit = recoverySeparation - contact.Pair.Separation;
            float recovery = deficit > 0f
                ? Mathf.Min(deficit / Mathf.Max(Time.fixedDeltaTime, 1e-6f), maxRecoverySpeed)
                : 0f;

            float target = forbidden + recovery;
            if (target <= 0f) return 0f;

            float inverseMass = InverseEffectiveMass(bodyA, rA, normal) + InverseEffectiveMass(bodyB, rB, normal);
            if (inverseMass <= 1e-12f) return 0f;

            float magnitude = responseScale * target / inverseMass;
            Vector3 impulse = normal * magnitude;

            // Equal and opposite, applied at the witness so the torque is the contact's own.
            if (!bodyA.isKinematic) bodyA.AddForceAtPosition(-impulse, point, ForceMode.Impulse);
            if (!bodyB.isKinematic) bodyB.AddForceAtPosition(impulse, point, ForceMode.Impulse);

            return magnitude;
        }

        /// <summary>
        /// The body's inverse effective mass at a contact offset along a normal:
        /// <c>1/m + n . ((I^-1 (r x n)) x r)</c>. Kinematic bodies contribute nothing.
        /// </summary>
        private static float InverseEffectiveMass(Rigidbody body, Vector3 r, Vector3 normal)
        {
            if (body.isKinematic) return 0f;

            Vector3 angular = Vector3.Cross(InverseInertia(body, Vector3.Cross(r, normal)), r);
            return 1f / body.mass + Vector3.Dot(normal, angular);
        }

        /// <summary>Applies the body's inverse world inertia tensor to a world vector.</summary>
        private static Vector3 InverseInertia(Rigidbody body, Vector3 world)
        {
            Quaternion frame = body.rotation * body.inertiaTensorRotation;
            Vector3 local = Quaternion.Inverse(frame) * world;
            Vector3 tensor = body.inertiaTensor;

            local.x = tensor.x > 1e-12f ? local.x / tensor.x : 0f;
            local.y = tensor.y > 1e-12f ? local.y / tensor.y : 0f;
            local.z = tensor.z > 1e-12f ? local.z / tensor.z : 0f;

            return frame * local;
        }

        private void Record(
            Pair pair, bool rejected, in BladeShellContact contact,
            Vector3 normal, float impulse, double queryMs,
            BladeShellData dataA, BladeShellData dataB)
        {
            if (!traceEnabled) return;

            BladeFeatureType typeA = BladeFeatureType.Unresolved;
            BladeFeatureType typeB = BladeFeatureType.Unresolved;
            string idA = "(none)";
            string idB = "(none)";

            if (!rejected && contact.Status == BladeContactStatus.Contact)
            {
                typeA = dataA.TypeOf(contact.Pair.FeatureA);
                typeB = dataB.TypeOf(contact.Pair.FeatureB);
                idA = dataA.IdOf(contact.Pair.FeatureA);
                idB = dataB.IdOf(contact.Pair.FeatureB);
            }

            var record = new BladeContactEvent(
                step, pair.A.name, pair.B.name, rejected,
                rejected ? BladeContactStatus.NoContact : contact.Status,
                rejected ? 1f : contact.TimeOfImpact,
                typeA, typeB, idA, idB, normal,
                rejected ? float.MaxValue : contact.Pair.Separation,
                impulse, queryMs, rejected ? 0 : contact.Iterations, pair.Stats.ExactFeatureTests);

            trace.Enqueue(record);
            while (trace.Count > Mathf.Max(1, traceCapacity)) trace.Dequeue();

            if (logContacts && record.IsUsable) UnityEngine.Debug.Log($"[BladeContact] {record}", this);
        }

        /// <summary>Empties the trace ring buffer.</summary>
        public void ClearTrace() => trace.Clear();

        /// <summary>
        /// Returns this manager to the state it had before any run: no step history, no counters, no
        /// cached per-pair state, and PhysX ownership back where registration left it.
        /// </summary>
        /// <remarks>
        /// Per-pair state is the part that matters and the part easiest to forget. A stale
        /// <c>LastDrivenStep</c> makes the first step of the next run look already-driven; a latched
        /// <c>HandedOff</c> leaves PhysX owning a contact the next run has not established yet; and a
        /// retained <c>LastNormal</c> seeds the next run's first response with a direction from the
        /// previous one.
        /// </remarks>
        public void ResetRunState()
        {
            step = 0;
            LastStepMs = 0d;
            LastSolverMs = 0d;
            LastResponseMs = 0d;
            LastPairsDriven = 0;
            LastPairsRejected = 0;
            LastContacts = 0;
            AbandonedQueries = 0;

            foreach (Pair pair in pairs)
            {
                pair.LastDrivenStep = -1;
                pair.LastNormal = Vector3.zero;
                pair.Stats.Reset();
                pair.ResetCounters();

                if (pair.HandedOff)
                {
                    ApplySuppression(pair, true);
                    pair.HandedOff = false;
                }
            }

            trace.Clear();
        }
    }
}
