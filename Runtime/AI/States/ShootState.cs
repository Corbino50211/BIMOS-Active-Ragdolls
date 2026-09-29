using UnityEngine;

namespace ActiveRagdoll
{
    /// <summary>
    /// Ranged combat with a held <see cref="NPCWeapon"/>. The arm muscles aim the physical gun at the target,
    /// and the NPC fires in bursts only when the real muzzle is on target. Recoil, hits and shoves all spoil the
    /// aim. It backs off when an enemy gets too close, moves to regain line of sight, and drops empty guns.
    /// </summary>
    internal sealed class ShootState : NPCState
    {
        private int _burstLeft;
        private float _nextShot;
        private float _aimStart;

        public ShootState(NPCStateMachine machine) : base(machine) { }

        public override NPCStateId Id => NPCStateId.Shoot;

        public override void Enter()
        {
            Machine.ApplyProfile(Machine.Profiles.shoot, Machine.Profiles.blendTime);
            _burstLeft = NewBurst();
            _nextShot = Time.time + Machine.Weapons.firstShotDelay;
            _aimStart = Time.time;
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

            Vector3 aimPoint = target.CenterPoint + target.Velocity * Machine.Attack.aimLead;
            holder.AimAt(aimPoint);
            LookAt(target.AimPoint);

            // Back away from anyone crowding the gun; otherwise hold position and face the target.
            Vector3 move = Vector3.zero;
            if (distance < Machine.Weapons.minShootDistance && Machine.HasLocomotion)
                move = -(to / Mathf.Max(distance, 1e-3f)) * Machine.Locomotion.WalkSpeed * 0.6f;
            SetMovement(move, to);

            if (Time.time < _nextShot || !Machine.HasBalance || Machine.Balance.State != BalanceState.Balanced)
                return;

            float error = holder.AimError(aimPoint);
            bool impatient = Time.time - _aimStart > 1.5f;
            if (error > weapon.AimTolerance && !(impatient && error <= weapon.AimTolerance * 3f))
                return;

            if (!weapon.NPCFire(Character.gameObject))
                return;

            _aimStart = Time.time;
            if (--_burstLeft > 0)
            {
                _nextShot = Time.time + weapon.FireInterval * Random.Range(0.9f, 1.3f);
            }
            else
            {
                _burstLeft = NewBurst();
                NPCStateMachine.WeaponSettings w = Machine.Weapons;
                _nextShot = Time.time + Random.Range(w.burstPauseMin, Mathf.Max(w.burstPauseMin, w.burstPauseMax));
            }
        }

        private int NewBurst()
        {
            NPCStateMachine.WeaponSettings w = Machine.Weapons;
            return Random.Range(Mathf.Max(1, w.burstMin), Mathf.Max(w.burstMin, w.burstMax) + 1);
        }
    }
}
