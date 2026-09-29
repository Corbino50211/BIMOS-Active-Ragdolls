using UnityEngine;
using UnityEngine.Events;
using Object = UnityEngine.Object;

namespace ActiveRagdoll
{
    /// <summary>
    /// Hit detection for one ragdoll body. Converts collisions into damage/pain/balance input, receives
    /// weapon damage (bullets, stabs, strikes, explosions) through <see cref="IDamageable"/>, and deals the
    /// character's own strike damage when its fist lands during an active strike window.
    /// <para>Always active: corpses still take impulses and raise hit events.</para>
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Active Ragdoll/Body Part")]
    [RequireComponent(typeof(Rigidbody))]
    public sealed class BodyPart : MonoBehaviour, IDamageable
    {
        [SerializeField] private BoneRole _role;

        [Tooltip("Damage multiplier for hits on this part (e.g. 2.5 for the head).")]
        [SerializeField, Min(0f)] private float _damageMultiplier = 1f;

        [Tooltip("Part health. When depleted the part is crippled (muscles weakened; legs stop supporting). 0 = no part health.")]
        [SerializeField, Min(0f)] private float _maxPartHealth;

        [Tooltip("Muscle strength kept by a crippled part.")]
        [SerializeField, Range(0f, 1f)] private float _crippledMuscleMultiplier = 0.15f;

        [Tooltip("A single hit dealing at least this much damage (after multipliers) kills outright, e.g. headshots. 0 = off.")]
        [SerializeField, Min(0f)] private float _lethalDamage;

        [SerializeField] private UnityEvent _onCrippled = new UnityEvent();

        private ActiveRagdollCharacter _character;
        private Rigidbody _body;
        private int _boneIndex = -1;
        private float _partHealth;
        private bool _crippled;

        public BoneRole Role => _role;
        public ActiveRagdollCharacter Character => _character;
        public int BoneIndex => _boneIndex;
        public Rigidbody Body => _body != null ? _body : (_body = GetComponent<Rigidbody>());
        public float DamageMultiplier => _damageMultiplier;
        public float LethalDamage => _lethalDamage;
        public float PartHealth => _partHealth;
        public float MaxPartHealth => _maxPartHealth;
        public bool IsCrippled => _crippled;

        /// <summary>Sets the role and role-appropriate defaults (used when parts are created automatically).</summary>
        internal void SetRole(BoneRole role)
        {
            _role = role;
            switch (BoneRoles.GetGroup(role))
            {
                case BoneGroup.Head:
                    _damageMultiplier = 2.5f;
                    _lethalDamage = 60f;
                    _maxPartHealth = 0f;
                    break;
                case BoneGroup.Torso:
                    _damageMultiplier = role == BoneRole.Pelvis ? 0.9f : 1f;
                    _maxPartHealth = 0f;
                    break;
                case BoneGroup.Arm:
                    _damageMultiplier = 0.6f;
                    _maxPartHealth = 60f;
                    break;
                case BoneGroup.Leg:
                    _damageMultiplier = 0.7f;
                    _maxPartHealth = 70f;
                    break;
                default:
                    _damageMultiplier = 0.4f;
                    _maxPartHealth = 0f;
                    break;
            }
        }

        internal void Bind(ActiveRagdollCharacter character, int boneIndex)
        {
            _character = character;
            _boneIndex = boneIndex;
            _body = GetComponent<Rigidbody>();
            BoneRole expected = character.BonesInternal[boneIndex].role;
            if (_role != expected)
            {
                Debug.LogWarning($"{ActiveRagdollCharacter.LogPrefix} BodyPart on '{name}' says {_role} but is bound as {expected}; using {expected}.", this);
                _role = expected;
            }
            ResetPart();
        }

        /// <summary>Restores part health and function (respawn).</summary>
        public void ResetPart()
        {
            _partHealth = _maxPartHealth;
            _crippled = false;
        }

        /// <summary>Weapon/script damage entry point. Applies <see cref="DamageInfo.impulse"/> at the hit point.</summary>
        public void ApplyDamage(in DamageInfo damage)
        {
            Receive(damage, damage.impulse, true);
        }

        /// <summary>Cripples the part immediately (e.g. scripted injuries).</summary>
        public void Cripple()
        {
            if (_crippled || _character == null)
                return;
            _crippled = true;
            _partHealth = 0f;

            JointMotorDriver motors = _character.Motors;
            if (motors != null && motors.IsInitialized)
                CrippleRecursive(motors, _boneIndex);

            if (BoneRoles.IsLeg(_role))
            {
                BalanceController balance = _character.Balance;
                if (balance != null && balance.IsInitialized)
                    balance.SetLegFunction(BoneRoles.GetSide(_role), 0f);
            }

            _onCrippled?.Invoke();
        }

        private void CrippleRecursive(JointMotorDriver motors, int index)
        {
            motors.SetBoneFunction(index, Mathf.Min(motors.GetBoneFunction(index), _crippledMuscleMultiplier));
            int[] children = _character.BonesInternal[index].children;
            for (int c = 0; c < children.Length; c++)
                CrippleRecursive(motors, children[c]);
        }

        internal void Receive(in DamageInfo info, Vector3 physicalImpulse, bool applyImpulse)
        {
            Rigidbody body = Body;
            if (applyImpulse && body != null && info.impulse.sqrMagnitude > 0f && RagdollMath.IsFinite(info.impulse) && RagdollMath.IsFinite(info.point))
                body.AddForceAtPosition(info.impulse, info.point, ForceMode.Impulse);

            if (_character == null || !_character.IsValid)
                return;

            float applied = 0f;
            RagdollHealth health = _character.Health;
            if (health != null && info.amount > 0f)
                applied = health.ApplyDamage(info, this);

            if (_maxPartHealth > 0f && applied > 0f && !_crippled)
            {
                _partHealth -= applied;
                if (_partHealth <= 0f)
                    Cripple();
            }

            float severity = _character.ComputeSeverity(physicalImpulse.magnitude, applied);
            _character.RegisterHit(new RagdollHit(_boneIndex, _role, info.point, physicalImpulse, applied, info.type, info.source, severity));
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (_character == null || !_character.IsValid)
                return;

            Collider other = collision.collider;
            if (other == null || _character.Owns(other) || collision.contactCount == 0)
                return;

            Rigidbody otherBody = collision.rigidbody;

            // Outgoing: this is the striking fist/forearm during an active strike.
            ProceduralAnimator procedural = _character.Procedural;
            if (!_character.IsDead && procedural != null && procedural.IsStrikingBone(_boneIndex))
                DealStrikeDamage(collision, procedural);

            // Weapons compute their own damage; another character's active strike is dealt by its own fist.
            if (otherBody != null)
            {
                if (otherBody.TryGetComponent(out IImpactDamageDealer _))
                    return;
                if (otherBody.TryGetComponent(out BodyPart otherPart) && otherPart._character != null
                    && otherPart._character.Procedural != null && otherPart._character.Procedural.IsStrikingBone(otherPart._boneIndex))
                    return;
            }

            float impulse = collision.impulse.magnitude;
            if (!(impulse > 0f))
                return;

            RagdollHealth health = _character.Health;
            ImpactDamageSettings settings = health != null ? health.ImpactSettings : s_defaultImpact;
            bool fromBody = otherBody != null;
            float minSpeed = fromBody ? settings.minBodySpeed : settings.minEnvironmentSpeed;
            float minImpulse = fromBody ? settings.minBodyImpulse : settings.minEnvironmentImpulse;
            float relativeSpeed = collision.relativeVelocity.magnitude;

            ContactPoint contact = collision.GetContact(0);
            Vector3 push = contact.normal; // points into this body: the direction it is being pushed
            Vector3 physicalImpulse = push * impulse;
            GameObject source = fromBody ? otherBody.gameObject : other.gameObject;

            if (relativeSpeed < minSpeed || impulse < minImpulse)
            {
                if (fromBody && impulse >= settings.minReactionImpulse)
                    Receive(new DamageInfo(0f, DamageType.Blunt, contact.point, push, Vector3.zero, source, contact.thisCollider), physicalImpulse, false);
                return;
            }

            float perImpulse = fromBody ? settings.bodyDamagePerImpulse : settings.environmentDamagePerImpulse;
            var info = new DamageInfo((impulse - minImpulse) * perImpulse, fromBody ? DamageType.Blunt : DamageType.Impact,
                contact.point, push, Vector3.zero, source, contact.thisCollider);
            Receive(info, physicalImpulse, false);
        }

        private void DealStrikeDamage(Collision collision, ProceduralAnimator procedural)
        {
            Collider other = collision.collider;
            IDamageable target = Damageables.Find(other);
            if (target == null)
                return;

            Object key = target as Object;
            if (target is BodyPart part)
            {
                if (part._character == _character)
                    return;
                if (part._character != null)
                    key = part._character;
            }
            if (key == null || !procedural.TryRegisterStrikeHit(key, out StrikeDefinition strike) || strike == null)
                return;

            ContactPoint contact = collision.GetContact(0);
            Vector3 direction = -contact.normal; // from this fist into the target
            float speedFactor = Mathf.Clamp01(collision.relativeVelocity.magnitude / strike.fullDamageSpeed);
            var info = new DamageInfo(strike.damage * speedFactor, DamageType.Blunt, contact.point, direction,
                direction * (strike.impulse * speedFactor), _character.gameObject, other);
            target.ApplyDamage(info);
        }

        private static readonly ImpactDamageSettings s_defaultImpact = new ImpactDamageSettings();
    }
}
