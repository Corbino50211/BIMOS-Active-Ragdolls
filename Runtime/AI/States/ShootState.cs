using UnityEngine;

namespace ActiveRagdoll
{
    /// <summary>
    /// Ranged combat with a held <see cref="NPCWeapon"/>. The arm muscles point the physical gun at the target
    /// and the NPC fires steadily at the weapon's fire rate (no bursts, no reloads). Aim assist: each shot has
    /// <see cref="NPCStateMachine.WeaponSettings.hitChance"/> of going straight at the target; the rest are near
    /// misses. It backs off when an enemy gets too close and moves to regain line of sight.
    /// </summary>
    internal sealed class ShootState : NPCState
    {
        private float _nextShot;

        public ShootState(NPCStateMachine machine) : base(machine) { }

        public override NPCStateId Id => NPCStateId.Shoot;

        public override void Enter()
        {
            Machine.ApplyProfile(Machine.Profiles.shoot, Machine.Profiles.blendTime);
            _nextShot = Time.time + Machine.Weapons.firstShotDelay;
        }

        public override void Exit()
        {
            if (Machine.Holder != null)
                Machine.Holder.StopAiming();
        }

        public override void Tick(float deltaTime)
        {
            NPCWeaponHolder holder = Machine.Holder;
            NPCPerception perception = Machine.Perception;
            CombatTarget target = perception != null ? perception.Target : null;
            if (holder == null || !holder.IsArmed || target == null || !Machine.ShouldEngage)
            {
                Machine.ResumeBehaviour();
                return;
            }

            NPCWeapon weapon = holder.Weapon;
            if (!weapon.HasAmmo)
            {
                holder.Drop();
                Machine.ResumeBehaviour();
                return;
            }

            Vector3 to = FlatTo(target.CenterPoint, out float distance);
            if (!perception.CanSeeTarget || distance > weapon.Range)
            {
                Machine.ChangeState(NPCStateId.Walk); // close in / regain line of sight
                return;
            }

            NPCStateMachine.WeaponSettings w = Machine.Weapons;
            Vector3 aimPoint = target.CenterPoint + target.Velocity * Machine.Attack.aimLead;
            holder.AimAt(aimPoint);
            LookAt(target.AimPoint);

            // Back away from anyone crowding the gun; otherwise hold position and face the target.
            Vector3 move = Vector3.zero;
            if (distance < w.minShootDistance && Machine.HasLocomotion)
                move = -(to / Mathf.Max(distance, 1e-3f)) * Machine.Locomotion.WalkSpeed * 0.6f;
            SetMovement(move, to);

            if (Time.time < _nextShot)
                return;
            if (Machine.HasBalance && Machine.Balance.State == BalanceState.Lost)
                return;
            if (holder.AimError(aimPoint) > w.maxAimError)
                return; // arm still swinging round; don't fire out of the side of the gun

            if (!weapon.NPCFireAt(Character.gameObject, PickShotPoint(target, weapon, w)))
                return;
            _nextShot = Time.time + weapon.FireInterval * Random.Range(0.9f, 1.15f);
        }

        /// <summary>A point on the target's body (a hit), or one that passes close by (a miss).</summary>
        private static Vector3 PickShotPoint(CombatTarget target, NPCWeapon weapon, NPCStateMachine.WeaponSettings w)
        {
            Vector3 point = Vector3.Lerp(target.CenterPoint, target.AimPoint, Random.Range(0f, 0.6f))
                + Random.insideUnitSphere * 0.08f;
            if (Random.value < w.hitChance)
                return point;

            Vector3 line = point - weapon.Muzzle.position;
            Vector3 side = Vector3.Cross(line, Random.onUnitSphere);
            if (side.sqrMagnitude < 1e-6f)
                side = Vector3.Cross(line, Vector3.up);
            return point + side.normalized * (w.missDistance * Random.Range(0.6f, 1.4f));
        }
    }
}
