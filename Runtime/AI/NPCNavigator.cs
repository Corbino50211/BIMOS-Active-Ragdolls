using UnityEngine;
using UnityEngine.AI;

namespace ActiveRagdoll
{
    /// <summary>
    /// Path following for a physical body. It never moves anything itself: it returns a steering direction
    /// that the AI turns into a desired velocity for <see cref="LocomotionController"/>. Three layers:
    /// <list type="number">
    /// <item><b>NavMesh paths</b> (when one is baked). Start and goal are sampled from floor level with a
    /// generous radius, so a target standing in an obstacle's clearance hole (next to a table) still gets a
    /// path to the nearest reachable point. An NPC knocked off the mesh walks back onto it first.</item>
    /// <item><b>Local avoidance</b>: hip- and knee-height sphere probes steer around obstacles the NavMesh
    /// doesn't know about (moved props, no NavMesh at all).</item>
    /// <item><b>Stuck recovery</b>: no progress for a while triggers a detour to the clearer side and a repath.</item>
    /// </list>
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Active Ragdoll/AI/NPC Navigator")]
    public sealed class NPCNavigator : MonoBehaviour
    {
        private const int MaxCorners = 32;
        private const int MaxHits = 16;

        [Header("NavMesh")]
        [SerializeField] private bool _useNavMesh = true;
        [Tooltip("NavMesh area mask (-1 = all areas).")]
        [SerializeField] private int _areaMask = NavMesh.AllAreas;
        [SerializeField, Min(0.05f)] private float _repathInterval = 0.35f;
        [Tooltip("Repath immediately when the destination moves further than this (m).")]
        [SerializeField, Min(0f)] private float _repathDistance = 0.5f;
        [SerializeField, Min(0.05f)] private float _cornerReachDistance = 0.45f;
        [Tooltip("How far (m) from the floor under the NPC / target to search for the NavMesh. Must cover obstacle clearance holes.")]
        [SerializeField, Min(0.1f)] private float _sampleDistance = 3f;
        [Tooltip("Off-mesh distance (m) beyond which the NPC first walks back onto the NavMesh.")]
        [SerializeField, Min(0f)] private float _offMeshTolerance = 0.3f;

        [Header("Local Avoidance")]
        [SerializeField] private bool _avoidObstacles = true;
        [SerializeField] private LayerMask _obstacleMask = ~0;
        [SerializeField, Min(0.1f)] private float _probeDistance = 1.2f;
        [SerializeField, Min(0.05f)] private float _probeRadius = 0.2f;
        [Tooltip("Dynamic rigidbodies lighter than this (kg) are pushed through, not avoided.")]
        [SerializeField, Min(0f)] private float _minObstacleMass = 5f;
        [SerializeField, Range(4, 16)] private int _avoidanceSamples = 10;
        [SerializeField, Range(30f, 180f)] private float _maxAvoidanceAngle = 135f;

        [Header("Stuck Recovery")]
        [Tooltip("Seconds without progress before the NPC is considered stuck.")]
        [SerializeField, Min(0.2f)] private float _stuckTime = 1.5f;
        [Tooltip("Distance (m) that counts as progress.")]
        [SerializeField, Min(0.05f)] private float _stuckDistance = 0.3f;
        [SerializeField, Min(0.1f)] private float _detourTime = 1.2f;
        [SerializeField, Range(20f, 110f)] private float _detourAngle = 75f;

        [Header("Crowd")]
        [Tooltip("Other characters closer than this (m) push this one away (crowd separation).")]
        [SerializeField, Min(0f)] private float _separationRadius = 0.9f;

        private static readonly RaycastHit[] s_hits = new RaycastHit[MaxHits];

        private ActiveRagdollCharacter _self;
        private Transform _ignoredRoot;
        private NavMeshPath _path;
        private readonly Vector3[] _corners = new Vector3[MaxCorners];
        private int _cornerCount;
        private int _cornerIndex;
        private Vector3 _destination;
        private bool _hasDestination;
        private bool _pathValid;
        private float _nextRepath;
        private float _preferredSide = 1f;

        private Vector3 _progressAnchor;
        private float _progressTime;
        private float _lastSteerTime = float.NegativeInfinity;
        private float _detourUntil = float.NegativeInfinity;
        private float _detourSide = 1f;

        public bool HasDestination => _hasDestination;
        public bool HasPath => _pathValid;
        public bool IsDetouring => Time.time < _detourUntil;
        public Vector3 Destination => _destination;

        private void Awake()
        {
            _self = GetComponent<ActiveRagdollCharacter>();
        }

        /// <summary>Colliders under this root (usually the current target) are never avoided.</summary>
        public void SetIgnoredRoot(Transform root) => _ignoredRoot = root;

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
            _detourUntil = float.NegativeInfinity;
        }

        /// <summary>Distance left to travel along the current path (straight-line distance without a path).</summary>
        public float RemainingDistance(Vector3 from)
        {
            if (!_hasDestination)
                return 0f;
            if (!_pathValid || _cornerIndex >= _cornerCount)
                return RagdollMath.Flatten(_destination - from).magnitude;

            float d = RagdollMath.Flatten(_corners[_cornerIndex] - from).magnitude;
            for (int i = _cornerIndex + 1; i < _cornerCount; i++)
                d += RagdollMath.Flatten(_corners[i] - _corners[i - 1]).magnitude;
            return d + RagdollMath.Flatten(_destination - _corners[_cornerCount - 1]).magnitude;
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

                // Past the last corner (the nearest reachable point to the goal): head for the goal itself.
                bool atEnd = _cornerIndex == _cornerCount - 1 && RagdollMath.Flatten(_corners[_cornerIndex] - from).sqrMagnitude < reachSqr;
                if (!atEnd)
                    target = _corners[_cornerIndex];
            }

            Vector3 desired = RagdollMath.SafeNormalize(RagdollMath.Flatten(target - from), Vector3.zero);
            if (desired == Vector3.zero)
                return desired;

            UpdateStuck(from, desired);
            if (Time.time < _detourUntil)
                desired = Quaternion.Euler(0f, _detourSide * _detourAngle, 0f) * desired;

            if (_avoidObstacles)
                desired = Avoid(from, desired);
            return desired;
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

        // ------------------------------------------------------------------ NavMesh

        private Vector3 FloorPoint(Vector3 point)
        {
            if (_self != null && _self.IsValid)
            {
                BalanceController balance = _self.Balance;
                if (balance != null && balance.IsInitialized && balance.HasGround && Vector3.Distance(point, _self.Position) < 0.01f)
                    return new Vector3(point.x, balance.GroundHeight, point.z);
            }
            if (Physics.Raycast(point + Vector3.up * 0.1f, Vector3.down, out RaycastHit hit, 3f, ~RagdollLayers.NonEnvironmentMask(), QueryTriggerInteraction.Ignore))
                return hit.point;
            return point;
        }

        private void Repath(Vector3 from)
        {
            _nextRepath = Time.time + _repathInterval;
            _path ??= new NavMeshPath();

            Vector3 startFloor = FloorPoint(from);
            Vector3 goalFloor = FloorPoint(_destination);
            if (NavMesh.SamplePosition(startFloor, out NavMeshHit start, _sampleDistance, _areaMask)
                && NavMesh.SamplePosition(goalFloor, out NavMeshHit end, _sampleDistance, _areaMask)
                && NavMesh.CalculatePath(start.position, end.position, _areaMask, _path)
                && _path.status != NavMeshPathStatus.PathInvalid)
            {
                _cornerCount = _path.GetCornersNonAlloc(_corners);
                _pathValid = _cornerCount > 0;
                // corners[0] is the sampled start. Skip it when we're on the mesh; walk to it when knocked off.
                bool offMesh = RagdollMath.Flatten(start.position - startFloor).magnitude > _offMeshTolerance;
                _cornerIndex = offMesh || _cornerCount < 2 ? 0 : 1;
            }
            else
            {
                _pathValid = false; // no NavMesh here: steer straight at the destination (avoidance still applies)
                _cornerCount = 0;
            }
        }

        // ------------------------------------------------------------------ Local avoidance

        private Vector3 Avoid(Vector3 from, Vector3 desired)
        {
            if (!Blocked(from, desired))
                return desired;

            float step = _maxAvoidanceAngle / Mathf.Max(1, _avoidanceSamples / 2);
            for (int i = 1; i <= _avoidanceSamples; i++)
            {
                // Alternate sides, trying the previously successful side first (prevents dithering).
                float side = (i % 2 == 1) ? _preferredSide : -_preferredSide;
                float angle = side * step * ((i + 1) / 2);
                Vector3 dir = Quaternion.Euler(0f, angle, 0f) * desired;
                if (!Blocked(from, dir))
                {
                    _preferredSide = Mathf.Sign(angle);
                    return dir;
                }
            }

            // Boxed in: slide sideways and let stuck recovery take over if that fails too.
            return Quaternion.Euler(0f, _preferredSide * 90f, 0f) * desired;
        }

        private bool Blocked(Vector3 from, Vector3 dir)
        {
            float standing = _self != null && _self.IsValid ? _self.StandingPelvisHeight : 1f;
            return ProbeHits(from, dir) || ProbeHits(from - Vector3.up * (standing * 0.55f), dir);
        }

        private bool ProbeHits(Vector3 origin, Vector3 dir)
        {
            int count = Physics.SphereCastNonAlloc(origin, _probeRadius, dir, s_hits, _probeDistance, _obstacleMask & ~RagdollLayers.NonEnvironmentMask(), QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                RaycastHit hit = s_hits[i];
                Collider c = hit.collider;
                if (c == null)
                    continue;
                if (hit.distance <= 0f && hit.point == Vector3.zero)
                    continue; // started inside it: can't tell which way is out; stuck recovery handles this
                if (hit.normal.y > 0.6f)
                    continue; // walkable surface (slope, step, floor), not a wall
                if (_self != null && _self.Owns(c))
                    continue;
                if (_ignoredRoot != null && c.transform.IsChildOf(_ignoredRoot))
                    continue;
                Rigidbody rb = c.attachedRigidbody;
                if (rb != null)
                {
                    if (rb.TryGetComponent(out BodyPart _))
                        continue; // other characters: crowd separation handles them
                    if (!rb.isKinematic && rb.mass < _minObstacleMass)
                        continue; // light props get shoved out of the way
                }
                return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ Stuck recovery

        private void UpdateStuck(Vector3 from, Vector3 desired)
        {
            float now = Time.time;
            bool resumed = now - _lastSteerTime > 0.5f;
            _lastSteerTime = now;
            if (resumed || RagdollMath.Flatten(from - _progressAnchor).sqrMagnitude > _stuckDistance * _stuckDistance)
            {
                _progressAnchor = from;
                _progressTime = now;
                return;
            }

            // Close to the goal is "arrived", not stuck.
            if (RemainingDistance(from) < 1.2f || now - _progressTime < _stuckTime || now < _detourUntil)
                return;

            Vector3 left = Quaternion.Euler(0f, -_detourAngle, 0f) * desired;
            Vector3 right = Quaternion.Euler(0f, _detourAngle, 0f) * desired;
            bool leftBlocked = Blocked(from, left);
            bool rightBlocked = Blocked(from, right);
            if (leftBlocked != rightBlocked)
                _detourSide = leftBlocked ? 1f : -1f;
            else
                _detourSide = -_detourSide; // both open or both closed: try the other way from last time

            _detourUntil = now + _detourTime;
            _nextRepath = 0f;
            _progressAnchor = from;
            _progressTime = now;
        }

        private void OnDrawGizmosSelected()
        {
            if (_pathValid)
            {
                Gizmos.color = Color.blue;
                for (int i = 1; i < _cornerCount; i++)
                    Gizmos.DrawLine(_corners[i - 1], _corners[i]);
                if (_cornerIndex < _cornerCount)
                    Gizmos.DrawWireSphere(_corners[_cornerIndex], 0.15f);
            }
            if (IsDetouring && _self != null && _self.IsValid)
            {
                Gizmos.color = Color.magenta;
                Gizmos.DrawWireSphere(_self.Position, 0.3f);
            }
        }
    }
}
