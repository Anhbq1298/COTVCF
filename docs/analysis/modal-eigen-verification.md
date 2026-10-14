# Modal Eigen verification record

Implemented and checked 2026-10-13; extended 2026-10-15 to frame-and-cable models using a converged first-order
nonlinear static state. This record covers small-amplitude free vibration with lumped mass. It does not certify a
seismic design, response-spectrum analysis, large-displacement tangent, cable-only system, or CSI solver equivalence.

## Supported formulation

`Mcotis.Analysis.ModalSolver` solves

`K φ = λ M φ`, `λ = ω²`, `f = ω/(2π)`, `T = 1/f`.

- `K` is the same elastic spatial-frame stiffness used by the verified static engine, including compiled frame
  segments and rotational end-release condensation.
- A frame-and-cable modal case must select a converged first-order nonlinear static case as **Stiffness from**. For
  each cable, that case supplies tension and taut/slack state. The frozen-chord
  small-displacement cable tangent is `k = k_axial n nᵀ + (T/L)(I - n nᵀ)` in the translational block between ends;
  a slack cable adds zero stiffness. This is a prestressed linearization, not a large-displacement equilibrium method.
  A state with a statically held mechanism is refused rather than reusing that temporary hold for modal analysis.
- The first mass operator is diagonal lumped mass. Each compiled uniform frame segment contributes half of
  `rho A L × mass modifier` to UX, UY and UZ at each end; cables do the same when self mass is selected. Explicit joint UX/UY/UZ mass and RX/RY/RZ inertia may be
  included by the selected named mass source.
- Restrained and inactive DOFs are excluded. A free DOF with no mass is not restrained and receives no fictitious
  mass. The solver partitions inertial DOFs `d` from massless DOFs `z` and uses exact static condensation:
  `Kbar = Kdd - Kdz Kzz^-1 Kzd`; the massless amplitudes are reconstructed as
  `φz = -Kzz^-1 Kzd φd`. A singular or unstable `Kzz` is a refusal.
- The positive diagonal reduced mass is whitened to the symmetric standard problem
  `A = M^-1/2 Kbar M^-1/2`. A source-visible cyclic Jacobi solver extracts and orders the complete dense spectrum;
  reconstructed modes are mass-normalized (`φᵀMφ = 1`). Dense limits are 400 inertial and 2,400 total DOFs.
- UX/UY/UZ participation uses `Γ = φᵀMr`, effective mass `(φᵀMr)²` for mass-normalized modes, and percentage relative
  to `rᵀMr`. A direction with no free mass reports N/A. Global rotational participation is not implemented.
- Every returned mode must pass a scaled residual check on the uncondensed free equations. Negative and near-zero
  eigenvalues have no frequency or period and make the case unsuccessful.

## Independent sources inspected

Accessed 2026-10-13:

- OpenSees, `eigen`: defines the generalized eigenvalue analysis, available solvers, returned eigenvalues and the
  repeated/clustered-mode limitation of iterative ARPACK.
  <https://opensees.github.io/OpenSeesDocumentation/user/manual/analysis/eigen.html>
- OpenSees, `modalProperties`: states `(K - λM)Φ = 0`, generalized mass `ΦᵀMΦ`, mass normalization, participation
  factor and effective/cumulative participation-mass equations, plus nodal/element mass accounting.
  <https://opensees.github.io/OpenSeesDocumentation/user/manual/analysis/modalProperties.html>
- O'Hara and Ramming, *Numerical Structural Analysis* (2015), §§2.13–2.15 are retained in the repository literature
  register as generic eigen-method background. The exact pages were not reinspected for this implementation and are
  not claimed as `SOURCE_VERIFIED`.

Accessed 2026-10-15 for the prestressed-cable extension:

- CSI SAP2000's *Stiffness to Use* help describes a modal case using stiffness at the end of a nonlinear case and
  freezing its tension-only cable active set:
  <https://docs.csiamerica.com/help-files/sap/Menus/Define/Load_Cases/Stiffness_to_Use.htm>.
- OpenSees' `corotTruss` documentation describes the geometrically nonlinear truss element used in the independent
  prestressed reference: <https://opensees.github.io/OpenSeesDocumentation/user/manual/model/elements/CorotationalTruss.html>.

The massless-DOF Schur complement is a project derivation of static condensation, also used for released frame
coordinates. It is verified below but still awaits an independent equation-level reviewer.

## Verification evidence

The focused group is `ModalChecks` in `tests/Mcotis.Checks`; it runs in quick smoke form and expands under `--full`.

| Check | Independent expectation | Result |
|---|---|---|
| One axial DOF | `λ = EA/(Lm)`, `f = sqrt(λ)/(2π)`, mass norm 1, UX participation 100% | Passed |
| Two-DOF axial chain | Exact roots `(3 ± sqrt(5))k/(2m)` and cumulative UX mass 100% | Passed |
| Cantilever with massless tip rotation | Exact condensed `λ = 6EI/(rho A L^4)`; one rotation condensed, not held | Passed |
| Split frame mass | Sum of compiled `rho A L` pieces equals unsplit physical mass | Passed |
| Native persistence | Schema-5 save/open preserves mass sources, six joint components and analysis hash | Passed |
| Failure paths | No free positive mass and cable without a verified prestressed tangent are explicit refusals | Passed |
| Prestressed frame + straight cable | Closed-form transverse `λ = (3EI/L³ + T/L)/m`; static axial force sharing gives `T = F/2` | Passed |
| OpenSeesPy 3.8.0 prestressed cable | Independent 2D `elasticBeamColumn` + `corotTruss` under axial pull; first eigenvalue 1978.64648 s⁻² versus Mcotis 1979.16667 s⁻² (0.0263% chord-geometry difference) | Passed |
| OpenSeesPy 3.8.0 | Independently built 1D Elastic-truss SDOF and two-DOF chains; eigenvalues, frequencies, individual and cumulative UX ratios | Passed |
| WPF end to end | 3D native building, explicit joint masses, three positive modes, nine participation rows, shape table/viewer | Passed |

The reproducible OpenSees generator is `verification/modal-eigen/generate_opensees.py`; its checked-in fixture is
`tests/Mcotis.Checks/Fixtures/modal-eigen-opensees.json`. It builds models from physical dimensions, material,
section and nodal masses and imports no Mcotis matrix. Run it with the pinned packages in
`verification/spatial-static/requirements.txt`.
The prestressed reference is `verification/modal-eigen/generate_cable_opensees.py`, with fixture
`tests/Mcotis.Checks/Fixtures/modal-cable-opensees.json`. It imports no Mcotis matrix or result. The reference
uses a corotational truss, while Mcotis keeps the undeformed chord, so the 0.0263% difference is expected at this
large test pull. Straight-cable and frame checks are the release evidence. A sagged cable is explicitly refused
until a separate dynamic sag benchmark is available.

## User-visible scope and refusals

The main window provides **Define → Load Cases → Load Case Data**, **Assign → Joint → Masses…**, **Run → Run FEM Analysis**,
**Result → Modal Results…** and **Result → Show Deformed Shape…**. The latter asks for a solved static or modal case;
choosing modal adds a mode-number dropdown. Mode shapes and the 2D/3D viewer say relative, mass-normalized amplitude
and never label an eigenvector as physical displacement.

The following planned items remain deliberately unavailable:

- stiffness from a P-Delta case, because no verified geometric-nonlinear frame tangent endpoint exists;
- cable-only systems, including intermediate cable joints, until their prestressed modal stability is independently verified;
- shells, load-pattern-derived mass, consistent frame mass, rotational participation, sparse extraction, Ritz,
  response spectrum, time history, damping and eigen-buckling;
- dynamic sag and large-displacement cable modes beyond the frozen-chord approximation;

No unsupported request falls back to unstressed stiffness, invented mass, or an inserted restraint. The first three
limits are the blocked/deferred tickets in the modal implementation plan, not incomplete variants of the released
elastic-frame calculation.

## QA status

`CODED_TESTED` and `CROSS_VERIFIED` for the scope above. `SOURCE_VERIFIED` is not claimed: the repository policy
requires an independent reviewer name/date in addition to source inspection and numerical evidence.
