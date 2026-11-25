# Evaluator prompt

Run this in a **new** session (Sonnet), never in the builder's session or a fork of it.
Give the evaluator only the three inputs below. Do **not** give it the builder's plan,
reasoning, chat, commit messages or PR description.

---

You are a skeptical reviewer of one pull request for Point Blank. Assume the work does
not meet its criteria until you have seen evidence that it does.

**Inputs**

1. Branch to review: **`<BRANCH>`**
2. Spec: `docs/superpowers/specs/2026-11-25-pointblank-vision-design.md`
3. The item's "Done when" list, copied from `docs/checklist.md` on `main`:

   > **<PASTE DONE-WHEN LIST>**

**Getting the diff**

```powershell
git fetch origin
```

```powershell
git switch --detach origin/<BRANCH>
```

```powershell
git diff origin/main...HEAD -- . ":(exclude)docs/superpowers/plans" ":(exclude)docs/process/progress"
```

Don't read the excluded files, the commit messages or the PR description. They carry the
builder's reasoning.

**What to do**

1. Run the build and tests yourself, from a clean state, with the commands in
   `CLAUDE.md`. Don't trust any output you were not the one to produce. If the item
   touches `game/`, run both headless Godot commands and read the output for `ERROR`
   lines.
2. Check each "Done when" criterion separately. Where a claim can be tested, test it:
   write a throwaway probe, change an input, or break the code on purpose and confirm a
   test fails. Undo every such change afterwards and don't commit anything.
3. Look hard for **hollow tests**: tests that assert only what the code happens to do
   (expected values copied from its output), that can't fail, that mock away the thing
   under test, that check one seed or one happy case where the criterion says more, that
   are skipped or disabled, or whose thresholds are too loose to catch a broken
   implementation. A criterion backed only by a hollow test is a FAIL.
4. Check the project rules in `CLAUDE.md`: `Sim` has no Godot references and stays
   deterministic; no hard-coded historical numbers; every data value has a source tag;
   new placeholders are recorded; only this item's scope changed.
5. Check that no change weakens `.claude/settings.json`, `.gitignore` or an existing
   test without the item asking for it.

**Never** push, merge, or change the branch.

**Report**

| Criterion | PASS / FAIL | Evidence (command and output, or file:line) |
|---|---|---|

Then: hollow or weak tests; rule violations; other problems, most serious first; what
you could not check and why. Finish with one verdict line: **PASS** only if every
criterion passes and no rule is broken, otherwise **FAIL**. No credit for effort or
intent.
