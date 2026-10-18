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

## 2026-10-18 · Optimization results: Member Utilization tab
- Status: Done
- Who: Philip Pham (with Claude Code)
- Where: Optimization → Optimization VCF / Optimization TAS → results window → Member Utilization (between Run and Chart)
- What: Every analysed case now keeps the utilization of all the members of its model, and the new tab lists them for the case chosen in its Case box (or selected on the Run sheet), most utilized first, with the governing result and a summary line. Cables: largest tension of the solved results over the design capacity (section area × cable strength × φ). Columns whose section a Design type takes (RC, CFT, steel encased, mega composite): the Design engine, with the Design window's code, method and factors, on the ULS results (all solved results when none is named ULS); an effective length not keyed there is the member's length. Other frames (chords, braces) have no design engine and are listed as not designed. Saved with the study; a study saved before this shows a note asking for a new run. Design runs per case, so a study with many column members takes longer.
- Checks: --optimization-study (cable ratio and not-designed frame on a synthetic case). The engine on a real column case is not covered yet, and --optimization-study's frozen-baseline comparison reports the default VCF model's cost and deflection moving; this change adds a field after those values are computed and does not alter them.

## 2026-10-18 · Consolidate pending branches and column-design work
- Status: Done
- Who: Codex
- Where: Main application; Design; retained study-report services; analysis plans
- What: Merged all pending remote branches into master and committed the local column-design changes. Kept the current main-window geometry implementation and retired workspace archive intact; removed the obsolete unused native-workspace controller brought by the older geometry branch. Current load-case and modal verification records remain authoritative; earlier conflicting specifications are retained under docs/analysis/archived-plans.
- Checks: solution build and quick library/desktop smoke checks pass. --column-design passes its engineering checks but reports 66 frozen design/model baseline differences, the pre-existing default-CFT change recorded below; baselines were not recaptured. Historical report suites were not run for this consolidation.

## 2026-10-18 · Column optimization: tw = tf and b = h options and the number of cases shown
- Status: Done
- Who: Philip Pham (with Claude Code)
- Where: Design → Steel Encased / Mega Composite Column Design… → Optimization tab
- What: A **tw = tf** tick next to the web range makes the web as thick as the flange, and a **b = h** tick next to the flange width makes the profile as wide as it is deep; that dimension is not stepped, so a run has far fewer cases. The tab shows the number of cases a run would design (size × steel grade × concrete grade × bar arrangement) above the Run button and recounts it whenever a range, grade, bar or profile input changes; over the 250,000 limit it says so.
- Checks: --study-ui (the count, with and without tw = tf, and after a range change; run past the unrelated assertions noted in the Design tab entry); --dev-log.

## 2026-10-18 · Column design: results in a Design tab; members show the effective length
- Status: Done
- Who: Philip Pham (with Claude Code)
- Where: Design → any column design → Design Reference dialog (Members & Loads, Design tab)
- What: Run Design no longer closes the dialog and opens a separate results window: the results (summary per member and the step-by-step calculation, Copy / Save Report) appear in a new **Design** tab placed before Optimization, so the basis, section, members and results are always in one window; Cancel becomes Close and Run Design can be repeated. The Members & Loads list shows each member's section, material and the effective length l0 of the Design Basis (following what is keyed in there) instead of the member's geometric length, which read 20 m against an l0 of 10 m.
  The summary table has a **Role** column after Member (Mega column, Top chord, End frames…, as Frame Information gives it for the VCF system; empty for a model that has no such roles).
- Checks: --study-ui (Design tab and member lines; that suite stops earlier on an unrelated viewer-supports assertion and at Assign → Joint → Restraints from uncommitted menu work, so the new check was run past them); --dev-log.

## 2026-10-18 · Default VCF model: CFT chords as a defined filled tube; CFT exports as a steel tube
- Status: In progress
- Who: Philip Pham (with Claude Code)
- Where: File → Load VCF… (Frame Sections); File → Export to SAP2000 / ETABS; Optimization → VCF; Design → CFT Design…
- What: The top and bottom chords of the default VCF model use CFT_SHS[1200x2000x150_S460]_C70/85 as a defined concrete filled tube (2000 × 1200 × 150 mm, S460, C70/85) instead of a general section with transformed properties only; area and inertias are unchanged. Exported to SAP2000 or ETABS a concrete filled tube is a steel tube (box or pipe) of the same outline and wall in the tube's steel, with modifiers for area, shear areas, torsion, I22, I33, mass and weight that carry E·A, E·I, G·J and the weight of the composite (`CompositeSapSection.Tube`). Because the section is now defined, its self-weight and cost come from its concrete and steel quantities: the Optimization VCF results change (case 1 deflection 254 → 230 mm, cost SGD 10.0 M → 16.5 M) and so do the CFT design forces of the default model. Their frozen baselines (`vcf/*`, `design/model/*`) are NOT re-captured yet; they wait for the user's approval.
  A new CFT section (Define → Section Properties → Add New Property…) starts in C100/115 and S690 when the library has them.
- Checks: --fem-model (the section, its members, the exported tube and its modifiers); --study-ui (new CFT defaults), --etabs-export, --sap-import, --dev-log; --optimization-study and --column-design fail on the frozen baselines until approved.

## 2026-10-17 · Default VCF model: mega columns of 3x3+4H700x90S690C90/105; mega columns export as rectangular RC
- Status: Done
- Who: Philip Pham (with Claude Code)
- Where: File → Load VCF… (the built-in default model); Define → Section Properties (library); Optimization → VCF; File → Export to SAP2000 / ETABS
- What: The four mega columns of the default VCF system (members 170, 181, 186, 187, the verticals at the span ends) carry the mega composite section 3x3+4H700x90S690C90/105: 3000 × 3000 concrete C90/105, 4 × I 700×700×70/90 in S690 (fy 650 MPa for the plate thickness), 96 Ø40 bars (A = 11.70 m², I33 = 9.76 m⁴); it is in the default Sections library. The outer mega columns (188, 191) and the chords are unchanged. The Optimization VCF results change with it (user approved 2026-10-17): the baselines `vcf/*` and the Run Design on the default model's forces `design/model/*` are re-captured; TAS and the guide's column designs are untouched.
  Exported to SAP2000 or ETABS, any mega composite column section becomes a rectangular RC section of the same outline and concrete grade, with property modifiers (area, shear areas, torsion, I22, I33, mass, weight) that give it the stiffness, self-weight and mass of the composite column in Mcotis (`Application/CompositeSapSection`). The torsion constant of the rectangle is the St Venant formula, so J matches to the accuracy of SAP2000's own value, not exactly.
  The User Guide (Load VCF, Export to SAP2000 / ETABS, Column design, Mega column design) describes both.
- Checks: --fem-model (the section, its four members and the exported rectangle with its modifiers); --optimization-study and --column-design against the re-captured baselines; --etabs-export, --sap-import.

## 2026-10-17 · Optimization → Compare with SAP2000
- Status: In progress
- Who: Philip Pham (with Claude Code)
- Where: Optimization → Compare with SAP2000…
- What: Shows the member force table of Export Results (Mega Column, chords, cables, … with Bending, Shear, Axial and Member size) with every force column split into Mcotis and SAP2000. The SAP2000 values are read, read-only, from the analysed model open in SAP2000 for the chosen load case / combination; its members carry the names Export to SAP2000 gave them (the Mcotis member names). Peak absolute values per row; bending is M3 and shear V2 of SAP2000 (in-plane, default local axes). Cables report 0 for bending and shear. Copy puts the table on the clipboard; Export to Word saves it as a landscape .docx with every force column split in two. Not yet verified against a live SAP2000 model.
- Checks: none yet (build only; needs a live SAP2000 run).

## 2026-10-17 · Post-Processing menu moved after Optimization
- Status: Done
- Who: Philip Pham (with Claude Code)
- Where: Main window menu bar
- What: The Post-Processing menu is now the last menu, after Optimization (File … Display, Design, Help, | Optimization, Post-Processing).
- Checks: quick smoke checks (menu bar order in Mcotis.Desktop.Checks).

## 2026-10-17 · Mega column design: l0 = 10 m and the Section tab from the member section
- Status: Done
- Who: Philip Pham (with Claude Code)
- Where: Design → Mega Composite Column Design… (Design Basis and Section tabs); the other column designs share the Section rule
- What: The mega composite column design starts with l0,y = l0,z = 10 m (Reset returns to it). The Section tab always shows the section of the first ticked member (its Member Section), when the dialog opens and whenever the first ticked member changes, instead of a section kept from an earlier design; the fields can still be edited for one run.
- Checks: quick smoke checks (Design Reference dialog); --dev-log.

## 2026-10-17 · Load combinations: stored SLS / ULS limit state
- Status: Planned
- Who: Unassigned
- Where: Load Cases / Combinations (main window); Export Results
- What: Export Results splits SLS and ULS by the words "SLS" / "ULS" in the case names, because a combination carries no limit state. Store the limit state (SLS, ULS or none) on each combination in the document, set it in Load Case Data, and let Export Results fill each part from it instead of from the names.
- Checks: none yet.

## 2026-10-17 · Export results in separate SLS and ULS parts
- Status: Done
- Who: Philip Pham (with Claude Code)
- Where: Result → Show Results → Export Results
- What: The dialog has an SLS part and a ULS part, each with its own load cases / combinations and result types, so one report can hold SLS deflection and loading next to ULS forces. SLS starts with every result ticked, deflection included. ULS starts with every result except deflection, which stays available. No case carries a limit state, so both parts list every solved case and the case names only set the initial ticks ("SLS" / "ULS"). A part without cases is skipped. A case ticked in both parts is reported once with both sets of result types. Cases ticked with no result type are refused, and the message names the part.
- Checks: --result-export --skip-pdf; quick smoke checks; --dev-log.

## 2026-10-17 · Export results with member force summary tables
- Status: Done
- Who: Codex
- Where: Result → Show Results → Export Results; Word and PDF reports
- What: Each selected case includes a blue-header table of Member, Bending (MNm), Shear (MN), Axial (MN) and Member size. All summary tables appear at the end of the Word/PDF report, after every figure and caption. VCF rows distinguish mega columns, chords and external/internal cables; other models retain member names. Values are independent absolute peaks over member ends and stored frame stations; selected force types control the columns, assigned section names give the sizes, and unavailable values stay marked.
- Checks: --result-export --skip-pdf (Word, tables and dialog); quick smoke checks; --dev-log; PDF HTML rendered and visually checked with Chrome. The installed Edge print process timed out in --result-export; the existing browser exporter is unchanged.

## 2026-10-15 · Geometry editing: grids and levels, view planes, Draw, Move, snapping
- Status: Done
- Who: markbui, Claude
- Where: Define → Grid Systems… / Levels…; Draw menu and the tool strip at the left of the model view; Edit → Move… /
  Move by Two Points / Delete; the PLANE bar above the model view; Display → View Options… (Ctrl+W)
- What: The plan of docs/geometry (branch feature/geometry-editing-plan), built in the main window. Grid systems
  (Cartesian, origin, rotation, X lines 1, 2, 3 …, Y lines A, B, C …, Quick Grid) and levels (Generate Levels) are
  saved with the document (.mcotis schema 7), undone as one step and no part of the analysis input — they make and move
  no joint. One view context: the plane (XY / XZ / YZ / 3D) and its level or grid line, shared by the projection box, the
  toolbar and the PLANE bar; a plane shows the members lying in it; the view holds still while the model is edited. Draw
  Point / Frame / Cable by clicks with snapping (points, midpoints, member and grid intersections, grid lines,
  perpendicular, nearest, ortho; Alt off, Shift ortho), chained, on the plane shown or in 3D on the work plane (never a
  guessed depth), and by coordinates; Generate Points at Grid Intersections; Move by DX DY DZ or two points; Delete lists
  what goes with the selection. Each edit is one named Undo step; a point within 1 mm of a point, a member of no length,
  doubled or along another, and a move onto a point are refused with the reason. Display Options hide points, frames,
  cables, grid lines and levels (hidden is not picked). TemplateBuilding gives its building its grid and levels.
- Checks: --geometry-editing (also in --full); Mcotis.Checks group "Geometry editing"; --template-building-ui

## 2026-10-15 · Geometry editing: what the first milestone leaves open
- Status: Planned
- Who: Unassigned
- Where: docs/geometry/GEOMETRY_EDITING_PLAN.md ("Implementation status", unchecked items)
- What: Grids and levels in the working-session autosave (crash recovery restores the model without them; the .mcotis
  keeps them); the native name sequence for drawn objects (names now follow the model's names, so a deleted name can be
  given again); drawing the first member of a blank model; end releases and local axes in the draw tool; a preview
  crosshair in Point by Coordinates; snap and display settings kept between sessions; members crossing a plane drawn as
  cut outlines; radial grids; Copy / Replicate / Mirror / Divide / Merge Joints; measured performance.
- Checks: none yet

## 2026-10-15 · Deformed-shape display dialog
- Status: Done
- Who: Codex
- Where: Result → Show Deformed Shape… (F5)
- What: The dedicated F5 box groups solved Case / Combo / Mode selection with automatic or user-defined scale,
  displacement magnitude or UX/UY/UZ contours, an automatic or entered signed range, undeformed wire shadow and
  cubic frame curve. Apply updates the 2D/3D viewer without editing the model; OK applies and closes, Close discards
  unapplied inputs. Modal values are labelled relative. Hinge-state dots are explicitly unavailable because there are
  no hinge results.
- Checks: --result-tools-ui, --main-modal, --spatial-3d

## 2026-10-15 · Optional 3D member centre-lines
- Status: Done
- Who: Codex
- Where: 3D model view → E toggle; Display → Extruded 3D members; Display → View Options → General Options
- What: Turn extrusion off to show thin frame and cable centre-lines in the 3D view, including static and modal
  deformed shapes. The original section-sized view remains the default. Joint hover, member selection, result contours
  and the current camera stay available when switching.
- Checks: --spatial-3d, --result-tools-ui

## 2026-10-15 · Spatial deformation and six-component joint hover in 3D
- Status: Done
- Who: Codex
- Where: Result → Show Deformed Shape… (F5) → 3D view; hover a joint
- What: Static deformed frames use the native X/Y/Z displacement at every station; modal frames curve in 3D from
  joint translations and rotations. The 3D joint hover card shows UX, UY, UZ, RX, RY and RZ, in mm/mrad for static
  results and as relative amplitudes for a mode shape. Displayed joint markers follow the magnified shape.
- Checks: --spatial-3d, --main-modal

## 2026-10-15 · Modal shapes in the main viewer for frame-and-cable models
- Status: Done
- Who: markbui, Codex
- Where: Define → Load Cases → Load Case Data; Result → Show Deformed Shape… (F5); Result → Modal Results…
- What: A modal case can use the converged first-order nonlinear state of a frame-and-straight-cable model. The solver
  freezes the cable taut/slack state and tension, adds its prestressed tangent and selected cable self mass, and never
  invents restraints or mass. F5 offers solved static and modal cases, then a mode-number dropdown; XY, YZ and 3D views
  include UY and label modal amplitudes as relative. A native model with no Tab 01 candidate-load definitions can run
  with a single loaded joint; its document loads no longer trigger the legacy tributary-width rule. Native imports
  retain their mass-source identities through load-table replacement, so their modal cases can run. Cable-only,
  sagged-cable, P-Delta and shell modes remain explicit refusals. Straight-cable modes are checked against a closed
  form and independent OpenSeesPy corotTruss fixture.
- Checks: --main-modal, --result-tools-ui, --full (both Mcotis.Checks and Mcotis.Desktop.Checks)

## 2026-10-15 · M9: tied arch startup in stable Full 3D
- Status: Done
- Who: markbui, Claude, Codex
- Where: Application startup; File → Load TAS…; Edit → Tied Arch System; Run → Run FEM Analysis; Optimization →
  Optimization TAS
- What: Mcotis opens its one main window on the tied arch system, with the model view in 3D and all six degrees of
  freedom active. The planar arch is stable out of its plane because the model carries its source data: ETABS's other-axis
  inertia and torsional rigidity for every section, the source's out-of-plane member releases, two pinned bases and 24
  UY bearing restraints. The general solver adds no restraint and holds no unloaded mechanism. The Full-3D main run and
  every TAS optimization case keep the frozen in-plane answer to round-off. With the current dense solver, the nine-case
  optimization check measured 436.9 ms/case versus the former XZ baseline's 86.6 ms/case (5.05×); a sparse solver remains
  the planned performance improvement, not a change of analysis path.
- Checks: --tas-3d (startup model / view / DOFs, source stiffness / releases / restraints, 10 stable static cases,
  zero out-of-plane response, XZ parity to 4.4E-15 m/rad and 8.2E-9 kN/kN·m, 304 optimization baseline values to
  2.8E-13 relative); dotnet build MCOTiS.sln

## 2026-10-15 · M8: one window — TemplateBuilding and native models in the main window
- Status: Done
- Who: markbui, Claude
- Where: Edit → TemplateBuilding…; File → Open…; Assign → Joint → Masses…; Define → Load Cases (linear static);
  the "Save changes?" prompt (New Model, Open…, Load VCF…, TemplateBuilding, closing the window); File → Open Native
  Model… removed
- What: The separate native workspace window is retired (archived in `archive/Mcotis.Desktop/NativeWorkspace/`, not
  compiled; its README maps each function to the main window). Edit → TemplateBuilding… generates the building into the
  main window, in the 3D view; File → Open… opens a `.mcotis` the native workspace saved. A native document comes in like
  an import (`NativeDocumentImport`, `MainViewModel.OpenNativeDocument`): names, sections (A, I33, I22, J), materials
  (E, ν, weight, mass density), releases, modifiers, restraints (none added), joint masses, loads, cases, combinations,
  mass sources, degrees of freedom and the document's identity are kept; a cable target force becomes the cable's
  pretension row; what the window's model cannot hold yet (shells, rotated local axes, releases other than M3, shear
  areas, loads along Y, envelope combinations …) is refused with every reason before the open model changes. Joint masses
  are model data in the main window, by joint name like restraints: Assign → Joint → Masses… (the native form, on the
  selected joints, one Undo step), Object Information, saved in the file, deleted with their joint; MS1 counts them.
  Linear static cases on the unstressed stiffness are solved in the main window (as the native workspace solved them;
  the engine's linear solve, verified against OpenSeesPy); on another case's stiffness they are still refused, and a
  model with cables is refused for them. A model with changes not saved asks "Save changes to …?" (Yes / No / Cancel)
  before another model takes its place or the window closes.
- Checks: --template-building-ui (rewritten for one window: the building in the main window, its run equal to its own
  document solved directly to 4.6E-18 m, reactions 680 kN, joint masses, modal on MS1, Save / Open, a native-workspace
  file, the prompt); --general-solver (the former native-workspace cable display: the engine directly, and the main
  window taking the document in); --mcotis-file (a native model the window cannot hold yet is refused with the reason);
  --case-editor

## 2026-10-15 · M7: design and optimization read the document and its six-component results
- Status: Done
- Who: markbui, Claude
- Where: Design → RC / CFT / Steel Encased / Mega Composite Column Design… (Run Design, Optimization tab); Optimization →
  Optimization Results…; File → Save / Open
- What: Column design takes each member's forces from the run's six-component results by frame identity
  (`Application/Design/NativeDesignForces`: a case as solved, a combination as the sum of its terms, as the planar results
  are combined) and its length from the document. The local axes map explicitly to the code's: M3 with V2 → y-y (the depth
  lies along local 2), M2 with V3 → z-z. The RC, CFT / steel encased and mega-column checks take the FEM moment about z-z
  into their minor-axis and biaxial checks (EC2 5.8.9, EC4 6.7.3.7 with the imperfection in either plane), check the shear
  along local 3, and refuse a case whose torsion exceeds 1 % of its largest moment (and 1 kNm) — the column checks do not
  design torsion and never drop it silently; the report states the mapping. A model in its plane gives exactly the earlier
  results (the frozen design baseline holds). The optimization study shown is saved beside the model
  (`<name>.results/optimization-study.json`, with its FEM outputs) and shown again on Open, marked as of the model before
  later edits when it is; a model without one opens with an empty results window. Material unit costs stay in the model's
  material library, saved in the .mcotis file and shared by Optimization VCF, TAS and column optimization; they are not in
  the native material definitions yet. Not done in M7: section / load / definition forms editing the document directly,
  reports reading names other than through `LegacyNames`, SAP2000 / ETABS adapters in a library of their own (Planned).
- Checks: --column-design (3D forces: axis symmetry, both-plane biaxial, minor shear, torsion refusal and the optimizer;
  production baseline 352 values, largest relative difference 1.5E-10); --mcotis-file (study saved, reopened, stale,
  emptied)

## 2026-10-15 · Planned: SAP2000 / ETABS as adapters of the document in a library of their own
- Status: Planned
- Who: Unassigned
- Where: File → Import From SAP2000 / ETABS, Export to SAP2000 / ETABS
- What: Task of 2026-10-14, step "Move the remaining consumers off the projection": import into a new native document and
  export from the document, in an interop library (`Mcotis.Interop`) the core libraries do not reference. Today the
  importers fill the main window's configuration, which is then made a document; the exports read the configuration.
- Checks: none yet

## 2026-10-14 · M6: the main window's file is a .mcotis document
- Status: Done
- Who: markbui, Claude
- Where: File → New Model, Open… (Ctrl+O), Save (Ctrl+S), Save As… (Ctrl+Shift+S) — formerly New Config, Load Config…,
  Save Config, Save Config As…
- What: Save writes the model as a native .mcotis document (`Mcotis.Persistence`, replaced in one step): the document of
  the main window's session, with the configuration the controls not yet moved still read, and beside it the file's run
  state — revision, lock and the output databases of a locked revision — so a locked model opens again with its results.
  Open reads it as it was saved: the same document and identity, a history of its own, clean, its generator flag as the
  file has it (a model saved detached is never flagged again on Open). An older .cotfem.db / .cotfem.json opens as an
  import: Save asks for a .mcotis file (offered under the same name) and the older file is never written. A damaged file,
  or a model of the native workspace (no main-window configuration yet — M8), is refused with the reason and the open
  model stays. The working-session journal (recovery) still keeps its internal working database; the built-in VCF model
  is still read from its bundled database. Topic "Configurations" of the user guide is now "Model files".
- Checks: --mcotis-file (new, in the quick run: Save / Open round trip, analysis equal to 0, locked results, detach,
  older file never written, refusals, journal); --fem-model (Save / Save As on .mcotis); --model-history; --fem-db;
  --fem-schema; --sap-import; --etabs-import

## 2026-10-14 · M5: the main viewport and joint restraints read the native document
- Status: Done
- Who: markbui, Codex
- Where: Main window model viewport; Assign → Joint → Restraints…; Restraints Summary; point information; Edit → Undo / Redo
- What: The shared 2D/3D viewport now projects its geometry and support symbols from the main workspace's authoritative
  `ModelDocument`, using one presentation-only adapter shared with the temporary native window. Joint-restraint lookup
  also reads the document. Assign Restraints commits the six-component native assignment and the transitional
  main-window payload atomically as one `DocumentSession` command, so its autosave does not add another Undo step.
  `NativeToLegacyProjection` remains only for controls not migrated yet; sections, loads and definitions move next.
- Checks: --model-history (native viewport projection, atomic restraint assignment, one-step Undo / Redo, autosave,
  fixed-support analysis and Save); Mcotis.Desktop.Checks

## 2026-10-14 · One parametric study engine for Optimization VCF and Optimization TAS
- Status: Done
- Who: markbui, Claude
- Where: Optimization → Optimization VCF / Optimization TAS → Run Optimization; `Application/ParametricOptimization.cs`
- What: The two optimizations are two configurations of one engine (`ParametricOptimization`): it walks a study's cases,
  has each built by the study's generator (`IParametricStudy.Candidate`: `VcfOptimizationStudy` through
  `VcfGenerator.WithBays` / `Candidate`, `TasOptimizationStudy` through `TasGenerator.Build`), costs it, analyses it with
  the general solver through the same document path as Run FEM Analysis, and judges it with pluggable evaluators (the
  deflection and uplift check, the structural responses). A future generator becomes optimizable by giving a study.
  No result changes: the §2 baselines are reproduced, at 0.86× (VCF) and 1.07× (TAS) of the baseline time per case.
- Checks: --optimization-study (§2 baselines and timing); --etabs-import (Optimization TAS); --generator-flag;
  Mcotis.Desktop.Checks --full

## 2026-10-14 · The generator flag: Optimization VCF / TAS offered by the model's generator, not its name
- Status: Done
- Who: markbui, Claude
- Where: Optimization → Optimization VCF / Optimization TAS (enabled, and the tooltip says why not); Edit → Re-apply
  Generator…, Edit → Detach from Generator; File → Load VCF / Load TAS, Edit → VCF System / Tied Arch System
- What: A model a parametric generator made carries its flag (`GeneratorOrigin` in the native document; the
  configuration's `generator`): the generator, its version, its parameters, and the generation key of each object
  (VCF: chord and line along the truss, other joints by name; TAS: joint by name; a member by its two joints). The
  generators (`ParametricGenerators.cs`: `IParametricGenerator`, `VcfGenerator`, `TasGenerator`) rebuild the system
  from the flag and validate the document against it: sections, loads, cases and the other compatible edits keep it
  optimizable; a generated object deleted, a joint moved, a member reconnected, a member added, a restraint changed on
  a generated joint disable it, each listed with the object and the reason. The Optimization menu reads the flag and
  the validation only (`CanOptimizeVcf` / `CanOptimizeTas`; before, a VCF-looking geometry or the model's name).
  Re-apply rebuilds the system from the flag's parameters and Detach removes the flag, each one step of the model's
  history. A configuration saved before the flag (the built-in VCF model included) gets the VCF flag when it is opened
  if the VCF generator validates it; a TAS configuration saved before cannot (its parameters were not saved), and an
  imported model — an .e2k of the tied arch included — has none. Intended differences (for the user's approval):
  an imported SAP2000 / ETABS VCF model is no longer offered Optimization VCF; Edit → Tied Arch System now holds every
  deck joint and every arch joint between the springings in UY, as the bundled ETABS model (and so File → Load TAS)
  does — in-plane results unchanged (`--optimization-study` reproduces the §2 baselines). Also fixed: deleting a joint
  that took a fan cable crashed the study preview when another cable followed that cable; the follower now keeps the
  force it had as its own target.
- Checks: --generator-flag (Load TAS / Load VCF flagged and valid; compatible edit; restraint change and deletions
  listed per object; Undo restores; Re-apply and its Undo / Redo; Detach and its Undo; other parameters and the
  elevated cable; Save / Open in both file forms; an imported .e2k not flagged; a VCF system through 12 and back to
  10 bays; moved, reconnected, added and renamed objects on the document); --optimization-study; --native-parity;
  --etabs-import; --user-guide; Mcotis.Desktop.Checks --full

## 2026-10-14 · Undo and Redo in the main window: the model's document history
- Status: Done
- Who: markbui, Claude
- Where: Edit → Undo (Ctrl+Z) / Redo (Ctrl+Y); the window title (`*` while the model differs from its file); File → Save
  Config
- What: Every change of the model in the main window is now one step of its native document's history (the document
  session of `Mcotis.Application`): the whole configuration is written into the document — the engineering model as
  native objects, and what the native model has no place for yet (section and material libraries, the Tab 01 / Tab 03
  setup, the extras of the case rows) as the document's application data (`NativeToLegacyProjection`, the one bridge).
  Undo and Redo move the session and restore the configuration the document holds. A model opened, imported or
  generated starts its own history; a locked model (results on hand) waits for Unlock. Dirty state is the session's:
  Save makes the model clean, Undo back to the saved document clean again. The core document (schema 6 of `.mcotis`)
  gained the application data and the generator flag of a parametric model (`GeneratorOrigin`, for Optimization, next),
  and an edit that replaces a document's content as one recorded step. This is the transitional form of the authority
  flip: the main window still edits its own model and writes each edit into the document; the parts of the
  configuration move into the native model one by one, and the bridge goes with the last of them.
- Checks: --model-history (two edits undone and redone, no step added by writing the restored model again, dirty and
  clean, Save, no Undo while locked); Mcotis.Checks group "Application data, generator flag and content replacement"
  (schema 6 round trip, the hash unaffected, undo, migration from 5, replacement as one step); --user-guide;
  Mcotis.Desktop.Checks --full; --template-building-ui; --result-tools-ui

## 2026-10-14 · Joint restraints are explicit and editable; the moment at a fixed support is in the reactions
- Status: Done
- Who: markbui, Claude
- Where: Assign → Joint → Restraints… and Restraints Summary; every model opened, imported or generated; Run FEM
  Analysis → reactions (Results Table column My, the label beside the support, its hover read-out); the output database
- What: A model that holds no restraint — the VCF system, an older configuration, an import that restrained nothing —
  gets the column-base pins written into it once, as real assignments (`MainViewModel.MakeRestraintsExplicit`). After
  that no rule adds a support: the run, the viewer and the exports use the restraints the model holds, a joint made free
  stays free, and a model with none says so. Assign → Joint → Restraints… edits the six components of the joints
  selected on the model view, with the fast restraints Fixed / Pinned / Roller / Free (OK / Apply / Close). The
  reactions carry the moment of a support that holds the rotation (My, counter-clockwise positive in the XZ elevation),
  combinations included; the output database has the column `my_knm`. User-visible change: Export to SAP2000 / ETABS of
  the VCF system now writes its pins as assignments the model holds (before, as a rule that left the target's own
  restraints alone).
- Checks: --fem (My = P L at a fixed cantilever, closed form); --native-parity (the moment compared with the run before
  M1); --sap-import (the VCF pins are assignments; a joint made free is free in the run, the viewer and the export);
  --result-tools-ui (the dialog); --user-guide; --etabs-import; --etabs-export; --fem-db; --fem-model

## 2026-10-14 · Output database v2 for six-component and modal results
- Status: Done
- Who: markbui, Codex
- Where: the FEM output database of Run FEM Analysis (`.cotfem` outputs), reports, Results Table
- What: Output schema v2 keeps the existing planar tables for their existing readers and adds separately normalized,
  identity-based tables for all six joint displacements/rotations and reactions, all six local frame station actions
  and movements, modal modes, shapes, participation and mass inventory. It records each solver result with its input
  hash and formulation version and restores the exact native result, object-name map and run notes when a locked
  working session reopens. Schema-v1 outputs still open as planar results and never acquire invented 3D/modal data.
  Results Table now has explicit Joint 6-DOF and Frame 6-component tabs; Modal Results reopens from the output database.
- Checks: --fem-db (v2 normalized rows, exact round-trip, schema-v1 read); --main-modal (six-component result tables);
  Mcotis.Desktop.Checks

## 2026-10-14 · Modal cases in the main window
- Status: Done
- Who: markbui, Claude
- Where: Define → Load Cases → Load Case Data (type Modal (Eigenvectors)); Run → Run FEM Analysis; Result → Modal
  Results… (Modes, Modal participation, Mode shape, Modal mass inventory; Show Mode Shape draws a mode on the model)
- What: A modal case of the Load Cases table on the unstressed stiffness is now solved by Run FEM Analysis with the
  static cases — the modal solver of the standalone system on the model's native document (the capability verified in
  `docs/analysis/modal-eigen-verification.md`). Its mass is that of its mass source (`MS1`, the members' self mass, is
  offered). The tables are those of the native workspace (one control, `ModalResultsView`, for both windows). A mode
  drawn on the model is a mass-normalized eigenvector, magnified and said to be a relative amplitude; showing a static
  result replaces it. Modal cases are not static results: they are not offered in the result pickers. Refused with the
  reason, as before: modes on the stiffness at the end of another case, models with cables (no verified prestressed
  tangent) and shells. A run needs a static case or combination beside its modal cases. Output schema v2 now persists
  the modes with the locked revision (see the Done entry above).
- Checks: --main-modal (six positive modes of the tied arch system, equal to the modal solver on the document solved
  directly, participation, the static results unchanged, the mode drawn, the refusal); --template-building-ui

## 2026-10-14 · Run FEM Analysis and the optimizations analyse native documents
- Status: Done
- Who: markbui, Claude
- Where: Run → Run FEM Analysis; Optimization → Optimization VCF / Optimization TAS (every case)
- What: Run FEM Analysis no longer hands the solver the application's own description of the model: it solves the
  model's native document (`MainViewModel.NativeDocumentSession`, brought up to the model when the run locks it) with
  `Mcotis.Analysis` — `FemDocumentRun`, the one analysis path of the application. Each case of Optimization VCF and
  Optimization TAS is made a native document the same way (`FemStudyService.AnalyzeCandidate`) and solved by the same
  path. The results are named for the tables, the viewer, the reports and Design by one map (`LegacyNames`, origin name
  → identity); they carry the solver's own six-component results too. The results of the main window are now tracked
  by the analysis input hash of the document they were computed from (Not run / Running / Current / Stale / Failed, the
  library's rule), and the Design dialog says which. A joint connected to no frame or cable is left out of the
  analysis, as the run always did, and the run says so. Nothing a user sees changes in the numbers: the run on the
  document gives what the run gave before to round-off (relative 2.4E-10 at most, 42,000 values over six models), and
  the optimizations reproduce their production baselines (VCF 6E-10, TAS 4E-13). Time per case: VCF 217 ms (was 249),
  TAS 105 ms (was 87, 1.22×, within the 1.5× budget — half of the first build's extra time was the new section check,
  now made once per run).
- Checks: --native-parity (the run on the document against the run before it, every result of six models);
  --optimization-study and --column-design (the production baselines); --fem-model; --run-monitor; --general-solver;
  --fem-db; --sap-import; --etabs-import; Mcotis.Checks --full; Mcotis.Desktop.Checks --full

## 2026-10-14 · TemplateBuilding: the material has a mass density
- Status: Done
- Who: markbui, Claude
- Where: Edit → TemplateBuilding… → Properties and Supports → Mass density (t/m³)
- What: The template's material had a mass density of 0, so a modal case with self mass only had no mass at all
  (`modal.mass.none`) unless joint masses were assigned. The form now has Mass density (t/m³): it starts at weight
  density / 9.80665 (8.00477 for 78.5 kN/m³), follows the weight density until a value is typed, and keeps a typed
  value. A building with its self mass alone now gives its modes.
- Checks: --template-building-ui (the field, and three positive modes of a template building on self mass only)

## 2026-10-14 · A frame section is asked only for the stiffness the active degrees of freedom use
- Status: Done
- Who: markbui, Claude
- Where: the analysis of the standalone system (`Mcotis.Analysis`): the native window's Run Analysis, and the native
  document of the main window (`--native-parity`)
- What: A section property is needed only where its stiffness term moves a degree of freedom that is on
  (`FrameStiffnessDemand`). A frame of the XZ plane analysed in the XZ plane bends about local 3 alone: its I22 and J
  act on UY, RX and RZ, which are off, so a section without them is analysed as it is. In full 3D the same terms act
  and the frame is refused by name (`static.section.stiffness`, `modal.section.stiffness`), saying which property is
  missing. The rule is the same for XY, YZ, custom sets and turned local axes (then I22 may be the in-plane inertia).
  Nothing is assumed for a term that acts. This closes the known gap of the native document: the model imported from
  SAP2000, whose frames hold no inertia out of the plane, is now solved from its document with the results of Run FEM
  Analysis (2,990 values, relative 1.9E-10), and is refused frame by frame in full 3D.
- Checks: Mcotis.Checks group "Section stiffness the active degrees of freedom call on" (closed form, XZ / XY / 3D,
  turned axes, modal); --native-parity (the gap is gone); Mcotis.Checks --full (3,081)

## 2026-10-14 · Production baselines of the Optimization and Design menus
- Status: Done
- Who: markbui, Claude
- Where: Optimization → Optimization VCF / Optimization TAS / Optimization Results; Design → RC / CFT / Steel Encased /
  Mega Composite Column Design (Run Design and the Optimization tab) — checks only, nothing changes in the app
- What: Before the main window moves onto the native document, what these production tools give is frozen in
  `tests/Mcotis.Desktop.Checks/Fixtures/production-baselines.json` (990 values): Optimization VCF on the default model
  with the user guide's nine cases (truss depth 18–22 m × LL_ABOVE 5–10 kPa, read from the real form) and Optimization
  TAS on the built-in system with its nine cases (h 2–4 m × LL 5–10 kPa) — every row, the summary line and the study
  report (sensitivities, summary with the flat parameters, conclusions, figures); column design and column design
  optimization of the four types on the guide's C1 / C2 forces; Run Design on the FEM forces of the default model.
  Every later change must reproduce them — analysis numbers to round-off, the rest exactly — or list the difference
  here with its cause and the user's approval, and capture again (`--baseline-capture`). The time per case is recorded
  (VCF 249 ms, TAS 87 ms) and a study more than 1.5× slower is reported. Recorded as it is: on the default model, two
  mega columns (186, 187) have an infinite major-axis utilisation in Run Design.
- Checks: --optimization-study; --column-design; both in Mcotis.Desktop.Checks --full; --baseline-capture (opt-in)

## 2026-10-14 · Moment at a fixed support in the reactions
- Status: Done
- Who: markbui, Claude
- Where: Run → Run FEM Analysis → Reactions (viewer, table, hover); the FEM output database and reports
- What: Since a model can be held on fixed supports (entry below), a support can carry a moment. Done 2026-10-14 with
  the explicit restraints (entry "Joint restraints are explicit and editable…", above): My, counter-clockwise positive
  in the XZ elevation, in the Results Table, beside the support in the viewer, in its hover read-out and in the output
  database. The printed FEM reports do not list it yet.
- Checks: --fem; --native-parity

## 2026-10-14 · Run FEM Analysis holds a model as SAP2000 or ETABS restrains it
- Status: Done
- Who: Codex, markbui, Claude
- Where: Run → Run FEM Analysis; Assign → Joint → Restraints; Object Information of a joint; File → Import From SAP2000 /
  Import From ETABS (summary); File → Export to SAP2000 / Export to ETABS (preview); Save / Load MCOTiS Config
- What: A model imported from SAP2000 or ETABS is analysed on the joint restraints that model has, component by
  component (UX, UY, UZ, RX, RY, RZ): a fixed support is fixed and a roller is a roller, no longer every support a
  pin at the lowest column bases. The restraints are part of the model: saved in `.cotfem.db` and `.cotfem.json`,
  moved with a renamed joint and removed with a deleted one, shown on Assign → Joint Restraints and in Object
  Information. A model that holds none — the VCF system, a configuration of an older version — keeps the pins found
  at its column bases, as before; where none is found the run reports what is not held. Supports are drawn, and
  reactions listed, at the joints held in the XZ plane (the TAS model also holds 24 joints in UY only). Exports: a
  model with restraints is written as it is — every component, and a joint it leaves free is freed in the target;
  a model without them only pins a column base the target leaves free and keeps the target's own restraints. The
  export preview says how many joints change. Both exports set the target's active DOFs to the model's. Known
  limits: the reactions table has the forces in X and Z only — the moment at a fixed support is not listed yet;
  restraints cannot be edited in the main window yet; Optimization VCF and Optimization TAS still analyse their
  candidates on pinned column bases, so for an imported model with other supports they differ from Run FEM
  Analysis; the changed export code has not been run against a live SAP2000 or ETABS (the checks use stand-ins).
- Checks: --fem (a cantilever on one fixed support, closed form); --sap-import (a fixed base among pins: run, export,
  a restrained joint deleted then run at once, Save / Load as .cotfem.json, the VCF model's pins exported as a rule);
  --fem-model (a connected model run before any autosave); --etabs-import; --etabs-export; --fem-db;
  Mcotis.Desktop.Checks --full; Mcotis.Checks --full

## 2026-10-14 · Main workspace hands every model to one native 3D document
- Status: In progress
- Who: Codex, markbui, Claude
- Where: Startup / Load VCF / Load TAS / Load Config; File → Import From SAP2000 / Import From ETABS; FEM working autosave
- What: The model open in the main workspace now also owns one `DocumentSession` / `ModelDocument`. VCF and TAS are
  recorded as generated templates with stable `GenerationKey` identities; SAP2000 and ETABS models are recorded as
  imports with their source names. The handoff preserves XYZ geometry, effective frame/cable properties, the joint
  restraints the model is analysed with (see the entry above), pattern loads, cable targets, load cases,
  combinations, mass sources and analysis DOFs, and the document can round-trip through `.mcotis`. The document is
  brought up to the model when it is loaded or imported, at each working autosave and when a run locks it. Since
  2026-10-14 Run FEM Analysis solves it (entry above); the controls and the viewer still read the window's own model.
  A model the document cannot express still opens and saves; its run is refused with the reason. The document is the same model as the one that is run: solved as it is by the standalone solver it
  gives the results of Run FEM Analysis to round-off (relative differences of the order of 1E-11; the check accepts
  1E-8) in every static case of the VCF system, the tied arch system with and without its cable, the ETABS import
  and a VCF model on a fixed base — 17,856 values. The gap first known — a frame whose section holds no bending inertia
  out of the plane (members of the SAP2000 check model) — is closed (entry above, 2026-10-14). Next: the
  run reading the document, VCF and TAS as native generators, the viewer reading the document, `.mcotis` as the
  window's file.
- Checks: --native-parity (the document solved against the run, and the known gap); --fem-db; --fem-schema;
  --sap-import (full stand-in import → FEM → export); --etabs-import (full offline import, native handoff, TAS
  generation and FEM); --etabs-export; Mcotis.Desktop.Checks --full; --dev-log; MCOTiS.sln build

## 2026-10-13 · Native modal eigen analysis with explicit physical mass
- Status: Done
- Who: Codex
- Where: Native model window → Define → Modal Eigen Case…; Assign → Joint Mass…; Run → Run Analysis; Modes,
  Modal participation and Mode shape amplitudes result tabs
- What: Native elastic frame models can define a named lumped mass source, include member `rho A L` self mass and
  six-component joint mass/inertia, extract mass-normalized Eigen modes, and inspect frequency, period, residual,
  UX/UY/UZ effective/cumulative mass, physical/free mass inventory and relative 2D/3D mode shapes. Free massless DOFs are condensed exactly rather
  than restrained or given fictitious mass. Native schema 5 persists the inputs and migrates schemas 3/4. A requested
  nonlinear/P-Delta stiffness state, cable mode or shell remains explicitly refused until its independently verified
  tangent/mass formulation exists; it never falls back to elastic stiffness.
- Checks: Mcotis.Checks --full (closed forms, mass conservation, persistence, refusals and two independently generated
  OpenSeesPy 3.8.0 modal models); Mcotis.Desktop.Checks --template-building-ui (3D modal run, results and bindings);
  both default smoke suites; MCOTiS.sln build; --dev-log

## 2026-10-13 · Analysis Options: degrees of freedom in the main window
- Status: Done
- Who: markbui, Claude
- Where: Run → Analysis Options… → tab "Degrees of Freedom" (beside "Cable Force Fitting"; also the toolbar)
- What: The main window's Analysis Options have a Degrees of Freedom tab again, with the choices its planar model
  has, each one a box that can be changed. For the model: RY, the rotation of the joints — switched off, no joint
  turns: the rotation is held at every joint, as by a support (it is not a release), and a moment applied to a joint
  is refused instead of dropped. For cable joints: UX and UZ — a joint that only cables reach has the ones that are
  ticked. Switching UX off there holds such a joint sideways, so a cable modelled in two segments through a joint
  nothing else reaches can be analysed; it used to stop the run as unstable, and the message of that instability now
  names this option. UX and UZ of the model are not offered: the model spans its XZ plane, and the solver refuses a
  translation that is off unless every joint has the same coordinate along it; UY, RX and RZ are not degrees of
  freedom of a planar model. The settings are used by Run FEM Analysis, Optimization VCF and Optimization TAS, are
  saved with the configuration (a file from before opens with everything on), and changing them clears the
  results. The run monitor counts and lists the degrees of freedom of each run accordingly, with "-" for one that is
  off. The form of the native workspace (Edit → TemplateBuilding…) is unchanged: it still offers 3D, the plane
  presets and custom sets.
- Checks: --fem (a cable in two segments with UX off for cable joints gives the tension, deflection and reactions of
  the one cable); --run-monitor (RY off against PL³/24EI; the counts of the run; a moment on a rotation that is off
  refused; a translation of the plane refused); --template-building-ui (the form); --full of
  Mcotis.Desktop.Checks and of Mcotis.Checks; --user-guide; --dev-log

## 2026-10-13 · Run monitor: load cases that open on convergence, numerical stability by joint and DOF, and their own log
- Status: Done
- Who: markbui, Claude
- Where: Run → Run FEM Analysis → the run monitor (also Run → Analysis Monitor, and the toolbar): tab "Load Cases",
  beside "Cable Target-Force Convergence"
- What: The run monitor is laid out again, in the application's own type; the SAP2000-style blue fixed-width log and
  the "Equilibrium by Load Case" tab are gone. On top: the run — Running, Completed, Completed with Warnings, Failed
  or Cancelled — the bar counting the load cases ended, the degrees of freedom as the equations were assembled
  (active | solved | restrained), start and elapsed time. Then a note on numerical stability that can be closed; the
  model in numbers under the name of its file (joints, restrained joints, frames, cables, shells, load patterns, load
  assignments, load cases and combinations counted apart, the supports); and one entry per solve of the run — per
  pretension stage the fit of its cable target forces, then its cases; a combination is not a solve and is not
  listed. An entry reads Pending, Running, Converged, Converged with Warnings, Not Converged, Numerical Instability,
  Failed or Cancelled with its time; a click opens it, several stay open, a failed one opens itself. Open, it shows
  Convergence, Numerical Stability and Instabilities (0 = none detected), the convergence in detail (iterations,
  tolerance satisfied or not, out-of-balance as measured, fitting runs, the checks the solver made), and its own log
  of what the solver reported, in order. When a degree of freedom is reported, a table lists the affected joints by
  name with their global coordinates and the six columns UX UY UZ RX RY RZ: the digits of accuracy lost (amber from
  6, red above 11), ∞ in bold red where a singularity was identified, - for a degree of freedom that is off, R for
  a restrained one, N/A where there is no estimate; the remarks name the degrees of freedom.
  The numbers are the solver's: it now estimates the digits lost at every solved degree of freedom as
  log10(Kii·(K⁻¹)ii) — which does not depend on the elimination order and is not CSI's number — and, when it cannot
  factorize a stiffness, finds the movements without stiffness and the degrees of freedom that move in them. A small
  pivot stays a finite number; ∞ is kept for an exactly zero stiffness, a joint only slack cables hold, and a
  movement the stiffness does not resist at double precision ("possible mechanism"). Non-convergence is not reported
  as an instability. The solution itself is untouched: results are what they were. Known limits: the thresholds
  (6 / 11) are options of the solver without a control in the app yet; the native workspace receives the same
  diagnostics but does not show them yet. Method, thresholds, differences from CSI and limits:
  docs/solver/numerical-stability-diagnostics.md.
- Checks: dotnet run --project tests/Mcotis.Checks (2,904 checks, 137 of them new); --run-monitor; --run-monitor-ui;
  default offline Mcotis.Desktop.Checks; --general-solver; --fem; --fem-sap; --template-building-ui; --dev-log

## 2026-10-13 · Run monitor: a closed load case says what was found; technical diagnostics apart
- Status: Done
- Who: markbui, Claude
- Where: Run → Run FEM Analysis → the run monitor (also Run → Analysis Monitor), tab "Load Cases"
- What: A load case that is closed now says, after its status, what the solver found of it: "Converged · Instabilities:
  0"; "Converged with Warnings · Instabilities: 0 · Accuracy warnings: 2" (or "Cable target forces not met", "1
  rotation left out"); "Numerical Instability · 6 affected DOFs"; "Not Converged · 100 iterations · Instabilities:
  N/A"; "Failed · Cable target-force fit failed"; "Cancelled · Not run". The 0 is the solver's count of severe
  instabilities in a case whose numerical stability it assessed — not a residual and not digits lost; a case it did
  not assess reads N/A, never 0. The name and the outcome share the row and both give way in a narrow window; the full
  line is in the tooltip and is what a screen reader reads. Open, a case keeps its convergence, iterations and cause of
  failure in view; the measured numbers behind them — scaled backward error, out-of-balance, smallest scaled pivot,
  resultant of internal forces, largest loss of accuracy — are under "Technical diagnostics", closed until asked for,
  between the table and the log. The table's coordinate columns read X (m), Y (m), Z (m). The reason of a run that
  failed is in red, and is also written in the log of every case that was stopped or not run. The note states the
  thresholds the run was reported by. Nothing of the solver or of its diagnostics changed.
- Checks: --run-monitor (the closed row for a stable frame, a stiffness contrast, a withheld result, a mechanism, an
  iteration stopped at its limit, an unmet fit, a failed fit, a planned and a cancelled run); --run-monitor-ui (the
  rows, Technical diagnostics opening and closing, the table's columns, a refused run); --general-solver; --dev-log

## 2026-10-13 · Load cases: four kinds and references by identity in the model and in every file; older files are upgraded when opened
- Status: Done
- Who: markbui, Claude
- Where: No new window. File → Load Config… / Save Config (.cotfem.db, .cotfem.json); the native window's File → Open /
  Save (.mcotis); Run → Run FEM Analysis and the native Run → Run Analysis (the refusals)
- What: First two steps of docs/analysis/LOAD_CASE_STATE_REFACTOR_PLAN.md (PR-01, PR-02). A load case is now one of
  four kinds — Linear Static, Nonlinear Static, Initial P-Delta, Modal (Eigenvectors) — and keeps two different
  references apart: the nonlinear case it continues from (its loads are added to that case's end state) and the
  nonlinear case whose end stiffness a linear or modal case uses (that case's loads are not applied again). It can
  also hold P-Delta as its geometric nonlinearity, its own load steps / iterations / tolerances, and for a modal case
  a mass source and a number of modes. The rules are one set for both windows: a linear case does not continue from a
  case, a modal case has no loads, only a nonlinear or initial P-Delta case can be continued from or lend its
  stiffness, no case depends on itself; a native model also refuses a combination of a modal case. The order in
  which cases have to run — a case that is not ticked but is needed runs as a prerequisite; the cases behind a failed
  one are blocked — is worked out by a run plan in the analysis library. NOTHING NEW IS SOLVED. The form to define
  these cases and the run by the plan are the entry of Load Case Data above; a case that
  asks for P-Delta, for modes, for the stiffness of another case or for its own iteration controls is refused at Run
  with the reason ("P-Delta is not available yet — numerical verification pending … The case is not solved, and it is
  not replaced by a first-order analysis"). The cases that were solved before give the same numbers. Files: native
  .mcotis is schema 4, a configuration .cotfem.db is schema 2 and a .cotfem.json is version 2. An older file opens as
  it was — every case with the meaning, the identity, the load patterns and factors, the run and cable-fit flags it
  had; nothing is switched on by the upgrade, and the file on disk is not touched until it is saved. A configuration
  keeps its revision through the upgrade, so its results stay its results. In a configuration a case now refers to
  another by identity, so renaming a case cannot break what continues from it. A file that contradicts itself —
  a reference to a case that is not there, two cases under one name, a name and an identity that point to different
  cases, tables that disagree with the stored model — is refused with the case named, not repaired by a guess.
  TO KNOW: a file saved by this version is not opened by an earlier build of Mcotis ("newer than this app supports");
  update before exchanging configurations. The bundled default model is the same model, saved as schema 2.
- Checks: Mcotis.Checks (group "Load cases: kinds, references, run order, schema 4 and what is not solved yet", and
  its --full); --fem-schema (the upgrade of a schema-1 database and a version-1 JSON file written by the earlier
  code, the four kinds through both files, the refusals, the bundled default, a P-Delta case opened, refused at Run
  and saved again); --fem-db; --fem-model; --full of Mcotis.Desktop.Checks; --dev-log

## 2026-10-13 · Analysis Options: full 3D to start with, every degree of freedom the user's to set; the run goes on where the loads allow and reports the rest
- Status: Done
- Who: markbui, Claude
- Where: Run → Analysis Options… → tab "Degrees of Freedom"; the report is in Run → Analysis Monitor
- What: The main window's Degrees of Freedom tab is now the same as the native workspace's: the preset (Full 3D, XZ,
  YZ, XY plane, Custom), the six degrees of freedom and those of cable joints, each a box that can be changed. A model
  starts in Full 3D — the default model, a new blank model — with the translations for cable joints; this replaces the
  rule of the entry below that only RY could be changed. The set goes to the solver as chosen and is never corrected;
  the form refuses only a set without a translation. A model of the main window lies in its XZ plane on pin supports
  that are all on one line, so in Full 3D nothing holds it out of that plane: it can turn about the line of its
  supports. The loads act in the plane and do no work on that movement, so — as ETABS does — the run goes on: the
  solver holds that movement, solves, and reports it. Run FEM Analysis then reads Completed with Warnings, every load
  case "Converged with Warnings · Instabilities: 78 · 1 unloaded movement held" (default model), and the table lists
  the movement joint by joint — RX of every joint and UY of every joint off the line of the supports, remark
  "possible mechanism, not loaded: held". The results are those of the XZ plane (default model: displacements within
  2·10⁻¹² m, forces within 2·10⁻⁶ kN, every case and combination); the displacement along the held movement is given as
  zero. A movement the loads DO act on — a mechanism under its load — has no result, as before, and neither has a
  degree of freedom that nothing holds at all (a cable joint with no stiffness sideways: rule of 2026-10-12, unchanged).
  The same holds in the plane: a mechanism the loads leave alone no longer stops a main-window run. The native
  workspace is unchanged. A configuration saved before the degrees of freedom were an option opens in the XZ plane it
  was analysed in. Other sets the model cannot be analysed with stop the run with the reason — "UX is switched off,
  but the joints of the model are not all at one X: …" — every load case Cancelled · Not run with the reason in its
  log; a load on a degree of freedom that is off is refused by the joint. A cable joint that a switched-off degree of
  freedom holds against the pull of an inclined cable is said: the cases read Converged with Warnings and their log
  names the cable and the joint. In 3D a frame is given the out-of-plane inertia and the torsion constant of its
  section (a Section Designer section without a torsion constant takes the polar moment, as it does on export);
  frames that come without them are refused by name. Messages of the solver name a frame or a cable by its own name.
- Checks: Mcotis.Checks (movements held: a column free to spin, a beam on rollers, a hinged portal, a beam hung from a
  cable — each to its closed form and to the digit of the same model restrained; refused when the load acts on the
  movement, and without the option) and its --full; --run-monitor (a rigid portal and a hung beam in Full 3D against
  the plane; a hinged portal pushed sideways refused; UX off; a cable joint held by an option); --run-monitor-ui (the
  default model through the window in Full 3D against the XZ plane; UX off); --template-building-ui (the form, the 3D
  start); --fem-model (what a new model, a blank one and an older file start with); --fem; --general-solver;
  --fem-sap; --full of Mcotis.Desktop.Checks; --user-guide; --dev-log

## 2026-10-13 · Archive legacy MB Tabs 01–05
- Status: Done
- Who: Codex
- Where: Main window; MB menu; `archive/Mcotis.Desktop/LegacyTabs01To05/`
- What: Removed Loading, Truss Height, Pretension, Output Settings and Iteration from the active desktop UI and
  archived their exact XAML and page-only code-behind outside the compiled project. Five inert slots retain the
  established indices of the remaining pages; legacy data/configuration seams stay only for file compatibility and
  the current VCF/TAS/optimization workflows. Run Study and Cancel Study are no longer exposed.
- Checks: default offline Mcotis.Desktop.Checks; --dev-log

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

## 2026-10-13 · VCF System can replace a non-VCF working model
- Status: Done
- Who: Codex
- Where: Edit → VCF System…
- What: On a TAS or another model without valid VCF chord groups, VCF System now asks before replacing the entire
  Mcotis working model with the built-in VCF configuration, then opens the VCF editor; it never shows the unrelated
  model as a VCF preview and never writes SAP2000. Cancel keeps the current model. An existing VCF still opens directly,
  and a blank New Config still creates its first VCF after frame and cable sections have been defined.
- Checks: default offline Mcotis.Desktop.Checks; --etabs-import; --dev-log

## 2026-10-13 · Help → User Guide
- Status: Done
- Who: Philip Pham, Claude
- Where: Help → User Guide (F1); docs/USER_GUIDE.md
- What: An encyclopedia of Mcotis, like a wiki: an article for every function — where to find it, what it does, how to use
  it and what to watch out for — in categories that follow the menu bar (Getting started, Files and models, Define, Edit,
  Assign, Run, Results, Design, Optimization, MB study, Reference). The window has the contents by category with a search
  box (every word must match; Enter opens the first result or a results page), and each article shows an infobox with its
  category and menu path, its lead, a contents box, its sections, See also and the articles that link to it; links
  between articles navigate, with Back / Forward / Main page. Articles can show screenshots with a caption
  (![caption](images/file.png), the files in docs/user-guide/images, built into the app); Optimization VCF is written as
  a step-by-step guide with six — the four sheets of the form and the Run and Chart sheets of the results. Export to Word…
  (guide window, on any article) saves the article as a .docx: A4, 2.54 cm margins, Arial 11 with 1.15 line spacing,
  headings, bullet and numbered lists, the screenshots scaled to the page width with their captions, the article and
  page number in the footer. The text is docs/USER_GUIDE.md, built into the app: whoever adds or changes a function
  updates its article (one `# Title` with Category, Where and See also lines, then Markdown with [[links]]).
- Checks: --user-guide (format, every link and screenshot resolves, the app carries the current guide, every menu command
  of the main window is covered, search, the window's pages, screenshots and navigation; the Word export is A4, Arial 11,
  1.15 spacing, holds the screenshots and is valid Open XML)

## 2026-10-13 · Illustrated step-by-step Optimization TAS user guide
- Status: Done
- Who: Philip Pham, Codex
- Where: Help → User Guide → Optimization TAS
- What: Eight steps cover preparation, Geometry, Storey Force, optional elevated-cable pretension, candidate Sections, the deck deflection/uplift check, Run results and Chart interpretation. Six real dialog screenshots follow a solved nine-case h × LL example; the optional cable screenshot is clearly separated. The article includes range/count rules, load calculations, actual example results, costs, troubleshooting and Word sharing.
- Checks: --tas-guide-capture (offline real dialogs and nine solved cases); --user-guide (eight steps, six packaged screenshots, links, window rendering and valid Word export); --dev-log

## 2026-10-13 · Export graphical results as numbered report figures
- Status: Done
- Who: Philip Pham, Codex
- Where: Result → Show Results… → Export Result…
- What: Word and PDF exports now contain captures of the model result diagrams, with Figure numbers and load case/output captions. Select axial/cable forces, bending, shear, reactions or deflection; one figure for each selected case and output. Uses the screen renderer, fitted to the full model with high-resolution images and result labels; preserves the live view. A4, Arial 11, spacing 1.15 and the requested margins remain. Cable target fit stays in Results Table and is not a graphical export option.
- Checks: --result-export (real model/case captures, distinct diagrams, captions, embedded images, no tables, Word schema/page settings, PDF and dialog save/open); --result-tools-ui

## 2026-10-13 · Detailed Optimization VCF user guide
- Status: Done
- Who: Philip Pham, Codex
- Where: Help → User Guide → Optimization VCF
- What: Expanded to eight illustrated steps: model preparation, geometry ranges, fan grading and shared cable steps, tributary loading, section candidates, bottom-chord deflection/uplift checks, Run interpretation and Chart refinement. Retains six screenshots and explains the nine-case example, combination counts, synchronization, fitting status, costs and troubleshooting.
- Checks: --user-guide (article, eight steps, six screenshots, links, rendering and Word export); --dev-log

## 2026-10-13 · Shared User Guide Word export format
- Status: Done
- Who: Philip Pham, Codex
- Where: Help → User Guide → any article → Export to Word…
- What: Every article uses A4, Arial 11 throughout (including headings, captions and footer), 1.15 line spacing and margins top/left 1.88 cm, bottom 1.28 cm, right 1.58 cm. Screenshots fit the new content width; the page footer stays inside the smaller bottom margin.
- Checks: --user-guide (all 62 article exports, styles, margins and screenshot width; valid illustrated Word documents); --dev-log

## 2026-10-13 · Illustrated column design optimization user guide
- Status: Done
- Who: Philip Pham, Codex
- Where: Help → User Guide → Column design optimization
- What: Eight detailed steps with nine real-dialog screenshots cover the basis, common member/case scope, RC/CFT/steel encased/mega size controls, grades and bars, unit rates, cheapest-first results and Use Selected. A labelled CFT force example demonstrates the actual search; pricing units, search limits, full-design review and separate model assignment are explained.
- Checks: --column-guide-capture (real dialogs, completed CFT search and Use Selected); --column-design; --user-guide (steps, nine embedded screenshots, links, rendering and Word export); --dev-log

## 2026-10-13 · Export selected analysis results to Word or PDF
- Status: Done
- Who: Philip Pham, Codex
- Where: Result → Show Results… → Export Result…
- What: Select solved load cases/combinations and all or individual output types, save a Word or PDF report and open it. Portrait A4, Arial 11, 1.15 line spacing; margins top/left 1.88 cm, bottom 1.28 cm, right 1.58 cm. Tables retain frame stations, signed reactions, deflections and cable fit warnings. PDF uses installed Edge or Chrome.
- Checks: --result-export (selection, stations, units, missing values, warnings, Word schema/page settings, real PDF generation and dialog save/open); rendered PDF A4/Arial 11, margins and repeated headers verified across five pages
## 2026-10-13 · Archive the complete legacy MB workspace
- Status: Done
- Who: Codex
- Where: Main window menus and toolbar; `archive/Mcotis.Desktop/LegacyMb/`
- What: Removed the top-level MB menu and its remaining 06A Optimization and 08 Quantities pages, plus the legacy
  06 Post-Processing page, Post-Processing → Study Reports and the Print All Reports toolbar entry. Their frozen
  XAML and navigation entry points are kept outside the compiled project. Empty slots 0–6 and 8 retain FEM's
  established index 7. The separate standalone Optimization menu for VCF/TAS remains active.
- Checks: solution build; --study-ui archive assertions; --user-guide; --dev-log. KNOWN GAP of --study-ui, older than
  this change: its Optimization VCF checks were written for the default model before the bundled database of
  2026-10-11. The fan-cable forces they named (30,000 … 40,000 kN) are now those of the bundled model (5,000 …
  15,000 kN), and Cable4b-2 follows Cable4a-2 as the bundled model has it (user, 2026-10-13). The suite now runs
  100 checks, through the Load Cases pane, and stops at Define → Result Load Case: the check opens a menu item the
  Define menu does not have, and did not have at the commit before. --study-ui is not part of --full.

## 2026-10-13 · Load cases: verified P-Delta, linear static on a saved stiffness, modal analysis and result storage
- Status: Planned
- Who: Unassigned
- Where: Run → Run FEM Analysis; the result tables; the numerical analysis libraries
- What: The remaining steps of docs/analysis/LOAD_CASE_STATE_REFACTOR_PLAN.md, in their order: P-Delta and the state a nonlinear case ends in (PR-04,
  blocked until the geometric stiffness and the restoring force have passed the literature gate — ALG-07, ALG-08);
  linear static on the stiffness at the end of a nonlinear case (PR-05); mass sources and modal analysis (PR-06);
  results of six degrees of freedom and of modes in the result database (PR-07). Until each is verified, a case that
  asks for it is refused at Run with the reason — see the entry below.
- Checks: none yet

## 2026-10-13 · Load cases: Load Case Data for the four types; the run follows a plan and solves what a case needs first
- Status: Done
- Who: markbui, Codex, Claude
- Where: Define → Load Cases → + Add / Modify (Load Case Data); Run → Run FEM Analysis → Set Load Cases to Run; Run →
  Analysis Monitor
- What: PR-03 of docs/analysis/LOAD_CASE_STATE_REFACTOR_PLAN.md. Load Case Data has a Type: Linear Static, Nonlinear
  Static, Initial P-Delta, Modal (Eigenvectors), and shows what belongs to the type. A nonlinear case has "Continue
  from" (its loads are added to the end state of that case), its geometric nonlinearity (None / P-Delta) and, when
  ticked, its own load steps, iterations and tolerances; an initial P-Delta case is a nonlinear case with P-Delta
  fixed on; a linear or modal case has "Stiffness from" instead (only the end stiffness of that case is used, its
  loads are not applied again); a modal case has a mass source and a number of modes in place of loads. Both lists
  offer nonlinear cases only, and never a case that depends on the one being edited; a case others start from cannot
  be made linear or modal. A model without a mass source is offered MS1 — the self mass of its members — which is
  kept with the configuration once a modal case names it; there is no form for mass sources yet. NOTHING NEW IS
  SOLVED. A type or option that is not solved yet — P-Delta, modal, linear static, own iteration controls — can be
  defined and saved; the form says so, the Load Cases table shows "Unavailable — verification pending" in its Status
  column, a new case of such a type is added not set to run, and Run with it ticked stops with the reason, never
  solving it as a simpler case. The Load Cases table now shows Type, Starts From / Stiffness, Geometric and Status,
  and the case a case continues from is set in Load Case Data, no longer in the table. Set Load Cases to Run shows
  the plan of the run: for each case its role (Requested, or Prerequisite — not set to run but needed first), what
  it depends on, and whether it is solved. CHANGE TO A RUN: a case that is not set to run but that a case of the run
  continues from is now solved and listed with its own results, as SAP2000 and ETABS do; its log in the run monitor
  says "Not set to run: solved because … continues from it". Before, only its loads were carried. The results of
  the other cases are the same (default model, only ULS set to run: Pretension and ULS equal those of a run of every
  case), and the cases are solved and listed in the order of the Load Cases table as before.
- Checks: --case-editor (the plan, the order of the run, prerequisites, rows outside the run, the form for every
  type, the run dialog, save and reopen, a run with a prerequisite against a run of everything); --study-ui (Set
  Load Cases to Run and the Load Cases pane in the main window; the suite stops later for a reason of its own — see
  the entry below);
  --fem-schema; --run-monitor-ui; --template-building-ui; --full of Mcotis.Desktop.Checks; --full of Mcotis.Checks;
  --user-guide; --dev-log

## 2026-10-13 · Cable force fitting is shown only for a model that has cables
- Status: Done
- Who: markbui, Claude
- Where: Run → Analysis Options…; Result → Cable target fit; Show Results; Results Table; Run → Analysis Monitor
- What: A model without cables has no cable target forces to fit, so what belongs to that fit is no longer shown for
  it. In Analysis Options the second tab is "Cable Force Fitting" — tolerance, fitting iterations, frame mesh, cable
  sag — when the model has cables, and "Frame Mesh" with the mesh alone when it has none; the settings that are not
  shown keep their values. Also not shown without cables: Result → Cable target fit, its choice in Show Results, its
  tab of the Results Table, and the Cable Target-Force Convergence tab of the run monitor. With the tied arch system
  they appear as soon as it has its elevated cable. The native model window follows the same rule for its Analysis
  Options. Not changed: the degrees of freedom of cable joints on the first tab, and Purpose / Cable Target Forces
  in Load Case Data and the Load Cases table.
- Checks: --template-building-ui (the form with and without cables; Apply keeps what is not shown);
  --result-tools-ui (the tied arch system against the VCF system, in the main window and its three result windows);
  --etabs-import (which models have cables); --run-monitor-ui; --full of Mcotis.Desktop.Checks; --user-guide;
  --dev-log

## 2026-10-13 · Optimization menu: only the optimization of the system that is open is offered
- Status: Done
- Who: markbui, Claude
- Where: Optimization → Optimization VCF… / Optimization TAS…
- What: Each optimization varies its own system, so the menu now follows the model that is open. On a VCF system
  Optimization VCF is offered and Optimization TAS is greyed; with the tied arch system open (File → Load TAS… or
  Edit → Tied Arch System…) it is the other way round; on a model that is neither — an imported ETABS model, a blank
  one — both are greyed. Before, both were always enabled, whatever model was open. A greyed item still shows its
  tooltip, which begins with the system it is for. Optimization Results… stays available: it shows the last run.
- Checks: --result-tools-ui (the menu in the main window on the VCF model, the tied arch system and back; this suite
  had also still expected the archived MB menu after Optimization — corrected); --etabs-import (every model the
  application can hold, and that the menu is told when the system changes); --full of Mcotis.Desktop.Checks;
  --user-guide; --dev-log

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

## 2026-10-11 · Storey force and loaded points
- Status: Done
- Who: markbui
- Where: Edit → VCF System… → Storey Force sheet
- What: Tributary width, storeys and qz per load pattern; the loaded points picked from a list or on the preview.
- Checks: offline suite

## 2026-10-11 · Load cases, Set Load Cases to Run, FEM reports
- Status: Done
- Who: markbui
- Where: Define → Load Cases / Load Combinations; Run → Run FEM Analysis; Post-Processing → FEM Live Results (HTML)…
- What: Load cases defined in the app (Default Setup: Pretension, SLS, ULS), chosen to run before each run; FEM results
  reported live in HTML.
- Checks: offline suite

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

## 2026-10-11 · Optimization VCF
- Status: Done
- Who: Philip Pham; markbui
- Where: Run → Optimization VCF…; Run → Optimization Results…
- What: Runs the geometry × cable force × storey force × section variants with the internal solver; deflection check
  (span / ratio and an uplift limit on the bottom chord) and total cost per case.
- Checks: OptimizationChecks (offline suite)

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

## 2026-10-11 · Bundled default model and FEM databases
- Status: Done
- Who: markbui
- Where: File → Default Model; File → Load Config / Save Config (.cotfem.db); Run → Unlock FEM Model
- What: The default model is a bundled MCOTiS database; a configuration is one .cotfem.db; a run locks the model and writes
  its output database, Unlock deletes the outputs.
- Checks: --fem-db; --fem-model

## 2026-10-11 · Frame local axes
- Status: Done
- Who: markbui
- Where: Assign → Frame → Local Axes…; Display → Frame local axes; selection on the 3D view
- What: Sets a frame's local-axis angle (in-plane inertia rotates with it, kept when saved) and draws the axes of the
  selected frames; members can be selected in 3D.
- Checks: --fem-model

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

## 2026-10-06 · Report printing and PDF export
- Status: Done
- Who: markbui
- Where: MB → 06 Post-Processing → reports; toolbar Print All Reports
- What: Print Setup and PDF export of the study reports (headless Chromium); a module's report regenerated on its own.
- Checks: ReportPrintChecks (offline suite)

## 2026-10-06 · FEM analysis, cable sag, FEM against SAP2000
- Status: Done
- Who: Philip Pham; markbui
- Where: Run → Run FEM Analysis; Run → Analysis Options (cable sag); Run → Analysis Monitor
- What: The internal 2D solver with SAP loading, frame meshing, pretension stages, cable sag, the run monitor and the 3D
  view; member forces compared with SAP2000.
- Checks: --fem-sap

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

## 2026-09-08 · Feature-oriented presentation refactor (plan Phase 8)
- Status: Planned
- Who: Unassigned
- Where: code: src/Mcotis.Desktop/Presentation
- What: Focused feature view models and views, typed outcomes, more regression checks (docs/implementation-plan.md, Phase 8).
- Checks: none yet

## 2026-09-08 · SAP operations and recovery UX (plan Phase 7)
- Status: Planned
- Who: Unassigned
- Where: top bar connection; a Recovery Center
- What: Connection health and model fingerprint, a Recovery Center over the run journal, Open Candidate in SAP
  (docs/implementation-plan.md, Phase 7).
- Checks: none yet

## 2026-09-08 · Cable Sync UX (plan Phase 6)
- Status: Planned
- Who: Unassigned
- Where: MB → 03 Pretension
- What: Master / follower hierarchy with resolved-force preview, immediate orphan / cycle validation, explicit action when a
  master is removed (docs/implementation-plan.md, Phase 6).
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
