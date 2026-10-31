using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

/// <summary>
///     Reports blade-blade contact from the <b>PhysX contact predicate itself</b> — whether the solver generated
///     a contact between these two swords this step — together with the contact point, its migration and the
///     accumulated impulse.
///     <para>
///         <b>Why this exists.</b> Contact was previously inferred from a closest-pair distance threshold
///         ("gap ≤ 5 mm"). That is not contact, it is proximity, and it produced a false state change: against an
///         authored gap of 6.15 mm a 4 mm settle flipped the reported state FALSE → TRUE without anything about
///         the mechanics having been measured. The authored blade gap is visual headroom deliberately left to
///         avoid interpenetration and is <b>not</b> a measured physical separation, so no distance derived from it
///         can define contact. Ask PhysX instead.
///     </para>
///     <para>
///         <b>Read-only.</b> This adds no force, no constraint and no contact modification. In particular it is
///         NOT <c>BladeContactStabilizer</c> - DELETED 2026-08-21, it was disabled everywhere and never ran; enabling it would
///         change a declared setting.
///     </para>
///     <para>
///         A consequence worth stating plainly: two <i>kinematic</i> actors generate no contacts in PhysX at all.
///         While both swords are held kinematic through ARM-1 this observer will correctly report no contact, no
///         matter how close the blades are. Contact is only physically meaningful once both swords are dynamic.
///     </para>
/// </summary>
[DisallowMultipleComponent]
public class BladeContactObserver : MonoBehaviour
{
    [Tooltip("The opposing sword's root (the transform carrying its Rigidbody). Contacts against anything else " +
             "are ignored, so guard-vs-scenery or marker-vs-floor cannot masquerade as a bind.")]
    public Transform otherSword;

    private readonly List<ContactPoint> _buf = new List<ContactPoint>();

    /// <summary>True if PhysX generated a blade-blade contact on the most recent physics step.</summary>
    public bool InContact { get; private set; }

    /// <summary>Consecutive fixed steps with contact. This is the SUSTAINED-contact criterion.</summary>
    public int SustainedSteps { get; private set; }

    /// <summary>Fixed steps since contact was last seen; 0 while in contact.</summary>
    public int StepsSinceContact { get; private set; }

    /// <summary>Impulse-weighted mean contact point, world space, from the most recent contact step.</summary>
    public Vector3 ContactPoint { get; private set; }

    /// <summary>Contact point recorded the first time contact was established since the last <see cref="ResetObserver" />.</summary>
    public Vector3 FirstContactPoint { get; private set; }

    public bool HasEverContacted { get; private set; }

    /// <summary>Magnitude of the collision impulse on the most recent contact step, N·s.</summary>
    public float ImpulseNs { get; private set; }

    public int ContactCount { get; private set; }

    /// <summary>
    ///     Distance the contact point has migrated from where contact was first established, mm.
    ///     <b>Migration is recorded, not scored.</b> Contact location changes the mechanical geometry so it must be
    ///     tracked, but movement of the contact while pressure is being established is not by itself a failure and
    ///     is a different quantity from sword pose displacement.
    /// </summary>
    public float MigrationMm
    {
        get { return HasEverContacted ? Vector3.Distance(ContactPoint, FirstContactPoint) * 1000f : 0f; }
    }

    private bool _contactThisStep;
    private Vector3 _pointThisStep;
    private float _impulseThisStep;
    private int _countThisStep;

    public void ResetObserver()
    {
        InContact = false;
        SustainedSteps = 0;
        StepsSinceContact = 0;
        HasEverContacted = false;
        ContactPoint = Vector3.zero;
        FirstContactPoint = Vector3.zero;
        ImpulseNs = 0f;
        ContactCount = 0;
        _contactThisStep = false;
    }

    private bool IsOther(Collision c)
    {
        if (otherSword == null || c.rigidbody == null) return false;
        return c.rigidbody.transform == otherSword || c.rigidbody.transform.IsChildOf(otherSword);
    }

    private void Accumulate(Collision c)
    {
        if (!IsOther(c)) return;
        _buf.Clear();
        var n = c.GetContacts(_buf);
        if (n <= 0) return;
        var p = Vector3.zero;
        for (var i = 0; i < n; i++) p += _buf[i].point;
        _pointThisStep = p / n;
        _countThisStep = n;
        _impulseThisStep = c.impulse.magnitude;
        _contactThisStep = true;
    }

    private void OnCollisionEnter(Collision c)
    {
        Accumulate(c);
    }

    private void OnCollisionStay(Collision c)
    {
        Accumulate(c);
    }

    // Collision callbacks run before FixedUpdate's tail, so the per-step flag is consumed here and cleared for
    // the next step. LateFixed-style bookkeeping in FixedUpdate would read the PREVIOUS step's flag.
    private void FixedUpdate()
    {
        if (_contactThisStep)
        {
            InContact = true;
            SustainedSteps++;
            StepsSinceContact = 0;
            ContactPoint = _pointThisStep;
            ImpulseNs = _impulseThisStep;
            ContactCount = _countThisStep;
            if (!HasEverContacted)
            {
                HasEverContacted = true;
                FirstContactPoint = _pointThisStep;
            }
        }
        else
        {
            InContact = false;
            SustainedSteps = 0;
            StepsSinceContact++;
        }

        _contactThisStep = false;
    }

    public string Report()
    {
        return string.Format(CultureInfo.InvariantCulture,
            "contact={0} sustained={1} steps  points={2}  impulse={3:F4} Ns  point {4}  migration {5:F2} mm",
            InContact, SustainedSteps, ContactCount, ImpulseNs, ContactPoint.ToString("F5"), MigrationMm);
    }
}
