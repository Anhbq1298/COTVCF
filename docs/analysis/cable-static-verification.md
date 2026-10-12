# General static solver — cables, target forces, stages, split frames and stations: verification record

Written 2026-10-12. Code: `src/Mcotis.Analysis` — `StaticSolver` (formulation `spatial-static-2`), `CableElement`,
`SpatialFrameElement`, `ModelCompiler` (`topology-dofs-2`), `DenseAlgebra`. It replaces `LinearStaticSolver`, whose
linear frame path it keeps unchanged; that path's record is [spatial-static-verification.md](spatial-static-verification.md).

The rules the user set for this work (2026-10-12): the planar frame solver is archived; the general solver is used
everywhere; and **nothing it computes is shown as a result before it is validated**. This record is that validation.
It keeps the levels of evidence apart, as [literature.md](literature.md) requires. Nothing here is
`SOURCE_VERIFIED`: no reviewer has approved it, and the cable source texts were not read in this work — the formulas
are the ones the archived planar solver already cited. The evidence is numerical.

## Where it runs

Every analysis of Mcotis runs this solver.

- **The main window** (Run → Run FEM Analysis, Run → Optimization VCF) reaches it through
  `src/Mcotis.Desktop/Application/FemSolver.cs`. That class holds no numerical method: it hands the application's
  model to the solver and gives the results back in the application's result types. The main window's model is
  planar, so it is run with the XZ degrees of freedom.
- **The native workspace** (Edit → TemplateBuilding…, File → Open Native Model…) calls it directly, with the degrees
  of freedom the model selects.

The planar solver is no longer part of the application. It is kept, frozen, in
`tests/Mcotis.Desktop.Checks/Archive/PlanarFrameSolver.cs` as the baseline of the comparison below.

## What the solver covers

- **Frames split at points lying on them.** A point on a frame's axis between its ends (within 1e-6 m) is connected to
  it: the frame is compiled as one member per piece. Loads placed by relative distance are divided among the pieces; a
  release applies at the frame's own end only. A point within 1 mm of the axis but not on it is reported, not connected.
- **Stations along a frame.** Internal actions and the deflected line at equal intervals along every piece, as many as
  the model's frame mesh setting. They are output only: no result at a joint depends on their number.
- **Cables: tension only**, straight, with a pretension (lack of fit × EA / L). A slack cable carries nothing.
- **Cable sag** under the cable's self-weight (model setting): the shallow parabola, with Ernst's equivalent stiffness.
- **Cable target forces.** The case set to fit them adjusts the pretension of every targeted cable until its tension
  is within the model's tolerance of the target at the end of that case.
- **Staged cases.** A nonlinear static case carries the loads of the cases it continues from, and the pretension of
  the fit case in that chain.
- The DOF presets of the compiler (Full 3D, XZ, XY, YZ, custom), in three dimensions throughout.

It refuses, with a diagnostic and no result: a linear static case on a model that has cables; a linear case that
continues from another or fits target forces; a nonlinear case continuing from a linear one; target forces applied
by a case that does not fit them; a fit case with no target, with two targets on one cable, or with a target that is
not a positive tension; more than one fit case in a chain; a load on a DOF that nothing holds; an equilibrium that
does not converge or is not stable; internal forces that do not balance; everything the linear record lists (shells,
axial/shear/torsion releases, the size limits).

Target forces that are not met within the tolerance are refused by default. With the option `AcceptClosestFit` the
solver returns the equilibrium of the closest pretension found, marked as not fitted, with the reason. The main
window uses that option — it has always shown the closest pretension with a note — and the native workspace does not.

## Formulation records

| Record | What | Source as cited (not inspected in this work) | Implementation | Checked by |
|---|---|---|---|---|
| TOPO-01 | A frame is connected at every point on its axis; one Euler–Bernoulli element per piece | Application rule; the planar solver did the same within 1 mm | `ModelCompiler`, `SpatialFrameElement.AddFrameLoad` | Closed forms; the same beam modelled as two frames; OpenSees `cable-truss`; planar solver |
| STAT-01 | Actions at a section from the equilibrium of the part before it; displacements by integrating `u1′ = F1/EA`, `θ′ = (M1/GJ, M2/EI2, M3/EI3)`, `u2′ = θ3`, `u3′ = −θ2` from the I end (4-point Gauss between load discontinuities, exact for point and linear loads) | Statics and the Euler–Bernoulli beam of ALG-01 | `SpatialFrameElement.Evaluate` | Closed forms; the same frame with joints at the stations; planar solver stations |
| CABLE-01 | Straight cable: `T = max(0, T0 + k·e)`, `k = EA/L`, `e` = change of the chord length (small displacements) | Standard bar kinematics; tension-only as a convex energy: Kanno, *Nonsmooth Mechanics and Convex Optimization*, 2011 | `CableElement` | Closed forms; OpenSees; planar solver |
| CABLE-02 | Sagging cable: `T/k − a/T² = (T0 + k·e)/k`, `a = w_n²L³/24`; tangent `k/(1 + 2ka/T³)` | Irvine, *Cable Structures*, 1981; Ernst, Der Bauingenieur 40(2), 1965 | `CableElement.SaggingTension`, `Tangent`, `Energy` | Closed forms incl. the exact elastic catenary; planar solver. **No independent program.** |
| CABLE-03 | Equilibrium = minimum of the potential energy, by Newton's method with a backtracking line search and a Levenberg shift | Nocedal & Wright, *Numerical Optimization*, 2006 | `StaticSolver.Equilibrium` | Every cable check; residual, stability and balance gates at run time |
| CABLE-04 | Target forces: Newton iteration on the pretensions, finite-difference Jacobian, minimum-norm correction | Own algorithm, following the intent of a target-force load; not another program's | `StaticSolver.FitPretension`, `DenseAlgebra.MinimumNormSolve` | Closed forms (one, two interacting, two in series); OpenSees (pretension recovered); planar solver (same fitting runs) |
| CASE-01 | A continuing case carries the earlier loads; the result does not depend on the load path because the energy is convex | Follows from CABLE-01/02 | `StaticSolver.Solve` (case chain) | Closed forms; OpenSees `stage` + load sets; planar solver |

Units as in the document: kN, m; rotations in radians.

## Evidence

Everything below runs offline. Counts and differences are those of the runs of 2026-10-12.

### 1. Closed forms and independent scalar solutions

`dotnet run --project tests/Mcotis.Checks`, group *General static solver* (`GeneralStaticChecks.cs`, 1,221 checks
with the OpenSees comparison). Every expected value is worked out in the check file, not taken from the solver.

| Subject | What is compared |
|---|---|
| Split frames | `PL³/48EI` at a point on a beam; equality with the same beam modelled as two frames; a trapezoidal line load and member point loads before, after and exactly at the split; a hinged frame split at midspan is the simply supported beam (`5wL⁴/384EI`, `wL²/8` through the split) |
| Stations | Deflected line, slope, moment and shear of a simply supported beam under a uniform load; every kind of load in both bending planes with torsion on a rolled section, against the joints of the same frame with points put at the stations; a point load exactly at a station; a hinged end's own rotation |
| Tension only | A weight on a cable (`T = P`, `PL/EA`); a cable above a cantilever tip sharing load by stiffness; the same cable slack when pushed |
| Target forces | One cable: pretension `T(k_b + k_c)/k_b`; two cables on one beam through the beam's influence coefficients; two cables in series split evenly; the fitting runs kept; the closest fit when asked for |
| Stages | A case continuing from the fit case: `T + P·k_c/(k_b + k_c)`; an uplift that slackens the cable; a factor of 2 on the target pattern; cases solved together equal to cases solved alone |
| Space | The whole structure turned and moved in space: same tension and pretension, displacement turned with it; XZ preset equal to full 3D for a planar structure |
| Sag | `T³ = EA·w²L²/24` for a cable cut to its chord; sag `wL²/8T`; Ernst's ratio exactly 1/3 there; `w·cos θ` on a slope; the weight modifier; the cable equation re-evaluated from the solved displacements of a structure |
| Sag vs. exact theory | The same level cable as an exact elastic catenary (Irvine): the tensions differ by less than 0.2 % at a sag of 1.0 % of the span |
| Refusals | Each refusal listed above |

### 2. The archived planar solver, and the application's own checks

`dotnet run --project tests/Mcotis.Desktop.Checks -- --general-solver` (also in the default run;
`GeneralSolverParityChecks.cs`). The application's analysis and the archived planar solver are given the same input;
everything the application shows of a run is compared: joints, reactions, frame ends, every station along every
frame, cables, sag, and the fitting — applied pretension, each fitting run and their number.

| Model | Cases | Values | Largest difference |
|---|---|---|---|
| Cable truss, 4 bays, chords as single frames, crossed cable diagonals; no pretension | 3, some cables slack | 1,275 | 7e-16 m, 1e-11 kN |
| … with cable sag | 3 | 1,307 | 2e-15 m, 1e-11 kN, sag 3e-15 m |
| … a given pretension applied as it is, with and without sag | 3 each | 1,275 / 1,307 | 1e-15 m, 1e-11 kN |
| … target forces fitted within 10 %, 1 % and 0.001 % | 3 each | about 1,380 each | 3e-15 m, 2e-11 kN; same fitting runs (2, 2, 3) |
| … target forces that cannot be met: both give the closest pretension, after 100 fitting runs | 2 | 882 | 3e-11 m, 6e-7 kN |
| **The default model as the application runs it**: 41 joints, 38 frames, 16 cables; target forces within 10 %, sag on, 10 stations per frame piece | Pretension, SLS, ULS | 11,361 | 1e-9 m, 1.5e-4 kN in cables, 8e-4 kN·m in frames — the planar solver's own convergence tolerance (1e-7 of its largest force) |

The largest downward deflection of the default model is the same in both: Pretension 22.9 mm, SLS 243.8 mm,
ULS 392.5 mm. (The 39.6 mm / 46.3 mm quoted in the standalone plan are reproduced by neither; where those came from
was not established.)

The checks that were written for the application's solver now run through the general solver:

- `--fem` — 58 checks against closed forms (`PL³/48EI`, `5wL⁴/384EI`, `wL²/8`, reactions and equilibrium, stations
  against an unmeshed frame, superposition, the cable sag equation, Ernst's formula, fitting and its failure note).
- `--fem-sap` — the stored SAP2000 study of 135 candidates, 4,995 values: total vertical reaction within 0.002 %,
  cable forces within 3.82 %, deflections within 3.22 %, reactions within 1.2 % of the largest SAP2000 value — the
  same figures the planar solver gave. The remaining difference is what SAP2000 analysed and this solver does not:
  large displacements, catenary cables, shear-deformable frames.
- The default offline run of `tests/Mcotis.Desktop.Checks` — the application's whole suite.

### 3. OpenSees

`verification/cable-static/generate_opensees.py` builds the models from the physical specification
`tests/Mcotis.Checks/Fixtures/cable-static-spec.json`; it never reads Mcotis matrices or results. A cable is a `Truss`
element with a material that has no stiffness in compression, in an `InitStrainMaterial` equal to pretension / EA.
OpenSees 3.8.0 (openseespy 3.8.0.0), Newton, `NormUnbalance` 1e-9.

Four models — a cable on a cantilever tip without and with pretension, a mast with three stays in space, a planar
cable truss whose chords are single native frames — 11 solutions, 626 values: largest difference 2e-12 m and
6e-8 kN, the same three cables slack. The reference is given each cable's pretension; the native model is given the
resulting tensions as targets and finds that pretension again. Stations were not compared with OpenSees.

### Gates at run time

A result is returned only if: the linear solution's scaled backward error is within 1e-10, or the cable
equilibrium's out-of-balance force is within 1e-9 of the largest load or pretension; the tangent at the equilibrium
is positive definite on every DOF that something holds; and the internal forces of the whole model have no
resultant force or moment (within 1e-8). A translation held by slack cables only is reported as undetermined, not
given a number.

## What this evidence does not show

- **Sag has no independent program behind it.** The planar solver uses the same formula, so agreement with it shows
  the implementation, not the theory. The theory was compared with the exact catenary for one level cable only.
- **Small displacements.** There is no geometric stiffness: a straight cable has no stiffness across its own line, and
  no P-Delta. The differences from SAP2000 quoted above come from that.
- **The fitting tolerance is the model's.** At the default 10 % the result is the first fitting run inside 10 %, not
  the state that meets the targets exactly.
- **Loads along a cable in the main window.** The application takes such a load as before: its resultant goes to the
  cable's ends, and with cable sag on its part normal to the chord makes the cable sag. The solver's cable sags under
  its self-weight times one factor per load pattern, so with sag on the loads along the cables must be in the same
  proportion to each other in every case — as self-weight is — and must not act across a vertical cable. A model
  that is otherwise is refused with that explanation; the planar solver accepted it.
- **Differences from the planar solver, by decision:** a factor other than 1 on the cable target-force pattern scales
  the target (the planar solver scaled the fitted pretension); a chain with two fit cases is refused in the native
  workspace. Neither arises in the comparisons above.
- **Connection tolerance.** The native workspace connects a point within 1e-6 m of a frame and warns up to 1 mm. The
  main window keeps the application's rule — a joint within 1 mm is on the frame — by passing each frame on as its
  pieces.
- A cable target force's relative distance is not used: in this theory the tension is one value along the cable.
- Dense algebra: 600 free / 2,400 total DOFs in the native workspace; the main window raises that to about 1,000
  joints, where the memory of a dense matrix ends. No shells, P-Delta, large displacements or modal analysis.
- No reviewer has approved these records.

## Reproduce

```
dotnet run --project tests/Mcotis.Checks
dotnet run --project tests/Mcotis.Desktop.Checks -- --general-solver
dotnet run --project tests/Mcotis.Desktop.Checks -- --fem
dotnet run --project tests/Mcotis.Desktop.Checks -- --fem-sap
python verification/cable-static/generate_opensees.py     # needs verification/spatial-static/requirements.txt
```
