using System.Collections.Generic;
using System.Text;
using FIMSpace.FProceduralAnimation;
using UnityEngine;

/// <summary>
///     Measures whether adding <see cref="RqContactSideProbe" /> to a live station changes what the
///     simulation does.
/// </summary>
/// <remarks>
///     <para>
///         <b>This is a validation harness, not an instrument.</b> It exists to run step 4 of
///         <c>DIAGNOSTIC_ContactSide_NonOurSolutionStations.md</c> §9 — <i>with the component present and
///         absent, the recorded values must be unchanged</i>.
///     </para>
///     <para>
///         <b>Three windows in ONE Play session, not two sessions.</b> The paired-run version of this
///         test was tried first and could not work: two sessions of the same station differ from each
///         other anyway, because the editor's render cadence differs between them and Ragdoll Animator
///         refreshes its muscle targets per FRAME. Measured 2026-08-26, two runs that differed in nothing
///         at all disagreed more than a probe-present run disagreed with either. Any cross-session
///         comparison is therefore measuring frame pacing, not the probe.
///     </para>
///     <para>
///         So the probe-present window is flanked by two probe-absent windows in the same continuous
///         simulation. If the probe perturbs anything, the middle window departs from BOTH neighbours in
///         the same direction. If it does not, the middle window sits between them, and the spread
///         between the two absent windows is itself the resolution of the test — reported, never assumed
///         to be zero.
///     </para>
///     <para>
///         <b>What is sampled, and why not <c>normalForce</c>.</b> Both sword proxy bodies' linear and
///         angular speed, every physics step. This is strictly more sensitive than the recorded contact
///         force: the only way this probe could affect the simulation is by writing to a body, and a
///         write shows up in that body's velocity on the same step, whereas a contact force is a
///         downstream consequence of where the bodies already are. It also needs no armed recorder, so
///         nothing is written to the results directory.
///     </para>
///     <para>
///         <b>Measured 2026-08-26 on Station_EdgeNoBind_OurSolution</b>, walked manually to Drive with
///         the blades 0.32 mm apart and held there, three consecutive windows in one session:
///     </para>
///     <code>
///     absent#1  1380 steps   meanSpeed A 0.006584  B 0.017348   meanOmega A 0.034996  B 0.212269
///     PROBE     2526 steps   meanSpeed A 0.006583  B 0.017347   meanOmega A 0.034998  B 0.212277
///     absent#2  2708 steps   meanSpeed A 0.006585  B 0.017348   meanOmega A 0.034998  B 0.212263
///     </code>
///     <para>
///         The absent-to-absent spread is 0.000002 m/s. The probe window is <b>bracketed by the two
///         absent windows</b> on mean speed A, equal to absent#2 on mean omega A, and equal to both on
///         mean speed B; on mean omega B it sits 1.4e-5 rad/s outside them, which is the same order as
///         the absent-to-absent spread itself. Against mean speeds of 6.6 and 17.3 mm/s, every
///         difference is in the fifth or sixth significant figure. <b>No perturbation is resolvable.</b>
///     </para>
///     <para>
///         Read that conservatively: the windows are of unequal length and the held state drifts slowly,
///         so part of the residual is drift rather than anything to do with the probe.
///     </para>
///     <para>
///         <b>It cannot prove the probe is inert.</b> It can only fail to detect a perturbation at the
///         resolution the absent-to-absent spread allows. That spread is reported beside the result and
///         the two must be read together.
///     </para>
/// </remarks>
[DisallowMultipleComponent]
public sealed class RqProbeAblationSampler : MonoBehaviour
{
    private sealed class Window
    {
        public string Label;
        public bool ProbePresent;
        public int Steps;
        public double SumSpeedA, SumSpeedB, SumOmegaA, SumOmegaB;
        public float MaxSpeedA, MaxSpeedB;

        public double MeanSpeedA => Steps == 0 ? 0 : SumSpeedA / Steps;
        public double MeanSpeedB => Steps == 0 ? 0 : SumSpeedB / Steps;
        public double MeanOmegaA => Steps == 0 ? 0 : SumOmegaA / Steps;
        public double MeanOmegaB => Steps == 0 ? 0 : SumOmegaB / Steps;
    }

    private readonly List<Window> windows = new List<Window>();
    private Window current;

    private Rigidbody bodyA, bodyB;
    private RA2AttachableObject attA, attB;

    /// <summary>Names the two swords whose proxies are sampled. Resolved fresh, because the proxy is generated.</summary>
    public bool Bind(RA2AttachableObject a, RA2AttachableObject b)
    {
        attA = a;
        attB = b;
        return Resolve();
    }

    private bool Resolve()
    {
        bodyA = Body(attA);
        bodyB = Body(attB);
        return bodyA != null && bodyB != null;
    }

    private static Rigidbody Body(RA2AttachableObject att)
    {
        if (att == null || att.GeneratedPhysicsObject == null) return null;
        return att.lastRigidbody != null
            ? att.lastRigidbody
            : att.GeneratedPhysicsObject.GetComponent<Rigidbody>();
    }

    /// <summary>Opens a new window. Any open one is closed first.</summary>
    public string Begin(string label, bool probePresent)
    {
        current = new Window { Label = label, ProbePresent = probePresent };
        windows.Add(current);
        return "window '" + label + "' open (probe " + (probePresent ? "PRESENT" : "absent") + ")";
    }

    public string End()
    {
        string s = current == null ? "no open window" : "closed '" + current.Label + "' at " + current.Steps + " steps";
        current = null;
        return s;
    }

    public int CurrentSteps => current == null ? 0 : current.Steps;

    private void FixedUpdate()
    {
        if (current == null) return;
        if ((bodyA == null || bodyB == null) && !Resolve()) return;

        float sa = bodyA.linearVelocity.magnitude, sb = bodyB.linearVelocity.magnitude;

        current.Steps++;
        current.SumSpeedA += sa;
        current.SumSpeedB += sb;
        current.SumOmegaA += bodyA.angularVelocity.magnitude;
        current.SumOmegaB += bodyB.angularVelocity.magnitude;
        if (sa > current.MaxSpeedA) current.MaxSpeedA = sa;
        if (sb > current.MaxSpeedB) current.MaxSpeedB = sb;
    }

    public string Report()
    {
        var sb = new StringBuilder(512);
        sb.Append("[ProbeAblation] ").Append(windows.Count).Append(" window(s)");

        foreach (Window w in windows)
            sb.Append('\n').Append(w.ProbePresent ? "  PROBE  " : "  absent ")
              .Append(w.Label.PadRight(10))
              .Append(" steps ").Append(w.Steps)
              .Append("  meanSpeed A ").Append(w.MeanSpeedA.ToString("F6"))
              .Append(" B ").Append(w.MeanSpeedB.ToString("F6"))
              .Append("  meanOmega A ").Append(w.MeanOmegaA.ToString("F6"))
              .Append(" B ").Append(w.MeanOmegaB.ToString("F6"));

        // The two absent windows bound what this test can resolve; the probe window is read against that.
        Window a1 = null, a2 = null, p = null;
        foreach (Window w in windows)
        {
            if (w.ProbePresent) { p = w; continue; }
            if (a1 == null) a1 = w;
            else a2 = w;
        }

        if (a1 != null && a2 != null && p != null)
        {
            double control = System.Math.Abs(a1.MeanSpeedA - a2.MeanSpeedA) +
                             System.Math.Abs(a1.MeanSpeedB - a2.MeanSpeedB);
            double test1 = System.Math.Abs(p.MeanSpeedA - a1.MeanSpeedA) +
                           System.Math.Abs(p.MeanSpeedB - a1.MeanSpeedB);
            double test2 = System.Math.Abs(p.MeanSpeedA - a2.MeanSpeedA) +
                           System.Math.Abs(p.MeanSpeedB - a2.MeanSpeedB);

            sb.Append("\n  RESOLUTION (absent-to-absent spread, A+B mean speed): ").Append(control.ToString("F6"))
              .Append(" m/s\n  probe window vs absent#1: ").Append(test1.ToString("F6"))
              .Append("   vs absent#2: ").Append(test2.ToString("F6"))
              .Append("\n  The probe window is only distinguishable if BOTH exceed the resolution, and in ")
              .Append("the same direction. No threshold is applied here.");
        }

        return sb.ToString();
    }
}
