using System.Collections.Generic;
using BladeContact;
using UnityEngine;

/// <summary>
///     The L2 research-question layer: per-CONTACT tangential behaviour chosen from what the two touching
///     blade features actually ARE.
/// </summary>
/// <remarks>
///     <para>
///         Each fixed step, for every pair of registered blades PhysX reported touching, this runs one
///         closest-feature query against the authored shells, reads the two raw feature identities, maps
///         them to <see cref="BladeSemanticRegion"/> by authored designation only, classifies the contact
///         as a <see cref="BladeContactScenario"/>, resolves a <see cref="BladeTangentialPolicy"/>, and
///         adds an equal-and-opposite tangential force at the contact point. Nothing else is touched.
///     </para>
///     <para>
///         <b>What this is NOT.</b> It does not suppress, replace or correct PhysX contact. It does not
///         alter admissibility, time of impact, penetration or the contact normal. It does not write
///         collider <see cref="PhysicsMaterial"/> friction -- a collider material is one value for every
///         contact that collider is in, and a sword touches several things at once. It adds a term; PhysX
///         keeps its own.
///     </para>
///     <para>
///         <b>The parameter values are PROVISIONAL.</b> The two endpoint parameter sets in
///         <see cref="BladeTangentialPolicy"/> and the tangential compliance below are placeholders for
///         demonstrating the mechanism. They are NOT calibrated material friction, and no magnitude
///         measured through this layer is a physical claim about the specimen until they are declared and
///         calibrated.
///     </para>
/// </remarks>
/// <remarks>
///     <see cref="DefaultExecutionOrder" /> is declared, not left to chance. Anything that READS this
///     solver's published readout in its own <c>FixedUpdate</c> must run after it, or it reads the
///     previous step's values and every row it writes mixes two instants. That is not hypothetical: it
///     happened, silently, to every measurement this project recorded up to 2026-08-23.
/// </remarks>
/// <remarks>
///     <para>
///         <b>STATUS 2026-08-25 — read before extending this.</b> The ATTRIBUTION half of this component
///         is the Phase 1 result and is live: the authored-shell classification, the INITIAL APPROACH
///         latch (<c>Initial*</c> on <see cref="Snapshot" />) and <c>resolvePolicyFromLatchedScenario</c>.
///         Measured at EdgeNoBind, turning the latch on took the share of loaded steps governed by the
///         latched <c>EdgeEdge</c> verdict from 5.6 % to 100 %.
///     </para>
///     <para>
///         <b>The ACTUATOR half — the tangential penalty force — is RETIRED as the candidate solution.</b>
///         It is kept, running and unchanged, as the baseline every prior run was measured under and as an
///         ablation arm. Do not tune it expecting it to produce a bind. It cannot: Coulomb friction is
///         tangential and the property that has to be preserved is normal-direction non-crossing. Raising
///         <c>CuttingPair.StaticBindThreshold</c> was tried and produced jitter, not grip.
///     </para>
///     <para>
///         Phase 2 pursues a temporary relationship CONSTRAINT gated on the latch. See
///         <c>Workspace/Week7/Phase1/PHASE1_CLOSE_AND_PHASE2_OPEN.md</c> and
///         <c>Workspace/Week7/CHANNEL_COMPARISON_ForceMaterialJoint.md</c>.
///     </para>
/// </remarks>
[DisallowMultipleComponent]
[DefaultExecutionOrder(-100)]
public sealed class RqBladeTangentialSolver : MonoBehaviour
{
    /// <summary>Tangential state of one blade-vs-blade contact.</summary>
    public enum TangentialState
    {
        /// <summary>No PhysX contact between these two blades this step.</summary>
        Separated,

        /// <summary>Held: the tangential demand is inside the bind limit and the contact is not travelling.</summary>
        Bound,

        /// <summary>Broken away: the demand exceeded the bind limit and the contact is travelling.</summary>
        Slipping,

        /// <summary>The contact was live and then the normal load fell away.</summary>
        Released
    }

    // ---------------------------------------------------------------------------------------------
    // RETIRED 2026-08-25 — the PRESSURE-GATED BIND. Do not rebuild this.
    //
    // What was here: BindPressureSample (Undeclared / FirstLoad / PeakOverWindow / SettledAfterWindow),
    // BindGateState, bindPressureThresholdNewtons, bindPressureWindowSteps, and UpdateBindGate — a gate
    // that would latch a "bind" when a latched EdgeEdge attribution coincided with opposed pressure past
    // a declared threshold.
    //
    // Why it is gone, and why re-deriving it is a dead end:
    //   1. The pressure sample it needed was never declarable. First non-zero normal force measures
    //      7.99 N Flat / 6.88 N EdgeNoBind / 7.49 N EdgeBind — three presentations agreeing to about
    //      1 N, because it is ContinuousSpeculative pushing against a penetration that has not happened.
    //      It discriminates nothing, and it is the LARGEST force in the onset sequence.
    //   2. More fundamentally, the thing the gate would have switched on was a FRICTION response, and
    //      friction is retired as the candidate actuator — see PHASE1_CLOSE_AND_PHASE2_OPEN.md.
    //      Coulomb friction is tangential; the property that has to be preserved is normal-direction
    //      non-crossing. A gated friction coefficient cannot deliver it however it is triggered.
    //
    // What SURVIVES and is still live below: the attribution latch itself (Initial* on Snapshot) and
    // resolvePolicyFromLatchedScenario. Those are the Phase 1 result. Phase 2's gate, if it has one,
    // governs a CONSTRAINT and its release condition should be attribution validity, not a force
    // threshold — that is the same declared-number trap this block died of.
    //
    // Recoverable from git history: BladeContact d8c1c3f.
    // ---------------------------------------------------------------------------------------------
    /// <summary>Everything a readout needs about one contact, sampled after the last solve.</summary>
    public struct Snapshot
    {
        public bool Valid;
        public string LabelA;
        public string LabelB;

        // RAW layer -- reported unchanged, never reasoned backwards from.
        public BladeFeatureType RawTypeA;
        public BladeFeatureType RawTypeB;
        public string RawIdA;
        public string RawIdB;

        /// <summary>
        ///     The features this classification actually selected, as references into each side's
        ///     <see cref="BladeShellData" />.
        /// </summary>
        /// <remarks>
        ///     Published so a consumer can read back the ATTRIBUTED geometry itself -- the authored edge's
        ///     own two vertices, or the facet's own triangle -- rather than reconstructing it by parsing
        ///     <see cref="RawIdA" />. A diagnostic that draws the attribution has to draw what was selected;
        ///     if it instead draws a feature it chose by its own rule, it demonstrates nothing about what
        ///     the classifier did. Valid only while <see cref="ClassificationValid" /> is true.
        /// </remarks>
        public BladeFeatureRef FeatureA;

        public BladeFeatureRef FeatureB;

        // SEMANTIC layer -- derived from authored designation only.
        public BladeSemanticRegion RegionA;
        public BladeSemanticRegion RegionB;
        public BladeContactScenario Scenario;
        public bool ClassificationValid;

        /// <summary>
        ///     True while the published attribution was CARRIED FORWARD from an earlier step because a
        ///     later query found no usable witness.
        /// </summary>
        /// <remarks>
        ///     Reporting only — it does not gate the tangential term. It exists because
        ///     <see cref="ClassificationValid" /> stays true through a failed query, so without this a
        ///     stale attribution is indistinguishable from a fresh one unless a consumer thinks to compare
        ///     <see cref="ClassifyAttemptedAtStep" /> against <see cref="ClassWrittenAtStep" />. Cleared
        ///     the moment a query succeeds again.
        /// </remarks>
        public bool ClassificationStale;

        /// <summary>How many queries on this pair have found no usable witness, cumulative.</summary>
        public int FailedQueries;

        // Resolved policy for that scenario.
        public BladeTangentialParameters Parameters;
        public float EdgeNonCuttingBind;

        public TangentialState State;
        public float NormalForce;
        public float TangentialDemand;
        public float BindLimit;
        public float AppliedTangentialForce;
        public float BreakawayForce;
        public bool BreakawayRecorded;

        /// <summary>Tangential separation of the two anchored material points, metres.</summary>
        public float TangentialDisplacement;

        /// <summary>How far the contact has travelled ALONG each blade since it was first classified.</summary>
        public float DriftAlongA;
        public float DriftAlongB;

        public float TangentialSpeed;

        /// <summary>Most negative PhysX contact separation this step. Negative means overlap.</summary>
        public float SignedOverlap;

        /// <summary>
        ///     Separation between the two ATTRIBUTED features, as <see cref="BladeFeaturePair.Separation" />
        ///     reported it. Metres, unsigned.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Two limits. It exists only while a pair exists, and it is written only inside
        ///         <c>Classify</c>, so between classifications it is HELD — up to <c>classificationInterval</c>
        ///         steps old. It is adequate for a range and is not a per-step measurement.
        ///     </para>
        ///     <para>
        ///         <b>Not interchangeable with a consumer's own edge-gap measurement.</b> This is the
        ///         separation between whichever two features the classifier selected. A recorder that
        ///         measures the closest approach of the authored sharp edges directly is computing a
        ///         different quantity by a different route, and the two have been observed to disagree by
        ///         millimetres even when they name the same authored ring. Neither is derived from the
        ///         other; do not substitute one for the other, and do not treat agreement as expected.
        ///     </para>
        /// </remarks>
        public float ShellSeparation;

        /// <summary>The two PhysX colliders that actually produced the deepest contact this step.</summary>
        public string ContactColliderA;
        public string ContactColliderB;

        /// <summary>Their PhysicsMaterials, as PhysX will combine them. Reported, never written.</summary>
        public string ContactMaterialA;
        public string ContactMaterialB;

        public int ContactCount;
        /// <summary>
        ///     The step this pair's TANGENTIAL SOLVE last ran. This is a solve timestamp and says nothing
        ///     about how old the CLASSIFICATION is: the solve runs every step a contact is reported, while
        ///     the classifier runs on its own interval. Reading this as classification freshness is wrong,
        ///     and did mislead one measurement pass. Use <see cref="ClassifyAttemptedAtStep" /> and
        ///     <see cref="ClassWrittenAtStep" /> for that.
        /// </summary>
        public int LastSolvedStep;

        /// <summary>
        ///     The step the classifier was last INVOKED on this pair, whether or not it produced a usable
        ///     witness. Gated by <c>classificationInterval</c>, so consecutive steps normally share a value.
        /// </summary>
        public int ClassifyAttemptedAtStep;

        /// <summary>
        ///     The step a classification was last successfully WRITTEN for this pair, i.e. a usable witness
        ///     was found and the feature identities and scenario were replaced.
        /// </summary>
        /// <remarks>
        ///     When an attempt runs out of budget or yields no valid witness the pair KEEPS its previous
        ///     class and this value does not move, so <c>ClassifyAttemptedAtStep</c> being newer than this
        ///     is exactly the signature of a stale class being carried forward. The difference between this
        ///     and the reading step is the real age of the attribution, and it is the number a measurement
        ///     must quote rather than <see cref="LastSolvedStep" />.
        /// </remarks>
        public int ClassWrittenAtStep;

        /// <summary>Steps since this relationship was first seen in which PhysX reported it touching.</summary>
        public int TouchedSteps;

        /// <summary>Steps in which PhysX reported no contact between two blades that were engaged.</summary>
        /// <remarks>
        ///     A single-point contact on a cutting edge drops in and out of the narrow phase. Every step it
        ///     is missing, the measured normal load is zero and so is the bind limit that is proportional
        ///     to it. This counter is here so that is visible rather than inferred.
        /// </remarks>
        public int DropoutSteps;

        // ---- INITIAL APPROACH: the latched fact -----------------------------------------------------
        //
        // Everything above this line is the CONTACT EVOLUTION fact -- what the classifier says now.
        // Everything below is the INITIAL APPROACH fact -- what it said at this pair's first successful
        // classification, which is at first pair, before load is transmitted. The design's rule is that
        // the second may never overwrite the first, and the structural expression of that rule is that
        // Classify() writes the fields above on EVERY success and the fields below only under the latch
        // condition. There is no code path that writes an Initial* field from a later classification.

        /// <summary>True once this pair has latched an initial attribution.</summary>
        public bool InitialLatched;

        /// <summary>The scenario as first classified. Protected: a later classification cannot change it.</summary>
        public BladeContactScenario InitialScenario;

        public BladeFeatureType InitialRawTypeA;
        public BladeFeatureType InitialRawTypeB;
        public string InitialRawIdA;
        public string InitialRawIdB;

        /// <summary>The features the FIRST classification selected, for reading back the attributed geometry.</summary>
        public BladeFeatureRef InitialFeatureA;

        public BladeFeatureRef InitialFeatureB;

        /// <summary>Solver-clock step the latch was taken on. Same clock as <see cref="ClassWrittenAtStep" />.</summary>
        public int InitialLatchedAtStep;

        /// <summary>
        ///     Normal load at the instant of the latch. Expected to be ZERO: PhysX reports the pair while
        ///     the inflated hulls are within their combined contactOffset, ~20 mm here, and the sealed Q1
        ///     runs latched at 0 N on all three stations. A non-zero value means the latch was taken after
        ///     load arrived, which is a different observation and must not be read as the Q1 one.
        /// </summary>
        public float InitialNormalForce;

        /// <summary>Authored-shell separation of the two attributed features at the latch, metres.</summary>
        public float InitialShellSeparation;

        /// <summary>
        ///     How many times this pair has latched. 0 before the first latch, 1 for a pair that latched
        ///     once and held.
        /// </summary>
        /// <remarks>
        ///     A liveness figure, not a diagnostic. Agreement between two runs on the latched scenario
        ///     means nothing if neither pair ever latched, and this is the column that says which it was.
        ///     Greater than 1 means <c>relatchOnReEngagement</c> fired and the latched fact is NOT the
        ///     first-pair fact.
        /// </remarks>
        public int LatchGeneration;

        /// <summary>
        ///     True when the current classification names a different scenario than the latched one. Both
        ///     facts stay published; this only saves every consumer from comparing them itself.
        /// </summary>
        public bool ScenarioEvolved;

        // ---- Which fact actually drove behaviour this step -------------------------------------------

        /// <summary>
        ///     The scenario the tangential policy was resolved from on this step -- the latched one when
        ///     <c>resolvePolicyFromLatchedScenario</c> is on and a latch exists, otherwise the current one.
        /// </summary>
        /// <remarks>
        ///     Published per step rather than inferred from the toggle, because "which fact was in force"
        ///     is the question a reader of a recorded run has to be able to answer from the row.
        /// </remarks>
        public BladeContactScenario GoverningScenario;
    }

    private sealed class Pair
    {
        public RqBladeContact A;
        public RqBladeContact B;

        // Accumulated from the collision callbacks of the most recent completed step.
        public Vector3 PointSum;
        public Vector3 NormalSum;
        public Vector3 Impulse;
        public int ContactCount;
        public float SignedOverlap;
        public Collider DeepestA;
        public Collider DeepestB;
        public int ReportedStep = -1;

        // Stick anchors: one material point per body, in that body's own local space.
        public bool Anchored;
        public Vector3 AnchorLocalA;
        public Vector3 AnchorLocalB;

        public bool WitnessLatched;
        public Vector3 FirstWitnessLocalA;
        public Vector3 FirstWitnessLocalB;
        public Vector3 LatestWitnessLocalA;
        public Vector3 LatestWitnessLocalB;

        public TangentialState State = TangentialState.Separated;
        public int SlipCandidateSteps;
        public int EngagedSinceStep = -1;
        public float BreakawayForce;
        public bool BreakawayRecorded;
        public int NextClassifyStep;

        /// <summary>
        ///     Set the step this contact is Released, cleared by the re-latch that consumes it.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         <b>A Pair is never removed on separation.</b> Only <see cref="Forget" /> or a null body
        ///         removes one; separation merely sets <see cref="TangentialState.Released" /> and drops the
        ///         anchor. In a RECORDED run the recorder calls <c>Forget</c> at Begin and again when the
        ///         gate phase is commanded, so a latch cannot outlive the run that took it. In FREE PLAY
        ///         nothing calls <c>Forget</c>, so without this flag a latch taken on one engagement would
        ///         be published forever, across every later engagement of the same two blades.
        ///     </para>
        ///     <para>
        ///         Measured before relying on it: across the three sealed Q1 runs, <c>Released</c> occurs on
        ///         ZERO steps inside the run's own engagement. EdgeBind's 343 Released rows all belong to
        ///         the pair carried in from before the approach, which <c>ClearCarriedContact</c> destroys
        ///         at HOLD. So re-latching is inert on every sealed run and changes only free play.
        ///     </para>
        /// </remarks>
        public bool AwaitingRelatch;

        public readonly BladeShellScratch Scratch = new BladeShellScratch();
        public readonly HashSet<(EntityId, EntityId)> CountedCollisions = new HashSet<(EntityId, EntityId)>();

        public Snapshot Readout;
    }

    private readonly struct Key : System.IEquatable<Key>
    {
        private readonly EntityId low;
        private readonly EntityId high;

        public Key(RqBladeContact a, RqBladeContact b)
        {
            EntityId x = a.GetEntityId();
            EntityId y = b.GetEntityId();
            bool ordered = x.CompareTo(y) < 0;
            low = ordered ? x : y;
            high = ordered ? y : x;
        }

        public bool Equals(Key other) => low.Equals(other.low) && high.Equals(other.high);
        public override bool Equals(object obj) => obj is Key other && Equals(other);
        public override int GetHashCode() => System.HashCode.Combine(low, high);
    }

    [Header("Behaviour")]
    [Tooltip("Off leaves every blade contact to ordinary PhysX, so the same rig can be run both ways.")]
    [SerializeField] private bool applyTangentialBehaviour = true;

    [Tooltip("PROVISIONAL. Tangential response endpoints per semantic scenario. Placeholders for " +
             "demonstrating the mechanism, NOT calibrated material friction.")]
    [SerializeField] private BladeTangentialPolicy policy = BladeTangentialPolicy.Default;

    [Header("Tangential compliance (PROVISIONAL model parameters)")]
    [Tooltip("N/m. How hard a held contact is pulled back to where it stuck. Sets how far a bound " +
             "contact creeps before it reaches its bind limit. A model parameter, not a measurement.")]
    [SerializeField] private float bindStiffness = 20000f;

    [Tooltip("N per m/s. Damping on the held contact, so the bind does not ring.")]
    [SerializeField] private float bindDamping = 60f;

    [Tooltip("Consecutive fixed steps the slip condition must hold before the contact is called broken. " +
             "A crossed knife-edge contact carries one contact point and jitters, and a single-step " +
             "excursion above the bind limit is that jitter, not a breakaway. This is hysteresis on the " +
             "state machine, not a physical threshold.")]
    [Min(1)]
    [SerializeField] private int slipDwellSteps = 4;

    [Header("Query")]
    [Tooltip("Fixed steps between closest-feature classifications. A new pair is always classified on " +
             "its first step, so a strike is classified on impact; after that the classification is held " +
             "and re-queried at this interval. The tangential force is applied EVERY step regardless. " +
             "At 200 Hz one closest-feature query on this specimen costs a few milliseconds, which is the " +
             "whole fixed-step budget. This is the dial that keeps the demonstration watchable; it is not " +
             "a claim that the contact only changes this often.")]
    [Min(1)]
    [SerializeField] private int classificationInterval = 20;

    [SerializeField] private BladeSweepSettings sweepSettings = BladeSweepSettings.Default;

    [Tooltip("MODELLING DECISION, default OFF. When a closest-feature query finds no usable witness, " +
             "clear the attribution instead of carrying the previous one forward.\n\n" +
             "OFF (default): the pair keeps acting on the last relationship it identified. A contact does " +
             "not stop being edge-on-edge because a query failed, and this is the behaviour every run so " +
             "far was measured under.\n\n" +
             "ON: the attribution is cleared, which sets ClassificationValid false — and that gates BOTH " +
             "the resolved policy parameters AND whether the tangential term is applied at all. So this " +
             "does not merely change the readout: OurSolution switches off for that contact until a query " +
             "succeeds, and the contact reverts to ordinary PhysX. If failures are intermittent the layer " +
             "will flicker.\n\n" +
             "Either way the staleness is now reported: ClassificationStale marks a carried-forward class " +
             "and the first failure on each pair is logged.")]
    [SerializeField] private bool clearAttributionOnQueryFailure;

    [Header("v1 — latched INITIAL APPROACH, and the bind gate built on it")]
    [Tooltip("MODELLING DECISION, default OFF so every run recorded before this existed stays " +
             "reproducible. Same pattern as clearAttributionOnQueryFailure: the default is the OLD " +
             "behaviour and turning it on is a declaration.\n\n" +
             "OFF (default): the tangential policy is resolved from the CURRENT scenario, which " +
             "Classify() overwrites on every successful classification. So the evolved fact drives " +
             "behaviour, and an EdgeEdge engagement that the classifier later reads as EdgeFlat is " +
             "driven as EdgeFlat.\n\n" +
             "ON: the policy is resolved from the LATCHED scenario instead — what the pair was " +
             "classified as at first pair, before load was transmitted. The current scenario is still " +
             "computed, still published and still recorded; it just stops steering.\n\n" +
             "Either way BOTH facts are published every step, and governingScenario says which one was " +
             "in force.")]
    [SerializeField] private bool resolvePolicyFromLatchedScenario;

    [Tooltip("PROPOSED, UNRATIFIED. Re-latch the initial attribution on the first re-touch after a " +
             "contact was Released, so a latch describes the engagement it was taken in.\n\n" +
             "Why it is here: a Pair is never removed on separation. In a recorded run the recorder " +
             "calls Forget at Begin and at the gate phase, so a latch dies with the pair; in free play " +
             "nothing calls Forget, so without this a latch persists across separate engagements " +
             "forever.\n\n" +
             "Measured before defaulting it ON: across the three sealed Q1 runs, Released occurs on ZERO " +
             "steps inside the run's own engagement, so this changes nothing on any sealed run. It is " +
             "still a modelling decision and it is the researcher's to ratify — read latchGeneration in " +
             "the timeline to see whether it ever fired.")]
    [SerializeField] private bool relatchOnReEngagement = true;

    [Header("Diagnostics")]
    [Tooltip("Log the first attribution made for each blade pair, once per pair.\n\n" +
             "This fires when PhysX first reports a pair, which is while the INFLATED COOKED HULLS are " +
             "within their combined contactOffset — not when the blades meet. It has been observed at " +
             "24 mm of positive separation carrying zero normal force. The message says so; do not read " +
             "it, or quote it, as a contact event.")]
    [SerializeField] private bool logFirstClassification = true;

    [Tooltip("Pause the editor on the step that log line is emitted — the FIRST attribution for a pair, " +
             "which is the moment the inflated hulls come into range and NOT the moment the blades " +
             "meet.\n\n" +
             "OFF by default and it must stay off for any recorded run: pausing mid-run stops the " +
             "physics clock, so a run paused here is not a run.\n\n" +
             "It exists because the interesting frame is BEFORE the blades meet, and every attempt to " +
             "stop there on a wall-clock wait or on a first-contact trigger has arrived too late — by " +
             "the time PhysX reports a contact the pass-through has already happened. This stops on the " +
             "proximity event instead, which is upstream of both.\n\n" +
             "Editor only. In a build it does nothing.")]
    [SerializeField] private bool pauseOnFirstClassification;

    private readonly Dictionary<Key, Pair> pairs = new Dictionary<Key, Pair>();
    private readonly List<Key> scratchKeys = new List<Key>();
    private readonly List<ContactPoint> contactBuffer = new List<ContactPoint>(32);
    private readonly HashSet<Key> loggedPairs = new HashSet<Key>();

    /// <summary>Pairs already warned about a carried-forward attribution, so the warning fires once each.</summary>
    private readonly HashSet<Key> staleWarned = new HashSet<Key>();

    private int step;

    private BladeClassificationTrace liveTrace;

    /// <summary>
    /// Opt-in capture of what each fresh classification query actually computed. OFF by default and never
    /// serialized, so a measurement run cannot inherit it from a prefab.
    /// </summary>
    /// <remarks>
    /// The capture is append-only and is never read back by the solver, so it cannot change which feature
    /// pair is selected. It does spend time inside the query, which counts against
    /// <see cref="BladeSweepSettings.DiagnosticTimeBudgetMs" /> — that is why
    /// <see cref="BladeClassificationTrace.Completion" /> is recorded rather than assumed.
    /// </remarks>
    public bool TraceClassifications { get; set; }

    /// <summary>
    /// The most recent traced query. Reused between classifications, so a consumer must
    /// <see cref="BladeClassificationTrace.Clone" /> anything it intends to keep.
    /// </summary>
    public BladeClassificationTrace LastClassificationTrace { get { return liveTrace; } }

    /// <summary>
    /// Fixed step the trace in <see cref="LastClassificationTrace" /> belongs to, or -1. Watch this for
    /// change rather than polling the trace, so a consumer can never read the same capture twice or read a
    /// half-written one.
    /// </summary>
    public int LastClassificationTraceStep { get; private set; } = -1;

    /// <summary>Whether the semantic layer is currently adding anything at all.</summary>
    public bool ApplyTangentialBehaviour
    {
        get { return applyTangentialBehaviour; }
        set { applyTangentialBehaviour = value; }
    }

    /// <summary>The policy in force. Serialized, provisional, and restated in every readout.</summary>
    public BladeTangentialPolicy Policy { get { return policy; } }

    public float BindStiffness { get { return bindStiffness; } }
    public float BindDamping { get { return bindDamping; } }

    /// <summary>Fixed steps between closest-feature classifications.</summary>
    public int ClassificationInterval { get { return classificationInterval; } }

    /// <summary>Whether a failed query clears the attribution instead of carrying it forward.</summary>
    public bool ClearAttributionOnQueryFailure { get { return clearAttributionOnQueryFailure; } }

    /// <summary>Whether the tangential policy is resolved from the LATCHED scenario.</summary>
    public bool ResolvePolicyFromLatchedScenario { get { return resolvePolicyFromLatchedScenario; } }

    /// <summary>Whether a re-touch after Released takes a fresh initial-attribution latch.</summary>
    public bool RelatchOnReEngagement { get { return relatchOnReEngagement; } }

    /// <summary>Milliseconds spent in closest-feature queries during the last solved step.</summary>
    public double LastQueryMs { get; private set; }

    /// <summary>Blade-vs-blade contacts solved during the last step.</summary>
    public int LastSolvedPairs { get; private set; }

    /// <summary>
    ///     Every relationship the solver currently holds, most recently solved first.
    /// </summary>
    /// <remarks>
    ///     For free-play readouts, where nothing knows in advance which two blades will meet. Pairs that
    ///     have separated are still listed, with the state they ended in, until one of the blades leaves.
    /// </remarks>
    public void CollectSnapshots(List<Snapshot> into)
    {
        into.Clear();
        foreach (KeyValuePair<Key, Pair> entry in pairs)
            if (entry.Value.Readout.Valid) into.Add(entry.Value.Readout);

        into.Sort((x, y) => y.LastSolvedStep.CompareTo(x.LastSolvedStep));
    }

    /// <summary>Latest state of the contact between two specific blades, for a readout.</summary>
    public bool TryGetSnapshot(RqBladeContact a, RqBladeContact b, out Snapshot snapshot)
    {
        Pair pair;
        if (a != null && b != null && pairs.TryGetValue(new Key(a, b), out pair))
        {
            snapshot = pair.Readout;
            return snapshot.Valid;
        }

        snapshot = default(Snapshot);
        return false;
    }

    /// <summary>
    ///     Re-establishes where a contact is held from, and clears its breakaway record.
    /// </summary>
    /// <remarks>
    ///     Called when a rig latches its measurement zero. Without it the bind is anchored wherever the
    ///     blades first touched, which for a knife-edge contact is a metastable apex-on-apex pose that
    ///     beds in millimetres later. Anchoring at the measurement zero instead means the reported
    ///     displacement and the held-contact stretch start counting from the same instant.
    /// </remarks>
    public void ReAnchor(RqBladeContact a, RqBladeContact b)
    {
        Pair pair;
        if (a == null || b == null || !pairs.TryGetValue(new Key(a, b), out pair)) return;

        pair.Anchored = false;
        pair.WitnessLatched = false;
        pair.SlipCandidateSteps = 0;
        pair.Readout.TouchedSteps = 0;
        pair.Readout.DropoutSteps = 0;
        pair.BreakawayRecorded = false;
        pair.BreakawayForce = 0f;
        pair.Readout.DriftAlongA = 0f;
        pair.Readout.DriftAlongB = 0f;
    }

    /// <summary>Drops every relationship a blade is in, when it leaves the scene.</summary>
    public void Forget(RqBladeContact blade)
    {
        scratchKeys.Clear();
        foreach (KeyValuePair<Key, Pair> entry in pairs)
        {
            if (entry.Value.A == blade || entry.Value.B == blade) scratchKeys.Add(entry.Key);
        }

        for (int i = 0; i < scratchKeys.Count; i++) pairs.Remove(scratchKeys[i]);
    }

    /// <summary>
    ///     Records one PhysX collision. Only contacts where BOTH colliders are registered blades are kept;
    ///     everything else -- guard, grip, pommel, floor, props, unregistered swords -- is ignored here and
    ///     left to PhysX untouched.
    /// </summary>
    internal void ReportCollision(RqBladeContact reporter, Collision collision)
    {
        int count = collision.GetContacts(contactBuffer);
        if (count == 0) return;

        for (int i = 0; i < count; i++)
        {
            ContactPoint contact = contactBuffer[i];
            if (!reporter.Owns(contact.thisCollider)) continue;

            RqBladeContact other;
            if (!RqBladeContact.TryResolve(contact.otherCollider, out other)) continue;
            if (other == reporter) continue;

            Key key = new Key(reporter, other);
            Pair pair;
            if (!pairs.TryGetValue(key, out pair))
            {
                pair = new Pair { A = reporter, B = other };
                pairs.Add(key, pair);
            }

            // Both blades receive a callback for the same contact. Keep one side's view of it so the
            // contact count and the impulse are not counted twice.
            if (pair.A != reporter) continue;

            if (pair.ReportedStep != step)
            {
                pair.ReportedStep = step;
                pair.PointSum = Vector3.zero;
                pair.NormalSum = Vector3.zero;
                pair.ContactCount = 0;
                pair.SignedOverlap = float.MaxValue;
                pair.Impulse = Vector3.zero;
                pair.DeepestA = null;
                pair.DeepestB = null;
                pair.CountedCollisions.Clear();
            }

            // One blade can carry several shapes, so PhysX delivers a separate Collision per COLLIDER pair
            // in the same step, each with its own impulse. They are summed once per collider pair -- not
            // once per contact point inside it, and not keyed on the Collision object, which Unity reuses.
            var shapePair = (contact.thisCollider.GetEntityId(), contact.otherCollider.GetEntityId());
            if (pair.CountedCollisions.Add(shapePair)) pair.Impulse += collision.impulse;

            pair.PointSum += contact.point;
            pair.NormalSum += contact.normal;
            pair.ContactCount++;

            if (contact.separation < pair.SignedOverlap)
            {
                pair.SignedOverlap = contact.separation;
                pair.DeepestA = contact.thisCollider;
                pair.DeepestB = contact.otherCollider;
            }
        }
    }

    private void FixedUpdate()
    {
        step++;
        LastSolvedPairs = 0;

        if (pairs.Count == 0)
        {
            LastQueryMs = 0.0;
            return;
        }

        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        double queryMs = 0.0;

        scratchKeys.Clear();
        foreach (KeyValuePair<Key, Pair> entry in pairs) scratchKeys.Add(entry.Key);

        for (int i = 0; i < scratchKeys.Count; i++)
        {
            Key key = scratchKeys[i];
            Pair pair = pairs[key];

            if (pair.A == null || pair.B == null || pair.A.Body == null || pair.B.Body == null)
            {
                pairs.Remove(key);
                continue;
            }

            // The callbacks for step N arrive after step N is simulated, so they are consumed at the top
            // of step N+1 -- which is also the moment the poses they refer to are the current ones.
            bool touching = pair.ReportedStep == step - 1 && pair.ContactCount > 0;

            if (!touching)
            {
                if (pair.State == TangentialState.Bound || pair.State == TangentialState.Slipping)
                {
                    pair.State = TangentialState.Released;

                    // The engagement this pair's latch describes has ended. Arm a re-latch for the next
                    // re-touch, and let that re-touch classify on its own first step rather than waiting
                    // out the remainder of the classification interval -- a re-engagement is a new
                    // approach, and a latch taken up to classificationInterval steps into it is not a
                    // first-pair attribution. Consumed in Classify, and only when relatchOnReEngagement
                    // is on.
                    pair.AwaitingRelatch = true;
                    if (relatchOnReEngagement) pair.NextClassifyStep = 0;
                }

                pair.Anchored = false;
                pair.Readout.State = pair.State;
                pair.Readout.NormalForce = 0f;
                pair.Readout.AppliedTangentialForce = 0f;
                pair.Readout.TangentialDemand = 0f;
                pair.Readout.ContactCount = 0;

                // A dropout only counts while the pair was engaged. Once it has genuinely separated and
                // been released, absence is not a dropout.
                if (pair.EngagedSinceStep >= 0 && pair.State == TangentialState.Released)
                    pair.EngagedSinceStep = -1;
                else if (pair.EngagedSinceStep >= 0)
                    pair.Readout.DropoutSteps++;

                continue;
            }

            double before = clock.Elapsed.TotalMilliseconds;
            Solve(key, pair, clock);
            queryMs += clock.Elapsed.TotalMilliseconds - before;
            LastSolvedPairs++;
        }

        LastQueryMs = queryMs;
    }

    private void Solve(Key key, Pair pair, System.Diagnostics.Stopwatch clock)
    {
        float dt = Time.fixedDeltaTime;
        Vector3 point = pair.PointSum / pair.ContactCount;
        Vector3 normal = pair.NormalSum.sqrMagnitude > 1e-12f ? pair.NormalSum.normalized : Vector3.up;

        // PhysX reports the impulse it needed to resolve this pair; over the step that is the normal load
        // holding the two blades together. Magnitude only, so the callback's sign convention cannot leak in.
        //
        // Computed HERE rather than after the classification, which is where it used to sit. It depends on
        // nothing the classifier touches -- only the accumulated impulse, the contact normal and dt -- so
        // the move changes no value. It is needed earlier because the latch has to record the load at the
        // instant it was taken, and "zero force at the latch" is the whole Q1 claim: a latch that quietly
        // happened after load arrived is a different observation and has to be visible as one.
        float normalForce = Mathf.Abs(Vector3.Dot(pair.Impulse, normal)) / Mathf.Max(dt, 1e-6f);

        // Classify. The raw witness is reported unchanged; the semantic layer is derived from the
        // authored designation of those two features and from nothing else.
        if (step >= pair.NextClassifyStep)
        {
            pair.NextClassifyStep = step + classificationInterval;

            // Recorded whether or not the attempt yields a usable witness. An attempt that fails leaves
            // ClassWrittenAtStep behind, which is what makes a carried-forward class visible as stale.
            pair.Readout.ClassifyAttemptedAtStep = step;
            Classify(key, pair, clock, normalForce);
        }

        UpdateDrift(pair);

        // WHICH FACT STEERS. The latched scenario is the INITIAL APPROACH fact and the published one is
        // the CONTACT EVOLUTION fact. Both are always computed and always published; this chooses which
        // of them the policy is resolved from, and publishes that choice per step so a recorded row says
        // for itself which fact was in force rather than leaving it to be inferred from a toggle.
        //
        // The default is the CURRENT scenario, which is what every run before this was measured under.
        BladeContactScenario governing = resolvePolicyFromLatchedScenario && pair.Readout.InitialLatched
            ? pair.Readout.InitialScenario
            : pair.Readout.Scenario;

        pair.Readout.GoverningScenario = governing;

        // Recomputed every step rather than at classification, so it cannot go stale while an attribution
        // is being carried forward or has been cleared by a failed query. False whenever there is no
        // current verdict to compare against.
        pair.Readout.ScenarioEvolved = pair.Readout.InitialLatched &&
                                       pair.Readout.ClassificationValid &&
                                       pair.Readout.Scenario != pair.Readout.InitialScenario;

        // ClassificationValid still gates, unchanged. A latch does not keep the layer alive through a
        // cleared attribution: if clearAttributionOnQueryFailure is ON and a query fails, the tangential
        // term switches off for this contact whether or not something is latched. Whether a latched
        // attribution SHOULD survive that is a separate modelling question and is not decided here.
        BladeTangentialParameters prm = pair.Readout.ClassificationValid
            ? policy.Resolve(governing)
            : default(BladeTangentialParameters);

        Rigidbody bodyA = pair.A.Body;
        Rigidbody bodyB = pair.B.Body;

        if (!pair.Anchored)
        {
            pair.AnchorLocalA = bodyA.transform.InverseTransformPoint(point);
            pair.AnchorLocalB = bodyB.transform.InverseTransformPoint(point);
            pair.Anchored = true;
            pair.State = TangentialState.Bound;
            pair.BreakawayRecorded = false;
            pair.BreakawayForce = 0f;
        }

        Vector3 anchorA = bodyA.transform.TransformPoint(pair.AnchorLocalA);
        Vector3 anchorB = bodyB.transform.TransformPoint(pair.AnchorLocalB);

        Vector3 offset = anchorB - anchorA;
        Vector3 tangentialOffset = offset - normal * Vector3.Dot(offset, normal);

        Vector3 relative = bodyB.GetPointVelocity(point) - bodyA.GetPointVelocity(point);
        Vector3 tangentialVelocity = relative - normal * Vector3.Dot(relative, normal);

        // The force that would hold this contact exactly where it stuck.
        Vector3 demand = -(bindStiffness * tangentialOffset + bindDamping * tangentialVelocity);
        float bindLimit = prm.StaticBindThreshold * normalForce;
        float slideLimit = prm.DynamicFriction * normalForce;

        Vector3 force = Vector3.zero;

        if (pair.State == TangentialState.Bound)
        {
            bool overLimit = demand.magnitude > bindLimit &&
                             tangentialVelocity.magnitude > prm.ReleaseThreshold;

            pair.SlipCandidateSteps = overLimit ? pair.SlipCandidateSteps + 1 : 0;

            if (pair.SlipCandidateSteps >= slipDwellSteps)
            {
                pair.State = TangentialState.Slipping;
                pair.SlipCandidateSteps = 0;
                if (!pair.BreakawayRecorded)
                {
                    pair.BreakawayForce = bindLimit;
                    pair.BreakawayRecorded = true;
                }
            }
            else
            {
                force = demand.magnitude > bindLimit ? demand.normalized * bindLimit : demand;
            }
        }

        if (pair.State == TangentialState.Slipping)
        {
            if (tangentialVelocity.sqrMagnitude > 1e-12f)
                force = -tangentialVelocity.normalized * slideLimit;

            if (tangentialVelocity.magnitude < prm.ReleaseThreshold)
            {
                pair.AnchorLocalA = bodyA.transform.InverseTransformPoint(point);
                pair.AnchorLocalB = bodyB.transform.InverseTransformPoint(point);
                pair.State = TangentialState.Bound;
                pair.SlipCandidateSteps = 0;
            }
        }

        if (applyTangentialBehaviour && pair.Readout.ClassificationValid && normalForce > 0f)
        {
            bodyB.AddForceAtPosition(force, point, ForceMode.Force);
            bodyA.AddForceAtPosition(-force, point, ForceMode.Force);
        }
        else
        {
            force = Vector3.zero;
        }

        pair.Readout.Valid = true;
        pair.Readout.LabelA = pair.A.Label;
        pair.Readout.LabelB = pair.B.Label;
        pair.Readout.Parameters = prm;
        pair.Readout.EdgeNonCuttingBind = policy.EdgeNonCuttingBind;
        pair.Readout.State = pair.State;
        pair.Readout.NormalForce = normalForce;
        pair.Readout.TangentialDemand = demand.magnitude;
        pair.Readout.BindLimit = bindLimit;
        pair.Readout.AppliedTangentialForce = force.magnitude;
        pair.Readout.BreakawayForce = pair.BreakawayForce;
        pair.Readout.BreakawayRecorded = pair.BreakawayRecorded;
        pair.Readout.TangentialDisplacement = tangentialOffset.magnitude;
        pair.Readout.TangentialSpeed = tangentialVelocity.magnitude;
        pair.Readout.SignedOverlap = pair.SignedOverlap;
        pair.Readout.ContactCount = pair.ContactCount;
        pair.Readout.ContactColliderA = Describe(pair.DeepestA);
        pair.Readout.ContactColliderB = Describe(pair.DeepestB);
        pair.Readout.ContactMaterialA = DescribeMaterial(pair.DeepestA);
        pair.Readout.ContactMaterialB = DescribeMaterial(pair.DeepestB);
        pair.Readout.LastSolvedStep = step;
        pair.Readout.TouchedSteps++;
        if (pair.EngagedSinceStep < 0) pair.EngagedSinceStep = step;
    }

    /// <summary>Names a collider for the readout, so which SHAPE is in contact is never inferred.</summary>
    private static string Describe(Collider collider) =>
        collider == null ? "(none)" : collider.name;

    /// <summary>
    ///     Names a collider's PhysicsMaterial and its coefficients, for the readout only.
    /// </summary>
    /// <remarks>
    ///     Reported, never written. A null material means the collider is running PhysX's built-in
    ///     defaults, which is a real and undeclared value and should be visible as such.
    /// </remarks>
    private static string DescribeMaterial(Collider collider)
    {
        if (collider == null) return "(none)";

        PhysicsMaterial material = collider.sharedMaterial;
        if (material == null) return "no material (PhysX built-in 0.6/0.6)";

        return material.name + " s" + material.staticFriction.ToString("F2") +
               "/d" + material.dynamicFriction.ToString("F2") + " " + material.frictionCombine;
    }

    private void Classify(Key key, Pair pair, System.Diagnostics.Stopwatch clock, float normalForce)
    {
        BladePose poseA = pair.A.SimulatedPlacement;
        BladePose poseB = pair.B.SimulatedPlacement;

        BladeClassificationTrace trace = null;
        if (TraceClassifications)
        {
            if (liveTrace == null) liveTrace = new BladeClassificationTrace();
            liveTrace.Reset(BladeShellSweep.SpecificityTieBand, sweepSettings);
            liveTrace.LabelA = pair.A.Label;
            liveTrace.LabelB = pair.B.Label;
            liveTrace.Step = step;
            trace = liveTrace;
        }

        BladeFeaturePair witness;
        bool ok = BladeShellSweep.TryClosestFeaturePair(
            pair.A.Data, poseA, pair.B.Data, poseB,
            sweepSettings, pair.Scratch, null, clock, trace, out witness);

        if (!ok || !witness.FeatureA.IsValid || !witness.FeatureB.IsValid)
        {
            // A query that found no usable witness. The comment that used to sit here claimed the readout
            // was "published anyway, with ClassificationValid false, so an abandoned query is visible as
            // such". IT WAS NOT. This branch returns without touching ClassificationValid, so the pair
            // goes on publishing its previous attribution as though it were current, and every field a
            // consumer reads -- Scenario, RawIdA/B, RegionA/B -- is the old one while ClassificationValid
            // still reads true. Detecting it required noticing that ClassifyAttemptedAtStep had moved and
            // ClassWrittenAtStep had not, which no consumer did.
            //
            // Now marked explicitly. ClassificationStale says a carried-forward class is being published,
            // and the first failure on each pair is logged, so this can never again be silent.
            pair.Readout.ClassificationStale = true;
            pair.Readout.FailedQueries++;

            if (staleWarned.Add(key))
                Debug.LogWarning(
                    "[L2] " + pair.A.Label + " x " + pair.B.Label + ": closest-feature query found no " +
                    "usable witness at step " + step + ". The pair is CARRYING FORWARD its previous " +
                    "attribution (" + pair.Readout.Scenario + ", " + pair.Readout.RawIdA + " x " +
                    pair.Readout.RawIdB + ", written at step " + pair.Readout.ClassWrittenAtStep +
                    "). Rows from here are stale until a query succeeds — see ClassificationStale. " +
                    (clearAttributionOnQueryFailure
                        ? "clearAttributionOnQueryFailure is ON, so the attribution has been cleared and " +
                          "this contact falls back to ordinary PhysX until a query succeeds."
                        : "clearAttributionOnQueryFailure is OFF, so the last relationship keeps acting."),
                    this);

            // Whether to keep acting on the last known relationship or fall back to ordinary PhysX is a
            // MODELLING decision, not a reporting one: ClassificationValid gates both the resolved policy
            // parameters and whether the tangential term is applied at all. Clearing it means OurSolution
            // switches off for this contact until a query succeeds. Default is to hold, which is the
            // behaviour every run so far was measured under.
            if (clearAttributionOnQueryFailure)
            {
                pair.Readout.ClassificationValid = false;
                pair.Readout.Scenario = default(BladeContactScenario);
                pair.Readout.RawIdA = pair.Readout.RawIdB = "(query failed)";
                pair.Readout.FeatureA = pair.Readout.FeatureB = default(BladeFeatureRef);
            }

            if (trace != null) LastClassificationTraceStep = step;
            return;
        }

        BladeFeatureType typeA = pair.A.Data.TypeOf(witness.FeatureA);
        BladeFeatureType typeB = pair.B.Data.TypeOf(witness.FeatureB);

        pair.Readout.RawTypeA = typeA;
        pair.Readout.RawTypeB = typeB;
        pair.Readout.RawIdA = pair.A.Data.IdOf(witness.FeatureA);
        pair.Readout.RawIdB = pair.B.Data.IdOf(witness.FeatureB);
        pair.Readout.FeatureA = witness.FeatureA;
        pair.Readout.FeatureB = witness.FeatureB;
        pair.Readout.RegionA = BladeContactScenarios.RegionOf(typeA);
        pair.Readout.RegionB = BladeContactScenarios.RegionOf(typeB);
        pair.Readout.Scenario = BladeContactScenarios.Classify(typeA, typeB);
        pair.Readout.ClassificationValid = true;
        pair.Readout.ClassificationStale = false;

        if (trace != null)
        {
            // Stamped from the readout that was just written, so the trace's verdict and the solver's are
            // the same value and cannot drift apart in analysis.
            trace.Scenario = pair.Readout.Scenario;
            trace.ClassificationValid = true;
            LastClassificationTraceStep = step;
        }

        // Only reached when a usable witness replaced the feature identities above. An attempt that
        // returned early left this behind, so (readingStep - ClassWrittenAtStep) is the attribution's
        // true age.
        pair.Readout.ClassWrittenAtStep = step;
        pair.Readout.ShellSeparation = witness.Separation;

        LatchInitialAttribution(pair, normalForce);

        // Against the transform the shell query was posed with, which is the body actually carrying the
        // blade. Using the authored transform here would express the witness in a frame the contact is
        // not in whenever an attachment system is hosting the sword's physics.
        pair.LatestWitnessLocalA = pair.A.ActiveTransform.InverseTransformPoint(witness.WitnessA);
        pair.LatestWitnessLocalB = pair.B.ActiveTransform.InverseTransformPoint(witness.WitnessB);

        if (!pair.WitnessLatched)
        {
            pair.FirstWitnessLocalA = pair.LatestWitnessLocalA;
            pair.FirstWitnessLocalB = pair.LatestWitnessLocalB;
            pair.WitnessLatched = true;

            if (logFirstClassification && loggedPairs.Add(key))
            {
                // NOT "first contact". This fires when PhysX first reports a pair for these two blades,
                // which happens while the INFLATED COOKED HULLS are within their combined contactOffset --
                // under ContinuousSpeculative, extended further by closing speed. The blades have not met:
                // observed at 24.0 mm of positive separation carrying zero normal force. Calling it contact
                // put a claim about the specimen into the console that the apparatus had not made, and it
                // was read as one.
                //
                // What it does mean is that the shells are near enough to be worth attributing, so the
                // attribution below is the first answer OurSolution has for this pair -- computed on the
                // authored shells at the simulating body's placement, with no inflation of any kind.
                Debug.Log(
                    "[L2] proximity pair (inflated PhysX hulls in range; NOT contact) " +
                    pair.A.Label + " x " + pair.B.Label + ": first attribution " +
                    BladeContactScenarios.Describe(typeA, pair.Readout.RawIdA, typeB, pair.Readout.RawIdB) +
                    "  shellSeparation " + (pair.Readout.ShellSeparation * 1000f).ToString("F3") + " mm" +
                    "  policy " + policy.Describe(pair.Readout.Scenario));
            }

            // Stops the editor on the proximity event, which is upstream of contact. Deliberately
            // outside the logFirstClassification gate so the pause does not depend on logging being on.
#if UNITY_EDITOR
            if (pauseOnFirstClassification)
            {
                UnityEditor.EditorApplication.isPaused = true;
                Debug.LogWarning(
                    "[L2] PAUSED on the first attribution for " + pair.A.Label + " x " + pair.B.Label +
                    " at shellSeparation " + (pair.Readout.ShellSeparation * 1000f).ToString("F3") +
                    " mm. This is the PROXIMITY event — the inflated hulls are in range and the blades " +
                    "have NOT met. The physics clock is now stopped, so this run is no longer a run.");
            }
#endif
        }
    }

    /// <summary>
    ///     Takes, or protects, this pair's INITIAL APPROACH attribution.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>This is the two-facts rule, expressed structurally.</b> <c>Classify</c> overwrites the
    ///         published scenario, raw ids, feature refs and regions on every successful classification —
    ///         that is the CONTACT EVOLUTION fact and it is supposed to move. This method is the only
    ///         writer of the <c>Initial*</c> fields, and it writes them only when nothing is latched, or
    ///         when a re-latch has been armed by a Released contact and re-latching is enabled. There is
    ///         no path by which a later classification can overwrite an earlier latch.
    ///     </para>
    ///     <para>
    ///         <b>Why latching at the first classification is latching at first pair.</b> A new pair's
    ///         <c>NextClassifyStep</c> is 0, so the first step PhysX reports it touching is always
    ///         classified — the interval only applies afterwards. PhysX reports the pair while the
    ///         inflated cooked hulls are within their combined <c>contactOffset</c>, about 20 mm here and
    ///         extended further by closing speed under <c>ContinuousSpeculative</c>, so the first
    ///         classification is made before load is transmitted. On the sealed Q1 runs it was made at
    ///         zero normal force at all three stations, with the blades 21.5 to 23.7 mm apart.
    ///     </para>
    ///     <para>
    ///         <b>What is deliberately NOT latched:</b> the witness point. <c>WitnessLatched</c> and
    ///         <c>FirstWitnessLocalA/B</c> already latch a POINT and are the origin for the drift measure;
    ///         they are a different quantity with a different lifetime and are left alone. Likewise
    ///         <see cref="ReAnchor" /> does not clear this latch: re-anchoring moves a measurement zero,
    ///         it does not begin a new engagement, and the initial attribution is a fact about the
    ///         engagement.
    ///     </para>
    /// </remarks>
    private void LatchInitialAttribution(Pair pair, float normalForce)
    {
        bool relatch = relatchOnReEngagement && pair.AwaitingRelatch;

        // Cleared unconditionally, including when re-latching is OFF. Left standing, a flag armed by a
        // release months of steps ago would fire the moment the toggle was flipped, and the latch would
        // silently move.
        pair.AwaitingRelatch = false;

        // PROTECTED. The evolved fact is published by the caller; it does not land here.
        if (pair.Readout.InitialLatched && !relatch) return;

        pair.Readout.InitialLatched = true;
        pair.Readout.InitialScenario = pair.Readout.Scenario;
        pair.Readout.InitialRawTypeA = pair.Readout.RawTypeA;
        pair.Readout.InitialRawTypeB = pair.Readout.RawTypeB;
        pair.Readout.InitialRawIdA = pair.Readout.RawIdA;
        pair.Readout.InitialRawIdB = pair.Readout.RawIdB;
        pair.Readout.InitialFeatureA = pair.Readout.FeatureA;
        pair.Readout.InitialFeatureB = pair.Readout.FeatureB;
        pair.Readout.InitialLatchedAtStep = step;
        pair.Readout.InitialNormalForce = normalForce;
        pair.Readout.InitialShellSeparation = pair.Readout.ShellSeparation;
        pair.Readout.LatchGeneration++;

        // Generation 1 is already announced by the first-attribution message in Classify. A LATER
        // generation is the surprising event -- it means the latched fact is no longer the first-pair
        // fact -- so it says so rather than being left to be noticed in a column.
        if (pair.Readout.LatchGeneration > 1)
            Debug.Log(
                "[L2] RE-LATCHED initial attribution " + pair.A.Label + " x " + pair.B.Label +
                " at step " + step + " (generation " + pair.Readout.LatchGeneration + "): " +
                pair.Readout.InitialScenario + ", " + pair.Readout.InitialRawIdA + " x " +
                pair.Readout.InitialRawIdB + ", " + pair.Readout.InitialNormalForce.ToString("F3") +
                " N. The contact was Released and re-engaged, so this pair's INITIAL APPROACH fact is " +
                "the re-engagement's, NOT the first-pair one. Set relatchOnReEngagement false to hold " +
                "the first latch instead.", this);
    }


    /// <summary>
    ///     How far the witness has travelled ALONG each blade since the contact was first classified.
    /// </summary>
    /// <remarks>
    ///     This is the blade-local statement of the bind. A world-space displacement cannot separate a
    ///     contact that is held on both blades while the blades themselves move from one that is sliding
    ///     down them.
    /// </remarks>
    private static void UpdateDrift(Pair pair)
    {
        if (!pair.WitnessLatched) return;

        pair.Readout.DriftAlongA = Vector3.Dot(
            pair.LatestWitnessLocalA - pair.FirstWitnessLocalA, pair.A.LocalLongAxis);
        pair.Readout.DriftAlongB = Vector3.Dot(
            pair.LatestWitnessLocalB - pair.FirstWitnessLocalB, pair.B.LocalLongAxis);
    }
}
