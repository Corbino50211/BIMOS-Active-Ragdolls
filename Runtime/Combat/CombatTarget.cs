using System.Collections.Generic;
using UnityEngine;

namespace ActiveRagdoll
{
    /// <summary>
    /// Something NPCs can perceive and attack. Targets on a different <see cref="Team"/> are hostile.
    /// Put one on the player (the BIMOS integration adds <c>BIMOSPlayerTarget</c> automatically) and on
    /// each NPC (so NPCs of different teams can fight each other).
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Active Ragdoll/Combat Target")]
    public class CombatTarget : MonoBehaviour
    {
        private static readonly List<CombatTarget> s_active = new List<CombatTarget>();

        /// <summary>All enabled targets. Do not modify.</summary>
        public static IReadOnlyList<CombatTarget> Active => s_active;

        private static float s_lastDiscovery = float.NegativeInfinity;

        /// <summary>
        /// Optional hook invoked (at most once per second) when an NPC finds no hostile targets at all.
        /// Integrations use it to attach targets to players spawned at runtime (see the BIMOS integration).
        /// </summary>
        public static System.Action DiscoveryHandler;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            s_active.Clear();
            s_lastDiscovery = float.NegativeInfinity;
            Registered = null;
        }

        /// <summary>Asks the registered integration to look for new targets (rate limited).</summary>
        public static void RequestDiscovery()
        {
            if (DiscoveryHandler == null || Time.unscaledTime - s_lastDiscovery < 1f)
                return;
            s_lastDiscovery = Time.unscaledTime;
            DiscoveryHandler();
        }

        [Tooltip("Targets on the same team never attack each other. Player = 0, NPCs = 1 by default.")]
        [SerializeField] private int _team;

        [Tooltip("Where strikes aim and perception looks (usually the head). Defaults to 1.6 m above this transform.")]
        [SerializeField] private Transform _aimPoint;

        [Tooltip("Body centre used for distances and navigation. Defaults to 1 m above this transform.")]
        [SerializeField] private Transform _centerPoint;

        [Tooltip("Optional rigidbody used to estimate the target's velocity (for leading strikes).")]
        [SerializeField] private Rigidbody _velocitySource;

        private IHealth _health;

        public int Team
        {
            get => _team;
            set => _team = value;
        }

        public virtual Vector3 AimPoint => _aimPoint != null ? _aimPoint.position : transform.position + Vector3.up * 1.6f;
        public virtual Vector3 CenterPoint => _centerPoint != null ? _centerPoint.position : transform.position + Vector3.up;
        public virtual Vector3 Velocity => _velocitySource != null ? _velocitySource.linearVelocity : Vector3.zero;
        public virtual bool IsAlive => _health == null || _health.IsAlive;

        public bool IsHostileTo(int team) => team != _team;

        /// <summary>True if <paramref name="collider"/> is part of this target (used for line-of-sight checks).</summary>
        public virtual bool Owns(Collider collider) => collider != null && collider.transform.IsChildOf(transform);

        /// <summary>Finds the target that owns a collider or GameObject, if any.</summary>
        public static CombatTarget FindOwner(GameObject gameObject)
        {
            return gameObject != null ? gameObject.GetComponentInParent<CombatTarget>() : null;
        }

        protected virtual void Awake()
        {
            // GetComponentInParent searches this GameObject first.
            _health = GetComponentInParent<IHealth>();
        }

        protected void SetPoints(Transform aimPoint, Transform centerPoint, Rigidbody velocitySource)
        {
            _aimPoint = aimPoint;
            _centerPoint = centerPoint;
            _velocitySource = velocitySource;
        }

        internal void EditorSetup(int team, Transform aimPoint, Transform centerPoint, Rigidbody velocitySource)
        {
            _team = team;
            SetPoints(aimPoint, centerPoint, velocitySource);
        }

        /// <summary>Raised whenever a target is enabled. Integrations hook in here.</summary>
        public static event System.Action<CombatTarget> Registered;

        protected virtual void OnEnable()
        {
            if (!s_active.Contains(this))
                s_active.Add(this);
            Registered?.Invoke(this);
        }

        protected virtual void OnDisable()
        {
            s_active.Remove(this);
        }
    }
}
