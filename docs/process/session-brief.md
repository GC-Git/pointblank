# Session brief (builder)

Copy the block below into a new session and fill in the two blanks. Model: as marked on
the checklist item (S = Sonnet, O = strongest model).

---

Build checklist item **<NUMBER AND TITLE>** of Point Blank. Branch: **`item/<number>-<slug>`**
(or the `claude/...` worktree branch the app created).

1. Read `CLAUDE.md`, the spec
   (`docs/superpowers/specs/2026-11-25-pointblank-vision-design.md`) and your item in
   `docs/checklist.md`. If `docs/process/progress/item-<number>.md` exists, read it and
   carry on from there.
2. Write a detailed plan for this item only (`superpowers:writing-plans`), saved to
   `docs/superpowers/plans/YYYY-MM-DD-item-<number>-<slug>.md`. Then implement it
   yourself in this session (`superpowers:executing-plans`); don't ask which execution
   method to use, and don't fan out parallel agents.
3. Test first (`superpowers:test-driven-development`): write the tests for the item's
   "Done when" list before the code, watch them fail, then make them pass. Don't weaken
   or skip a test to get it passing.
4. Stay inside the item. List other problems you notice as follow-ups; don't fix them.
5. Verify with the commands in `CLAUDE.md` and keep the output.
6. Commit on your branch, push it, and open a PR against `main`. Put the verification
   output, any new placeholder values and any known gaps in the description. If `gh` is
   not installed, give the owner the compare URL
   (`https://github.com/GC-Git/pointblank/compare/main...<branch>`) with a title and
   description ready to paste. **Do not merge, and never push to `main`.**

**Limits**

- Turn cap: about 60 turns. As you approach it, stop and report.
- **If blocked** (tests can't be made to pass, a requirement is unclear or contradicts
  the spec, a permission is denied): stop and report what is blocking you and what you
  tried. Don't work around it.
- **If a usage limit hits or is close:** commit work in progress and write
  `docs/process/progress/item-<number>.md` with what is done (and verified), what is
  left, the next step, and any open question. Push the branch. Delete the note in the
  final commit before the PR is ready.

**Done when:** every line of the item's "Done when" holds, with evidence, and the PR is
ready for the evaluator.
