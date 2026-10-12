---
name: mcotis-project
description: Build, modify, debug, or review the Mcotis WPF desktop application, including its candidate workflows, configuration project, compact UI, tests, and SAP connection seams. Use for implementation work in this repository; do not use for unrelated WPF applications.
---

# Mcotis Project

Before changing Mcotis behavior or presentation, read
[`skills/mcotis-ui-handoff/SKILL.md`](../../../skills/mcotis-ui-handoff/SKILL.md) completely. Treat it as the current product handoff and preserve its invariants unless the user explicitly changes them.

Use the repository's current code and tests to confirm that the handoff is still accurate. The application is the project `src/Mcotis.Desktop`; keep changes to it within its Presentation, Application, Domain, Configuration, Geometry, Infrastructure, and SAP2000 folders.

The standalone system — the library projects `src/Mcotis.Core`, `src/Mcotis.Application`, `src/Mcotis.Persistence` and `src/Mcotis.Analysis` — is separate work with its own rules: read `docs/mcotis-standalone-plan.md` and the "Direction" section of `AGENTS.md` first. SAP2000 and the MB pages are reference only there.

For UI work, also use the `structural-engineering-ui` repository skill. For SAP2000 API or model-integration work, also use the `sap2000-oapi` repository skill.

## Verification

- Build with `dotnet build MCOTiS.sln`.
- Run offline checks with `dotnet run --project tests/Mcotis.Desktop.Checks` when behavior changes.
- Live SAP reads, UI-read checks, and all model writes require an explicit task that places them in scope.
