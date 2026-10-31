using UnityEngine;

/// <summary>
///     Anything that wants to watch a station run without being the thing that runs it.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why this boundary exists.</b> Running and recording were one component, and that is what
///         makes station-specific logic creep in: a component that both drives the protocol and knows
///         what to record ends up knowing which station it is on. Split, the runner knows the protocol
///         and nothing else, and an observer knows what it measures and nothing about the walk.
///     </para>
///     <para>
///         <b>Neither side may contain station-specific logic.</b> Everything that differs between the
///         ten stations — which collider to probe, which markers carry e+, the declared valid reference,
///         which hulls are live — is a serialized declaration on the instrument that needs it, never a
///         branch in the runner or in a recorder. If a change here needs to ask "which station is this",
///         the answer belongs on a probe.
///     </para>
///     <para>
///         An abstract MonoBehaviour rather than an interface, so a runner can hold observers in a
///         serialized list and the wiring is visible in the inspector instead of found at runtime.
///     </para>
/// </remarks>
public abstract class RqRunObserver : MonoBehaviour
{
    /// <summary>
    ///     True when this observer's own instruments are ready and a run may be armed.
    /// </summary>
    /// <remarks>
    ///     This is what "arm when ready" reads. Readiness is each observer's own business — a probe
    ///     cannot resolve until Ragdoll Animator has generated the sword proxies, and arming before that
    ///     produces a run whose first phases were measured by an instrument that did not yet exist.
    /// </remarks>
    public abstract bool ReadyToArm { get; }

    /// <summary>Why not, in words, for the log while a run is waiting. Never a silent false.</summary>
    public abstract string ReadinessReason { get; }

    /// <summary>The run is starting. The observer should clear whatever it accumulates.</summary>
    public abstract void OnRunBegin(RqStationRunner runner);

    /// <summary>
    ///     A phase has just been commanded. <paramref name="state" /> is the animator's State value:
    ///     0 Pause, 1 SNAP, 2 HOLD, 3 Contact, 4 Drive.
    /// </summary>
    /// <remarks>
    ///     Called AFTER the runner has written the parameter, so an observer reading the animator sees
    ///     the phase it is being told about. An observer that needs a reading taken BEFORE the phase
    ///     takes effect must take it here — one fixed step later is a different instant.
    /// </remarks>
    public abstract void OnPhase(int state, int step);

    /// <summary>Every fixed step of the run, after the walk has been serviced.</summary>
    public abstract void OnStep(int step);

    /// <summary>
    ///     The run is over. <paramref name="reason" /> says why, and is written into whatever is sealed.
    /// </summary>
    public abstract void OnRunEnd(string reason);

    /// <summary>
    ///     True when this observer has determined the run must not continue.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is how a validity gate stops a run without the runner knowing what a gate is. The
    ///         runner polls it after every <see cref="OnPhase" /> and every <see cref="OnStep" /> and
    ///         ends the run if any observer says so.
    ///     </para>
    ///     <para>
    ///         A veto is a RESULT, not an error. The observer that raised it is responsible for saying
    ///         so in its own output; the runner only stops.
    ///     </para>
    /// </remarks>
    public virtual bool VetoRun => false;

    /// <summary>Why the run was vetoed. Read by the runner for its log only.</summary>
    public virtual string VetoReason => "";
}
