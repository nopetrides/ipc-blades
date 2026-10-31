using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;

/// <summary>
///     Minimal manual pose-authoring aid: drag the wrist and elbow, and be told immediately when the pose asks
///     the servo rig for something it cannot do.
///     <para>
///         <b>Editor-only.</b> Nothing is added to the scene and no runtime component is required by a finished
///         station — this is an <see cref="EditorWindow" /> that draws into the Scene view while it is open.
///     </para>
///     <para>
///         <b>It authors nothing on its own.</b> There are no correction arrows, no nearest-legal-pose
///         suggestions, no automatic redistribution and no clamping. When a joint goes illegal it says so and
///         names the responsible DOF; moving back is the author's decision, because several different legal
///         corrections usually exist and only a human can judge which best preserves the visual reconstruction.
///     </para>
///     <para>
///         <b>Why the gizmo can be trusted.</b> Envelopes and verdicts come from
///         <see cref="ActiveArmRagdoll.DeriveChainSpecs" /> and <see cref="JointLimitEvaluator" /> — the same
///         derivation <c>Build()</c> consumes and the same evaluator the runtime audit uses. The gizmo therefore
///         shows the joint that will actually be created, not an editor approximation of it.
///     </para>
/// </summary>
public class PoseAuthoringWindow : EditorWindow
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    /// <summary>
    ///     <b>Serialized on purpose.</b> A plain private field is wiped by every domain reload — i.e. every script
    ///     recompile — and the window then sat at "No rig selected" with no way back: <c>OnSelectionChange</c>
    ///     only fires when the selection <i>changes</i>, so re-clicking the same dummy did nothing, and
    ///     "Re-derive specs" bailed out immediately because it never consulted the selection. The gizmos simply
    ///     stopped appearing and the window looked broken. <c>EditorWindow</c> serializes this across reloads.
    /// </summary>
    [SerializeField] private ActiveArmRagdoll _rig;

    /// <summary>Latches a failed <see cref="Derive" /> so the automatic recovery in <see cref="EnsureReady" />
    /// cannot retry it every repaint. Cleared whenever the rig changes or the author asks for a re-derive.</summary>
    private bool _deriveFailed;

    private JointLimitSpec[] _specs;
    private Quaternion[] _zero;
    private HumanBodyBones[] _bones;
    private Transform[] _tf;
    private float _deriveErrorDeg;
    private string _deriveMsg = "No rig selected.";

    // Authoring handles. The wrist target is an authoring control, NOT a measurement, and is deliberately never
    // snapped to the sword root, grip centre, grip axis or any palm proxy: the grip is an extended volume and
    // none of those is a measured grasp point.
    private Vector3 _wristPos;
    private Quaternion _wristRot;

    /// <summary>
    ///     Elbow SWIVEL about the shoulder→wrist axis, degrees. With the wrist fixed, the elbow's only freedom is
    ///     to rotate around that axis on a circle of solutions — one angular DOF, not three. A free 3D pole
    ///     handle exposes it badly: most drag directions do nothing, and the marker never lands on the elbow.
    /// </summary>
    private float _elbowSwivelDeg;

    private Vector3 _elbowHint; // derived from the swivel; kept for display only
    private bool _handlesInitialised;

    // ---------------------------------------------------------------------------------------------------
    // Two-slot authoring — PLAN_ArmContactTarget.md [removed - see git history] Step 3 · DECISION_ArmContactTarget.md [removed - see git history] §7
    //
    // ONE control. Switching saves the displayed pose into the current slot, loads the other slot EXACTLY,
    // and reinitialises the existing wrist/reach and elbow helpers. There is no Capture / Apply / Preview /
    // Commit workflow, and loading a slot never runs IK — a stored pose means its stored rotations.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>Which complete pose the author is editing. The toggle changes only this.</summary>
    public enum PoseSlot
    {
        Snap,
        ArmContact
    }

    [SerializeField] private PoseSlot _slot = PoseSlot.Snap;

    /// <summary>
    ///     The window's working copies, one per slot, in <see cref="_bones" /> order.
    ///     <para>
    ///         ⚠️ <b>The SNAP slot is a working copy, NOT the physical joint reference.</b> Switching slots must
    ///         never write <c>_jointZero</c> — accepting a new SNAP is a separate, deliberate act, and a pose
    ///         toggle that quietly redefined the joint reference would move the ground every measurement stands
    ///         on. Viewing ARM-CONTACT therefore cannot disturb the accepted Flat SNAP.
    ///     </para>
    ///     Serialized so a recompile does not discard authoring in progress, for the same reason
    ///     <see cref="_rig" /> is.
    /// </summary>
    [SerializeField] private Quaternion[] _slotSnap;

    [SerializeField] private Quaternion[] _slotArmContact;

    /// <summary>
    ///     Whether the ARM-CONTACT <b>workspace</b> has been initialised yet.
    ///     <para>
    ///         ⚠️ <b>This is an editor-workspace flag and nothing more.</b> It is deliberately not
    ///         <c>ActiveArmRagdoll.ArmContactAuthored</c> and emphatically not approval: the first switch to
    ///         ARM-CONTACT starts from an exact copy of SNAP so the author is not editing identity rotations or
    ///         garbage, and <b>a copy of SNAP is not an authored contact pose</b>. Nothing here touches
    ///         the deleted controller's <c>armContactQuarantined</c>, so opening the slot cannot authorise runtime
    ///         ARM-CONTACT.
    ///     </para>
    /// </summary>
    [SerializeField] private bool _armContactWorkspaceSeeded;

    /// <summary>
    ///     Which rig the two slots currently belong to.
    ///     <para>
    ///         ⚠️ <b>Without this the slots leak between dummies.</b> <see cref="EnsureSlots" /> re-seeds only on a
    ///         null or wrong-length array, and every dummy has the same 7 joints — so selecting a different rig
    ///         kept the previous rig's poses, and the next switch to ARM-CONTACT would have written one dummy's
    ///         pose onto another. Measured on Flat: the window sat on Dummy_B_Flat holding a SNAP slot that
    ///         matched Dummy_A_Flat to 0.000° and was 58.221° from B.
    ///     </para>
    /// </summary>
    [SerializeField] private ActiveArmRagdoll _slotsRig;

    [Tooltip("Locked-axis residual above which a joint is called INVALID.")]
    private float _lockedToleranceDeg = 1.0f;

    private float _envelopeRadius = 0.12f;
    private bool _drawEnvelopes = true;

    /// <summary>Which joint to draw named DOF axes for. All 7 at once is unreadable; the elbow is the default
    /// because its hinge (Forearm Stretch) is the axis an author most needs to line up.</summary>
    private int _axisFocus = 5;

    private bool _drawDofAxes = true;

    // Wrist twist has no home at the hand: Unity's humanoid locks the hand's third axis because that rotation is
    // forearm pronation. With this off, the wrist rotation handle is effectively unusable - any roll lands on a
    // locked axis and the joint reads INVALID wherever you put it.
    private bool _routeTwistToForearm = true;
    private float _appliedForearmTwistDeg;

    /// <summary>Twist the wrist demanded that the forearm could not legally absorb. Non-zero means the requested
    /// hand orientation is NOT reachable by this chain — a kinematic shortfall, not a tuning problem.</summary>
    private float _unroutableTwistDeg;

    /// <summary>
    ///     Three states, deliberately not two.
    ///     <para>
    ///         The editor's fixed-axis model was originally allowed to fail a pose outright. It cannot: the
    ///         settled runtime joint is the oracle, and this approximation has already condemned poses Unity
    ///         considers perfectly legal. So an analytical representability warning is its own state - something
    ///         the editor cannot certify either way and which the runtime must arbitrate - rather than a verdict.
    ///     </para>
    /// </summary>
    private enum State
    {
        /// <summary>No known issue.</summary>
        Clear,

        /// <summary>Muscle-space legal, but the fixed-axis model predicts a representability problem it cannot
        /// certify. Requires live-servo confirmation; NOT an authoring failure.</summary>
        RuntimeCheck,

        /// <summary>A known violation: outside Unity's humanoid limit or outside the declared clearance.</summary>
        Invalid
    }

    // ---- Humanoid muscle space: THE AUTHORITY on whether an authored pose is legal ------------------------
    //
    // Unity's humanoid DOFs are NOT fixed orthogonal axes, so the fixed-axis ConfigurableJoint decomposition can
    // attribute a large rotation to a joint's LOCKED axis for a pose that is perfectly representable in muscle
    // space and anatomically unremarkable. Measured on Dummy_B_Flat: Right Hand In-Out -0.200 and Down-Up 0.172,
    // i.e. the hand barely displaced, while the fixed-axis evaluator reported 88.48 deg of locked residual.
    //
    // So legality is judged HERE, against the same muscle values HumanoidLimitAudit uses - which also means the
    // gizmo and the formal audit cannot disagree. The fixed-axis reading is retained, but demoted to a separate
    // REPRESENTABILITY warning about the servo rig, not a verdict on the author's pose.
    private HumanPoseHandler _hph;
    private HumanPose _hp;

    private struct MuscleReading
    {
        public int Index;
        public string Name;
        public float Value;
    }

    private void RefreshMuscles()
    {
        if (_rig == null) return;
        var anim = _rig.GetComponent<Animator>();
        if (anim == null || anim.avatar == null || !anim.isHuman) return;
        if (_hph == null) _hph = new HumanPoseHandler(anim.avatar, anim.transform);
        _hph.GetHumanPose(ref _hp);
    }

    private List<MuscleReading> MusclesOf(HumanBodyBones bone)
    {
        var list = new List<MuscleReading>();
        if (_hp.muscles == null) return list;
        for (var d = 0; d < 3; d++)
        {
            var mi = HumanTrait.MuscleFromBone((int) bone, d);
            if (mi < 0 || mi >= _hp.muscles.Length) continue;
            list.Add(new MuscleReading {Index = mi, Name = HumanTrait.MuscleName[mi], Value = _hp.muscles[mi]});
        }

        return list;
    }

    /// <summary>Legality in the authoring parameterisation. |v| &gt; 1 is outside Unity's own limit.</summary>
    private State MuscleState(HumanBodyBones bone, out string detail)
    {
        detail = "";
        var st = State.Clear;
        var parts = new List<string>();
        foreach (var m in MusclesOf(bone))
        {
            var a = Mathf.Abs(m.Value);
            if (a > 1f)
            {
                st = State.Invalid;
                parts.Add(string.Format(Ci, "{0} v={1:F3} OUTSIDE Unity's limit", m.Name, m.Value));
            }
            else if (a > JointLimitPolicy.DeclaredClearance)
            {
                // Exceeding the declared clearance is a violation of policy, not a near-miss.
                st = State.Invalid;
                parts.Add(string.Format(Ci, "{0} v={1:F3} past the {2:F2} declared envelope", m.Name, m.Value,
                    JointLimitPolicy.DeclaredClearance));
            }
        }

        detail = string.Join(" · ", parts.ToArray());
        return st;
    }

    [MenuItem("Tools/P3T/Pose authoring (gizmos)", false, 200)]
    private static void Open()
    {
        GetWindow<PoseAuthoringWindow>("Pose Authoring").Show();
    }

    private void OnEnable()
    {
        SceneView.duringSceneGui += OnScene;
        EnsureReady();
    }

    private void OnDisable()
    {
        SceneView.duringSceneGui -= OnScene;
    }

    private void OnSelectionChange()
    {
        TryAdoptSelection();
        Repaint();
    }

    /// <summary>Adopting the rig again when the window regains focus — the cheapest place to notice that a
    /// recompile happened while the author was looking elsewhere.</summary>
    private void OnFocus()
    {
        EnsureReady();
    }

    /// <summary>
    ///     Get back to a usable state <b>regardless of the order things happened in</b>: select-then-open,
    ///     open-then-select, or a script recompile in the middle of either.
    ///     <para>
    ///         Three separate ways the window used to end up blank, all of which this covers: (1) the rig field
    ///         wiped by a domain reload; (2) the window opened before anything was selected, so
    ///         <c>OnEnable</c>'s adopt found nothing and <c>OnSelectionChange</c> never fired because the
    ///         selection did not subsequently change; (3) <c>_rig</c> surviving but <c>_specs</c> not, since the
    ///         derived specs are not serializable.
    ///     </para>
    ///     <para>
    ///         <see cref="Derive" /> briefly perturbs the humanoid pose to measure the DOF axes, so it must not be
    ///         run speculatively on every repaint: it is called only when the specs are actually missing, and a
    ///         failure is latched so a rig that cannot derive does not retry forever.
    ///     </para>
    /// </summary>
    private void EnsureReady()
    {
        if (_rig == null) TryAdoptSelection();
        if (_rig != null && _specs == null && !_deriveFailed) Derive();
    }

    private void TryAdoptSelection()
    {
        var go = Selection.activeGameObject;
        if (go == null) return;
        var rig = go.GetComponentInParent<ActiveArmRagdoll>();
        if (rig == null || rig == _rig) return;
        _rig = rig;
        _deriveFailed = false;
        Derive();
    }

    /// <summary>
    ///     Re-derive the chain specs. Done on demand, not per repaint: the derivation briefly perturbs the
    ///     humanoid pose to MEASURE the DOF axes, and doing that continuously would fight the author. The specs
    ///     depend on the avatar rather than the pose, so caching them is correct.
    /// </summary>
    private void Derive()
    {
        _specs = null;
        _handlesInitialised = false;

        // REBUILD THE POSE HANDLER. It is bound to a specific avatar and root transform, so a handler cached
        // from a previously selected rig keeps reporting THAT rig's muscle values. Selecting another dummy then
        // showed one rig's legality in the panel while the Scene-view gizmos evaluated the other rig's bones -
        // Dummy_A_Flat's shoulder reads 1.709 (INVALID) and Dummy_B_Flat's reads 0.221 (CLEAR), so the two
        // disagreed exactly as observed.
        _hph = null;
        _hp = new HumanPose();

        if (_rig == null)
        {
            _deriveMsg = "No rig selected. Click a dummy (or any child of one) in the Hierarchy.";
            return;
        }

        // Snapshot and restore the authored pose around the measurement, so authoring can never be disturbed by
        // the act of inspecting it.
        //
        // POSITIONS MUST BE RESTORED TOO. DeriveChainSpecs measures the DOF axes through CaptureJointSpace,
        // which drives the humanoid via SetHumanPose - and that writes bone POSITIONS as well as rotations,
        // leaving float noise (~1e-6) behind on every bone it touches. Restoring rotations alone let that noise
        // accumulate into the scene as prefab overrides on ~49 bones, and displaced one Hips by 5.1 m.
        var all = _rig.GetComponentsInChildren<Transform>(true);
        var savedRot = new Quaternion[all.Length];
        var savedPos = new Vector3[all.Length];
        for (var i = 0; i < all.Length; i++)
        {
            savedRot[i] = all[i].localRotation;
            savedPos[i] = all[i].localPosition;
        }

        var ok = _rig.DeriveChainSpecs(out _specs, out _zero, out _bones, out _deriveErrorDeg);

        for (var i = 0; i < all.Length; i++)
        {
            all[i].localRotation = savedRot[i];
            all[i].localPosition = savedPos[i];
        }

        if (!ok)
        {
            _specs = null;
            _deriveFailed = true; // stop EnsureReady retrying this every repaint
            _deriveMsg = "Could not derive: the rig needs a humanoid Animator and the full bone chain.";
            return;
        }

        _deriveFailed = false;

        _tf = new Transform[_bones.Length];
        var anim = _rig.GetComponent<Animator>();
        for (var i = 0; i < _bones.Length; i++) _tf[i] = anim.GetBoneTransform(_bones[i]);
        _deriveMsg = "Derived " + _specs.Length + " joint specs (pose-restore error " +
                     _deriveErrorDeg.ToString("F6", Ci) + " deg).";
    }

    /// <summary>Read the displayed pose off the actuated bones — the complete set, in <see cref="_bones" /> order.</summary>
    private Quaternion[] ReadDisplayedPose()
    {
        if (_tf == null) return null;
        var q = new Quaternion[_tf.Length];
        for (var i = 0; i < _tf.Length; i++) q[i] = _tf[i] != null ? _tf[i].localRotation : Quaternion.identity;
        return q;
    }

    /// <summary>
    ///     Write a stored pose onto the actuated bones <b>exactly</b>. No IK, no projection, no canonicalisation —
    ///     loading a stored pose means loading its stored rotations, or the slot would not round-trip.
    /// </summary>
    private void WriteDisplayedPose(Quaternion[] q)
    {
        if (_tf == null || q == null || q.Length != _tf.Length) return;
        Undo.RecordObjects(_tf, "Load authoring pose");
        for (var i = 0; i < _tf.Length; i++)
            if (_tf[i] != null)
                _tf[i].localRotation = q[i];
    }

    /// <summary>
    ///     The one operation behind the toggle: save the displayed pose into the current slot, load the selected
    ///     slot, reinitialise the existing helpers from it.
    /// </summary>
    private void SwitchSlot(PoseSlot target)
    {
        if (_tf == null || _bones == null) return;
        EnsureSlots();

        // 1. save what is on screen into the slot being left
        var displayed = ReadDisplayedPose();
        if (_slot == PoseSlot.Snap) _slotSnap = displayed;
        else _slotArmContact = displayed;

        // 2. load the selected slot exactly
        _slot = target;
        WriteDisplayedPose(_slot == PoseSlot.Snap ? _slotSnap : _slotArmContact);

        // 3. reinitialise wrist/reach + elbow helpers from the newly loaded pose
        InitHandles();
        SceneView.RepaintAll();
    }

    /// <summary>
    ///     Make sure both slots exist and match the chain. SNAP seeds from the accepted joint reference;
    ///     ARM-CONTACT seeds from the rig's stored target if it has one, otherwise from an exact copy of SNAP.
    ///     <b>Read-only with respect to the rig</b> — this never writes <c>_jointZero</c>.
    /// </summary>
    private void EnsureSlots()
    {
        if (_rig == null || _bones == null) return;
        var n = _bones.Length;

        // The slots belong to ONE rig. Selecting a different dummy must not inherit the previous dummy's poses —
        // every chain is 7 joints, so a length check cannot catch it. Dropping them here re-seeds below from the
        // rig now selected, and covers every entry point (object field, selection change, adopt) at once.
        if (_slotsRig != _rig)
        {
            _slotsRig = _rig;
            _slotSnap = null;
            _slotArmContact = null;
            _armContactWorkspaceSeeded = false;
            _slot = PoseSlot.Snap; // never leave a foreign ARM-CONTACT slot selected on a freshly chosen rig
        }

        if (_slotSnap == null || _slotSnap.Length != n)
        {
            var zero = _rig.JointZeroLocalRotations;
            _slotSnap = zero != null && zero.Length == n ? (Quaternion[]) zero.Clone() : ReadDisplayedPose();
        }

        if (_slotArmContact != null && _slotArmContact.Length == n) return;
        var stored = _rig.ArmContactTargets;
        if (stored != null && stored.Length == n)
        {
            _slotArmContact = (Quaternion[]) stored.Clone();
        }
        else
        {
            // First use: begin from an exact copy of SNAP so the author is not editing garbage. This is a
            // STARTING CANDIDATE, not an approved target, and it authorises nothing.
            _slotArmContact = (Quaternion[]) _slotSnap.Clone();
            _armContactWorkspaceSeeded = true;
        }
    }

    private void InitHandles()
    {
        if (_tf == null || _tf.Length < 7) return;
        var hand = _tf[6];
        var elbow = _tf[5];
        var upper = _tf[4];
        if (hand == null || elbow == null || upper == null) return;
        _wristPos = hand.position;
        _wristRot = hand.rotation;

        // Recover the current swivel so the handle starts ON the elbow instead of somewhere else.
        Vector3 centre, dir, refPerp;
        float radius;
        if (ElbowCircle(upper.position, _wristPos, upper, elbow, hand, out centre, out dir, out radius, out refPerp))
        {
            var v = Vector3.ProjectOnPlane(elbow.position - centre, dir);
            _elbowSwivelDeg = v.sqrMagnitude > 1e-10f ? Vector3.SignedAngle(refPerp, v.normalized, dir) : 0f;
        }

        _elbowHint = elbow.position;
        _handlesInitialised = true;
    }

    /// <summary>
    ///     The circle of elbow solutions for a fixed shoulder and wrist: centre, axis (shoulder→wrist), radius,
    ///     and a stable in-plane reference direction the swivel angle is measured from.
    /// </summary>
    private static bool ElbowCircle(Vector3 a, Vector3 target, Transform upper, Transform lower, Transform hand,
        out Vector3 centre, out Vector3 dir, out float radius, out Vector3 refPerp)
    {
        centre = a;
        dir = Vector3.forward;
        radius = 0f;
        refPerp = Vector3.up;
        var l1 = Vector3.Distance(a, lower.position);
        var l2 = Vector3.Distance(lower.position, hand.position);
        if (l1 < 1e-5f || l2 < 1e-5f) return false;
        var to = target - a;
        var dist = Mathf.Clamp(to.magnitude, Mathf.Abs(l1 - l2) + 1e-4f, l1 + l2 - 1e-4f);
        if (to.sqrMagnitude < 1e-10f) return false;
        dir = to.normalized;
        var along = (l1 * l1 + dist * dist - l2 * l2) / (2f * dist);
        radius = Mathf.Sqrt(Mathf.Max(0f, l1 * l1 - along * along));
        centre = a + dir * along;
        refPerp = Vector3.ProjectOnPlane(Vector3.up, dir);
        if (refPerp.sqrMagnitude < 1e-8f) refPerp = Vector3.ProjectOnPlane(Vector3.right, dir);
        refPerp.Normalize();
        return true;
    }

    // ---- evaluation -------------------------------------------------------------------------------------

    /// <summary>
    ///     The joint's angular zero — <b>the physical joint reference, never muscle-zero</b>.
    ///     <para>
    ///         Passing <c>_zero</c> (muscle-zero) here is what made the window call the frozen Flat SNAP INVALID:
    ///         it measured the residual from Unity's rest pose while decomposing it on the physical joint's axes,
    ///         which is neither a clean Avatar statement nor a servo one. Under <c>AuthoredSnap</c> the reference
    ///         is the accepted SNAP, so the authored pose is representable <i>by construction</i>.
    ///     </para>
    ///     <para>
    ///         <c>_jointZero</c> only exists once <c>Build()</c> has run, so in edit mode the reference is the
    ///         SNAP slot — which is the same pose the articulation will be built around.
    ///     </para>
    /// </summary>
    private Quaternion JointReference(int i)
    {
        // Authoring SNAP: the candidate IS the reference. Build() adopts whatever is authored here, so the
        // accepted pose is representable by definition and there is no representability question to ask.
        if (_slot == PoseSlot.Snap) return _tf[i].localRotation;

        // Authoring ARM-CONTACT: the reference is SNAP, so the residual is exactly the part of the contact
        // target this joint cannot hold. Prefer the built _jointZero; in edit mode it does not exist yet, and
        // the SNAP slot is the same pose the articulation will be built around.
        var jz = _rig != null ? _rig.JointZeroLocalRotations : null;
        if (jz != null && _bones != null && jz.Length == _bones.Length) return jz[i];
        if (_slotSnap != null && _bones != null && _slotSnap.Length == _bones.Length) return _slotSnap[i];
        return _tf[i].localRotation;
    }

    private JointLimitEvaluation Eval(int i)
    {
        return JointLimitEvaluator.Evaluate(_specs[i], JointReference(i), _tf[i].localRotation, 0.75f);
    }

    /// <summary>
    ///     What the author can actually do about a locked-axis violation on this joint, in movement vocabulary.
    ///     <b>Guidance only — nothing here redistributes rotation automatically.</b> The author decides how the
    ///     rest of the pose changes; only <see cref="JointLimitEvaluator.ClampJointTarget" /> edits anything, on
    ///     this joint alone, and only when pressed.
    /// </summary>
    private static void LockedAxisHelp(HumanBodyBones bone, out string what, out string[] tryThese)
    {
        switch (bone)
        {
            case HumanBodyBones.RightShoulder:
                what = "This pose asks the shoulder for rotation about an axis the physical shoulder joint does " +
                       "not provide.";
                tryThese = new[] {"Right Upper Arm rotation", "or the shoulder rotation itself"};
                return;
            case HumanBodyBones.RightLowerArm:
                what = "The elbow target requires sideways motion the physical elbow joint does not provide.";
                tryThese = new[] {"elbow swivel", "wrist/reach target", "upper-arm orientation"};
                return;
            case HumanBodyBones.RightHand:
                what = "The hand target contains roll that belongs in the forearm-side authoring motion.";
                tryThese = new[] {"Forearm Twist / the existing route-twist-to-forearm path", "or reduce the hand roll"};
                return;
            default:
                what = "This pose asks the joint for rotation about an axis it does not provide.";
                tryThese = new[] {"the parent joint's orientation", "or reduce the rotation on this joint"};
                return;
        }
    }

    private State StateOf(JointLimitEvaluation e)
    {
        if (e.Engaged || e.OutsideDeclaredEnvelope || e.LockedResidualDeg > _lockedToleranceDeg)
            return State.Invalid;
        // "legal, but approaching the declared boundary"
        if (e.SwingEnvelopeOccupancy > 0.9f || e.TwistClearanceDeg < 5f) return State.RuntimeCheck;
        return State.Clear;
    }

    /// <summary>Which DOF is responsible, with the number that made it responsible. This is the part that makes
    /// "the elbow looks fine but the wrist twist is what went illegal" visible without another audit cycle.</summary>
    private string Blame(JointLimitEvaluation e, JointLimitSpec s)
    {
        var parts = new List<string>();
        if (e.LockedResidualDeg > _lockedToleranceDeg)
            parts.Add(string.Format(Ci, "LOCKED {0} residual {1:F2} deg", s.YLocked ? "Y" : "Z",
                e.LockedResidualDeg));
        if (e.TwistClearanceDeg < 0f)
            parts.Add(string.Format(Ci, "X twist {0:F1} deg outside [{1:F0}, {2:F0}]", e.TwistDeg, s.TwistLow,
                s.TwistHigh));
        else if (e.TwistClearanceDeg < 5f)
            parts.Add(string.Format(Ci, "X twist {0:F1} deg, only {1:F1} deg clearance", e.TwistDeg,
                e.TwistClearanceDeg));
        if (e.SwingEnvelopeOccupancy > 1f)
            parts.Add(string.Format(Ci, "swing occupancy {0:F3} > declared {1:F2} (Y {2:F1} deg, Z {3:F1} deg)",
                e.SwingOccupancy, JointLimitPolicy.DeclaredClearance, e.SwingYDeg, e.SwingZDeg));
        else if (e.SwingEnvelopeOccupancy > 0.9f)
            parts.Add(string.Format(Ci, "swing occupancy {0:F3}, near declared {1:F2}", e.SwingOccupancy,
                JointLimitPolicy.DeclaredClearance));
        return parts.Count == 0 ? "" : string.Join(" · ", parts.ToArray());
    }

    // ---- window ------------------------------------------------------------------------------------------

    private void OnGUI()
    {
        EditorGUILayout.HelpBox(
            "Manual authoring aid. It reports; it never repairs. Envelopes and verdicts come from the same " +
            "derivation Build() consumes and the same evaluator the runtime audit uses.", MessageType.None);

        // Self-heal before drawing, so the click ORDER never matters: select-then-open, open-then-select, or a
        // recompile part-way through either.
        EnsureReady();

        EditorGUI.BeginChangeCheck();
        _rig = (ActiveArmRagdoll) EditorGUILayout.ObjectField("Rig", _rig, typeof(ActiveArmRagdoll), true);
        if (EditorGUI.EndChangeCheck())
        {
            _deriveFailed = false;
            Derive();
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            // "Re-derive specs" used to bail out with "No rig selected" whenever the field was empty, which is
            // exactly when an author reaches for it. It now adopts the current selection first.
            if (GUILayout.Button("Re-derive specs"))
            {
                _deriveFailed = false;
                if (_rig == null) TryAdoptSelection();
                if (_rig != null) Derive();
            }

            if (GUILayout.Button("Reset handles to pose")) InitHandles();
        }

        // ---- the one Step 3 control ----
        // Deliberately a single toggle. No Capture / Apply / Preview / Commit: switching IS the save-and-load,
        // and storing or restoring are consequences of it rather than things the author performs.
        if (_rig != null && _tf != null && _bones != null)
        {
            EnsureSlots();
            EditorGUI.BeginChangeCheck();
            var picked = (PoseSlot) EditorGUILayout.EnumPopup(
                new GUIContent("Authoring Pose",
                    "Which complete pose you are editing. Switching saves the displayed pose into the current " +
                    "slot, loads the other slot exactly, and re-seeds the wrist and elbow helpers. It never " +
                    "runs IK and never rewrites the physical joint reference."), _slot);
            if (EditorGUI.EndChangeCheck() && picked != _slot)
            {
                Undo.RegisterCompleteObjectUndo(this, "Switch authoring pose");
                SwitchSlot(picked);
            }

            if (_slot == PoseSlot.ArmContact)
            {
                EditorGUILayout.HelpBox(
                    _armContactWorkspaceSeeded && !_rig.ArmContactAuthored
                        ? "ARM-CONTACT workspace started as an exact copy of SNAP so you are not authoring from " +
                          "identity rotations. A copy of SNAP is NOT an authored contact pose, and editing here " +
                          "does not lift the runtime ARM-CONTACT quarantine."
                        : "Editing the ARM-CONTACT pose. This does not lift the runtime ARM-CONTACT quarantine.",
                    MessageType.Info);

                // Build() takes `authored[i] = bones[i].localRotation` and, under AuthoredSnap, makes that the
                // joint reference. So entering Play with ARM-CONTACT on screen would silently redefine SNAP as
                // the contact pose — the exact drift this window is supposed to make impossible.
                // The ONE commissioning action. Deliberately not a Save/Commit/Approve workflow: it hands the
                // displayed candidate to runtime-only storage for this Play session and authorises execution.
                // It does not approve, freeze or serialize anything.
                using (new EditorGUI.DisabledScope(!Application.isPlaying))
                {
                    if (GUILayout.Button(new GUIContent("Test ARM-CONTACT Candidate",
                            "Runtime only. Copies the displayed ARM-CONTACT candidate into non-serialized " +
                            "commissioning storage and authorises THIS Play session to attempt ARM-CONTACT. It " +
                            "does NOT approve or freeze the target, does not change armContactQuarantined on " +
                            "disk, and does not survive Play-mode exit.")))
                    {
                        var displayed = ReadDisplayedPose();
                        string why;
                        if (!_rig.LoadCommissioningTarget(displayed, out why))
                            Debug.LogError("[PoseAuthoring] Commissioning target REJECTED on " + _rig.name +
                                           " — " + why, _rig);
                        else
                            Debug.LogWarning("[PoseAuthoring] COMMISSIONING candidate loaded on " + _rig.name +
                                             " for THIS PLAY SESSION ONLY.\nThis is NOT approval: " +
                                             "armContactQuarantined is unchanged on disk, no target was " +
                                             "serialized, and nothing here is experimental/frozen data. It is " +
                                             "destroyed on Play-mode exit.", _rig);
                    }
                }

                if (!Application.isPlaying)
                    EditorGUILayout.LabelField("   (commissioning is available in Play Mode)",
                        EditorStyles.miniLabel);

                EditorGUILayout.HelpBox(
                    "⛔ Switch back to SNAP before entering Play Mode.\n" +
                    "Build() adopts whatever pose is displayed as the physical joint reference, so playing while " +
                    "ARM-CONTACT is on screen would redefine SNAP as the contact pose — silently, and without " +
                    "touching this window's stored slots.", MessageType.Warning);
            }
        }

        // Numeric elbow swivel. The Scene disc is fine for finding the pose; this is for setting it precisely
        // and for reading back what you actually chose.
        if (_tf != null && _tf.Length >= 7 && _handlesInitialised)
        {
            EditorGUI.BeginChangeCheck();
            var s = EditorGUILayout.Slider(
                new GUIContent("Elbow swivel (deg)",
                    "Rotates the elbow around the shoulder->wrist axis. With the wrist fixed this is the elbow's " +
                    "ONLY freedom - the wire circle in the Scene view is every position it can reach."),
                _elbowSwivelDeg, -180f, 180f);
            if (EditorGUI.EndChangeCheck() && _tf[4] != null && _tf[5] != null && _tf[6] != null)
            {
                Undo.RecordObjects(new Object[] {_tf[4], _tf[5], _tf[6]}, "Elbow swivel");
                _elbowSwivelDeg = s;
                Vector3 c, ax, rp;
                float rad;
                if (ElbowCircle(_tf[4].position, _wristPos, _tf[4], _tf[5], _tf[6], out c, out ax, out rad, out rp))
                {
                    _elbowHint = c + Quaternion.AngleAxis(_elbowSwivelDeg, ax) * rp * rad;
                    SolveTwoBone(_tf[4], _tf[5], _tf[6], _wristPos, _elbowHint);
                    if (_routeTwistToForearm) _appliedForearmTwistDeg = RouteTwistToForearm(_tf[5], _tf[6], _wristRot);
                    _tf[6].rotation = _wristRot;
                    SceneView.RepaintAll();
                }
            }
        }

        _routeTwistToForearm = EditorGUILayout.Toggle(
            new GUIContent("Route twist to forearm",
                "The hand joint has NO twist DOF - Unity locks it, because wrist twist is forearm pronation. " +
                "With this off, any roll you ask of the wrist lands on a locked axis and the hand reads INVALID " +
                "wherever you put it. This is a fixed kinematic routing rule, not an optimiser."),
            _routeTwistToForearm);
        if (_routeTwistToForearm && Mathf.Abs(_appliedForearmTwistDeg) > 1e-4f)
            EditorGUILayout.LabelField(" ", string.Format(Ci, "last drag routed {0:F2} deg into forearm pronation",
                _appliedForearmTwistDeg), EditorStyles.miniLabel);
        if (_routeTwistToForearm && Mathf.Abs(_unroutableTwistDeg) > 0.5f)
            EditorGUILayout.HelpBox(string.Format(Ci,
                "{0:F1} deg of the twist you asked for CANNOT be absorbed: the forearm is at its declared " +
                "pronation envelope (+/-{1:F1} deg of its +/-{2:F0} deg range). The hand cannot hold it either — " +
                "that axis is Locked. This wrist orientation is not reachable by this chain, so it is a " +
                "kinematic shortfall rather than something to tune. Rotate the shoulder or upper arm to bring " +
                "the forearm's pronation back into range, or accept a different wrist presentation.",
                _unroutableTwistDeg, _specs != null && _specs.Length > 5
                    ? _specs[5].YLimit * JointLimitPolicy.DeclaredClearance : 0f,
                _specs != null && _specs.Length > 5 ? _specs[5].YLimit : 0f), MessageType.Warning);

        _drawEnvelopes = EditorGUILayout.Toggle("Draw envelopes", _drawEnvelopes);
        _drawDofAxes = EditorGUILayout.Toggle(
            new GUIContent("Name DOF axes",
                "Draws and NAMES each joint axis with its Unity humanoid DOF. The joint's X is not 'twist' in " +
                "the anatomical sense - on the forearm it is Forearm Stretch, the elbow hinge."), _drawDofAxes);
        if (_drawDofAxes && _bones != null)
        {
            var names = new string[_bones.Length];
            for (var k = 0; k < _bones.Length; k++) names[k] = _bones[k].ToString();
            _axisFocus = EditorGUILayout.Popup("   axes for", Mathf.Clamp(_axisFocus, 0, names.Length - 1), names);
            if (_specs != null && _axisFocus < _specs.Length)
            {
                var s = _specs[_axisFocus];
                EditorGUI.indentLevel++;
                EditorGUILayout.LabelField("X (red)", s.DofName(0) + (s.TwistLimited
                    ? string.Format(Ci, "   [{0:F0}, {1:F0}]", s.TwistLow, s.TwistHigh) : ""),
                    EditorStyles.miniLabel);
                EditorGUILayout.LabelField("Y (green)", s.DofName(1) + (s.YLocked
                    ? "" : string.Format(Ci, "   +/-{0:F0}", s.YLimit)), EditorStyles.miniLabel);
                EditorGUILayout.LabelField("Z (blue)", s.DofName(2) + (s.ZLocked
                    ? "" : string.Format(Ci, "   +/-{0:F0}", s.ZLimit)), EditorStyles.miniLabel);
                EditorGUI.indentLevel--;
            }
        }
        _envelopeRadius = EditorGUILayout.Slider("Envelope size", _envelopeRadius, 0.02f, 0.4f);
        _lockedToleranceDeg = EditorGUILayout.Slider("Locked residual tol (deg)", _lockedToleranceDeg, 0.1f, 10f);
        EditorGUILayout.LabelField(_deriveMsg, EditorStyles.wordWrappedMiniLabel);

        if (_specs == null || _tf == null)
        {
            EditorGUILayout.HelpBox(
                _rig == null
                    ? "No rig yet — click a dummy in the Hierarchy (any child of it works too, e.g. a bone or a " +
                      "pose handle). The window picks it up automatically; the order in which you select it and " +
                      "open this window does not matter."
                    : "Rig is set but its joint specs are missing — press \"Re-derive specs\". Specs are not " +
                      "serializable, so they are lost on every script recompile and rebuilt on demand.",
                _rig == null ? MessageType.Info : MessageType.Warning);
            return;
        }

        EditorGUILayout.Space();
        RefreshMuscles();
        var worst = State.Clear;
        var problems = new List<string>();
        var unrepresentable = new List<string>();
        for (var i = 0; i < _specs.Length; i++)
        {
            if (_tf[i] == null) continue;
            var e = Eval(i);

            // ONE QUESTION: can the declared physical rig hold this target? Unity's opinion of the pose is not
            // asked. The Mecanim legality layer used to arbitrate here and produced a verdict that was neither a
            // clean Avatar statement nor a servo one; it is gone from this path. Only LOCKED axes constrain,
            // because referenceTestFreeDofs builds the intended DOFs Free — the spec's Humanoid ranges are stated
            // about muscle-zero and do not describe the joint being built.
            var st = e.LockedResidualDeg > _lockedToleranceDeg ? State.Invalid : State.Clear;
            if (st > worst) worst = st;

            var prev = GUI.color;
            GUI.color = st == State.Invalid ? new Color(1f, 0.45f, 0.45f) : new Color(0.6f, 1f, 0.6f);
            EditorGUILayout.LabelField(_bones[i].ToString(),
                st == State.Invalid ? "CANNOT HOLD THIS TARGET" : "can hold this target");
            GUI.color = prev;

            if (st != State.Invalid) continue;

            string what;
            string[] tryThese;
            LockedAxisHelp(_bones[i], out what, out tryThese);
            EditorGUI.indentLevel++;
            EditorGUILayout.LabelField(what, EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.LabelField("Try adjusting:", EditorStyles.miniBoldLabel);
            foreach (var s in tryThese) EditorGUILayout.LabelField("   • " + s, EditorStyles.wordWrappedMiniLabel);

            // Convenience only, and scoped to ONE joint. It removes exactly the rotation this joint cannot
            // command and leaves every other joint alone — the author still decides how the rest of the pose
            // changes. ProjectToFeasible stays dead; this is not it (see ClampJointTarget).
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(16f);
                if (GUILayout.Button(new GUIContent("Clamp this joint",
                        "Remove only the rotation this joint physically cannot command. Changes this joint and " +
                        "nothing else. A target that is already commandable is returned untouched."),
                    GUILayout.Width(130f)))
                {
                    float removed;
                    var clamped = JointLimitEvaluator.ClampJointTarget(_specs[i], JointReference(i),
                        _tf[i].localRotation, out removed);
                    if (removed > 0f)
                    {
                        Undo.RecordObject(_tf[i], "Clamp " + _bones[i] + " to what it can command");
                        _tf[i].localRotation = clamped;
                        InitHandles();
                        SceneView.RepaintAll();
                    }
                }

                EditorGUILayout.LabelField(string.Format(Ci, "Technical: locked-axis residual {0:F2} deg",
                    e.LockedResidualDeg), EditorStyles.miniLabel);
            }

            EditorGUI.indentLevel--;
            problems.Add(_bones[i] + ": cannot hold this target (locked-axis residual " +
                         e.LockedResidualDeg.ToString("F2", Ci) + " deg)");
            unrepresentable.Add(_bones[i] + ": " + what);
        }

        EditorGUILayout.Space();
        if (worst == State.Invalid)
            EditorGUILayout.HelpBox("NOT READY FOR AUDIT — pose is outside Unity's humanoid limits\n\n" +
                                    string.Join("\n", problems.ToArray()), MessageType.Error);
        else if (unrepresentable.Count > 0)
            EditorGUILayout.HelpBox(
                "READY FOR RUNTIME REPRESENTABILITY CHECK\n\n" +
                "This is NOT a pass and NOT a failure. Muscle-space legality is clean, so the pose is legal and " +
                "the authoring work is done. What remains is a prediction from the editor's fixed-axis model, " +
                "which cannot certify itself either way - the settled runtime joint is the oracle. Command this " +
                "pose through the live servos WITHOUT canonical repair and let that arbitrate.\n\n" +
                "Every muscle is inside Unity's limits, so the pose itself is fine and this is NOT something to " +
                "fix by moving the hand. What follows is a property of the RIG.\n\n" +
                string.Join("\n", unrepresentable.ToArray()) +
                "\n\nAt the HAND this is almost always FOREARM TWIST: Unity distributes forearm twist across the " +
                "forearm AND the hand bone, and this chain locks the hand's twist axis, so the share Unity puts " +
                "on the hand cannot be represented. Measured: sweeping the forearm-twist muscle alone drives the " +
                "hand's locked residual from 62.5 deg to 0.47 deg with the hand's own muscles untouched " +
                "(FINDING_ForearmTwistDistribution). Reducing forearm pronation will reduce this; it is not a " +
                "wrist-posing error.\n\n" +
                "Do NOT re-pose to chase it, do NOT change the Avatar, and do NOT unlock the wrist twist on the " +
                "strength of this number alone.", MessageType.Warning);
        else
            EditorGUILayout.HelpBox(
                "READY FOR AUDIT" +
                "\n\nThis only means the authoring aid sees no outstanding problem. It is NOT a verified pose: " +
                "bake through StationPoseIO, run the Humanoid and ConfigurableJoint audits, then confirm the " +
                "live servos reproduce it without materially changing the pose.", MessageType.Info);
    }

    // ---- scene view --------------------------------------------------------------------------------------

    private void OnScene(SceneView sv)
    {
        // Recover here too: the author may be working entirely in the Scene view with this window open but never
        // focused, in which case OnGUI's recovery would not have run and the gizmos would stay missing.
        EnsureReady();
        if (_rig == null || _specs == null || _tf == null) return;
        if (!_handlesInitialised) InitHandles();
        if (!_handlesInitialised) return;

        DrawHandles();
        // DrawGripFrames() is GONE. The manually authored GripFrame_Sword it drew has been retired: it was a
        // second, hand-placed definition of "where the grasp is", competing with the real attachment. The
        // Hand<->Sword relationship is now shown by HandSwordConnectionVisualizer, which derives its endpoints
        // from HandSwordConnection - the same type that creates the joint.
        if (_drawEnvelopes) DrawEnvelopes();
        if (_drawDofAxes) DrawNamedDofAxes();
        Repaint();
    }

    private void DrawHandles()
    {
        var upper = _tf[4];
        var lower = _tf[5];
        var hand = _tf[6];
        if (upper == null || lower == null || hand == null) return;

        EditorGUI.BeginChangeCheck();
        var newWrist = Handles.PositionHandle(_wristPos, _wristRot);
        var newRot = Handles.RotationHandle(_wristRot, _wristPos);

        // ELBOW SWIVEL. The circle is the complete set of elbow positions reachable with this shoulder and
        // wrist, so it shows exactly what the control can and cannot do. The handle rotates about the
        // shoulder->wrist axis, which is the single DOF actually available.
        var newSwivel = _elbowSwivelDeg;
        Vector3 centre, axis, refPerp;
        float radius;
        if (ElbowCircle(upper.position, _wristPos, upper, lower, hand, out centre, out axis, out radius,
                out refPerp))
        {
            _elbowHint = centre + Quaternion.AngleAxis(_elbowSwivelDeg, axis) * refPerp * radius;

            Handles.color = new Color(1f, 1f, 1f, 0.25f);
            Handles.DrawLine(upper.position, _wristPos);          // the swivel axis itself
            Handles.color = new Color(0.4f, 0.8f, 1f, 0.55f);
            Handles.DrawWireDisc(centre, axis, radius);           // every elbow position available
            Handles.color = new Color(0.4f, 0.8f, 1f);
            Handles.DrawLine(upper.position, _elbowHint);
            Handles.DrawLine(_elbowHint, _wristPos);
            Handles.SphereHandleCap(0, _elbowHint, Quaternion.identity,
                HandleUtility.GetHandleSize(_elbowHint) * 0.06f, EventType.Repaint);
            Handles.Label(_elbowHint, string.Format(Ci, "  elbow swivel {0:F1} deg", _elbowSwivelDeg));

            // ---- OUTSIDE / INSIDE ELBOW ------------------------------------------------------------------
            // A swivel angle is the mechanism, not the intent. What an author is placing is the bony point of
            // the elbow (olecranon) and the crease it folds into. So: work out which way the elbow actually
            // folds, mark both sides, and let the OUTSIDE marker be dragged directly - the arm follows.
            Vector3 inside, outside;
            if (ElbowFoldDirections(upper, lower, hand, out inside, out outside))
            {
                var mlen = HandleUtility.GetHandleSize(_elbowHint) * 0.35f;

                // The plane the forearm sweeps as the elbow folds, so the fold is visible rather than inferred.
                Handles.color = new Color(1f, 0.8f, 0.3f, 0.20f);
                var hinge = HingeAxisWorld(lower);
                Handles.DrawSolidArc(lower.position, hinge, (hand.position - lower.position).normalized, -60f,
                    Vector3.Distance(lower.position, hand.position) * 0.55f);

                Handles.color = new Color(0.6f, 0.85f, 1f);
                Handles.DrawLine(_elbowHint, _elbowHint + inside * mlen);
                Handles.Label(_elbowHint + inside * mlen, "  INSIDE (folds here)");

                Handles.color = new Color(1f, 0.55f, 0.2f);
                var outPos = _elbowHint + outside * mlen;
                Handles.DrawLine(_elbowHint, outPos);
                Handles.Label(outPos, "  OUTSIDE (bony point) - drag me");

                var moved = Handles.FreeMoveHandle(outPos, HandleUtility.GetHandleSize(outPos) * 0.09f,
                    Vector3.zero, Handles.SphereHandleCap);
                if ((moved - outPos).sqrMagnitude > 1e-12f)
                {
                    // Swing the whole arm about the shoulder->wrist axis until the bony point faces where it
                    // was dragged. Only the component around that axis is achievable, so the rest is ignored
                    // rather than approximated.
                    var cur = Vector3.ProjectOnPlane(outside, axis);
                    var want = Vector3.ProjectOnPlane(moved - _elbowHint, axis);
                    if (cur.sqrMagnitude > 1e-10f && want.sqrMagnitude > 1e-10f)
                        newSwivel = _elbowSwivelDeg +
                                    Vector3.SignedAngle(cur.normalized, want.normalized, axis);
                }
            }
        }

        if (!EditorGUI.EndChangeCheck()) return;
        Undo.RecordObjects(new Object[] {upper, lower, hand}, "Author arm pose");
        _wristPos = newWrist;
        _wristRot = newRot;
        _elbowSwivelDeg = Mathf.Repeat(newSwivel + 180f, 360f) - 180f;
        if (ElbowCircle(upper.position, _wristPos, upper, lower, hand, out centre, out axis, out radius,
                out refPerp))
            _elbowHint = centre + Quaternion.AngleAxis(_elbowSwivelDeg, axis) * refPerp * radius;
        SolveTwoBone(upper, lower, hand, _wristPos, _elbowHint);

        // ROUTE TWIST TO THE FOREARM. The hand joint has NO twist DOF - Unity's humanoid gives Right_Hand only
        // Down-Up and In-Out, and locks the third axis, because wrist twist is forearm pronation in both Unity's
        // rig and in anatomy (Right_LowerArm carries Twist In-Out +/-90). Writing all three DOF to the hand makes
        // every rotation illegal on the locked axis, so the wrist handle would be unusable without this.
        //
        // This is NOT an optimiser-style redistribution: there is no search, no objective and no choice. It is a
        // fixed kinematic routing rule - the DOF is applied to the joint that actually owns it. It is visible in
        // the panel and can be switched off.
        if (_routeTwistToForearm) _appliedForearmTwistDeg = RouteTwistToForearm(lower, hand, _wristRot);
        hand.rotation = _wristRot;

        // NO PROJECTION HERE. `ProjectChainToJointSpace` used to run on all 7 bones after every drag, and it is
        // gone (Noah, 2026-08-08): the mechanical implementation must not edit the reconstruction to make itself
        // happy. The authored pose is now the physical joint REFERENCE configuration - the articulation is built
        // around whatever the author put here - so there is nothing left for a projection to repair.
        //
        // It was also measurably wrong. `ProjectToFeasible` is not the identity on an already-CLEAR joint:
        // measured on Flat it would have moved RightUpperArm 43.9 deg (A) and 72.7 deg (B) while that joint
        // reported locked residual 0.000, not engaged, inside the envelope. RightUpperArm is the only link with
        // both swing axes free, and the projection rebuilds swing as AngleAxis(Y)*AngleAxis(Z) from y and z
        // components extracted independently, which does not invert its own extraction. Its "would move"
        // distances are therefore untrustworthy for multi-axis swing, so it is not a read-only diagnostic here
        // either. The function still exists in JointLimits.cs, uncalled, pending an independent fix.
    }

    /// <summary>
    ///     Pre-rotate the forearm about its own declared twist axis so the hand does not have to express twist it
    ///     has no DOF for. Returns the twist applied, degrees. Rotating about the forearm's long axis does not
    ///     move the wrist, so the authored wrist position is preserved.
    /// </summary>
    private float RouteTwistToForearm(Transform lower, Transform hand, Quaternion desiredHandWorld)
    {
        if (_specs == null || _tf == null || _tf.Length < 7 || lower == null || hand == null) return 0f;

        // Pick the forearm DOF that actually carries pronation by ALIGNMENT, not by index. The joint's X is not
        // the anatomical twist: for RightLowerArm, X is Forearm Stretch (+/-80) and Y is Forearm Twist In-Out
        // (+/-90). Rotating about X would bend the elbow instead of pronating - which is exactly the bug this
        // replaces. The right axis is whichever FREE forearm DOF best aligns with the axis the HAND has locked,
        // since that locked axis is the rotation the hand cannot hold.
        var handParent = hand.parent != null ? hand.parent.rotation : Quaternion.identity;
        var handJoint = handParent * _zero[6] * _specs[6].Basis;
        var lockedAxis = handJoint * (_specs[6].YLocked ? Vector3.up : Vector3.forward);
        if (!_specs[6].YLocked && !_specs[6].ZLocked) return 0f; // nothing is locked; nothing to route

        var parentRot = lower.parent != null ? lower.parent.rotation : Quaternion.identity;
        var lowerJoint = parentRot * _zero[5] * _specs[5].Basis;
        var axis = Vector3.zero;
        var best = 0f;
        // X is always Limited; Y and Z only when not locked.
        var cands = new[] {Vector3.right, _specs[5].YLocked ? Vector3.zero : Vector3.up,
            _specs[5].ZLocked ? Vector3.zero : Vector3.forward};
        foreach (var c in cands)
        {
            if (c == Vector3.zero) continue;
            var w = (lowerJoint * c).normalized;
            var d = Mathf.Abs(Vector3.Dot(w, lockedAxis.normalized));
            if (d <= best) continue;
            best = d;
            axis = w;
        }

        if (axis.sqrMagnitude < 1e-8f || best < 0.5f) return 0f; // no forearm DOF meaningfully carries it

        // How much of the requested change in hand orientation is rotation ABOUT that axis?
        var delta = desiredHandWorld * Quaternion.Inverse(hand.rotation);
        // Swing-twist decomposition of delta about `axis`.
        var r = new Vector3(delta.x, delta.y, delta.z);
        var proj = Vector3.Dot(r, axis) * axis;
        var twist = new Quaternion(proj.x, proj.y, proj.z, delta.w);
        if (twist.x * twist.x + twist.y * twist.y + twist.z * twist.z + twist.w * twist.w < 1e-12f) return 0f;
        twist.Normalize();

        float angle;
        Vector3 twAxis;
        twist.ToAngleAxis(out angle, out twAxis);
        if (angle > 180f) angle -= 360f;
        if (Vector3.Dot(twAxis, axis) < 0f) angle = -angle;
        if (Mathf.Abs(angle) < 1e-4f) return 0f;

        // ONLY route what the forearm can legally hold. Forearm pronation sits on joint Y, which the evaluator
        // counts in the SWING ellipse - and the forearm's Z is Locked, so that ellipse is degenerate and
        // occupancy is simply |swingY| / YLimit. The declared envelope is therefore +/-YLimit*clearance, and
        // asking for more just moves the violation from the hand to the forearm. Clamp, and report the shortfall
        // instead of silently creating a new invalid joint.
        var saved = lower.localRotation;
        var applied = angle;
        if (!ForearmLegalAt(lower, axis, angle))
        {
            float lo = 0f, hi = angle;
            for (var k = 0; k < 14; k++)
            {
                var mid = (lo + hi) * 0.5f;
                if (ForearmLegalAt(lower, axis, mid)) lo = mid;
                else hi = mid;
            }

            applied = lo;
        }

        lower.localRotation = saved;
        _unroutableTwistDeg = angle - applied;
        if (Mathf.Abs(applied) < 1e-4f) return 0f;
        lower.rotation = Quaternion.AngleAxis(applied, axis) * lower.rotation;
        return applied;
    }

    /// <summary>Would the forearm still be inside its declared envelope after this much rotation about the axis?</summary>
    private bool ForearmLegalAt(Transform lower, Vector3 axis, float deg)
    {
        var saved = lower.localRotation;
        lower.rotation = Quaternion.AngleAxis(deg, axis) * lower.rotation;
        var e = JointLimitEvaluator.Evaluate(_specs[5], _zero[5], lower.localRotation, 0.75f);
        lower.localRotation = saved;
        return !e.Engaged && !e.OutsideDeclaredEnvelope && e.LockedResidualDeg <= _lockedToleranceDeg;
    }

    /// <summary>
    ///     Plain analytic two-bone IK over UpperArm → LowerArm → Hand, with the elbow hint as the pole.
    ///     <para>
    ///         Deliberately drives ONLY the arm. Shoulder, UpperChest, Chest and Spine are left alone: if the arm
    ///         cannot be made both visually correct and legal without them, the author should move the proximal
    ///         reconstruction deliberately rather than have IK quietly redistribute error into the torso.
    ///     </para>
    /// </summary>
    private static void SolveTwoBone(Transform upper, Transform lower, Transform hand, Vector3 target, Vector3 pole)
    {
        var a = upper.position;
        var l1 = Vector3.Distance(a, lower.position);
        var l2 = Vector3.Distance(lower.position, hand.position);
        if (l1 < 1e-5f || l2 < 1e-5f) return;

        var toTarget = target - a;
        var dist = Mathf.Clamp(toTarget.magnitude, Mathf.Abs(l1 - l2) + 1e-4f, l1 + l2 - 1e-4f);
        var dir = toTarget.sqrMagnitude > 1e-10f ? toTarget.normalized : Vector3.forward;

        // Elbow position on the circle of solutions, chosen by the pole.
        var cos = Mathf.Clamp((l1 * l1 + dist * dist - l2 * l2) / (2f * l1 * dist), -1f, 1f);
        var along = l1 * cos;
        var radius = Mathf.Sqrt(Mathf.Max(0f, l1 * l1 - along * along));
        var poleDir = Vector3.ProjectOnPlane(pole - a, dir);
        if (poleDir.sqrMagnitude < 1e-8f) poleDir = Vector3.ProjectOnPlane(Vector3.up, dir);
        if (poleDir.sqrMagnitude < 1e-8f) poleDir = Vector3.ProjectOnPlane(Vector3.right, dir);
        var elbow = a + dir * along + poleDir.normalized * radius;

        // Rotate each bone so its existing child offset points where it now must.
        upper.rotation = Quaternion.FromToRotation(lower.position - a, elbow - a) * upper.rotation;
        lower.rotation = Quaternion.FromToRotation(hand.position - lower.position, (a + dir * dist) - lower.position) *
                         lower.rotation;
    }

    private void DrawEnvelopes()
    {
        // The Scene labels and the panel MUST reach the same verdict from the same source. They previously did
        // not: the panel had moved to muscle space while these labels still used the fixed-axis approximation,
        // so one could read INVALID while the other read CLEAR for the same joint.
        RefreshMuscles();
        for (var i = 0; i < _specs.Length; i++)
        {
            var t = _tf[i];
            if (t == null) continue;
            var e = Eval(i);
            string muscleWhy;
            var st = MuscleState(_bones[i], out muscleWhy);
            var col = st == State.Invalid ? new Color(1f, 0.3f, 0.3f)
                : st == State.RuntimeCheck ? new Color(1f, 0.8f, 0.2f) : new Color(0.35f, 0.9f, 0.45f);

            // The joint frame in world: parent orientation, composed with muscle-zero and the joint basis. This
            // is the frame the evaluator decomposes in, so what is drawn is what is judged.
            var parentRot = t.parent != null ? t.parent.rotation : Quaternion.identity;
            var jointWorld = parentRot * _zero[i] * _specs[i].Basis;
            var o = t.position;
            var r = _envelopeRadius * HandleUtility.GetHandleSize(o) * 2f;

            var xAxis = jointWorld * Vector3.right; // twist
            var yAxis = jointWorld * Vector3.up;
            var zAxis = jointWorld * Vector3.forward;

            Handles.color = col;
            // Swing envelope at the DECLARED clearance, not the hard stop — authoring works inside the envelope.
            var sy = _specs[i].YLocked ? 0f : _specs[i].YLimit * JointLimitPolicy.DeclaredClearance;
            var sz = _specs[i].ZLocked ? 0f : _specs[i].ZLimit * JointLimitPolicy.DeclaredClearance;
            DrawSwingEllipse(o, xAxis, yAxis, zAxis, sy, sz, r);

            // Twist arc, with the declared envelope drawn inside the hard range.
            if (_specs[i].TwistLimited)
            {
                Handles.color = new Color(col.r, col.g, col.b, 0.30f);
                Handles.DrawSolidArc(o, xAxis, Quaternion.AngleAxis(_specs[i].TwistLow, xAxis) * yAxis,
                    _specs[i].TwistHigh - _specs[i].TwistLow, r * 0.55f);
                Handles.color = col;
                Handles.DrawLine(o, o + (Quaternion.AngleAxis(e.TwistDeg, xAxis) * yAxis) * r * 0.65f);
            }

            // Locked axis: shown explicitly, with the residual that exists about it.
            if (_specs[i].YLocked || _specs[i].ZLocked)
            {
                var lockedAxis = _specs[i].YLocked ? yAxis : zAxis;
                Handles.color = e.LockedResidualDeg > _lockedToleranceDeg ? Color.red : new Color(1f, 1f, 1f, 0.35f);
                Handles.DrawDottedLine(o - lockedAxis * r * 0.5f, o + lockedAxis * r * 0.5f, 2f);
            }

            var repr = e.LockedResidualDeg > _lockedToleranceDeg;
            if (st != State.Clear || repr)
            {
                Handles.color = col;
                var label = _bones[i] + "  " + st.ToString().ToUpper();
                if (muscleWhy.Length > 0) label += "\n" + muscleWhy;
                // Representability is a RIG property, not a verdict on the pose - labelled as such so it can
                // never be mistaken for "you posed this wrongly".
                if (repr)
                    label += string.Format(Ci, "\n[rig] {0:F1} deg about the LOCKED axis - not a posing error",
                        e.LockedResidualDeg);
                Handles.Label(o + Vector3.up * r * 0.6f, label);
            }
        }
    }

    /// <summary>
    ///     ⚠️ <b>UNREACHABLE, and must stay that way.</b> Nothing calls this. It clamps a bone's muscles and
    ///     writes the result back over the author's pose — the same class of destructive repair as the removed
    ///     projection, and it additionally goes through <c>SetHumanPose</c>, which writes bone POSITIONS.
    ///     <para>
    ///         Kept only because it documents the muscle-space clamp and the isolation trick below it. Do not
    ///         wire it to a button.
    ///     </para>
    /// </summary>
    private void SnapToNearestValid(int i)
    {
        if (_rig == null || _hp.muscles == null) return;
        var anim = _rig.GetComponent<Animator>();
        if (anim == null || anim.avatar == null) return;

        // Clamp in MUSCLE space, which is where legality is actually defined. The previous version clamped in
        // the fixed-axis joint decomposition and threw the hand a long way off for a pose Unity considered
        // perfectly legal — it was solving in the wrong space, for a violation that did not exist.
        var bones = _rig.GetComponentsInChildren<Transform>(true);
        Undo.RecordObjects(bones, "Snap " + _bones[i] + " to nearest valid");

        RefreshMuscles();
        var clear = JointLimitPolicy.DeclaredClearance;
        var changed = false;
        foreach (var m in MusclesOf(_bones[i]))
        {
            var v = Mathf.Clamp(_hp.muscles[m.Index], -clear, clear);
            if (Mathf.Approximately(v, _hp.muscles[m.Index])) continue;
            _hp.muscles[m.Index] = v;
            changed = true;
        }

        if (!changed) return;

        // ISOLATE THE CHANGE TO ONE BONE.
        //
        // SetHumanPose writes the WHOLE humanoid, and the Get->Set round trip is not exactly the identity, so
        // clamping a single bone's muscles still nudged every other bone - "hitting nearest valid affects the
        // entire model". It also writes the humanoid body position, which lands on the Hips and once displaced
        // a rig by 5.1 m.
        //
        // So: let Unity compute the clamped bone's rotation with its own authoritative muscle mapping, then put
        // EVERY other transform back exactly as it was. Only the target bone's localRotation survives. Bones
        // below it swing with it, which is correct - their local rotations are unchanged.
        var target = _tf[i];
        var rootPos = anim.transform.position;
        var rootRot = anim.transform.rotation;
        var savedPos = new Vector3[bones.Length];
        var savedRot = new Quaternion[bones.Length];
        for (var k = 0; k < bones.Length; k++)
        {
            savedPos[k] = bones[k].localPosition;
            savedRot[k] = bones[k].localRotation;
        }

        _hph.SetHumanPose(ref _hp);

        anim.transform.SetPositionAndRotation(rootPos, rootRot);
        for (var k = 0; k < bones.Length; k++)
        {
            bones[k].localPosition = savedPos[k];                 // positions are never authored content
            if (bones[k] != target) bones[k].localRotation = savedRot[k];
        }

        InitHandles(); // the arm moved; bring the handles back onto it
        SceneView.RepaintAll();
    }

    /// <summary>The forearm's declared hinge axis (its Forearm Stretch DOF) in world space.</summary>
    private Vector3 HingeAxisWorld(Transform lower)
    {
        var parentRot = lower.parent != null ? lower.parent.rotation : Quaternion.identity;
        return ((parentRot * _zero[5] * _specs[5].Basis) * Vector3.right).normalized;
    }

    /// <summary>
    ///     Which way the elbow folds, resolved from geometry rather than from a DOF sign.
    ///     <para>
    ///         The forearm swings about its hinge axis, so the wrist's instantaneous travel is
    ///         <c>cross(hinge, elbow→wrist)</c>. Flexion is whichever of the two senses brings the wrist
    ///         <b>closer to the shoulder</b> — a test that does not care how Unity signs "Forearm Stretch".
    ///         <paramref name="inside" /> is then the crease side (the arm folds into it) and
    ///         <paramref name="outside" /> the opposite side, where the bony point sits.
    ///     </para>
    /// </summary>
    private bool ElbowFoldDirections(Transform upper, Transform lower, Transform hand, out Vector3 inside,
        out Vector3 outside)
    {
        inside = Vector3.zero;
        outside = Vector3.zero;
        var hinge = HingeAxisWorld(lower);
        var v = hand.position - lower.position;
        if (v.sqrMagnitude < 1e-10f) return false;
        var travel = Vector3.Cross(hinge, v);
        if (travel.sqrMagnitude < 1e-10f) return false;
        travel.Normalize();

        // Does rotating the wrist along +travel move it toward the shoulder? If so that sense is flexion.
        var probe = lower.position + Quaternion.AngleAxis(3f, hinge) * v;
        var flexIsPositive = Vector3.Distance(probe, upper.position) < Vector3.Distance(hand.position, upper.position);
        inside = flexIsPositive ? travel : -travel;
        outside = -inside;
        return true;
    }

    /// <summary>
    ///     Draw and NAME one joint's three axes, using Unity's own DOF names.
    ///     <para>
    ///         This exists because the swing/twist vocabulary misleads while authoring: the joint's X is
    ///         "twist" to the decomposition but <i>Forearm Stretch</i> — the elbow hinge — on the forearm. Being
    ///         told "invalid twist" while bending an elbow is exactly the wrong feedback. The hinge is drawn as a
    ///         full double-ended axis so the bony point of the elbow can be lined up against it.
    ///     </para>
    /// </summary>
    private void DrawNamedDofAxes()
    {
        var i = Mathf.Clamp(_axisFocus, 0, _specs.Length - 1);
        var t = _tf[i];
        if (t == null) return;
        var spec = _specs[i];
        var parentRot = t.parent != null ? t.parent.rotation : Quaternion.identity;
        var jointWorld = parentRot * _zero[i] * spec.Basis;
        var o = t.position;
        var len = _envelopeRadius * HandleUtility.GetHandleSize(o) * 3f;

        var axes = new[] {jointWorld * Vector3.right, jointWorld * Vector3.up, jointWorld * Vector3.forward};
        var cols = new[] {new Color(1f, 0.35f, 0.35f), new Color(0.4f, 1f, 0.4f), new Color(0.45f, 0.6f, 1f)};
        var locked = new[] {!spec.TwistLimited, spec.YLocked, spec.ZLocked};

        for (var s = 0; s < 3; s++)
        {
            var a = axes[s].normalized;
            Handles.color = locked[s] ? new Color(0.55f, 0.55f, 0.55f) : cols[s];
            // Double-ended: a hinge has two sides, and for the elbow the OUTSIDE end is the bony point.
            if (locked[s]) Handles.DrawDottedLine(o - a * len, o + a * len, 3f);
            else Handles.DrawLine(o - a * len, o + a * len);
            Handles.Label(o + a * len * 1.05f, "  " + spec.DofName(s));
            if (!locked[s]) Handles.Label(o - a * len * 1.05f, "  (opposite end)");
        }

        Handles.color = Color.white;
        Handles.Label(o, "  " + _bones[i]);
    }

    /// <summary>Wire ellipse in the joint's Y/Z plane — the declared swing envelope. A locked axis contributes no
    /// extent, so a joint with one free swing axis correctly draws as a line rather than a fake cone.</summary>
    private static void DrawSwingEllipse(Vector3 o, Vector3 x, Vector3 y, Vector3 z, float degY, float degZ, float r)
    {
        const int steps = 48;
        var prev = Vector3.zero;
        for (var k = 0; k <= steps; k++)
        {
            var a = k / (float) steps * Mathf.PI * 2f;
            var ay = degY * Mathf.Cos(a);
            var az = degZ * Mathf.Sin(a);
            var dir = Quaternion.AngleAxis(ay, z) * Quaternion.AngleAxis(az, y) * x;
            var p = o + dir * r;
            if (k > 0) Handles.DrawLine(prev, p);
            prev = p;
        }
    }
}
