# Frame distributed loads — verification record

Date: 2026-10-18 · Code: `Mcotis.Analysis.SpatialFrameElement` (projected loads, distributed moments),
`Mcotis.Core.FrameLoads` · Checks: `tests/Mcotis.Checks/FrameLoadChecks.cs` (group "Frame distributed loads", in the
quick and the `--full` run).

AGENTS.md asks that nothing a new solver capability computes is shown as a result before it has passed its checks
against closed forms, the archived planar solver where that covers the case, and OpenSees where it can be modelled.
This records what was checked for the two capabilities Assign → Frame → Frame Load adds to the element.

## What the element does

- **Projected loads** (SAP2000 directions 7–9, 11): the value is per unit length of the frame projected on the plane
  normal to the load direction. The element multiplies it by √(1 − (e₁·d)²) and treats it as an ordinary load per unit
  frame length. No new integration.
- **Distributed moments** (SAP2000 MyType 2): consistent nodal loads by virtual work on the rotations of the element's
  shape functions — θ₁ linear for torsion; θ₃ = u₂′ and θ₂ = −u₃′ of the Hermite cubics for bending — integrated by
  3-point Gauss, exact for a linearly varying moment. Station actions add ∫ m ds to the free body.

## Closed forms (relative tolerance 1E-9 unless stated)

| Case | Quantity | Expected |
| --- | --- | --- |
| Fixed-fixed 6 m, uniform gravity 10 kN/m | reactions, end moments | qL/2, qL²/12 |
| Fixed-fixed 6 m, triangle 0 → 10 kN/m (absolute distances) | reactions, end moments | 3qL/20, 7qL/20; qL²/30, qL²/20 |
| Simply supported 10 m, trapezoid 0 → 8 → 0 kN/m + 2 kN/m uniform + partial 3 kN/m on 7–9 m | reactions (1E-8); bending moment at every station (1E-7) | statics |
| 30° and 60° frames, pin + vertical roller, gravity and projected gravity 5 kN/m | total and roller reactions | q·L and q·L·cosθ; half at the roller |
| 30° cantilever, projected X | horizontal reaction | q·L·sin 30° |
| Horizontal cantilever, Local 2 vs Global Z trapezoid | every joint displacement | identical (Local 2 = +Z) |
| Cantilever 4 m, uniform moment about Y | tip rotation, tip deflection, end reaction, M(x) at every station | mL²/2EI, −mL³/3EI, mL, m(L − x) |
| Cantilever 4 m, uniform torque | tip twist, end reaction, T(x) at every station | mL²/2GJ, mL, m(L − x) |
| Simply supported 4 m, uniform moment about Y | reactions, rotations, bending and shear at every station | ±m couple, beam straight, M = 0, V = m |
| XZ degrees of freedom, moment about Z | compile | refused (`dof.load.inactive`), never dropped |

All pass. The global equilibrium check of every solve (`EquilibriumError ≤ 1E-8`) passes as well.

## Archived planar solver and OpenSees

- The archived planar solver covers in-plane projected loads already (`FemLoadDirection.Projected*`); the general
  solver's parity with it (`--general-solver` in the desktop checks) is unchanged by this work, which adds no path
  for loads the legacy model holds.
- OpenSees: the existing reference suite (`--full`: 12 frame cases, 11 cable solutions, modal) passes unchanged —
  max |Δu| 2.1E-15, max |Δforce| 2.1E-12. OpenSees `eleLoad -beamUniform` has no distributed moment, so distributed
  moments are verified against closed forms only. A projected or partial trapezoidal force case in OpenSees is not
  added yet (open).

## Limits

- Desktop checks (`tests/Mcotis.Desktop.Checks`) were not run for this change: the WPF project was not built in the
  environment where the change was made. Run them on Windows before merging.
- Rigid end offsets do not exist in Mcotis; when they arrive, distances stay joint to joint as in SAP2000.
