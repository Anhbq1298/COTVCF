# COT VCF tools

WPF / .NET 8 application for the COT_VCF workflow: connect to SAP2000, inspect geometry, configure point loads, preview truss height, configure cable forces, and validate inputs.

## Run

Requires Windows x64, .NET 8 Desktop Runtime, and SAP2000 26. The build references the installed `CSiAPIv1.dll`; no CSI binaries are committed.

```powershell
dotnet build ConsoleApp1.csproj
dotnet run --project ConsoleApp1.csproj
```

For a different SAP installation location:

```powershell
dotnet build ConsoleApp1.csproj '-p:SapInstallDir=C:\Your SAP2000 folder'
```

Executable: `bin\Debug\net8.0-windows\win-x64\COT VCF tools.exe`.

## Workflow

1. Open the intended model in SAP2000. Click **Connect to SAP2000**, then **Get geometry / Refresh**. The application only attaches to an existing instance.
2. In **Point loading**, add points and choose existing load patterns. Enter global Fx/Fy/Fz in the displayed force unit. **Apply** replaces one unstepped global force assignment for each point/pattern, preserving its moments. Multiple or non-global existing assignments are rejected for review in SAP2000.
3. In **Truss height**, select non-overlapping top, middle and bottom groups. **Capture baseline** measures the height between level top and bottom chords. Set minimum, maximum and increment, then use the slider or preview-height field. Top points remain fixed; middle and bottom points move down by half and all of the height change. **Reset preview** returns to the last committed height. **Apply geometry** commits the preview while retaining the original baseline in memory.
4. In **Cable force**, add cable/tendon objects. Cable values identify the shape-tension component returned by CSI. Tendon values identify the prestress assignment; stress-based assignments explicitly use stress × section area. These are configuration forces, not solved analysis results. Edit ranges and targets without writing to SAP2000.
5. In **Iteration**, review the summary and **Validate inputs**. **Run iteration** is intentionally disabled; no optimization or analysis loop is implemented.

The viewer supports XY/XZ/YZ projections, wheel zoom, drag pan, fit/reset, and a fixed coordinate indicator. XZ is the initial plane for this workflow. Select rows with Ctrl/Shift; Delete removes configuration rows and Ctrl+C copies them. Arrow keys adjust engineering numeric inputs; Home fits a focused viewer.

## State and Apply behavior

- UI → ViewModel → application workflow → SAP gateway. All CSI calls live under `src/SAP2000`; calls run on a dedicated STA dispatcher.
- Current SAP geometry, captured baseline, and proposed geometry are separate. Opening tabs does not query SAP.
- SAP present units are preserved. Changed units or model identity block writes. Start a fresh application session to configure a different model/unit system.
- Refresh preserves selections and ranges. Missing objects are flagged; external geometry changes invalidate the captured baseline. Point-load rows return to pending status because external load edits may have occurred.
- Models must be saved and unlocked before Apply. The application never unlocks, saves, closes, or launches SAP2000.
- Geometry Apply checks object names, connectivity, baseline coordinates, the Global coordinate system and point merge tolerance before modifying points.
- Recovery JSON files are written to the executable's `Recovery` directory before writes. Failed batches attempt to restore all attempted assignments/coordinates. If recovery fails, the error lists the affected operations and recovery file. Recovery files support manual review; an automated restore command is not implemented.

## Checks

```powershell
dotnet run --project tests\COT_VCF.Checks.csproj
dotnet run --project tests\COT_VCF.Checks.csproj -- --sap-read
dotnet run --project tests\COT_VCF.Checks.csproj -- --ui-read
```

The first command checks baseline math, validation, preview/commit separation, refresh behavior, and SAP write behavior using API test doubles. The last two require a running SAP2000 instance; both only read SAP data. UI checks briefly open all five screens and save renders under `.work`.

Verified locally against SAP2000 26.3.0: 41 points, 56 frames, 16 cables and 3 load patterns in kN/m. All 16 cable force sources were readable. Live SAP writes have not been exercised on the user's model; rollback and write guards are tested with API doubles. Tendon extraction is implemented against CSI documentation but this live model contains cables only.

## Scope and references

This is an initial configuration application, organized into Presentation, Application, Domain, SAP2000, Geometry and Infrastructure namespaces in a single project. Full session save/load, general undo/redo, automated baseline restoration, optimization, solved-result extraction and object selection in the viewer remain future work. Truss geometry currently requires level top/bottom chords; non-level chord mappings are rejected rather than inferred.

Implementation follows `.claude/skills/structural-engineering-ui/SKILL.md`, `design-system/structural-engineering/MASTER.md`, and the project SAP skill guidance on boundaries, units and return codes. Actual signatures and parameter meanings were verified against the installed `CSiAPIv1.dll` and `CSI_OAPI_Documentation.chm`: several illustrative skill signatures differ from the installed API. In SAP2000 26, `GetPresentUnits_2` returns `-100`; the gateway uses the supported `GetPresentUnits` enum instead.
