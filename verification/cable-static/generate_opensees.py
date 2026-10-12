"""Independent OpenSeesPy models of frames with tension-only cables, from physical benchmark specs.

Never imports Mcotis matrices or results. Run it in a Python environment with
verification/spatial-static/requirements.txt. The output is a reference fixture, not a declaration of production
validation.

A cable is a Truss element (linear geometry) with an elastic material that has no stiffness in compression, wrapped
in an initial strain equal to pretension / EA: at the model geometry it carries its pretension, and it goes slack
when it would have to carry compression. Small displacements, no sag, no self-weight.
"""
import argparse
import importlib.metadata
import json
from pathlib import Path

import openseespy.opensees as ops

ROOT = Path(__file__).resolve().parents[2]
FIXTURES = ROOT / "tests" / "Mcotis.Checks" / "Fixtures"
ACTIVE = {"Full3D": range(6), "XZ": [0, 2, 4], "XY": [0, 1, 5], "YZ": [1, 2, 3]}


def solve(case, section, cable, loads):
    ops.wipe()
    ops.model("basic", "-ndm", 3, "-ndf", 6)
    active = ACTIVE[case["mask"]]
    # a joint that only cables reach has no rotational stiffness: its rotations are not equations of the model
    framed = {node for frame in case["frames"] for node in [frame["i"], frame["j"], *frame.get("via", [])]}
    for node, (xyz, fix) in enumerate(zip(case["nodes"], case["fix"]), 1):
        ops.node(node, *xyz)
        ops.fix(node, *[int(fix[d] or d not in active or (d >= 3 and node not in framed)) for d in range(6)])
    shear = section["E"] / (2 * (1 + section["nu"]))
    element = 0
    for transform, frame in enumerate(case["frames"], 1):
        ops.geomTransf("Linear", transform, *frame["vecxz"])
        stops = [frame["i"], *frame.get("via", []), frame["j"]]
        for start, end in zip(stops, stops[1:]):
            element += 1
            ops.element("elasticBeamColumn", element, start, end, section["A"], section["E"], shear,
                        section["J"], section["Iy"], section["Iz"], transform)
    cables = []
    for index, item in enumerate(case["cables"], 1):
        elastic, strained = 1000 + index, 2000 + index
        ops.uniaxialMaterial("Elastic", elastic, cable["E"], 0.0, 0.0)  # tension stiffness E, none in compression
        ops.uniaxialMaterial("InitStrainMaterial", strained, elastic, item["pretension"] / (cable["E"] * cable["A"]))
        element += 1
        ops.element("Truss", element, item["i"], item["j"], cable["A"], strained)
        cables.append(element)
    ops.timeSeries("Linear", 1)
    ops.pattern("Plain", 1, 1)
    for load in loads:
        ops.load(load["node"], *load["values"])
    ops.constraints("Plain")
    ops.numberer("RCM")
    ops.system("FullGeneral")
    ops.test("NormUnbalance", 1e-9, 50)
    ops.algorithm("Newton")
    ops.integrator("LoadControl", 1.0)
    ops.analysis("Static")
    code = ops.analyze(1)
    if code != 0:
        raise RuntimeError(f"{case['name']}: OpenSees analyze returned {code}")
    ops.reactions()
    nodes = [{"node": n, "displacement": ops.nodeDisp(n), "reaction": ops.nodeReaction(n)}
             for n in range(1, len(case["nodes"]) + 1)]
    forces = [{"cable": n, "tension": max(0.0, ops.eleResponse(tag, "axialForce")[0])}
              for n, tag in enumerate(cables, 1)]
    return {"nodes": nodes, "cables": forces}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, default=FIXTURES / "cable-static-opensees.json")
    args = parser.parse_args()
    spec = json.loads((FIXTURES / "cable-static-spec.json").read_text(encoding="utf-8"))
    reference = {
        "source": "Independent OpenSeesPy elasticBeamColumn + Truss (tension-only Elastic in InitStrainMaterial) models",
        "versions": {"OpenSees": ops.version(),
                     **{p: importlib.metadata.version(p) for p in ["openseespy", "openseespywin", "numpy"]}},
        "units": spec["units"],
        "assumptions": "Prismatic EB frames; straight tension-only cables with a lack of fit; small displacements; "
                       "no sag, self-weight, shear deformation or P-Delta",
        "cases": [{"name": case["name"],
                   "loads": {name: solve(case, spec["section"], spec["cable"], loads)
                             for name, loads in case["loads"].items()}}
                  for case in spec["cases"]],
    }
    args.output.write_text(json.dumps(reference, indent=2, allow_nan=False) + "\n", encoding="utf-8")
    print(f"Generated {sum(len(c['loads']) for c in reference['cases'])} OpenSees cable reference solutions: {args.output}")


if __name__ == "__main__":
    main()
