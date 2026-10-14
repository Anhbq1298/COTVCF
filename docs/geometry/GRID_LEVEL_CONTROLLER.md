# Grid and Level Controller — Phase 1 and Next Steps

## Implemented in feature/geometry-editing-plan

The native model window's viewport receives an independent orientation/coordinate selector:
- 3D: full geometry
- XY plan @ Z = constant: level/floor plan
- XZ elevation @ Y = constant: Y grid/elevation
- YZ elevation @ X = constant: X grid/elevation
- Manual fixed-coordinate entry in model metres; previous/next reference plane from point coordinates.

`NativeGridLevelController` derives sorted unique plane coordinates from `ModelDocument.Points`, filters the viewer geometry to points and **whole frames/cables** on a selected plane, and does not mutate analysis input. Tolerance (1e-6 m) is deliberately separate from snapping tolerance.

**Known limitations**: no separately named user-defined grids yet, no persistent grid metadata, no dashed reference-grid rendering, no members crossing planes as cut/section outlines, no rectangular/rotated grid systems, no native drawing gestures on that plane. The existing viewer still exposes its separate projection combo; selecting that alone only changes projection, not the coordinate slice. The native toolbar owns slice selection. Run results remain full-model regardless of view.

## Required next slice: SAP2000-style persistent grid system

- Add independently named grid systems with X grid lines, Y grid lines, radial/rotated grid later; numeric positions in document length units, labels A/B/C and 1/2/3, origin, angle, visibility. Avoid conflating user-defined grids with joint coordinates.
- Add storeys/levels with unique names (Base, L1, L2, Roof) and precise Z elevation (not merely incremental heights), plus editable ordering, insertion/removal and duplicate-elevation validation.
- Grid and story metadata is geometry reference data, not FEM nodes/elements and not a solver input. Put immutable definitions in the native `Mcotis.Core` document; extend `DocumentEditor`, `DocumentCommands` and persistence schema with backward migration. Add user controls for **Define > Grid Systems** and **Define > Story Data**.
- Show grid guides/axis bubble labels and level annotations on appropriate XY/XZ/YZ slices. In 3D, show/hide reference planes independently.
- Named choices like `XY Plan @ L2 (+8.000 m)`, `XZ Elevation @ Grid B (Y=+6.000 m)`, `YZ Elevation @ Grid 3 (X=+12.000 m)`; also support arbitrary numeric plane.
- Snap grid intersections on current plane without creating FEM joints. Snap and Draw Frame use the same active plane from this controller, never infer depth from the mouse in 3D.
- Option to show geometry **lying on plane** (implemented) or **cutting plane** (future, display-only), with a definable tolerance for imported coordinates, distinct from snapping.

## Acceptance criteria for next slice

1. Definitions saved to `.mcotis`, reopen identically, reversible by one Undo.
2. Changes to labels/gridline positions do not silently move physical joints or mark analysis results stale if geometry is unchanged.
3. Switching plans preserves valid world coordinates. Draw at XY Z=12 places joints at Z=12.
4. Existing `ModelDocument` legacy native file migration, template buildings, imported geometry, and analysis results remain valid.
5. Grid labels, offsets and model units formatted correctly; no duplicate or ambiguous names.
6. Explicit visibility of viewplane controls when 3D/XY/XZ/YZ changes.
7. UI regression checks for plan selection, coordinate stepping, typing and projection switch.

## Detailed implementation checklist — SAP2000 / ETABS style

This checklist complements [GEOMETRY_EDITING_PLAN.md](GEOMETRY_EDITING_PLAN.md). All unchecked items are planned, not implemented.

### Grid Systems WPF design
- [ ] Build `Define > Grid Systems...` WPF dialog with grid-system selector, `New / Duplicate / Rename / Delete` and coordinate reference (X origin, Y origin, rotation). Version 1 supports Cartesian only.
- [ ] Tab `Grid Lines`: two DataGrids for X and Y lines, columns `Grid ID | Coordinate (m) | Spacing from Previous (m) | Visible`; preserve user-edited coordinate ordering, and use named X grids `1, 2, 3` and Y grids `A, B, C` by default.
- [ ] Tab `Quick Grid`: number of bays NX/NY, uniform DX/DY, origin X/Y; optional list of unequal spacings, with actual computed coordinates preview. Positive counts, finite coordinates, collision-free names and actionable validation.
- [ ] Tab `Display`: axis bubble labels at model extents, line style, extents, show grid coordinates, default visibility; options should not generate physical FE joints.
- [ ] Stage changes until Apply/OK; Cancel must leave model unchanged; Apply should commit one undoable edit and update the viewport without losing camera state.
- [ ] Persist grid definitions in versioned native document data and Save/Open .mcotis; backward compatibility for models created without reference grids.

### Story and Z spacing controller
- [ ] Build `Define > Levels...` form with table `Name | Z Elevation | Height to Above | Show`. Editing a level's elevation adjusts its displayed reference plane only, not connected FE geometry.
- [ ] `Generate Levels` dialog fields `Base Z | Number of Storeys | Typical Storey Height`; allow individually edited heights and custom names after generation.
- [ ] Support manual Z plane not matching a named level. Display current plane and coordinate in the viewport ribbon, including negative elevations and unit suffix.
- [ ] Optional checkbox `Generate FE Joints at Grid Intersections`, **off by default**; first show count and coordinate preview and ensure point IDs/names are unique and deduplicated with model-space tolerance.

### View switching and grid rendering
- [ ] Replace split-brain native toolbar versus internal viewer projection with single synchronized `ActiveViewContext`; both selectors must show exactly the same active view.
- [ ] XY uses `Z=constant`, XZ uses `Y=constant`, YZ uses `X=constant`. For 3D, no fixed coordinate. Per-view camera state is maintained across transitions.
- [ ] Show level/grid list from persisted definitions, with an `At coordinate...` option; previous/next navigates saved named references, not every joint coordinate when named grids exist.
- [ ] Draw light dashed grids under structural members, labelled bubbles, subtle current-plane highlight and visible origins; gridlines are non-selectable reference visuals.
- [ ] Render XY grid crossings and XZ/YZ level intersections for snapping with distinct `Grid intersection` glyph; avoid confusing geometric crossings with actual connected joints.
- [ ] Preserve fit/zoom/pan/selection and avoid full-frame rebuilds on mouse movement. Transition should remain smooth even when no members lie in the selected view.

### Connected workflows
- [ ] Point dialog's `On active plane` mode auto-fixes the out-of-plane coordinate; user controls remaining two coordinates by typing or snap.
- [ ] Frame draw I/J coordinates and live preview use the same plane and grid data. A grid intersection becomes an FE joint only after an explicit Add Point / Draw Frame action.
- [ ] Model view settings and reference geometry must not modify analysis DOFs or solver state; structural edits do invalidate prior results.
- [ ] Tests: irregular grids, renamed/moved lines, story Z changes, duplicate/negative coordinates, save/open, undo/redo, mixed 2D/3D plane switching and imported models.

### UI form layout sketch

```text
Define Grid Systems
┌─────────────────────────────────────────────────────────┐
│ Grid system: [GLOBAL v]    [New] [Copy] [Rename]       │
│ [Grid Lines] [Quick Grid] [Display]                    │
│                                                         │
│ X Grids                        Y Grids                  │
│ ID   Coordinate   Spacing     ID   Coordinate   Spacing │
│ 1      0.000        —        A      0.000       —      │
│ 2      6.000       6.000     B      4.000      4.000   │
│ 3     14.000       8.000     C      9.000      5.000   │
│ [+] [-]                       [+] [-]                   │
│                                                         │
│ Origin X [0.000] Origin Y [0.000] Angle [0.0°]        │
│                                                         │
│                    [Preview] [Apply] [Cancel] [OK]     │
└─────────────────────────────────────────────────────────┘

Add Joint / Point
┌───────────────────────────────────────────┐
│ Name      [ Auto P...         ]           │
│ Reference [Absolute XYZ       v]          │
│ X (m)     [ 14.000            ] [Pick]     │
│ Y (m)     [  4.000            ] [Pick]     │
│ Z (m)     [  9.300            ]            │
│ [x] Use active work plane                 │
│                                           │
│         [Add Point] [Add Next] [Cancel]  │
└───────────────────────────────────────────┘
```

**Dependency:** persistent grids/levels first, then canvas rendering and snapping, then coordinate forms and frame editor. Do not auto-create a full Cartesian FEM mesh simply by defining a visual grid.
