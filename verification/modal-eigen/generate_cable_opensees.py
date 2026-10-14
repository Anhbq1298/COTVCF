"""Independent prestressed frame-and-cable modal comparison in OpenSeesPy.

The frame remains linear and the parallel cable is a corotational truss. An axial load is applied first;
the eigen solve then uses the converged tangent. Units are kN, m, t, s.
"""
import json
import math
from pathlib import Path

import openseespy.opensees as ops

root = Path(__file__).resolve().parents[2]
output = root / "tests" / "Mcotis.Checks" / "Fixtures" / "modal-cable-opensees.json"
E, A, I, L, force, mass = 200_000_000.0, 0.02, 0.0005, 4.0, 10_000.0, 3.0

ops.wipe()
ops.model("basic", "-ndm", 2, "-ndf", 3)
ops.node(1, 0.0, 0.0)
ops.node(2, L, 0.0)
ops.fix(1, 1, 1, 1)
ops.mass(2, mass, mass, 0.0)
ops.geomTransf("Linear", 1)
ops.element("elasticBeamColumn", 1, 1, 2, A, E, I, 1)
ops.uniaxialMaterial("Elastic", 1, E)
ops.element("corotTruss", 2, 1, 2, A, 1)
ops.timeSeries("Linear", 1)
ops.pattern("Plain", 1, 1)
ops.load(2, force, 0.0, 0.0)
ops.constraints("Plain")
ops.numberer("Plain")
ops.system("BandGeneral")
ops.test("NormUnbalance", 1e-9, 100)
ops.algorithm("Newton")
ops.integrator("LoadControl", 1.0)
ops.analysis("Static")
if ops.analyze(1) != 0:
    raise RuntimeError("The prestress analysis did not converge")

axial_displacement = ops.nodeDisp(2, 1)
cable_tension = E * A * axial_displacement / L
eigenvalues = list(ops.eigen("-fullGenLapack", 2))
if any(not math.isfinite(value) or value <= 0 for value in eigenvalues):
    raise RuntimeError(f"No positive modal eigenpairs: {eigenvalues}")

reference = {
    "source": "OpenSeesPy 2D elasticBeamColumn + corotTruss; nonlinear static pull, then eigen",
    "version": ops.version(),
    "units": {"length": "m", "force": "kN", "mass": "t", "time": "s"},
    "elasticModulus": E, "area": A, "inertia": I, "length": L,
    "appliedPull": force, "tipMassUXandUZ": mass,
    "tipAxialDisplacement": axial_displacement,
    "cableTension": cable_tension,
    "eigenvalues": eigenvalues,
    "frequenciesHz": [math.sqrt(value) / (2 * math.pi) for value in eigenvalues],
}
output.write_text(json.dumps(reference, indent=2, allow_nan=False) + "\n", encoding="utf-8")
print(f"Generated {output}: T={cable_tension:g} kN, eigenvalues={eigenvalues}")
