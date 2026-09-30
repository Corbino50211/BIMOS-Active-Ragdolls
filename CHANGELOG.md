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
  `BladeWeapon` (stab, slash, blunt), `Ballistics` (hitscan, explosions).
- Knife stabbing (`BladeWeapon`, `Impalement`, `StabMaterial`, `StabbableSurface`): blades stick into NPCs, the
  world and props by speed or steady pressure. They stay in when let go, slide deeper and back out against
  friction, and twist and lever in the wound, which loosens it and causes damage and pain. They drag whatever
  they're stuck in and tear out when wrenched sideways. Works with Rigidbody and ArticulationBody blades.
  `BIMOSBlade` (auto-added to grabbable blades) weakens the stabbed limb while held, blames wounds on the player
  and adds controller haptics.
- Blood (`RagdollBlood`, `BloodFX`): asset-free, pooled blood for bullets, stabs and slashes. Entry and exit
  sprays, heartbeat-pumped wounds, seeping around stuck blades, a gush on extraction, and splats where droplets
  land. Added to every NPC automatically.
- Bone breaking (`BoneBreaking`): arms, legs and neck snap from big blows, overextension past the joint limit
  or bullets. Broken joints go floppy (no muscle, wider limits); broken legs drop the NPC, broken arms drop the gun,
  and a broken neck kills. Generated crack sound. Added to every NPC automatically.
- AI: `NPCStateMachine` (Idle, Walk, Attack, Stagger, Fallen, GettingUp, Dead), `NPCPerception`, `NPCNavigator`.
- BIMOS 1.0.0 integration: `BIMOSPlayerTarget`, `BIMOSPlayerHealth` (hand blocking), `BIMOSRagdollGrabs`,
  automatic player bootstrap.
- Editor: Ragdoll Builder (Humanoid to NPC), capsule mannequin generator, test arena, layer setup, validating
  inspector.
- Sample: Desktop Test Harness.
