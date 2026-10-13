# Mcotis — General 3D, DOF Selection, Global P-Delta, Eigen Modal

Repository note (2026-10-12): this is the supplied planning specification. See [foundation-status.md](foundation-status.md)
for audited module names, implemented scope and verification evidence. Work targets the standalone library projects under `src/` (`Mcotis.Core`, `Mcotis.Analysis` …);
native units remain kN/m/t, Cable remains its own object, and the existing desktop/MB behavior is preserved.
The model/DOF compiler foundation is coded, and a dense 3D static engine for frames and tension-only cables is coded
and cross-checked ([spatial-static-verification.md](spatial-static-verification.md),
[cable-static-verification.md](cable-static-verification.md)); shells, a sparse solver, diagnostics, P-Delta, modal
analysis and reviewer approval of the source records remain open. On 2026-10-12 the planar solver was archived and
the application was put on this engine.

Status: TECHNICAL IMPLEMENTATION SPECIFICATION / NOT YET IMPLEMENTED

Scope: FEM core, analysis configuration, independent validation. Excludes MB tab.

Source gate: No algorithm may be marked implemented/validated without an equation-level source record and tests in `literature.md`.

## A. Invariants and architecture

- Core domain is 3D XYZ irrespective of chosen plane; 2D XZ is a **DOF preset**, not a separate matrix assembly engine. Preserve existing results through migration/regression tests.
- Stable immutable internal IDs plus user-visible globally unique `Name` for Points, Frames, Shells; validate namespaces, rename references transactionally. Distinguish application objects, meshed analysis elements, and solver DOFs.
- Architecture: `Domain` (objects, load patterns/cases, mass source, constraints) -> `ModelCompiler` (validation, active DOF mask, analytical mesh, element mapping) -> `ElementLibrary` (K_e, M_e, K_g, force recovery) -> `SparseAssembler` -> `Solvers` -> `Postprocessor` -> `Diagnostics`; versioned immutable compiled model / results.
- Plugins/adapters: OpenSeesPy test harness and optional CSI import/export outside FEM core; presentation ViewModels independent of solver and domain. No SAP/ETABS runtime dependence.
- Source references and implementation annotations must map to code and tests; no copied proprietary algorithms/code. Public CSI help is behavior specification, not a published mathematical derivation.

## B. P0 — Establish baseline and source traceability

1. Pin an existing repo revision, inventory model schema, mesh/DOF conventions, solver and UI entry points; **repository code has not yet been audited in this document**.
2. Save 2D XZ example models/results as immutable golden test fixtures, including tension, shear, bending, distributed forces, springs, releases and reactions.
3. Catalog sources in `literature.md` and capture exact equations/figure numbers from O'Hara & Ramming §§4.1–4.13, 5.1–5.13 and Rugarli Chs 3–9, 11–13. Independently verify any transcribed sign convention and formula via a worked example.
4. Establish controlled units (SI internal; user display units optional), sign conventions, global/local coordinate policy, tolerance policy and versioned result snapshots.

Exit: Published conventions, baseline fixtures, first source-to-code traceability matrix.

## C. P1 — General 3D engine, Analyze > Set Degrees of Freedom

### Model and topology
- `Point`: Name, XYZ, restraints, masses, springs/links; `Frame`: Name, I/J references, section/material, orientation vector/roll, offsets, end releases; `Shell`: Name, ordered corner references, thickness/material, local axes (initial schema; shell kernel separately gated).
- Full 3D frame nodal DOF order: `[UX, UY, UZ, RX, RY, RZ]`. Truss uses translational DOFs; elements declare `RequiredDOFs` and compatible/optional rotations. Disallow deleting arbitrary DOFs that make element/constraint mappings invalid without a clear diagnostic.
- Valid presets: Full 3D = all 6; XZ = UX UZ RY; YZ = UY UZ RX; XY = UX UY RZ. Custom may select any subset **only after compatibility validation**. On disabling a DOF, transform/omit disallowed prescribed loads with explicit warnings; do not silently discard forces or restraints.
- Analysis DOF mask is a property of *analysis configuration*, distinct from an object-level support/release. Physical modelling outside an active plane should trigger validation rather than silent projection.
- Map constrained/free DOFs deterministically: `DofIndex(NodeInternalId, component) -> integer or inactive`; mathematical Dirichlet elimination or equivalent proven constraint transformation; preserve external nodal forces for full equilibrium/reactions.

### UI
`Analyze > Set Degrees of Freedom` modal: Full 3D (default), XZ Plane, YZ Plane, XY Plane, Custom; 6 checkboxes UX..RZ; read-only affected element count, warning preview, reset, Apply/Cancel. A persistent 3D/plane badge in modelling and analysis results. Plane view is a camera/view choice; plane DOF is an analysis assumption. Save with model, retain migration mapping.

### 3D frame formulation
- Source-controlled 12×12 Euler–Bernoulli spatial frame local elastic stiffness; 3D direction cosine transformation `K_global = Tᵀ K_local T`; `TᵀT ≈ I`, determinant proper orientation; explicitly match local torsion and bending signs.
- Frame end release through verified stiffness partition/static condensation; never simulate a hinge with nearly zero ad hoc member stiffness.
- Non-collinear third-axis construction must handle vertical/aligned members reliably. Zero-length frame, undefined section, invalid axes rejected pre-assembly.
- Assemble free stiffness and full load vector with equivalent member nodal loading and fixed-end actions. Reaction recovery from **uncondensed** assembled equations.

### Tests
Exact 3D cantilever axial, torsion, strong/weak bending; 3D inclined frame; arbitrary rigid transformation invariance; 2D XZ legacy bitwise/tolerance regression; custom DOF incompatible model; releases; load consistency; joint and global moment equilibrium. Compare equivalent OpenSeesPy elasticBeamColumn model *after* matching shear model, offsets, releases, orientation and mass.

Exit: Complete 3D linear static functionality and stable legacy XZ migration.

## D. P2 — Solver and ETABS-style Instability Diagnostics

- Both `Standard (diagnostic)` and `Sparse (performance)` solve the same compiled matrices; the difference is reporting depth and permitted factoring method, **not physics**. Standard + Show All Detected Instabilities is first release default for debugging.
- Precheck: component graph, isolated/duplicate-coincident but unmerged joints, zero/invalid section, missing restraint, over-release, conflicting imposed DOFs, nonfinite or inconsistent units, unusual stiffness ratios.
- Matrix layer: check symmetric residual `||K-Kᵀ||`, diagonal scaling, factorization status (Cholesky for SPD, suitable pivoting symmetric-indefinite method for diagnostic), pivot warnings and inertia where available; scaled residual `||Ku-F||/(||K||||u||+||F||)`; condition estimate where feasible.
- On failure: rank-revealing / sparse nullspace or smallest eigenmodes as a **second diagnostic pass**. Eigenvectors/nullspace basis can mix several mechanisms: localize by participation, connectivity and releases, indicate cause as hypothesis with confidence.
- "Show all" promises all **detected/estimated** independent modes at selected numerical tolerance, not a guaranteed exhaustive physical explanation. Avoid rounding pivots to zero without tracking validity.
- Distinguish singular rigid modes, mechanisms, ill-conditioning, negative tangent eigenvalues under geometric stiffness, constraint conflicts and false mode caused by mass singularity.
- UI: issue list with severity, matrix evidence, component/DOF/name, scope and 3D animated mode where meaningful; one-click select objects; export JSON/text. Solver must fail safely without misleading displacements when untrustworthy.

Acceptance: known 6 rigid-body modes in unsupported 3D frame; partial release mechanism; disconnected substructures; weak spring warning; stable fully fixed cases; compare diagnostic modes with independent small-matrix SVD on test cases.

## E. P3 — Global P-Delta, iterative based on load patterns

UI location `Analyze > Global P-Delta` (or `Define > P-Delta Options`, with link from Analyze): `None` / `Iterative — Based on Loads` / `Non-Iterative — Based on Mass`; initial P-Delta is an analysis **stiffness-state setting**, not a conventional independently superposable static load case. Select signed (LoadPattern, scale factor) rows; controls for relative equilibrium force tolerance, displacement increment tolerance, max iterations, load steps, divergence policy. Provide default none on migration. Display source load state in downstream load cases.

Numerical flow (requires formula verification against cited textbook/CSI reference chapters before implementation):
1. Run validated first-order analysis of weighted patterns; compute member axial force distribution with documented compression sign.
2. Form geometric stiffness `K_g(N)` in member-local coordinates (O'Hara & Ramming §§5.8–5.11), then transform/assemble tangent `K_t = K_e + K_g` using a clearly documented `K_g` sign convention.
3. Iterate to equilibrium `r(u)=F_ext-F_int(u)`, `K_t(u_k) Δu = r_k`, update solution and forces; distinguish tangent Newton from a *fixed geometric-stiffness reanalysis* approximation. Verify consistency between chosen tangent and restoring-force law. Step loads where needed.
4. Converge using **both** scaled residual and displacement correction plus iteration limits; report history and final forces; never return a successfully completed dependent case after a failed initial P-Delta state.
5. Freeze the converged initial P-Delta stiffness state for subsequent *linear* cases; document that these cases may be linearly superposed only when they share that same state. Do not accidentally add initial gravity loads a second time.
6. Detect near-zero/negative tangent modes, record potential buckling. Small-rotation P-Delta is **not** full corotational large-displacement analysis.

Acceptance: analytical beam-column amplification references, separate compressive/tensile effects, geometry and load scaling, equilibrium each iteration, OpenSeesPy `geomTransf('PDelta',...)` matched assumptions. Failure near buckling must produce explicit incomplete status. No claim to duplicate SAPFire internal algorithm.

## F. P4 — Mass sources and floor/diaphragm model (shared prerequisite)

MassSource name and combinations of (self mass from material density, additional point/line/area mass, signed scaled vertical load patterns divided by g); prohibit or identify negative effective mass explicitly. Ensure no duplicated self-mass when load-derived mass is combined. Units and gravitational constant configuration. Nodal lumped mass vs consistent frame mass must be deliberate, saved and tested.

For approximate story P-Delta, introduce **explicit** `Story` definitions, story elevations and diaphragm groups, floor tributary masses, and gravity direction. No automatic grouping by similar Z within ambiguous tolerances. Warn when multiple/unconnected diaphragms, sloping floors, no story, or no credible floor-level mass exist.

Acceptance: total/translational mass and COM checks, mass-source sum independently hand calculated, mass invariance under remeshing where applicable, no double count, mass matrix symmetry and positive semidefiniteness.

## G. P5 — Non-iterative P-Delta based on mass — RESEARCH GATE

CSI states this method is *story-by-story*, uses a simplified stick model, is approximate and **non-iterative**; it does **not** publish sufficient detailed formulas in the cited help page to code a validated clone. **Do not substitute the element `K_g(N)` iteration from section E or assign mass*g as equivalent axial loads and claim it matches CSI.**

Before implementation require: (1) exact public CSI Analysis Reference passage and/or authoritative published derivation describing story geometric stiffness, interstory drift relation, gravity effects, multiple diaphragms and DOF mapping; (2) explicit statement of limitations (e.g. local member buckling); (3) validated hand-computable story benchmark; (4) independently reproduced CSI results where accessible, or flag as Mcotis-original approximation with own cited derivation; (5) OpenSeesPy comparison at *structural-response* level, with non-equivalence documented.

Suggested UI: disable mass-based P-Delta if required story/diaphragm/mass definition is incomplete; show "Approximate global sway P-Delta only"; provide method limitations in tooltip and report. This method may need a different supported eligibility class than arbitrary 3D frames.

## H. P6 — Modal load case: Eigenvectors only

`Define > Load Cases > Add > Modal`; unique name, Subtype `Eigen` initially; selected MassSource; requested/max mode count, optional frequency shift/cutoff and convergence tolerance only if supported by chosen sparse generalized eigensolver; stiffness state = elastic or converged initial P-Delta state. *No load pattern amplitude* required for a free-vibration eigenproblem.

Mathematics: reduce to active unconstrained DOFs; `K*φ = ω² M*φ` for undamped linear vibration; frequency `f=ω/(2π)`, period `T=1/f`; unit-consistent mass normalization `φᵀ M φ=1` (chosen internal convention); check `||Kφ-ω²Mφ||` and `φᵀMφ` orthogonality. Resolve singular/zero-mass blocks with validated elimination/static condensation or generalized solver method. Avoid blindly inverting M.

Modal effective mass: direction r, `Γ_i=(φ_iᵀ M r)/(φ_iᵀ M φ_i)` and `M_eff,i=(φ_iᵀ M r)^2/(φ_iᵀ M φ_i)`; report percentages relative to `rᵀ M r`, only where defined. For global rotational participation, use consistent rotation influence vectors; do not assume all node rotational DOFs carry rotational inertia. Distinguish zero/negative eigenvalues from solver errors.

Results: modes table (eigenvalue, f, T), mass participation UX/UY/UZ, cumulative percentages, eigenvector animated deformed shape with arbitrary display scale, comparison MAC / subspace comparisons for close/repeated eigenvalues. UI should never label eigenvector amplitude as physical displacement.

Acceptance: SDOF oscillator, axial truss/mass system, Euler-Bernoulli bending benchmarks, restrained/unrestrained 3D frames, coupled torsion+bending, massless rotational DOFs, repeated eigenvalues, OpenSeesPy `eigen` and `modalProperties` with exactly matched mass distribution.

## I. P7 — Combined workflows and source-controlled QA

- Analysis dependency graph: LoadPatterns -> MassSources -> initial P-Delta state -> Linear Static & Modal cases. Invalidate results only for affected nodes on model change. Named cases, immutable solve snapshots.
- Every analysis exports `analysis-manifest.json`: repo commit, formulation version, model hash, source method IDs, solver type/version, tolerances, mass sources, DOF mask, diagnostics and benchmarks. Report includes units, P-Delta stiffness state, mass basis and warnings.
- Test tiers: (1) element unit matrices and symmetry; (2) analytical/hand benchmark; (3) independently generated OpenSeesPy model; (4) optional independently inspected CSI comparison; (5) full 2D legacy regression; (6) property/metamorphic tests (rigid transforms, element reorder, mesh refinement).
- Compare tolerances should be numerical scale- and quantity-specific, combine absolute and relative thresholds; set them *after* evaluating formulations and benchmark precision, not an arbitrary universal 0.1% across all cases.
- Release gates: source verified, code reviewed, relevant test families green, unstable tests reported correctly, documentation/UX verified, no errors silently suppressed.

## J. Suggested code modules (adjust to actual repository)

```
Mcotis.Domain/{Geometry,Elements,Loads,Mass,Constraints,AnalysisCases}
Mcotis.Analysis/{Compilation,DofMapping,ElementKernels,Assembly,LinearStatic,PDelta,Modal,Results}
Mcotis.Numerics/{SparseFactorizations,Eigensolvers,MatrixDiagnostics}
Mcotis.Diagnostics/{ModelChecks,Mechanisms,Conditioning,Reports}
Mcotis.Presentation/{AnalysisSettings,ViewModels,3DViewer,ResultExplorer}
Mcotis.Verification/{Fixtures,AnalyticalBenchmarks,OpenSeesPyAdapter,Comparison}
```

Do not assume folders/projects listed above are current: integrate via a separate repository architecture review before moving existing types.

## K. Suggested PR sequence

PR01 source register + regression fixtures + conventions; PR02 domain XYZ + DOF maps + migration; PR03 3D beam/truss kernels and 3D static tests; PR04 UI Analyze > Set DOF; PR05 full diagnostics + all-instabilities viewer; PR06 geometric stiffness kernel + unit tests; PR07 iterative global P-Delta; PR08 mass sources and floors; PR09 researched and benchmarked mass-based P-Delta; PR10 modal eigen case + mass participation; PR11 integration, report, independent cross-validation.

### Blocking questions to resolve via evidence, not guesses
- Does the uploaded Rugarli book provide a fully implementable mass-based story P-Delta formula? Currently **not verified**.
- Does the available public CSI Analysis Reference specify precise non-iterative mass-based P-Delta equations? **Unverified**; the help files document behavior only.
- Can an arbitrary frame model without floors use story mass-based P-Delta? **No blanket support**; restrict until separately derived/validated.
- Exact current Mcotis class names, startup UI and storage schemas? **Repository inspection required**.
