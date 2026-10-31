using UnityEngine;

/// <summary>
///     <b>THE SINGLE DEFINITION of the Hand↔Sword mechanical attachment.</b> Both the attachment creation and its
///     visualisation read from here, so there is exactly one mechanical relationship and one picture of it.
///     <para>
///         Everything that wants to know "where does the sword connect to the arm" asks this type. Nothing
///         re-derives it. That rule exists because every previous attempt to locate the grasp independently
///         misled — sword transform origin, hand-bone origin, grip <c>Renderer.bounds</c> (a world-space AABB of
///         a rotated object), a finger-proximal palm proxy, the cylinder centre — and one produced a retracted
///         "158 mm outrigger" claim. A visualiser that computed its own endpoints would be that mistake again,
///         wearing a <c>LineRenderer</c>.
///     </para>
///     <para>
///         <b>This is not a grasp model.</b> It says where the constraint binds. It says nothing about anatomical
///         grasp, palm position, finger contact or grasp quality — none of which are modelled. Do not derive
///         grip error, grasp accuracy, a hand-to-grip tolerance or any PASS/FAIL from the quantities here.
///     </para>
/// </summary>
public static class HandSwordConnection
{
    /// <summary>
    ///     The joint anchor on the SWORD, in sword-local space. Zero is not a convenience default — it is the
    ///     anchor <see cref="Attach" /> actually writes, so the visualiser drawing it is drawing the real thing.
    ///     Change it here and both the joint and the picture change together.
    /// </summary>
    public static readonly Vector3 SwordAnchorLocal = Vector3.zero;

    /// <summary>Resolved state of the connection between one rig's driven hand and one sword.</summary>
    public struct State
    {
        /// <summary>Both sides resolved; the endpoints below are meaningful.</summary>
        public bool Valid;

        /// <summary>A real <see cref="FixedJoint" /> binding this sword to this hand exists <b>right now</b>.
        /// Read from the live component — never inferred from a lifecycle mode.</summary>
        public bool Attached;

        /// <summary>
        ///     Hand-side endpoint, world space: the <b>Right_Hand / wrist body origin</b>.
        ///     <para>
        ///         ⚠️ This is <b>not</b> the joint's <c>connectedAnchor</c>. See <see cref="Resolve" />.
        ///     </para>
        /// </summary>
        public Vector3 HandPoint;

        /// <summary>
        ///     Sword-side endpoint, world space: the <b>sword attachment-body origin</b>.
        ///     <para>
        ///         ⚠️ This is <b>not</b> "the joint's anchor" as a concept, even though the anchor currently sits
        ///         at the same place. See <see cref="Resolve" />.
        ///     </para>
        /// </summary>
        public Vector3 SwordPoint;

        public Transform Hand;
        public Rigidbody HandBody;
        public FixedJoint Joint;

        /// <summary>
        ///     Distance between the two endpoints, millimetres — <b>the geometry of the visual relationship line,
        ///     and nothing else.</b>
        ///     <para>
        ///         ⛔ Do not print it as grasp error. Do not threshold it. Do not compare A against B as grasp
        ///         quality. Do not use it in any pass/fail. Do not read it as FixedJoint extension — the joint's
        ///         own anchors are coincident, so this is not joint stretch. Do not read it as an anatomical grip
        ///         distance — no grasp mechanics are modelled.
        ///     </para>
        /// </summary>
        public float SeparationMm
        {
            get { return Vector3.Distance(HandPoint, SwordPoint) * 1000f; }
        }
    }

    /// <summary>
    ///     Which two bodies are being considered as a constrained pair, where they currently are, and whether a
    ///     real attachment exists between them.
    ///     <para>
    ///         <b>⚠️ THE ENDPOINTS ARE THE TWO BODIES' ORIGINS — NOT THE JOINT'S TWO ANCHORS.</b> The distinction
    ///         is semantic and must not be blurred:
    ///     </para>
    ///     <code>
    ///     the actual FixedJoint relationship:
    ///         sword joint anchor  ↔  auto-configured connectedAnchor on Right_Hand
    ///         (these COINCIDE at the moment the joint is created)
    ///
    ///     what Resolve reports, and what the visualiser draws:
    ///         Right_Hand / wrist body origin  ↔  sword attachment-body origin
    ///         (derived automatically from the same two bodies involved in the connection)
    ///     </code>
    ///     <para>
    ///         Because <see cref="Attach" /> leaves <c>autoConfigureConnectedAnchor</c> true, the joint's own two
    ///         anchors are coincident by construction. A line drawn between them would be near zero-length and
    ///         would show only solver error — which is <b>not</b> what this answers. This answers: <i>which hand
    ///         body and which sword body are being considered as a constrained pair, and what is their current
    ///         spatial relationship?</i>
    ///     </para>
    /// </summary>
    public static State Resolve(ActiveArmRagdoll rig, Transform sword)
    {
        var st = new State();
        if (rig == null || sword == null) return st;

        st.Hand = rig.DrivenHand;
        if (st.Hand == null) return st;
        st.HandBody = st.Hand.GetComponent<Rigidbody>(); // null before Build(); the hand point still resolves

        // Body origins, not joint anchors. SwordAnchorLocal is zero, so this IS the sword body's origin; it is
        // written this way so that if the anchor ever moves, the picture follows the mechanism rather than
        // silently disagreeing with it.
        st.SwordPoint = sword.TransformPoint(SwordAnchorLocal);
        st.HandPoint = st.Hand.position;
        st.Valid = true;

        // ATTACHED is read from the live scene, never inferred. A lifecycle mode of ARM-HOLD does not mean the
        // attachment succeeded, and a stale "connected" picture after the joint is gone would be a lie.
        foreach (var fj in sword.GetComponents<FixedJoint>())
        {
            if (fj == null) continue;
            if (st.HandBody != null && fj.connectedBody != st.HandBody) continue;
            st.Joint = fj;
            st.Attached = true;
            break;
        }

        return st;
    }

    /// <summary>
    ///     Create the attachment. <b>The only place a Hand↔Sword <see cref="FixedJoint" /> is made.</b>
    ///     <para>
    ///         Binds whatever hand/sword relationship currently exists and <b>repositions neither object</b> —
    ///         that is the accepted mechanical rule at ARM-HOLD, and it is why the sword does not jump.
    ///     </para>
    /// </summary>
    /// <returns>The created joint, or null if the chain has not been built.</returns>
    public static FixedJoint Attach(ActiveArmRagdoll rig, Rigidbody sword)
    {
        if (rig == null || sword == null) return null;
        var hand = rig.DrivenHand;
        var handRb = hand != null ? hand.GetComponent<Rigidbody>() : null;
        if (handRb == null)
        {
            Debug.LogError("[HandSwordConnection] Build the chain before attaching a sword.");
            return null;
        }

        var fj = sword.gameObject.AddComponent<FixedJoint>();
        fj.connectedBody = handRb;
        fj.anchor = SwordAnchorLocal;
        fj.enablePreprocessing = false;
        return fj;
    }
}
