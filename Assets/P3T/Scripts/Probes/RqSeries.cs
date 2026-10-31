using System.Collections.Generic;
using System.Globalization;

/// <summary>
///     One observable sampled every fixed step of a window, with its own summary statistics.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why the recorder needs this at all.</b> Most of the primary observables were sealed as a
///         single END-OF-WINDOW value: the migration at the last step, the rotation gain at the last
///         step. That value cannot distinguish a contact that ran steadily down the blade from one that
///         ran out and came back, and it cannot say whether a gain of 0.9 was held throughout or reached
///         once at the end. The distribution over the window is a different fact from its final value,
///         and both are wanted.
///     </para>
///     <para>
///         <b>Computed here, in code, before the seal.</b> Not derived afterwards from a timeline by
///         hand or by eye. Every figure in a sealed file is produced by this class from the samples it
///         was given, so the file states what it measured rather than inviting a reader to recompute it
///         and get a slightly different answer.
///     </para>
///     <para>
///         <b>Every statistic is emitted, including ones that will often agree.</b> First and last, min
///         and max, mean and median: on a monotonic series several of these coincide, and that
///         coincidence is itself the finding — it says the quantity moved one way and did not come back.
///         Suppressing a column because it is expected to duplicate another would remove the evidence
///         for exactly that.
///     </para>
///     <para>
///         <b>Each sample carries its step and whether the blades were touching.</b> Added 2026-08-29.
///         A bare list of values can only produce ONE mean, over everything it was given, and the
///         REPEATABILITY3 set showed two separate ways that mean can describe nothing real:
///     </para>
///     <list type="bullet">
///         <item>
///             <b>Span.</b> On a run whose contact side inverts, the blades end up on the wrong sides of
///             each other. A sword cannot pass through a sword, so every step after that is contact in a
///             configuration the apparatus cannot produce. A mean spanning the inversion averages real
///             opposed contact together with that. Thirteen of the fifty runs invert, and on three
///             stations the inversion covers 76 to 91 per cent of the window.
///         </item>
///         <item>
///             <b>Denominator.</b> The contact recorder ZEROES normal force, off-normal force and added
///             tangential force on any step where nothing is touching. Averaging those zeros in dilutes
///             the mean in exact proportion to time out of contact — measured at
///             <c>mean_over_all_steps / mean_over_touching_steps == touching_steps / total_steps</c> to
///             within 3e-4 across all fifty runs. On <c>Station_Flat_QuickDirty</c>, which touches on
///             only 83 of 234 steps, that understates the force by a factor of 2.8.
///         </item>
///     </list>
///     <para>
///         The two are independent, so a series is summarised over all FOUR combinations and the reader
///         chooses. Retaining the step and the touching flag alongside the value is what makes that a
///         filter over one sampling rather than four samplings that could disagree.
///     </para>
///     <para>
///         Samples are retained rather than folded into running moments, because the median needs them
///         and a window is a few hundred steps. Nothing here is on a hot path that a list allocation
///         would trouble.
///     </para>
/// </remarks>
public sealed class RqSeries
{
    /// <summary>Which steps of the window a summary covers.</summary>
    public enum Span
    {
        /// <summary>Every sampled step of the drive window.</summary>
        FullDriveWindow = 0,

        /// <summary>
        ///     Only steps on which the contact side was NOT inverted. Identical to
        ///     <see cref="FullDriveWindow" /> on a run that never inverts.
        ///     <para>
        ///         This MASKS the inverted steps rather than truncating at the first one, and the two are
        ///         not the same operation. Truncation is right for a state that never reverses; masking is
        ///         right for one that does, and this set contains both. A sustained crossing never
        ///         reverses - its inverted span always runs to the window's last step - so masking gives
        ///         exactly what truncating would. An excursion reverses within one or two steps, and
        ///         truncating at it would discard every valid step after: measured on one
        ///         Station_Flat_PhysX run, truncating at a single reversing step at 195 cost 39 of 234
        ///         steps on a run where contact and opposed force never broke.
        ///     </para>
        /// </summary>
        ContactDriveWindow = 1
    }

    /// <summary>Which of the covered steps actually contribute a sample.</summary>
    public enum Denominator
    {
        /// <summary>
        ///     Every step in the span. A step where nothing was touching contributes whatever the
        ///     recorder wrote for it, which for the force series is an explicit zero.
        /// </summary>
        AllSteps = 0,

        /// <summary>
        ///     Only steps where the pair was delivering contact. For a quantity that does not exist
        ///     while the blades are apart, this is the mean of the quantity; <see cref="AllSteps" /> is
        ///     the mean of the quantity multiplied by the duty cycle.
        /// </summary>
        TouchingOnly = 1
    }

    /// <summary>One sample, with the context needed to filter it afterwards.</summary>
    private struct Sample
    {
        public float Value;

        /// <summary>In-window step, zero at the drive transition.</summary>
        public int Step;

        /// <summary>Whether the pair was delivering contact on this step.</summary>
        public bool Touching;

        /// <summary>Whether the contact side was inverted on this step, against the declared reference.</summary>
        public bool Inverted;
    }

    /// <summary>The column stem, used as the <c>series</c> value of every row it contributes.</summary>
    public readonly string Name;

    /// <summary>The unit, for the header only. Never converted.</summary>
    public readonly string Unit;

    private readonly List<Sample> samples = new List<Sample>(512);
    private readonly List<float> scratch = new List<float>(512);

    public RqSeries(string name, string unit)
    {
        Name = name;
        Unit = unit;
    }

    /// <summary>
    ///     The in-window step the NEXT <see cref="Add" /> belongs to. Set once per step by the recorder that
    ///     owns these series, immediately before it samples them all.
    ///     <para>
    ///         Static because every series is fed from a single per-step block, so one assignment gives all of
    ///         them the true step. It is deliberately the STEP and not the sample ordinal: series that skip
    ///         steps — the patch family contributes no sample on a step that delivered no contact points —
    ///         would otherwise report an ordinal that does not correspond to any step, which is exactly the
    ///         index-is-not-a-step trap the trail writer already carries a warning about.
    ///     </para>
    /// </summary>
    public static int SampleStep = -1;

    /// <summary>
    ///     Whether the pair was delivering contact on the step the NEXT <see cref="Add" /> belongs to. Set
    ///     from the same per-step block as <see cref="SampleStep" />, for the same reason.
    /// </summary>
    public static bool SampleTouching;

    /// <summary>
    ///     Whether the contact side was inverted on the step the NEXT <see cref="Add" /> belongs to. Set
    ///     from the same per-step block as <see cref="SampleStep" />, for the same reason.
    /// </summary>
    public static bool SampleInverted;

    /// <summary>How many samples were taken in total, before any filtering.</summary>
    public int Count => samples.Count;

    /// <summary>
    ///     The retained samples, for the per-step file. Exposed so the per-step rows and the summary
    ///     statistics come from ONE sampling: a second component reading the probes on its own schedule
    ///     could sample at a different point in the physics step and disagree with the aggregates that
    ///     carry the same name.
    /// </summary>
    public void SampleAt(int i, out float value, out int step, out bool touching, out bool inverted)
    {
        Sample s = samples[i];
        value = s.Value;
        step = s.Step;
        touching = s.Touching;
        inverted = s.Inverted;
    }

    /// <summary>Takes one sample. Non-finite values are rejected rather than poisoning every statistic.</summary>
    public void Add(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value)) return;

        samples.Add(new Sample
        {
            Value = value, Step = SampleStep, Touching = SampleTouching, Inverted = SampleInverted
        });
    }

    /// <summary>Clears everything, for the start of a new window.</summary>
    public void Reset()
    {
        samples.Clear();
        scratch.Clear();
    }

    /// <summary>The statistics of one filtered view. All eleven, computed together from one pass.</summary>
    public struct View
    {
        public int N;
        public float First, Last, Min, Max, Mean, Median, Sd, Range, Sum;

        /// <summary>In-window step at which <see cref="Max" /> was reached. -1 when the view is empty.</summary>
        public int ArgMaxStep;
    }

    /// <summary>
    ///     Summarises the samples that fall in <paramref name="span" /> under <paramref name="den" />.
    /// </summary>
    /// <param name="span">Which steps of the window to cover.</param>
    /// <param name="den">Which of the covered steps contribute.</param>
    /// <remarks>
    ///     Every statistic is recomputed for the view rather than adjusted from the full-window one. In
    ///     particular <see cref="View.ArgMaxStep" /> must be: the step at which a bounded span reached ITS
    ///     maximum is not in general the step the whole window did, and carrying the latter over would
    ///     locate a maximum outside the span it claims to describe.
    /// </remarks>
    public View Compute(Span span, Denominator den)
    {
        var v = new View { ArgMaxStep = -1 };

        bool maskInverted = span == Span.ContactDriveWindow;
        bool touchingOnly = den == Denominator.TouchingOnly;

        double sum = 0d;
        float min = float.MaxValue, max = float.MinValue;
        bool have = false;

        scratch.Clear();

        for (int i = 0; i < samples.Count; i++)
        {
            Sample s = samples[i];

            if (maskInverted && s.Inverted) continue;
            if (touchingOnly && !s.Touching) continue;

            if (!have)
            {
                v.First = s.Value;
                have = true;
            }

            v.Last = s.Value;

            sum += s.Value;
            scratch.Add(s.Value);

            if (s.Value < min) min = s.Value;

            // First occurrence wins: on a plateau this locates the step the maximum was REACHED, not the
            // last step it happened to persist for. Strictly greater-than is what makes that true.
            if (s.Value > max)
            {
                max = s.Value;
                v.ArgMaxStep = s.Step;
            }
        }

        v.N = scratch.Count;
        if (v.N == 0) return v;

        v.Min = min;
        v.Max = max;
        v.Range = max - min;
        v.Sum = (float)sum;
        v.Mean = (float)(sum / v.N);

        scratch.Sort();
        v.Median = (v.N & 1) == 1
            ? scratch[v.N / 2]
            : (scratch[v.N / 2 - 1] + scratch[v.N / 2]) * 0.5f;

        // Sample standard deviation, Bessel-corrected. A DISPERSION figure for one window, not a
        // repeatability band. This rig's run-to-run scatter is a separate and unestablished quantity,
        // and this must never be substituted for it.
        if (v.N >= 2)
        {
            double mean = sum / v.N;
            double acc = 0d;
            for (int i = 0; i < scratch.Count; i++)
            {
                double d = scratch[i] - mean;
                acc += d * d;
            }

            v.Sd = (float)System.Math.Sqrt(acc / (v.N - 1));
        }

        return v;
    }

    /// <summary>The column names of the long-format series file, in the order <see cref="Row" /> emits them.</summary>
    public static string Header()
    {
        return "series,unit,span,denominator,n,first,last,min,max,mean,median,sd,range,sum,argmax_step";
    }

    /// <summary>
    ///     One row: this series under one span and one denominator. Span and denominator are VALUES
    ///     rather than column-name prefixes, so a reader filters on them instead of parsing them out of
    ///     a name — which is the failure that let <c>mean_normal_n</c> be read as a sample count for as
    ///     long as it was.
    /// </summary>
    public string Row(string format, Span span, Denominator den)
    {
        View v = Compute(span, den);
        var ic = CultureInfo.InvariantCulture;

        return Name + "," + Unit + "," +
               (span == Span.FullDriveWindow ? "full_drive_window" : "contact_drive_window") + "," +
               (den == Denominator.AllSteps ? "all_steps" : "touching_only") + "," +
               v.N.ToString(ic) + "," +
               v.First.ToString(format) + "," + v.Last.ToString(format) + "," +
               v.Min.ToString(format) + "," + v.Max.ToString(format) + "," +
               v.Mean.ToString(format) + "," + v.Median.ToString(format) + "," +
               v.Sd.ToString(format) + "," + v.Range.ToString(format) + "," + v.Sum.ToString(format) + "," +
               v.ArgMaxStep.ToString(ic);
    }
}
