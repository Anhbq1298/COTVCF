# Native building and analysis forms

Implemented 2026-10-12. **One window since 2026-10-15** (M8 of the main-window migration; task of 2026-10-14: "one
window, one model"): the separate native workspace window and File → Open Native Model… are retired — archived under
`archive/Mcotis.Desktop/NativeWorkspace/`, not compiled — and everything they did is in the main window, on its one
native document.

## TemplateBuilding

Reach the template from **Edit → TemplateBuilding…**, including on a blank or locked model. The building it creates
becomes the model of the main window, shown in the 3D view; a model with changes not saved is offered to be saved first
("Save changes to …?" — Yes saves, No drops the changes, Cancel keeps the model).

1. Enter X bays, Y bays and storeys; click **Update Tables**. Existing rows retain their inputs when counts change.
2. Enter each X/Y bay width and each storey height in metres. Heights are increments above the floor below.
   Zero bays in Y creates an XZ frame; zero bays in X creates a YZ frame. At least one direction needs a bay.
3. Enter each floor's downward X-beam and Y-beam line loads, in kN/m. Each value applies to every beam parallel
   to that axis on that floor; edge beams receive the same value. These are already line loads, not area loads
   converted using tributary widths. Columns receive no imposed line load. Negative values are refused.
4. Review material E (kN/m²), Poisson ratio, weight density (kN/m³), mass density (t/m³; weight density / g until a
   value is typed) and general column/beam A (m²), I33, I22 and J (m⁴). Starter values are editable illustrative
   properties, not a designed section or material specification. Local section axes follow
   [the frame engine conventions](analysis/spatial-static-verification.md).
5. Select fixed or pinned bases and optional member self-weight; click **Create Model**.

The generator shares grid points between adjoining members, splits columns at every floor and beams at every bay,
and creates BuildingLoad and BuildingStatic (a linear static case). Connections are rigid. There are no slabs,
diaphragms, braces, offsets or cable objects. It writes Full 3D degrees of freedom (the XZ or YZ plane for a building
with bays in one direction only). A TemplateBuilding model carries no generator flag: it can be designed, not optimized.

## How the main window takes a native document in

`Application/NativeDocumentImport` reads the document and `MainViewModel.OpenNativeDocument` makes it the window's
model, the way Import From ETABS makes an ETABS model the window's model: the objects keep their names; sections keep
A, I33, I22 and J; materials keep E, Poisson ratio (`FemSectionProperties.PoissonRatio`), weight and mass density;
releases, modifiers, restraints (none is added — a model that holds none is reported unstable by its run), joint
masses, loads, load cases with their kinds and options, combinations, mass sources and the degrees of freedom are taken
as they are; a cable target force becomes the cable's pretension row (Tab 03). The Tab 01 storey loads every run of the
window reads get a neutral entry — 0 kPa on the top joints in a pattern without joint loads there — so the run takes
the document's own loads exactly. The model keeps the document's identity. What the window's model cannot hold yet is
refused, all at once with each object, before the open model changes: shells, rotated local axes, releases other than
the moment about local 3, shear areas, joint loads along Y or moments about X or Z, frame loads along Y or local 3 (or
local 1 / 2 on a frame out of the XZ plane), more than one target force per cable or one read elsewhere than
mid-length, envelope combinations.

`--template-building-ui` shows that the window's run gives what the building's own document gives solved directly —
every joint, six components (largest difference 4.6E-18 m on 3.0E-4 m) — with the reactions balancing the line loads.

## Files

**File → Open…** opens a `.mcotis` saved by the former native workspace as the main window's model; Save then writes it
in the main window's form (see `docs/USER_GUIDE.md`, "Model files"). Native files are schema 6: load case kinds and
options (schema 5), modal mass sources and six-component joint mass assignments, application data and the generator
flag. An older schema is upgraded when the file is opened, and written as schema 6 on Save.

## Modal cases and joint masses

Define → Load Cases → **Load Case Data**, kind Modal: a mass source and a number of modes; MS1 counts the members' self
mass and the joint masses. **Assign → Joint → Masses…** gives the joints selected on the model view nonnegative
UX / UY / UZ lumped masses [t] and RX / RY / RZ inertias [t·m²] — one step of Undo / Redo, saved with the model, kept by
name like a restraint. Element self mass is `rho A L` times the frame mass modifier, split equally between its ends.
Run FEM Analysis solves an unstressed elastic frame modal case with the active degrees of freedom of Analysis Options;
P-Delta / tangent-source modes, cables and shells are refused with the missing capability; elastic stiffness is never
silently substituted for a requested tangent state. Result → **Modal Results…** lists the modes, participation, mass
inventory and the selected mass-normalized eigenvector, drawn as a relative amplitude, never as a displacement.

## Analysis Options

**Run → Analysis Options… → Degrees of Freedom**: the presets (Full 3D, the XZ, YZ and XY planes) and six boxes for the
model, six for cable joints (UX UY UZ by default). The form keeps any set that has a translation in it; whether the model
can be analysed with it is for the run to say, in the Run Monitor (user, 2026-10-13: "if not proper then report in the
log"). A model starts in Full 3D. A model of a plane on pins that are all on one line is not held out of its plane in
Full 3D; its loads do no work on that movement, so the run holds it, solves and lists it as a warning (user, same day:
"ETABS can run regardless") — never a movement the loads act on, and never a degree of freedom nothing holds. The other
tab edits the cable target-fitting tolerance, maximum iterations, frame mesh (1–100: the stations each frame piece is
read at, which changes no joint result) and cable sag; without cables it is the frame mesh alone.

The engine's limits: 3,000 free / 6,000 total degrees of freedom in the main window, small displacements, no shells,
no P-Delta. Linear static cases are solved on the unstressed stiffness since 2026-10-15 — the engine's linear elastic
solve, verified against OpenSeesPy ([analysis/spatial-static-verification.md](analysis/spatial-static-verification.md));
a model with cables needs a nonlinear case and Run says so. Cable modes remain unavailable until a verified prestressed
tangent state exists. The evidence: [analysis/cable-static-verification.md](analysis/cable-static-verification.md),
[analysis/modal-eigen-verification.md](analysis/modal-eigen-verification.md).

Verification: `dotnet run --project tests/Mcotis.Checks -- --full` includes template topology, line-load accounting, support
reactions, self-weight, modal closed forms and OpenSeesPy comparisons, planar cases, invalid input, Undo/Redo and Save/Open.
`--template-building-ui` on `tests/Mcotis.Desktop.Checks` checks the form, the building in the main window (its run against
the document solved directly, joint masses, the modal case, Save / Open, a native-workspace file, the "Save changes?"
prompt), the main window's Analysis Options and the run monitor, and WPF bindings; it captures the forms in `.work/*-ui.png`.
