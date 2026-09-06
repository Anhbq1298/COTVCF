# COT VCF tools

WPF / .NET 8 desktop application for defining structural-study candidates from an existing SAP2000 model, then building and analyzing one isolated child `.sdb` model per selected combination.

The workflow is sequential:

`SAP baseline -> loading candidates -> truss-height candidates -> cable-pretension candidates -> analyzed and verified child models`

Tabs 2–4 only configure and preview candidates. SAP2000 assignments are changed only while Tab 6 builds child models; the original baseline model is never used as an output.

## Tab 1 — SAP Model

1. Open and save the intended model in SAP2000, then click **Connect SAP**.
2. The application attaches to that existing SAP2000 instance and reads model identity, units, lock state, geometry, load patterns, and relevant objects.
3. The first connected model and its original geometry become the immutable session baseline used by every candidate.
4. **Refresh** rereads the connected model. A changed model, units, or external geometry can invalidate pending writes rather than being accepted silently.

The model viewer supports XY/XZ/YZ projection, fit/reset, zoom, pan, labels, and synchronized selection where applicable.

## Tab 2 — Loading Criteria

1. **Add Load Locations** selects joints from the cached SAP geometry. Locations are deduplicated and ordered by global X.
2. Define one or more loading rows as either a fixed `qz` or a `Min / Max / Step` range.
3. Set the out-of-plane tributary width `B` and whole-number `NStories`.
4. **View Candidates** previews every loading combination and the resulting joint forces.

For each load definition, candidate values are generated on-grid from Min through Max. A loading candidate is the Cartesian selection of one `qz` value from every definition. Tributary area is based on X-direction midpoint spacing and width `B`, with half-spacing at the end joints:

`Fz = -qz × TributaryArea × NStories`

During Tab 6 execution, each joint/load-pattern force assignment is **replaced, not added**. Existing moments on that assignment are preserved. Ambiguous multiple assignments or incompatible coordinate systems are rejected instead of guessed.

## Tab 3 — Truss Height

1. Select non-overlapping Top, Middle, and Bottom joint groups.
2. Choose the reference chord that must remain fixed.
3. **Capture Baseline** records the original height `H0` and original joint coordinates.
4. Define the candidate `Min / Max / Step` range and preview values with the slider or **View Candidates**.

Every height candidate is calculated from the immutable baseline, never from the previous candidate, so changes cannot accumulate or drift. For `ΔH = candidate height - H0`:

- Top reference: Top `0`, Middle `-0.5ΔH`, Bottom `-ΔH`.
- Middle reference: Top `+0.5ΔH`, Middle `0`, Bottom `-0.5ΔH`.
- Bottom reference: Top `+ΔH`, Middle `+0.5ΔH`, Bottom `0`.

Tab 3 previews geometry only; it does not write to SAP2000.

## Tab 4 — Pretension

1. **Add Cable** selects cable objects from the cached geometry.
2. For each cable, select or enter an existing SAP load pattern.
3. Define a fixed target force or a `Min / Max / Step` range.
4. The pretension space is the Cartesian product of the per-cable target-force candidates.

These are SAP cable **Target Force** assignments, not analysis results. During Tab 6 execution, an existing target force for the same cable/load pattern is deleted and replaced, then read back for verification. Tab 4 itself does not write to SAP2000.

## Tab 6 — Iteration Study

Tab 6 composes deterministic `Lxx_Hxx_Pxx` rows from the three candidate dimensions:

- `NL`: Cartesian loading combinations from Tab 2.
- `NH`: truss-height candidates from Tab 3.
- `NP`: Cartesian cable-pretension combinations from Tab 4.

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
7. Run every SAP2000 analysis case currently marked to run.
8. Save the analyzed model and its results.
9. Reopen and verify the persisted geometry, loading, and pretension.
10. Return the worker to the baseline before accepting another candidate.

A child becomes **Ready** only after analysis, result saving, and persisted verification succeed. Existing child files or non-empty candidate folders are never overwritten. Candidate failure is isolated where possible; a worker that cannot recover the baseline is removed. Tab 6 does not extract results yet.

### SAP2000 instance count

The Tab 6 selector is the **total SAP2000 instance count**, not an additional-worker count:

- Select `1`: use the already-connected baseline instance; start `0` additional processes.
- Select `2`: use the connected instance plus `1` additional process.
- Select `3`: use the connected instance plus `2` additional processes—exactly `3` study instances total.

The connected instance is worker 1 and processes candidates. The study pool borrows it and does not close it. Calls are sequential inside each worker and parallel across workers; the next available worker receives the next queued candidate.

## Project configuration

The COT VCF project JSON stores the configuration for Tabs 2–6 and their study selections; loading a project never writes to SAP2000. **Save Config** updates the current tab section, while **Save All** validates and saves all sections atomically. Saved configuration is checked against the connected model before it is applied.

## Build and run

Requirements: Windows x64, .NET 8 Desktop Runtime, and SAP2000 26. `CSiAPIv1.dll` is referenced from the local SAP2000 installation and is not committed.

```powershell
dotnet build COTVCF.sln
dotnet run --project COTVCF.csproj
```

For a different SAP2000 installation:

```powershell
dotnet build COTVCF.sln '-p:SapInstallDir=C:\Your SAP2000 folder'
```

Default executable:

`bin\Debug\net8.0-windows\win-x64\COT VCF tools.exe`

## Checks

```powershell
dotnet run --project tests\COT_VCF.Checks.csproj
dotnet run --project tests\COT_VCF.Checks.csproj -- --study-ui
```

The first command runs offline workflow and guarded SAP-write tests using test doubles. The second opens the Tab 6 UI smoke test and checks WPF bindings. Live SAP checks are intentionally separate because they require a running SAP2000 instance.

## Implementation boundaries

- UI → ViewModel → application services → SAP gateway.
- All CSI calls live under `src/SAP2000` and execute through dedicated STA lanes.
- SAP units, model identity, object existence, baseline coordinates, and return codes are verified around writes.
- Recovery JSON is written before guarded write batches; failed batches attempt rollback.
