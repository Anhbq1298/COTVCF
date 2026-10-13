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
Since the general solver (2026-10-12) **Run → Run Analysis** also solves a native model that has cables: tension-only
cables, cable target forces fitted in the case set to fit them, nonlinear static cases continuing from another, and
cable sag. The **Cable results** tab lists each cable's tension, whether it is taut or slack, its target, the applied
pretension, its tension at the fit stage, its sag and its remaining stiffness. Warnings of a run that succeeded (a
joint rotation nothing reaches, a translation held by slack cables only) are shown under the result line. There is
no shell, P-Delta or modal analysis. The template form still creates frames only. Template UI does not change the
numerical source verification gates or establish production readiness; the evidence is in
[analysis/cable-static-verification.md](analysis/cable-static-verification.md).

The main window's **Run → Analysis Options…** is the form of the main window's own run. That run uses the same
general solver. Because the main window's model is planar, its Degrees of Freedom tab offers only what that model
can change (user, 2026-10-12: a tab of boxes that cannot be changed is redundant; 2026-10-13: the tab is wanted, to
change degrees of freedom): RY for the model, and UX and UZ for cable joints. UX and UZ of the model stay on — the
model spans its plane — and there is no preset. The run monitor states the degrees of freedom of each run. On its
other tab the form edits the
cable target-fitting tolerance, maximum iterations, frame mesh (1–100: the stations each frame piece is read at,
which changes no joint result) and cable sag. Individual cable target
forces remain in the cable assignments/load patterns. In the native options the same four settings are active: the
native run reads the fitting tolerance, the iteration limit and cable sag, and reads each frame at the mesh stations.

**Degrees of freedom of cable joints** (native options, Degrees of Freedom tab): six checkboxes, UX UY UZ on by
default. A joint that only cables reach has these, among the active degrees of freedom; the others are switched off
there. A cable gives stiffness along its own line only, so a movement left on that nothing else holds — another
member, a restraint — makes the model unstable, and Run reports it with the joint and the movement instead of
solving. In the main window cable joints move in UX and UZ; that is fixed and not shown as an option. Locked/running legacy models show their options read-only.

Verification: `dotnet run --project tests/Mcotis.Checks` includes template topology, line-load accounting, support
reactions, self-weight, planar cases, invalid input, Undo/Redo and Save/Open. `--template-building-ui` on
`tests/Mcotis.Desktop.Checks` checks staged forms, native runs, diagnostics, result state, Save/Open and WPF bindings;
it captures the forms in `.work/*-ui.png`. Existing numerical benchmarks remain unchanged.
