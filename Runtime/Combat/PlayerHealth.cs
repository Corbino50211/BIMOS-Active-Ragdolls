using UnityEngine;
using UnityEngine.Events;

namespace ActiveRagdoll
{
    /// <summary>
    /// Simple player health that NPC strikes (and any other <see cref="IDamageable"/> source) can damage.
    /// Put it on the player root; hits on any child collider find it. Physical impulses from hits are
    /// applied to the rigidbody that was struck, so the player's physics rig gets shoved too.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Active Ragdoll/Player Health")]
    public class PlayerHealth : MonoBehaviour, IDamageable, IHealth
    {
        [SerializeField, Min(1f)] private float _maxHealth = 100f;
        [SerializeField, Min(0f)] private float _regenDelay = 4f;
        [SerializeField, Min(0f)] private float _regenRate = 12f;
        [Tooltip("Scales the physical impulse of incoming hits on the player's bodies.")]
        [SerializeField, Min(0f)] private float _impulseScale = 1f;

        [SerializeField] private UnityEvent<float> _onDamaged = new UnityEvent<float>();
        [SerializeField] private UnityEvent _onDeath = new UnityEvent();
        [SerializeField] private UnityEvent _onRespawn = new UnityEvent();

        private float _current;
        private float _lastDamageTime = float.NegativeInfinity;
        private bool _dead;

        public float MaxHealth => _maxHealth;
        public float CurrentHealth => _current;
        public bool IsAlive => !_dead;

        public event System.Action<DamageInfo, float> Damaged;
        public event System.Action Died;

        protected virtual void Awake()
        {
            _current = _maxHealth;
        }

        /// <summary>Override to scale damage by where the player was hit (e.g. blocking with the hands).</summary>
        protected virtual float ModifyDamage(in DamageInfo damage) => damage.amount;

        public void ApplyDamage(in DamageInfo damage)
        {
            if (damage.hitCollider != null && damage.impulse.sqrMagnitude > 0f && RagdollMath.IsFinite(damage.impulse))
            {
                Rigidbody rb = damage.hitCollider.attachedRigidbody;
                if (rb != null && !rb.isKinematic)
                    rb.AddForceAtPosition(damage.impulse * _impulseScale, damage.point, ForceMode.Impulse);
            }

            if (_dead)
                return;

            float amount = ModifyDamage(damage);
            if (!(amount > 0f) || !RagdollMath.IsFinite(amount))
                return;

            _current = Mathf.Max(0f, _current - amount);
            _lastDamageTime = Time.time;
            Damaged?.Invoke(damage, amount);
            _onDamaged?.Invoke(amount);
            if (_current <= 0f)
            {
                _dead = true;
                Died?.Invoke();
                _onDeath?.Invoke();
            }
        }

        public void Respawn()
        {
            _dead = false;
            _current = _maxHealth;
            _onRespawn?.Invoke();
        }

        protected virtual void Update()
        {
            if (_dead || _regenRate <= 0f || _current >= _maxHealth)
                return;
            if (Time.time - _lastDamageTime >= _regenDelay)
                _current = Mathf.Min(_maxHealth, _current + _regenRate * Time.deltaTime);
        }
    }
}
