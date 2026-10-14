# Modal Eigen verification record

Implemented and checked 2026-10-13. This record covers the first supported modal capability in the standalone
system: small-amplitude free vibration of unstressed elastic frame models with lumped mass. It does not certify a
seismic design, a response-spectrum analysis, a nonlinear tangent state, or CSI solver equivalence.

## Supported formulation

`Mcotis.Analysis.ModalSolver` solves

`K φ = λ M φ`, `λ = ω²`, `f = ω/(2π)`, `T = 1/f`.

- `K` is the same elastic spatial-frame stiffness used by the verified static engine, including compiled frame
  segments and rotational end-release condensation.
- The first mass operator is diagonal lumped mass. Each compiled uniform frame segment contributes half of
  `rho A L × mass modifier` to UX, UY and UZ at each end. Explicit joint UX/UY/UZ mass and RX/RY/RZ inertia may be
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
| OpenSeesPy 3.8.0 | Independently built 1D Elastic-truss SDOF and two-DOF chains; eigenvalues, frequencies, individual and cumulative UX ratios | Passed |
| WPF end to end | 3D native building, explicit joint masses, three positive modes, nine participation rows, shape table/viewer | Passed |

The reproducible OpenSees generator is `verification/modal-eigen/generate_opensees.py`; its checked-in fixture is
`tests/Mcotis.Checks/Fixtures/modal-eigen-opensees.json`. It builds models from physical dimensions, material,
section and nodal masses and imports no Mcotis matrix. Run it with the pinned packages in
`verification/spatial-static/requirements.txt`.

## User-visible scope and refusals

The native workspace provides **Define → Modal Eigen Case…**, **Assign → Joint Mass…**, **Run → Run Analysis**, and
Modes / Modal participation / Mode shape tabs. Mode shapes and the 2D/3D viewer say relative, mass-normalized
amplitude and never label an eigenvector as physical displacement.

The following planned items remain deliberately unavailable:

- stiffness from a converged nonlinear or P-Delta case, because no verified frozen tangent endpoint exists;
- cable modes, because they require that converged prestressed tangent and a frozen taut/slack active set;
- shells, load-pattern-derived mass, consistent frame mass, rotational participation, sparse extraction, Ritz,
  response spectrum, time history, damping and eigen-buckling;
- persisted native result history. The current standalone workspace persists model input only and marks in-memory
  results current/stale by revision and analysis hash.

No unsupported request falls back to unstressed stiffness, invented mass, or an inserted restraint. The first three
limits are the blocked/deferred tickets in the modal implementation plan, not incomplete variants of the released
elastic-frame calculation.

## QA status

`CODED_TESTED` and `CROSS_VERIFIED` for the scope above. `SOURCE_VERIFIED` is not claimed: the repository policy
requires an independent reviewer name/date in addition to source inspection and numerical evidence.
