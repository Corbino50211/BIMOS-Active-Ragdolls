using UnityEngine;

namespace ActiveRagdoll
{
    /// <summary>
    /// Fetches a weapon: walks straight to it at full speed and, once within
    /// <see cref="NPCStateMachine.WeaponSettings.pickupRange"/>, snaps it into the hand. Gives up if the weapon
    /// is taken, can't be reached in time, or an enemy gets too close to ignore.
    /// </summary>
    internal sealed class EquipState : NPCState
    {
        private NPCWeapon _weapon;

        public EquipState(NPCStateMachine machine) : base(machine) { }

        public override NPCStateId Id => NPCStateId.Equip;

        public override void Enter()
        {
            _weapon = Machine.PendingWeapon;
            Machine.ApplyProfile(Machine.Profiles.walk, Machine.Profiles.blendTime);
        }

        public override void Exit()
        {
            if (Machine.Navigator != null)
                Machine.Navigator.Stop();
        }

        public override void Tick(float deltaTime)
        {
            NPCWeaponHolder holder = Machine.Holder;
            if (_weapon == null || holder == null || !_weapon.IsAvailable || holder.IsArmed)
            {
                Machine.ResumeBehaviour();
                return;
            }

            if (Machine.StateTime > Machine.Weapons.equipTimeout)
            {
                holder.IgnoreWeapon(_weapon, 15f);
                Machine.ResumeBehaviour();
                return;
            }

            // An enemy right on top of us: fight now, fetch later.
            NPCPerception perception = Machine.Perception;
            if (Machine.ShouldEngage && perception.CanSeeTarget)
            {
                FlatTo(perception.Target.CenterPoint, out float threat);
                if (threat < 2f)
                {
                    Machine.ResumeBehaviour();
                    return;
                }
                LookAt(perception.Target.AimPoint);
            }

            Vector3 grip = _weapon.GripPosition;
            Vector3 to = FlatTo(grip, out float distance);
            float height = Mathf.Abs(grip.y - holder.FloorHeight());

            // In range: the gun snaps into the hand.
            if (distance <= Machine.Weapons.pickupRange && height < 2.2f)
            {
                SetMovement(Vector3.zero, to);
                if (!holder.PickUp(_weapon))
                    holder.IgnoreWeapon(_weapon, 15f);
                Machine.ResumeBehaviour();
                return;
            }

            Vector3 steer = distance > 1e-3f ? to / distance : Vector3.zero;
            NPCNavigator navigator = Machine.Navigator;
            if (navigator != null)
            {
                navigator.SetIgnoredRoot(_weapon.RootTransform);
                navigator.SetDestination(grip);
                Vector3 path = navigator.GetSteeringDirection(Character.Position);
                if (path.sqrMagnitude > 0f)
                    steer = path;
            }
            float speed = Machine.HasLocomotion ? Machine.Locomotion.WalkSpeed : 0f;
            SetMovement(steer * speed, to);
        }
    }
}
