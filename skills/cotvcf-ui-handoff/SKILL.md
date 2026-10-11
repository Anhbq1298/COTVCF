---
name: cotvcf-ui-handoff
description: Continue COTVCF WPF UI work with the current solution/project layout, navigation style, preview behavior, and SAP connection flow.
---

# COTVCF UI Handoff

Use this when continuing work on the COTVCF WPF desktop app in this repository. It records the current project cleanup and UI decisions so another agent does not rediscover or accidentally undo them.

## Current Project Layout

- Main solution: `COTVCF.sln`.
- Main app project: `COTVCF.csproj`.
- The old `ConsoleApp1.csproj` identity was removed; everything user-facing should be treated as COTVCF.
- `COTVCF.sln` currently includes the app and `tests/COT_VCF.Checks.csproj`; the desktop build script targets the app project directly.
- `.gitignore` includes `.vs/`, plus build folders such as `bin/`, `obj/`, `.work/`, `tests/bin/`, and `tests/obj/`.
- The app is a WPF app targeting `net8.0-windows`, `x64`, with SAP2000 API reference `CSiAPIv1.dll`.

## Build And Run

- Build the app and checks together with `dotnet build COTVCF.sln -m:1`; when this machine has only SAP2000 27,
  add `-p:SapInstallDir="C:\Program Files\Computers and Structures\SAP2000 27"`.
- If the running desktop app locks `bin/Debug/net8.0-windows/win-x64/Mcotis.exe` or `.dll`, close it with `Get-Process -Name 'Mcotis' -ErrorAction SilentlyContinue | Stop-Process -Force`, then rebuild.
- For compile-only checks while the app is open, use `dotnet build COTVCF.csproj -o .work\build-check`.
- Launch the app from `bin/Debug/net8.0-windows/win-x64/Mcotis.exe`.
- **Brand:** the product is **Mcotis** (Meinhardt Cities of Tomorrow Integrated Structures) — main window title `Mcotis — Meinhardt Cities of Tomorrow Integrated Structures`, exe / assembly `Mcotis` (pack URIs `/Mcotis;component/…`). Internal identifiers keep the old name for compatibility: namespaces `COT_VCF`, `CotVcfConfigDto`, the FEM file format `COTVCF.FemModel` / `.cotfem.json`, `%LOCALAPPDATA%\COTVCF`, report storage keys.
- The built application folder is `bin/COT ParametricTools_V2` (version 2.0.0.0). Produce it with
  `dotnet publish COTVCF.csproj -c Release --self-contained false -o "bin/COT ParametricTools_V2"` — never by
  copying a build output by hand: the earlier hand-copied `COT VCF Tools V1.0` folder went stale and showed an old app.
- The solution also has a `FreshBuild` configuration. `dotnet build COTVCF.sln -c FreshBuild` deletes the existing
  `bin/` directory and builds a complete app directly into a newly created `bin/` directory (`bin/Mcotis.exe`).
  Close any running Mcotis instance from `bin/` first; this build also removes any Debug or published outputs there.
- To rebuild and create the **Mcotis desktop icon**, run `./build-desktop.ps1`. The script checks for a running app,
  performs `FreshBuild`, and creates `Desktop/Mcotis.lnk` pointing to `bin/Mcotis.exe`. Keep the DLLs in `bin/` beside
  the executable; a standalone `.exe` on the desktop cannot run without them.
- `src/Presentation/MainWindow.xaml` is UTF-8. A previous encoding error turned em dashes, ellipses and engineering
  symbols into broken multi-character text; do not re-encode this XAML through Windows-1252.

## Compact Engineering Density Pass

The UI was redeployed as a compact engineering/BIM desktop layout. Do not revert to the
earlier tall-header / large-button / blue-block look.

- Priority order for space: Preview Panel > DataGrid > primary controls > secondary info > chrome.
- `Theme.xaml` is the single lever for density. Current values: `Button` MinHeight 28, `TextBox`
  MinHeight 26, `ComboBox` 24, `DataGrid` RowHeight 24 / header 26, `TabItem` 152x42, base font 12.
- Palette is a neutral light chrome (`Background` #E6E8EB, `Surface` #F3F4F6, `Raised` #FFFFFF,
  `Border` #C7CCD2). The model viewport is white (`ViewportBg` #FFFFFF) with dark geometry
  (`ViewportGeom` #3A4048) and a faint `ViewportGrid`; keep the `Viewport*` seam so the ground
  can be re-themed from `Theme.xaml` alone. `Accent` #1F4788 stays; `AccentBright` #2E90FA is the
  selection / focus / cable colour.
- Keep headers to one small line. Section labels use the `Subheading` style (11 px, muted, uppercase text).
- Short button labels: `Connect SAP`, `+ Add`, `- Remove`, `Capture Baseline`, `Reset`, `Apply to SAP`,
  `Validate`. Icon-only actions (refresh, row delete, viewer tools) use borderless buttons.

## Navigation Style — desktop menu bar (ETABS-like)

- A menu bar (`MainMenu`, top of the window) replaces the left tab rail: **File · MB · Define · Edit · Assign · Run ·
  Result · Post-Processing · Display · Design · Help**. Menu items reuse the existing commands; navigation is presentation only
  (`MainViewModel.Menu.cs`, click handlers in `MainWindow.xaml.cs`). Do not bring the rail or the toolbar buttons back.
  - **File**: New Config, Load Config…, Save Config (**Ctrl+S**), Save Config As… (**Ctrl+Shift+S**) · Default Model ·
    Read FEM Properties from SAP. Save (`SaveFemModel`) overwrites the active configuration and asks for a file only when
    there is none; Save As (`SaveFemModelAs`, asked for by the user 2026-10-12) always asks, starting from the active
    file's folder and name, and the file it writes becomes the active configuration. Both are on the toolbar (Save As
    beside Save) and bound as `Window.InputBindings` in `MainWindow.xaml`; the dialog is `MainViewModel.AskFemSavePath`
    (a check replaces it). One `.cotfem.db`
    is the complete MCOTiS configuration; the old split project/model JSON commands are not exposed.
  - **MB**: 01 Loading … 06A Optimization, 08 Quantities (each with its status as the gesture text; 06A disabled
    without raw data). Choosing one sets `SelectedTab`; the page fills the left pane.
  - **Define**: Materials, Section Properties, Load Patterns, Load Cases / Load Combinations · Result Load Case.
    There is no Define → Loading: the loads of the model are entered in Edit → VCF System (Storey Force / Cable Force),
    one fixed value each — see Tab 07 FEM, "The flow the user set". The 01 Loading page stays under MB with its ranges;
    its AddPoints / RemoveAppliedPoints / AddLoadDefinition / DeleteLoadDefinition commands work on an offline model too
    (they only edit what the app holds).
  - **Edit**: VCF System…. A blank configuration enables this after at least one frame section and one cable section
    exist. The window previews the built-in VCF topology without putting objects in the working model; Apply creates and
    names the joints, frames and cables and assigns the selected library sections.
  - **Assign**: Joint Restraints (Pin Supports).
  - **Run**: Run Study, Cancel Study · Run FEM Analysis, Cancel FEM Analysis, Analysis Monitor · Optimization VCF… ·
    Analysis Options. **Optimization VCF…** opens the modal `OptimizationVcfWindow`: the Edit → VCF Geometry inputs as a
    search space — sheet **Geometry** has Min / Max / Step (and the value count n) per parameter (bays, bay width, truss
    depth, clear height, back-span, back-stay height; keys shared with VcfGeometryWindow). The middle chord is **not** a
    variable: it is always `VcfOptimization.MiddleChordRatio` (0.5) × truss depth, shown read-only under Truss depth and
    not counted in the variants. Sheet **Cable Force** (decided by the user, 2026-10-11 — see "Cable Force sheet" below):
    one shared **Steps** count for every cable, a Min / Max (kN) per cable and the fan cables by their length.
    Sheet **Storey Force**: qz range (kPa, ≥ 0) per 01 Loading load definition, plus NStories (whole, ≥ 1) and
    tributary width B (> 0), with the total Fz at the min … max corner (−qz × loaded length × B × NStories). Force
    variants = cable steps × the storey counts; combinations = geometry × force × section variants. Sheet **Sections**
    a check-list drop-down of candidate sections per member role and per cable (none ticked = keep as assigned). Preview
    at Minimum / Current / Maximum corner; the combination count is shown; invalid ranges or corners grey OK.
    **Cable Force sheet** (`VcfCableForces`, `VcfOptimization.CableTargets` — the one function the runner, the rows and
    the preview all use, so they cannot disagree):
    - **All cables step together.** `Steps` = n values; at step j every cable is at Min + (Max − Min) × j / (n − 1)
      (`VcfForceSpan`), so the cables add n force variants, never a product per cable. With 1 step every cable is at its
      Min. Do not bring back a step or an independent axis per cable. The form starts from 03 Pretension: each cable with
      a force of its own (independent, or a Same Step follower — `VcfOptimization.CableSpan`) at its value / range, Steps
      at the most values a Tab 03 range has. A cable that takes another's value on Tab 03 keeps following it.
    - **Fan cables by length** (`VcfFanGrading`; fan cables = `VcfGeometry.FanCables`: top corner → interior bottom-chord
      node, by side, the nearest first). A longer cable needs more force and the number of fan cables follows the bays,
      so the form **opens on `+ kN per m`** (Linear): the user types the Min / Max of the shortest fan cable and an
      increase per metre; every fan cable of a case takes T shortest + increase × (L − L shortest) with the lengths of
      that case — both sides, whatever the bays, bay width and depth. The increase starts at the model's own slope from
      its shortest to its longest fan cable (`FitGradient`, whole kN/m, ≥ 0). `× length` (Proportional) is T shortest ×
      L / L shortest. `Manual` gives each fan cable its own Min / Max; a fan cable more bays add then takes the force
      of the cable it copies × its length ratio. **Forces a length rule works out are rounded to 100 kN**
      (`VcfOptimization.ForceRoundingKn`) — typed values are not.
    - **Rows follow Preview at.** `ShowCables` lists the cables of the previewed corner: FAN CABLES (L, Model = the
      model's own force, Min, Max, Step; the shortest tagged `shortest` with the inputs, a cable the bays add tagged
      `new`, the others read-only) and OTHER CABLES (inputs). A cable that takes another's value (Same Values; the far
      side of a graded fan) is not listed. The inputs are created once (`SpanBoxes`) and the rows rebuilt only when
      their set changes, so typing keeps the caret.
    - **Preview at** names its corners after the fan when the bays range leaves the model's: `Less cables · 6 bays` /
      `Current · 8 bays` / `More cables · 12 bays` (plain `Minimum · n bays` / `Maximum · n bays` when that corner has
      the model's fan). The caption under it reads `12 bays · span 120 m · fan cables 6 + 6 · new: … · not in this
      variant: … · labels: pretension [MN]`. The viewer writes each fan cable's force under the bottom-chord node it is
      anchored at (`Structural2DViewer.JointNotes` — along the cables the labels collide at the corner), any other
      cable's along it (`MemberNotes`; two that cross at mid-length move towards their upper ends) and emphasises the
      cables the bays add (`HighlightedMembers`).
    - Results: one column for the cable steps, named after the first cable that varies (the shortest fan cable when the
      fan is graded) and showing its force (`VcfCableStep`).
    `Application/VcfOptimization` holds `VcfRange` (Min, Min + Step, … ≤ Max — the Tab 01 / 03 rule, no off-step Max) and
    `VcfOptimizationSettings`; OK keeps them on `MainViewModel.VcfOptimizationSettings` (memory only, not saved in a
    file). **Run Optimization** (enabled while valid and ≤ `VcfOptimizationRunner.MaxCases` = 10,000) keeps them, opens
    `OptimizationResultsWindow` (also Run → Optimization Results…; Run → Cancel Optimization / Cancel Run) and runs
    `MainViewModel.RunVcfOptimizationAsync`: `VcfOptimizationRunner` walks the axes mixed-radix (last axis fastest) and builds
    each case in memory — `VcfGeometry.ChangeBays` / `Build` (middle chord 0.5 × depth), member properties (regenerated
    members copy their template; role / cable sections via `MainViewModel.SectionSwapper`, the same rule as Apply), SAP
    loading remapped for a bay change, Tab 01 point loads −qz × trib. length × B × NStories, the cable targets of the case's
    step (`VcfOptimization.CableTargets`; followers keep Same Values / Ratio / Offset / Step) — then `FemStudyService.Analyze` with the Analysis
    Options and Define → Load Cases plan. **Deflection check** (dialog bar above the buttons): the result the deflection is
    read from (`VcfOptimizationSettings.DeflectionResult`, a load case or combination set to run; default the first named
    SLS — on the default model the default setup's staged `SLS: 1.0DL+1.0LL`, i.e. the prestressed structure; with SAP
    Cases it is SAP2000's un-prestressed `NL SLS001: 1.0DL+1.0LL`, which deflects about ten times more) and the limit span / `SpanRatio` (default 250;
    span = bays × bay width between the mega columns), and the **uplift limit** (`UpliftLimitMm`, default 0 mm — asked for
    by the user, 2026-10-11): the largest upward displacement allowed in that result. **Both limits are read on the bottom
    chord points (BTrPt…, the case's `BottomPoints`) only** — the user's decision, 2026-10-11, and the points Tab 04
    monitors; do not go back to every joint. Why: over every joint the downward maximum is the top of a mega column once
    the fan lifts the span (default model at its own forces: 39.6 mm at TTrPt001), and a back-stay pulls the outer column
    tops up (201 / 204, ~2 mm), which would fail every case at 0 mm. Why an uplift limit at all: with a strong fan the
    bottom chord's downward maximum is at its ends over the mega columns and passes span / ratio however far mid-span has
    risen (default model at its own forces, SLS: 19.8 mm down at BTrPt011, 122.2 mm up at BTrPt006) — the uplift limit is
    what fails such a case. An uplift within `VcfOptimization.UpliftToleranceMm` (0.05 mm, the 0.1 mm the table
    shows) of the limit passes. Each row: the varying inputs, **max downward deflection** in that
    result and its point, span, limit, δ / limit, **uplift**, Check OK / NG (both limits; the status names an uplift above
    its limit), status (failures such as non-convergence or an unsolved
    result are listed, not thrown), and **Total cost [SGD]** = Σ member cost per metre × member length over every frame and
    cable of the case (`MainViewModel.CostPerMetre`: composite sections — CFT / encased — from `CompositeSection.Cost`
    (concrete m³ + steel / rebar t at their rates); any other member gross area × `MaterialProperty.UnitCostPerCubicMetre`
    (SGD/t × t/m³); a member without a rate leaves the total blank and the status names it). The summary counts the passing
    cases and names the cheapest passing case and the least deflection. The working
    model and SAP2000 never change. The cases vary the model Mcotis holds (`HeldFemProperties`) — the optimization does
    not read SAP2000 either. Checked: the case at the model's own values equals Run FEM Analysis, and a case equals Run
    FEM Analysis on the same geometry applied with Edit → VCF Geometry. This is the parametric study with the internal
    solver: **many cases belong here, not in Run FEM Analysis** (see Tab 07 FEM).
  - **Result**: Show Deformed Shape… (F5), Show Member Forces… (F6), Show Joint Reactions… (F7);
    Axial forces / cables, Bending moments, Shear forces, Support reactions, Deflection, Cable target fit
    (opens the table) · Results Table…; enabled once the run has results (`HasFemResults`).
  - **Display**: Frame mesh / Member size / Loading (checkable, bound to the FEM view toggles) · View Options.
  - **Design**: RC Column Design…, CFT (Concrete Filled Tube) Design…, Steel Encased Composite Column Design…. Each
    opens the modal `DesignReferenceWindow` with three tabs: *Design Basis* (code, method, Singapore NA, effective lengths
    l0,y / l0,z in m — required, > 0 —, γC / γS / γM0, max. concrete / steel grade, references), *Section* (type-specific
    mm / MPa inputs built from a field table; *From Member Section* fills them from the first ticked member's FEM section
    and material) and *Members & Loads* (frame members, ticked by default when the section suits the design; load cases /
    combinations, combinations set to run ticked by default). Reset / Run Design / Cancel. Codes come from
    `Domain/ColumnDesignCodes`: RC → SS EN 1992-1-1 (EC2); CFT and steel encased → SS EN 1994-1-1 extended by BCA BC4:2025.
    The basis is kept per type (`MainViewModel.DesignBasis`). Run Design designs each member with the N / V / M stations of
    the FEM run of the model (`MainViewModel.RunColumnDesign` → `Application/Design/ColumnDesignEngine`) and opens the
    non-modal `DesignResultsWindow`: summary grid (governing case, NEd, MEd,y, MEd,z, VEd, N / M-y / M-z+biaxial / V
    utilisations, max, OK / CHECK (detailing) / FAIL / ERROR) above the selected member's step-by-step calculation;
    Copy Report / Save Report… (.txt). The 2D FEM moment bends the major axis y-y; z-z carries imperfections only.
    Offline checks: `dotnet run --project tests/COT_VCF.Checks.csproj -- --column-design`.
  - **Help**: Workflow Help, FEM Procedure.
- Every launch opens the built-in default model before the window is shown; no blank viewport or choice appears at startup.
  File → New Config still starts an unsaved blank model. Its creation flow is
  **Define materials/sections → Edit VCF System → Define loads/cases → Run**. Define remains available on that blank model;
  MB, Assign, Run, Result, Post-Processing, Display and Design require geometry. The New Config blank viewer offers
  **Load Default Template** and **Continue with Blank Model**. The template uses File → Default Model; Continue hides
  the choice and keeps the model empty. New Config clears the active working-session manifest.
- The `WorkflowTabs` TabControl stays (indices unchanged) but its tab strip is hidden (`ItemContainerStyle` →
  `Collapsed`). A one-line pane header above it shows `PaneMenu  ›  PaneTitle` (e.g. `MB › 01  LOADING`,
  `Define › SECTIONS`) with the page status (`PaneStatus`) on the right and, offline, the `OfflineModelSummary` line.
- The pane header has an **✕** (`ClosePaneButton`, shown while the shared viewer is) that hides the left pane — the
  params column goes to 0 (its MinWidth is dropped too) and the viewer takes the full width. Any MB / Define / Assign /
  Run / Result / Display item opens the pane again (`OpenPane`). At startup the pane is closed (`App.OnStartup` → `ClosePane`), so the app
  opens on the model view only. Full-width pages (05, 06, 06A, 08) always show it.
- **Edit → VCF System…** opens `VcfGeometryWindow`: `VcfGeometry.Analyze` (Application) reads the system from the
  02 Truss Height chord groups — bays and bay width, truss depth, middle chord above the bottom chord, clear height
  (ground → bottom chord), back-span (mega column → outer column) and back-stay node height — and the role of every
  member (top / middle / bottom chord, vertical chord, mega column = vertical frames on the end bay lines, end frames,
  cables by their own names). The form edits those parameters and the section per role / per cable (from the sections
  already in the model); a live preview with dimensions redraws on every change; invalid input is shown and greys
  OK / Apply. `VcfGeometry.Build` moves every node by piecewise-linear maps of X (outer lines, bay lines, centred) and Z
  (ground, back-stay, bottom / middle / top chord) — names and topology never change. Apply
  (`ApplyVcfGeometryAsync`) writes geometry, baseline and session baseline, loading-point coordinates, Tab 02 ΔH range
  (kept around the re-measured height), the chosen sections (copied from a member that has them; local axis and
  releases kept) and Tab 03 cable section / area / capacity; FEM results are cleared. A connected session becomes an
  offline model (` · VCF geometry`); SAP2000 is never written and Connect SAP restores the SAP model.
  **Number of bays** is an input too (2–200): `VcfGeometry.ChangeBays` lays the chord lines out again at the old bay width
  around the truss centre and names chord points by position from their pattern (TTrPt001 … TTrPt0nn — the right-end corner
  is renamed, `RenamedPoints`); members tied to an end line (full-length chords, mega columns, back-stays, end frames)
  follow it; per-bay chord members and interior verticals are regenerated (existing names kept by position, new ones
  continue the numbering); the fan cables keep their pattern — ⌊n/2⌋ per side from each top corner to the bottom chord up
  to mid-span — new ones continue the cable numbering (Cable9a-2 / 9b-2) and copy their side's nearest fan cable. Anything
  outside that pattern refuses the change with the member named. Apply then copies member properties from the templates,
  maps / drops SAP loads, reloads every top point when all were loaded, sets the Tab 02 groups, removes / adds Tab 03 cables
  (a cable that followed a removed one becomes independent and takes over its range) and remaps the Tab 04 lists. The
  window groups Frame and Cable sections under one **Sections** box.
  The parameters sit on workbook-style sheets (`ParameterTabs`): **Geometry | Cable Force | Storey Force | Sections**. The two
  force sheets follow the preview: **Cable Force** (each cable of the previewed
  model: Tab 03 load pattern, sync, pretension — fixed value or min … max (count), resolved through the sync graph for
  followers; a new fan cable shows its template's; the pretension of an independent cable is typed here — see Tab 07 FEM,
  "The flow the user set") and **Storey Force** (the load inputs — B, storeys, loaded points, one qz per load pattern —
  above a read-out of each loaded point: X, tributary area = tributary
  length × B, and per load definition Fz = −qz × area × NStories, a range when qz is a range; totals per definition — the
  same rule as StudyCompositionService.BuildPointLoads).
- **Display → View Options…** (and the toolbar eye) opens `ViewOptionsWindow`, titled *Display Options* and laid out like
  SAP2000's: tabs *Object Options* (Labels: point / frame / cable / member size; Show: dimensions, loading arrows, frame
  mesh) and *General Options* (View Type: XY / XZ / YZ / Extrude 3D; Results: frame/cable result values, undeformed guide, deflection
  contour); *Reset Form to Default Values*, *Reset Form to Current Window Settings*; OK / Close / Apply. The form is staged:
  Apply / OK write it to the viewer and view model, Close discards. FEM pane 7 is only a card with *Open View Options*.
- **Define → Materials…** (first Define item) opens `DefineMaterialsWindow` (modal, like SAP2000's Define Materials): material
  list; *Get From SAP2000* (`ISapGateway.GetMaterialsAsync`, read-only, kN-m; CSiAPIv1 27: PropMaterial.GetNameList,
  GetMaterial, GetWeightAndMass, GetMPIsotropic / GetMPUniaxial, GetOConcrete_2 (→ _1), GetOSteel_1, GetORebar_1,
  GetOTendon_1, GetOColdFormed; grade from the "Material Properties 01 - General" display table — there is no grade getter);
  Add New / Add Copy / Modify/Show / Delete (refused while members use it); Show Advanced Properties; OK / Cancel (staged).
  `MaterialPropertyWindow` = Material Property Data: General Data (name + colour palette, type — new materials only —,
  grade, notes dialog), Weight and Mass (mass = W/g), Isotropic or Uniaxial (tendon, rebar) Property Data (G computed),
  Other Properties per type (concrete f'c / expected / lightweight + shear factor; steel & rebar Fy Fu Fye Fue; tendon &
  cold formed Fy Fu), Switch To Advanced Property Display. `MaterialProperty` (Domain) is the library entry; without SAP it
  is seeded from the member properties (`EnsureMaterials`: cable → tendon, C… concrete, S… steel). OK
  (`CommitMaterials`) writes E, unit weight and unit mass (and a rename) to the FEM members using the material and clears
  FEM results — SAP2000 is never written. The library is saved in the FEM model file (`Materials`, optional).
- **Material unit cost** (`MaterialProperty.UnitCost` SGD/t + `CostNote`; Material Property Data → *Cost*: SGD/t, the per-m³
  equivalent for concrete (× mass per volume), the basis, *Suggest Rate*). Defaults (`MaterialCosts`, filled where a
  material has none; an entered / kept rate survives Get From SAP2000): project rates C80/95 240, 100 / 120 / 150 MPa
  320 / 450 / 1,200 SGD/m³, HD jumbo S460M 4,860 and built-up S690QL 7,500 SGD/t; BCA 2025 averages ready-mixed Grade 40
  ≈121 SGD/m³ (for C30/37) and rebar 16–32 mm ≈690 SGD/t (material only); estimates — other concrete interpolated by f'c,
  S355 / S450 extrapolated by Fy from S460M / S690QL. Tendon / cable strand 12,000 SGD/t (project rate; replaces the
  earlier ≈1,000 SGD/t estimate in older files, an entered rate is kept).
- **Define → Section Properties ▸ Frame / Tendon / Cable Sections…** (and *Member Sections Table…*, the former Define →
  Sections member grid) open `SectionListWindow` (modal, like SAP2000's Frame Properties): Find this property (filters and
  selects), the list; Get From SAP2000 (`ISapGateway.GetSectionsAsync`, read-only, attaches without Connect SAP like the
  materials; CSiAPIv1 27: PropFrame.GetNameList(…, 0 = all), GetTypeOAPI, GetRectangle / GetTube_1 / GetISection_1 / GetPipe /
  GetCircle / GetChannel_2 / GetTee_1 / GetAngle_1, GetSectProps, GetMaterial, GetModifiers; PropCable.GetNameList /
  GetProp / GetModifiers; PropTendon.GetNameList / GetProp); Import New Property… (disabled — no section database here);
  Add New (shape picker) / Add Copy / Modify/Show / Delete (refused while assigned); OK / Cancel (staged).
  `FrameSectionWindow` = SAP2000's shape form ("Box/Tube Section" …): name, notes, display colour; Dimensions per shape
  (Rectangle, Box, I, Pipe, Circle, Channel, Tee, Angle; General / Section Designer shows SAP2000's A, I33, I22, J
  read-only); live `SectionSketch` drawing with the 2 / 3 axes; Material with + (Define Materials); Set Modifiers… (8
  section modifiers); Section Properties… (A, I33, I22, J, As2, As3 from `FrameSectionProperty.Values()`); Time
  Dependent disabled. `StrandSectionWindow` = cable / tendon data (material, linked diameter / area, cable modifiers).
  Libraries `FrameSections` / `CableSections` / `TendonSections` are seeded from the member properties
  (`EnsureSections`), saved in the FEM model file, offered by VCF Geometry, and OK (`CommitFrameSections` /
  `CommitStrandSections`) rebuilds the FEM properties of members with a changed / renamed section (`PropertiesFor`: area
  and I33 / I22 × section modifiers × member modifiers, rotated by the member's local axis; material data from the
  library; Tab 03 cable area / capacity follow). SAP2000 is never written.
- **Composite frame sections**: Add New Property also offers *Concrete Filled Tube (CFT)* (`FrameShape.FilledTube`,
  SapType "FilledTube") and *Steel Encased Composite* (`FrameShape.Encased`, SapType "Encased" — both names make the
  Design menu tick them). `FrameSectionProperty.Composite` is the Design menu's `ColumnSectionInput` (mm / MPa);
  `Material` = concrete, `SteelMaterial`, `RebarMaterial`. `CompositeSectionWindow` (1010 px, three columns): Steel Tube
  (rectangular / circular, H/D, B, t) or Concrete Section + Encased Steel I + Reinforcement; Materials (concrete / steel
  / rebar with +; fck, fy, fyk from the material or its grade name, `MaterialProperty.StrengthMpa`; no rebar material →
  `MainViewModel.DefaultRebar` B500); Design Data (φt, NG,Ed/NEd); Section Limits (EN 1994-1-1 δ, d/t or h/t, cover,
  ρs, h/b, BC4 grades); live sketch (steel, concrete, bars); Composite Properties (Ac, Aa, As, transformed A / I33 / I22
  to Ecm, self-weight, Npl,Rk / Npl,Rd, δ, (EI)eff); Material Quantity and Cost per m (concrete m³ × SGD/m³ + steel and
  rebar t × SGD/t from the material unit costs). `CompositeSection` (Domain) does the parts / transformation / cost;
  `MainViewModel.AnalyseComposite` applies the library materials; FEM members take the transformed A / I with E = Ecm
  and an equivalent γ / ρ so A·γ is the true self-weight.
- **Mega Composite Column** (`FrameShape.MegaComposite`, SapType "MegaComposite"; `ColumnDesignType.MegaComposite`):
  the steel encased form with 4 or 6 copies of one I profile at (±dsx, ±dsy) [+ (±dsx, 0)] (`ProfileCount`,
  `ProfileOffsetY` = dsy in-plane, `ProfileOffsetZ` = dsx, `ProfileName`, `BarLayers` on the b faces;
  `ColumnSectionInput.MegaDefault` 2200², C100, 4 × I 500×430×60/37 S460, Ø40). Profiles are entered by plates (no
  catalogue in the repo); the form shows kg/m and compares it with the designation's mass ("HD 400x551" → 551 kg/m).
  Design → *Mega Composite Column Design…* runs `MegaDesigner`: the major axis by `MegaColumnEngine`, a formula-for-
  formula C# port of `src/Application/Design/mega-column/engine.ts` (CTBUH / ArcelorMittal 2016 §4, Tables 11–17; only
  addition: η of EC2 Table 3.1 on 0.85 fcd for high-strength concrete); plus e0 = l0/200 (y), μd ≤ 1.0, minor axis and
  biaxial by the exact fibre plastic curve (curve c), web shear, tension. The report shows the method's Mpl,y,Rd next
  to the exact plastic Mpl,y,Rd: the engine's hn equations use fyd / fsd on the steel in the 2hn zone where EC4 Annex C
  uses (2fyd − fc), so the method is ~19 % conservative on the default section (EC4 form: within 2 % of exact).
  `tests/MegaColumnChecks.cs` ports `engine.test.ts` and the §4.2.5 worked values.
- The top bar shows only the tool icons, Connect SAP and Refresh (project name, study summary and status line removed on
  request; the offline model is named in the pane header).
- **Define → Sections…** opens the member-property table in its own non-modal window (`DefineSectionsWindow`, same
  bindings, one instance brought forward on repeat). FEM pane 0 is only a small card with *Open Sections*; the FEM
  inputs default pane is Load Patterns.
- Avoid setting `Foreground` on the whole `TabItem`; it leaks into page content. Set tab header text
  colors directly in `MainWindow.xaml`.
- DataGrid selection uses a pale blue background (`Selected`) with dark text and a `SelectedBorder`
  cell border; avoid the saturated Windows default selection blue.

## Top Bar Connection Flow

- The top bar is compact (`Padding` 10,6): tool icons on the left (the logo + title block was removed on request; the window title bar still names the app); on the right a status dot
  (green when connected), a monospace `StatusLine` (model / units / lock, or `SAP2000 - <state>`),
  the `Connect SAP` primary button and an icon-only refresh button (tooltip `Refresh connection and
  geometry`). The project name and study summary sit after the title. New Config / Save All / Load Config /
  Load Model live in **File**, Run study in **Run**, Help in **Help** — not on the top bar.
- **Tool icons** (`TopToolbar`, after the title, ETABS-like; styles `ToolbarButton` / `ToolbarTextButton` /
  `ToolbarSeparator` in `Theme.xaml`, Segoe Fluent / MDL2 glyphs drawn by the template's own TextBlock because the
  implicit TextBlock font would otherwise replace the icon font): New Config · Load Config · Save Config · Save Config As · Print All
  Reports | Lock/Unlock · Run FEM Analysis · Analysis Options · Analysis Monitor | Deformed Shape (F5) · Member Forces
  (F6) · Joint Reactions (F7) | Select · Pan · Clear Selection | Zoom extents · Zoom in · Zoom out | 3-d · xy · xz ·
  yz | Show Undeformed Shape · View Options · Show Results. The lock icon reflects `IsFemModelLocked`; unlocking uses
  the existing output cleanup and requires another run. F5/F6/F7 share a WPF result picker with a load-case/combo
  selector: F5 fixes Deflected Shape, F6 offers N/V/M, F7 fixes Reactions. The general Show Results picker still offers
  all outputs. The viewer icons call `Structural2DViewer.FitView` / `ZoomBy` / `ShowPlane`.
- `Connect SAP` connects and immediately loads geometry. Refresh reconnects and updates
  geometry / load patterns / force sources for the current session.
- `MainViewModel.StatusLine` feeds the top-bar status text; keep it in the `Update()` notify list.
- Do not move connection controls into the viewer.
- File → New Config is available without SAP2000. It closes the active configuration session and creates an offline,
  unsaved blank model with no geometry, object assignments, material/section library or FEM results. The user defines
  materials and frame/cable sections first; Edit → VCF System commits the first geometry. Saved files and per-user Print
  Setup are retained.

## Viewer Behavior

- `Structural2DViewer` renders on a white viewport (`ViewportBg`) with a faint `ViewportGrid`.
  Geometry is dark: frames `ViewportGeom`, cables/tendons `AccentBright` (dashed / dotted),
  joints hollow with a white ring.
- Viewer controls are a small light floating panel at the top-right of the canvas: projection combo,
  Fit, Reset view (returns to XZ), the `L` toggle to hide/show applied loads (two-way bound to Display → Loading),
  and three label toggles `P` / `F` / `C` bound to `ShowPointLabels` /
  `ShowFrameLabels` / `ShowCableLabels`. Frame and cable/tendon labels are drawn along the member,
  rotated to the element's screen-space direction and kept upright (`ModelCanvas.MemberLabel`); configured
  cables keep their own horizontal `CableLabel` from the overlay and are skipped by the `C` pass.
  `ToolbarContent` still renders (top-left overlay) for contextual controls.
- Axis overlay sits on a white translucent chip: X red, Y blue, Z green. The model view has no pin-legend chip and no
  "Wheel zoom · Drag pan · … Dotted tendon" hint line (both removed on request); pins are still drawn at the base joints.
- **Dimensions** (`MainViewModel.ShowDimensions`, default on; viewer toggle `D` after P/F/C, Display → Dimensions, View Options window): in a 2D projection
  `ModelCanvas.DrawDimensions` draws a chain of the distinct node positions along the horizontal axis below the model
  (each bay span, then the overall span `X span … m`) and along the vertical axis at its right (each level-to-level
  height rotated, the overall `H … m`, and `Z +…` elevation of every node level from the lowest). Positions closer than
  1 mm are one level; a label that does not fit its segment is skipped. Fitting leaves a band for them (`DimensionBandX/Y`).
- **Logo**: isometric truss with cable stays on a transparent background. `AppIcon.ico` (16–256 px, sharpened small
  frames) is the .exe icon and, through `AppIcon.Window`, the icon of every window and code-built dialog (title bar,
  taskbar, Alt-Tab); `AppLogo.png` (256 px, `Theme.xaml` `AppLogo`) is the same picture for in-app use.
- Still supports wheel zoom, drag pan, double-click fit, `Home` to fit.

## Engineering Inputs

- Standalone `EngineeringInput` defaults to 220 px wide with an 11 px muted label above a 24 px
  Consolas editor and a compact unit suffix. Pass an explicit `Width` for inline/compact placements
  (e.g. the B / NStories row) and `Label=""` to hide the label; give those an `AutomationProperties.Name`.
- Do not apply the 220 px width to DataGrid cell editors.

## Workflow tabs & shared model preview

- There is **no** standalone "SAP Model" tab. The left rail is **01 Loading, 02 Truss Height, 03 Pretension,
  04 Output Settings, 05 Iteration, 06 Post-Processing, 06A Optimization, 07 FEM, 08 Quantities** (`SelectedIndex`
  0-8: Optimization is index 6 — `TabOptimization` — FEM is index 7 — `TabFem` — and Quantity Take-off is index 8 —
  `TabQuantities`). Connecting is the top-bar `Connect SAP` button only. Project JSON stores the Tabs 01-05 inputs and the Print Setup; nothing of
  the FEM tab is saved in it (the separate FEM model file from Save Model holds that). DTO property names (`Tab02Loading` … `Tab05IterationStudy`) and JSON keys are
  unchanged legacy v1 identifiers.
- There is **one** `Structural2DViewer` (`x:Name="SharedViewer"`), hoisted out of the `TabControl` into a
  three-column grid in `MainWindow.xaml`: `ParamsColumn` (the `TabControl`, params only) | `ViewerSplitterColumn` |
  `ViewerColumn` (the `SharedViewerPane` `DockPanel`). Switching tabs never rebuilds the viewer.
- `MainWindow.xaml.cs` `ApplyViewerLayout()` (called from `Loaded` and `WorkflowTabs_SelectionChanged`): tabs
  01-04 (index 0-3) and 07 FEM (index 7) get the resizable ~40/60 params/preview split; tabs 05 Iteration,
  06 Post-Processing, 06A Optimization (indices 4-6) and 08 Quantities (index 8) hide the viewer + splitter and the
  `TabControl` takes the full width. The user's dragged split is remembered in
  `paramsColumnWidth` / `viewerColumnWidth`.
- The viewer's `Model` and every overlay come from `MainViewModel` `SharedViewer*` computed properties gated on
  `selectedTab` (`SharedViewerModel`, `SharedViewerAppliedPoints`, `SharedViewerTopPoints/Middle/Bottom`,
  `SharedViewerCables`, `SharedViewerCaption`, `SharedViewerHint`, `ShowTrussLegend`). `NotifySharedViewer()` is
  called from the `SelectedTab` setter and from `Update()`, so both a tab switch and any state refresh re-raise them.
- Overlays per tab: Loading and FEM → applied-point arrows + selection sync; Truss → Top/Middle/Bottom recoloured joints
  + legend chip; Pretension → `State.Cables` highlight/labels + selection sync; Output → `State.Cables`
  highlight/labels plus point labels (so the user can read names to pick deflection points, foundation reactions,
  member-force groups and monitored cables). FEM uses the linked geometry and Tab 01 load-point overlays. Caption
  (Consolas, top) = `HeightSummary` on Truss,
  `CablePreviewSummary` on Pretension, `GeometrySummary` on Output, empty on Loading.
- Do **not** re-add a per-tab `Structural2DViewer`; the params panels are viewer-free.

## Tab 07 FEM

- **Who runs what** (decided by the user, 2026-10-11 — do not go back to running candidates here):
  - **Run → Run FEM Analysis = one run of the model as it is defined** (`FemStudyService.ComposeModel` →
    `MainViewModel.AnalyzeFemAsync`): the model's own geometry (`State.Baseline`, no Tab 02 height change — the chord
    groups and the height range are not needed), the Tab 01 loads and the Tab 03 cable targets, with the sections,
    materials, loading and settings Mcotis holds. The run id is `FemStudyService.ModelRunId` ("Model"); it is not shown.
  - **A Tab 01 / Tab 03 range is a parametric-study input.** In this run it enters with its **first value** (the first
    `LoadingCandidates` / `PretensionCandidates` combination, so followers keep their sync rule). `FemRunSummary` on Run →
    Analysis Options says so by name: `1 FEM run · the model as defined · truss height 20 m · ranges at their first
    value: LL_ABOVE qz 5 kPa`. The first-value rule is the implementer's choice; change it in `ComposeModel` if the user
    asks for another (e.g. a separate base value).
  - **No SAP2000 in the run.** `AnalyzeFemAsync` never calls the gateway — connected or not. It solves
    `HeldFemProperties()`: what Load Model, File → Read FEM Properties from SAP and the Define / Assign edits put in
    the model (a connected run used to re-read SAP2000 and overwrite those edits). After Connect / Refresh the held
    inputs are marked out of date (`femInputsOutdated`) and the run asks for Read FEM Properties from SAP instead of
    fetching them. Not yet exercised against a live SAP2000 — only against the SAP stand-in of the checks.
  - **Many cases = the parametric study**: Run → Optimization VCF (internal solver) and Run → Run Study (Tab 05,
    SAP2000). `FemStudyService.Compose` — the Cartesian Tab 01 × 02 × 03 space — stays for the SAP2000 benchmark
    (`--fem-sap`); the app does not run it.
  - No candidate picker anywhere in the FEM results: Run Monitor, Define → Result Load Case, Show Results and the
    Results Table choose a load case only (`FemRuns` holds the one run, or nothing after a failed run).
- **Load cases are defined in the app** (Define → Load Cases / Load Combinations, `MainViewModel.LoadCases.cs`,
  `FemLoadPlan.DefaultSetup`; asked for by the user 2026-10-11: "mimic SAP2000's load case setup with our own solver").
  - **Default Setup** = SAP2000's staging for a cable structure, three `Nonlinear Static` rows with Src `App`:
    `Pretension` = `1 <target-force pattern> + 1 <self-weight pattern>` (the cable targets are met here — the fit stage);
    `SLS: 1.0DL+1.0LL` = `1 <dead> + 1 <live>`, From `Pretension`; `ULS: 1.35DL+1.5LL` = `0.35 <self-weight> + 1.35 <dead>
    + 1.5 <live>`, From `Pretension` (a case that continues from another carries its loads, so 1.0 + 0.35 = 1.35 SW). The
    names are constants of `FemLoadPlan`; keep "SLS" / "ULS" in them — the optimization's default deflection result and
    the design's default cases find them by that.
  - Built from what the app has: the self-weight pattern (first pattern with a SW multiplier that is not a Tab 01 /
    Tab 03 pattern; `SW` × 1 is added when no pattern carries self-weight), the Tab 01 load patterns (dead or live by
    pattern type, else by name — `LL…` / `…LIVE…` is live, 1.5; the rest dead, 1.35) and the Tab 03 target-force
    pattern. Without a target-force pattern there is no Pretension case and SLS / ULS start unstressed with 1.0 / 1.35 SW.
  - **The built-in default model opens on it** (`ApplyFemModel` → `UseDefaultFemLoadCases` when `isDefault`); a model
    opened with Load Model keeps its saved rows, a SAP read gives SAP's rows. **SAP Cases** (`ApplySapFemLoadCases`,
    enabled with `HasSapLoadCases`) shows the snapshot saved from SAP2000 (`RowsFromSap`); when SAP's cases leave the
    pretension or the Tab 01 loads out, `RowsFromSap` adds the default cases for the missing part (formerly
    FEM_PT / FEM_TAB01), continuing from SAP's own pretension case with whatever self-weight that case lacks.
  - Checked on the default model: the default Pretension / SLS / ULS give the displacements, cable forces and reactions of
    SAP2000's `Pretension`, `NL SLS001…-staged` and `NL ULS001…-staged` rows (SLS 39.6 mm, ULS 46.3 mm).
  - **Editing**: `+ Add` (a nonlinear static case with the Tab 01 loads, From the pretension case), `- Remove` (refused
    while another case continues from it or a combination uses it), and editable **Case** (unique; no ` + ` / ` - ` in a
    name — they separate definition terms; followers and combinations follow a rename), **Definition** and **From**
    (dropdown of `FemInitialCaseChoices`, changed in place so a From cell never loses its value; a case cannot continue
    from itself, longer cycles are refused when the run starts). Default Setup and SAP Cases replace the table (the view
    asks first) and clear the results; the other edits leave the last results on screen. Rows are saved with Save Model.
- **The flow the user set (2026-10-11)**: define load patterns → assign loads → define load cases → Run → choose the
  cases → results. Run always solves the model as it is then: current geometry, section assignments, loads and cable
  targets (the VCF System window shows all four).
  - **Define → Load Patterns** (`AddFemLoadPattern` / `RemoveFemLoadPattern`): a name box, a type (Dead / Live / Other,
    editable in the grid too) and `+ Add` / `- Remove`. A new pattern goes into `FemPatternRows` and `State.LoadPatterns`
    (so 01 Loading and 03 Pretension can pick it) and carries nothing until a load is assigned. Remove is refused while
    a load case, 01 Loading or 03 Pretension uses it. Only on the app's own model (`CanDefineLoadPatterns`): a connected
    SAP2000 model keeps SAP2000's patterns. **Role / Assigned loads are live** (`RefreshFemPatternRows` →
    `FemLoadPlan.Describe`, from `Update()`), not the text saved with the row.
  - **The loads and the cable forces are part of the parametric set-up — Edit → VCF System — as one fixed value each**
    (user, 2026-10-11: "copy them out of the MB tabs because they belong to the parametric set-up; a fixed number only,
    no range option; the ranges are set later, when an optimization is run"). The sheets edit the same state as 01
    Loading / 03 Pretension; they never show or offer a range. A value that is a range on the MB tab shows its first
    value (the one the model is analysed with, tooltip says so) and becomes fixed only when another value is typed.
    Everything is staged in the window and written by Apply / OK before the geometry, so a change of bays carries it over.
    - **Cable Force**: the Pretension column takes a value for a cable with a value of its own — an independent cable
      (→ `ApplyRange = false`, `TargetForce`) or a Same Step follower (→ `MinForce = MaxForce`); staged in
      `cableForceEdits`, written through `MainViewModel.SetCableTargetForces`. A cable that follows another's value
      (Same Values / Ratio / Offset) and a new-fan-cable row are read-only (a follower shows `follows <cable>` until Apply).
    - **Storey Force**: `Trib. width B`, `NStories`, a grid of loads — Load Pattern (from `State.LoadPatterns`) and
      qz [kPa] with a delete button — and `+ Add Load`; then **LOADED POINTS (n)** and below it the table of Fz per
      loaded point for the form's values. Written through `MainViewModel.SetStoreyLoads` (a load whose value is unchanged
      keeps its Tab 01 definition).
    - **The user chooses the loaded points** (asked for explicitly, 2026-10-11 — keep all three ways): `+ Add` opens the
      point picker with the model's points that are not loaded yet (`MainViewModel.ChooseLoadPoints`, title
      `Add Loaded Points`); `+ Add Selected (n)` loads the points selected **on the preview** — click a point or drag a
      window, as in SAP2000 (`Structural2DViewer.SelectedJoints`; the viewer raises `SelectionChanged`, added for this);
      `- Remove` takes the load off the rows selected in the table (multi-select) and off the loaded points selected on
      the preview. The preview draws a load arrow at every loaded point (`Preview.AppliedPoints`) and highlights the row
      selected in the table. All staged (`storeyPoints`) until Apply / OK; the three buttons wait for Apply while the form
      changes the number of bays (the points would be those of another model).
      An invalid input (B ≤ 0, storeys not a whole number ≥ 1, a row without pattern or qz, a pattern twice) is listed
      under the preview and greys Apply / OK.
    - Do not add a range, Min / Max / Step or an "Incr?" switch to these sheets: ranges live in Run → Optimization VCF
      (which already starts each range from these values) and, for the SAP2000 study, on the MB tabs.
  - **Run opens Set Load Cases to Run** (`RunLoadCasesWindow`, as SAP2000's): Case Name | Type | From | Status
    (Finished / Not Run) | Action (Run / Do Not Run); Run/Do Not Run Case (or double-click), Run/Do Not Run All;
    **Run Now** keeps the choice and starts the analysis, **OK** keeps it only, **Cancel** discards it. The action is the
    row's `Run` flag of Define → Load Cases. Wiring: `RunFemAnalysis` → `RunFemAnalysisFromUi` → the view's
    `MainViewModel.ChooseFemLoadCases` hook (set in `MainWindow`, like `CommitEditors`); without a view (checks)
    `AnalyzeFemAsync` runs the ticked cases directly. Every Run entry point (menu, toolbar, Analysis Options) goes through it.
- SAP properties are read on the existing STA gateway lane in kN/m units: assigned frame/cable sections,
  effective section material, elastic modulus (isotropic getter except SAP `Tendon` materials, which use the
  uniaxial getter), frame area/in-plane inertia modifiers, and frame end rotation releases. A CSI `None`
  material-overwrite sentinel means "use the section material"; it is never passed as a material name.
- `Read Properties` populates the read-only member-property grid before analysis; it does not write SAP data.
- FEM has nested `Analysis` / `Results` subtabs. Results offers, for the run and the selected load case,
  Axial Force, Bending Moment, Shear, Reactions, and Deflected Shape; the shared viewer draws filled blue-positive /
  red-negative member diagrams, green support vectors, or the visually magnified deformed shape. The adjacent
  grid lists values and units; diagram widths are auto-scaled and are not numeric scale. These are internal FEM
  combinations built from Tab 01 loading rows, not SAP2000 saved analysis cases or combinations.
- The internal model is linear-elastic 2D XZ Euler-Bernoulli frames plus axial tension-only cables with Tab 03
  target forces fitted in a pretension-only stage (FEM Analysis: ±% tolerance, default 10, and max iterations,
  default 100; all cables adjusted together) before the Tab 01 loads act on the prestressed state. FEM page tabs
  are Inputs (pin supports, "Read from SAP", and sub-tabs Sectional properties — A/E/I/releases plus γ, ρ, w = γ·A·mod w,
  m = ρ·A·mod m and Mod A/I/m/w — and Loads — editable load patterns (SW multiplier), static load cases (Run, definition
  "1 DEAD + 1 PT", continues-from) and combinations (Run, factors); read via ISapGateway.GetFemLoadingAsync, never
  written back; FemLoadPlan resolves them per candidate: Tab 01 loads replace SAP joint forces at applied points in their
  pattern; each case solved tension-only; pretension fitted in the case whose own definition holds the Tab 03 pattern;
  linear-additive combos superpose case results; the default staged cases are added for what SAP's own cases leave out — see **Load cases** below) | Analysis (fitting ±%, max iterations, Frame
  mesh segments per frame (default 10). Like SAP, each frame is first split at every member-end joint lying on it
  within 1 mm (FrameJoints.Pieces — e.g. full-length chords crossed by verticals and cable anchors), then each piece
  is meshed; cables are never split or meshed. Interior mesh nodes are statically condensed onto the joints and
  recovered as stations for diagrams/deflected shape; the viewer splits frames the same way), run controls, cable table:
  Target / Pretension / Dev. % / Status / Result / Applied P / Iter.) | View (ViewToggle buttons: Frame mesh,
  Member size, Loading — viewer overlays only while the FEM tab is open) | Results (Load case / combo selector drives the
  table, viewer, hover and the cable table Result column; Axial force view draws frame
  axial diagrams and cable tensions together. *Result values* labels N/V/M at frame-piece midpoints and cable tension
  near each cable's I-end, rotated to the projected member axis and kept upright. Force values with magnitude above
  1000 kN display in MN; moments above 1000 kN·m display in MN·m. Loading, reaction and hover labels use the same
  display conversion; stored FEM values remain in kN and kN·m. Reactions view draws Rx and Rz as separate arrows
  with separate labels — never a combined resultant). In every result view, hovering within 10 px of a member shows a section
  read-out: frames → name, x from I-end [m] and %, N/V/M and ux/uz interpolated between mesh stations, with a dashed
  section-cut marker; cables → tension, slack state, target; supports (priority) → Rx/Rz.
- Shared viewer projection dropdown has XY / XZ / YZ / 3D. 3D (StructureModel3D + WPF Viewport3D, orthographic,
  true isometric default) extrudes every frame's SAP section to scale along SAP default local axes rotated by the
  local-axis angle; shapes from PropFrame type getters (rectangle, box/concrete box, I, channel, tee, angle, circle,
  pipe/concrete pipe, general) via FemSectionProperties.Shape, else an equivalent solid rectangle with the same A, I33,
  I22 (SD, filled-tube, built-up…); cables are rods of their gross area; concrete materials are shaded concrete.
  Drag orbit, right/Shift-drag pan, wheel zoom, double-click / Fit fits. Needs Read from SAP for sizes.
- FEM deflection viewer colours the magnified shape by displacement magnitude |U| [mm], with a displayed range
  (maximum 0 = automatic). Undeformed geometry is a light dashed guide, independently hideable; contour can be disabled.
  Clicking a displayed joint shows only Ux, Uy, Uz [mm]; Uy is zero for the planar solver. These are presentation controls only.
- **FEM panes** (07 FEM, `SelectedTab` 7; `FemPane` 0–7 selects a pane in the hidden-strip `FemPanes` TabControl): Define →
  Sections (0) / Load Patterns (1) / Load Cases & Combinations (2) / Result Load Case (4), Assign → Joint
  Restraints (3), Run → Analysis Options (5: the INTERNAL 2D FRAME ANALYSIS card with Run Analysis / Cancel / Monitor),
  Result → Results (6: diagram title, summary, issue, View Results), Display → View Options (7). Each pane maps onto
  the former FEM page (`SelectedFemPage`: 0–3 Inputs, 4–6 Analysis & Results, 7 View) so the shared viewer behaves as
  before; setting `SelectedFemPage` directly moves `FemPane` to that page's default pane. The FemResultsTree is no
  longer on the page (Result menu replaces it; `ShowFemResult` shows the run).
- (Former layout, for reference) FEM used Inputs / Analysis & Results / View, with the Candidate and Load case / combo dropdowns inside Result Explorer.
  Explorer shows six flat outputs (axial/cables, bending, shear, reactions, deflection, cable target fit), without candidate branches.
  Dropdowns filter the diagram and tables; View Results opens FemResultsWindow with the same selections.
  There are no floating result buttons on the model view (removed on request): the result type is chosen in Show
  Results (toolbar) or the Result menu. Solver behavior is unchanged.
- Run Analysis — after Run Now in Set Load Cases to Run — opens the non-modal FEM Run Monitor (FemRunMonitorWindow; reopen with Analysis → Monitor): status
  (Running — indeterminate bar — / Finished / No result), live log inside a collapsed Diagnostic details expander
  (FemLog, one line per step), Cable dropdown (default Cable1a-2) and Loadcase/Loadcomb dropdown — no Candidate
  dropdown and no Follow latest, there is one run — and ConvergenceChart of P_result/P_preset per pretension-fitting run (run 0 = 0, line at 1, ±tol
  band) from FemCableResponse.FitHistoryKN. Help is a window (HelpWindow) with Workflow and FEM procedure topics;
  the ⓘ next to Monitor and top-bar Help on Tab 07 open the FEM procedure directly. Keep the topic concise:
  preparation, inputs, run settings, results and main limitations; omit debug logs and algorithm details.
  Keep the FEM procedure text in sync with the solver. Frame moments use one sign
  convention at both ends (I-end = -f2), so diagrams are continuous across joints. Targets that cannot be met
  are not an error: the run keeps the closest pretension and is marked "Not fitted" (reason in the Status tooltip).
  A run whose equilibrium solve fails has no results (`FemRuns` empty, Result menu disabled): the progress text reads
  `Analysis failed · <reason>` and `FemCableFitRows` holds one "Failed" row with the reason.
  That row has a **blank Cable cell** (`FemCableFitRow.FailedCase`) — no "(all cables)" or other placeholder. Pins are auto-detected at the lowest joints connected to near-vertical frame
  members, drawn at the base joints in the shared viewer; they restrain X/Z translations and leave rotation free.
- Tension-only cable equilibrium uses damped Newton iterations with a potential-energy line search, not a Boolean
  active-set toggle. Failure reports include the final free-DOF residual and active cable count.
  **Round-off next to equilibrium** (fixed 2026-10-11): the energy test cannot confirm a step whose energy change is
  below the round-off of the energy sum, and used to end in "line search stalled" although the Newton step was right.
  Measured on the default model at its own truss height, in a case without pretension: residual 7.1e-4 kN against a
  2.1e-4 kN tolerance, slope −1.2e-12, energy noise ≈ 4e-11, and the full step takes the residual to 3e-10 kN. Now,
  when the energy search accepts nothing, a full step that at least halves the residual is taken (`SolveEquilibrium`);
  convergence is still judged by the residual alone, and runs that converged before are bit-identical (`--fem-sap`
  unchanged). This is what made the default model fail at its baseline height — do not remove it.
- Unsupported inputs fail explicitly: non-planar geometry, tendon objects, advanced local axes, axial or
  in-plane shear releases, and rotated local axes combined with in-plane releases. P-Δ, large-displacement
  geometry updates and SAP analysis settings are not modeled.
- **Cable sag** (`FemCableSag`, Analysis checkbox `Cable sag`, default on → `FemStudyService.Analyze(…, cableSag)` →
  `PlanarFrameSolver.SolveCases(…, cableSag)`; the solver API itself defaults to off) is the one geometric
  nonlinearity. `WithSag` gives each cable the load normal to its chord in the load set being solved (uniform-
  equivalent; self-weight w → w·cos θ, so a vertical cable or a case without load on the cable stays straight).
  `SaggingTension` solves `T/k − a/T² = Δℓ + P/k`, `a = w_n²L³/24` (shallow parabola; unique positive root — no
  slack switch); the tangent is Ernst's `k/(1 + w_n²L²EA/12T³)`; the energy is `T²/2k − 2a/T` (still convex, so
  one solve per case still gives the staged result). `Assemble` linearizes a sagging cable at the current
  displacement, so the residual and the reactions are exact. `P_applied` keeps its meaning (lack of fit × EA/L) and
  is what target-force fitting adjusts. `FemCableResponse.SagM` / `TangentStiffnessRatio` → cable table columns
  `Sag mm` / `Stiff. %` and the hover read-out; combinations carry neither. The load still reaches the cable's
  end joints as before.
- **Provenance and trust**: the solver is this application's own code — nothing from OpenSees or SAP2000;
  MathNet.Numerics only. The class summary of `PlanarFrameSolver` and the README list the published sources
  (McGuire/Gallagher/Ziemian, Cook et al., Guyan 1965, Irvine 1981, Ernst 1965,
  Gimsing & Georgakis, Kanno 2011, Nocedal & Wright, Levenberg 1944, CSI manual for Target Force). Keep those
  two places in step; omit benchmark and verification references from the concise FEM help. Cite only what the code actually implements, and state only the agreement the benchmark
  below actually shows. It has not been compared with OpenSees.
- **Pretension stages** (`FemLoadPlan.FitCases` / `StagesFor`, used by `FemStudyService.Analyze`): every case whose
  own definition applies the Tab 03 target-force pattern is a stage of its own — SAP2000 meets a Target Force at
  the end of that case, under that case's loads — and a case takes its pretension from the stage it continues from
  (`InitialCase` chain). One `SolveCases` call per stage. Do not go back to one fit for all cases: on the real
  model `Pretension` (target force + self-weight) and `Pretension Only [For Ref]` both apply the pattern, and the
  staged result cases continue from the first.
- **FEM against SAP2000** (`tests/FemSapBenchmark.cs`, `--fem-sap`; fixture `tests/fem-sap-benchmark`: `model.s2k`,
  `project.json`, `sap-results.json`; `--fem-sap-capture` rebuilds it from a study on disk). Offline — never
  attaches to SAP2000. `tests/SapModelText.cs` reads SAP2000's text copy of the model (`.$2k`) into the same
  inputs `SapGateway` reads live, and refuses any analysis input it does not map. Like for like means: same
  candidates (keys, heights, every cable target checked), same supports, same stage (only the cases SAP2000's
  results were read from are run; the pretension is fitted in `Pretension` with SAP2000's own TFTol), same loads
  (ΣFz first — independent of stiffness), same cable lengths (below). Result on COT Framing_V1.2, 135 candidates:
  ΣFz 0.002 %, cable force ≤ 3.8 %, deflection ≤ 3.2 %, Fz ≤ 0.32 %, Fx ≤ 1.2 % of the largest SAP2000 value;
  the bands in the check (5 / 5 / 5 / 1 / 2 %) are regression bands for this study. Cable sag changes a cable
  force by ≤ 0.2 % at these tensions (17–51 MN).
- **Two defects the benchmark found** (the first is open, the second is fixed):
  1. `SapGateway.GetFemSectionPropertiesAsync` weighs a composite Section Designer section as base-material unit
     weight × the transformed area SAP2000 reports (CFT chords 149.6 instead of 105.2 kN/m; +4 % total load on
     this model). SAP2000 weighs each shape with its own material; `SapModelText.CompositeUnitWeight` does that
     from the Section Designer tables and is confirmed by ΣFz. The live read needs the same (the same tables are
     available through SAP2000's database-tables API) and must be verified against a running SAP2000.
  2. **Fixed.** `SapGateway.MovePointsAsync` used to move joints and leave the cable shape data alone. A SAP2000
     cable object defined by its undeformed length (`CableObj.GetCableData` type 8) keeps that number when its end
     joints move, so taller candidates were analysed with cables whose undeformed length was up to 29 % shorter
     than the chord they span — 41 % stiffer (evidence: SAP2000's own `.$2k` copy of every child model of the
     benchmark study lists `UnDefLength` = the baseline chord and `UnDefRelLen` < 1, 0.707 for Cable2a at the
     tallest height; ΣFz agrees only with baseline cable weights). **Now** the geometry write reads every such
     cable before the move and afterwards sets its undeformed length to the same length *relative to its chord*
     (`SetCableData`, type and every other setting kept), reads it back, and rolls the joints and cables back if it
     did not take. Other cable definitions (tension, sag, relative length) are left to SAP2000. Constants:
     type 8 = undeformed length; parameter [9] = undeformed length, [10] = relative length — a reply where
     [9] / chord ≠ [10] is refused before anything is written. This applies to the study's child models and to
     Tab 02's own apply. Verified against SAP2000 27.1 by `--cable-length-live <baseline.sdb> <project.json>
     [--analyse]` (`tests/CableLengthLiveCheck.cs`; starts its own SAP2000 on a copy under `.work`, never attaches
     to the user's): all 16 cables at relative length 1.000 at the tallest height, and with `--analyse` the
     tallest candidate through the real study path — SAP2000 peak deflection 93.3 → 114.5 mm, FEM 114.7 mm, cable
     forces within 1.6 %. **A study run before the fix is wrong above the baseline height and has to be run
     again**; the committed benchmark fixture is such a study, which is why the benchmark still reproduces the
     stale lengths from the lengths SAP2000 recorded (it adapts once the fixture is re-captured from a new study).
- **Save Model / Load Model** (`MainViewModel.FemModel.cs`, `Configuration/FemModelFile.cs`, `*.cotfem.json`): Inputs card
  buttons `Read from SAP` · `Save Model` · `Load Model`; `Load Model` is also in the top bar after `Load Config`, because
  the workflow area is disabled before a connection. Save Model (connected, baseline captured) re-reads the FEM
  properties/loading from SAP (edited Loads rows kept) and writes one file: `ModelInfo`, session baseline / current /
  truss-baseline geometry, load patterns, load case names, the Tabs 01–05 project (`ConfigurationExtractor.Extract`),
  member `FemSectionProperties`, `SapFemLoading`, the Loads rows and the FEM Analysis settings. Never writes SAP2000.
  Load Model restores all of it as an **offline model**: `State.Connection = "Offline model"`, `IsOfflineModel`,
  `HasWorkingModel` (= connected or offline) enables the workflow grid; command availability stays `HasSapConnection`
  except `RunFemAnalysis` / `SaveFemModel` and the 01 Loading add / remove commands (`HasWorkingModel`) and `LoadFemModel` (always, when idle).
  `AnalyzeFemAsync` solves the properties/loading the model holds — offline and connected alike, never a SAP call; the
  offline run equals the connected run of the same model
  (`tests/FemModelFileChecks.cs`). The model file is not a project file: loading closes the active project session.
  Connect SAP leaves offline mode (live model becomes the session; Tabs 01–05 are kept and re-checked by Refresh; the
  FEM inputs held are then out of date until Read FEM Properties from SAP).
- **Default model**: `src/Resources/Default.cotfem.db` (MCOTiS input database for COT Framing_V1.2_100m.sdb, embedded as
  `COTVCF.Default.cotfem.db`; `FemInputDatabase.ReadDefault`). `App.OnStartup` calls `LoadDefaultFemModel()`, so the
  app opens offline on 07 FEM, `OFFLINE · READY`. Its Tabs 01–03 span 3 loadings × 6 heights × 1 pretension — the
  parametric study's space; Run Analysis is one run at its own 20 m truss height with LL_ABOVE at 5 kPa, the first value
  of its 5 … 10 range. The bundled database's geometry, materials, sections, loading and load cases are the default setup;
  startup creates a fresh unlocked session without carrying over its saved output registrations. Connect SAP clears the
  default's geometry, Tabs 01–05 and FEM inputs (`ClearDefaultFemModel`) — unlike a user-loaded model, which keeps them.
  Replace the default by saving a new database over that file and rebuilding. Checks construct the view model directly, so
  they never auto-load it.
- The global `Load Config` picker starts in the repository `tests` folder when that folder is available.
- The connection gateway attaches to SAP2000 only. An ETABS-originated model must be open/exported into SAP2000;
  direct ETABS process attachment is not part of this FEM workflow.

## Tab 08 Quantity Take-off (`MainViewModel.Quantities.cs`, `src/Application/Quantities/`)

Concrete volume and steel mass of **one model** by assigned section, from that model's own SAP2000 data: the
**connected model**, read live from the running SAP2000, or a **study candidate**, from its own saved data. Reads
only: never a write, nothing saved in the project JSON.

- **Connected model = live** (the user asked for this: a table read from a file saved hours ago "is not real time").
  `QuantityCandidate.Connected(State.Model)` is always the first dropdown entry (`Id` = `Connected model`,
  `IsConnectedModel`), and the tab opens on it when nothing is selected. It is read through
  `WorkflowService.ReadDatabaseTablesAsync` → `ISapGateway.ReadDatabaseTablesAsync` on the connected gateway's lane
  (`QuantityTakeoffService.ReadConnectedAsync`): never cached, never from a file, so an unsaved change in SAP2000 shows.
  It is re-read on **Refresh** (`ReloadQuantities`, the icon beside the dropdown), every time the tab is shown
  (`ShowQuantityTab`), and after the top-bar refresh (the session change clears the tab and it opens on the connected
  model again). It is on request, not polled. `ReadConnectedQuantitiesAsync` runs it under `IsBusy` like every other
  call to the connected SAP2000 and **waits** while another operation is busy (connect / refresh / study) instead of
  reading in the middle of it; a `COMException` sets `Connection Lost` as elsewhere. The gateway verifies the active
  model is still the connected one (`Verify(expected)`) — if not, the error tells the user to reconnect; do not read
  whatever happens to be open. Read-only: display tables in present units; no unit change, no unlock, no save.
  Header status `Live model`; source line `Connected model · <file> · read live from the connected SAP2000 at HH:mm:ss`.
  A candidate never uses the connected SAP2000: its file is the truth, and its source line shows when it was written.
  **Verified live on SAP2000 27.1** (`--quantity-connected`, 2026-10-07, COT Framing_V1.2): 17 tables in ~1.5 s; table
  keys in title case, field keys identical to the text export, group `All` accepted (the blank group name is the
  fallback); model, units and lock state unchanged. SAP2000 rounds display-table numbers to about five figures
  (`UnitMass` 7.849, a cable `Area` 0.028802), so `SapGateway.Refine` replaces `UnitMass`, the frame-section `Area`
  and the cable-section `Area` with `PropMaterial.GetWeightAndMass` / `PropFrame.GetSectProps` / `PropCable.GetProp`
  where the getter agrees with the displayed value to 0.1 % — with that the live take-off equals the saved one.

- **Tab**: `08  QUANTITIES`, index 8, after `07 FEM`, full width (no shared viewer). It sits inside the workflow grid
  gated by `HasSapConnection`, and the view model gates too: without a connection the candidate list is empty and a
  selection loads nothing; losing the connection clears the tab and cancels a read in flight. Header status:
  `Connect SAP` / `No candidate models` / `N candidates` / `Reading…` / `<id>` (+ ` · incomplete`) / `▲ Unavailable` /
  `× Read failed`.
- **Layout**: heading `QUANTITY TAKE-OFF · BY ASSIGNED SECTION`; one line with `CANDIDATE` + the dropdown
  (`QuantityCandidatePicker`, items shown by `Id`), an icon-only `Reload`, a `Cancel` that is visible only while a
  separate SAP2000 instance is reading, and the status text; a monospace source line; a 2 px progress bar while
  loading; the problem text (warning colour for unavailable, error colour for a failed read); then the table, a
  fixed method caption and the notes — all in a 900 px left-aligned column.
- **The agreed table** — exactly `Section | Figure | Member count | Total length m | Concrete volume m³ | Steel mass t`,
  one row per assigned section (frames, then cables, natural name order) plus a bold `Total` row (it is the last item
  of `QuantityRows`, so sorting is off). No portion, grade or height-variation column: choosing another candidate is the
  comparison between models. Do not add charts, exports or further tables here without being asked.
- **Figure column** (added on request, right after `Section`): the section drawn to scale by
  `Controls/SectionFigureView` from `QuantitySectionRow.Figure` (`QuantitySectionFigure`: outlines in metres, X right /
  Y up, shapes back to front; a second outline is an opening, filled even-odd). It comes from the same stored data as
  the quantities — the Section Designer shapes in ZOrder; a standard section from `t3` / `t2` / `tf` / `tw` / `t2b` /
  `tfb` (box/tube, I/wide flange, rectangular, circle, pipe); a cable from its stored `Diameter` — and is null when
  that data does not describe a drawable shape (cell shows a muted `—`; never a partial or equivalent shape). A
  section whose quantities are withheld is still drawn as stored. Colours are the theme tokens `SectionConcrete` /
  `SectionSteel` (steel is edged in its own colour so thin plates read as steel). Every thumbnail (64 × 42) is
  scaled to fit, so `QuantityRow.FigureSize` prints the overall `W × D mm` under it and the cell tooltip shows a
  larger view. This grid alone uses `RowHeight="NaN"` / `MinRowHeight="24"`: figure rows are ~60 px, the Total row
  stays at the compact 24 px. The figure is a picture only — no quantity is taken from it.
- **Unavailable is never zero**: `QuantityRow` quantities are `double?`; null renders `n/a` in the warning colour
  with the reason as the cell tooltip, a column total with a null in it is null, and the Total tooltip states the sum
  of the sections that could be taken off. `0.000` means the section really has none of that material. Do not turn a
  missing value into 0, and do not show a partial sum as the total.
- **Candidates** (`QuantityCandidateCatalog.Discover(BaselineModelPath)`): every `Lxx_Hxx_Pxx` named by the run
  journal (`StudyRunJournal.ReadAll`: `StudyRun.json` + `RunHistory`) or by a folder under `<baseline folder>/Study`,
  whose model or text export is on disk. The model path is `StudyPathService.GetIterationModelPath`, else the path the
  journal recorded inside that candidate's folder, else the one `.sdb` in it (a study keeps its children when the
  baseline is renamed). The record that counts is the latest run that worked on the candidate; a Failed / unfinished
  one adds a warning note. The list is rescanned when the tab is shown, on Reload and on a session change while the
  tab is shown; the list object is kept while its contents are equal (`QuantityCandidate` has value equality), so
  the dropdown keeps its selection. Nothing is selected automatically.
- **Loading** (`LoadQuantities`): every selection takes a new request number and cancels the previous token; only
  the latest request may publish (`request != quantityRequest` → drop), so an earlier, slower read can never replace
  a newer selection. States: `NoCandidate`, `Loading`, `Ready`, `Unavailable` (reason shown, table empty), `Error`.
- **Data source** (`QuantityTakeoffService`), always the selected candidate's own data:
  1. `TryTextExportAsync` — SAP2000's text copy `<model>.$2k` (written beside the `.sdb` at every save; read by
     `SapTableSet.ReadText`). Used when its first line names this file, it ends with `END TABLE DATA`,
     `QuantityTakeoffCalculator.MissingInputs` is empty, and it is not outdated. **Outdated is not "older than the
     .sdb"**: the study saves the child (export written) and the analysis then writes the `.sdb` again 5–30 s later
     on the real study, so a model newer than its export is accepted when a run window of the journal covers both
     writes, or — with no record — within `UnrecordedAnalysisAllowance` (15 min). Never starts SAP2000.
  2. `ReadWithSapAsync` → `IsolatedSapTableReader` — the isolated read-only workflow: copy the `.sdb` to
     `%LocalAppData%\COTVCF\QuantityRead\<guid>`, start a `SapInstanceHost` of its own (id 90) on the copy,
     `ISapGateway.ReadDatabaseTablesAsync` (`DatabaseTables.GetAvailableTables` + `GetTableForDisplayArray`, group
     `All`, present units — no unit change, no unlock, no edit, no save), `ApplicationExit(false)`, delete the copy.
     One at a time (static semaphore); results are kept per model file stamp. The view model runs it under `IsBusy`
     (so Connect / Refresh / a study run cannot start meanwhile and attach to that instance), chains reads through
     `quantitySapRead`, and refuses while another operation is busy. Without a reader (checks) the state is
     `Unavailable`. Signatures verified against CSiAPIv1 2.15 (SAP2000 27) and the table read itself verified
     live on the connected model; **the separate instance on a private copy has not yet been run live** —
     `--quantity-live <candidate.sdb>` does that (opt-in, starts its own SAP2000, never attaches).
- **Calculation** (`QuantityTakeoffCalculator.Calculate(SapTableSet)` — one calculation for both sources; table and
  field names are matched case-insensitively because the API uses title case):
  - length = distance between the end joints in `JOINT COORDINATES` (frames and cables; cable sag / `UnDefLength`
    not applied); section = `AnalSect` / `CableSect` of the assignment tables;
  - Section Designer sections (`Shape="SD Section"`): physical area per shape from `SECTION DESIGNER PROPERTIES`
    04 I/wide flange, 09 box/tube, 12 solid rectangle, 16 polygon (01 general, 21 reference line, 30 fiber carry no
    area). **A shape with a higher `ZOrder` replaces what it covers** (`SectionDesignerGeometry.VisibleAreas`: shapes
    as convex pieces, exact convex-minus-convex cuts); a tube's interior is empty unless another shape fills it.
    Established on the stored model: the mega column reports 5.3656 m² = (4.5 − 0.21) + 0.21 × E_s/E_c, i.e. the
    embedded I-shapes displace concrete. No stored shape has `Reinforcing=Yes`;
  - any other frame section and a cable section: the section's `Area` with its `Material` (a member's `MatProp`
    overwrite replaces the material; `Default` / `None` mean none);
  - material class from `MATERIAL PROPERTIES 01` `Type`: Concrete → volume; Steel, Rebar, Tendon → mass with that
    material's `UnitMass` from table 02; units from `PROGRAM CONTROL` `CurrUnits` / the model's present units
    (`SapTableUnits`: force·s²/length⁴ → t/m³);
  - the transformed `Area` SAP2000 reports for an SD section is **only a check** (Σ A·E/E_base within 0.1 %, else
    the section is withheld) — never a quantity. Do not substitute it, the baseline's properties, the FEM
    properties (`FemSectionProperties` areas are transformed), an equivalent shape or a height-derived length;
  - each object once: a name listed twice is counted once (note); a member assigned to two sections goes to
    `(no section assigned)`; twin objects on the same joints are both counted and noted.
- **Withheld with a reason** (row n/a, reason in tooltip and notes): reinforcement (SD shape, or a concrete section
  with data in another `FRAME SECTION PROPERTIES` table — then concrete is given and steel is n/a), any other SD
  shape table, rounded polygon corners, shapes overlapping at one Z-order, non user-defined shapes, non-prismatic /
  auto-select sections, curved members, a material overwrite on an SD section, a material type that is neither
  concrete nor steel, a steel without `UnitMass`, missing section / joint / coordinates. To support a new shape:
  add its pieces in `QuantityTakeoff.cs`, a stored example to the checks, and keep the area check.
- **Checks**: `tests/QuantityTakeoffChecks.cs` in the default run — expected values worked out independently by
  hand from `tests/fem-sap-benchmark/model.s2k` and derived candidates (joints moved, a section reassigned): units,
  geometry, composite separation, overlap, grouping, totals, duplicates, every withheld case, the catalog, source
  selection and freshness, the isolated read against `ApiDouble` API doubles (private copy, candidate folder
  byte-identical, only read calls), and the view model (gating, switching, stale requests, cancel, failure,
  connection loss) and the live connected model (opens on it, Refresh and tab re-entry read SAP2000 again, waits
  while busy, failure, lost connection). `--study-ui` checks the tab, the headers, the live read, Refresh, `n/a` cells
  and gating. `--quantity-connected` (opt-in, live, read-only) takes off the model open in the running SAP2000. `--quantity-study
  <baseline.sdb>` prints every candidate of a real study from its exports (offline).

## Loading Criteria Tab

- Tab 1 is `LOADING CRITERIA`; do not restore the former per-point Fx/Fy/Fz assignment grid or an Apply-to-SAP action here.
- Tab content is params only (left ~40%); the model lives in the shared preview on the right (see **Shared Model Preview**). The params column stacks separate `APPLIED POINTS` and `LOAD DEFINITION` cards.
- `+ Add Load Locations` opens the existing WPF `SelectionService` dialog using points from the loaded geometry. The dialog supports search, multi-select, select-all, and preselects existing load locations. Selected names are mapped to the cached Global X/Y/Z coordinates, deduplicated, sorted by X then point name, and assigned one-based order values; the user does not need to preselect objects in SAP2000.
- Applied points are preview-only overlays on the existing viewer. Draw a fixed screen-size downward load arrow, normal applied-point highlight, stronger current-point highlight, and labels without modifying SAP geometry.
- Grid and viewer current selection are synchronized in both directions. Removing grid rows removes their overlays and arrows, re-sorts, and does not write to SAP2000.
- `LoadDefinition` is generic and defaults to `DL_Above` and `LL_Above`. Each row independently switches between single-qz mode and Min/Max/Step mode through `ApplyIncrementStep`.
- Disabled numeric cells must remain grey, unfocusable, excluded from validation, and derived from `ApplyIncrementStep` rather than separate UI state.
- Candidate values are internal. Single mode returns only qz; range mode returns `Min + n * Step` while the value is at most Max and does not append an off-grid Max.
- Keep `Out-of-plane tributary width, B [m]` above the load grid as a shared positive numeric input.
- `Trib. width B` and `NStories` share one compact horizontal line above the load grid (small text label + narrow `EngineeringInput`, ~118 / ~96 px). `View Candidates` sits at the right of that line, commits pending editors, then opens a read-only WPF DataGrid with `Applied Pt`, `Case`, `qz`, `TribArea`, `NStories`, and `FzkN`. Its summary line reads `N candidates for loading cases, applied at M locations.`
- A loading combination is a **Cartesian pick of one qz per load definition** — the candidate count is `∏ CandidateValues.Count` over the load definitions (`LoadingCandidates.TotalCount` / `.Enumerate`, mirrors `PretensionCandidates`). View Candidates rows are the applied-point × combination × load-definition expansion (`Case` = `C{n} · {LoadName}`). Tributary area uses X-direction midpoint tributary lengths times B; endpoints receive half the adjacent spacing. `NStories` is a shared whole-number input with minimum 1, and `FzkN = -qz * TribArea * NStories` for downward global-Z loading.
- Keep the `- Remove` action visually secondary to the primary `+ Add` location picker.
- `+ Add Load` creates an empty-name, non-increment row with qz `0.00` and focuses its Load Name cell. Each row has a compact delete icon.
- Validation is inline: qz must be nonnegative; Min must be nonnegative; Max must be at least Min; Step must be positive. No normal cell-validation dialog belongs in Tab 1.
- Load-grid numeric bindings use `NullableDoubleConverter`: clearing an active numeric cell stores `null` and leaves it visually blank instead of restoring `0.00`; validation may still mark the required active cell invalid.
- Keep star-sized columns with practical `MinWidth` values and horizontal scrolling as a narrow-window fallback.

## Truss Height Tab

Tab 2 **defines geometry-variation candidates only**. It never writes SAP2000 — there is no
"Apply to SAP" here. The primary action is `View Candidates`.

- Layout: params only — a scrollable column (`POINT GROUPS` + `REF.`, `HEIGHT RANGE`, `PREVIEW`) with
  `PreviewMessage` at its top. `HeightSummary` (`SAP · Preview · ΔH · Ref`) is now the shared-preview caption;
  the model itself is the shared preview on the right.
- Group rows carry a colour dot: Top = red `#C62828`, Middle = blue `#2563EB`, Bottom = green
  `#2E7D32`. The shared viewer paints matching enlarged joint markers (`SharedViewerTopPoints` /
  `Middle` / `Bottom`, gated on the truss tab). Only joints are recoloured — member line styles are
  untouched. The legend chip overlays the shared viewer bottom-left, shown only on the truss tab.
- `REF.` column = mutually-exclusive radio (`TrussHeightConfiguration.Reference`, enum
  `ReferenceChord`, default `Top`) bound through `EnumMatchConverter` (`{StaticResource EnumMatch}`).
  Tooltip: "Selected chord elevation remains fixed while generating truss-height candidates."
- Candidate rule (`TrussGeometryService.ChordDeltas`, ΔH = candidate − baseline height, never
  hard-coded direction): Top ref → (0, −0.5ΔH, −ΔH); Middle ref → (+0.5ΔH, 0, −0.5ΔH);
  Bottom ref → (+ΔH, +0.5ΔH, 0). Applied to **baseline** coordinates every time — no drift.
- `TrussHeightConfiguration.CandidateCount` = `floor((Max−Min)/Step)+1` (capped), recomputed on
  every Min/Max/Step keystroke; `CandidateHeights`, `LandsOnMaximum` alongside. `CandidateWarning`
  flags a range whose last step misses the maximum.
- `Capture Baseline` freezes `state.Baseline` (points + members), `BaselineHeight` H₀ and seeds
  Min = H₀, Max = 1.5·H₀, Step = H₀/20. Status: `✓ Baseline captured · H₀ …`.
- Preview height + slider are visualisation only (slider has a per-step `TickBar`; see the `Slider`
  style in `Theme.xaml`). `TrussGeometryService.Validate` no longer forces preview height on-grid.
- `View Candidates` (`MainWindow.ViewTrussCandidates_Click` → `MainViewModel.CreateTrussCandidates`)
  opens a read-only grid `# · Height · ΔH · Reference · Top ΔZ · Mid ΔZ · Bot ΔZ`; selecting a row
  sets `CurrentHeight` so the main viewer previews it. `CanViewTrussCandidates` /
  `TrussCandidatesHint` gate + explain the button (groups, baseline, valid range).
- `ApplyGeometry` / `WorkflowService.ApplyGeometryAsync` / `SapGateway.MovePointsAsync` still exist
  but are **no longer surfaced** in Tab 2 (kept for a later execution workflow).
- Page content uses `Text` / `Muted` on `Surface`. If content appears invisible, check for inherited
  `Foreground` from tab styling.

## Cable Pretension Tab (tab label "04  PRETENSION")

Tab 3 **defines candidate PRETENSION target forces** for selected SAP2000 cables. Editing it never
writes SAP2000 — assignment is a later Run-workflow step. No "Current Force" column.

- Model: `CablePretensionConfig` (Domain) — `CableName`, editable target-force **`LoadPattern`**
  (`LoadCase` remains only as a version-1.0 code/config compatibility alias; default `"PRETENSION"`;
  the grid header must say `Load Pattern`).
  The editable `ComboBox` is bound to `State.LoadPatterns`, so the user picks / types the SAP load pattern
  consumed by `CableObj.SetLoadTargetForce`; an analysis load-case name is not accepted),
  `ApplyRange` (Fixed ↔ Range, mirrors `LoadDefinition`), `TargetForce` / `MinForce` / `MaxForce` /
  `StepForce` (nullable, `NullableDoubleConverter`), `CandidateCount` (allocation-free),
  `GetCandidateForces()` (materialised on demand, capped at `MaterializeCeiling`), `Status`
  (`Configured` / `Invalid` / `Missing in SAP`), `IDataErrorInfo` for inline cell validation.
  `ApplicationState.Cables` is `ObservableCollection<CablePretensionConfig>`.
- Per-cable candidates: Fixed → `[TargetForce]`; Range → `Min, Min+Step, … ≤ Max` (never appends an
  off-grid Max; `Min==Max` ⇒ 1). Count = `floor((Max−Min)/Step + 1e-6) + 1`.
- Total = **Cartesian product** `∏ CandidateCount` — `PretensionCandidates.TotalCount` returns
  `BigInteger`; `PretensionCandidates.Enumerate` yields `PretensionCombination`s lazily (odometer,
  one dict per item). Never materialise the product to count it.
- Summary strip (top-right): `Cables N · Variable N · Pretension Candidates <BigInteger N0>`
  (`MainViewModel.CableCount` / `VariableCableCount` / `PretensionCandidateCount`), live on every edit.
- `Section`, `Area m²`, and `Capacity kN` are immutable, grey read-only columns. Section and area come from
  SAP2000; capacity is always derived as `area × fpu × 1000 × φ`. Changing `fpu` or `φ` refreshes every visible
  capacity, and Cable Forces `Regenerate` persists and uses those newly derived values without re-running SAP2000.
- Tab 3 content is params only (left ~40%); the model is the shared preview on the right (see **Shared
  Model Preview**). On the pretension tab the shared viewer receives `SharedViewerCables` (= `State.Cables`),
  highlights configured cable members, always labels them by `CableName`, and synchronizes
  `MainViewModel.SelectedCable` between cable hits and the current grid row. `CablePreviewSummary` is the caption.
- Toolbar `+ Add Cable` → `MainViewModel.AddCablesFromSelection` opens the shared `ISelectionService`
  dialog listing every cable in `State.Geometry` (search / multi-select / preselects rows already
  added); picked names that are new become `CablePretensionConfig` rows. No live SAP read — the
  geometry snapshot already holds the model's cables. `− Remove` deletes selected rows locally only.
- Apply path (not wired to a Tab 3 button; consumed by the frozen Tab 05 worker pipeline):
  `ICablePretensionService.ApplyTargetForceAsync` →
  `ISapGateway.SetCableTargetForceAsync` → `SapModel.CableObj.SetLoadTargetForce(name, "PRETENSION",
  ref P, ref RD=0.5, eItemType.Objects)`. Replace an existing same-pattern target by deleting it first,
  then read it back with `GetLoadTargetForce`. The pattern must already exist; never silently create one
  from an analysis load-case name. This is the CSI equivalent of Assign → Cable Loads → Target Force —
  do not substitute another cable load type.
- `ConfigurationValidator` cable issues now use section `"Cable pretension"`; the old
  `CableForceConfiguration` / `WorkflowService.AddCablesAsync` / force-source reading on refresh are gone
  (`SapGateway.ReadForcesAsync` / `ForceSource` remain, unused). `ICablePretensionService` /
  `CablePretensionService` exist as the prepared apply seam but are not yet wired into `App.xaml.cs`.

## Output Settings Tab (05)

Tab 04 defines study-wide output monitoring only; editing the tab does not write to SAP2000.

- Fixed output units: displacement mm, force kN, moment kNm.
- Output groups use the reserved `[O]-` namespace: `[O]-DeflectionMonitorPts`, dynamic member-force groups `[O]-{user suffix}`, `[O]-FoundationLeft`, `[O]-FoundationRight`, and `[O]-CableMonitor`.
- Deflection monitoring selects baseline Point Objects and fixes the future output to Global U3.
- The monitored-points card carries an inline `Limit band ↓U3 [min] – [max] mm` row (nullable TextBoxes with
  `NullableDouble`; blank = open bound) persisted as `DeflectionLimitMinMm` / `DeflectionLimitMaxMm` in
  `Tab05OutputSettings`. It is the **single** deflection criterion (`DeflectionLimitBand`): a candidate passes when
  Min ≤ peak downward deflection δ = −min(Uz) ≤ Max. It is the Tab 06A deflection criterion (same two values,
  editable there too) and `Deflection_Report.html` draws it as a green wash between two dashed limit lines with a per-chart
  `✓ n / N in limit band` count and ✓/✗ per candidate. It is a post-processing input: excluded from the run
  fingerprint, applied on the next Generate/Regenerate. The former hidden study Pass/Fail limits (50/75 mm) are gone.
- Member-force groups are user-defined application groups over baseline Frame Objects; one frame belongs to at most one member-force output group. A shared result case/combo is required. Extraction retains signed local P/V2/V3 and T/M2/M3 for every SAP analysis element and result station returned for each selected frame object; it must not collapse an auto-meshed frame object to one governing value.
- Foundations require exactly two baseline points on the Left and exactly two on the Right, with no overlap.
- Cable output selects baseline Cable Objects plus a SAP analysis load case. This is distinct from Tab 03's target-force load pattern.
- Configuration is persisted as `Tab05OutputSettings`; opening a project restores definitions only and never mutates SAP.
- When Tab 05 Run starts, `OutputGroupPreparationService` is the preflight gate: verify the active model is the immutable session parent and unlocked, replace/read-back all requested `[O]-` groups, save the parent .sdb, then freeze the execution snapshot. No worker or child may start before this succeeds.
- Member-force extraction retains every SAP station and result step; support reactions retain the configured Left/Right foundation points and global force/moment components. Tab 06 generates their reports from the raw JSON package.
- `Support_Reactions_Report.html` opens on its **Summary** (see "Module 06 Support Reactions" below). Its generic
  views stay under `View`: per support point (or foundation total), a range band among the in-scope
  candidates: min–max wash, darker P25–P75 core, median line, labelled extremes, and candidate dots on top (dots hidden
  above `IndividualProfileLimit`). `Band by` splits it per L / H / P id using the Deflection report's palette order
  keyed by study-wide id (colour follows the id, never the filter); dimensions with more ids than colours are disabled.
  The matrix view ends with Min / Max / Range rows.
- Children inherit the saved parent groups. The run extracts the configured results as raw candidate data;
  it does not create Excel workbooks or HTML reports.

## Iteration Study Tab (06)

Study composition plus isolated child-model building, analysis, raw-result extraction, and file-based post-processing.

- `IterationStudyConfig` (Domain, `ApplicationState.Study`): `Mode` (`IterationStudyMode` enum
  `FixPretension` / `FixHeight` / `Combined`), `FixedHeightIndex`, `FixedPretensionIndex` (0-based).
- Candidate-space counts: **N_L** = `LoadingCandidates.TotalCount` (∏ `CandidateValues.Count`, Cartesian),
  **N_H** = `State.Truss.CandidateCount` (0 until baseline captured), **N_P** = `PretensionCandidates.TotalCount`
  (`BigInteger`).
- Total runs (`StudyPlan.TotalRuns`): Fix Pretension → N_L·N_H, Fix Height → N_L·N_P, Combined → N_L·N_H·N_P.
  `StudyPlan.Enumerate` yields `(loadIndex, heightIndex, pretensionIndex)` lazily — the seam for a future engine.
- Mode radios bind `State.Study.Mode` via `{StaticResource EnumMatch}` (`EnumMatchConverter` is generic).
  Fix Pretension shows a 1-based "Combination #" `EngineeringInput` (max = N_P) + a preview of that
  combination's per-cable forces; Fix Height shows a `ComboBox` of `State.Truss.CandidateHeights`
  (SelectedIndex ↔ `FixedHeightIndex`); Combined shows neither. `CURRENT STUDY` panel prints the mode,
  each dimension's count or `FIXED · H#/P#`, and `Total Runs` (accent).
- Candidate dropdown refreshes preserve a valid fixed ID. If a saved or transient selection is unavailable
  while candidates exist, the composer falls back to the first candidate (`H01` / `P01`) and the ViewModel
  synchronizes that effective ID back to the study state before presenting the generated rows.
- Keep the candidate `ItemsSource` reference stable when its semantic contents have not changed. Broadcasting
  a false candidate-list change during an H/P selection clears the WPF `ComboBox` while the model still holds
  the new ID, making the first selection look blank and forcing a second click.
- Right column keeps the `Issues` grid + `Validate` button. `IterationSummary` property is retained but no
  longer bound.
- `StudyCompositionService` creates deterministic `StudyIteration` rows (`Lxx_Hxx_Pxx`) and complete
  loading/height/pretension candidates. Each row exposes the selected joint/load-pattern/Fz assignments
  and cable/load-pattern/target-force assignments in dedicated columns/tooltips; do not hide pretension
  after a truncated generic description. Previewing paths never creates a directory.
- Study paths are always derived from the immutable first-connected baseline:
  `<baseline folder>/Study/<iteration id>/<baseline stem>_<iteration id>.sdb`. Existing non-empty iteration
  folders or child models block the run and are never silently overwritten.
- The user selects 1–3 **total** SAP2000 instances. The already-connected baseline instance is worker 1;
  only workers 2 and 3 create additional background SAP2000 processes. The connected host is borrowed and
  never closed by the study pool. A central channel assigns the next job to the next available worker; calls
  remain sequential within each lane.
- All requested workers must start and open the baseline before any candidate is dispatched. Each job then
  reopens the same baseline, SaveAs creates the child, applies height/loading/pretension using the child
  `ModelInfo`, `File.Save`s the configured child once, runs every SAP2000 analysis case currently marked to
  run, then verifies the child **in place** and returns to the baseline before another job.
- After `Analyze.RunAnalysis` the worker does **not** `File.Save` again and does **not** re-`OpenFile` the
  child: `RunAnalysis` already persists the analyzed `.sdb` + its result files (`.Y*` / `.OUT` / `.LOG` …),
  and a redundant save-or-reopen makes SAP2000 delete those result files the next time the worker opens the
  baseline for the next candidate. `IterationModelBuilder` verifies the just-analyzed model via
  `GetCurrentModelInfoAsync` (must be locked) + `IterationCandidateVerifier` reads; `SapGateway.RunAnalysisAsync`
  throws if the model is unlocked after the run (no runnable load cases). `StudyIterationStatus.SavingAnalysis`
  / `StudyWorkerStage.SavingAnalysis` remain in the enums but are no longer emitted.
- `StudyExecutionSnapshotFactory` freezes candidate assignments, geometry/groups, paths, selections, and
  worker count at run start. Tabs 01–03, study settings, run checkboxes, and project load/save actions are
  disabled while execution is active.
- The Tab 05 monitor shows each worker ID, gate state, candidate, discrete 1/10 stage, completed count, and
  overall Ready/Running/Queued/Failed/Skipped totals. Cancellation is observed only between SAP API calls;
  active workers attempt to return to baseline and queued jobs become Cancelled.
- A candidate-level failure, including a nonzero `Analyze.RunAnalysis` result, may continue on a recovered
  worker. Failure to restore the baseline faults that worker and removes it from scheduling; healthy workers
  continue.
- The SAP/model-run boundary ends with raw files: each successful candidate persists `Candidate_Result.json`,
  then the study publishes canonical lossless `Study/RawData/Study_Raw_Results.json` plus a flat
  `Study_Raw_Results.csv` companion. It does not generate `.xlsx` or `.html`, including candidate diagnostics.
- Tab 06 is the only report-generation boundary. `Generate Outputs` / `Regenerate` reads the raw JSON and creates
  the candidate diagnostics, consolidated workbook, deflection/cable/member-force/support-reaction HTML without a SAP
  gateway. The CSV is a spreadsheet-friendly raw view; JSON is the canonical complete post-processing input.
- **`Generate Outputs` writes everything; a module's `Regenerate` writes its own report only**
  (`StudyPostProcessor.Generate(…, StudyOutputs outputs)`: `RegenerateDeflectionReport` / `…MemberForceReport` /
  `…SupportReactionReport` / `…CableForceReport` each pass their one flag). The Excel workbook is by far the
  slowest output (≈ 17 of 20 s on the 135-candidate study; a single report takes 0.6–2 s) and no report depends on
  it — never put it, the diagnostics or the other reports back behind a module's Regenerate. A report regenerated
  alone gets its own run-journal entry (`StudyRunJournal.RefreshArtifact`); the other entries stay. If the raw
  package changed since the outputs were generated (its journal hash no longer matches) every output is stale, so
  a module's Regenerate then regenerates everything and says so.
- Capacity and the Tab 06A acceptance criteria are post-processing inputs. Changing `fpu`, `φ`, or a criterion must
  not invalidate reusable analyzed children; the next explicit Tab 06 / 06A generation uses the current values.
- **The Study Shortlist is gone** (Module 08, `Study_Shortlist.html/.xlsx`, `StudyDecisionService`, the ranking
  weights and the absolute cable kN bounds). Tab 06A replaces its pass / fail gate; do not reintroduce a second
  gate. An older project or raw package that still carries those fields loads normally — they are ignored.
  Users may edit the raw JSON in place and regenerate; derived artifacts are considered stale until then.

## Tab 06 Post-Processing · Module 07 Cable Forces (`Cable_Forces_Report.html`)

Prestress target vs final analysed cable force. The file name, the report shell (header, metadata line, Result
Library, sticky `#bar`, `Force (kN)` / `% of capacity` toggle, `section.blk`, `details.block` Source Data, `#warns`)
and the Module 07 buttons / bindings (`View Report`, `Regenerate`, `Open Excel`, `Open Study Folder`) are fixed —
change the engineering content, not the identity.

- **Self-contained HTML**: inline CSS / SVG / JavaScript only. No CDN, chart library, server or external resource.
  Everything renders client-side from the embedded `#report-data` JSON (`CableForceReportDto`).
- **SAP-free and backward compatible**: `Regenerate` reads `Study_Raw_Results.json` through `StudyPostProcessor`
  (workbook regeneration via `CableForceReportGenerator.RegenerateFromWorkbook` stays supported). Only values that
  were already extracted are read (`TargetForceKN`, `ForceKN`, status, L / H / P ids, `TrussHeightM`, result case);
  a missing per-row target falls back to the candidate's own `PretensionCables` entry for the same cable. Side,
  delta force / %, ranges, governing rows and chart order are **derived at report time** and never written back
  to the raw JSON, the workbook result sheets, SAP2000 or the project. Raw schema stays `SchemaVersion = 1`.
  A format change to this report must never require a SAP2000 run or a study re-run.
- **Force development** (`CableForceMath`): `DeltaForce = Final − Target`; `DeltaPercent = (Final − Target) / Target × 100`
  (> 0 pickup, < 0 loss). Delta % is N/A when the target is 0 or missing or the final force is missing. A missing
  result stays `null` → an `n/a` gap / `Result unavailable`, never 0.
- **Capacity source of truth = current Tab 03** (`CablePretensionConfig.ResolveCapacity(fpu, φ)` = area × fpu × 1000 × φ).
  `MainViewModel.GenerateStudyOutputs` passes `CurrentCableCapacities()` plus the names of every cable on Tab 03;
  `CableCapacityResolver.Resolve` decides per **exact cable object name**: current Tab 03 value → else, only for a
  cable Tab 03 does not know, the capacity stored with the results for that same name → else unavailable. A Tab 03
  cable whose capacity cannot be resolved is unavailable (`Capacity unavailable from current Tab 3 configuration`),
  not a fallback. Never infer a capacity from forces, the chart maximum, another cable, a side, or a constant.
  Changing fpu / φ then `Regenerate` moves the limit and utilisation without touching target / final / H / P.
- **Result case wording**: charts say `Final analysed force`; the actual case comes from `CableForceStudyDto.ResultCase`
  (metadata line and tooltips). Do not hardcode `ULS`.
- **Cable Grouping** (toolbar panel): every cable gets `Side A` / `Side B` / `Unassigned`, with `Apply Grouping`,
  `Auto Detect`, `Reset`. It is presentation state only and re-renders client-side; it never changes a force,
  delta, capacity, utilisation or governing candidate. `CableGroupDetector.Suggest` embeds the Auto Detect
  suggestion (`autoGroups`): a side is suggested only when exactly one marker in the name (a/b, A/B, L/R,
  Left/Right at a word edge) has a counterpart cable differing by that marker alone; anything else — lone
  markers, two competing markers, an a/b/c series, arbitrary names — stays `Unassigned`. The report opens on that
  suggestion, states `N cables currently unassigned.`, and charts unassigned cables separately (one
  `All cables · Unassigned` chart per P while nothing is assigned). The applied grouping is kept in `localStorage`
  per baseline model; `Reset` clears it.
- **Cable order** is natural-name order (`NaturalNameComparer`: Cable1, Cable2, Cable10) — never a hard-coded list.
- **01 · Cable Force Overview**: one chart per truss height actually present in the results. Per cable, a yellow
  target range and a blue final-force range across the pretension candidates with a result there (pale below the
  minimum, solid min → max, from 0; identical targets draw one target level). The header note names what is missing
  (`Range based on 8 available pretension candidates; P08-H30 unavailable.`).
- **02 · Force Development by Pretension Candidate**: for every P candidate one chart per populated group
  (`Pxx — Side A`, `Pxx — Side B`; an empty side shows `No cables assigned to Side A.`). Two-level category axis:
  bars clustered per cable, one bar per truss height labelled with the real height (`H20` = 20 m). Yellow = target
  from 0; solid blue stacked on top = pickup (top = final); hatched blue band from final up to target = loss (lower
  edge = final); a dark cap marks the final force. Final force is labelled first, Δ% only where it fits; label
  format is decided per chart so neighbours never collide.
- **Limit**: red `#C62828` dashed `6 4`. Force mode: one continuous `Cable Force Limit` line when every displayed
  cable has an equal capacity (within tolerance), otherwise one segment per cable cluster that has a capacity, and
  nothing for a cable without one. `% of capacity` mode: one `Design Limit · 100%` line with headroom above it.
  Exceedance turns only the final-force value and cap red; bars keep yellow / blue. Draw order: grid, bars, limit,
  final-force markers and labels.
- **03 · Governing / Capacity Review**: per cable the largest final force, its candidate / height / P, current
  capacity, utilisation and a within / exceeds / N/A verdict, for the selected load (study-wide line when the
  study has several loads).
- A **Load** selector appears only when the study has more than one loading candidate; loads are never mixed
  inside a chart. One shared Y-scale from 0 per load and value mode.
- Checks: `tests/CableForceReportChecks.cs` covers the derivations in C# and runs the report's own script against
  its embedded data with Jint (test-only package, shared harness `tests/ReportPage.cs`) through the
  `window.cableReport` handle (`setMode`, `setLoad`, `applyGroups`, `autoDetect`, `resetGroups`), including the
  DOM-id regression check.
- Printing: the report uses the shared print engine below (toolbar `Print Report`). On paper it re-lays its
  charts out against the sheet, keeps one pretension candidate (Side A + Side B) together per page where it fits,
  prints a `Printed view:` line (value mode · load · grouping) and leaves the Source Data table out unless asked
  for.

## Tab 06 Post-Processing · Modules 02 / 03 / 05 Top Chord, Bottom Chord and Mega Column (`Member_Forces_Report.html`)

A visualization layer inside the existing member-force report — no SAP2000, extraction, candidate or raw-data
change. `MemberGroupSummary.Kind` reads the member-force group name: `chord` plus `top` / `upper` → `top`,
`chord` plus `bottom` / `btm` / `lower` → `bottom` (e.g. `TopChord`, `[O]-BottomChord`), `mega` plus `col` →
`mega` (e.g. `MegaColumns`). Such a group gets a `Summary` section in the report DTO
(`MemberGroupSummaryDto`: `Kind`, `Title`, `ByStation`, `Candidates`); any other group (Vierendeel …) keeps only
the generic views.

- **Derived at report time, in C#** (`MemberGroupSummary.Summarise`, tested): per iteration the governing axial
  force over **every valid station and result step** — Top Chord and Mega Column = max |P| (compression
  magnitude), Bottom Chord = max P (tension as stored) — with the signed source P, frame, analysis element,
  physical station and result step it comes from; plus, for a chord, a profile with **one governing point per
  physical station** (profile coordinate rounded to 0.1 mm, so two elements meeting at a node do not plot
  twice). Nothing is smoothed, interpolated or defaulted: an iteration without a valid station has no value,
  never 0.
- **Mega Column is Summary only — never indicate a station for it** (`MemberGroupSummary.HasStations` = false,
  `ByStation` = false). A mega-column group is a set of separate columns, so a position along "the group" means
  nothing: no `View by Station`, no station in the tooltip, no `Station [m]` column in the values table,
  `StationM` = null and an empty `Profile` in the data, and `summary(i, 'station')` / `&detail=station` stay on
  the Summary. The frame object and analysis element stay — they say which column governs. Title
  `Mega Column – Governing Axial Compression`, axis `Governing Compression |P| (MN)`, trace line
  `governing over the whole group and every result step`.
- **Summary is the default** (`Display = Summary`, value `summary`): heading `Top Chord` + `Summary` tag, then one
  card per loading candidate in scope — never mixed. Grouped columns: pretension candidates in natural order
  (P01 → P09), within each one bar per truss height (`H` + the real height), a small gap between candidates,
  value above each bar in MN to one decimal, and one trend line per height through the bar-top centres with a
  small circular marker, on the same Y-axis (from 0, shared by the group's cards). A missing iteration
  (e.g. P08-H30) has no bar and no marker, shows a muted `n/a`, and **breaks** its trend line. Titles:
  `Top Chord – Governing Axial Compression` / `Governing Compression |P| (MN)`; `Bottom Chord – Governing Axial
  Tension` / `Governing Tension P (MN)`. A `Governing values` table (optional on paper) lists every bar's source.
- **By Station is a chord drill-down, not a tab**: `View by Station` on a chord Summary opens it; it shows `By Station`,
  `← Summary` and a `Pretension Candidate` selector (actual candidates, default the first), and one line per
  truss height along the physical chord (left support → midspan → right support): `Compression |P| (MN)` for the
  top chord, `Tension P (MN)` for the bottom chord with the stored sign kept (compression near the supports
  plots below 0). Do not add `By Station` to the Display select or as a permanent tab.
- **Height styling is global**: colour by study-wide height id in the shared palette order (the Support Reactions
  height bands) and line style by height in the Deflection order (solid, dash, dot …). Same in Summary and By
  Station; a filter never repaints a height. The palette's green / orange pair is weak for red-green colour
  blindness, which is why every bar also carries its `H` label and every line its dash style.
- Choosing a chord or the mega columns in **Group** always lands on the Summary; with `All groups` those groups
  show their Summary and the other groups their profile chart. Component / Result step / Positions hide while
  only summaries are shown (they read P over every station and step). `All profiles` / `Envelope` /
  `Station matrix` remain under Display for every group, chords and mega columns included. The L / H / P filters
  apply to the summaries too.
- **Tooltips / traceability**: Summary — `P03 · H25`, governing force in MN, source P [kN] and step, frame /
  element (and, for a chord, station), iteration key and loading, group and output case; By Station — candidate,
  height, station, force, source P. The output case is the group's own (`OutputCase`), never a hardcoded name.
- **Deep links**: `#summary=top|bottom|mega` opens on that group's Summary; `&detail=station&pretension=P03`
  drills into a chord. A URL fragment cannot be passed through the Windows shell, so generation writes tiny
  launcher pages, `Top_Chord_Report.html` / `Bottom_Chord_Report.html` / `Mega_Column_Report.html` (removed when
  the study has no such group). Modules 02 / 03 / 05 `View Report` open the launcher (`OpenTopChordReport` /
  `OpenBottomChordReport` / `OpenMegaColumnReport`); without the group they fall back to the whole member-force
  report. `Print Report` in the report prints the Summary it is showing. Module 04 (Vierendeel) is unchanged.
- Checks: `tests/MemberForceReportChecks.cs` covers the derivation in C# and runs the report's own script (Jint,
  `tests/ReportPage.cs`) through `window.memberReport.summary(groupIndex, 'summary' | 'station', pretensionId)`.

## Tab 06 Post-Processing · Module 06 Support Reactions (`Support_Reactions_Report.html`)

A visualization layer inside the existing support-reaction report — no SAP2000, extraction, candidate or raw-data
change. It shares the Summary look of the member-force report (`ReportSummaryTheme.Css`: heading + view tag, cards,
legend, chart text, values table); do not fork that CSS per report.

- **Inner / outer is derived, not hardcoded** (`SupportReactionSummary.ResolveRoles`): per foundation side, the
  inner support is the point nearer in plan to the centre of all foundation points (towards the other foundation),
  the outer one the farther. Current model: Left inner Pt 198 / outer Pt 200, Right inner Pt 199 / outer Pt 205.
  A side without exactly two located points at different distances gets no role and no Summary — the report then
  opens on `Individual supports` as before. Never guess a role, never assume Left = Right.
- **Derived at report time, in C#** (`SupportReactionSummary.Summarise`, tested): per iteration and result step a
  `SupportReactionPairDto(Inner, Outer, Net)` for `Fx` and for `Fz`, in stored kN with the SAP sign.
  `Net = Inner + Outer` is the **algebraic** sum (+24.3 + (−14.9) = +9.4, never 39.2). A component is null unless
  both supports have a valid value in that step: no sum over one joint, no zero, no estimate. `Fy` and the moments
  are not part of the Summary (they stay in the generic views).
- **Summary is the default** (`View = Summary`): heading `Support Reactions` + `Summary` tag, a one-line reading
  guide, then (1) four KPI cards, (2) `Vertical Reaction Fz`, (3) `Horizontal Reaction Fx`, one card per loading
  candidate in scope — never mixed. It reads the selected **Result step**; `Component` and `Band by` hide.
- **KPI cards** cover both foundation sides and every iteration in scope: `Max Inner Compression` (max Fz inner),
  `Max Outer Uplift` (max |Fz outer| where it is negative; `No uplift` otherwise), `Max Individual Horizontal
  Reaction` (max of |Fx inner|, |Fx outer|), `Max Net Horizontal Reaction per Side` (max |Fx inner + Fx outer|).
  Each shows the value in MN, `L · P · H · side` (the load only when the study has several; `Left/Right` when both
  sides read the same to the shown decimal) and the role with its joint(s). Clicking one opens that iteration by
  candidate.
- **Chart grammar** (both charts identical): pretension candidates in natural order; within each, one slot per
  truss height; within each height **three adjacent bars — Inner | Outer | Σ Net** — about a visible zero line,
  half a bar of air after each height and a wider gap before the next candidate. Signs are kept (Fz: + compression,
  − uplift; Fx: global X direction). Σ Net is its own bar from zero, never stacked. Colour is the **role**, not the
  height: inner `#1f4788` solid, outer `#ff7f0e` solid, Σ Net hatched + outlined slate (a resultant, not a
  support). Values are signed MN to one decimal, above a positive bar / below a negative one, the net in bold.
- **Joint identification is mandatory on every bar**: two-level label `Inner` / `Pt 198`, `Outer` / `Pt 200`,
  `Σ Net` / `198+200` — on two lines when the bars are wide enough, otherwise one upright line
  (`Inner Pt 198`). The legend only names the three categories; it never replaces the bar labels.
- **Density**: when a row cannot give each bar a labelled 9.5 px, the candidates wrap to further rows (5 heights →
  P01–P05 / P06–P09), each row captioned `Fz · L01 · Left Foundation · P01 – P05`. Do not shrink the labels or drop
  the joints instead.
- **Missing iteration** (e.g. P08-H30): its height slot stays blank with a muted `n/a` — no bars, no bar labels, no
  zero, nothing interpolated — and the values table says `Result unavailable`.
- **Foundation side**: the toolbar `Foundation Side` select is the side selector — the Summary opens on the first
  (Left) side; `Right` and `Left + Right` (separate charts per side, never one bar for two sides' joints) are one
  click away. One signed axis per component is shared by every card shown.
- **By Candidate is a drill-down, not a tab**: `View by Candidate` (or a click on a bar / KPI card) opens it with
  `← Summary`, `Pretension Candidate` and `Truss Height` selectors (plus `Loading Candidate` when the study has
  several). It shows both sides of one iteration: a schematic left to right — Left outer, Left inner | Right inner,
  Right outer — with an Fz and an Fx arrow per support in its actual direction, the signed value, `Compression` /
  `Uplift`, and `Net Left` / `Net Right` (ΣFz, ΣFx) below; then a table `Foundation | Role | Point | Fx | Fz` with
  the two resultant rows. A missing iteration says so and draws nothing. Do not add `By Candidate` to the View
  select.
- **Tooltips / traceability**: candidate · height · side, role · joint, signed MN, behaviour (Fz), source kN and
  result step, iteration key and loading, output case; the net shows `Σ Fx = Fx198 + Fx200 = …`. A collapsed
  `Reaction values` table (optional on paper) lists the kN source of every bar.
- **Paper**: the Summary re-lays its charts at the sheet width (`reportPrint.register`); a chart card continues
  on the next sheet between its rows. `Print Report` prints the view and side the report is showing.
- **Deep links**: `#group=Right|all`, `#view=supports|totals|matrix`, `#detail=candidate&load=L01&pretension=P03&height=H02`.
- Checks: `tests/SupportReactionReportChecks.cs` covers the derivation in C# and runs the report's own script (Jint,
  `tests/ReportPage.cs`) through `window.reactionReport.summary('summary' | 'candidate', { loadId, pretensionId, heightId })`.

## Tab 06A Optimization (`MainViewModel.Optimization.cs`, `Study/Optimized/`)

Qualifies the stored candidates against acceptance criteria and writes the existing reports for the qualified
("optimized") set. Post-processing only: raw data in, no SAP2000, never part of a run or its fingerprint.
"Optimized" means "passes every active criterion" — there is no ranking or objective function.

- **Tab**: `06A  OPTIMIZATION`, index 6, after `06 POST-PROCESSING`. `IsEnabled` is bound to `HasRawData`; the
  header status reads `Needs raw data` / `Reading raw data…` / `Qualified n / N`. Layout: raw-data strip with
  `Generate Optimized Reports`, `Print All Reports…` (the optimized set as one PDF — see the print engine) and
  `Open Optimized Folder`; a CRITERIA card and a RESULT card side by side; the
  CANDIDATES table (virtualised `DataGrid`, status filter) filling the rest.
- **Metrics once, criteria as often as they change**: `StudyMetricsStore.Load` derives one `CandidateMetrics` row
  per candidate (δ = `DeflectionLimitBand.PeakDownMm` over every monitored point; the final force of every cable)
  and keeps them in `Study/RawData/Study_Candidate_Metrics.json`, a rebuildable sidecar used only while the raw
  file's length and timestamp still match. `StudyQualifier.Evaluate` runs on those rows — a criterion edit never
  re-reads the raw package (hundreds of MB for a large study). Loading happens off the UI thread.
- **Criteria** (`QualificationCriteria`, from `OutputSettingsConfiguration`): each has an enable switch.
  1. *Deflection within range*: Min ≤ δ ≤ Max, inclusive, blank bound open. **It is the Tab 04 limit band** —
     the same `DeflectionLimitMinMm/MaxMm`, edited in either place; never add a second deflection range.
  2. *Cable forces below the limit*: every cable's final force / design capacity ≤ `MaxCableUtilization`
     (default 1.00). Capacity comes from `CableCapacityResolver` exactly as in the Cable Forces report (current
     Tab 03, then stored). The governing utilisation and its cable are reported whether or not the criterion is on.
  The switches and the limit persist in `Tab05OutputSettings` (`DeflectionCriterionEnabled`,
  `CableCriterionEnabled`, `MaxCableUtilization`); older projects load with both on and 1.00.
- **Statuses**: `Qualified` / `Not qualified` (each failed criterion with its governing value, limit and cable) /
  `Not assessed` (a result an active criterion needs is missing or invalid, a cable has no capacity, or the
  analysis did not complete). Not assessed is never qualified and a missing value is never 0. A criterion that
  fails wins over missing data elsewhere. To add a criterion: a verdict in `StudyQualifier.EvaluateOne`, a switch
  in the settings, a row in the summary's criteria table.
- **Optimized reports**: `StudyOptimizationProcessor.Generate` writes into `Study/Optimized/` —
  `Optimization_Summary.html`, `Optimization_Criteria.json`, and Deflection / Member Forces (+ chord and mega
  launchers) / Support Reactions / Cable Forces from **the existing generators** given the qualified subset and a
  `ReportScope`. No forked chart code. The full reports in `Study/` are never touched. With no qualified
  candidate only the summary is written and an earlier set's chart reports are removed.
- **`ReportScope`** (optional last argument of each generator) is what makes a subset report honest:
  a banner under the header and ` · Optimized set` in the printed page header; the full study's L / H / P ids, so
  an id keeps its colour, line style and order when others drop out; and the excluded iterations, so a blank slot
  reads **NQ / "Not qualified"** — distinct from `n/a` = no result — never a zero. Diagnostics links point back to
  the study folder (`../`).
- **`Optimization_Summary.html`** (rendered in C#, no script of its own): KPI cards (qualified, not qualified with
  deflection only / cable only / both, not assessed, the study's own δ and utilisation range), the criteria table,
  the **acceptance map** (δ against governing cable utilisation for every candidate, limits drawn, acceptance window
  shaded, qualified points filled), the qualified candidates, and every candidate with status and reason
  (optional on paper).
- **Staleness**: `Optimization_Criteria.json` records the criteria, the capacity signature, the raw-data identity
  and the qualified keys; `StudyOptimizationProcessor.IsCurrent` drives the tab's "up to date" / "out of date —
  regenerate" line.
- Checks: `tests/OptimizationChecks.cs` (metrics, inclusive bounds, not-assessed cases, the sidecar, the optimized
  set with untouched full reports, identity and NQ marks through the report scripts, staleness, 1,000 candidates)
  and the `--study-ui` Tab 06A block (enabled state, live qualification, filter, generate).

## Report Print Engine (`ReportPrintEngine`, Print Report screen, Tab 06 Print Setup)

Reports stay one self-contained HTML file. **One report** is printed from the report: its `Print Report` button
opens a composition screen — setup on the left, a live preview of the sheets on the right — and what the preview
shows is what the browser prints. **Everything at once** is printed by the application: `Print All Reports…` writes
every report as one PDF. **Every HTML view is on the engine**: Deflection, Member Forces, Support Reactions, Cable
Forces, the Tab 06A Optimization Summary and the per-candidate diagnostics. A new report must plug in the same way —
do not give a report its own print header / footer, and do not put a per-module PDF button back (`Export PDF` and
the `Export*Pdf` commands were removed on purpose).

- **Print All Reports…** (Tab 06 beside `Generate Outputs`: `PrintAllReports`; Tab 06A beside
  `Generate Optimized Reports`: `PrintAllOptimizedReports`): a Windows Save dialog asks only where to save — no
  print dialog — then `ReportPdfExporter.ExportPackage` (headless Edge, else Chrome, private profile) writes one
  PDF and the app opens it. Content, in module order (`StudyReportPackage.Items`): Optimization Summary
  (optimized set), Deflection, the member-force report once per group Summary it has (Top Chord, Bottom Chord,
  Mega Column — else once as it opens), Support Reactions, Cable Forces. Module 04 Vierendeel has no view of its
  own and is not in the package. It prints with the **Print Setup card as it is now** (not the values embedded
  at generation, and not what a browser remembers), pages numbered through (`Page 12 of 31`).
  **How**: `StudyReportPackage.Html` is a small page with one frame per report, opened as `#print=package`. Each
  report composes its own sheets — the same ones its Print Report screen shows — so nothing is drawn twice or
  differently. Frames talk by `postMessage` (`{ cotvcf: "print", type }`): report `ready` → package `setup` →
  report `sheets` (count, paper) → package `number` (offset, total). The frame is made exactly count × sheet
  height tall; in a package a sheet is sized in mm, not px, so the page cuts fall on the sheet edges. Reports
  generated before this existed are refused with "Regenerate" (`ReportPrintEngine.SupportsPackage`); a report
  that never answers is named on a first page of the PDF. The 135-candidate study (56 MB member-force report,
  loaded three times) prints 31 pages in about 10 s.

- **Print Report screen** (`window.reportPrint`, `#pr-screen`): the `Print Report` button — accent-outlined,
  placed straight after the main toolbar controls in every report, never behind hint or status text — opens the
  screen; it never calls the browser's print dialog itself. Left (`#pr-setup`): Company, Project, Job No.,
  Revision, Prepared by, Checked by, Footer note, Logo (`Choose…` / `Remove`), Paper (A4 / A3 / Letter),
  Orientation, Page numbers, Date, Include source data table, `Print / Save as PDF`, `Use this report's defaults`.
  Right (`#pr-pages`): the composed sheets, with a page count and a preview size (`Fit width` / `Whole page` /
  `100 %`). Every edit re-stamps or re-composes the sheets at once. `Print / Save as PDF` is the only thing that
  opens the browser dialog; `✕ Close` returns to the report and leaves nothing behind (no `pr-*` class on `body`).
- **Sheets, not paged media**: the preview's `.pr-sheet` elements (`.pr-head` / `.pr-body` / `.pr-foot`, sized
  from the paper in mm) *are* the printout — `@page{size:W H;margin:0}`, one sheet per page. While printing the
  live report is clipped to zero height, not `display:none`, so SVG pattern definitions still resolve. Bands:
  16 mm header (logo at most 7 mm tall), 13 mm footer, 10 mm sides — keep them this slim, the sheet is for the
  charts. Header: logo (or company) left, report name · model centre, project over `Job No. … · Rev …` right.
  Footer: company over `Prepared by … · Checked by …` left, note centre, date over `Page N of M` right. Ctrl+P
  prints the same sheets (`beforeprint` / `afterprint`).
- **Fixed content order**, whatever the order on screen: information → legends → general notes → summary →
  charts → source tables. Legends and general notes are stated once, under one `Legend · general notes` heading
  before the first chart — the same legend under several charts collapses to one (same content = same legend).
  A note that belongs to one chart stays inside that chart's block and travels with it.
- **A report marks its blocks**; the engine clones them out of the live DOM, so the report on screen is untouched:
  - `data-pr="info|legend|note|summary|chart|source"` — one printable block and its place in the order. A block is
    kept whole on a sheet (title + chart + its notes) and is scaled down only when it is taller than a sheet;
  - `data-pr-keep` — a heading that stays with the block after it;
  - `data-pr-split="selector"` — a block that may continue on the next sheet between the matched parts, its frame
    (title, table head) repeated; a single large table splits between rows anyway;
  - `data-pr-id` / `data-pr-type` / `data-pr-title` / `data-pr-from` — identity for the report model;
  - `data-print-controls` on the toolbar(s) + one `data-print-view` element → a `Printed view: Group: … ·
    Component: … · Loading: all` line; `data-print-fit` on a wide table's wrapper → scaled to the sheet width;
  - `class="no-print"` / `class="print-only"`. A tagged block nested in another is printed in its own place, not
    twice; collapsed `<details>` are opened while composing.
  A document that marks nothing prints its top-level blocks in the order they stand (the diagnostics).
  Controls never print as controls: `no-print` ones are dropped, and a `select` / text input inside a block
  becomes plain text (`span.pr-val`), so the sheet records what the chart was showing.
- **Report model**: `reportPrint.model()` returns `{ metadata, info, legends, generalAnnotations, summary, charts,
  sourceTables }`, each entry `{ id, type, title, duplicatedFrom, … }` in print order. `charts` is the single
  ordered list of the chart section — a new chart is appended to it, never inserted.
- **Paper rules are `.pr-sheet`-scoped CSS**, not `@media print`: the preview is on screen and must look like the
  paper. Only the engine has an `@media print` block. A report whose charts are drawn against the window width
  registers `window.reportPrint.register({ before(page), after() })` and re-lays them out for `page.contentPx` /
  `page.contentHeightPx`. Charts are inline SVG and are never rasterised — text stays text, bars and hatches stay
  vector, colours print exactly (`print-color-adjust: exact`). Never let content overflow a sheet.
- **Setup values**: the report embeds the Tab 06 Print Setup as its defaults (`#print-data`). What the user types
  in the Print Report screen is kept by the browser (`localStorage`, key `cotvcf.printSetup.v1`, shared by every
  report opened from disk in that browser profile) and wins over the embedded defaults until `Use this report's
  defaults`. A logo too large for the store is printed for the session and has to be chosen again next time.
- **Tab 06 Print Setup card** (`ReportPrintSettings`, `PRINT SETUP · HEADER / FOOTER`, above module 01): the
  starting values of that screen — company, logo file, project, job no., revision, prepared by, checked by, footer
  note, default paper, orientation, page numbers, date, include Source Data. The card is the one source; it is
  kept in two places. **Per Windows user** (`ReportPrintSettingsStore` →
  `%LocalAppData%\COTVCF\ReportPrintSettings.json`), saved on every edit — what the card shows at start-up. **In
  the project file** as `printSetup` (see Config Project File): written by `Save All` and by the card's
  `Save Print Setup`, restored onto the card by `Load Config`; the status line under the card reads
  `Saved in <file>` / `Modified * — differs from <file>` / `Not in <file> yet` / `No project open`
  (`PrintSetupProjectStatus`). It is never part of the study folder or the run fingerprint. Like capacities and
  criteria it is a post-processing input: applied on the next Generate / Regenerate
  (`StudyPostProcessor.Generate(…, printSettings)`). `MainViewModel` takes the store as an optional constructor
  argument; without one (checks) settings stay in memory.
- **Logo**: `ReportPrintEngine.LogoDataUri` embeds the file as a data URI (PNG / JPG / SVG / GIF / WEBP / BMP, at
  most `MaxLogoBytes`), so the report needs no external resource. A missing / unsupported / oversized logo is
  reported on the card (`DescribeLogo`) and the company name is printed top-left instead. SVG prints as vector.
  Candidate diagnostics are written once per candidate, so they are described with `embedLogo: false` and print
  the company name top-left — a study with hundreds of candidates must not carry hundreds of logo copies.
- **URL fragment**: `#print[&paper=A3&orientation=portrait&source=1]` composes the sheets on load (what the checks'
  headless browser prints); `#print=screen` opens the Print Report screen; `#print=package` is one report of
  Print All Reports (waits for the package's setup). Report-specific options ride on the same fragment (Cable
  Forces: `mode=ut`, `load=L02`; Member Forces: `summary=top|bottom|mega`, `group`, `component`, `view`,
  `sampling`, `band`; Support Reactions: `group`, `view`).
- Engine pieces a report emits: `ReportPrintEngine.Css` (inside its `<style>`), `PageStyle` (in `<head>`),
  `ToolbarButton` inside its toolbar (or `Bar` when it has none), and `Scripts(dto)` before its own script.
- Per report:
  - **Deflection** — the candidate definitions (`#candKey`, collapsed on screen) are the legend and the warnings
    the general notes, both above the charts. The charts are one ordered list (`#panels`): each has its own id
    (`chart_deflection_03`), and **`Duplicate` always appends the copy to the end of the list** with the source's
    current state (case, view, filters, zoom, highlights) and the title `… – Copy`; the copy is independent from
    then on. `window.deflectionReport.charts()` lists `{ id, duplicatedFrom, title }`. Each chart is redrawn at the
    sheet width (`preserveAspectRatio="none"`, so it must be redrawn, not stretched); the Source Data Matrix
    prints only when asked and continues over sheets by block.
  - **Cable Forces** — legend and capacity notes once; overview and per-pretension cards as chart blocks, the
    governing table with them; Source Data when asked.
  - **Member Forces** — prints the current toolbar view; the chart legend once after the section heading, a
    missing-result note inside the card it concerns, matrix views continuing over sheets with repeated headings.
  - **Support Reactions** — KPI cards as the summary, the Inner / Outer / Σ Net legend once, one chart card per
    P × H block continuing between rows.
  - **Optimization Summary** — tagged in C#: KPI cards and criteria as the summary, the acceptance map and the
    qualified-candidates table as charts, the all-candidates table as source.
    **Diagnostics** — unmarked, printed in document order with the `Bar`.
- Checks: `tests/ReportPrintChecks.cs` (settings store, logo embedding, sheet composition and order, saved setup,
  `#print` options, every report carries the engine, the package's items / page / refusal of older reports)
  through the Jint page in `tests/ReportPage.cs`. Real layout needs a real browser: `--report-pdf` (opt-in) prints
  every report with `ReportPdfExporter`, prints the package (as many pages as the reports one by one; the setup
  handed over wins) and drives the Print Report screen of each report through `tests/ReportBrowser.cs` (order, no
  overflowing sheet, live edits, A3 / portrait, source toggle, saved setup, print called once, clean close).
  `--study-ui` checks the Print Setup card, the seven `View Report` buttons, that no module has a PDF button, and
  Print All Reports on both tabs with a fake Save dialog and a fake printer (`AskPdfPath`, `PrintPackage`).

## Config Project File (`src/Configuration/`)

The JSON is a **Mcotis project** (document-editor model), not an export/import blob. It stores Tabs 01–05,
including the Tab 05 mode/fixed candidate/selection exclusions, and the Tab 06 Print Setup. Never serialise
ViewModels; project save/load never mutates SAP.

- **`printSetup`** (`PrintSetupConfigDto`: company, logo path, project, job no., revision, prepared / checked by,
  footer note, paper, orientation, page numbers, date, include Source Data) is **not a tab section**: no
  `ConfigTabs` flag, not in `PresentTabs`, never validated against the model. `ConfigService.SaveAsync(uiTabs,
  pickPath, printSetup)` writes it only when the caller hands the card over — `Save All`
  (`RunSave(ConfigTabs.All, printSetup: true)`) and the card's `Save Print Setup` (`RunSave(ConfigTabs.None,
  printSetup: true)`, which changes nothing else). A per-tab save keeps the project's `printSetup`, like every
  other section it does not own. `OpenProjectFile` applies it with `ConfigurationApplicator.ApplyPrintSetup`; a
  project saved before it existed has no section and leaves the card as it is. The logo is stored as a path — on
  a machine without that file the card says so and the company name is printed instead.

- `ConfigProjectSession` (held by `ConfigService`): `FilePath`, in-memory `Project` (`CotVcfConfigDto`),
  `Tab02/03/04Dirty`. Once a project is open the in-memory `Project` is the source of truth; the file is
  just persistence. `MainViewModel.ProjectName` shows `Project: <file>` in the top bar.
- **Load Config = Open Project** (`MainViewModel.OpenProject`): full-or-nothing — every present tab must
  validate against the connected model (`check.AllPresentOk`), else the current project is untouched.
  On success: apply present tabs, `SetActiveProject`, `ClearDirty()`. A null tab section = "Not configured",
  not corrupt. Renamed model (`ModelAppearsRenamed`) → still opens, yellow note.
- **Per-tab `Save Config`** (`SaveTabConfig` param `Loading`/`Truss`/`Cable` → `SaveTab`): overwrites
  **only that section** of a clone of the in-memory project — all other tabs come from `Session.Project`,
  **not their unsaved UI**. Rebuilds the snapshot from all present tabs (`ConfigurationExtractor.RebuildSnapshot`),
  re-validates the whole project vs the connected SAP model (§29), writes the whole file to `Session.FilePath`
  (no dialog), clears only that tab's dirty flag. First save with no active project → Save-As, becomes the project.
- **Top `Save All`** (`SaveAllConfig` → `SaveAll` → `SaveAsync(ConfigTabs.All)`): extracts every tab from
  the UI, validates all, **atomic** — any failure writes nothing (one popup). Otherwise rewrites the whole
  project, clears all dirty.
- `ConfigService.SaveAsync(uiTabs, pickPath)` is the single save engine; `SaveOutcome { Saved | Cancelled | Blocked }`.
- Tab header status (`MainViewModel.TabState`): `Not configured` → `Review inputs` (invalid) →
  `Not saved` (configured, absent from project) → `Modified *` (differs from project) → `Saved`.
  Dirty flags set from the VM's change subscriptions, suppressed during `applyingProject`.
- `CotVcfConfigDto` tab sections are **nullable** (`PresentTabs`); `ModelSnapshotDto` also carries
  `OriginalFileName` (preserved across renamed-model saves) alongside `FileName`.
- DTO (`ConfigDtos.cs`): `CotVcfConfigDto { ConfigVersion="1.0", Units(m/kN/kPa), Model(snapshot),
  Tab02Loading, Tab03TrussHeight, Tab04CableForce }`. Fixed internal units. `Tab03` stores **ΔH relative
  to baseline height** (`Min/Max/StepDeltaH`), not absolute heights — on load the applicator re-runs
  `WorkflowService.CaptureBaseline()` then reapplies the offsets. Enums serialise as strings
  (`ReferenceChord`, `CableForceMode`). No candidates / Cartesian products / analysis results / UI state.
- `ModelSnapshotDto` carries only the points / cables / load patterns the config references, taken from
  `State.Baseline` (original coordinates). Used for validation only, never re-applied. Compat check:
  points/coords ±1 mm, cable endpoints I→I/J→J, load patterns, load cases (`ISapGateway.GetLoadCaseNamesAsync`);
  `TabCheck` per tab; `FileErrors` (bad version / no tabs) abort the whole open.
- `ConfigurationApplicator.Apply(project, check)` rebuilds `State.AppliedPoints` / `LoadDefinitions` /
  `Truss` groups / `Cables` for the tabs whose check is `Ok`; the reactive `Update()` / `UpdatePreview()`
  path regenerates candidates and refreshes the preview (Top=red / Middle=blue / Bottom=green joints).
  `Tab03` stores ΔH vs baseline → the applicator re-runs `CaptureBaseline()` then reapplies the offsets.

## Important Files

- `src/Presentation/MainWindow.xaml`: main window shell, compact top bar, left tab rail, page layouts.
- `src/Presentation/Theme.xaml`: density tokens, palette (chrome + `Viewport*`), button / grid / tab
  styles, and the `AppLogo` Vierendeel-truss `DrawingImage` (window icon + top-bar mark).
- `src/Presentation/MainViewModel.cs`: command wiring; `StatusLine` for the top-bar status text.
- `src/Presentation/MainViewModel.Quantities.cs` + `src/Application/Quantities/`: Tab 08 Quantity Take-off.
- `src/Presentation/Controls/Structural2DViewer.xaml`: dark viewport shell + floating overlay controls.
- `src/Presentation/Controls/Structural2DViewer.xaml.cs`: canvas drawing (viewport brushes), fit/pan/zoom,
  reset-to-XZ, axis colors, point labels.
- `src/Presentation/Controls/EngineeringInput.xaml`: compact 24 px numeric editor with optional label.
- **Standard materials** (MaterialLibrary.Standard, source "Library"): S355 (S355J2, EN 10025-2), S460 (S460M, EN 10025-4), S690 (S690QL, EN 10025-6) with fy / fu and thickness bands in the notes; C70/85, C80/95, C90/105 (EC2), C100/115 (EN 206), C120/135, UHPC C130/145, C150/165, C175/190 (DAfStb / NF P18-470) and C185/200; Ecm by the EC2 expression. Added once per model load by `EnsureMaterials` (`ResetMaterialLibrary` on Load Model); a model material of the same name keeps its E / γ and only gains missing strengths (`FillGaps`). SGD rates from `MaterialCosts` (S355 ≈ 3,655 /t is an estimate — no published Singapore rate found).
- **Model view tools (SAP2000-style):** Select is the default — a left click selects / deselects the joint or member under the cursor (frames win over coincident cables), a drag on empty space draws a window (left→right: wholly inside; right→left: crossing); the selection is highlighted in amber with an 'n Points, n Frames selected' read-out. Pan (toolbar four-arrow toggle, Structural2DViewer.PanMode) makes the left drag move the model; the middle button always pans. Esc leaves pan mode and clears the selection; Clear Selection on the toolbar. Show Undeformed Shape (frame icon, Display menu, F4) hides the result overlay (MainViewModel.ShowUndeformed) until a result is shown again.
- **Assign menu (SAP2000 layout):** Joint ▸ Restraints (Pin Supports)…, Frame ▸ Frame Sections…, Cable ▸ Cable Sections…. The section items act on the members selected on the model view (frames / cables only; otherwise a 'select first' message) through `AssignSectionWindow` (Find this property, the library list with the selection's section preselected, Define Frame/Cable Sections…, OK / Close / Apply, double-click = OK) → `MainViewModel.AssignSection` (`PropertiesFor` from the library section; each member keeps its local axis and modifiers; cables also update Tab 03 area / capacity; FEM results cleared; SAP2000 never written).
- **Frame local axes:** Assign ▸ Frame ▸ Local Axes… acts on selected frames from either the 2D canvas or a click in the 3D view (drag still orbits; selected members highlight amber). `AssignFrameAxisWindow` accepts an absolute angle in degrees about local 1. `MainViewModel.AssignFrameLocalAxis` keeps each member's section, recalculates its in-plane inertia from I33 cos²θ + I22 sin²θ, updates the 3D section orientation, clears results and saves the angle in the model database; SAP2000 is not written.
- **Real sections in 3D** (asked for by the user, 2026-10-12: "it only shows a rectangular box — show the real section; a composite one see-through"): a frame whose plain `FemSectionShape` is not its outline gets a `SectionDrawing` (`Application/SectionDrawing.cs`: regions of convex polygons per material in the local 2 / 3 axes, the enclosing one marked `Shell`), handed to the viewer as `Structural2DViewer.SectionDrawings` (`MainViewModel.SharedViewerSectionDrawings`, by member; it follows `SharedViewerSections`). `StructureModel3D.Build` extrudes one piece per material — steel and concrete in their own colours — and, for a composite section, draws the shell see-through (`CompositeShellOpacity` 0.55, front faces only, added after every solid piece because WPF blends only over what is already drawn) so the material inside shows. Sources: (1) the library's composite sections as defined — CFT = tube walls + concrete core (rectangular or round), steel encased = concrete + the I profile, mega composite = concrete + its 4 / 6 profiles (reinforcement is not drawn); (2) a **General / Section Designer section**, which the model holds only as transformed A / I33 / I22: `SectionDrawings.Recover` reads the dimensions its name states and accepts them only when they reproduce those properties — three dimensions (`CFT_SHS[1200x2000x150_S460]_C70/85`) as a tube, hollow or filled (Ea / Ec from the materials), each property within 1 %; two dimensions on a concrete section (`MegaCol_1.5x3_…`) as a solid block when the stored properties are the block's plus a little steel — drawn as concrete alone, its embedded steel is not known. The inertias decide which dimension is the depth. Anything else stays the equivalent solid, as before; never draw an outline that does not check. On the default model all 38 frames are now real (chords 2.0 × 1.2 m instead of the 2.27 × 1.45 equivalent). A standard shape (box, I, pipe …) needs no drawing. Selection restores each piece's own back material (`MemberVisual.BackMaterial`; a see-through piece has none). The deflected shape uses the same outlines, shaded solid by the contour. Frame Information's Shape row reads the drawing's description.
- **Deflected shape in 3D** (asked for by the user, 2026-10-12): with the result on Deflected shape, the 3D view draws it too (`StructureModel3D.BuildDeflected`, chosen in `Structural2DViewer.Rebuild3D`): every member with its to-scale section moved by its joint displacements — a meshed frame through its stations, so it bends, keeping the section axes of the member as built — enlarged so the largest |U| is 5 % of the model's largest dimension, shaded by |U| with the 2D palette (one mesh per member, the contour read by texture coordinate from `ContourBrush`, so selection and hit-testing still work per member), and the model as built as thin grey axes. The Undeformed guide / Contour |U| / Range options are the same controls as in 2D (drawn above the 3D host) and rebuild the scene; a legend sits bottom-right (`Deflection3DLegend`: range and `Shape magnified × n`). The solver is planar: displacements are X and Z. There is no node read-out in 3D (2D: click a node).
- **Right-click → Frame / Cable Information** (asked for by the user, 2026-10-12: "open the dialogue just like ETABS for frame properties"): a right-click on a member in the 2D canvas (`ModelCanvas.RequestInformationAt`) or in the 3D view (a right-click that does not drag — a right-drag still pans; `RequestInformation3DAt`) selects it and raises `Structural2DViewer.MemberInformationRequested`; `MainWindow` opens the modal `ObjectInformationWindow`. Tabs **Geometry** (identification with the VCF role, length, slope, End I / End J with coordinates), **Assignments** (section, shape, material; E, A, I, unit weight, self-weight and mass per length; property modifiers; a frame's local-axis angle and end moment releases, or a cable's 03 Pretension pattern / target force or range / sync / capacity) and **Loads** (self-weight per pattern with a multiplier, distributed / point / gravity member loads, the cable target force). Section bands over name / value lines; values are read-only boxes so they can be copied. Content comes from `MainViewModel.MemberInformation` (testable, no window). **Modify Assignments…** opens the existing editable `MemberAssignmentWindow` (still on double-click) and the form refreshes. Members only — a right-click on a joint or on empty space does nothing.
- **Axis display:** the model viewer's top-right **A** toggle, Display ▸ Frame local axes, and Display Options ▸ Frame local axes share `ShowFrameLocalAxes`. They draw red local 1, green local 2 and blue local 3 arrows on frames in 2D and 3D. With frame selection, only those frames' axes are shown; otherwise all frames are shown. The 3D arrows use the same section angle and local-axis basis as the extruded frame sections.
