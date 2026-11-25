# Point Blank — Vision & Design

Date: 2026-11-25 · Status: draft, awaiting owner sign-off

## 1. What this is, and why

Point Blank is a single-player, real-time tactical WWII game, US vs Germany, played as
standalone battles of 15–20 units per side, seen from a 2D top-down view. It sits in the
same genre as the old Close Combat games but is an original work: original name, art,
UI, maps and code.

The owner wants to release or sell it someday. That means original assets only, recorded
sources for every historical number, licence-clean data, and eventually a finished game
(menus, settings, saves), not just a simulation.

### The moments the game exists to deliver

1. **Fix and assault.** Pin an enemy position with tank and infantry fire until it can't
   shoot back, then move troops in.
2. **Bombard and creep.** Hammer a position with artillery, then advance carefully
   without knowing what survived.

Both moments depend on three systems. These are the heart of the game and get built
first:

- **Suppression.** Incoming fire degrades a unit's ability to act, separately from
  casualties.
- **Morale.** A unit's will to fight. It shifts with suppression, casualties, leadership
  and isolation, and changes how well the unit carries out orders.
- **Fog of war.** Spotting and concealment. You only see what your troops have spotted,
  and that knowledge goes stale.

## 2. Pillars

1. **You command; they fight.** The player gives orders to squads, teams, vehicles and
   emplacements. The squad leader runs the men inside the squad. For example, *he* sends
   the bazooka man to the hedge. The player never controls an individual soldier.
2. **Troops are competent on their own.** Without orders, units still act sensibly:
   return fire, take cover, face threats, report contacts. Only morale, suppression and
   the chain of command get in the way of good behaviour, never bad AI or bad
   pathfinding.
3. **Grounded in real data.** Movement speeds, carried ammo, weapon and armour stats,
   penetration, unit organisation and tactics come from documented sources, recorded per
   value.
4. **The ground matters.** Bocage, hedges, woods, fields, roads, elevation, buildings and
   walls shape what you can see, where you can shoot, and what stops a bullet.
5. **A worthy opponent.** The computer commander fights with real US/German doctrine at
   both the squad and company level.

## 3. Goals (first release scope)

- **Setting:** Normandy, June–August 1944. Other theaters are possible later.
- **Factions:** US Army and German Heer (Waffen-SS optional later).
- **Battle flow:** choose map and side → set the ending condition (timer and/or
  objectives) → buy troops from a points budget → paused deployment (defender: half the
  map; attacker: the quarter of the map along their own edge) → real-time battle with
  pause-and-order → result screen.
- **Forces:** 15–20 units per side (about a reinforced company): infantry squads and
  teams, HQs, support weapons (MGs, mortars, AT guns), vehicles (tanks, tank destroyers,
  halftracks, armoured cars), emplacements.
- **Unit structure:** squads with leaders, and teams within squads, following real 1944
  tables of organisation. A leader's death affects the squad (slower or failed internal
  orders, morale hit, possible replacement by the next in command), based on real
  practice.
- **Orders pass through the chain of command** *(owner chose)*: a player or AI order
  reaches a unit after a delay that depends on its leader being alive, its morale, and
  whether it has contact with its HQ (distance or radio). Shaken units may hesitate or
  refuse. Leaders and HQs therefore matter, and the delay must be shown on screen so the
  player understands it.
- **Combat simulation:** projectile-level small-arms fire, armour penetration against
  real armour thickness and angle, penetration of walls and objects by material and
  thickness, HE blast and fragmentation, smoke (artillery, tank, grenade) that blocks
  sight.
- **Buildings:** a variety of building and wall types with different protection. Walls
  and whole buildings can be damaged and destroyed. Troops can occupy buildings, and the
  roof hides to show the interior when your units are inside or you inspect it.
- **Terrain:** a tile map with height, cover, concealment and movement cost per tile:
  bocage, hedges, woods, fields (crop height), roads, water, rough ground.
- **Height and floors** *(builder's call)*: ground elevation per tile, plus buildings with
  1–3 discrete floors. A unit on an upper floor sees and fires over hedges and bocage
  and is more exposed to fire. Line of sight works in 3D (position + height) on a 2D map.
- **Map size and time** *(builder's call)*: maps about 500 m–1 km per side, battles at
  1:1 real time with pause and speed controls (e.g. 1×/2×/4×). A typical battle lasts
  15–45 minutes.
- **Off-map support:** artillery and air strikes, with delay, inaccuracy and limited
  availability.
- **Maps:** several maps based on real Normandy locations, built from real terrain data
  (elevation and land use), edited by hand for play.
- **AI opponent:** two layers that share one order system:
  - *Unit AI* (both sides): battle drills and reactions to contact, for example return
    fire, go to ground, fire and movement inside the squad, and rally.
  - *Commander AI* (enemy only): reads the map and the situation, then makes and adapts a
    plan (defend key terrain, set up a base of fire before an assault, counterattack,
    use artillery) following each side's doctrine.
  - The AI issues the same orders a player can, and sees only what its side has spotted.

## 4. Non-goals (first release)

- Multiplayer of any kind.
- A campaign or operations layer. Battles are standalone.
- 3D graphics.
- Individual soldier control.
- Eastern Front, British/Commonwealth forces, other years.
- Modding tools (keep the data in readable files, but no official mod support yet).

## 5. Anti-goals (a failure even if everything "works")

- Units standing around while being shot, or ignoring obvious threats.
- The player having to micromanage units to keep them alive or effective.
- Made-up stats presented as historical.
- Using anything from Close Combat or other games (names, art, maps, UI layout, data
  files).
- A simulation so opaque the player can't tell *why* a unit is pinned. Morale and
  suppression state must be visible.

## 6. Constraints

- **Engine:** Godot 4.7 .NET build (installed at `C:\Godot_v4.7-stable_mono_win64`).
- **Language:** C# (.NET installed).
- **Art:** 2D pixel art via Aseprite (Steam install) with the Aseprite MCP server
  (`.mcp.json`), and possibly the PixelLab extension. Check PixelLab's commercial terms
  before shipping any of its output.
- **Platform:** Windows PC first.
- **Repo:** `GC-Git/pointblank`. The owner signs off by merging pull requests.
- **Data licensing:** OpenStreetMap (ODbL) and public elevation data are allowed with
  attribution. Historical facts are free to use, but each value records its source.
- **Scale:** about 300 simulated soldiers plus vehicles, at a steady frame rate on a
  mid-range PC.

## 7. Approaches considered

| Approach | Trade-off | Verdict |
|---|---|---|
| **A. Pure C# simulation core; Godot only draws and takes input** | Fast headless tests, deterministic replays, Claude can verify its own work; we write our own line of sight and pathfinding | **Chosen** |
| B. Everything inside Godot nodes and physics | Fastest first prototype, but physics isn't deterministic, so tests and replays become unreliable and the game rules get tangled with rendering | Rejected |
| C. Same split as A, in GDScript | Simpler language, but too slow for this much simulation, with weaker testing | Rejected |

The owner delegated this choice. A was chosen because the testability it gives is what
makes long unattended build sessions safe.

## 8. Architecture (decided)

- **`Sim` (C# class library, no Godot references).** All game rules. Fixed timestep
  (e.g. 10 ticks per simulated second; the builder may tune this), one seeded random
  number generator, and no wall-clock time or floating-point behaviour that differs
  between machines. Same seed and same orders give an identical battle.
- **`Sim.Tests`.** Run with `dotnet test`. Includes unit tests and **scenario tests**,
  which run a scripted battle headless for N seeds and check outcomes statistically.
- **`Game` (Godot project).** Reads the sim state every frame and draws it. Turns player
  input into the same order commands the AI uses. Debug overlays show line-of-sight
  cones, suppression and morale values, the AI's plan, and spotted vs actual positions.
- **Data files.** Units, weapons, ammunition, armour, materials and organisation tables
  live in readable data files (JSON or similar), each value with a `source` field. No
  historical number is hard-coded.
- **Orders.** One command vocabulary (move, move fast, sneak, fire, smoke, defend,
  ambush, …) shared by the player and the AI.
- **Replays.** A battle is saved as seed + map + forces + timestamped orders. Used for
  bug reports and evaluator checks.
- **Tools.** A headless battle runner CLI (`run scenario X with seeds 1..100, output
  stats`) and a screenshot mode, so automated checks can see the game.

## 9. How "done" is checked

Every checklist item ships with tests written *before* the code. These scenario tests
define the first playable version (placeholder art, flat or simple map):

- An idle squad that comes under fire takes cover or returns fire within 2 s, in 100% of
  seeds.
- A squad crossing open ground under sustained MG fire becomes pinned in at least 80% of
  seeds within a set time. The same squad advancing through hedges is pinned clearly less
  often.
- A suppressed unit's rate and accuracy of fire drop measurably.
- After its leader is killed, a squad follows orders more slowly and its morale drops.
- A unit can't see or shoot an enemy with no line of sight. An unspotted enemy doesn't
  appear on the player's screen.
- After an artillery barrage, defender morale and suppression are measurably degraded,
  and some defenders are left whose positions the attacker doesn't know.
- Same seed + same orders → identical outcome (determinism test).

Later milestones add their own scenario tests in the same style (penetration, building
destruction, AI commander plans, and so on). Fun and feel are judged by the owner
playing, at sign-off points.

## 10. Milestones and sign-off points (owner approves each)

1. **This document.**
2. **The checklist** (generated from this document, edited by the owner).
3. **First playable:** the core loop (suppression, morale, fog of war, line of sight,
   orders, both moments playable with squares on a simple map).
4. **Art style:** a sample sprite sheet and a sample map tile set.
5. **Terrain and buildings:** real ground types, wall penetration, occupiable and
   destructible buildings.
6. **Vehicles and support:** tanks, armour penetration, smoke, artillery, air strikes.
7. **AI commander:** doctrine-based plans for both sides.
8. **Full battle flow:** purchase, deployment, ending conditions, several real-location
   maps.
9. **Release polish:** menus, settings, saves, audio, credits and attributions.

## 11. Research policy

Historical data and doctrine are collected by dedicated research sessions with web
access, written into data files with a source (title, author or publisher, page or URL)
for each value. Where sources disagree or nothing exists, the value is marked
`estimated` with reasoning.

*(Builder's call)* Research does not block the first playable. Early milestones may use
values marked `placeholder`, which the UI and data never present as historical. Research
sessions run alongside the build, starting once the checklist is approved. Every
`placeholder` must be replaced by a sourced or `estimated` value before milestone 8, and
a test fails the build if any remain at that point. Starting sources include US WWII Army field manuals (rifle
company and infantry regiment), the German *Truppenführung* (H.Dv. 300), the US Army's
post-war Foreign Military Studies written by German officers, and 1944 tables of
organisation.

## 12. How the build runs (answers to the owner's five questions)

1. **Knows what done looks like:** each checklist item has acceptance criteria and
   scenario tests written first.
2. **Can check its own work:** `dotnet test`, the headless battle runner, and screenshot
   mode.
3. **Allowed to touch:** this repo only. No pushing to `main`, no force pushes, no
   deleting outside the repo. Enforced in `.claude/settings.json`.
4. **What stops it:** each checklist item runs in its own session with a turn cap. It
   stops and reports if tests can't be made to pass. The owner is on **Claude Pro**,
   which has usage limits per 5-hour window and per week, so:
   - Checklist items are small: each should finish comfortably within one usage window
     (aim for a few hours of work, roughly 30–60 turns).
   - Routine items and evaluator reviews use a cheaper model (Sonnet). Design-heavy
     items (AI commander, morale model) use the strongest model.
   - Run one build session at a time, and no parallel fan-outs.
   - If a session hits a usage limit, it leaves its branch with a progress note so the
     next session can pick up.
5. **Where the result goes:** a branch and pull request per item. A separate evaluator
   session reviews the diff against the acceptance criteria, without seeing the
   builder's reasoning. The owner merges, and that merge is the sign-off.

## 13. Left to the builder

- Exact tick rate, data file format, project folder layout.
- Pathfinding algorithm and tile size.
- Internal design of the suppression and morale models (must satisfy the scenario tests
  and use researched inputs where they exist).
- UI layout details before the art-style sign-off.
- Which Normandy locations become the first maps (propose them; owner picks).
