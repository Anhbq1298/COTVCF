# MCOTiS — Load Case, Nonlinear State, P-Delta, Modal, and Database Refactor Plan

**Planning date:** 2026-10-13  
**Repository:** Anhbq1298/COTVCF (master audited 2026-10-13)  
**Status:** IMPLEMENTATION PLAN ONLY. No solver, SQLite, or document-schema migration has been executed by this plan.  
**Scope:** Standalone MCOTiS FEM (all workspaces, native general 3D/2D by DOF selection), not MB study features. CSI integrations remain external adapters, never a numerical dependency.

## 0. Decision summary — exact user-facing behavior

Expose four choices under **Define → Load Cases → Add New Case**:

1. **Linear Static** — uses either the unstressed elastic stiffness or the tangent stiffness at the *converged end* of a named nonlinear case. A linear case applies its **own load increments only**; it does not reapply the gravity/pretension loads of the stiffness-source case. Multiple linear cases using exactly the same stiffness state may be linearly superposed.
2. **Nonlinear Static** — starts from zero or **continues from the complete converged end state** of a named nonlinear case. Loads entered in the new case are **incremental** to those already in that prior state. Supports tension-only cables, target-force fitting where physically applicable, and geometric nonlinearity **None** or **P-Delta**. Do not promise material nonlinearity, large-displacement formulation, path-dependent yielding, or buckling-postcritical tracking until separately coded and verified.
3. **Initial P-Delta** — an explicitly named, visible, runnable **load case preset** under Load Cases (e.g. PDELTA_GRAVITY), not an undocumented global stiffness toggle. It uses the *Nonlinear Static numerical engine* with geometric nonlinearity = P-Delta and user-defined load-pattern factors. It produces a converged equilibrium state and its tangent stiffness for subsequent linear static/modal cases. Designated as CaseKind.InitialPDelta to meet the UI request without duplicating a fourth solver. Ordinary Nonlinear Static with P-Delta gives the same governing formulation, but is not necessarily a designated initial stiffness state.
4. **Modal (Eigenvectors)** — eigenvalue analysis using a user-selected mass source and the unstressed stiffness or tangent stiffness from a named converged nonlinear case. No static load-pattern factors in a basic eigen case. Ritz is **not** in scope for first release.

The source distinction is important: CSI describes an initial P-Delta analysis as a **nonlinear static case named PDELTA**, and distinguishes the **geometric nonlinearity option inside that case** from the stiffness-source selection for later linear cases. A separate named MCOTiS P-Delta preset is a UX decision, **not** a claim of a separate CSI solver algorithm. See [C1, C2, C3, C4] below.

**Do not conflate two different references:**

- Nonlinear **Continue from** = resume displacements + active loads + member stresses/axial forces + pretension/cable active sets + internal history, then increment loading to a new equilibrium.
- Linear / Modal **Use stiffness from** = read a frozen tangent stiffness from the converged endpoint only; **do not** carry prior-case applied loads into the linear result. Modal reads stiffness and mass, not an incremented equilibrium.

### Worked model and run graph

~~~text
LOAD PATTERNS: SW, DEAD, LIVE, WINDX, PRETENSION
                          |
NL_PRET [Nonlinear Static; cable fit; SW+PRETENSION]
             |
             +--> PDELTA_GRAVITY [Initial P-Delta;
             |    Continue from NL_PRET; incremental DEAD + 0.25 LIVE;
             |    geometric = PDelta; save endpoint Kt]
             |          |
             |          +--> LIN_WINDX [Linear Static; use Kt of PDELTA_GRAVITY;
             |          |                 WINDX only; delta result]
             |          +--> LIN_LIVE [Linear Static; same Kt; LIVE only]
             |          +--> MODES_PD [Modal Eigen; same Kt; MassSource=MS1]
             |          +--> NL_SLS [Nonlinear Static; continue full PDELTA_GRAVITY
             |          |             state; add incremental SLS loads]
             |          +--> NL_ULS [Nonlinear Static; same parent state,
             |                        independent branch; add incremental ULS loads]
             |
             +--> NL_ALTERNATE [another independently converged nonlinear branch]
~~~

If PDELTA_GRAVITY needs **total** DEAD rather than an incremental DEAD, show the already accumulated loads before saving and calculate/enter only the difference. Never silently replace the accumulated load state. All children of a failed parent are BLOCKED, not falsely marked CONVERGED.

## 1. What is in master now (repository-specific gaps)

| Area | Actual file | Present behavior / gap |
|---|---|---|
| Native cases | src/Mcotis.Core/Loads.cs | LoadCaseType only LinearStatic and NonlinearStatic; LoadCase has Components, InitialCaseId, Run, FitCableTargetForces. No stiffness source, geometric-mode property, modal options or mass-source reference. |
| Native validation | src/Mcotis.Core/DocumentValidator.cs | Checks initial-case cycles and references; no distinct dependency semantics for stiffness consumers; no type-specific validation for modal or P-Delta. |
| Native hashing | src/Mcotis.Core/AnalysisInputHasher.cs | Hashes type, initial case, components and run flags; must hash new calculation-affecting fields and referenced mass data deterministically. |
| Native 3D static solver | src/Mcotis.Analysis/StaticSolver.cs | Dense verified/cross-checked elastic 3D frame + tension-only cable workflow, *not* geometric stiffness P-Delta. A linear case currently cannot use InitialCaseId, and is forbidden in a cable model. Nonlinear chained cases are flattened into accumulated load-pattern totals and recomputed, with target-force-fitting state reused; **this is not general transfer of a converged geometric/material state.** |
| Model compilation | src/Mcotis.Analysis/ModelCompiler.cs | General active/restrained UX/UY/UZ/RX/RY/RZ DOF indexing; preserve, do not add a planar-only solver. |
| Legacy desktop case editor | src/Mcotis.Desktop/Presentation/LoadCaseWindow.xaml(.cs) | Shows fixed type as Nonlinear Static; Continue From, Normal/Cable Target Forces; no actual type chooser, stiffness-source control, P-Delta options or modal form. |
| Legacy desktop case rows | src/Mcotis.Desktop/Application/FemLoadModel.cs | FemCaseRow.Type has no setter; FemLoadPlan.StagedCaseType hardcoded Nonlinear Static. Resolve() flattens case chains; Continue From is treated as cumulative loads. |
| Legacy desktop case VM | src/Mcotis.Desktop/Presentation/MainViewModel.LoadCases.cs | New and saved cases force Nonlinear Static; old case type/source preserved only as descriptive strings. |
| Legacy UI solver bridge | src/Mcotis.Desktop/Application/FemSolver.cs | Uses native StaticSolver but validates XZ planarity and only UX/UZ/RY for its older workspace adapter. Preserve this legacy compatibility boundary while routing new native workspaces to the 3D engine. |
| Native file | src/Mcotis.Persistence/NativeDocumentFile.cs and NativeDocumentFormat.cs | Native .mcotis JSON schema **3**; cases currently persist only type, components, initial case, fit and run. Need schema-4 migration. |
| Desktop model file | src/Mcotis.Desktop/Configuration/FemModelFile.cs | Legacy .cotfem.json version **1**; FemDefinitionRowDto cannot represent analysis-state setup; upgrade reader/writer. |
| Input SQLite | src/Mcotis.Desktop/Configuration/FemDatabase.cs | FemInputSnapshot schema **1**, .cotfem.db uses model_snapshot JSON as authoritative and normalized tables. SQL INSERT hardcodes analysis_type='NonlinearStatic' even if a row says otherwise; load_cases stores **initial_case_name** rather than an ID. |
| Result SQLite | same FemDatabase.cs | run_cases, joint_displacements, support_reactions, etc. are currently planar-only results. No modal tables, named stiffness states, six-DOF outputs or state fingerprints. |
| UI monitoring | src/Mcotis.Desktop/Application/FemRunMonitor.cs | Has Pending/Converged/NotConverged/NumericalInstability and diagnostic infrastructure; extend rather than replace. |
| Previous roadmap | docs/analysis/IMPLEMENTATION_PLAN.md and literature.md | Contains P-Delta and modal research/verification gates; this plan specifically resolves **case-vs-global setting** and **continue-vs-frozen-stiffness** semantics. |

Existing shell definitions are present, but the current static solver refuses shell analysis. Do not label a modal/P-Delta shell model supported until its stiffness/mass formulation is implemented and benchmarked.

## 2. Canonical model contract (Core) — one truth across workspaces

Extend the **native ModelDocument** rather than creating additional view-model-specific solver rules.

Recommended logical contract (design sketch, not compilable code):

~~~csharp
enum CaseKind { LinearStatic, NonlinearStatic, InitialPDelta, ModalEigen }
enum GeometricNonlinearity { None, PDelta } // LargeDisplacement only after verification
enum StiffnessSourceKind { Unstressed, EndOfNonlinearCase }
enum InitialStateKind { Zero, EndOfNonlinearCase }
enum ModalMassMatrixKind { Lumped } // Consistent mass later
record StiffnessSource(StiffnessSourceKind Kind, ObjectId? SourceCaseId);
record InitialState(InitialStateKind Kind, ObjectId? SourceCaseId);
record StaticSolveControls(int MaxLoadSteps, int MaxIterationsPerStep,
    double RelativeResidualTolerance, double RelativeDisplacementTolerance);
record ModalEigenOptions(ObjectId MassSourceId, int RequestedModes,
    ModalMassMatrixKind MassMatrixKind);
record LoadCase(
    ObjectId Id, string Name, CaseKind Kind,
    ImmutableArray<LoadCaseComponent> Components,
    bool Run, bool FitCableTargetForces,
    GeometricNonlinearity GeometricMode,
    InitialState InitialState,
    StiffnessSource StiffnessSource,
    StaticSolveControls? NonlinearControls,
    ModalEigenOptions? ModalOptions
);
~~~

The physical solver dispatch is a separate enum: LinearStatic, NonlinearStatic, ModalEigen. InitialPDelta maps to **NonlinearStatic** and enforces PDelta. Keep the public type stable via named string enum serialization; never assume enum integer ordinals survive a version change.

**Exclusivity/invariants:**

- Linear Static: source = Unstressed or EndOfNonlinearCase. InitialState = none / not applicable. GeometricMode = None; no cable-fit in linear case. May be used in a cable model **only if** reference tangent stiffness is valid, active-set frozen and all DOFs stable; no unconditional cable prohibition when sourcing a valid prestressed state.
- Nonlinear Static: initial = Zero or EndOfNonlinearCase. StiffnessSource = not applicable. GeometricMode = None or PDelta. Cable targets only in a nonlinear stage implementing the target-fit behavior.
- Initial P-Delta: nonlinear-static implementation + geometric PDelta; independent named case and optional continuation from a nonlinear parent. It is a recommended/eligible stiffness producer, not a magical global all-case load.
- Modal Eigen: stiffness = Unstressed or EndOfNonlinearCase; one MassSourceId, positive requested mode count; Components must be empty; no cable fitting. Eigenvalues on a prestressed state may be nonpositive, in which case instability is reported, not an invented real frequency.
- A producer of a nonlinear state **must be a converged nonlinear or P-Delta case**. Do not reference a linear static/modal case as a state producer.
- A selected case with Run=false may still be a prerequisite of a selected case; execute once, mark as **Prerequisite** in monitor.
- One case ID/name is unique model-wide. Rename preserves ID and references; deletion is refused or requires explicit dependency reassignment.

Use a **typed AnalysisCaseDependency** (kind = ContinueState / FrozenStiffness / Combination / MassSource), rather than repurposing InitialCaseId for three different meanings. The initial reference can be a single ID on native cases, with the new stiffness-source reference independent.

### Preset global P-Delta compatibility

The older roadmap mentions Analyze → Global P-Delta settings: None / Iterative Based on Loads / Non-Iterative Based on Mass. Treat them only as a **shortcut for creating/selecting an InitialPDelta load case** for subsequent linear/modal cases, never a second implicit geometrical stiffness correction. If both preset and explicit stiffness-source selection are offered, state the precedence in UI and prevent double application.

- None: source = Unstressed by default.
- Iterative Based on Loads: create or update a named InitialPDelta case with scaled load patterns; source it explicitly.
- Non-Iterative Based on Mass: **defer / disabled** until a sourced story/diaphragm approximation exists and has passed ALG-09; do not masquerade as member-based PDelta.

## 3. Analysis execution architecture: dependency DAG and state snapshots

Create Mcotis.Analysis.Cases containing:

- **CaseDependencyPlanner**: topological sort of user-run cases plus prerequisites, stable order by ObjectId, cycle detection, incompatible reference detection, reverse dependency index for invalidation, and separate combination evaluation (combination terms are not structural loads).
- **CaseExecutionCoordinator**: runs each case at most once per model revision; cancellation and failure propagation; case-specific progress and run status.
- **AnalysisStateSnapshot** (internal immutable): DocumentId, model revision/hash, producer CaseId, formulation version, converged displacements (six slots/joint), applied load vector/pattern factor history, support reactions, element axial/stress resultants, cable pretension and active/slack flags, internal variables, initial coordinate assumptions, **assembled converged tangent Kt**, active/restrained DOF map, convergence/equilibrium/conditioning metadata and a state fingerprint. Never mutate one snapshot to solve another branch.
- **StateRepository / stiffness cache**: keyed by (model hash, case definition/input hash, algorithm version, parent state fingerprint, numerical options). Save the status and provenance in run output; keep full Kt in memory per solve run, **do not put a potentially huge dense Kt in SQLite** by default. Reassemble deterministically on reload when needed.
- **Independent branches**: NL_SLS and NL_ULS must both clone the same converged parent, not run sequentially unless explicitly chained.

A linear case from a nonlinear state must solve:

    Kt(parent) * delta_u = delta_F_case

and recover **incremental** actions/reactions around the frozen parent. Its load-case result is delta_u, delta_member_forces, delta_reactions, and states the reference state ID. Do not automatically include parent displacements or forces in the linear case result. An optional absolute display may show parent + increment but must be visibly labelled and is **not** what additive combinations use.

A nonlinear child must solve:

    state_0 = deep immutable state(parent)
    F_target = F_parent + delta_F_child
    R(u, internal_state, F_target) = 0
    Kt = dR/du from the same declared physical formulation
    Kt * delta_u = -R  // convention: internal minus external residual

Keep and report load step history; do not flatten nonlinear chains to a single combined load vector. Old flattening may produce similar solutions for path-independent tension-only cables under small displacements, but will **not** provide correct P-Delta/history behavior generally.

**Failure behavior:** parent failed/nonconverged/unstable ⇒ dependent child BlockedByPrerequisite (no fake result). Case with an ill-conditioned tangent receives the established UX/UY/UZ/RX/RY/RZ stability table and joint coordinates, rather than silent conditioning or arbitrary support insertion.

### Cable constraints and geometric warnings

- Existing tension-only cable model is nonlinear even with geometric PDelta **off**, because its force–displacement active set is nonlinear. The solver must compute tangent from the currently active cable set.
- LinearStatic using a converged pretensioned state holds that active set and stiffness fixed; added linear load does **not** reactivate an inactive cable or independently iterate to a changed cable equilibrium. Warn in editor/tooltips.
- PDelta for frame beam-columns is distinct from a true geometrically exact cable/corotational large-rotation model. Significant cable sag/movement may require a separately validated large-displacement solver; do not claim PDelta alone covers it.
- Existing target-force fit logic should run as a nonlinear **stage-specific** operation; carry accepted pretension/initial strain values into a child. Never secretly rerun the fit at every child step.

## 4. Solver changes (after documented derivations)

### 4.1 Separate elastic assembly from state-dependent tangent

Extract from StaticSolver.cs: compiled topology, element linear material stiffness Ke, equivalent nodal loading, restraints, reaction recovery, nonlinear element residuals. Add reusable ElementState, AssembleTangent(state, mode), AssembleInternalForces(state). For PDelta frame elements:

    Kt = Ke + Kg(N, geometry, DOF convention)

Use the documented **tension-positive axial-force sign convention** (compressive N produces lateral softening, tensile N stiffening). Compute Kg in the element local axes, apply local-axis transformation, end releases and mesh mapping **consistent with the first-order kernel**. The existing energy-minimization claim of globally convex cable-only behavior must not be reused as a claim for PDelta: geometric stiffness can be indefinite near buckling. A positive-definite Cholesky-only strategy is insufficient for general near-critical geometric problems; a controlled symmetric-indefinite solver and inertia/nullspace diagnostics are a separate numerics gate.

Implementation **cannot start** until ALG-07 and ALG-08 equation-level reviews locate/derive the exact element Kg and force residual consistent with the chosen small-rotation P-Delta formulation, including how it interacts with frame releases and cable tangents. Unit consistency kN/m/t and local moment signs are compulsory. No invented CSI internal formula.

### 4.2 Nonlinear iteration

Increment load target in configurable steps. At each step use tangent Newton / modified Newton only with a documented consistent restoring-force law and convergence logic:

- relative scaled equilibrium residual (forces and moments with dimensional scaling);
- relative displacement correction (translations and rotations scaled separately);
- max iterations, max steps, line-search/adaptive stepping as supported by the chosen verified reference;
- finite force, symmetric/valid tangent, pivot diagnostics, and final independent global reaction/element equilibrium audit.

**Converged** requires both tolerances and a stable/physically acceptable state. Report NotConverged or NumericalInstability distinctly. On failure keep diagnostic history, not a successful analysis result. Reuse the app-level detailed instability grid: joint Name, XYZ, UX/UY/UZ/RX/RY/RZ digits lost and remarks; do not output infinity as a finite digit count.

### 4.3 Frozen tangent linearization

After successful nonlinear/P-Delta run, expose its Kt and baseline element state through an immutable endpoint object. A linear case on this source assembles its own delta loads and solves exactly once with Kt; it recovers incremental internal actions using the same frozen linearization (including geometric stiffness effects in consistent action recovery). **No new PDelta iteration** due to the linear case's extra loads.

A number of separate linear cases using exactly the same Kt and compatible result meaning can be added in linear combinations. Linear results using different parent states **must not be silently combined as one superposable linear scenario**; force them into separately labelled result groups or require an explicit engineering acknowledgement and forbid unsupported automatic design-combination claims. Nonlinear responses are not generally superposable; never implement naive scaling/addition of nonlinear case result vectors as though they were linear load increments.

### 4.4 Modal (Eigen), after mass-source support

Create MassSource as native named object with stable ObjectId. First release supports material self mass plus explicitly assigned nodal masses; adding load-pattern-converted mass is a gated enhancement requiring sign, gravity g and self-mass double-count QA. Define gravity convention and kN/m/t consistent SI: kN*s²/m = tonne.

On reduced active DOFs, solve

    K_reference * phi_i = omega_i^2 * M * phi_i
    frequency_i = omega_i / (2*pi), period_i = 1/frequency_i

Mass normalization phi_i^T M phi_i = 1 (chosen convention). Validate eigen residuals, mass-orthogonality, effective participating masses in UX/UY/UZ, consistent treatment of **massless rotational DOFs** and zero/negative eigenvalues. Do not directly invert a singular mass matrix; condensation or eigensolver must have a cited derivation and verification. The stiffness source is the converged parent tangent or unstressed Ke. Source case gravity loads **are not modal loads**.

A modal case returns a dedicated **ModalResult**; it must not be stuffed into FemAnalysisResult as invented forces/displacements. Modally animated shapes are normalized mode shapes, not a physical displacement result. Shell/modal masses remain unsupported until shell kernels exist.

## 5. Database and file format migration — MUST be implemented as part of feature

### 5.1 Native .mcotis (canonical long-term model)

Change NativeDocumentFormat.CurrentSchemaVersion **3 → 4**. Modify NativeDocumentFile.LoadCaseDto, NativeDocumentFormat.ToFile / ToDocument, Core.Loads, DocumentValidator, AnalysisInputHasher. Add mass source / point mass DTOs when modal implementation lands. Use a strict in-memory migration (AddLoadCaseStateConfig : 3→4); never mutate source on Open. Known schema-3 cases migrate:

- LinearStatic -> Kind=LinearStatic, StiffnessSource=Unstressed; no InitialState; Geometry=None.
- NonlinearStatic -> Kind=NonlinearStatic, InitialState=EndOfNonlinearCase(old InitialCaseId) or Zero; Geometry=None (preserves prior first-order tension-only behavior), FitCableTargetForces preserved.
- Run and Components preserved byte-for-byte in meaning; ObjectIds retained, including all references.
- No automatic “enable PDelta” on migration; no default mass-source invention; reject malformed references instead of guessing.
- New schema has explicit required mode-specific records or validated null/not-applicable fields. Unknown enum variants must fail clearly.
- Opening schema 1/2 still chains their existing migrations through schema 4; old output hashes become stale until refreshed.

### 5.2 .cotfem.db SQLite (desktop configuration)

Change FemInputSnapshot.CurrentSchemaVersion **1 → 2** and provide a **versioned reader path** for v1, then atomic v2 rewrite on Save. Currently Read reads model_snapshot JSON and validates; Write recreates the complete DB and normalized tables. Exploit that existing atomic rewrite rather than implementing fragile ad-hoc ALTER statements, but ensure old version is explicitly transformed in memory before writing.

Recommended v2 normalized design (exact SQLite syntax and PK choices finalized during implementation):

~~~sql
-- Keep existing load_cases and load_case_components, no hardcoded type:
-- load_cases(case_id PK, name UNIQUE NOCASE, analysis_type,
--            purpose, run, sequence_no, payload_json, ...)
CREATE TABLE case_analysis_options(
    case_id TEXT PRIMARY KEY REFERENCES load_cases(case_id) ON DELETE CASCADE,
    case_kind TEXT NOT NULL,             -- LinearStatic/NonlinearStatic/InitialPDelta/ModalEigen
    geometric_mode TEXT NOT NULL,        -- None/PDelta (only where applicable)
    initial_case_id TEXT REFERENCES load_cases(case_id),
    stiffness_source_case_id TEXT REFERENCES load_cases(case_id),
    stiffness_source_kind TEXT NOT NULL, -- Unstressed/EndOfNonlinearCase/NotApplicable
    mass_source_id TEXT REFERENCES mass_sources(mass_source_id),
    max_load_steps INTEGER,
    max_iterations_per_step INTEGER,
    residual_tolerance REAL,
    correction_tolerance REAL,
    requested_modes INTEGER
);
CREATE TABLE mass_sources(
    mass_source_id TEXT PRIMARY KEY,
    name TEXT NOT NULL UNIQUE COLLATE NOCASE,
    source_json TEXT NOT NULL
);
-- Optional later: point_mass_assignments / load_based_mass_components.
CREATE INDEX idx_case_initial ON case_analysis_options(initial_case_id);
CREATE INDEX idx_case_stiffness ON case_analysis_options(stiffness_source_case_id);
~~~

For v2, normalized IDs **must be the actual stable case IDs**, not names; use an ID-based link for parent/stiffness references. Existing code computes StableId using names/old IDs; upgrade carefully so a rename never destroys cross-references. Keep the JSON snapshot as the *declared authority* until a separately planned canonical-storage unification, and add consistency checks that normalized tables match it. Never silently prefer a contradictory normalized row.

Specific fixes in src/Mcotis.Desktop/Configuration/FemDatabase.cs:

- Replace INSERT load_cases (... 'NonlinearStatic' ...) with the persisted/validated case kind.
- Persist geometry mode, initial-case ID, stiffness producer ID, modal mode count, mass source and nonlinear controls.
- Leave load_case_components **empty for modal**, not a fake pattern.
- Reconstruct UI DTO rows with all case options (not only Type/Definition/InitialCase).
- Support case names on old files during migration only; resolve to stable IDs once with fail-fast ambiguous/missing reference detection.
- Update default embedded resource src/Mcotis.Desktop/Resources/Default.cotfem.db and round-trip its v1 content through the migration; do not accidentally replace user defaults.
- Validate schema version and foreign keys, and preserve database identity/revision/revision hash, lock and output-registry semantics.

### 5.3 Legacy .cotfem.json

Bump FemModelFileDto.CurrentVersion **1 → 2**. Extend FemDefinitionRowDto with optional typed CaseKind, InitialCaseId, StiffnessSourceCaseId, GeometricMode, nonlinear controls, modal options, mass source; retain old name-based InitialCase only as an import compatibility field. Existing v1 JSON becomes v2 in memory on read with the same defaults as the native migration, never silently renamed to “PDelta.”

### 5.4 Run/result database

Current run tables store UX, UZ, RY only and assume every case is static. Version the output database independently. Add:

- run_cases: stable case ID, type, calculated/prerequisite flag, status, endpoint source ID and fingerprint, formulation version, solver options/inputs hash, convergence criteria and final numerical diagnostics.
- joint_dof_results: (case_id, point_id, dof_name, displacement_or_rotation, support_force_or_moment, meaning=Incremental/Absolute) for six DOFs; migrate/adapter view for old XZ report readers.
- case_state_metadata: converged parent ID, effective Kt fingerprint, axial-force state summary, convergence and stability state, persisted input hash; **do not persist full Kt unless justified by an explicit cache format and size limit**.
- modal_modes: (case_id, mode_number, eigenvalue, omega, frequency_hz, period_s, stability_status).
- modal_vectors: (case_id, mode_number, point_id, dof_name, normalized_amplitude).
- modal_mass_participation: (case_id, mode_number, direction, effective_mass, percentage, cumulative_percentage).
- Existing frame/cable response tables remain for static cases; store incremental/absolute meaning explicitly; a modal case has no fabricated frame actions.

Never show stale cases as current after geometry, DOF, masses, load components, parent reference, material properties or formulation version changes. A parent change invalidates its dependent descendants transitively.

### 5.5 Example serialized case configurations (illustrative only)

~~~json
[
  {
    "name": "PDELTA_GRAVITY", "caseKind": "InitialPDelta",
    "initialState": {"kind": "Zero"},
    "geometricMode": "PDelta",
    "components": [{"pattern": "DEAD", "factor": 1.0}, {"pattern": "LIVE", "factor": 0.25}]
  },
  {
    "name": "WIND_X", "caseKind": "LinearStatic",
    "stiffnessSource": {"kind": "EndOfNonlinearCase", "caseName": "PDELTA_GRAVITY"},
    "components": [{"pattern": "WINDX", "factor": 1.0}]
  },
  {
    "name": "MODES_PDELTA", "caseKind": "ModalEigen",
    "stiffnessSource": {"kind": "EndOfNonlinearCase", "caseName": "PDELTA_GRAVITY"},
    "modal": {"massSource": "MS1", "requestedModes": 12}
  },
  {
    "name": "NL_ULS", "caseKind": "NonlinearStatic",
    "initialState": {"kind": "EndOfNonlinearCase", "caseName": "PDELTA_GRAVITY"},
    "geometricMode": "PDelta",
    "components": [{"pattern": "ULT_INCREMENT", "factor": 1.0}]
  }
]
~~~

NOTE: these examples use *names for readability only*; actual files/database dependencies must use immutable ObjectIds.

## 6. ViewModels and UI UX plan

**Define → Load Cases** grid columns: Case Name | Type | Geometric Nonlinearity | Starts From / Stiffness From | Loads or Mass Source | Run | Status. Preserve app-level typography/layout and existing Load Pattern editor.

**Modify/Show Case** is a type-switching form, not the current fixed Nonlinear Static modal:

- Common: name, type, notes, Run, dependency hint.
- Linear Static tab: “Stiffness to use” [Unstressed / At End of Nonlinear Case]; prior-case selector only when second choice; scaled patterns grid.
- Nonlinear Static tab: “Initial conditions” [Zero / Continue from End of Nonlinear Case]; geometric [None / P-Delta]; load steps, max Newton iterations, convergence tolerances; incremental pattern grid; optional cable-force fitting.
- Initial P-Delta tab: same nonlinear solver settings, locked geometric=P-Delta; label “named initial P-Delta case”; default load examples (DEAD + fraction LIVE) **not auto-applied without user selecting patterns**.
- Modal tab: Eigenvectors, Mass Source selector, requested modes, stiffness source; omit the static Loads Applied grid. Tooltip “Results are mode shapes/frequencies, not applied-load displacements.”
- When a parent is selected, show a preview of parent load factors/pretension state and clearly label **Incremental loads** in nonlinear children and **Stiffness only — prior loads excluded** in linear/modal children.
- Validate cycles, wrong-type producer, missing parent or mass source inline; allow editing source case even if Run=false and show it will be solved as prerequisite when needed.
- “Run Load Cases” computes DAG, lists dependencies and outcomes. No source/solver banner in monitor; display case names/types, convergence 0 residual or clearly formatted residual, and joint/XYZ/digits diagnostics for unsafe DOFs (UX UY UZ RX RY RZ).
- Modal result tab: modes, frequencies/periods, participation and mode shape visualization. Static result tab stays on static cases. Preserve the “not converged” vs “numerically unstable” distinction.
- In native workspace default from ModelDocument.LoadCases; old desktop UI must become an adapter over that contract rather than a competing authoritatively editable case definition.

## 7. Phased implementation and deliverables — in dependency order

### PR-01 — Typed case domain + dependency planner (NO numerical changes)
Files: Core/Loads.cs, ModelDocument.cs, DocumentValidator.cs, DocumentEditor.cs, AnalysisInputHasher.cs; new Application/CaseDependencyPlanner.cs.
- Add four user-facing kinds, typed option records and modes; type-specific validation.
- Stable ID references, DAG topological scheduling, cycle/wrong-type/failed-prerequisite handling.
- Keep existing static cases valid through migration. No dummy “modal solved” implementation.
**Gate:** can define and validate PDELTA → WIND/MODES and nonlinear chained branches at domain level.

### PR-02 — Schema migrations + persistence (NO numerical changes)
Files: Persistence/NativeDocumentFile.cs, NativeDocumentFormat.cs; Desktop/Configuration/FemDatabase.cs, FemModelFile.cs, Resources/Default.cotfem.db; associated DTOs.
- Native schema 3→4; SQLite input schema 1→2; legacy JSON 1→2; deterministic snapshots.
- Normalize case analysis options/mass source model; fix hardcoded nonlinear type and name-based references.
- Read-old/write-new roundtrip; no loss of run flag, target force fit or component factors.
**Gate:** old native and desktop models still open, save, reopen, and give same old static results.

### PR-03 — Case editor + run orchestration (feature-flag unsupported calculations)
Files: LoadCaseWindow.xaml(.cs), MainViewModel.LoadCases.cs, RunLoadCasesWindow.xaml(.cs), FemLoadModel.cs; new CaseExecutionCoordinator.
- Type-dependent form, parent selectors and diagnostics, DAG monitor, isolated result-type interfaces.
- Remove flattening as a **future solver contract** without breaking the legacy static compatibility adapter.
- PDelta/Modal controls may be visible as “Unavailable — numerical verification pending” until PR-04/05.
**Gate:** PDELTA → LIN_WIND DAG is planned correctly, not incorrectly run as cumulative loading.

### PR-04 — P-Delta numerical kernel + nonlinear endpoint state
Files: Mcotis.Analysis/SpatialFrameElement.cs, StaticSolver.cs, CableElement.cs; new GeometricStiffness.cs, NonlinearState.cs, CaseStateRepository.cs.
- Equation review ALG-07/08 first, then verified Kg + consistent restoring force, Newton steps, full state transfer.
- Nonlinear from zero/previous case, cable stage continuation and independent branching; no more just load flattening.
- Capture validated Kt only after convergence; detect failed/negative/near-singular modes.
**Gate:** first-order case unchanged with Geometric=None; compression/tension PDelta benchmark and actual state continuation pass.

### PR-05 — Linear static using source endpoint Kt
- Compute incremental linear responses with frozen Kt, keep parent loads out of result.
- Allow cable-stiffness frozen states when valid; disallow false superposition across incompatible sources.
**Gate:** WINDX independent linear loads share a Kt and superpose; nonlinear child results do not masquerade as linear.

### PR-06 — Mass source + Modal Eigen
Files: Core mass definitions + compiled mass + generalized eigensolver + modal result/presentation.
- Source reviewed for mass matrix/eigen/participation; one mass-source backend, no fabricated shell masses.
- Modes from unstressed or prior converged PDelta/nonlinear state.
**Gate:** analytical SDOF/two-DOF and independently specified prestressed modal comparison; stability diagnostics for zero/negative eigenvalues.

### PR-07 — Full desktop result DB, monitoring and compatibility hardening
- Result DB v2 with six DOF and modal tables; case history, dependency state and hashes.
- Old XZ/static output and default workspace still work, legacy MB untouched.
- Remove duplicated case rules/extra transformations gradually once native workspace coverage is proven.
**Gate:** end-to-end save → reopen → run DAG → results → edit source → correct stale descendants.

### Post-MVP / research-gated
- Story-mass approximate PDelta: blocked until exact valid source and known floor/diaphragm definitions.
- Large-displacement corotational cable/beam, material plasticity, geometric imperfections, eigen buckling, shells, sparse production solver: independent verification gates, **not implied by this plan**.

## 8. Minimal verification suite (keep vibe-coding feedback short)

**One focused test fixture per behavior class; reuse existing spatial/cable regression benchmarks.** Do not duplicate the dozens of broad desktop tests into every new feature.

1. **CaseDAG**: one graph tests prerequisites, two branches, cycle and failed-parent propagation.
2. **SchemaRoundTrip**: one parameterized fixture covering native v3→v4, input SQLite v1→v2, legacy JSON v1→v2; IDs and load factors remain unchanged.
3. **PDeltaSignAndTangent**: 2D/3D beam-column compression softens, tension stiffens, verified matrix symmetry and finite-difference residual derivative.
4. **NonlinearContinuation**: child load is incremental, parent pretension/stress retained, independent branches do not contaminate each other.
5. **FrozenStiffness**: linear perturbation under saved Kt, no parent load in response, valid identical-Kt superposition and incompatible-source rejection/warning.
6. **CableActiveSet**: nonlinear state transfer and deliberate frozen inactive cable behavior in sourced linear case.
7. **EigenSmallSystems**: SDOF/two-DOF frequencies, residual, normalization and participation.
8. **ModalPrestress**: prestress/axial-load frequency shift with exactly equivalent independently defined OpenSeesPy (or analytical) benchmark.
9. **Instability**: singular model plus compression-induced ill-conditioning; per-joint six DOF digits and no successful result on failure.
10. **EndToEnd**: a real saved default VCF/load-case DAG reaches correct result type, completes/reports blocks, and marks descendant result hashes stale on parent change.

Run fast unit group during iteration; existing complete legacy regression and optional independent OpenSeesPy comparison as PR/release gates, not every edit. Numerical source approvals remain mandatory even if tests pass.

## 9. References and mandatory formula QA register

**Primary CSI behavior checked 2026-10-13.** Pages below describe external behavior but do not, by themselves, certify implementation of the internal numerical algorithm.

- **[C1] CSI SAP2000 “Stiffness to Use”** — exact difference between unstressed and end-of-nonlinear stiffness, exclusion of parent loads in sourced linear responses, same-stiffness linear superposition, caution about frozen tension-only active sets. https://docs.csiamerica.com/help-files/sap/Menus/Define/Load_Cases/Stiffness_to_Use.htm
- **[C2] CSI ETABS “Initial Conditions”** — full nonlinear continuation preserves displacement/stress/loads/energies/history; new loads are incremental; nonlinear sequences branch freely. https://docs.csiamerica.com/help-files/etabs/Menus/Define/Load_Cases/Initial_Conditions.htm
- **[C3] CSI SAP2000 “Nonlinear Static Form”** — named nonlinear static case can establish initial PDelta stiffness and analyze cables. https://docs.csiamerica.com/help-files/sap/Menus/Define/Load_Cases/Static/Nonlinear/Nonlinear_Static_Form.htm
- **[C4] CSI SAP2000 “Geometric Nonlinearity Parameters”** — None, PDelta, PDelta+Large Displacements; PDelta is partial second-order and not full corotational large rotation. https://docs.csiamerica.com/help-files/sap/Menus/Define/Load_Cases/Static/Nonlinear/Geometric_Nonlinearity_Parameters.htm
- **[C5] CSI SAP2000 “Modal Form”** — Eigenvectors vs Ritz; modal stiffness may be from end of nonlinear case. https://docs.csiamerica.com/help-files/sap/Menus/Define/Load_Cases/Modal_Form.htm
- **[C6] CSI ETABS “Initial P-Delta Analysis”** — load-based iterative versus mass-based approximate story method and restrictions. https://docs.csiamerica.com/help-files/etabs/Menus/Analyze/Set_Analysis_Options/Initial_P-Delta_Analysis.htm
- **[C7] CSI SAP2000/ETABS “Analysis Reference Manual”** — mathematical reference candidate, inspect **precise printed page/equation** before coding. https://docs.csiamerica.com/manuals/sap2000/CSiRefer.pdf
- **[O1] OpenSees PDelta transformation** — independent formulation/comparison reference; not a declaration of CSI numerical identity. https://opensees.github.io/OpenSeesDocumentation/user/manual/model/geomTransf/PDelta.html
- **[B1]** O'Hara and Ramming, *Numerical Structural Analysis*, 2015, chapter 5 §§5.8–5.11 geometric stiffness, pp. 251–264 (as recorded in existing literature.md). Require original rendered equations before transcription.

Required additional source records in docs/analysis/literature.md: CASE-02 (complete nonlinear state continuation), CASE-03 (frozen tangent reference), ALG-07 (frame Kg incl. sign/releases), ALG-08 (consistent nonlinear Newton formulation), ALG-10/11/12 (mass/eigen/participation), PERSIST-01 (file and DB schema migration). Each entry: equation/passage + assumption + code method + one benchmark + independent reviewer/date + evidence status.

**Do not claim SOURCE_VERIFIED based on the present design.** For any algorithm not covered by a verified formula and independently reviewed benchmark, implement the schema and UX first but leave Run disabled with an actionable message.

## 10. Acceptance definition / exact hand-off instruction

The feature is finished only when a user can:

1. Define the four load-case choices, edit them, save them in native/SQLite files and reopen all old configurations without losing meaning.
2. Define an initial PDelta case, see its nonlinear convergence, and use its **converged end stiffness** in separate Linear Static and Modal cases without accidentally including original applied loads twice.
3. Define nonlinear cases continuing from another nonlinear case, including multiple independent children, and see load accumulation, parent state and new incremental loads distinctly.
4. See automatic prerequisite scheduling, clear failure propagation, convergence and per-joint six-DOF instability diagnostics.
5. Run identical workflows in a 3D native workspace and an appropriate 2D model by DOF selection; not require SAP2000.
6. Verify quantitative outputs against inspected sources and independent benchmarks, with no silent fallback to a first-order/static approximation when PDelta/modal is requested.

**Recommended start:** PR-01 then PR-02 are the immediate high-value low-risk work. PR-04 must remain blocked until geometric stiffness and nonlinear restoring force derivations pass the literature gate. Avoid one massive refactor PR.
