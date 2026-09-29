using UnityEngine;

namespace ActiveRagdoll
{
    /// <summary>
    /// Hit reaction: muscles and balance soften in proportion to severity so the blow visibly moves the body,
    /// propulsion stops and the gait takes capture-point recovery steps. Recovers to the previous behaviour,
    /// or falls if balance is lost meanwhile.
    /// </summary>
    internal sealed class StaggerState : NPCState
    {
        private float _duration;
        private float _severity;

        public StaggerState(NPCStateMachine machine) : base(machine) { }

        public override NPCStateId Id => NPCStateId.Stagger;

        public override void Enter()
        {
            _severity = Machine.PendingStaggerSeverity;
            _duration = DurationFor(_severity);
            if (Machine.HasProcedural)
                Machine.Procedural.CancelStrike();
            SetMovement(Vector3.zero, Vector3.zero);
            ApplySeverityProfile();
        }

        /// <summary>Another hit landed while staggering.</summary>
        public void Extend(float severity)
        {
            _duration = Mathf.Max(_duration, Machine.StateTime + DurationFor(severity) * 0.6f);
            if (severity > _severity)
            {
                _severity = severity;
                ApplySeverityProfile();
            }
        }

        public override void Tick(float deltaTime)
        {
            float t = Machine.StateTime;
            bool stillStumbling = Machine.HasBalance && Machine.Balance.State == BalanceState.Stumbling;
            float maxDuration = Machine.Reactions.staggerDurationMax * 1.5f;
            if (t < _duration || (stillStumbling && t < maxDuration))
                return;

            if (Machine.HasBalance && Machine.Balance.State == BalanceState.Lost)
                return; // the balance-lost event moves us to Fallen

            Machine.ResumeBehaviour();
        }

        private float DurationFor(float severity)
        {
            NPCStateMachine.ReactionSettings r = Machine.Reactions;
            return Mathf.Lerp(r.staggerDurationMin, r.staggerDurationMax, severity);
        }

        private void ApplySeverityProfile()
        {
            NPCStateMachine.StateProfiles p = Machine.Profiles;
            RagdollProfile profile = RagdollProfile.Lerp(p.walk, p.stagger, 0.5f + 0.5f * _severity);
            Machine.ApplyProfile(profile, p.staggerBlendTime);
        }
    }
}
