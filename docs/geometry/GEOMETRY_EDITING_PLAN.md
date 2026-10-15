# Native Geometry Editing — Implementation Plan

Status (2026-10-15): **implemented in the main window** for the first usable-canvas milestone — see "Implementation status" below.
The plan was first written for the native workspace (`NativeModelWindow`, branch `feature/geometry-editing-plan`); that window has
been retired into the main window (M8), so the work lives there. Unchecked items below are still open; an item is checked only
with its code and a check.
Scope: native Mcotis model, reusable across workspaces. Preserve current solver, persistence, import/export and architecture.

## Repository integration points (inspected on 2026-10-15)
- `src/Mcotis.Application/DocumentCommands.cs`: `AddPoint`, `AddFrame`, `MovePoints`, `Remove`, `Duplicate`, `Edit`; all are already undoable via `DocumentSession`.
- `src/Mcotis.Desktop/Presentation/MainWindow.xaml(.cs)` and `MainViewModel` (since M8 the one window; `NativeModelWindow` / `NativeModelViewModel` are archived in `archive/Mcotis.Desktop/NativeWorkspace/`).
- `src/Mcotis.Desktop/Presentation/NativeDocumentView.cs`: maps the document into the `GeometryModel` the viewer draws.
- `src/Mcotis.Desktop/Presentation/Controls/Structural2DViewer.xaml.cs`: joint/member selection and 2D/3D display. Must not mutate model directly.
- `src/Mcotis.Core`: point/frame object IDs and model editor; `src/Mcotis.Application`: commands and session.
- Read `AGENTS.md`, `skills/mcotis-ui-handoff/SKILL.md`, `docs/mcotis-standalone-plan.md` before implementation.


## Implementation status (2026-10-15)

| Part | Where | Code | Checks |
|---|---|---|---|
| Grid systems and levels as document data (schema 7, migration 6 → 7, no analysis input, one Undo step) | Define → Grid Systems… / Levels… | `Mcotis.Core/ReferenceGeometry.cs`, `DocumentEditor.SetReferenceGeometry`, `Mcotis.Persistence` | `Mcotis.Checks` group "Geometry editing" |
| Draw / move / delete planning: point reuse within 1 mm, no zero-length, no doubled or overlapping member, each point moved once, delete cascade | — (library) | `Mcotis.Application/GeometryEditService.cs` | same group |
| One view context: plane + coordinate shared by the projection box, toolbar, Display Options and the PLANE bar; slices show only members lying in the plane; held view frame while editing | PLANE bar above the model view | `Presentation/GeometryEditing/ViewPlanes.cs`, `MainViewModel.GeometryEditing.cs`, `ModelCanvas.Editing.cs` | `Mcotis.Desktop.Checks -- --geometry-editing` |
| Grid lines, bubbles, level lines and tags drawn under the model; grid at the base and the shaded work plane in 3D | model view | `GeometryEditing/ReferenceLines.cs`, `ModelCanvas.Editing.cs`, `Structural2DViewer.Editing.cs` | same flag (pure layout + snapshot `.work/geometry-editing-ui.png`) |
| Point, Frame, Cable and Move tools with snapping (point, midpoint, member intersection, grid intersection, grid line, perpendicular, nearest, ortho; Alt off, Shift ortho), chain drawing, Esc / right-click; 3D on the work plane by a ray, never a guessed depth | Draw menu, tool strip, Edit → Move by Two Points | `GeometryEditing/Snapping.cs`, `ModelCanvas.Editing.cs`, `Structural2DViewer.Editing.cs`, `MainWindow.GeometryEditing.cs` | same flag |
| Forms: Point by Coordinates (absolute / relative / on plane / pasted table), Frame / Cable by Coordinates, Move (DX DY DZ), Generate Points at Grid Intersections | Draw, Edit | `GeometryForms.cs`, `ReferenceGeometryWindow.cs` | view model paths in the same flag |
| Delete: confirmation lists what goes with the selection; one named Undo step | Edit → Delete, Del, tool strip | `MainViewModel.Edit.cs`, `DeleteConsequences` | same flag |
| Display Options (Ctrl+W): points, frames, cables, grid lines, levels, labels — hidden kinds are neither drawn nor picked | Display → View Options… | `ViewOptionsWindow` | same flag |

Known limits of this milestone: edits of the main window go through the legacy configuration and become one document step
each (`SyncNativeDocument`), so automatic names (P, F, C) follow the names in the model, not the native sequence, and a name
freed by a delete can be issued again; the working-session autosave (`.cotfem.db`) does not hold grids and levels, so crash
recovery restores the model without them (the `.mcotis` file holds them); the Point form has no preview crosshair; snap
settings and display options last for the session; drawing on a blank model (no member yet) is not offered; end releases and
local axes are assigned after drawing (Assign menu), not in the draw tool; performance is not measured yet.

## Goals
1. Interactive Draw Frame: two clicks (start/end), consecutive chain mode, preview line, precise coordinate input, Escape cancel, Right-click finish, keyboard Enter accept coordinates. Auto-create joint only when no existing joint within **model-unit** geometric tolerance. Never create zero-length frames.
2. Move: selected points or frames by drag or explicit DX/DY/DZ. Selecting a frame moves its end joints; explain/shared-endpoint semantics before commit and show live preview. A separate 'Disconnect and Move' is out of scope.
3. Delete: selected objects, with explicit preview/confirmation for dependent objects. Native `DocumentEditor.Remove` governs cascading semantics; document and test current behaviour. No dangling loads/assignments/member references.
4. Snap: independently toggle Point, End, Midpoint, Intersection, Grid, Perpendicular, Nearest, Ortho, and optional user coordinate entry. Default End+Point+Grid. Show snap glyph/tooltip and prioritize point/end over intersection, midpoint, grid, nearest. Alt temporarily suppresses snapping; Shift enables Ortho when drawing.
5. Display Options window **Ctrl+W**: point names, frame names, cable names, joint symbols, local axes, sections/extrusions, supports, grid, dimensions, loads, undeformed/deformed overlays; show/hide per category. Default settings do not change FEM data. Provide Restore Defaults; checkbox choices respond immediately and cancel restores previous settings.
6. Undo/Redo: one gesture = one atomic `DocumentSession.Execute(DocumentCommands.Edit(...))`. No history entry for canceled/no-op operations. Mark results stale through the existing revision tracking.
7. Native 3D support: drawing happens on explicit work plane (XY/XZ/YZ at a selected elevation/coordinate, or custom plane later); screen-to-plane ray intersection. In perspective 3D, never guess depth. Indicate active work plane and use it for all mouse-to-world conversion.
8. Reuse across any workspace exposing a native `DocumentSession`; no CSI/SAP dependency and no UI-to-solver reference.

## Suggested module layout
- `src/Mcotis.Desktop/Presentation/GeometryEditing/GeometryTool.cs`: Select, DrawFrame, Move, Delete enums/state.
- `.../GeometryEditing/GeometryInteractionController.cs`: mouse + keyboard state machine, selection, previews, cancel/commit.
- `.../GeometryEditing/ViewportCoordinateMapper.cs`: 2D transform and 3D ray/work-plane intersection.
- `.../GeometryEditing/SnapOptions.cs`, `SnapResolver.cs`, `SnapCandidate.cs`: pure snapping algorithm; spatial index/candidate filtering.
- `.../GeometryEditing/DisplayOptions.cs` and `DisplayOptionsWindow.xaml(.cs)`: presentation-only view settings.
- `src/Mcotis.Application/GeometryEditService.cs`: adapter turning resolved points and selected object IDs into existing document commands; no WPF or viewer dependencies.
- Extend `Structural2DViewer` with tool-neutral hit test / ray API, preview visuals, work plane feedback; keep viewer display-only.
- Add menu items and input bindings to `NativeModelWindow`; share command layer with main-window native workflow as it is migrated.

## Interaction details
**Draw Frame**
- Start draw tool by toolbar/menu; first click resolves world point. Prefer an existing joint ID within tolerance; otherwise stage a new joint (not yet committed).
- Mouse move draws dashed preview and displays length + XYZ.
- Second click resolves endpoint. Commit using one `DocumentCommands.Edit("Draw Frame", editor => { add staged joints; add frame; })` transaction. Preflight identical start/end and duplicate-member policies; generate stable unique names and IDs.
- Continue chain using new endpoint as next start when Chain is enabled. Escape discards current preview; final right-click exits drawing.
- Coordinate text boxes allow explicit XYZ and optional length/angle on the active plane; no pixel-to-model tolerance shortcut.

**Move**
- Convert selected frame IDs to their endpoint point IDs, deduplicate, and include explicitly selected points.
- Preview transformed geometry, connected frame changes, and displacement in current units. By default shared point connectivity is preserved (adjacent frames deform accordingly).
- Commit `MovePoints` once. Block invalid zero-length / coincident-member outcomes before commit.

**Delete**
- Delete selection with Delete key (when canvas has focus and no text editor captures key).
- Review and test cascade policy for frame endpoints, connected members, restraints, point loads and frame distributed loads. Deleting only a frame must not delete shared joints unless explicitly requested.
- Selection cleared and dependent UI refreshed after successful transaction.

**Snapping**
- Work in model space and evaluate candidates within a screen-radius threshold (default ~10 px after projection); snap model-point deduplication uses a separate, unit-aware tolerance.
- Distinguish geometric intersection from physically connected joint: intersection-only creates a joint/splits members only through an explicit user action, not silently.
- Nearest/perpendicular refer to points on members; create connection/splitting only if opted-in, otherwise avoid false FE connectivity.
- Report snap type, coordinates and target name in small floating label.

**Display / Ctrl+W**
- Introduce a single `DisplayOptionsState` scoped to a window/view, distinct from `ModelDocument` and FEM analysis settings.
- Menu `Display > Show Display Options… (Ctrl+W)`; register a WPF command at window level but suppress while text controls need Ctrl+W, if appropriate. Ensure only one dialog opens.
- Bind `Structural2DViewer` visibility toggles via dependency properties; preserve existing `ShowPointLabels`, `ShowFrameLabels`, `ShowCableLabels`, `ShowDimensions` and local-axis display when present.
- Hiding objects never deletes or suppresses them in FEM; picking should respect visibility; initial view settings need not be persisted to the `.mcotis` model file.

## Build sequence
1. Document existing `DocumentEditor.Remove` cascade and viewer mouse-event ownership; identify conflicts with pan/select/result modes.
2. Pure snapping coordinate math and tests first; add work-plane mapper for orthographic 2D and 3D camera ray.
3. Add viewport overlay API and state-machine coordinator; maintain select/pan interactions.
4. Add Draw Frame; document-session transaction; unique names; coordinate entry; chain mode.
5. Add Move and Delete with dependency preview and undo/redo.
6. Add Snap Options flyout and feedback glyphs.
7. Add Display Options dialog, bindings and Ctrl+W in native window; extend shared/main window when the same native session integration is available.
8. Run targeted checks + regression suites and manual WPF interactions. Keep unrelated MB/archive code untouched.

## Targeted acceptance tests
- Draw on XY/XZ/YZ at nonzero work-plane offsets; viewport zoom and pan do not affect world coordinates.
- Draw between two existing joints reuses IDs; draw to empty space creates exactly one new joint; zero-length reject.
- Two sequential draw segments: shared intermediate point has single ID and proper FEM connectivity.
- Duplicate edge policy deterministic; no silent overlapping frame.
- Move frames sharing a joint: each point translated only once; connectivity retained; undo restores all coordinates.
- Delete a frame preserves shared joints and other connected frames; no dangling load/assignment IDs; undo restores.
- Snap ranking, threshold at multiple zoom levels, ortho and Alt override; intersection is not silently a new connection.
- Ctrl+W opens display dialog exactly once, displays current options, does not modify analysis input/revision; hidden member cannot accidentally be selected.
- Undo/Redo, Save/Open `.mcotis`, run model, imported models and existing mouse pan/selection remain functional.

## Non-goals
- Drawing shells, cables and solids (reuse tool architecture later).
- New FE solver, geometric nonlinearity, or any CSI dependency.
- Automatic merging, member segmentation, topology surgery at non-joint crossings without user consent.
- Main model database replacement or persisted view settings in first slice.


## UI/UX expansion backlog — actionable TODO (2026-10-15)

**Goal:** make the WPF model viewport a responsive, reliable structural modeller, with SAP2000/ETABS-style grid/story definitions, point and frame drafting, and seamless XY/XZ/YZ/3D navigation. **This section is a development TODO, not a claim that the features are implemented.** Build in small reviewed slices, prioritizing a functional canvas over cosmetic rewrites.

### P0 — integrity and foundations (must precede editing)
- [x] Inspect existing `ModelCanvas` and `Structural2DViewer` event routing, projection transforms, 3D camera and pan/selection logic; map mouse capture and key conflicts.
- [x] Make one canonical `ActiveViewContext` (orientation XY/XZ/YZ/3D, fixed world coordinate, named plane/grid, work plane, camera state). The native toolbar and viewer's current projection combo **must synchronize bidirectionally**, avoiding display-mode mismatch. View selection must not alter analysis DOFs.
- [x] Establish clear distinction: model reference grid != FE joints; storey elevation != physical point; hidden object != deleted object; projected member != physically on plane.
- [x] Complete `DocumentSession` command/state integration; editing blocked during analysis, model revisions invalidate results correctly, view-only actions leave analysis hash and results untouched.
- [x] Introduce a unit-aware geometry tolerance plus independent pixel hit/snap radius; forbid silent topology changes and zero-length/duplicate frames.
- [x] Keep Core/Application/Persistence independent of WPF, old MB/archive and SAP/ETABS; main-window projection must not become a second source of truth.
- [x] Confirm existing delete-cascade behaviour for loads, point assignments, restraints and shared endpoints before exposing Delete.

### P1 — grids, levels and WPF definition forms
- [x] (one Grids and Levels window, tabs Grid Systems / Levels; reorder = Sort by coordinate) **Define > Grid Systems...** compact WPF dialog, tabs `Cartesian` / `Display`, one or more named grid systems, origin X/Y/Z and orientation angle, editable X and Y gridline tables with unique labels, cumulative coordinate, incremental spacing and visibility; add/insert/delete/rename/reorder lines. Start with orthogonal Cartesian grids; mark radial/curvilinear grids as future.
- [x] **Define > Levels...** compact WPF form: columns `Level Name | Elevation Z | Height to Next | Master/Similar (future) | Visible`, editable levels, Base/L1/L2/Roof aliases, sort by Z and validate uniqueness and finite coordinates.
- [x] (prefixes in the library only, not in the form) **Quick Grid Generator** from count + spacing: `Number X / Spacing X`, `Number Y / Spacing Y`, `Number Z / Spacing Z` (or individual unequal spacings); origin X/Y/Z, optional prefixes, Preview before Apply. Numeric spacings are model coordinates, not physical generated FEM points unless user explicitly clicks `Generate Joints`.
- [x] Grid preview displays number and spacing changes before committing; Apply and OK create **one undo transaction**, Cancel discards all staged changes. Preserve user edits if validation fails.
- [x] (suggestions: Suggest from Model in the form; the navigator lists point coordinates) Persist named grid systems and levels in `.mcotis` through versioned document schema/migrations; legacy models without definitions open with generated reference suggestions, not fake persisted grids.
- [x] Render thin/dashed grid lines, consistent X/Y bubbles, elevation tags, coordinates/units and optional shaded active work plane in XY/XZ/YZ/3D; grid extents not infinite or expensive.
- [x] (the 3D work plane shading has no switch of its own yet) Show/hide grid lines, labels, level reference lines and work plane separately; viewport snaps only to **visible eligible grid geometry** as controlled by snap settings, without producing FE nodes.
- [x] Active plane combo examples: `XY Plan — L2, Z=+8.000 m`; `XZ Elevation — Grid B, Y=+6.000 m`; `YZ Elevation — Grid 3, X=+12.000 m`; allow arbitrary coordinate even without a named level.
- [x] (Ctrl+Page Up / Down; no mouse-wheel modifier) Previous/next grid or level buttons with mouse wheel modifier/keyboard shortcuts; active plane highlighted, model view refitted only when explicitly requested (preserve zoom otherwise).

### P2 — Add Point by coordinates (dedicated WPF form)
- [x] **Draw > Point Object...** / left tool strip `Point`: WPF dialog with `Name (auto/manual) | X | Y | Z` fields, displayed model length unit, signed decimals and copy-from-selected-joint control.
- [x] Modes `Absolute XYZ`, `Relative DX/DY/DZ` from named joint or last point, `On Active Plane` (fixed coordinate read-only by default, explicit override).
- [ ] Numeric XYZ input preview crosshair immediately; `Add Point` commits one `DocumentCommands.AddPoint`, `Apply / Add Next` keeps dialog active, `Cancel` discards preview; Enter commit, Esc cancel.
- [x] (the table refuses a bad row by its line; positions with a point are left out, counted after Apply; grid points by levels, not by chosen axes) Batch point creation tab/table: paste rows from Excel, validate row-by-row, show duplicates/invalid numbers before atomic Apply; optional `Generate Points from Grid Intersections` with selected axes and levels plus preflight count.
- [x] (the proposed name is not shown in the tooltip) On-canvas click-to-place points uses same codepath; cursor tooltip shows global (X,Y,Z), snap kind and proposed point name; point selection highlights in views.
- [x] (refused, naming the point; Draw Frame reuses it) Existing point within model tolerance: offer `Reuse Existing` or reject duplicate; do not silently create coincident disconnected nodes. Unique immutable IDs and editable distinct Names.

### P3 — frame drafting, editing and deleting
- [x] (releases and local axes: Assign after drawing) **Draw > Frame Object** with `Draw Single` and `Draw Continuous`: choose active plane, optional default frame section, end releases and local axis options; click I/J joints or coordinates; rubber-band preview length + vector and snap symbols.
- [x] (automatic member name) WPF `Add Frame by Coordinates` dialog with `Frame Name | XI,YI,ZI | XJ,YJ,ZJ | Section`, endpoint pick buttons, `Reuse/Create End Joints` choice, validation before commit.
- [x] Point-to-point mouse clicks auto-create only missing points; use one `DocumentCommands.Edit` transaction for each frame and staged endpoints; chain drawing has deterministic Esc/Right-click finish.
- [ ] Selection: point/frame single click, Ctrl additive toggle, Shift window/crossing rectangle, status count; distinguish select from pan, respect invisible objects.
- [x] (Move by Two Points with a ghost of the selection; no drag) **Edit > Move** form: `DX DY DZ` relative offset or `From/To` points; interactive drag preview; frame movement moves its endpoint joints once, preserving shared connectivity unless an explicit disconnection operation is introduced later.
- [x] **Edit > Delete Selected** and Delete key: show affected connected members/loads/assignments before destructive cascade; remove frame alone without deleting shared joints; delete joint with dependent objects only after explicit confirmation.
- [x] (export not run against a live SAP2000 / ETABS) Extend one-step undo/redo to each Add/Move/Delete operation; no undo record for previews, canceled actions or no-ops; confirm save/reopen and export use edited native document.

### P4 — snapping and interaction comfort
- [ ] Compact `Snap Options` popover: Endpoint/Point, Midpoint, Grid Intersection, Member Intersection, Perpendicular, Nearest, Ortho; checkboxes independently persisted as user settings rather than structural model data.
- [x] Candidate resolver projects into screen space for a fixed pixel radius and ranks exact physical point > endpoint > intersection > midpoint > grid > nearest; display snap glyph, target label and world coordinates.
- [ ] Intersection without FE joint is visual only until the user explicitly chooses split/connect; adding point on frame offers an explicit `Split Frame` action later, never implicitly changes member connectivity.
- [ ] Escape cancels only active interaction; middle-button / Space+drag pans; wheel zooms centered under cursor; double click Fit if not drawing; prevent stuck captured mouse on losing focus.
- [x] (no measurements yet) Camera position and zoom maintained when switching nearby levels and orientations where practical; aim for redraw without flicker, fast hit testing and no full scene rebuild per mouse move.
- [ ] Performance goals for representative models: input response <50 ms, viewport interactions target 60 FPS, avoid allocations/rebuilding static mesh on mouse move; measure and document hardware/model sizes rather than promising unconditional performance.

### P5 — display settings and usability
- [x] (groups Objects and Grids & Levels in the existing Object / General Options tabs) **Display > Show Display Options...** via `Ctrl+W`: compact WPF dialog with categories `Objects`, `Labels`, `Grids & Levels`, `Results`; switches for joints, frames, cables, local axes, section extrusions, supports, loads, dimensions, gridlines, reference levels and undeformed/deformed shape.
- [x] (staged Apply / OK / Close as the form always was; no live preview) Split presentation `DisplayOptionsState` from model persistent grid definitions and analysis options; Apply live preview, Cancel restores prior values, Restore Defaults works, hidden objects are not selectable by accident.
- [x] (Snap in the Draw menu; units stay at the far right) Floating left vertical CAD-like toolbar: Select / Point / Frame / Move / Delete / Snap; upper-right View selector; bottom status bar `Plane | X Y Z | Units | Snap | Selected | Mode`; use existing theme, fonts and resource dictionaries.
- [x] Show a brief contextual instruction when a tool is activated (`Select first frame end`, `Select destination`, etc.), unobtrusive errors beside invalid fields, and escape/help affordances; no modal popup for ordinary operations.
- [ ] Standardize usable tooltips, keyboard focus, tab order, high DPI and resizing; prevent shortcuts firing inside numeric TextBoxes and when analysis locks the document.

### Implementation boundaries / dependency sequence
- [x] Slice 01: view context synchronization + stable work plane + smoke checks.
- [x] Slice 02: grid/storey domain models, persistence/migrations, reference-grid rendering.
- [x] Slice 03: WPF Grid/Level/XYZ Spacing forms with preview, editing, Apply/Cancel.
- [x] Slice 04: Add Point coordinate form and point canvas tool + snapping foundation.
- [x] Slice 05: Frame draw tool and coordinate dialog + reliable connect-by-ID.
- [x] Slice 06: selection, Move, Delete with safe cascade preview + undo/redo.
- [ ] Slice 07: full snapping UI, Ctrl+W display form, polish and performance work.
- [ ] After each slice: focused smoke and targeted checks, build, Save/Open round-trip, legacy projection and analysis parity; full regression before release. Mark each checkbox only with implementation evidence and a test record.

### UI verification scenarios (minimum)
- [x] Grid forms handle unequal X/Y/Z spacings (e.g. X 0/6/14; Y 0/4/9; Z 0/4.5/9.3), coordinate origin offsets, negative elevations and duplicated labels.
- [x] Grid/level creation doesn't create nodes or affect static/modal FEM analysis result hashes; manual `Generate Points` does.
- [ ] XY @ Z=9.3, XZ @ Y=4, YZ @ X=14 show correct filtered geometry and cursor XYZ, preserving nonzero fixed coordinate when adding objects.
- [x] Add two joints by form, draw connecting frame, move one end, undo, delete frame and undo: names/IDs and geometry remain stable and loads never dangle.
- [x] (hidden) A frame crossing but not lying on a plane is either drawn as an explicit cut indicator or hidden, never falsely shown as a connected in-plane frame.
- [ ] Switching views, zooming and snapping rapidly remains responsive without mis-selections or orphaned previews.
- [ ] Check model save/open, imported geometry, native template, running analysis, result staleness, selection, shortcut behavior, and high-DPI dialogs.

### Later, not part of initial usable-canvas milestone
- [ ] Copy/Replicate (linear along X/Y/Z and storey replication), Rotate, Mirror, Divide Frames, Merge Joints and multi-object transforms.
- [ ] Shell/Cable interactive drafting and extruded object editing, rotated/radial grids, multiple dockable views, section cuts, and custom UCS.
