namespace ActiveRagdoll
{
    /// <summary>Stands balanced in a relaxed stance until a hostile is perceived.</summary>
    internal sealed class IdleState : NPCState
    {
        public IdleState(NPCStateMachine machine) : base(machine) { }

        public override NPCStateId Id => NPCStateId.Idle;

        public override void Enter()
        {
            Machine.ApplyProfile(Machine.Profiles.idle, Machine.Profiles.blendTime);
            StopMoving();
            ClearLook();
        }

        public override void Tick(float deltaTime)
        {
            NPCPerception perception = Machine.Perception;
            if (perception != null && perception.HasTarget)
            {
                Machine.ChangeState(NPCStateId.Walk);
                return;
            }

            if (perception != null && perception.Target != null)
                LookAt(perception.LastKnownAimPoint);
            else
                ClearLook();
        }
    }
}
