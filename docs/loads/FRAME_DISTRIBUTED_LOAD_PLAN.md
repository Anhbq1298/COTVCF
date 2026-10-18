# Frame distributed loads — implementation package

Status: In progress · Base: `origin/master` at `f827d1a` (2026-10-18) · Requested by Mark, 2026-10-18.

**State (2026-10-18, branch `feature/frame-distributed-loads`)**

| Slice | State |
| --- | --- |
| FL-01 Core + methods | Done — `Mcotis.Core.FrameLoads`, record fields, validation, hash, file schema 8 |
| FL-02 Analysis | Done — projected loads and distributed moments, verified against closed forms (`docs/analysis/frame-distributed-load-verification.md`); moments are therefore solved, not refused |
| FL-03 Document owns loads | Partly — natively assigned loads survive rebuilds (`Application/NativeFrameLoads`); Replace / Delete clear the legacy mirror. Not done: faithful mapping of imported legacy directions (step 1) |
| FL-04 Dialog | Written — menu, `AssignFrameLoadWindow`, `MainViewModel.AssignFrameDistributedLoad`. **Not compiled or run yet**: build the desktop project on Windows and run its checks |
| FL-05 Display | Not started |
| FL-06 Adapters | Not started — SAP2000 / ETABS export warns that natively assigned frame loads are left out |
| FL-07 Records | Done for the slices above |

Goal (user, 2026-10-18): add **Assign → Frame → Frame Load** to the main window, defining line loads on the selected
frames **exactly as SAP2000 does** — forces or moments, global or local directions, projected or not, a 4-point
trapezoid plus a uniform load, and Add / Replace / Delete. The load is data of the frame in the native document; the
frame element carries the methods that assign, read, resolve and integrate it.

This package is split into slices FL-01 … FL-07. Each slice builds, passes the quick checks and leaves the app working,
as the standalone plan asks (`docs/mcotis-standalone-plan.md`, AGENTS.md "One central model").

---

## 1. What already exists (found on master)

| Area | File | State |
| --- | --- | --- |
| Native load record | `src/Mcotis.Core/Loads.cs` | `FrameDistributedLoad(Id, PatternId, TargetId, Direction, RelativeStart, RelativeEnd, ValueStart, ValueEnd)` — one linear segment, force only, not projected |
| Directions | `src/Mcotis.Core/Loads.cs` | `LoadDirection { GlobalX, GlobalY, GlobalZ, Local1, Local2, Local3, Gravity }` — no projected variants |
| Validation | `src/Mcotis.Core/DocumentValidator.cs` `Loads()` | target is a frame, finite values, 0 ≤ start < end ≤ 1 |
| Persistence | `src/Mcotis.Persistence/NativeDocumentFormat.cs` | `LoadDto.FrameDistributed`; schema version 7 |
| Hash | `src/Mcotis.Core/AnalysisInputHasher.cs` | hashes direction, distances, values |
| Element | `src/Mcotis.Analysis/SpatialFrameElement.cs` | `LoadVector`, `AddDistributedLoad` (3-pt Gauss, Hermite), `AddFrameLoad` (split pieces), `ActionsAt` / `Evaluate` (exact stations) — forces only |
| DOF check | `src/Mcotis.Analysis/ModelCompiler.cs` | refuses a global load on an inactive DOF; refuses local loads unless Full 3D |
| Legacy loads | `src/Mcotis.Desktop/Geometry/FemLoads.cs` | `FemMemberLoad` with `FemLoadDirection` (XZ plane only: Local1/2, X, Z, projected X/Z, Gravity, projected Gravity) |
| Legacy → native | `src/Mcotis.Desktop/Application/LegacyNativeDocumentMigration.cs` | `AddMemberDistributed` **collapses** every legacy direction to native GlobalX / GlobalZ |
| SAP import | `src/Mcotis.Desktop/SAP2000/SapGateway.cs` | `GetLoadDistributed`; ignores moments, Local 3, Y and Y-projected |
| ETABS import/export | `Application/Etabs/EtabsModel.cs`, `SAP2000/EtabsGateway.Export.cs` | same XZ subset |
| Menu | `Presentation/MainWindow.xaml` | Assign → Frame has Frame Sections…, Local Axes… only |
| Native-first assign pattern | `MainViewModel.cs` `AssignJointRestraints`, `AssignJointMass` | one document command, one Undo step, `NativeToLegacyProjection.WriteConfiguration` in the same commit |

**The main risk.** Most main-window edits still rebuild the document from the legacy configuration
(`NativeToLegacyProjection.Write` → `LegacyNativeDocumentMigration.Convert` → `ReplaceContent`). Frame loads made
natively would be wiped or collapsed to X/Z by the next such edit. FL-03 removes that path for frame distributed loads
before any UI writes them.

---

## 2. SAP2000 behaviour to reproduce

Verify each item against the installed `CSiAPIv1.dll` / CSI OAPI help before coding (AGENTS.md). The repository's
`.claude/skills/sap2000-skills/04-loads/AppliedLoads.md` shows a simplified, incomplete `SetLoadDistributed` signature;
do not rely on it.

| SAP dir code | Direction | Coordinate system | Load per unit |
| --- | --- | --- | --- |
| 1, 2, 3 | Local 1, 2, 3 | Local | member length |
| 4, 5, 6 | X, Y, Z | Global | member length |
| 7, 8, 9 | X, Y, Z projected | Global | projected length |
| 10 | Gravity (−Z; positive acts down) | Global | member length |
| 11 | Gravity projected | Global | projected length |

- **Load type**: `MyType` 1 = force [kN/m], 2 = moment [kN·m/m]; the same direction list for both.
- **Trapezoid**: up to 4 points; distance from End-I, relative (0 … 1) or absolute [m]. Linear between points, zero
  before the first and after the last. A repeated distance gives a step. Stored as one row per segment.
- **Uniform**: a separate row from 0 to 1, added on top of the trapezoid.
- **Options**: Add to existing · Replace existing (delete every distributed load of that pattern on the frame, then
  assign — the `Replace` argument of `SetLoadDistributed`) · Delete existing (`DeleteLoadDistributed`, that pattern).
  Point loads of the pattern are untouched by all three.
- **Projected**: intensity × (length of the member projected on the plane normal to the load direction) / member
  length = q · √(1 − (e₁·d)²), with e₁ the member axis and d the unit load direction.
- **Sign**: local loads along +1/+2/+3; global along +X/+Y/+Z; gravity positive downward.
- **Rigid end offsets**: distances run joint to joint. Mcotis has no rigid end zones today, so nothing to do; note it
  for when they arrive.

---

## 3. Slices

### FL-01 · Core model and frame-load methods (`Mcotis.Core`, `Mcotis.Persistence`)

Extend the native record, keeping every existing call site compiling unchanged (new members with defaults):

```csharp
public enum FrameLoadType { Force, Moment }
public enum LoadDistanceType { Relative, Absolute }

public sealed record FrameDistributedLoad(ObjectId Id, ObjectId PatternId, ObjectId TargetId, LoadDirection Direction,
    double RelativeStart, double RelativeEnd, double ValueStart, double ValueEnd) : LoadAssignment(Id, PatternId, TargetId)
{
    public FrameLoadType Type { get; init; } = FrameLoadType.Force;
    /// <summary>Per unit projected length (SAP2000 dir 7–9, 11). Global directions only.</summary>
    public bool Projected { get; init; }
    /// <summary>How the user entered the distances; the relative ones are always the stored truth.</summary>
    public LoadDistanceType EnteredAs { get; init; } = LoadDistanceType.Relative;
}
```

Add the frame-load methods (new file `src/Mcotis.Core/FrameLoads.cs`):

```csharp
public enum LoadAssignMode { Add, Replace, Delete }

/// <summary>What the Frame Load dialog sends: SAP2000's Frame Distributed Loads form.</summary>
public sealed record FrameDistributedLoadDefinition(ObjectId PatternId, FrameLoadType Type, LoadDirection Direction,
    bool Projected, LoadDistanceType DistanceType,
    ImmutableArray<(double Distance, double Value)> Points, double Uniform);

public static class FrameLoads
{
    /// <summary>The segments SAP2000 stores for one frame of length <paramref name="length"/>: one per pair of
    /// consecutive points (zero-length and all-zero segments left out) plus the uniform row 0 → 1.</summary>
    public static ImmutableArray<FrameDistributedLoad> Segments(FrameDistributedLoadDefinition d, ObjectId frameId, double length);

    /// <summary>Add / Replace / Delete on each frame, in one editor transaction. Returns the frames changed.</summary>
    public static int Assign(DocumentEditor editor, IEnumerable<ObjectId> frameIds, FrameDistributedLoadDefinition d, LoadAssignMode mode);

    public static IEnumerable<FrameDistributedLoad> Of(ModelDocument document, ObjectId frameId, ObjectId? patternId = null);

    /// <summary>q at a relative distance: the sum of the segments that cover it, zero elsewhere.</summary>
    public static double IntensityAt(ModelDocument document, ObjectId frameId, ObjectId patternId, LoadDirection direction, double relative);
}
```

`DocumentCommands.AssignFrameDistributedLoad(frameIds, definition, mode)` wraps `FrameLoads.Assign` as one history step
named "Assign Frame Distributed Loads".

Validation (`DocumentValidator.Loads()`), new messages under `load.value`:
- `Projected` only with GlobalX/Y/Z or Gravity.
- Absolute distances within 0 … frame length; relative within 0 … 1; points non-decreasing (checked in `Segments`,
  refused with the point number).
- Moment loads are valid model data; whether they can be *run* is FL-02's gate.

Persistence: schema 7 → 8. `LoadDto` gains optional `loadType`, `projected`, `enteredAs`; a missing member reads as the
default, so migration 7 → 8 only bumps the version. A version-7 reader refuses a version-8 file as today.

Hash: `AnalysisInputHasher` appends the three new fields **only when not default**, so every existing document keeps its
hash and its stored results stay current.

Checks (`tests/Mcotis.Checks`, new `--frame-loads`):
- `Segments`: 4-point trapezoid of the screenshot (0/5/… m, 0/8/… kN/m) → the right rows; step from a repeated
  distance; uniform row; absolute ↔ relative; out-of-range and decreasing points refused.
- `Assign`: Add appends; Replace removes only distributed loads of that pattern on that frame (point loads and other
  patterns kept); Delete; one Undo step restores all.
- Validator: projected on a local direction refused; existing `ModelChecks` invalid cases unchanged.
- File round trip of every new field; a version-7 file opens with defaults.

### FL-02 · Analysis: projected loads and distributed moments (`Mcotis.Analysis`)

`SpatialFrameElement`:
- `LoadVector(direction, value, projected)` scales by √(1 − (e₁·d)²) when projected (whole frame axis, not the piece:
  pieces share it).
- `AddDistributedMoment(start, end, direction, mStart, mEnd)`: consistent nodal loads by virtual work —
  torsion m₁ linear to nodes 3 / 9; bending m₂, m₃ through the derivatives of the Hermite shapes
  (F₂ terms ∫ m₃ N′ dx, M₃ terms ∫ m₃ H′ dx; mirrored for m₂). Same 3-point Gauss as forces (exact for linear m).
- `ActionsAt`: add ∫ m ds to the moment of the free body; `Evaluate` adds the segment ends as stops.
- `AddFrameLoad` passes `Type` and `Projected` through.

`ModelCompiler`: a global moment load checks RX/RY/RZ against the DOF mask (Gravity has no moment meaning other than
about −Z, i.e. RZ); local moments follow the existing Full-3D rule.

**Validate before showing (AGENTS.md).** Until the record below is complete, the run refuses a moment load with the
reason in the run log ("distributed moments are not verified yet") — never solved as something else.
New record `docs/analysis/frame-distributed-load-verification.md`:
- Closed forms: fixed-fixed and simply supported beams under UDL, triangle, trapezoid, partial span; uniform torsional
  and bending moment per length; projected load on a 30° and a 60° member (reactions = q × horizontal length).
- Archived planar solver (`--general-solver`): in-plane projected cases, already supported there.
- OpenSees: `eleLoad -beamUniform` with partial / linearly varying intensity for force cases (no distributed moments
  in OpenSees: closed forms only, said in the record).
- Full numerical suites (`--full`) on both check projects: this changes load recovery.

### FL-03 · The document owns frame distributed loads (`Mcotis.Desktop/Application`)

Without this slice a native assignment is lost on the next legacy edit.
1. `LegacyNativeDocumentMigration.Convert` maps legacy directions **faithfully** instead of collapsing them:
   Local1/Local2 → native Local1/Local2; ProjectedX/Z/Gravity → GlobalX/Z/Gravity with `Projected = true`.
   Gate: `--native-parity` stays within 1E-8 on VCF, TAS, TAS with cable, the .e2k import and VCF fixed base.
2. `NativeToLegacyProjection.Write` (the rebuild path) carries the current document's `FrameDistributedLoad`s over by
   frame identity instead of re-deriving them from `snapshot.Loading.MemberLoads`. Frames that no longer exist drop
   their loads (as `MainViewModel.Edit.cs` does today).
3. `femSapLoading.MemberLoads` becomes a read-only mirror of the document for the consumers not moved yet
   (`ObjectInformation`, `FemLoadModel` counts, `FemRunMonitor` summary, VCF optimisation candidates). Loads with no
   legacy form (GlobalY, Local3, Y-projected, moments) are listed in Load assignment warnings as "kept in the model,
   not shown in this table yet" — not dropped.
4. Same treatment for `FramePointLoad`, since Replace / Delete must not disturb them.

Checks: `--native-parity`, `--fem-model`, a new desktop check that assigns a GlobalY load natively, makes an unrelated
legacy edit (section change), and finds the load unchanged; Undo/Redo across both.

### FL-04 · Assign → Frame → Frame Load dialog (`Mcotis.Desktop/Presentation`)

Menu (`MainWindow.xaml`, under Assign → Frame, after Local Axes…):

```xml
<MenuItem Header="Frame _Load…" Click="AssignFrameLoad_Click" AutomationProperties.Name="Assign Frame Distributed Loads"
          ToolTip="Distributed forces or moments on the frames selected on the model view: SAP2000's Frame Distributed Loads — trapezoid and uniform, global, projected or local, added to, replacing or deleting that pattern's loads"/>
```

`AssignFrameLoad_Click` (MainWindow.xaml.cs): selected frames as `AssignFrameAxes_Click` does; none selected → the same
"Select one or more frames…" message; cables in the selection are left out with a note (SAP's cable loads are a
different form).

`AssignFrameLoadWindow` (new, compact engineering UI per `skills/mcotis-ui-handoff`), laid out as SAP2000's form:

| Group | Controls |
| --- | --- |
| Load Pattern | combo of the document's patterns |
| Load Type and Direction | Forces / Moments radio · Coord Sys combo (GLOBAL, Local) · Direction combo, filtered: Local → 1, 2, 3; GLOBAL → X, Y, Z, X Projected, Y Projected, Z Projected, Gravity, Gravity Projected |
| Trapezoidal Loads | 4 columns × (Distance, Load) · Relative / Absolute Distance from End-I radio · units kN/m or kN·m/m, m |
| Uniform Load | one box |
| Options | Add to Existing / Replace Existing (default) / Delete Existing |
| Buttons | OK · Close · Apply |

SAP defaults: distances 0, 0.25, 0.75, 1 (relative), loads 0, uniform 0, Replace. Delete greys out the value boxes.
The form refuses only what it cannot read (non-numbers, decreasing distances); it does not refuse a direction because
the DOF options might not run it (AGENTS.md: the run refuses and says why).

`MainViewModel.AssignFrameDistributedLoad(IReadOnlyCollection<string> frames, FrameDistributedLoadDefinition d,
LoadAssignMode mode)`, modelled on `AssignJointMass`: `CanEditFemModel` guard, names → ids through `LegacyNames`, one
`NativeDocumentSession.Execute` with `FrameLoads.Assign` + `NativeToLegacyProjection.WriteConfiguration`, then
`ClearFemResults`, `ScheduleFemWorkingSave`, `NotifySharedViewer`, `NotifyModelHistory`, and a `Message` such as
"Distributed loads (Replace) on 3 frames in LIVE (FEM results cleared; SAP2000 unchanged)."

Checks (`tests/Mcotis.Desktop.Checks`, `MainWindowModalChecks` + new `--frame-load-ui`): menu path exists and is
disabled while locked; direction list filters by coordinate system; the screenshot's input produces the expected
document rows; Apply keeps the window open; Undo removes the whole assignment.

### FL-05 · Display and Object Information

- Viewer (2D `Structural2DViewer` and 3D): when Display → Loading is on, draw each frame's distributed load of the
  shown pattern as the trapezoid outline with arrows and end values (as ETABS's elevation in the screenshot); moments as
  curved arrows. Today only joint arrows are drawn.
- Object Information: list the frame's distributed loads from the document (pattern, type, direction, projected,
  distances, values), replacing the `femSapLoading` read.

Checks: `--fem-view` (or the existing viewer checks): a known load draws the expected outline points.

### FL-06 · SAP2000 / ETABS adapters

- SAP import (`SapGateway.ReadAsync`): read every `GetLoadDistributed` row — `MyType` 1 and 2, dirs 1–11 — into native
  loads; no more "out of plane — ignored" warnings for dirs 3, 5, 8 and moments. A user coordinate system other than
  GLOBAL / Local still warns and is skipped.
- SAP export: `SetLoadDistributed(Name, LoadPat, MyType, Dir, Dist1, Dist2, Val1, Val2, CSys, RelDist = true,
  Replace, ItemType)`; first row of a frame+pattern with Replace = true, the rest false — the target ends with exactly the
  document's loads.
- ETABS import/export (`EtabsModel`, `EtabsGateway.Export`): the full direction table, including Y, Local 3, moments.
- Exports read loads from the document, not `femSapLoading`.

Checks: `--sap-import`, `--etabs-import`, `--etabs-export` stand-ins extended with a GlobalY, a Local3, a projected and
a moment load. Live SAP2000 / ETABS runs only when the user asks (AGENTS.md).

### FL-07 · Records

- `docs/DEVELOPMENT_LOG.md`: the Planned entry added with this package; each slice updates it in its own commit
  (Status In progress → Done, Checks filled in).
- `docs/USER_GUIDE.md`: "Assign → Frame → Frame Load" section with the direction table above.
- `skills/mcotis-ui-handoff/SKILL.md`: Assign menu list and the note that frame loads are document data.
- `docs/mcotis-standalone-plan.md`: FL-03 removes one consumer of the legacy loading; note it under phases 5–7.

---

## 4. Order and gates

| Slice | Depends on | Gate before merge |
| --- | --- | --- |
| FL-01 Core + methods | — | quick checks, `--frame-loads`, `--dev-log` |
| FL-02 Analysis | FL-01 | `--full` on both projects; verification record; moments refused until verified |
| FL-03 Document owns loads | FL-01 | `--native-parity`, `--fem-model`, new survival check |
| FL-04 Dialog | FL-01, FL-03 | `--frame-load-ui`, modal checks |
| FL-05 Display | FL-04 | viewer checks |
| FL-06 Adapters | FL-01, FL-03 | import/export stand-in checks |
| FL-07 Records | each slice | `--dev-log` |

FL-02 and FL-03 can run in parallel. A user can use the dialog for forces once FL-04 lands; moments appear in results
only after FL-02's verification record is complete. The Optimization and Design production baselines must not move
(`--optimization-study`, `--column-design`); FL-03's faithful direction mapping is the one place that could move them —
any difference is reported to the user before it is accepted.

## 5. Open points for the user

1. FL-03 changes how imported SAP / ETABS frame loads are stored (no longer collapsed to X/Z). Results must stay equal
   to round-off; if a baseline moves, approve or reject before merge.
2. Cables selected with frames: leave them out with a note (proposed), or add SAP's cable distributed load later as its
   own form.
3. Point loads on frames (SAP's Frame Point Loads) use the same pattern; propose it as the next form once this lands.
