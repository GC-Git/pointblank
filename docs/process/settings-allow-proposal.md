# Proposed allow rules (owner applies these)

`.claude/settings.json` in this repo only **restricts** agents (deny and ask rules).
The allow rules below would let sessions work with fewer prompts. They loosen
permissions, so the agent did not apply them. If you want them, paste the `allow`
array into the `permissions` object of `.claude/settings.json` yourself. Deny and ask
rules still win over allow rules.

```json
"allow": [
  "Edit(/**)",

  "Bash(dotnet build *)",
  "Bash(dotnet test *)",
  "Bash(dotnet restore *)",
  "Bash(dotnet new *)",
  "Bash(dotnet sln *)",
  "Bash(dotnet add *)",
  "Bash(dotnet list *)",
  "Bash(dotnet format *)",
  "PowerShell(dotnet build *)",
  "PowerShell(dotnet test *)",
  "PowerShell(dotnet restore *)",
  "PowerShell(dotnet new *)",
  "PowerShell(dotnet sln *)",
  "PowerShell(dotnet add *)",
  "PowerShell(dotnet list *)",
  "PowerShell(dotnet format *)",

  "Bash(git status *)",
  "Bash(git diff *)",
  "Bash(git log *)",
  "Bash(git show *)",
  "Bash(git ls-files *)",
  "Bash(git fetch *)",
  "Bash(git add *)",
  "Bash(git commit *)",
  "Bash(git switch *)",
  "Bash(git push -u origin claude/*)",
  "Bash(git push origin claude/*)",
  "Bash(git push -u origin item/*)",
  "Bash(git push origin item/*)",
  "PowerShell(git status *)",
  "PowerShell(git diff *)",
  "PowerShell(git log *)",
  "PowerShell(git show *)",
  "PowerShell(git ls-files *)",
  "PowerShell(git fetch *)",
  "PowerShell(git add *)",
  "PowerShell(git commit *)",
  "PowerShell(git switch *)",
  "PowerShell(git push -u origin claude/*)",
  "PowerShell(git push origin claude/*)",
  "PowerShell(git push -u origin item/*)",
  "PowerShell(git push origin item/*)",

  "Bash(\"/c/Godot_v4.7-stable_mono_win64/Godot_v4.7-stable_mono_win64_console.exe\" *)",
  "PowerShell(& \"C:\\Godot_v4.7-stable_mono_win64\\Godot_v4.7-stable_mono_win64_console.exe\" *)"
]
```

Notes:

- Push is allowed only to `claude/*` and `item/*` branches, so any other form of
  `git push` still prompts.
- The PowerShell Godot rule (call operator with a quoted path) is not covered by the
  Claude Code permission docs. If it still prompts, approve once and keep the rule the
  app writes.
