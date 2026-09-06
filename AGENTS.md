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

## Repository Checks

- Build with `dotnet build COTVCF.sln`.
- Run offline checks with `dotnet run --project tests/COT_VCF.Checks.csproj`.
- Do not run `--sap-read`, `--ui-read`, or write to a live SAP2000 model unless the user asks for that live integration work.
- Preserve unrelated user changes, including local Visual Studio state.
