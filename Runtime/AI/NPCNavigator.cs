using UnityEngine;
using UnityEngine.AI;

namespace ActiveRagdoll
{
    /// <summary>
    /// Path following for a physical body. Uses the NavMesh when one is baked (AI Navigation package) and
    /// falls back to direct steering otherwise. It never moves anything itself: it only returns a steering
    /// direction that the AI turns into a desired velocity for <see cref="LocomotionController"/>.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Active Ragdoll/AI/NPC Navigator")]
    public sealed class NPCNavigator : MonoBehaviour
    {
        private const int MaxCorners = 32;

        [SerializeField] private bool _useNavMesh = true;
        [Tooltip("NavMesh area mask (-1 = all areas).")]
        [SerializeField] private int _areaMask = NavMesh.AllAreas;
        [SerializeField, Min(0.05f)] private float _repathInterval = 0.35f;
        [Tooltip("Repath immediately when the destination moves further than this (m).")]
        [SerializeField, Min(0f)] private float _repathDistance = 0.5f;
        [SerializeField, Min(0.05f)] private float _cornerReachDistance = 0.45f;
        [Tooltip("How far (m) from the body to search for the NavMesh.")]
        [SerializeField, Min(0.1f)] private float _sampleDistance = 1.6f;
        [Tooltip("Other characters closer than this (m) push this one away (crowd separation).")]
        [SerializeField, Min(0f)] private float _separationRadius = 0.9f;

        private NavMeshPath _path;
        private readonly Vector3[] _corners = new Vector3[MaxCorners];
        private int _cornerCount;
        private int _cornerIndex;
        private Vector3 _destination;
        private bool _hasDestination;
        private bool _pathValid;
        private float _nextRepath;

        public bool HasDestination => _hasDestination;
        public bool HasPath => _pathValid;
        public Vector3 Destination => _destination;

        public void SetDestination(Vector3 destination)
        {
            if (!RagdollMath.IsFinite(destination))
                return;
            if (!_hasDestination || (destination - _destination).sqrMagnitude > _repathDistance * _repathDistance)
                _nextRepath = 0f;
            _destination = destination;
            _hasDestination = true;
        }

        public void Stop()
        {
            _hasDestination = false;
            _pathValid = false;
            _cornerCount = 0;
            _cornerIndex = 0;
        }

        /// <summary>Normalised horizontal direction to move in from <paramref name="from"/>, or zero if there is no destination.</summary>
        public Vector3 GetSteeringDirection(Vector3 from)
        {
            if (!_hasDestination)
                return Vector3.zero;

            if (_useNavMesh && Time.time >= _nextRepath)
                Repath(from);

            Vector3 target = _destination;
            if (_pathValid)
            {
                float reachSqr = _cornerReachDistance * _cornerReachDistance;
                while (_cornerIndex < _cornerCount - 1 && RagdollMath.Flatten(_corners[_cornerIndex] - from).sqrMagnitude < reachSqr)
                    _cornerIndex++;
                target = _corners[_cornerIndex];
            }

            return RagdollMath.SafeNormalize(RagdollMath.Flatten(target - from), Vector3.zero);
        }

        /// <summary>Horizontal push (0..~1 per neighbour) away from nearby characters.</summary>
        public Vector3 ComputeSeparation(Vector3 from, ActiveRagdollCharacter self)
        {
            if (_separationRadius <= 0f)
                return Vector3.zero;
            Vector3 push = Vector3.zero;
            var all = ActiveRagdollCharacter.All;
            for (int i = 0; i < all.Count; i++)
            {
                ActiveRagdollCharacter other = all[i];
                if (other == null || other == self || other.IsDead)
                    continue;
                Vector3 d = RagdollMath.Flatten(from - other.Position);
                float distance = d.magnitude;
                if (distance < _separationRadius && distance > 1e-3f)
                    push += d / distance * (1f - distance / _separationRadius);
            }
            return push;
        }

        private void Repath(Vector3 from)
        {
            _nextRepath = Time.time + _repathInterval;
            _path ??= new NavMeshPath();

            if (NavMesh.SamplePosition(from, out NavMeshHit start, _sampleDistance, _areaMask)
                && NavMesh.SamplePosition(_destination, out NavMeshHit end, _sampleDistance, _areaMask)
                && NavMesh.CalculatePath(start.position, end.position, _areaMask, _path)
                && _path.status != NavMeshPathStatus.PathInvalid)
            {
                _cornerCount = _path.GetCornersNonAlloc(_corners);
                _cornerIndex = Mathf.Min(1, Mathf.Max(0, _cornerCount - 1));
                _pathValid = _cornerCount > 0;
            }
            else
            {
                _pathValid = false; // no NavMesh here: steer straight at the destination
                _cornerCount = 0;
            }
        }

        private void OnDrawGizmosSelected()
        {
            if (!_pathValid)
                return;
            Gizmos.color = Color.blue;
            for (int i = 1; i < _cornerCount; i++)
                Gizmos.DrawLine(_corners[i - 1], _corners[i]);
        }
    }
}
