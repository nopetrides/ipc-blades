using System.Collections.Generic;
using System.Reflection;
using System.Text;
using BladeContact;
using FIMSpace.FProceduralAnimation;
using UnityEngine;

/// <summary>
///     Runs <see cref="RqContactSideProbe" /> alongside the package's own contact-side readout and counts
///     how often the two disagree.
/// </summary>
/// <remarks>
///     <para>
///         <b>This is a validation harness, NOT a probe.</b> It references both the marker-derived
///         instrument and <c>BladeContact.Runtime</c>, on purpose — comparing them is the whole job. That
///         is exactly why it does not live in <c>Assets/P3T/Scripts/Probes/</c>, whose rule 3 forbids a
///         contact-model reference. Nothing here may ever be used as a measurement.
///     </para>
///     <para>
///         <b>Step 1 of the validation protocol</b>
///         (<c>DIAGNOSTIC_ContactSide_NonOurSolutionStations.md</c> §9): the probe's <c>Ae±/Af±</c> must
///         agree string-for-string with <c>AppendContactSide</c> on every tick. <b>Any disagreement is a
///         defect in the probe</b> — the package version is the reference, not the challenger.
///     </para>
///     <para>
///         <b>It builds its own probes at runtime and edits no scene object.</b> Each
///         <see cref="RqStationDemoReadout" /> it finds is read by reflection for the two blades it was
///         authored with, and a probe is spawned on a fresh child object wired to the same two swords and
///         the same <c>term</c>. So the comparison cannot be biased by a hand-wired duplicate, and no
///         station prefab is touched.
///     </para>
///     <para>
///         <b>The declared reference is deliberately NOT copied across.</b> Only the pair strings are
///         compared. A verdict is a presentation fact belonging to the researcher, and copying one here
///         would make this harness look like it had declared something.
///     </para>
///     <para>
///         <b>What agreement can and cannot establish.</b> It establishes that two independent routes to
///         the same frame return the same signs on the ticks that were sampled. It does not establish
///         that either is correct, and it says nothing about ticks in states the session never entered —
///         so a run that never reaches contact validates far less than one that does. The tick count and
///         the number of distinct pair strings seen are both reported, because agreement across a
///         thousand ticks of a single unchanging state is nearly worthless and must not read as strong.
///     </para>
/// </remarks>
[DisallowMultipleComponent]
public sealed class RqContactSideAgreementCheck : MonoBehaviour
{
    [Tooltip("Log the running tally every this many seconds. Zero logs only on demand.")]
    [Min(0f)]
    [SerializeField] private float logEverySeconds = 5f;

    [Tooltip("Log the first N disagreements in full. A disagreement is a defect in the probe, so the " +
             "detail matters more than the count.")]
    [Min(0)]
    [SerializeField] private int logFirstDisagreements = 10;

    private sealed class Pairing
    {
        public string Station;
        public RqStationDemoReadout Readout;
        public RqBladeContact BladeA;
        public RqBladeContact BladeB;
        public RqContactSideProbe Probe;
        public bool LeadsWithEdge;

        public int Ticks;
        public int Compared;
        public int Agreed;
        public int Disagreed;
        public int Logged;
        public readonly HashSet<string> DistinctReference = new HashSet<string>();
        public string LastReference = "";
        public string LastProbe = "";
        public float SmallestDisagreementMargin = float.MaxValue;
        public float LargestDisagreementMargin;
        public readonly StringBuilder History = new StringBuilder(256);
        public string PreviousReference = "";
    }

    private readonly List<Pairing> pairings = new List<Pairing>();
    private readonly HashSet<RqStationDemoReadout> paired = new HashSet<RqStationDemoReadout>();
    private float nextLog;
    private float nextScan;

    private void FixedUpdate()
    {
        // RESCANNED, not built once. Measured 2026-08-26: building on the first step found ONE of the
        // three OurSolution readouts, because the other two station roots were still inactive at that
        // instant. A harness that silently watches one station and reports "1 station(s)" is exactly
        // the sort of quiet under-coverage that makes a clean result meaningless.
        if (Time.time >= nextScan)
        {
            nextScan = Time.time + 1f;
            Build();
        }

        for (int i = 0; i < pairings.Count; i++) Tick(pairings[i]);

        if (logEverySeconds <= 0f || Time.time < nextLog) return;

        nextLog = Time.time + logEverySeconds;
        Debug.Log(Report());
    }

    /// <summary>The running tally, safe to read at any time.</summary>
    public string Report()
    {
        var sb = new StringBuilder(512);
        sb.Append("[ContactSideAgreement] ").Append(pairings.Count).Append(" station(s)");

        for (int i = 0; i < pairings.Count; i++)
        {
            Pairing p = pairings[i];
            sb.Append('\n').Append(p.Station)
              .Append("  ticks ").Append(p.Ticks)
              .Append("  compared ").Append(p.Compared)
              .Append("  agreed ").Append(p.Agreed)
              .Append("  DISAGREED ").Append(p.Disagreed)
              .Append("  distinct states ").Append(p.DistinctReference.Count)
              .Append(p.Disagreed > 0
                          ? "  disagreement margin " + p.SmallestDisagreementMargin.ToString("F4") +
                            " to " + p.LargestDisagreementMargin.ToString("F4") +
                            (p.LeadsWithEdge ? " mm" : " |dot|")
                          : "")
              .Append("  last ref '").Append(p.LastReference)
              .Append("' probe '").Append(p.LastProbe).Append('\'');

            if (p.History.Length > 0) sb.Append("\n    history ").Append(p.History);

            if (p.Probe != null && p.Probe.Refused)
                sb.Append("  PROBE REFUSED: ").Append(p.Probe.RefusalReason);
            else if (p.Probe != null && !p.Probe.Resolved)
                sb.Append("  probe not resolved");
        }

        return sb.ToString();
    }

    private void Tick(Pairing p)
    {
        p.Ticks++;

        // SAMPLE THE PROBE AND COMPUTE THE REFERENCE IN THE SAME INSTANT. Reading the probe's cached
        // FixedUpdate value instead compares two different points in the script execution order:
        // measured 2026-08-26, that produced 110 alternating single-tick disagreements across 733
        // ticks at the three stations, the probe and the reference each flipping on opposite ticks.
        // Taking both here dropped that to 0 in 4338 ticks at EdgeBind and Flat. The instrument was
        // the suspect and the instrument was the fault.
        if (p.Probe == null || !p.Probe.Sample()) return;

        string reference;
        if (!TryReferencePair(p, out reference)) return;

        string probe = p.LeadsWithEdge ? p.Probe.EdgePair : p.Probe.FacePair;

        p.Compared++;
        p.LastReference = reference;
        p.LastProbe = probe;
        p.DistinctReference.Add(reference);

        // The SEQUENCE, not just the set. A pop-through is a transition, and a run that ends inverted
        // reads identically to one that started inverted unless the change itself is on the record.
        if (reference != p.PreviousReference)
        {
            p.History.Append(p.History.Length == 0 ? "" : " -> ").Append('[').Append(p.Ticks).Append("] ")
                     .Append(reference);
            Debug.Log("[ContactSideAgreement] " + p.Station + " tick " + p.Ticks + ": '" +
                      p.PreviousReference + "' -> '" + reference + "'   probe '" + probe + "'   " +
                      "acrossEdge " + (p.Probe.AAcrossEdgeMetres * 1000f).ToString("F2") + "/" +
                      (p.Probe.BAcrossEdgeMetres * 1000f).ToString("F2") + " mm   alongBlade " +
                      (p.Probe.AAlongBladeMetres * 1000f).ToString("F0") + "/" +
                      (p.Probe.BAlongBladeMetres * 1000f).ToString("F0") + " mm", this);
            p.PreviousReference = reference;
        }

        if (reference == probe)
        {
            p.Agreed++;
            return;
        }

        p.Disagreed++;

        // How near the decision boundary the disagreeing sign was. A sign taken microns from the
        // centreline, or off a face dot of 0.03, is undetermined rather than wrong — and the two
        // routes differ by a known ~1.1 deg on the face axis. Recorded so the two cases can be told
        // apart instead of both being called a defect.
        float margin = p.LeadsWithEdge
            ? Mathf.Min(Mathf.Abs(p.Probe.AAcrossEdgeMetres), Mathf.Abs(p.Probe.BAcrossEdgeMetres)) * 1000f
            : Mathf.Min(Mathf.Abs(p.Probe.AFaceDot), Mathf.Abs(p.Probe.BFaceDot));

        if (margin < p.SmallestDisagreementMargin) p.SmallestDisagreementMargin = margin;
        if (margin > p.LargestDisagreementMargin) p.LargestDisagreementMargin = margin;

        if (p.Logged >= logFirstDisagreements) return;

        p.Logged++;
        Debug.LogError("[ContactSideAgreement] " + p.Station + " DISAGREEMENT on tick " + p.Ticks +
                       ": reference '" + reference + "' vs probe '" + probe + "'.  " +
                       "probe edge " + p.Probe.EdgePair + " face " + p.Probe.FacePair +
                       "  faceDot " + p.Probe.AFaceDot.ToString("F4") + "/" +
                       p.Probe.BFaceDot.ToString("F4") +
                       "  alongBlade " + (p.Probe.AAlongBladeMetres * 1000f).ToString("F1") + "/" +
                       (p.Probe.BAlongBladeMetres * 1000f).ToString("F1") + " mm" +
                       "  acrossEdge " + (p.Probe.AAcrossEdgeMetres * 1000f).ToString("F3") + "/" +
                       (p.Probe.BAcrossEdgeMetres * 1000f).ToString("F3") + " mm" +
                       "  renderLag " + p.Probe.ARenderLagMm.ToString("F2") + "/" +
                       p.Probe.BRenderLagMm.ToString("F2") + " mm", this);
    }

    /// <summary>
    ///     The package's own answer, recomputed by the same expression <c>AppendContactSide</c> uses —
    ///     including its 10-iteration <c>ClosestPoint</c> seed and its use of the body pose for the axes.
    ///     Reproduced rather than called because the readout writes its result into a private
    ///     <c>StringBuilder</c> and refreshes only a few times a second.
    /// </summary>
    private static bool TryReferencePair(Pairing p, out string pair)
    {
        pair = null;
        if (p.BladeA == null || p.BladeB == null) return false;

        Collider colA = LiveCollider(p.BladeA);
        Collider colB = LiveCollider(p.BladeB);
        if (colA == null || colB == null) return false;

        BladeProfileAsset profA = p.BladeA.Shell != null ? p.BladeA.Shell.ProfileAsset : null;
        BladeProfileAsset profB = p.BladeB.Shell != null ? p.BladeB.Shell.ProfileAsset : null;
        if (profA == null || profB == null) return false;

        Vector3 pA = colA.bounds.center, pB = colB.bounds.center;
        for (int i = 0; i < 10; i++)
        {
            pA = colA.ClosestPoint(pB);
            pB = colB.ClosestPoint(pA);
        }

        BladePose poseA = p.BladeA.SimulatedPlacement, poseB = p.BladeB.SimulatedPlacement;

        if (p.LeadsWithEdge)
        {
            Vector3 eA = (poseA.Rotation * profA.EdgeAxis).normalized;
            Vector3 eB = (poseB.Rotation * profB.EdgeAxis).normalized;

            bool aPlus = Vector3.Dot(pA - poseA.Position, eA) >= 0f;
            bool bPlus = Vector3.Dot(pB - poseB.Position, eB) >= 0f;

            pair = "Ae" + (aPlus ? "+" : "-") + " / Be" + (bPlus ? "+" : "-");
            return true;
        }

        Vector3 nA = (poseA.Rotation * profA.FaceNormal).normalized;
        Vector3 nB = (poseB.Rotation * profB.FaceNormal).normalized;

        float aFace = Vector3.Dot((pB - pA).normalized, nA);
        float bFace = Vector3.Dot((pA - pB).normalized, nB);

        pair = "Af" + (aFace >= 0f ? "+" : "-") + " / Bf" + (bFace >= 0f ? "+" : "-");
        return true;
    }

    private static Collider LiveCollider(RqBladeContact blade)
    {
        BladeShell shell = blade != null ? blade.Shell : null;
        IReadOnlyList<Collider> live = shell != null ? shell.ActiveColliders : null;
        for (int i = 0; live != null && i < live.Count; i++)
            if (live[i] != null && live[i].attachedRigidbody != null)
                return live[i];

        return null;
    }

    // ---------------------------------------------------------------------------------------------

    private void Build()
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        FieldInfo fA = typeof(RqStationDemoReadout).GetField("bladeA", Private);
        FieldInfo fB = typeof(RqStationDemoReadout).GetField("bladeB", Private);
        FieldInfo fTerm = typeof(RqStationDemoReadout).GetField("term", Private);

        if (fA == null || fB == null || fTerm == null)
        {
            Debug.LogError("[ContactSideAgreement] RqStationDemoReadout no longer exposes bladeA/bladeB/" +
                           "term, so the comparison cannot be wired from the authored readout. Refusing " +
                           "rather than wiring it by hand, which would let the two sides be given " +
                           "different swords without anyone noticing.", this);
            return;
        }

        var readouts = FindObjectsByType<RqStationDemoReadout>(FindObjectsInactive.Exclude);
        int before = pairings.Count;

        foreach (RqStationDemoReadout r in readouts)
        {
            if (!paired.Add(r)) continue;

            var bladeA = fA.GetValue(r) as RqBladeContact;
            var bladeB = fB.GetValue(r) as RqBladeContact;
            if (bladeA == null || bladeB == null) continue; // not an OurSolution station; nothing to compare against

            RA2AttachableObject attA = bladeA.GetComponent<RA2AttachableObject>();
            RA2AttachableObject attB = bladeB.GetComponent<RA2AttachableObject>();
            string colliderName = bladeA.Shell != null && bladeA.Shell.BladeCollider != null
                ? bladeA.Shell.BladeCollider.name
                : null;

            if (attA == null || attB == null || colliderName == null)
            {
                Debug.LogWarning("[ContactSideAgreement] " + r.name + ": no attachable or no named blade " +
                                 "collider, so no probe can be built for it.", this);
                continue;

            }

            bool edgeLeads = (int)fTerm.GetValue(r) == 0; // ContactTerm.Edge

            var host = new GameObject("ContactSideProbe (agreement harness)");
            host.transform.SetParent(transform, false);

            RqContactSideProbe probe = host.AddComponent<RqContactSideProbe>();
            Wire(probe, "swordA", attA);
            Wire(probe, "swordB", attB);
            Wire(probe, "contactColliderName", colliderName);
            Wire(probe, "term", edgeLeads ? 0 : 1);

            pairings.Add(new Pairing
            {
                Station = r.name,
                Readout = r,
                BladeA = bladeA,
                BladeB = bladeB,
                Probe = probe,
                LeadsWithEdge = edgeLeads
            });
        }

        if (pairings.Count != before)
            Debug.Log("[ContactSideAgreement] now watching " + pairings.Count + " pairing(s); this scan " +
                      "saw " + readouts.Length + " active readout(s).", this);
    }

    private static void Wire(RqContactSideProbe probe, string field, object value)
    {
        FieldInfo f = typeof(RqContactSideProbe).GetField(
            field, BindingFlags.Instance | BindingFlags.NonPublic);

        if (f == null)
        {
            Debug.LogError("[ContactSideAgreement] RqContactSideProbe has no field '" + field + "'.");
            return;
        }

        f.SetValue(probe, f.FieldType.IsEnum ? System.Enum.ToObject(f.FieldType, value) : value);
    }
}
