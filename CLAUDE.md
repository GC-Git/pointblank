# Point Blank

An original single-player, real-time WWII tactics game (US vs Germany, Normandy 1944,
2D top-down, 15–20 units per side). Same genre as the old Close Combat games, but it
shares no names, art, maps, UI layout or data files with them or any other game. The
owner wants to sell it someday.

- Spec: [vision & design](docs/superpowers/specs/2026-11-25-pointblank-vision-design.md)
- Checklist: [docs/checklist.md](docs/checklist.md)
- Starting a session: [docs/process/session-brief.md](docs/process/session-brief.md)
- Reviewing a PR: [docs/process/evaluator.md](docs/process/evaluator.md)

## Layout

| Path | What |
|---|---|
| `src/Sim/` | Pure C# deterministic simulation core. All game rules. |
| `tests/Sim.Tests/` | xUnit tests, run headless. |
| `data/` | JSON data tables (units, weapons, ammo, materials, organisation, command, fire, suppression, ...); every value has a `source`. Loader: `src/Sim/Data`. |
| `tools/Runner/` | Headless battle runner CLI and its scenarios. References `Sim`. |
| `game/` | Godot 4.7 .NET project. Rendering and input only. References `Sim`. |
| `PointBlank.sln` | All four projects. Godot builds this one (`solution_directory=".."`). |
| `docs/` | Spec, checklist, plans, process prompts. |

Requires .NET SDK 9 and the .NET 8 runtime (projects target `net8.0`, the default of
Godot 4.7), and Godot 4.7 mono at `C:\Godot_v4.7-stable_mono_win64`.

## Commands (PowerShell, from the repo root)

Build:

```powershell
dotnet build PointBlank.sln
```

Test:

```powershell
dotnet test PointBlank.sln
```

Run battles headless and save their stats (add `--replays <directory>` to keep one
replay file per seed):

```powershell
dotnet run --project tools/Runner -- --scenario wander --seeds 1..100 --out TestResults/stats.json
```

Re-run a saved replay and check its final hash (exit code 0 match, 1 mismatch, 2 bad
input):

```powershell
dotnet run --project tools/Runner -- --replay TestResults/replays/wander-seed1.replay.json
```

List placeholder counts in the data files (deterministic; optional directory argument):

```powershell
dotnet run --project tools/Runner -- --data-report
```

Build the C# project through Godot (headless):

```powershell
& "C:\Godot_v4.7-stable_mono_win64\Godot_v4.7-stable_mono_win64_console.exe" --headless --path game --build-solutions --quit
```

Run the game headless (loads the main scene and quits):

```powershell
& "C:\Godot_v4.7-stable_mono_win64\Godot_v4.7-stable_mono_win64_console.exe" --headless --path game --quit
```

Don't rely on the exit code alone: read the output and confirm there are no `ERROR`
lines.

## Rules

1. **`Sim` never references Godot** — no package, project or `using Godot`. Game rules
   go in `Sim`; `game/` only draws state and turns input into orders.
2. **`Sim` stays deterministic**: same seed and same orders give the same battle. No
   wall-clock time, no unseeded randomness, no machine-dependent floating point.
   Use `Fixed` (Q31.32) for numbers, `World.Rng` for randomness, `World.Tick` for time,
   and add every new piece of state to the state hash (`ISimSystem.AppendState`). A text
   scan fails the tests on banned APIs; its rules and allow list are in
   `tests/Sim.Tests/Determinism/`. It covers `tools/Runner/Scenarios/` too, because
   scenarios advance the battle. It can't see unnamed types, so check what new
   library calls return.
3. **No hard-coded historical numbers.** They belong in data files.
4. **Every data value has a source tag**: a reference (title, author/publisher, page or
   URL), `estimated` (with reasoning), or `placeholder`. Never present a made-up value
   as historical.
5. **Record every placeholder** (in the data with its tag, and in the table below until
   the data-file report from item 0.4 replaces the table).
6. **Original assets only**; data must be licence-clean.
7. **One checklist item per session, branch and pull request.** Tests from the item's
   "Done when" list are written first. Tick the item only when its PR is merged.
8. **Never push to `main`**, never force push, never merge your own PR. The owner's
   merge is the sign-off, except where the merge rule below applies.
9. **Delete only inside the repo**, using relative paths.
10. **Run `git push` and `gh pr create` as separate commands.** Combined in one line,
    `--base main` trips the push-to-main deny rule.
11. **Fog of war: UI and AI code may only use `SideView`** (`world.ViewFor(side)`). It shows
    the side's own units as copies, enemies only as spotted `ContactView` copies, and order
    status and issuing for its own units. The raw state of both sides (`World.GetSystem`,
    `OrgSystem.Squads`, live `Soldier`/`Squad` objects, `CommandSystem.StatusOf`,
    `SpottingSystem.KnownEnemies`, `MovementSystem.Execute`) is `internal` to `Sim`, visible
    only to `Sim.Tests` and the headless runner through `InternalsVisibleTo`. Never widen that
    list for `game/` or AI code, and never make those members public.
    `SpottingSystem.DebugTruth()` is the one public truth accessor, and it is for debug
    overlays only. Tests in `tests/Sim.Tests/Boundary/` enforce this by reflection.

## Merge rule (owner's standing instruction, 2026-11-25)

The coordinating session may merge a checklist item's PR when the independent evaluator's
verdict is MERGE and all tests pass, then start the next item. It stops and waits for the
owner at every ⛳ sign-off item, on any evaluator FAIL or DON'T MERGE, or if a build
session is blocked. Build sessions never merge their own PR.

## Permissions

`.claude/settings.json` denies pushes to `main`, force pushes, `git reset --hard` and
deletes aimed outside the repo. These rules match command text, so they are a guard
rail and not a security boundary: for example, a plain `git push` while `main` is
checked out only prompts. Follow the rules above even where no pattern catches a case,
and don't rewrite a command to get around one. Optional allow rules:
[docs/process/settings-allow-proposal.md](docs/process/settings-allow-proposal.md).

## Placeholder values

The authoritative list is the report: `dotnet run --project tools/Runner -- --data-report`
(it lists every placeholder as `table/entry/value`). Currently every value in `data/` is a
sample placeholder, added by item 0.4 to exercise the loader; none is historical.

Data file format: `data/<table>.json` (tables: units, weapons, ammo, materials, organisation, terrain, postures, sight, movement, formation, command, spotting, fire, suppression) = `{"table": "...", "entries": [{"id": "...", "values":
{"<name>": {"value": "853", "unit": "m/s", "source": {...}}}}]}`. `source` is
`{"kind":"reference","ref":"title, author/publisher, page or URL"}`,
`{"kind":"estimated","reasoning":"..."}` or `{"kind":"placeholder","note":"..."}`. The loader
(in `Sim`, so the banned-API scan covers it) rejects a value without a valid source. Each table
lists its required values with a type: `n` (numeric: strict decimal, at most 9 integer and 9
fractional digits, so it always fits Q31.32) or `t` (text, kept as written); a value the schema
doesn't list is numeric unless its unit is `"text"`. Maps are not data tables: they live in
`data/maps/*.map.json` (format in `src/Sim/Map/MapFile.cs`; wall materials must be ids in
`materials.json`). To regenerate the test map after editing `TestMapBuilder`, run the tests once
with `PB_WRITE_TEST_MAP=1`.

**Switching on the no-placeholders test (item 7.7):** in
`tests/Sim.Tests/Data/NoPlaceholdersTests.cs`, change the one line
`private const string? SkipUntilItem77 = "...";` to `= null;`. The test then fails while any
placeholder remains.
