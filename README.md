# BIMOS Active Ragdolls

Physics-driven **active ragdoll NPCs** for Unity 6 VR, designed to sit alongside the
[BIMOS](https://github.com/KadenZombie8/BIMOS) physics player rig (v1.0.0). The NPCs walk, fight and die as
**continuously simulated ragdolls**, in the style of Boneworks/Bonelab:

- **Every limb is a Rigidbody on a ConfigurableJoint.** Joint motors ("muscles") pull each joint toward a pose
  supplied by an animated puppet rig (an Animator, the built-in procedural layer, or both).
- **Balance and walking are physical.** Bounded upright torques and pelvis support keep the body standing.
  Bounded propulsion moves it, gated on the feet actually being on the ground. A BIMOS-style stepping gait places
  the feet from the body's *measured* velocity, and capture-point feedback turns shoves into recovery steps or falls.
- **The body is never kinematic, in any state.** Pushes, punches, bullets, stabs, grabs and explosions move the
  exact body part they hit while the NPC is idle, walking, attacking, staggering, getting up, or dead.
- **Strikes are performed physically.** The procedural layer swings the animated fist, and the muscles (boosted,
  plus a hand pin) make the real fist follow. Damage only happens if the physical fist connects.
- **Death relaxes the muscles.** The body collapses into a persistent corpse that can still be shot, stabbed,
  grabbed and dragged.

It works with **no animation assets**: the procedural layer supplies stance, gait, guard and strikes. Animator
clips can be layered in when you have them.

---

## Requirements

| | |
|---|---|
| Unity | **6000.0+** (uses Unity 6 physics API names such as `Rigidbody.linearVelocity`, like BIMOS 1.0.0) |
| BIMOS | 1.0.0 (optional; enables the integration assembly) |
| AI Navigation | optional; only needed to bake NavMeshes. NPCs steer directly when no NavMesh exists |
| Input System | only for the *Desktop Test Harness* sample (already a BIMOS dependency) |

## Installation

**Package Manager → + → Add package from git URL…**

```
https://github.com/Corbino50211/BIMOS-Active-Ragdolls.git
```

If you imported BIMOS as a `.unitypackage` (into `Assets/`) instead of through the Package Manager, add
`ACTIVE_RAGDOLL_BIMOS` to **Project Settings → Player → Scripting Define Symbols** so the BIMOS integration
assembly compiles. When BIMOS is installed as the `com.kadenzombie8.bimos` package, the define is set
automatically.

## Quick start (about 2 minutes)

1. **Tools → Active Ragdoll → Setup Layers**: creates the `ActiveRagdoll` layer.
2. Either:
   - **Tools → Active Ragdoll → Create Test Arena**: a floor and three capsule-mannequin NPCs, no art needed; or
   - **Tools → Active Ragdoll → Ragdoll Builder**: select any **Humanoid** model in the scene and press **Build**.
3. Put your **BIMOS player** in the scene. The integration automatically adds a `BIMOSPlayerTarget` so NPCs
   can find you. Add `BIMOSPlayerHealth` to the player if you want to take damage.
   No headset? Import the **Desktop Test Harness** sample and use *Create Test Arena* again.
4. Press Play. The NPCs notice you, walk over, put their guard up and throw punches. Shove them, shoot them,
   stab them, grab their arms.

## What's in the package

| Component | Role |
|---|---|
| `ActiveRagdollCharacter` | Hub. Bone mapping, rest pose, target capture, visual mapping, profile blending, the fixed physics order, fault recovery. |
| `JointMotorDriver` | Muscles. Slerp-drive stiffness is derived from each joint's subtree inertia. Handles pain, injury, grab weakness, strike boost and pinning. |
| `BalanceController` | Centre of mass, ground and foot sensing, support factor, capture point. Bounded upright torque and height support. Detects stumbles and balance loss. |
| `LocomotionController` | Desired velocity and facing turned into bounded torso propulsion and heading. |
| `ProceduralAnimator` | Pose layer on the animated rig: stepping gait, guard/relaxed arms, strikes, lean, flinch, head look. |
| `BodyPart` | Per-body hit detection: collision → damage, pain and balance input. Receives weapon damage. Deals strike damage. |
| `RagdollHealth` | Health, damage-type multipliers, lethal hits, death. |
| `NPCStateMachine` | Idle / Walk / Attack / Stagger / Fallen / GettingUp / Dead. |
| `NPCPerception`, `NPCNavigator` | Sight, FOV, line of sight, memory; NavMesh path following with direct-steer fallback and crowd separation. |
| `CombatTarget`, `PlayerHealth` | What NPCs hunt, and a damageable player. |
| `HitscanGun`, `BladeWeapon`, `Ballistics` | Bullets with recoil, stabs that embed and pull out, explosions. |
| `BIMOSPlayerTarget`, `BIMOSPlayerHealth`, `BIMOSRagdollGrabs` | BIMOS integration: target the physics head and pelvis, block with your hands, grab any NPC limb. |
| Editor | Ragdoll Builder (Humanoid → NPC), mannequin generator, layer setup, a validating inspector. |

## Documentation

- [Setup & wiring](Documentation~/Setup.md): automatic and manual rig setup, required components, layers and
  collision, BIMOS player setup, weapon wiring.
- [Physics tuning](Documentation~/PhysicsTuning.md): project settings, joint drive values, balance, locomotion,
  damage thresholds, VR performance budget.
- [Architecture](Documentation~/Architecture.md): frame pipeline, state machine, hit pipeline, and the edge cases
  that are handled.

## Known limitations

- Getting up is an *assisted* physical get-up: balance torque ramps in while the gait steps the feet under the
  hips. It is robust, but it reads more "pulled up" than a mocapped get-up. Feeding a get-up clip through the
  Animator improves it.
- No crawling. An NPC with both legs crippled stays down (alive) until killed.
- No dismemberment.
- One running strike at a time per NPC, with combos chained sequentially.

## Credits

BIMOS by [KadenZombie8](https://github.com/KadenZombie8/BIMOS) (MIT). This package only uses BIMOS's public API.
