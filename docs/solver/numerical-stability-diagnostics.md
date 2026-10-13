# Numerical stability diagnostics of the static solver

Written 2026-10-13 with the diagnostics themselves. It records what the solver reports of the conditioning of the
stiffness it factorizes, the mathematics behind each number, the thresholds, what the run monitor shows of it, how it
differs from the convention of CSI's programs, what it cannot tell, and the checks it has passed.

Levels of evidence are kept apart, as in [../analysis/literature.md](../analysis/literature.md). The formulas below are
checked numerically against closed forms and against independent computations (§8). The cited texts are named from the
literature and were **not inspected page by page** for this record; nothing here is `SOURCE_VERIFIED`. Nothing has been
compared with a run of SAP2000 or ETABS.

## 1. What it is, and where

| Layer | Code | Role |
|---|---|---|
| Numerics | `src/Mcotis.Analysis/StabilityDiagnostics.cs` — `StiffnessConditioning`; `DenseAlgebra.cs` — `ScaledCholesky.ScaledInverseDiagonal` | The estimate and the rank-revealing factorization, on a matrix |
| Solver | `StaticSolver.cs` — `Run.Stability`, the three places a stiffness is factorized or refused | Assessment by joint and degree of freedom: `StaticStability` on `StaticDiagnostics`, `StaticOutcome` and `StaticProgress` |
| Application | `src/Mcotis.Desktop/Application/FemStability.cs` — `FemStabilityReport`, `FemStabilityText` | Joint names and global coordinates; the words used for each condition |
| Monitoring | `Application/FemRunMonitor.cs` | Status and log of every load case of a run |
| View model / view | `Presentation/FemRunMonitorViewModel.cs`, `FemRunMonitorWindow.xaml` | The run monitor |

The solver passes structured values upwards — an enum for the condition, a nullable number for the digits — never a
sentence for the screen. `Mcotis.Analysis` refers to `Mcotis.Core` and the runtime only (`Mcotis.Checks`, Independence).

**The diagnostics read the stiffness and change nothing of a solution.** The solution path — assembly, natural-order
scaled Cholesky, Newton iteration, residual and equilibrium checks, every tolerance — is as it was; `--general-solver`,
`--fem` and `--fem-sap` give the numbers they gave before. Cost: one pass over the factor per case, n³/6 operations,
the same order as the one factorization it follows (a case with cables makes tens of factorizations while it iterates).

## 2. Mathematical formulation

Notation: K is the stiffness on the n solved degrees of freedom of a case — the frame stiffness for a case without
cables, the tangent stiffness at the converged state for a case with them. D = diag(K), H = D^(-1/2) K D^(-1/2) is K
scaled to a unit diagonal; ε = 2.22·10⁻¹⁶.

### 2.1 The estimate of digits lost

For every solved degree of freedom i

    cᵢ = Kᵢᵢ · (K⁻¹)ᵢᵢ = (H⁻¹)ᵢᵢ ≥ 1,        digits lost dᵢ = log10(cᵢ).

Three readings of the same number:

1. **A stiffness ratio.** (K⁻¹)ᵢᵢ is the movement of i under a unit load on i with every other degree of freedom free;
   1/Kᵢᵢ is that movement with every other one held. cᵢ is the stiffness of i with the rest of the structure held, over
   its stiffness with the rest free.
2. **A cancellation.** 1/(K⁻¹)ᵢᵢ is the Schur complement of K on i: sᵢ = Kᵢᵢ − kᵀ K_rest⁻¹ k. It is formed by taking
   from Kᵢᵢ a quantity that agrees with it in its leading log10(cᵢ) decimal digits. Those digits cancel; what is left
   carries that many fewer significant digits. That is a statement about floating-point subtraction, not an analogy.
3. **A pivot ratio that does not depend on the order.** In an LDLᵀ or Cholesky factorization the pivot of the equation
   eliminated *last* is exactly its Schur complement, so its diagonal-decay ratio Kᵢᵢ / pivotᵢ equals cᵢ. For an
   equation eliminated earlier the pivot is the Schur complement on a leading block only, which is larger, so its
   ratio is smaller. cᵢ is therefore the largest pivot ratio equation i can show under any ordering, and the ratio it
   would show were it eliminated last.

Bounds, for H symmetric positive definite with a unit diagonal (its eigenvalues sum to n, so 1 ≤ λ_max ≤ n):

    maxᵢ cᵢ ≤ ‖H⁻¹‖₂ = 1/λ_min ≤ κ₂(H),        maxᵢ cᵢ ≥ trace(H⁻¹)/n ≥ 1/(n·λ_min) ≥ κ₂(H)/n².

So the largest estimate brackets the condition number of the scaled stiffness within n². κ₂(H) is the number that
governs the accuracy of a Cholesky solution: the relative error of the scaled solution D^(1/2)x is bounded by about
κ₂(H)·n·ε, and diagonal scaling is within a factor n of the best possible diagonal scaling of a positive definite
matrix (Demmel 1989; van der Sluis 1969; Higham 2002, §10.1 and §7.3).

**What it is not.** It is not `log10(largest pivot / current pivot)`: a ratio against the globally largest pivot
measures how differently the equations are scaled (kN/m beside kN·m/rad), not accuracy, and it is not used anywhere.
It is not a guaranteed forward error of a particular displacement either: it bounds what cancellation can take away,
it does not say that it was taken (§7). And it is not CSI's number (§6).

**How it is computed.** With K = D^(1/2)·L·Lᵀ·D^(1/2) from the factorization the solver has already made,
(H⁻¹)ᵢᵢ is the squared length of column i of L⁻¹ (`ScaledInverseDiagonal`: one triangular solve per column, one
column held at a time). Its own accuracy: (H⁻¹)ᵢᵢ is computed with a relative error of the order of κ₂(H)·n·ε, so the
digits are good to a few hundredths up to about 12 and to a few tenths between 12 and 13.5.

### 2.2 Singular, nearly singular, not positive definite

The solver's natural-order factorization refuses a case when a scaled pivot is not above `RelativePivotTolerance`
(10⁻¹²) — unchanged. Because pivotᵢ·cᵢ ≥ 1 in scaled terms, every such refusal is at a degree of freedom that has lost
at least 12 digits. The diagnostic then says *which kind* of refusal it is, by factorizing H again **with diagonal
pivoting** — at each step the largest remaining diagonal is eliminated next (the Cholesky factorization of a
semi-definite matrix: Higham 1990; LAPACK `xPSTRF`). It stops when the largest remaining diagonal is not above

    τ(n) = (n + 100)·ε.

n·ε is the stopping tolerance LAPACK uses for a matrix that is given exactly. The stiffness is not given exactly: its
entries are transformed, assembled and scaled in floating point, to roughly ten ε each, in rows whose absolute sum is
of the order of ten; 100·ε is the allowance for that. For n = 100, τ = 4.4·10⁻¹⁴ (13.4 digits); for n = 3000,
6.9·10⁻¹³ (12.2 digits). **This allowance is an engineering estimate, not a proven bound** (§7). One measurement
of why n·ε alone is too tight: the swaying portal of §8, an exact mechanism with 8 equations, leaves a pivot of
−2.9·10⁻¹⁵ in the solver's factorization — above n·ε = 1.8·10⁻¹⁵, and a factor 8 below τ = 2.4·10⁻¹⁴.

| What the pivoted factorization finds | State | What is reported |
|---|---|---|
| It completes: every pivot above τ | `NearlySingular` | The stiffness is positive definite at double precision. Every equation has its estimate dᵢ, a finite number above 12 at the worst ones. **No ∞.** |
| It stops with r < n pivots; what remains is within ±10·τ | `Singular` | n − r independent movements without stiffness. Each remaining equation q gives one: y = (−L₁₁⁻ᵀ·l_q, e_q). A degree of freedom *moves in* a mode when its component, in the energy scaling yᵢ = √Kᵢᵢ·uᵢ, exceeds 10⁻⁶ of the largest. Those are reported ∞. The others get **no number**: K⁻¹ does not exist. |
| What remains has a diagonal below −10·τ | `NotPositiveDefinite` | A negative pivot, where it was found. No digits. |

Two exact cases need no factorization:

- **No stiffness at all.** Kᵢᵢ = 0 exactly: no member contributes to that movement and no restraint holds it. For a
  positive semi-definite K the whole row is then zero, the equation stands alone, and K is singular in exact
  arithmetic. Reported ∞ at that degree of freedom (`NoStiffness`). The equations it does not touch keep their
  estimates.
- **Slack cables only.** The same in the tangent at the converged state of one case: the cables that reach the joint
  are slack there (`SlackCablesOnly`).

So **∞ is reported in exactly three situations**: an exactly zero diagonal stiffness; a tangent diagonal that is zero
because the cables there are slack; and a movement in a mode the stiffness does not resist at double precision.
A pivot that is merely small — anything the pivoted factorization still completes — is a finite number.

Why the modes and not the failing pivot: the equation at which a natural-order factorization fails is the *last* of a
mechanism in the numbering of the model, which says where the numbering ends, not where the mechanism is. The null
space of K does not depend on any ordering; the degrees of freedom with a component in it are the ones that move.

### 2.3 Cases with cables

- At the converged state the tangent **without** the small stiffness the iteration lends slack cables is assessed —
  the same matrix the solver already factorizes for its stability check.
- When an equilibrium iteration cannot go on because a degree of freedom has no stiffness even with that small
  stiffness, the assessment is of that iteration (`Stage = EquilibriumIteration`, with its number): the zero
  diagonals are reported ∞, and **nothing else is estimated** from a tangent that was regularized for the search.
- An iteration that stalls or reaches its limit has no assessment at all (`Stability = null`). Non-convergence is not
  turned into a statement about the stiffness.

### 2.4 A movement without stiffness that the loads do not act on

A singular stiffness has no result — unless the solver is asked to go on (`StaticOptions.HoldUnloadedMechanisms`;
the main window asks, the native workspace does not; user, 2026-10-13: "ETABS can run regardless") and the
singularity is of one kind only: movements the stiffness does not resist (the modes of §2.2), nothing negative, and
no equation that stands alone without any stiffness.

- **What is solved.** K is positive semi-definite with K·yₖ = 0 for the movements yₖ. K·u = f has solutions exactly
  when the loads do no work on them, yₖᵀ·f = 0, and they are then u + Σ cₖ·yₖ: one deformation, with any amount of
  those movements. The solver takes the member of that family in which the equations left over by the pivoted
  factorization do not move (`StiffnessConditioning.SolveHeld`): it factorizes with diagonal pivoting exactly as the
  assessment does, holds those equations at zero and solves the rest. Every member has the same strains and so the
  same forces; only the amount of the movements differs, and it is given as zero.
- **What decides.** Nothing is assumed about the loads. The residual of a held equation is the work of the loads on
  that movement, so the usual test — the scaled backward error of the *whole* system, the held equations included,
  within the accepted tolerance (10⁻¹⁰) — is the test that the loads leave the movements alone. A case that fails it
  has no equilibrium and is refused as unstable, exactly as without the option. The resultant of the internal forces
  is checked as for any result.
- **With cables.** The equilibrium is iterated as always. A converged state meets every equilibrium equation to the
  iteration's tolerance, those of the movements its tangent does not resist included; with nothing negative in that
  tangent the state is an equilibrium, neutral along those movements, and it is accepted instead of being withheld.
- **What is reported.** The case has its result and reads Converged with Warnings. Its stability stays what it is:
  singular, the movements counted, every degree of freedom that moves in them ∞ and listed — with
  `StaticStability.MovementsHeld`, the remark "possible mechanism, not loaded: held", a warning
  (`static.mechanism.held`) and a line in the log. It is never silent.
- **What is still refused.** A movement the loads act on; a negative pivot; a degree of freedom that nothing holds at
  all — a zero diagonal, a joint only slack cables reach (the rule of 2026-10-12 for cable joints stands).

This is not claimed to be what CSI's programs do internally, which has not been inspected. The user's observation is
that ETABS completes the analysis of such a model with warnings; this gives the same outcome for the case that
matters here, a model in a plane analysed in 3D, and it states its condition: the loads do no work on what is held.

## 3. Interpretation

| Solver outcome | Run monitor status | Numerical stability |
|---|---|---|
| Result returned, no degree of freedom at or above the warning threshold, no warning | **Converged** | Passed · Instabilities 0 |
| Result returned, and: a degree of freedom at or above the warning threshold, or cable target forces not met (closest pretension used), or a rotation left out of the equations | **Converged with Warnings** | as assessed |
| `static.nonlinear.noConvergence`, `static.nonlinear.stalled` | **Not Converged** | Not assessed |
| `static.matrix.unstable` | **Numerical Instability** | `Singular` / `NearlySingular` / `NotPositiveDefinite` |
| `static.residual` or `static.equilibrium` (the stiffness was factorized, the solver's accuracy checks then withheld the result) **and** degrees of freedom are reported | **Numerical Instability** | `PositiveDefinite`, with the digits lost |
| the same without anything reported; any other refusal | **Failed** | as assessed, or not assessed |
| Failed while the cable target-force fit it continues from was being fitted | **Failed** — "the fit this case continues from failed" | that of the fit |
| Not reached / stopped | **Pending**, **Running**, **Cancelled** | — |

"Instabilities: 0" counts degrees of freedom reported as severe or singular: none was detected. It says nothing of
an equilibrium residual, which is shown beside it as measured (`1.88E-15`, `1.92E-06 kN (accepted 1.15E-04 kN)`),
never as zero.

Remarks use the solver's classification, in these words: *Numerical accuracy warning* (warning band), *Ill-conditioned*
(severe, solved or withheld), *Nearly singular* (severe, refused), *Singular (∞) — no stiffness and no restraint*,
*Singular (∞) — held by slack cables only*, *Singular (∞) — possible mechanism*, *Not positive definite (negative
pivot)*. A movement without stiffness is called a **possible** mechanism: the diagnostic is numerical, and a
stiffness contrast beyond what double precision holds looks the same (§7).

## 4. Thresholds

| Quantity | Default | Where | Meaning |
|---|---|---|---|
| Warning | 6 digits | `StabilityThresholds.WarningDigits` | d ≥ 6: numerical accuracy warning |
| Severe | 11 digits | `StabilityThresholds.SevereDigits` | d > 11: severe |
| Pivot tolerance | 10⁻¹² | `StaticOptions.RelativePivotTolerance` | Unchanged: the solver refuses the case |
| Rounding level | (n + 100)·ε | `StiffnessConditioning.RoundingLevel` | Below it a remainder is not told from zero |
| Mode participation | 10⁻⁶ | `StiffnessConditioning.ModeParticipation` | Share of the largest component from which a degree of freedom counts as moving |

d < 6 is normal, 6 ≤ d ≤ 11 a warning, d > 11 severe; the comparison is made on the value as computed, fractions
included (5.9996 is normal, 11.000001 severe). The two reporting thresholds are options — `StaticOptions.Stability`,
and `FemSolver.StabilityThresholds` for the main window — and change what is listed, never a result
(`StablePlaneFrame`: results identical digit for digit at thresholds 6 / 11 and 10⁻⁹ / 11). **There is no control for
them in the application yet**; the run monitor's note states the ones in force. The last two rows are constants of
the diagnostic.

The run monitor shows digits with one decimal, and with more where one decimal would read as the other side of a
threshold (5.96, not 6.0).

## 5. Joints and degrees of freedom

Equation i of the reduced system is `model.Dofs[free[i]]`: a point identity and one of UX, UY, UZ, RX, RY, RZ. The
solver's factorization keeps the natural order, so there is no permutation to undo; the pivoted factorization of §2.2
carries its own permutation and maps back through it. The application turns the point identity into the joint's own
name and global coordinates [m] (`FemStabilityReport.From`); the main window gives its joints their own names in the
solver's model, so the name shown is the name in the model.

The table always has the six columns. What a cell holds:

| Cell | Meaning |
|---|---|
| a number | Digits lost, an equation with an estimate |
| ∞ | An equation at which a singularity was identified (§2.2) |
| N/A | On, but without an estimate: an equation of a singular stiffness outside the movements found; an equation of an iteration-stage assessment; a rotation left out of the equations |
| R | Restrained by a support: not an equation |
| - | Inactive: switched off by the plane of the model (UY, RX, RZ in the XZ plane), or not used by any member at the joint (the rotation of a joint only cables reach) |

Only solved degrees of freedom are ever assessed, so nothing is reported against an inactive or a restrained one.
Member-end moment releases are condensed inside the frame element before assembly; a joint whose every frame end is
released has a rotation without stiffness, which the solver leaves out with a warning when it is unloaded (N/A, and
the case reads Converged with Warnings) and reports as an exactly zero stiffness when a moment loads it (∞). The
model has no joint constraints, no condensed or dependent degrees of freedom at joints, and no disconnected joint
reaches the solver (the compiler refuses one).

A joint is listed when at least one of its degrees of freedom is at or above the warning threshold; its other
solved degrees of freedom are listed with it, with their own values.

## 6. Differences from CSI's convention

What CSI publishes (knowledge-base pages listed under References; no claim is made about the internals of its
solvers): the digits lost are estimated *during the reduction of the stiffness matrix*, from the magnitudes of the
values being subtracted; the count and its location depend on the order in which the equations are solved; the
standard solver reports them per equation with the joint and degree of freedom, the advanced solver only that
digits were lost; nothing is reported below 6, 6 to 11 is informational, above 11 gives an instability warning.

| | CSI, as documented | MCOTIS |
|---|---|---|
| Reporting bands | < 6 nothing · 6–11 informational · > 11 warning | The same bands, as defaults that can be changed |
| The number | Lost digits found during the reduction, in the solution order | log10(Kᵢᵢ·(K⁻¹)ᵢᵢ), after the factorization, independent of the order (§2.1) |
| Relation | — | For one matrix, never smaller than a reduction-time ratio at the same degree of freedom, and equal to it for the equation eliminated last. **Not validated against a CSI program**: the two numbers have not been compared on any model |
| Location | The equation where the loss shows; depends on reordering | The degree of freedom the number belongs to; for a singular stiffness, every degree of freedom that moves in a mode without stiffness |
| ∞ | Not a CSI convention | A singularity identified as in §2.2 |
| Results with lost digits | Reported; the analysis goes on up to the instability warning | The solver's own checks — scaled backward error ≤ 10⁻¹⁰, resultant of internal forces ≤ 10⁻⁸ — withhold a result earlier. In the verification chain of §8 results are returned up to 8 digits lost and withheld from 9; where that falls depends on the model |
| Refusal | — | A pivot not above 10⁻¹² of its diagonal: no result (unchanged) |

The practical consequence of the last two rows: in MCOTIS the band "6 to 11, results available" is narrower than
in CSI's programs, and "severe, above 11, with results" has not been observed — such a case is withheld or refused.

## 7. Limitations

- **Dense.** The estimate costs n³/6 and the failure analysis n³/3, on the dense matrices of this engine (limits:
  600 free degrees of freedom in the native workspace, 3,000 in the main window). A sparse engine will need another
  way to the diagonal of the inverse (selected inversion, or a stochastic estimate), to be validated on its own.
- **An estimate of what can be lost, not of what was.** dᵢ bounds the cancellation in the stiffness of i. A
  particular load can excite the ill-conditioned direction weakly and lose fewer digits. §8 shows the estimate
  tracking the loss the solver measures on itself in one chain of models; that is evidence for that chain.
- **Singular or beyond double precision cannot be told apart.** A remainder below τ is reported as singular. A real
  stiffness contrast above about 10^13.5 (small models; less for large ones) leaves such a remainder too
  (`SingularMatrices`: 10¹³ is a number, 10¹⁶ is ∞). Hence "possible mechanism".
- **The allowance in τ is estimated**, from the arithmetic of assembly, not proven. Too small, and an exact mechanism
  in a large model could be reported as nearly singular with about 13 to 14 digits — still severe, still refused,
  but not ∞. Too large, and a nearly singular stiffness would be reported ∞.
- **Diagonal pivoting is not a guaranteed rank revealer.** Matrices exist on which it overestimates the rank
  (Higham 1990). None is known among stiffness matrices; none has been looked for systematically.
- **Mode participation is a threshold.** A degree of freedom moving by less than 10⁻⁶ of the largest (in the energy
  scaling) is not listed. With several movements without stiffness the modes found are a basis of them: the *set* of
  degrees of freedom is what is reported, not which mode each belongs to.
- **A negative pivot is located by the elimination**, and that location does depend on the order. Frames and
  tension-only cables give a positive semi-definite stiffness, so this case is reached only by rounding today; it is
  checked on matrices, not on a model. It will matter once geometric stiffness (P-Delta, buckling) exists.
- **The joint coordinates are those of the model as built**; the displaced position is not used.
- **The native workspace** (Edit → TemplateBuilding) receives the same `StaticStability` with every outcome but does
  not show it yet: its result views are unchanged.
- **Not assessed**: cases refused before they are solved, and iterations that do not converge.
- **A held movement (§2.4) is as good as the rank the pivoted factorization finds.** A stiffness whose rank it
  overestimates is not held but refused, which is the safe side; one whose rank it underestimated would have an
  equation held that carries load — and then fail the backward error, so it is refused too. The displacement along a
  held movement is not a result: it is reported as zero, and any other amount is an equally valid equilibrium. Holding
  is not offered in the native workspace yet, and a comparison with another program's treatment has not been made.

For analyses that do not exist yet: a nonlinear static case with load steps would carry the step in
`StaticStability` beside `Stage` and `Iteration`; geometric stiffness makes `NotPositiveDefinite` a physical state
(a load above the buckling load); a modal analysis factorizes a shifted K − σM, to which the same estimate applies
with a different meaning. None of that is implemented, and the monitor shows "Not assessed" for what gives no
assessment.

## 8. Verification

`dotnet run --project tests/Mcotis.Checks` — group *Numerical stability diagnostics* (165 checks, no SAP2000, no WPF);
`dotnet run --project tests/Mcotis.Desktop.Checks -- --run-monitor` (53 checks, the application's solver through the
monitor and its view model; also in the `-- --full` run); `-- --run-monitor-ui` (24 checks, the window). Models are solved in the checks; no
value shown is supplied by a check.

### The estimate

| Check | Expected, and from where | Result |
|---|---|---|
| Two springs in series, k₂/k₁ = 1, 10³, 10⁸, 10¹¹ | K⁻¹ in closed form: c = 1 + k₂/k₁ for both | within 10⁻⁴ digits |
| Full 7 × 7 matrix with rows of very different size | log10(Kᵢᵢ·(K⁻¹)ᵢᵢ) from an inverse by Gauss–Jordan elimination in the check | within 10⁻⁸, from the natural-order factor and from the pivoted one |
| The same matrix with its equations reversed | Each estimate moves with its equation | unchanged to 10⁻⁸ |
| H = [[1, r], [r, 1]], r up to 1 − 10⁻⁹ | c = 1/(1 − r²); κ₂/n² ≤ c ≤ κ₂ | holds |
| Cantilever tip, XZ plane | 12EI/L³, 6EI/L², 4EI/L: c = 4 for UZ and RY, 1 for UX — whatever E, I, L | log10 4 and 0 to 10⁻¹² |
| Cantilever tip, 3D | UY, UZ, RY, RZ: log10 4; UX, RX: 0 | to 10⁻¹² |
| Space frame, joint J3: UX, UZ, RX, RY | log10 of its flexibility with the model free over that with every other degree of freedom held — two analyses by the solver itself, unit load | within 10⁻⁷ digits |

### The loss it predicts, measured

A soft frame to a support and a stiff one beyond it, axially two springs in series, area ratio R. Estimate at UX of
both joints: log10(1 + R). The solver's own measure of its result — the resultant of all internal forces relative to
their size, zero in exact arithmetic:

| R | Digits lost (estimate) | Relative resultant | Outcome |
|---|---|---|---|
| 10³ | 3.0 | 1.7·10⁻¹⁴ | Solved |
| 10⁵ | 5.0 | 2.4·10⁻¹² | Solved |
| 10⁶ | 6.0 | 6.9·10⁻¹² | Solved, warning |
| 10⁷ | 7.0 | 5.1·10⁻¹⁰ | Solved, warning; end displacement PL/EA of the two in series to 10⁻⁹ m |
| 10⁸ | 8.0 | 1.9·10⁻⁹ | Solved, warning |
| 10⁹ … 10^11.9 | 9.0 … 11.9 | above the accepted 10⁻⁸ | Factorized; result withheld (`static.equilibrium`); finite digits, severe above 11 |
| 10^12.5 … 10^13.5 | 12.5 … 13.5 | — | Refused (`static.matrix.unstable`): nearly singular, finite digits, no ∞ |
| 10¹⁴, 10¹⁵ | — | — | Refused: singular, ∞ at UX of both joints (the limit of §7) |

The resultant grows with the estimate and stays within ε·10^d at every R solved (checked for 10³ … 10⁸).

### The required cases

| Case | Model | What is checked |
|---|---|---|
| 1 Stable 2D frame | Cantilever in XZ; beam on two pins (application) | Converged; Passed; Instabilities 0; only UX, UZ, RY assessed |
| 2 Stable 3D frame | Cantilever in space; three-member space frame | Converged; all six degrees of freedom of a free joint assessed, with their closed-form values |
| 3 2D mechanism | Portal on two pins, beam hinged at both ends | Refused as singular, one movement without stiffness; ∞ at the rotation of both feet and the sway and rotation of both heads; the vertical movement of the heads N/A; nothing against UY, RX, RZ; with the sway held the same portal is solved |
| 4 3D unrestrained rotation | Column on a ball joint, head held sideways | Singular; RZ of its two joints, at (2, 5, 0) and (2, 5, 3); bending rotations and axial movement not in the mode |
| 5 Nearly singular | The chain above | Warning at 10⁷; finite and severe at 10^11.5; nearly singular, 13.0 digits, no ∞ at 10¹³ |
| 6 Singular | Beam on two rollers; the portal; a moment on a joint whose every frame end is released | No result; ∞ at UX of every joint of the sliding beam and only there; an exactly zero stiffness reported at that rotation |
| 7 Non-convergence | Cable on a cantilever, iteration limit 1 | Not Converged with its iteration and out-of-balance; no assessment, no instability; with its iterations the same case converges, Instabilities 0 |
| 8 Inactive degrees of freedom | Cases 1 and 3; a cable joint | UY, RX, RZ "-"; pinned translations "R"; the rotation of a cable joint "-" |
| 9 Independence | Beam hung from a cable joint that only slack cables reach when pushed up; a cable in two segments with a target force | Up, Down, Up: Numerical Instability, Converged, Numerical Instability, each with its own times and log; the case continuing from a failed fit reads Failed and says so, the case that does not is judged on its own |
| 10 Consistency | Every run of the application-level checks | The monitor's status of every case equals the classification of the solver's last report of it; results exist exactly when every case converged; a case that only started, or a run that was cancelled, is never Converged |

**Movements held (§2.4).** In `Mcotis.Checks`, each against a closed form and against the same model with the
movement restrained, to the digit (displacements 10⁻¹² m, reactions and end actions 10⁻⁹ kN):

| Model | Movement not acted on | Solved to | Refused when |
|---|---|---|---|
| Column in space on a ball joint, head held sideways, loaded along its axis | Spin about its axis: RZ of its two joints | PL/EA; the column with RZ restrained | A torque about its axis is added |
| Beam on two rollers, transverse load | Slide along its axis: UX of its three joints | PL³/48EI; the beam with one end pinned | A load along its axis is added |
| Portal with its beam hinged at both ends, a weight on one head | Sway: 6 degrees of freedom | PL/EA of the loaded column, the whole weight at its foot | It is pushed sideways |
| The beam on rollers hung from a cable at mid-span (iterated) | The slide | P/(k_beam + k_cable) and the cable's share; the beam with one end pinned | — |

Without the option every one of them is refused, as before: the default is unchanged.

**A model in a plane, analysed in full 3D** (the main window starts a model in full 3D since 2026-10-13). Such a model
stands on pins that are all on one line, so its stiffness in 3D is always singular: it turns about that line. What is
reported is checked against the kinematics of that rotation, and what is solved against the analysis in the plane —
neither against a stored answer.

- A rigid portal on two pins (`--run-monitor`): in 3D the solver finds one movement without stiffness and names
  exactly RX of its four joints and UY of the two joints off the line of the pins — a rotation θ about X gives RX = θ
  everywhere and UY = ∓θ·z, zero on the line — with R at the three translations of each pin and nothing against UX,
  UZ, RY. Held, the case is solved: its joints, reactions and every frame station equal those of the XZ analysis
  within 2·10⁻¹⁸ m and 8·10⁻¹⁴ kN.
- A beam hung from a cable (`--run-monitor`), an iterated equilibrium: the beam twists about its own axis, RX of its
  three joints; held, the result equals the plane's within 2·10⁻¹⁵ m and 10⁻¹⁰ kN.
- The same portal with its beam hinged, pushed sideways, in 3D: two movements, one of them under load — refused, 12
  degrees of freedom listed, nothing held.
- The default model through the window (`--run-monitor-ui`): 246 active, 234 solved, 12 restrained; every case one
  movement, 78 degrees of freedom = RX of the 41 joints + UY of the 37 off the line of the 4 pins; the fit of the
  cable target forces and its cases all solved. Against the same model in the XZ plane, for every case and
  combination: displacements within 2·10⁻¹² m, forces within 2·10⁻⁶ kN — joints, reactions, frame stations, cables.

The out-of-plane inertia and the torsion constant are those of the sections; a section without a torsion constant
takes the polar moment I22 + I33, as on export to SAP2000 and ETABS. The movement found, and the in-plane result, do
not depend on their size, only on their being positive. The main window shows the in-plane components of a result.

Other checks: the classification of matrices (free spring: one mode, both equations in it; a held equation beside it
not counted; a zero diagonal; contrast 10¹³ against 10¹⁶; a negative pivot is neither singular nor a number of
digits); thresholds on fractional values; cable target forces not met read Converged with Warnings; the log keeps
600 iteration lines per case and says how many it left out.

**Regression** (the solution is untouched): `Mcotis.Checks` 2,904 checks, of which the 2,767 that existed pass
unchanged, OpenSees comparisons included; `--general-solver` (the application's analysis against the archived planar
solver, default model included) with the differences it had; `--fem`; `--fem-sap`. On the default model: 115
equations, largest loss 2.2 digits at TTrPt010 UX, smallest scaled pivot 1.2·10⁻², four solves, all Converged.

**Not done**: a comparison with OpenSees (it reports no such quantity; a condition number of its matrix could be
compared and has not been) or with a CSI program; a model near the size limits; a systematic search for stiffness
matrices on which diagonal pivoting misjudges the rank.

## References

Named from the literature; not inspected page by page for this record.

- J. W. Demmel, *On floating point errors in Cholesky*, LAPACK Working Note 14, 1989 — accuracy of Cholesky in terms
  of the diagonally scaled matrix.
- A. van der Sluis, *Condition numbers and equilibration of matrices*, Numerische Mathematik 14 (1969) 14–23 —
  diagonal scaling of a positive definite matrix is within a factor n of optimal.
- N. J. Higham, *Accuracy and Stability of Numerical Algorithms*, 2nd ed., SIAM, 2002 — Chapter 10 (Cholesky
  factorization, definite and semi-definite), §7.3 (scaling to minimize the condition number).
- N. J. Higham, *Analysis of the Cholesky decomposition of a semi-definite matrix*, in *Reliable Numerical
  Computation*, Oxford University Press, 1990, pp. 161–185 — complete pivoting, the stopping criterion, its limits.
- LAPACK, `DPSTRF` — Cholesky factorization with complete pivoting of a positive semi-definite matrix; default
  tolerance n·ε·max(diagonal).
- R. D. Cook, D. S. Malkus, M. E. Plesha, R. J. Witt, *Concepts and Applications of Finite Element Analysis*,
  4th ed., Wiley, 2002 — the diagonal decay test of a stiffness factorization.
- E. L. Wilson, *Three-Dimensional Static and Dynamic Analysis of Structures*, Computers and Structures, Inc. —
  the loss of significant figures in the solution of equilibrium equations.

CSI documentation consulted for the reporting convention only (retrieved 2026-10-13):

- CSI Knowledge Base, *Lost digit of accuracy and Ill-Conditions* —
  https://web.wiki.csiamerica.com/wiki/spaces/etabs/pages/1474683/Lost+digit+of+accuracy+and+Ill-Conditions
- CSI Knowledge Base, *Numerical problems FAQ* — https://web.wiki.csiamerica.com/wiki/x/-YQe
- CSI, *Analysis Reference Manual* — https://docs.csiamerica.com/manuals/etabs/Analysis%20Reference.pdf (named in
  the request; not read for this record)
