using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using Object = UnityEngine.Object;

namespace ActiveRagdoll
{
    /// <summary>
    /// Procedural pose layer applied to the animated rig after the Animator and before the joint targets are
    /// captured. It makes the system fully functional without any animation clips, and blends over clips
    /// when they exist:
    /// <list type="bullet">
    /// <item><b>Gait</b> – BIMOS-style stepping (see BIMOS.Feet): feet stay planted in the world and step
    /// along a quadratic-Bezier arc when they fall too far behind a target that leads the <i>measured</i> body
    /// velocity. Capture-point feedback moves the targets under a stumbling body so it takes recovery steps.</item>
    /// <item><b>Arms</b> – relaxed ↔ guard stance and timed strikes (windup → strike → recover) via two-bone IK,
    /// with strike muscle boosts and hand pinning pushed to the <see cref="JointMotorDriver"/>.</item>
    /// <item><b>Spine/head</b> – lean into motion, strike torso twist, hit flinch spring, head look-at.</item>
    /// </list>
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Active Ragdoll/Procedural Animator")]
    [RequireComponent(typeof(ActiveRagdollCharacter))]
    public sealed class ProceduralAnimator : MonoBehaviour
    {
        [Serializable]
        public sealed class GaitSettings
        {
            [Tooltip("Foot error (m) that triggers a step while standing still.")]
            [Min(0.01f)] public float idleStepThreshold = 0.1f;
            [Tooltip("Foot error (m) that triggers a step at walking speed.")]
            [Min(0.01f)] public float movingStepThreshold = 0.2f;
            [Tooltip("Step duration (s) when slow.")]
            [Min(0.05f)] public float slowStepDuration = 0.34f;
            [Tooltip("Step duration (s) when fast or stumbling.")]
            [Min(0.05f)] public float fastStepDuration = 0.2f;
            [Min(0f)] public float stepHeight = 0.07f;
            [Min(0f)] public float stepHeightPerSpeed = 0.035f;
            [Min(0f)] public float maxStepHeight = 0.18f;
            [Tooltip("How far ahead (s of measured velocity) foot targets lead the body.")]
            [Min(0f)] public float leadTime = 0.18f;
            [Tooltip("Extra seconds of velocity feet overshoot by when they land, on top of Lead Time. Longer, more natural strides instead of shuffling. 0 = off.")]
            [Min(0f)] public float overshootTime = 0.1f;
            [Tooltip("A planted foot stays down until it's this fraction as far behind the hips as it landed in front of them (0..1). 0 = step as soon as the foot drifts by the step threshold (the old shuffle).")]
            [Range(0f, 1f)] public float strideSymmetry = 0.85f;
            [Tooltip("Largest landing overshoot, as a fraction of leg length (keeps running strides reachable).")]
            [Range(0.1f, 1f)] public float maxOvershoot = 0.5f;
            [Tooltip("How strongly foot targets move toward the capture point when the body is off balance.")]
            [Min(0f)] public float capturePointGain = 0.8f;
            [Tooltip("Maximum horizontal step reach from the hip, as a fraction of leg length.")]
            [Range(0.3f, 1f)] public float maxStepReach = 0.75f;
            [Min(0.1f)] public float stanceWidthScale = 1f;
            [Range(0f, 30f)] public float toeOutAngle = 6f;
            [Range(0f, 30f)] public float kneeOutAngle = 8f;
            [Tooltip("Heading change (degrees) that on its own triggers a step.")]
            [Min(1f)] public float yawStepThreshold = 35f;
            [Min(0f)] public float velocitySmoothing = 12f;
            [Tooltip("Largest height difference (m) a single step may climb or drop.")]
            [Min(0f)] public float maxStepUp = 0.35f;
            [Tooltip("How hard a swinging foot is pulled along its step arc (0..1). Joint muscles alone can't swing the foot fast enough against the body's momentum, so the toes catch and drag behind. This carries the foot to where it should land.")]
            [Range(0f, 1f)] public float swingFootPin = 0.8f;
            [Tooltip("How hard a planted foot is held where it was placed (0..1). Stops it being dragged along behind the body. Low, so shoves still slide it.")]
            [Range(0f, 1f)] public float plantedFootPin = 0.25f;
            [Tooltip("Shin pull while swinging, as a fraction of Swing Foot Pin. Brings the knee through instead of trailing.")]
            [Range(0f, 1f)] public float swingShinPin = 0.4f;
        }

        [Serializable]
        public sealed class ArmSettings
        {
            [Tooltip("Relaxed wrist position (right hand, arm-length units relative to the shoulder in the heading frame).")]
            public Vector3 relaxedHandOffset = new Vector3(0.12f, -0.93f, 0.08f);
            [Tooltip("Guard wrist position (right hand, arm-length units). Negative x = toward the centre line.")]
            public Vector3 guardHandOffset = new Vector3(-0.28f, 0.02f, 0.62f);
            [Tooltip("Direction the elbow points (right arm, heading frame).")]
            public Vector3 elbowHint = new Vector3(0.55f, -0.8f, -0.2f);
            public Vector3 strikeElbowHint = new Vector3(0.75f, -0.6f, -0.1f);
            [Tooltip("Distance (m) from the wrist to the knuckles.")]
            [Min(0f)] public float fistLength = 0.08f;
            [Min(0f)] public float swayAmplitude = 0.015f;
            [Min(0f)] public float swayFrequency = 0.8f;
            [Min(0.1f)] public float guardBlendSpeed = 3f;
        }

        [Serializable]
        public sealed class SpineSettings
        {
            [Tooltip("Forward lean (degrees) per m/s of forward speed.")]
            [Min(0f)] public float leanPerSpeed = 5f;
            [Min(0f)] public float maxLean = 14f;
            [Tooltip("Flinch impulse (deg/s per unit severity scaled by frequency).")]
            [Min(0f)] public float flinchPerSeverity = 55f;
            [Min(0f)] public float maxFlinch = 35f;
            [Min(0.1f)] public float flinchFrequency = 9f;
            [Range(0f, 2f)] public float flinchDampingRatio = 0.6f;
            [Tooltip("Lowest pelvis height (fraction of standing height) the crouch offset can reach.")]
            [Range(0.1f, 1f)] public float minCrouchRatio = 0.35f;
        }

        [Serializable]
        public sealed class WalkMotionSettings
        {
            [Tooltip("Arms swing opposite the legs: hand travel as a fraction of the opposite foot's travel. 0 = arms hang still.")]
            [Range(0f, 1.5f)] public float armSwing = 0.5f;
            [Tooltip("Largest arm swing, in arm lengths.")]
            [Range(0f, 0.6f)] public float maxArmSwing = 0.3f;
            [Tooltip("Share of the arm swing kept with the guard up.")]
            [Range(0f, 1f)] public float guardArmSwing = 0.25f;
            [Tooltip("How far (m) the hips dip when the feet are furthest apart, rising again over the planted foot.")]
            [Range(0f, 0.1f)] public float pelvisBob = 0.03f;
            [Tooltip("How far (m) the hips shift over the planted foot while the other one swings.")]
            [Range(0f, 0.1f)] public float pelvisSway = 0.025f;
            [Tooltip("Hip rotation (degrees) with the stride: the forward leg's hip leads.")]
            [Range(0f, 20f)] public float pelvisTwist = 7f;
            [Tooltip("How much the chest turns back against the hip rotation (1 = shoulders square, >1 = shoulders counter-rotate).")]
            [Range(0f, 2f)] public float chestCounterTwist = 1.4f;
            [Tooltip("Toes lift (degrees) as the heel strikes.")]
            [Range(0f, 40f)] public float heelStrike = 12f;
            [Tooltip("Toes point down (degrees) as the foot pushes off.")]
            [Range(0f, 40f)] public float toeOff = 18f;
        }

        [Serializable]
        public sealed class LookSettings
        {
            [Range(0f, 120f)] public float maxYaw = 75f;
            [Range(0f, 90f)] public float maxPitchUp = 35f;
            [Range(0f, 90f)] public float maxPitchDown = 45f;
            [Min(0f)] public float speed = 8f;
        }

        private sealed class Leg
        {
            public BodySide side;
            public readonly LimbIK ik = new LimbIK();
            public Transform foot;
            public int footBone = -1;
            public int shinBone = -1;
            public float lateral;
            public float ankleHeight;
            public Quaternion footRelToYaw = Quaternion.identity;

            public Vector3 planted;
            public float plantedYaw;
            public Vector3 plantedNormal = Vector3.up;

            public bool swinging;
            public float swingT;
            public float swingDuration;
            public Vector3 swingStart;
            public float swingStartYaw;
            public Vector3 swingStartNormal = Vector3.up;
            public float targetGroundY;
            public Vector3 targetNormal = Vector3.up;
            public bool midProbeDone;

            public Vector3 current;
            public float currentYaw;
            public float currentPitch;
            public Vector3 currentNormal = Vector3.up;
            public Vector3 ideal;
            public float idealYaw;
            public float error;
        }

        private sealed class Arm
        {
            public BodySide side;
            public readonly LimbIK ik = new LimbIK();
            public int upperBone = -1;
            public int lowerBone = -1;
            public int handBone = -1;
        }

        [Header("Layers")]
        [SerializeField] private bool _proceduralLegs = true;
        [Tooltip("Disable if an Animator supplies arm animation (strikes will then only boost muscles and pin the hand).")]
        [SerializeField] private bool _proceduralArms = true;
        [SerializeField] private bool _proceduralSpine = true;
        [SerializeField] private bool _headLook = true;

        [SerializeField] private GaitSettings _gait = new GaitSettings();
        [SerializeField] private ArmSettings _arms = new ArmSettings();
        [SerializeField] private SpineSettings _spine = new SpineSettings();
        [Tooltip("Secondary walking motion: arm swing, hip bob, sway and twist, heel-to-toe roll. All scale with walking speed.")]
        [SerializeField] private WalkMotionSettings _walkMotion = new WalkMotionSettings();
        [SerializeField] private LookSettings _look = new LookSettings();

        [Header("Events")]
        [Tooltip("Invoked when a foot plants (position). Hook footstep audio here.")]
        [SerializeField] private UnityEvent<Vector3> _onFootstep = new UnityEvent<Vector3>();

        private ActiveRagdollCharacter _character;
        private JointMotorDriver _motors;
        private readonly Leg[] _legs = new Leg[2];
        private readonly Arm[] _armRigs = new Arm[2];
        private Transform _pelvisT;
        private Transform _spineT;
        private Transform _chestT;
        private Transform _headT;
        private Vector3 _headForwardLocal = Vector3.forward;
        private bool _legsValid;
        private float _legLength = 0.9f;
        private int _lastStepped = -1;
        private Vector3 _smoothedVelocity;
        private float _walkAmount;
        private readonly float[] _footForward = new float[2];
        private float _walkBob;
        private float _walkSway;
        private float _walkTwist;
        private float _speed;
        private float _guard;
        private float _swayPhase;
        private float _lookWeight;
        private bool _hasLookTarget;
        private Vector3 _lookTarget;
        private Vector3 _smoothedLookDir = Vector3.forward;
        private Vector3 _flinchAngle;
        private Vector3 _flinchVelocity;
        private float _pelvisHeightOffset;
        private bool _initialized;

        // Strike state
        private StrikeDefinition _strike;
        private BodySide _strikeSide;
        private float _strikeTime;
        private Vector3 _strikeTarget;
        private int _strikeId;
        private BodySide _overrideSide = BodySide.Center;
        private readonly List<Object> _strikeHits = new List<Object>(4);

        // Arm aim overrides (weapons, reaching)
        private readonly bool[] _armAim = new bool[2];
        private readonly Vector3[] _armAimWrist = new Vector3[2];
        private readonly bool[] _armAimHasRotation = new bool[2];
        private readonly Quaternion[] _armAimHandRotation = new Quaternion[2];
        private float _extraLean;

        // ------------------------------------------------------------------ Public API

        public event Action<BodySide, Vector3> Footstep;

        public bool IsInitialized => _initialized;
        public bool ProceduralLegs { get => _proceduralLegs; set => _proceduralLegs = value; }
        public bool ProceduralArms { get => _proceduralArms; set => _proceduralArms = value; }

        /// <summary>Vertical offset (m, usually ≤ 0) applied to the animated pelvis: crouching, getting up.</summary>
        public float PelvisHeightOffset
        {
            get => _pelvisHeightOffset;
            set => _pelvisHeightOffset = RagdollMath.IsFinite(value) ? Mathf.Min(0.2f, value) : 0f;
        }

        public bool IsStriking => _strike != null;
        public StrikeDefinition CurrentStrike => _strike;
        public BodySide StrikeSide => _strikeSide;
        public int StrikeId => _strikeId;

        public StrikePhase CurrentStrikePhase
        {
            get
            {
                if (_strike == null) return StrikePhase.None;
                if (_strikeTime < _strike.windupTime) return StrikePhase.Windup;
                if (_strikeTime < _strike.windupTime + _strike.strikeTime) return StrikePhase.Strike;
                return StrikePhase.Recover;
            }
        }

        /// <summary>True during the part of the strike where a fist contact counts as a hit.</summary>
        public bool IsStrikeActive
        {
            get
            {
                if (_strike == null) return false;
                float t = _strikeTime - _strike.windupTime;
                return t >= 0f && t <= _strike.strikeTime + _strike.recoverTime * _strike.activeTail;
            }
        }

        /// <summary>Extra forward torso lean in degrees (reaching down to pick something up).</summary>
        public float ExtraLean
        {
            get => _extraLean;
            set => _extraLean = RagdollMath.IsFinite(value) ? Mathf.Clamp(value, -20f, 60f) : 0f;
        }

        /// <summary>
        /// Overrides one arm: the wrist is driven to <paramref name="wristTarget"/> and, optionally, the physical
        /// hand to <paramref name="handBodyRotation"/> (world). Used to aim held weapons and reach for pickups.
        /// Strikes on the same arm take priority.
        /// </summary>
        public void SetArmAim(BodySide side, Vector3 wristTarget, Quaternion handBodyRotation, bool applyRotation)
        {
            int i = side == BodySide.Left ? 0 : side == BodySide.Right ? 1 : -1;
            if (i < 0 || !RagdollMath.IsFinite(wristTarget) || !RagdollMath.IsFinite(handBodyRotation))
                return;
            _armAim[i] = true;
            _armAimWrist[i] = wristTarget;
            _armAimHasRotation[i] = applyRotation;
            _armAimHandRotation[i] = handBodyRotation;
        }

        public void ClearArmAim(BodySide side)
        {
            int i = side == BodySide.Left ? 0 : side == BodySide.Right ? 1 : -1;
            if (i >= 0)
                _armAim[i] = false;
        }

        /// <summary>Animated shoulder position of an arm (null-safe: returns false if the arm isn't rigged).</summary>
        public bool TryGetShoulder(BodySide side, out Vector3 shoulder, out float armLength)
        {
            Arm arm = GetArm(side);
            shoulder = arm != null ? arm.ik.Upper.position : Vector3.zero;
            armLength = arm != null ? arm.ik.Length : 0f;
            return arm != null;
        }

        public void SetLookTarget(Vector3 worldPoint)
        {
            if (!RagdollMath.IsFinite(worldPoint)) return;
            _lookTarget = worldPoint;
            _hasLookTarget = true;
        }

        public void ClearLookTarget() => _hasLookTarget = false;

        /// <summary>True if the given arm exists and is not disabled by injury.</summary>
        public bool CanStrikeWith(BodySide side)
        {
            Arm arm = GetArm(side);
            if (arm == null || !arm.ik.IsValid) return false;
            if (_motors == null) return true;
            return _motors.GetBoneFunction(arm.lowerBone) > 0.5f && _motors.GetBoneFunction(arm.upperBone) > 0.5f;
        }

        /// <summary>Starts a strike. Returns false if the arm is unavailable or a strike is already running.</summary>
        public bool BeginStrike(StrikeDefinition definition, Vector3 targetPoint)
        {
            if (!_initialized || definition == null || _strike != null || _character.IsDead)
                return false;
            BodySide side = definition.side == BodySide.Center ? BodySide.Right : definition.side;
            if (!CanStrikeWith(side) || !RagdollMath.IsFinite(targetPoint))
                return false;

            _strike = definition;
            _strikeSide = side;
            _strikeTime = 0f;
            _strikeTarget = targetPoint;
            _strikeId++;
            _strikeHits.Clear();
            return true;
        }

        /// <summary>Updates the aim point. Only honoured during the windup; the strike commits once thrown.</summary>
        public void UpdateStrikeTarget(Vector3 targetPoint)
        {
            if (_strike != null && _strikeTime < _strike.windupTime && RagdollMath.IsFinite(targetPoint))
                _strikeTarget = targetPoint;
        }

        public void CancelStrike()
        {
            _strike = null;
            _strikeHits.Clear();
            ClearStrikeOverrides();
        }

        /// <summary>True if <paramref name="boneIndex"/> is the striking forearm/hand during the active window.</summary>
        public bool IsStrikingBone(int boneIndex)
        {
            if (!IsStrikeActive) return false;
            Arm arm = GetArm(_strikeSide);
            return arm != null && (boneIndex == arm.lowerBone || boneIndex == arm.handBone);
        }

        /// <summary>Registers a hit on <paramref name="victim"/> for the current strike; false if it was already hit.</summary>
        public bool TryRegisterStrikeHit(Object victim, out StrikeDefinition definition)
        {
            definition = _strike;
            if (!IsStrikeActive || victim == null || _strikeHits.Contains(victim))
                return false;
            _strikeHits.Add(victim);
            return true;
        }

        /// <summary>Kicks the torso flinch spring away from an impact.</summary>
        public void AddFlinch(Vector3 impulse, float severity)
        {
            if (!_initialized || severity <= 0.001f)
                return;
            Vector3 dir = RagdollMath.Flatten(impulse);
            if (dir.sqrMagnitude < 1e-8f)
                return;
            dir.Normalize();
            Vector3 axis = Vector3.Cross(Vector3.up, dir); // rotates "up" toward the push direction
            _flinchVelocity += axis * (Mathf.Clamp01(severity) * _spine.flinchPerSeverity * _spine.flinchFrequency);
        }

        /// <summary>Re-plants both feet where the physical feet currently are (after teleports, landing, getting up).</summary>
        public void ResetFeet()
        {
            if (!_legsValid) return;
            Transform root = _character.AnimatedRoot;
            SyncFeetToBody(root.position.y, _character.HeadingRotation.eulerAngles.y);
        }

        public void ResetState()
        {
            CancelStrike();
            ClearLookTarget();
            _armAim[0] = _armAim[1] = false;
            _extraLean = 0f;
            _flinchAngle = _flinchVelocity = Vector3.zero;
            _pelvisHeightOffset = 0f;
            _guard = 0f;
            ResetFeet();
        }

        internal void OnKilled()
        {
            CancelStrike();
            ClearLookTarget();
            _armAim[0] = _armAim[1] = false;
            _extraLean = 0f;
            _pelvisHeightOffset = 0f;
        }

        // ------------------------------------------------------------------ Lifecycle

        internal bool Initialize(ActiveRagdollCharacter character)
        {
            _character = character;
            _motors = character.Motors;
            Transform root = character.AnimatedRoot;
            float bindYaw = RagdollMath.Yaw(root.forward, 0f);
            Quaternion bindYawRot = RagdollMath.YawRotation(bindYaw);
            Vector3 rootPos = root.position;

            _pelvisT = TargetOf(BoneRole.Pelvis);
            _spineT = TargetOf(BoneRole.Spine) ?? TargetOf(BoneRole.Chest);
            _chestT = TargetOf(BoneRole.Chest) ?? _spineT;
            _headT = TargetOf(BoneRole.Head);
            if (_headT != null)
                _headForwardLocal = Quaternion.Inverse(_headT.rotation) * (bindYawRot * Vector3.forward);

            // Legs
            _legsValid = true;
            float legLengthSum = 0f;
            for (int s = 0; s < 2; s++)
            {
                BodySide side = s == 0 ? BodySide.Left : BodySide.Right;
                var leg = new Leg { side = side };
                Transform upper = TargetOf(BoneRoles.UpperLeg(side));
                Transform lower = TargetOf(BoneRoles.LowerLeg(side));
                Transform foot = TargetOf(BoneRoles.Foot(side));
                leg.footBone = character.GetBoneIndex(BoneRoles.Foot(side));
                leg.shinBone = character.GetBoneIndex(BoneRoles.LowerLeg(side));
                if (!leg.ik.Bind(upper, lower, foot, bindYawRot * Vector3.forward) || leg.footBone < 0)
                {
                    _legsValid = false;
                    break;
                }
                leg.foot = foot;
                leg.lateral = Vector3.Dot(upper.position - rootPos, bindYawRot * Vector3.right) * _gait.stanceWidthScale;
                leg.ankleHeight = Mathf.Max(0.01f, foot.position.y - rootPos.y);
                leg.footRelToYaw = Quaternion.Inverse(bindYawRot) * foot.rotation;
                legLengthSum += leg.ik.Length;
                _legs[s] = leg;
            }
            if (_legsValid)
            {
                _legLength = legLengthSum * 0.5f;
                SyncFeetToBody(rootPos.y, bindYaw);
            }
            else if (_proceduralLegs)
            {
                Debug.LogWarning($"{ActiveRagdollCharacter.LogPrefix} '{name}': leg targets are incomplete; procedural gait disabled.", this);
            }

            // Arms (optional)
            for (int s = 0; s < 2; s++)
            {
                BodySide side = s == 0 ? BodySide.Left : BodySide.Right;
                var arm = new Arm
                {
                    side = side,
                    upperBone = character.GetBoneIndex(BoneRoles.UpperArm(side)),
                    lowerBone = character.GetBoneIndex(BoneRoles.LowerArm(side)),
                    handBone = character.GetBoneIndex(BoneRoles.Hand(side)),
                };
                Transform upper = TargetOf(BoneRoles.UpperArm(side));
                Transform lower = TargetOf(BoneRoles.LowerArm(side));
                Transform hand = TargetOf(BoneRoles.Hand(side));
                if (hand == null && lower != null && lower.childCount > 0)
                    hand = lower.GetChild(0);
                if (arm.upperBone >= 0 && arm.lowerBone >= 0 && arm.ik.Bind(upper, lower, hand, bindYawRot * Vector3.back))
                    _armRigs[s] = arm;
            }

            _smoothedLookDir = bindYawRot * Vector3.forward;
            _swayPhase = UnityEngine.Random.value * Mathf.PI * 2f;
            _initialized = true;
            return true;
        }

        private Transform TargetOf(BoneRole role)
        {
            int i = _character.GetBoneIndex(role);
            if (i < 0) return null;
            Transform t = _character.BonesInternal[i].target;
            return t != null ? t : null;
        }

        private Arm GetArm(BodySide side)
        {
            if (side == BodySide.Left) return _armRigs[0];
            if (side == BodySide.Right) return _armRigs[1];
            return null;
        }

        /// <summary>Called by <see cref="ActiveRagdollCharacter"/> in LateUpdate, after animation, before target capture.</summary>
        internal void ModifyPose(float dt)
        {
            if (!_initialized)
                return;

            RagdollProfile profile = _character.Profile;
            Quaternion yawRot = _character.HeadingRotation;
            float headingYaw = yawRot.eulerAngles.y;

            AdvanceStrike(dt);
            UpdateFlinch(dt);

            ComputeWalkMotion(profile, yawRot);
            ApplyPelvisOffset(yawRot);
            if (_proceduralSpine)
                ApplySpine(profile, yawRot);
            if (_headLook)
                ApplyHeadLook(dt, profile.lookWeight, yawRot);
            if (_proceduralLegs && _legsValid)
                UpdateGait(dt, profile.gaitWeight, yawRot, headingYaw);
            if (_proceduralArms)
                ApplyArms(dt, profile.guardWeight, yawRot);

            PushStrikeMuscleOverrides();
        }

        // ------------------------------------------------------------------ Pelvis & spine

        private void ApplyPelvisOffset(Quaternion yawRot)
        {
            if (_pelvisT == null)
                return;
            float standing = _character.StandingPelvisHeight;
            float offset = Mathf.Max(_pelvisHeightOffset, -standing * (1f - _spine.minCrouchRatio)) + _walkBob;
            Vector3 shift = Vector3.up * offset + yawRot * Vector3.right * _walkSway;
            if (shift.sqrMagnitude > 1e-8f)
                _pelvisT.position += shift;

            if (Mathf.Abs(_walkTwist) > 0.01f)
            {
                // Hips turn with the stride; the spine turns back so the shoulders counter-rotate.
                _pelvisT.rotation = Quaternion.AngleAxis(_walkTwist, Vector3.up) * _pelvisT.rotation;
                if (_spineT != null)
                    _spineT.rotation = Quaternion.AngleAxis(-_walkTwist * _walkMotion.chestCounterTwist, Vector3.up) * _spineT.rotation;
            }
        }

        /// <summary>
        /// Secondary walking motion from where the feet are (last frame's gait): how far each foot is ahead of
        /// the other drives arm swing and hip twist, their spread drives the hip bob, and the swinging leg
        /// shifts the hips over the planted one. Everything scales with walking speed, so standing is still.
        /// </summary>
        private void ComputeWalkMotion(in RagdollProfile profile, Quaternion yawRot)
        {
            _walkBob = _walkSway = _walkTwist = 0f;
            _footForward[0] = _footForward[1] = 0f;
            if (!_proceduralLegs || !_legsValid || _legs[0] == null || _legs[1] == null)
            {
                _walkAmount = 0f;
                return;
            }

            LocomotionController locomotion = _character.Locomotion;
            float walkSpeed = locomotion != null ? Mathf.Max(0.3f, locomotion.WalkSpeed) : 1.3f;
            _walkAmount = profile.gaitWeight * Mathf.Clamp01(_speed / walkSpeed);
            if (_walkAmount <= 0.001f)
                return;

            Vector3 root = _character.AnimatedRoot.position;
            Vector3 forward = yawRot * Vector3.forward;
            float left = Vector3.Dot(_legs[0].current - root, forward);
            float right = Vector3.Dot(_legs[1].current - root, forward);
            float mid = (left + right) * 0.5f;
            _footForward[0] = left - mid;
            _footForward[1] = right - mid;

            float stride = Mathf.Max(0.1f, _legLength * 0.9f);
            float spread = Mathf.Clamp01(Mathf.Abs(left - right) / stride);
            _walkBob = -_walkMotion.pelvisBob * spread * _walkAmount;
            _walkTwist = -_walkMotion.pelvisTwist * Mathf.Clamp((right - left) / stride, -1f, 1f) * _walkAmount;

            for (int s = 0; s < 2; s++)
            {
                Leg leg = _legs[s];
                if (!leg.swinging)
                    continue;
                // Over the planted (other) foot, most at mid-swing.
                float toward = BoneRoles.Sign(_legs[1 - s].side);
                _walkSway = _walkMotion.pelvisSway * Mathf.Sin(Mathf.PI * Mathf.Clamp01(leg.swingT)) * toward * _walkAmount;
            }
        }

        private void UpdateFlinch(float dt)
        {
            if (dt <= 0f)
                return;
            float w = _spine.flinchFrequency;
            Vector3 acceleration = -_flinchAngle * (w * w) - _flinchVelocity * (2f * _spine.flinchDampingRatio * w);
            _flinchVelocity += acceleration * dt;
            _flinchAngle += _flinchVelocity * dt;
            if (_flinchAngle.sqrMagnitude > _spine.maxFlinch * _spine.maxFlinch)
                _flinchAngle = _flinchAngle.normalized * _spine.maxFlinch;
            if (!RagdollMath.IsFinite(_flinchAngle) || !RagdollMath.IsFinite(_flinchVelocity))
                _flinchAngle = _flinchVelocity = Vector3.zero;
        }

        private void ApplySpine(in RagdollProfile profile, Quaternion yawRot)
        {
            if (_spineT == null)
                return;

            Vector3 forward = yawRot * Vector3.forward;
            Vector3 right = yawRot * Vector3.right;
            float forwardSpeed = Vector3.Dot(_smoothedVelocity, forward);
            float sideSpeed = Vector3.Dot(_smoothedVelocity, right);
            float pitch = Mathf.Clamp(forwardSpeed * _spine.leanPerSpeed, -_spine.maxLean * 0.5f, _spine.maxLean) * profile.gaitWeight + _extraLean;
            float roll = Mathf.Clamp(-sideSpeed * _spine.leanPerSpeed * 0.5f, -_spine.maxLean * 0.5f, _spine.maxLean * 0.5f) * profile.gaitWeight;
            Quaternion lean = Quaternion.AngleAxis(pitch, right) * Quaternion.AngleAxis(roll, forward);

            float flinchDeg = _flinchAngle.magnitude;
            Quaternion halfFlinch = flinchDeg > 0.01f ? Quaternion.AngleAxis(flinchDeg * 0.5f, _flinchAngle / flinchDeg) : Quaternion.identity;
            Quaternion twist = Quaternion.AngleAxis(StrikeTwistDegrees(), Vector3.up);

            if (_chestT == null || _chestT == _spineT)
            {
                _spineT.rotation = twist * halfFlinch * halfFlinch * lean * _spineT.rotation;
            }
            else
            {
                _spineT.rotation = lean * halfFlinch * _spineT.rotation;
                _chestT.rotation = twist * halfFlinch * _chestT.rotation;
            }
        }

        private void ApplyHeadLook(float dt, float weight, Quaternion yawRot)
        {
            if (_headT == null)
                return;

            _lookWeight = Mathf.MoveTowards(_lookWeight, _hasLookTarget ? weight : 0f, dt * 4f);
            if (_lookWeight <= 0.001f)
                return;

            Vector3 desired = _hasLookTarget ? _lookTarget - _headT.position : yawRot * Vector3.forward;
            if (desired.sqrMagnitude < 1e-6f)
                return;

            Vector3 local = Quaternion.Inverse(yawRot) * desired.normalized;
            float yaw = Mathf.Clamp(Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg, -_look.maxYaw, _look.maxYaw);
            float pitch = Mathf.Clamp(Mathf.Asin(Mathf.Clamp(local.y, -1f, 1f)) * Mathf.Rad2Deg, -_look.maxPitchDown, _look.maxPitchUp);
            Vector3 clamped = yawRot * (Quaternion.Euler(-pitch, yaw, 0f) * Vector3.forward);
            _smoothedLookDir = Vector3.Slerp(_smoothedLookDir, clamped, RagdollMath.Smoothing(_look.speed, dt)).normalized;

            Vector3 current = _headT.rotation * _headForwardLocal;
            Quaternion delta = Quaternion.FromToRotation(current, _smoothedLookDir);
            _headT.rotation = Quaternion.Slerp(Quaternion.identity, delta, _lookWeight) * _headT.rotation;
        }

        // ------------------------------------------------------------------ Gait

        private void UpdateGait(float dt, float weight, Quaternion yawRot, float headingYaw)
        {
            Vector3 rootPos = _character.AnimatedRoot.position;
            if (weight <= 0.001f)
            {
                SyncFeetToBody(rootPos.y, headingYaw);
                PinFeet(0f, false);
                return;
            }

            BalanceController balance = _character.Balance;
            bool hasBalance = balance != null && balance.isActiveAndEnabled && balance.IsInitialized;
            Vector3 velocity = RagdollMath.Flatten(hasBalance ? balance.ComVelocity : _character.Pelvis.linearVelocity);
            _smoothedVelocity = Vector3.Lerp(_smoothedVelocity, velocity, RagdollMath.Smoothing(_gait.velocitySmoothing, dt));
            _speed = _smoothedVelocity.magnitude;

            bool grounded = !hasBalance || balance.Support > 0.15f;
            Vector3 right = yawRot * Vector3.right;
            Vector3 capture = Vector3.zero;
            float urgency = 0f;
            if (hasBalance)
            {
                capture = Vector3.ClampMagnitude(RagdollMath.Flatten(balance.BalanceOffset) * _gait.capturePointGain, _legLength * 0.6f);
                urgency = Mathf.Clamp01(balance.BalanceError / Mathf.Max(0.01f, balance.StumbleDistance));
            }

            LocomotionController locomotion = _character.Locomotion;
            float walkSpeed = locomotion != null ? Mathf.Max(0.1f, locomotion.WalkSpeed) : 1.3f;
            float runSpeed = locomotion != null ? Mathf.Max(walkSpeed, locomotion.RunSpeed) : 3f;

            // Feet land ahead of the hips by the lead plus an overshoot that grows with speed, and (below)
            // stay planted until the body has carried them about as far behind. Stride length then scales with
            // velocity like a real walk (Raibert-style placement) instead of short shuffles under the body.
            Vector3 lead = Vector3.ClampMagnitude(_smoothedVelocity * (_gait.leadTime + _gait.overshootTime),
                Mathf.Max(_legLength * _gait.maxOvershoot, _speed * _gait.leadTime)); // the cap never cuts into the plain lead
            float strideThreshold = lead.magnitude * (1f + _gait.strideSymmetry);
            for (int s = 0; s < 2; s++)
            {
                Leg leg = _legs[s];
                Vector3 ideal = rootPos + right * leg.lateral + lead + capture;
                ideal.y = rootPos.y;
                leg.ideal = ideal;
                leg.idealYaw = headingYaw + BoneRoles.Sign(leg.side) * _gait.toeOutAngle;
            }

            if (!grounded)
            {
                // Airborne: legs reach for where they'd land, like BIMOS's air pose.
                for (int s = 0; s < 2; s++)
                {
                    Leg leg = _legs[s];
                    leg.swinging = false;
                    leg.planted = leg.current = leg.ideal;
                    leg.plantedYaw = leg.currentYaw = leg.idealYaw;
                    leg.plantedNormal = leg.currentNormal = Vector3.up;
                }
            }
            else
            {
                if (!_legs[0].swinging && !_legs[1].swinging)
                {
                    float threshold = Mathf.Lerp(_gait.idleStepThreshold, _gait.movingStepThreshold, Mathf.Clamp01(_speed / walkSpeed));
                    if (_gait.strideSymmetry > 0f)
                        threshold = Mathf.Max(threshold, strideThreshold);
                    threshold *= Mathf.Lerp(1f, 0.5f, urgency);
                    for (int s = 0; s < 2; s++)
                    {
                        Leg leg = _legs[s];
                        float yawError = Mathf.Abs(Mathf.DeltaAngle(leg.plantedYaw, leg.idealYaw)) / _gait.yawStepThreshold;
                        leg.error = RagdollMath.Flatten(leg.planted - leg.ideal).magnitude + yawError * threshold;
                    }

                    int pick = _legs[0].error >= _legs[1].error ? 0 : 1;
                    if (pick == _lastStepped && _legs[1 - pick].error > threshold * 0.6f)
                        pick = 1 - pick;
                    if (_legs[pick].error > threshold)
                    {
                        float fastness = Mathf.Max(Mathf.Clamp01(_speed / runSpeed), urgency);
                        BeginSwing(_legs[pick], Mathf.Lerp(_gait.slowStepDuration, _gait.fastStepDuration, fastness), rootPos.y);
                    }
                }

                for (int s = 0; s < 2; s++)
                {
                    Leg leg = _legs[s];
                    if (leg.swinging)
                    {
                        AdvanceSwing(leg, s, dt, rootPos.y);
                    }
                    else
                    {
                        leg.current = leg.planted;
                        leg.currentYaw = leg.plantedYaw;
                        leg.currentNormal = leg.plantedNormal;
                        leg.currentPitch = Mathf.MoveTowards(leg.currentPitch, 0f, dt * 150f); // foot rolls flat
                    }
                }
            }

            for (int s = 0; s < 2; s++)
                SolveLeg(_legs[s], weight, headingYaw);

            bool balanced = !hasBalance || balance.State != BalanceState.Lost;
            PinFeet(balanced ? weight : 0f, grounded);
        }

        /// <summary>
        /// Pulls the physical feet toward their gait targets: firmly while swinging (so they get where they're
        /// going instead of dragging on their toes), lightly while planted. Off while airborne or falling.
        /// </summary>
        private void PinFeet(float weight, bool grounded)
        {
            if (_motors == null || !_motors.IsInitialized)
                return;
            for (int s = 0; s < 2; s++)
            {
                Leg leg = _legs[s];
                if (leg == null)
                    continue;
                float foot = 0f, shin = 0f;
                if (grounded && weight > 0f)
                {
                    foot = weight * (leg.swinging ? _gait.swingFootPin : _gait.plantedFootPin);
                    shin = leg.swinging ? foot * _gait.swingShinPin : 0f;
                }
                if (leg.footBone >= 0) _motors.SetPinWeight(leg.footBone, foot);
                if (leg.shinBone >= 0) _motors.SetPinWeight(leg.shinBone, shin);
            }
        }

        private void BeginSwing(Leg leg, float duration, float rootY)
        {
            leg.swinging = true;
            leg.swingT = 0f;
            leg.swingDuration = Mathf.Max(0.08f, duration);
            leg.swingStart = leg.planted;
            leg.swingStartYaw = leg.plantedYaw;
            leg.swingStartNormal = leg.plantedNormal;
            leg.midProbeDone = false;
            ProbeGround(leg.ideal, rootY, out leg.targetGroundY, out leg.targetNormal);
        }

        private void AdvanceSwing(Leg leg, int legIndex, float dt, float rootY)
        {
            leg.swingT += dt / leg.swingDuration;
            float t = Mathf.Clamp01(leg.swingT);
            float remaining = (1f - t) * leg.swingDuration;

            Vector3 target = leg.ideal + _smoothedVelocity * remaining;
            Vector3 hip = leg.ik.Upper.position;
            Vector3 reach = RagdollMath.Flatten(target - hip);
            float maxReach = _legLength * _gait.maxStepReach;
            if (reach.sqrMagnitude > maxReach * maxReach)
                target = new Vector3(hip.x, target.y, hip.z) + reach.normalized * maxReach;

            if (!leg.midProbeDone && t >= 0.5f)
            {
                ProbeGround(target, rootY, out leg.targetGroundY, out leg.targetNormal);
                leg.midProbeDone = true;
            }
            target.y = leg.targetGroundY;

            float height = Mathf.Min(_gait.maxStepHeight, _gait.stepHeight + _gait.stepHeightPerSpeed * _speed);
            Vector3 control = (leg.swingStart + target) * 0.5f + Vector3.up * (height * 2f);
            leg.current = RagdollMath.QuadraticBezier(leg.swingStart, control, target, t);
            leg.currentYaw = Mathf.LerpAngle(leg.swingStartYaw, leg.idealYaw, t);
            // Heel-to-toe: toes point down as the foot pushes off, lift as the heel comes down.
            float pitch = 0f;
            if (t < 0.4f)
                pitch = -_walkMotion.toeOff * Mathf.Sin(Mathf.PI * t / 0.4f);
            else if (t > 0.6f)
                pitch = _walkMotion.heelStrike * Mathf.Sin(Mathf.PI * 0.5f * (t - 0.6f) / 0.4f);
            leg.currentPitch = pitch * _walkAmount;
            leg.currentNormal = Vector3.Slerp(leg.swingStartNormal, leg.targetNormal, t);

            if (leg.swingT >= 1f)
            {
                leg.swinging = false;
                leg.planted = target;
                leg.plantedYaw = leg.idealYaw;
                leg.plantedNormal = leg.targetNormal;
                _lastStepped = legIndex;
                Footstep?.Invoke(leg.side, target);
                _onFootstep?.Invoke(target);
            }
        }

        private void ProbeGround(Vector3 point, float rootY, out float groundY, out Vector3 normal)
        {
            groundY = rootY;
            normal = Vector3.up;
            BalanceController balance = _character.Balance;
            int mask = balance != null && balance.IsInitialized ? balance.GroundMask : ~RagdollLayers.NonEnvironmentMask();
            float up = _gait.maxStepUp + 0.1f;
            Vector3 origin = new Vector3(point.x, rootY + up, point.z);
            if (PhysicsQuery.Raycast(origin, Vector3.down, up + _gait.maxStepUp + 0.1f, mask, _character, null, out RaycastHit hit)
                && Vector3.Angle(hit.normal, Vector3.up) < 50f)
            {
                groundY = hit.point.y;
                normal = hit.normal;
            }
        }

        private void SolveLeg(Leg leg, float weight, float headingYaw)
        {
            Vector3 ankle = leg.current + leg.currentNormal * leg.ankleHeight;
            Quaternion yaw = RagdollMath.YawRotation(leg.currentYaw);
            Quaternion roll = Mathf.Abs(leg.currentPitch) > 0.01f ? Quaternion.AngleAxis(-leg.currentPitch, yaw * Vector3.right) : Quaternion.identity;
            Quaternion footRotation = Quaternion.FromToRotation(Vector3.up, leg.currentNormal) * roll * yaw * leg.footRelToYaw;
            if (weight < 0.999f)
                ankle = Vector3.Lerp(leg.foot.position, ankle, weight);

            Vector3 knee = RagdollMath.YawRotation(headingYaw + BoneRoles.Sign(leg.side) * _gait.kneeOutAngle) * Vector3.forward;
            leg.ik.Solve(ankle, knee, weight);
            leg.foot.rotation = weight >= 0.999f ? footRotation : Quaternion.Slerp(leg.foot.rotation, footRotation, weight);
        }

        private void SyncFeetToBody(float groundY, float headingYaw)
        {
            var bones = _character.BonesInternal;
            for (int s = 0; s < 2; s++)
            {
                Leg leg = _legs[s];
                if (leg == null) continue;
                Rigidbody body = bones[leg.footBone].body;
                Vector3 p = body != null ? body.position : leg.foot.position;
                leg.planted = leg.current = new Vector3(p.x, groundY, p.z);
                leg.plantedYaw = leg.currentYaw = headingYaw + BoneRoles.Sign(leg.side) * _gait.toeOutAngle;
                leg.plantedNormal = leg.currentNormal = Vector3.up;
                leg.swinging = false;
            }
        }

        // ------------------------------------------------------------------ Arms & strikes

        private void ApplyArms(float dt, float guardWeight, Quaternion yawRot)
        {
            _guard = Mathf.MoveTowards(_guard, guardWeight, dt * _arms.guardBlendSpeed);
            float swayT = Time.time * _arms.swayFrequency * Mathf.PI * 2f + _swayPhase;

            for (int s = 0; s < 2; s++)
            {
                Arm arm = _armRigs[s];
                if (arm == null)
                    continue;

                float sx = BoneRoles.Sign(arm.side);
                Vector3 shoulder = arm.ik.Upper.position;
                float length = arm.ik.Length;
                Vector3 relaxed = shoulder + yawRot * (Mirror(_arms.relaxedHandOffset, sx) * length);
                float phase = swayT + s * 1.7f;
                Vector3 guard = shoulder + yawRot * (Mirror(_arms.guardHandOffset, sx) * length
                    + new Vector3(0f, Mathf.Sin(phase), Mathf.Cos(phase * 0.7f)) * _arms.swayAmplitude);

                Vector3 target = Vector3.Lerp(relaxed, guard, _guard);
                if (_walkAmount > 0.001f && _walkMotion.armSwing > 0f)
                {
                    // Opposite the same-side leg: right arm forward as the right foot goes back.
                    float swing = -_footForward[arm.side == BodySide.Left ? 0 : 1] * _walkMotion.armSwing;
                    swing = Mathf.Clamp(swing, -_walkMotion.maxArmSwing * length, _walkMotion.maxArmSwing * length)
                        * Mathf.Lerp(1f, _walkMotion.guardArmSwing, _guard);
                    target += yawRot * new Vector3(0f, Mathf.Max(0f, swing) * 0.35f, swing);
                }
                Vector3 hint = yawRot * Mirror(_arms.elbowHint, sx);
                bool striking = _strike != null && _strikeSide == arm.side;
                bool aiming = !striking && _armAim[s];
                if (striking)
                {
                    target = EvaluateStrike(shoulder, length, guard, yawRot, sx);
                    hint = yawRot * Mirror(_arms.strikeElbowHint, sx);
                }
                else if (aiming)
                {
                    target = _armAimWrist[s];
                    hint = yawRot * Mirror(_arms.strikeElbowHint, sx);
                }

                arm.ik.Solve(target, hint, 1f);

                if (aiming && _armAimHasRotation[s] && arm.handBone >= 0)
                    arm.ik.End.rotation = _armAimHandRotation[s] * _character.BonesInternal[arm.handBone].bodyToTarget;
            }
        }

        private static Vector3 Mirror(Vector3 v, float sx) => new Vector3(v.x * sx, v.y, v.z);

        private Vector3 EvaluateStrike(Vector3 shoulder, float length, Vector3 guard, Quaternion yawRot, float sx)
        {
            StrikeDefinition d = _strike;
            Vector3 windup = shoulder + yawRot * (Mirror(d.windupOffset, sx) * length);
            Vector3 toTarget = _strikeTarget - shoulder;
            float distance = toTarget.magnitude;
            Vector3 dir = distance > 1e-3f ? toTarget / distance : yawRot * Vector3.forward;
            float reach = Mathf.Clamp(distance - _arms.fistLength + d.overshoot, length * 0.3f, length * 0.995f);
            Vector3 end = shoulder + dir * reach;

            float t = _strikeTime;
            if (t < d.windupTime)
                return Vector3.Lerp(guard, windup, RagdollMath.SmoothStep01(t / d.windupTime));

            t -= d.windupTime;
            if (t < d.strikeTime)
            {
                float u = t / d.strikeTime;
                Vector3 p = Vector3.Lerp(windup, end, RagdollMath.EaseOutQuad(u));
                float arc = Mathf.Sin(u * Mathf.PI);
                return p + yawRot * (new Vector3(sx * d.hookArc, d.rise, 0f) * (arc * length));
            }

            t -= d.strikeTime;
            return Vector3.Lerp(end, guard, RagdollMath.SmoothStep01(t / d.recoverTime));
        }

        private float StrikeTwistDegrees()
        {
            if (_strike == null)
                return 0f;
            StrikeDefinition d = _strike;
            float sx = BoneRoles.Sign(_strikeSide);
            float t = _strikeTime;
            float twist;
            if (t < d.windupTime)
            {
                twist = -0.5f * RagdollMath.SmoothStep01(t / d.windupTime);
            }
            else if ((t -= d.windupTime) < d.strikeTime)
            {
                twist = Mathf.Lerp(-0.5f, 1f, RagdollMath.EaseOutQuad(t / d.strikeTime));
            }
            else
            {
                t -= d.strikeTime;
                twist = Mathf.Lerp(1f, 0f, RagdollMath.SmoothStep01(t / d.recoverTime));
            }
            // A right-hand punch drives the right shoulder forward: the torso yaws left (negative).
            return -sx * twist * d.torsoTwist;
        }

        private void AdvanceStrike(float dt)
        {
            if (_strike == null)
                return;
            _strikeTime += dt;
            if (_strikeTime >= _strike.TotalTime)
                CancelStrike();
        }

        private void PushStrikeMuscleOverrides()
        {
            if (_motors == null || !_motors.IsInitialized)
                return;

            if (_strike == null)
            {
                ClearStrikeOverrides();
                return;
            }

            if (_overrideSide != _strikeSide)
                ClearStrikeOverrides();
            _overrideSide = _strikeSide;

            StrikeDefinition d = _strike;
            float boost = 1f;
            float pin = 0f;
            switch (CurrentStrikePhase)
            {
                case StrikePhase.Windup:
                    boost = Mathf.Lerp(1f, d.muscleBoost, 0.5f);
                    break;
                case StrikePhase.Strike:
                    boost = d.muscleBoost;
                    pin = d.handPinWeight;
                    break;
                case StrikePhase.Recover:
                    float u = (_strikeTime - d.windupTime - d.strikeTime) / d.recoverTime;
                    boost = Mathf.Lerp(d.muscleBoost, 1f, u);
                    break;
            }

            Arm arm = GetArm(_strikeSide);
            if (arm == null)
                return;
            _motors.SetBoost(arm.upperBone, boost);
            _motors.SetBoost(arm.lowerBone, boost);
            int fist = arm.handBone >= 0 ? arm.handBone : arm.lowerBone;
            if (arm.handBone >= 0)
                _motors.SetBoost(arm.handBone, boost);
            _motors.SetPinWeight(fist, pin);
            int chest = _character.ChestIndex;
            if (chest >= 0)
                _motors.SetBoost(chest, 1f + (boost - 1f) * 0.5f);
        }

        private void ClearStrikeOverrides()
        {
            if (_overrideSide == BodySide.Center || _motors == null || !_motors.IsInitialized)
            {
                _overrideSide = BodySide.Center;
                return;
            }

            Arm arm = GetArm(_overrideSide);
            if (arm != null)
            {
                _motors.SetBoost(arm.upperBone, 1f);
                _motors.SetBoost(arm.lowerBone, 1f);
                _motors.SetPinWeight(arm.lowerBone, 0f);
                if (arm.handBone >= 0)
                {
                    _motors.SetBoost(arm.handBone, 1f);
                    _motors.SetPinWeight(arm.handBone, 0f);
                }
            }
            int chest = _character.ChestIndex;
            if (chest >= 0)
                _motors.SetBoost(chest, 1f);
            _overrideSide = BodySide.Center;
        }

        private void OnDrawGizmosSelected()
        {
            if (!_initialized || !_legsValid)
                return;
            for (int s = 0; s < 2; s++)
            {
                Leg leg = _legs[s];
                Gizmos.color = leg.swinging ? Color.magenta : Color.white;
                Gizmos.DrawWireCube(leg.current, new Vector3(0.08f, 0.02f, 0.16f));
                Gizmos.color = Color.gray;
                Gizmos.DrawLine(leg.current, leg.ideal);
            }
            if (_strike != null)
            {
                Gizmos.color = IsStrikeActive ? Color.red : new Color(1f, 0.6f, 0.2f);
                Gizmos.DrawWireSphere(_strikeTarget, 0.06f);
            }
        }
    }
}
