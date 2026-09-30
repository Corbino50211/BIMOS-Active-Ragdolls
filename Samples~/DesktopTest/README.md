# Desktop Test Harness

A mouse-and-keyboard stand-in for the BIMOS player, so active ragdoll NPCs can be tested without a headset.

**Use:** import this sample, then run **Tools → Active Ragdoll → Create Test Arena**. That adds a
`Desktop Test Player`. Or add `DesktopTestPlayer` to an empty GameObject standing on your floor.

| Input | Action |
|---|---|
| WASD / mouse | move / look (click to capture the cursor, Esc to release) |
| Left mouse | hitscan shot (34 damage, 12 N·s knock-back) |
| Right mouse | shove (45 N·s, no damage) |
| F | short-range stab test |
| G | grenade at the crosshair |
| V | throw a knife: it sticks in NPCs and walls (`BladeWeapon`) |
| B | snap the bone under the crosshair (arm, leg or neck) |
| hold Q / E | twist / pull on the stuck knife under the crosshair (3 N·m / 250 N) |
| K / R | kill all NPCs / respawn all NPCs |
| T | toggle 0.25× slow motion |

The player is a physical capsule with `PlayerHealth` and a team-0 `CombatTarget`, so NPCs hunt it and their
punches really shove it. Requires the Input System package (a BIMOS dependency).
