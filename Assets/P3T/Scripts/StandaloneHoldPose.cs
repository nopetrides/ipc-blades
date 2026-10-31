using UnityEngine;

/// <summary>
///     Parks a standalone dummy in ONE authored phase of its station's clip set and leaves it there.
/// </summary>
/// <remarks>
///     <para>
///         These dummies are swing targets, not stations. They have no SwordRef pair, no station camera
///         and no video plane, so they are never registered against the physical trial and the occlusion
///         probe cannot discover them. Nothing measured comes off them; they exist so a sword can be
///         picked up and swung against a body that is holding a blade the way an actor held it.
///     </para>
///     <para>
///         <b>The pose is not authored here.</b> The dummy runs the SAME
///         <c>AnimatorOverrideController</c> as its station's B side, so what it holds IS that station's
///         own authored Hold clip, not a second copy that could drift away from it. Re-author the
///         station's clip and the target follows. This component only decides WHICH phase to sit in.
///     </para>
///     <para>
///         <c>Auto</c> is switched off so the sequential Snap -> Hold -> Contact -> Drive transitions
///         never fire; only the AnyState transition into the declared phase does. Without this the
///         controller's own defaults (<c>Auto = true</c>, <c>State = 0</c>) would run the whole sequence
///         and the target would end up driving.
///     </para>
///     <para>
///         <b>The SNAP -> HOLD routing rule does not apply here.</b> That rule exists because two blades
///         driven straight to Hold from the rest pose reach it on the wrong side of each other. A
///         standalone target carries one blade and has nothing to cross, so it goes straight to its phase.
///     </para>
/// </remarks>
[RequireComponent(typeof(Animator))]
[DisallowMultipleComponent]
public sealed class StandaloneHoldPose : MonoBehaviour
{
    /// <summary>The four authored phases, matching the <c>State</c> values of <c>Motions_Controller</c>.</summary>
    public enum Phase
    {
        Snap = 1,
        Hold = 2,
        Contact = 3,
        Drive = 4
    }

    private static readonly int AutoId = Animator.StringToHash("Auto");
    private static readonly int StateId = Animator.StringToHash("State");

    [Tooltip("Which authored phase this target holds. Hold is the frame the physical action begins from, " +
             "and is the only one that reproduces a reference configuration; the others are available " +
             "for inspection.")]
    [SerializeField] private Phase phase = Phase.Hold;

    /// <summary>The phase this target is parked in.</summary>
    public Phase HeldPhase => phase;

    private void Start()
    {
        var animator = GetComponent<Animator>();

        if (animator.runtimeAnimatorController == null)
        {
            Debug.LogWarning(
                $"[StandaloneHoldPose] {name}: no animator controller, so there is no authored pose to " +
                "hold. The dummy will hang in its rest pose.", this);
            return;
        }

        animator.SetBool(AutoId, false);
        animator.SetInteger(StateId, (int)phase);
    }
}
