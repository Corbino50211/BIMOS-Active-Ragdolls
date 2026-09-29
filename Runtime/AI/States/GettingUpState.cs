using UnityEngine;

namespace ActiveRagdoll
{
    /// <summary>
    /// Physically stands the body back up. Muscles re-tension first, then balance torque ramps in to bring
    /// the torso upright while the crouched target pelvis height rises to standing. The gait steps the feet
    /// under the hips, and the height support can only lift as far as the planted legs reach, so the body
    /// has to get its feet under itself to stand. A big enough hit knocks it back down.
    /// </summary>
    internal sealed class GettingUpState : NPCState
    {
        private float _startHeight;

        public GettingUpState(NPCStateMachine machine) : base(machine) { }

        public override NPCStateId Id => NPCStateId.GettingUp;

        public override void Enter()
        {
            Machine.GetUpAttempts++;
            StopMoving();
            if (Machine.HasLocomotion)
                Machine.Locomotion.SnapHeading(Character.GetBodyYaw());
            if (Machine.HasProcedural)
                Machine.Procedural.ResetFeet();
            if (Machine.HasBalance)
            {
                Machine.Balance.ResetBalance();
                _startHeight = Machine.Balance.PelvisHeight;
            }
            else
            {
                _startHeight = Character.StandingPelvisHeight * 0.3f;
            }
            ApplyRamp(0f);
        }

        public override void Exit()
        {
            if (Machine.HasProcedural)
                Machine.Procedural.PelvisHeightOffset = 0f;
        }

        public override void Tick(float deltaTime)
        {
            NPCStateMachine.ReactionSettings r = Machine.Reactions;
            float t = Machine.StateTime;
            float u = t / r.getUpDuration;
            ApplyRamp(u);

            if (u < 1f)
                return;

            bool upright = !Machine.HasBalance
                || (Machine.Balance.TiltAngle < r.getUpSuccessTilt && Machine.Balance.PelvisHeightRatio > r.getUpSuccessHeight);
            if (upright)
            {
                Machine.GetUpAttempts = 0;
                if (Machine.HasBalance)
                    Machine.Balance.ResetBalance();
                Machine.ResumeBehaviour();
            }
            else if (t > r.getUpDuration * 2f)
            {
                Machine.ChangeState(NPCStateId.Fallen);
            }
        }

        private void ApplyRamp(float u)
        {
            NPCStateMachine.StateProfiles p = Machine.Profiles;
            RagdollProfile profile = p.gettingUp;
            float tension = RagdollMath.SmoothStep01(u / 0.3f);
            float balance = RagdollMath.SmoothStep01(u / 0.6f);
            profile.muscleStrength = Mathf.Lerp(p.fallen.muscleStrength, p.gettingUp.muscleStrength, tension);
            profile.balanceStrength = p.gettingUp.balanceStrength * Mathf.Lerp(0.1f, 1f, balance);
            profile.gaitWeight = p.gettingUp.gaitWeight * RagdollMath.SmoothStep01(u / 0.2f);
            Character.SetProfileImmediate(profile);

            if (Machine.HasProcedural)
            {
                float standing = Character.StandingPelvisHeight;
                float from = Mathf.Max(_startHeight, standing * 0.45f);
                float height = Mathf.Lerp(from, standing, RagdollMath.SmoothStep01((u - 0.25f) / 0.75f));
                Machine.Procedural.PelvisHeightOffset = Mathf.Min(0f, height - standing);
            }
        }
    }
}
