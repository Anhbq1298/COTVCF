---
name: cotvcf-project
description: Build, modify, debug, or review the COTVCF WPF desktop application, including its candidate workflows, configuration project, compact UI, tests, and SAP connection seams. Use for implementation work in this repository; do not use for unrelated WPF applications.
---

# COTVCF Project

Before changing COTVCF behavior or presentation, read
[`skills/cotvcf-ui-handoff/SKILL.md`](../../../skills/cotvcf-ui-handoff/SKILL.md) completely. Treat it as the current product handoff and preserve its invariants unless the user explicitly changes them.

Use the repository's current code and tests to confirm that the handoff is still accurate. Keep changes to the existing application within its Presentation, Application, Domain, Configuration, Geometry, Infrastructure, and SAP2000 boundaries.

The standalone system under `mcotis/` is separate work with its own rules: read `docs/mcotis-standalone-plan.md` and the "Direction" section of `AGENTS.md` first. SAP2000 and the MB pages are reference only there.

For UI work, also use the `structural-engineering-ui` repository skill. For SAP2000 API or model-integration work, also use the `sap2000-oapi` repository skill.

## Verification

- Build with `dotnet build COTVCF.sln`.
- Run offline checks with `dotnet run --project tests/COT_VCF.Checks.csproj` when behavior changes.
- Live SAP reads, UI-read checks, and all model writes require an explicit task that places them in scope.
