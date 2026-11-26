# Item 2.4: progress note (third round, realism first)

Status: **work in progress, out of tool budget. One test is red:** `BombardCreepTests.The_creep_finds_the_survivors_one_by_one_and_the_barrage_pays_for_itself`, at assertion (2), progressive discovery. Every other test touched in this round passed in targeted runs. I did not run the full suite after the last data change.

## Done (in this commit)

- **A. Hedge concealment, at the spotting layer** (`SpottingSystem.AtHedge`, new keys in `spotting.json`):
  - A man within `hedge_position_distance` (2 m) of a hedge between him and the observer is at a firing position.
  - Still and not firing, he can be spotted only within `hedge_still_range` (40 m).
  - Moving, only within `hedge_moving_range` (100 m).
  - Firing, at any range, with the usual rate and firing factor.
  - LOS stays geometry: the ray through a firing position is clear but obscured by `hedge_adjacent_obscurement`, now 0.25 (a gap), and the range falloff is kept. LOS is still symmetric.
  - The 1.2 test is back to its original setup (target 1 m behind the hedge). It now asserts the obscured ray at the LOS layer. Its name changed to `Prone_soldier_at_a_hedge_has_only_an_obscured_ray_from_100m_and_standing_is_clear`. A new spotting test covers "unspotted from 100 m while still and silent, spotted when firing, and spotted at 26 m", prone and kneeling. Both pass.
- **B. Per-man fire decisions** (`UnitAiBrain.Discipline`, new keys in `unit_ai.json`), the same rules on both sides:
  - A man holds fire at or above suppression 45, and then also lies low.
  - The leader and the MG gunner open at once.
  - The others join 1–4 s later, each with his own deterministic hash draw.
  - Delays double with no leader.
  - In a shaken, pinned or rallying squad, a man stays silent with chance 0.35. In a steady squad the chance is 0 (`silent_chance`).
  - On Defend or Ambush, a squad opens fire on its own only within `defend_open_range` (120 m), unless it is fired on.
  - Men lie flat under shellfire.
  - `FireSystem.SetAiSilent` applies the decisions, and the silent set is in the hash.
- **C.** Spotting is per pair of soldiers. A firer reveals only himself; no shared squad contact was found.
- **E.** The realism rule was added to `CLAUDE.md` (rule 12).
- **Regressions:** the UnitAi, Fire and Pin tests pass.
  - pin-open 100/100; pin-hedges 38/100, down from 70 (the bar, open minus hedges ≥ 20, still holds); pin-open-nocas 100/100.
  - fix-assault: 93/100 at least 60% or reached, mean advance 88% of unopposed.
  - Firing line: 100/100 seeds at ≥ 75% within 10 s.
  - The move-order return-fire test needed the leader to open at once (`open_delay` 0).

## Creep numbers (seeds 1–200; from the last run, with hedge_adjacent_obscurement 0.25)

| | barrage | control |
|---|---|---|
| Able defenders at the creep start / known then | 6.19 / 3.20 | 9.99 / 0.01 |
| Peak suppression, morale, routing | 72.5, 40.0, 48% | 1.2, 90.0, 0% |
| Defenders who fired; median spread first to last | 4.08; 8.0 s (140 seeds with 2+ firers) | 9.64; 7.6 s (200) |
| Bands reached (150/100/50/20 m) | 192/114/87/54 | 198/0/0/0 |
| Known at those bands (mean over the seeds that reached each) | 1.43/1.60/1.47/1.13 | 0.39/–/–/– |
| Found by fire / within 50 m / never | 1.33 / 0.00 / 1.42 | 4.26 / 0.00 / 5.47 |
| US creep casualties | 2.73 | 5.95 |

- Checklist bar (reported, not asserted): fog at the creep start in 76/200 seeds.
- **Failing:** of the 87 barrage seeds that reached 50 m, **0** knew strictly more survivors there than at 150 m.

## Diagnosis so far, and the next step

Discovery is not progressive, and nobody is found close in. Contributing causes:

- **Spotting at a hedge is still slow up close.** Spotting multiplies posture, motion, obscurement, concealment and the observer factor. Against a still, prone man the rate stays low even within 40 m, for example about 0.01 per second when the observer is pinned.
- **The squad gets close only when the defence has collapsed.** Survivors are routing or dead by then, and the count is of able defenders known.

Next: trace one barrage seed that reaches 50 m, per tick. Record the US observers' pairwise rates against each defender, and each defender's state. Then decide whether the close-range rule needs its own rate (a "visible at close range" floor, new key), or whether this outcome is the realistic one and should be reported to the owner.
