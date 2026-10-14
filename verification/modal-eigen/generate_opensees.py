"""Independent axial modal models; never imports Mcotis matrices or model data.

Run with the pinned requirements in ../spatial-static/requirements.txt. The checked-in output is a reproducible
comparison fixture, not a claim that OpenSees and Mcotis share an implementation.
"""
import argparse
import importlib.metadata
import json
import math
from pathlib import Path

import openseespy.opensees as ops

ROOT = Path(__file__).resolve().parents[2]
FIXTURES = ROOT / "tests" / "Mcotis.Checks" / "Fixtures"
E = 200_000_000.0
A = 0.02
L = 4.0


def axial_chain(name, masses):
    """A 1D chain of independently specified elastic trusses and nodal lumped masses."""
    ops.wipe()
    ops.model("basic", "-ndm", 1, "-ndf", 1)
    ops.node(1, 0.0)
    ops.fix(1, 1)
    ops.uniaxialMaterial("Elastic", 1, E)
    for index, mass in enumerate(masses, 2):
        ops.node(index, (index - 1) * L)
        ops.mass(index, mass)
        ops.element("truss", index - 1, index - 1, index, A, 1)
    eigenvalues = list(ops.eigen("-fullGenLapack", len(masses)))
    modal = ops.modalProperties("-return")
    return {
        "name": name,
        "masses": masses,
        "eigenvalues": eigenvalues,
        "frequenciesHz": [math.sqrt(value) / (2 * math.pi) for value in eigenvalues],
        "participationRatioPercentUX": list(modal["partiMassRatiosMX"]),
        "cumulativeRatioPercentUX": list(modal["partiMassRatiosCumuMX"]),
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, default=FIXTURES / "modal-eigen-opensees.json")
    args = parser.parse_args()
    cases = [axial_chain("sdof", [3.0]), axial_chain("two-dof", [2.0, 2.0])]
    reference = {
        "source": "Independent OpenSeesPy one-dimensional Elastic truss chains with nodal lumped mass",
        "versions": {"OpenSees": ops.version(), **{package: importlib.metadata.version(package)
            for package in ["openseespy", "openseespywin"]}},
        "units": {"length": "m", "force": "kN", "mass": "t", "time": "s"},
        "assumptions": "Linear axial trusses; nodal lumped translational mass; fixed first node",
        "cases": cases,
    }
    args.output.write_text(json.dumps(reference, indent=2, allow_nan=False) + "\n", encoding="utf-8")
    print(f"Generated {len(cases)} OpenSees modal reference cases: {args.output}")


if __name__ == "__main__":
    main()
