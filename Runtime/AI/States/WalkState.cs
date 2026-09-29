using UnityEngine;

namespace ActiveRagdoll
{
    /// <summary>
    /// Chases a visible target (or searches its last known position) using physics locomotion, and hands
    /// over to <see cref="AttackState"/> when in range, facing and balanced.
    /// </summary>
    internal sealed class WalkState : NPCState
    {
        public WalkState(NPCStateMachine machine) : base(machine) { }

        public override NPCStateId Id => NPCStateId.Walk;

        public override void Enter()
        {
            Machine.ApplyProfile(Machine.Profiles.walk, Machine.Profiles.blendTime);
        }

        public override void Exit()
        {
            if (Machine.Navigator != null)
                Machine.Navigator.Stop();
        }

        public override void Tick(float deltaTime)
        {
            NPCPerception perception = Machine.Perception;
            CombatTarget target = perception != null ? perception.Target : null;
            if (target == null || !perception.HasTarget || !Machine.IsAggressive)
            {
                Machine.ChangeState(NPCStateId.Idle);
                return;
            }

            if (Machine.TryStartEquip())
                return;

            NPCStateMachine.ChaseSettings chase = Machine.Chase;
            bool visible = perception.CanSeeTarget;
            Vector3 goal = visible ? target.CenterPoint : perception.LastKnownPosition;
            Vector3 toGoal = FlatTo(goal, out float distance);

            if (!visible && distance < 0.8f)
            {
                // Reached the last known position without regaining sight: look around, then give up.
                SetMovement(Vector3.zero, Vector3.zero);
                LookAt(perception.LastKnownAimPoint);
                if (perception.TimeSinceSeen > chase.searchTime)
                {
                    perception.ForgetTarget();
                    Machine.ChangeState(NPCStateId.Idle);
                }
                return;
            }

            Vector3 from = Character.Position;
            Vector3 steer = toGoal.sqrMagnitude > 1e-6f ? toGoal / distance : Vector3.zero;
            float travel = distance;
            NPCNavigator navigator = Machine.Navigator;
            if (navigator != null)
            {
                navigator.SetIgnoredRoot(target.transform);
                navigator.SetDestination(goal);
                Vector3 path = navigator.GetSteeringDirection(from);
                if (path.sqrMagnitude > 0f)
                    steer = path;
                // Around an obstacle the path is longer than the straight line: don't slow down early.
                travel = Mathf.Max(distance, navigator.RemainingDistance(from));
            }

            float speed = 0f;
            if (Machine.HasLocomotion)
            {
                LocomotionController locomotion = Machine.Locomotion;
                speed = travel > chase.runDistance ? locomotion.RunSpeed : locomotion.WalkSpeed;
                float stop = visible ? chase.stopDistance : 0.3f;
                speed *= Mathf.Clamp01((travel - stop) / chase.slowRadius);
            }

            Vector3 separation = navigator != null
                ? navigator.ComputeSeparation(from, Character) * chase.separationWeight
                : Vector3.zero;
            // Face the target only when the route heads roughly toward it; otherwise face along the detour.
            bool pathTowardTarget = Vector3.Angle(steer, toGoal) < 50f;
            Vector3 facing = visible && distance < chase.faceTargetDistance && pathTowardTarget ? toGoal : Vector3.zero;
            SetMovement(steer * speed + separation, facing);

            if (visible)
            {
                LookAt(target.AimPoint);
                if (Machine.CanShoot(target, distance))
                    Machine.ChangeState(NPCStateId.Shoot);
                else if (Machine.CanAttack(target, distance, toGoal))
                    Machine.ChangeState(NPCStateId.Attack);
            }
            else
            {
                LookAt(perception.LastKnownAimPoint);
            }
        }
    }
}
