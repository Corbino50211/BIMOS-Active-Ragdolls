using UnityEngine;

namespace ActiveRagdoll
{
    /// <summary>
    /// Fetches a weapon: walks to it, crouches and leans to reach it, and grips it once the physical hand gets
    /// there. Gives up if the weapon is taken, can't be reached, or an enemy gets too close to ignore.
    /// </summary>
    internal sealed class EquipState : NPCState
    {
        private NPCWeapon _weapon;
        private float _reachStart;

        public EquipState(NPCStateMachine machine) : base(machine) { }

        public override NPCStateId Id => NPCStateId.Equip;

        public override void Enter()
        {
            _weapon = Machine.PendingWeapon;
            _reachStart = -1f;
            Machine.ApplyProfile(Machine.Profiles.walk, Machine.Profiles.blendTime);
        }

        public override void Exit()
        {
            if (Machine.Holder != null)
                Machine.Holder.StopAiming();
            if (Machine.HasProcedural)
            {
                Machine.Procedural.PelvisHeightOffset = 0f;
                Machine.Procedural.ExtraLean = 0f;
            }
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

            if (_reachStart < 0f && distance > 0.55f)
            {
                Vector3 from = Character.Position;
                Vector3 steer = distance > 1e-3f ? to / distance : Vector3.zero;
                NPCNavigator navigator = Machine.Navigator;
                if (navigator != null)
                {
                    navigator.SetIgnoredRoot(_weapon.RootTransform);
                    navigator.SetDestination(grip);
                    Vector3 path = navigator.GetSteeringDirection(from);
                    if (path.sqrMagnitude > 0f)
                        steer = path;
                }
                float speed = Machine.HasLocomotion
                    ? Machine.Locomotion.WalkSpeed * Mathf.Clamp(0.25f + (distance - 0.4f) / 0.8f, 0.25f, 1f)
                    : 0f;
                SetMovement(steer * speed, to);
                holder.StopAiming();
                return;
            }

            // Within reach: stop, crouch and lean until the hand gets to the grip.
            if (_reachStart < 0f)
                _reachStart = Time.time;
            SetMovement(Vector3.zero, to);

            if (Machine.HasProcedural)
            {
                float lowestShoulder = holder.FloorHeight() + holder.ShoulderHeight - holder.ArmLength * 0.8f;
                float drop = Mathf.Max(0f, lowestShoulder - grip.y);
                Machine.Procedural.PelvisHeightOffset = -Mathf.Min(drop, Character.StandingPelvisHeight * 0.6f);
                Machine.Procedural.ExtraLean = Mathf.Clamp(drop * 45f, 0f, 40f);
            }
            holder.ReachFor(grip);

            float handDistance = holder.HandDistanceTo(grip);
            float reachTime = Time.time - _reachStart;
            if (handDistance < 0.3f || (reachTime > 1.2f && handDistance < 0.8f))
            {
                if (!holder.PickUp(_weapon))
                    holder.IgnoreWeapon(_weapon, 15f);
                Machine.ResumeBehaviour();
                return;
            }

            if (reachTime > 2.5f)
            {
                holder.IgnoreWeapon(_weapon, 15f);
                Machine.ResumeBehaviour();
            }
        }
    }
}
