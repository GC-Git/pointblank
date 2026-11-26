# Item 2.3 progress (one open question for the owner)

Plan: `docs/superpowers/plans/2026-11-25-item-2.3-unit-ai.md`.

## Round 2: fixed

- **Fire-and-movement stall, root cause:** friendly fire. The 1.7 hold-fire rule holds a shot only when a friend is
  within `mask_clearance` (0.5 m) of the line, but rounds within `near_miss_radius` (2 m) are near misses. The covering
  group fired past its own bounding men; the near misses (shared across the squad within 10 m) suppressed the squad
  until it was Pinned, and `CommandSystem` holds a pinned squad's move. Traced on seed 2: suppression 43 to 60, Pinned
  at tick 138, `Held/Pinned` after. **Fix:** AI fire holds while a friend is within the near-miss radius
  (`FireSystem.AiClearance`; player orders keep the 1.7 rule). Bounding groups also move at MoveFast speed while the
  others are held (a bound is a rush), and the earlier stall/swap fallback was removed.
- **Firing line, worst seeds:** tile snapping put neighbours 2 m apart (within the AI clearance), and the BAR team
  (at the front of the column) walked across the line's front to its flank. **Fix:** line slots spaced by clearance +
  one tile, tiles chosen laterally clear of every line tile; the line forms one tile ahead of the front man so movers
  pass behind the firers; the task tile keeps clear of the line's lanes; a man holding fire keeps his shot credit (up
  to one round) and fires the moment the line clears.

## Open question (blocks the "60% under fire" assertion)

Under fire from a German Gruppe at 230 m in the open (even with its MG crew removed), the US squad is suppressed to
Pinned before or soon after it starts moving (the item 2.1 rule: an MG pins a squad in the open in 80%+ of seeds, and a
pinned squad holds advance moves). It then breaks and routs. So a mean advance of 60% of the unopposed distance under that
fire contradicts the 2.1 and 2.2 tuning, not the drill. The test asserts 60% in contact (covering fire, enemy holding
fire), and under fire it asserts that every slow seed spent most of its time pinned or broken. Owner decision needed:
retune suppression or pinning, change the scenario (range, cover), or accept that a squad under that fire is pinned.
