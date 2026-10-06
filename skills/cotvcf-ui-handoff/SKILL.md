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
- `COTVCF.sln` intentionally includes only `COTVCF.csproj`. Do not add `tests/COT_VCF.Checks.csproj` back to the solution unless the user asks.
- `.gitignore` includes `.vs/`, plus build folders such as `bin/`, `obj/`, `.work/`, `tests/bin/`, and `tests/obj/`.
- The app is a WPF app targeting `net8.0-windows`, `x64`, with SAP2000 API reference `CSiAPIv1.dll`.

## Build And Run

- Build the app with `dotnet build COTVCF.sln`.
- If the running desktop app locks `bin/Debug/net8.0-windows/win-x64/COT VCF tools.exe` or `.dll`, close it with `Get-Process -Name 'COT VCF tools' -ErrorAction SilentlyContinue | Stop-Process -Force`, then rebuild.
- For compile-only checks while the app is open, use `dotnet build COTVCF.csproj -o .work\build-check`.
- Launch the app from `bin/Debug/net8.0-windows/win-x64/COT VCF tools.exe`.

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

## Navigation Style

- Left-side workflow tabs are a compact rail: fixed 152x42 items, `Surface` fill, a 3 px `AccentBright`
  stripe on the left edge of the selected tab, `Raised` fill when selected. No pill blocks, no angled
  decorations, no red mark.
- Tab headers show `NN  NAME` (12 px semibold, `Text`) over a muted 10 px status line.
- Avoid setting `Foreground` on the whole `TabItem`; it leaks into page content. Set tab header text
  colors directly in `MainWindow.xaml`.
- DataGrid selection uses a pale blue background (`Selected`) with dark text and a `SelectedBorder`
  cell border; avoid the saturated Windows default selection blue.

## Top Bar Connection Flow

- The top bar is compact (`Padding` 10,6): logo + title on the left; on the right a status dot
  (green when connected), a monospace `StatusLine` (model / units / lock, or `SAP2000 - <state>`),
  the `Connect SAP` primary button, an icon-only refresh button (tooltip `Refresh connection and
  geometry`), and `Help`.
- `Connect SAP` connects and immediately loads geometry. Refresh reconnects and updates
  geometry / load patterns / force sources for the current session.
- `MainViewModel.StatusLine` feeds the top-bar status text; keep it in the `Update()` notify list.
- Do not move connection controls into the viewer.

## Viewer Behavior

- `Structural2DViewer` renders on a white viewport (`ViewportBg`) with a faint `ViewportGrid`.
  Geometry is dark: frames `ViewportGeom`, cables/tendons `AccentBright` (dashed / dotted),
  joints hollow with a white ring.
- Viewer controls are a small light floating panel at the top-right of the canvas: projection combo,
  Fit, Reset view (returns to XZ), and three label toggles `P` / `F` / `C` bound to `ShowPointLabels` /
  `ShowFrameLabels` / `ShowCableLabels`. Frame and cable/tendon labels are drawn along the member,
  rotated to the element's screen-space direction and kept upright (`ModelCanvas.MemberLabel`); configured
  cables keep their own horizontal `CableLabel` from the overlay and are skipped by the `C` pass.
  `ToolbarContent` still renders (top-left overlay) for contextual controls.
- Axis overlay sits on a white translucent chip: X red, Y blue, Z green.
- Still supports wheel zoom, drag pan, double-click fit, `Home` to fit.

## Engineering Inputs

- Standalone `EngineeringInput` defaults to 220 px wide with an 11 px muted label above a 24 px
  Consolas editor and a compact unit suffix. Pass an explicit `Width` for inline/compact placements
  (e.g. the B / NStories row) and `Label=""` to hide the label; give those an `AutomationProperties.Name`.
- Do not apply the 220 px width to DataGrid cell editors.

## Workflow tabs & shared model preview

- There is **no** standalone "SAP Model" tab. The left rail is **01 Loading, 02 Truss Height, 03 Pretension,
  04 Output Settings, 05 Iteration, 06 Post-Processing, 07 FEM** (`SelectedIndex` 0-6). Connecting is the
  top-bar `Connect SAP` button only. Project JSON continues to store only the existing Tabs 01-05 inputs; DTO
  property names (`Tab02Loading` … `Tab05IterationStudy`) and JSON keys are unchanged legacy v1 identifiers.
- There is **one** `Structural2DViewer` (`x:Name="SharedViewer"`), hoisted out of the `TabControl` into a
  three-column grid in `MainWindow.xaml`: `ParamsColumn` (the `TabControl`, params only) | `ViewerSplitterColumn` |
  `ViewerColumn` (the `SharedViewerPane` `DockPanel`). Switching tabs never rebuilds the viewer.
- `MainWindow.xaml.cs` `ApplyViewerLayout()` (called from `Loaded` and `WorkflowTabs_SelectionChanged`): tabs
  01-04 (index 0-3) and 07 FEM (index 6) get the resizable ~40/60 params/preview split; tabs 05 Iteration and
  06 Post-Processing (indices 4-5) hide the viewer + splitter and the `TabControl` takes the full width. The user's dragged split is remembered in
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

- Runs every Cartesian Tab 01 × Tab 02 × Tab 03 candidate combination in the internal solver; it does not use
  Tab 05 study mode/filters and never writes to SAP2000.
- SAP properties are read on the existing STA gateway lane in kN/m units: assigned frame/cable sections,
  effective section material, elastic modulus (isotropic getter except SAP `Tendon` materials, which use the
  uniaxial getter), frame area/in-plane inertia modifiers, and frame end rotation releases. A CSI `None`
  material-overwrite sentinel means "use the section material"; it is never passed as a material name.
- `Read Properties` populates the read-only member-property grid before analysis; it does not write SAP data.
- FEM has nested `Analysis` / `Results` subtabs. Results selects one completed `Lxx_Hxx_Pxx` candidate and offers
  Axial Force, Bending Moment, Shear, Reactions, and Deflected Shape; the shared viewer draws filled blue-positive /
  red-negative member diagrams, green support vectors, or the visually magnified deformed shape. The adjacent
  grid lists values and units; diagram widths are auto-scaled and are not numeric scale. These are internal FEM
  combinations built from Tab 01 loading rows, not SAP2000 saved analysis cases or combinations.
- The internal model is linear-elastic 2D XZ Euler-Bernoulli frames plus axial tension-only cables with Tab 03
  target forces fitted in a pretension-only stage (FEM Analysis: ±% tolerance, default 10, and max iterations,
  default 100; all cables adjusted together) before the Tab 01 loads act on the prestressed state. FEM page tabs
  are Inputs (pin supports, Read Properties, SAP member properties) | Analysis (fitting ±%, max iterations, Frame
  mesh segments per frame (default 10). Like SAP, each frame is first split at every member-end joint lying on it
  within 1 mm (FrameJoints.Pieces — e.g. full-length chords crossed by verticals and cable anchors), then each piece
  is meshed; cables are never split or meshed. Interior mesh nodes are statically condensed onto the joints and
  recovered as stations for diagrams/deflected shape; the viewer splits frames the same way), run controls, cable table:
  Target / Pretension / Dev. % / Status / Result / Applied P / Iter.) | View (ViewToggle buttons: Frame mesh,
  Member size, Loading — viewer overlays only while the FEM tab is open) | Results (Axial force view draws frame
  axial diagrams and cable tensions together; "Axial values (kN)" checkbox labels each frame piece and each cable
  ("T n kN", drawn on top) in whole kN; Reactions view draws Rx and Rz as separate arrows with separate whole-kN
  labels — never a combined resultant). Frame moments use one sign
  convention at both ends (I-end = -f2), so diagrams are continuous across joints. A case whose targets cannot be met
  is not an error: it keeps the closest pretension, is marked "Not fitted" (reason in the Status tooltip), and the
  run continues. A case whose equilibrium solve fails gets one "Failed" row with the reason; the run continues. Pins are auto-detected at the lowest joints connected to near-vertical frame
  members, drawn at the base joints in the shared viewer; they restrain X/Z translations and leave rotation free.
- Tension-only cable equilibrium uses damped Newton iterations with a potential-energy line search, not a Boolean
  active-set toggle. Failure reports include the final free-DOF residual and active cable count.
- Unsupported inputs fail explicitly: non-planar geometry, tendon objects, advanced local axes, axial or
  in-plane shear releases, and rotated local axes combined with in-plane releases. Nonlinear cable sag, geometric
  nonlinearity, and SAP analysis settings are not modeled.
- Results are streamed per candidate so cancellation or a later failure retains completed rows. Candidate count
  is capped at the existing 50,000-row preview limit.
- The global `Load Config` picker starts in the repository `tests` folder when that folder is available.
- The connection gateway attaches to SAP2000 only. An ETABS-originated model must be open/exported into SAP2000;
  direct ETABS process attachment is not part of this FEM workflow.

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
  Min ≤ peak downward deflection δ = −min(Uz) ≤ Max. The Tab 06 Shortlist gates on it (outside = Fail) and
  `Deflection_Report.html` draws it as a green wash between two dashed limit lines with a per-chart
  `✓ n / N in limit band` count and ✓/✗ per candidate. It is a post-processing input: excluded from the run
  fingerprint, applied on the next Generate/Regenerate. The former hidden study Pass/Fail limits (50/75 mm) are gone.
- Member-force groups are user-defined application groups over baseline Frame Objects; one frame belongs to at most one member-force output group. A shared result case/combo is required. Extraction retains signed local P/V2/V3 and T/M2/M3 for every SAP analysis element and result station returned for each selected frame object; it must not collapse an auto-meshed frame object to one governing value.
- Foundations require exactly two baseline points on the Left and exactly two on the Right, with no overlap.
- Cable output selects baseline Cable Objects plus a SAP analysis load case. This is distinct from Tab 03's target-force load pattern.
- Configuration is persisted as `Tab05OutputSettings`; opening a project restores definitions only and never mutates SAP.
- When Tab 05 Run starts, `OutputGroupPreparationService` is the preflight gate: verify the active model is the immutable session parent and unlocked, replace/read-back all requested `[O]-` groups, save the parent .sdb, then freeze the execution snapshot. No worker or child may start before this succeeds.
- Member-force extraction retains every SAP station and result step; support reactions retain the configured Left/Right foundation points and global force/moment components. Tab 06 generates their reports from the raw JSON package.
- `Support_Reactions_Report.html` draws, per support point (or foundation total), a range band among the in-scope
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
  the candidate diagnostics, consolidated workbook, deflection/cable/member-force/support-reaction HTML, and shortlist outputs without a SAP
  gateway. The CSV is a spreadsheet-friendly raw view; JSON is the canonical complete post-processing input.
- Capacity and decision criteria are post-processing inputs. Changing `fpu`, `φ`, or criteria must not invalidate
  reusable analyzed children; the next explicit Tab 06 generation uses the current derived capacities and criteria.
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
- Printing: the report uses the shared print engine below (toolbar `Print report ▾`, Module 07 `Export PDF`). On paper it
  re-lays its charts out against the sheet, keeps one pretension candidate (Side A + Side B) together per page where
  it fits, prints a `Printed view:` line (value mode · load · grouping) and leaves the Source Data table out unless
  asked for.

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
  `OpenBottomChordReport` / `OpenMegaColumnReport`), and their `Export PDF` print that Summary to
  `Top_Chord_Report.pdf` / `Bottom_Chord_Report.pdf` / `Mega_Column_Report.pdf`; without the group they fall
  back to the whole member-force report. Module 04 (Vierendeel) is unchanged.
- Checks: `tests/MemberForceReportChecks.cs` covers the derivation in C# and runs the report's own script (Jint,
  `tests/ReportPage.cs`) through `window.memberReport.summary(groupIndex, 'summary' | 'station', pretensionId)`.

## Report Print Engine (`ReportPrintEngine`, Tab 06 Print Setup, Export PDF)

Reports stay one self-contained HTML file; paper output is produced by the report itself with CSS Paged Media, so
the browser's Print and the app's PDF export give the same pages. **Every HTML view is on the engine**: Deflection,
Member Forces, Support Reactions, Cable Forces, the Study Shortlist and the per-candidate diagnostics. A new report
must plug in the same way — do not give a report its own print header / footer.

- **Print Setup** (`ReportPrintSettings`, Tab 06 card `PRINT SETUP · HEADER / FOOTER`, above module 01): company,
  logo file, project, job no., revision, prepared by, checked by, footer note, default paper (A4 / A3 / Letter),
  orientation, page numbers, date, include Source Data. It is a **per-user preference**
  (`ReportPrintSettingsStore` → `%LocalAppData%\COTVCF\ReportPrintSettings.json`), saved on every edit — not part of
  the COT VCF project file, the study folder or the run fingerprint. Like capacities and criteria it is a
  post-processing input: applied on the next Generate / Regenerate (`StudyPostProcessor.Generate(…, printSettings)`).
  `MainViewModel` takes the store as an optional constructor argument; without one (checks) settings stay in memory.
- **Logo**: `ReportPrintEngine.LogoDataUri` embeds the file as a data URI (PNG / JPG / SVG / GIF / WEBP / BMP, at
  most `MaxLogoBytes`), so the report needs no external resource. A missing / unsupported / oversized logo is
  reported on the card (`DescribeLogo`) and the company name is printed top-left instead. SVG prints as vector.
  Candidate diagnostics are written once per candidate, so they are described with `embedLogo: false` and print
  the company name top-left — a study with hundreds of candidates must not carry hundreds of logo copies.
- **Page layout**: the engine script (`window.reportPrint`) builds one `@page` rule into `<style id="pr-page">`:
  sheet size and orientation, margins (24 mm top, 16 mm bottom, 38 px sides), and the header / footer in the
  **page-margin boxes** on every page — top-left logo (or company), top-centre project over report name · model,
  top-right job no. · revision and date, bottom-left company and signatories, bottom-centre note, bottom-right
  `Page N of M` (`counter(page)` / `counter(pages)`). Side boxes are whole pixels and the centre is a `calc()` of
  the rest so the three borders print as one unbroken rule. User text goes through a CSS-string escaper; a logo
  value that is not an embedded image is ignored. Needs Chromium 131+ (Edge / Chrome); the browser's own header and
  footer are suppressed automatically. Firefox prints the content without the margin header / footer.
- **Chart quality**: charts are inline SVG and are never rasterised — text stays text, bars and hatches stay
  vector, colours print exactly (`print-color-adjust: exact`). A report registers
  `window.reportPrint.register({ before(page), after() })` and re-lays its charts out for `page.contentPx` (the
  sheet's content width) instead of the window; if the sheet is narrower than a comfortable design width the SVG is
  laid out wider and scaled down by CSS (`max-width:100%`), never below 80 %.
- **Triggers**: the report's `Print report ▾` button — accent-outlined, placed straight after the main toolbar
  controls in every report, never behind hint or status text — opening the panel (paper, orientation, include
  Source Data, `Print / Save as PDF`),
  plain Ctrl+P (`beforeprint` / `afterprint`), and the URL fragment `#print[&paper=A3&orientation=portrait&source=1]`
  which lays the page out for paper on load. Report-specific options ride on the same fragment (Cable Forces:
  `mode=ut`, `load=L02`).
- **Export PDF** (`ReportPdfExporter`; an `Export PDF` button on every module 01–08, modules 02–05 sharing the
  member-force report): runs the installed Edge (else Chrome) headless with a private profile on
  `report.html#print` and writes `<report>.pdf` beside the report, then opens it. No SAP2000, no network. It prints
  the report as last generated — change Print Setup, Regenerate, then export.
- Engine pieces a report emits: `ReportPrintEngine.Css` (inside its `<style>`), `PageStyle` (in `<head>`),
  `ToolbarButton` and `Panel` inside its toolbar (or `Bar` when it has none), and `Scripts(dto)` before its own
  script. Optional hooks the engine acts on while laid out for paper (`body.pr-on`):
  - `data-print-controls` on the toolbar(s) + one `data-print-view` element → the engine prints a
    `Printed view: Group: … · Component: … · Loading: all` line from the hidden selects and filter buttons;
  - `data-print="source"` on an optional data table (hidden unless *Include Source Data* — `body.pr-nosrc`);
  - `data-print-fit` on a wide table's wrapper → scaled inside its own box to the sheet width (re-measured, floor
    35 %). Never let content overflow the sheet: Chromium then shrinks the **whole page**. Use `body.pr-on` rules
    (not only `@media print`) for anything that changes a table's width, so it is measured as it will print;
  - `class="print-only"` / `class="no-print"`;
  - `window.reportPrint.register({ before(page), after() })` for charts drawn against the window width.
  Report-specific `@media print` rules (what to hide, `break-inside: avoid`, legend placement) stay with the report.
- Per report: **Deflection** redraws each chart at the full sheet width (`preserveAspectRatio="none"`, so it must
  be redrawn, not stretched), moves the legend under the chart in columns, keeps the per-chart case / view / filter
  controls as a record, and prints the Source Data Matrix only when asked (`#print&group=N`). **Member Forces** and
  **Support Reactions** print the current toolbar view — one chart block per page-fit, matrix views paginate with
  repeating headings (`#print&group=…&component=…&view=…`, plus `sampling` / `band`). **Shortlist** and
  **diagnostics** are plain documents with the `Bar`.
- Checks: `tests/ReportPrintChecks.cs` (settings store, logo embedding, generated page rule and escaping, screen ⇄
  paper re-layout, `#print` options, every report carries the engine, exporter command line). The chart reports'
  own scripts need a real browser, so `--report-pdf` (opt-in) prints every report to a real PDF with the installed
  browser; `--study-ui` renders the Print Setup card and the eight Export PDF buttons with fakes.

## Config Project File (`src/Configuration/`)

The JSON is a **COT VCF project** (document-editor model), not an export/import blob. It stores Tabs 01–05,
including the Tab 05 mode/fixed candidate/selection exclusions. Never serialise ViewModels; project save/load
never mutates SAP.

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
- `src/Presentation/Controls/Structural2DViewer.xaml`: dark viewport shell + floating overlay controls.
- `src/Presentation/Controls/Structural2DViewer.xaml.cs`: canvas drawing (viewport brushes), fit/pan/zoom,
  reset-to-XZ, axis colors, point labels.
- `src/Presentation/Controls/EngineeringInput.xaml`: compact 24 px numeric editor with optional label.
