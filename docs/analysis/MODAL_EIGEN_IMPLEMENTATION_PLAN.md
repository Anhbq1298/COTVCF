# MCOTiS — Modal Eigen Analysis: Detailed Engineering and Implementation Specification

> **Status:** planning and source review; **not implemented**. Created 2026-10-13 against `Anhbq1298/COTVCF` master `f80b18a3fb3e39baa67cea4f1199a6e6702a5e33`, with companion load-case architecture plan on parent branch `plan/load-case-state-architecture`.
>
> **Scope:** Native MCOTiS frame/cable FEM in `Mcotis.Core`, `Mcotis.Analysis`, `Mcotis.Application`, `Mcotis.Persistence`, and the WPF application. Same six-DOF assembly serves Full 3D and permitted 2D DOF masks. No reliance on CSI runtime. Keep MB workflows outside core.
>
> **Normative guard:** UI/DB schemas may ship independently; no numerical modal result may be shown as validated before source-by-equation inspection, benchmark verification, and reviewer sign-off. A modal case requiring an unsupported element or prestressed state must fail visibly, never switch silently to first-order elastic stiffness.

> **2026-10-13 USER REQUIREMENT UPDATE:** The modal MVP **must** include **Define → Mass Source**, automatically include structural SW as element selfmass factor 1.0, and support selectable additional load patterns with editable mass multipliers **from the first release**. Generated SW inside selected load patterns is excluded so it cannot be counted twice. The complete actionable checklist, code map and source links are in [MASS_SOURCE_IMPLEMENTATION_PLAN.md](MASS_SOURCE_IMPLEMENTATION_PLAN.md). Any earlier references here to load-pattern mass being “later” or user-toggleable selfmass are superseded by this requirement.

## 1. Design decisions / definition of done

**MVP is eigenvectors, not Ritz, response-spectrum, time history, damping, material plasticity, eigen-buckling, or CSI solver emulation.** One or more named `ModalEigen` load cases are defined at `Define → Load Cases`. They have a stable `ObjectId`, Run flag, an explicit or inherited Mass Source, requested number of modes, and a stiffness source:

1. **Unstressed / elastic**: `Kref = Ke` on current compiled model (but with the documented initial cable-activation policy).
2. **End of nonlinear case**: `Kref = Kt(state_NL)`, where parent is successfully converged nonlinear static, including named P-Delta cases. Parent is an automatic run prerequisite, even if unchecked for Run.

Modal is a **linearized eigenproblem around a reference equilibrium**, not a static load. No applied load-pattern rows; no modal "initial loads" or generalized displacement from the nonlinear source. A modal source from a P-Delta case contains its geometric stiffness **once**, not another P-Delta iteration.

Required outputs: `Mode No.`, `eigenvalue ω² [s^-2]`, `frequency [Hz]`, `period [s]`, `participation factors`, `effective modal mass / cumulative %` for physically defined global directions, normalized six-DOF mode vector per joint, mode-shape display in native 2D/3D viewer, provenance and warnings. Modal deformed shape is **arbitrarily scaled** and is not a displacement caused by loads.

**Deliverable acceptance**: a saved 2D or 3D frame/truss model can define an Eigen modal case, mass source, stiffness source; run through the existing execution/monitoring machinery; produce physically and numerically independently verified eigenpairs and mass participation; persist case definitions/results, reopen and visualize; report ill-conditioning, failed parent and zero/negative modes without inventing frequency values. Cable prestressed dynamic analysis must wait for actual frozen tangent state and validated cable formulation.

## 2. Repository audit and architecture constraints

| Concern | Actual current file(s) | What must change |
|---|---|---|
| Load-case model | `src/Mcotis.Core/Loads.cs` | `LoadCaseType` currently `LinearStatic`, `NonlinearStatic`; introduce `ModalEigen`, `ModalCaseOptions`/stiffness source under common case contract from companion plan, not a parallel modal-only model. |
| ID/name behavior | `src/Mcotis.Core/ModelDocument.cs`, `Naming.cs` | Stable `ObjectId` for cases and mass sources; include mass-source name scope and editor actions; no name-to-ID dependencies at run time. |
| Mass properties | `src/Mcotis.Core/Definitions.cs`, `Assignments.cs` | Material `MassDensity` [t/m³] and frame/cable mass modifiers already exist; add nodal mass assignments and named mass sources; keep weight vs mass independent. |
| Validator/hash | `src/Mcotis.Core/DocumentValidator.cs`, `AnalysisInputHasher.cs` | Ensure modal case has no static components, positive requested modes, resolvable mass source, valid nonlinear stiffness producer, acyclic references, mass validity and complete deterministic input hashing. |
| Compiler/DOFs | `src/Mcotis.Analysis/ModelCompiler.cs`, `DegreesOfFreedom.cs` | Reuse six-slot UX/UY/UZ/RX/RY/RZ mapping and reduced free indices; assemble independent `M` for the same compiled topology and constraints. Disable/inactive DOFs by explicit user mask; never add supports to "fix" modal instability. |
| 3D frame | `src/Mcotis.Analysis/SpatialFrameElement.cs` | Existing prismatic Euler–Bernoulli `Ke` and coordinates; add verified per-element mass operator; match frame splitting, releases and local axes. |
| Cable | `src/Mcotis.Analysis/CableElement.cs`, `StaticSolver.cs` | Include cable mass; eigen stiffness of a converged cable state must use verified active-set plus prestress/geometric tangent, not an always-active elastic bar. |
| Static/PDelta | `src/Mcotis.Analysis/StaticSolver.cs` | Currently no geometric stiffness or general full-state continuation; publish immutable `ConvergedAnalysisState` and `K_t` through companion nonlinear refactor first. Do **not** put modal algorithms inside StaticSolver. |
| Native session/results | `src/Mcotis.Application/AnalysisResultTracker.cs`, `DocumentSession.cs` | Case-execution coordinator and per-case typed outcome; modal result tracked and marked stale by model/parent/source changes. |
| WPF native | `src/Mcotis.Desktop/Presentation/NativeModelViewModel.cs`, `NativeModelWindow.xaml` | It currently calls `StaticSolver.Solve` and stores `StaticResult`; use typed dispatcher and an actual modal result tab/viewmodel. |
| WPF legacy | `src/Mcotis.Desktop/Presentation/LoadCaseWindow.xaml(.cs)`, `MainViewModel.LoadCases.cs`, `RunLoadCasesWindow.xaml(.cs)` | Existing dialog is hardwired Nonlinear Static; create type-aware modal UI, preserve one source of analysis truth in Core; don't build second eigensolver in Desktop. |
| Input files | `src/Mcotis.Persistence/NativeDocumentFile.cs`, `NativeDocumentFormat.cs`; `src/Mcotis.Desktop/Configuration/FemDatabase.cs`, `FemModelFile.cs` | Coordinate with schema 3→4 (.mcotis), SQLite 1→2 (.cotfem.db), legacy JSON 1→2 plan. No second conflicting migration/version bump if parent PR has already landed. |
| Results DB | `src/Mcotis.Desktop/Configuration/FemDatabase.cs` | Existing `run_cases` and planar results are static-only. Add typed modal tables, mass inventory, provenance and mode DOF amplitudes in separately versioned output schema. |
| Source/QA | `docs/analysis/literature.md`, `docs/analysis/IMPLEMENTATION_PLAN.md` | Extend ALG-10/11/12/14 evidence; attach `MODAL_SOURCE_QA.md`, reviewed equations and benchmark records. |

`Mcotis.Analysis.csproj` currently references **only Core**, with no eigensolver package. `MathNet.Numerics` exists in **Desktop**, not in Analysis. Any choice of dense LAPACK / MathNet / sparse eigensolver belongs in the numerical library with pinned package/version and licensing review. Do not add CSI or WPF references to numerical projects.

**Current applicability:** compiled Shell objects are refused; a shell mass/stiffness modal result is unavailable. Existing `FemSolver` older desktop adapter deliberately restricts its input to XZ; the new native workspace uses all six DOFs. Update the app's 2D adapter later, but do not invent another 2D eigensolver.

## 3. Domain schema

Proposed illustrative contract; adapt fields to companion CaseKind/State refactor and do not duplicate it:

```csharp
public enum ModalSubtype { Eigen }
public enum ModalMassMatrixForm { Lumped, Consistent } // first release: Lumped only
public enum ModalStiffnessKind { Unstressed, EndOfNonlinearCase }

public sealed record ModalCaseOptions(
    ModalSubtype Subtype,
    ObjectId MassSourceId,
    int RequestedModes,
    ModalMassMatrixForm MassMatrixForm,
    ModalStiffnessKind StiffnessKind,
    ObjectId? StiffnessCaseId,
    double? FrequencyShiftHz = null,
    double? CutoffFrequencyHz = null
);

public sealed record MassSource(
    ObjectId Id, string Name,
    bool IncludeAdditionalMass,
    ImmutableArray<MassFromLoadPattern> LoadPatternMass
) : INamedObject
{
    // In every source selfmass is included once; there is no user toggle.
    public bool IncludeElementSelfMass => true;
}

public readonly record struct MassFromLoadPattern(ObjectId PatternId, double Factor);

// Per-joint structural inertial input: translational [t]; direct rotational [t·m²].
// Six entries align with UX,UY,UZ,RX,RY,RZ.
public sealed record JointMassAssignment(
    ObjectId PointId,
    double Mux, double Muy, double Muz,
    double Irx, double Iry, double Irz
);
```

**Source of truth:** every mode lives under one native `LoadCase`, via `ModalCaseOptions`. Use `ObjectId` references for mass source and nonlinear stiffness parent, not strings. MassSource is a model definition, not a load pattern and not an output-only setting. Retain `LoadPatternType` separate from `LoadCaseType`.

**Defaults:** automatic name `MODAL` or `MODAL 2`, Eigen, first valid named mass source or a documented default `MS1`, `RequestedModes = 12` as an editable **user-facing default**, not a guaranteed number of computed modes, elastic stiffness. If the model has no physical mass, say so and refuse to run rather than assigning artificial mass. At the same time, a fresh model is allowed to define an empty MassSource for editing: invalid for *Run*, not for model persistence.

**Mass source selection nuance:** SAP2000 generally inherits the mass source of the nonlinear stiffness producer when a later linear/modal case uses its end stiffness. For MCOTiS, initially **default to inherited mass source** on nonlinear-state-based Modal, with an explicit option to override only if the implementation documents that this is an intentional MCOTiS variation; show source difference prominently in reports. On unstressed case use the model default mass source. Keep a single effective MassSourceId in the resolved immutable run snapshot.

**Compatibility rules:**
- Modal cannot have scaled static `Components`, `InitialState`, or cable target force fit. These fields are absent/inapplicable, not zeroed out silently when changing type.
- Stiffness parent must be converged nonlinear static or Initial P-Delta, not Linear Static or another Modal. Dependency must be acyclic; skipped/failed parent => Blocked.
- Requested mode count positive, capped by implementation-specific resource limit. Return requested, found and excluded counts separately.
- Selectable DOF mask is a model assumption. The valid physical modes correspond to its free DOFs, not the 6 × number of joints blindly.
- Incompatible unsupported element, mass source, or stiffness state blocks run (no guessed fallback).

## 4. Physical mass source and mass inventory

**User-facing workflow:** Define → Mass Source → Add/Modify a named source → SW read-only 1.0 (automatic element self mass) → Add SDL/LIVE/etc load patterns with editable factors → Preview source-by-source physical and free mass → select Mass Source in Define → Load Cases → Modal. Never rely on a static load pattern literally named SW to compute automatic selfmass. Additional patterns containing a generated SelfWeightMultiplier contribute only their *explicit assigned* eligible gravity loads. See [MASS_SOURCE_IMPLEMENTATION_PLAN.md](MASS_SOURCE_IMPLEMENTATION_PLAN.md).

### 4.1 Required first-release sources

1. **Element self mass** from volume × material mass density; per frame segment use `rho * A * L * MassModifier`. Existing material density unit t/m³; force kN, length m, time s so inertial translation has units t = kN·s²/m.
2. **Additional joint masses** explicitly entered for any active translation and optional direct rotational inertia. Require finite nonnegative values; reject a negative *effective* mass matrix. Physical non-diagonal joint inertial tensors require a separate validated later feature.
3. **Frame/cable additional line mass**, if enabled after source review; distinguish from element self-mass and from equivalent static gravity loads; retain mass along frame split segments without duplication.
4. **Load-pattern-derived mass (mandatory MVP)**: the user selects named additional gravity patterns and multipliers in Define → Mass Source. Interpret **explicit assigned gravity loads** only; never add pattern-generated SW again. Convert eligible downward weight / g with signed gravity directions and verified lumping. Unsupported horizontal/moment/prestress, shell or ambiguous imported loading must be reported/blocked instead of guessed. [Full checklist](MASS_SOURCE_IMPLEMENTATION_PLAN.md).

**Important**: In CSI, Element Self Mass and Specified Load Patterns can be selected together; overlapping self-weight must be avoided. CSI's SAP2000 help describes load pattern gravity conversion, signed mass before combination and warnings/clipping for negative effective mass. MCOTiS should be more conservative initially: reject invalid negative combined effective mass rather than silently clip it. See `MODAL_SOURCE_QA.md`. The mass definition must distinguish weight `W` [kN] from mass `W/g` [t], with **g = 9.80665 m/s² by default** in native SI (configurable under documented engineering convention only).

**DOF mapping:** by default an isotropic translational self-mass contributes `m` to each of UX/UY/UZ where active. **It does NOT mean mass = 3m**. Total physical mass is the sum of one distribution, while direction-specific inertial mass totals can each equal `m`. Rotational direct inertia is zero unless physically specified or provided by consistent beam mass.

### 4.2 Lumped mass MVP

Implement the physically defined lumping rule consistently with the chosen beam element: for a uniform frame element, each end receives half the `rho*A*L` translational mass. A split frame conserves total mass; never count the whole original parent frame once per piece. For cable, initial mass uses the straight chord length with a clear sag/actual-length limitation; exact catenary mass is deferred until source verified. Material `FrameModifiers.Mass` and `CableModifiers.Mass` affect mass only; `Weight` modifiers affect gravity loading, not dynamic mass.

**No "zero-mass rotation = restrained rotation" shortcut.** With nodal lumped mass, rotations often have no mass but still have bending stiffness. They must remain in `K` and be correctly statically condensed (Section 5). Nodal joints without any inertial mass but with stiffness can also be condensed; disconnected/missing-stiffness DOFs must be diagnosed, not discarded because they're light.

### 4.3 Consistent beam mass (later, verify first)

For Euler–Bernoulli interpolation `N(x)`, the element consistent translational-inertia matrix is

```math
M_e = \int_0^L \rho A\;N(x)^T N(x)\,dx
```

with the applicable axial and two bending interpolations, local-to-global transforms and physically appropriate releases. **Do not** simply attach a copied 12×12 mass matrix with mismatched local DOF order or replace bending interpolation with generic entries. The consistent mass can introduce off-diagonal translation-rotation coupling; a zero diagonal alone does not prove a physically massless DOF. For release condensation, verify dynamic release treatment (do not simply apply elastic-static condensation formula to a consistent mass without a mathematically justified reduction).

**Comparison policy:** Results from lumped and consistent mass are different valid approximations; do not make a single tolerance test assert they are numerically identical. Preserve formulation selection and result hash so old results are marked stale after switching. OpenSees `elasticBeamColumn` defaults to lumped mass and `-cMass` requests consistent mass; match these flags before comparing.

### 4.4 Mass-source QA table produced on demand

Provide:
- source names and enabled components;
- each material/section and assigned mass modifier;
- total physical element mass [t], point/extra [t], gravity-derived [t] separately;
- sum of free/translational mass by UX/UY/UZ (excluding restrained DOFs for effective modal participation);
- total vs free mass and center of mass (CoM) with documented convention;
- zero/negative/null mass issue list by joint and DOF;
- explicit duplicated-self-weight warning when self mass and selected gravity load patterns overlap;
- mass conservation when a member splits, model is renumbered, or coordinates are rigidly transformed.

## 5. Mathematics — first release

### 5.1 Eigenproblem and units

For undamped small-amplitude motion around a stable equilibrium:

```math
M \ddot{u} + K_{ref}u = 0,\qquad
K_{ref}\,\phi_i = \lambda_i M\,\phi_i,\qquad
\lambda_i = \omega_i^2.
```

```math
f_i = \frac{\sqrt{\lambda_i}}{2\pi}\quad(\lambda_i>0),\qquad
T_i=\frac{1}{f_i}.
```

Dimension: `K` translational block [kN/m] and `M` [t] imply `λ` [s⁻²]. Full matrices mix translations [m] and rotations [rad]; apply physically consistent DOF scaling for norms/conditioning rather than one global unscaled threshold.

Stiffness and mass must be **symmetric** (within justified numerical tolerance). Negative eigenvalues are not positive frequencies. Near-zero eigenvalues indicate a mechanism/rigid-body or critical state (or numerical inaccuracy); differentiate them using scaled stiffness/mass diagnostics and null vectors. No success for unhandled negative/corrupted mass or unstable source case.

### 5.2 Constrained and massless DOFs — exact algorithm for lumped M

First reduce restraints and inactive DOFs using `CompiledDof.ReducedIndex`; retain all six nodal components for output. Partition free DOFs into `d` with strictly positive diagonal lumped mass and `z` with zero inertial mass (the latter **still free structurally**):

```text
Kdd*qd + Kdz*qz = lambda*Mdd*qd
Kzd*qd + Kzz*qz = 0
```

When `Kzz` is demonstrably invertible on the appropriate stable subspace:

```math
q_z=-K_{zz}^{-1}K_{zd}q_d,\qquad
\bar K=K_{dd}-K_{dz}K_{zz}^{-1}K_{zd},\qquad
\bar M=M_{dd}.
```

The reduced generalized eigenproblem is `Kbar*v = λ*Mbar*v`; reconstruct **all** DOFs by `qz = -Kzz^-1*Kzd*v`. Use factorization solves, not explicit `Inverse(Kzz)`. If `Kzz` is singular/ill-conditioned, report joints/components and **do not** automatically constrain them or apply fictitious mass. Validate condensability with a numerical rank / inertia test, including the case where the **full K** has zero/negative physical eigenvalues but `Kzz` is still factorizable. A positive-definite-only algorithm is allowed for strictly stable MVP problems, and otherwise must report unsupported/unstable instead of suppressing modes.

For a **consistent/non-diagonal** mass matrix use a separately verified generalized symmetric semidefinite algorithm or nullspace decomposition, not this simplistic diagonal split.

### 5.3 Eigen solver API and implementation choice

Create `IGeneralizedEigenSolver` in `Mcotis.Analysis/Modal/Numerics` with `Solve(K, M, requestedCount, options, CancellationToken)`, returning eigenpairs + residual/error metrics. The initial dense solver may use a proven symmetric generalized eigen routine **only when** `Mbar` is symmetric positive definite; LAPACK `DSYGVD/DSYGV` explicitly assumes this. Pin dependency/native binary compatibility and include deterministic matrix layout/transpose interop tests. Alternative: verified Cholesky whitening of positive definite `Mbar` followed by a symmetric standard eigensolver; verify normalization/accuracy and avoid dense expansion once models become large.

**Production scale:** later sparse symmetric assembly + shift-invert/Lanczos/ARPACK-type eigensolver and sparse factorizations; no made-up performance claim. If implemented through MathNet or vendor BLAS, isolate behind interface and do not place numerical code in Desktop. Define exact resource bounds from measured memory, not arbitrary number of joints. Return "model exceeds supported dense modal size" rather than hang/crash.

**Frequency shift / cutoff:** hide or disable controls until the actual selected solver honors them; do not accept user input only to ignore it. Basic MVP obtains the lowest positive requested modes with zero/negative eigenvalues classified; do not discard nonpositive eigenpairs and relabel the next positive as Mode 1 without diagnostic disclosure.

### 5.4 Eigenpair normalization, orthogonality and checks

Choose internal mass-normalization:

```math
\phi_i^T M\phi_i=1,\quad
\phi_i^T M\phi_j\approx0,\quad
\phi_i^T K_{ref}\phi_j\approx\lambda_i\delta_{ij}.
```

Numerical residual check for every mode:

```math
\eta_i=\frac{\lVert K\phi_i-\lambda_i M\phi_i\rVert_2}
{\lVert K\rVert_2\lVert\phi_i\rVert_2
 +|\lambda_i|\lVert M\rVert_2\lVert\phi_i\rVert_2},
```

with denominator guard and error reporting where norms are zero. The final reconstructed 6-DOF mode vector must pass residual tests on the **original reduced K and M**, not merely the condensed eigensystem. Use a consistent, conditioned DOF-scaled norm because forces vs moments have differing dimensions. Tolerances are tunable engineering solver settings but derived from benchmark error growth, not indiscriminate hardcoded percent. Check M-orthogonality for distinct modes and subspace orthogonality for repeated ones.

Deterministic display convention: normalize physically with `M` internally; **for display only**, scale maximum translational component of each mode to one (where nonzero) and choose the sign so a deterministic highest-amplitude component is positive. Degenerate eigenvector bases can rotate between solves; compare subspaces/MAC, not column signs.

### 5.5 Modal participation / effective mass

For global translation direction `r_j` (unit rigid displacement of every eligible active joint in UX/UY/UZ; zero in other components):

```math
\Gamma_{ij}=\frac{\phi_i^T M r_j}{\phi_i^T M\phi_i},\qquad
M_{eff,ij}=\frac{(\phi_i^T M r_j)^2}{\phi_i^T M\phi_i},\qquad
P_{ij}=100\frac{M_{eff,ij}}{r_j^TMr_j}.
```

Cumulative = ordered sum of `P_ij`; use double precision and error bounds. If `r_j^TMr_j=0` (inactive/zero-mass direction), display **N/A**, not 0% or infinity. Fewer extracted modes imply cumulative participation **below** 100% legitimately; never renormalize partial modal mass to 100%.

For RMX/RMY/RMZ, rigid-body rotation participation is **not** simply rotational nodal inertia. It includes translational masses moving about a specified reference point (prefer physical center of mass), and any actual nodal rotational inertia. Supply `r_R` using cross-products `δu=δθ×(x−x_CM)` plus direct rotational DOFs. **MVP UI defaults to translational UX/UY/UZ**, and rotational participation can be added only after its rigid-body influence-vector derivation and reference-point convention have been independently benchmarked.

### 5.6 Initial-state stiffness selection

`Kref` selection:
- **Elastic/unloaded**: verified elastic member stiffness `Ke`, with cable prestrain/tension-only policy declared and validated. In an unstressed cable-only structure, an axial-only cable tangent must not be represented as a tensioned prestressed transverse support.
- **Converged nonlinear / P-Delta**: frozen full tangent `Kt` at prior end state, including member axial-force geometric stiffness, active cable tension/stiffness and the particular DOF constraints. Modal solves *no additional* static equilibrium and uses *no parent gravity load* as modal forcing.

Absolute prerequisite: previous plan's state snapshot + case DAG + PDelta kernel if using PDelta. If parent is not converged or has changing/invalid cable active set, stop. A short modal perturbation may have motion that would slacken a taut cable — eigen solution is *infinitesimal and frozen active set*, so communicate this limitation. If a cable is entirely slack, it has no stiffness except physically justified tangent contributions and its mass still participates.

Avoid counting the axial prestress geometric effects twice (e.g., if `Kt` already includes them, don't add another `Kg`). Verify compression lowers appropriate frequency, tension raises it, subject to boundary geometry. Approaching instability should produce small/zero/negative eigenvalues, not a solver success at some plausible artificial frequency.

### 5.7 Repeated, zero and negative modes

- `λ > tol`: stable positive eigenfrequency.
- `|λ| <= physically scaled eigen tolerance`: zero/near-zero mode; show joint DOF shape plus "rigid body/mechanism/near-critical" classification, not infinite `T`.
- `λ < -tol`: negative eigenvalue of linearized stiffness; physically an instability direction with `ω` imaginary. Report instability; frequency/period N/A.
- Indefinite `K` must not be accidentally refused solely because its Cholesky fails before diagnostics; a solver capable of inertia/eigen-sign inspection is needed, or the run must flag "unstable eigenproblem unsupported by current solver".
- Repeated eigenvalues: arbitrary orthonormal eigenbasis, mode numbers stable only by eigenvalue order; use subspace comparisons / MAC and display numerical degeneracy warnings, not assert individual mode-shape identity.

## 6. Result contracts, persistence and provenance

### 6.1 Typed result objects

```csharp
public sealed record ModalAnalysisResult(
    ObjectId DocumentId,
    ObjectId ModalCaseId,
    string InputHash,
    long Revision,
    ObjectId EffectiveMassSourceId,
    ObjectId? StiffnessProducerCaseId,
    string StiffnessFingerprint,
    ModalSolverMetadata Solver,
    ImmutableArray<ModalModeResult> Modes,
    ModalMassInventory MassSummary,
    ImmutableArray<ModalDiagnostic> Diagnostics
);

public sealed record ModalModeResult(
    int ModeNumber,
    double EigenvaluePerS2,
    double? FrequencyHz,
    double? PeriodSeconds,
    ModalEigenStatus Status,
    ImmutableArray<ModalJointAmplitude> Shape,
    ImmutableArray<ModalParticipation> Participation,
    double ScaledResidual
);

public sealed record ModalJointAmplitude(
    ObjectId PointId,
    double UX, double UY, double UZ, double RX, double RY, double RZ
);
```

Mode vectors are dimensioned amplitudes under mass normalization (and can mix translations/rotations); display scale is a visual value stored **separately** in result/view settings. Do not place modal results inside existing `StaticResult` or planar `FemAnalysisResult`, invent static reactions for modes, or pass modal case into static load-combination arithmetic.

### 6.2 DB schema: extend companion migrations, not invent parallel formats

**Native `.mcotis`:** current schema 3. Companion case-refactor plan upgrades to 4. Extend **that same 3→4 migration if not merged yet** with modal case and MassSource fields; if the schema-4 work lands first, increment to 5 with an explicit 4→5 migration. Old file must produce exactly the old static response and no silently created modal results. `FileDto` case options and MassSource DTO should use IDs. `AnalysisInputHasher` covers material rho, mass modifiers, nodal additions, mass source membership/factors, stiffness-source ID, modal options and formulation version on result metadata.

**Desktop input `.cotfem.db`:** current `FemInputSnapshot` v1; companion plan targets v2. Add normalized `mass_sources`, `mass_source_components`, `joint_mass_assignments` and modal options under `case_analysis_options` (not duplicate 1:N `load_cases`). Keep `model_snapshot` JSON authoritative until deliberate unified persistence; validate normalized projection. Rename must preserve primary ObjectId, reference IDs and case-run dependencies. `Default.cotfem.db` seeded migration tested.

**Legacy `.cotfem.json`:** current version 1; companion targets v2. Extend `FemDefinitionRowDto` and settings with typed `ModalEigen`, selected mass source ID and source-case ID; allow v1 in-memory migration; never infer that a name called "MODAL" was actually a modal case.

**Typed run output DB**, version separately: suggested structures

```sql
CREATE TABLE modal_case_runs (
  run_id TEXT NOT NULL, case_id TEXT NOT NULL,
  mass_source_id TEXT NOT NULL, stiffness_source_case_id TEXT,
  stiffness_fingerprint TEXT NOT NULL,
  requested_modes INTEGER NOT NULL, found_modes INTEGER NOT NULL,
  solver_version TEXT NOT NULL, options_json TEXT NOT NULL,
  mass_inventory_json TEXT NOT NULL,
  PRIMARY KEY (run_id, case_id)
);

CREATE TABLE modal_modes (
  run_id TEXT NOT NULL, case_id TEXT NOT NULL, mode_number INTEGER NOT NULL,
  eigenvalue_s2 REAL NOT NULL, frequency_hz REAL, period_s REAL,
  status TEXT NOT NULL, scaled_residual REAL NOT NULL,
  PRIMARY KEY (run_id, case_id, mode_number)
);

CREATE TABLE modal_joint_amplitudes (
  run_id TEXT NOT NULL, case_id TEXT NOT NULL, mode_number INTEGER NOT NULL,
  point_id TEXT NOT NULL, ux REAL NOT NULL, uy REAL NOT NULL,
  uz REAL NOT NULL, rx REAL NOT NULL, ry REAL NOT NULL, rz REAL NOT NULL,
  PRIMARY KEY (run_id, case_id, mode_number, point_id)
);

CREATE TABLE modal_participation (
  run_id TEXT NOT NULL, case_id TEXT NOT NULL, mode_number INTEGER NOT NULL,
  direction TEXT NOT NULL, factor REAL, effective_mass_t REAL,
  participating_ratio_pct REAL, cumulative_ratio_pct REAL,
  PRIMARY KEY (run_id, case_id, mode_number, direction)
);
```

Finalize referential FKs against actual versioned `run_info` and the canonical case-ID table, plus migrations and indexes during implementation. Persist numerical diagnostics and quantity units, source model hash, library/solver version, mass-matrix method, complete DOF mask, reference nonlinear case fingerprint and all warnings. Matrix `K`, `M` cache in memory only by default, not as huge JSON/SQLite blobs.

### 6.3 Cache and staleness rules

Cache key includes `document analysis hash + revision + compiled-DOF version + mass-source ID/hash + mass formulation version + modal options + source case endpoint fingerprint + solver version`. Invalidating geometry, support, active DOF, section stiffness, rho, mass modifiers, nodal/line mass, mass-source factors, parent case, and geometric formulation must invalidate affected results transitively.

A parent PDELTA case marked Run=false but needed by MODES is analyzed once and listed "Prerequisite". Failed/not-converged parent blocks modal. Mode count and display scale changes: mode count affects solve/result; display scale affects **only view**, not numerical hash.

## 7. UI and results experience

### 7.1 Define → Mass Sources

Compact data grid / Add–Modify form, matching `Presentation/Theme.xaml`:

- `Source Name` / `Default`;
- checkboxes: Element Self Mass, Additional Mass, Load Pattern Mass (latter disabled with reason until implemented);
- load-pattern multiplier rows and warning for weight/self-weight duplication;
- mass inventory preview with total element mass, free mass by X/Y/Z, CoM, zero-mass direction warnings;
- engineer can assign joint masses per point under `Assign → Joint → Mass` (not mixed with point forces), units [t] translations and [t·m²] inertias.

### 7.2 Define → Load Cases → Modal

```text
┌──────────────────────────────────────────────────────────────┐
│ Load Case Data                                               │
│ Name             MODAL                                      │
│ Load Case Type   [Modal ▼]         Subtype [Eigen ▼]         │
│                                                            │
│ STIFFNESS TO USE                                            │
│ (●) Unstressed / elastic                                    │
│ ( ) At End of Nonlinear Case   [PDELTA_GRAVITY ▼]          │
│                                                            │
│ MASS SOURCE                                                │
│ [MS1 ▼]    [Edit mass sources]                              │
│                                                            │
│ EIGENVECTOR OPTIONS                                        │
│ Number of modes [ 12 ]                                     │
│ Mass matrix [Lumped — v1]                                  │
│ [advanced frequency shift disabled until supported]        │
│                                                            │
│ Hint: free vibration only; no static Loads Applied          │
│                                  [Cancel] [OK]              │
└──────────────────────────────────────────────────────────────┘
```

Use same spacing/fonts as application; no decorative solver/source banners.

### 7.3 Run Analysis monitor

One row per requested and prerequisite case. Modal stages can be: Validating → Assembling Mass → Reducing DOFs → Extracting Eigenpairs → Validating/Participation → Finished. User-visible statuses: Pending, Running, Converged, ConvergedWithWarnings, NotConverged (numerical eigen solver), NumericalInstability (negative/near-zero stiffness mechanism), Blocked (upstream failed), Failed, Cancelled. The modal case's "Converged" means **all reported eigenpairs pass numerical validation**; it does not imply structural seismic design adequacy.

For diagnostics, reuse standard joint coordinates and UX/UY/UZ/RX/RY/RZ grid with per-DOF lost digits / specific remarks; eigen-specific issues also need mode number and eigenvalue. Never output fake `∞` digits; distinguish singularity from a finite estimate.

### 7.4 Show Results → Modal

- Mode summary: Mode, Period (s), Frequency (Hz), eigenvalue (s^-2), status, residual.
- Participation: UX/UY/UZ ratio %, effective mass [t], cumulative %, total/free mass basis, N/A if direction unavailable. A cumulative fraction is **not** normalized over only extracted modes.
- Mode Shapes: mode selector, 2D/3D mesh viewer, controlled amplifications and optional animation; slider changes visual scale only, no numerical reanalysis.
- Model overlay: original vs mode-shape geometry, selected joint six-component amplitude, active DOF mask; exact 3D coordinates retained. A modal shape is signed relative motion and has no physical absolute displacement magnitude.
- Exportable report/print includes source case and mass source, number requested/found, negative/zero modes, key warnings and independent verification status.

If there are 2D/3D workspace-specific viewers, share `ModalResultViewModel` and response structures; only the drawing adapter should vary.

## 8. Phase-by-phase development roadmap / executable PR tickets

Dependency diagram:

```text
Companion PR01 typed load cases + state DAG
            |
       MOD-01 mass domain / persistence
            |
       MOD-02 mass inventory + assembler
            |
       MOD-03 massless DOF reduction
            |
       MOD-04 symmetric eigen solver + QA
            |
       MOD-05 normalization / participation / result contracts
            |
       MOD-06 unstressed modal native workflow
            |
Companion PDelta nonlinear endpoint -----> MOD-07 prestressed / PDelta modal
            |
       MOD-08 WPF result explorer + output SQLite + compatibility
            |
       MOD-09 final regression / source approval
```

### MOD-01 — Mass source, point mass, DTOs and case configuration

**Required parallel tickets:** MASS_SOURCE_IMPLEMENTATION_PLAN.md MS-00 through MS-02. SW is fixed and on. Load patterns are selectable and persisted by ID; not deferred.
**Code:** `Core/Mass.cs` (new), `Core/Loads.cs`, `ModelDocument.cs`, `DocumentEditor.cs`, `DocumentValidator.cs`, `Naming.cs`, `AnalysisInputHasher.cs`; `Persistence/NativeDocumentFile.cs`, `NativeDocumentFormat.cs`; Desktop DTOs.
- Define MassSource as immutable named definition + per-joint assignments, modal case ID references, cycle/type validation.
- Plan migrations together with companion case plan; v1/v3 compatibility.
- Add delete/ref-integrity and rename stability.
- **Done:** an eigen case and mass source survive save/open; no solver needed.

### MOD-02 — Mass assembly and inventory

**Required parallel tickets:** MS-03 (automatic rho×A×L selfmass) and **MS-04 (selected load patterns converted to gravity mass, excluding generated SW)**. The original mass assembler task is not complete if it cannot assemble the user-selected pattern multipliers.
**Code:** `Analysis/Modal/MassAssembler.cs`, `ModalMassInventory.cs`, per-frame/cable mass adapters.
- Total mass from material rho, A, L, mass modifiers; split frames using compiled segments.
- Explicit rule for lumped frame/cable mass and nodal masses; M symmetric, PSD, units checked.
- Reject negative or non-finite mass; warn duplication; track contributed source by object ID.
- **Done:** total mass vs independent arithmetic, mesh subdivision conservation, 2D/3D invariance.

### MOD-03 — Dynamic DOF reduction and diagnostics
**Code:** `Analysis/Modal/ModalDofReducer.cs`, `ModalReductionReport.cs`.
- Separate restrained/inactive vs free-but-massless; exact lumped static condensation with `Kzz` factorization; reconstruct six DOFs; detect rank loss and mechanisms.
- Integrate existing `StabilityDiagnostics.cs` names, XYZ and DOF warning semantics.
- **Done:** EB cantilever with massless rotations matches hand / independently built reference; no imaginary support additions.

### MOD-04 — Validated eigen backend
**Code:** `Analysis/Modal/IGeneralizedEigenSolver.cs`, dense implementation, numerical residual utilities; versioned solver metadata.
- Source-check LAPACK symmetric definite contract or equivalent; pin package/interop and licensing; cancellation and memory limits.
- Sort eigenpairs, detect nonpositive eigenvalues, reproduce full eigen residuals, no silent truncation.
- **Done:** analytical 1- and 2-DOF models, more modes than inertial DOFs, repeated eigenvalues; strict failure handling.

### MOD-05 — Participation and result types
**Code:** `Analysis/Modal/ModalSolver.cs`, `ModalResult.cs`, `ModalParticipation.cs`.
- Mass normalize; compute UX/UY/UZ factors, effective/total/cumulative masses, CoM, N/A handling.
- Return immutable modal typed outcome; normalized 6-DOF vectors; deterministic display conventions separate.
- **Done:** effective mass invariance to eigenvector scale/sign, complete benchmark participation sum near 100% where applicable.

### MOD-06 — Native unstressed modal end-to-end

**Release requirement:** Define → Mass Source tab and mass inventory UI (MS-05), source-identity persistence, automatically included SW + user selected SDL/LIVE/etc patterns, with negative/unsupported/double-count safeguards. An elastic eigen-only SW implementation is an internal intermediate, **not** completion of MOD-06.
**Code:** `Application/CaseExecutionCoordinator.cs` (from companion plan), native adapter/viewmodel, UI editor, persistence.
- Modal cases on elastic frame models and 2D truss DOF presets with correctly defined masses; automatic DAG.
- Existing `StaticSolver` untouched for static numerical response except shared assembly extraction.
- **Done:** standalone 2D/3D Save/Open → Run Modal → modal results; no SAP dependency.

### MOD-07 — Modal from converged nonlinear / P-Delta / cable state
**Blocked until** nonlinear endpoint + Kt verified in companion PR-04/05.
- Resolve `StiffnessSourceCaseId` and inherit/resolve MassSource; parent run once, fingerprint; reconstruct full tangent consistent with physical state.
- Spectral frequency shift under compression/tension, pretensioned taut/slack cable active-set behavior.
- **Done:** independently specified OpenSeesPy and classical benchmark; parent failure blocks result; identical Kt makes reproducible modal results.

### MOD-08 — UI viewer + output DB
**Code:** WPF `LoadCaseWindow` modal view, `NativeModelViewModel`, `ModalResultsViewModel`, native 3D/2D viewer adapter, `FemDatabase.cs` output writers/readers.
- Mode table, participation, geometry visualization, result history; database typed mode vectors.
- UI never conflates modal shapes with static displacements; print/export.
- **Done:** reload persisted run and display same frequencies, amplitudes and warnings without re-running.

### MOD-09 — Release QA and documentation
- Complete `docs/analysis/MODAL_SOURCE_QA.md` and `literature.md` ALG-10/11/12/14 with inspected equation/pages/reviewers.
- Baseline independent comparison and cross-platform numeric consistency where supported.
- Run focused checks plus existing legacy static regressions at PR/release gate.
- **Done:** all relevant numerical tests and source approvals green; unsupported shell/large-displacement modes clearly blocked.

### Explicitly deferred tickets

- MOD-10 **only further advanced mass-source options** (e.g. explicit additional area/line mass on newly supported element types and rotational participation with reviewed CoM reference). **Load-pattern-derived gravity mass and SW deduplication are part of MVP MS-04/MOD-02**, not postponed.
- MOD-11 consistent frame mass and mass/rotation coupling, dynamic release verification.
- MOD-12 sparse shift-invert eigensolver for larger models; user frequency shift/cutoff controls then enabled.
- MOD-13 Ritz vectors; response spectrum, modal time history, damping, dynamic load combinations and design-code reports.
- MOD-14 shells, rigid diaphragms, torsionally complex link elements and material nonlinear tangent.
- MOD-15 MAC / subspace modal comparison across design revisions.

## 9. Minimal verification matrix (do not inflate unit test count)

Develop **one compact group per engineering failure class**, parameterize examples rather than writing new unit test files for every UI condition:

| Fixture | Physics/numerical case | Primary pass criterion |
|---|---|---|
| M-01 | 1DOF spring mass `k,m` | `ω²=k/m`, f/T analytical, eigen residual and normalized eigenvector. |
| M-02 | 2DOF coupled mass/spring, unequal masses | Independent analytical eigenvalues, two M-orthogonal shapes, participation sum and sign/scale invariance. |
| M-03 | 2D EB cantilever, frame distributed mass with massless joint rotations | Massless DOF condensation and reconstructed original residual; compare hand/Rayleigh and independent OpenSees using exactly matched lumped mass. |
| M-04 | 3D cantilever torsion and two-direction bending with joint masses | All six DOF maps, restraints, directionality, no spurious torsion mass; independent OpenSees match. |
| M-05 | Simple truss, 2D mask; then rotated full-3D version | Same physical frequencies for corresponding motion and mass, accepted inactive DOFs. |
| M-06 | Frame split by interior joint / frame order and rename | Conserved physical mass and invariant eigenvalues; identifiers stable after rename/save. |
| M-07 | Unsupported and mechanism conditions: no mass, negative mass, near-zero rigid-body, bad Kzz, more requested modes than inertial DOFs | Explicit error/warning and no fabricated frequencies/supports. |
| M-08 | Degenerate/repeated eigenvalue + numerically close modes | M-orthogonality, subspace comparison, no false strict column-by-column comparison. |
| M-09 | Parent nonlinear/PDelta: compression vs tension and prestressed active cable | Physically signed shift with same tangent assumption; parent-failure propagation. |
| M-10 | End-to-end project saved in .mcotis and .cotfem.db, old schema migration | Same case/mass inputs and results after reopen; stale when changing mass/parent; no regression of old static cases. |

Validation must compare only the same physical model, `rho`, mass lumping/consistent flag, element releases, restraints, pretension, geometry transformation and DOF masks. OpenSeesPy `eigen` and `modalProperties` are verification tools, **not runtime dependencies**. Their default ARPACK N−1 inertial-DOF limitation should not be mistaken for a MCOTiS mass-model error. Keep tolerances numerical/physics-specific, and separate 2% approximation differences (e.g. distributed lumped mass vs ideal continuum) from 1e-10 algebraic residual targets.

## 10. Engineering risk register / decisions

| Risk | Mitigation / required decision |
|---|---|
| Kzz singular from zero-mass/free mechanism | Fail with exact joint/DOF instead of auto restraints or small fictitious masses; physically reduce only verified condensable DOFs. |
| Incorrect mass unit `kg` vs `t` in kN–m–s | Explicit dimension checks and hand SDOF benchmark; tests verify material rho t/m³ and point mass t. |
| Counting self weight twice in mass | Distinguish MassSource flags from load cases, audit each pattern and material; warn/reject duplicate defined contribution. |
| Cable assigned elastic transverse stiffness without tension | Require validated converged cable state for prestressed tangent; unstressed slack transverse DOF can be unstable. |
| Double K_g in prestressed modal | Consume exact frozen Kt only, never append another PDelta global correction. |
| M positive semidefinite / rotational massless | Static condensation of genuinely zero-mass blocks; general consistent M requires different algorithm. |
| Eigen branch changes mode number/order | Sign selection for display only; cluster/subspace QA and repeated-mode warning. |
| User requests more modes than exist | Report requested/found/excluded, no padded fake modes; cap by inertial rank. |
| Basic modal period mistaken for seismic design | UI states free-vibration linear modes only; no response spectrum/seismic demand unless that feature exists. |
| Unreviewed numerical formula used as validated | Source-by-equation register + independent benchmarks + approval before releasing output. |

## 11. Source-backed traceability and QA policy

See `MODAL_SOURCE_QA.md` in the same folder for exact web citations, formula support, open evidence, independent benchmark requirement, and reviewers. Main numerical and behavioral sources:

1. CSI SAP2000 **Modal Form** (Eigen/Ritz differences, initial vs end-of-nonlinear stiffness): https://docs.csiamerica.com/help-files/sap/Menus/Define/Load_Cases/Modal_Form.htm
2. CSI SAP2000 **Stiffness to Use**: https://docs.csiamerica.com/help-files/sap/Menus/Define/Load_Cases/Stiffness_to_Use.htm
3. CSI SAP2000 **Mass Source** (material/additional/loads, default mass and nonlinear-source behavior): https://docs.csiamerica.com/help-files/sap/Menus/Define/Mass_Source.htm
4. OpenSees **modalProperties** (generalized eigen equation, mass, participation, rigid rotations): https://opensees.github.io/OpenSeesDocumentation/user/manual/analysis/modalProperties.html
5. OpenSees **eigen command**: https://opensees.github.io/OpenSeesDocumentation/user/manual/analysis/eigen.html
6. OpenSees **elasticBeamColumn** (`-mass`, `-cMass`): https://opensees.github.io/OpenSeesDocumentation/user/manual/model/elements/elasticBeamColumn.html
7. LAPACK **DSYGVD** (symmetric definite generalized eigen solver, requires SPD `B=M`): https://www.netlib.org/lapack/explore-html/d5/d0e/group__hegvd_gaf0bdec790208a5a56bbe0a5abcf581d0.html
8. Steven E. O'Hara & Carisa H. Ramming, *Numerical Structural Analysis* (2015), §§2.13–2.15 (eigensolvers), 3D frame stiffness Ch. 4 (per existing `literature.md`): original book equations must be inspected before claiming verification.
9. CSI Analysis Reference Manual candidate: https://docs.csiamerica.com/manuals/sap2000/CSiRefer.pdf ; equation-level review not yet completed for this plan.

**Documentation source status:** inspected public HTML behavior and OpenSees/LAPACK formula statements; **no book/CSI PDF formula has been re-inspected in this update**; this document is not a declaration that MCOTiS numerical implementation is complete or `SOURCE_VERIFIED`.

## 12. Coding-agent handoff (acceptance instructions)

1. Read `AGENTS.md`, `docs/mcotis-standalone-plan.md`, companion `LOAD_CASE_STATE_REFACTOR_PLAN.md`, `docs/analysis/literature.md`, this file and `MODAL_SOURCE_QA.md`.
2. Branch from latest compatible case-domain/persistence implementation, not the obsolete hardwired desktop form. Inspect actual symbols again before editing.
3. Implement one MOD ticket at a time; numerical kernels always inside `Mcotis.Analysis`, domain inside Core, persistence separately, WPF thin viewmodels.
4. Update schema versions **once per actual format change**, with in-memory migration tests and no data-loss silent defaults.
5. Treat all user-selected options as real engineering assumptions; if unsupported, disable selection clearly rather than accept then ignore.
6. Do not classify `M` SPD until zero-mass DOFs have been physically resolved; do not add arbitrary diagonal mass or restraints.
7. Give failures precise case, source, joint XYZ, DOF, detected issue and disposition; modal failures never produce plausible made-up `f/T`.
8. Keep only the 10 focused fixtures as an engineering acceptance suite with parametrization; run complete old static checks before merge.
9. Require independent reference comparison and explicit source approval of mass operator, massless DOF reduction, generalized eigen and modal participation.
10. Mark done only after model save→open→modal solve→results→edit parent/mass→staleness behavior works for a 2D and 3D model without CSI installed.

## 13. Integrated TODO / QA gates for Modal + Mass Source

Track all substantive work with checkboxes; use [Mass Source MS-00…MS-07](MASS_SOURCE_IMPLEMENTATION_PLAN.md) for exact code locations, matrix formulas, database schema and independent benchmarks.

- [ ] Domain: ModalEigen typed case; named/default MassSource; stable ObjectId links; automatic structural SW policy enforced in Core.
- [ ] Define → Mass Source tab (main and native workspace), including locked SW 1.0 display, additional-pattern selection, mass multipliers and source inventory.
- [ ] Persist/migrate native .mcotis, desktop .cotfem.db, legacy .cotfem.json with unchanged old static load patterns.
- [ ] Validate physical rho×A×L mass and separate gamma/static SW behavior; verify total mass unaffected by member splitting.
- [ ] Assemble eligible explicit downward gravity load assignments from selected patterns using factor/g with **all generated SW contributions excluded**.
- [ ] Negative-mass/unsupported-pattern/ambiguous-import failures show exact affected pattern/object/joint, never silent clipping.
- [ ] Massless DOF condenser, symmetric generalized eigen solver, original-matrix residuals and normalized results independently checked.
- [ ] UX/UY/UZ effective and cumulative participation derived from assembled M and actual free DOF influence vectors.
- [ ] Modal case selects Mass Source, optionally inherits parent source when stiffness is end of nonlinear case; parent failures block.
- [ ] End-to-end report includes SW contribution, additional pattern factors/masses, excluded generated SW, total/free mass and modal status.
- [ ] 2D/3D workspace runs and reopened result database reproduce eigenvalues without CSI.
- [ ] Source-by-equation QA and focused MS-01…MS-10/M-01…M-10 benchmark evidence reviewed before release.

**Release gate:** MOD-06 remains incomplete until MS-04 selectable load-pattern mass is implemented and validated. This revision supersedes the previous optional/deferred status of load-based mass.
