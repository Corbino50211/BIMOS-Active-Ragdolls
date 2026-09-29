# Architecture

## Two rigs, one body

```
            animation / procedural pose                         rendering
   ┌──────────────────────────────┐   joint targets   ┌──────────────────────────┐
   │ Animated rig ("puppet")      │ ───────────────▶  │ Ragdoll (physics)         │
   │ SkinnedMesh + Animator       │                   │ Rigidbodies + Configurable│
   │ root follows the pelvis      │ ◀───────────────  │ Joints, never kinematic   │
   └──────────────────────────────┘   simulated pose  └──────────────────────────┘
```

The **animated rig** is what you see. Its root is placed under the physical pelvis every frame, rotated to the
locomotion heading, with no root motion. The Animator and the procedural layer pose it. That pose is captured as
**targets**:

- Each joint gets the child's rotation relative to its parent, for the joint motors.
- The pelvis and chest get world rotations, for the balance controller.
- The pelvis gets a height above the floor, for height support.

Afterwards the **simulated** pose is written back onto the same bones, so the mesh shows the physics.

The ragdoll is a separate, flat hierarchy. That's why moving the animated root never teleports a body, and why
PuppetMaster-style mapping is possible.

## Frame pipeline

`ActiveRagdollCharacter` runs at execution order 1000, after gameplay scripts and before rendering.

| Phase | Work |
|---|---|
| `Update` | Move the animated root under the pelvis (floor height from the balance probe, heading yaw). Reset the mapped bones to their rest pose, so the Animator and procedural layer never read back last frame's physics. |
| *Animator* | Evaluates clips (optional). |
| `LateUpdate` | `ProceduralAnimator.ModifyPose`: pelvis crouch → spine lean/twist/flinch → head look → gait IK → arm IK → strike muscle overrides. **Capture targets.** **Map simulated pose → visual bones** (parents first; skipped for sleeping corpses). |
| `FixedUpdate` | Fault check → profile blend → `Balance.Sense` → `Locomotion.PhysicsStep` → `Balance.ApplyForces` → `JointMotorDriver.PhysicsStep`. |

Everything runs from these three callbacks on one component per NPC, in a fixed order. Subsystems have no
`Update`/`FixedUpdate` of their own. That keeps the order deterministic and the per-NPC callback overhead low.

## Behaviour profiles, not physics switches

A `RagdollProfile` holds **muscle, balance and locomotion strength** plus **gait, guard and look weights**. States
blend between profiles. Nothing ever sets `isKinematic` or disables a collider or joint, so every state stays
fully reactive:

| State | Muscle | Balance | Locomotion | Gait | Guard | Look | Notes |
|---|---|---|---|---|---|---|---|
| Idle | 1.0 | 1.0 | 1.0 | 1 | 0.3 | 1 | relaxed stance |
| Walk | 1.0 | 1.0 | 1.0 | 1 | 0.85 | 1 | chase / search |
| Attack | 1.1 | 1.1 | 1.0 | 1 | 1 | 1 | plus the striking arm's boost and fist pin |
| Stagger | lerp → 0.5 | lerp → 0.75 | lerp → 0.6 | 1 | 0.3 | 0.3 | scaled by hit severity |
| Fallen | 0.18 | 0 | 0 | 0 | 0 | 0 | protective muscle tone |
| GettingUp | ramps 0.18 → 0.9 | ramps 0.1 → 1 | 0.3 | ramps in | 0.2 | 0.5 | crouch height rises to standing |
| Dead | → 0 over 0.35 s | 0 | 0 | 0 | 0 | 0 | limp damping keeps joint friction |

### Transitions

```
Idle ──target seen──▶ Walk ──in range, facing, balanced, cooldown──▶ Attack ──strike done──▶ Walk
  ▲                    │  └──lost target, search expired──▶ Idle        │ (combo: chain another strike)
  │                    │                                                │
  └─── any of Idle/Walk/Attack(severe)/Stagger ── hit severity ≥ threshold or stumble ──▶ Stagger ──▶ Walk/Idle
       any living state ── balance lost (tilt, capture point, low pelvis, airborne, knockdown, legs crippled) ──▶ Fallen
       Fallen ── settled ≥ 0.4 s and down ≥ 1.2 s and legs work ──▶ GettingUp ──upright──▶ Walk/Idle
       GettingUp ── hard hit or failed after 2× duration ──▶ Fallen (3 attempts, then a 3 s rest)
       any ── health ≤ 0, lethal hit, Kill(), fell out of world ──▶ Dead (terminal; only Respawn leaves it)
```

State changes requested while another transition is in progress are queued and applied afterwards, so re-entrant
events (for example `Kill()` raising `Died` inside `DeadState.Enter`) are safe.

## Hit pipeline

```
bullet / blade / explosion / script ──▶ IDamageable.ApplyDamage(DamageInfo)
collision (fist, prop, player hand, wall) ──▶ BodyPart.OnCollisionEnter ── thresholds ──┐
                                                                                         ▼
                                                   BodyPart.Receive
                  ┌─────────────────────────────────────┼───────────────────────────────────┐
     AddForceAtPosition(impulse)            RagdollHealth.ApplyDamage             Part HP → cripple
     (skipped when the solver               (× part × type multipliers,           (muscle ×0.15; legs stop
      already applied it)                    lethal hits, death)                   supporting)
                                                        │
                                   ActiveRagdollCharacter.RegisterHit
            ┌──────────────────────────┬────────────────┴────────────┬──────────────────────────┐
     motors: pain on joint     balance: stagger /          procedural: torso         HitReceived event →
     and its neighbours        knockdown by impulse        flinch spring             state machine (stagger,
                                                                                     aggro on attacker)
```

Outgoing strikes use the same pipeline in reverse. While a strike is active, the striking forearm or fist's
`BodyPart` damages whatever `IDamageable` it touches: the player, another NPC, a prop. Each victim is hit at most
once per strike, and damage scales with contact speed.

## Edge cases handled

| Case | Handling |
|---|---|
| Invalid rig (missing bones or joints, ragdoll inside the visual rig, cycles, duplicate roles) | `Validate` lists every problem (inspector and console). The character disables itself instead of throwing. |
| Leftover colliders or rigidbodies on the visual rig | Disabled at startup with a warning. The builder removes them. |
| Solver explosion (NaN, runaway velocity) | Pelvis checked every step, all bodies every 32 steps. Teleports back to the last valid position in rest pose; after 5 failures in 2 s, kills and deactivates with an error. |
| Falling out of the world | Killed, `On Fell Out Of World` raised, optionally deactivated. |
| Pooling / deactivation | The rest pose is restored before deactivation so joints re-capture correct frames. `Respawn` revives, heals body parts, and resets pain and injuries. |
| Domain reload disabled (Enter Play Mode Options) | Static registries reset in `SubsystemRegistration`. |
| Missing optional components | Each subsystem is optional and null-checked. Missing arms mean no attacks; missing perception means an idle NPC. |
| No NavMesh | Direct steering fallback, per path request. |
| Target destroyed or disabled mid-fight | Perception forgets it; attacks cancel and resume the right behaviour. |
| Missing `ActiveRagdoll` layer | Warned once. Own-collider filtering doesn't depend on layers. |
| BIMOS not installed | The integration assembly isn't compiled (define constraint). The core has no BIMOS dependency. |
| Players spawned after scene load | The discovery hook attaches a `BIMOSPlayerTarget` on demand (rate limited). |
| Blade stuck in a body that dies, despawns or breaks the joint | Extracted; collisions restored after a delay so it doesn't pop out violently. |
| Time scale 0 / zero delta time | Velocity capture and flinch integration skip zero-dt frames. |
| Corpse at rest | Visual mapping freezes, drives aren't touched, the Animator is disabled. Any impulse wakes it instantly. |
