# Spatial linear static engine — verification record

Written 2026-10-12. Codex implemented the kernel, solver, benchmark specification and OpenSeesPy comparison.
Claude completed the regression, added the free-body equilibrium check and inspected the cited rendered book pages.
The current implementation has been checked again after the project-folder reorganization; the evidence below
applies to the tested frame cases and does not establish readiness for general production models.

This record keeps the levels of evidence apart. Nothing here is `SOURCE_VERIFIED` in the sense of
[literature.md](literature.md): that status needs an independent reviewer's approval, which has not been given.

## What the engine is

`src/Mcotis.Analysis`: `SpatialFrameElement`, `LinearStaticSolver`, `DenseAlgebra` (formulation
`spatial-eb-linear-1`). A small dense engine for validating the 3D formulation, not the production solver.

- Prismatic Euler–Bernoulli frames, small displacements, one element per native frame. No shear deformation, no offsets.
- Loads: point forces and moments; frame point loads; linearly varying frame line loads over any part of the
  length; self-weight (weight density × section area × weight modifier × the pattern's self-weight multiplier, along −Z).
- Member-end moment releases M2 / M3 by static condensation; the released end rotations are recovered.
- Supports with zero displacement; the DOF masks of the compiler (Full 3D, XZ, XY, YZ, custom).
- Reactions come from the full, uncondensed equations `K u − P`. Constraint forces of DOFs a mask suppresses are
  reported apart from support reactions.
- Units as in the document: kN, m; rotations in radians.

It refuses, with a diagnostic and no result: nonlinear or staged load cases, cable target fitting, any model that holds
a cable, shells (in the compiler), axial / shear / torsion releases, a point lying inside a frame's length, zero bending
stiffness, zero torsional stiffness outside a plane preset, more than 2400 full or 600 free DOFs, a stiffness matrix
that is not symmetric or not positive definite at the pivot tolerance, and a scaled residual above 1e-10. A joint
rotation that no stiffness reaches (all connected ends released) is left out and reported, not stabilised.

## Conventions the code uses

| Item | Convention |
|---|---|
| Joint DOF order | `UX, UY, UZ, RX, RY, RZ` |
| Element DOF order | local `u1, u2, u3, r1, r2, r3` at I, then at J |
| Local 1 | from I to J |
| Local 2 | global Z made normal to local 1; for a member within about 8° of vertical (\|cos\| ≥ 0.99), global X made normal to local 1 |
| Local 3 | local 1 × local 2 (right-handed) |
| Local-axis angle | rotates local 2 and 3 about local 1, right-handed, in degrees |
| `Inertia33` | bending in the 1–2 plane (rotation about local 3); `Inertia22` in the 1–3 plane |
| Torsion | `G J` with `G = E / (2 (1 + ν))` |
| End actions | forces the joints exert on the member ends, in local axes: `K_m u − P_equivalent` |

The local-axis rule is a project convention (it is the usual default of frame programs), not something taken from the
book. The benchmark specification states each member's orientation vector explicitly, so the OpenSees models do not
depend on this rule being guessed.

## Source inspection

Source: S. E. O'Hara and C. H. Ramming, *Numerical Structural Analysis*, Momentum Press, 2015 (register entry B1).
The pages below were read from the rendered PDF on 2026-10-12.

| Record | Passage | What it states | Code | Agreement |
|---|---|---|---|---|
| ALG-01 | eq. 4.33, printed p. 187 | The 12×12 elastic member stiffness, order `Δx Δy Δz θx θy θz` at i then j | `SpatialFrameElement.LocalStiffness` | Every entry agrees in position and magnitude with x, y, z ↔ 1, 2, 3, `I_z` ↔ `Inertia33`, `I_y` ↔ `Inertia22`, `I_x G` ↔ `G J`. Signs agree except one printed entry — see below |
| ALG-02 | eq. 4.34, p. 188 | `[K_g] = [R]ᵀ [K_m] [R]` | `GlobalStiffness`, `ToLocal`, `ToGlobal` | Same congruence, applied per 3-vector with the member's direction cosines |
| ALG-03 | eqs. 4.35 – 4.38, p. 188 | Loads as joint loads minus rotated fixed-end actions; solve with restrained rows and columns removed; reactions `K_g Δ_g +` fixed-end actions; member forces `K_m R Δ_g +` fixed-end actions | `LinearStaticSolver.Solve`, `SpatialFrameElement.Recover` | Same procedure. The code carries equivalent nodal loads, which are the fixed-end actions with the opposite sign |
| ALG-05 | §5.3, p. 217; §5.1, pp. 205–206 | A released member uses its released stiffness in the assembly and in force recovery, with modified fixed-end actions | constructor of `SpatialFrameElement`, `FinalizeLoad`, `Recover` | Same intent by a different route: the code condenses the released rotations out of eq. 4.33 instead of using the book's tabulated released matrices. Those tables were not compared entry by entry |
| LOAD-01 | [TU Delft, Euler–Bernoulli beam elements](https://interactivetextbooks.citg.tudelft.nl/computational-modelling/structural_linear/euler_bernouilli.html), eqs. 4.19–4.30; B1 §3.4, pp. 109–110, Table 3.11 | Hermite interpolation and virtual-work nodal loads; three-point Gauss rule | `AddPointLoad`, `AddDistributedLoad` | Source passages inspected. Local 1–3 bending uses the negative local-2 rotation convention. Uniform, point and partial triangular load benchmarks pass; independent reviewer approval remains pending |

The dense solver uses diagonal equilibration and the standard symmetric-positive-definite Cholesky form
`A = L Lᵀ`, as described in the [LAPACK DPOSV specification](https://www.netlib.org/lapack/double/dposv.f).
This is an independent small dense implementation, not LAPACK's blocked production routine. It has hand-solvable,
scale-change and singular-system tests; it does not provide a condition estimate or exhaustive mechanism diagnosis.
The inspected B1 PDF's SHA-256 is `92691F42103D17127A5EC96FCA3154B2D6C23A06EDF1048DC4230E30F596DEED`.

**One printed sign does not match the code.** In eq. 4.33 the entry in row `Δiz`, column `θiy` is printed
`+6 E I_y / L²`, while its mirror entry, row `θiy`, column `Δiz`, is printed `−6 E I_y / L²`. A stiffness matrix is
symmetric, so the printed pair is inconsistent with that formulation. No publisher erratum was located in this work.
The code has `−6 E I_y / L²` in both places. The evidence supporting the code's
sign is the right one is numerical: the independent integration below reproduces it, the rigid-body rotation about
local 2 gives zero force only with it, and bending in the 1–3 plane matches the closed forms and OpenSees.

## Numerical evidence

All of it runs in `dotnet run --project tests/Mcotis.Checks` (group *Spatial linear static engineering benchmarks*,
1083 checks) unless stated.

| Evidence | What it shows | Result |
|---|---|---|
| Independent integration of the element | All 144 entries of the local stiffness from constant axial / twist strain and 2-point Gauss integration of the Hermite curvatures, without using the element's matrix | agree to 1e-12 relative |
| Rigid-body motions | The six rigid-body motions of the element produce no force | zero to 1e-8 |
| Closed forms | Cantilever under each of the six tip actions; uniform load; partial triangular load; propped cantilever with a released end; simply supported two-element beam | agree to 1e-9 relative |
| Free-body equilibrium | For all 12 benchmark models: applied loads, reactions and suppressed-DOF forces sum to zero force and zero moment about the origin, the applied resultants worked out from the stored loads | zero to 1e-8 / 1e-7 |
| Transformations | Orthonormal right-handed axes; a rigid rotation of model and loads rotates the response; stiffness scaling | pass |
| Refusals | Tested nonlinear/staged setup, unsupported shear release, size limit, zero torsion, loaded released rotation, interior joint, nonfinite assembly and singular/near-singular cases return no result | pass for the listed checks; not exhaustive branch coverage |
| OpenSeesPy | 12 models built from the physical specification with `elasticBeamColumn` and the `Linear` transformation: displacements, reactions and local end actions, 516 numbers | largest difference 2.1e-15 in displacement or rotation, 2.1e-12 in force or moment |
| Legacy planar solver | Six XZ models solved by the existing `PlanarFrameSolver` and by this engine with the XZ mask: point, line and member point loads, end releases, a portal, section roll of 30° and 90° (`dotnet run --project tests/COT_VCF.Checks -- --spatial-frame-regression`) | displacements to 1e-10, reactions and end actions to 1e-8 absolute + 1e-7 relative |

The OpenSees reference is reproducible: `verification/spatial-static/generate_opensees.py` with the versions in
`requirements.txt` (OpenSees 3.8.0, openseespy 3.8.0.0, numpy 2.5.4) regenerated a file identical to the stored
fixture on 2026-10-12.

## What this does and does not establish

- OpenSees' `elasticBeamColumn` is the same theory (linear Euler–Bernoulli). Agreement to round-off shows the two
  implementations compute the same thing; it does not validate the theory for a given structure, and two programs
  agreeing is not proof of either.
- The orientation vectors in the specification were chosen to give the native default axes. The comparison therefore
  checks the stiffness, transformation, loads and releases for those axes; it is not an independent check of the
  default-axis rule itself.
- The legacy comparison covers the planar cases listed. It does not cover cables, target-force fitting, cable sag,
  staged cases, meshing with interior joints, or the default model — the engine refuses those.
- Only moment releases are implemented. Release combinations that make a member a mechanism are caught by the
  factorisation, not classified.
- The dense factorisation and its pivot test are adequate for these sizes. Conditioning is not estimated, and a failed
  pivot is reported as a location, not as a diagnosis of the mechanism.
- No reviewer has approved the source records. The equation numbers above were read by the implementing agents only.

## Still open

Cables and target-force fitting; shells; shear-flexible frames; offsets; axial, shear and torsion releases; frames
with interior joints (automatic splitting); a sparse solver and instability diagnostics; P-Delta; mass and modal
analysis; the desktop dialog for degrees of freedom. The desktop application still analyses with `PlanarFrameSolver`;
nothing in the user interface reaches this engine yet.
