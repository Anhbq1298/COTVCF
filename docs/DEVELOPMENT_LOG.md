# Mcotis development log

Every function added or changed in Mcotis is recorded here, newest first. The app shows this file under
**Help → Development Plans**, so every team member can see what exists, where to find it and how it was checked.

**Compulsory for everyone, people and coding agents alike.** A commit that changes anything under `src/` — the app or the
libraries — must add or update an entry in this file. The commit hook in `.githooks/commit-msg` (installed by the
build) refuses such a commit without one; write `[no-log]` in the commit message only for a change no user can notice
(a refactor, a comment, a test-only fix). The checks (`dotnet run --project tests/Mcotis.Desktop.Checks -- --dev-log`)
refuse an entry that breaks the format below.

Entry format — a `## date · title` line, then these fields, each on its own `- Field: value` line (a longer value
continues on the next lines, indented by two spaces):

- Status: Done, In progress or Planned
- Who: the developer(s), as in git (Unassigned for a plan nobody has taken)
- Where: how a user reaches it in the app (menu path, window, tab), or the code area when it has no UI
- What: what it does, in a sentence or two, with anything a user must know
- Checks: the check(s) that prove it (a `--flag` of the checks project), or "none yet"

## 2026-10-13 · Help → User Guide
- Status: Done
- Who: Philip Pham, Claude
- Where: Help → User Guide (F1); docs/USER_GUIDE.md
- What: An encyclopedia of Mcotis, like a wiki: an article for every function — where to find it, what it does, how to use
  it and what to watch out for — in categories that follow the menu bar (Getting started, Files and models, Define, Edit,
  Assign, Run, Results, Design, Optimization, MB study, Reference). The window has the contents by category with a search
  box (every word must match; Enter opens the first result or a results page), and each article shows an infobox with its
  category and menu path, its lead, a contents box, its sections, See also and the articles that link to it; links
  between articles navigate, with Back / Forward / Main page. The text is docs/USER_GUIDE.md, built into the app: whoever
  adds or changes a function updates its article (one `# Title` with Category, Where and See also lines, then Markdown
  with [[links]]).
- Checks: --user-guide (format, every link resolves, the app carries the current guide, every menu command of the main
  window is covered, search, and the window's main page, article page and navigation)

## 2026-10-13 · Run monitor opens on an analysis log laid out like SAP2000's
- Status: Done
- Who: markbui, Claude
- Where: Run → Run FEM Analysis → the run monitor (also Run → Analysis Monitor): the new tab "Analysis Log", beside
  "Equilibrium by Load Case" and "Cable Target-Force Convergence"
- What: Under the bar the run monitor now has three tabs instead of panels one above the other, and it opens on the
  analysis log. The log is laid out like the analysis log of SAP2000 — blue fixed-width text, section titles spaced
  out with the time at the right, NAME = value lines — and says what this solver does, in its own terms. It begins
  with the model in numbers (joints, restraints, frames, cables, load patterns, load assignments, cases and
  combinations), the load cases to be solved and the analysis options, then the equations of the run: active degrees
  of freedom, restrained, solved. Every solve has a block, the fit of the cable target forces first: how it starts
  (from zero, or from the pretension fitted for its stage), one line per equilibrium iteration — fitting run,
  iteration, unbalance, relative unbalance, joints converged, where the unbalance is largest — with the trial
  pretensions of a fitting run counted in one line, and at the end the stability check of the converged state, the
  smallest scaled pivot, the resultant of the internal forces, what the fit reached and the time. The log closes with
  the largest axial force, deflection and number of slack cables of every load case and combination, the output
  database and ANALYSIS COMPLETE. A case that is unstable or does not converge ends with an error block that names
  the joint and the movement; a run that fails or is cancelled closes with ANALYSIS INCOMPLETE. Text in the log can
  be selected and copied. The log replaces the collapsed "Diagnostic details" list; the equilibrium table and the
  convergence chart are as before, each on its own tab. The results of a run are unchanged.
- Checks: dotnet run --project tests/Mcotis.Checks (2,767 checks); default offline Mcotis.Desktop.Checks; --fem;
  --fem-sap; --general-solver; --template-building-ui; --dev-log

## 2026-10-13 · Column design optimization (RC, CFT, steel encased, mega column)
- Status: Done
- Who: Philip Pham, Claude
- Where: Design → RC Column / CFT (Concrete Filled Tube) / Steel Encased Composite Column / Mega Composite Column Design… →
  Optimization (tab after Members & Loads); run the FEM analysis and tick the members on Members & Loads first
- What: Finds the cheapest section of the column type that passes every ticked member and load case — one section for
  all of them — and gives its cost per metre of column. The inputs that drive the cost depend on the type: the concrete
  outline h × b (RC, steel encased, mega; square or not), the steel tube H × B or D and wall t (CFT), the encased I section
  h, b, tf, tw (steel encased, mega; mega also 4 and / or 6 profiles placed from an edge distance), the steel grade
  (S235 – S690, not for RC) and concrete grade (C30/37 – C185/200), stepped grade by grade, and the bars (sizes, bars per b
  face, h faces at the same spacing or a range, cover; optional inside a CFT tube). Each range is min / max / step; the
  effective lengths are those of Design Basis. Steel fy follows the plate thickness (tube wall or flange) by the EN 10025
  bands. Unit costs come from the material library (steel SGD/t per grade, concrete SGD/m³ per grade, rebar SGD/t) and
  can be edited in the tab. Cost = steel and bars by mass + concrete by net volume, with the links of RC / encased / mega
  columns; formwork is not priced. Run prices every combination, sorts them cheapest first and designs them in that order
  with the type's own design (as Run Design) until the 10 cheapest passing sections are found, in the background, with
  Stop; sections that cannot be built (wall too slender for its steel, profile not fitting the outline, bars without
  room) are left out first, and more than 250,000 combinations are refused (narrow the ranges). The results list each
  section's steel (with the fy used), concrete, bars, kg/m, m³/m, SGD/m, utilisation and the governing member and case;
  Use Selected (or a double-click) puts it on the Section tab, and Run Design then gives its full report. The CFT design
  now also takes bars inside the tube (Section tab, Bars inside the tube).
- Checks: --column-design (fy bands; cost per metre of a CFT and an RC candidate by hand; for each type the chosen section
  is the cheapest passing one — every cheaper candidate designed and failing — and Run Design passes it; bars in the tube
  add resistance)

## 2026-10-13 · Optimization results: Run and Chart sheets
- Status: Done
- Who: Philip Pham, Claude
- Where: Optimization → Optimization VCF… / Optimization TAS… → Run Optimization; Optimization → Optimization Results… →
  Run and Chart
- What: The results window has two sheets. Run is the case table as before. Chart is the parametric study of the run,
  written like the results section of a paper: up to 20 numbered figures — for every response that varies, its main
  effect against every parameter that varies (the mean over the other parameters with the min … max band; deflection,
  utilisation δ / limit with the 1.0 line, base horizontal force of one end, total cost, cost per metre of span, mass,
  base vertical force, max cable tension, frame axial force and moment, uplift), a sensitivity ranking (swing of each key
  response per parameter, as % of its mean), cost against utilisation for every case (passing / failing) and an
  interaction plot of the two parameters that move the deflection most — then a summary (study, compliance, each key
  response with its governing parameter, elasticity and linearity, the optimum, limitations) and numbered conclusions;
  Copy Summary puts the text on the clipboard. Each case now also records the base reactions of one end (the sum over
  the two column bases left of the middle — over all four the horizontal force is zero), the largest frame axial force,
  moment and cable tension in the deflection result, and the system mass.
- Checks: --optimization-study (known main effects, sensitivities, figure set, summary, one-end base force); the default
  suite (a 4-case run on the default model gives its Chart sheet); --etabs-import (after an Optimization TAS run over H1
  and bays the Chart sheet holds numbered figures, utilisation against L / 250, and a summary)

## 2026-10-12 · Tied arch system with an elevated cable
- Status: Done
- Who: Philip Pham, Claude
- Where: Edit → Tied Arch System… → Geometry (With elevated cable, H0) and Cable Force; Optimization → Optimization TAS…
  → Geometry (H0), Cable Force and Sections (tie cable)
- What: With elevated cable ticked, the arch springs at H0 (default 10 m) from the top of two columns of the arch's own
  section, pinned on the ground, and one cable — TIE, the VCF model's strand section 192T15.7 — ties the two springing
  points; H1 and H2 are measured from the springing (deck at H0 + H2). The Cable Force sheet sets the tie's target force
  (default 20,000 kN) in the Pretension load pattern. As for the VCF cables, the Pretension case applies it with the
  self-weight and fits the tie to it, and the SLS / ULS cases (and the P-Δ ULS cases) continue from it, prestressed then
  loaded. Optimization TAS of such a system adds H0 to its geometry ranges, the tie's pretension in steps (min / max /
  steps, as Optimization VCF) and the tie among its section roles; each case is fitted and run with its cable. The
  viewer still dimensions H1 from the springing.
- Checks: --etabs-import (the elevated system is built, pinned on the ground, meets the tie's target in Pretension and
  solves; Optimization TAS reproduces its run and steps the pretension); --result-tools-ui (the forms show and hide H0
  and Cable Force)

## 2026-10-12 · Mcotis opens with the tied arch system
- Status: Done
- Who: Philip Pham, Claude
- Where: Starting Mcotis
- What: The app now opens with the tied arch system — TAS - Tied Arch System, as File → Load TAS… opens it, ready to run —
  instead of the VCF model. File → Load VCF… still opens the VCF model; should TAS not load, the VCF model opens
  instead and the status bar says why.
- Checks: --etabs-import (a fresh start opens TAS with 30 joints and 59 frames, ready to run)

## 2026-10-12 · Optimization TAS
- Status: Done
- Who: Philip Pham, Claude
- Where: Optimization → Optimization TAS…; Optimization → Optimization Results…
- What: The parametric study of the tied arch system, run as Optimization VCF is. The form has min / max / step for H1,
  h (H2 = H1 + h, so the top chord is always above the arch crown; 2–5 m @ 1 by default), the number of bays and their
  spacing (sheet Geometry, the others starting fixed at the open system) and for the storeys above
  — SW 7–15 kPa @ 1, SDL 3–10 @ 1, LL 5–10 @ 2.5, floors n 5–10 @ 1 and the tributary width B (sheet Storey Force, with
  the SLS / ULS point load per deck joint) — candidate sections per role (arch rib, top chord, mega column, end strut,
  strut, brace), the deflection check (max δ of a top chord joint in the chosen result ≤ L / ratio, uplift limit) and a
  preview at the minimum, current or maximum corner. Run Optimization builds every case in memory as Edit → Tied Arch
  System would (an odd number of bays is skipped with the reason), analyses it with the internal FEM
  and lists it in the Optimization Results table — the same table, run loop, checks and cost as Optimization VCF, which
  now share one loop. The default space is 5,184 cases. Needs the tied arch system open (File → Load TAS… or Edit →
  Tied Arch System…); the open model is not changed. The span of an arch is L: the model view labels it "Span L = 120 m",
  and the form and the results give the limit as L/250 (480 mm for TAS).
- Checks: --etabs-import (a case with the model's inputs deflects as Run FEM Analysis of Load TAS; odd bays skipped;
  section candidates change the result; bad ranges refused); --result-tools-ui; --fem-model (Optimization VCF unchanged)

## 2026-10-12 · Edit → Tied Arch System
- Status: Done
- Who: Philip Pham, Claude
- Where: Edit → Tied Arch System…
- What: Builds the tied arch system from H1 (support → arch crown), H2 (support → top chord), the number of bays (even,
  6–40; default 12) and their spacing (default 10 m; span L = bays × spacing), and the storeys above: SW 7–15 kPa
  (step 1), SDL 3–10 kPa (step 1), LL 5–10 kPa (step 2.5), n 5–10 floors and the tributary width B (default 12 m). The
  form shows the point load on each top chord joint, SLS = (SW + SDL + LL) × s × B × n and ULS = (1.35 SW + 1.35 SDL +
  1.5 LL) × s × B × n (18,000 / 25,200 kN by default), and a live preview with the SLS loads and the arch height H.
  The arch is the parabola through the two supports and the crown (H1 at mid-span), with a joint at every vertical
  line; the layout, members, sections, releases, load patterns, cases and combinations are those of the TAS model
  (three braced bays at each end, pinned struts and braces). Apply / OK rebuilds the model: DEAD_above (SW + SDL) and
  LL_above (LL) on the top chord joints, half at the two end joints; LL_left / LL_right on each half. With the
  defaults it is the TAS model of File → Load TAS….
- Checks: --etabs-import (the defaults build the TAS model's joints, loads and deflections; H1 25 / H2 30 / 10 bays of
  12 m is on the parabola, ready to run, and its supports carry bays × the point load; out-of-range inputs refused)

## 2026-10-12 · Load TAS (Tied Arch System) and Load VCF
- Status: Done
- Who: Philip Pham, Claude
- Where: File → Load TAS…; File → Load VCF… (was File → Default Model)
- What: Opens the TAS model — the CoT 120 m composite arch deck (ETABS 22.2 .e2k, bundled with the app) — through the
  same import as File → Import From ETABS: the C90/105, S690 and B500 materials, CFT, steel encased and Section
  Designer sections, end releases, pinned bases, 6 load patterns with their 40 joint loads, 9 static load cases and 8
  combinations. ETABS splits every column, strut and brace at each story it crosses; Load TAS joins each straight member
  back into one frame (only where nothing else meets the joint, with no load, restraint or release there), so the model
  has 30 joints and 59 frames instead of 114 and 143, with the same results. The model is offline and ready to run
  without ETABS; it replaces the current model after a confirmation and shows straight away (the import summary appears
  only if the model is not ready to run). Import From ETABS still keeps the frames as ETABS splits them. With
  dimensions on, the XZ view of an arch model also dimensions the arch height H at the left, from the supports to the
  crown (H = 30 m for TAS), with a dashed line out from the crown; the arch is the frames of one section that leave two
  pinned supports inclined and join them. The model is named "TAS - Tied Arch System" in the title bar and messages.
  File → Default Model is renamed File → Load VCF… (the same built-in VCF model, COT Framing_V1.2_100m).
- Checks: --etabs-import (the bundled file is tests/etabs-import/CoT120.e2k; Load TAS is ready to run, has no joint
  left inside a straight member, deflects and reacts as the split model, and its arch is found with H = 30 m; a braced
  portal has no arch; the title is TAS - Tied Arch System); --fem-model (Load VCF… opens the default model); --dev-log

## 2026-10-12 · Run monitor shows the equilibrium of a run; no Degrees of Freedom tab in the main window
- Status: Done
- Who: markbui, Claude
- Where: Run → Run FEM Analysis → the run monitor (also Run → Analysis Monitor): the bar and the lines under it, and
  the new table "Equilibrium by load case". Run → Analysis Options… in the main window
- What: While a run goes, the monitor now says where its equilibrium stands. Under the bar: the degrees of freedom of
  the run (how many are active, solved and restrained) and the iteration in progress — the fitting run, the
  out-of-balance force against the accepted one, how many joints are in equilibrium and the joint and movement where
  the out-of-balance is largest. The bar counts the load cases that have ended. The table has one line per load case,
  the fit of the cable target forces first: solved degrees of freedom, iterations, out-of-balance, accepted value,
  joints converged, where the out-of-balance is largest, and the status — Converged, Not converged, Unstable, Failed
  (the reason is the status tooltip) or Stopped. The lines stay after the run and are written to Diagnostic details.
  A joint counts as converged when every solved degree of freedom of it is within the accepted out-of-balance. The
  results of a run are unchanged. In the main window, Analysis Options no longer has a Degrees of Freedom tab: nothing
  on it could be changed there, since that model is analysed in its XZ plane. The form opens on the cable settings,
  and an unstable-model message there no longer points to that tab. The native workspace keeps the tab, where the
  degrees of freedom, those of cable joints included, are a choice.
- Checks: dotnet run --project tests/Mcotis.Checks (2,767 checks); default offline Mcotis.Desktop.Checks; --fem;
  --fem-sap; --general-solver; --template-building-ui; --dev-log

## 2026-10-12 · Degrees of freedom of cable joints; a movement nothing holds is unstable
- Status: Done
- Who: markbui, Claude
- Where: Native workspace: Run → Analysis Options… → Degrees of Freedom, new group "Degrees of freedom of cable
  joints"; the Cable Force Fitting tab is now active there too. The unstable report: every run, in both windows
- What: A joint that only cables reach has the degrees of freedom the user gives cable joints (UX, UY, UZ by default),
  among the active ones; the rest are switched off there. The solver no longer holds or passes over a movement by
  itself: one that is on and that no member and no restraint holds — across a cable's line, at a cable end left free,
  at a joint between cables that all go slack — is reported as an unstable model, with the joint and the movement, and
  no results. Before, such a movement was left at zero with a note, or passed over silently in the main window. In the
  main window cable joints move in UX and UZ and that cannot be changed there, so a cable modelled in several segments
  through a joint only cables reach, or a cable end that nothing holds, now stops the run: model it as one cable, and
  hold every cable end with a frame or a support. The default model and the stored SAP2000 study have no such joint
  and give the same results as before. A cable that pulls in a movement switched off at its joint is held there as by
  a support; the model says so when it is compiled. Native files are now schema 3; older files open with UX, UY, UZ
  for their cable joints. In the native Analysis Options the fitting tolerance, iterations, frame stations and cable
  sag can now be edited, since the native run reads them.
- Checks: dotnet run --project tests/Mcotis.Checks (2,751 checks); default offline Mcotis.Desktop.Checks; --fem;
  --fem-sap; --general-solver; --template-building-ui; --dev-log

## 2026-10-12 · One solver everywhere: the general 3D solver runs the application
- Status: Done
- Who: markbui, Claude
- Where: Run → Run FEM Analysis and Run → Optimization VCF in the main window; Run → Run Analysis and the new Cable
  results tab in the native workspace (Edit → TemplateBuilding… / File → Open Native Model…); code in
  src/Mcotis.Analysis (StaticSolver) and src/Mcotis.Desktop/Application/FemSolver.cs
- What: Every analysis in Mcotis now runs the general solver of the standalone system: 3D frames connected at every
  point lying on them, tension-only cables with sag, cable target forces fitted in the case set to fit them, staged
  nonlinear cases, and internal forces and the deflected line at stations along every frame. The main window's model
  is planar, so it is run with the XZ degrees of freedom; its inputs, options and result views are unchanged, and so
  are its numbers — on the default model all 11,361 values of a run agree with the planar solver to that solver's own
  tolerance, and the stored SAP2000 study compares as before (cable forces within 3.8 %, deflections 3.2 %). In the
  native workspace Run Linear Static is now Run Analysis, and a model with cables shows their results. The planar
  solver is archived: it is no longer in the application and is kept, frozen, with the checks as the comparison
  baseline. Limits: small displacements; no shells, P-Delta or modal; about 1,000 joints in the main window, 600 free
  DOFs in the native workspace. With cable sag on, loads along the cables must be in the same proportion in every
  case (as self-weight is) and not act across a vertical cable; otherwise the run says so. The native workspace
  refuses target forces it cannot meet; the main window keeps showing the closest pretension with its note.
  Evidence: docs/analysis/cable-static-verification.md.
- Checks: dotnet run --project tests/Mcotis.Checks (2,705 checks); default offline Mcotis.Desktop.Checks;
  --general-solver; --fem; --fem-sap; --template-building-ui; --dev-log

## 2026-10-12 · The old name is gone: everything is Mcotis
- Status: Done
- Who: markbui, Claude
- Where: Whole repository; in the app: 07 FEM → Inputs → Save Model / Load Model, the app's data folder, printed reports
- What: The name COTVCF is replaced everywhere. Code: the namespaces COT_VCF.* are Mcotis.Desktop.* (Mcotis.* stays with
  the libraries), CotVcfConfigDto is McotisConfigDto, the application's checks moved from tests/COT_VCF.Checks to
  tests/Mcotis.Desktop.Checks, and the skills are skills/mcotis-ui-handoff and .agents/skills/mcotis-project. What a
  user can notice: a saved FEM model file says Mcotis.FemModel (one saved as COTVCF.FemModel still opens); the app
  keeps its data in %LocalAppData%\Mcotis, so the print setup, sessions and logs under %LocalAppData%\COTVCF are not
  picked up (copy ReportPrintSettings.json across to keep the print setup); a report generated before this change must
  be regenerated before it prints in a package, and the browser asks for the print setup once more. A branch made
  before this change must be merged with rename detection.
- Checks: dotnet build MCOTiS.sln; dotnet run --project tests/Mcotis.Checks; default offline Mcotis.Desktop.Checks
  (old format name still opens); --dev-log

## 2026-10-12 · Analysis Options form and TemplateBuilding native workspace
- Status: Done
- Who: Codex
- Where: Run → Analysis Options; Edit → TemplateBuilding; File → Open Native Model
- What: A staged WPF form shows active DOFs and cable fitting inputs. The existing planar solver uses the fitting
  tolerance, iteration limit, frame mesh and sag inputs; its DOFs remain XZ. TemplateBuilding creates a new native
  workspace with per-bay X/Y widths, per-storey heights and downward beam line loads, general section properties,
  fixed/pinned bases and optional self-weight. Native Save/Open, Undo/Redo, geometry view, DOF settings and linear
  static frame results are connected to the standalone libraries. No slabs or diaphragms; the native engine remains
  limited to frames and 600 free / 2,400 total DOFs. Native cable fitting and result-file storage remain unavailable.
- Checks: dotnet run --project tests/Mcotis.Checks (24 template checks); --template-building-ui; --result-tools-ui;
  --study-ui; --dev-log; dotnet build MCOTiS.sln

## 2026-10-12 · MCOTiS solution name
- Status: Done
- Who: Codex
- Where: Repository root — MCOTiS.sln
- What: Renames COTVCF.sln to MCOTiS.sln, preserving the projects and build configurations. Documentation and agent
  build commands use the new solution name.
- Checks: dotnet build MCOTiS.sln; --dev-log

## 2026-10-12 · One folder per project: src and tests
- Status: Done
- Who: markbui
- Where: Repository layout (no change in the app)
- What: The app is now the project src/Mcotis.Desktop (it was at the repository root with its code under src/). The
  libraries of the standalone system are its siblings in src/ (they were under mcotis/); the checks are
  tests/Mcotis.Desktop.Checks and tests/Mcotis.Checks. Build and run commands are unchanged except for the paths:
  dotnet build MCOTiS.sln, ./build-desktop.ps1, dotnet run --project tests/Mcotis.Desktop.Checks. A branch made before this
  change must be merged with rename detection. The build now finds SAP2000 27 when 26 is not installed.
- Checks: solution build; dotnet run --project tests/Mcotis.Checks; default offline Mcotis.Desktop.Checks; --dev-log;
  --result-tools-ui

## 2026-10-12 · Standalone 3D linear static frame engine
- Status: Done
- Who: Codex, Claude
- Where: src/Mcotis.Analysis (library only; the desktop app still analyses with its own planar solver)
- What: Solves 3D frames of the standalone model for linear static cases: point, line and member point loads,
  self-weight, end moment releases, the Full 3D / XZ / XY / YZ / custom degrees of freedom. It refuses what it does not
  cover instead of approximating it: cables, shells, nonlinear or staged cases, frames with a point inside their length,
  unstable models. Checked against closed forms, twelve OpenSeesPy models and the app's planar solver; what that
  evidence does and does not show is in docs/analysis/spatial-static-verification.md. One sign printed in the source
  book's stiffness matrix is a misprint and is recorded there.
- Checks: dotnet run --project tests/Mcotis.Checks (1447 checks); --spatial-frame-regression

## 2026-10-12 · One form for a member: information and assignments together
- Status: Done
- Who: markbui
- Where: Model view — right-click or double-click a frame or cable (Frame / Cable Information)
- What: The separate Member Assignments form is gone. The information form now changes the assignments itself, on its
  Assignments tab: section, local-axis angle and end moment releases of a frame; section, target pattern and target
  force of a cable. OK / Cancel / Apply; only what was changed is written. A locked model shows the same form read-only.
  A double-click opens it on Assignments, a right-click on Geometry.
- Checks: --result-tools-ui

## 2026-10-12 · Standalone 3D topology and DOF compiler foundation
- Status: Done
- Who: Codex
- Where: src/Mcotis.Core; Mcotis.Analysis; Mcotis.Persistence (library only, no desktop menu yet)
- What: Adds persisted Full 3D, XZ/XY/YZ and custom DOF masks, immutable topology compilation with free/restrained/inactive
  mappings, load/geometry diagnostics and schema-1 to schema-2 migration preserving XZ. Keeps unsupported shells explicit.
  The spatial stiffness solver, staged desktop dialog, P-Delta and modal algorithms remain planned with source gates.
- Checks: dotnet run --project tests/Mcotis.Checks (364 checks); default offline Mcotis.Desktop.Checks; solution build

## 2026-10-12 · Optimization tab on the menu bar
- Status: Done
- Who: markbui
- Where: Optimization (menu bar, between Help and MB)
- What: Optimization VCF…, Optimization Results… and Cancel Optimization have a menu of their own, set apart by a
  separator and highlighted. They are no longer under Run.
- Checks: --result-tools-ui; --study-ui

## 2026-10-12 · Selecting points, frames and cables on the model view
- Status: Done
- Who: markbui
- Where: Model view, 2D and 3D; Select tool (V); status bar
- What: A left click selects a point as well as a frame or a cable, in the 3D view too, where every point is now drawn.
  A right-click shows the object's information — Point Information for a point: coordinates, the members that meet at
  it, its support and its loads. V is the Select tool; Esc only clears the selection and no longer leaves Pan. The
  status bar counts the selected frames, cables, points and shells (the app has no shells yet). Selecting by a window
  stays 2D only: a drag in 3D rotates the model.
- Checks: --result-tools-ui

## 2026-10-12 · Help → Development Plans and the compulsory development log
- Status: Done
- Who: Philip Pham
- Where: Help → Development Plans; docs/DEVELOPMENT_LOG.md
- What: Shows this log in the app — in progress and planned work first, then what is done, newest first. Writing an entry
  is compulsory for every change of the app: the build installs .githooks, whose commit-msg hook refuses a commit that
  changes src/, App.xaml or src/Mcotis.Desktop/Mcotis.Desktop.csproj without a log entry ([no-log] only for changes no user can notice); AGENTS.md
  tells every coding agent to write the entry.
- Checks: --dev-log

## 2026-10-12 · Cable pretension in Export to ETABS
- Status: Planned
- Who: Unassigned
- Where: File → Export to ETABS…
- What: ETABS has no cable target-force load, so Export to ETABS writes cables as tension-only frames without their
  Tab 03 pretension. Carry the pretension across (for example as an equivalent strain or temperature load).
- Checks: none yet

## 2026-10-12 · P-Δ in the internal solver
- Status: Planned
- Who: Unassigned
- Where: Run → Run FEM Analysis
- What: ETABS / SAP2000 nonlinear cases with P-Δ are solved first-order by Mcotis; models imported from ETABS (the CoT arch)
  rely on P-Δ in their design combinations.
- Checks: none yet

## 2026-10-12 · Export to ETABS
- Status: Done
- Who: Philip Pham
- Where: File → Export to ETABS…
- What: Writes the Mcotis model into the model open in ETABS after a read-only preview and an OK: frames matched to ETABS
  frames by their end coordinates (in either direction), sections, materials, releases, supports, every joint and frame
  load, load cases and combinations, plane-frame analysis. Stories follow the joint levels in a model that never had
  objects. CFT / encased / Section Designer sections go as general sections with their true self-weight; cables as
  tension-only frames (no pretension, see the plan above). The ETABS model is not saved.
- Checks: --etabs-export; --etabs-export-live (opt-in: a private hidden ETABS, round trip)

## 2026-10-12 · Import From ETABS
- Status: Done
- Who: Philip Pham
- Where: File → Import From ETABS (without a running ETABS: an .e2k file)
- What: Reads the model open in ETABS (read-only, its database tables) or an .e2k file into an offline Mcotis model ready to
  run: joints (label@story), frames, materials, sections (I, box, pipe, CFT, steel encased, Section Designer with ETABS's
  own A / I and true self-weight), releases, loads, load cases and combinations; Tab 01 fitted to the ETABS joint loads.
  Run FEM Analysis no longer needs truss chords or cables, so an arch runs.
- Checks: --etabs-import; --etabs-live (opt-in, read-only)

## 2026-10-12 · Strand Bundle for cable sections
- Status: Done
- Who: markbui
- Where: Define → Section Properties → Cable Sections… → Add / Modify
- What: A cable section worked out from its strands (number × area of one strand) and named by the convention, e.g.
  192T15.7; strand section management and section drawing.
- Checks: offline suite

## 2026-10-12 · Save Config As and Object Information
- Status: Done
- Who: markbui
- Where: File → Save Config As… (Ctrl+Shift+S); right-click a member on the model view → Object Information
- What: Saves the configuration under a new name. Object Information shows a frame's or cable's geometry, assignments and
  loads as ETABS does; Modify Assignments edits them (double-click a member opens the same form).
- Checks: offline suite

## 2026-10-12 · Edit → Delete, Import From / Export to SAP2000
- Status: Done
- Who: Philip Pham, Claude
- Where: Edit → Delete (Del key, not while typing); File → Import From SAP2000; File → Export to SAP2000…
- What: Delete removes the joints, frames and cables selected on the model view after an OK / Cancel confirmation (no
  undo); as in SAP2000 a deleted joint takes the members connected to it. Everything that names a removed object follows
  (member properties, SAP loading, Tab 01 loaded points and point loads, Tab 02 chord groups — the baseline is captured
  again —, Tab 03 cables, Tab 04 output lists); FEM results are cleared and the model becomes an offline "· edited"
  model. Import From SAP2000 brings the model open in SAP2000 in, read-only, ready to run: geometry, member properties,
  materials and sections as SAP2000 defines them, load patterns and loads, cases and combinations, the Tab 02 chord groups
  (TTrPt / MTrPt / BTrPt joints, else the three highest levels of horizontal frames) with the baseline, Tab 03 cables with
  their SAP target forces, and Tab 01 fitted to the SAP joint loads (a pattern uniform per tributary length becomes qz on
  those joints, B = 1 m and 1 storey — set the real B and storeys before a parametric study); a summary says what was
  imported and whether Run Analysis is ready. Export to SAP2000 first previews, read-only, what it would change in the
  open SAP2000 model, then after OK writes materials, sections, geometry (members and joints not in Mcotis are removed),
  section / local axis / releases / modifiers, pins, load patterns, Tab 01 loads, cable target forces, load cases (new
  nonlinear ones with P-Δ and large displacements), combinations and run flags. It needs kN-m units, unlocks a locked
  model only after confirming (SAP2000 deletes its results) and does not save: review and save in SAP2000, or close
  without saving to discard. The SAP2000 calls are checked against SAP2000 27's API but have not yet been run against a
  live SAP2000 — try it on a copy of a model first.
- Checks: --sap-import (import ready to run and runs; Tab 01 reproduces the SAP joint loads; chords without the COT
  names; the export carries the imported model back and is previewed before it is written); the default suite (Delete of
  a cable and of a joint with its members on the default model, the edited model runs)

## 2026-10-11 · New Config, the blank model and the desktop build
- Status: Done
- Who: markbui
- Where: File → New Config; the start screen (Load Default Template / Continue with Blank Model); Edit → VCF System…;
  build-desktop.ps1
- What: A new configuration can start blank: define the materials and frame / cable sections, then create the VCF
  system under Edit → VCF System…, which gives the new members the sections chosen there. The start screen offers the
  built-in template (now File → Load VCF…) or a blank model; menus that need geometry stay off until there is some.
  build-desktop.ps1 builds the app (FreshBuild configuration) into bin/ and puts a Mcotis shortcut on the desktop.
- Checks: --blank-ui; --fem-db; --fem-model

## 2026-10-11 · Result pickers, result values and the toolbar
- Status: Done
- Who: markbui
- Where: Result → Show Deformed Shape… (F5), Show Member Forces… (F6), Show Joint Reactions… (F7); the toolbar;
  Post-Processing → Output Settings; Display → View Options
- What: F5 / F6 / F7 open a picker for the load case (and N, V or M for member forces) of the deflection, member force
  and reaction views. Values on the model view are written in compact units with a fixed number of decimals (MN, MN·m)
  and never upside down; the stored results stay in kN and kN·m. Cable force diagrams are scaled so crossing cables
  stay legible, and Optimization VCF shows its force ranges in MN. The toolbar buttons are regrouped with new icons,
  and Output Settings moved under Post-Processing.
- Checks: --result-tools-ui; ViewerResultDisplayChecks (default offline suite)

## 2026-10-11 · Frame local axes
- Status: Done
- Who: markbui
- Where: Assign → Frame → Local Axes…; Display → Frame local axes; selection on the 3D view
- What: Sets a frame's local-axis angle (in-plane inertia rotates with it, kept when saved) and draws the axes of the
  selected frames; members can be selected in 3D.
- Checks: --fem-model

## 2026-10-11 · Bundled default model and FEM databases
- Status: Done
- Who: markbui
- Where: File → Default Model; File → Load Config / Save Config (.cotfem.db); Run → Unlock FEM Model
- What: The default model is a bundled MCOTiS database; a configuration is one .cotfem.db; a run locks the model and writes
  its output database, Unlock deletes the outputs.
- Checks: --fem-db; --fem-model

## 2026-10-11 · Load cases, Set Load Cases to Run, FEM reports
- Status: Done
- Who: markbui
- Where: Define → Load Cases / Load Combinations; Run → Run FEM Analysis; Post-Processing → FEM Live Results (HTML)…
- What: Load cases defined in the app (Default Setup: Pretension, SLS, ULS), chosen to run before each run; FEM results
  reported live in HTML.
- Checks: offline suite

## 2026-10-11 · Storey force and loaded points
- Status: Done
- Who: markbui
- Where: Edit → VCF System… → Storey Force sheet
- What: Tributary width, storeys and qz per load pattern; the loaded points picked from a list or on the preview.
- Checks: offline suite

## 2026-10-11 · Optimization VCF
- Status: Done
- Who: Philip Pham; markbui
- Where: Run → Optimization VCF…; Run → Optimization Results…
- What: Runs the geometry × cable force × storey force × section variants with the internal solver; deflection check
  (span / ratio and an uplift limit on the bottom chord) and total cost per case.
- Checks: OptimizationChecks (offline suite)

## 2026-10-11 · Composite sections and column design
- Status: Done
- Who: Philip Pham, Claude
- Where: Define → Section Properties → Frame Sections (CFT, Steel Encased, Mega Composite); Design → RC / CFT / Steel
  Encased / Mega Composite Column Design… → Design Basis, Section, Members & Loads → Run Design
- What: Composite frame sections with transformed properties and cost per metre. Column design from the FEM forces of the
  selected FEM candidate: RC to SS EN 1992-1-1 + Singapore NA (strain compatibility, nominal curvature or stiffness,
  shear, biaxial, detailing), CFT and steel encased to SS EN 1994-1-1 extended by BCA BC4:2025 (plastic N–M, buckling
  curves, second order, biaxial, shear, local buckling and scope rules), mega columns by the CTBUH method. Before a run the
  Design Reference dialog shows the design code and its references, the effective lengths l0,y / l0,z (m), partial
  factors and grade limits (Design Basis), the section and materials, filled from the member's FEM section (Section), and
  the members and load cases (Members & Loads; frames selected on the model view are ticked). The 2D model's moment bends
  the major axis; the minor axis carries imperfections. Run Design opens Design Results: a summary per member (governing
  case, NEd, MEd, VEd, utilisations, OK / CHECK / FAIL) above the step-by-step calculation with clause references; Copy /
  Save Report.
- Checks: --column-design (sections and capacities against hand calculations, pass / fail of members); MegaColumnChecks
  (offline suite)

## 2026-10-10 · Desktop menu UI, materials and section libraries, VCF System
- Status: Done
- Who: Philip Pham
- Where: menu bar (File · MB · Define · Edit · Assign · Run · Result · Post-Processing · Display · Design · Help);
  Define → Materials…, Section Properties; Edit → VCF System…
- What: ETABS-like desktop layout, offline FEM models, material / section libraries, the VCF geometry editor with an
  editable bay count, display tools.
- Checks: offline suite

## 2026-10-07 · Quantity take-off and FEM results tree
- Status: Done
- Who: markbui
- Where: MB → 08 Quantities; Result → Results Table…
- What: Concrete volume and steel mass by assigned section, live from the connected SAP2000 model, with section figures;
  FEM results browsed by load case.
- Checks: --quantity-connected (opt-in, live); QuantityTakeoffChecks (offline suite)

## 2026-10-06 · FEM analysis, cable sag, FEM against SAP2000
- Status: Done
- Who: Philip Pham; markbui
- Where: Run → Run FEM Analysis; Run → Analysis Options (cable sag); Run → Analysis Monitor
- What: The internal 2D solver with SAP loading, frame meshing, pretension stages, cable sag, the run monitor and the 3D
  view; member forces compared with SAP2000.
- Checks: --fem-sap

## 2026-10-06 · Report printing and PDF export
- Status: Done
- Who: markbui
- Where: MB → 06 Post-Processing → reports; toolbar Print All Reports
- What: Print Setup and PDF export of the study reports (headless Chromium); a module's report regenerated on its own.
- Checks: ReportPrintChecks (offline suite)

## 2026-09-15 · Member forces and support reactions
- Status: Done
- Who: Nicholas3120
- Where: MB → 04 Output Settings; MB → 06 Post-Processing
- What: Member-force and support-reaction extraction from the study and their interactive HTML reports.
- Checks: MemberForceReportChecks; SupportReactionReportChecks (offline suite)

## 2026-09-09 · Cable forces, deflection report, cable sync
- Status: Done
- Who: aBui
- Where: MB → 03 Pretension; MB → 06 Post-Processing
- What: Cable force report with design capacities, deflection report by candidate, cable Sync modes (Same Values / Ratio /
  Offset / Same Step), reuse of built child models.
- Checks: CableForceReportChecks (offline suite)

## 2026-09-08 · Cable Sync UX (plan Phase 6)
- Status: Planned
- Who: Unassigned
- Where: MB → 03 Pretension
- What: Master / follower hierarchy with resolved-force preview, immediate orphan / cycle validation, explicit action when a
  master is removed (docs/implementation-plan.md, Phase 6).
- Checks: none yet

## 2026-09-08 · SAP operations and recovery UX (plan Phase 7)
- Status: Planned
- Who: Unassigned
- Where: top bar connection; a Recovery Center
- What: Connection health and model fingerprint, a Recovery Center over the run journal, Open Candidate in SAP
  (docs/implementation-plan.md, Phase 7).
- Checks: none yet

## 2026-09-08 · Feature-oriented presentation refactor (plan Phase 8)
- Status: Planned
- Who: Unassigned
- Where: code: src/Mcotis.Desktop/Presentation
- What: Focused feature view models and views, typed outcomes, more regression checks (docs/implementation-plan.md, Phase 8).
- Checks: none yet

## 2026-09-06 · Output settings and the SAP2000 iteration study
- Status: Done
- Who: Anh Bui; aBui; AnhBui
- Where: MB → 04 Output Settings; MB → 05 Iteration; Run → Run Study
- What: Output groups prepared on the parent model, configuration save / load, the study run by 1–3 SAP2000 workers with
  child models per candidate.
- Checks: --study-ui; StudyExecutionChecks (offline suite)

## 2026-09-05 · First release
- Status: Done
- Who: AnhBui
- Where: MB → 01 Loading, 02 Truss Height, 03 Pretension
- What: The Mcotis candidate workflow: loading criteria, truss-height and pretension candidates on the SAP2000 model.
- Checks: offline suite
