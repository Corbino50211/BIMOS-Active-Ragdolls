using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace ActiveRagdoll
{
    /// <summary>
    /// Hub of an active ragdoll character. It owns the mapping between the physical ragdoll and the
    /// animated (visual) rig and runs every subsystem in a fixed order so behaviour is deterministic:
    /// <list type="bullet">
    /// <item><b>Update</b> – moves the animated root under the physical pelvis (no root motion) and resets the
    /// animated bones to their rest pose so the Animator / procedural layer always start clean.</item>
    /// <item><b>LateUpdate</b> – procedural pose layer, then captures joint targets from the animated rig, then
    /// writes the simulated pose back onto the animated rig for rendering.</item>
    /// <item><b>FixedUpdate</b> – profile blending → balance sensing → locomotion → balance forces → joint motors.</item>
    /// </list>
    /// The ragdoll is never made kinematic. States only change how hard the muscles, balance and propulsion
    /// push, so the body reacts to external forces in every state, including death.
    /// </summary>
    [DefaultExecutionOrder(ExecutionOrder)]
    [DisallowMultipleComponent]
    [AddComponentMenu("Active Ragdoll/Active Ragdoll Character")]
    public sealed class ActiveRagdollCharacter : MonoBehaviour
    {
        /// <summary>Runs late so animation and user scripts have already posed the animated rig.</summary>
        public const int ExecutionOrder = 1000;

        internal const string LogPrefix = "[ActiveRagdoll]";
        private const float MaxTargetSpeed = 25f;

        private static readonly List<ActiveRagdollCharacter> s_all = new List<ActiveRagdollCharacter>();

        /// <summary>Every enabled, valid character. Do not modify.</summary>
        public static IReadOnlyList<ActiveRagdollCharacter> All => s_all;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => s_all.Clear();

        [Header("Rigs")]
        [Tooltip("Root of the animated (visual) rig: the model with the SkinnedMeshRenderer and optional Animator. It is moved under the physical pelvis every frame, so the ragdoll bodies must NOT be its children.")]
        [SerializeField] private Transform _animatedRoot;

        [Tooltip("Optional Animator on the animated rig supplying target poses. Root motion is disabled automatically.")]
        [SerializeField] private Animator _animator;

        [Tooltip("Parent of the ragdoll bodies. It should stay still; bodies move in world space.")]
        [SerializeField] private Transform _ragdollRoot;

        [SerializeField] private List<RagdollBone> _bones = new List<RagdollBone>();

        [Header("Physics")]
        [SerializeField] private RagdollPhysicsSettings _physics = new RagdollPhysicsSettings();

        [Header("Behaviour")]
        [SerializeField] private RagdollProfile _initialProfile = RagdollProfile.Standing;

        [Tooltip("Seconds over which the muscles relax after death. A short relax gives a more natural collapse than an instant cut.")]
        [SerializeField, Min(0.01f)] private float _deathRelaxTime = 0.35f;

        [Tooltip("Disable the Animator once a corpse has relaxed (saves CPU; the corpse is driven purely by physics).")]
        [SerializeField] private bool _disableAnimatorWhenDead = true;

        [Header("Visual Mapping")]
        [Tooltip("Map positions as well as rotations onto the visual bones. Keeps hands/feet exactly where the physics bodies are.")]
        [SerializeField] private bool _mapPositions = true;

        [Tooltip("Stop updating the visual rig of a corpse while every body is asleep.")]
        [SerializeField] private bool _freezeVisualsWhenAsleep = true;

        [Header("Safety")]
        [Tooltip("Pelvis heights below this count as falling out of the world.")]
        [SerializeField] private float _killHeight = -200f;

        [Tooltip("Any body faster than this (m/s) is treated as a solver explosion and the character is reset to its last valid position.")]
        [SerializeField, Min(1f)] private float _maxBodySpeed = 120f;

        [SerializeField] private bool _deactivateWhenOutOfWorld = true;

        [Header("Events")]
        [SerializeField] private UnityEvent _onDied = new UnityEvent();
        [SerializeField] private UnityEvent _onRevived = new UnityEvent();
        [SerializeField] private UnityEvent _onFellOutOfWorld = new UnityEvent();

        // Subsystems
        private JointMotorDriver _motors;
        private BalanceController _balance;
        private LocomotionController _locomotion;
        private ProceduralAnimator _procedural;
        private RagdollHealth _health;

        // Topology
        private readonly int[] _roleToIndex = new int[BoneRoles.Count];
        private readonly HashSet<Collider> _ownColliders = new HashSet<Collider>();
        private readonly HashSet<Rigidbody> _ownBodies = new HashSet<Rigidbody>();
        private int[] _mapOrder = Array.Empty<int>();
        private int _pelvisIndex = -1;
        private int _chestIndex = -1;
        private float _totalMass;

        // State
        private bool _initialized;
        private bool _isValid;
        private bool _isDead;
        private bool _outOfWorld;
        private bool _animatorDisabledByDeath;
        private RagdollProfile _profile;
        private RagdollProfile _profileFrom;
        private RagdollProfile _profileTarget;
        private float _profileBlend = 1f;
        private float _profileBlendDuration = 0.01f;

        // Bind data
        private Vector3 _pelvisForwardLocal = Vector3.forward;
        private Vector3 _pelvisUpLocal = Vector3.up;
        private Vector3 _pelvisRootOffset;
        private float _standingPelvisHeight = 1f;

        // Captured targets
        private bool _hasCapturedTargets;
        private Quaternion _targetPelvisRotation = Quaternion.identity;
        private Quaternion _targetChestRotation = Quaternion.identity;
        private float _targetPelvisHeight = 1f;

        // Runtime bookkeeping
        private float _lastBodyYaw;
        private bool _allAsleep;
        private bool _mappedWhileAsleep;
        private Vector3 _lastValidGroundPosition;
        private float _lastValidYaw;
        private int _faultCount;
        private float _faultWindowStart = float.NegativeInfinity;
        private int _stepCounter;

        // ------------------------------------------------------------------ Public API

        public event Action<ActiveRagdollCharacter> Died;
        public event Action<ActiveRagdollCharacter> Revived;

        /// <summary>Raised for every registered hit (collisions, bullets, stabs, strikes), including on corpses.</summary>
        public event RagdollHitHandler HitReceived;

        /// <summary>True once the rig passed validation and initialised. Invalid characters disable themselves.</summary>
        public bool IsValid => _isValid;
        public bool IsDead => _isDead;
        public IReadOnlyList<RagdollBone> Bones => _bones;
        internal List<RagdollBone> BonesInternal => _bones;
        public int BoneCount => _bones.Count;
        public int PelvisIndex => _pelvisIndex;
        public RagdollBone PelvisBone => _pelvisIndex >= 0 ? _bones[_pelvisIndex] : null;
        public Rigidbody Pelvis => _pelvisIndex >= 0 ? _bones[_pelvisIndex].body : null;

        /// <summary>The upper-torso body (chest, or spine if the rig has no chest body).</summary>
        public Rigidbody Chest => _chestIndex >= 0 ? _bones[_chestIndex].body : Pelvis;
        public int ChestIndex => _chestIndex;
        public float TotalMass => _totalMass;
        public Transform AnimatedRoot => _animatedRoot;
        public Transform RagdollRoot => _ragdollRoot;
        public Animator Animator => _animator;
        public RagdollPhysicsSettings PhysicsSettings => _physics;

        /// <summary>The current (blended) profile the subsystems read every physics step.</summary>
        public RagdollProfile Profile => _profile;
        public RagdollProfile TargetProfile => _profileTarget;
        public RagdollProfile InitialProfile => _initialProfile;

        public JointMotorDriver Motors => _motors;
        public BalanceController Balance => _balance;
        public LocomotionController Locomotion => _locomotion;
        public ProceduralAnimator Procedural => _procedural;
        public RagdollHealth Health => _health;

        /// <summary>World rotation the animated rig wants the pelvis at (used by the balance controller).</summary>
        public Quaternion TargetPelvisRotation => _targetPelvisRotation;

        /// <summary>World rotation the animated rig wants the upper torso at.</summary>
        public Quaternion TargetChestRotation => _targetChestRotation;

        /// <summary>Height of the animated pelvis above the animated root (floor), i.e. the height the body wants to hold.</summary>
        public float TargetPelvisHeight => _targetPelvisHeight;

        /// <summary>Pelvis height above the floor in the rest pose.</summary>
        public float StandingPelvisHeight => _standingPelvisHeight;

        /// <summary>Physical pelvis position (rendered/interpolated).</summary>
        public Vector3 Position => _pelvisIndex >= 0 ? _bones[_pelvisIndex].bodyTransform.position : transform.position;

        /// <summary>Facing used for the animated rig: the locomotion heading while the character is in control, otherwise the body's own facing.</summary>
        public Quaternion HeadingRotation
        {
            get
            {
                if (!_isDead && _locomotion != null && _locomotion.isActiveAndEnabled && _locomotion.IsInitialized)
                    return _locomotion.HeadingRotation;
                return RagdollMath.YawRotation(GetBodyYaw());
            }
        }

        public bool Owns(Collider c) => c != null && _ownColliders.Contains(c);

        public bool Owns(Rigidbody rb) => rb != null && _ownBodies.Contains(rb);

        public int GetBoneIndex(BoneRole role) => _initialized ? _roleToIndex[(int)role] : FindBoneIndexSlow(role);

        public bool TryGetBone(BoneRole role, out RagdollBone bone)
        {
            int i = GetBoneIndex(role);
            bone = i >= 0 ? _bones[i] : null;
            return bone != null;
        }

        public Rigidbody GetBody(BoneRole role)
        {
            int i = GetBoneIndex(role);
            return i >= 0 ? _bones[i].body : null;
        }

        /// <summary>
        /// Blends the character toward a new strength profile over <paramref name="blendTime"/> seconds.
        /// Re-sending the current target does not restart the blend. Ignored while dead (use <see cref="Revive"/>).
        /// </summary>
        public void SetProfile(in RagdollProfile profile, float blendTime)
        {
            if (_isDead)
                return;

            RagdollProfile p = profile.Sanitized();
            if (blendTime <= 0f || !_initialized)
            {
                _profile = _profileTarget = _profileFrom = p;
                _profileBlend = 1f;
                return;
            }

            if (Approximately(p, _profileTarget))
                return;

            _profileFrom = _profile;
            _profileTarget = p;
            _profileBlend = 0f;
            _profileBlendDuration = blendTime;
        }

        public void SetProfileImmediate(in RagdollProfile profile) => SetProfile(profile, 0f);

        /// <summary>Kills the character: muscles relax over the death relax time and it becomes a persistent, fully physical corpse.</summary>
        public void Kill()
        {
            if (!_isValid || _isDead)
                return;

            _isDead = true;
            _profileFrom = _profile;
            _profileTarget = RagdollProfile.Limp;
            _profileBlend = 0f;
            _profileBlendDuration = Mathf.Max(0.01f, _deathRelaxTime);

            if (_motors != null) _motors.OnKilled();
            if (_procedural != null) _procedural.OnKilled();
            if (_locomotion != null) _locomotion.Stop();

            Died?.Invoke(this);
            _onDied?.Invoke();
        }

        /// <summary>Brings a dead character back (for pooling/respawn). Optionally stands it up at its current location.</summary>
        public void Revive(bool standUp = true)
        {
            if (!_isValid)
                return;

            _isDead = false;
            _outOfWorld = false;
            if (_animator != null && _animatorDisabledByDeath)
                _animator.enabled = true;
            _animatorDisabledByDeath = false;

            if (_motors != null) _motors.ResetState();
            if (_balance != null) _balance.ResetState();
            if (_procedural != null) _procedural.ResetState();
            if (_health != null) _health.ResetHealth();

            if (standUp)
                Teleport(CurrentGroundPosition(), HeadingRotation);

            SetProfileImmediate(_initialProfile);
            Revived?.Invoke(this);
            _onRevived?.Invoke();
        }

        /// <summary>
        /// Instantly places the ragdoll in its rest (standing) pose with its feet at <paramref name="groundPosition"/>,
        /// facing <paramref name="facing"/>'s yaw, with all velocities cleared.
        /// </summary>
        public void Teleport(Vector3 groundPosition, Quaternion facing)
        {
            if (!_isValid || !RagdollMath.IsFinite(groundPosition))
                return;

            float yawDeg = RagdollMath.Yaw(facing * Vector3.forward, _lastBodyYaw);
            Quaternion yaw = RagdollMath.YawRotation(yawDeg);
            Vector3 pelvisPosition = groundPosition + Vector3.up * _standingPelvisHeight;

            for (int i = 0; i < _bones.Count; i++)
            {
                RagdollBone b = _bones[i];
                if (b.body == null) continue;
                Vector3 p = pelvisPosition + yaw * b.restPosition;
                Quaternion r = yaw * b.restRotation;
                b.body.position = p;
                b.body.rotation = r;
                b.bodyTransform.SetPositionAndRotation(p, r);
                b.body.linearVelocity = Vector3.zero;
                b.body.angularVelocity = Vector3.zero;
            }

            _lastBodyYaw = yawDeg;
            _lastValidYaw = yawDeg;
            _lastValidGroundPosition = groundPosition;
            _hasCapturedTargets = false;
            _mappedWhileAsleep = false;
            _allAsleep = false;

            if (_locomotion != null) _locomotion.SnapHeading(yawDeg);
            if (_balance != null) _balance.OnTeleported(groundPosition.y);
            if (_procedural != null) _procedural.ResetFeet();
        }

        /// <summary>Called by body parts (and weapons via body parts) to feed a hit into pain, balance, flinch and listeners.</summary>
        public void RegisterHit(in RagdollHit hit)
        {
            if (!_isValid)
                return;

            if (!_isDead && hit.boneIndex >= 0 && hit.boneIndex < _bones.Count)
            {
                if (_motors != null) _motors.AddImpactPain(hit.boneIndex, hit.impulse.magnitude, hit.damage);
                if (_balance != null) _balance.NotifyImpact(hit.impulse, hit.boneIndex);
                if (_procedural != null) _procedural.AddFlinch(hit.impulse, hit.severity);
            }

            HitReceived?.Invoke(hit);
        }

        /// <summary>Normalised hit severity from impulse and damage (0 = negligible, 1 = knockdown-level).</summary>
        public float ComputeSeverity(float impulseMagnitude, float appliedDamage)
        {
            float knockdown = _balance != null ? _balance.KnockdownImpulse : 60f * Mathf.Max(1f, _totalMass) / 70f;
            float fromImpulse = knockdown > 0f ? impulseMagnitude / knockdown : 0f;
            float maxHealth = _health != null ? Mathf.Max(1f, _health.MaxHealth) : 100f;
            float fromDamage = appliedDamage / maxHealth * 2.5f;
            return Mathf.Clamp01(Mathf.Max(fromImpulse, fromDamage));
        }

        /// <summary>Yaw (degrees) the physical body is facing. Robust when lying down: prone faces the head direction, supine the feet.</summary>
        public float GetBodyYaw()
        {
            if (_pelvisIndex < 0)
                return transform.eulerAngles.y;

            Rigidbody pelvis = _bones[_pelvisIndex].body;
            if (pelvis == null)
                return _lastBodyYaw;

            Quaternion r = pelvis.rotation;
            Vector3 forward = r * _pelvisForwardLocal;
            Vector3 flat = RagdollMath.Flatten(forward);
            if (flat.sqrMagnitude > 0.25f)
            {
                _lastBodyYaw = RagdollMath.Yaw(flat, _lastBodyYaw);
                return _lastBodyYaw;
            }

            Vector3 up = r * _pelvisUpLocal;
            Vector3 lying = forward.y < 0f ? up : -up;
            _lastBodyYaw = RagdollMath.Yaw(lying, _lastBodyYaw);
            return _lastBodyYaw;
        }

        /// <summary>
        /// Validates the rig without modifying anything (usable in edit mode).
        /// </summary>
        /// <returns>True when there are no errors.</returns>
        public bool Validate(List<string> errors, List<string> warnings)
        {
            errors ??= new List<string>();
            warnings ??= new List<string>();

            Transform animatedRoot = _animatedRoot != null ? _animatedRoot : (_animator != null ? _animator.transform : null);
            if (animatedRoot == null)
                errors.Add("No animated root assigned. Assign the visual model (the object with the SkinnedMeshRenderer / Animator).");

            if (_bones == null || _bones.Count == 0)
            {
                errors.Add("No ragdoll bones assigned. Use Tools > Active Ragdoll > Ragdoll Builder or 'Auto Assign Bones' in the inspector.");
                return false;
            }

            var seen = new bool[BoneRoles.Count];
            var bodies = new Dictionary<Rigidbody, int>();
            for (int i = 0; i < _bones.Count; i++)
            {
                RagdollBone b = _bones[i];
                if (b == null)
                {
                    errors.Add($"Bone slot {i} is empty.");
                    continue;
                }

                string label = $"Bone {i} ({b.role})";
                if (seen[(int)b.role])
                    errors.Add($"{label}: role {b.role} is assigned more than once.");
                seen[(int)b.role] = true;

                if (b.body == null)
                {
                    errors.Add($"{label}: no Rigidbody assigned.");
                    continue;
                }

                if (bodies.ContainsKey(b.body))
                    errors.Add($"{label}: Rigidbody '{b.body.name}' is used by more than one bone.");
                else
                    bodies.Add(b.body, i);

                if (b.body.isKinematic)
                    warnings.Add($"{label}: Rigidbody is kinematic; it will be made dynamic at runtime (the active ragdoll is always simulated).");

                if (animatedRoot != null && b.body.transform.IsChildOf(animatedRoot))
                    errors.Add($"{label}: Rigidbody '{b.body.name}' is inside the animated rig. The ragdoll must be a separate hierarchy because the animated root is moved every frame.");

                if (b.target == null)
                    warnings.Add($"{label}: no animated target bone; the body will hold its rest pose and will not be rendered.");
                else if (animatedRoot != null && !b.target.IsChildOf(animatedRoot))
                    warnings.Add($"{label}: target '{b.target.name}' is not under the animated root.");

                if (b.role == BoneRole.Pelvis)
                {
                    if (b.joint != null)
                        warnings.Add($"{label}: the pelvis is the root body and should not have a joint; it will be ignored.");
                }
                else if (b.joint == null)
                {
                    errors.Add($"{label}: missing ConfigurableJoint connecting it to its parent body.");
                }
                else
                {
                    if (b.joint.gameObject != b.body.gameObject)
                        errors.Add($"{label}: the joint must be on the same GameObject as the Rigidbody (child side of the joint).");
                    if (b.joint.connectedBody == null)
                        errors.Add($"{label}: joint has no connected body.");
                    if (b.joint.configuredInWorldSpace)
                        errors.Add($"{label}: joint 'Configured In World Space' must be off for motor targets to work.");
                    if (b.joint.xMotion != ConfigurableJointMotion.Locked || b.joint.yMotion != ConfigurableJointMotion.Locked || b.joint.zMotion != ConfigurableJointMotion.Locked)
                        warnings.Add($"{label}: linear motion is not locked; limbs may stretch.");
                }

                if (b.muscleMultiplier < 0f)
                    warnings.Add($"{label}: negative muscle multiplier will be treated as 0.");
            }

            // Parent links must point at other ragdoll bodies and form a tree rooted at the pelvis.
            for (int i = 0; i < _bones.Count; i++)
            {
                RagdollBone b = _bones[i];
                if (b == null || b.body == null || b.joint == null || b.role == BoneRole.Pelvis || b.joint.connectedBody == null)
                    continue;
                if (!bodies.ContainsKey(b.joint.connectedBody))
                {
                    errors.Add($"Bone {i} ({b.role}): joint is connected to '{b.joint.connectedBody.name}', which is not a ragdoll bone.");
                    continue;
                }

                int current = i;
                int steps = 0;
                while (steps++ <= _bones.Count)
                {
                    RagdollBone cb = _bones[current];
                    if (cb == null || cb.role == BoneRole.Pelvis || cb.joint == null || cb.joint.connectedBody == null)
                        break;
                    if (!bodies.TryGetValue(cb.joint.connectedBody, out current))
                        break;
                }

                if (steps > _bones.Count)
                    errors.Add($"Bone {i} ({b.role}): joint chain contains a cycle.");
            }

            if (!seen[(int)BoneRole.Pelvis]) errors.Add("Missing required bone: Pelvis.");
            if (!seen[(int)BoneRole.Spine] && !seen[(int)BoneRole.Chest]) errors.Add("Missing required bone: Spine or Chest.");
            if (!seen[(int)BoneRole.Head]) errors.Add("Missing required bone: Head.");
            BoneRole[] legs =
            {
                BoneRole.LeftUpperLeg, BoneRole.LeftLowerLeg, BoneRole.LeftFoot,
                BoneRole.RightUpperLeg, BoneRole.RightLowerLeg, BoneRole.RightFoot,
            };
            foreach (BoneRole r in legs)
                if (!seen[(int)r]) errors.Add($"Missing required bone: {r} (legs are needed to balance and walk).");

            if (!seen[(int)BoneRole.LeftUpperArm] || !seen[(int)BoneRole.LeftLowerArm] || !seen[(int)BoneRole.RightUpperArm] || !seen[(int)BoneRole.RightLowerArm])
                warnings.Add("One or both arms are missing; the character cannot attack with them.");

            if (_animator != null && _animator.applyRootMotion)
                warnings.Add("Animator 'Apply Root Motion' is on; it will be turned off (the physical body drives movement).");

            return errors.Count == 0;
        }

        // ------------------------------------------------------------------ Unity lifecycle

        private void Reset()
        {
            _animator = GetComponentInChildren<Animator>();
            _animatedRoot = _animator != null ? _animator.transform : null;
            Transform ragdoll = transform.Find("Ragdoll");
            if (ragdoll != null) _ragdollRoot = ragdoll;
        }

        private void OnValidate()
        {
            _physics ??= new RagdollPhysicsSettings();
            _physics.Sanitize();
            _initialProfile = _initialProfile.Sanitized();
            _deathRelaxTime = Mathf.Max(0.01f, _deathRelaxTime);
            _maxBodySpeed = Mathf.Max(1f, _maxBodySpeed);
        }

        private void Awake()
        {
            _isValid = Initialize();
            if (!_isValid)
                enabled = false;
        }

        private void OnEnable()
        {
            if (!_isValid)
                return;
            if (!s_all.Contains(this))
                s_all.Add(this);
            // Physics.IgnoreCollision pairs are dropped when colliders are deactivated, so re-apply them.
            ApplySelfCollision();
        }

        private void OnDisable()
        {
            s_all.Remove(this);
            // ConfigurableJoints re-capture their reference frame when re-activated. Put the ragdoll back in
            // its rest pose before deactivation so a pooled corpse doesn't come back with twisted joint frames.
            if (_isValid && !gameObject.activeInHierarchy)
                Teleport(CurrentGroundPosition(), RagdollMath.YawRotation(GetBodyYaw()));
        }

        private void Update()
        {
            if (!_isValid || VisualsFrozen || !NeedsTargets)
                return;
            FollowAnimatedRoot();
            ResetAnimatedPose();
        }

        private void LateUpdate()
        {
            if (!_isValid || VisualsFrozen)
                return;

            float dt = Time.deltaTime;
            if (NeedsTargets)
            {
                if (_procedural != null && _procedural.isActiveAndEnabled)
                    _procedural.ModifyPose(dt);
                CaptureTargets(dt);
            }
            else if (_pelvisIndex >= 0)
            {
                // Corpse: keep the animated root near the body so renderer bounds and child objects follow it.
                FollowAnimatedRoot();
            }

            MapToVisual();
            _mappedWhileAsleep = _allAsleep;
        }

        private void FixedUpdate()
        {
            if (!_isValid)
                return;

            float dt = Time.fixedDeltaTime;
            if (dt <= 0f)
                return;

            if (!CheckSimulationHealth())
                return;

            UpdateProfileBlend(dt);
            UpdateSleepState();

            if (_isDead)
            {
                if (_disableAnimatorWhenDead && !_animatorDisabledByDeath && _animator != null && _profileBlend >= 1f)
                {
                    _animator.enabled = false;
                    _animatorDisabledByDeath = true;
                }

                if (_allAsleep)
                    return;
            }

            bool balanceActive = _balance != null && _balance.isActiveAndEnabled;
            if (balanceActive)
                _balance.Sense(dt);

            if (!_isDead)
            {
                if (_locomotion != null && _locomotion.isActiveAndEnabled)
                    _locomotion.PhysicsStep(dt);
                if (balanceActive)
                    _balance.ApplyForces(dt);
            }

            if (_motors != null && _motors.isActiveAndEnabled)
                _motors.PhysicsStep(dt);
        }

        private void OnDrawGizmosSelected()
        {
            if (_bones == null)
                return;
            Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.9f);
            for (int i = 0; i < _bones.Count; i++)
            {
                RagdollBone b = _bones[i];
                if (b == null || b.body == null || b.joint == null || b.joint.connectedBody == null)
                    continue;
                Gizmos.DrawLine(b.body.transform.position, b.joint.connectedBody.transform.position);
            }
        }

        // ------------------------------------------------------------------ Initialisation

        private bool Initialize()
        {
            var errors = new List<string>();
            var warnings = new List<string>();
            _physics ??= new RagdollPhysicsSettings();
            _physics.Sanitize();

            bool ok = Validate(errors, warnings);
            foreach (string w in warnings)
                Debug.LogWarning($"{LogPrefix} '{name}': {w}", this);
            if (!ok)
            {
                foreach (string e in errors)
                    Debug.LogError($"{LogPrefix} '{name}': {e}", this);
                Debug.LogError($"{LogPrefix} '{name}' is disabled because its rig is invalid.", this);
                return false;
            }

            if (_animatedRoot == null)
                _animatedRoot = _animator.transform;

            for (int r = 0; r < _roleToIndex.Length; r++)
                _roleToIndex[r] = -1;

            var bodyToIndex = new Dictionary<Rigidbody, int>(_bones.Count);
            for (int i = 0; i < _bones.Count; i++)
            {
                RagdollBone b = _bones[i];
                b.index = i;
                b.parentIndex = -1;
                b.bodyTransform = b.body.transform;
                _roleToIndex[(int)b.role] = i;
                bodyToIndex[b.body] = i;
                _ownBodies.Add(b.body);
            }

            _pelvisIndex = _roleToIndex[(int)BoneRole.Pelvis];
            _chestIndex = _roleToIndex[(int)BoneRole.Chest] >= 0 ? _roleToIndex[(int)BoneRole.Chest] : _roleToIndex[(int)BoneRole.Spine];
            if (_ragdollRoot == null)
                _ragdollRoot = _bones[_pelvisIndex].bodyTransform.parent;

            // Topology
            var childLists = new List<int>[_bones.Count];
            for (int i = 0; i < _bones.Count; i++)
                childLists[i] = new List<int>(4);
            for (int i = 0; i < _bones.Count; i++)
            {
                RagdollBone b = _bones[i];
                if (i == _pelvisIndex || b.joint == null)
                    continue;
                int parent = bodyToIndex[b.joint.connectedBody];
                b.parentIndex = parent;
                childLists[parent].Add(i);
            }
            for (int i = 0; i < _bones.Count; i++)
                _bones[i].children = childLists[i].ToArray();

            ConfigureBodies();
            CacheBindPose();
            ComputeMassProperties();
            BindBodyParts();
            ConfigureAnimator();
            DisableStrayPhysicsOnAnimatedRig();

            _initialized = true;
            _profile = _profileFrom = _profileTarget = _initialProfile.Sanitized();
            _profileBlend = 1f;

            // Subsystems (optional, each disables itself if it can't initialise).
            _motors = GetComponent<JointMotorDriver>();
            _balance = GetComponent<BalanceController>();
            _locomotion = GetComponent<LocomotionController>();
            _procedural = GetComponent<ProceduralAnimator>();
            _health = GetComponent<RagdollHealth>();

            InitializeSubsystem(_motors, _motors != null && _motors.Initialize(this));
            InitializeSubsystem(_balance, _balance != null && _balance.Initialize(this));
            InitializeSubsystem(_locomotion, _locomotion != null && _locomotion.Initialize(this));
            InitializeSubsystem(_procedural, _procedural != null && _procedural.Initialize(this));
            if (_health != null) _health.Bind(this);

            if (_motors == null)
                Debug.LogWarning($"{LogPrefix} '{name}' has no JointMotorDriver; it will behave as a passive ragdoll.", this);

            CaptureTargets(0f);
            Rigidbody pelvis = _bones[_pelvisIndex].body;
            _lastValidGroundPosition = pelvis.position - Vector3.up * _standingPelvisHeight;
            _lastValidYaw = GetBodyYaw();
            return true;
        }

        private void InitializeSubsystem(Behaviour component, bool success)
        {
            if (component == null || success)
                return;
            Debug.LogError($"{LogPrefix} '{name}': {component.GetType().Name} failed to initialise and has been disabled.", component);
            component.enabled = false;
        }

        private void ConfigureBodies()
        {
            int layer = -1;
            if (!string.IsNullOrEmpty(_physics.ragdollLayer))
            {
                layer = LayerMask.NameToLayer(_physics.ragdollLayer);
                if (layer < 0)
                    Debug.LogWarning($"{LogPrefix} '{name}': layer '{_physics.ragdollLayer}' does not exist (Tools > Active Ragdoll > Setup Layers). Keeping authored layers.", this);
            }

            for (int i = 0; i < _bones.Count; i++)
            {
                RagdollBone b = _bones[i];
                Rigidbody rb = b.body;
                rb.isKinematic = false;
                rb.useGravity = true;
                rb.solverIterations = _physics.solverIterations;
                rb.solverVelocityIterations = _physics.solverVelocityIterations;
                rb.maxAngularVelocity = _physics.maxAngularVelocity;
                rb.maxDepenetrationVelocity = _physics.maxDepenetrationVelocity;
                rb.linearDamping = _physics.linearDamping;
                rb.angularDamping = _physics.angularDamping;
                rb.interpolation = _physics.interpolation;
                rb.collisionDetectionMode = IsFastSegment(b.role) ? _physics.fastSegmentCollisionDetection : _physics.defaultCollisionDetection;

                var list = new List<Collider>(2);
                foreach (Collider c in rb.GetComponentsInChildren<Collider>(true))
                {
                    if (c.GetComponentInParent<Rigidbody>() != rb)
                        continue;
                    list.Add(c);
                    _ownColliders.Add(c);
                    if (layer >= 0) c.gameObject.layer = layer;
                }
                b.colliders = list.ToArray();
                if (layer >= 0) rb.gameObject.layer = layer;
                if (b.colliders.Length == 0)
                    Debug.LogWarning($"{LogPrefix} '{name}': {b.role} has no colliders and cannot be hit.", rb);

                ConfigurableJoint j = b.joint;
                if (j != null && i != _pelvisIndex)
                {
                    j.enableCollision = false;
                    if (j.rotationDriveMode != RotationDriveMode.Slerp)
                        j.rotationDriveMode = RotationDriveMode.Slerp;
                }
            }
        }

        private static bool IsFastSegment(BoneRole role)
        {
            BoneGroup g = BoneRoles.GetGroup(role);
            return g == BoneGroup.Hand || g == BoneGroup.Foot || role == BoneRole.LeftLowerArm || role == BoneRole.RightLowerArm;
        }

        private void CacheBindPose()
        {
            RagdollBone pelvis = _bones[_pelvisIndex];
            float bindYaw = RagdollMath.Yaw(_animatedRoot.forward, 0f);
            Quaternion bindYawRot = RagdollMath.YawRotation(bindYaw);
            Quaternion bindYawInv = Quaternion.Inverse(bindYawRot);
            Vector3 pelvisPos = pelvis.bodyTransform.position;
            Quaternion pelvisRot = pelvis.bodyTransform.rotation;

            _pelvisForwardLocal = Quaternion.Inverse(pelvisRot) * (bindYawRot * Vector3.forward);
            _pelvisUpLocal = Quaternion.Inverse(pelvisRot) * Vector3.up;
            _lastBodyYaw = bindYaw;

            for (int i = 0; i < _bones.Count; i++)
            {
                RagdollBone b = _bones[i];
                Transform bt = b.bodyTransform;
                b.restPosition = bindYawInv * (bt.position - pelvisPos);
                b.restRotation = bindYawInv * bt.rotation;

                if (b.parentIndex >= 0)
                {
                    Transform parent = _bones[b.parentIndex].bodyTransform;
                    b.initialRelativeRotation = Quaternion.Inverse(parent.rotation) * bt.rotation;
                    b.jointSpace = RagdollMath.JointSpace(b.joint);
                    b.jointSpaceInverse = Quaternion.Inverse(b.jointSpace);
                }
                b.targetRelativeRotation = b.initialRelativeRotation;

                if (b.target != null)
                {
                    b.targetToBody = Quaternion.Inverse(b.target.rotation) * bt.rotation;
                    b.bodyToTarget = Quaternion.Inverse(b.targetToBody);
                    b.targetOriginInBody = bt.InverseTransformPoint(b.target.position);
                    b.bodyOriginInTarget = b.target.InverseTransformPoint(bt.position);
                    b.targetRestLocalPosition = b.target.localPosition;
                    b.targetRestLocalRotation = b.target.localRotation;
                    b.targetDepth = DepthUnder(b.target, _animatedRoot);
                }
            }

            // Visual mapping order: parents before children in the animated hierarchy.
            var order = new List<int>(_bones.Count);
            for (int i = 0; i < _bones.Count; i++)
                if (_bones[i].target != null)
                    order.Add(i);
            order.Sort((a, c) => _bones[a].targetDepth.CompareTo(_bones[c].targetDepth));
            _mapOrder = order.ToArray();

            // Heights/offsets use the pelvis *body* origin: that is what the balance controller measures and
            // what captured targets describe (target.TransformPoint(bodyOriginInTarget)).
            Vector3 rootPos = _animatedRoot.position;
            _standingPelvisHeight = pelvisPos.y - rootPos.y;
            if (_standingPelvisHeight < 0.2f)
            {
                Debug.LogWarning($"{LogPrefix} '{name}': the pelvis is only {_standingPelvisHeight:0.00} m above the animated root. The animated root should sit at floor level in the rest pose.", this);
                _standingPelvisHeight = Mathf.Max(0.2f, _standingPelvisHeight);
            }
            _pelvisRootOffset = RagdollMath.Flatten(Quaternion.Inverse(_animatedRoot.rotation) * (pelvisPos - rootPos));
        }

        private static int DepthUnder(Transform t, Transform root)
        {
            int depth = 0;
            while (t != null && t != root)
            {
                depth++;
                t = t.parent;
            }
            return depth;
        }

        private void ComputeMassProperties()
        {
            _totalMass = 0f;
            for (int i = 0; i < _bones.Count; i++)
                _totalMass += _bones[i].body.mass;

            // Post-order accumulation of subtree mass (children always have larger depth than parents).
            var depth = new int[_bones.Count];
            for (int i = 0; i < _bones.Count; i++)
            {
                int d = 0;
                int p = _bones[i].parentIndex;
                while (p >= 0 && d <= _bones.Count)
                {
                    d++;
                    p = _bones[p].parentIndex;
                }
                depth[i] = d;
                _bones[i].subtreeMass = _bones[i].body.mass;
            }

            var byDepth = new List<int>(_bones.Count);
            for (int i = 0; i < _bones.Count; i++) byDepth.Add(i);
            byDepth.Sort((a, b) => depth[b].CompareTo(depth[a]));
            foreach (int i in byDepth)
            {
                int p = _bones[i].parentIndex;
                if (p >= 0)
                    _bones[p].subtreeMass += _bones[i].subtreeMass;
            }
        }

        private void BindBodyParts()
        {
            for (int i = 0; i < _bones.Count; i++)
            {
                RagdollBone b = _bones[i];
                BodyPart part = b.body.GetComponent<BodyPart>();
                if (part == null)
                {
                    part = b.body.gameObject.AddComponent<BodyPart>();
                    part.SetRole(b.role);
                }
                part.Bind(this, i);
                b.part = part;
            }
        }

        private void ConfigureAnimator()
        {
            if (_animator == null)
                return;
            _animator.applyRootMotion = false;
            _animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            if (_animator.updateMode != AnimatorUpdateMode.Normal)
            {
                Debug.LogWarning($"{LogPrefix} '{name}': Animator update mode set to Normal. Targets are captured once per rendered frame; physics-rate animation adds cost without improving the muscles' tracking.", this);
                _animator.updateMode = AnimatorUpdateMode.Normal;
            }
        }

        private void DisableStrayPhysicsOnAnimatedRig()
        {
            int disabled = 0;
            foreach (Collider c in _animatedRoot.GetComponentsInChildren<Collider>(true))
            {
                if (_ownColliders.Contains(c) || !c.enabled)
                    continue;
                c.enabled = false;
                disabled++;
            }

            foreach (Rigidbody rb in _animatedRoot.GetComponentsInChildren<Rigidbody>(true))
            {
                if (_ownBodies.Contains(rb))
                    continue;
                rb.isKinematic = true;
                rb.detectCollisions = false;
                disabled++;
            }

            if (disabled > 0)
                Debug.LogWarning($"{LogPrefix} '{name}': disabled {disabled} collider(s)/rigidbody(s) on the animated rig. Visual bones are moved every frame and must not carry physics (remove leftover ragdoll-wizard components).", this);
        }

        private void ApplySelfCollision()
        {
            int n = _bones.Count;
            if (n == 0)
                return;

            SelfCollisionMode mode = _physics.selfCollision;
            int maxDepth = mode == SelfCollisionMode.Full ? 1 : _physics.selfCollisionIgnoreDepth;

            for (int a = 0; a < n; a++)
            {
                for (int b = a + 1; b < n; b++)
                {
                    bool ignore = mode == SelfCollisionMode.Disabled || JointDistance(a, b, maxDepth) <= maxDepth;
                    if (!ignore)
                        continue;
                    Collider[] ca = _bones[a].colliders;
                    Collider[] cb = _bones[b].colliders;
                    for (int x = 0; x < ca.Length; x++)
                        for (int y = 0; y < cb.Length; y++)
                            if (ca[x] != null && cb[y] != null)
                                Physics.IgnoreCollision(ca[x], cb[y], true);
                }
            }
        }

        /// <summary>Number of joints between two bones, or int.MaxValue if more than <paramref name="limit"/>.</summary>
        private int JointDistance(int a, int b, int limit)
        {
            // Walk up from both until the lowest common ancestor (trees are shallow: <= 5 levels).
            int da = 0;
            int x = a;
            while (x >= 0)
            {
                int db = 0;
                int y = b;
                while (y >= 0)
                {
                    if (x == y)
                    {
                        int d = da + db;
                        return d <= limit ? d : int.MaxValue;
                    }
                    y = _bones[y].parentIndex;
                    db++;
                }
                x = _bones[x].parentIndex;
                da++;
            }
            return int.MaxValue;
        }

        private int FindBoneIndexSlow(BoneRole role)
        {
            if (_bones == null) return -1;
            for (int i = 0; i < _bones.Count; i++)
                if (_bones[i] != null && _bones[i].role == role)
                    return i;
            return -1;
        }

        // ------------------------------------------------------------------ Frame pipeline

        private bool NeedsTargets => !_isDead || _profile.muscleStrength > 0.001f;

        private bool VisualsFrozen => _freezeVisualsWhenAsleep && _isDead && _allAsleep && _mappedWhileAsleep;

        private void FollowAnimatedRoot()
        {
            Transform pelvisT = _bones[_pelvisIndex].bodyTransform;
            Vector3 p = pelvisT.position;
            float groundY = p.y - _standingPelvisHeight;
            if (_balance != null && _balance.IsInitialized && _balance.HasGround)
                groundY = Mathf.Max(_balance.GroundHeight, p.y - _standingPelvisHeight * 1.5f);

            Quaternion yaw = HeadingRotation;
            Vector3 offset = yaw * _pelvisRootOffset;
            _animatedRoot.SetPositionAndRotation(new Vector3(p.x - offset.x, groundY, p.z - offset.z), yaw);
        }

        private void ResetAnimatedPose()
        {
            for (int k = 0; k < _mapOrder.Length; k++)
            {
                RagdollBone b = _bones[_mapOrder[k]];
                b.target.SetLocalPositionAndRotation(b.targetRestLocalPosition, b.targetRestLocalRotation);
            }
        }

        private void CaptureTargets(float dt)
        {
            bool computeVelocity = _hasCapturedTargets && dt > 1e-5f;
            float invDt = computeVelocity ? 1f / dt : 0f;

            for (int i = 0; i < _bones.Count; i++)
            {
                RagdollBone b = _bones[i];
                if (b.target == null)
                {
                    b.hasTarget = false;
                    continue;
                }

                Quaternion rot = b.target.rotation * b.targetToBody;
                Vector3 pos = b.target.TransformPoint(b.bodyOriginInTarget);
                b.targetVelocity = computeVelocity ? Vector3.ClampMagnitude((pos - b.targetPosition) * invDt, MaxTargetSpeed) : Vector3.zero;
                b.targetPosition = pos;
                b.targetRotation = rot;
                b.hasTarget = true;
            }

            for (int i = 0; i < _bones.Count; i++)
            {
                RagdollBone b = _bones[i];
                if (b.parentIndex < 0)
                    continue;
                RagdollBone parent = _bones[b.parentIndex];
                b.targetRelativeRotation = b.hasTarget && parent.hasTarget
                    ? Quaternion.Inverse(parent.targetRotation) * b.targetRotation
                    : b.initialRelativeRotation;
            }

            RagdollBone pelvis = _bones[_pelvisIndex];
            Quaternion heading = HeadingRotation;
            _targetPelvisRotation = pelvis.hasTarget ? pelvis.targetRotation : heading * pelvis.restRotation;
            RagdollBone chest = _bones[_chestIndex >= 0 ? _chestIndex : _pelvisIndex];
            _targetChestRotation = chest.hasTarget ? chest.targetRotation : heading * chest.restRotation;
            _targetPelvisHeight = pelvis.hasTarget ? pelvis.targetPosition.y - _animatedRoot.position.y : _standingPelvisHeight;
            _hasCapturedTargets = true;
        }

        private void MapToVisual()
        {
            for (int k = 0; k < _mapOrder.Length; k++)
            {
                int i = _mapOrder[k];
                RagdollBone b = _bones[i];
                Transform bt = b.bodyTransform;
                if (bt == null)
                    continue;
                Quaternion rot = bt.rotation * b.bodyToTarget;
                if (_mapPositions || i == _pelvisIndex)
                    b.target.SetPositionAndRotation(bt.TransformPoint(b.targetOriginInBody), rot);
                else
                    b.target.rotation = rot;
            }
        }

        private void UpdateProfileBlend(float dt)
        {
            if (_profileBlend >= 1f)
                return;
            _profileBlend = Mathf.Min(1f, _profileBlend + dt / Mathf.Max(0.001f, _profileBlendDuration));
            _profile = RagdollProfile.Lerp(_profileFrom, _profileTarget, _profileBlend);
        }

        private void UpdateSleepState()
        {
            bool asleep = true;
            for (int i = 0; i < _bones.Count; i++)
            {
                Rigidbody rb = _bones[i].body;
                if (rb != null && !rb.IsSleeping())
                {
                    asleep = false;
                    break;
                }
            }
            if (!asleep)
                _mappedWhileAsleep = false;
            _allAsleep = asleep;
        }

        private static bool Approximately(in RagdollProfile a, in RagdollProfile b)
        {
            const float e = 0.001f;
            return Mathf.Abs(a.muscleStrength - b.muscleStrength) < e
                && Mathf.Abs(a.balanceStrength - b.balanceStrength) < e
                && Mathf.Abs(a.locomotionStrength - b.locomotionStrength) < e
                && Mathf.Abs(a.gaitWeight - b.gaitWeight) < e
                && Mathf.Abs(a.guardWeight - b.guardWeight) < e
                && Mathf.Abs(a.lookWeight - b.lookWeight) < e;
        }

        // ------------------------------------------------------------------ Safety

        private Vector3 CurrentGroundPosition()
        {
            Vector3 p = _pelvisIndex >= 0 && _bones[_pelvisIndex].body != null ? _bones[_pelvisIndex].body.position : transform.position;
            float groundY = p.y - _standingPelvisHeight;
            if (_balance != null && _balance.IsInitialized && _balance.HasGround)
                groundY = _balance.GroundHeight;
            return new Vector3(p.x, groundY, p.z);
        }

        private bool CheckSimulationHealth()
        {
            Rigidbody pelvis = _bones[_pelvisIndex].body;
            if (pelvis == null)
            {
                Debug.LogError($"{LogPrefix} '{name}': pelvis body was destroyed; disabling character.", this);
                _isValid = false;
                enabled = false;
                return false;
            }

            Vector3 pos = pelvis.position;
            Vector3 vel = pelvis.linearVelocity;
            float maxSpeedSqr = _maxBodySpeed * _maxBodySpeed;
            if (!RagdollMath.IsFinite(pos) || !RagdollMath.IsFinite(vel) || vel.sqrMagnitude > maxSpeedSqr)
            {
                RecoverFromFault("pelvis state is invalid (NaN or runaway velocity)");
                return false;
            }

            // Full sweep of every body a few times per second.
            if ((++_stepCounter & 31) == 0)
            {
                for (int i = 0; i < _bones.Count; i++)
                {
                    Rigidbody rb = _bones[i].body;
                    if (rb == null)
                        continue;
                    Vector3 v = rb.linearVelocity;
                    if (!RagdollMath.IsFinite(rb.position) || !RagdollMath.IsFinite(v) || v.sqrMagnitude > maxSpeedSqr)
                    {
                        RecoverFromFault($"{_bones[i].role} state is invalid (NaN or runaway velocity)");
                        return false;
                    }
                }
            }

            if (pos.y < _killHeight)
            {
                HandleOutOfWorld();
                return false;
            }

            _lastValidGroundPosition = CurrentGroundPosition();
            _lastValidYaw = _lastBodyYaw;
            return true;
        }

        private void RecoverFromFault(string reason)
        {
            float now = Time.time;
            if (now - _faultWindowStart > 2f)
            {
                _faultWindowStart = now;
                _faultCount = 0;
            }
            _faultCount++;

            if (_faultCount > 5)
            {
                Debug.LogError($"{LogPrefix} '{name}': simulation keeps failing ({reason}); deactivating the character. Check joint limits, masses and overlapping colliders.", this);
                Kill();
                gameObject.SetActive(false);
                return;
            }

            if (_faultCount == 1)
                Debug.LogWarning($"{LogPrefix} '{name}': {reason}. Resetting to the last valid position.", this);
            Teleport(_lastValidGroundPosition, RagdollMath.YawRotation(_lastValidYaw));
        }

        private void HandleOutOfWorld()
        {
            if (_outOfWorld)
                return;
            _outOfWorld = true;
            Kill();
            _onFellOutOfWorld?.Invoke();
            if (_deactivateWhenOutOfWorld)
                gameObject.SetActive(false);
        }

        // ------------------------------------------------------------------ Editor support

        internal void EditorAssign(Transform animatedRoot, Animator animator, Transform ragdollRoot, List<RagdollBone> bones)
        {
            _animatedRoot = animatedRoot;
            _animator = animator;
            _ragdollRoot = ragdollRoot;
            _bones = bones ?? new List<RagdollBone>();
        }
    }
}
