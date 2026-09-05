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

## Navigation Style

- Left-side tabs should look like blue navigation blocks inspired by the user's reference image.
- Tab headers use blue backgrounds, white primary text, light-blue status text, and a small red accent on the selected tab.
- Avoid setting `Foreground` on the whole `TabItem`; it leaks into page content. Set tab header text colors directly in `MainWindow.xaml`.
- Keep each tab item fixed-height so all five tabs remain visible.
- DataGrid selection uses a pale blue background with dark text and a restrained blue-grey cell border; avoid the saturated Windows default selection blue.

## Top Bar Connection Flow

- `Connect` belongs in the top-right bar next to the connection status and `Help`.
- `Connect` should connect to SAP2000 and immediately load geometry.
- The refresh control is an icon button in the top bar with tooltip `Refresh connection and geometry`.
- Refresh should reconnect and update geometry/load patterns/force source data for the current session.
- Do not put `Connect to SAP2000` or `Get geometry / Refresh` back inside the SAP model preview toolbar.

## Viewer Behavior

- `Structural2DViewer` supports optional `ToolbarContent` for contextual controls.
- `Structural2DViewer` supports `ShowPointLabels`; when true, the canvas draws every joint label beside its point.
- The axis overlay is transparent and color-coded:
  - X axis: red
  - Y axis: blue
  - Z axis: green
- The viewer still supports projection plane selection, fit, reset view, mouse wheel zoom, drag pan, and double-click fit.

## Engineering Inputs

- Standalone `EngineeringInput` controls use a consistent 220 px width.
- Their numeric editor height is 27 px, which is 25% lower than the previous 36 px height. Do not apply the 220 px width to DataGrid cell editors.

## SAP Model Tab

- The SAP model page should prioritize the model preview.
- The old in-page title/description and large model-info panel were removed to give the viewer more height.
- The geometry summary stays below the preview.

## Loading Criteria Tab

- Tab 2 is `LOADING CRITERIA`; do not restore the former per-point Fx/Fy/Fz assignment grid or an Apply-to-SAP action here.
- Keep the 45/55 two-column layout: the existing 2D viewer is on the left, and the right side has separate `APPLIED POINTS` and `LOAD DEFINITION` cards.
- `+ Add Load Locations` opens the existing WPF `SelectionService` dialog using points from the loaded geometry. The dialog supports search, multi-select, select-all, and preselects existing load locations. Selected names are mapped to the cached Global X/Y/Z coordinates, deduplicated, sorted by X then point name, and assigned one-based order values; the user does not need to preselect objects in SAP2000.
- Applied points are preview-only overlays on the existing viewer. Draw a fixed screen-size downward load arrow, normal applied-point highlight, stronger current-point highlight, and labels without modifying SAP geometry.
- Grid and viewer current selection are synchronized in both directions. Removing grid rows removes their overlays and arrows, re-sorts, and does not write to SAP2000.
- `LoadDefinition` is generic and defaults to `DL_Above` and `LL_Above`. Each row independently switches between single-qz mode and Min/Max/Step mode through `ApplyIncrementStep`.
- Disabled numeric cells must remain grey, unfocusable, excluded from validation, and derived from `ApplyIncrementStep` rather than separate UI state.
- Candidate values are internal. Single mode returns only qz; range mode returns `Min + n * Step` while the value is at most Max and does not append an off-grid Max.
- Keep `Out-of-plane tributary width, B [m]` above the load grid as a shared positive numeric input.
- Keep the tributary-width field compact (about 220 px) and the secondary `Remove selected` action visually smaller than the primary location picker.
- `+ Add Load` creates an empty-name, non-increment row with qz `0.00` and focuses its Load Name cell. Each row has a compact delete icon.
- Validation is inline: qz must be nonnegative; Min must be nonnegative; Max must be at least Min; Step must be positive. No normal cell-validation dialog belongs in Tab 2.
- Load-grid numeric bindings use `NullableDoubleConverter`: clearing an active numeric cell stores `null` and leaves it visually blank instead of restoring `0.00`; validation may still mark the required active cell invalid.
- Keep star-sized columns with practical `MinWidth` values and horizontal scrolling as a narrow-window fallback.

## Truss Height Tab

- Layout should match the point-loading setout:
  - Left: preview.
  - Middle: `GridSplitter`.
  - Right: point groups and height range configuration.
- Page content should use normal dark/blue text on white backgrounds. If content appears white/invisible, check for inherited `Foreground` from tab styling.

## Cable Force Tab

- In-page title/description was removed.
- Cable force `DataGrid` also uses star sizing plus `MinWidth` to reduce clipped columns.
- Keep horizontal scrolling enabled.

## Iteration Tab

- In-page title/description was removed.
- The tab opens directly into the iteration summary/issues layout with the validation controls below.

## Important Files

- `src/Presentation/MainWindow.xaml`: main window shell, top bar, tabs, page layouts.
- `src/Presentation/Theme.xaml`: shared styles, including buttons, grids, and left tab template.
- `src/Presentation/MainViewModel.cs`: command wiring for connect, refresh, add/remove/apply actions.
- `src/Presentation/Controls/Structural2DViewer.xaml`: viewer shell and toolbar area.
- `src/Presentation/Controls/Structural2DViewer.xaml.cs`: canvas drawing, fit/pan/zoom, axis colors, point labels.
