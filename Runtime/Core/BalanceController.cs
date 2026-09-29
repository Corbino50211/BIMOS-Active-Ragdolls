using System;
using UnityEngine;

namespace ActiveRagdoll
{
    public enum BalanceState
    {
        Balanced = 0,
        Stumbling = 1,
        Lost = 2,
    }

    public enum BalanceLossReason
    {
        None = 0,
        Tilt = 1,
        CapturePoint = 2,
        LowPelvis = 3,
        Airborne = 4,
        Knockdown = 5,
        LegsDisabled = 6,
        Forced = 7,
    }

    public delegate void StumbleHandler(float severity, Vector3 direction);

    /// <summary>
    /// Keeps the ragdoll upright with bounded "virtual muscle" forces and decides when it has lost its balance.
    /// <para>
    /// <b>Sensing</b>: centre of mass and its velocity, ground under the pelvis, per-foot ground contact, a
    /// smoothed support factor (0 airborne .. 1 both feet planted) and the capture point
    /// <c>x + v·√(h/g)</c> relative to the feet.
    /// </para>
    /// <para>
    /// <b>Forces</b> (all scaled by support × profile balance strength, all capped): an upright PD torque on the
    /// pelvis and chest toward the animated torso orientation (this is also how the body turns), and a vertical
    /// support force at the pelvis toward the animated pelvis height, limited by what the planted legs can reach.
    /// Take the feet off the ground, or hit the body harder than the caps can resist, and it falls for real.
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Active Ragdoll/Balance Controller")]
    [RequireComponent(typeof(ActiveRagdollCharacter))]
    public sealed class BalanceController : MonoBehaviour
    {
        private const float ReferenceMass = 70f;

        [Header("Ground Sensing")]
        [Tooltip("Layers the character can stand on. BIMOSRig and Ignore Raycast are always excluded.")]
        [SerializeField] private LayerMask _groundMask = ~0;
        [Tooltip("How far (m) a foot may be above the ground and still count as planted.")]
        [SerializeField, Min(0f)] private float _footGroundTolerance = 0.1f;
        [Tooltip("Radius (m) around each planted foot that counts as support.")]
        [SerializeField, Min(0f)] private float _footSupportRadius = 0.12f;
        [SerializeField, Min(0.01f)] private float _supportRiseRate = 10f;
        [SerializeField, Min(0.01f)] private float _supportFallRate = 5f;

        [Header("Upright Torque")]
        [Tooltip("Natural frequency (rad/s) of the torso's upright spring.")]
        [SerializeField, Min(0f)] private float _uprightFrequency = 10f;
        [SerializeField, Range(0f, 3f)] private float _uprightDampingRatio = 1f;
        [Tooltip("Scales the yaw (turning) part of the torque. The body's yaw inertia is much lower than its tilt inertia.")]
        [SerializeField, Range(0f, 2f)] private float _yawResponse = 0.6f;
        [Tooltip("Torque cap in N·m per kg of body mass. This is what a hard enough shove overcomes.")]
        [SerializeField, Min(0f)] private float _maxUprightTorquePerKg = 9f;
        [Tooltip("Share of the upright torque applied to the chest (the rest goes to the pelvis).")]
        [SerializeField, Range(0f, 1f)] private float _chestTorqueShare = 0.5f;

        [Header("Height Support")]
        [Tooltip("Fraction of body weight carried by the support force. The legs' muscles carry the rest, which keeps feet pressed into the ground.")]
        [SerializeField, Range(0f, 1.2f)] private float _gravityCompensation = 0.85f;
        [SerializeField, Min(0f)] private float _heightFrequency = 9f;
        [SerializeField, Range(0f, 3f)] private float _heightDampingRatio = 1f;
        [Tooltip("Support force cap as a multiple of body weight.")]
        [SerializeField, Min(0f)] private float _maxSupportRatio = 1.6f;
        [Tooltip("Fraction of full leg length the support force may extend the legs to.")]
        [SerializeField, Range(0.5f, 1f)] private float _legReachMargin = 0.97f;

        [Header("Balance Loss Detection")]
        [Tooltip("Balance error (m) that triggers a stumble and recovery steps.")]
        [SerializeField, Min(0f)] private float _stumbleDistance = 0.35f;
        [Tooltip("Balance error (m) beyond which the character falls.")]
        [SerializeField, Min(0f)] private float _fallDistance = 1.05f;
        [Tooltip("Weight of the static centre-of-mass offset from the feet (the rest of the error is velocity-based).")]
        [SerializeField, Range(0f, 1f)] private float _staticOffsetWeight = 0.5f;
        [SerializeField, Range(10f, 90f)] private float _fallTiltAngle = 55f;
        [SerializeField, Min(0f)] private float _tiltGraceTime = 0.12f;
        [Tooltip("Pelvis height (fraction of standing height) that counts as knocked down.")]
        [SerializeField, Range(0.1f, 0.9f)] private float _lowPelvisRatio = 0.55f;
        [SerializeField, Min(0f)] private float _lowPelvisGraceTime = 0.35f;
        [SerializeField, Min(0f)] private float _airborneGraceTime = 0.75f;
        [Tooltip("Impulse (N·s, for a 70 kg body) that causes a stagger.")]
        [SerializeField, Min(0f)] private float _staggerImpulse = 16f;
        [Tooltip("Impulse (N·s, for a 70 kg body) that knocks the character down outright.")]
        [SerializeField, Min(0f)] private float _knockdownImpulse = 60f;
        [Tooltip("Hits on the legs count this much more toward stagger/knockdown (sweeps).")]
        [SerializeField, Min(1f)] private float _legHitMultiplier = 1.4f;
        [SerializeField, Min(0f)] private float _stumbleCooldown = 0.25f;

        private ActiveRagdollCharacter _character;
        private Rigidbody _pelvis;
        private Rigidbody _chest;
        private Vector3 _chestUpLocal = Vector3.up;
        private readonly int[] _footIndex = { -1, -1 };
        private readonly int[] _upperLegIndex = { -1, -1 };
        private readonly float[] _footRestHeight = new float[2];
        private readonly float[] _hipRestHeight = new float[2];
        private readonly bool[] _footGrounded = new bool[2];
        private readonly Vector3[] _footGroundPoint = new Vector3[2];
        private readonly float[] _legFunction = { 1f, 1f };
        private float _pelvisAboveHips;
        private float _totalMass = ReferenceMass;
        private float _inertia = 6f;
        private int _resolvedGroundMask;
        private bool _initialized;

        private float _tiltTimer;
        private float _lowTimer;
        private float _airTimer;
        private float _fallTimer;
        private float _lastStumbleTime = float.NegativeInfinity;

        // ------------------------------------------------------------------ Public state

        public bool IsInitialized => _initialized;
        public BalanceState State { get; private set; }

        /// <summary>When false, balance is never declared lost (fallen and getting-up states turn this off).</summary>
        public bool DetectionEnabled { get; set; } = true;

        /// <summary>0 (airborne) .. 1 (planted), scaled by leg function.</summary>
        public float Support { get; private set; }
        public bool IsGrounded { get; private set; }
        public bool HasGround { get; private set; }
        public float GroundHeight { get; private set; }
        public Vector3 GroundNormal { get; private set; } = Vector3.up;
        public Vector3 CenterOfMass { get; private set; }
        public Vector3 ComVelocity { get; private set; }
        public Vector3 CapturePoint { get; private set; }
        public Vector3 SupportCenter { get; private set; }

        /// <summary>Horizontal vector from where the body should be to where it is heading (m). Recovery steps aim along it.</summary>
        public Vector3 BalanceOffset { get; private set; }
        public float BalanceError { get; private set; }
        public float TiltAngle { get; private set; }
        public float PelvisHeight { get; private set; }
        public float PelvisHeightRatio => _character != null ? PelvisHeight / Mathf.Max(0.01f, _character.StandingPelvisHeight) : 1f;
        public float StumbleDistance => _stumbleDistance;
        public float LegFunction => 0.5f * (_legFunction[0] + _legFunction[1]);
        public float MassScale => _totalMass / ReferenceMass;
        public float StaggerImpulse => _staggerImpulse * MassScale;
        public float KnockdownImpulse => _knockdownImpulse * MassScale;
        public int GroundMask => _resolvedGroundMask;

        public bool IsFootGrounded(BodySide side) => side == BodySide.Left ? _footGrounded[0] : side == BodySide.Right && _footGrounded[1];

        public event Action<BalanceLossReason> BalanceLost;
        public event StumbleHandler Stumbled;

        // ------------------------------------------------------------------ Public API

        /// <summary>Scales a leg's contribution to support (0 = leg disabled). Both legs disabled = cannot stand.</summary>
        public void SetLegFunction(BodySide side, float value)
        {
            if (side == BodySide.Center) return;
            _legFunction[side == BodySide.Left ? 0 : 1] = Mathf.Clamp01(value);
        }

        public void ForceLoseBalance(BalanceLossReason reason = BalanceLossReason.Forced)
        {
            if (!_initialized || State == BalanceState.Lost) return;
            Lose(reason);
        }

        /// <summary>Declares the character balanced again (e.g. after a successful get-up).</summary>
        public void ResetBalance()
        {
            State = BalanceState.Balanced;
            _tiltTimer = _lowTimer = _airTimer = _fallTimer = 0f;
        }

        /// <summary>Full reset for respawn/pooling, including leg function.</summary>
        public void ResetState()
        {
            ResetBalance();
            Support = 0f;
            _legFunction[0] = _legFunction[1] = 1f;
            DetectionEnabled = true;
        }

        internal void OnTeleported(float groundHeight)
        {
            ResetBalance();
            GroundHeight = groundHeight;
            HasGround = true;
            Support = LegFunction;
        }

        /// <summary>Feeds an impact impulse into stagger/knockdown detection.</summary>
        public void NotifyImpact(Vector3 impulse, int boneIndex)
        {
            if (!_initialized || !DetectionEnabled || State == BalanceState.Lost || _character.IsDead)
                return;

            float magnitude = impulse.magnitude;
            if (!(magnitude > 0f))
                return;

            var bones = _character.BonesInternal;
            if (boneIndex >= 0 && boneIndex < bones.Count && BoneRoles.IsLeg(bones[boneIndex].role))
                magnitude *= _legHitMultiplier;

            if (magnitude >= KnockdownImpulse)
            {
                Lose(BalanceLossReason.Knockdown);
                return;
            }

            if (magnitude >= StaggerImpulse)
                RaiseStumble(Mathf.InverseLerp(StaggerImpulse, KnockdownImpulse, magnitude), RagdollMath.Flatten(impulse));
        }

        // ------------------------------------------------------------------ Lifecycle

        internal bool Initialize(ActiveRagdollCharacter character)
        {
            _character = character;
            var bones = character.BonesInternal;
            _pelvis = character.Pelvis;
            _chest = character.Chest != null ? character.Chest : _pelvis;
            if (_pelvis == null)
                return false;

            _chestUpLocal = Quaternion.Inverse(_chest.rotation) * Vector3.up;
            float floor = character.AnimatedRoot.position.y;

            _footIndex[0] = character.GetBoneIndex(BoneRole.LeftFoot);
            _footIndex[1] = character.GetBoneIndex(BoneRole.RightFoot);
            _upperLegIndex[0] = character.GetBoneIndex(BoneRole.LeftUpperLeg);
            _upperLegIndex[1] = character.GetBoneIndex(BoneRole.RightUpperLeg);

            float hipsY = 0f;
            for (int f = 0; f < 2; f++)
            {
                if (_footIndex[f] < 0 || _upperLegIndex[f] < 0)
                {
                    Debug.LogError($"{ActiveRagdollCharacter.LogPrefix} '{name}': BalanceController needs both legs.", this);
                    return false;
                }
                _footRestHeight[f] = Mathf.Max(0.02f, bones[_footIndex[f]].body.worldCenterOfMass.y - floor);
                float hipY = bones[_upperLegIndex[f]].body.position.y;
                _hipRestHeight[f] = Mathf.Max(0.1f, hipY - floor);
                hipsY += hipY * 0.5f;
            }
            _pelvisAboveHips = _pelvis.position.y - hipsY;

            _totalMass = Mathf.Max(1f, character.TotalMass);
            Vector3 com = Vector3.zero;
            for (int i = 0; i < bones.Count; i++)
                com += bones[i].body.worldCenterOfMass * bones[i].body.mass;
            com /= _totalMass;
            float inertia = 0f;
            for (int i = 0; i < bones.Count; i++)
            {
                Rigidbody rb = bones[i].body;
                Vector3 principal = rb.inertiaTensor;
                inertia += rb.mass * (rb.worldCenterOfMass - com).sqrMagnitude + (principal.x + principal.y + principal.z) / 3f;
            }
            _inertia = Mathf.Max(0.1f, RagdollMath.IsFinite(inertia) ? inertia : _totalMass * 0.1f);

            _resolvedGroundMask = _groundMask.value & ~RagdollLayers.NonEnvironmentMask();
            GroundHeight = floor;
            HasGround = true;
            Support = 1f;
            PelvisHeight = character.StandingPelvisHeight;
            State = BalanceState.Balanced;
            _initialized = true;
            return true;
        }

        internal void Sense(float dt)
        {
            if (!_initialized)
                return;

            var bones = _character.BonesInternal;

            // Centre of mass.
            Vector3 com = Vector3.zero;
            Vector3 vel = Vector3.zero;
            float mass = 0f;
            for (int i = 0; i < bones.Count; i++)
            {
                Rigidbody rb = bones[i].body;
                if (rb == null) continue;
                float m = rb.mass;
                com += rb.worldCenterOfMass * m;
                vel += rb.linearVelocity * m;
                mass += m;
            }
            if (mass > 0f)
            {
                com /= mass;
                vel /= mass;
            }
            CenterOfMass = com;
            ComVelocity = vel;

            // Ground under the pelvis.
            Vector3 pelvisPos = _pelvis.position;
            float standing = _character.StandingPelvisHeight;
            if (PhysicsQuery.Raycast(pelvisPos + Vector3.up * 0.05f, Vector3.down, standing * 2.5f + 0.05f, _resolvedGroundMask, _character, null, out RaycastHit hit))
            {
                HasGround = true;
                GroundHeight = hit.point.y;
                GroundNormal = hit.normal;
            }
            else
            {
                HasGround = false;
                GroundNormal = Vector3.up;
                GroundHeight = pelvisPos.y - standing;
            }
            PelvisHeight = pelvisPos.y - GroundHeight;

            // Feet.
            int grounded = 0;
            Vector3 supportSum = Vector3.zero;
            for (int f = 0; f < 2; f++)
            {
                _footGrounded[f] = false;
                Rigidbody foot = bones[_footIndex[f]].body;
                if (foot == null) continue;
                Vector3 c = foot.worldCenterOfMass;
                float probe = 0.25f + _footRestHeight[f] + _footGroundTolerance;
                if (PhysicsQuery.Raycast(c + Vector3.up * 0.25f, Vector3.down, probe, _resolvedGroundMask, _character, null, out RaycastHit footHit))
                {
                    _footGrounded[f] = true;
                    _footGroundPoint[f] = footHit.point;
                    supportSum += footHit.point;
                    grounded++;
                }
            }
            IsGrounded = grounded > 0;
            SupportCenter = grounded > 0 ? supportSum / grounded : new Vector3(com.x, GroundHeight, com.z);

            float supportTarget = IsGrounded ? LegFunction : 0f;
            if (PelvisHeight > standing * 1.4f)
                supportTarget = 0f;
            Support = Mathf.MoveTowards(Support, supportTarget, (supportTarget > Support ? _supportRiseRate : _supportFallRate) * dt);

            TiltAngle = Vector3.Angle(_chest.rotation * _chestUpLocal, Vector3.up);

            // Capture point and balance error.
            float comHeight = Mathf.Max(0.1f, com.y - GroundHeight);
            float tc = Mathf.Sqrt(comHeight / Gravity);
            Vector3 flatVel = RagdollMath.Flatten(vel);
            CapturePoint = new Vector3(com.x + flatVel.x * tc, GroundHeight, com.z + flatVel.z * tc);

            LocomotionController locomotion = _character.Locomotion;
            Vector3 commanded = locomotion != null && locomotion.isActiveAndEnabled ? locomotion.CommandedVelocity : Vector3.zero;
            Vector3 offset = ComputeStaticOffset(com) * _staticOffsetWeight + (flatVel - commanded) * tc;
            BalanceOffset = RagdollMath.IsFinite(offset) ? offset : Vector3.zero;
            BalanceError = BalanceOffset.magnitude;

            if (!_character.IsDead)
                Detect(dt);
        }

        internal void ApplyForces(float dt)
        {
            if (!_initialized || _character.IsDead)
                return;

            float s = _character.Profile.balanceStrength * Support;
            if (s <= 1e-3f)
                return;

            if (_chest != _pelvis)
            {
                ApplyUpright(_pelvis, _character.TargetPelvisRotation, 1f - _chestTorqueShare, s);
                ApplyUpright(_chest, _character.TargetChestRotation, _chestTorqueShare, s);
            }
            else
            {
                ApplyUpright(_pelvis, _character.TargetPelvisRotation, 1f, s);
            }

            ApplyHeightSupport(Mathf.Min(1f, s));
        }

        // ------------------------------------------------------------------ Internals

        private static float Gravity => Mathf.Max(0.1f, -Physics.gravity.y);

        private void ApplyUpright(Rigidbody rb, Quaternion target, float share, float strength)
        {
            if (share <= 0f || rb == null)
                return;

            Vector3 up = Vector3.up;
            Vector3 error = RagdollMath.AngularError(rb.rotation, target);
            Vector3 yawError = up * Vector3.Dot(error, up);
            Vector3 tiltError = error - yawError;
            Vector3 w = rb.angularVelocity;
            Vector3 yawW = up * Vector3.Dot(w, up);
            Vector3 tiltW = w - yawW;

            float f = _uprightFrequency;
            float kp = f * f;
            float kd = 2f * _uprightDampingRatio * f;
            Vector3 alpha = (tiltError * kp - tiltW * kd) + (yawError * kp - yawW * kd) * _yawResponse;
            Vector3 torque = alpha * (_inertia * share * strength);
            torque = Vector3.ClampMagnitude(torque, _maxUprightTorquePerKg * _totalMass * share * strength);
            if (RagdollMath.IsFinite(torque))
                rb.AddTorque(torque, ForceMode.Force);
        }

        private void ApplyHeightSupport(float strength)
        {
            float target = Mathf.Min(_character.TargetPelvisHeight, ReachableHeight());
            float vy = _pelvis.linearVelocity.y;
            float w = _heightFrequency;
            float g = Gravity;
            float acceleration = g * _gravityCompensation + w * w * (target - PelvisHeight) - 2f * _heightDampingRatio * w * vy;
            float force = Mathf.Clamp(acceleration * _totalMass, 0f, _maxSupportRatio * _totalMass * g) * strength;
            if (force > 0f && RagdollMath.IsFinite(force))
                _pelvis.AddForce(Vector3.up * force, ForceMode.Force);
        }

        /// <summary>Highest pelvis height the planted legs can physically reach.</summary>
        private float ReachableHeight()
        {
            var bones = _character.BonesInternal;
            float best = 0f;
            bool any = false;
            for (int f = 0; f < 2; f++)
            {
                if (!_footGrounded[f])
                    continue;
                Rigidbody hip = bones[_upperLegIndex[f]].body;
                if (hip == null)
                    continue;
                Vector3 d = RagdollMath.Flatten(hip.position - _footGroundPoint[f]);
                float length = _hipRestHeight[f] * _legReachMargin;
                float horizontal = d.magnitude;
                float hipHeight = horizontal < length ? Mathf.Sqrt(length * length - horizontal * horizontal) : 0f;
                float pelvisHeight = hipHeight + (_footGroundPoint[f].y - GroundHeight) + _pelvisAboveHips;
                best = Mathf.Max(best, pelvisHeight);
                any = true;
            }
            return any ? Mathf.Max(best, _character.StandingPelvisHeight * 0.3f) : float.PositiveInfinity;
        }

        private Vector3 ComputeStaticOffset(Vector3 com)
        {
            Vector3 c = RagdollMath.Flatten(com);
            Vector3 closest;
            if (_footGrounded[0] && _footGrounded[1])
            {
                Vector3 a = RagdollMath.Flatten(_footGroundPoint[0]);
                Vector3 b = RagdollMath.Flatten(_footGroundPoint[1]);
                Vector3 ab = b - a;
                float t = ab.sqrMagnitude > 1e-6f ? Mathf.Clamp01(Vector3.Dot(c - a, ab) / ab.sqrMagnitude) : 0f;
                closest = a + ab * t;
            }
            else if (_footGrounded[0])
            {
                closest = RagdollMath.Flatten(_footGroundPoint[0]);
            }
            else if (_footGrounded[1])
            {
                closest = RagdollMath.Flatten(_footGroundPoint[1]);
            }
            else
            {
                return Vector3.zero;
            }

            Vector3 d = c - closest;
            float dist = d.magnitude;
            if (dist <= _footSupportRadius)
                return Vector3.zero;
            return d * ((dist - _footSupportRadius) / dist);
        }

        private void Detect(float dt)
        {
            if (!DetectionEnabled || State == BalanceState.Lost)
            {
                _tiltTimer = _lowTimer = _airTimer = _fallTimer = 0f;
                return;
            }

            if (LegFunction <= 0.01f)
            {
                Lose(BalanceLossReason.LegsDisabled);
                return;
            }

            _tiltTimer = TiltAngle > _fallTiltAngle ? _tiltTimer + dt : 0f;
            if (_tiltTimer > _tiltGraceTime)
            {
                Lose(BalanceLossReason.Tilt);
                return;
            }

            _lowTimer = PelvisHeightRatio < _lowPelvisRatio ? _lowTimer + dt : 0f;
            if (_lowTimer > _lowPelvisGraceTime)
            {
                Lose(BalanceLossReason.LowPelvis);
                return;
            }

            _airTimer = IsGrounded ? 0f : _airTimer + dt;
            if (_airTimer > _airborneGraceTime)
            {
                Lose(BalanceLossReason.Airborne);
                return;
            }

            if (BalanceError > _fallDistance)
            {
                _fallTimer += dt;
                if (_fallTimer > 0.05f)
                {
                    Lose(BalanceLossReason.CapturePoint);
                    return;
                }
            }
            else
            {
                _fallTimer = 0f;
            }

            if (BalanceError > _stumbleDistance)
                RaiseStumble(Mathf.InverseLerp(_stumbleDistance, _fallDistance, BalanceError), BalanceOffset);
            else if (State == BalanceState.Stumbling && BalanceError < _stumbleDistance * 0.5f && Time.time - _lastStumbleTime > 0.2f)
                State = BalanceState.Balanced;
        }

        private void RaiseStumble(float severity, Vector3 direction)
        {
            if (Time.time - _lastStumbleTime < _stumbleCooldown)
                return;
            _lastStumbleTime = Time.time;
            if (State == BalanceState.Balanced)
                State = BalanceState.Stumbling;
            Stumbled?.Invoke(Mathf.Clamp01(severity), direction);
        }

        private void Lose(BalanceLossReason reason)
        {
            State = BalanceState.Lost;
            _tiltTimer = _lowTimer = _airTimer = _fallTimer = 0f;
            BalanceLost?.Invoke(reason);
        }

        private void OnValidate()
        {
            _fallDistance = Mathf.Max(_fallDistance, _stumbleDistance + 0.05f);
            _knockdownImpulse = Mathf.Max(_knockdownImpulse, _staggerImpulse + 1f);
        }

        private void OnDrawGizmosSelected()
        {
            if (!_initialized)
                return;
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(CenterOfMass, 0.05f);
            Gizmos.DrawLine(CenterOfMass, new Vector3(CenterOfMass.x, GroundHeight, CenterOfMass.z));
            Gizmos.color = State == BalanceState.Balanced ? Color.green : State == BalanceState.Stumbling ? new Color(1f, 0.5f, 0f) : Color.red;
            Gizmos.DrawWireSphere(CapturePoint, 0.06f);
            Gizmos.DrawLine(SupportCenter, SupportCenter + BalanceOffset);
            Gizmos.color = Color.cyan;
            for (int f = 0; f < 2; f++)
                if (_footGrounded[f])
                    Gizmos.DrawWireSphere(_footGroundPoint[f], _footSupportRadius);
        }
    }
}
