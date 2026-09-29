using UnityEngine;
using UnityEngine.Events;

namespace ActiveRagdoll
{
    /// <summary>
    /// Physical blade (knife, sword, spear). Distinguishes stabs (tip-first, along the blade), slashes
    /// (edge-on, across the blade) and blunt hits. A good stab can embed: a sliding joint lets the blade
    /// travel deeper with friction, and it pulls out when withdrawn, so a held knife can drag the NPC it is
    /// stuck in (Boneworks-style). Works on anything <see cref="IDamageable"/>; embedding needs a dynamic
    /// rigidbody. Requires a Rigidbody (e.g. a BIMOS grabbable).
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Active Ragdoll/Weapons/Blade Weapon")]
    [RequireComponent(typeof(Rigidbody))]
    public sealed class BladeWeapon : MonoBehaviour, IImpactDamageDealer
    {
        [Header("Geometry")]
        [Tooltip("Transform at the very tip of the blade.")]
        [SerializeField] private Transform _tip = null;
        [Tooltip("Blade direction in this object's local space, from handle toward tip.")]
        [SerializeField] private Vector3 _bladeAxis = Vector3.forward;
        [SerializeField, Min(0.01f)] private float _bladeLength = 0.18f;
        [Tooltip("Colliders that make up the blade (edge). Empty = all colliders on this weapon.")]
        [SerializeField] private Collider[] _bladeColliders;
        [Tooltip("Contacts within this distance (m) of the tip count as tip contacts.")]
        [SerializeField, Min(0.005f)] private float _tipRadius = 0.06f;

        [Header("Stab")]
        [SerializeField, Min(0f)] private float _minStabSpeed = 1.6f;
        [SerializeField, Range(1f, 89f)] private float _maxStabAngle = 35f;
        [SerializeField, Min(0f)] private float _stabDamage = 20f;
        [SerializeField, Min(0f)] private float _stabDamagePerSpeed = 6f;
        [SerializeField, Min(0f)] private float _stabImpulsePerSpeed = 1.5f;

        [Header("Embedding")]
        [SerializeField] private bool _embed = true;
        [Tooltip("Sliding friction (N·s/m) while embedded.")]
        [SerializeField, Min(0f)] private float _embedFriction = 60f;
        [Tooltip("Force (N) that tears the blade out regardless of direction.")]
        [SerializeField, Min(1f)] private float _embedBreakForce = 2500f;
        [Tooltip("Depth (m) below which a withdrawing blade comes out.")]
        [SerializeField, Min(0f)] private float _extractDepth = 0.015f;
        [Tooltip("Time (s) after embedding before the blade can be pulled out (lets it travel in).")]
        [SerializeField, Min(0f)] private float _embedGraceTime = 0.12f;
        [Tooltip("Delay (s) before collisions with the body are restored after extraction.")]
        [SerializeField, Min(0f)] private float _collisionRestoreDelay = 0.3f;

        [Header("Slash")]
        [SerializeField, Min(0f)] private float _minSlashSpeed = 3f;
        [SerializeField, Min(0f)] private float _slashDamagePerSpeed = 4f;
        [SerializeField, Min(0f)] private float _slashImpulsePerSpeed = 0.5f;

        [Header("Blunt")]
        [SerializeField, Min(0f)] private float _minBluntImpulse = 6f;
        [SerializeField, Min(0f)] private float _bluntDamagePerImpulse = 0.5f;

        [Header("Events")]
        [SerializeField] private UnityEvent<Vector3> _onStab = new UnityEvent<Vector3>();
        [SerializeField] private UnityEvent<Vector3> _onSlash = new UnityEvent<Vector3>();
        [SerializeField] private UnityEvent _onEmbed = new UnityEvent();
        [SerializeField] private UnityEvent _onExtract = new UnityEvent();

        private Rigidbody _rb;
        private Vector3 _lastTipVelocity;
        private ConfigurableJoint _embedJoint;
        private Rigidbody _embeddedBody;
        private Collider[] _embeddedColliders;
        private Vector3 _entryLocal;
        private float _embedTime;
        private Collider[] _pendingRestore;
        private float _restoreTime;

        public bool IsEmbedded => _embedJoint != null;
        public Rigidbody EmbeddedBody => _embeddedBody;
        public Vector3 BladeAxisWorld => transform.TransformDirection(_bladeAxis).normalized;
        public Vector3 TipPosition => _tip != null ? _tip.position : transform.position + BladeAxisWorld * _bladeLength;

        private void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            if (_bladeAxis.sqrMagnitude < 1e-6f)
                _bladeAxis = Vector3.forward;
            if (_bladeColliders == null || _bladeColliders.Length == 0)
                _bladeColliders = GetComponentsInChildren<Collider>();
            if (_tip == null)
                Debug.LogWarning($"{ActiveRagdollCharacter.LogPrefix} BladeWeapon '{name}' has no tip transform; using blade axis × length.", this);
        }

        private void FixedUpdate()
        {
            _lastTipVelocity = _rb.GetPointVelocity(TipPosition);

            if (_pendingRestore != null && Time.time >= _restoreTime)
                RestorePendingCollisions();

            if (_embedJoint == null && _embeddedBody != null)
            {
                Extract(); // joint broke or was removed externally
                return;
            }

            if (_embedJoint != null)
            {
                if (_embeddedBody == null || !_embeddedBody.gameObject.activeInHierarchy)
                {
                    Extract();
                    return;
                }

                if (Time.time - _embedTime >= _embedGraceTime)
                {
                    Vector3 axis = BladeAxisWorld;
                    Vector3 entry = _embeddedBody.transform.TransformPoint(_entryLocal);
                    float depth = Vector3.Dot(TipPosition - entry, axis);
                    float outward = Vector3.Dot(_rb.linearVelocity - _embeddedBody.GetPointVelocity(entry), axis);
                    if (depth < _extractDepth && outward <= 0f)
                        Extract();
                }
            }
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (_embedJoint != null || collision.contactCount == 0)
                return;

            Collider other = collision.collider;
            IDamageable target = Damageables.Find(other);
            if (target == null)
                return;

            ContactPoint contact = collision.GetContact(0);
            Rigidbody otherBody = collision.rigidbody;
            Vector3 otherVelocity = otherBody != null ? otherBody.GetPointVelocity(contact.point) : Vector3.zero;
            Vector3 relative = _lastTipVelocity - otherVelocity; // pre-impact, measured before the solver resolved the hit
            Vector3 axis = BladeAxisWorld;
            float along = Vector3.Dot(relative, axis);

            bool nearTip = Vector3.Distance(contact.point, TipPosition) <= _tipRadius;
            if (nearTip && along >= _minStabSpeed && Vector3.Angle(relative, axis) <= _maxStabAngle)
            {
                target.ApplyDamage(new DamageInfo(_stabDamage + along * _stabDamagePerSpeed, DamageType.Stab, contact.point,
                    axis, axis * (along * _stabImpulsePerSpeed), gameObject, other));
                _onStab?.Invoke(contact.point);
                if (_embed && otherBody != null && !otherBody.isKinematic)
                    Embed(otherBody, contact.point);
                return;
            }

            Vector3 lateral = relative - axis * along;
            float lateralSpeed = lateral.magnitude;
            if (lateralSpeed >= _minSlashSpeed && IsBladeCollider(contact.thisCollider))
            {
                Vector3 dir = lateral / lateralSpeed;
                target.ApplyDamage(new DamageInfo(lateralSpeed * _slashDamagePerSpeed, DamageType.Slash, contact.point,
                    dir, dir * (lateralSpeed * _slashImpulsePerSpeed), gameObject, other));
                _onSlash?.Invoke(contact.point);
                return;
            }

            float impulse = collision.impulse.magnitude;
            if (impulse >= _minBluntImpulse)
            {
                Vector3 dir = RagdollMath.SafeNormalize(relative, -contact.normal);
                target.ApplyDamage(new DamageInfo((impulse - _minBluntImpulse) * _bluntDamagePerImpulse, DamageType.Blunt,
                    contact.point, dir, Vector3.zero, gameObject, other));
            }
        }

        private bool IsBladeCollider(Collider c)
        {
            if (c == null || _bladeColliders == null)
                return false;
            for (int i = 0; i < _bladeColliders.Length; i++)
                if (_bladeColliders[i] == c)
                    return true;
            return false;
        }

        private void Embed(Rigidbody body, Vector3 entryPoint)
        {
            RestorePendingCollisions();

            _embeddedBody = body;
            _entryLocal = body.transform.InverseTransformPoint(entryPoint);
            var colliders = new System.Collections.Generic.List<Collider>(2);
            foreach (Collider c in body.GetComponentsInChildren<Collider>())
                if (c.attachedRigidbody == body)
                    colliders.Add(c);
            _embeddedColliders = colliders.ToArray();
            SetIgnore(_embeddedColliders, true);

            Vector3 axisWorld = BladeAxisWorld;
            Vector3 axisLocal = _bladeAxis.normalized;
            Vector3 secondary = Mathf.Abs(Vector3.Dot(axisLocal, Vector3.up)) > 0.9f ? Vector3.forward : Vector3.up;
            secondary = (secondary - axisLocal * Vector3.Dot(secondary, axisLocal)).normalized;
            float half = _bladeLength * 0.5f;

            // The limit is symmetric around the anchor offset, so anchor half a blade behind the tip:
            // offset -half = tip at the entry point, +half = blade fully buried.
            ConfigurableJoint joint = gameObject.AddComponent<ConfigurableJoint>();
            joint.autoConfigureConnectedAnchor = false;
            joint.connectedBody = body;
            joint.axis = axisLocal;
            joint.secondaryAxis = secondary;
            joint.anchor = transform.InverseTransformPoint(TipPosition - axisWorld * half);
            joint.connectedAnchor = _entryLocal;
            joint.xMotion = ConfigurableJointMotion.Limited;
            joint.yMotion = ConfigurableJointMotion.Locked;
            joint.zMotion = ConfigurableJointMotion.Locked;
            joint.angularXMotion = ConfigurableJointMotion.Locked;
            joint.angularYMotion = ConfigurableJointMotion.Locked;
            joint.angularZMotion = ConfigurableJointMotion.Locked;
            joint.linearLimit = new SoftJointLimit { limit = half };
            joint.xDrive = new JointDrive { positionSpring = 0f, positionDamper = _embedFriction, maximumForce = float.MaxValue };
            joint.breakForce = _embedBreakForce;
            joint.breakTorque = _embedBreakForce;
            joint.enableCollision = false;
            joint.enablePreprocessing = false;

            _embedJoint = joint;
            _embedTime = Time.time;
            _onEmbed?.Invoke();
        }

        /// <summary>Releases the blade from whatever it is stuck in.</summary>
        public void Extract()
        {
            if (_embedJoint != null)
                Destroy(_embedJoint);
            _embedJoint = null;

            if (_embeddedColliders != null)
            {
                _pendingRestore = _embeddedColliders;
                _restoreTime = Time.time + _collisionRestoreDelay;
            }
            bool wasEmbedded = _embeddedBody != null;
            _embeddedColliders = null;
            _embeddedBody = null;
            if (wasEmbedded)
                _onExtract?.Invoke();
        }

        private void OnJointBreak(float breakForce)
        {
            // Unity destroys the broken joint itself; FixedUpdate finishes the cleanup.
            _embedJoint = null;
        }

        private void RestorePendingCollisions()
        {
            if (_pendingRestore == null)
                return;
            SetIgnore(_pendingRestore, false);
            _pendingRestore = null;
        }

        private void SetIgnore(Collider[] targets, bool ignore)
        {
            if (targets == null || _bladeColliders == null)
                return;
            for (int i = 0; i < _bladeColliders.Length; i++)
            {
                Collider mine = _bladeColliders[i];
                if (mine == null) continue;
                for (int j = 0; j < targets.Length; j++)
                    if (targets[j] != null)
                        Physics.IgnoreCollision(mine, targets[j], ignore);
            }
        }

        private void OnDisable()
        {
            Extract();
            RestorePendingCollisions();
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.red;
            Vector3 tip = TipPosition;
            Gizmos.DrawLine(tip - BladeAxisWorld * _bladeLength, tip);
            Gizmos.DrawWireSphere(tip, _tipRadius);
        }
    }
}
