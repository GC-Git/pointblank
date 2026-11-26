# Item 2.3 progress (work in progress)

Plan: `docs/superpowers/plans/2026-11-25-item-2.3-unit-ai.md`.

## Done and verified (`dotnet test PointBlank.sln`: 418 passed, 1 failed, 1 skipped)

- `src/Sim/UnitAi`: `UnitAiBrain` (reads only `SideView`, acts only through `IAiEffects`; boundary test), `UnitAiSystem`,
  `UnitAiRules`, `Bearings`. Data: `data/unit_ai.json`, `data/unit_ai_tasks.json`, `rout_threat_radius` in morale.
- `SideView`: `RoeOf`, `IncomingFire` (16-sector bearing + distance band, source id only for known contacts, own
  side's rounds excluded), `SoldierView.Moving`. `OrderKind.SetRoe`, `RoeMode`, `OrderNote.InvalidRoe`.
- `FireSystem` AI fire layer (team mask, below player Fire orders); `MovementSystem` held teams and local moves;
  `Sync` early-outs in fire and suppression (perf).
- Morale follow-ups: flee to nearest reachable fallback point, mean bearing of known enemies, Broken-ceiling note.
- Scenario results (seeds 1-100): reaction within 2 s 100/100 (open and hidden shooter); no soldier standing in the
  open under fire > 3 s: 0 violations in idle-open, idle-hidden and advance-under-fire (longest 2.2 s); firing line:
  mean 95% of riflemen fire within 10 s, 98/100 seeds >= 75% (min 71%); BAR task 20/20; 40 squads 3.05 ms/tick.

## Left

- **Fire and movement test fails:** groups alternate and one fires while the other moves in 61% of advance ticks,
  but in most seeds the squad barely advances (mean 7 m in 90 s; a few seeds advance 40-86 m). The stall/swap
  fallback did not change the numbers, so the bounding men seem to stay "moving" without net progress: suspect an
  interaction between held teams and the guide's slots/leash or member replanning in `MovementSystem.Step`
  (held men are skipped before `Replan`/`MoveMember`; free men's goals may oscillate). Next step: trace one
  failing seed (e.g. seed 2 of the test) per tick: guide `Seg/P`, each free man's goal and `AnyBeyondLeash`.
- Then run the full suite, delete this note, commit, push, open the PR.
