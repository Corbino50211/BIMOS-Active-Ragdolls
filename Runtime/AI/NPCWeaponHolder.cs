using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace ActiveRagdoll
{
    /// <summary>
    /// Lets an NPC carry and use an <see cref="NPCWeapon"/> with its physical hand. The weapon is snapped into
    /// the palm and held by a FixedJoint (to a Rigidbody or an ArticulationBody, like BIMOS's demo pistol). The
    /// arm's muscles aim it, so recoil kicks the arm, hits knock the aim off, and a player who grabs the gun
    /// can yank it away (the NPC lets go after <c>Disarm Time</c>).
    /// Added automatically to NPCs whose state machine has weapons enabled.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Active Ragdoll/AI/NPC Weapon Holder")]
    [RequireComponent(typeof(ActiveRagdollCharacter))]
    public sealed class NPCWeaponHolder : MonoBehaviour
    {
        [Tooltip("Hand that holds weapons (the other hand is used if this arm is crippled).")]
        [SerializeField] private BodySide _preferredHand = BodySide.Right;

        [Tooltip("Wrist-to-palm distance (m) for a 1 m pelvis height; scales with the character.")]
        [SerializeField, Min(0f)] private float _gripDistance = 0.07f;

        [Tooltip("How far the arm extends toward the target while aiming (fraction of arm length).")]
        [SerializeField, Range(0.5f, 1f)] private float _aimReach = 0.88f;

        [Tooltip("Arm muscle multiplier while aiming (steadier aim, slower recoil recovery if lower).")]
        [SerializeField, Range(1f, 4f)] private float _aimMuscleBoost = 1.8f;

        [Tooltip("Hand pin weight while aiming (pulls the gun hand onto the aim line).")]
        [SerializeField, Range(0f, 1f)] private float _aimPinWeight = 0.3f;

        [SerializeField] private bool _dropOnDeath = true;

        [Tooltip("Chance to drop the weapon when knocked down.")]
        [SerializeField, Range(0f, 1f)] private float _dropOnKnockdownChance = 0.35f;

        [Tooltip("Seconds someone else (a BIMOS hand) must hold the weapon before the NPC lets go.")]
        [SerializeField, Min(0f)] private float _disarmTime = 0.3f;

        [SerializeField] private UnityEvent _onPickedUp = new UnityEvent();
        [SerializeField] private UnityEvent _onDropped = new UnityEvent();

        private ActiveRagdollCharacter _character;
        private bool _initialized;
        private NPCWeapon _weapon;
        private FixedJoint _joint;
        private BodySide _hand = BodySide.Right;
        private int _handBone = -1;
        private Vector3 _muzzleInHand = Vector3.forward;
        private Vector3 _upInHand = Vector3.up;
        private readonly Dictionary<BodySide, Vector3> _handDirLocal = new Dictionary<BodySide, Vector3>();
        private readonly Dictionary<BodySide, Vector3> _thumbLocal = new Dictionary<BodySide, Vector3>();
        private float _armLength = 0.55f;
        private float _shoulderHeight = 1.45f;
        private float _scale = 1f;

        private enum ArmMode { None, Aim, Reach }
        private ArmMode _armMode;
        private Vector3 _armPoint;
        private BodySide _boostedSide = BodySide.Center;
        private float _externalHold;

        private readonly List<Collider> _weaponColliders = new List<Collider>();
        private readonly List<Collider> _pendingRestore = new List<Collider>();
        private float _restoreTime;
        private readonly Dictionary<NPCWeapon, float> _ignoredUntil = new Dictionary<NPCWeapon, float>();

        public NPCWeapon Weapon => _weapon;
        public bool IsArmed => _weapon != null;
        public BodySide Hand => _hand;
        public float ArmLength { get { EnsureInitialized(); return _armLength; } }

        /// <summary>Standing shoulder height above the floor.</summary>
        public float ShoulderHeight { get { EnsureInitialized(); return _shoulderHeight; } }
        public float DropOnKnockdownChance => _dropOnKnockdownChance;

        // ------------------------------------------------------------------ Setup

        private void Awake()
        {
            _character = GetComponent<ActiveRagdollCharacter>();
        }

        private bool EnsureInitialized()
        {
            if (_initialized)
                return true;
            if (_character == null || !_character.IsValid)
                return false;

            var bones = _character.BonesInternal;
            _scale = Mathf.Max(0.2f, _character.StandingPelvisHeight);
            foreach (BodySide side in new[] { BodySide.Left, BodySide.Right })
            {
                int lower = _character.GetBoneIndex(BoneRoles.LowerArm(side));
                int hand = _character.GetBoneIndex(BoneRoles.Hand(side));
                int upper = _character.GetBoneIndex(BoneRoles.UpperArm(side));
                if (lower < 0 || upper < 0)
                    continue;
                int grip = hand >= 0 ? hand : lower;
                RagdollBone g = bones[grip];
                Vector3 forearm = bones[hand >= 0 ? hand : lower].restPosition - bones[hand >= 0 ? lower : upper].restPosition;
                Quaternion invRest = Quaternion.Inverse(g.restRotation);
                Vector3 dir = RagdollMath.SafeNormalize(invRest * forearm, Vector3.forward);
                Vector3 thumb = invRest * Vector3.forward; // thumbs point forward in T/A bind poses
                thumb = RagdollMath.SafeNormalize(thumb - dir * Vector3.Dot(thumb, dir), Vector3.Cross(dir, Vector3.right));
                _handDirLocal[side] = dir;
                _thumbLocal[side] = thumb;

                if (side == _preferredHand || _armLength <= 0.56f)
                {
                    Vector3 shoulder = bones[upper].restPosition;
                    _armLength = (bones[lower].restPosition - shoulder).magnitude
                        + ((hand >= 0 ? bones[hand].restPosition : bones[lower].restPosition + forearm) - bones[lower].restPosition).magnitude;
                    _shoulderHeight = _character.StandingPelvisHeight + shoulder.y;
                }
            }

            _character.Died += OnDied;
            _initialized = _handDirLocal.Count > 0;
            return _initialized;
        }

        private void OnDestroy()
        {
            if (_character != null)
                _character.Died -= OnDied;
        }

        private void OnDied(ActiveRagdollCharacter character)
        {
            StopAiming();
            if (_dropOnDeath)
                Drop();
        }

        private void OnDisable()
        {
            Drop();
            RestoreCollisionsNow();
        }

        // ------------------------------------------------------------------ Queries

        public bool CanUseHand(BodySide side)
        {
            if (!EnsureInitialized() || !_handDirLocal.ContainsKey(side))
                return false;
            JointMotorDriver motors = _character.Motors;
            int lower = _character.GetBoneIndex(BoneRoles.LowerArm(side));
            return motors == null || !motors.IsInitialized || motors.GetBoneFunction(lower) > 0.5f;
        }

        public bool CanHoldWeapons => CanUseHand(_preferredHand) || CanUseHand(BoneRoles.Opposite(_preferredHand));

        /// <summary>Nearest available, reachable weapon within <paramref name="radius"/> (m), or null.</summary>
        public NPCWeapon FindWeapon(float radius)
        {
            if (!EnsureInitialized() || !CanHoldWeapons || _character.IsDead)
                return null;

            NPCWeapon.RequestDiscovery();
            Vector3 from = _character.Position;
            float floor = FloorHeight();
            float bestSqr = radius * radius;
            NPCWeapon best = null;
            var all = NPCWeapon.All;
            for (int i = 0; i < all.Count; i++)
            {
                NPCWeapon w = all[i];
                if (w == null || !w.IsAvailable || !w.HasAmmo)
                    continue;
                if (_ignoredUntil.TryGetValue(w, out float until) && Time.time < until)
                    continue;
                Vector3 grip = w.GripPosition;
                float height = grip.y - floor;
                if (height < -0.4f || height > _shoulderHeight + _armLength * 0.6f)
                    continue; // below the floor we stand on, or out of reach overhead
                float sqr = RagdollMath.Flatten(grip - from).sqrMagnitude;
                if (sqr < bestSqr)
                {
                    bestSqr = sqr;
                    best = w;
                }
            }
            return best;
        }

        public void IgnoreWeapon(NPCWeapon weapon, float seconds)
        {
            if (weapon != null)
                _ignoredUntil[weapon] = Time.time + seconds;
        }

        public Vector3 PalmPosition(BodySide side)
        {
            if (!EnsureInitialized() || !_handDirLocal.ContainsKey(side))
                return _character != null ? _character.Position : transform.position;
            Rigidbody hand = GripBody(side, out _);
            return hand.position + hand.rotation * _handDirLocal[side] * (_gripDistance * _scale);
        }

        /// <summary>Distance from the free hand's palm to a point.</summary>
        public float HandDistanceTo(Vector3 point) => Vector3.Distance(PalmPosition(ChooseHand()), point);

        /// <summary>Angle (degrees) between the muzzle's line of fire and <paramref name="point"/>.</summary>
        public float AimError(Vector3 point)
        {
            if (_weapon == null)
                return 180f;
            Transform muzzle = _weapon.Muzzle;
            return Vector3.Angle(muzzle.forward, point - muzzle.position);
        }

        public float FloorHeight()
        {
            BalanceController balance = _character.Balance;
            if (balance != null && balance.IsInitialized && balance.HasGround)
                return balance.GroundHeight;
            return _character.Position.y - _character.StandingPelvisHeight;
        }

        private BodySide ChooseHand()
        {
            if (_weapon != null)
                return _hand;
            return CanUseHand(_preferredHand) ? _preferredHand : BoneRoles.Opposite(_preferredHand);
        }

        private Rigidbody GripBody(BodySide side, out int boneIndex)
        {
            boneIndex = _character.GetBoneIndex(BoneRoles.Hand(side));
            if (boneIndex < 0)
                boneIndex = _character.GetBoneIndex(BoneRoles.LowerArm(side));
            return boneIndex >= 0 ? _character.BonesInternal[boneIndex].body : null;
        }

        // ------------------------------------------------------------------ Pick up / drop

        /// <summary>Snaps <paramref name="weapon"/> into the free hand and joints it there.</summary>
        public bool PickUp(NPCWeapon weapon)
        {
            if (weapon == null || !weapon.IsAvailable || !EnsureInitialized() || _character.IsDead)
                return false;
            if (_weapon != null)
                Drop();

            BodySide side = ChooseHand();
            if (!CanUseHand(side))
                return false;
            Rigidbody hand = GripBody(side, out int handBone);
            if (hand == null)
                return false;

            // Orient the weapon: barrel along the hand, top of the gun toward the thumb.
            Vector3 handDir = hand.rotation * _handDirLocal[side];
            Vector3 thumb = hand.rotation * _thumbLocal[side];
            Transform body = weapon.BodyTransform;
            Transform muzzle = weapon.Muzzle;
            Quaternion muzzleInBody = Quaternion.Inverse(body.rotation) * muzzle.rotation;
            Quaternion bodyRotation = Quaternion.LookRotation(handDir, thumb) * Quaternion.Inverse(muzzleInBody);
            Vector3 gripOffset = Quaternion.Inverse(body.rotation) * (weapon.GripPosition - body.position);
            Vector3 bodyPosition = PalmPosition(side) - bodyRotation * gripOffset;
            TeleportWeapon(weapon, bodyPosition, bodyRotation);

            // Don't let the gun fight the body holding it.
            _weaponColliders.Clear();
            foreach (Collider c in weapon.RootTransform.GetComponentsInChildren<Collider>(true))
                if (!c.isTrigger)
                    _weaponColliders.Add(c);
            _pendingRestore.RemoveAll(c => _weaponColliders.Contains(c));
            SetIgnoreWithBody(_weaponColliders, true);

            _joint = hand.gameObject.AddComponent<FixedJoint>();
            if (weapon.Articulation != null)
                _joint.connectedArticulationBody = weapon.Articulation;
            else
                _joint.connectedBody = weapon.Rigidbody;
            _joint.enableCollision = false;

            _weapon = weapon;
            _hand = side;
            _handBone = handBone;
            weapon.Holder = this;
            _muzzleInHand = Quaternion.Inverse(hand.rotation) * muzzle.forward;
            _upInHand = Quaternion.Inverse(hand.rotation) * muzzle.up;
            _externalHold = 0f;
            _onPickedUp?.Invoke();
            return true;
        }

        /// <summary>Lets go of the weapon (collisions with it come back shortly after).</summary>
        public void Drop()
        {
            StopAiming();
            if (_joint != null)
                Destroy(_joint);
            _joint = null;

            if (_weapon == null)
                return;

            NPCWeapon dropped = _weapon;
            _weapon = null;
            if (dropped != null)
                dropped.Holder = null;

            _pendingRestore.AddRange(_weaponColliders);
            _weaponColliders.Clear();
            _restoreTime = Time.time + 0.5f;
            _onDropped?.Invoke();
        }

        private static void TeleportWeapon(NPCWeapon weapon, Vector3 bodyPosition, Quaternion bodyRotation)
        {
            if (weapon.Articulation != null)
            {
                ArticulationBody root = weapon.ArticulationRoot != null ? weapon.ArticulationRoot : weapon.Articulation;
                Transform link = weapon.Articulation.transform;
                Quaternion linkInRoot = Quaternion.Inverse(root.transform.rotation) * link.rotation;
                Vector3 linkPosInRoot = Quaternion.Inverse(root.transform.rotation) * (link.position - root.transform.position);
                Quaternion rootRotation = bodyRotation * Quaternion.Inverse(linkInRoot);
                Vector3 rootPosition = bodyPosition - rootRotation * linkPosInRoot;
                root.TeleportRoot(rootPosition, rootRotation);
                root.linearVelocity = Vector3.zero;
                root.angularVelocity = Vector3.zero;
            }
            else if (weapon.Rigidbody != null)
            {
                Rigidbody rb = weapon.Rigidbody;
                rb.position = bodyPosition;
                rb.rotation = bodyRotation;
                rb.transform.SetPositionAndRotation(bodyPosition, bodyRotation);
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
        }

        private void SetIgnoreWithBody(List<Collider> weaponColliders, bool ignore)
        {
            var bones = _character.BonesInternal;
            for (int b = 0; b < bones.Count; b++)
            {
                Collider[] own = bones[b].colliders;
                for (int i = 0; i < own.Length; i++)
                {
                    if (own[i] == null) continue;
                    for (int w = 0; w < weaponColliders.Count; w++)
                        if (weaponColliders[w] != null)
                            Physics.IgnoreCollision(own[i], weaponColliders[w], ignore);
                }
            }
        }

        private void RestoreCollisionsNow()
        {
            if (_pendingRestore.Count == 0 || _character == null || !_character.IsValid)
                return;
            SetIgnoreWithBody(_pendingRestore, false);
            _pendingRestore.Clear();
        }

        // ------------------------------------------------------------------ Aiming

        /// <summary>Points the held weapon at <paramref name="point"/> (call every frame while aiming).</summary>
        public void AimAt(Vector3 point)
        {
            if (_weapon == null || !RagdollMath.IsFinite(point))
                return;
            _armMode = ArmMode.Aim;
            _armPoint = point;
        }

        /// <summary>Reaches the free hand toward <paramref name="point"/> (picking something up).</summary>
        public void ReachFor(Vector3 point)
        {
            if (!RagdollMath.IsFinite(point))
                return;
            _armMode = ArmMode.Reach;
            _armPoint = point;
        }

        public void StopAiming()
        {
            _armMode = ArmMode.None;
            ProceduralAnimator procedural = _character != null ? _character.Procedural : null;
            if (procedural != null)
            {
                procedural.ClearArmAim(BodySide.Left);
                procedural.ClearArmAim(BodySide.Right);
            }
            SetArmBoost(BodySide.Center);
        }

        private void Update()
        {
            if (!EnsureInitialized())
                return;

            if (_pendingRestore.Count > 0 && Time.time >= _restoreTime)
                RestoreCollisionsNow();

            if (_weapon != null)
            {
                // Weapon destroyed / disabled / joint broken / taken by a player.
                if (_weapon == null || !_weapon.isActiveAndEnabled || _joint == null)
                {
                    Drop();
                }
                else if (_weapon.IsHeldExternally)
                {
                    _externalHold += Time.deltaTime;
                    if (_externalHold >= _disarmTime)
                        Drop();
                }
                else
                {
                    _externalHold = 0f;
                }
            }

            ProceduralAnimator procedural = _character.Procedural;
            if (procedural == null || !procedural.IsInitialized || _character.IsDead)
                return;

            if (_armMode == ArmMode.None || (_armMode == ArmMode.Aim && _weapon == null))
            {
                procedural.ClearArmAim(BodySide.Left);
                procedural.ClearArmAim(BodySide.Right);
                SetArmBoost(BodySide.Center);
                return;
            }

            BodySide side = ChooseHand();
            if (!procedural.TryGetShoulder(side, out Vector3 shoulder, out float length))
                return;

            if (_armMode == ArmMode.Reach)
            {
                Vector3 toPoint = _armPoint - shoulder;
                Vector3 wrist = shoulder + Vector3.ClampMagnitude(toPoint, length * 0.98f);
                procedural.SetArmAim(side, wrist, Quaternion.identity, false);
                SetArmBoost(BodySide.Center);
                return;
            }

            // Aim: extend the arm along the line to the target and orient the hand so the barrel lines up,
            // keeping the top of the gun up.
            Vector3 dir = RagdollMath.SafeNormalize(_armPoint - shoulder, _character.HeadingRotation * Vector3.forward);
            Vector3 wristTarget = shoulder + dir * (length * _aimReach);
            Transform muzzle = _weapon.Muzzle;
            Vector3 fireDir = RagdollMath.SafeNormalize(_armPoint - muzzle.position, dir);
            Vector3 up = Vector3.up - fireDir * Vector3.Dot(Vector3.up, fireDir);
            Quaternion handRotation = Quaternion.LookRotation(fireDir, RagdollMath.SafeNormalize(up, Vector3.up))
                * Quaternion.Inverse(Quaternion.LookRotation(_muzzleInHand, _upInHand));
            procedural.SetArmAim(side, wristTarget, handRotation, true);
            SetArmBoost(side);
        }

        private void SetArmBoost(BodySide side)
        {
            JointMotorDriver motors = _character != null ? _character.Motors : null;
            if (motors == null || !motors.IsInitialized || side == _boostedSide)
                return;

            if (_boostedSide != BodySide.Center)
                ApplyBoost(motors, _boostedSide, 1f, 0f);
            if (side != BodySide.Center)
                ApplyBoost(motors, side, _aimMuscleBoost, _aimPinWeight);
            _boostedSide = side;
        }

        private void ApplyBoost(JointMotorDriver motors, BodySide side, float boost, float pin)
        {
            int upper = _character.GetBoneIndex(BoneRoles.UpperArm(side));
            int lower = _character.GetBoneIndex(BoneRoles.LowerArm(side));
            int hand = _character.GetBoneIndex(BoneRoles.Hand(side));
            motors.SetBoost(upper, boost);
            motors.SetBoost(lower, boost);
            motors.SetBoost(hand, boost);
            motors.SetPinWeight(hand >= 0 ? hand : lower, pin);
        }

        private void OnDrawGizmosSelected()
        {
            if (_weapon == null)
                return;
            Transform muzzle = _weapon.Muzzle;
            Gizmos.color = Color.red;
            Gizmos.DrawRay(muzzle.position, muzzle.forward * 2f);
            if (_armMode == ArmMode.Aim)
            {
                Gizmos.color = Color.yellow;
                Gizmos.DrawLine(muzzle.position, _armPoint);
            }
        }
    }
}
