# Changelog

## [1.0.0] - 2026-09-29

### Added
- `ActiveRagdollCharacter` hub: separate animated and physics rigs, target capture, visual mapping, profile
  blending, deterministic physics-step order, validation, solver-fault recovery, pooling-safe deactivation.
- `JointMotorDriver`: inertia-scaled slerp-drive muscles with saturation, pain, injury, grab weakness, strike
  boost and pinning.
- `BalanceController`: support sensing, capture-point balance error, bounded upright torque and reach-limited
  height support, stumble and fall detection.
- `LocomotionController`: bounded torso propulsion and heading, gated by foot support.
- `ProceduralAnimator`: BIMOS-style stepping gait with capture-point recovery steps, guard/relaxed arms, physical
  strikes, lean, flinch, head look. `AnimatorParameterBridge` for clip-driven setups.
- Combat: `BodyPart` hit detection, `RagdollHealth`, `PlayerHealth`, `CombatTarget`, `HitscanGun`,
  `BladeWeapon` (stab/embed/extract, slash, blunt), `Ballistics` (hitscan, explosions).
- AI: `NPCStateMachine` (Idle, Walk, Attack, Stagger, Fallen, GettingUp, Dead), `NPCPerception`, `NPCNavigator`.
- BIMOS 1.0.0 integration: `BIMOSPlayerTarget`, `BIMOSPlayerHealth` (hand blocking), `BIMOSRagdollGrabs`,
  automatic player bootstrap.
- Editor: Ragdoll Builder (Humanoid to NPC), capsule mannequin generator, test arena, layer setup, validating
  inspector.
- Sample: Desktop Test Harness.
