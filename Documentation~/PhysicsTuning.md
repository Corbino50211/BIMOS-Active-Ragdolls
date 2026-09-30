# Physics Tuning

All defaults below are what the code ships with. They're tuned for BIMOS's **144 Hz** fixed step and stay stable
down to **72 Hz**. Values marked *per kg* or *per 70 kg* scale automatically with the character's mass, and joint
stiffness scales with each joint's inertia, so the same settings work for a 50 kg or a 120 kg NPC.

---

## 1. Project settings

| Setting | Recommended | Why |
|---|---|---|
| **Time → Fixed Timestep** | 1/144 (BIMOS's `Player.Awake` forces this) | All PD controllers keep ω·dt ≤ 0.2 even at 1/72, so 72–144 Hz is safe. Below 60 Hz, lower the frequencies below. |
| **Physics → Default Solver Iterations** | leave at 6 | Ragdoll bodies override it per body (12 position / 4 velocity). BIMOS's hands use 60/10 on their own. |
| **Physics → Solver Type** | Projected Gauss Seidel (default); Temporal Gauss Seidel is worth trying | TGS handles stiff joint chains and big mass ratios better, but it changes BIMOS's feel too, so test both. |
| **Physics → Default Max Angular Speed** | any (overridden per body to 40 rad/s) | Unity's historic default of 7 rad/s makes limbs feel sluggish and punches weak. |
| **Physics → Reuse Collision Callbacks** | **on** (default) | Body parts use `OnCollisionEnter`; reuse avoids garbage allocations. |
| **Physics → Auto Sync Transforms** | off (default) | Teleports write both `Rigidbody.position` and the transform. |
| **Physics → Sleep Threshold** | 0.005 (default) | Corpses fall asleep and their visuals freeze (see performance). |
| **Layers** | `ActiveRagdoll` collides with everything, itself included | See *Setup → Layers*. |

## 2. Rigidbodies (`ActiveRagdollCharacter → Physics`)

Enforced on every ragdoll body at startup.

| Field | Default | Notes |
|---|---|---|
| Solver Iterations | 12 | Chains of driven joints need more than 6. Raise to 16–20 if limbs jitter under heavy load. |
| Solver Velocity Iterations | 4 | Reduces bounce on hard impacts. |
| Max Angular Velocity | 40 rad/s | |
| Max Depenetration Velocity | 4 m/s | Stops overlapping spawns or piles from exploding. |
| Linear / Angular Damping | 0.02 / 0.3 | Mild angular damping keeps limp limbs from spinning forever. |
| Interpolation | Interpolate | Smooth rendering when the display rate (90 Hz) ≠ the physics rate (144 Hz). |
| Fast Segment Collision Detection | Continuous Speculative | Hands, forearms and feet, so strikes don't tunnel. |
| Adaptive CCD Speed | 6 m/s | Faster bodies switch to speculative CCD until they slow below half this, so flung ragdolls don't pass through thin walls or tables. 0 = off. |
| Min Inertia Radius | 0.03 m | Floors each body's inertia at `mass × r²`. Very small inertias (hands, feet) jitter under strong drives. 0 = keep Unity's values. |
| Corpse Sleep Threshold | 0.03 | Dead bodies go to sleep sooner, so they settle instead of twitching on the floor. They still wake when touched or grabbed. |
| Self Collision | IgnoreNearby, depth 2 | See *Setup → Layers*. |

### Mass distribution (builder)

Fractions of total mass (default 70 kg), after Winter's anthropometric tables:

| Pelvis | Spine | Chest | Head | Upper arm | Forearm | Hand | Thigh | Shin | Foot |
|---|---|---|---|---|---|---|---|---|---|
| 14.2% | 13.9% | 21.6% | 8.1% | 2.8% | 1.6% | 0.6% | 10.0% | 4.65% | 1.45% |

Without a chest body, the spine takes the chest's share. Without hand bodies, the forearms take the hands'
share. The largest ratio between adjacent bodies is about 3:1, well inside what PhysX handles (keep it under
10:1 on custom rigs).

### Joint limits (builder)

X is the ConfigurableJoint's primary axis. The sign convention: rotating a bone direction D toward M about
cross(D, M) is a **negative** X angle (the same convention Unity's own Ragdoll Wizard uses for knees and hips).

| Joint | Axis | Low X | High X | Y swing | Z swing | Notes |
|---|---|---|---|---|---|---|
| Spine → pelvis | right | −40 (bend forward) | +25 | 25 (twist) | 20 (side bend) | |
| Chest → spine | right | −30 | +20 | 20 | 15 | |
| Head → chest | right | −50 (nod) | +40 | 70 (turn) | 35 (tilt) | |
| Hip | right | −30 (extension) | +110 (flexion) | 30 (twist) | 40 (abduction) | |
| Knee | right | −145 (flexion) | +3 | locked | locked | hinge |
| Ankle | right | −45 (toe down) | +30 (toe up) | 15 | 20 | |
| Shoulder | cross(arm, forward) | −100 (raise forward) | +40 | 95 (raise/lower) | 60 (twist) | |
| Elbow | cross(forearm, forward) | −145 (flexion) | +3 | locked | locked | hinge |
| Wrist | cross(hand, forward) | −70 | +70 | 30 | 25 | |

## 3. Muscles (`JointMotorDriver`)

Every joint uses a **Slerp drive**. For each joint, the driver computes the inertia `I` of everything the joint
moves (the subtree's point masses about the joint anchor, plus each body's own rotational inertia), then:

```
spring   k = strength · I · ω²
damper   d = 2ζ · √(k·I)  +  limpDamping · I
maxForce   = k · saturationAngle + d · 10 rad/s
strength   = profile.muscleStrength × bone multiplier × injury × strike boost × (1 − pain·0.85) × (grabbed ? 0.25 : 1)
```

`ω` sets how snappy a limb is, `ζ` sets how much it overshoots, and the **saturation angle** is the pose error at
which the muscle stops getting stronger. That last value is what makes NPCs pushable: past it, a shove simply wins.

| Group | ω (rad/s) | ζ | Saturation | Approx. values, 70 kg mannequin |
|---|---|---|---|---|
| Torso | 22 | 0.9 | 60° | spine k ≈ 2100 N·m/rad, d ≈ 170 |
| Head | 16 | 0.9 | 45° | k ≈ 15 |
| Arm | 18 | 0.85 | 50° | shoulder k ≈ 130, d ≈ 12; ~5° sag holding a guard |
| Hand | 12 | 0.8 | 40° | |
| Leg | 30 | 0.9 | 60° | hip k ≈ 2250, knee k ≈ 340 |
| Foot | 24 | 0.9 | 45° | |

**Strength changes are rate-limited:** a muscle loses strength at up to 20/s, so hits and grabs take effect at once. It
regains strength at up to 4/s (*Strength Recovery Rate*), so a released limb eases back into pose over about a
quarter second instead of snapping. The strike boost is applied on top, unsmoothed, so punches stay instant.

*Limp damping* 1.5 × I gives dead limbs joint friction, so corpses don't wobble like jelly. Drives are re-sent to
PhysX only when they change by more than 2%.

**Pain** (the hit reactions that make the NPCs feel like Boneworks NPCs): each hit adds
`0.025 × impulse (N·s) + 0.02 × damage` to the struck joint, with 45% spreading to its neighbours. Pain recovers
at 0.9 per second, and at full pain a joint keeps 15% of its strength. A solid punch visibly knocks a limb or head
aside, and it recovers over about a second.

**Strike assist**: during the strike phase, the striking arm's muscles are multiplied by the strike's *Muscle
Boost* (2.2–3.0). The fist is also *pinned* toward its animated position with a 14 rad/s critically damped
acceleration, capped at 80 m/s² × pin weight. That's enough to land the punch, and not enough to stop a collision
from deflecting it.

## 4. Balance (`BalanceController`)

| Field | Default | Effect |
|---|---|---|
| Upright Frequency / Damping | 10 rad/s / 1.0 | PD torque on pelvis and chest toward the animated torso orientation. It also turns the body, with yaw at ×0.6. |
| Max Upright Torque Per Kg | 9 N·m/kg | **The cap a shove has to overcome.** Raise it for sturdier NPCs. |
| Chest Torque Share | 0.5 | Half on the chest, so a heavy upper body doesn't fold over the pelvis. |
| Gravity Compensation | 0.85 | Fraction of weight carried by the pelvis support. The legs' muscles carry the rest, which keeps the feet planted. |
| Height Frequency / Damping | 9 rad/s / 1.0 | Pelvis height PD toward the animated height, **capped by what the planted legs can reach**. |
| Max Support Ratio | 1.6 × weight | |
| Foot Ground Tolerance | 0.1 m | A foot within this distance of the ground counts as planted. |
| Support rise / fall | 10 /s, 5 /s | Support factor smoothing. Every balance and propulsion force is scaled by it. |

### Balance-loss detection

The balance error is `0.5 × (CoM distance outside the feet) + (CoM velocity − commanded velocity) × √(h/g)`. That
is a capture-point error that ignores motion the NPC is doing on purpose.

| Trigger | Default | Result |
|---|---|---|
| Balance error > Stumble Distance | 0.35 m | Stagger, plus recovery steps toward the capture point |
| Balance error > Fall Distance (≥ 50 ms) | 1.05 m | Fall |
| Chest tilt > Fall Tilt Angle (≥ 0.12 s) | 55° | Fall |
| Pelvis < 55% of standing height (≥ 0.35 s) | | Fall |
| No foot on the ground (≥ 0.75 s) | | Fall (thrown, dropped off a ledge) |
| Single impulse ≥ Stagger Impulse | 16 N·s per 70 kg | Stagger |
| Single impulse ≥ Knockdown Impulse | 60 N·s per 70 kg | Fall immediately. Leg hits count ×1.4 (sweeps). |
| Both legs crippled | | Fall; can't get up |

## 5. Locomotion (`LocomotionController`)

| Field | Default | Notes |
|---|---|---|
| Walk / Run Speed | 1.3 / 3.2 m/s | Run is used beyond `Chase → Run Distance` (6 m). A crippled leg caps speed. |
| Velocity Gain | 6 /s | Velocity error to acceleration. |
| Max Acceleration / Deceleration | 5 / 7 m/s² | Force caps. The total force is spread over pelvis, spine and chest by mass, and scaled by support. |
| Setpoint Acceleration / Deceleration | 2.5 / 3.5 m/s² | How fast the commanded velocity ramps. Tracking lag (ramp ÷ gain) feeds the balance error, so keep ramp ÷ gain ≲ 0.6 m/s or walking starts will read as stumbles. |
| Turn Speed | 240°/s | Heading turn rate. The upright torque makes the body follow. |
| Max Heading Lead | 100° | How far the heading may lead the body. |
| Propel Through Center Of Mass | 1 | The force is pushed on the torso, which sits above the centre of mass, so on its own it pitches the NPC forward and the legs trail. A matching counter-torque cancels that turning, so the push moves the body without tipping it. 0 = the old forward-tipping push. |

## 6. Gait (`ProceduralAnimator → Gait`)

This follows BIMOS's `Feet` stepping model: feet stay planted in the world and step, one at a time, along a
quadratic-Bezier arc when they fall too far from a target that leads the measured velocity. Recovery steps happen
because the target shifts toward the capture point.

**Walk motion** (`ProceduralAnimator → Walk Motion`) is what stops the walk looking stiff. All of it scales with
walking speed, so a standing NPC stays still.

- **Arm swing (0.5):** each arm swings opposite its same-side leg, capped at 0.3 arm lengths. 25% of the swing is
  kept with the guard up.
- **Hip bob (0.03 m):** the hips are lowest with the feet furthest apart and rise over the planted foot.
- **Hip sway (0.025 m):** the hips shift over the planted foot while the other foot swings.
- **Hip twist (7°):** the forward leg's hip leads, and the chest turns back 1.4× as much, so the shoulders
  counter-rotate.
- **Heel-to-toe roll:** the toes point down 18° as the foot pushes off, lift 12° as the heel strikes, then roll
  flat.

Set any of them to 0 to remove that motion.

**Foot pins:** joint muscles alone can't swing a foot forward fast enough against the body's momentum, so the
toes catch the ground and the legs trail behind on tiptoe. The gait therefore pulls each physical foot toward its
target, using the same capped pin as strike assist (14 rad/s, at most 80 m/s² × weight). The pull is firm while the
foot swings, with the shin brought through too, and light while it's planted, so shoves still slide it. Pins are off
while airborne or once balance is lost. Set both to 0 for pure joint-driven legs.

**Stride:** feet land ahead of the hips by *Lead Time + Overshoot Time* of the measured velocity. They then stay
planted until the body has carried them *Stride Symmetry* × as far behind, so the step threshold grows with speed.
At walking speed (1.3 m/s) a foot lands about 0.39 m ahead, lifts about 0.33 m behind, and the stride is about 1.1 m.
That's a heel-to-toe walk rather than a shuffle under the body. Setting *Overshoot Time* and *Stride Symmetry* to 0
restores the old gait exactly. If the NPC over-strides and stumbles, lower *Overshoot Time*; if it still shuffles,
raise it.

| Field | Default |
|---|---|
| Idle / moving step threshold | 0.10 / 0.20 m (halved at full urgency) |
| Slow / fast step duration | 0.34 / 0.20 s |
| Step height | 0.07 m + 0.035 × speed (max 0.18) |
| Lead time | 0.18 s of velocity |
| Overshoot time | +0.10 s of velocity (capped at 0.5 × leg length) |
| Stride symmetry | 0.85 |
| Swing / planted foot pin | 0.8 / 0.25 (shin 0.4 × swing while swinging) |
| Capture point gain | 0.8 |
| Max step reach | 0.75 × leg length |
| Max step up/down | 0.35 m |

## 7. Damage thresholds (`RagdollHealth → Impact Damage`, `BodyPart`)

| | Min speed | Min impulse | Damage per N·s |
|---|---|---|---|
| Moving bodies (fists, props, player hands) | 2.5 m/s | 3 N·s | 1.2 |
| Static environment | 7 m/s | 40 N·s | 0.5 |

Non-damaging body contacts above 2 N·s still cause pain and flinch.

| Part | Damage × | Part HP (crippled at 0) | Other |
|---|---|---|---|
| Head | 2.5 | – | a single hit of ≥ 60 applied damage kills |
| Chest / spine | 1.0 | – | |
| Pelvis | 0.9 | – | |
| Arms | 0.6 | 60 | crippled arm: 15% strength, can't strike |
| Legs | 0.7 | 70 | crippled leg: no support from it; both = down for good |
| Hands / feet | 0.4 | – | |

Damage-type multipliers: stab ×1.2, everything else ×1.

**If your punches feel too weak or too strong:** BIMOS hand impulses depend on your hand joint settings. Watch
`BalanceController → Balance Error` and the health readout in the `ActiveRagdollCharacter` inspector during play,
then adjust *Min Body Impulse* and *Body Damage Per Impulse*.

## 8. VR performance

Per NPC, per physics step: 13–16 bodies and 12–15 joints, 3 short raycasts (pelvis and two feet), plus centre-of-
mass and drive bookkeeping. Drives are only re-sent when they change. Per frame: the procedural IK on about 10
transforms, plus at most 2 ground probes per step taken. Perception runs at 5 Hz with a random phase per NPC.
Steady-state garbage allocation is zero: `NonAlloc` queries, cached arrays, no LINQ.

Corpses are cheap. Once every body sleeps, visual mapping freezes, drives aren't touched, and the Animator is
disabled.

These budgets are **rough starting points to profile against**, not measurements:

| Target | Active (alive) NPCs to start from |
|---|---|
| PC VR | 8–12 |
| Quest 3 / Quest Pro | 3–5 |
| Quest 2 | 2–4 |

Levers if you need more NPCs: build without hands (−2 bodies) or chest (−1); drop solver iterations to 8/2; set a
corpse lifetime; keep far-away NPCs deactivated until they're needed (deactivation is pooling-safe).

## 9. Troubleshooting

| Symptom | Adjust |
|---|---|
| NPC stiff or "robotic", hard to shove | Lower `Arm`/`Torso` frequency or saturation angle, or `Max Upright Torque Per Kg`. |
| Falls over from light touches | Raise `Max Upright Torque Per Kg`, `Stumble/Fall Distance`, `Knockdown Impulse`. |
| Stutters into stagger while walking | Raise `Stumble Distance`, or lower `Setpoint Acceleration` relative to `Velocity Gain`. |
| Feet slide / skate | Lower `Lead Time`, raise foot friction (high-friction PhysicsMaterial on the foot colliders), lower `Gravity Compensation` slightly so the feet bear more weight. |
| Floaty or bouncy walk | Lower `Gravity Compensation` (0.75–0.8) and `Height Frequency`. |
| Limbs jitter at rest | Raise solver iterations, check adjacent mass ratios, check for overlapping colliders between non-adjacent bodies. |
| Punches don't land | Raise the strike's `Muscle Boost` and `Hand Pin Weight`, and the `Attack Range` / `Lunge Speed`. |
| Gets up badly | Lengthen `Get Up Duration`, raise `Muscle` in the GettingUp profile, or drive a get-up clip through the Animator. |
| Console: "simulation keeps failing" | A joint limit, mass ratio or overlapping spawn is exploding the solver. The character resets itself up to 5 times in 2 s, then deactivates and logs this error. |
