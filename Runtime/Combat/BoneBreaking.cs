using System;
using UnityEngine;
using UnityEngine.Events;

namespace ActiveRagdoll
{
    /// <summary>
    /// Bones that snap. A limb breaks when it takes a big enough blow (a hard hit, being thrown into a wall,
    /// a bad landing), sometimes when shot, or when it's forced well past its joint limit (wrenched by a BIMOS
    /// hand, kicked sideways at the knee).
    /// <para>
    /// A broken bone goes floppy: that joint's muscle gives out and its limits open up, so the limb dangles and
    /// bends the wrong way. A broken leg can't hold the NPC up, a broken arm can't punch or hold a gun, and a
    /// broken neck lets the head loll (and kills, if set). Accompanied by a crack sound and a flinch. Everything
    /// heals on <see cref="ActiveRagdollCharacter.Revive"/>. Added to every character automatically
    /// (see <see cref="AutoAdd"/>); add it yourself to change settings.
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Active Ragdoll/Combat/Bone Breaking")]
    [RequireComponent(typeof(ActiveRagdollCharacter))]
    public sealed class BoneBreaking : MonoBehaviour
    {
        /// <summary>Give every active ragdoll breakable bones automatically. Set false before the scene loads to opt out.</summary>
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
            if (AutoAdd && character != null && character.GetComponent<BoneBreaking>() == null)
                character.gameObject.AddComponent<BoneBreaking>();
        }

        [Header("Which bones")]
        [SerializeField] private bool _arms = true;
        [SerializeField] private bool _legs = true;
        [SerializeField] private bool _neck = true;
        [Tooltip("A broken neck kills the NPC.")]
        [SerializeField] private bool _brokenNeckKills = true;
        [Tooltip("Corpses' bones can break too.")]
        [SerializeField] private bool _corpses = true;

        [Header("What breaks them")]
        [Tooltip("A blow that changes the bone's speed by this much (m/s) snaps it. A hard punch is ~4, being thrown into a wall ~15.")]
        [SerializeField, Min(1f)] private float _impactBreakSpeed = 11f;
        [Tooltip("Neck: multiplier on the impact threshold (it's tougher than a forearm in game terms).")]
        [SerializeField, Min(0.1f)] private float _neckToughness = 1.6f;
        [Tooltip("Chance a bullet hitting an arm or leg breaks it.")]
        [SerializeField, Range(0f, 1f)] private float _bulletBreakChance = 0.15f;
        [Tooltip("Forcing a joint this many degrees past its limit snaps it (wrenching, sideways knee kicks).")]
        [SerializeField, Range(3f, 60f)] private float _overextension = 12f;

        [Header("Broken")]
        [Tooltip("Muscle left in a broken joint (0 = completely floppy).")]
        [SerializeField, Range(0f, 1f)] private float _brokenStrength = 0f;
        [Tooltip("Extra degrees a broken joint's limits open by, so it bends the wrong way.")]
        [SerializeField, Range(0f, 120f)] private float _extraLimit = 60f;
        [Tooltip("How much a broken leg still supports (0 = none; the NPC goes down).")]
        [SerializeField, Range(0f, 1f)] private float _brokenLegSupport = 0.1f;

        [Header("Feedback")]
        [Tooltip("Crack sounds (optional). Empty = a generated crack.")]
        [SerializeField] private AudioClip[] _snapSounds = Array.Empty<AudioClip>();
        [SerializeField, Range(0f, 1f)] private float _volume = 0.9f;
        [SerializeField] private UnityEvent<BoneRole> _onBoneBroken = new UnityEvent<BoneRole>();

        private struct Limits
        {
            public ConfigurableJointMotion x, y, z;
            public SoftJointLimit lowX, highX, swingY, swingZ;
        }

        private static AudioClip s_crack;

        private ActiveRagdollCharacter _character;
        private bool[] _breakable = Array.Empty<bool>();
        private bool[] _broken = Array.Empty<bool>();
        private int[] _strain = Array.Empty<int>();
        private Limits[] _limits = Array.Empty<Limits>();
        private float _armedTime;
        private bool _ready;

        /// <summary>Raised when a bone snaps.</summary>
        public event Action<BoneRole> BoneBroken;

        public bool IsBroken(BoneRole role)
        {
            int i = _character != null ? _character.GetBoneIndex(role) : -1;
            return i >= 0 && i < _broken.Length && _broken[i];
        }

        private void Start()
        {
            _character = GetComponent<ActiveRagdollCharacter>();
            if (_character == null || !_character.IsValid)
            {
                enabled = false;
                return;
            }

            var bones = _character.BonesInternal;
            int n = bones.Count;
            _breakable = new bool[n];
            _broken = new bool[n];
            _strain = new int[n];
            _limits = new Limits[n];
            for (int i = 0; i < n; i++)
            {
                RagdollBone b = bones[i];
                if (b.joint == null || b.parentIndex < 0)
                    continue;
                BoneRole r = b.role;
                bool arm = r == BoneRole.LeftUpperArm || r == BoneRole.LeftLowerArm || r == BoneRole.RightUpperArm || r == BoneRole.RightLowerArm;
                bool leg = r == BoneRole.LeftUpperLeg || r == BoneRole.LeftLowerLeg || r == BoneRole.RightUpperLeg || r == BoneRole.RightLowerLeg;
                _breakable[i] = (arm && _arms) || (leg && _legs) || (r == BoneRole.Head && _neck);
                ConfigurableJoint j = b.joint;
                _limits[i] = new Limits
                {
                    x = j.angularXMotion, y = j.angularYMotion, z = j.angularZMotion,
                    lowX = j.lowAngularXLimit, highX = j.highAngularXLimit, swingY = j.angularYLimit, swingZ = j.angularZLimit,
                };
            }

            _character.HitReceived += OnHit;
            _character.Revived += OnRevived;
            _armedTime = Time.time + 1f;
            _ready = true;
        }

        private void OnDestroy()
        {
            if (_character == null)
                return;
            _character.HitReceived -= OnHit;
            _character.Revived -= OnRevived;
        }

        private void OnEnable() => _armedTime = Time.time + 1f; // settle after spawning/pooling

        // ------------------------------------------------------------------ Triggers

        private void OnHit(in RagdollHit hit)
        {
            if (!CanBreak(hit.boneIndex))
                return;
            RagdollBone b = _character.BonesInternal[hit.boneIndex];

            if (hit.damageType == DamageType.Bullet)
            {
                if (b.role != BoneRole.Head && UnityEngine.Random.value < _bulletBreakChance)
                    Break(hit.boneIndex);
                return;
            }

            // Speed change the blow gave the bone (its own mass, plus a share of what hangs off it).
            float mass = Mathf.Max(0.1f, b.body.mass);
            float threshold = _impactBreakSpeed * (b.role == BoneRole.Head ? _neckToughness : 1f);
            if (hit.impulse.magnitude / mass >= threshold)
                Break(hit.boneIndex);
        }

        private void FixedUpdate()
        {
            if (!_ready || Time.time < _armedTime)
                return;
            var bones = _character.BonesInternal;
            for (int i = 0; i < bones.Count; i++)
            {
                if (!CanBreak(i))
                    continue;
                if (Overextension(bones, i) > _overextension)
                {
                    // A few steps in a row, so one bad solver step on a hard landing doesn't count.
                    if (++_strain[i] >= 3)
                        Break(i);
                }
                else
                {
                    _strain[i] = 0;
                }
            }
        }

        private bool CanBreak(int i)
        {
            return _ready && i >= 0 && i < _breakable.Length && _breakable[i] && !_broken[i]
                && Time.time >= _armedTime && (_corpses || !_character.IsDead);
        }

        /// <summary>Degrees the joint is currently past its twist or swing limit (0 when within limits).</summary>
        private float Overextension(System.Collections.Generic.List<RagdollBone> bones, int i)
        {
            RagdollBone b = bones[i];
            RagdollBone parent = bones[b.parentIndex];
            if (b.body == null || parent.body == null)
                return 0f;

            Quaternion relative = Quaternion.Inverse(parent.body.rotation) * b.body.rotation;
            Quaternion q = b.jointSpaceInverse * Quaternion.Inverse(relative) * b.initialRelativeRotation * b.jointSpace;
            if (q.w < 0f)
                q = new Quaternion(-q.x, -q.y, -q.z, -q.w);

            // Twist about the joint's X axis, and swing (everything else).
            float twistMag = Mathf.Sqrt(q.x * q.x + q.w * q.w);
            Quaternion twist = twistMag > 1e-5f ? new Quaternion(q.x / twistMag, 0f, 0f, q.w / twistMag) : Quaternion.identity;
            Quaternion swing = q * Quaternion.Inverse(twist);
            float twistAngle = 2f * Mathf.Atan2(Mathf.Abs(twist.x), Mathf.Abs(twist.w)) * Mathf.Rad2Deg;
            float swingAngle = 2f * Mathf.Acos(Mathf.Clamp01(Mathf.Abs(swing.w))) * Mathf.Rad2Deg;

            Limits l = _limits[i];
            float over = 0f;
            if (l.x != ConfigurableJointMotion.Free)
            {
                float allowed = l.x == ConfigurableJointMotion.Locked ? 0f : Mathf.Max(Mathf.Abs(l.lowX.limit), Mathf.Abs(l.highX.limit));
                over = Mathf.Max(over, twistAngle - allowed);
            }
            if (l.y != ConfigurableJointMotion.Free && l.z != ConfigurableJointMotion.Free)
            {
                float y = l.y == ConfigurableJointMotion.Locked ? 0f : l.swingY.limit;
                float z = l.z == ConfigurableJointMotion.Locked ? 0f : l.swingZ.limit;
                over = Mathf.Max(over, swingAngle - Mathf.Max(y, z));
            }
            return over;
        }

        // ------------------------------------------------------------------ Breaking

        /// <summary>Snaps a bone (also callable from scripts).</summary>
        public void Break(BoneRole role) => Break(_character != null ? _character.GetBoneIndex(role) : -1);

        private void Break(int i)
        {
            if (!_ready || i < 0 || i >= _broken.Length || _broken[i] || !_breakable[i])
                return;
            _broken[i] = true;
            RagdollBone b = _character.BonesInternal[i];
            BoneRole role = b.role;

            // Floppy: the joint's muscle gives out and its limits open up.
            ConfigurableJoint j = b.joint;
            Limits l = _limits[i];
            j.angularXMotion = ConfigurableJointMotion.Limited;
            j.lowAngularXLimit = new SoftJointLimit { limit = Mathf.Max(-177f, Mathf.Min(l.lowX.limit, 0f) - _extraLimit) };
            j.highAngularXLimit = new SoftJointLimit { limit = Mathf.Min(177f, Mathf.Max(l.highX.limit, 0f) + _extraLimit) };
            if (l.y != ConfigurableJointMotion.Free)
            {
                j.angularYMotion = ConfigurableJointMotion.Limited;
                j.angularYLimit = new SoftJointLimit { limit = Mathf.Min(177f, (l.y == ConfigurableJointMotion.Locked ? 0f : l.swingY.limit) + _extraLimit * 0.75f) };
            }
            if (l.z != ConfigurableJointMotion.Free)
            {
                j.angularZMotion = ConfigurableJointMotion.Limited;
                j.angularZLimit = new SoftJointLimit { limit = Mathf.Min(177f, (l.z == ConfigurableJointMotion.Locked ? 0f : l.swingZ.limit) + _extraLimit * 0.75f) };
            }

            JointMotorDriver motors = _character.Motors;
            if (motors != null && motors.IsInitialized)
            {
                motors.SetBoneFunction(i, Mathf.Min(motors.GetBoneFunction(i), _brokenStrength));
                motors.AddPain(i, 1f);
            }

            if (BoneRoles.IsLeg(role))
            {
                BalanceController balance = _character.Balance;
                if (balance != null && balance.IsInitialized)
                    balance.SetLegFunction(BoneRoles.GetSide(role), _brokenLegSupport);
            }
            else if (BoneRoles.IsArm(role))
            {
                NPCWeaponHolder holder = GetComponent<NPCWeaponHolder>();
                if (holder != null && holder.IsArmed && holder.Hand == BoneRoles.GetSide(role))
                    holder.Drop();
            }

            PlayCrack(b.body.worldCenterOfMass);
            b.body.WakeUp();

            if (!_character.IsDead)
            {
                // A flinch: the NPC reacts to the snap like a hard hit.
                _character.RegisterHit(new RagdollHit(i, role, b.body.worldCenterOfMass, Vector3.zero, 0f,
                    DamageType.Blunt, null, 0.6f));
                if (role == BoneRole.Head && _brokenNeckKills)
                    _character.Kill();
            }

            BoneBroken?.Invoke(role);
            _onBoneBroken?.Invoke(role);
        }

        private void OnRevived(ActiveRagdollCharacter character)
        {
            var bones = _character.BonesInternal;
            for (int i = 0; i < _broken.Length; i++)
            {
                _strain[i] = 0;
                if (!_broken[i])
                    continue;
                _broken[i] = false;
                ConfigurableJoint j = bones[i].joint;
                if (j == null)
                    continue;
                Limits l = _limits[i];
                j.angularXMotion = l.x;
                j.angularYMotion = l.y;
                j.angularZMotion = l.z;
                j.lowAngularXLimit = l.lowX;
                j.highAngularXLimit = l.highX;
                j.angularYLimit = l.swingY;
                j.angularZLimit = l.swingZ;
            }
            _armedTime = Time.time + 1f;
        }

        // ------------------------------------------------------------------ Sound

        private void PlayCrack(Vector3 position)
        {
            if (_volume <= 0f)
                return;
            AudioClip clip = _snapSounds != null && _snapSounds.Length > 0 ? _snapSounds[UnityEngine.Random.Range(0, _snapSounds.Length)] : null;
            if (clip == null)
                clip = s_crack != null ? s_crack : (s_crack = BuildCrack());
            AudioSource.PlayClipAtPoint(clip, position, _volume);
        }

        /// <summary>A short, sharp double crack over a dull thump.</summary>
        private static AudioClip BuildCrack()
        {
            const int rate = 44100;
            int length = rate * 18 / 100;
            var data = new float[length];
            var random = new System.Random(1234);
            for (int s = 0; s < length; s++)
            {
                float t = s / (float)rate;
                float noise = (float)(random.NextDouble() * 2.0 - 1.0);
                float crack = noise * Mathf.Exp(-t / 0.005f);
                float t2 = t - 0.018f;
                if (t2 > 0f)
                    crack += 0.6f * noise * Mathf.Exp(-t2 / 0.004f);
                float thump = 0.5f * Mathf.Sin(2f * Mathf.PI * 95f * t) * Mathf.Exp(-t / 0.035f);
                data[s] = Mathf.Clamp(crack + thump, -1f, 1f) * 0.9f;
            }
            AudioClip clip = AudioClip.Create("Bone Crack", length, 1, rate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
