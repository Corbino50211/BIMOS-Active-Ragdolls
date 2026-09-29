using UnityEngine;
using UnityEngine.AI;

namespace ActiveRagdoll
{
    /// <summary>
    /// Non-combat behaviour. With the Idle disposition the NPC stands balanced in a relaxed stance; with
    /// Wander it strolls between random points around its home, pausing at each. Either way it hands over
    /// to <see cref="WalkState"/> as soon as it is aggressive (Hostile, or provoked) and has a target.
    /// </summary>
    internal sealed class IdleState : NPCState
    {
        private bool _hasWanderPoint;
        private Vector3 _wanderPoint;
        private float _pauseUntil;
        private float _wanderStarted;

        public IdleState(NPCStateMachine machine) : base(machine) { }

        public override NPCStateId Id => NPCStateId.Idle;

        public override void Enter()
        {
            Machine.CalmDownIfTargetLost();
            Machine.ApplyProfile(Machine.Profiles.idle, Machine.Profiles.blendTime);
            StopMoving();
            ClearLook();
            _hasWanderPoint = false;
            _pauseUntil = Time.time + Random.Range(0.5f, Machine.Wander.pauseMin + 0.5f);
        }

        public override void Tick(float deltaTime)
        {
            if (Machine.ShouldEngage)
            {
                Machine.ChangeState(NPCStateId.Walk);
                return;
            }

            if (Machine.TryStartEquip())
                return;

            // Non-hostile NPCs still notice people: they look at whoever they can see.
            NPCPerception perception = Machine.Perception;
            if (perception != null && perception.Target != null && perception.HasTarget)
                LookAt(perception.LastKnownAimPoint);
            else
                ClearLook();

            if (Machine.Disposition == NPCDisposition.Wander)
                TickWander();
            else
                SetMovement(Vector3.zero, Vector3.zero);
        }

        private void TickWander()
        {
            NPCStateMachine.WanderSettings w = Machine.Wander;
            if (!Machine.HasLocomotion || Time.time < _pauseUntil)
            {
                SetMovement(Vector3.zero, Vector3.zero);
                return;
            }

            if (!_hasWanderPoint && !PickWanderPoint())
            {
                Pause();
                return;
            }

            Vector3 from = Character.Position;
            Vector3 to = RagdollMath.Flatten(_wanderPoint - from);
            bool arrived = to.magnitude < 0.6f;
            if (arrived || Time.time - _wanderStarted > w.giveUpTime)
            {
                Pause();
                return;
            }

            Vector3 steer = to.normalized;
            NPCNavigator navigator = Machine.Navigator;
            if (navigator != null)
            {
                navigator.SetIgnoredRoot(null);
                navigator.SetDestination(_wanderPoint);
                Vector3 path = navigator.GetSteeringDirection(from);
                if (path.sqrMagnitude > 0f)
                    steer = path;
            }

            float speed = Machine.Locomotion.WalkSpeed * w.speedFactor * Mathf.Clamp01(to.magnitude / 0.8f + 0.3f);
            Vector3 separation = navigator != null ? navigator.ComputeSeparation(from, Character) * Machine.Chase.separationWeight : Vector3.zero;
            SetMovement(steer * speed + separation, Vector3.zero);
        }

        private void Pause()
        {
            _hasWanderPoint = false;
            StopMoving();
            NPCStateMachine.WanderSettings w = Machine.Wander;
            _pauseUntil = Time.time + Random.Range(w.pauseMin, w.pauseMax);
        }

        private bool PickWanderPoint()
        {
            Vector3 home = Machine.HomePosition;
            float radius = Machine.Wander.radius;
            for (int attempt = 0; attempt < 6; attempt++)
            {
                Vector2 r = Random.insideUnitCircle * radius;
                Vector3 candidate = home + new Vector3(r.x, 0f, r.y);
                if (RagdollMath.Flatten(candidate - Character.Position).magnitude < 1.5f)
                    continue; // too close to bother walking

                // Prefer points on the NavMesh when one exists.
                if (NavMesh.SamplePosition(candidate, out NavMeshHit hit, 2f, NavMesh.AllAreas))
                    candidate = hit.position;

                _wanderPoint = candidate;
                _hasWanderPoint = true;
                _wanderStarted = Time.time;
                return true;
            }
            return false;
        }
    }
}
