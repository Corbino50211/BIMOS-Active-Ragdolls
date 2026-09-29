using UnityEngine;

namespace ActiveRagdoll
{
    /// <summary>Base class for <see cref="NPCStateMachine"/> states. Plain C# objects, created once per NPC.</summary>
    internal abstract class NPCState
    {
        protected readonly NPCStateMachine Machine;

        protected NPCState(NPCStateMachine machine)
        {
            Machine = machine;
        }

        public abstract NPCStateId Id { get; }

        public virtual void Enter() { }

        public virtual void Exit() { }

        public virtual void Tick(float deltaTime) { }

        protected ActiveRagdollCharacter Character => Machine.Character;

        protected void SetMovement(Vector3 velocity, Vector3 facing)
        {
            if (!Machine.HasLocomotion) return;
            Machine.Locomotion.DesiredVelocity = velocity;
            Machine.Locomotion.DesiredFacing = facing;
        }

        protected void StopMoving()
        {
            if (Machine.HasLocomotion) Machine.Locomotion.Stop();
            if (Machine.Navigator != null) Machine.Navigator.Stop();
        }

        protected void LookAt(Vector3 point)
        {
            if (Machine.HasProcedural) Machine.Procedural.SetLookTarget(point);
        }

        protected void ClearLook()
        {
            if (Machine.HasProcedural) Machine.Procedural.ClearLookTarget();
        }

        /// <summary>Flat vector and distance from the pelvis to a point.</summary>
        protected Vector3 FlatTo(Vector3 point, out float distance)
        {
            Vector3 v = RagdollMath.Flatten(point - Character.Position);
            distance = v.magnitude;
            return v;
        }
    }
}
