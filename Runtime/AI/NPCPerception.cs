using UnityEngine;

namespace ActiveRagdoll
{
    /// <summary>
    /// Finds and remembers a hostile <see cref="CombatTarget"/>: sight range, field of view, close-range
    /// awareness, line of sight from the physical head, and memory of the last known position. Runs at a
    /// fixed interval with a random phase so many NPCs don't raycast on the same frame.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Active Ragdoll/AI/NPC Perception")]
    [RequireComponent(typeof(ActiveRagdollCharacter))]
    public sealed class NPCPerception : MonoBehaviour
    {
        [Tooltip("This NPC's team. Overridden by a CombatTarget on the same object, if present.")]
        [SerializeField] private int _team = 1;
        [SerializeField, Min(0f)] private float _sightRange = 25f;
        [SerializeField, Range(1f, 360f)] private float _fieldOfView = 150f;
        [Tooltip("Hostiles closer than this are noticed regardless of facing (still need line of sight).")]
        [SerializeField, Min(0f)] private float _proximityRange = 3f;
        [Tooltip("Seconds a target is remembered after losing sight of it.")]
        [SerializeField, Min(0f)] private float _memoryDuration = 8f;
        [SerializeField, Min(0.02f)] private float _updateInterval = 0.2f;
        [Tooltip("When hurt by an unknown source, aggro onto the nearest hostile within this range.")]
        [SerializeField, Min(0f)] private float _aggroOnAttackedRange = 20f;
        [Tooltip("Layers that block line of sight.")]
        [SerializeField] private LayerMask _occlusionMask = ~0;

        private ActiveRagdollCharacter _character;
        private CombatTarget _target;
        private bool _canSee;
        private Vector3 _lastKnownPosition;
        private Vector3 _lastKnownAimPoint;
        private float _lastSeenTime = float.NegativeInfinity;
        private float _nextUpdate;
        private int _mask;

        public int Team => _team;
        public CombatTarget Target => _target;
        public bool CanSeeTarget => _canSee && HasTarget;
        public float TimeSinceSeen => Time.time - _lastSeenTime;
        public Vector3 LastKnownPosition => _lastKnownPosition;
        public Vector3 LastKnownAimPoint => _lastKnownAimPoint;

        /// <summary>True while a live, enabled target is known and still remembered.</summary>
        public bool HasTarget => _target != null && _target.isActiveAndEnabled && _target.IsAlive && TimeSinceSeen <= _memoryDuration;

        private void Awake()
        {
            _character = GetComponent<ActiveRagdollCharacter>();
            CombatTarget own = GetComponent<CombatTarget>();
            if (own != null)
                _team = own.Team;
            _mask = _occlusionMask.value & ~RagdollLayers.MaskOf("Ignore Raycast");
            _nextUpdate = Time.time + Random.value * _updateInterval;
        }

        public void ForgetTarget()
        {
            _target = null;
            _canSee = false;
            _lastSeenTime = float.NegativeInfinity;
        }

        /// <summary>Called when this NPC is hit. Aggro onto the attacker, or the nearest hostile if unknown (thrown knife, gunfire).</summary>
        public void NotifyAttacked(GameObject source)
        {
            if (!isActiveAndEnabled || _character == null || !_character.IsValid || _character.IsDead)
                return;

            CombatTarget attacker = CombatTarget.FindOwner(source);
            if (attacker == null || !attacker.IsHostileTo(_team) || !attacker.IsAlive || attacker.transform.IsChildOf(transform))
                attacker = NearestHostile(_aggroOnAttackedRange);
            if (attacker == null)
                return;

            _target = attacker;
            _lastSeenTime = Time.time;
            _lastKnownPosition = attacker.CenterPoint;
            _lastKnownAimPoint = attacker.AimPoint;
            Attacked?.Invoke(attacker);
        }

        /// <summary>Raised when this NPC is hurt or grabbed by a hostile (after the attacker becomes the target).</summary>
        public event System.Action<CombatTarget> Attacked;

        private void Update()
        {
            if (_character == null || !_character.IsValid || _character.IsDead)
                return;

            if (Time.time >= _nextUpdate)
            {
                _nextUpdate = Time.time + _updateInterval;
                Sense();
            }
            else if (_canSee && _target != null)
            {
                // Cheap tracking between scans while the target is visible.
                _lastKnownPosition = _target.CenterPoint;
                _lastKnownAimPoint = _target.AimPoint;
            }
        }

        private Vector3 EyePosition()
        {
            Rigidbody head = _character.GetBody(BoneRole.Head);
            return head != null ? head.position : _character.Position + Vector3.up * 0.6f;
        }

        private void Sense()
        {
            if (_target != null && (!_target.isActiveAndEnabled || !_target.IsAlive))
                ForgetTarget();

            Vector3 eye = EyePosition();
            Vector3 forward = _character.HeadingRotation * Vector3.forward;
            float halfFov = _fieldOfView * 0.5f;

            CombatTarget best = null;
            bool bestVisible = false;
            float bestScore = float.PositiveInfinity;

            var targets = CombatTarget.Active;
            for (int i = 0; i < targets.Count; i++)
            {
                CombatTarget t = targets[i];
                if (t == null || !t.isActiveAndEnabled || !t.IsAlive || !t.IsHostileTo(_team) || t.transform.IsChildOf(transform))
                    continue;

                Vector3 aim = t.AimPoint;
                Vector3 to = aim - eye;
                float distance = to.magnitude;
                if (distance > _sightRange || distance < 1e-3f)
                    continue;

                bool inView = distance <= _proximityRange || Vector3.Angle(forward, RagdollMath.Flatten(to)) <= halfFov;
                bool visible = inView && HasLineOfSight(eye, t, to / distance, distance);
                if (!visible && t != _target)
                    continue;

                float score = distance * (t == _target ? 0.7f : 1f) * (visible ? 1f : 1.5f);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = t;
                    bestVisible = visible;
                }
            }

            if (best == null)
            {
                _canSee = false;
                if (_target == null && !AnyHostileExists())
                    CombatTarget.RequestDiscovery();
                return;
            }

            _target = best;
            _canSee = bestVisible;
            if (_canSee)
            {
                _lastSeenTime = Time.time;
                _lastKnownPosition = best.CenterPoint;
                _lastKnownAimPoint = best.AimPoint;
            }
        }

        private bool HasLineOfSight(Vector3 eye, CombatTarget target, Vector3 direction, float distance)
        {
            if (!PhysicsQuery.Raycast(eye, direction, distance, _mask, _character, null, out RaycastHit hit))
                return true;
            return target.Owns(hit.collider);
        }

        private bool AnyHostileExists()
        {
            var targets = CombatTarget.Active;
            for (int i = 0; i < targets.Count; i++)
                if (targets[i] != null && targets[i].IsHostileTo(_team))
                    return true;
            return false;
        }

        private CombatTarget NearestHostile(float range)
        {
            Vector3 from = _character.Position;
            CombatTarget best = null;
            float bestSqr = range * range;
            var targets = CombatTarget.Active;
            for (int i = 0; i < targets.Count; i++)
            {
                CombatTarget t = targets[i];
                if (t == null || !t.isActiveAndEnabled || !t.IsAlive || !t.IsHostileTo(_team) || t.transform.IsChildOf(transform))
                    continue;
                float sqr = (t.CenterPoint - from).sqrMagnitude;
                if (sqr < bestSqr)
                {
                    bestSqr = sqr;
                    best = t;
                }
            }
            return best;
        }

        private void OnDisable()
        {
            _canSee = false;
        }

        private void OnDrawGizmosSelected()
        {
            if (_character == null || !_character.IsValid)
                return;
            Gizmos.color = new Color(1f, 1f, 0f, 0.4f);
            Vector3 eye = EyePosition();
            Gizmos.DrawWireSphere(eye, _proximityRange);
            if (_target != null)
            {
                Gizmos.color = _canSee ? Color.red : Color.gray;
                Gizmos.DrawLine(eye, _canSee ? _target.AimPoint : _lastKnownAimPoint);
            }
        }
    }
}
