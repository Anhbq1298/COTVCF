# Mcotis — Literature, Equations and QA/QC Traceability

Repository note (2026-10-12): this is the supplied research register. Book/PDF descriptions below are supplied
claims, not independently inspected source passages in this implementation. See [foundation-status.md](foundation-status.md)
for the actual code audit, inspected ALG-04 behavioral evidence and tests, and
[spatial-static-verification.md](spatial-static-verification.md) for the 3D linear static engine: the B1 passages for
ALG-01/02/03/05 were inspected there and the code is cross-checked numerically. No record has been promoted to
SOURCE_VERIFIED or RELEASED — that needs a reviewer's approval; the mass-based P-Delta gate remains open.

**Status:** living research / verification register — not a declaration that all equations or algorithms have been verified.

**Rule:** `SOURCE_VERIFIED` requires an inspected source passage with equation/figure/page, stated assumptions, independent benchmark and reviewer approval. `DOCUMENTED_BEHAVIOR` alone is insufficient for numerical implementation.

## 1. Evidence codes
- `B` = uploaded book, mathematical/engineering reference (include printed page and edition).
- `C` = public CSI/ETABS/SAP documentation; behavioral or derivation support depending on section.
- `O` = OpenSeesPy public docs/source, independent reference model; comparing two codes is not proof of correctness.
- `A` = analytical/classical structural mechanics benchmark.
- `R` = additional peer-reviewed numerical method reference, to be sourced if missing.
- `OPEN` = not yet verified from source; blocked for code merge.

## 2. Primary uploaded references

**B1.** Steven E. O'Hara and Carisa H. Ramming, *Numerical Structural Analysis*, Momentum Press, 2015. Uploaded 302-page PDF. ISBN 978-1-60650-489-5 e-book. Chapter 2 §§2.6–2.10 Gaussian/Gauss-Jordan, Cholesky, error equations; §§2.13–2.15 eigen methods (printed pp. 63–95). Chapter 4 §§4.1–4.3 coordinate rotation/transform (pp. 147–154), §§4.10–4.13 XZ, XY, 3D member/global stiffness (pp. 174–204). Chapter 5 §§5.1–5.3 releases including 3D (pp. 205–229), §5.4 nonprismatic (pp. 229–238), §§5.5–5.7 shear stiffness (pp. 239–250), §§5.8–5.11 2D/3D geometric stiffness and shear coupling (pp. 251–264), §5.12 torsion (p. 265), §5.13 substructuring (p. 266). **Chapter/section headings confirmed against PDF contents. Numerical formulas in §§4–5 must be checked individually prior to transcription.**

**B2.** Paolo Rugarli, *Structural Analysis with Finite Elements*, Thomas Telford / ICE Publishing, 2010, uploaded 414-page PDF. Chapters 3–9: simplified elasticity theories, FE essentials/assembly/forces/constraints/solving/error assessment; Ch. 11 common elements; Ch. 12 practical modelling; Ch. 13 practical problems. **Text extraction in this PDF is garbled for much of the mathematics**: verify from rendered page images before attributing formulas. Supports modeling discipline/diagnostics, not by itself a verified production sparse solver specification.

**B3.** Friedel Hartmann and Casimir Katz, *Structural Analysis with Finite Elements*, Springer-Verlag, 2004. **Only a 2-page book review (Francesco Ubertini, Meccanica 40:325–326, 2005) is uploaded, not the book.** Do not attribute uninspected equations to Hartmann/Katz.

## 3. Public CSI documentation — verified URLs

**C1.** ETABS `Set Active Degrees of Freedom`: https://docs.csiamerica.com/help-files/etabs/Menus/Analyze/Set_Analysis_Options/Set_Active_Degrees_of_Freedom.htm . Lists six DOFs; Full 3D, XZ UX/UZ/RY, YZ UY/UZ/RX and other plane presets. `DOCUMENTED_BEHAVIOR`.

**C2.** ETABS `Preset P-Delta Options`: https://docs.csiamerica.com/help-files/etabs/Menus/Define/Preset_P_Delta_Options.htm . **Three documented choices**: None; Non-Iterative—Based on Mass (approximate story-by-story / simplified stick / best with diaphragms; weaker at local buckling); Iterative—Based on Loads (user scale factors of load patterns, element-based, iterative equilibrium, recommended when gravity load patterns available). `DOCUMENTED_BEHAVIOR`, not source of full numeric equations.

**C3.** ETABS `Initial P-Delta Analysis`: https://docs.csiamerica.com/help-files/etabs/Menus/Analyze/Set_Analysis_Options/Initial_P-Delta_Analysis.htm . Converged load state applied before linear static/modal, iterative tolerance based on relative force error; buckling/nonconvergence limitations; no large-rotation treatment. `DOCUMENTED_BEHAVIOR`.

**C4.** ETABS `Modal Case Data Form`: https://docs.csiamerica.com/help-files/etabs/Menus/Define/Load_Cases/Modal_Case_Data_Form.htm . Eigenvectors represent undamped free-vibration eigenmodes; modal cases linear; eigen vs Ritz choice; number of modes, convergence, frequency shift, reference stiffness states. `DOCUMENTED_BEHAVIOR`.

**C5.** ETABS `Mass Source`: https://docs.csiamerica.com/help-files/etabs/Menus/Define/Mass_Source.htm . Material self-mass, additional mass, load-pattern-converted mass via load/g; avoids negative mass; directions/distribution must be matched in benchmark. `DOCUMENTED_BEHAVIOR`.

**C6.** CSI `Analysis Reference Manual`: https://docs.csiamerica.com/manuals/sap2000/CSiRefer.pdf and https://docs.csiamerica.com/manuals/etabs/Analysis%20Reference.pdf . **Candidate mathematical authority; exact relevant passages, equation numbers and version must be read and registered before implementing.** `OPEN` for detailed noniterative mass-P-Delta formula.

**C7.** ETABS `Load Cases`: https://docs.csiamerica.com/help-files/etabs/Menus/Define/Load_Cases/Define_Load_Cases.htm . A load pattern alone produces no analysis response: it must be used in a load case. `DOCUMENTED_BEHAVIOR`.

## 4. Algorithms/formulas — sourcing and verification register

| ID | Formula/behavior to verify | Source | Status | Evidence/test required |
|---|---|---|---|---|
| ALG-01 | 3D 12×12 local linear elastic beam stiffness, EA, EIy, EIz, GJ | B1 eq. 4.33 (p. 187); C6 | CODED; passage inspected; cross-checked (analytical, OpenSeesPy); reviewer pending. Printed entry (Δiz, θiy) has the opposite sign of its mirror entry — a misprint; the code is symmetric | Done in `SpatialStaticChecks`: all 144 entries by independent integration, rigid-body modes, cantilevers, torsion |
| ALG-02 | Orthogonal 3D local axes, transform `K_G = Tᵀ K_L T` | B1 eq. 4.34 (p. 188); §§4.1–4.3 not re-read | CODED; eq. 4.34 inspected; cross-checked; reviewer pending. The default local-axis rule is a project convention, not from B1 | Done: orthonormal right-handed axes, rigid-rotation invariance, OpenSeesPy with explicit orientation vectors |
| ALG-03 | Global assembly and reduced DOF equation (dense for now) | B1 eqs. 4.35–4.38 (p. 188); B2 not inspected | CODED (dense engine); passage inspected; cross-checked; reviewer pending. Sparse assembly still OPEN | Done: two-element beam, free-body force and moment equilibrium of 12 models, legacy XZ regression |
| ALG-04 | DOF masks / plane presets | C1 | DOCUMENTED_BEHAVIOR | 2D XZ regression, incompatible element warning |
| ALG-05 | Member-end release condensation | B1 §5.3 (p. 217), §5.1 (pp. 205–206) | CODED for M2 / M3 only, by static condensation; the book's tabulated released matrices were not compared entry by entry; cross-checked; reviewer pending | Done: propped cantilever, pin–pin beam, OpenSeesPy `-releasez`, loaded released joint refused as a mechanism |
| LOAD-01 | Equivalent nodal loads of frame point and linearly varying line loads (Hermite shape functions) | no source passage inspected | CODED; numerical evidence only | Done: uniform, partial triangular and member point loads against closed forms and OpenSeesPy `eleLoad` |
| ALG-06 | Shear-flexible 3D frame options | B1 §§5.5–5.7 | OPEN equation-level | Timoshenko benchmark, avoid EB/OpenSees mismatch |
| ALG-07 | Geometric stiffness `K_t = K_e + K_g` with stated compression convention | B1 §§5.8–5.11; C6 | OPEN equation-level | bending under P and −P; stability threshold |
| ALG-08 | Iterative P-Delta residual Newton update, convergence | C2/C3 behavior; B1 §1.10 scalar Newton as background, C6 exact formulation pending | OPEN numerical derivation | tangent finite-difference consistency, residual history, beam-column cases |
| ALG-09 | Approximate noniterative **story-mass-based** P-Delta | C2/C3 behavior | BLOCKED: exact public derivation not verified | locate authoritative story-stiffness formula, floors/diaphragm assumptions and comparison |
| ALG-10 | Assemble mass from self/additional/loads; positive mass rules | C5 | DOCUMENTED_BEHAVIOR, OPEN implementation math | hand mass inventory, center of mass, g conversion |
| ALG-11 | Generalized vibration eigenproblem `K φ = ω² M φ` | C4/C6; B1 §2.13–2.15 for generic eigen methods | OPEN equation-level | oscillator, beam eigenvalues, eigen residual |
| ALG-12 | Modal participation, mass normalization and effective masses | C6; independent vibration source needed | OPEN | mass-orthogonality and sum participation checks |
| ALG-13 | Full instability diagnosis using factorization/nullspace and scaled residual | B1 §§2.6–2.10 partial background; B2 Chs.7–9; CSI standard solver behavior source TBD | OPEN | 6 rigid modes, rank-deficient frame, near-singular springs |
| ALG-14 | Stiffness from converged P-Delta state used in subsequent modal/static | C3/C4 | DOCUMENTED_BEHAVIOR | frequency shift under compression, consistent stiffness state |

**Warning:** Standard numerical linear algebra equations in this register are *proposed implementation targets* until audited against the listed source. Their appearance here does not certify an exact CSI implementation.

## 5. Source-to-code record template (complete per formula)

```
ID: ALG-xx
Source: author/title/edition, section, printed page, equation/figure number, URL or uploaded file
Exact assumptions: linear/nonlinear, strain/rotation limits, shear model, axis/signs, geometry
Independent derivation review: reviewer + date + corrected dimensions/signs
Code: namespace/class/method + revision
Test: benchmark ID and expected answer with tolerance rationale
OpenSeesPy analogue: element / transformation / mass / constraint equivalence notes
CSI comparison: optional, version, model and results; differences disclosed
Status: OPEN | SOURCE_VERIFIED | CODED | CROSS_VERIFIED | RELEASED
```

## 6. Minimum verification suite and provenance

- `BENCH-01` 2D XZ frame legacy regression and geometry-preserving 3D migration.
- `BENCH-02` 3D cantilever: axial/weak-strong flexure/torsion (hand mechanics + OpenSeesPy).
- `BENCH-03` 3D rotated frame and arbitrary element numbering/orientation invariance.
- `BENCH-04` hinged mechanism, unsupported frame, disconnected component, tiny spring (matrix singularity/conditioning).
- `BENCH-05` 3D member releases with static condensation and reaction recovery.
- `BENCH-06` P-Delta compression/tension amplification and convergence; load-pattern references.
- `BENCH-07` story mass-source audit and mass-based P-Delta after ALG-09 research gate.
- `BENCH-08` SDOF frequency and two-mode spring-mass; 3D beam modal reference.
- `BENCH-09` frame modal effective mass/orthogonality/MAC with OpenSeesPy.
- `BENCH-10` prestressed modal K_t eigenmodes compared only with equivalent OpenSeesPy tangent formulation.

OpenSeesPy tests should be built *independently from benchmark specifications*, not only translated from Mcotis's already assembled element matrices. Commit fixture source, software versions, units, tolerances and comparison script; flag formulation mismatches as `NOT COMPARABLE` rather than force artificial pass.

## 7. Outstanding research tickets

- `RES-01 HIGH`: CSI Analysis Reference — locate complete mass-based initial P-Delta derivation, or identify alternate published/peer-reviewed treatment. **Mandatory before ALG-09 implementation.**
- `RES-02 HIGH`: Validate detailed 3D beam/frame stiffness, bending axis conventions and shear effects from B1 original equations.
- `RES-03 HIGH`: Verify geometric tangent stiffness + equilibrium iteration method in C6 and academic computational mechanics reference, including load stepping and failure thresholds.
- `RES-04 HIGH`: Find authoritative mass matrix/modal participation formulations, consistent vs lumped mass and handling massless DOFs; CSI Analysis Reference and structural dynamics textbook.
- `RES-05 MEDIUM`: Sparse factorization, rank-revealing LDLᵀ, numerical inertia and mechanism nullspace methods. Need production-grade numerical linear algebra citations beyond B1 elementary algorithms.
- `RES-06 MEDIUM`: Confirm CSI standard solver reporting semantics and distinguish public help claims from internal algorithm claims.
- `RES-07 HIGH`: Examine Mcotis repository revision and migrate concrete module names/constraints; this document has not audited the current code.

## 8. Reviewer acceptance checklist

- [ ] Source is accessible and section/page/equation checked against rendered original.
- [ ] Source assumptions documented, including deliberate deviations.
- [ ] Units, DOFs, axis rotation, sign conventions and static condensation independently checked.
- [ ] Test covers hand answer, equilibrium, sensitivities and failure cases.
- [ ] OpenSeesPy model physics matched and discrepancies explained.
- [ ] Tolerance derives from numerical precision, magnitude and formulation comparability.
- [ ] No invalid model silently auto-stabilized or solved with fake supports.
- [ ] User-facing diagnostics explain validity and warnings.
- [ ] Code-level record links source + implementation + benchmark + review approval.
