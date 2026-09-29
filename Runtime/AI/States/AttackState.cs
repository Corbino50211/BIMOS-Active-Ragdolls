using UnityEngine;

namespace ActiveRagdoll
{
    /// <summary>
    /// Throws physically simulated strikes: the procedural layer moves the animated fist along the strike
    /// path, the arm's muscles are boosted and the fist pinned so the ragdoll's real hand follows, and the NPC
    /// steps in. Damage only happens if the physical fist actually connects. Can chain combos.
    /// </summary>
    internal sealed class AttackState : NPCState
    {
        private StrikeDefinition _strike;
        private int _combo;

        public AttackState(NPCStateMachine machine) : base(machine) { }

        public override NPCStateId Id => NPCStateId.Attack;

        public override void Enter()
        {
            _combo = 0;
            if (!TryBeginStrike())
            {
                Machine.SetAttackCooldown(0.3f);
                Machine.ChangeState(NPCStateId.Walk);
                return;
            }
            Machine.ApplyProfile(Machine.Profiles.attack, Machine.Profiles.blendTime * 0.5f);
        }

        public override void Exit()
        {
            if (Machine.HasProcedural && Machine.Procedural.IsStriking)
                Machine.Procedural.CancelStrike();
            _strike = null;
        }

        public override void Tick(float deltaTime)
        {
            NPCPerception perception = Machine.Perception;
            CombatTarget target = perception != null ? perception.Target : null;
            ProceduralAnimator procedural = Machine.Procedural;
            if (target == null || !Machine.HasProcedural)
            {
                Machine.SetRandomAttackCooldown();
                Machine.ResumeBehaviour();
                return;
            }

            Vector3 toTarget = FlatTo(target.CenterPoint, out float distance);
            if (procedural.IsStriking)
            {
                procedural.UpdateStrikeTarget(Machine.GetAimPoint(target, _strike));
                LookAt(target.AimPoint);

                StrikePhase phase = procedural.CurrentStrikePhase;
                bool lunge = (phase == StrikePhase.Windup || phase == StrikePhase.Strike) && distance > Machine.Chase.stopDistance * 0.8f;
                Vector3 dir = distance > 1e-3f ? toTarget / distance : Vector3.zero;
                SetMovement(lunge && _strike != null ? dir * _strike.lungeSpeed : Vector3.zero, toTarget);
                return;
            }

            // Strike finished: maybe chain another one.
            bool canChain = perception.CanSeeTarget && _combo < Machine.Attack.maxCombo
                && distance <= Machine.Attack.attackRange && Random.value < Machine.Attack.comboChance;
            if (canChain && TryBeginStrike())
            {
                _combo++;
                return;
            }

            Machine.SetRandomAttackCooldown();
            Machine.ResumeBehaviour();
        }

        private bool TryBeginStrike()
        {
            NPCPerception perception = Machine.Perception;
            CombatTarget target = perception != null ? perception.Target : null;
            if (target == null || !Machine.HasProcedural)
                return false;

            _strike = Machine.ChooseStrike();
            if (_strike == null)
                return false;
            return Machine.Procedural.BeginStrike(_strike, Machine.GetAimPoint(target, _strike));
        }
    }
}
