using System.Collections.Generic;
using System.Text;
using BladeContact;
using FIMSpace.FProceduralAnimation;
using UnityEngine;

/// <summary>
///     Minimum-spec debug view of the PhysX proximity envelope: when a contact PAIR first appears, at
///     what separation, and when the surfaces actually meet.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why it works on every station.</b> It reads nothing but PhysX's own collision reports —
///         <c>ContactPoint.separation</c> off the shapes that are actually simulating. There is no
///         classifier, no authored shell, no profile and no treatment in the path, so a Standard Unity
///         station reports exactly as an Augmented exploratory one does. The solver's
///         <c>pauseOnFirstClassification</c> cannot do this: it fires from the attribution, which
///         requires an <c>RqBladeContact</c>, and those exist on the three Augmented stations only.
///     </para>
///     <para>
///         <b>The thing it exists to show.</b> A contact PAIR is not a touch. PhysX generates contact
///         points inside the combined contact offset, so a pair is reported with POSITIVE separation
///         while the blades are still apart — about 20 mm at the project's 0.01 m per collider, and
///         further under <c>ContinuousSpeculative</c> by closing speed. The interval between the first
///         pair and the first overlap IS that envelope, and this measures it directly, in steps and in
///         millimetres, per station.
///     </para>
///     <para>
///         <b>DEBUG, not a measurement instrument.</b> It keeps its own counts rather than reading
///         <see cref="RqStationContactRecorder" />, because that recorder does nothing until a zero is
///         latched and a manual walk never latches one. Two counters of the same thing can drift, so
///         nothing here is sealed into a run and nothing downstream reads it. Where the two disagree,
///         the recorder is the record.
///     </para>
///     <para>
///         <b>It shares the relay rather than adding a second one.</b> <see cref="BladeCollisionRelay" />
///         raises a multicast event, so this subscribes alongside whatever else is listening and adds no
///         callback load beyond its own handler.
///     </para>
///     <para>
///         <b>READ-ONLY.</b> No force, no material, no collider state, no transform. It can pause the
///         editor, and that is the only thing it does to the world.
///     </para>
/// </remarks>
[DefaultExecutionOrder(300)]
[DisallowMultipleComponent]
public sealed class RqProximityDebug : MonoBehaviour
{
    [Header("The pair — left empty, both resolve from the station root by the frozen naming")]
    [SerializeField] private RA2AttachableObject swordA;

    [SerializeField] private RA2AttachableObject swordB;

    [Tooltip("Shape names that ARE the blade. A contact involving anything else — a crossguard, a grip " +
             "— is ignored, because the envelope being measured is the blade pair's.")]
    [SerializeField]
    private List<string> bladeShapeNames = new List<string> { "Blade", "QuickEdgeHull", "QuickFlatHull" };

    [Header("What to report")]
    [SerializeField] private bool logFirstPair = true;

    [SerializeField] private bool logFirstOverlap = true;

    [Tooltip("Log every step while a pair exists. Verbose — one line per fixed step. Off unless the " +
             "approach itself is being read step by step.")]
    [SerializeField] private bool logEveryStep;

    [Header("Pausing — editor only, OFF by default")]
    [Tooltip("Pause on the first step a contact PAIR is reported. This is the frame BEFORE the blades " +
             "meet, which is the one that has been hard to reach: a wall-clock wait overshoots and a " +
             "first-contact trigger is too late by construction, because by the time PhysX reports an " +
             "overlap the pass-through has already happened.\n\n" +
             "Pausing stops the physics clock, so a run paused here is not a run. Never leave it on for " +
             "a recorded run.")]
    [SerializeField] private bool pauseOnFirstPair;

    [Tooltip("Pause on the first step the closest reported separation reaches zero — the moment the " +
             "surfaces meet. Later than pauseOnFirstPair by the width of the envelope.")]
    [SerializeField] private bool pauseOnFirstOverlap;

    // ----- state -----------------------------------------------------------------------------------

    private readonly List<ContactPoint> buffer = new List<ContactPoint>(32);
    private readonly List<Collider> liveA = new List<Collider>();
    private readonly List<Collider> liveB = new List<Collider>();

    private GameObject boundA, boundB;
    private BladeCollisionRelay relayA;

    private int step;
    private int reportedStep = -1;
    private int contactsThisStep;
    private float closestThisStep;
    private string shapesThisStep = "";

    private bool sawPair, sawOverlap;
    private int firstPairStep = -1, firstOverlapStep = -1;
    private float firstPairSeparationMm;

    /// <summary>Fixed steps between the first contact pair and the first overlap. −1 until both seen.</summary>
    /// <remarks>This is the speculative envelope, measured rather than derived from the offset setting.</remarks>
    public int EnvelopeSteps => sawPair && sawOverlap ? firstOverlapStep - firstPairStep : -1;

    /// <summary>Separation on the step the pair first appeared, mm. Positive means the blades were apart.</summary>
    public float FirstPairSeparationMm => firstPairSeparationMm;

    /// <summary>True once a contact pair has been reported at all.</summary>
    public bool SawPair => sawPair;

    /// <summary>True once a separation at or below zero has been reported.</summary>
    public bool SawOverlap => sawOverlap;

    private void LateUpdate()
    {
        Rebind();
    }

    /// <summary>
    ///     Follows the blades onto whatever shapes are carrying them, and subscribes to A's relay.
    /// </summary>
    /// <remarks>
    ///     Only A's relay is subscribed. Both bodies report the same collision, and taking both would
    ///     double every count.
    /// </remarks>
    private void Rebind()
    {
        Resolve();
        if (swordA == null || swordB == null) return;

        GameObject genA = swordA.GeneratedPhysicsObject;
        GameObject genB = swordB.GeneratedPhysicsObject;
        if (genA == boundA && genB == boundB && liveA.Count > 0) return;

        if (relayA != null) relayA.Collided -= OnCollided;
        relayA = null;
        boundA = genA;
        boundB = genB;
        liveA.Clear();
        liveB.Clear();

        if (!CollectLive(swordA, liveA) || !CollectLive(swordB, liveB)) return;

        Rigidbody body = swordA.lastRigidbody != null
            ? swordA.lastRigidbody
            : genA != null ? genA.GetComponent<Rigidbody>() : null;
        if (body == null) return;

        relayA = body.GetComponent<BladeCollisionRelay>();
        if (relayA == null) relayA = body.gameObject.AddComponent<BladeCollisionRelay>();
        relayA.Collided += OnCollided;
    }

    private void Resolve()
    {
        if (swordA != null && swordB != null) return;

        Transform root = transform;
        while (root != null && !root.name.StartsWith("Station_")) root = root.parent;
        if (root == null) return;

        foreach (var att in root.GetComponentsInChildren<RA2AttachableObject>(true))
        {
            if (swordA == null && att.gameObject.name.StartsWith("Sword_A")) swordA = att;
            else if (swordB == null && att.gameObject.name.StartsWith("Sword_B")) swordB = att;
        }
    }

    private bool CollectLive(RA2AttachableObject att, List<Collider> into)
    {
        List<Collider> source = att.GeneratedPhysicsObject != null
            ? att.GeneratedPhysicsColliders
            : att.AttachableColliders;
        if (source == null) return false;

        for (int i = 0; i < source.Count; i++)
        {
            Collider c = source[i];
            if (c == null || !c.enabled) continue;

            string bare = c.name;
            int colon = bare.IndexOf(':');
            if (colon > 0) bare = bare.Substring(0, colon);
            if (!bladeShapeNames.Contains(bare)) continue;

            into.Add(c);
        }

        return into.Count > 0;
    }

    private bool Owns(List<Collider> list, Collider c)
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i] == c) return true;

        return false;
    }

    private void OnCollided(Collision collision)
    {
        int count = collision.GetContacts(buffer);
        for (int i = 0; i < count; i++)
        {
            ContactPoint cp = buffer[i];
            bool ab = Owns(liveA, cp.thisCollider) && Owns(liveB, cp.otherCollider);
            bool ba = Owns(liveB, cp.thisCollider) && Owns(liveA, cp.otherCollider);
            if (!ab && !ba) continue;

            if (reportedStep != step)
            {
                reportedStep = step;
                contactsThisStep = 0;
                closestThisStep = float.MaxValue;
                shapesThisStep = "";
            }

            contactsThisStep++;
            if (cp.separation < closestThisStep)
            {
                closestThisStep = cp.separation;
                shapesThisStep = Bare(cp.thisCollider.name) + " x " + Bare(cp.otherCollider.name);
            }
        }
    }

    private static string Bare(string n)
    {
        int colon = n.IndexOf(':');
        return colon > 0 ? n.Substring(0, colon) : n;
    }

    private void FixedUpdate()
    {
        step++;

        // Callbacks for step N arrive after step N is simulated, so they are consumed at the top of N+1.
        bool pair = reportedStep == step - 1 && contactsThisStep > 0;
        if (!pair) return;

        float sepMm = closestThisStep * 1000f;
        bool overlap = closestThisStep <= 0f;

        if (logEveryStep)
            Debug.Log("[Proximity] " + Label() + " step " + step + "  contacts " + contactsThisStep +
                      "  separation " + sepMm.ToString("F3") + " mm  " +
                      (overlap ? "OVERLAP" : "APART") + "  " + shapesThisStep, this);

        if (!sawPair)
        {
            sawPair = true;
            firstPairStep = step;
            firstPairSeparationMm = sepMm;

            if (logFirstPair)
                Debug.Log("[Proximity] " + Label() + ": FIRST CONTACT PAIR at step " + step +
                          "  separation " + sepMm.ToString("F3") + " mm  contacts " + contactsThisStep +
                          "  " + shapesThisStep + "\n" +
                          (sepMm > 0f
                              ? "    Separation is POSITIVE: PhysX has a pair because the shapes are " +
                                "inside their combined contact offset. The blades have NOT met."
                              : "    Separation is already at or below zero on the first reported step, " +
                                "so the pair and the meeting are the same step here."), this);

#if UNITY_EDITOR
            if (pauseOnFirstPair)
            {
                UnityEditor.EditorApplication.isPaused = true;
                Debug.LogWarning("[Proximity] " + Label() + ": PAUSED on the first contact pair at " +
                                 sepMm.ToString("F3") + " mm. The physics clock is stopped, so this run " +
                                 "is no longer a run.", this);
            }
#endif
        }

        if (overlap && !sawOverlap)
        {
            sawOverlap = true;
            firstOverlapStep = step;

            if (logFirstOverlap)
                Debug.Log("[Proximity] " + Label() + ": FIRST OVERLAP at step " + step +
                          "  separation " + sepMm.ToString("F3") + " mm  " + shapesThisStep + "\n" +
                          "    Envelope: " + EnvelopeSteps + " step(s) after the first pair, which " +
                          "appeared at " + firstPairSeparationMm.ToString("F3") + " mm. That interval is " +
                          "the speculative envelope, measured rather than taken from the offset setting.",
                          this);

#if UNITY_EDITOR
            if (pauseOnFirstOverlap)
            {
                UnityEditor.EditorApplication.isPaused = true;
                Debug.LogWarning("[Proximity] " + Label() + ": PAUSED on the first overlap.", this);
            }
#endif
        }
    }

    private void OnDisable()
    {
        if (relayA != null) relayA.Collided -= OnCollided;
        relayA = null;
    }

    /// <summary>Resets the first-pair and first-overlap latches so the next approach reports afresh.</summary>
    [ContextMenu("Reset latches")]
    public void ResetLatches()
    {
        sawPair = false;
        sawOverlap = false;
        firstPairStep = -1;
        firstOverlapStep = -1;
        firstPairSeparationMm = 0f;
    }

    /// <summary>One line for a readout or a log.</summary>
    public string ShortLine()
    {
        var sb = new StringBuilder(96);
        sb.Append("Proximity ");
        if (!sawPair) return sb.Append("no pair yet").ToString();

        sb.Append("pair@").Append(firstPairStep).Append(" ")
          .Append(firstPairSeparationMm.ToString("F2")).Append(" mm");
        if (sawOverlap) sb.Append("   overlap@").Append(firstOverlapStep)
                          .Append("   envelope ").Append(EnvelopeSteps).Append(" steps");
        else sb.Append("   no overlap yet");
        return sb.ToString();
    }

    private string Label()
    {
        Transform root = transform;
        while (root != null && !root.name.StartsWith("Station_")) root = root.parent;
        return root != null ? root.name : name;
    }
}
