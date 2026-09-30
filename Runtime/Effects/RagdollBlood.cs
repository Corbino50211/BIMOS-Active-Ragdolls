using System.Collections.Generic;
using UnityEngine;

namespace ActiveRagdoll
{
    /// <summary>
    /// Blood for an active ragdoll, drawn by <see cref="BloodFX"/> (no assets needed).
    /// <list type="bullet">
    /// <item><b>Shot:</b> a spray back out of the entry hole, a bigger one out of the exit wound, then the entry
    /// hole gushes in time with the heartbeat for a few seconds.</item>
    /// <item><b>Stabbed:</b> a spurt on the way in; while the blade is in, blood seeps round it (more as the
    /// wound is twisted open); when it comes out, the wound gushes and keeps bleeding.</item>
    /// <item><b>Slashed:</b> a spray along the cut.</item>
    /// </list>
    /// Wounds pump while the NPC is alive and slow to a seep after death. Droplets that land leave splats.
    /// Added to every character automatically (see <see cref="AutoAdd"/>); add it yourself to change settings.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Active Ragdoll/Effects/Ragdoll Blood")]
    [RequireComponent(typeof(ActiveRagdollCharacter))]
    public sealed class RagdollBlood : MonoBehaviour
    {
        /// <summary>Give every active ragdoll blood automatically. Set false before the scene loads to opt out.</summary>
        public static bool AutoAdd = true;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Register()
        {
            AutoAdd = true;
            ActiveRagdollCharacter.Initialized -= OnCharacterInitialized;
            ActiveRagdollCharacter.Initialized += OnCharacterInitialized;
        }

        private static void OnCharacterInitialized(ActiveRagdollCharacter character)
        {
            if (AutoAdd && character != null && character.GetComponent<RagdollBlood>() == null)
                character.gameObject.AddComponent<RagdollBlood>();
        }

        [Header("Look")]
        [SerializeField] private Color _color = new Color(0.42f, 0.01f, 0.02f, 1f);
        [Tooltip("Scales every spray and gush (0 = no blood).")]
        [SerializeField, Range(0f, 3f)] private float _amount = 1f;
        [SerializeField, Range(0.005f, 0.08f)] private float _dropletSize = 0.025f;

        [Header("What bleeds")]
        [SerializeField] private bool _bullets = true;
        [SerializeField] private bool _stabs = true;
        [SerializeField] private bool _slashes = true;
        [Tooltip("Corpses bleed when shot or stabbed too.")]
        [SerializeField] private bool _corpsesBleed = true;

        [Header("Wounds")]
        [Tooltip("How long (s) a bullet or stab wound keeps bleeding.")]
        [SerializeField, Min(0f)] private float _bleedTime = 7f;
        [Tooltip("Heartbeats per second while alive (wounds pump in time).")]
        [SerializeField, Min(0.1f)] private float _heartRate = 1.4f;
        [Tooltip("Seconds after death until wounds only seep.")]
        [SerializeField, Min(0.1f)] private float _deathFade = 4f;
        [SerializeField, Range(1, 16)] private int _maxWounds = 8;

        private struct Wound
        {
            public Transform bone;
            public Vector3 localPoint;
            public Vector3 localDirection;
            public float strength;
            public float start;
            public float end;
            public float carry;
        }

        private struct TrackedBlade
        {
            public Impalement impalement;
            public Transform bone;
            public Vector3 localPoint;
            public Vector3 localOutward;
            public float carry;
        }

        private ActiveRagdollCharacter _character;
        private readonly List<Wound> _wounds = new List<Wound>();
        private readonly List<TrackedBlade> _blades = new List<TrackedBlade>();
        private float _deathTime = float.NegativeInfinity;

        public Color BloodColor { get => _color; set => _color = value; }
        public float Amount { get => _amount; set => _amount = Mathf.Max(0f, value); }

        private void Awake()
        {
            _character = GetComponent<ActiveRagdollCharacter>();
        }

        private void OnEnable()
        {
            if (_character == null)
                return;
            _character.HitReceived += OnHit;
            _character.Died += OnDied;
            _character.Revived += OnRevived;
        }

        private void OnDisable()
        {
            if (_character != null)
            {
                _character.HitReceived -= OnHit;
                _character.Died -= OnDied;
                _character.Revived -= OnRevived;
            }
            _wounds.Clear();
            _blades.Clear();
        }

        private void OnDied(ActiveRagdollCharacter character) => _deathTime = Time.time;

        private void OnRevived(ActiveRagdollCharacter character)
        {
            _deathTime = float.NegativeInfinity;
            _wounds.Clear();
        }

        // ------------------------------------------------------------------ Hits

        private void OnHit(in RagdollHit hit)
        {
            if (_amount <= 0f || !isActiveAndEnabled || (_character.IsDead && !_corpsesBleed))
                return;
            if (hit.boneIndex < 0 || hit.boneIndex >= _character.BonesInternal.Count)
                return;
            RagdollBone bone = _character.BonesInternal[hit.boneIndex];
            if (bone.body == null)
                return;

            switch (hit.damageType)
            {
                case DamageType.Bullet when _bullets:
                    Shot(bone, hit);
                    break;
                case DamageType.Stab when _stabs:
                    if (!IsImpalementTick(hit.point))
                        Stabbed(bone, hit);
                    break;
                case DamageType.Slash when _slashes:
                    Slashed(hit);
                    break;
            }
        }

        private void Shot(RagdollBone bone, in RagdollHit hit)
        {
            Vector3 dir = HitDirection(bone, hit);
            Vector3 inherit = bone.body.linearVelocity;

            // Back-spatter from the entry hole, then the exit wound, which is the big one.
            BloodFX.Spray(hit.point - dir * 0.01f, -dir, Count(12), 1.8f, 45f, _color, _dropletSize, inherit);
            if (TryFindExit(bone, hit.point, dir, out Vector3 exit))
                BloodFX.Spray(exit + dir * 0.01f, dir, Count(30), 4f, 25f, _color, _dropletSize * 1.2f, inherit);

            AddWound(bone.bodyTransform, hit.point, -dir, 1f);
        }

        private void Stabbed(RagdollBone bone, in RagdollHit hit)
        {
            Vector3 dir = HitDirection(bone, hit);
            BloodFX.Spray(hit.point - dir * 0.01f, -dir, Count(14), 1.5f, 50f, _color, _dropletSize, bone.body.linearVelocity);
            // A stab that stuck is tracked through its Impalement; one that didn't leaves an open wound.
            if (!IsImpaledNear(hit.point))
                AddWound(bone.bodyTransform, hit.point, -dir, 0.8f);
        }

        private void Slashed(in RagdollHit hit)
        {
            Vector3 along = RagdollMath.SafeNormalize(hit.Direction, Vector3.up);
            Vector3 side = Vector3.Cross(along, Vector3.up);
            for (int i = 0; i < 3; i++)
                BloodFX.Spray(hit.point + along * ((i - 1) * 0.04f), side * (i % 2 == 0 ? 1f : -1f) + along, Count(6), 2.2f, 40f, _color, _dropletSize);
        }

        private Vector3 HitDirection(RagdollBone bone, in RagdollHit hit)
        {
            Vector3 dir = hit.Direction;
            if (dir.sqrMagnitude > 0.5f)
                return dir;
            if (hit.source != null)
                dir = hit.point - hit.source.transform.position;
            if (dir.sqrMagnitude < 1e-6f)
                dir = bone.body.worldCenterOfMass - hit.point; // from the surface inward
            return RagdollMath.SafeNormalize(dir, Vector3.forward);
        }

        /// <summary>The far side of the body part along the shot.</summary>
        private static bool TryFindExit(RagdollBone bone, Vector3 entry, Vector3 dir, out Vector3 exit)
        {
            const float reach = 0.7f;
            var ray = new Ray(entry + dir * reach, -dir);
            float best = float.PositiveInfinity;
            exit = entry;
            foreach (Collider c in bone.colliders)
            {
                if (c == null || !c.enabled) continue;
                if (c.Raycast(ray, out RaycastHit hit, reach - 0.02f) && hit.distance < best)
                {
                    best = hit.distance;
                    exit = hit.point;
                }
            }
            return best < float.PositiveInfinity;
        }

        private int Count(int baseCount) => Mathf.Max(1, Mathf.RoundToInt(baseCount * _amount));

        private void AddWound(Transform bone, Vector3 point, Vector3 outward, float strength)
        {
            if (bone == null || _bleedTime <= 0f)
                return;
            if (_wounds.Count >= _maxWounds)
                _wounds.RemoveAt(0);
            _wounds.Add(new Wound
            {
                bone = bone,
                localPoint = bone.InverseTransformPoint(point),
                localDirection = bone.InverseTransformDirection(RagdollMath.SafeNormalize(outward, Vector3.up)),
                strength = strength,
                start = Time.time,
                end = Time.time + _bleedTime * Random.Range(0.8f, 1.2f),
            });
        }

        // ------------------------------------------------------------------ Stuck blades

        /// <summary>Stab "hits" raised by a blade already in the body (wound damage ticks), not a new stab.</summary>
        private bool IsImpalementTick(Vector3 point)
        {
            IReadOnlyList<Impalement> active = Impalement.Active;
            for (int i = 0; i < active.Count; i++)
            {
                Impalement imp = active[i];
                if (imp.Character == _character && imp.Age > 0.05f && (imp.EntryPoint - point).sqrMagnitude < 0.01f)
                    return true;
            }
            return false;
        }

        private bool IsImpaledNear(Vector3 point)
        {
            IReadOnlyList<Impalement> active = Impalement.Active;
            for (int i = 0; i < active.Count; i++)
                if (active[i].Character == _character && (active[i].EntryPoint - point).sqrMagnitude < 0.01f)
                    return true;
            return false;
        }

        private void TrackBlades()
        {
            IReadOnlyList<Impalement> active = Impalement.Active;
            for (int i = 0; i < active.Count; i++)
            {
                Impalement imp = active[i];
                if (imp.Character != _character || imp.BodyPart == null || IsTracked(imp))
                    continue;
                Transform bone = imp.BodyPart.transform;
                _blades.Add(new TrackedBlade
                {
                    impalement = imp,
                    bone = bone,
                    localPoint = bone.InverseTransformPoint(imp.EntryPoint),
                    localOutward = bone.InverseTransformDirection(-imp.Blade.BladeAxisWorld),
                });
            }
        }

        private bool IsTracked(Impalement imp)
        {
            for (int i = 0; i < _blades.Count; i++)
                if (_blades[i].impalement == imp)
                    return true;
            return false;
        }

        // ------------------------------------------------------------------ Bleeding

        private void Update()
        {
            if (_amount <= 0f || _character == null)
                return;
            float dt = Time.deltaTime;
            if (dt <= 0f)
                return;

            TrackBlades();
            float pump = Pump();

            // Blades in the body: seep around the blade; gush when it comes out.
            for (int i = _blades.Count - 1; i >= 0; i--)
            {
                TrackedBlade b = _blades[i];
                if (b.bone == null)
                {
                    _blades.RemoveAt(i);
                    continue;
                }
                Vector3 point = b.bone.TransformPoint(b.localPoint);
                Vector3 outward = b.bone.TransformDirection(b.localOutward);
                if (!b.impalement.IsAttached)
                {
                    BloodFX.Spray(point, outward, Count(28), 2.8f * Mathf.Max(0.4f, pump), 30f, _color, _dropletSize * 1.2f);
                    AddWound(b.bone, point, outward, 1.4f);
                    _blades.RemoveAt(i);
                    continue;
                }
                // Twisting it open lets more out.
                float rate = 10f * (0.4f + b.impalement.Looseness * 1.5f) * pump;
                b.carry = Emit(point + outward * 0.01f, outward, rate, 0.5f * pump, 70f, b.carry, dt, b.bone);
                _blades[i] = b;
            }

            float now = Time.time;
            for (int i = _wounds.Count - 1; i >= 0; i--)
            {
                Wound w = _wounds[i];
                if (w.bone == null || now >= w.end)
                {
                    _wounds.RemoveAt(i);
                    continue;
                }
                float life = 1f - (now - w.start) / Mathf.Max(0.01f, w.end - w.start);
                float flow = w.strength * life * pump;
                Vector3 point = w.bone.TransformPoint(w.localPoint);
                Vector3 outward = w.bone.TransformDirection(w.localDirection);
                w.carry = Emit(point + outward * 0.01f, outward, 22f * flow, 1.6f * flow + 0.2f, 35f, w.carry, dt, w.bone);
                _wounds[i] = w;
            }
        }

        /// <summary>Heartbeat: sharp pulses while alive, fading to a weak seep after death.</summary>
        private float Pump()
        {
            if (_character.IsDead)
                return Mathf.Lerp(0.15f, 0.8f, Mathf.Clamp01(1f - (Time.time - _deathTime) / _deathFade));
            float beat = Mathf.Sin(Time.time * _heartRate * Mathf.PI * 2f);
            return 0.25f + 1.1f * Mathf.Pow(Mathf.Max(0f, beat), 3f);
        }

        private float Emit(Vector3 point, Vector3 dir, float perSecond, float speed, float cone, float carry, float dt, Transform bone)
        {
            carry += perSecond * _amount * dt;
            int count = Mathf.FloorToInt(carry);
            if (count <= 0)
                return carry;
            Rigidbody rb = bone.GetComponent<Rigidbody>();
            BloodFX.Spray(point, dir, count, speed, cone, _color, _dropletSize * 0.8f, rb != null ? rb.linearVelocity : Vector3.zero);
            return carry - count;
        }
    }
}
