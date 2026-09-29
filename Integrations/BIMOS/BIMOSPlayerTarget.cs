using BIMOS;
using UnityEngine;

namespace ActiveRagdoll.BIMOSIntegration
{
    /// <summary>
    /// Makes a BIMOS player perceivable and attackable by NPCs. Aims at the physics head, measures distance to
    /// the physics pelvis and reads velocity from the pelvis rigidbody, so NPCs track where the player's body
    /// actually is (not the headset). Added automatically to every <see cref="Player"/> by
    /// <see cref="BIMOSIntegrationBootstrap"/>, or add it to the BIMOS player prefab yourself.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Active Ragdoll/BIMOS/BIMOS Player Target")]
    public sealed class BIMOSPlayerTarget : CombatTarget
    {
        private Player _player;

        public Player Player => _player;

        private PhysicsRig Rig => _player != null ? _player.PhysicsRig : null;

        protected override void Awake()
        {
            base.Awake();
            ResolvePlayer();
        }

        private void ResolvePlayer()
        {
            _player = GetComponent<Player>();
            if (_player == null)
                _player = GetComponentInParent<Player>();
            if (_player == null)
                _player = GetComponentInChildren<Player>();
            if (_player == null)
                Debug.LogWarning($"{ActiveRagdollCharacter.LogPrefix} BIMOSPlayerTarget on '{name}' could not find a BIMOS Player; using transform-based fallback points.", this);
        }

        public override Vector3 AimPoint
        {
            get
            {
                PhysicsRig rig = Rig;
                return rig != null && rig.HeadRigidbody != null ? rig.HeadRigidbody.position : base.AimPoint;
            }
        }

        public override Vector3 CenterPoint
        {
            get
            {
                PhysicsRig rig = Rig;
                return rig != null && rig.PelvisRigidbody != null ? rig.PelvisRigidbody.worldCenterOfMass : base.CenterPoint;
            }
        }

        public override Vector3 Velocity
        {
            get
            {
                PhysicsRig rig = Rig;
                return rig != null && rig.PelvisRigidbody != null ? rig.PelvisRigidbody.linearVelocity : base.Velocity;
            }
        }

        public override bool Owns(Collider collider)
        {
            if (collider == null)
                return false;
            if (_player != null && collider.transform.IsChildOf(_player.transform))
                return true;
            return base.Owns(collider);
        }
    }
}
