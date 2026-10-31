using System;
using UnityEngine;

namespace BladeContact
{
    /// <summary>
    ///     Forwards one body's collisions to whoever is answering for the blade that body is carrying.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Unity delivers collision callbacks to the GameObject the Rigidbody is on, and nowhere else.
    ///         When a blade's physics is hosted by a proxy body — an attachment system's rigid copy, say —
    ///         the component that knows what that blade IS lives on a different object entirely and will
    ///         never be called. This is added to the proxy so the callbacks reach it.
    ///     </para>
    ///     <para>
    ///         Both enter and stay are forwarded. A maintained contact only reports through
    ///         <c>OnCollisionStay</c>, and a layer that studies maintained contact cannot be driven by
    ///         first-touch alone.
    ///     </para>
    ///     <para>
    ///         It is added at runtime and dies with the object it was added to. Subscribers must tolerate
    ///         it disappearing without notice.
    ///     </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class BladeCollisionRelay : MonoBehaviour
    {
        /// <summary>Raised for every collision this body reports, on enter and on stay.</summary>
        public event Action<Collision> Collided;

        private void OnCollisionEnter(Collision collision)
        {
            Action<Collision> handler = Collided;
            if (handler != null) handler(collision);
        }

        private void OnCollisionStay(Collision collision)
        {
            Action<Collision> handler = Collided;
            if (handler != null) handler(collision);
        }
    }
}
