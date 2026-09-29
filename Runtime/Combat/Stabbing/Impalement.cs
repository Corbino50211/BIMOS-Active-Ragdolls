using System.Collections.Generic;
using UnityEngine;

namespace ActiveRagdoll
{
    public enum ExtractReason
    {
        /// <summary>Drawn back out along the wound.</summary>
        PulledOut,
        /// <summary>Wrenched sideways hard enough to break free.</summary>
        TornOut,
        /// <summary><see cref="BladeWeapon.Extract"/> was called.</summary>
        Forced,
        /// <summary>The target or the blade was destroyed or disabled.</summary>
        TargetLost,
    }

    /// <summary>
    /// A blade stuck in something. Created by <see cref="BladeWeapon"/> when a stab gets in.
    /// <para>
    /// A ConfigurableJoint keeps the blade on the line of the wound: it can slide along it (deeper, back out)
    /// and twist about it, can be levered a few degrees, and can't move sideways. Its drives act as friction
    /// along the blade and about its axis, so the blade stays put until pushed, pulled or turned hard enough,
    /// like Boneworks. The wound loosens as it's worked, and cutting deeper, sawing and twisting cause damage
    /// and pain. Pulling the tip back past the entry hole frees it.
    /// </para>
    /// </summary>
    public sealed class Impalement
    {
        private static readonly List<Impalement> s_active = new List<Impalement>();

        /// <summary>Every blade currently stuck in something. Do not modify.</summary>
        public static IReadOnlyList<Impalement> Active => s_active;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => s_active.Clear();

        /// <summary>Number of blades stuck in a character.</summary>
        public static int CountIn(ActiveRagdollCharacter character)
        {
            int n = 0;
            for (int i = 0; i < s_active.Count; i++)
                if (s_active[i].Character == character)
                    n++;
            return n;
        }

        /// <summary>Distance (m) the blade can travel back past the entry hole before the joint stops it.</summary>
        internal const float ExitTravel = 0.05f;

        /// <summary>The blade is out once its tip is this far (m) back outside the entry hole.</summary>
        private const float ExitDepth = -0.004f;

        /// <summary>Drive dampers big enough that the friction caps, not the damping, decide when the blade moves.</summary>
        private const float SlideDamper = 1e5f;
        private const float TwistDamper = 1e4f;

        private const float MaxLooseness = 0.85f;

        /// <summary>Relative spin (rad/s) below which twisting/levering doesn't wound or loosen.</summary>
        private const float WorkThreshold = 0.5f;

        /// <summary>Sliding speed (m/s) below which sawing doesn't wound.</summary>
        private const float SawThreshold = 0.02f;

        public BladeWeapon Blade { get; }

        /// <summary>The collider the blade went into.</summary>
        public Collider Collider { get; }

        /// <summary>The ragdoll part it is in, or null.</summary>
        public BodyPart BodyPart { get; }

        public ActiveRagdollCharacter Character => BodyPart != null ? BodyPart.Character : null;
        public StabMaterial Material { get; }

        /// <summary>How far (m) the tip is past the entry hole.</summary>
        public float Depth { get; private set; }

        public float DeepestDepth { get; private set; }
        public float MaxDepth { get; }

        /// <summary>Total twist (degrees, signed) since the blade went in.</summary>
        public float TwistAngle { get; private set; }

        /// <summary>0 = a fresh tight wound; rises as the blade is twisted and levered (less friction, more play).</summary>
        public float Looseness { get; private set; }

        public bool IsHeld { get; private set; }
        public float Age => Time.time - _startTime;
        public bool IsAttached => _joint != null;

        /// <summary>World position of the entry hole.</summary>
        public Vector3 EntryPoint => _frame != null ? _frame.TransformPoint(_entryLocal) : _entryLocal;

        internal Collider[] KnifeColliders { get; }
        internal Collider[] TargetColliders { get; }

        private readonly PhysicsBodyRef _knife;
        private readonly PhysicsBodyRef _target;
        private readonly bool _targetHadBody;
        private readonly IDamageable _damageable;
        private readonly Transform _frame;
        private readonly Vector3 _entryLocal;
        private readonly float _startTime;

        private ConfigurableJoint _joint;
        private GameObject _anchorObject;
        private bool _hadAnchor;
        private float _lastDepth;
        private float _damage;
        private float _lastDamageTime;
        private float _lastFeedbackTime = float.NegativeInfinity;
        private float _appliedSwing = -1f;
        private float _appliedGrip = -1f;
        private JointMotorDriver _heldMotors;

        private Impalement(BladeWeapon blade, PhysicsBodyRef knife, PhysicsBodyRef target, Collider collider, IDamageable damageable,
            StabMaterial material, Transform frame, Vector3 entryLocal, float depth, float maxDepth, Collider[] knifeColliders, Collider[] targetColliders)
        {
            Blade = blade;
            _knife = knife;
            _target = target;
            _targetHadBody = target.Exists;
            Collider = collider;
            _damageable = damageable;
            BodyPart = damageable as BodyPart;
            Material = material;
            _frame = frame;
            _entryLocal = entryLocal;
            Depth = DeepestDepth = _lastDepth = depth;
            MaxDepth = maxDepth;
            KnifeColliders = knifeColliders;
            TargetColliders = targetColliders;
            _startTime = _lastDamageTime = Time.time;
        }

        /// <summary>
        /// Joins the blade to the target. <paramref name="entry"/> must lie on the blade line; the tip is
        /// <paramref name="depth"/> past it along <paramref name="axis"/>. Returns null for combinations PhysX
        /// can't joint (an articulation blade into another articulation).
        /// </summary>
        internal static Impalement Create(BladeWeapon blade, PhysicsBodyRef knife, Collider collider, IDamageable damageable,
            StabMaterial material, Vector3 entry, Vector3 axis, float depth, float maxDepth, Collider[] knifeColliders)
        {
            PhysicsBodyRef target = PhysicsBodyRef.FromCollider(collider);
            if (!knife.Exists || target.Is(knife) || (knife.Articulation != null && target.Articulation != null))
                return null;

            // A Joint needs a Rigidbody on its own GameObject. Rigidbody blades carry the joint; an
            // articulation blade (BIMOS demo style) is the joint's connected body instead, hung off the target's
            // rigidbody or, in the static world, off a kinematic anchor that follows the stabbed object.
            GameObject anchor = null;
            GameObject owner;
            bool ownerIsKnife = knife.Rigidbody != null;
            if (ownerIsKnife)
                owner = knife.GameObject;
            else if (target.Rigidbody != null)
                owner = target.GameObject;
            else
            {
                anchor = new GameObject("Blade Anchor");
                anchor.transform.SetPositionAndRotation(entry, Quaternion.LookRotation(axis));
                anchor.transform.SetParent(collider.transform, true);
                var anchorBody = anchor.AddComponent<Rigidbody>();
                anchorBody.isKinematic = true;
                owner = anchor;
            }

            // The linear limit is symmetric about the anchors, so offset the blade-side anchor to put the
            // allowed range at [-ExitTravel, maxDepth] of tip depth.
            float centre = (maxDepth - ExitTravel) * 0.5f;
            float halfRange = (maxDepth + ExitTravel) * 0.5f;
            Vector3 tip = entry + axis * depth;
            Vector3 bladePoint = tip - axis * centre;

            Transform ownerT = owner.transform;
            Vector3 secondary = Vector3.ProjectOnPlane(knife.Transform.up, axis);
            if (secondary.sqrMagnitude < 1e-4f)
                secondary = Vector3.ProjectOnPlane(knife.Transform.right, axis);

            var joint = owner.AddComponent<ConfigurableJoint>();
            joint.autoConfigureConnectedAnchor = false;
            if (ownerIsKnife)
            {
                if (target.Rigidbody != null)
                    joint.connectedBody = target.Rigidbody;
                else if (target.Articulation != null)
                    joint.connectedArticulationBody = target.Articulation;
                joint.anchor = ownerT.InverseTransformPoint(bladePoint);
                joint.connectedAnchor = target.Exists ? target.Transform.InverseTransformPoint(entry) : entry;
            }
            else
            {
                joint.connectedArticulationBody = knife.Articulation;
                joint.anchor = ownerT.InverseTransformPoint(entry);
                joint.connectedAnchor = knife.Transform.InverseTransformPoint(bladePoint);
            }
            joint.axis = ownerT.InverseTransformDirection(axis);
            joint.secondaryAxis = ownerT.InverseTransformDirection(secondary.normalized);
            joint.xMotion = ConfigurableJointMotion.Limited;
            joint.yMotion = ConfigurableJointMotion.Locked;
            joint.zMotion = ConfigurableJointMotion.Locked;
            joint.angularXMotion = ConfigurableJointMotion.Free;
            joint.rotationDriveMode = RotationDriveMode.XYAndZ;
            joint.linearLimit = new SoftJointLimit { limit = halfRange };
            joint.breakForce = material.breakForce;
            joint.breakTorque = material.breakForce * 0.12f;
            joint.enableCollision = false;
            joint.enablePreprocessing = false;

            var targetColliders = new List<Collider>(4);
            if (target.Exists)
            {
                foreach (Collider c in target.Transform.GetComponentsInChildren<Collider>())
                    if (!c.isTrigger && (target.Rigidbody != null ? c.attachedRigidbody == target.Rigidbody : c.attachedArticulationBody == target.Articulation))
                        targetColliders.Add(c);
            }
            if (!targetColliders.Contains(collider))
                targetColliders.Add(collider);
            Collider[] targets = targetColliders.ToArray();
            SetIgnore(knifeColliders, targets, true);

            Transform frame = target.Exists ? target.Transform : (anchor != null ? anchor.transform : null);
            Vector3 entryLocal = frame != null ? frame.InverseTransformPoint(entry) : entry;

            var impalement = new Impalement(blade, knife, target, collider, damageable, material, frame, entryLocal,
                depth, maxDepth, knifeColliders, targets)
            {
                _joint = joint,
                _anchorObject = anchor,
                _hadAnchor = anchor != null,
            };
            impalement.ApplySwingLimit(material.swingLimit);
            impalement.ApplyFriction(Mathf.Lerp(0.25f, 1f, Mathf.Clamp01(depth / Mathf.Max(0.01f, maxDepth))));
            knife.WakeUp();
            target.WakeUp();
            s_active.Add(impalement);
            return impalement;
        }

        /// <summary>Runs friction, wound and hold logic. Returns false (with a reason) when the blade comes out.</summary>
        internal bool Step(float dt, out ExtractReason reason)
        {
            reason = ExtractReason.TargetLost;
            if (!_knife.Exists || (_targetHadBody && !_target.Exists) || Collider == null || !Collider.enabled
                || !Collider.gameObject.activeInHierarchy || (_hadAnchor && _anchorObject == null))
                return false;
            if (_joint == null)
            {
                reason = ExtractReason.TornOut; // Unity destroys a joint that exceeds its break force
                return false;
            }

            Vector3 axis = Blade.BladeAxisWorld;
            Vector3 entry = EntryPoint;
            float depth = Vector3.Dot(Blade.TipPosition - entry, axis);
            if (!RagdollMath.IsFinite(depth))
                return false;
            Depth = depth;

            if (Age >= Blade.EmbedGraceTime && depth < ExitDepth)
            {
                reason = ExtractReason.PulledOut;
                return false;
            }

            float fill = Mathf.Clamp01(depth / Mathf.Max(0.01f, MaxDepth));
            ApplyFriction(Mathf.Lerp(0.25f, 1f, fill) * (1f - Looseness));

            float along = Vector3.Dot(_knife.GetPointVelocity(entry) - _target.GetPointVelocity(entry), axis);
            Vector3 relativeSpin = _knife.AngularVelocity - _target.AngularVelocity;
            float spin = Vector3.Dot(relativeSpin, axis);

            // Working the blade opens the wound. Solver jitter while the body moves around with the blade in
            // it is ignored; only deliberate motion counts.
            float lever = (relativeSpin - axis * spin).magnitude;
            TwistAngle += spin * dt * Mathf.Rad2Deg;
            float twistDegrees = Mathf.Abs(spin) > WorkThreshold ? spin * dt * Mathf.Rad2Deg : 0f;
            float leverDegrees = lever > WorkThreshold ? lever * dt * Mathf.Rad2Deg : 0f;
            Looseness = Mathf.Min(MaxLooseness, Looseness + (Mathf.Abs(twistDegrees) + leverDegrees) / 360f * Material.woundWidening);
            ApplySwingLimit(Material.swingLimit * (1f + 2f * Looseness));

            float newCut = 0f;
            if (depth > DeepestDepth)
            {
                newCut = depth - DeepestDepth;
                DeepestDepth = depth;
            }
            float saw = Mathf.Max(0f, Mathf.Abs(depth - _lastDepth) - newCut);
            if (saw < SawThreshold * dt)
                saw = 0f;
            _lastDepth = depth;

            if (_damageable != null && Material.woundDamageScale > 0f)
            {
                _damage += (newCut * Blade.CutDamagePerMeter + saw * Blade.SawDamagePerMeter
                    + Mathf.Abs(twistDegrees) * Blade.TwistDamagePerDegree + leverDegrees * Blade.LeverDamagePerDegree) * Material.woundDamageScale;
                if (_damage >= 3f || (_damage > 0.05f && Time.time - _lastDamageTime >= 0.3f))
                    FlushDamage(entry, axis);
            }

            UpdateHold();
            AddPain(dt * Blade.EmbeddedPainPerSecond + Mathf.Abs(twistDegrees) * Blade.TwistPainPerDegree);

            float grind = Mathf.Clamp01(Mathf.Abs(along) / 0.4f + Mathf.Abs(spin) / 5f);
            if (grind > 0.1f && Time.time - _lastFeedbackTime >= 0.05f)
            {
                _lastFeedbackTime = Time.time;
                Blade.SendFeedback(BladeFeedback.Grind, grind * (1f - 0.6f * Looseness));
            }
            return true;
        }

        /// <summary>Removes the joint and pending effects. Collisions are restored later by the blade.</summary>
        internal void Release()
        {
            if (_damage > 0.05f && _damageable != null)
                FlushDamage(EntryPoint, Blade.BladeAxisWorld);
            if (_joint != null)
            {
                // Destroy is deferred to the end of the frame; free the joint now so later physics steps
                // this frame don't hold the blade.
                _joint.xMotion = ConfigurableJointMotion.Free;
                _joint.yMotion = ConfigurableJointMotion.Free;
                _joint.zMotion = ConfigurableJointMotion.Free;
                _joint.angularYMotion = ConfigurableJointMotion.Free;
                _joint.angularZMotion = ConfigurableJointMotion.Free;
                _joint.xDrive = default;
                _joint.angularXDrive = default;
                _joint.angularYZDrive = default;
                Object.Destroy(_joint);
            }
            _joint = null;
            if (_anchorObject != null)
                Object.Destroy(_anchorObject);
            _anchorObject = null;
            SetHold(false);
            s_active.Remove(this);
        }

        private void FlushDamage(Vector3 entry, Vector3 axis)
        {
            float amount = _damage;
            _damage = 0f;
            _lastDamageTime = Time.time;
            _damageable.ApplyDamage(new DamageInfo(amount, DamageType.Stab, entry, axis, Vector3.zero, Blade.AttributionSource, Collider));
        }

        private void UpdateHold() => SetHold(Blade.IsHeld);

        private void SetHold(bool held)
        {
            IsHeld = held;
            // While a hand holds a blade stuck in a live NPC, that limb goes weak like a grabbed limb, so the
            // handle can drag, twist and pin it.
            bool weaken = held && Blade.HeldWeakensLimb && BodyPart != null && Character != null && !Character.IsDead;
            if (weaken == (_heldMotors != null))
                return;
            if (weaken)
            {
                JointMotorDriver motors = Character.Motors;
                if (motors == null || !motors.IsInitialized)
                    return;
                motors.AddHold(BodyPart.BoneIndex);
                _heldMotors = motors;
            }
            else
            {
                _heldMotors.RemoveHold(BodyPart.BoneIndex);
                _heldMotors = null;
            }
        }

        private void AddPain(float amount)
        {
            if (amount <= 0f || BodyPart == null || Character == null || Character.IsDead)
                return;
            JointMotorDriver motors = Character.Motors;
            if (motors != null && motors.IsInitialized)
                motors.AddPain(BodyPart.BoneIndex, amount);
        }

        private void ApplySwingLimit(float degrees)
        {
            if (_joint == null || Mathf.Abs(degrees - _appliedSwing) < 0.5f)
                return;
            _appliedSwing = degrees;
            ConfigurableJointMotion motion = degrees < 0.25f ? ConfigurableJointMotion.Locked : ConfigurableJointMotion.Limited;
            _joint.angularYMotion = motion;
            _joint.angularZMotion = motion;
            var limit = new SoftJointLimit { limit = Mathf.Min(degrees, 177f) };
            _joint.angularYLimit = limit;
            _joint.angularZLimit = limit;
        }

        /// <summary>
        /// Friction along and about the blade, as velocity drives with a huge damper capped at the friction
        /// force: the solver holds the blade still until something pushes, pulls or turns it harder than the
        /// cap, then lets it slide at the cap (Coulomb friction). Being implicit, it holds a 0.2 kg knife
        /// against a hand as stably as it holds a 70 kg player hanging from it.
        /// </summary>
        private void ApplyFriction(float grip)
        {
            if (_joint == null || Mathf.Abs(grip - _appliedGrip) < 0.02f)
                return;
            _appliedGrip = grip;
            float twist = Material.twistFriction * grip;
            _joint.xDrive = new JointDrive { positionSpring = 0f, positionDamper = SlideDamper, maximumForce = Material.slideFriction * grip };
            _joint.angularXDrive = new JointDrive { positionSpring = 0f, positionDamper = TwistDamper, maximumForce = twist };
            // Levering within the swing play meets some resistance too.
            _joint.angularYZDrive = new JointDrive { positionSpring = 0f, positionDamper = TwistDamper, maximumForce = twist * 2f };
        }

        internal static void SetIgnore(Collider[] a, Collider[] b, bool ignore)
        {
            if (a == null || b == null)
                return;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] == null) continue;
                for (int j = 0; j < b.Length; j++)
                    if (b[j] != null && a[i] != b[j])
                        Physics.IgnoreCollision(a[i], b[j], ignore);
            }
        }
    }
}
