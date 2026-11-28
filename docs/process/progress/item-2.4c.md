# Item 2.4c: fix round progress (not mergeable yet)

## Done
- Review fixes 1-4: start and stop gates, `unmask_fire_cool_off`, `unmask_max_shifts`, anchor against creep, safe routes,
  live destinations instead of stale shift tiles, no shift over a same-tick drill move, no shifting after Hold.
- `MaskedGateTests`: each gate removed in turn makes at least one masked test fail.
- Checklist items 2.10 and 2.11 added; 2.4c text rewritten.
- The shifting-drill tests in `MaskedSquadTests` now hold suppression at zero (assertions unchanged): without that the
  squad pins itself with its own fire and the required gates keep the men where they are.
- Build clean; both headless Godot commands exit 0 with no ERROR lines; runner scenarios ran over seeds 1-50.
- Tests: 590 passed, 7 skipped, 1 failed.

## Left
- `ViewFollowUpTests.Incoming_fire_is_drawn_as_a_bearing_from_the_squad_toward_the_shooters_sector` fails ("only 4
  rounds came in", needs 20). Not changed.
- The behaviour behind it: in `fix-assault` seed 1 the base of fire (squad 2) shifts a little, then its own rounds pass
  its men (28 near misses by tick 200, none from anyone else but 4 enemy), and it goes Pinned, Broken, Routing. With
  shifting switched off it takes 8 of its own near misses by then, and is Shaken at worst.

## Measured, `fix-assault` base of fire, seeds 1-50

| | shifting off | shifting with the fixes |
|---|---|---|
| masked share of on-task man-time | 73.4% | 53.9% |
| man-shot-ticks per seed | 63 | 40 |
| stationary masked time in a pinned or broken squad | 27% | 78% |

So the drill as it stands makes this squad fire less. Cause (from code reading and the seed 1 probe): a player Fire order
holds a man's fire only when a comrade is within `mask_clearance` (0.5 m) of his line, but a round within
`near_miss_radius` (2 m) suppresses. Men unmasked by a comrade stepping away can then fire close past others. The probe
does not show which men's rounds did it. Narrower or wider route lanes made no difference to seed 1.

## Next step (owner decision)
1. Make men under a player Fire order hold fire when a comrade is within the near-miss radius, as the AI's own fire does.
2. Or change how friendly rounds suppress.
3. Or take the shifting drill out and document why men stay masked (the item allows that).

## Open question
Which of the three? Options 1 and 2 change existing behaviour, which this round was told not to retune.
