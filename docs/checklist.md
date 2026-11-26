# Point Blank — Build Checklist

Spec: [`docs/superpowers/specs/2026-11-25-pointblank-vision-design.md`](superpowers/specs/2026-11-25-pointblank-vision-design.md)
Status: draft, awaiting owner sign-off

## How to use this checklist

- **One item = one session = one branch = one pull request.** Do the items in order
  unless an item says it can move.
- At the start of each session, the builder reads the spec and this item, then writes a
  detailed plan for the item (`superpowers:writing-plans`, saved under
  `docs/superpowers/plans/`). The tests in **Done when** are written before the code.
- The item's **Done when** list is the acceptance criteria. The evaluator checks the PR
  against it and nothing else.
- **Model:** S = Sonnet (routine), O = strongest model (design-heavy). Sized to fit one
  Claude Pro usage window.
- ⛳ = owner sign-off point (owner plays or reviews before the next milestone starts).
- Tick an item only when its PR is merged.
- Checklist milestones 1–2 together are the spec's "First playable" milestone. Checklist
  milestones 3–8 match spec milestones 4–9.
- "Seeds" means scenario tests run headless over seeds 1–100 unless stated otherwise.

---

## Milestone 0 — Foundation

- [x] **0.1 Repo, rules and skeleton** (S)
  Solution with `Sim` (C# class library, no Godot references), `Sim.Tests` (xUnit), and a
  `Game` Godot 4.7 .NET project that references `Sim`. Add `CLAUDE.md` (build and test
  commands, spec link, rules), `.claude/settings.json` (repo-only edits; deny push to
  `main`, force push, and deleting outside the repo), `.gitignore`, and
  `docs/process/evaluator.md` and `docs/process/session-brief.md` (reusable prompts).
  *Done when:* `dotnet test` passes with one trivial test. The Godot project opens and
  runs headless (`--headless --quit`) with no errors. Settings deny `git push origin
  main`.

- [x] **0.2 Deterministic clock and RNG** (S)
  Fixed-timestep world loop (10 ticks per simulated second), one seeded RNG, and a world
  state hash.
  *Done when:* the same seed and the same scripted inputs give an identical state hash
  after 10,000 ticks across 3 runs, and different seeds give different hashes. A test
  fails if `System.Random`, `DateTime` or `float` appear in `Sim` outside approved
  wrappers (fixed-point or deterministic maths only).

- [x] **0.3 Headless battle runner and replays** (S)
  CLI: `runner --scenario X --seeds 1..100 --out stats.json`. Replay file =
  seed + map + forces + timestamped orders.
  *Done when:* running a scenario twice yields byte-identical stats. A saved replay
  re-runs to the same final hash.

- [x] **0.4 Data files with sources** (S)
  JSON schema and loader for units, weapons, ammo, materials and organisation tables.
  Every value carries `source`, which is a reference, `estimated` (with reasoning), or
  `placeholder`.
  *Done when:* the loader rejects a value with no source tag. A report lists placeholder
  counts. A disabled-by-default test fails when placeholders > 0 (switched on in item
  7.7).

## Milestone 1 — Core simulation (first playable, part 1)

- [x] **1.1 Map model** (S)
  Tile map: elevation per tile, terrain type (open, road, hedge, light woods to start),
  and per-type cover, concealment and movement cost. Buildings as footprints with 1–3
  floors. Map file format and a 300 × 300 m test map.
  *Done when:* round-trip load/save tests pass, and terrain lookups return the spec'd
  values.

- [x] **1.2 Line of sight (3D on a 2D map)** (O)
  Eye height by posture (standing, kneeling, prone) and by floor. Blocking and partial
  blocking by hedges, woods, elevation and buildings.
  *Done when:* a prone soldier behind a hedge is unseen from 100 m on flat ground; a
  standing soldier is seen. A soldier on a 2nd floor sees over the hedge. A hill crest
  blocks LOS. A performance test runs 300 × 300 LOS checks per tick under budget (set in
  the item plan).

- [x] **1.3 Soldiers, teams, squads, leaders** (S)
  Organisation from data (placeholder 1944 US rifle squad and German Gruppe). Positions,
  posture, and next-in-command succession.
  *Done when:* when a leader dies, the next in command takes over after a delay, and the
  squad loses its leader bonus during that delay.

- [x] **1.4 Pathfinding and movement** (S)
  A* on tiles with terrain costs. Orders `Move`, `MoveFast`, `Sneak` with posture and
  speed from data. Squad members keep a spread formation.
  *Done when:* units take the cheaper route around woods, `Sneak` is slower and lower
  posture than `Move`, and two squads crossing paths don't deadlock.

- [x] **1.5 Order pipeline with chain of command** (O)
  One order vocabulary for player and AI. Delivery delay depends on leader alive, morale,
  and HQ contact. Shaken units may hesitate or refuse. Delay state is exposed for the UI.
  *Done when:* with seeds, mean delay is higher for a leaderless squad and for a squad
  out of HQ contact. A broken squad refuses an advance order at least 50% of the time and
  never refuses a fall-back order.

- [x] **1.6 Spotting and fog of war** (O)
  Each side has its own knowledge of enemy contacts, which goes stale (last-known
  position). Spotting chance from distance, concealment, movement and posture.
  *Done when:* a moving enemy is spotted sooner than a still, prone one. A player-side
  query never returns unspotted enemies. A contact that moves out of sight shows its
  last-known position, not its live one.

- [ ] **1.7 Small-arms fire** (O)
  Projectile-level shots with accuracy from data and range, cover from terrain, and ammo
  consumption.
  *Done when:* hit rate falls with range and with target cover. A unit with no ammo
  stops firing and reports it. Shots are blocked by hedges with the configured
  probability.

## Milestone 2 — Core simulation (first playable, part 2)

- [ ] **2.1 Suppression** (O)
  Incoming fire (near misses, volume, calibre) builds suppression, which decays over
  time.
  *Done when:* a suppressed unit's rate and accuracy of fire drop measurably. A squad
  crossing open ground under sustained MG fire is pinned in ≥80% of seeds within 30 s,
  and through hedges it's pinned in clearly fewer seeds (at least 20 points lower).

- [ ] **2.2 Morale** (O)
  Inputs: suppression, casualties, leader, isolation, nearby friendly support. States:
  steady, shaken, pinned, broken, routing, rallying.
  *Done when:* after its leader is killed, the squad's morale drops and it obeys orders
  more slowly. An unsupported squad that takes 50% casualties breaks in most seeds. A
  squad near its HQ rallies faster.

- [ ] **2.3 Unit AI: reactions and drills** (O)
  Idle units react to contact. Fire and movement within the squad. The squad leader
  assigns tasks to his men (e.g. sends the AT man forward).
  *Done when:* an idle squad that comes under fire takes cover or returns fire within 2 s
  in 100% of seeds. No unit under fire stays standing in the open for more than 3 s
  unless ordered `MoveFast`.

- [ ] **2.4 Off-map artillery** (O)
  Call-for-fire with delay and dispersion, HE blast and fragmentation, limited missions.
  *Done when:* after a 2-minute barrage on a defended position, defender morale and
  suppression are measurably degraded. In ≥30% of seeds the attacker has not spotted
  every surviving defender.

- [ ] **2.5 Godot view and controls** (S)
  Draw the map, units as placeholder shapes, and visible enemies only. Selection,
  right-click orders, pause, 1×/2×/4× speed. Show morale, suppression and order-delay
  state on the unit and in a panel.
  *Done when:* screenshot-mode tests capture the expected frames. The UI reads only
  player-side knowledge (test).

- [ ] **2.6 Debug overlays and screenshot mode** (S)
  Toggleable LOS cones, suppression and morale values, true vs known positions, AI
  intent.
  *Done when:* each overlay toggles and appears in a screenshot test.

- [ ] **2.7 First playable scenarios** (S) ⛳
  Two scenarios: *fix and assault* (US platoon + tank vs German MG position) and
  *bombard and creep* (US with artillery vs dug-in German squad), against a simple
  scripted defender.
  *Done when:* both run start to finish from the runner and in Godot with no errors.
  **Owner plays both and signs off on the feel (Milestone 3 in the spec).**

## Milestone 3 — Art style

- [ ] **3.1 Art pipeline** (S)
  Aseprite source files in `art/source`, a Lua/CLI export script to `art/export` (sheet
  + JSON), and Godot import. Record PixelLab's commercial terms in `docs/licensing.md`.
  *Done when:* one command regenerates all exports, and Godot shows an exported test
  sprite.

- [ ] **3.2 Sample sprites and tiles** (S) ⛳
  US and German rifleman (idle, walk, crawl, prone, fire, die; 8 facings) and a sample
  tile set (field, road, hedge, woods, building roof and interior).
  *Done when:* they're shown in the first-playable scenario. **Owner signs off on the art
  style.**

## Milestone 4 — Terrain and buildings

- [ ] **4.1 Research: terrain and materials** (S, web)
  Ballistic protection and concealment for earth banks and bocage, hedges, woods,
  brick, stone, timber and wall thicknesses, all with sources.
  *Done when:* the data files are filled and every value has a source or `estimated`
  with reasoning.

- [ ] **4.2 Full terrain set** (S)
  Bocage, woods, orchards, crop fields (crop height), water, rough ground, sunken roads.
  *Done when:* each type's cover, concealment and movement effect is covered by a
  scenario test.

- [ ] **4.3 Penetration of walls and objects** (O)
  Projectile energy against material and thickness, with residual energy after a
  penetration.
  *Done when:* rifle rounds don't pass a stone wall of the researched thickness, a heavy
  MG penetrates a timber wall, and residual energy reduces damage behind the wall.

- [ ] **4.4 Buildings: occupy and see inside** (S)
  Enter and exit buildings, per-floor positions and windows. The roof hides when your
  units are inside or on inspection.
  *Done when:* a squad ordered into a building spreads across floors and windows. A test
  checks roof visibility rules.

- [ ] **4.5 Damage and destruction** (O)
  Walls take damage and breach. Floors and whole buildings collapse, with casualties.
  Rubble becomes terrain.
  *Done when:* HE breaches a wall at the researched damage threshold, a collapsed
  building injures occupants, and rubble changes LOS and cover.

## Milestone 5 — Vehicles and support

- [ ] **5.1 Research: vehicles, guns, armour, ammo** (S, web)
  Normandy 1944 US and German AFVs, AT guns, MGs and mortars: armour thickness and
  angle, gun penetration tables, rates of fire, speeds, ammo loadouts.
  *Done when:* the data files are filled with sources.

- [ ] **5.2 Vehicles: movement, facing, crew** (S)
  Turning, hull and turret facing, crew states (buttoned up, bailed out), immobilisation.
  *Done when:* a buttoned tank spots worse than an unbuttoned one, and a bailed-out crew
  becomes an infantry team.

- [ ] **5.3 Armour penetration** (O)
  Hit location, thickness and angle against penetration at range, by ammo type (AP,
  APC, HE, HEAT). Partial and full penetration results.
  *Done when:* reference matchups match the researched tables at set ranges, e.g. a
  front hit at 500 m from a 75 mm M3 against a Panther glacis fails in ≥99% of seeds.

- [ ] **5.4 Support weapons and emplacements** (S)
  MG teams, mortars, AT guns, bazooka and Panzerschreck, foxholes, trenches, bunkers.
  *Done when:* an emplaced MG takes researched protection, and crews setting up or
  packing a support weapon take time.

- [ ] **5.5 Smoke** (S)
  Smoke from artillery, mortars, tanks and grenades blocks LOS, drifts and fades.
  *Done when:* a smoke screen blocks LOS for its duration and then clears, and a squad
  ordered to smoke then cross takes fewer casualties across seeds.

- [ ] **5.6 Air strikes** (S)
  Request, delay, approach, inaccuracy, possible friendly fire, limited sorties.
  *Done when:* a strike arrives within the delay window and misses by the researched
  spread.

## Milestone 6 — AI commander

- [ ] **6.1 Research: doctrine** (O, web)
  Summaries of US and German company and platoon doctrine for defence, attack and
  reaction to contact, with sources (field manuals, *Truppenführung*, Foreign Military
  Studies).
  *Done when:* `docs/research/doctrine-us.md` and `docs/research/doctrine-de.md` exist,
  each rule tagged with its source and translated into AI behaviour notes.

- [ ] **6.2 Map analysis** (O)
  Key terrain, covered routes, fields of fire, likely enemy approaches.
  *Done when:* on test maps, analysis finds the hand-marked key terrain and covered
  routes.

- [ ] **6.3 Defensive planner** (O)
  Deploys and runs a defence following its side's doctrine (e.g. German forward MG
  positions with a counterattack reserve).
  *Done when:* AI vs scripted attacker over seeds, the defender holds clearly better than
  a random deployment.

- [ ] **6.4 Attack planner** (O)
  Base of fire plus manoeuvre, artillery prep and smoke, following its side's doctrine.
  *Done when:* AI vs AI over seeds, the attacker uses a base of fire in ≥90% of attacks
  and beats a frontal-rush baseline.

- [ ] **6.5 Plan adaptation** (O) ⛳
  Re-plans on contact, counterattacks, withdraws, commits reserves.
  *Done when:* an AI-vs-AI tournament report is generated. **Owner plays against the AI
  on both sides and signs off.**

## Milestone 7 — Full battle flow

- [ ] **7.1 Research: rosters and costs** (S, web)
  1944 TO&E for the unit types offered, plus a points-cost scheme.
- [ ] **7.2 Purchase screen** (S)
  Points budget, faction rosters, unit info cards.
- [ ] **7.3 Deployment phase** (S)
  Paused placement. Defender gets half the map, attacker the quarter along their own
  edge.
  *Done when:* units can't be placed outside the zone (test).
- [ ] **7.4 Ending conditions and results** (S)
  Timer and/or objectives chosen before the battle, plus a results screen.
- [ ] **7.5 Real-map pipeline** (O)
  OpenStreetMap + public elevation data → tile map, an in-game map editor for hand
  fixes, and attribution recorded.
- [ ] **7.6 Three Normandy maps** (S)
  Locations proposed by the builder, picked by the owner.
- [ ] **7.7 Placeholder gate** (S) ⛳
  Switch on the placeholders-must-be-zero test and fix whatever remains. **Owner plays a
  full battle from purchase to result and signs off.**

## Milestone 8 — Release polish

- [ ] **8.1 Menus, settings, saves** (S)
- [ ] **8.2 Audio** (S)
- [ ] **8.3 Credits, attributions, licences** (S)
- [ ] **8.4 Windows build and installer** (S) ⛳
  **Owner signs off on the release candidate.**
