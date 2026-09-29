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

Add it to the blade's Rigidbody (the BIMOS grabbable). Then:

- Create a child **Tip** transform at the point, and set **Blade Axis** (local, handle → tip) and **Blade Length**.
- Optionally list the **Blade Colliders** (the edge) so slashes only count on the edge.

Behaviour:

- **Stab**: the tip leads at ≥ 1.6 m/s within 35° of the blade axis. Deals stab damage plus a per-speed bonus.
  If *Embed* is on, a sliding joint holds the blade in the body with friction. It travels deeper when pushed, can
  drag the NPC around, pulls out when withdrawn, and tears out above *Embed Break Force*.
- **Slash**: the edge moves sideways at ≥ 3 m/s.
- **Blunt**: anything else hard enough (such as the pommel).

Stab and slash use the tip velocity measured before the physics step. That's reliable even though the solver has
already bounced the blade by the time the collision callback runs.

### Explosions

`Ballistics.Explode(center, radius, maxDamage, maxVelocityChange, layerMask, source)` kicks every body in range
(mass-scaled, with distance falloff). Each ragdoll character takes damage only once.

### Anything else

Any Rigidbody that hits a body part hard enough deals blunt damage from the collision impulse: a thrown brick, a
swung pipe, your fist (see `RagdollHealth → Impact Damage`). Static geometry only hurts on violent impacts, such as
being thrown into a wall, never on normal falls. To give a weapon its own damage rules, implement
`IImpactDamageDealer` on its Rigidbody (body parts then skip their generic impact damage) and call
`ApplyDamage` yourself.

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
- **Equip:** walk to the weapon, crouch and lean, reach, and grip it with the physical hand (a FixedJoint to the
  gun's Rigidbody or ArticulationBody).
- **Shoot:** the arm muscles aim the physical gun, and the NPC fires in bursts only when the real muzzle is on
  target (*Aim Tolerance*). Recoil, hits and shoves spoil the aim. It backs off if you get within 1.5 m, and moves to
  regain line of sight.
- **Disarm:** grab the gun in an NPC's hand and it lets go after 0.3 s. NPCs drop guns when they die, and sometimes
  when knocked down (35%).

**Your own guns:** add `HitscanGun` (or derive from `NPCWeapon` and implement `NPCFire`). Set **Muzzle** and
optionally **Grip** (where the palm goes; defaults to a child named `Grip`), plus Range, NPC Fire Interval and Aim
Tolerance. Disable *Use Weapons* on an NPC to keep it fists-only.

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
