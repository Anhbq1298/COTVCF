# Cable / Tendon Section Optimisation

## Scope

The tool sizes native cable objects whose assigned sections represent an integer number of 15.7 mm strands. It changes
strand count only. The nominal area is 150 mm² per strand. A section name that states a strand count must agree with the
native section area; an unsupported diameter, non-integral area, missing section, or inconsistent name/area stops the run
with an explanation. It does not infer design identity from geometry.

One optimization group is formed from cables that share an assigned native section ID, share a section name in the
main-window configuration, or are linked through Cable Force Sync references. A Sync follower and its reference (also
through a chain of followers) receive one suggested strand count even when they currently use separate section IDs. If
linked tendons start with different counts, the optimizer reports each member's actual starting UR, analyses the current
model once, then uses the largest current count as the shared trial size. The grouping does not use endpoint geometry.
Candidate documents preserve point/member IDs,
connectivity, restraints, assignments apart from the trial cable section, load patterns, target-force loads, load cases,
and analysis settings. The solver still analyzes the whole native model.

## Sizing and iteration

For the default breaking stress of 2160 MPa and preliminary coefficient of 0.50, one strand has a nominal capacity of
`150 mm² × 2160 MPa × 0.50 / 1000 = 162 kN`. The suggested count is
`max(1, ceil(abs(calculated force) / (target UR × strand capacity)))`. Current UR uses the force in the initial section;
Suggested UR uses the latest analyzed force and the suggested count. For each group, the controlling force is the largest
absolute fitted cable force across the selected pretension-fitting cases.

Each pass starts from the same immutable native document and applies the current trial count to its candidate copy. It
runs the existing native document analysis for the pretension-fitting cases, including their prerequisites. One group
is moved by one strand toward its calculated count per pass; groups with fewer trial strands are considered first, then
by tendon name. Target-force loads and their load sequence are not changed or rescaled. The fitted cable tension is the
calculated force; each pass records that force and its target separately.

Force convergence compares each cable in each selected fitting case between consecutive passes. Deflection convergence
compares global `UX`, `UY`, and `UZ` at every consistently named joint in those same fitting cases. Both maxima must be at
or below the single UI tolerance. A missing monitoring point or fitting-case result fails the run rather than comparing
different analysis conditions.

The relative-change denominator follows `max(abs(previous), floor)`. The force floor is the larger of `1e-6 kN` and
one-billionth of the largest absolute target or observed force. The deflection floor is the larger of `1e-9 m` and
one-billionth of the model diagonal or observed displacement magnitude. These floors keep zero and near-zero values
finite without changing the underlying analysis values.

## Review and application

Iteration history stores trial strand counts, each fitting-case cable target and calculated force, monitored joint
deflections, both maximum changes, and their governing force or joint component. When a model has a rooted `.mcotis` file,
the latest history is saved beside it in the model's results folder; otherwise it remains in the current session.

Iteration never applies the proposed section. Apply requires a current result for the unchanged document and writes all
suggested groups as one undoable model edit. If the FEM model is locked, Apply unlocks it first; because the previous
revision's outputs are no longer valid, the user is told that current FEM results and registered output databases will
be cleared. If the convergence requirements were not met before the iteration limit, Apply also asks for confirmation.
The edit changes sections and their derived cable capacities only; target-force data, patterns, and cases remain unchanged.
