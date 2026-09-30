# Setup & Wiring

This guide covers turning a humanoid model into an active-ragdoll NPC (automatically or by hand), the
layer and collision setup, the BIMOS player side, and wiring weapons.

---

## 1. Layers and collision

Run **Tools → Active Ragdoll → Setup Layers** once per project. It creates an `ActiveRagdoll` layer in the
first free user slot.

| Layer | Who is on it | Must collide with |
|---|---|---|
| `ActiveRagdoll` | every NPC ragdoll body and collider (applied at runtime by `ActiveRagdollCharacter`) | everything, **including itself** (NPCs shove each other, corpses pile up) and `BIMOSRig` (your hands and body hit NPCs) |
| `BIMOSRig` | the BIMOS player physics rig (BIMOS's `PhysicsRig.Awake` sets it and ignores `BIMOSRig`↔`BIMOSRig`) | `ActiveRagdoll` |

The default collision matrix already allows all of this. Only change it if your project has disabled
layer pairs.

Collisions *within* one NPC are handled per character, not per layer. Jointed neighbours never collide, and with
`Self Collision = IgnoreNearby` (default depth 2), bodies two or fewer joints apart don't collide either. So thighs
don't fight the pelvis and forearms in the guard don't fight the chest, while hands can still hit the head and
feet can't pass through each other.

Queries use masks built from these layers:

- **Ground probes** (feet, pelvis) use `BalanceController.Ground Mask` minus `Ignore Raycast` and `BIMOSRig`,
  and always skip the NPC's own colliders. NPCs *can* stand on corpses.
- **Line of sight** uses `NPCPerception.Occlusion Mask` minus `Ignore Raycast`. `BIMOSRig` stays included so the
  ray can hit the player.
- **HitscanGun** ignores `BIMOSRig` by default (`Ignore Player Rig`), so you can't shoot your own hands.

If the layer doesn't exist at runtime, a warning is logged once and the authored layers are kept. Everything
still works, because own-collider filtering doesn't depend on layers.

## 2. Automatic setup (recommended)

### From a Humanoid model

1. Import the model with **Rig → Animation Type: Humanoid**.
2. Drag it into the scene, standing in its bind/T/A pose with its feet on the floor.
3. **Tools → Active Ragdoll → Ragdoll Builder**, pick the model, press **Build**.

The builder (fully undoable):

- Creates `<Model> (Active Ragdoll)` with three children:
  ```
  <Model> (Active Ragdoll)      ← all runtime components live here; this transform never moves
  ├─ Visual                     ← animated root, at floor level; moved under the pelvis every frame
  │  └─ <Model>                 ← your model: SkinnedMeshRenderer + Animator (root motion off)
  └─ Ragdoll                    ← flat list of physical bodies (never parented under Visual)
     ├─ Pelvis   (Rigidbody, BodyPart, Collider child)
     ├─ Spine    (… + ConfigurableJoint → Pelvis)
     ├─ Chest    (… → Spine)          Head → Chest
     ├─ LeftUpperArm → Chest,  LeftLowerArm → LeftUpperArm,  LeftHand → LeftLowerArm   (same for Right)
     └─ LeftUpperLeg → Pelvis, LeftLowerLeg → LeftUpperLeg,  LeftFoot → LeftLowerLeg   (same for Right)
  ```
- Sizes colliders from the skeleton: box pelvis/torso, sphere head, capsule limbs, fist capsules, and flat foot
  boxes that sit on the floor.
- Distributes the total mass with standard anthropometric fractions (see *Physics tuning*).
- Authors anatomical joint limits (knees and elbows are hinges; see *Physics tuning*).
- Removes leftover Rigidbodies, Colliders and Joints from the model (such as Unity Ragdoll Wizard output). The
  visual rig must not carry physics.
- Adds and wires: `ActiveRagdollCharacter`, `JointMotorDriver`, `BalanceController`, `LocomotionController`,
  `ProceduralAnimator`, `RagdollHealth`. With *Add AI* on, it also adds `CombatTarget` (team 1, aim = head,
  centre = chest), `NPCPerception`, `NPCNavigator` and `NPCStateMachine`. It adds `AnimatorParameterBridge` if the
  Animator has a controller, and `BIMOSRagdollGrabs` if the BIMOS integration is compiled.

Save the result as a prefab. Prefabs spawn and pool correctly (see *Pooling* below).

### Without any model

**Tools → Active Ragdoll → Create Mannequin NPC** generates a 1.8 m capsule mannequin and builds it.
**Create Test Arena** adds a floor and three mannequins, plus the desktop test player if that sample is imported.

## 3. Manual wiring (custom rigs)

Use this when your ragdoll is hand-made or your rig isn't Humanoid.

**Hierarchy rules** (validated at startup and live in the inspector):

1. The **animated rig** (the visible model) and the **ragdoll** (the bodies) are separate hierarchies. No ragdoll
   Rigidbody may be a descendant of the animated root, because the animated root is moved every frame and would
   teleport the bodies.
2. The **animated root** sits at **floor level** in the rest pose, with its forward axis being the character's
   forward.
3. In the rest pose, each ragdoll body is aligned with its animated bone. Offsets are allowed: they're captured at
   startup and preserved.
4. Every body except the pelvis has a **ConfigurableJoint on itself** whose **Connected Body is its parent body**.
   Linear motion is locked and *Configured In World Space* is off.
5. The joint tree is rooted at the pelvis.

**Required bones:** Pelvis, Spine *or* Chest, Head, and both legs (UpperLeg, LowerLeg, Foot). Arms are optional
but needed for attacks. Hands are optional (without them the fists can ride on the forearms).

**Steps:**

1. Add `ActiveRagdollCharacter` to a root object that contains both the animated rig and the ragdoll root.
2. Add a `BodyPart` to each ragdoll Rigidbody and set its **Role**.
3. Press **Auto Assign Bones** in the `ActiveRagdollCharacter` inspector. It builds the bone list from the
   BodyParts and finds animated targets from the Humanoid avatar, or by name (`Pelvis`, `LeftUpperArm`, …) on
   generic rigs. Check the list and fix any target it couldn't find.
4. Add the subsystems you want (see the component table in the README). Each is optional and disables itself
   cleanly if it can't initialise. Without `JointMotorDriver`, the character is a passive ragdoll.
5. Fix everything the inspector reports as an error (red).

## 4. BIMOS player setup

Nothing is required:

- `BIMOSIntegrationBootstrap` adds a `BIMOSPlayerTarget` (team 0) to every `BIMOS.Player` after each scene load.
  It runs again whenever an NPC finds no hostile targets, which covers players spawned later by BIMOS spawn points.
  Opt out with `BIMOSIntegrationBootstrap.Enabled = false` before the first scene loads, or define
  `ACTIVE_RAGDOLL_NO_BIMOS_BOOTSTRAP`.
- NPCs aim at `PhysicsRig.HeadRigidbody` and measure distance to `PhysicsRig.PelvisRigidbody`, so they track your
  physical body rather than the headset.

Optional:

- Add **`BIMOSPlayerHealth`** to the BIMOS `Player` object so NPC strikes damage you. Hits on your physics hands
  count as **blocked** (×0.2 by default), and head hits hurt more (×1.5). Anything you're holding blocks by
  itself.
- NPC punches also physically shove your BIMOS rig, because both bodies are simulated.

### Grabbing NPCs (`BIMOSRagdollGrabs`)

Every ragdoll collider gets a BIMOS `Grab` (existing Grabs with custom hand poses are kept). BIMOS finds grabs
with an `OverlapBox` on all layers and joins your physics hand to the limb's Rigidbody with a `FixedJoint`.

While you hold a limb, its whole chain drops to `JointMotorDriver.Grabbed Strength` (25%), so you can wrench an
arm or drag the NPC by the head while the rest of it keeps fighting. Grabbing a live NPC also makes it target you.
Corpses stay grabbable. Set **Grabbable When Alive** off for corpse-only grabbing.

## 5. Weapons

All damage flows through `IDamageable.ApplyDamage(in DamageInfo)`. `BodyPart` implements it: it applies the
impulse at the exact hit point, adds pain to the struck joint and its neighbours, feeds balance (stagger or
knockdown), and applies health damage with the part's multiplier.

### Guns: `HitscanGun`

Add it to a BIMOS grabbable gun. Set **Muzzle** to a transform at the barrel tip, pointing forward. On the gun's
BIMOS `Interactable`:

| Interactable event | HitscanGun method |
|---|---|
| `TriggerDownEvent` | `TriggerDown()` |
| `TriggerUpEvent` | `TriggerUp()` |
| *or* `OnTick (float, bool, bool)` | `OnTriggerTick` (dynamic) |

Recoil is an impulse on the gun's own Rigidbody at the muzzle, so your physics hands feel it. `On Hit (point,
normal)` is for impact effects. Custom guns can call `Ballistics.FireHitscan(...)` directly.

### Knives, swords, spears: `BladeWeapon`

**Quick test:** **Tools → Active Ragdoll → Create Test Knife** makes a 30 cm knife with a Rigidbody, a
`BladeWeapon` and, if BIMOS is installed, a BIMOS `Grab` on the handle. The Test Arena includes one. Without a
headset, the Desktop Test Harness can throw knives (**V**) and twist (**Q**) or pull (**E**) the one you're
looking at.

**Your own knife:** put `BladeWeapon` on the knife's Rigidbody (or ArticulationBody) GameObject, the object BIMOS
grabs. Then:

- Create a child **Tip** transform at the point, and set **Blade Axis** (local, handle → tip) and **Blade Length**
  (tip to guard; the blade can go in this far).
- List the **Blade Colliders** (the sharp part). Colliders you leave out, such as the guard and handle, keep
  colliding, so the guard physically stops the blade at the hilt. Slashes only count on blade colliders.
- Give the Rigidbody **Continuous Dynamic** collision detection so fast stabs and throws don't tunnel.
- Add a BIMOS grab to the handle. `BIMOSBlade` is added automatically at runtime.

#### How stabbing works

1. **Getting in.** Only the tip stabs: the contact must be within *Tip Radius* of the tip. The blade must also
   meet the surface within *Max Surface Angle* (65°) of head-on, or it glances off. Then either:
   - it's moving along its own axis (within *Max Stab Angle*, 35°) faster than the surface's *Min Stab Speed*,
     which is a stab or a throw; or
   - it's pushed steadily along its axis harder than *Min Press Force*, which is leaning on the handle. This
     lets you slowly push a knife into a pinned NPC.

   The entry hole is where the blade line crosses the surface. The blade keeps *Entry Speed Kept* of its speed
   and carries on in until friction stops it.
2. **Staying in.** A ConfigurableJoint keeps the blade on the line of the wound. The blade can slide along the
   line (between just outside the hole and the guard), spin about it, and lever a few degrees (*Swing Limit*),
   but can't move sideways. The joint's drives are the friction: huge dampers capped at *Slide Friction* (N) and
   *Twist Friction* (N·m). The solver holds the blade dead still until something pushes, pulls or turns it harder
   than that, then lets it slide. Let go and it stays in. A shallow blade is held with only a quarter of the
   friction, a fully buried one with all of it.
3. **Working it.**
   - **Push deeper:** cuts new tissue, *Cut Damage Per Meter*.
   - **Saw back and forth:** *Saw Damage Per Meter*.
   - **Twist or lever:** *Twist Damage Per Degree* plus pain. A quarter turn hurts a lot, and the NPC's limb
     goes limp from the pain.

   Twisting and levering also **loosen the wound** (*Wound Widening*). Friction drops and the blade gets more
   side-to-side play, so a worked knife comes out more easily. Slow jitter while the NPC moves around with a
   knife in it doesn't count.
4. **Holding it.** While a BIMOS hand holds a blade stuck in a live NPC, that limb goes weak like a grabbed limb.
   You can drag the NPC by the handle, twist its arm with the knife, or pin it. Wounds are blamed on you, so the
   NPC aggroes on you. The controller buzzes: a thump going in, a grinding rumble while the blade slides or
   twists, a pop coming out. Assign optional stab and extract sounds on `BIMOSBlade`.
5. **Getting out.** Pull the tip back out past the entry hole and it comes free. Collisions with that body come
   back once the blade is clear, so it isn't shoved out violently. Wrench it sideways past *Break Force* and it
   tears out. It also comes out if the target is destroyed or disabled, and `Extract()` forces it out.

Blades stick into ragdolls with **Flesh Material**, and into anything that isn't damageable with **World
Material** (wood-like: needs a real stab, holds about 450 N, enough to hang from). Put a **Stabbable Surface** on
an object to give it its own material, for example *Penetrable* off for metal. Blades never stick into the player
(they only wound) or into BIMOS rig colliders. *Stick In World* turns off sticking into the environment.

For VFX and SFX hooks: `Embedded`, `Extracted` (with an `ExtractReason`) and `Feedback` events, the Unity
events, `Current` (the `Impalement`: depth, twist, looseness, held), and `Impalement.Active` / `CountIn(character)`.
Any holder can implement `IBladeWielder` to get the same hold, blame and feedback behaviour as `BIMOSBlade`.

**Tuning feel:**
- Blade comes out too easily when you let go → raise *Slide Friction*.
- Too hard to twist → lower *Twist Friction*. BIMOS hands are strong; the default 1.5 N·m is a firm wrist turn.
- Hard to get in → lower *Min Stab Speed* or *Min Press Force*.
- The NPC flails when you drag it → lower `JointMotorDriver → Grabbed Strength`, the same setting as hand grabs.

### Explosions

`Ballistics.Explode(center, radius, maxDamage, maxVelocityChange, layerMask, source)` kicks every body in range
(mass-scaled, with distance falloff). Each ragdoll character takes damage only once.

### Anything else

Any Rigidbody that hits a body part hard enough deals blunt damage from the collision impulse: a thrown brick, a
swung pipe, your fist (see `RagdollHealth → Impact Damage`). Static geometry only hurts on violent impacts, such as
being thrown into a wall, never on normal falls. To give a weapon its own damage rules, implement
`IImpactDamageDealer` on its Rigidbody (body parts then skip their generic impact damage) and call
`ApplyDamage` yourself.

### Blood (`RagdollBlood`)

Every NPC bleeds automatically. There's nothing to set up and no assets are needed: the particles and splat
textures are generated at runtime and rendered with `Sprites/Default`, which works in both URP and the built-in
pipeline.

- **Shot:** a spray back out of the entry hole, a bigger one out of the exit wound (found by tracing through the
  body part), then the entry hole pumps blood in time with the heartbeat for about 7 s.
- **Stabbed:** a spurt on the way in. While the knife is in, blood seeps round the blade, more as you twist the
  wound open. When the knife comes out, the wound gushes and keeps bleeding.
- **Slashed:** a spray along the cut.
- After death, wounds slow to a seep over 4 s. Corpses still bleed when shot or stabbed (*Corpses Bleed*).
- Droplets that land leave blood splats on floors, walls and props. Up to 80 exist at once; the oldest is
  reused. Splats on moving props follow the prop.

To change the look, add **Ragdoll Blood** to the NPC yourself. It has settings for colour, amount (0 = none),
droplet size, which weapon types bleed, bleed time and heart rate. For global control:

- `RagdollBlood.AutoAdd = false` turns automatic blood off.
- `BloodFX.ParticleMaterial` and `BloodFX.SplatMaterial` let you use your own materials.
- `BloodFX.MaxSplats` and `BloodFX.SplatChance` limit the splats.
- `BloodFX.Spray(...)` emits blood from your own scripts.

Everything is one pooled particle system emitted from code, so heavy bleeding doesn't instantiate or allocate
anything, which keeps it light enough for Quest.

### Broken bones (`BoneBreaking`)

Every NPC gets breakable bones automatically: upper and lower arms, upper and lower legs, and the neck.

**What breaks a bone:**
- **A big blow:** one that changes the bone's speed by *Impact Break Speed* (11 m/s). A hard punch is about 4;
  being thrown into a wall or landing badly from a height is about 15. The neck is 1.6× tougher.
- **Being forced past the joint limit:** more than *Overextension* (12°) for a few physics steps. For example,
  wrenching an arm with a BIMOS hand, or kicking a knee sideways.
- **Bullets:** a *Bullet Break Chance* (15%) per arm or leg hit.

**What a broken bone does:**
- **Floppy:** the joint's muscle gives out (*Broken Strength* 0) and its limits open by *Extra Limit* (60°), so the
  limb dangles and bends the wrong way.
- **Broken leg:** the leg can barely hold weight, so the NPC goes down.
- **Broken arm:** the arm can't punch or hold a gun, and a gun in that hand is dropped.
- **Broken neck:** the head lolls, and the NPC dies if *Broken Neck Kills* is on.
- **Feedback:** a crack sound (generated, or your own *Snap Sounds*) and a flinch.
- **Healing:** everything heals on `Revive`.

Add **Bone Breaking** to an NPC to change the settings. Use `BoneBreaking.AutoAdd = false` to turn it off
everywhere. Break bones from scripts with `Break(BoneRole)`, and react with the `BoneBroken` event.

## 5b. NPCs using guns

NPCs can pick up and fire any `NPCWeapon`. That includes `HitscanGun` and, after a one-click install, the
**BIMOS demo Pistol**.

**BIMOS demo pistol:** when the BIMOS Demo sample is in the project, Unity asks once to *Install BIMOS demo weapon
support*. You can also do it later from **Tools → Active Ragdoll → Install BIMOS Demo Weapon Support**. It
generates two scripts in `Assets/ActiveRagdoll Generated/BIMOS Demo Weapons/`, because a package can't reference
imported samples directly. The scripts:

- give every demo `Pistol` an NPC adapter automatically. NPC shots go through the pistol's own `Fire()`, so you get
  its sound, slide recoil, muzzle flash, casings, bullet holes and knock-back.
- make **your** demo-pistol shots damage NPCs. Without this the demo pistol only pushes them, because it damages
  through the samples' own `IDamageable`. Damage is 10 × `BIMOSDemoDamageBridge.DamageScale` (3), then body-part
  multipliers apply.
- make NPC shots damage you, if the player has `BIMOSPlayerHealth` or `PlayerHealth`.

BIMOS sockets only accept a magazine that a hand is holding, so NPCs can't reload. NPC-held demo pistols have
infinite ammo by default. Set `BIMOSDemoPistolWeapon.InfiniteAmmoForNPCs = false` to make them fire only chambered
rounds and then drop the empty gun.

**Behaviour** (`NPCStateMachine → Weapons`):

- Hostile NPCs arm themselves with the nearest reachable weapon within *Search Radius* (12 m), even before they've
  seen you (*Arm When Calm*).
- In a fight they only detour for a weapon if it's no more than *Max Detour* (3 m) further than you. If you're
  within 2.5 m, they fight with fists instead.
- **Equip:** walk to the weapon at full speed. Within *Pickup Range* (1.5 m) it snaps into the hand, held by a
  FixedJoint to the gun's Rigidbody or ArticulationBody.
- **Shoot:** the arm muscles point the physical gun at you, and the NPC fires steadily at the gun's *NPC Fire
  Interval*, with no bursts, pauses or reloads. It waits *First Shot Delay* (0.4 s) to react first. **Aim
  assist:** each shot has *Hit Chance* (50%) of going straight at your body; the rest pass about *Miss Distance*
  (0.8 m) wide. Shots only fire while the real gun points within *Max Aim Error* (40°) of you, so bullets never
  leave the side of the barrel. It backs off if you get within 1.5 m, and moves to regain line of sight.
- **Disarm:** grab the gun in an NPC's hand and it lets go after 0.3 s. NPCs drop guns when they die, and sometimes
  when knocked down (35%).

**Your own guns:** add `HitscanGun` (or derive from `NPCWeapon` and implement `NPCFire`). Set **Muzzle** and
optionally **Grip** (where the palm goes; defaults to a child named `Grip`), plus Range and NPC Fire Interval. Disable *Use Weapons* on an NPC to keep it fists-only.

## 6. Disposition (Idle / Wander / Hostile)

Set on `NPCStateMachine` (also settable from code: `Disposition`, `RetaliateWhenAttacked`):

| Disposition | Behaviour |
|---|---|
| **Idle** | Stands in place and looks at people it can see. |
| **Wander** | Strolls between random points within *Wander → Radius* of where it spawned, pausing 2–6 s at each. Uses the NavMesh when there is one. |
| **Hostile** | Hunts any hostile it perceives: chases, then attacks when in range. The default. |

**Hostile When Attacked** (Idle/Wander only):

- **On**: being hurt or grabbed provokes the NPC. It fights back like a Hostile NPC, then calms down and goes back
  to idling or wandering once it has lost track of its attacker (perception memory plus search time).
- **Off**: it never fights. Hits still stagger it or knock it down physically, and afterwards it gets up and
  carries on with what it was doing.

## 7. Navigation

`NPCNavigator` steers in three layers:

1. **NavMesh path.** Bake a NavMesh with a `NavMeshSurface` (AI Navigation package). Start and goal are sampled
   from the floor under the NPC and the target within *Sample Distance* (3 m). So if you stand in the clearance
   hole next to a table, the NPC still gets a path to the nearest reachable point and then closes in. An NPC
   knocked off the mesh walks back onto it first.
2. **Local avoidance.** Hip- and knee-height sphere probes (*Probe Distance* 1.2 m) steer around anything the
   NavMesh doesn't know about: props that have been moved, or everything when no NavMesh exists. The current
   target, other characters (crowd separation handles those) and dynamic props under *Min Obstacle Mass* (5 kg)
   are ignored, so NPCs shove small props aside.
3. **Stuck recovery.** With no progress (*Stuck Distance* 0.3 m in *Stuck Time* 1.5 s), the NPC detours toward the
   clearer side for 1.2 s and repaths.

**Movable props** (BIMOS tables, crates) baked into a static NavMesh leave a stale hole when pushed. Give heavy
props a `NavMeshObstacle` with *Carve* enabled so the mesh follows them. Local avoidance covers the gap until it
updates. Select an NPC in play mode to see its path (blue), its current corner, and a magenta ring while it
detours.

## 8. Animation clips (optional)

The procedural layer works without clips. To add animation:

1. Give the model's Animator a controller. `AnimatorParameterBridge` (added by the builder) feeds these parameters
   when they exist: `Speed`, `Forward`, `Strafe` (float), `Grounded`, `Dead` (bool), `State` (int, `NPCStateId`),
   `Strike` (trigger).
2. On `ProceduralAnimator`, turn off the layers your clips replace:
   - **Procedural Legs** off: a walk blend tree on `Forward`/`Strafe` drives the legs instead. Feet may slide a
     little more than with the stepping gait.
   - **Procedural Arms** off: arm animation drives the arms. Strikes still boost the muscles and pin the fist.
3. The Animator's update mode is forced to *Normal*, and root motion is forced off. The physical body is the only
   thing that moves the character.

## 9. Spawning, pooling and despawning

- **Spawn** prefabs normally. Joints capture their reference pose on `Awake`, so the prefab must be saved in its
  rest pose (the builder does this).
- **Respawn/pool:** `NPCStateMachine.Respawn(groundPosition, facing)` revives and stands the NPC up in its rest
  pose. `ActiveRagdollCharacter.Teleport(...)` moves it without reviving.
- **Deactivating** an NPC restores its rest pose first. ConfigurableJoints re-capture their reference frame on
  re-activation, and this stops a pooled corpse from coming back with twisted joints.
- **Corpse lifetime:** `NPCStateMachine → Corpse → Lifetime` (0 = forever). *Destroy On Expire* off deactivates
  instead, for pooling.
