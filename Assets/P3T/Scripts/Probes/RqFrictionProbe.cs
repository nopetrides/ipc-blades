using System.Collections.Generic;
using System.Text;
using FIMSpace.FProceduralAnimation;
using UnityEngine;

/// <summary>
///     The friction PhysX will actually use on this station's live shapes — not what was declared.
/// </summary>
/// <remarks>
///     <para>
///         <b>READ-ONLY. This component writes nothing</b> — no material, no body, no collider, no
///         enabled state. It reads and reports.
///     </para>
///     <para>
///         <b>Why it exists.</b> Every one of QuickDirty's three silent failure modes was found by
///         reading the configuration, not by any measurement noticing something was wrong: none of them
///         produced an error, a warning or an anomalous readout. And eight ablation runs were measured at
///         a live 0.450/0.350 while the project declaration still said 0.3, a mismatch that had to be
///         recorded as a limitation rather than corrected. A sealed file stating the DECLARED number is
///         stating something that may not have been true of the run.
///     </para>
///     <para>
///         <b>It reports EVERY live shape, not a filtered subset, and that is the point.</b> Filtering to
///         "the blade" is exactly what hides the second failure mode: the combine-mode priority rule is
///         not local to the thing you configured, so ANY participating collider at a higher-priority mode
///         governs the pair — including one that inherited that mode from the project default without
///         anyone choosing it. A probe that only looked at the two hulls someone edited would report the
///         edit as effective while a third shape silently overrode it.
///     </para>
///     <para>
///         <b>It reads the PROXY, which is the only thing that carries a contact.</b> On a worn sword
///         Ragdoll Animator disables the authored colliders and rebuilds them on a generated body; the
///         authored sword is kinematic with <c>detectCollisions = false</c>. A material read off the
///         authored collider is not necessarily the material in the contact — and on a QuickDirty blade
///         the original hull is disabled on the proxy while the authored one still reads enabled, so the
///         authored view is actively misleading there.
///     </para>
///     <para>
///         <b>The ratio matters, not the magnitudes.</b> Coulomb friction can only produce the depinning
///         threshold a bind needs if static EXCEEDS dynamic, and how much it exceeds by is what decides
///         whether a release is visible. Measured on this project: 6.0/3.0 is a ratio of 0.500 and
///         produces one; <c>BladeGrip</c>'s 0.450/0.350 is 0.778 and was too shallow to. So the ratio is
///         reported beside both coefficients on every line.
///     </para>
///     <para>
///         <b>It sets no threshold and declares no value.</b> Where a declared figure is not serialized
///         the probe says NOT DECLARED rather than assuming one, and nothing here decides whether a
///         mismatch invalidates a run.
///     </para>
/// </remarks>
[DisallowMultipleComponent]
public sealed class RqFrictionProbe : MonoBehaviour
{
    /// <summary>
    ///     Combine-mode priority, which is NOT the enum's own order.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Unity 6000.4 <c>PhysicsMaterialCombine</c>: <i>"there is a special case when the two
    ///         overlapping colliders have physics materials with different combine modes set. In this
    ///         particular case, the function that has the highest priority is used. The priority order is
    ///         as follows: Average &lt; Minimum &lt; Multiply &lt; Maximum."</i>
    ///     </para>
    ///     <para>
    ///         <b>The enum's numeric values are Average 0, Multiply 1, Minimum 2, Maximum 3</b> —
    ///         verified live. So <c>Multiply</c> sorts BELOW <c>Minimum</c> numerically while ranking
    ///         ABOVE it in priority, and comparing the enum values directly gets those two backwards.
    ///         This table exists because that is a trap, not because a table is tidier.
    ///     </para>
    /// </remarks>
    private static int Priority(PhysicsMaterialCombine mode)
    {
        switch (mode)
        {
            case PhysicsMaterialCombine.Average: return 0;
            case PhysicsMaterialCombine.Minimum: return 1;
            case PhysicsMaterialCombine.Multiply: return 2;
            case PhysicsMaterialCombine.Maximum: return 3;
            default: return 0;
        }
    }

    /// <summary>One live shape and the material values PhysX will use for it.</summary>
    public struct Shape
    {
        public string Side;
        public string Name;
        public string Material;

        /// <summary>True when the shape carries no material and inherits the project default.</summary>
        public bool Inherited;

        public float StaticFriction;
        public float DynamicFriction;
        public PhysicsMaterialCombine FrictionCombine;
        public float Bounciness;
        public PhysicsMaterialCombine BounceCombine;
        public bool Enabled;

        /// <summary>Dynamic over static. 1.000 means no depinning threshold at all.</summary>
        public float Ratio => StaticFriction > 0f ? DynamicFriction / StaticFriction : 0f;

        /// <summary>True when static and dynamic are equal, so no depinning threshold exists.</summary>
        public bool Collapsed => Mathf.Approximately(StaticFriction, DynamicFriction);
    }

    /// <summary>One cross pair of live shapes, and the coefficient PhysX resolves for it.</summary>
    public struct Pair
    {
        public string A;
        public string B;
        public PhysicsMaterialCombine ModeA;
        public PhysicsMaterialCombine ModeB;
        public PhysicsMaterialCombine Mode;
        public float StaticFriction;
        public float DynamicFriction;
        public bool ModesDisagreed;

        /// <summary>Which side's mode won on priority, when they disagreed.</summary>
        public string ModeWonBy;

        public float Ratio => StaticFriction > 0f ? DynamicFriction / StaticFriction : 0f;
        public bool Collapsed => Mathf.Approximately(StaticFriction, DynamicFriction);
    }

    [Header("The pair")]
    [Tooltip("Sword A's attachable. The probe reaches the generated proxy through it, because the proxy " +
             "is the only body that carries a contact.")]
    [SerializeField] private RA2AttachableObject swordA;

    [SerializeField] private RA2AttachableObject swordB;

    [Tooltip("Shape names treated as THE BLADE on this station, for labelling only. Every live shape is " +
             "reported and paired whatever is listed here; this only marks which rows are the blade. " +
             "Filtering the report to these would hide the failure mode where a third participating " +
             "shape overrides the two that were configured.")]
    [SerializeField]
    private List<string> bladeShapeNames = new List<string> { "Blade", "QuickEdgeHull", "QuickFlatHull" };

    [Header("The project default material — DECLARED, not readable at runtime")]
    [Tooltip("What a collider with NO material of its own inherits. Unity exposes no runtime getter for " +
             "the project Default Material, so it cannot be measured during Play and has to be declared " +
             "here.\n\nMeasured in ProjectSettings/DynamicsManager.asset on 2026-08-26: it is BladeGrip, " +
             "0.450/0.350, frictionCombine MAXIMUM. That matters because the Blade collider carries no " +
             "explicit material on any of the ten stations, so on every PhysX and OurSolution station " +
             "the blade contact runs on the project default at the highest-priority combine mode.\n\n" +
             "Left empty, the probe reports inherited shapes as UNKNOWN rather than guessing.")]
    [SerializeField] private PhysicsMaterial projectDefaultMaterial;

    [Header("Declared values — the researcher's, and left unset until then")]
    [Tooltip("The DECLARED static friction for this station's blades, for comparison against live. " +
             "NEGATIVE means not declared, and the probe then reports NOT DECLARED rather than assuming " +
             "a figure. This component never fills it in.")]
    [SerializeField] private float declaredStaticFriction = -1f;

    [Tooltip("The DECLARED dynamic friction. Negative means not declared.")]
    [SerializeField] private float declaredDynamicFriction = -1f;

    [Tooltip("The combine mode this station is INTENDED to run at. Any live shape whose mode outranks " +
             "this one governs the pair instead, which is the silent override this field exists to " +
             "detect. Average is the enum's zero and is treated as 'not declared'.")]
    [SerializeField] private PhysicsMaterialCombine intendedCombine = PhysicsMaterialCombine.Average;

    [Tooltip("Log the declaration once when it first resolves. The lines are always readable from " +
             "DeclarationLines() regardless.")]
    [SerializeField] private bool logOnResolve = true;

    private readonly List<Shape> shapes = new List<Shape>(8);
    private readonly List<Pair> pairs = new List<Pair>(16);
    private bool resolved;
    private bool logged;
    private bool wornAtSample;
    private string refusalReason = "";

    /// <summary>True once both proxies were found and every live shape was read.</summary>
    public bool Resolved => resolved;

    /// <summary>Why not, when it could not resolve.</summary>
    public string RefusalReason => refusalReason;

    /// <summary>Every live shape read on the most recent sample, blade or not.</summary>
    public IReadOnlyList<Shape> Shapes => shapes;

    /// <summary>Every cross pair of ENABLED shapes and its resolved coefficient.</summary>
    public IReadOnlyList<Pair> Pairs => pairs;

    /// <summary>True when any enabled shape has static equal to dynamic.</summary>
    public bool AnyCollapsed
    {
        get
        {
            for (int i = 0; i < shapes.Count; i++)
                if (shapes[i].Enabled && shapes[i].Collapsed) return true;

            return false;
        }
    }

    /// <summary>
    ///     The highest-priority combine mode among ENABLED shapes — the one that governs every pair it
    ///     participates in.
    /// </summary>
    public PhysicsMaterialCombine GoverningCombine { get; private set; }

    /// <summary>Which shape carries <see cref="GoverningCombine" />.</summary>
    public string GoverningShape { get; private set; } = "";

    /// <summary>
    ///     True when an enabled shape's combine mode outranks <see cref="intendedCombine" />.
    /// </summary>
    /// <remarks>
    ///     This is failure mode 2 made visible. The setting that was edited can be correct and inert at
    ///     the same time, and nothing else in the apparatus says so.
    /// </remarks>
    public bool CombineOverridden { get; private set; }

    /// <summary>True when a declared coefficient exists and a live one differs from it.</summary>
    public bool DeclaredMismatch { get; private set; }

    private void FixedUpdate()
    {
        Sample();
    }

    /// <summary>
    ///     Reads every live shape now and resolves the pair coefficients.
    /// </summary>
    /// <remarks>
    ///     Re-read every step rather than cached at resolve: a material can be swapped at runtime — that
    ///     is exactly the attribution-selected-material mechanism this project considered and declined —
    ///     and a probe that cached its answer would keep reporting the old value if anything ever did.
    /// </remarks>
    public bool Sample()
    {
        shapes.Clear();
        pairs.Clear();
        DeclaredMismatch = false;
        CombineOverridden = false;
        GoverningCombine = PhysicsMaterialCombine.Average;
        GoverningShape = "";

        if (swordA == null || swordB == null)
        {
            refusalReason = "swordA and swordB must both be assigned.";
            resolved = false;
            return false;
        }

        var liveA = new List<int>();
        var liveB = new List<int>();

        if (!Collect(swordA, "A", liveA) || !Collect(swordB, "B", liveB))
        {
            resolved = false;
            return false;
        }

        // Governing mode across everything enabled on BOTH swords, since a pair is governed by the
        // higher-priority of its two members and any enabled shape can become a member.
        for (int i = 0; i < shapes.Count; i++)
        {
            if (!shapes[i].Enabled) continue;
            if (Priority(shapes[i].FrictionCombine) <= Priority(GoverningCombine) &&
                !string.IsNullOrEmpty(GoverningShape)) continue;

            GoverningCombine = shapes[i].FrictionCombine;
            GoverningShape = shapes[i].Side + ":" + shapes[i].Name;
        }

        if (intendedCombine != PhysicsMaterialCombine.Average &&
            Priority(GoverningCombine) > Priority(intendedCombine))
            CombineOverridden = true;

        // EVERY cross pair of enabled shapes. Not just the blade pair: a coefficient is resolved per
        // pair, and which pairs can exist is a property of what is enabled, not of what was intended.
        for (int i = 0; i < liveA.Count; i++)
        for (int j = 0; j < liveB.Count; j++)
            pairs.Add(Combine(shapes[liveA[i]], shapes[liveB[j]]));

        CheckDeclared();

        resolved = true;
        refusalReason = "";

        if (logOnResolve && !logged)
        {
            logged = true;
            foreach (string line in DeclarationLines()) Debug.Log("[Friction] " + line, this);
        }

        return true;
    }

    private bool Collect(RA2AttachableObject att, string side, List<int> liveIndices)
    {
        GameObject generated = att.GeneratedPhysicsObject;
        List<Collider> source;

        if (generated == null)
        {
            // Not worn. The authored shapes are what exist, and the record says so rather than
            // pretending it read the simulating ones.
            wornAtSample = false;
            source = att.AttachableColliders;
            if (source == null)
            {
                refusalReason = side + ": no proxy and no authored collider list.";
                return false;
            }
        }
        else
        {
            wornAtSample = true;
            source = att.GeneratedPhysicsColliders;
            if (source == null)
            {
                refusalReason = side + ": the proxy exists but its generated collider list is null.";
                return false;
            }
        }

        for (int i = 0; i < source.Count; i++)
        {
            Collider c = source[i];
            if (c == null) continue;

            // The proxy names its copies '<name>:Attachable Physics', so the leading name is the shape.
            string bare = c.name;
            int colon = bare.IndexOf(':');
            if (colon > 0) bare = bare.Substring(0, colon);

            PhysicsMaterial m = c.sharedMaterial;
            bool inherited = m == null;
            if (inherited) m = projectDefaultMaterial;

            var s = new Shape
            {
                Side = side,
                Name = bare,
                Inherited = inherited,
                Material = m == null
                    ? (inherited ? "INHERITS PROJECT DEFAULT — UNKNOWN, not declared" : "(none)")
                    : m.name + (inherited ? " (inherited)" : ""),
                Enabled = c.enabled,
            };

            if (m != null)
            {
                s.StaticFriction = m.staticFriction;
                s.DynamicFriction = m.dynamicFriction;
                s.FrictionCombine = m.frictionCombine;
                s.Bounciness = m.bounciness;
                s.BounceCombine = m.bounceCombine;
            }

            shapes.Add(s);

            // A disabled shape is REPORTED but never paired: including it would put a coefficient in the
            // record that nothing can have used. Reporting it matters because "disabled" is precisely
            // the fix that was applied to the original hull, and a record that omitted it could not show
            // the fix was in force.
            if (c.enabled && m != null) liveIndices.Add(shapes.Count - 1);
        }

        return true;
    }

    /// <summary>The coefficient PhysX resolves for one pair, by the documented priority rule.</summary>
    private static Pair Combine(Shape a, Shape b)
    {
        PhysicsMaterialCombine mode;
        string wonBy;

        if (a.FrictionCombine == b.FrictionCombine)
        {
            mode = a.FrictionCombine;
            wonBy = "both";
        }
        else if (Priority(a.FrictionCombine) > Priority(b.FrictionCombine))
        {
            mode = a.FrictionCombine;
            wonBy = "A:" + a.Name;
        }
        else
        {
            mode = b.FrictionCombine;
            wonBy = "B:" + b.Name;
        }

        return new Pair
        {
            A = a.Name,
            B = b.Name,
            ModeA = a.FrictionCombine,
            ModeB = b.FrictionCombine,
            Mode = mode,
            ModeWonBy = wonBy,
            ModesDisagreed = a.FrictionCombine != b.FrictionCombine,
            StaticFriction = Apply(mode, a.StaticFriction, b.StaticFriction),
            DynamicFriction = Apply(mode, a.DynamicFriction, b.DynamicFriction),
        };
    }

    private static float Apply(PhysicsMaterialCombine mode, float x, float y)
    {
        switch (mode)
        {
            case PhysicsMaterialCombine.Minimum: return Mathf.Min(x, y);
            case PhysicsMaterialCombine.Multiply: return x * y;
            case PhysicsMaterialCombine.Maximum: return Mathf.Max(x, y);
            default: return (x + y) * 0.5f;
        }
    }

    private void CheckDeclared()
    {
        if (declaredStaticFriction < 0f && declaredDynamicFriction < 0f) return;

        for (int i = 0; i < pairs.Count; i++)
        {
            if (declaredStaticFriction >= 0f &&
                !Mathf.Approximately(pairs[i].StaticFriction, declaredStaticFriction))
                DeclaredMismatch = true;
            if (declaredDynamicFriction >= 0f &&
                !Mathf.Approximately(pairs[i].DynamicFriction, declaredDynamicFriction))
                DeclaredMismatch = true;
        }
    }

    /// <summary>
    ///     The friction declaration for a run's sealed header — every live shape, every pair, and every
    ///     condition that changes what the numbers mean.
    /// </summary>
    /// <remarks>
    ///     Nothing is omitted on the grounds that it should be the same as something else. A value that
    ///     is expected to match is printed anyway, so the match is demonstrated in the file rather than
    ///     asserted in a comment.
    /// </remarks>
    public List<string> DeclarationLines()
    {
        var lines = new List<string>(24);

        if (!resolved)
        {
            lines.Add("friction,state,NOT RESOLVED,reason," +
                      (string.IsNullOrEmpty(refusalReason) ? "not sampled yet" : refusalReason));
            return lines;
        }

        int enabled = 0;
        for (int i = 0; i < shapes.Count; i++)
            if (shapes[i].Enabled) enabled++;

        lines.Add("friction,source," + (wornAtSample ? "PROXY (the simulating shapes)" : "AUTHORED (not worn)") +
                  ",shapesReported," + shapes.Count + ",shapesEnabled," + enabled +
                  ",pairs," + pairs.Count +
                  ",note,read live, never from a declaration asset. EVERY live shape is reported and " +
                  "paired, not just the blade — the combine-priority rule is not local to the shape that " +
                  "was configured, so a third participating shape can govern the pair silently");

        lines.Add("friction,projectDefaultMaterial," +
                  (projectDefaultMaterial == null
                      ? "NOT DECLARED"
                      : projectDefaultMaterial.name + ",defaultStatic," +
                        projectDefaultMaterial.staticFriction.ToString("F4") + ",defaultDynamic," +
                        projectDefaultMaterial.dynamicFriction.ToString("F4") + ",defaultCombine," +
                        projectDefaultMaterial.frictionCombine) +
                  ",note,Unity exposes no runtime getter for the project Default Material, so this is a " +
                  "DECLARED value and not a measured one. A shape marked inherited uses it");

        lines.Add("friction,combinePriorityRule,Average < Minimum < Multiply < Maximum" +
                  ",enumValues,Average 0; Multiply 1; Minimum 2; Maximum 3" +
                  ",note,per the Unity 6000.4 PhysicsMaterialCombine reference. The enum's numeric order " +
                  "is NOT the priority order — Multiply sorts below Minimum numerically and above it in " +
                  "priority — so comparing enum values directly gets those two backwards");

        lines.Add("friction,governingCombine," + GoverningCombine +
                  ",governingShape," + (string.IsNullOrEmpty(GoverningShape) ? "(none)" : GoverningShape) +
                  ",intendedCombine," +
                  (intendedCombine == PhysicsMaterialCombine.Average ? "NOT DECLARED" : intendedCombine.ToString()) +
                  ",overridden," + (CombineOverridden ? "YES" : "no") +
                  ",note,the governing mode is the highest-priority mode among ENABLED shapes on either " +
                  "sword. It governs every pair the shape carrying it participates in");

        for (int i = 0; i < shapes.Count; i++)
        {
            Shape s = shapes[i];
            lines.Add("frictionShape,side," + s.Side + ",shape," + s.Name +
                      ",enabled," + (s.Enabled ? "true" : "false") +
                      ",material," + s.Material +
                      ",inherited," + (s.Inherited ? "true" : "false") +
                      ",static," + s.StaticFriction.ToString("F4") +
                      ",dynamic," + s.DynamicFriction.ToString("F4") +
                      ",ratio," + s.Ratio.ToString("F4") +
                      ",frictionCombine," + s.FrictionCombine +
                      ",combinePriority," + Priority(s.FrictionCombine) +
                      ",bounciness," + s.Bounciness.ToString("F4") +
                      ",bounceCombine," + s.BounceCombine +
                      ",depinning," + (s.Collapsed ? "NONE — static equals dynamic" : "present"));
        }

        for (int i = 0; i < pairs.Count; i++)
        {
            Pair p = pairs[i];
            lines.Add("frictionPair,a," + p.A + ",b," + p.B +
                      ",modeA," + p.ModeA + ",modeB," + p.ModeB +
                      ",modeUsed," + p.Mode + ",modeWonBy," + p.ModeWonBy +
                      ",modesDisagreed," + (p.ModesDisagreed ? "true" : "false") +
                      ",static," + p.StaticFriction.ToString("F4") +
                      ",dynamic," + p.DynamicFriction.ToString("F4") +
                      ",ratio," + p.Ratio.ToString("F4") +
                      ",depinning," + (p.Collapsed ? "NONE" : "present"));
        }

        if (CombineOverridden)
            lines.Add("friction,WARNING,COMBINE MODE OVERRIDDEN" +
                      ",governing," + GoverningCombine + ",by," + GoverningShape +
                      ",intended," + intendedCombine +
                      ",note,a live shape carries a higher-priority combine mode than this station " +
                      "intends, so the intended mode is CORRECT AND INERT AT THE SAME TIME. This is the " +
                      "failure mode where two edits each look complete and the second silently voids the " +
                      "first. It produces no error and no anomalous readout");

        if (AnyCollapsed)
            lines.Add("friction,WARNING,STATIC EQUALS DYNAMIC on at least one enabled shape" +
                      ",consequence,NO DEPINNING THRESHOLD" +
                      ",note,Coulomb friction can only produce the depinning threshold a bind needs if " +
                      "static EXCEEDS dynamic. Where they are equal a null stick-slip result means " +
                      "NOTHING — the model could not have produced the effect being looked for");

        // No NOT DECLARED line. When no declared coefficient is serialized there is nothing to compare
        // against, and the per-shape frictionShape array plus the frictionPair array above already state
        // every coefficient that ran. Emitting NOT DECLARED made a complete record read like a missing
        // measurement. The comparison below still runs whenever a declared value IS set.
        if (declaredStaticFriction >= 0f || declaredDynamicFriction >= 0f)
            lines.Add("friction,declaredStatic," +
                      (declaredStaticFriction < 0f ? "not declared" : declaredStaticFriction.ToString("F4")) +
                      ",declaredDynamic," +
                      (declaredDynamicFriction < 0f ? "not declared" : declaredDynamicFriction.ToString("F4")) +
                      ",mismatch," + (DeclaredMismatch ? "YES" : "no") +
                      ",note,a mismatch means at least one live pair did NOT resolve to the declared " +
                      "coefficient. Whether that invalidates the run is not decided here");

        return lines;
    }

    /// <summary>One compact line for a screen readout.</summary>
    public string ShortLine()
    {
        if (!resolved) return "Friction: not resolved";

        var sb = new StringBuilder(128);
        sb.Append("Friction gov=").Append(GoverningCombine);
        for (int i = 0; i < pairs.Count; i++)
        {
            sb.Append("  ").Append(pairs[i].A).Append('x').Append(pairs[i].B).Append(' ')
              .Append(pairs[i].StaticFriction.ToString("F2")).Append('/')
              .Append(pairs[i].DynamicFriction.ToString("F2")).Append(" r")
              .Append(pairs[i].Ratio.ToString("F2"));
        }

        if (CombineOverridden) sb.Append("   [COMBINE OVERRIDDEN]");
        if (AnyCollapsed) sb.Append("   [NO DEPINNING]");
        if (DeclaredMismatch) sb.Append("   [DECLARED MISMATCH]");
        return sb.ToString();
    }
}
