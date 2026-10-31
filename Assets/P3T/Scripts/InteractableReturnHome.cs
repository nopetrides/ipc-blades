using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace P3T
{
    /// <summary>
    /// Interactive-mode safety tether for a grabbable prop (e.g. the sword).
    /// Records its spawn pose on Start ("home"), then while NOT held:
    ///   * waits out <c>releaseDelay</c> first, keeping the released momentum so the prop can be thrown
    ///     and can carry through a swing,
    ///   * then drifts back to home, translation and rotation scaled to finish TOGETHER, and
    ///   * if it ends up far from BOTH its home and the player, kills momentum and teleports home
    ///     (catches "let go and it flew across the room").
    /// It rests only when position AND orientation are both home.
    ///
    /// HMD-tested 2026-08-21 (Noah), which found all three of those behaviours missing: the tether engaged
    /// on the first frame after release so nothing could be thrown; translation and rotation ran at fixed
    /// independent speeds so they landed at different times; and the rest test looked at position only, so a
    /// prop that arrived home un-rotated stopped dead and never finished turning.
    /// The player reference is resolved via <see cref="Camera.main"/> so it keeps working if the
    /// player swaps between rigs later (no fixed player object assumed).
    ///
    /// This is an interactive-mode convenience only; it is not part of the measurement pipeline.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Rigidbody))]
    [RequireComponent(typeof(XRGrabInteractable))]
    public sealed class InteractableReturnHome : MonoBehaviour
    {
        [Tooltip("Optional explicit player transform. If empty, uses Camera.main (survives rig swaps).")]
        [SerializeField] private Transform playerOverride;

        [Tooltip("Metres from BOTH home AND the player before a hard reset (kill momentum + teleport).")]
        [SerializeField] private float hardResetDistance = 3f;

        [Tooltip("Gentle drift-home speed (m/s) when released within range.")]
        [SerializeField] private float returnSpeed = 0.6f;

        [Tooltip("Gentle drift-home rotate speed (deg/s).")]
        [SerializeField] private float returnRotateSpeed = 120f;

        [Tooltip("Stop drifting once within this distance of home (m).")]
        [SerializeField] private float restThreshold = 0.002f;

        [Tooltip("Stop rotating once within this many degrees of the home orientation. Paired with " +
                 "restThreshold: the tether rests only when BOTH are satisfied. NOMINAL VALUE - set it to " +
                 "whatever reads right in the headset; this is demo convenience, not a measurement threshold.")]
        [SerializeField] private float restAngleDegrees = 0.5f;

        [Tooltip("Grace period after release, in seconds, before the tether engages. During it the sword keeps " +
                 "its released momentum, so it can be thrown and can carry through a swing. Zero reproduces the " +
                 "old behaviour, where the tether grabbed the sword on the first frame after release and killed " +
                 "the throw. NOMINAL VALUE - Noah's to set.")]
        [SerializeField] private float releaseDelay = 1.5f;

        [Tooltip("Log every tether state change once (grabbed / released / engaged / rested). Diagnostic aid " +
                 "for headset testing, where the console is not visible while wearing the HMD.")]
        [SerializeField] private bool logTransitions = true;

        /// <summary>Last time this was held, so the release grace period can be measured from it.</summary>
        private float _lastHeldTime = -999f;

        /// <summary>
        ///     Held state, driven by XRI's OWN select events rather than by polling
        ///     <c>XRGrabInteractable.isSelected</c> once per physics step.
        ///     <para>
        ///         <b>Why events.</b> The polled version failed in the headset (Noah, 2026-08-21): the sword was
        ///         grabbed and released, yet <c>_lastHeldTime</c> was still at its <c>-999</c> initialiser
        ///         afterwards, so no <c>FixedUpdate</c> ever observed <c>isSelected == true</c> and the tether
        ///         never engaged at all — it read as "hangs in place and never returns". The drift maths was
        ///         verified correct headlessly over the same build, which is what narrowed it to this signal.
        ///         <c>selectEntered</c>/<c>selectExited</c> fire on the interaction manager's own update, so they
        ///         cannot be missed between physics steps.
        ///     </para>
        /// </summary>
        private bool _held;

        private bool _engagedLogged;

        private Rigidbody _body;
        private XRGrabInteractable _grab;
        private Camera _cam;
        private Vector3 _homePos;
        private Quaternion _homeRot;

        private void Awake()
        {
            _body = GetComponent<Rigidbody>();
            _grab = GetComponent<XRGrabInteractable>();
        }

        private void Start()
        {
            // "home" = wherever the prop was placed in the scene at play start.
            _homePos = transform.position;
            _homeRot = transform.rotation;
        }

        private void OnEnable()
        {
            if (_grab == null) return;
            _grab.selectEntered.AddListener(OnGrabbed);
            _grab.selectExited.AddListener(OnReleased);
        }

        private void OnDisable()
        {
            if (_grab == null) return;
            _grab.selectEntered.RemoveListener(OnGrabbed);
            _grab.selectExited.RemoveListener(OnReleased);
        }

        private void OnGrabbed(UnityEngine.XR.Interaction.Toolkit.SelectEnterEventArgs args)
        {
            _held = true;
            _lastHeldTime = Time.time;
            _engagedLogged = false;
            if (logTransitions) Debug.Log("[Tether] " + name + " GRABBED", this);
        }

        private void OnReleased(UnityEngine.XR.Interaction.Toolkit.SelectExitEventArgs args)
        {
            _held = false;
            _lastHeldTime = Time.time; // the grace period is measured from here
            if (logTransitions)
                Debug.Log("[Tether] " + name + " RELEASED — grace " + releaseDelay.ToString("0.##") + " s, vel="
                          + _body.linearVelocity.magnitude.ToString("0.###") + " m/s", this);
        }

        /// <summary>Re-record the current pose as the new home (call if you reposition it at runtime).</summary>
        public void SetHomeToCurrent()
        {
            _homePos = transform.position;
            _homeRot = transform.rotation;
        }

        private Transform Player
        {
            get
            {
                if (playerOverride != null) return playerOverride;
                if (_cam == null) _cam = Camera.main; // re-resolve lazily so a rig swap is picked up
                return _cam != null ? _cam.transform : null;
            }
        }

        private void FixedUpdate()
        {
            // Event-driven held state, with the polled property kept as a belt-and-braces fallback in case a
            // grab happens through a path that does not raise the events.
            if (_held || (_grab != null && _grab.isSelected))
            {
                _lastHeldTime = Time.time; // held: don't interfere, and restart the release grace period
                return;
            }

            // GRACE PERIOD. Deliberately returns BEFORE ZeroVelocity(), so the sword keeps the momentum it was
            // released with and can actually be thrown. The previous version zeroed velocity on the first frame
            // after release, which is why a throw died instantly and there was no hang time.
            if (Time.time - _lastHeldTime < releaseDelay) return;

            float dHome = Vector3.Distance(_body.position, _homePos);
            float aHome = Quaternion.Angle(_body.rotation, _homeRot);

            // REST ONLY WHEN BOTH CHANNELS ARE HOME. Testing position alone is what left the sword parked at
            // the right place in the wrong orientation: it reached home positionally, took this branch, and
            // stopped dead with rotation unfinished.
            if (dHome <= restThreshold && aHome <= restAngleDegrees)
            {
                ZeroVelocity();
                if (logTransitions && _engagedLogged)
                {
                    _engagedLogged = false;
                    Debug.Log("[Tether] " + name + " RESTED at home (pos and rotation both complete)", this);
                }
                return;
            }

            if (logTransitions && !_engagedLogged)
            {
                _engagedLogged = true;
                Debug.Log("[Tether] " + name + " ENGAGED — returning from " + dHome.ToString("0.###")
                          + " m and " + aHome.ToString("0.#") + " deg", this);
            }

            Transform player = Player;
            float dPlayer = player != null ? Vector3.Distance(_body.position, player.position) : 0f;

            // Flew away: far from home AND far from the player (or no player found) -> hard reset.
            bool farFromBoth = dHome > hardResetDistance && (player == null || dPlayer > hardResetDistance);
            if (farFromBoth)
            {
                ZeroVelocity();
                _body.position = _homePos;
                _body.rotation = _homeRot;
                return;
            }

            // Drift home. Both channels are scaled to finish TOGETHER: each declared speed is treated as a
            // ceiling, the slower channel sets the duration, and the faster one is slowed to match. Stepping
            // both at fixed independent speeds is what made translation and rotation land at different times.
            ZeroVelocity();
            float tPos = returnSpeed > 0f ? dHome / returnSpeed : 0f;
            float tRot = returnRotateSpeed > 0f ? aHome / returnRotateSpeed : 0f;
            float t = Mathf.Max(tPos, tRot);
            if (t <= Mathf.Epsilon)
            {
                _body.position = _homePos;
                _body.rotation = _homeRot;
                return;
            }

            float dt = Time.fixedDeltaTime;
            _body.position = Vector3.MoveTowards(_body.position, _homePos, dHome / t * dt);
            _body.rotation = Quaternion.RotateTowards(_body.rotation, _homeRot, aHome / t * dt);
        }

        private void ZeroVelocity()
        {
            if (_body.isKinematic) return;
            _body.linearVelocity = Vector3.zero;
            _body.angularVelocity = Vector3.zero;
        }
    }
}
