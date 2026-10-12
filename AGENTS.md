# COTVCF Agent Guidance

## Shared Agent Instructions

- Treat all relevant guidance under `.claude/` as agent-agnostic project guidance. It applies to Codex and other coding agents even though the directory is named for Claude.
- Use the repository skills under `.agents/skills/` to discover and route that guidance. When a task matches a skill, read its `SKILL.md` completely before changing code.
- Keep `.claude/` as the source of truth for the shared structural-engineering and SAP2000 material. The Codex skills are adapters; do not duplicate the full guidance into them.
- If new guidance is added under `.claude/`, treat it as applicable immediately and update the matching `.agents/skills/` adapter when discovery or routing needs to change.

## Instruction Precedence

When guidance conflicts, use this order:

1. The user's current request and the repository's current code, tests, and verified runtime behavior.
2. `skills/cotvcf-ui-handoff/SKILL.md` for current COTVCF product and UI decisions.
3. Task-specific guidance under `.claude/skills/` and `.instructions.md`.
4. Generic examples or suggested package lists in the skills.

In particular, preserve the compact engineering UI described by the COTVCF handoff even when the generic structural UI skill shows older or more spacious examples. For SAP2000, verify API signatures against the installed `CSiAPIv1.dll` or official CSI documentation because library examples may target another API version.

## Development Log (compulsory)

- Every change that adds or changes a function of the app must add or update an entry at the top of
  `docs/DEVELOPMENT_LOG.md`, in the same commit. Help → Development Plans shows that file to the whole team, so it is how
  everyone checks what exists, who built it, where to find it and how it was verified.
- Entry format (the file's header has the details): `## yyyy-mm-dd · title`, then `- Status:` (Done, In progress or
  Planned), `- Who:`, `- Where:` (menu path / window / tab), `- What:`, `- Checks:` (the `--flag` of the checks, or "none yet").
  Newest first. Record planned work and known gaps as Planned entries.
- This applies to every team member and every coding agent, without being asked. The build installs `.githooks`
  (`core.hooksPath`); `.githooks/commit-msg` refuses a commit that changes `src/`, `App.xaml*` or `COTVCF.csproj` without
  the log. Use `[no-log]` in the commit message only for a change no user can notice. `--dev-log` checks the format.

## Repository Checks

- Build with `dotnet build COTVCF.sln`.
- Run offline checks with `dotnet run --project tests/COT_VCF.Checks.csproj`.
- Do not run `--sap-read`, `--ui-read`, or write to a live SAP2000 model unless the user asks for that live integration work.
- Preserve unrelated user changes, including local Visual Studio state.
