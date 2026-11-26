# Item 2.4: progress note (fix round after the DON'T MERGE on 593fa11)

Status: **out of tool budget. One test is red:** `ArtilleryTests.After_the_lift_each_man_bolts_on_his_own_and_no_tick_flips_the_whole_squad`. In 1 of 20 seeds, one tick still flips the whole routing squad's posture at once.

I have not run the full suite since the last fixes. On the earlier run (before the per-man start fix), two tests failed: the standing-in-open scan for `equal-fire` and `fix-assault-nobase` (routers standing 3.5 s). After the fix, all 6 standing-scan scenarios pass in a targeted run (0 violations, longest 2.1 s).

## Done

- **Desynced bolt.**
  - When a flee starts (`MoraleSystem.Flee`, with a unit AI in the world), every man is held where he is, in his own posture. `MovementSystem.SetHeldSoldier` is the new per-man hold, and it is hashed.
  - The unit AI releases each man at his own tick, drawn once per flee (`UnitAiBrain.BoltDelay`, hashed in `Mind.BoltAt`). The tick is his own deterministic draw within `bolt_delay` (3 s; the leader within a quarter of that). To that it adds:
    - `bolt_suppression_delay` × his suppression / 100;
    - `bolt_burst_delay` × (1 − his last burst distance / `bolt_burst_radius`).
  - Under fire, each man's rush and drop cycle is offset by his own draw within `rush_jitter` (1.5 s).
  - The squad keeps one shared move, so it still routs in one direction.
  - Each cowering man drops on his own reaction within `rush_jitter`: MoraleSystem no longer sets the whole squad prone. A routed, stopped squad under fire also drops man by man.
  - All new keys are placeholders in `unit_ai.json`.
- **Bolt spread:** 2.5 s first to last man in all 20 test seeds (was 0 s on the same tick).
- **Criteria asserts:** the fog bar is asserted (at least 30%; it measured 199/200). Suppression against the control is asserted (peak at least 30 above the control: 71.3 vs 1.2).
- **Standing at a hedge:** firing-position concealment now applies only to men whose eyes are below the hedge top. From 100 m, at 1 m behind the hedge: standing is spotted in 53 ticks, kneeling and prone never.
- **Duplicate `<summary>` docs:** fixed in MoraleSystem (`AddShock`), SpottingSystem (`LastFiredTick`) and FireSystem (`Masked`).

## Left

1. Find the remaining whole-squad posture flip in that seed and desync it. Then run the full suite. The squad-pin flip (the 2.1 rule: a pinned squad goes prone at once, in `SuppressionSystem`) is excluded from the test and reported, but it is still a squad-wide flip. Fixing it touches 2.1 behaviour, so it needs a decision.
2. Re-run the full suite, the Godot build, the pin and fix-assault scenarios, and the creep stats. Delete this note, commit and push.

## Numbers from the last full run (before the last two fixes)

- pin-open 100/100; pin-hedges 55/100; pin-open-nocas 100/100.
- fix-assault 93/100 with a base of fire, 13/100 without.
- Firing line 100/100 seeds.
- Creep (barrage vs control):
  - fog 199/200;
  - bands reached (150/100/50/20 m): 183/100/76/51;
  - known at those bands: 1.53/1.99/1.99/2.35;
  - found by close sight 0, by fire 1.45, never 1.73;
  - broken men at the hedge on arrival 0/76;
  - US creep casualties 3.41 vs 5.93.
- Tests: 448 passed, 2 failed (the two scans, since fixed), 1 skipped.
