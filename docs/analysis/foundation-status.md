# 3D model and DOF compiler foundation

Implemented 2026-10-12 against baseline `c1e87ff5c88b1b00b82d9ad34d1ec037641a3c6e`.
This milestone implements native analysis configuration and topology compilation. It does not complete P1's
3D linear static engine or its UI, and does not mark any numerical formulation SOURCE_VERIFIED.

Later the same day a limited dense 3D linear static frame engine was added on top of this foundation. Its scope,
source inspection and numerical evidence are in [spatial-static-verification.md](spatial-static-verification.md);
statements below that say no numerical solver exists describe this milestone as it stood.

The first desktop connection is now **Edit → TemplateBuilding…** and its native workspace: generation, Save/Open,
Undo/Redo, DOF settings, linear static runs and result tables. See [native UI scope](../native-building-ui.md).
This does not complete the full desktop migration or change numerical verification gates.

## Repository audit and scope

The supplied [implementation plan](IMPLEMENTATION_PLAN.md) and [literature register](literature.md) are retained as
planning inputs. They were written before a code audit; their statements about uploaded textbooks are supplied
provenance, not evidence that this implementation inspected those PDFs. The original PDFs were not supplied in this
request. Equation-level review, independent review approval and OpenSeesPy comparisons remain open.

Existing `src/Mcotis.Core` already provides XYZ geometry, immutable identities, globally unique object names,
six-component restraints/releases, independent Frame/Cable/Shell objects and immutable documents. Application already
provides transactional commands, revision/dirty state, Undo/Redo and hash-linked results. Persistence already provides
atomic JSON files. These implementations are reused.

`src/Mcotis.Desktop/Geometry/PlanarFrameSolver.cs` remains the desktop's XZ frame/cable solver (three equations per used point).
`src/Mcotis.Desktop/Application/FemStudyService.cs` is its workflow boundary. No legacy solver, integration, MB or presentation
code changes are part of this milestone. The new library has no reference to the desktop application, WPF, SQLite, CSI or MathNet.

## Implemented contract

- `AnalysisSettings.ActiveDofs`: Full3D by default; XZ = UX/UZ/RY, YZ = UY/UZ/RX, XY = UX/UY/RZ; custom nonempty
  subsets use the same mask. Invalid bits or no selected component fail document validation transactionally.
- `DocumentCommands.SetSettings` owns edits. DOF changes affect the input hash and therefore result staleness;
  Undo/Redo restores the associated assumptions and result state. Names remain excluded from the numerical hash.
- `ModelCompiler.Compile(document, revision)` produces an immutable `CompiledModel` or errors and no model.
  Its ordered points and members refer to original IDs. Point DOFs sort by GUID and then UX/UY/UZ/RX/RY/RZ;
  lookup uses `(PointId, Component)`, never a name.
- Every point retains six full slots, including supports and inactive components. Only free DOFs receive contiguous
  reduced indices. Restraints remain separate from the active mask. Cable-only points do not acquire unused free
  rotations. Full nodal vectors retain signed support forces/moments; equivalent member loads are not assembled yet.
- Coordinates are never projected. When a translation is disabled, all points must have a common coordinate along
  that axis within `Tolerances.Length` (1e-6 m). Offset planes are accepted. This conservative rule also applies to
  custom masks; it is not a general constraint-transform implementation.
- Nonzero point/member loads in disabled DOFs fail compilation. Local frame loads with a reduced mask fail until
  the verified axis transform can establish compatibility. Nonzero pattern self-weight requires UZ. These checks
  cover all stored patterns, including patterns not currently scheduled to run; nothing is silently removed.
- Missing frame/cable sections, unsupported Shell geometry, disconnected points, rotation-only masks and nodal-load
  accumulation overflow produce explicit diagnostics. Inactive restraints remain stored and produce warnings.
  Mechanisms, custom-mask stiffness coupling and section stiffness validity require the future element/solver layer;
  compilation success does not assert matrix stability or availability of a solver.

Internal units remain the native document's kN, m, t (time seconds); force moments are kN·m. Do not silently convert
the document to N/kg when implementing dynamics. Local member axes, matrix bending signs, release condensation and
reaction recovery remain formulation-review tasks.

## Native migration

Schema 2 requires `settings.activeDofs`. Schema 1 did not store a mask and migrates in memory to XZ, preserving the
previous planar assumption. Files that already contain this field while claiming schema 1 are refused as ambiguous.
Open never rewrites the source. Save writes schema 2. Numerical hashes now include DOFs: any future results store
must version its formulation/hash convention instead of reusing pre-DOF hashes as current results.

The committed `tests/Mcotis.Checks/Fixtures/schema1-xz.mcotis` is a synthetic schema-1 compatibility fixture,
not a historical analyzed result. It verifies identity, geometry, six full slots per point and three free XZ
equations. `DofCompilationChecks` also covers all presets, custom masks, insertion-order independence, failed
compilation, support loads, cable rotations, immutability, migration and Undo/Redo invalidation.

## Source-to-code record

| Record | Inspected evidence | Implementation | Verification | Status |
|---|---|---|---|---|
| ALG-04 | [CSI Set Active Degrees of Freedom](https://docs.csiamerica.com/help-files/etabs/Menus/Analyze/Set_Analysis_Options/Set_Active_Degrees_of_Freedom.htm), Full 3D/XZ/YZ descriptions; inspected 2026-10-12 | `DegreesOfFreedom.cs`, `AnalysisSettings`, `ModelCompiler` | `DofCompilationChecks.PresetsAndMapping` | Behavior inspected; configuration/compiler coded |
| APP-DOF-01 | Project convention: XYZ storage, immutable IDs, native kN/m/t, six canonical joint components | Full/reduced mapping and nodal vectors in `ModelCompiler` | Deterministic mapping, support-load and snapshot checks | Application convention tested; no stiffness derivation claim |
| APP-DOF-02 | Prior schema 1 has no active DOF setting; prior analysis direction is XZ | `NativeDocumentFormat.AddActiveDofs` | Committed schema-1 fixture and sequential migration checks | Migration tested |

XY and custom masks are COTVCF requirements from the supplied plan. The inspected CSI page lists Full 3D, XZ, YZ
and No Z Rotation; it does not list an XY button. Do not attribute the XY preset to that page.

Existing numerical baseline tests remain `tests/COT_VCF.Checks/PlanarFrameChecks.cs` (analytical deflections, forces/reactions,
distributed loads, mesh, releases, cable equilibrium/sag) and the saved benchmark inputs under
`tests/fem-sap-benchmark/`. They protect the current engine. A standalone solver comparison to those results has
not happened because this milestone has no numerical solver.

## Next release gates

1. Done in part — see [spatial-static-verification.md](spatial-static-verification.md): the B1 passages for
   ALG-01/02/03/05 are inspected and recorded; reviewer approval is still missing.
2. Done for frames without cables — same record: the 3D kernel, dense assembly and solver, six legacy XZ models and
   twelve independently specified OpenSeesPy models. Still open: cables and target-force fitting, frames with
   interior joints, the default model, a sparse solver and instability diagnostics.
3. Wire staged DOF editing and a persistent analysis badge into the future standalone desktop. The current app
   does not yet expose this library or a Set Degrees of Freedom dialog.
4. Preserve separate geometric-stiffness, mass, modal and diagnostics gates. ALG-09 remains blocked pending the
   mass-based story P-Delta derivation. Do not label an invented approximation as CSI's method.

Validation commands: `dotnet build MCOTiS.sln -m:1 -p:SapInstallDir="C:\Program Files\Computers and Structures\SAP2000 27"`,
`dotnet run --project tests/Mcotis.Checks`, `dotnet run --project tests/COT_VCF.Checks`.
