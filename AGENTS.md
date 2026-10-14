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
- SAP2000 is reference only. The complete MB workspace is archived under `archive/Mcotis.Desktop`; do not restore or
  extend it, and do not make any library project depend on it, the desktop application, WPF, SQLite or CSI libraries.
  `Mcotis.Checks` verifies this.
- The existing application is `src/Mcotis.Desktop`. It keeps working until a feature has moved to the new system. The
  legacy MB menu is gone; the separate Optimization menu for VCF/TAS stays last after a separator.
- One solver (user, 2026-10-12: "use general solver every where"): every analysis runs `Mcotis.Analysis.StaticSolver`.
  The application reaches it through `src/Mcotis.Desktop/Application/FemSolver.cs`, which hands over the application's
  planar model with the degrees of freedom Analysis Options sets — full 3D to start with, the XZ plane to analyse it in its plane; the native workspace calls it directly. Put no numerical method in the
  application. The main window is the standalone application — do not call it "Tab 07".
- The planar frame solver is archived (user, 2026-10-12): it is no longer part of the application. It lives, frozen,
  in `tests/Mcotis.Desktop.Checks/Archive/PlanarFrameSolver.cs`, only as the baseline `--general-solver` compares the
  application's analysis with. Do not develop it and do not call it from `src/`.
- No silent restraint (user, 2026-10-12: "neu ko restraint thi bao unstable thoi"): the solver does not hold or pass
  over a movement on the user's behalf. A degree of freedom that is on and that no member and no restraint holds is
  reported as an unstable model, with the joint and the movement, in both windows. The user decides: a restraint,
  another member, or the degree-of-freedom options — including those of cable joints (`AnalysisSettings.CableDofs`).
  The options are the user's too (2026-10-13: "can be set different degree of freedom rather than fixed it, if not
  proper then report in the log"): a form does not refuse a set because the model might not be analysable with it,
  and nothing corrects it — the run refuses it, and the run monitor reports the reason. A model starts in full 3D
  (same day: "make the planar use 3d as 3d is default"), and the run goes on where the loads allow (same day: "ETABS
  can run regardless"): in the main window a movement the stiffness does not resist and the loads of the case do no
  work on — a model in a plane turning about the line of its pins — is held, reported joint by joint as a warning,
  and the case is solved (`StaticOptions.HoldUnloadedMechanisms`; validated in `docs/solver/
  numerical-stability-diagnostics.md` §2.4). That is not a silent restraint and must not become one: a movement the
  loads act on, and a degree of freedom nothing holds at all (the rule above), still have no result. Do not choose
  the XZ plane for the user.
  One case is unchanged: the rotation of a frame joint whose every frame end is released is left out with a warning.
- Load cases (user, 2026-10-13: "implement this plan", then "continue this plan" — PR-01 to PR-03): a case is one of
  four kinds and keeps the case it continues from apart from the case whose stiffness it uses. The model, the rules
  (`Mcotis.Core.LoadCaseRules`, one set for both windows), the run plan, the files and the main window's Load Case
  Data hold all of it. P-Delta, linear static and case-specific iteration controls are refused at Run with the reason —
  never solved as a simpler case; modal cases on the unstressed stiffness are solved since 2026-10-14 (M1). A run solves its cases in the order of the Load
  Cases table, and a case that is not ticked but needed first is solved and said to be a prerequisite. The plan,
  what is done and what is gated (PR-04 on: the literature gate): `docs/analysis/LOAD_CASE_STATE_REFACTOR_PLAN.md`.
- One central model (user, 2026-10-13: "tao muốn model khởi tạo là trung tâm, có thể lưu đc, template kia chỉ là giúp
  model nhanh thôi, còn fem phải dựa vào 3d và loadcase"; "các tính năng import sap etabs export phải sure là xài
  đc"; "làm đi"): the main window is to work on one native `ModelDocument` — VCF, TAS and TemplateBuilding only
  generate objects into it, every analysis runs from it and its load cases, SAP2000 and ETABS are adapters around
  it, and Import / Export must keep working at every step. In progress, in slices that each pass the full checks.
  **The document becomes the source of truth of the main window; one window; the tied arch system in 3D by default**
  (user, 2026-10-14, the migration task M0–M8: "do all"): the legacy model becomes a read-only projection of the
  document for what has not moved yet, `NativeModelWindow` is retired when everything it has is in the main window,
  and the app opens on the tied arch system as a native document, in the 3D view, analysed in Full 3D — still a plane
  frame, held out of its plane by real section properties and restraints, never by a solver trick. The MB workflow is
  out of that task entirely: not ported, not tested, not deleted. The Optimization and Design menus are production
  tools and must give their frozen answers at every step (`tests/Mcotis.Desktop.Checks/Fixtures/production-baselines.json`,
  `--optimization-study`, `--column-design`); an intended difference is logged and approved by the user first.
  Optimization is offered by a generator flag stored in the document (`GeneratorOrigin`), never by the model's name.
  **Run FEM Analysis and every optimization case solve native documents since 2026-10-14** (`Application/FemDocumentRun`,
  M1): the model's `MainViewModel.NativeDocumentSession` (refreshed at load, import, autosave and when a run locks the
  model, until M2 makes it the model itself) and a document per candidate. Names reach the tables through one map,
  `Application/LegacyNames`; compare no names elsewhere. Modal cases run in the main window. Joint restraints are data
  of the model, and one rule gives the restraints a model is analysed, shown and exported with
  (`LegacyNativeDocumentMigration.Restraints`). `--native-parity` shows the run on the document against the run before
  it. State, limits and next steps: the handoff skill, "One central native model", and `docs/mcotis-standalone-plan.md`
  phases 5–7.
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

## Repository Checks — Lean Development Loop

- Build with `dotnet build MCOTiS.sln`.
- For ordinary changes, run **quick smoke checks**: `dotnet run --project tests/Mcotis.Checks` and `dotnet run --project tests/Mcotis.Desktop.Checks`. Both default runners intentionally omit the large historical/independent comparison suites.
- Test the **changed subsystem** with its existing targeted flag when necessary (e.g., `--general-solver`, `--template-building-ui`, `--fem-db`). Do not rerun every suite after each small UI/editing change.
- Run `dotnet run --project tests/Mcotis.Checks -- --full` and `dotnet run --project tests/Mcotis.Desktop.Checks -- --full` before a release. The full numerical suite is also mandatory when changing the FEM formulation, stiffness, DOF compilation, boundary conditions, equilibrium/instability detection, cables, nonlinear stages or result recovery. Keep the independent closed-form, archived solver and OpenSees benchmarks as verifiable evidence, not routine per-edit gates.
- For a bug fix, prefer **one focused regression** for the failure plus a representative success path. Avoid multiplying tests for equivalent input variations, duplicate assertions, or UI details that are not an actual behavior contract. Reuse fixtures and parameterization. Safety-critical failure paths still need explicit tests.
- Do not run `--sap-read`, `--ui-read`, or write to a live SAP2000 model unless the user asks for that live integration work.
- Preserve unrelated user changes, including local Visual Studio state.
