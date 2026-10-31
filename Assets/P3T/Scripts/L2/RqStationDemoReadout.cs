using System.Text;
using BladeContact;
using TMPro;
using UnityEngine;

/// <summary>
///     The in-headset label for one station: what it is, and what its contact is doing right now.
/// </summary>
/// <remarks>
///     <para>
///         Written for the supervisor demo. It answers, standing in front of any of the nine stations and
///         without a console: which treatment is this, is the custom tangential layer actually doing
///         anything here, are the blades in contact, and what did the layer decide the contact is.
///     </para>
///     <para>
///         <b>It appends to the station's existing floating label rather than adding a second panel.</b>
///         That label is already placed, already sized for VR and already says which pass and treatment the
///         station is; a second surface beside it would just be another thing to position and another
///         canvas to draw.
///     </para>
///     <para>
///         <b>It reports and never decides.</b> Nothing here configures a station, and it holds no
///         expected outcome. The treatment name is read from the recorder, the layer's state from the
///         solver, and the classification from the solver's own record. A station that is running ordinary
///         PhysX says so.
///     </para>
///     <para>
///         Kept to four or five short lines. The label it writes into is a single-line tooltip that grows
///         to fit its content, and a long readout turns a station sign into a wall of text read from three
///         metres away.
///     </para>
///     <para>
///         Refreshed a few times a second, not every frame. This ships in a standalone Quest build, where
///         a per-frame string rebuild on nine stations is a cost with no benefit at reading speed.
///     </para>
/// </remarks>
[DisallowMultipleComponent]
public sealed class RqStationDemoReadout : MonoBehaviour
{
    [Tooltip("The station's existing floating label. Its authored first line is kept and the live status " +
             "is appended below it.")]
    [SerializeField] private TMP_Text target;

    [Tooltip("Reads contact state. Measurement-only; this readout adds nothing to it.")]
    [SerializeField] private RqStationContactRecorder recorder;

    [Tooltip("The tangential layer, so the readout can say whether it is switched on. Left empty on a " +
             "station the layer does not reach, which is itself the correct thing to display.")]
    [SerializeField] private RqBladeTangentialSolver solver;

    [SerializeField] private RqBladeContact bladeA;
    [SerializeField] private RqBladeContact bladeB;

    [Tooltip("Seconds between refreshes.")]
    [Min(0.05f)]
    [SerializeField] private float refreshSeconds = 0.25f;

    [Header("Contact side — Ae± / Af±")]
    [Tooltip("Which term this station's presentation is described by. DECLARED per station, not inferred:\n\n" +
             "Edge — edge-on-edge. Reports which EDGE is engaged, by quillon colour.\n" +
             "Face — flat-on-flat. Reports which broad FACE is presented.\n\n" +
             "Both are always computed; this picks which one the label leads with, and the other is shown " +
             "in brackets with its |dot| so a weakly-determined reading is visible rather than hidden.")]
    [SerializeField] private ContactTerm term = ContactTerm.Edge;

    [Tooltip("Marker-derived contact side. PREFERRED SOURCE when assigned, on EVERY station.\n\n" +
             "Assigned, the Ae±/Af± line is computed from the sword's own quillon and tip markers and its " +
             "collision mesh, with NO contact model in the path. That is what lets all ten stations carry " +
             "the same line: without it the three OurSolution stations are labelled by the representation " +
             "under evaluation and the other seven are not labelled at all.\n\n" +
             "Left empty, the line falls back to the package computation, which only produces anything " +
             "where bladeA and bladeB are set. The two sources were measured to agree — see " +
             "Workspace/Week8/VALIDATION_ContactSideProbe.md §3.")]
    [SerializeField] private RqContactSideProbe contactSideProbe;

    [Tooltip("The DECLARED valid, non-crossed state for this station, e.g. \"Ae+ / Be+\" or \"Af- / Bf-\".\n\n" +
             "As declared 2026-08-23 — EdgeBind and EdgeNoBind are Ae+ / Be+, Flat is Af- / Bf-. Leave " +
             "empty and the line is reported without a verdict; nothing is inferred, because which state " +
             "is correct is a presentation fact and not this component's to invent.")]
    [SerializeField] private string declaredReference = "";

    public enum ContactTerm
    {
        Edge,
        Face
    }

    private string header = "";
    private float nextRefresh;
    private readonly StringBuilder text = new StringBuilder(512);

    /// <summary>Captures the authored label so the live lines can be rebuilt under it every refresh.</summary>
    private void Awake()
    {
        if (target == null) return;

        // The authored label is whatever the first line already says; everything after it is ours.
        string existing = target.text ?? "";
        int cut = existing.IndexOf('\n');
        header = cut >= 0 ? existing.Substring(0, cut) : existing;
    }

    /// <summary>Declares what this readout watches, so the wiring is serialized rather than discovered.</summary>
    public void Configure(
        TMP_Text label, RqStationContactRecorder stationRecorder,
        RqBladeTangentialSolver tangentialSolver, RqBladeContact a, RqBladeContact b)
    {
        target = label;
        recorder = stationRecorder;
        solver = tangentialSolver;
        bladeA = a;
        bladeB = b;
    }

    /// <summary>
    ///     Declares the marker-derived source, which term leads, and this station's valid state. All
    ///     three are serialized so the label's claim is inspectable rather than discovered at runtime.
    /// </summary>
    public void ConfigureContactSide(RqContactSideProbe probe, ContactTerm which, string declared)
    {
        contactSideProbe = probe;
        term = which;
        declaredReference = declared;
    }

    private void Update()
    {
        if (target == null || Time.unscaledTime < nextRefresh) return;
        nextRefresh = Time.unscaledTime + refreshSeconds;

        text.Length = 0;
        text.Append("<b>").Append(header).Append("</b>").Append('\n').Append("<size=80%>");

        bool layerReaches = solver != null && bladeA != null && bladeB != null;

        if (!layerReaches) text.Append("ordinary PhysX contact — no custom layer here");
        else if (!solver.ApplyTangentialBehaviour) text.Append("custom tangential layer <b>SWITCHED OFF</b>");
        else text.Append("custom tangential layer <b>ACTIVE</b>");

        if (recorder != null)
        {
            RqStationContactRecorder.Record record = recorder.Current;
            // PAIR and OVERLAP are different states and the readout says both. A pair exists inside the
            // contact offset while the blades are still apart, so "contact YES" on its own was a claim
            // the apparatus had not made.
            text.Append('\n').Append("pair ").Append(record.PairPresent ? "YES" : "no")
                .Append("   overlap ").Append(record.Overlapping ? "YES" : "no");
            if (record.PairPresent)
                text.Append(" · normal ").Append(record.NormalForce.ToString("F0")).Append(" N");
        }

        if (layerReaches)
        {
            RqBladeTangentialSolver.Snapshot snapshot;
            bool havePair = solver.TryGetSnapshot(bladeA, bladeB, out snapshot);

            if (havePair && snapshot.ClassificationValid)
            {
                // CONTACT EVOLUTION — what the classifier says now. Labelled "now" rather than
                // "classified" since the latched fact below is also a classification.
                text.Append(" · now <b>").Append(snapshot.Scenario).Append("</b>");
                if (snapshot.ScenarioEvolved) text.Append(" <i>(evolved)</i>");

                text.Append('\n').Append("witness ").Append(snapshot.RawTypeA)
                    .Append(" x ").Append(snapshot.RawTypeB);

                if (solver.ApplyTangentialBehaviour)
                    text.Append(" · added ").Append(snapshot.AppliedTangentialForce.ToString("F0"))
                        .Append(" N · ").Append(snapshot.State);
            }
            else if (!havePair)
            {
                text.Append('\n').Append("no classified blade-vs-blade contact yet");
            }

            if (havePair) AppendLatchedFact(snapshot);

            if (havePair && snapshot.ClassificationValid)
                text.Append('\n')
                    .Append("<size=75%>PROVISIONAL policy values — not calibrated friction</size>");
        }

        AppendContactSide();

        text.Append("</size>");
        target.text = text.ToString();
    }

    /// <summary>
    ///     Appends the LATCHED initial-approach fact and the bind gate, beneath the current attribution.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Both facts, on the label, always.</b> The label is where the demo is read from, and the
    ///         current attribution alone is the fact v1 exists to stop trusting: it moves during contact,
    ///         and at Flat a correct first attribution has been observed degrading into a spurious sharp
    ///         one about twenty steps later. Showing only "now" would put that degradation in front of a
    ///         viewer as though it were the verdict.
    ///     </para>
    ///     <para>
    ///         The ring is read with <see cref="BladeShellData.RingOf" /> — the same parser the recorder
    ///         uses — from the latched raw id, so the label and the sealed column cannot disagree.
    ///     </para>
    /// </remarks>
    private void AppendLatchedFact(in RqBladeTangentialSolver.Snapshot s)
    {
        text.Append('\n');

        if (!s.InitialLatched)
        {
            text.Append("initial <b>not latched</b> — no successful classification on this pair yet");
            return;
        }

        text.Append("initial <b>").Append(s.InitialScenario).Append("</b> ")
            .Append(BladeShellData.RingOf(s.InitialRawIdA)).Append('/')
            .Append(BladeShellData.RingOf(s.InitialRawIdB))
            .Append(" @ ").Append(s.InitialNormalForce.ToString("F1")).Append(" N");

        // Greater than one means the pair separated and re-engaged, so the latched fact is NOT the
        // first-pair fact. A viewer must not have to know that to read the line.
        if (s.LatchGeneration > 1) text.Append(" · re-latched ×").Append(s.LatchGeneration);

        text.Append(solver.ResolvePolicyFromLatchedScenario
            ? " · <b>steering</b>"
            : " · recording only");

        // RETIRED 2026-08-25: a "bind gate" line used to print here, driven by a pressure criterion that
        // was never declarable. Removed with the mechanism. The label now reports the LATCHED
        // ATTRIBUTION only, which is what Phase 1 actually established.
    }

    /// <summary>
    ///     Appends the contact side as <c>Ae±</c> / <c>Af±</c>, red when it departs from the station's
    ///     declared valid state.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Computed from the profile axes and the simulated placement — no Pilot.</b> The previous
    ///         version called <c>StationDebugGizmos.TryEdgePairClosest</c>, which reads
    ///         <c>Pilot.swordA/swordB</c>. Every station's Pilot is a shared INACTIVE rig with NULL swords,
    ///         so it always returned false and this block was dead code that could never display. Worse, had
    ///         those swords been non-null it would have reported the PILOT's pairing on every station of a
    ///         presentation class rather than the station's own.
    ///     </para>
    ///     <para>
    ///         Poses come from <c>RqBladeContact.SimulatedPlacement</c> — the generated ragdoll proxy, never
    ///         the authored transform.
    ///     </para>
    ///     <para>
    ///         <b>Two terms, and the station declares which applies.</b> <c>Ae±</c> is which EDGE is engaged;
    ///         <c>Af±</c> is which FACE is presented. Both are computed and both are shown, but the declared
    ///         <see cref="term" /> picks which one leads — the presentation is a per-station fact, not
    ///         something to infer from a magnitude threshold this component would have to invent. The face
    ///         <c>|dot|</c> is printed so a weakly-determined reading is visible rather than hidden: measured
    ///         ~0.97 at Flat where the face term is right, ~0.21 at EdgeBind where it is not.
    ///     </para>
    ///     <para>
    ///         <b>Signs.</b> <c>e+</c> is the edge toward the profile's <c>EdgeAxis</c>, which measures
    ///         exactly along the 276.6 mm crossguard span — Orange on A, Yellow on B, named from the
    ///         sword's own <c>Marker_Quillon_*</c> objects so the readout uses the apparatus's names rather
    ///         than a duplicate table that could drift. <c>FaceNormal = BladeAxis x EdgeAxis</c> (dot
    ///         1.0000), so <c>f+</c> is the face seen with the tip up and Orange/Yellow to the left.
    ///     </para>
    /// </remarks>
    private void AppendContactSide()
    {
        // The marker-derived source wins wherever it is assigned, so every station's line is produced
        // the same way. The package path below stays as the fallback and as the cross-check.
        if (contactSideProbe != null && AppendContactSideFromProbe()) return;

        if (bladeA == null || bladeB == null) return;

        Collider colA = LiveCollider(bladeA);
        Collider colB = LiveCollider(bladeB);
        if (colA == null || colB == null) return;

        BladeProfileAsset profA = bladeA.Shell != null ? bladeA.Shell.ProfileAsset : null;
        BladeProfileAsset profB = bladeB.Shell != null ? bladeB.Shell.ProfileAsset : null;
        if (profA == null || profB == null) return;

        // Closest approach between the two live hulls. ClosestPoint returns the query point unchanged when
        // it is inside, so this converges to a surface pair and CANNOT report overlap. Adequate for locating
        // the contact; never a separation claim.
        Vector3 pA = colA.bounds.center, pB = colB.bounds.center;
        for (int i = 0; i < 10; i++)
        {
            pA = colA.ClosestPoint(pB);
            pB = colB.ClosestPoint(pA);
        }

        BladePose poseA = bladeA.SimulatedPlacement, poseB = bladeB.SimulatedPlacement;
        Vector3 eA = (poseA.Rotation * profA.EdgeAxis).normalized;
        Vector3 eB = (poseB.Rotation * profB.EdgeAxis).normalized;
        Vector3 nA = (poseA.Rotation * profA.FaceNormal).normalized;
        Vector3 nB = (poseB.Rotation * profB.FaceNormal).normalized;

        // Edge: the contact point relative to that blade's OWN origin, on its own edge axis.
        bool aEdgePlus = Vector3.Dot(pA - poseA.Position, eA) >= 0f;
        bool bEdgePlus = Vector3.Dot(pB - poseB.Position, eB) >= 0f;

        // Face: which way that blade's face normal points relative to the other blade.
        float aFaceDot = Vector3.Dot((pB - pA).normalized, nA);
        float bFaceDot = Vector3.Dot((pA - pB).normalized, nB);

        string edgePair = "Ae" + (aEdgePlus ? "+" : "-") + " / Be" + (bEdgePlus ? "+" : "-");
        string facePair = "Af" + (aFaceDot >= 0f ? "+" : "-") + " / Bf" + (bFaceDot >= 0f ? "+" : "-");

        bool edgeLeads = term == ContactTerm.Edge;
        string lead = edgeLeads ? edgePair : facePair;
        string other = edgeLeads ? facePair : edgePair;

        bool departed = !string.IsNullOrEmpty(declaredReference) &&
                        lead.Replace(" ", string.Empty) != declaredReference.Replace(" ", string.Empty);

        text.Append('\n');

        if (departed)
            text.Append("<color=#FF4040><b>").Append(lead).Append("</b>  (declared ")
                .Append(declaredReference).Append(")</color>");
        else
            text.Append("<b>").Append(lead).Append("</b>");

        if (edgeLeads)
            text.Append("  ").Append(QuillonName(bladeA, aEdgePlus)).Append(" vs ")
                .Append(QuillonName(bladeB, bEdgePlus));

        text.Append("  <size=75%>[").Append(other).Append("  face|dot| ")
            .Append(Mathf.Abs(aFaceDot).ToString("F2")).Append('/')
            .Append(Mathf.Abs(bFaceDot).ToString("F2")).Append("]</size>");
    }

    /// <summary>
    ///     The same line, from the marker-derived probe. Returns false when it has nothing to say, so the
    ///     caller can fall back rather than print a blank where a reading used to be.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Identical presentation to the package path below</b>, deliberately: same lead pair, same
    ///         bracketed other pair with both face <c>|dot|</c> values, same red on a departure from the
    ///         declared state. A viewer walking the ten stations must not be able to tell which source a
    ///         label came from, because the whole point is that it no longer matters.
    ///     </para>
    ///     <para>
    ///         <b>Only a FULL inversion is red.</b> Both sides departing is the pass-through signature;
    ///         one side moving is the contact travelling around the blade, which happens in valid runs and
    ///         is shown in normal weight with a note. That split is the probe's, not this label's — it is
    ///         read from <c>Inverted</c> and <c>OneSideChanged</c> rather than recomputed here, so the
    ///         label and any gate built on the probe cannot disagree.
    ///     </para>
    /// </remarks>
    private bool AppendContactSideFromProbe()
    {
        if (contactSideProbe.Refused)
        {
            text.Append('\n').Append("<color=#FF4040>contact side unavailable — ")
                .Append(contactSideProbe.RefusalReason).Append("</color>");
            return true;
        }

        if (!contactSideProbe.HasReading) return false;

        bool edgeLeads = term == ContactTerm.Edge;
        string lead = edgeLeads ? contactSideProbe.EdgePair : contactSideProbe.FacePair;
        string other = edgeLeads ? contactSideProbe.FacePair : contactSideProbe.EdgePair;

        text.Append('\n');

        if (contactSideProbe.Inverted)
            text.Append("<color=#FF4040><b>").Append(lead).Append("</b>  (declared ")
                .Append(contactSideProbe.DeclaredReference).Append(")</color>");
        else
            text.Append("<b>").Append(lead).Append("</b>");

        // The probe's own quillon names, NOT QuillonName below: that one resolves through the profile
        // asset and would print "? vs ?" on the seven stations that have no profile.
        if (edgeLeads)
            text.Append("  ").Append(contactSideProbe.AEngagedQuillon).Append(" vs ")
                .Append(contactSideProbe.BEngagedQuillon);

        if (contactSideProbe.OneSideChanged)
            text.Append("  <size=75%>(one side, declared ")
                .Append(contactSideProbe.DeclaredReference).Append(")</size>");

        text.Append("  <size=75%>[").Append(other).Append("  face|dot| ")
            .Append(Mathf.Abs(contactSideProbe.AFaceDot).ToString("F2")).Append('/')
            .Append(Mathf.Abs(contactSideProbe.BFaceDot).ToString("F2")).Append("]</size>");

        return true;
    }

    /// <summary>The blade collider actually carried by a simulating body, or null before the proxy exists.</summary>
    private static Collider LiveCollider(RqBladeContact blade)
    {
        BladeShell shell = blade != null ? blade.Shell : null;
        System.Collections.Generic.IReadOnlyList<Collider> live = shell != null ? shell.ActiveColliders : null;
        for (int i = 0; live != null && i < live.Count; i++)
            if (live[i] != null && live[i].attachedRigidbody != null)
                return live[i];

        return null;
    }

    /// <summary>
    ///     Colour name of the quillon on the given edge side, read from the sword's own
    ///     <c>Marker_Quillon_*</c> objects.
    /// </summary>
    private static string QuillonName(RqBladeContact blade, bool plusSide)
    {
        BladeProfileAsset prof = blade != null && blade.Shell != null ? blade.Shell.ProfileAsset : null;
        if (prof == null) return "?";

        BladePose pose = blade.SimulatedPlacement;
        Vector3 edge = (pose.Rotation * prof.EdgeAxis).normalized;

        string best = "?";
        float bestDot = plusSide ? float.MinValue : float.MaxValue;

        foreach (Transform t in blade.GetComponentsInChildren<Transform>(true))
        {
            if (!t.name.StartsWith(QuillonMarkerPrefix)) continue;

            float d = Vector3.Dot(t.position - pose.Position, edge);
            if (plusSide ? d <= bestDot : d >= bestDot) continue;

            bestDot = d;
            best = t.name.Substring(QuillonMarkerPrefix.Length);
        }

        return best;
    }

    private const string QuillonMarkerPrefix = "Marker_Quillon_";
}
