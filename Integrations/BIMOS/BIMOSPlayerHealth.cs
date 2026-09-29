using BIMOS;
using UnityEngine;

namespace ActiveRagdoll.BIMOSIntegration
{
    /// <summary>
    /// Player health for a BIMOS rig. Punches that land on the player's physics hands count as blocked, and
    /// head hits hurt more. Anything held in the hands (a gun, a knife) blocks by itself, because held
    /// objects are not part of the player hierarchy and so never route damage here.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Active Ragdoll/BIMOS/BIMOS Player Health")]
    public sealed class BIMOSPlayerHealth : PlayerHealth
    {
        [Tooltip("Damage multiplier for hits landing on the physics hands (blocking).")]
        [SerializeField, Range(0f, 1f)] private float _handBlockMultiplier = 0.2f;

        [Tooltip("Damage multiplier for hits landing on the physics head.")]
        [SerializeField, Min(0f)] private float _headMultiplier = 1.5f;

        private Player _player;

        protected override void Awake()
        {
            base.Awake();
            _player = GetComponent<Player>();
            if (_player == null)
                _player = GetComponentInParent<Player>();
        }

        protected override float ModifyDamage(in DamageInfo damage)
        {
            PhysicsRig rig = _player != null ? _player.PhysicsRig : null;
            Rigidbody hit = damage.hitCollider != null ? damage.hitCollider.attachedRigidbody : null;
            if (rig == null || hit == null)
                return damage.amount;

            if (hit == rig.LeftHandRigidbody || hit == rig.RightHandRigidbody)
                return damage.amount * _handBlockMultiplier;
            if (hit == rig.HeadRigidbody)
                return damage.amount * _headMultiplier;
            return damage.amount;
        }
    }
}
