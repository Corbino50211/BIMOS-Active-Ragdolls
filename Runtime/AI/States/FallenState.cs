using UnityEngine;

namespace ActiveRagdoll
{
    /// <summary>
    /// Down on the ground: balance and propulsion off, muscles at a low protective tone. Waits for the body
    /// to settle before trying to get up; crippled legs keep it down.
    /// </summary>
    internal sealed class FallenState : NPCState
    {
        private float _settled;
        private float _exhaustedUntil;

        public FallenState(NPCStateMachine machine) : base(machine) { }

        public override NPCStateId Id => NPCStateId.Fallen;

        public override void Enter()
        {
            Machine.ApplyProfile(Machine.Profiles.fallen, Machine.Profiles.fallenBlendTime);
            StopMoving();
            ClearLook();
            if (Machine.HasProcedural)
            {
                Machine.Procedural.CancelStrike();
                Machine.Procedural.PelvisHeightOffset = 0f;
            }
            _settled = 0f;
            if (Machine.GetUpAttempts >= Machine.Reactions.maxGetUpAttempts)
                _exhaustedUntil = Time.time + Machine.Reactions.getUpRetryDelay;
        }

        public override void Tick(float deltaTime)
        {
            NPCStateMachine.ReactionSettings r = Machine.Reactions;
            Rigidbody pelvis = Character.Pelvis;
            float speed = pelvis != null ? pelvis.linearVelocity.magnitude : 0f;
            _settled = speed < r.settleSpeed ? _settled + deltaTime : 0f;

            if (Machine.GetUpAttempts >= r.maxGetUpAttempts)
            {
                if (Time.time < _exhaustedUntil)
                    return;
                Machine.GetUpAttempts = 0;
            }

            bool legsWork = !Machine.HasBalance || Machine.Balance.LegFunction > 0.3f;
            if (legsWork && Machine.StateTime >= r.minDownTime && _settled >= r.settleTime)
                Machine.ChangeState(NPCStateId.GettingUp);
        }
    }
}
