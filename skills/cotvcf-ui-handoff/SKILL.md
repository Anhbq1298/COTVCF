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
  Fit, Reset view (returns to XZ), and a Show-labels toggle bound to `ShowPointLabels`.
  `ToolbarContent` still renders (top-left overlay) for contextual controls.
- Axis overlay sits on a white translucent chip: X red, Y blue, Z green.
- Still supports wheel zoom, drag pan, double-click fit, `Home` to fit.

## Engineering Inputs

- Standalone `EngineeringInput` defaults to 220 px wide with an 11 px muted label above a 24 px
  Consolas editor and a compact unit suffix. Pass an explicit `Width` for inline/compact placements
  (e.g. the B / NStories row) and `Label=""` to hide the label; give those an `AutomationProperties.Name`.
- Do not apply the 220 px width to DataGrid cell editors.

## Shared Model Preview (hoisted viewer)

- There is **one** `Structural2DViewer` (`x:Name="SharedViewer"`), hoisted out of the `TabControl` into a
  three-column grid in `MainWindow.xaml`: `ParamsColumn` (the `TabControl`, params only) | `ViewerSplitterColumn` |
  `ViewerColumn` (the `SharedViewerPane` `DockPanel`). Switching tabs never rebuilds the viewer.
- `MainWindow.xaml.cs` `ApplyViewerLayout()` (called from `Loaded` and `WorkflowTabs_SelectionChanged`) sets the
  column widths per `SelectedIndex`: tabs 2-4 (index 1-3) get the resizable ~40/60 params/preview split; tab 1
  (index 0) is preview-dominant (300 px params strip, no splitter); tabs 5-6 (index 4-5) hide the viewer + splitter
  and the `TabControl` takes the full width. The user's dragged split is remembered in `paramsColumnWidth` /
  `viewerColumnWidth`.
- The viewer's `Model` and every overlay come from `MainViewModel` `SharedViewer*` computed properties gated on
  `selectedTab` (`SharedViewerModel`, `SharedViewerAppliedPoints`, `SharedViewerTopPoints/Middle/Bottom`,
  `SharedViewerCables`, `SharedViewerCaption`, `SharedViewerHint`, `ShowTrussLegend`). `NotifySharedViewer()` is
  called from the `SelectedTab` setter and from `Update()`, so both a tab switch and any state refresh re-raise them.
- Caption (Consolas, top of the pane) = `GeometrySummary` on tab 1, `HeightSummary` on tab 3, `CablePreviewSummary`
  on tab 4, empty on tab 2. Hint (muted, bottom) is a short per-tab sentence. The Top/Middle/Bottom legend chip
  only shows on tab 3 (`ShowTrussLegend`).
- Do **not** re-add a per-tab `Structural2DViewer` to tabs 1-4; the params panels are viewer-free.

## SAP Model Tab

- Left params column is a compact read-only model card (`CONNECTED MODEL` name/path/version/units/lock +
  `GEOMETRY` summary). The shared preview is the dominant pane on this tab.

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
- Member-force groups are user-defined application groups over baseline Frame Objects; one frame belongs to at most one member-force output group. Future components are P/V2/V3 and T/M2/M3.
- Foundations require exactly two baseline points on the Left and exactly two on the Right, with no overlap.
- Cable output selects baseline Cable Objects plus a SAP analysis load case. This is distinct from Tab 03's target-force load pattern.
- Configuration is persisted as `Tab05OutputSettings`; opening a project restores definitions only and never mutates SAP.
- When Tab 05 Run starts, `OutputGroupPreparationService` is the preflight gate: verify the active model is the immutable session parent and unlocked, replace/read-back all requested `[O]-` groups, save the parent .sdb, then freeze the execution snapshot. No worker or child may start before this succeeds.
- Children inherit the saved parent groups. Actual SAP result extraction remains a future phase.

## Iteration Study Tab (06)

Study composition plus isolated child-model building and analysis. Tab 05 does not extract results.

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
  `ModelInfo`, saves the configured child, runs every SAP2000 analysis case currently marked to run, saves
  the analyzed model/results, reopens and verifies the child, and returns to the baseline before another job.
- `StudyExecutionSnapshotFactory` freezes candidate assignments, geometry/groups, paths, selections, and
  worker count at run start. Tabs 02–04, study settings, run checkboxes, and project load/save actions are
  disabled while execution is active.
- The Tab 05 monitor shows each worker ID, gate state, candidate, discrete 1/10 stage, completed count, and
  overall Ready/Running/Queued/Failed/Skipped totals. Cancellation is observed only between SAP API calls;
  active workers attempt to return to baseline and queued jobs become Cancelled.
- A candidate-level failure, including a nonzero `Analyze.RunAnalysis` result, may continue on a recovered
  worker. Failure to restore the baseline faults that worker and removes it from scheduling; healthy workers
  continue. Result extraction is not part of this phase.

## Config Project File (`src/Configuration/`)

The JSON is a **COT VCF project** (document-editor model), not an export/import blob. It stores Tabs 02–05,
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
