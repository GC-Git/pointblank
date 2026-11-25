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
| `game/` | Godot 4.7 .NET project. Rendering and input only. References `Sim`. |
| `PointBlank.sln` | All three projects. Godot builds this one (`solution_directory=".."`). |
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
   `tests/Sim.Tests/Determinism/`. It can't see unnamed types, so check what new
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
   merge is the sign-off.
9. **Delete only inside the repo**, using relative paths.

## Permissions

`.claude/settings.json` denies pushes to `main`, force pushes, `git reset --hard` and
deletes aimed outside the repo. These rules match command text, so they are a guard
rail and not a security boundary: for example, a plain `git push` while `main` is
checked out only prompts. Follow the rules above even where no pattern catches a case,
and don't rewrite a command to get around one. Optional allow rules:
[docs/process/settings-allow-proposal.md](docs/process/settings-allow-proposal.md).

## Placeholder values

| Value | Where | Why | Added by item |
|---|---|---|---|
| none yet | | | |
