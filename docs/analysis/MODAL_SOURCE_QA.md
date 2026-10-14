# Modal source QA status

Current implementation evidence is maintained in
[modal-eigen-verification.md](modal-eigen-verification.md) and the central
[literature register](literature.md), ALG-10 through ALG-12.

Status on 2026-10-13:

- generalized eigen equation, mass normalization and translational participation equations: original OpenSees
  documentation inspected; closed-form and independently specified OpenSeesPy comparisons pass;
- lumped frame self mass and explicit joint mass/inertia: coded and checked for conservation, persistence and
  positive-mass behavior;
- free massless coordinates: exact stiffness condensation coded and checked by a bending benchmark; independent
  equation-level review remains pending;
- nonlinear/P-Delta stiffness source, prestressed cable modes, shells, consistent/load-derived mass, global
  rotational participation and sparse production extraction: open or blocked, and explicitly refused where a model
  asks for them;
- evidence class: `CROSS_VERIFIED`, reviewer pending. Do not mark `SOURCE_VERIFIED` without the reviewer/date gate.

Reproducible evidence:

- `dotnet run --project tests/Mcotis.Checks -- --full`
- `dotnet run --project tests/Mcotis.Desktop.Checks -- --template-building-ui`
- `python verification/modal-eigen/generate_opensees.py`
