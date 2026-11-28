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

- [x] **1.7 Small-arms fire** (O)
  Projectile-level shots with accuracy from data and range, cover from terrain, and ammo
  consumption.
  *Done when:* hit rate falls with range and with target cover. A unit with no ammo
  stops firing and reports it. Shots are blocked by hedges with the configured
  probability.

## Milestone 2 — Core simulation (first playable, part 2)

- [x] **2.1 Suppression** (O)
  Incoming fire (near misses, volume, calibre) builds suppression, which decays over
  time.
  *Done when:* a suppressed unit's rate and accuracy of fire drop measurably. A squad
  crossing open ground under sustained MG fire is pinned in ≥80% of seeds within 30 s,
  and through hedges it's pinned in clearly fewer seeds (at least 20 points lower).

- [x] **2.2 Morale** (O)
  Inputs: suppression, casualties, leader, isolation, nearby friendly support. States:
  steady, shaken, pinned, broken, routing, rallying.
  *Done when:* after its leader is killed, the squad's morale drops and it obeys orders
  more slowly. An unsupported squad that takes 50% casualties breaks in most seeds. A
  squad near its HQ rallies faster.

- [x] **2.3 Unit AI: reactions and drills** (O)
  Idle units react to contact. Fire and movement within the squad. The squad leader
  assigns tasks to his men (e.g. sends the AT man forward).
  *Done when:* an idle squad that comes under fire takes cover or returns fire within 2 s
  in 100% of seeds. No unit under fire stays standing in the open for more than 3 s
  unless ordered `MoveFast`.

- [x] **2.4 Off-map artillery** (O)
  Call-for-fire with delay and dispersion, HE blast and fragmentation, limited missions.
  *Done when:* after a 2-minute barrage on a defended position, defender morale and
  suppression are measurably degraded. In ≥30% of seeds the attacker has not spotted
  every surviving defender.

- [x] **2.4a Per-man reactions everywhere** (S)
  Every order-driven or AI-driven posture change is made man by man, each after his own
  deterministic reaction delay. A man waits for a move order's posture before he walks,
  and a later order is never overridden by an older pending change.
  *Done when:* across the `bombard-creep`, `pin-open`, `fix-assault`, `equal-fire` and
  idle-reaction scenarios (seeds 1 to 50) no squad with 3 or more able men ever has every
  able man change posture on the same tick (permanent test; any exception justified in
  it). The delays are tagged data values and the pending state is in the state hash. All
  earlier "Done when" tests stay green with no retuned values.

- [x] **2.5 Godot view and controls** (S)
  Draw the map, units as placeholder shapes, and visible enemies only. Selection,
  right-click orders, pause, 1×/2×/4× speed. Show morale, suppression and order-delay
  state on the unit and in a panel.
  *Done when:* screenshot-mode tests capture the expected frames. The UI reads only
  player-side knowledge (test).

- [x] **2.6 Debug overlays and screenshot mode** (S)
  Toggleable LOS cones, suppression and morale values, true vs known positions, AI
  intent.
  *Done when:* each overlay toggles and appears in a screenshot test.

- [x] **2.4b Realistic posture under pin and orders** (S)
  Do before 2.7. Follow-ups from the 2.4a review: men get up while their squad is pinned
  (seeds 1-50: `fix-assault` 227 such rises, `equal-fire` 271; before 2.4a 183 and 216; one
  routing man rises every 4 s); quick stand/drop reversals remain; by code reading, not yet
  tested, `Halt` cancels any awaited posture change including going lower, so `Hold` just
  after `Sneak` may leave men standing, and an AI get-down made while a man awaits an order
  is also cancellable.
  *Done when:* over seeds 1-50, men don't get up while their squad is pinned unless a
  documented realistic reason applies; quick stand/drop reversals within 1 s are no higher
  than the pre-change baseline in every scenario; a test shows `Hold` just after `Sneak`
  leaves men low, and an AI get-down is not cancelled by a pending order. Existing criteria
  stay green with no retuning.
  Owner decision: in a routing squad that is pinned, men who are not personally pinned may get
  up and run in rushes. This is an accepted realistic reason under the "no rises while pinned"
  criterion: each man is judged by his own state, consistent with pinned men dropping
  individually.

- [x] **2.5a View and control follow-ups** (S)
  Do before 2.7. From the 2.5 review: the rules-of-engagement key pressed just after a move
  silently cancels the pending move (needs an owner decision on whether it should); at default
  zoom squads are tiny blobs and men and postures can't be told apart; with several squads
  selected the panel shows only the first, e.g. 'Order: none' while another has a pending order;
  Smoke and Ambush aren't offered; incoming-fire bearings and order rejections aren't drawn. Unit labels and the panel title
  show raw ids such as `us_rifle_squad`; pressing the rules-of-engagement key again before
  the first change applies repeats the same setting instead of cycling further. From the 2.6
  review: overlay text overlaps when zoomed in, and the orange values look like stale-contact
  labels; sight dots clear on every pan or zoom frame, so they stay mostly blank while moving;
  a sight pass costs 9-21 ms per frame (Debug, two squads; first run 48 ms).
  Also from the 2.6 review: the F4 intent label "firing" takes precedence over reload, ammo and
  task state (`fix-assault` seed 1: 13% of "firing" man-ticks were reloading, 9% had no fire task
  or were silenced; "reloading" never appears), so current state must be checked first; several
  intent states have no test (reloading, aiming, out of range, no ammo, rushing, down, firing
  line, flanking, ambushing, fleeing, cowering) and target lines are compared against the code
  under test; "cowering" covers a whole squad before each man reacts and "firing line" includes
  non-firing men; release builds still highlight F2-F4 although inert.
  During review the headless `--build-solutions` Godot command once reached build DONE then did
  not exit for over 8 minutes (not yet checked on main). Fixed in 2.5a: the console wrapper
  waits for every process the build starts, and MSBuild left a reusable worker node running;
  `Directory.Build.rsp` and `Directory.Build.props` stop persistent build processes.
  *Done when:* each is fixed or explicitly deferred by the owner, with tests.
  Owner decisions: changing the rules of engagement keeps a pending move order, and the
  change travels beside it with its own delay; the Smoke order is deferred until item 5.5
  (smoke is not built). Ambush is to be offered only if real ambush behaviour exists: the
  sim currently treats Ambush exactly like Defend, so it is not offered; the owner deferred
  the Ambush order to item 2.8.

- [x] **2.4c Squad masked by friendlies** (S)
  Do before 2.7. From the 2.6 review: in `fix-assault` seed 1 the second squad was mostly
  masked by friendlies and never fired (not yet verified).
  *Done when:* the cause is measured over seeds 1-50, and a squad that cannot fire because
  friendlies are in the way realistically shifts for a clear line or the reason it stays is
  documented; no scenario reshaping and no retuning.
  **Masked squads stay where they are (owner decision).** Measured over seeds 1-50
  (`MaskedFireMetrics`, run with `PB_MASK_REPORT=1`), share of on-task man-time masked by
  friendlies: `fix-assault` base of fire 73%; `bombard-creep` stationary squads 71/70/43%.
  Cause: the `fix-assault` base of fire stands in ranks along its own line of fire under a
  Fire order, which forms no firing line (all its masked time is behind its own men); in
  `bombard-creep` the first two of those squads are masked entirely by another squad, the
  third by another squad for 55%; moving squads are blocked by their own men while they move
  (nearly all of the `fix-assault` assault squad's masked time).
  A major cause of masking is the hold-fire distance: AI fire already holds for a comrade
  within 2 m of the line, short of the target (the larger of `near_miss_radius`, 2 m, and
  `mask_clearance`, 0.5 m); only player-ordered fire uses `mask_clearance` alone. An
  independent re-measurement over seeds 1-50 found base of fire masked 73% and
  `bombard-creep` squads 71/70/43% (another squad blocking for 100/100/55%); all three
  `bombard-creep` squads are on AI fire, and in 60-75% of their masked time every blocker is
  more than 0.5 m off the line.
  Tried (seeds 1-50): in a stationary squad with a fire task, each masked man stepped to a
  clear line. Without limits this reached 29% and 31/34/13%, by moving men who were under
  fire, suppressed or pinned. With gates and limits it reached 54% and 46/47/17%, but the
  `fix-assault` base of fire passed its own men closely, pinned itself and fired less: 40
  against 63 man-shot-ticks per seed, and the share of stationary masked time spent pinned or
  broken rose from 27% to 78%.
  The drill figures (the 29% and 54% results with their per-squad shares, 40 against 63
  shot-ticks, 27% to 78%) were measured with code since removed and can't be reproduced from
  the repo.
  Removed for that reason: the owner decided to take the drill out and let masked squads stay.
  Suspected cause of the side-stepping squad pinning itself, not proven: under player-ordered
  fire a man holds only for a comrade within `mask_clearance` (0.5 m) of his line, so friendly
  near misses within `near_miss_radius` (2 m) can still suppress. Friendly near misses already
  occur without the drill. Both the drill and a safe friendly firing distance are in item 2.11.
  The masking test is flat (it ignores height), kept as is.
  Kept from the item, independent of the drill: a routing man who has reached the end of his
  run while others of his squad are still running gets down under fire and stays down until he
  has somewhere to run (`RoutingNoRouteTests`). Over seeds 1-50, before its state was added to
  the hash, it changed the final hash in 4 `fix-assault`, 4 `fix-assault-nobase`, 6
  `bombard-creep` and no `pin-open` battles.

- [x] **2.12 Faster tests** (S)
  Do next after 2.4c. A full test run takes about 13 minutes and every build and review
  repeats it.
  *Done when:* a documented quick test command for everyday work runs much faster (report
  timings) while the full suite still runs every existing test and is required before
  merging; no test is weakened, skipped from the full run, or given fewer seeds in the full
  run.

- [x] **2.9 Defend halts a move** (S)
  Do before 2.7 (owner decision). From the 2.5a review: a delivered Defend order does not stop
  a move already under way (4 of 4 squads still moving 3 s later; same on main before 2.5a),
  and the panel then shows 'Defend: Done' while the squad marches.
  *Done when:* a delivered Defend halts the move through the chain of command with per-man
  reactions, with tests.

- [x] **2.4d Posture follow-ups** (S)
  Do before 2.7 unless the owner defers it. From the 2.4b review (seeds 1-50): the posture
  metric excuses every routing rise, including a few personally pinned men (4 cases in
  equal-fire and fix-assault); a new movement order replaces a pending AI get-down with a
  higher posture (34 of 1054 man-orders in fix-assault); calm men in unpinned moving squads
  lie prone over 5 s (240 cases in fix-assault, same before 2.4b); a few men rise while
  morale says Pinned but suppression does not. From the 2.4c review: the routing no-route
  get-down test (`RoutingNoRouteTests`) asserts only "no rises"; removing the reaction delay
  still passes it.
  From the 2.9 review: when a Defend halt is delivered under fire, men stood up from prone 1-5
  ticks after delivery while halting, then dropped; one `DefendHaltTests` assertion was always
  true. Owner decision (added during the build): a delivered Hold stops each man after his own
  reaction delay, as the Defend halt does, instead of every man on one tick.
  *Done when:* each is measured, then fixed or documented as realistic, with tests; the
  no-route test also asserts that the man gets down, and tests per-man timing (no
  synchrony; it fails without the reaction delay); men do not spring up during a halt, with a
  real check in `DefendHaltTests`; a delivered Hold stops the men one by one, with tests, the
  delay a tagged data value and delivery through the chain of command unchanged; no retuning
  or scenario reshaping.
  Measured over seeds 1-50 in the scenarios of `PostureDesyncTests`:
  - Pinned men's routing rises and rises under morale "Pinned": none were of a man, or squad
    morale, pinned when the tick began; each was pinned later in that tick, by fire arriving
    after he moved. Realistic, no change; the metric no longer excuses a pinned man.
  - A new move replacing a pending AI get-down (`fix-assault` 34 of 1054 man-orders): fixed for
    men suppressed or pinned (they finish getting down). A calm man's change is overtaken by the
    order, which is realistic. A routing move still replaces it; the man stays down until he
    bolts.
  - Calm men prone over 5 s in moving squads (`fix-assault` 245): all are on a Sneak move, in a
    team covering the others, routing, or in a squad under enemy fire in the last 6 s. No
    unexplained stretch over 1 s. Realistic, no change.
  - Rises while a Defend halted a squad under fire (each marching squad ordered at its first
    man under fire; `fix-assault`, `bombard-creep`, `equal-fire`): 12 in 101 halts, now 0.
  - Hold uses the same `order_posture_delay` as the Defend halt (no new value). Men keep their
    posture, or finish getting lower; men of an idle squad on their own short moves stop the
    same way. A squad stopped by a pin or by morale still stops at once.

- [ ] **2.13 Readable placeholder graphics** (S)
  Do before 2.7: the owner could not tell what was happening when playing. Plain placeholder
  shapes; pixel art stays milestone 3.
  *Done when:* soldier shapes clearly show standing, kneeling and prone and which way each man
  is working; tracers and muzzle flashes are drawn only for fire the player's side could see;
  shell explosions, wounded and dead are visible; spotted-enemy markers are clearer; the
  starting zoom is sensible; screenshot tests cover each and the fog-of-war boundary holds.

- [ ] **2.7 First playable scenarios** (S) ⛳
  Two scenarios: *fix and assault* (US platoon + tank vs German MG position) and
  *bombard and creep* (US with artillery vs dug-in German squad), against a simple
  scripted defender.
  *Done when:* both run start to finish from the runner and in Godot with no errors.
  **Owner plays both and signs off on the feel (Milestone 3 in the spec).**

- [ ] **2.8 Ambush order** (S)
  After the 2.7 play-through. Proposed behaviour, to be confirmed by the owner before
  building: men stay hidden and hold fire until the enemy is close or their leader opens
  fire, then each man joins after his own reaction; it differs from Defend and is offered
  in the view. Also from the 2.5a build: selection rings and nearby squad names can
  overlap (the Defend order's 'not available yet' reading was fixed in 2.5a). Open question for
  the owner: whether Ambush should halt a move.
  *Done when:* the owner has confirmed the behaviour, tests show it differs from Defend with
  no all-at-once opening, the view offers it, and the display follow-up is fixed.

- [ ] **2.10 Drag-to-place move orders** (S)
  After the 2.7 play-through (owner decision). Dragging a move order sets the line along which
  the squad spreads (along hedges and walls, on the protected side) and a general facing
  direction; men arrive individually. Placement at building windows and floors follows 4.4.
  Fences do not exist as terrain yet, and the sim has no facing yet.
  *Done when:* the owner has confirmed the design, tests cover the spread, cover side and
  facing behaviour, and the view supports dragging.

- [ ] **2.11 Movement when friendlies block fire** (S)
  Postponed by the owner until after the formal play-through: whether a moving squad blocked by
  its own men spreads out or halts a team to shoot, and whether a whole squad may shift without
  an order when another squad blocks it. Also revisits, from 2.4c, the side-stepping drill
  (masked men of a stationary squad stepping to a clear line) and a safe friendly firing
  distance (how close to a comrade a man may fire: today AI fire holds within 2 m of the line,
  player-ordered fire within 0.5 m).
  Also postponed until after the 2.7 play-through: whether halting men (Defend or Hold) should
  finish crossing an open gap or doorway first.
  *Done when:* the owner has decided, then it is implemented or documented, with tests.

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
  Includes offering the Smoke order in the game's order bar (deferred from item 2.5a by
  owner decision).

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
  Note: Release-configuration test runs currently show 13 failures (9 predate 2.5a); mostly
  debug-only overlays being inert, two game boundary failures undiagnosed. Resolve here.
  From the 2.12 review: some Quick_check tests pass with suppression disabled; the speed guard
  misses an added Skip or a removed test file; the 40-squad AI budget test has no quick
  equivalent; the bombard quick check takes 18 s; SeedRuns has no direct test.
  From the 2.4d review: no test isolates a new rise requested mid-halt; the pinned get-down
  case passes without the fix; no test that a routing move still replaces a get-down.
