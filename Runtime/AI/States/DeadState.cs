namespace ActiveRagdoll
{
    /// <summary>
    /// Terminal state. The character's muscles relax over its death relax time and it stays a fully
    /// simulated corpse: shootable, stabbable, grabbable and draggable. Optionally despawns after a lifetime.
    /// </summary>
    internal sealed class DeadState : NPCState
    {
        public DeadState(NPCStateMachine machine) : base(machine) { }

        public override NPCStateId Id => NPCStateId.Dead;

        public override void Enter()
        {
            Character.Kill();
            StopMoving();
            ClearLook();
            if (Machine.HasProcedural)
            {
                Machine.Procedural.CancelStrike();
                Machine.Procedural.PelvisHeightOffset = 0f;
            }
            if (Machine.Perception != null)
            {
                Machine.Perception.ForgetTarget();
                Machine.Perception.enabled = false;
            }
        }

        public override void Tick(float deltaTime)
        {
            float lifetime = Machine.Corpse.lifetime;
            if (lifetime > 0f && Machine.StateTime >= lifetime)
                Machine.ExpireCorpse();
        }
    }
}
