# Mcotis Agent Guidance

## Shared Agent Instructions

- Treat all relevant guidance under `.claude/` as agent-agnostic project guidance. It applies to Codex and other coding agents even though the directory is named for Claude.
- Use the repository skills under `.agents/skills/` to discover and route that guidance. When a task matches a skill, read its `SKILL.md` completely before changing code.
- Keep `.claude/` as the source of truth for the shared structural-engineering and SAP2000 material. The Codex skills are adapters; do not duplicate the full guidance into them.
- If new guidance is added under `.claude/`, treat it as applicable immediately and update the matching `.agents/skills/` adapter when discovery or routing needs to change.

## Instruction Precedence

When guidance conflicts, use this order:

1. The user's current request and the repository's current code, tests, and verified runtime behavior.
2. `skills/mcotis-ui-handoff/SKILL.md` for current Mcotis product and UI decisions.
3. Task-specific guidance under `.claude/skills/` and `.instructions.md`.
4. Generic examples or suggested package lists in the skills.

In particular, preserve the compact engineering UI described by the Mcotis handoff even when the generic structural UI skill shows older or more spacious examples. For SAP2000, verify API signatures against the installed `CSiAPIv1.dll` or official CSI documentation because library examples may target another API version.

## Direction: The Standalone System

Decided by the user on 2026-10-12; it takes precedence over older guidance that says otherwise.

- Mcotis is being rebuilt as a standalone system: the library projects `src/Mcotis.Core`, `src/Mcotis.Application`,
  `src/Mcotis.Persistence` and `src/Mcotis.Analysis`. The plan, its decisions and the state of each phase are in
  `docs/mcotis-standalone-plan.md`; read it before working on the new system.
- SAP2000 and the MB pages are reference only. Do not extend them, and do not make any of those library projects
  depend on them, on the desktop application, on WPF, on SQLite or on CSI libraries. `Mcotis.Checks` verifies this.
- The existing application is `src/Mcotis.Desktop`. It keeps working until a feature has moved to the new system. The
  MB menu stays last on the menu bar, after a separator.
- One solver (user, 2026-10-12: "use general solver every where"): every analysis runs `Mcotis.Analysis.StaticSolver`.
  The application reaches it through `src/Mcotis.Desktop/Application/FemSolver.cs`, which hands over the application's
  planar model with the XZ degrees of freedom; the native workspace calls it directly. Put no numerical method in the
  application. The main window is the standalone application — do not call it "Tab 07".
- The planar frame solver is archived (user, 2026-10-12): it is no longer part of the application. It lives, frozen,
  in `tests/Mcotis.Desktop.Checks/Archive/PlanarFrameSolver.cs`, only as the baseline `--general-solver` compares the
  application's analysis with. Do not develop it and do not call it from `src/`.
- Validate before showing a result (user, 2026-10-12): nothing a new solver capability computes is put in a result
  view, a report or a document as a result until it has passed its checks against closed forms, against the archived
  planar solver where that covers the case, and against OpenSees where it can be modelled there. Record the evidence
  and its limits in `docs/analysis/`; the current records are `spatial-static-verification.md` and
  `cable-static-verification.md`.

## Repository Layout

- `src/` holds one folder per project: `Mcotis.Desktop` (the WPF application: `App.xaml`, `Presentation`, `Application`,
  `Domain`, `Configuration`, `Geometry`, `Infrastructure`, `SAP2000`, `Resources`, `UI`) and the library projects above.
- `tests/` holds the check projects — `Mcotis.Desktop.Checks` for the application, `Mcotis.Checks` for the libraries — and the
  fixtures the application's checks read by path (`tests/fem-sap-benchmark`, `tests/etabs-import`).
- `bin/` at the repository root is the built application (`./build-desktop.ps1`); ordinary builds write to each
  project's own `bin/` and `obj/`.
- Run builds and checks from the repository root: the checks find fixtures and documents by paths relative to it.

## Development Log (compulsory)

- Every change that adds or changes a function of the app must add or update an entry at the top of
  `docs/DEVELOPMENT_LOG.md`, in the same commit. Help → Development Plans shows that file to the whole team, so it is how
  everyone checks what exists, who built it, where to find it and how it was verified.
- Entry format (the file's header has the details): `## yyyy-mm-dd · title`, then `- Status:` (Done, In progress or
  Planned), `- Who:`, `- Where:` (menu path / window / tab), `- What:`, `- Checks:` (the `--flag` of the checks, or "none yet").
  Newest first. Record planned work and known gaps as Planned entries.
- This applies to every team member and every coding agent, without being asked. The build installs `.githooks`
  (`core.hooksPath`); `.githooks/commit-msg` refuses a commit that changes anything under `src/` without
  the log. Use `[no-log]` in the commit message only for a change no user can notice. `--dev-log` checks the format.

## Repository Checks

- Build with `dotnet build MCOTiS.sln`.
- Run the new system's checks with `dotnet run --project tests/Mcotis.Checks`. They need no SAP2000 installation.
- Run offline checks with `dotnet run --project tests/Mcotis.Desktop.Checks`.
- Do not run `--sap-read`, `--ui-read`, or write to a live SAP2000 model unless the user asks for that live integration work.
- Preserve unrelated user changes, including local Visual Studio state.
