using System;
using UnityEngine;
using UnityEngine.Events;

namespace ActiveRagdoll
{
    public delegate void DamageHandler(in DamageInfo info, float appliedDamage, BodyPart part);

    /// <summary>
    /// Character-level health. Body parts forward every damaging hit here with their multipliers; reaching
    /// zero (or a lethal single hit) kills the character, which relaxes its muscles into a persistent,
    /// fully physical corpse.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Active Ragdoll/Ragdoll Health")]
    [RequireComponent(typeof(ActiveRagdollCharacter))]
    public sealed class RagdollHealth : MonoBehaviour, IHealth
    {
        [SerializeField, Min(1f)] private float _maxHealth = 100f;
        [SerializeField] private DamageTypeMultipliers _typeMultipliers = new DamageTypeMultipliers();
        [SerializeField] private ImpactDamageSettings _impactDamage = new ImpactDamageSettings();

        [Header("Regeneration")]
        [SerializeField, Min(0f)] private float _regenDelay = 0f;
        [Tooltip("Health per second after the delay. 0 = no regeneration.")]
        [SerializeField, Min(0f)] private float _regenRate = 0f;

        [Header("Events")]
        [SerializeField] private UnityEvent<float> _onDamaged = new UnityEvent<float>();
        [SerializeField] private UnityEvent _onDeath = new UnityEvent();

        private ActiveRagdollCharacter _character;
        private float _current;
        private float _lastDamageTime = float.NegativeInfinity;
        private bool _dead;
        private DamageInfo _lastDamage;

        public event DamageHandler Damaged;
        public event Action<DamageInfo> Died;

        public float MaxHealth => _maxHealth;
        public float CurrentHealth => _current;
        public float Normalized => _current / _maxHealth;
        public bool IsAlive => !_dead;
        public DamageInfo LastDamage => _lastDamage;
        public ImpactDamageSettings ImpactSettings => _impactDamage;

        private void Awake()
        {
            _current = _maxHealth;
        }

        internal void Bind(ActiveRagdollCharacter character)
        {
            if (_character != null)
                _character.Died -= OnCharacterDied;
            _character = character;
            _character.Died += OnCharacterDied;
            _current = _maxHealth;
            _dead = false;
        }

        private void OnDestroy()
        {
            if (_character != null)
                _character.Died -= OnCharacterDied;
        }

        /// <summary>Applies damage with type and body-part multipliers. Returns the damage actually applied.</summary>
        public float ApplyDamage(in DamageInfo info, BodyPart part)
        {
            if (_dead || !(info.amount > 0f) || !RagdollMath.IsFinite(info.amount))
                return 0f;

            float amount = info.amount * _typeMultipliers.Get(info.type) * (part != null ? part.DamageMultiplier : 1f);
            if (amount <= 0f)
                return 0f;

            _current = Mathf.Max(0f, _current - amount);
            _lastDamageTime = Time.time;
            _lastDamage = info;
            Damaged?.Invoke(info, amount, part);
            _onDamaged?.Invoke(amount);

            bool lethalHit = part != null && part.LethalDamage > 0f && amount >= part.LethalDamage;
            if (_current <= 0f || lethalHit)
                Die(info);
            return amount;
        }

        public void Heal(float amount)
        {
            if (_dead || !(amount > 0f))
                return;
            _current = Mathf.Min(_maxHealth, _current + amount);
        }

        public void Kill()
        {
            Die(new DamageInfo(_current, DamageType.Generic, transform.position, Vector3.zero, Vector3.zero));
        }

        /// <summary>Full health and alive again. Also resets every body part. Called by <see cref="ActiveRagdollCharacter.Revive"/>.</summary>
        public void ResetHealth()
        {
            _dead = false;
            _current = _maxHealth;
            _lastDamageTime = float.NegativeInfinity;
            if (_character == null)
                return;
            var bones = _character.BonesInternal;
            for (int i = 0; i < bones.Count; i++)
                if (bones[i].part != null)
                    bones[i].part.ResetPart();
        }

        private void Die(in DamageInfo info)
        {
            if (_dead)
                return;
            _dead = true;
            _current = 0f;
            _lastDamage = info;
            Died?.Invoke(info);
            _onDeath?.Invoke();
            if (_character != null)
                _character.Kill();
        }

        private void OnCharacterDied(ActiveRagdollCharacter character)
        {
            // Killed by something other than damage (fell out of the world, scripted Kill()).
            if (_dead)
                return;
            _dead = true;
            _current = 0f;
            Died?.Invoke(_lastDamage);
            _onDeath?.Invoke();
        }

        private void Update()
        {
            if (_dead || _regenRate <= 0f || _current >= _maxHealth)
                return;
            if (Time.time - _lastDamageTime >= _regenDelay)
                _current = Mathf.Min(_maxHealth, _current + _regenRate * Time.deltaTime);
        }

        private void OnValidate()
        {
            _maxHealth = Mathf.Max(1f, _maxHealth);
            _typeMultipliers ??= new DamageTypeMultipliers();
            _impactDamage ??= new ImpactDamageSettings();
        }
    }
}
