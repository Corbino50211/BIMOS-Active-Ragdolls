using System;
using UnityEngine;

namespace ActiveRagdoll
{
    /// <summary>
    /// The ragdoll's muscles. Every physics step it drives each ConfigurableJoint's slerp drive toward the
    /// pose captured from the animated rig.
    /// <para>
    /// Stiffness is derived from the inertia of everything the joint has to move
    /// (<c>k = strength · I · ω²</c>, <c>d = 2ζ·√(k·I)</c>). A shoulder that swings a whole arm and a wrist
    /// that swings a hand therefore respond with the same crispness, and tuning carries across rigs of any
    /// scale or mass. Drive torque saturates at a configurable pose error, so a strong enough push or punch
    /// always overpowers the muscle and moves the limb.
    /// </para>
    /// Modifiers stack multiplicatively: behaviour profile × per-bone multiplier × part function (injury) ×
    /// strike boost × pain × grab weakness.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Active Ragdoll/Joint Motor Driver")]
    [RequireComponent(typeof(ActiveRagdollCharacter))]
    public sealed class JointMotorDriver : MonoBehaviour
    {
        /// <summary>Damping torque headroom (rad/s) added on top of the spring saturation torque.</summary>
        private const float MaxDampedAngularSpeed = 10f;

        [Serializable]
        public struct MuscleTuning
        {
            [Tooltip("Natural frequency (rad/s) of the muscle at strength 1. Higher = snappier and harder to push around.")]
            [Min(0f)] public float frequency;

            [Tooltip("Damping ratio. 1 = critically damped, <1 = springy overshoot, >1 = sluggish.")]
            [Range(0f, 3f)] public float dampingRatio;

            [Tooltip("Pose error (degrees) at which the muscle torque saturates. Lower values make the limb easier to overpower.")]
            [Range(1f, 180f)] public float saturationAngle;

            public MuscleTuning(float frequency, float dampingRatio, float saturationAngle)
            {
                this.frequency = frequency;
                this.dampingRatio = dampingRatio;
                this.saturationAngle = saturationAngle;
            }
        }

        [Header("Muscle Tuning (per body group)")]
        [SerializeField] private MuscleTuning _torso = new MuscleTuning(22f, 0.9f, 60f);
        [SerializeField] private MuscleTuning _head = new MuscleTuning(16f, 0.9f, 45f);
        [SerializeField] private MuscleTuning _arm = new MuscleTuning(18f, 0.85f, 50f);
        [SerializeField] private MuscleTuning _hand = new MuscleTuning(12f, 0.8f, 40f);
        [SerializeField] private MuscleTuning _leg = new MuscleTuning(30f, 0.9f, 60f);
        [SerializeField] private MuscleTuning _foot = new MuscleTuning(24f, 0.9f, 45f);

        [Header("Limp")]
        [Tooltip("Joint friction kept even at zero strength (damper = inertia × value). Stops corpses behaving like jelly.")]
        [SerializeField, Min(0f)] private float _limpDamping = 1.5f;

        [Header("Pain (hit reactions)")]
        [Tooltip("Pain added per N·s of impact impulse. Pain temporarily weakens the struck body part and its neighbours.")]
        [SerializeField, Min(0f)] private float _painPerImpulse = 0.025f;
        [SerializeField, Min(0f)] private float _painPerDamage = 0.02f;
        [Tooltip("Strength removed at full pain (0.85 = a fully 'hurt' joint keeps 15% strength).")]
        [SerializeField, Range(0f, 1f)] private float _painWeakening = 0.85f;
        [Tooltip("Pain recovered per second.")]
        [SerializeField, Min(0f)] private float _painRecoveryRate = 0.9f;
        [Tooltip("Fraction of pain that spreads to the parent and child joints.")]
        [SerializeField, Range(0f, 1f)] private float _painSpread = 0.45f;

        [Header("External Control")]
        [Tooltip("Strength multiplier for a limb that is being held (e.g. grabbed by a BIMOS hand), so the player can move it.")]
        [SerializeField, Range(0f, 1f)] private float _grabbedStrength = 0.25f;

        [Header("Pinning (strike assist)")]
        [SerializeField, Min(0f)] private float _pinFrequency = 14f;
        [SerializeField, Range(0f, 3f)] private float _pinDampingRatio = 1f;
        [Tooltip("Acceleration cap (m/s²) of the pin force at weight 1. Keeps pins from overpowering collisions.")]
        [SerializeField, Min(0f)] private float _pinMaxAcceleration = 80f;

        [Header("Optimisation")]
        [Tooltip("Relative change below which a joint's drive isn't re-sent to PhysX.")]
        [SerializeField, Range(0f, 0.2f)] private float _driveUpdateThreshold = 0.02f;

        private ActiveRagdollCharacter _character;
        private float[] _inertia = Array.Empty<float>();
        private float[] _pain = Array.Empty<float>();
        private float[] _function = Array.Empty<float>();
        private float[] _boost = Array.Empty<float>();
        private float[] _pinWeight = Array.Empty<float>();
        private float[] _appliedSpring = Array.Empty<float>();
        private float[] _appliedDamper = Array.Empty<float>();
        private bool[] _grabbed = Array.Empty<bool>();
        private int[] _holds = Array.Empty<int>();
        private int[] _limb = Array.Empty<int>();
        private int _grabbedLimbMask;
        private bool _initialized;

        public bool IsInitialized => _initialized;
        public float PainPerImpulse => _painPerImpulse;

        // ------------------------------------------------------------------ Public API

        /// <summary>Current pain (0..1) of a bone.</summary>
        public float GetPain(int boneIndex) => InRange(boneIndex) ? _pain[boneIndex] : 0f;

        /// <summary>Adds pain to a bone and spreads part of it to its neighbours.</summary>
        public void AddPain(int boneIndex, float amount)
        {
            if (!InRange(boneIndex) || amount <= 0f || !RagdollMath.IsFinite(amount))
                return;

            _pain[boneIndex] = Mathf.Min(1f, _pain[boneIndex] + amount);
            float spread = amount * _painSpread;
            if (spread <= 0f)
                return;

            RagdollBone b = _character.BonesInternal[boneIndex];
            if (b.parentIndex >= 0)
                _pain[b.parentIndex] = Mathf.Min(1f, _pain[b.parentIndex] + spread);
            for (int c = 0; c < b.children.Length; c++)
                _pain[b.children[c]] = Mathf.Min(1f, _pain[b.children[c]] + spread);
        }

        internal void AddImpactPain(int boneIndex, float impulse, float damage)
        {
            AddPain(boneIndex, impulse * _painPerImpulse + damage * _painPerDamage);
        }

        /// <summary>Marks a bone as held by an external agent (BIMOS hand, physics gun...). Weakens its whole limb.</summary>
        public void SetGrabbed(int boneIndex, bool grabbed)
        {
            if (!InRange(boneIndex) || _grabbed[boneIndex] == grabbed)
                return;
            _grabbed[boneIndex] = grabbed;
            RebuildGrabMask();
        }

        public bool IsGrabbed(int boneIndex) => InRange(boneIndex) && (_grabbed[boneIndex] || _holds[boneIndex] > 0);

        /// <summary>
        /// Reference-counted alternative to <see cref="SetGrabbed"/> for secondary holders (e.g. a held knife
        /// impaled in the bone), so they don't clear a hand grab on the same bone. Pair with <see cref="RemoveHold"/>.
        /// </summary>
        public void AddHold(int boneIndex)
        {
            if (!InRange(boneIndex))
                return;
            _holds[boneIndex]++;
            RebuildGrabMask();
        }

        public void RemoveHold(int boneIndex)
        {
            if (!InRange(boneIndex) || _holds[boneIndex] == 0)
                return;
            _holds[boneIndex]--;
            RebuildGrabMask();
        }

        /// <summary>True if any segment of this bone's limb is held.</summary>
        public bool IsLimbGrabbed(int boneIndex)
        {
            if (!InRange(boneIndex)) return false;
            if (_grabbed[boneIndex] || _holds[boneIndex] > 0) return true;
            int limb = _limb[boneIndex];
            return limb != 0 && (_grabbedLimbMask & (1 << limb)) != 0;
        }

        /// <summary>Long-term functional multiplier (injury). 0.15 = a limp, disabled limb.</summary>
        public void SetBoneFunction(int boneIndex, float multiplier)
        {
            if (!InRange(boneIndex)) return;
            _function[boneIndex] = Mathf.Clamp01(RagdollMath.IsFinite(multiplier) ? multiplier : 1f);
        }

        public float GetBoneFunction(int boneIndex) => InRange(boneIndex) ? _function[boneIndex] : 0f;

        /// <summary>Short-term strength multiplier (e.g. strike tension). Reset with <see cref="ClearOverrides"/>.</summary>
        public void SetBoost(int boneIndex, float boost)
        {
            if (!InRange(boneIndex)) return;
            _boost[boneIndex] = Mathf.Clamp(RagdollMath.IsFinite(boost) ? boost : 1f, 0f, 5f);
        }

        /// <summary>Pulls the body toward its animated world position (0..1). Used to make strikes land.</summary>
        public void SetPinWeight(int boneIndex, float weight)
        {
            if (!InRange(boneIndex)) return;
            _pinWeight[boneIndex] = Mathf.Clamp01(RagdollMath.IsFinite(weight) ? weight : 0f);
        }

        public void ClearOverrides()
        {
            for (int i = 0; i < _boost.Length; i++)
            {
                _boost[i] = 1f;
                _pinWeight[i] = 0f;
            }
        }

        /// <summary>Clears pain, injuries and overrides (respawn/pooling). Grab state is left to the grabbing system.</summary>
        public void ResetState()
        {
            for (int i = 0; i < _pain.Length; i++)
            {
                _pain[i] = 0f;
                _function[i] = 1f;
                _appliedSpring[i] = -1f;
            }
            ClearOverrides();
        }

        internal void OnKilled()
        {
            ClearOverrides();
        }

        // ------------------------------------------------------------------ Lifecycle

        internal bool Initialize(ActiveRagdollCharacter character)
        {
            _character = character;
            var bones = character.BonesInternal;
            int n = bones.Count;
            _inertia = new float[n];
            _pain = new float[n];
            _function = new float[n];
            _boost = new float[n];
            _pinWeight = new float[n];
            _appliedSpring = new float[n];
            _appliedDamper = new float[n];
            _grabbed = new bool[n];
            _holds = new int[n];
            _limb = new int[n];

            var stack = new int[n];
            for (int i = 0; i < n; i++)
            {
                RagdollBone b = bones[i];
                _function[i] = 1f;
                _boost[i] = 1f;
                _appliedSpring[i] = -1f;
                _limb[i] = BoneRoles.GetLimbId(b.role);
                if (b.joint == null || b.parentIndex < 0)
                    continue;
                Vector3 pivot = b.joint.transform.TransformPoint(b.joint.anchor);
                _inertia[i] = ComputeSubtreeInertia(bones, i, pivot, stack);
            }

            _initialized = true;
            return true;
        }

        private static float ComputeSubtreeInertia(System.Collections.Generic.List<RagdollBone> bones, int root, Vector3 pivot, int[] stack)
        {
            float inertia = 0f;
            int top = 0;
            stack[top++] = root;
            while (top > 0)
            {
                int i = stack[--top];
                Rigidbody rb = bones[i].body;
                Vector3 principal = rb.inertiaTensor;
                float own = (principal.x + principal.y + principal.z) / 3f;
                inertia += rb.mass * (rb.worldCenterOfMass - pivot).sqrMagnitude + (RagdollMath.IsFinite(own) ? own : 0f);
                int[] children = bones[i].children;
                for (int c = 0; c < children.Length && top < stack.Length; c++)
                    stack[top++] = children[c];
            }
            return Mathf.Max(1e-4f, inertia);
        }

        internal void PhysicsStep(float dt)
        {
            if (!_initialized)
                return;

            var bones = _character.BonesInternal;
            RagdollProfile profile = _character.Profile;
            float muscle = profile.muscleStrength;
            bool dead = _character.IsDead;
            float painDecay = _painRecoveryRate * dt;

            for (int i = 0; i < bones.Count; i++)
            {
                if (_pain[i] > 0f)
                    _pain[i] = Mathf.Max(0f, _pain[i] - painDecay);

                RagdollBone b = bones[i];
                ConfigurableJoint joint = b.joint;
                if (joint != null && b.parentIndex >= 0)
                {
                    float s = muscle * Mathf.Max(0f, b.muscleMultiplier) * _function[i] * _boost[i] * (1f - _pain[i] * _painWeakening);
                    if (IsLimbGrabbed(i))
                        s *= _grabbedStrength;
                    if (!(s > 0f))
                        s = 0f;

                    MuscleTuning t = GetTuning(b.role);
                    float inertia = _inertia[i];
                    float w = t.frequency;
                    float spring = s * inertia * w * w;
                    float damper = 2f * t.dampingRatio * Mathf.Sqrt(spring * inertia) + _limpDamping * inertia;
                    float maxTorque = spring * (t.saturationAngle * Mathf.Deg2Rad) + damper * MaxDampedAngularSpeed;
                    ApplyDrive(i, joint, spring, damper, maxTorque);

                    // Leave the target alone at zero strength so a relaxed corpse can fall asleep.
                    if (spring > 1e-5f)
                        joint.targetRotation = RagdollMath.JointTargetRotation(b.jointSpace, b.jointSpaceInverse, b.initialRelativeRotation, b.targetRelativeRotation);
                }

                float pin = _pinWeight[i];
                if (pin > 0f && !dead && b.hasTarget && b.body != null)
                    ApplyPin(b, pin * Mathf.Min(1f, muscle));
            }
        }

        private void ApplyDrive(int i, ConfigurableJoint joint, float spring, float damper, float maxTorque)
        {
            float previous = _appliedSpring[i];
            if (previous >= 0f && !Differs(spring, previous) && !Differs(damper, _appliedDamper[i]))
                return;

            joint.slerpDrive = new JointDrive
            {
                positionSpring = spring,
                positionDamper = damper,
                maximumForce = maxTorque,
            };
            _appliedSpring[i] = spring;
            _appliedDamper[i] = damper;
        }

        private bool Differs(float value, float applied)
        {
            if ((value <= 0f) != (applied <= 0f))
                return true;
            return Mathf.Abs(value - applied) > _driveUpdateThreshold * Mathf.Max(Mathf.Abs(applied), 1e-4f);
        }

        private void ApplyPin(RagdollBone b, float weight)
        {
            Rigidbody rb = b.body;
            float w = _pinFrequency;
            Vector3 positionError = b.targetPosition - rb.position;
            Vector3 velocityError = b.targetVelocity - rb.linearVelocity;
            Vector3 acceleration = (positionError * (w * w) + velocityError * (2f * _pinDampingRatio * w)) * weight;
            acceleration = Vector3.ClampMagnitude(acceleration, _pinMaxAcceleration * weight);
            if (RagdollMath.IsFinite(acceleration))
                rb.AddForce(acceleration, ForceMode.Acceleration);
        }

        private MuscleTuning GetTuning(BoneRole role)
        {
            switch (BoneRoles.GetGroup(role))
            {
                case BoneGroup.Head: return _head;
                case BoneGroup.Arm: return _arm;
                case BoneGroup.Hand: return _hand;
                case BoneGroup.Leg: return _leg;
                case BoneGroup.Foot: return _foot;
                default: return _torso;
            }
        }

        private void RebuildGrabMask()
        {
            int mask = 0;
            for (int i = 0; i < _grabbed.Length; i++)
                if ((_grabbed[i] || _holds[i] > 0) && _limb[i] != 0)
                    mask |= 1 << _limb[i];
            _grabbedLimbMask = mask;
        }

        private bool InRange(int boneIndex) => _initialized && boneIndex >= 0 && boneIndex < _pain.Length;

        private void OnValidate()
        {
            _painPerImpulse = Mathf.Max(0f, _painPerImpulse);
            _painRecoveryRate = Mathf.Max(0f, _painRecoveryRate);
            _limpDamping = Mathf.Max(0f, _limpDamping);
            // Force every drive to be re-sent after inspector edits in play mode.
            for (int i = 0; i < _appliedSpring.Length; i++)
                _appliedSpring[i] = -1f;
        }
    }
}
