using UnityEngine;

namespace ActiveRagdoll
{
    /// <summary>
    /// Optional: feeds the character's physical state into Animator parameters so animation clips (walk
    /// blend trees, hit reactions, guard poses) can drive joint targets instead of, or on top of, the
    /// procedural layer. Parameters that don't exist on the controller are skipped silently.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Active Ragdoll/Animator Parameter Bridge")]
    [RequireComponent(typeof(ActiveRagdollCharacter))]
    public sealed class AnimatorParameterBridge : MonoBehaviour
    {
        [SerializeField] private string _speedParameter = "Speed";
        [SerializeField] private string _forwardParameter = "Forward";
        [SerializeField] private string _strafeParameter = "Strafe";
        [SerializeField] private string _groundedParameter = "Grounded";
        [SerializeField] private string _deadParameter = "Dead";
        [SerializeField] private string _stateParameter = "State";
        [SerializeField] private string _strikeParameter = "Strike";
        [SerializeField, Min(0f)] private float _dampTime = 0.1f;

        private ActiveRagdollCharacter _character;
        private Animator _animator;
        private int _speed, _forward, _strafe, _grounded, _dead, _state, _strike;
        private int _lastStrikeId;

        private void Start()
        {
            _character = GetComponent<ActiveRagdollCharacter>();
            _animator = _character != null ? _character.Animator : null;
            if (_animator == null || _animator.runtimeAnimatorController == null)
            {
                enabled = false;
                return;
            }

            _speed = Resolve(_speedParameter, AnimatorControllerParameterType.Float);
            _forward = Resolve(_forwardParameter, AnimatorControllerParameterType.Float);
            _strafe = Resolve(_strafeParameter, AnimatorControllerParameterType.Float);
            _grounded = Resolve(_groundedParameter, AnimatorControllerParameterType.Bool);
            _dead = Resolve(_deadParameter, AnimatorControllerParameterType.Bool);
            _state = Resolve(_stateParameter, AnimatorControllerParameterType.Int);
            _strike = Resolve(_strikeParameter, AnimatorControllerParameterType.Trigger);
        }

        private int Resolve(string parameter, AnimatorControllerParameterType type)
        {
            if (string.IsNullOrEmpty(parameter))
                return 0;
            foreach (AnimatorControllerParameter p in _animator.parameters)
                if (p.name == parameter && p.type == type)
                    return p.nameHash;
            return 0;
        }

        /// <summary>Called by <see cref="NPCStateMachine"/> on state changes.</summary>
        public void SetState(int state)
        {
            if (_state != 0 && isActiveAndEnabled && _animator.isActiveAndEnabled)
                _animator.SetInteger(_state, state);
        }

        private void Update()
        {
            if (_character == null || !_character.IsValid || !_animator.isActiveAndEnabled)
                return;

            float dt = Time.deltaTime;
            BalanceController balance = _character.Balance;
            Vector3 velocity = balance != null && balance.IsInitialized ? RagdollMath.Flatten(balance.ComVelocity) : Vector3.zero;
            Quaternion heading = _character.HeadingRotation;
            Vector3 local = Quaternion.Inverse(heading) * velocity;

            if (_speed != 0) _animator.SetFloat(_speed, velocity.magnitude, _dampTime, dt);
            if (_forward != 0) _animator.SetFloat(_forward, local.z, _dampTime, dt);
            if (_strafe != 0) _animator.SetFloat(_strafe, local.x, _dampTime, dt);
            if (_grounded != 0) _animator.SetBool(_grounded, balance == null || balance.IsGrounded);
            if (_dead != 0) _animator.SetBool(_dead, _character.IsDead);

            ProceduralAnimator procedural = _character.Procedural;
            if (_strike != 0 && procedural != null && procedural.IsStriking && procedural.StrikeId != _lastStrikeId)
            {
                _lastStrikeId = procedural.StrikeId;
                _animator.SetTrigger(_strike);
            }
        }
    }
}
