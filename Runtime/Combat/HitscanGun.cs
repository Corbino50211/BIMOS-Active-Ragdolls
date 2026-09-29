using UnityEngine;
using UnityEngine.Events;

namespace ActiveRagdoll
{
    /// <summary>
    /// Hitscan firearm with physical recoil. Designed for BIMOS guns: wire <c>Interactable.TriggerDownEvent</c>
    /// to <see cref="TriggerDown"/> and <c>TriggerUpEvent</c> to <see cref="TriggerUp"/> (or
    /// <c>Interactable.OnTick</c> to <see cref="OnTriggerTick"/>). Recoil is an impulse on the gun's own
    /// rigidbody at the muzzle, so the player's physics hands feel it. NPCs can pick it up and fire it too
    /// (it is an <see cref="NPCWeapon"/>; Muzzle and Range live in the NPC Use section).
    /// </summary>
    [AddComponentMenu("Active Ragdoll/Weapons/Hitscan Gun")]
    public sealed class HitscanGun : NPCWeapon
    {
        [Header("Ballistics")]
        [SerializeField, Min(0f)] private float _damage = 35f;
        [Tooltip("Knock-back impulse (N·s) delivered at the hit point.")]
        [SerializeField, Min(0f)] private float _impulse = 10f;
        [Tooltip("Cone half-angle (degrees).")]
        [SerializeField, Range(0f, 15f)] private float _spread = 0.4f;
        [SerializeField] private LayerMask _hitMask = ~0;
        [Tooltip("Never hit the BIMOS player rig (the shooter's own hands and body).")]
        [SerializeField] private bool _ignorePlayerRig = true;

        [Header("Fire Control")]
        [SerializeField, Min(1f)] private float _roundsPerMinute = 480f;
        [SerializeField] private bool _automatic = false;
        [Tooltip("Trigger value (0..1) treated as pulled when using OnTriggerTick.")]
        [SerializeField, Range(0.05f, 1f)] private float _triggerThreshold = 0.6f;

        [Header("Recoil")]
        [SerializeField] private Rigidbody _recoilBody;
        [SerializeField, Min(0f)] private float _recoilImpulse = 1.2f;

        [Header("Events")]
        [SerializeField] private UnityEvent _onFire = new UnityEvent();
        [Tooltip("Hit point and surface normal, for impact effects.")]
        [SerializeField] private UnityEvent<Vector3, Vector3> _onHit = new UnityEvent<Vector3, Vector3>();

        private float _nextFireTime;
        private bool _triggerHeld;
        private int _mask;

        public bool TriggerHeld => _triggerHeld;

        protected override void Awake()
        {
            base.Awake();
            if (_recoilBody == null)
                _recoilBody = GetComponentInParent<Rigidbody>();
            _mask = _hitMask.value;
            if (_ignorePlayerRig)
                _mask &= ~RagdollLayers.MaskOf(RagdollLayers.BIMOSRigLayer);
        }

        /// <summary>Fires one round if the fire rate allows. Returns true if a round was fired.</summary>
        public bool Fire() => Fire(null);

        /// <summary>NPC use: fires one round, never hitting the shooter's own body.</summary>
        public override bool NPCFire(GameObject shooter)
        {
            ActiveRagdollCharacter character = shooter != null ? shooter.GetComponent<ActiveRagdollCharacter>() : null;
            return Fire(character);
        }

        private bool Fire(ActiveRagdollCharacter shooter)
        {
            if (!isActiveAndEnabled || Time.time < _nextFireTime)
                return false;
            _nextFireTime = Time.time + 60f / _roundsPerMinute;

            Transform muzzle = Muzzle;
            Vector3 forward = muzzle.forward;
            if (_spread > 0f)
            {
                Vector2 r = Random.insideUnitCircle * Mathf.Tan(_spread * Mathf.Deg2Rad);
                forward = (forward + muzzle.right * r.x + muzzle.up * r.y).normalized;
            }

            Transform ignore = _recoilBody != null ? _recoilBody.transform : transform;
            GameObject source = shooter != null ? shooter.gameObject : gameObject;
            if (Ballistics.FireHitscan(muzzle.position, forward, Range * 5f, _mask, ignore, _damage, _impulse, source, out RaycastHit hit, DamageType.Bullet, shooter))
                _onHit?.Invoke(hit.point, hit.normal);

            if (_recoilBody != null && !_recoilBody.isKinematic && _recoilImpulse > 0f)
                _recoilBody.AddForceAtPosition(-forward * _recoilImpulse, muzzle.position, ForceMode.Impulse);

            _onFire?.Invoke();
            return true;
        }

        /// <summary>Semi-auto fires immediately; automatic keeps firing while held.</summary>
        public void TriggerDown()
        {
            _triggerHeld = true;
            Fire();
        }

        public void TriggerUp()
        {
            _triggerHeld = false;
        }

        /// <summary>Signature matches BIMOS <c>Interactable.TickEvent</c> (trigger, primary, secondary).</summary>
        public void OnTriggerTick(float trigger, bool primary, bool secondary)
        {
            bool pulled = trigger >= _triggerThreshold;
            if (pulled && !_triggerHeld)
                TriggerDown();
            else if (!pulled && _triggerHeld)
                TriggerUp();
        }

        private void Update()
        {
            if (_automatic && _triggerHeld)
                Fire();
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            _triggerHeld = false;
        }
    }
}
