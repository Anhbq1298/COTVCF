# Native Geometry Editing — Implementation Plan

Status: proposed; no runtime implementation in this branch.
Scope: native Mcotis model, reusable across workspaces. Preserve current solver, persistence, import/export and architecture.

## Repository integration points (inspected on 2026-10-15)
- `src/Mcotis.Application/DocumentCommands.cs`: `AddPoint`, `AddFrame`, `MovePoints`, `Remove`, `Duplicate`, `Edit`; all are already undoable via `DocumentSession`.
- `src/Mcotis.Desktop/Presentation/NativeModelWindow.xaml(.cs)`: menu, WPF keyboard handler, native document session.
- `src/Mcotis.Desktop/Presentation/NativeModelViewModel.cs`: maps `ModelDocument` into read-only `GeometryModel` for current viewer.
- `src/Mcotis.Desktop/Presentation/Controls/Structural2DViewer.xaml.cs`: joint/member selection and 2D/3D display. Must not mutate model directly.
- `src/Mcotis.Core`: point/frame object IDs and model editor; `src/Mcotis.Application`: commands and session.
- Read `AGENTS.md`, `skills/mcotis-ui-handoff/SKILL.md`, `docs/mcotis-standalone-plan.md` before implementation.

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
