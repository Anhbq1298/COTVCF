# Mcotis development log

Every function added or changed in Mcotis is recorded here, newest first. The app shows this file under
**Help → Development Plans**, so every team member can see what exists, where to find it and how it was checked.

**Compulsory for everyone, people and coding agents alike.** A commit that changes the app (`src/`, `App.xaml*`,
`COTVCF.csproj`) must add or update an entry in this file. The commit hook in `.githooks/commit-msg` (installed by the
build) refuses such a commit without one; write `[no-log]` in the commit message only for a change no user can notice
(a refactor, a comment, a test-only fix). The checks (`dotnet run --project tests/COT_VCF.Checks.csproj -- --dev-log`)
refuse an entry that breaks the format below.

Entry format — a `## date · title` line, then these fields, each on its own `- Field: value` line (a longer value
continues on the next lines, indented by two spaces):

- Status: Done, In progress or Planned
- Who: the developer(s), as in git (Unassigned for a plan nobody has taken)
- Where: how a user reaches it in the app (menu path, window, tab), or the code area when it has no UI
- What: what it does, in a sentence or two, with anything a user must know
- Checks: the check(s) that prove it (a `--flag` of the checks project), or "none yet"

## 2026-10-12 · Help → Development Plans and the compulsory development log
- Status: Done
- Who: Philip Pham
- Where: Help → Development Plans; docs/DEVELOPMENT_LOG.md
- What: Shows this log in the app — in progress and planned work first, then what is done, newest first. Writing an entry
  is compulsory for every change of the app: the build installs .githooks, whose commit-msg hook refuses a commit that
  changes src/, App.xaml or COTVCF.csproj without a log entry ([no-log] only for changes no user can notice); AGENTS.md
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
- Who: Philip Pham
- Where: Edit → Delete (Del); File → Import From SAP2000; File → Export to SAP2000…
- What: Delete removes the selected joints / frames / cables with every reference to them. Import From SAP2000 brings the
  open SAP2000 model in, ready to run (chord groups, cable targets, Tab 01 fitted to the SAP joint loads). Export to
  SAP2000 previews, then writes the Mcotis model into the open SAP2000 model (not saved).
- Checks: --sap-import; offline suite

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
- Who: Philip Pham
- Where: Define → Section Properties → Frame Sections (CFT, Steel Encased, Mega Composite); Design → RC / CFT / Steel
  Encased / Mega Composite Column Design…
- What: Composite frame sections with transformed properties and cost per metre; column design to EC2 / EC4 + BC4 and the
  CTBUH mega-column method, from the FEM forces.
- Checks: --column-design; MegaColumnChecks (offline suite)

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
- Where: code: src/Presentation
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
- What: The COTVCF candidate workflow: loading criteria, truss-height and pretension candidates on the SAP2000 model.
- Checks: offline suite
