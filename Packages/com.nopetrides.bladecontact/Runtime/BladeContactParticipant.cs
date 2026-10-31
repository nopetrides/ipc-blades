using UnityEngine;

namespace BladeContact
{
    /// <summary>
    /// Marks a <see cref="BladeShell"/> as taking part in custom blade contact, and registers it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Participation is a property of the SWORD, declared where the sword is. A shell with this component
    /// takes part; a shell without it is baseline-only and stays entirely with PhysX. Nothing about which
    /// swords are expected to meet is expressed here or anywhere else the manager can see — the manager
    /// derives every unordered relationship from whoever registered.
    /// </para>
    /// <para>
    /// That distinction matters for what the system can be trusted to do. If participation were configured
    /// as a list of pairs, a relationship omitted from the list would silently never be checked, and the
    /// omission would look identical to a pair that was checked and found clear. With participant
    /// registration the only way a relationship goes unexamined is if the broad phase proves it separated.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(BladeShell))]
    public sealed class BladeContactParticipant : MonoBehaviour
    {
        [Tooltip("Manager to register with. Left empty, the first one in the scene is used.")]
        [SerializeField] private BladeContactManager manager;

        private BladeShell shell;
        private BladeContactManager registeredWith;

        /// <summary>The shell this participant registers.</summary>
        public BladeShell Shell => shell != null ? shell : shell = GetComponent<BladeShell>();

        /// <summary>True while this shell is registered with a manager.</summary>
        public bool IsRegistered => registeredWith != null;

        private void OnEnable()
        {
            BladeContactManager target = manager != null
                ? manager
                : FindFirstObjectByType<BladeContactManager>();

            if (target == null)
            {
                Debug.LogWarning(
                    $"[BladeContact] {name}: no BladeContactManager found, so this shell is not " +
                    "participating and PhysX still owns its blade contact.", this);
                return;
            }

            if (!target.RegisterShell(Shell)) return;

            registeredWith = target;
        }

        private void OnDisable()
        {
            if (registeredWith == null) return;

            registeredWith.UnregisterShell(Shell);
            registeredWith = null;
        }
    }
}
