# Native building and analysis forms

Implemented 2026-10-12. Reach the template from **Edit → TemplateBuilding…**, including on a blank or locked
legacy model. Each template opens a separate native workspace and does not replace the main window's model.

1. Enter X bays, Y bays and storeys; click **Update Tables**. Existing rows retain their inputs when counts change.
2. Enter each X/Y bay width and each storey height in metres. Heights are increments above the floor below.
   Zero bays in Y creates an XZ frame; zero bays in X creates a YZ frame. At least one direction needs a bay.
3. Enter each floor's downward X-beam and Y-beam line loads, in kN/m. Each value applies to every beam parallel
   to that axis on that floor; edge beams receive the same value. These are already line loads, not area loads
   converted using tributary widths. Columns receive no imposed line load. Negative values are refused.
4. Review material E (kN/m²), Poisson ratio, weight density (kN/m³), and general column/beam A (m²), I33, I22 and J
   (m⁴). Starter values are editable illustrative properties, not a designed section or material specification.
   Local section axes follow [the frame engine conventions](analysis/spatial-static-verification.md).
5. Select fixed or pinned bases and optional member self-weight; click **Create Model**.

The generator shares grid points between adjoining members, splits columns at every floor and beams at every bay,
and creates BuildingLoad and BuildingStatic. Connections are rigid. There are no slabs, diaphragms, braces, offsets
or cable objects. The generator is one undoable command and refuses an existing geometry document; another
**Edit → TemplateBuilding…** starts a separate building. This first template does not regenerate an edited building.

In the native window, **File → Save / Save As / Open Native Model** uses JSON `.mcotis` files. Save writes the current
document. Open validates before replacement. Unsaved changes are checked on Open and Close. Results remain in
memory and are cleared on Open; the input file does not store them. The main window also has **File → Open Native Model…**.

**Run → Analysis Options… → Degrees of Freedom** provides 3D, XZ, YZ, XY and custom six-DOF selections. Apply commits
one command; OK commits and closes; Cancel discards edits since the last Apply. Global inactive DOFs are separate
from point supports. The compiler rejects geometry and loads incompatible with a selected plane; coordinates are
never projected. Run executes asynchronously and shows diagnostics on failure. Joint translations use m, rotations
rad, reaction forces kN and moments kN·m. Frame end actions use local axes. The geometry view is undeformed;
native deflection contours are not connected yet. Edited inputs mark existing results Stale; Undo can restore Current.

The native engine retains its documented capability limits, including 600 free / 2,400 total DOFs. The template
form allows up to 400 points for model creation/persistence, but larger free-DOF counts can still exceed the run limit.
It has no cable, shell, P-Delta, modal, staged or nonlinear analysis. Template UI does not change the numerical source
verification gates or establish production readiness.

The main window's **Run → Analysis Options…** is the existing planar solver's form. It shows fixed XZ DOFs and edits
actual cable target-fitting tolerance, maximum iterations, frame mesh (1–100) and cable sag. Individual cable target
forces remain in the cable assignments/load patterns. Native options show cable/mesh settings as inactive because
the native frame engine does not use them. Locked/running legacy models show their options read-only.

Verification: `dotnet run --project tests/Mcotis.Checks` includes template topology, line-load accounting, support
reactions, self-weight, planar cases, invalid input, Undo/Redo and Save/Open. `--template-building-ui` on
`tests/Mcotis.Desktop.Checks` checks staged forms, native runs, diagnostics, result state, Save/Open and WPF bindings;
it captures the forms in `.work/*-ui.png`. Existing numerical benchmarks remain unchanged.
