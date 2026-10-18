# MCOTiS — Modal Analysis Formula, Source and QA/QC Register

**Date:** 2026-10-13
**Plan:** [MODAL_EIGEN_IMPLEMENTATION_PLAN.md](MODAL_EIGEN_IMPLEMENTATION_PLAN.md)
**Baseline:** master `f80b18a3fb3e39baa67cea4f1199a6e6702a5e33`; companion case plan on `plan/load-case-state-architecture`.
**Status:** sourced *implementation specification*; no modal code or reviewer-accepted formula currently released.

**2026-10-13 user-required mass feature:** automatic SW through element self mass at factor 1.0, user-selected extra load patterns/factors from Define → Mass Source, and mandatory generated-SW exclusion. See [MASS_SOURCE_IMPLEMENTATION_PLAN.md](MASS_SOURCE_IMPLEMENTATION_PLAN.md) for TODOs, physical rule and exact code map; this is part of Modal MVP.

## Evidence levels

- `INSPECTED_HTML_BEHAVIOR`: original vendor online help supports feature semantics, **not** its unpublished internal matrix.
- `INSPECTED_FORMULA`: original public source shows the stated mathematical relationship and assumptions; does **not** mean MCOTiS implementation is correct.
- `DERIVED_PENDING_REVIEW`: mathematical consequence or standard mechanics derivative written here, but source passage/equation or reviewer missing.
- `OPEN_FORMULA`: not yet checked against source; **do not code as verified**.
- `CODED_TESTED`: planned status; never assign until PR and tests exist.
- `CROSS_VERIFIED`: planned status; independent benchmark run and documented.
- `SOURCE_VERIFIED`: original equation/page inspected, assumptions matched, reviewer approval/date, independent physics benchmark, code/test linkage.

Use `SOURCE_VERIFIED` only after actual independent engineering sign-off; a single agreement with CSI/OpenSees is not a derivation proof.

## Sources directly inspected on 2026-10-13

| ID | Reference | Inspected relevant statement | Evidence |
|---|---|---|---|
| C-MOD-01 | [CSI SAP2000 — Modal Form](https://docs.csiamerica.com/help-files/sap/Menus/Define/Load_Cases/Modal_Form.htm) | Modal analysis always linear; eigenvectors are undamped free-vibration modes; stiffness can be unstressed or from end of nonlinear case; shift/cutoff are elective options when supported. | INSPECTED_HTML_BEHAVIOR |
| C-MOD-02 | [CSI SAP2000 — Stiffness to Use](https://docs.csiamerica.com/help-files/sap/Menus/Define/Load_Cases/Stiffness_to_Use.htm) | Nonlinear-end-state stiffness can be reused in a linear case; nonlinear source is defined separately. | INSPECTED_HTML_BEHAVIOR |
| C-MASS-01 | [CSI SAP2000 — Mass Source](https://docs.csiamerica.com/help-files/sap/Menus/Define/Mass_Source.htm) | Element material self mass, extra assigned mass, gravity-load-derived mass; multi-source double-count risk; mass source inheritance rules; translational and rotational joint masses treated separately. | INSPECTED_HTML_BEHAVIOR |
| C-MASS-02 | [CSI ETABS — Mass Source](https://docs.csiamerica.com/help-files/etabs/Menus/Define/Mass_Source.htm) | User-selected material self mass, additional mass, gravity load patterns; gravity components converted via g. The ETABS handling of negative mass may differ from SAP2000 details and is **not** MCOTiS policy. | INSPECTED_HTML_BEHAVIOR |
| O-EIG-01 | [OpenSees — eigen command](https://opensees.github.io/OpenSeesDocumentation/user/manual/analysis/eigen.html) | Generalized eigenproblem solver interfaces; ARPACK N−1 inertial DOF limitation; available full LAPACK alternative. | INSPECTED_HTML_BEHAVIOR |
| O-MOD-01 | [OpenSees — modalProperties](https://opensees.github.io/OpenSeesDocumentation/user/manual/analysis/modalProperties.html) | Generalized eigenproblem `(K−λM)Φ=0`; modal mass, factors, participation, effective mass, rigid-body directions with CoM lever arms; mass normalization and consistent-vs-lumped matrices. | INSPECTED_FORMULA |
| O-MASS-01 | [OpenSees — elasticBeamColumn](https://opensees.github.io/OpenSeesDocumentation/user/manual/model/elements/elasticBeamColumn.html) | Structural element option `-mass` per unit length, and `-cMass` consistent mass. Comparison must match choice. | INSPECTED_HTML_BEHAVIOR |
| N-EIG-01 | [LAPACK — DSYGVD](https://www.netlib.org/lapack/explore-html/d5/d0e/group__hegvd_gaf0bdec790208a5a56bbe0a5abcf581d0.html) | Solver computes `A*x=λ B*x` for real symmetric A and **symmetric positive definite B**. Does not solve unreduced semidefinite mass by magic. | INSPECTED_FORMULA |

### Candidate texts not reinspected this turn

- **B1** O'Hara and Ramming, *Numerical Structural Analysis* (2015), §§2.13–2.15 and Chapter 4, as indexed in [literature.md](literature.md). Chapter/index claims originated in previous project documents, not newly inspected pages.
- **CSI Analysis Reference Manual**: https://docs.csiamerica.com/manuals/sap2000/CSiRefer.pdf ; targeted book/manual equation-page/edition inspection remains OPEN.
- **OpenSees code or additional peer-reviewed mass condensation derivation**: not inspected.

## Implementation formula checklist

| ID | Proposed formula/algorithm and assumptions | Primary evidence | Current classification | Required independent verification |
|---|---|---|---|---|
| ALG-10A | Element physical mass: `m=ρAL × mass modifier`; DOFs translational, rho [t/m³], A [m²], L [m] | C-MASS-01, O-MASS-01 (behavior); dimensionally from mechanics | DERIVED_PENDING_REVIEW | Hand arithmetic with material rho, modifiers; member split invariance; origin and units verified. |
| ALG-10B | Lumped beam/cable mass: two end nodes each `m/2` for a uniform two-noded element | O-MASS-01 distinguishes lumped/consistent; exact entry convention not derivation there | OPEN_FORMULA | Inspect FE mass-lumping derivation/source; model with interior joint vs unsplit, independent OpenSees lumped mass. |
| ALG-10C | **MVP** user-selected additional gravity patterns converted to inertial mass `m=α W_explicit/g`; all generated SW excluded because auto self mass is already included | C-MASS-01, C-MASS-02 | INSPECTED_HTML_BEHAVIOR; implementation discretization OPEN | Independent load equivalence and mapping; g unit check, mass signs, negative mass response, self-weight double-count. |
| ALG-10D | Consistent 3D frame mass `M_e=∫ρA NᵀN dx` with EB interpolations | Classical FEM; source passage not yet inspected | OPEN_FORMULA | Compare local 12×12 entries, sign/symmetry/PSD and local-global rotations; exact dynamic end-release treatment. |
| ALG-11A | `K φ = λ M φ`, `λ=ω²`, `f=ω/(2π)`, `T=1/f` when λ positive | O-MOD-01 eigen eq; standard harmonic substitution | INSPECTED_FORMULA for eigenproblem; frequency derivation reviewer pending | 1/2 DOF analytically, units kN–m–t; residual of each eigenpair on original DOFs. |
| ALG-11B | Reduce explicit supports/inactive components using native DOF mapping, not by adding supports | Existing ModelCompiler contract; general FEM constraints | DERIVED_PENDING_REVIEW | 2D vs 3D benchmark, full six DOF recovery, inactive directions stay inactive. |
| ALG-11C | For diagonal lumped `Mzz=0`, eliminate massless DOFs via Schur complement `Kbar=Kdd−Kdz Kzz⁻¹ Kzd`, provided `Kzz` invertible; back substitute | Algebraic elimination here, not a vendor-specific claim | DERIVED_PENDING_REVIEW | Hand partitioned 3 DOF model, 2D frame rotation-without-inertia, original K/M residual; singular Kzz correctly fails. |
| ALG-11D | Dense generalized symmetric-definite eigen solve on **condensed SPD M** | N-EIG-01 | INSPECTED_FORMULA for library contract, algorithm integration OPEN | Exact solver return codes/column layout, symmetric tolerances, negative eigenvalue distinction, repeated mode. |
| ALG-11E | `φᵀMφ=1`, M-orthogonality, `φᵀKφ=λ` | O-MOD-01 (mass normalization), spectral theory | INSPECTED_FORMULA for normalization; implementation/reviewer pending | Orthogonality/scale invariance; original full-system residual and norm scaling. |
| ALG-12A | `Γ=(φᵀMr)/(φᵀMφ)`, `Meff=(φᵀMr)^2/(φᵀMφ)`; participation denominator `rᵀMr` | O-MOD-01 explicitly provides MPF/MPM and cumulative ratios | INSPECTED_FORMULA | Two-DOF modal hand calc; participation invariance to sign/scaling and full-set sum, N/A with zero mass. |
| ALG-12B | Rotational direction vector about CoM includes `δu=δθ×(x−CoM)` plus direct nodal rotation | O-MOD-01 provides 3D T_j influence matrix and CoM | INSPECTED_FORMULA; concrete axis signs review needed | Hand eccentric mass, rigid-body rotation and axis sign; no erroneous rotational DOF-only participation. |
| ALG-14 | End-of-nonlinear tangent stiffness used in modal without adding gravity loads or PDelta twice | C-MOD-01, C-MOD-02 | INSPECTED_HTML_BEHAVIOR | Same parent fingerprint; compression/tension frequency effect; convergence and active cable set; OpenSees equivalent model. |
| UX-MOD-01 | Modes are linearized eigenvectors; shown amplitudes are arbitrary-scaled and not static deflections | C-MOD-01, O-MOD-01 | INSPECTED_HTML_BEHAVIOR | UI copy, report, project roundtrip. |
| PERSIST-MOD-01 | Case references and mass sources use stable immutable IDs with revisioned schema migrations | Application design decision | ENGINEERING_CONTRACT (project rule) | Old .mcotis/.cotfem.db fixtures round-trip with no damage; rename preserves references. |

**No rows are yet SOURCE_VERIFIED** for MCOTiS production release. `INSPECTED_FORMULA` verifies the cited source states a formula, not that it has been incorporated into software.

## Numerical QA gates

### Gate A — source matched to exact solver feature

For each `ALG`, provide (1) edition + page/equation/URL and access date; (2) chosen formulation; (3) assumptions and free/restrained DOFs; (4) mass/stiffness units; (5) whether CSI behavior is being emulated or MCOTiS own design.

### Gate B — hand benchmarks and independence

1. SDOF `k/m` eigenvalue, frequency, period, M normalization.
2. Nontrivial coupled 2 DOF eigenpairs and effective mass.
3. 2D beam whose rotations are massless, physically condensed without restraints.
4. 3D frame with rotated axes and equal/unequal mass distributions.
5. Prestressed beam-column compression/tension frequency shifts (only after sourced K_g).
6. Cable taut/slack tangent sources and standalone 2D truss DOF selection.
7. Pathological negative mass (reject), zero mass (reject or properly condense), mechanism (diagnose), repeated eigenvalues (subspace), eigen solver failure (no result).

### Gate C — independent OpenSees model comparison

Use **independent benchmark specification** to build OpenSeesPy and MCOTiS models separately, not copy Mcotis matrices into OpenSees.

Match all: supports, 2D vs 3D DOF choices, rho per **length** vs rho per **volume**, correct `-mass` and `-cMass`, frame local axes, mass modifier, release definitions, cable formulation/prestrain, geometric transformation and nonlinear state. For OpenSees `eigen` default ARPACK, recall N−1 inertial DOF restriction. Compare eigenvalues/frequencies plus MAC or subspaces; not eigenvector sign.

### Gate D — performance, stability and release

- Dimension/rank/report diagnostics correct with m, kN, t and seconds.
- Effective mass percentages based on physical mass in the direction (not normalized to extracted modes).
- No silent mass regularization, support insertion, negative-mass clipping, fallback to elastic K when parent failed, or app-level eigen computations.
- Mode display sign and scale are visuals only.
- Versioned result files store source fingerprint, mass method, solver precision and per-mode residuals.
- Renderer works for 2D and 3D native workspaces.
- An independent engineering reviewer approves actual equation-to-implementation crosswalk and demonstrates accepted numerical benchmark artifacts.

## Code-to-source records to fill before merge

```yaml
record_id: ALG-11C
source: "Exact printed article/book/paper or primary derivation"
equation_or_section: null
date_inspected: null
physical_assumptions:
  mass_form: "diagonal lumped"
  stiffness: "symmetric; Kzz invertible"
  active_dofs: "native reduced free DOFs"
code_path: "src/Mcotis.Analysis/Modal/ModalDofReducer.cs"
code_commit: null
test_fixture: "M-03"
independent_reference: null
expected_error_and_basis: null
reviewer: null
review_date: null
status: "DERIVED_PENDING_REVIEW"
```

For consistent mass and sparse solvers, use additional IDs rather than treating the lumped-mass finite-dimensional derivation as adequate for all cases.

## Mandatory status statement for engineering report

"Modal analysis uses the eigenvalue solution of the explicitly documented model mass and reference stiffness. Reported frequencies/mode shapes describe small-amplitude free vibration about that reference state. The selected mass formulation, parent stiffness, active DOFs, supported element types, limitations, numerical residuals and source review status are listed in this report. It does not by itself constitute a seismic response-spectrum or dynamic design verification."

## Mass Source mandatory implementation evidence — 2026-10-13

CSI SAP2000 Mass Source [C-MASS-01] explicitly describes a named default source, material self mass and scaled specified load patterns, downward global Z / g, and warns that selecting load patterns that include selfweight alongside Element Self Mass can double-count it. CSI ETABS [C-MASS-02] documents its corresponding Define → Mass Source UI and volume × mass density. SAP2000 Define Load Patterns [C-LP-01] explains how a Self-Weight Multiplier generates static gravity loads. OpenSees modalProperties [O-MOD-01] describes assembled mass matrices and total/free mass inventories.

**User-mandated MCOTiS policy (not published CSI behavior):**
- [ ] Structural Element Self Mass auto-ON and fixed at multiplier 1.0, obtained from material MassDensity and mass modifiers (not from the SW load pattern).
- [ ] Selected SDL/DEAD/LL/etc patterns add **only explicitly assigned downward gravity loading converted / g**. Generated selfweight in any selected pattern is excluded automatically using provenance and SelfWeightMultiplier; no brittle pattern-name lookup.
- [ ] A mixed SW+SDL pattern must give material selfmass once plus SDL/g, not selfmass plus total SW+SDL/g.
- [ ] No automatic extra patterns; mass multiplier not copied from static ULS load factor.
- [ ] If explicit/generated loads cannot be separated for an imported model, refuse that mass-conversion input instead of guessing.
- [ ] No silent absolute-value or negative-mass clipping; name pattern, member/joint and contribution.
- [ ] Selected patterns and factors are part of Modal MVP, not a deferred improvement.

| New record | Formula/behavior | Source classification | Verification |
|---|---|---|---|
| MASS-ALG-01 | Auto element selfmass m=rho A L × mass modifier (factor 1.0) | CSI material selfmass documented; exact lumping/release operator needs review | MASS-01,02, frame subdivision, material gamma/rho independence |
| MASS-ALG-03 | Signed explicit assigned gravity Wdown/g; factors alpha | CSI SAP Detailed Calculations, ETABS Mass Source | MASS-03,04,08, exact sign/local projection |
| MASS-ALG-04 | Exclude generated selfweight from selected pattern, regardless of source name or multiplier | CSI warns of double counting; exclusion is a **MCOTiS application rule** | MASS-05,06 mixed pattern and SW factor 0.5/1.0/2.0 |
| MASS-ALG-05 | Frame load-to-lumped nodal mass: mI=alpha/g ∫(1-s/L)w(s)ds, mJ=alpha/g ∫(s/L)w(s)ds | **DERIVED_PENDING_REVIEW**; not claimed CSI formula | Exact integration, mass conservation, centroid/split invariance, independent source |
| MASS-PERSIST-01 | Named/default source stable IDs in .mcotis and .cotfem.db | MCOTiS software requirement, not CSI numerical behavior | MASS-09 schema roundtrip and rename |
| MASS-UX-01 | Define → Mass Source tab, SW locked and pattern factors editable | User requirement inspired by CSI mass source concept | WPF workflow, 2D/3D model, default import |

Reference URLs:
- [CSI SAP2000 Mass Source](https://docs.csiamerica.com/help-files/sap/Menus/Define/Mass_Source.htm)
- [CSI ETABS Mass Source](https://docs.csiamerica.com/help-files/etabs/Menus/Define/Mass_Source.htm)
- [CSI SAP2000 Define Load Patterns](https://docs.csiamerica.com/help-files/sap/Menus/Define/Load_Pattern/Define_Load_Patterns.htm)
- [OpenSees modalProperties](https://opensees.github.io/OpenSeesDocumentation/user/manual/analysis/modalProperties.html)

**Evidence limitation:** live public reference pages establish behavior and published modal participation relationships. They do not establish MCOTiS's detailed frame-weight lumping as numerically verified. This must pass an independent derivation/reference and external benchmark before release.
