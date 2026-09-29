using System;
using UnityEngine;
using UnityEngine.Events;

namespace ActiveRagdoll
{
    /// <summary>How an NPC treats hostiles it perceives.</summary>
    public enum NPCDisposition
    {
        /// <summary>Stands in place. Fights only when attacked (if Retaliate When Attacked is on).</summary>
        Idle = 0,

        /// <summary>Strolls around its home point. Fights only when attacked (if Retaliate When Attacked is on).</summary>
        Wander = 1,

        /// <summary>Hunts any hostile it perceives.</summary>
        Hostile = 2,
    }

    public enum NPCStateId
    {
        Idle = 0,
        Walk = 1,
        Attack = 2,
        Stagger = 3,
        Fallen = 4,
        GettingUp = 5,
        Dead = 6,
        Equip = 7,
        Shoot = 8,
    }

    /// <summary>
    /// Behaviour layer of an active ragdoll NPC: Idle → Walk (chase/search) → Attack, with Stagger, Fallen,
    /// GettingUp and Dead driven by physics (balance loss, hits) and health. States never switch physics off;
    /// they only blend <see cref="RagdollProfile"/>s and set locomotion/pose goals, so the body stays
    /// simulated and hittable in every state.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Active Ragdoll/AI/NPC State Machine")]
    [RequireComponent(typeof(ActiveRagdollCharacter))]
    public sealed class NPCStateMachine : MonoBehaviour
    {
        [Serializable]
        public sealed class StateProfiles
        {
            public RagdollProfile idle = new RagdollProfile(1f, 1f, 1f, 1f, 0.3f, 1f);
            public RagdollProfile walk = new RagdollProfile(1f, 1f, 1f, 1f, 0.85f, 1f);
            public RagdollProfile attack = new RagdollProfile(1.1f, 1.1f, 1f, 1f, 1f, 1f);
            public RagdollProfile stagger = new RagdollProfile(0.5f, 0.75f, 0.6f, 1f, 0.3f, 0.3f);
            public RagdollProfile fallen = new RagdollProfile(0.18f, 0f, 0f, 0f, 0f, 0f);
            public RagdollProfile gettingUp = new RagdollProfile(0.9f, 1f, 0.3f, 1f, 0.2f, 0.5f);
            public RagdollProfile shoot = new RagdollProfile(1.1f, 1.1f, 1f, 1f, 0.6f, 1f);
            [Min(0f)] public float blendTime = 0.25f;
            [Min(0f)] public float staggerBlendTime = 0.05f;
            [Min(0f)] public float fallenBlendTime = 0.12f;
        }

        [Serializable]
        public sealed class ChaseSettings
        {
            [Tooltip("Run instead of walk beyond this distance (m).")]
            [Min(0f)] public float runDistance = 6f;
            [Tooltip("Stop approaching inside this distance (m, pelvis to target centre).")]
            [Min(0f)] public float stopDistance = 0.75f;
            [Min(0.01f)] public float slowRadius = 1f;
            [Tooltip("Face the target (instead of the path) inside this distance.")]
            [Min(0f)] public float faceTargetDistance = 4f;
            [Tooltip("Seconds spent searching the last known position before giving up.")]
            [Min(0f)] public float searchTime = 6f;
            [Min(0f)] public float separationWeight = 1f;
        }

        [Serializable]
        public sealed class AttackSettings
        {
            public StrikeDefinition[] strikes = StrikeDefinition.CreateDefaultSet();
            [Tooltip("Horizontal distance (m, pelvis to target centre) at which a strike is started.")]
            [Min(0f)] public float attackRange = 1.05f;
            [Range(0f, 180f)] public float maxAttackAngle = 35f;
            [Min(0f)] public float cooldownMin = 0.35f;
            [Min(0f)] public float cooldownMax = 1.1f;
            [Tooltip("Chance to chain straight into another strike.")]
            [Range(0f, 1f)] public float comboChance = 0.35f;
            [Min(0)] public int maxCombo = 2;
            [Tooltip("Seconds of target velocity to lead the aim point by.")]
            [Min(0f)] public float aimLead = 0.15f;
        }

        [Serializable]
        public sealed class ReactionSettings
        {
            [Tooltip("Hit severity (0..1) that staggers an idle/walking NPC.")]
            [Range(0f, 1f)] public float staggerSeverity = 0.2f;
            [Tooltip("Hit severity that interrupts an attack.")]
            [Range(0f, 1f)] public float interruptAttackSeverity = 0.5f;
            [Min(0f)] public float staggerDurationMin = 0.35f;
            [Min(0f)] public float staggerDurationMax = 1.2f;
            [Tooltip("Hit severity that knocks an NPC back down while it is getting up.")]
            [Range(0f, 1f)] public float getUpInterruptSeverity = 0.45f;

            [Header("Fallen / Getting Up")]
            [Min(0f)] public float minDownTime = 1.2f;
            [Tooltip("Pelvis speed (m/s) below which the body counts as settled.")]
            [Min(0f)] public float settleSpeed = 0.35f;
            [Min(0f)] public float settleTime = 0.4f;
            [Min(0.1f)] public float getUpDuration = 1.4f;
            [Min(1)] public int maxGetUpAttempts = 3;
            [Tooltip("Rest (s) after running out of attempts before trying again.")]
            [Min(0f)] public float getUpRetryDelay = 3f;
            [Range(0f, 90f)] public float getUpSuccessTilt = 30f;
            [Range(0f, 1f)] public float getUpSuccessHeight = 0.8f;
        }

        [Serializable]
        public sealed class WeaponSettings
        {
            [Tooltip("Pick up and use NPCWeapons (guns). Adds an NPCWeaponHolder automatically.")]
            public bool useWeapons = true;
            [Tooltip("How far (m) the NPC looks for weapons.")]
            [Min(0f)] public float searchRadius = 12f;
            [Tooltip("Hostile NPCs arm themselves even before they have seen anyone.")]
            public bool armWhenCalm = true;
            [Tooltip("Skip a weapon that is this much further (m) away than the enemy.")]
            [Min(0f)] public float maxDetour = 3f;
            [Min(0f)] public float equipTimeout = 10f;
            [Tooltip("Back away from enemies closer than this (m) while shooting.")]
            [Min(0f)] public float minShootDistance = 1.5f;
            [Min(0f)] public float firstShotDelay = 0.4f;
            [Min(1)] public int burstMin = 1;
            [Min(1)] public int burstMax = 3;
            [Min(0f)] public float burstPauseMin = 0.5f;
            [Min(0f)] public float burstPauseMax = 1.3f;
        }

        [Serializable]
        public sealed class CorpseSettings
        {
            [Tooltip("Seconds a corpse persists (0 = forever).")]
            [Min(0f)] public float lifetime;
            [Tooltip("Destroy the NPC when the lifetime expires (otherwise it is deactivated for pooling).")]
            public bool destroyOnExpire = true;
        }

        [Serializable]
        public sealed class WanderSettings
        {
            [Tooltip("How far (m) from its home point the NPC strolls.")]
            [Min(0.5f)] public float radius = 6f;
            [Tooltip("Walking speed as a fraction of Locomotion Walk Speed.")]
            [Range(0.2f, 1f)] public float speedFactor = 0.6f;
            [Min(0f)] public float pauseMin = 2f;
            [Min(0f)] public float pauseMax = 6f;
            [Tooltip("Give up on a wander point after this many seconds (blocked or unreachable).")]
            [Min(1f)] public float giveUpTime = 12f;
        }

        [Header("Behaviour")]
        [Tooltip("Idle: stands still. Wander: strolls around. Hostile: hunts any hostile it sees.")]
        [SerializeField] private NPCDisposition _disposition = NPCDisposition.Hostile;

        [Tooltip("Idle/Wander only. On: fights back when hurt or grabbed, then calms down once it loses the attacker. " +
                 "Off: never fights; after reacting physically it goes back to what it was doing.")]
        [SerializeField] private bool _retaliateWhenAttacked = true;

        [SerializeField] private WanderSettings _wander = new WanderSettings();

        [SerializeField] private StateProfiles _profiles = new StateProfiles();
        [SerializeField] private ChaseSettings _chase = new ChaseSettings();
        [SerializeField] private AttackSettings _attack = new AttackSettings();
        [SerializeField] private ReactionSettings _reactions = new ReactionSettings();
        [SerializeField] private WeaponSettings _weapons = new WeaponSettings();
        [SerializeField] private CorpseSettings _corpse = new CorpseSettings();

        [Header("Events")]
        [SerializeField] private UnityEvent _onAttack = new UnityEvent();
        [SerializeField] private UnityEvent _onStagger = new UnityEvent();
        [SerializeField] private UnityEvent _onFall = new UnityEvent();
        [SerializeField] private UnityEvent _onGetUp = new UnityEvent();
        [SerializeField] private UnityEvent _onDeath = new UnityEvent();

        private NPCState[] _states;
        private NPCState _current;
        private bool _transitioning;
        private bool _hasPending;
        private NPCStateId _pending;
        private bool _started;
        private float _nextAttackTime;
        private BodySide _lastStrikeSide = BodySide.Center;
        private float _pendingStaggerSeverity;
        private Vector3 _pendingStaggerDirection;
        private bool _provoked;
        private float _nextWeaponSearch;

        // ------------------------------------------------------------------ Accessors used by states

        public ActiveRagdollCharacter Character { get; private set; }
        public LocomotionController Locomotion { get; private set; }
        public BalanceController Balance { get; private set; }
        public ProceduralAnimator Procedural { get; private set; }
        public RagdollHealth Health { get; private set; }
        public NPCPerception Perception { get; private set; }
        public NPCNavigator Navigator { get; private set; }
        public AnimatorParameterBridge AnimatorBridge { get; private set; }
        public NPCWeaponHolder Holder { get; private set; }
        internal NPCWeapon PendingWeapon { get; private set; }

        /// <summary>Idle / Wander / Hostile. Can be changed at runtime.</summary>
        public NPCDisposition Disposition
        {
            get => _disposition;
            set
            {
                _disposition = value;
                if (value == NPCDisposition.Hostile)
                    _provoked = false;
            }
        }

        public bool RetaliateWhenAttacked
        {
            get => _retaliateWhenAttacked;
            set
            {
                _retaliateWhenAttacked = value;
                if (!value)
                    _provoked = false;
            }
        }

        /// <summary>True when the NPC will chase and fight: Hostile, or provoked while Idle/Wander.</summary>
        public bool IsAggressive => _disposition == NPCDisposition.Hostile || _provoked;

        /// <summary>Idle/Wander NPC that has been attacked and is fighting back.</summary>
        public bool IsProvoked => _provoked;

        /// <summary>Centre of the wander area (spawn point by default).</summary>
        public Vector3 HomePosition { get; set; }

        public WanderSettings Wander => _wander;
        public StateProfiles Profiles => _profiles;
        public ChaseSettings Chase => _chase;
        public AttackSettings Attack => _attack;
        public ReactionSettings Reactions => _reactions;
        public WeaponSettings Weapons => _weapons;
        public CorpseSettings Corpse => _corpse;

        public NPCStateId CurrentState => _current != null ? _current.Id : NPCStateId.Idle;
        public float StateTime { get; private set; }
        public int GetUpAttempts { get; internal set; }
        internal float PendingStaggerSeverity => _pendingStaggerSeverity;
        internal Vector3 PendingStaggerDirection => _pendingStaggerDirection;

        public event Action<NPCStateId, NPCStateId> StateChanged;

        public bool HasLocomotion => Locomotion != null && Locomotion.isActiveAndEnabled && Locomotion.IsInitialized;
        public bool HasBalance => Balance != null && Balance.isActiveAndEnabled && Balance.IsInitialized;
        public bool HasProcedural => Procedural != null && Procedural.isActiveAndEnabled && Procedural.IsInitialized;

        // ------------------------------------------------------------------ Public API

        /// <summary>Requests a state change. Dead is terminal except through <see cref="Respawn"/>.</summary>
        public void ChangeState(NPCStateId next) => ChangeState(next, false);

        /// <summary>Revives the NPC standing at <paramref name="groundPosition"/> (pooling / wave spawners).</summary>
        public void Respawn(Vector3 groundPosition, Quaternion facing)
        {
            if (Character == null || !Character.IsValid)
                return;
            Character.Revive(false);
            Character.Teleport(groundPosition, facing);
            if (Perception != null)
            {
                Perception.enabled = true;
                Perception.ForgetTarget();
            }
            HomePosition = groundPosition;
            _provoked = false;
            GetUpAttempts = 0;
            _nextAttackTime = 0f;
            ChangeState(NPCStateId.Idle, true);
        }

        /// <summary>Returns to the natural behaviour for the current situation (chase if there is a target, else idle).</summary>
        public void ResumeBehaviour()
        {
            ChangeState(ShouldEngage ? NPCStateId.Walk : NPCStateId.Idle);
        }

        /// <summary>Aggressive and has someone to fight.</summary>
        internal bool ShouldEngage => IsAggressive && Perception != null && Perception.HasTarget;

        /// <summary>A provoked Idle/Wander NPC calms down once it has lost its attacker.</summary>
        internal void CalmDownIfTargetLost()
        {
            if (_provoked && (Perception == null || !Perception.HasTarget))
                _provoked = false;
        }

        private void OnAttacked(CombatTarget attacker)
        {
            if (_disposition != NPCDisposition.Hostile && _retaliateWhenAttacked && CurrentState != NPCStateId.Dead)
                _provoked = true;
        }

        // ------------------------------------------------------------------ Lifecycle

        private void Awake()
        {
            _states = new NPCState[]
            {
                new IdleState(this),
                new WalkState(this),
                new AttackState(this),
                new StaggerState(this),
                new FallenState(this),
                new GettingUpState(this),
                new DeadState(this),
                new EquipState(this),
                new ShootState(this),
            };
        }

        private void Start()
        {
            Character = GetComponent<ActiveRagdollCharacter>();
            if (Character == null || !Character.IsValid)
            {
                Debug.LogError($"{ActiveRagdollCharacter.LogPrefix} '{name}': NPCStateMachine needs a valid ActiveRagdollCharacter; disabling.", this);
                enabled = false;
                return;
            }

            Locomotion = Character.Locomotion;
            Balance = Character.Balance;
            Procedural = Character.Procedural;
            Health = Character.Health;
            Perception = GetComponent<NPCPerception>();
            Navigator = GetComponent<NPCNavigator>();
            AnimatorBridge = GetComponent<AnimatorParameterBridge>();
            Holder = GetComponent<NPCWeaponHolder>();
            if (Holder == null && _weapons.useWeapons)
                Holder = gameObject.AddComponent<NPCWeaponHolder>();

            if (!HasLocomotion) Debug.LogWarning($"{ActiveRagdollCharacter.LogPrefix} '{name}': no LocomotionController; the NPC cannot move.", this);
            if (Perception == null) Debug.LogWarning($"{ActiveRagdollCharacter.LogPrefix} '{name}': no NPCPerception; the NPC will never find targets.", this);

            Character.Died += OnCharacterDied;
            Character.HitReceived += OnHit;
            if (Perception != null)
                Perception.Attacked += OnAttacked;
            HomePosition = Character.Position - Vector3.up * Character.StandingPelvisHeight;
            if (Balance != null)
            {
                Balance.BalanceLost += OnBalanceLost;
                Balance.Stumbled += OnStumbled;
            }

            _started = true;
            ChangeState(Character.IsDead ? NPCStateId.Dead : NPCStateId.Idle, true);
        }

        private void OnDestroy()
        {
            if (Character != null)
            {
                Character.Died -= OnCharacterDied;
                Character.HitReceived -= OnHit;
            }
            if (Perception != null)
                Perception.Attacked -= OnAttacked;
            if (Balance != null)
            {
                Balance.BalanceLost -= OnBalanceLost;
                Balance.Stumbled -= OnStumbled;
            }
        }

        private void Update()
        {
            if (!_started || _current == null)
                return;
            float dt = Time.deltaTime;
            StateTime += dt;
            // Settings may be changed from the inspector at runtime.
            if (_provoked && (_disposition == NPCDisposition.Hostile || !_retaliateWhenAttacked))
                _provoked = false;
            _current.Tick(dt);
        }

        // ------------------------------------------------------------------ Transitions

        private void ChangeState(NPCStateId next, bool force)
        {
            if (!_started)
                return;

            if (_transitioning)
            {
                _hasPending = true;
                _pending = next;
                return;
            }

            if (!force && _current != null && _current.Id == NPCStateId.Dead)
                return;

            _transitioning = true;
            NPCStateId previous = CurrentState;
            _current?.Exit();
            _current = _states[(int)next];
            StateTime = 0f;

            if (HasBalance)
                Balance.DetectionEnabled = next != NPCStateId.Fallen && next != NPCStateId.GettingUp && next != NPCStateId.Dead;

            _current.Enter();
            _transitioning = false;

            // A state that redirected during Enter (e.g. an attack that couldn't start) never really began:
            // don't announce it.
            bool redirected = _hasPending && _pending != next;
            if (!redirected)
            {
                StateChanged?.Invoke(previous, next);
                if (AnimatorBridge != null)
                    AnimatorBridge.SetState((int)next);
                switch (next)
                {
                    case NPCStateId.Attack: _onAttack?.Invoke(); break;
                    case NPCStateId.Stagger: _onStagger?.Invoke(); break;
                    case NPCStateId.Fallen: _onFall?.Invoke(); break;
                    case NPCStateId.GettingUp: _onGetUp?.Invoke(); break;
                    case NPCStateId.Dead: _onDeath?.Invoke(); break;
                }
            }

            if (_hasPending)
            {
                _hasPending = false;
                if (_pending != _current.Id)
                    ChangeState(_pending, false);
            }
        }

        internal void ApplyProfile(in RagdollProfile profile, float blendTime)
        {
            Character.SetProfile(profile, blendTime);
        }

        internal void EnterStagger(float severity, Vector3 direction)
        {
            if (CurrentState == NPCStateId.Stagger)
            {
                ((StaggerState)_current).Extend(severity);
                return;
            }
            _pendingStaggerSeverity = Mathf.Clamp01(severity);
            _pendingStaggerDirection = RagdollMath.Flatten(direction);
            ChangeState(NPCStateId.Stagger);
        }

        // ------------------------------------------------------------------ Event handlers

        private void OnCharacterDied(ActiveRagdollCharacter character)
        {
            ChangeState(NPCStateId.Dead, false);
        }

        private void OnBalanceLost(BalanceLossReason reason)
        {
            NPCStateId s = CurrentState;
            if (s == NPCStateId.Dead || s == NPCStateId.Fallen)
                return;
            if (Holder != null && Holder.IsArmed && UnityEngine.Random.value < Holder.DropOnKnockdownChance)
                Holder.Drop();
            ChangeState(NPCStateId.Fallen);
        }

        private void OnStumbled(float severity, Vector3 direction)
        {
            NPCStateId s = CurrentState;
            if (s == NPCStateId.Idle || s == NPCStateId.Walk || s == NPCStateId.Stagger || s == NPCStateId.Equip
                || ((s == NPCStateId.Attack || s == NPCStateId.Shoot) && severity >= _reactions.interruptAttackSeverity))
                EnterStagger(severity, direction);
        }

        private void OnHit(in RagdollHit hit)
        {
            if (Perception != null && hit.damage > 0f)
                Perception.NotifyAttacked(hit.source);

            NPCStateId s = CurrentState;
            if (s == NPCStateId.Dead || s == NPCStateId.Fallen)
                return;

            if (s == NPCStateId.GettingUp)
            {
                if (hit.severity >= _reactions.getUpInterruptSeverity)
                    ChangeState(NPCStateId.Fallen);
                return;
            }

            bool staggers = s == NPCStateId.Attack || s == NPCStateId.Shoot
                ? hit.severity >= _reactions.interruptAttackSeverity
                : hit.severity >= _reactions.staggerSeverity;
            if (staggers)
                EnterStagger(hit.severity, hit.impulse);
        }

        // ------------------------------------------------------------------ Combat helpers used by states

        internal bool AttackReady => Time.time >= _nextAttackTime;

        /// <summary>Armed with a working gun and the target is in range and in sight.</summary>
        internal bool CanShoot(CombatTarget target, float distance)
        {
            return _weapons.useWeapons && Holder != null && Holder.IsArmed && Holder.Weapon.HasAmmo
                && target != null && Perception != null && Perception.CanSeeTarget && distance <= Holder.Weapon.Range;
        }

        /// <summary>Starts fetching a nearby weapon when that makes sense. Returns true if it switched state.</summary>
        internal bool TryStartEquip()
        {
            if (!_weapons.useWeapons || Holder == null || Holder.IsArmed || Time.time < _nextWeaponSearch)
                return false;
            _nextWeaponSearch = Time.time + 0.5f;

            NPCWeapon weapon = Holder.FindWeapon(_weapons.searchRadius);
            if (weapon == null)
                return false;

            if (ShouldEngage)
            {
                Vector3 from = Character.Position;
                float toEnemy = RagdollMath.Flatten(Perception.Target.CenterPoint - from).magnitude;
                float toWeapon = RagdollMath.Flatten(weapon.GripPosition - from).magnitude;
                if ((Perception.CanSeeTarget && toEnemy < 2.5f) || toWeapon > toEnemy + _weapons.maxDetour)
                    return false; // enemy is right here, or the weapon is out of the way
            }
            else if (!(_weapons.armWhenCalm && _disposition == NPCDisposition.Hostile))
            {
                return false;
            }

            PendingWeapon = weapon;
            ChangeState(NPCStateId.Equip);
            return true;
        }

        internal void SetAttackCooldown(float seconds)
        {
            _nextAttackTime = Time.time + Mathf.Max(0f, seconds);
        }

        internal void SetRandomAttackCooldown()
        {
            SetAttackCooldown(UnityEngine.Random.Range(_attack.cooldownMin, Mathf.Max(_attack.cooldownMin, _attack.cooldownMax)));
        }

        internal bool CanAttack(CombatTarget target, float distance, Vector3 toTarget)
        {
            if (target == null || !AttackReady || !HasProcedural || distance > _attack.attackRange)
                return false;
            if (HasBalance && Balance.State != BalanceState.Balanced)
                return false;
            Vector3 facing = Character.HeadingRotation * Vector3.forward;
            if (Vector3.Angle(facing, toTarget) > _attack.maxAttackAngle)
                return false;
            return Procedural.CanStrikeWith(BodySide.Left) || Procedural.CanStrikeWith(BodySide.Right);
        }

        /// <summary>Weighted random strike from arms that still work, preferring to alternate hands.</summary>
        internal StrikeDefinition ChooseStrike()
        {
            StrikeDefinition[] strikes = _attack.strikes;
            if (strikes == null || strikes.Length == 0 || !HasProcedural)
                return null;

            float total = 0f;
            for (int i = 0; i < strikes.Length; i++)
                total += StrikeWeight(strikes[i]);
            if (total <= 0f)
                return null;

            float pick = UnityEngine.Random.value * total;
            for (int i = 0; i < strikes.Length; i++)
            {
                float w = StrikeWeight(strikes[i]);
                if (w <= 0f) continue;
                if (pick <= w)
                {
                    _lastStrikeSide = strikes[i].side;
                    return strikes[i];
                }
                pick -= w;
            }
            return null;
        }

        private float StrikeWeight(StrikeDefinition s)
        {
            if (s == null || s.weight <= 0f)
                return 0f;
            BodySide side = s.side == BodySide.Center ? BodySide.Right : s.side;
            if (!Procedural.CanStrikeWith(side))
                return 0f;
            if (Holder != null && Holder.IsArmed && Holder.Hand == side)
                return 0f; // that hand is holding a gun
            return s.side == _lastStrikeSide ? s.weight * 0.5f : s.weight;
        }

        internal Vector3 GetAimPoint(CombatTarget target, StrikeDefinition strike)
        {
            Vector3 point = strike != null && !strike.targetHead ? target.CenterPoint : target.AimPoint;
            return point + target.Velocity * _attack.aimLead;
        }

        internal void ExpireCorpse()
        {
            if (_corpse.destroyOnExpire)
                Destroy(gameObject);
            else
                gameObject.SetActive(false);
        }

        private void OnValidate()
        {
            _profiles ??= new StateProfiles();
            _chase ??= new ChaseSettings();
            _attack ??= new AttackSettings();
            _reactions ??= new ReactionSettings();
            _corpse ??= new CorpseSettings();
            _wander ??= new WanderSettings();
            _weapons ??= new WeaponSettings();
            _wander.pauseMax = Mathf.Max(_wander.pauseMax, _wander.pauseMin);
            _attack.cooldownMax = Mathf.Max(_attack.cooldownMax, _attack.cooldownMin);
            _reactions.staggerDurationMax = Mathf.Max(_reactions.staggerDurationMax, _reactions.staggerDurationMin);
        }
    }
}
