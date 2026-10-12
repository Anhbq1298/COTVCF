# Mcotis

**Mcotis** — *Meinhardt Cities of Tomorrow Integrated Structures* (formerly COT VCF Parametric Automation).

WPF / .NET 8 desktop application for defining structural-study candidates from an existing SAP2000 model, then building and analyzing one isolated child `.sdb` model per selected combination.

The study workflow is sequential:

`SAP baseline -> loading candidates -> truss-height candidates -> cable-pretension candidates -> analyzed and verified child models`

Tabs 1–4 only configure and preview candidates. SAP2000 assignments are changed only while Tab 5 builds child models; the original baseline model is never used as an output.

## Connecting

Open and save the intended model in SAP2000, then click **Connect SAP** in the top bar. The application attaches to that running instance and reads model identity, units, lock state, geometry, load patterns, and relevant objects. The first connected model and its original geometry become the immutable session baseline used by every candidate. **Refresh** rereads the connected model; a changed model, units, or external geometry can invalidate pending writes rather than being accepted silently. The shared model preview (right pane on Tabs 1–4) supports XY/XZ/YZ projection, fit/reset, zoom, pan, labels, and synchronized selection.

## Tab 1 — Loading Criteria

1. **Add Load Locations** selects joints from the cached SAP geometry. Locations are deduplicated and ordered by global X.
2. Define one or more loading rows as either a fixed `qz` or a `Min / Max / Step` range.
3. Set the out-of-plane tributary width `B` and whole-number `NStories`.
4. **View Candidates** previews every loading combination and the resulting joint forces.

For each load definition, candidate values are generated on-grid from Min through Max. A loading candidate is the Cartesian selection of one `qz` value from every definition. Tributary area is based on X-direction midpoint spacing and width `B`, with half-spacing at the end joints:

`Fz = -qz × TributaryArea × NStories`

During Tab 5 execution, each joint/load-pattern force assignment is **replaced, not added**. Existing moments on that assignment are preserved. Ambiguous multiple assignments or incompatible coordinate systems are rejected instead of guessed.

## Tab 2 — Truss Height

1. Select non-overlapping Top, Middle, and Bottom joint groups.
2. Choose the reference chord that must remain fixed.
3. **Capture Baseline** records the original height `H0` and original joint coordinates.
4. Define the candidate `Min / Max / Step` range and preview values with the slider or **View Candidates**.

Every height candidate is calculated from the immutable baseline, never from the previous candidate, so changes cannot accumulate or drift. For `ΔH = candidate height - H0`:

- Top reference: Top `0`, Middle `-0.5ΔH`, Bottom `-ΔH`.
- Middle reference: Top `+0.5ΔH`, Middle `0`, Bottom `-0.5ΔH`.
- Bottom reference: Top `+ΔH`, Middle `+0.5ΔH`, Bottom `0`.

Tab 2 previews geometry only; it does not write to SAP2000.

## Tab 3 — Pretension

1. **Add Cable** selects cable objects from the cached geometry.
2. For each cable, select or enter an existing SAP load pattern.
3. Define a fixed target force or a `Min / Max / Step` range.
4. The pretension space is the Cartesian product of the per-cable target-force candidates.

These are SAP cable **Target Force** assignments, not analysis results. During Tab 5 execution, an existing target force for the same cable/load pattern is deleted and replaced, then read back for verification. Tab 3 itself does not write to SAP2000.

## Tab 4 — Output Settings

Define the study-wide result monitoring: deflection monitor points, member-force groups, foundation-reaction points, and monitored cables plus a result load case. Each becomes an `[O]-` SAP group. Editing this tab does not write to SAP2000 — the `[O]-` groups are created on the immutable parent baseline as the first gate when Tab 5 runs, then the parent `.sdb` is saved before any worker starts.

## Tab 5 — Iteration Study

Tab 5 composes deterministic `Lxx_Hxx_Pxx` rows from the three candidate dimensions:

- `NL`: Cartesian loading combinations from Tab 1.
- `NH`: truss-height candidates from Tab 2.
- `NP`: Cartesian cable-pretension combinations from Tab 3.

Study modes control the final study space:

- **Fix Pretension:** `NL × NH × 1`.
- **Fix Height:** `NL × 1 × NP`.
- **Combined:** `NL × NH × NP`.

Each preview row shows its complete joint/load-pattern/Fz assignments and cable/load-pattern/target-force assignments. The selected row set and all resolved candidate data are frozen when the build starts.

### Child-model transaction

For every selected candidate, one worker performs this complete sequence:

1. Reopen the immutable baseline.
2. Create `<baseline folder>/Study/<iteration id>/<baseline>_<iteration id>.sdb` using **Save As**.
3. Apply the candidate height geometry.
4. Replace the candidate point-load assignments.
5. Replace the candidate cable target-force assignments.
6. Save the configured child model.
7. Run every SAP2000 analysis case currently marked to run. `RunAnalysis` locks the model and persists the analyzed `.sdb` plus its result files itself — the worker does not save or reopen the child again (doing so makes SAP2000 delete the result files when the next candidate opens the baseline).
8. Verify the analyzed child in place: it must be locked and still hold the resolved geometry, loading, and pretension.
9. Return the worker to the baseline before accepting another candidate.

A child becomes **Ready** only after analysis and in-place verification succeed. Existing child files or non-empty candidate folders are never overwritten. Candidate failure is isolated where possible; a worker that cannot recover the baseline is removed. Tab 5 does not extract results yet.

### SAP2000 instance count

The Tab 5 selector is the **total SAP2000 instance count**, not an additional-worker count:

- Select `1`: use the already-connected baseline instance; start `0` additional processes.
- Select `2`: use the connected instance plus `1` additional process.
- Select `3`: use the connected instance plus `2` additional processes—exactly `3` study instances total.

The connected instance is worker 1 and processes candidates. The study pool borrows it and does not close it. Calls are sequential inside each worker and parallel across workers; the next available worker receives the next queued candidate.

## Tab 7 — FEM

**Run → Run FEM Analysis** solves the model once, as it is defined: its own geometry (no Tab 2 height change), the
Tab 1 loads and the Tab 3 cable target forces. A range entered in Tab 1 or Tab 3 is an input of the parametric study,
not of this run; it enters with its first value, and Run → Analysis Options names what the run uses. Many cases —
other loadings, geometries, pretensions or sections — are the parametric study: **Run → Optimization VCF** with the
internal solver, or the Tab 5 study with SAP2000.

**Load cases are defined in the app** (Define → Load Cases / Load Combinations), with SAP2000's setup for a cable
structure as the default — the same staging, solved by the internal FEM:

| Case | Type | Definition | Continues from |
| --- | --- | --- | --- |
| `Pretension` | Nonlinear Static | 1 × target-force pattern + 1 × self-weight — the cable targets are met here | — |
| `SLS: 1.0DL+1.0LL` | Nonlinear Static | 1 × dead + 1 × live (the Tab 1 loads) | `Pretension` |
| `ULS: 1.35DL+1.5LL` | Nonlinear Static | 0.35 × self-weight + 1.35 × dead + 1.5 × live | `Pretension` |

A case that continues from another starts with that case's loads and fitted cable pretension on the structure, so ULS
carries 1.0 + 0.35 = 1.35 of the self-weight. **Default Setup** builds these from the self-weight pattern, the load
patterns of Tab 1 (dead or live by pattern type, else by name) and the target-force pattern of Tab 3; the built-in model
opens on them. **SAP Cases** shows the cases saved from SAP2000 instead, and **+ Add** / **- Remove** with the editable
Case, Definition and From columns define others.

The loads and the cable forces are part of the parametric set-up of the model, **Edit → VCF System**: the **Storey Force**
sheet takes the tributary width B, the number of storeys, one qz per load pattern and the loaded points — chosen from a
list (**+ Add**), picked on the preview by a click or a window (**+ Add Selected**) and taken off with **- Remove**; the
preview shows a load arrow at each. The **Cable Force** sheet takes the target force of each cable. They are the data of Tab 1 and Tab 3 with **one fixed value each** —
no range there. A range is an input of an optimization run (**Run → Optimization VCF**), set when it is run.

The flow is the one of SAP2000: **Define → Load Patterns** (add or remove patterns of the model — name, type, self-weight
multiplier), give them their loads in **Edit → VCF System**, define the load cases, then **Run**. Run first opens **Set
Load Cases to Run** — every case with its status and its action, Run or Do Not Run — and **Run Now** analyses the model
as it is at that moment: its geometry, section assignments, loads and cable target forces.

The analysis is internal and never calls SAP2000. It uses the sections, materials and loading the model holds: those
of the default model, of a model opened with **Load Model**, or the ones **File → Read FEM Properties from SAP**
brings in from a connected model — each assigned frame/cable section, effective material (including the CSI `None`
override sentinel), A, E, in-plane I, frame stiffness modifiers, and in-plane end releases. Isotropic materials use
SAP's isotropic modulus API; tendon materials use the uniaxial modulus API. After Connect SAP or Refresh the run asks
for that read first; it does not fetch the data itself. The results report peak downward
joint displacement, axial force, bending moment, and support reaction of the run. Auto-detected lowest column
bases are drawn as pin symbols and modeled with X/Z translations fixed and in-plane rotation free.

The Result menu displays Axial Force, Bending Moment, Shear, Reactions, or Deflected Shape of the run for the selected
load case or combination in the shared model
preview, with a matching numeric table. These are the internal FEM results, not saved SAP2000 load-case
results; the Tab 1 loads are assembled from the Tab 1 definitions. Axial, bending, and shear
results draw filled blue-positive/red-negative diagrams (positive axial force is tension); reactions draw support
vectors; the deformed shape is visually magnified. Diagram widths are auto-scaled for display, while the adjacent
table retains calculated values and units.

The active analysis connection is SAP2000. For an ETABS-originated model, open/export that model into SAP2000
first; this feature does not attach directly to an ETABS process.

The current solver is linear-elastic 2D XZ Euler-Bernoulli frame analysis with axial tension-only cables and
Tab 3 target forces met like SAP Target Force: the FEM Analysis page's fitting tolerance (default ±10%) and
max iterations (default 100) control a pretension-only stage in which all cable pretensions are adjusted together
(finite-difference Newton) until each cable is within tolerance of its target; the Tab 1 loads then act on that
prestressed state, so loads that alone exceed a target do not block fitting. As in SAP, a frame is split at every
member-end joint lying on it (within 1 mm), so a full-length chord connects to the verticals and cable anchors
along it; each piece is then meshed into N equal sub-elements (Analysis → Frame mesh, default 10; cables are not meshed); interior mesh nodes are condensed onto the
frame joints and recovered as stations for force diagrams and the deflected shape. Cable equilibrium uses damped Newton iterations with a potential-
energy line search, so cables can transition between taut and slack without a Boolean active-set toggle loop. Next to
the equilibrium, where a step changes the energy by less than the round-off of the energy sum, a full Newton step is
accepted when it at least halves the residual; convergence is always judged by the residual.
It rejects non-planar geometry, tendons, SAP advanced local axes, axial or in-plane shear releases, and rotated
local axes with in-plane releases. It does not run SAP2000 analysis cases.

**Cable sag** (Analysis → Cable sag, on by default) is the one geometric nonlinearity included. A cable that a load
case loads along its length — its self-weight, typically — hangs in a shallow parabola, and its tension T follows
`T/k − a/T² = extension + P/k` with `k = EA/L` and `a = (w·cos θ)²·L³/24`. Its stiffness is then Ernst's equivalent
`k / (1 + (w·cos θ·L)²·EA / 12T³)`, and it never goes fully slack. The cable table reports the sag and the stiffness
kept. This is the parabolic (Ernst) model, not a full catenary with large displacements: P-Δ and large-displacement
geometry updates are not modelled.

**Basis of the solver.** It was written for this application and contains no code from OpenSees, SAP2000 or any other
analysis package (MathNet.Numerics provides the linear algebra). It implements standard formulations: the direct
stiffness method with Euler–Bernoulli elements (McGuire, Gallagher & Ziemian, *Matrix Structural Analysis*, 2000),
static condensation (Guyan, AIAA Journal 3(2), 1965), the shallow elastic cable (Irvine, *Cable Structures*, 1981)
with Ernst's equivalent modulus (Der Bauingenieur 40(2), 1965), and energy minimisation by Newton's method with a
line search (Nocedal & Wright, *Numerical Optimization*, 2006). `--fem` checks it against closed-form beam results
and an independent solution of the cable equation.

**Benchmark against SAP2000** (`--fem-sap`, fixture in `tests/fem-sap-benchmark`). The FEM is run on SAP2000's own
text copy of the COT Framing_V1.2 model and compared, like for like, with the completed SAP2000 study of 135
candidates: same candidates, cable target forces met in the same case (`Pretension`, self-weight included), same
staged load cases. The total vertical reaction agrees to 0.002 %; cable forces to within 3.8 % and deflections to
within 3.2 % of the largest SAP2000 value; reactions to within 1.2 %. The remainder is the difference in theory
(SAP2000: P-Delta / large displacements, catenary cables, shear-deformable frames). Not compared with OpenSees.

The benchmark found two things that affect a comparison of the FEM tab with a SAP2000 study:

- The FEM's SAP read overstates the self-weight of composite Section Designer sections (base-material unit weight ×
  transformed area): 150 instead of 105 kN/m for the concrete-filled chords, 4 % more total load on this model.
- **Fixed — studies run before the fix have to be run again above the baseline height.** The SAP2000 study changes
  the truss height by moving joints, and a cable object defined by its undeformed length kept the length of the
  baseline model (SAP2000's own text copies of the child models record it): up to 29 % shorter than the chord it
  spans, so up to 41 % stiffer, in the tallest candidates. The geometry write now gives every such cable the same
  length relative to its chord on the new geometry. Verified in SAP2000 27.1 (`--cable-length-live`): on the tallest
  candidate the peak deflection goes from 93.3 mm to 114.5 mm, and the FEM tab gives 114.7 mm.

## Tab 8 — Quantity Take-off

Tab 8 shows the concrete volume and steel mass of **one model**, grouped by the section assigned to its members.
The **Candidate** dropdown lists **Connected model** first — the model that is open in the connected SAP2000, read
**live** — and then the study's candidates (`Lxx_Hxx_Pxx`). The tab opens on the connected model; the breakdown loads
in the background. Picking another entry is how models are compared, including different truss heights.

**Live means live on request.** The connected model is read from SAP2000 itself through the API each time: when the
tab is opened, when it is shown again, and when **Refresh** (the icon beside the dropdown, or the top-bar refresh) is
pressed. Change a section in SAP2000, press Refresh, and the table follows — saved or not. It is not polled in the
background. A candidate, by contrast, is a saved model: it is read from its own file and shows when that was written.

| Section | Figure | Member count | Total length m | Concrete volume m³ | Steel mass t |
|---|:-:|---:|---:|---:|---:|

One row per assigned section (frame sections, then cable sections) and a **Total** row. A quantity that cannot be
established reads **n/a** — never zero — with the reason as its tooltip and in the notes under the table; the total
of that column is then n/a as well, and its tooltip gives the sum of the sections that could be taken off. `0.000`
means the section really has none of that material.

**Figure** shows the section's cross-section drawn to scale from the same stored data: the Section Designer shapes
in their drawing order (concrete grey, steel blue, a tube with its opening), or a standard section from its stored
dimensions (box/tube, I/wide flange, rectangular, circle, pipe), or a cable as a circle of its stored diameter. Each
figure is scaled to fit its cell, so its overall width × depth in mm is printed under it; hover over it for a larger
view. A section whose data does not describe a drawable shape shows `—`. The figure is a picture only — no quantity is
taken from it.

**Where the candidates come from.** The dropdown lists the candidates of the study that belongs to the connected
baseline (`<baseline folder>/Study`): every `Lxx_Hxx_Pxx` the run journal (`StudyRun.json` and `RunHistory`) records
or that has a folder there, provided its model or SAP2000's text copy of it is on disk. A candidate a run failed on is
listed with a warning. The tab is available only while SAP2000 is connected, like the rest of the workflow.

**Where the data comes from.** Always the selected model's own data — never another model's, and never the Tab 7 FEM
properties.

*Connected model* — the database tables of the model open in the connected SAP2000 (`GetAvailableTables`,
`GetTableForDisplayArray`), on the application's own SAP2000 lane and in the model's present units. Read-only: no
table is opened for editing, units and lock state are not touched, nothing is saved. Like every call to the connected
SAP2000 it keeps the application busy for the moment it takes, and it waits for a connect, refresh or study that is
already running. If the model open in SAP2000 is no longer the one that was connected, the read says so — use the
top-bar refresh. SAP2000 rounds the numbers in these tables for display (a density as 7.849); a material's mass
density and the area of a frame or cable section are therefore taken from their own getters at full precision, so a
live take-off equals the one from the saved model. Run against SAP2000 27.1 (`--quantity-connected`): 17 tables in
about 1.5 s, model, units and lock state unchanged.

*A candidate* — never the connected SAP2000:

1. SAP2000's text copy of the candidate (`<model>.$2k`, written by SAP2000 beside the `.sdb` at every save) is used
   when it was written for that model, is complete, holds every table the take-off needs, and is not older than the
   model except by the study run itself (the run saves the child, which writes the export, and the analysis then
   writes the `.sdb` once more; the run journal accounts for that gap, and without a journal entry up to 15 minutes
   are accepted). This needs no SAP2000 at all.
2. Otherwise the model is read through an **isolated, read-only SAP2000 workflow**: the candidate's `.sdb` is copied
   into a private folder (`%LocalAppData%\COTVCF\QuantityRead`), a separate SAP2000 instance opens the copy, the
   same tables are read through the database-tables API (`GetAvailableTables`, `GetTableForDisplayArray`), and the
   instance is closed without saving. SAP2000 never opens the candidate's own files, and the connected SAP2000 — its
   model, units and lock state — is not involved. The application is busy while this runs; **Cancel** or another
   selection stops it. The table read itself is the one used for the connected model and has been run against
   SAP2000 27.1; **starting a separate instance on a private copy has not yet been run live** (it is covered by API
   doubles) — `--quantity-live` below does that.

The line under the dropdown states which source was used.

**How it is calculated.**

- *Member length* = distance between the member's two end joints in the candidate (frames and cables alike; a
  cable's sag and stored undeformed length are not applied).
- *Section areas* are physical areas per material. A Section Designer section is read from its drawn shapes — the
  shapes found in the stored models: I/wide flange, box/tube, solid rectangle and polygon with straight edges. A shape
  in front (higher Z-order) replaces what it covers, so an embedded steel shape displaces concrete; a tube's interior
  is empty unless another shape fills it. Any other frame section and a cable section have the one area of their one
  material (a member's material overwrite replaces it).
- *Concrete volume* = concrete area × length. *Steel mass* = steel area × length × that material's own mass density
  (`UnitMass`). SAP2000 material types Steel, Rebar and Tendon (cable strand) count as steel, Concrete as concrete.
  Values are converted to m, m³ and t from whatever units the model was saved in.
- The transformed area SAP2000 reports for a Section Designer section is used only as a check: the shape areas,
  weighted by their moduli, must reproduce it to 0.1 %, otherwise the section is withheld. It is never a quantity.
- Each object is counted once. A name the tables list twice is counted once; a member assigned to two different
  sections is counted with neither; two objects between the same joints with the same section are both counted and
  pointed out in the notes.

**Not taken off — shown as n/a with the reason, not estimated:** reinforcement (a shape or concrete section that
carries reinforcement data), Section Designer shapes other than the four above, polygons with rounded corners, shapes
that overlap at the same Z-order, library (non user-defined) shapes, non-prismatic sections, curved members, a
material overwrite on a composite section, materials that are neither concrete nor steel, a steel without a mass
density, and members whose section, joints or coordinates are missing.

On the COT Framing_V1.2 study the candidates differ by height only: 795.0 m³ of concrete and 2,862 t of steel at
20 m, 837.9 m³ and 3,262 t at 30 m (cable strand included). Charts, exports and further summary tables are not part
of this tab.

## Project configuration

The Mcotis project JSON stores the configuration for Tabs 1–5 and their study selections; loading a project never writes to SAP2000. **New Config** clears the current configuration and active file identity while retaining the connected SAP model and session baseline; the next save asks for a new file. Existing files and Print Setup preferences are retained. **Save Config** updates the current tab section, while **Save All** validates and saves all sections atomically. Saved configuration is checked against the connected model before it is applied. Configuration and workflow controls are enabled only after SAP connects successfully, and disable again if the connection is lost.

## Build and run

Requirements: Windows x64, .NET 8 Desktop Runtime, and a local SAP2000 installation providing `CSiAPIv1.dll` (SAP2000 26 is the project default; the installed SAP2000 27 API also builds). The API DLL is not committed.

```powershell
dotnet build MCOTiS.sln
dotnet run --project src/Mcotis.Desktop/Mcotis.Desktop.csproj
```

For a different SAP2000 installation:

```powershell
dotnet build MCOTiS.sln '-p:SapInstallDir=C:\Your SAP2000 folder'
```

Default executable:

`bin\Debug\net8.0-windows\win-x64\Mcotis.exe`

## Checks

```powershell
dotnet run --project tests\COT_VCF.Checks
dotnet run --project tests\COT_VCF.Checks -- --study-ui
dotnet run --project tests\COT_VCF.Checks -- --fem
dotnet run --project tests\COT_VCF.Checks -- --fem-sap
dotnet run --project tests\COT_VCF.Checks -- --quantity-study "<baseline>.sdb"
dotnet run --project tests\COT_VCF.Checks -- --quantity-connected
```

The first command runs offline workflow and guarded SAP-write tests using test doubles, including the Tab 8 quantity take-off (calculation, candidate records, source selection, the isolated read against API doubles, and the tab's loading / stale-request / connection behaviour). The second opens the WPF smoke test and checks workflow/FEM/quantity bindings. The third runs the internal FEM analytical and candidate-composition checks. The fourth compares the internal FEM with a completed SAP2000 study, like for like, from the fixture in `tests/fem-sap-benchmark` (offline; report in `.work/fem-vs-sap`). The fifth prints the quantity take-off of every candidate of a study on disk from the candidates' own text exports (offline; reads only). The sixth is live but read-only: it attaches to the running SAP2000, takes off the model open in it through the API exactly as Tab 8's *Connected model* does, prints the values as SAP2000 hands them over, and confirms that the model, its units and its lock state are unchanged. Live SAP checks are intentionally separate because they require SAP2000: `--quantity-live "<candidate>.sdb"` runs the isolated Tab 8 read on one candidate — it starts its own SAP2000 on a private copy, never attaches to a running one — and compares the result with that candidate's text export.

The checks project takes the same `-p:SapInstallDir=...` as the application when SAP2000 26 is not the installed version.

## Implementation boundaries

- UI → ViewModel → application services → SAP gateway.
- All CSI calls live under `src/Mcotis.Desktop/SAP2000` and execute through dedicated STA lanes.
- SAP units, model identity, object existence, baseline coordinates, and return codes are verified around writes.
- Recovery JSON is written before guarded write batches; failed batches attempt rollback.
- Tab 8 writes nothing: it reads candidate text exports, and where SAP2000 is needed it uses a separate instance on a private copy of the model.
