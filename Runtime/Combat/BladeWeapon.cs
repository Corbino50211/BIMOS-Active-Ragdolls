using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace ActiveRagdoll
{
    /// <summary>
    /// Physical blade (knife, sword, spear) with Boneworks-style stabbing. Distinguishes stabs (tip first,
    /// along the blade), slashes (edge on) and blunt hits.
    /// <para>
    /// A stab that gets in leaves the blade stuck (<see cref="Impalement"/>): it stays in when let go, slides
    /// deeper when pushed and back out when pulled against friction, twists and levers in the wound (which
    /// hurts and loosens it), drags whatever it's in, and frees itself when drawn out or wrenched sideways.
    /// Blades get in by speed (a stab) or by steady pressure (leaning on the handle). They stick into ragdolls,
    /// into the world (so you can climb on a knife in a wall), and into anything with a <see cref="StabbableSurface"/>.
    /// </para>
    /// Put it on the blade's Rigidbody or ArticulationBody GameObject (e.g. a BIMOS grabbable).
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Active Ragdoll/Weapons/Blade Weapon")]
    public sealed class BladeWeapon : MonoBehaviour, IImpactDamageDealer
    {
        private static readonly List<BladeWeapon> s_all = new List<BladeWeapon>();
        private static readonly ContactPoint[] s_contacts = new ContactPoint[16];

        /// <summary>Every enabled blade. Do not modify.</summary>
        public static IReadOnlyList<BladeWeapon> All => s_all;

        /// <summary>Raised when a blade is enabled (integrations use it to attach wielder adapters).</summary>
        public static event Action<BladeWeapon> Registered;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            s_all.Clear();
            Registered = null;
        }

        [Header("Geometry")]
        [Tooltip("Transform at the very tip of the blade.")]
        [SerializeField] private Transform _tip = null;
        [Tooltip("Blade direction in this object's local space, from handle toward tip.")]
        [SerializeField] private Vector3 _bladeAxis = Vector3.forward;
        [Tooltip("Length (m) of the sharp part, tip to guard. The blade can go in this far.")]
        [SerializeField, Min(0.01f)] private float _bladeLength = 0.18f;
        [Tooltip("Colliders that make up the blade. Empty = all colliders on this weapon. Colliders left out (the guard) stop the blade going in further.")]
        [SerializeField] private Collider[] _bladeColliders;
        [Tooltip("Contacts within this distance (m) of the tip count as tip contacts.")]
        [SerializeField, Min(0.005f)] private float _tipRadius = 0.05f;

        [Header("Stab")]
        [Tooltip("The tip must travel within this many degrees of the blade axis.")]
        [SerializeField, Range(1f, 89f)] private float _maxStabAngle = 35f;
        [Tooltip("The blade must meet the surface within this many degrees of head-on. Shallower hits glance off.")]
        [SerializeField, Range(1f, 89f)] private float _maxSurfaceAngle = 65f;
        [SerializeField, Min(0f)] private float _stabDamage = 20f;
        [SerializeField, Min(0f)] private float _stabDamagePerSpeed = 6f;
        [SerializeField, Min(0f)] private float _stabImpulsePerSpeed = 1.5f;

        [Header("Embedding")]
        [SerializeField] private bool _embed = true;
        [Tooltip("How ragdoll body parts take the blade.")]
        [SerializeField] private StabMaterial _fleshMaterial = StabMaterial.Flesh();
        [Tooltip("Stick into the environment and props (walls, tables, crates).")]
        [SerializeField] private bool _stickInWorld = true;
        [Tooltip("How the environment takes the blade. Override per object with a Stabbable Surface.")]
        [SerializeField] private StabMaterial _worldMaterial = StabMaterial.Wood();
        [Tooltip("Time (s) after going in before it can be pulled out (lets it travel in).")]
        [SerializeField, Min(0f)] private float _embedGraceTime = 0.1f;
        [Tooltip("Delay (s) before collisions with what it was stuck in are restored after it comes out.")]
        [SerializeField, Min(0f)] private float _collisionRestoreDelay = 0.25f;

        [Header("Wound (while stuck in something damageable)")]
        [Tooltip("Damage per metre of new depth cut.")]
        [SerializeField, Min(0f)] private float _cutDamagePerMeter = 120f;
        [Tooltip("Damage per metre of sliding back and forth in the wound.")]
        [SerializeField, Min(0f)] private float _sawDamagePerMeter = 40f;
        [Tooltip("Damage per degree of twisting.")]
        [SerializeField, Min(0f)] private float _twistDamagePerDegree = 0.08f;
        [Tooltip("Damage per degree of levering side to side.")]
        [SerializeField, Min(0f)] private float _leverDamagePerDegree = 0.05f;
        [Tooltip("Pain (0..1 per second) in the stabbed limb while the blade is in it.")]
        [SerializeField, Min(0f)] private float _embeddedPainPerSecond = 0.1f;
        [Tooltip("Pain per degree of twisting (a quarter turn ≈ 0.35).")]
        [SerializeField, Min(0f)] private float _twistPainPerDegree = 0.004f;
        [Tooltip("While a hand holds the blade, the stabbed limb goes weak like a grabbed limb, so the NPC can be dragged and pinned by the handle.")]
        [SerializeField] private bool _heldWeakensLimb = true;

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

        private struct PendingRestore
        {
            public Collider[] mine;
            public Collider[] theirs;
            public float time;
            public float deadline;
        }

        private PhysicsBodyRef _body;
        private Vector3 _lastTipVelocity;
        private Impalement _impalement;
        private float _nextEmbedTime;
        private IBladeWielder _wielder;
        private readonly List<PendingRestore> _pendingRestores = new List<PendingRestore>();

        public event Action<BladeWeapon, Impalement> Embedded;
        public event Action<BladeWeapon, Impalement, ExtractReason> Extracted;

        /// <summary>Raised for haptics/audio: stab, embed, grind (continuous), extract, tear-out, glance.</summary>
        public event Action<BladeWeapon, BladeFeedback, float> Feedback;

        public bool IsEmbedded => _impalement != null;

        /// <summary>What the blade is stuck in, or null.</summary>
        public Impalement Current => _impalement;

        /// <summary>The rigidbody the blade is stuck in, if any.</summary>
        public Rigidbody EmbeddedBody => _impalement != null && _impalement.Collider != null ? _impalement.Collider.attachedRigidbody : null;

        public Vector3 BladeAxisWorld => transform.TransformDirection(_bladeAxis).normalized;
        public Vector3 TipPosition => _tip != null ? _tip.position : transform.position + BladeAxisWorld * _bladeLength;
        public float BladeLength => _bladeLength;

        /// <summary>True while a wielder (e.g. a BIMOS hand) is holding it.</summary>
        public bool IsHeld => _wielder != null && _wielder.IsHoldingBlade;

        /// <summary>Who gets blamed for damage: the wielder if held, else the blade itself.</summary>
        public GameObject AttributionSource
        {
            get
            {
                GameObject w = _wielder != null && _wielder.IsHoldingBlade ? _wielder.Wielder : null;
                return w != null ? w : gameObject;
            }
        }

        public StabMaterial FleshMaterial => _fleshMaterial;
        public StabMaterial WorldMaterial => _worldMaterial;

        internal float EmbedGraceTime => _embedGraceTime;
        internal float CutDamagePerMeter => _cutDamagePerMeter;
        internal float SawDamagePerMeter => _sawDamagePerMeter;
        internal float TwistDamagePerDegree => _twistDamagePerDegree;
        internal float LeverDamagePerDegree => _leverDamagePerDegree;
        internal float EmbeddedPainPerSecond => _embeddedPainPerSecond;
        internal float TwistPainPerDegree => _twistPainPerDegree;
        internal bool HeldWeakensLimb => _heldWeakensLimb;

        /// <summary>Sets who is holding the blade (null = nobody). Components implementing <see cref="IBladeWielder"/> on this GameObject are found automatically.</summary>
        public void SetWielder(IBladeWielder wielder) => _wielder = wielder;

        private void Awake()
        {
            _body = PhysicsBodyRef.FromTransform(transform);
            if (!_body.Exists)
                Debug.LogError($"{ActiveRagdollCharacter.LogPrefix} BladeWeapon '{name}' needs a Rigidbody or ArticulationBody.", this);
            else if (_body.Transform != transform)
                Debug.LogWarning($"{ActiveRagdollCharacter.LogPrefix} BladeWeapon '{name}' should be on the same GameObject as its Rigidbody/ArticulationBody ('{_body.Transform.name}') to receive collisions.", this);

            if (_bladeAxis.sqrMagnitude < 1e-6f)
                _bladeAxis = Vector3.forward;
            if (_bladeColliders == null || _bladeColliders.Length == 0)
                _bladeColliders = GetComponentsInChildren<Collider>();
            if (_wielder == null)
                _wielder = GetComponent<IBladeWielder>();
            _fleshMaterial ??= StabMaterial.Flesh();
            _worldMaterial ??= StabMaterial.Wood();
        }

        private void Start()
        {
            if (_tip == null)
                Debug.LogWarning($"{ActiveRagdollCharacter.LogPrefix} BladeWeapon '{name}' has no tip transform; using blade axis × length.", this);
        }

        /// <summary>Sets up the blade geometry from code (for blades built at runtime).</summary>
        public void Configure(Transform tip, Vector3 localBladeAxis, float bladeLength, Collider[] bladeColliders = null)
        {
            _tip = tip;
            _bladeAxis = localBladeAxis.sqrMagnitude > 1e-6f ? localBladeAxis : Vector3.forward;
            _bladeLength = Mathf.Max(0.01f, bladeLength);
            _bladeColliders = bladeColliders != null && bladeColliders.Length > 0 ? bladeColliders : GetComponentsInChildren<Collider>();
        }

        private void OnEnable()
        {
            if (!s_all.Contains(this))
                s_all.Add(this);
            Registered?.Invoke(this);
        }

        private void OnDisable()
        {
            s_all.Remove(this);
            Extract();
            for (int i = 0; i < _pendingRestores.Count; i++)
                Impalement.SetIgnore(_pendingRestores[i].mine, _pendingRestores[i].theirs, false);
            _pendingRestores.Clear();
        }

        private void FixedUpdate()
        {
            if (!_body.Exists)
                return;
            _lastTipVelocity = _body.GetPointVelocity(TipPosition);
            ProcessPendingRestores();

            if (_impalement != null && !_impalement.Step(Time.fixedDeltaTime, out ExtractReason reason))
                FinishExtraction(reason);
        }

        /// <summary>Frees the blade from whatever it is stuck in.</summary>
        public void Extract() => FinishExtraction(ExtractReason.Forced);

        private void FinishExtraction(ExtractReason reason)
        {
            Impalement impalement = _impalement;
            if (impalement == null)
                return;
            _impalement = null;
            impalement.Release();
            _pendingRestores.Add(new PendingRestore
            {
                mine = impalement.KnifeColliders,
                theirs = impalement.TargetColliders,
                time = Time.time + _collisionRestoreDelay,
                deadline = Time.time + _collisionRestoreDelay + 2f,
            });
            _nextEmbedTime = Time.time + 0.15f;

            SendFeedback(reason == ExtractReason.TornOut ? BladeFeedback.TearOut : BladeFeedback.Extract, 1f);
            Extracted?.Invoke(this, impalement, reason);
            _onExtract?.Invoke();
        }

        internal void SendFeedback(BladeFeedback kind, float intensity)
        {
            if (_wielder != null)
                _wielder.OnBladeFeedback(this, kind, intensity);
            Feedback?.Invoke(this, kind, intensity);
        }

        private void OnCollisionEnter(Collision collision) => HandleContact(collision, true);

        private void OnCollisionStay(Collision collision)
        {
            // Leaning on the handle: steady pressure pushes the tip in without a swing.
            if (_embed && _impalement == null)
                HandleContact(collision, false);
        }

        private void HandleContact(Collision collision, bool entering)
        {
            if (_impalement != null || !_body.Exists)
                return;
            int count = collision.GetContacts(s_contacts);
            if (count == 0)
                return;

            Collider other = collision.collider;
            IDamageable target = Damageables.Find(other);
            PhysicsBodyRef otherBody = PhysicsBodyRef.FromCollider(other);
            Vector3 axis = BladeAxisWorld;
            Vector3 tip = TipPosition;

            int tipIndex = -1;
            float best = _tipRadius;
            for (int i = 0; i < count; i++)
            {
                float d = Vector3.Distance(s_contacts[i].point, tip);
                if (d <= best)
                {
                    best = d;
                    tipIndex = i;
                }
            }

            ContactPoint contact = s_contacts[tipIndex >= 0 ? tipIndex : 0];
            // Pre-impact velocity: measured before the solver resolved this hit.
            Vector3 relative = _lastTipVelocity - otherBody.GetPointVelocity(contact.point);
            float along = Vector3.Dot(relative, axis);

            if (tipIndex >= 0)
            {
                StabMaterial material = ResolveMaterial(other, target);
                bool headOn = Vector3.Angle(axis, -contact.normal) <= _maxSurfaceAngle;
                bool fast = entering && along >= material.minStabSpeed && Vector3.Angle(relative, axis) <= _maxStabAngle;
                bool pressed = !entering && material.penetrable && material.minPressForce > 0f && along > -0.2f
                    && Mathf.Abs(Vector3.Dot(collision.impulse, axis)) / Time.fixedDeltaTime >= material.minPressForce;

                if (headOn && (fast || pressed))
                {
                    bool embedded = _embed && material.penetrable && Time.time >= _nextEmbedTime && CanEmbedInto(other, target)
                        && Embed(other, otherBody, target, material, contact, axis, along);

                    // Pressure only counts when it gets the blade in; a fast stab always wounds.
                    if (target != null && (fast || embedded))
                    {
                        float speed = Mathf.Max(0f, along);
                        target.ApplyDamage(new DamageInfo(_stabDamage + speed * _stabDamagePerSpeed, DamageType.Stab, contact.point,
                            axis, axis * (speed * _stabImpulsePerSpeed), AttributionSource, other));
                        _onStab?.Invoke(contact.point);
                    }
                    if (!embedded && fast)
                        SendFeedback(material.penetrable ? BladeFeedback.Stab : BladeFeedback.Glance, Mathf.Clamp01(along / 6f));
                    if (fast || embedded)
                        return;
                }
                else if (fast)
                {
                    SendFeedback(BladeFeedback.Glance, Mathf.Clamp01(along / 6f));
                }
            }

            if (!entering || target == null)
                return;

            Vector3 lateral = relative - axis * along;
            float lateralSpeed = lateral.magnitude;
            if (lateralSpeed >= _minSlashSpeed && IsBladeCollider(contact.thisCollider))
            {
                Vector3 dir = lateral / lateralSpeed;
                target.ApplyDamage(new DamageInfo(lateralSpeed * _slashDamagePerSpeed, DamageType.Slash, contact.point,
                    dir, dir * (lateralSpeed * _slashImpulsePerSpeed), AttributionSource, other));
                _onSlash?.Invoke(contact.point);
                return;
            }

            float impulse = collision.impulse.magnitude;
            if (impulse >= _minBluntImpulse)
            {
                Vector3 dir = RagdollMath.SafeNormalize(relative, -contact.normal);
                target.ApplyDamage(new DamageInfo((impulse - _minBluntImpulse) * _bluntDamagePerImpulse, DamageType.Blunt,
                    contact.point, dir, Vector3.zero, AttributionSource, other));
            }
        }

        private StabMaterial ResolveMaterial(Collider other, IDamageable target)
        {
            StabbableSurface surface = StabbableSurface.Find(other);
            if (surface != null && surface.Material != null)
                return surface.Material;
            if (target != null)
                return _fleshMaterial;
            return _worldMaterial;
        }

        private bool CanEmbedInto(Collider other, IDamageable target)
        {
            if (other.isTrigger)
                return false;
            if (StabbableSurface.Find(other) != null)
                return true;
            if (target is BodyPart)
                return true;
            if (target != null)
                return false; // the player and other non-ragdoll damageables: wound, don't stick
            if (other.gameObject.layer == LayerMask.NameToLayer(RagdollLayers.BIMOSRigLayer))
                return false;
            return _stickInWorld;
        }

        private bool Embed(Collider other, PhysicsBodyRef otherBody, IDamageable target, StabMaterial material,
            ContactPoint contact, Vector3 axis, float along)
        {
            for (int i = 0; i < _pendingRestores.Count; i++)
                Impalement.SetIgnore(_pendingRestores[i].mine, _pendingRestores[i].theirs, false);
            _pendingRestores.Clear();

            // The entry hole is where the blade line crosses the surface.
            Vector3 tip = TipPosition;
            Vector3 inward = -contact.normal;
            float cos = Mathf.Max(0.2f, Vector3.Dot(axis, inward));
            float maxDepth = material.maxDepth > 0f ? Mathf.Min(material.maxDepth, _bladeLength) : _bladeLength;
            float depth = Mathf.Clamp(Vector3.Dot(tip - contact.point, inward) / cos, 0f, maxDepth * 0.9f);
            Vector3 entry = tip - axis * depth;

            Impalement impalement = Impalement.Create(this, _body, other, target, material, entry, axis, depth, maxDepth, _bladeColliders);
            if (impalement == null)
                return false;
            _impalement = impalement;

            // Carry on into the wound with whatever speed the surface didn't eat. The solver already bounced
            // the blade off it this step.
            Vector3 carried = otherBody.GetPointVelocity(entry) + axis * (Mathf.Max(0f, along) * material.entrySpeedKept);
            _body.LinearVelocity = carried;
            _body.AngularVelocity = otherBody.AngularVelocity;

            SendFeedback(BladeFeedback.Embed, Mathf.Max(0f, along));
            Embedded?.Invoke(this, impalement);
            _onEmbed?.Invoke();
            return true;
        }

        private void ProcessPendingRestores()
        {
            for (int i = _pendingRestores.Count - 1; i >= 0; i--)
            {
                PendingRestore p = _pendingRestores[i];
                if (Time.time < p.time)
                    continue;
                // Wait until the blade is clear, or it would be shoved out of the body violently.
                if (Time.time < p.deadline && Overlapping(p.mine, p.theirs))
                {
                    p.time = Time.time + 0.1f;
                    _pendingRestores[i] = p;
                    continue;
                }
                Impalement.SetIgnore(p.mine, p.theirs, false);
                _pendingRestores.RemoveAt(i);
            }
        }

        private static bool Overlapping(Collider[] a, Collider[] b)
        {
            for (int i = 0; i < a.Length; i++)
            {
                Collider x = a[i];
                if (x == null || !x.enabled) continue;
                for (int j = 0; j < b.Length; j++)
                {
                    Collider y = b[j];
                    if (y == null || !y.enabled) continue;
                    if (Physics.ComputePenetration(x, x.transform.position, x.transform.rotation,
                        y, y.transform.position, y.transform.rotation, out _, out _))
                        return true;
                }
            }
            return false;
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

        private void OnValidate()
        {
            _fleshMaterial ??= StabMaterial.Flesh();
            _worldMaterial ??= StabMaterial.Wood();
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.red;
            Vector3 tip = TipPosition;
            Gizmos.DrawLine(tip - BladeAxisWorld * _bladeLength, tip);
            Gizmos.DrawWireSphere(tip, _tipRadius);
            if (_impalement != null)
            {
                Gizmos.color = Color.yellow;
                Gizmos.DrawWireSphere(_impalement.EntryPoint, 0.015f);
            }
        }
    }
}
