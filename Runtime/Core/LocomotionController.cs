using UnityEngine;

namespace ActiveRagdoll
{
    /// <summary>
    /// Physics-based locomotion. The AI sets a desired velocity and facing; this component turns them into a
    /// bounded horizontal force on the torso and a heading for the animated rig (the balance controller then
    /// torques the body round to that heading). The legs are stepped by <see cref="ProceduralAnimator"/> from
    /// the body's <i>measured</i> velocity, so feet always match how the physics is actually moving.
    /// <para>
    /// Propulsion only acts while the feet support the body, and scales with support and the profile's
    /// locomotion strength. There is no root motion and no kinematic movement anywhere.
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Active Ragdoll/Locomotion Controller")]
    [RequireComponent(typeof(ActiveRagdollCharacter))]
    public sealed class LocomotionController : MonoBehaviour
    {
        [Header("Speeds")]
        [SerializeField, Min(0f)] private float _walkSpeed = 1.3f;
        [SerializeField, Min(0f)] private float _runSpeed = 3.2f;

        [Header("Propulsion")]
        [Tooltip("Proportional gain (1/s) from velocity error to acceleration.")]
        [SerializeField, Min(0f)] private float _velocityGain = 6f;
        [Tooltip("Force cap while speeding up (m/s²).")]
        [SerializeField, Min(0f)] private float _maxAcceleration = 5f;
        [Tooltip("Force cap while slowing down (m/s²).")]
        [SerializeField, Min(0f)] private float _maxDeceleration = 7f;

        [Tooltip("How fast the commanded velocity ramps up (m/s²). Kept well below the force cap so the body tracks it " +
                 "closely: tracking lag (ramp / gain) feeds the balance error, and too much of it reads as a stumble.")]
        [SerializeField, Min(0.1f)] private float _setpointAcceleration = 2.5f;
        [SerializeField, Min(0.1f)] private float _setpointDeceleration = 3.5f;
        [Tooltip("Minimum support (feet on the ground) before propulsion is applied.")]
        [SerializeField, Range(0f, 1f)] private float _minSupport = 0.25f;

        [Header("Turning")]
        [Tooltip("Degrees per second the heading turns toward the desired facing.")]
        [SerializeField, Min(0f)] private float _turnSpeed = 240f;
        [Tooltip("Maximum angle the heading may lead the physical body by.")]
        [SerializeField, Range(10f, 180f)] private float _maxHeadingLead = 100f;
        [Tooltip("Degrees per second the heading follows the body while the character is not in control.")]
        [SerializeField, Min(0f)] private float _bodyTrackingSpeed = 540f;

        private ActiveRagdollCharacter _character;
        private int[] _torsoIndices = System.Array.Empty<int>();
        private float _torsoMass;
        private Vector3 _desiredVelocity;
        private Vector3 _desiredFacing;
        private Vector3 _commandedVelocity;
        private float _headingYaw;
        private bool _initialized;

        public bool IsInitialized => _initialized;
        public float WalkSpeed => _walkSpeed;
        public float RunSpeed => _runSpeed;

        /// <summary>Top speed allowed right now (reduced by leg injuries).</summary>
        public float SpeedLimit
        {
            get
            {
                BalanceController balance = _character != null ? _character.Balance : null;
                float legs = balance != null && balance.IsInitialized ? Mathf.Max(0.35f, balance.LegFunction) : 1f;
                return _runSpeed * legs;
            }
        }

        /// <summary>Horizontal velocity the AI wants (m/s, world space). Clamped to <see cref="SpeedLimit"/>.</summary>
        public Vector3 DesiredVelocity
        {
            get => _desiredVelocity;
            set
            {
                Vector3 v = RagdollMath.Flatten(value);
                _desiredVelocity = RagdollMath.IsFinite(v) ? v : Vector3.zero;
            }
        }

        /// <summary>Direction to face (world). Zero means "face the direction of travel".</summary>
        public Vector3 DesiredFacing
        {
            get => _desiredFacing;
            set
            {
                Vector3 v = RagdollMath.Flatten(value);
                _desiredFacing = RagdollMath.IsFinite(v) ? v : Vector3.zero;
            }
        }

        /// <summary>The acceleration-limited velocity setpoint currently being tracked.</summary>
        public Vector3 CommandedVelocity => _commandedVelocity;

        /// <summary>True while the character has enough footing and strength to propel and steer itself.</summary>
        public bool IsControlling { get; private set; }

        public float HeadingYaw => _headingYaw;
        public Quaternion HeadingRotation => RagdollMath.YawRotation(_headingYaw);
        public Vector3 HeadingForward => HeadingRotation * Vector3.forward;

        public void Stop()
        {
            _desiredVelocity = Vector3.zero;
            _desiredFacing = Vector3.zero;
            _commandedVelocity = Vector3.zero;
        }

        public void SnapHeading(float yawDegrees)
        {
            if (RagdollMath.IsFinite(yawDegrees))
                _headingYaw = yawDegrees;
            _commandedVelocity = Vector3.zero;
        }

        internal bool Initialize(ActiveRagdollCharacter character)
        {
            _character = character;
            var list = new System.Collections.Generic.List<int>(3);
            _torsoMass = 0f;
            foreach (BoneRole role in new[] { BoneRole.Pelvis, BoneRole.Spine, BoneRole.Chest })
            {
                int i = character.GetBoneIndex(role);
                if (i < 0) continue;
                list.Add(i);
                _torsoMass += character.BonesInternal[i].body.mass;
            }
            _torsoIndices = list.ToArray();
            if (_torsoIndices.Length == 0 || _torsoMass <= 0f)
                return false;

            _headingYaw = character.GetBodyYaw();
            _initialized = true;
            return true;
        }

        internal void PhysicsStep(float dt)
        {
            if (!_initialized)
                return;

            float strength = _character.Profile.locomotionStrength;
            BalanceController balance = _character.Balance;
            bool hasBalance = balance != null && balance.isActiveAndEnabled && balance.IsInitialized;
            float support = hasBalance ? balance.Support : 1f;
            bool balanced = !hasBalance || balance.State != BalanceState.Lost;

            IsControlling = !_character.IsDead && strength > 0.05f && balanced && support >= _minSupport;

            float bodyYaw = _character.GetBodyYaw();
            if (IsControlling)
            {
                float targetYaw = _headingYaw;
                if (_desiredFacing.sqrMagnitude > 1e-4f)
                    targetYaw = RagdollMath.Yaw(_desiredFacing, _headingYaw);
                else if (_commandedVelocity.sqrMagnitude > 0.04f)
                    targetYaw = RagdollMath.Yaw(_commandedVelocity, _headingYaw);

                _headingYaw = Mathf.MoveTowardsAngle(_headingYaw, targetYaw, _turnSpeed * Mathf.Min(1f, strength) * dt);
                float lead = Mathf.DeltaAngle(bodyYaw, _headingYaw);
                if (Mathf.Abs(lead) > _maxHeadingLead)
                    _headingYaw = bodyYaw + Mathf.Sign(lead) * _maxHeadingLead;
            }
            else
            {
                _headingYaw = Mathf.MoveTowardsAngle(_headingYaw, bodyYaw, _bodyTrackingSpeed * dt);
            }
            _headingYaw = Mathf.Repeat(_headingYaw + 180f, 360f) - 180f;

            Vector3 desired = IsControlling ? Vector3.ClampMagnitude(_desiredVelocity, SpeedLimit) : Vector3.zero;
            float rampRate = desired.sqrMagnitude > _commandedVelocity.sqrMagnitude ? _setpointAcceleration : _setpointDeceleration;
            _commandedVelocity = Vector3.MoveTowards(_commandedVelocity, desired, rampRate * dt);

            if (!IsControlling)
                return;

            Vector3 actual = RagdollMath.Flatten(hasBalance ? balance.ComVelocity : _character.Pelvis.linearVelocity);
            Vector3 acceleration = (_commandedVelocity - actual) * _velocityGain;
            float limit = Vector3.Dot(acceleration, actual) < 0f ? _maxDeceleration : _maxAcceleration;
            acceleration = Vector3.ClampMagnitude(acceleration, limit);

            Vector3 totalForce = acceleration * (_character.TotalMass * Mathf.Min(1f, strength) * support);
            if (!RagdollMath.IsFinite(totalForce))
                return;

            var bones = _character.BonesInternal;
            for (int k = 0; k < _torsoIndices.Length; k++)
            {
                Rigidbody rb = bones[_torsoIndices[k]].body;
                if (rb != null)
                    rb.AddForce(totalForce * (rb.mass / _torsoMass), ForceMode.Force);
            }
        }

        private void OnValidate()
        {
            _runSpeed = Mathf.Max(_runSpeed, _walkSpeed);
        }
    }
}
