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
