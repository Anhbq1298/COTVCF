"""Independent models from physical benchmark specs; never imports Mcotis stiffness matrices.
Run in the workspace verification venv, or a Python environment with requirements.txt.
The output is a reference fixture, not a declaration of production validation.
"""
import argparse
import importlib.metadata
import json
from pathlib import Path

import numpy as np
import openseespy.opensees as ops

ROOT = Path(__file__).resolve().parents[2]
FIXTURES = ROOT / "tests" / "Mcotis.Checks" / "Fixtures"


def build_case(case, section):
    ops.wipe()
    ops.model("basic", "-ndm", 3, "-ndf", 6)
    active = {"Full3D": range(6), "XZ": [0, 2, 4], "XY": [0, 1, 5], "YZ": [1, 2, 3]}[case["mask"]]
    omitted = {(node, component) for node, component in case.get("omitNodeRotation", [])}
    for node, (xyz, fix) in enumerate(zip(case["nodes"], case["fix"]), 1):
        ops.node(node, *xyz)
        ops.fix(node, *[int(fix[d] or d not in active or (node, d) in omitted) for d in range(6)])
    ops.timeSeries("Linear", 1)
    ops.pattern("Plain", 1, 1)
    for tag, frame in enumerate(case["frames"], 1):
        ops.geomTransf("Linear", tag, *frame["vecxz"])
        args = ["elasticBeamColumn", tag, frame["i"], frame["j"], section["A"], section["E"],
                section["E"] / (2 * (1 + section["nu"])), section["J"], section["Iy"], section["Iz"], tag]
        if frame.get("releasez"):
            args.extend(["-releasez", frame["releasez"]])
        if frame.get("releasey"):
            args.extend(["-releasey", frame["releasey"]])
        ops.element(*args)
        # OpenSees defines local y = vecxz cross local x; derive load components from the specified orientation.
        x = np.array(case["nodes"][frame["j"] - 1], float) - case["nodes"][frame["i"] - 1]
        x /= np.linalg.norm(x)
        y = np.cross(frame["vecxz"], x)
        y /= np.linalg.norm(y)
        z = np.cross(x, y)
        rotation = np.array([x, y, z])
        uniform = frame.get("udlLocal")
        if "udlGlobal" in frame:
            uniform = rotation @ frame["udlGlobal"]
        if uniform is not None:
            ops.eleLoad("-ele", tag, "-type", "-beamUniform", uniform[1], uniform[2], uniform[0])
        if "pointGlobal" in frame:
            point = rotation @ frame["pointGlobal"]
            ops.eleLoad("-ele", tag, "-type", "-beamPoint", point[1], point[2], frame["pointAt"], point[0])
    for load in case.get("nodal", []):
        ops.load(load["node"], *load["values"])
    ops.constraints("Plain")
    ops.numberer("RCM")
    ops.system("BandSPD")
    ops.algorithm("Linear")
    ops.integrator("LoadControl", 1.0)
    ops.analysis("Static")
    code = ops.analyze(1)
    if code != 0:
        raise RuntimeError(f"{case['name']}: OpenSees analyze returned {code}")
    ops.reactions()
    nodes = [{"node": n, "displacement": ops.nodeDisp(n), "reaction": ops.nodeReaction(n)}
             for n in range(1, len(case["nodes"]) + 1)]
    frames = [{"frame": n, "localEndActions": ops.eleResponse(n, "localForce")}
              for n in range(1, len(case["frames"]) + 1)]
    if any(len(f["localEndActions"]) != 12 for f in frames):
        raise RuntimeError("OpenSees did not return twelve local end actions")
    return {"name": case["name"], "nodes": nodes, "frames": frames}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, default=FIXTURES / "spatial-static-opensees.json")
    args = parser.parse_args()
    spec = json.loads((FIXTURES / "spatial-static-spec.json").read_text(encoding="utf-8"))
    reference = {
        "source": "Independent OpenSeesPy elasticBeamColumn + Linear transform models",
        "versions": {"OpenSees": ops.version(), **{p: importlib.metadata.version(p) for p in ["openseespy", "openseespywin", "numpy"]}},
        "units": spec["units"], "assumptions": "Prismatic EB frames; no shear deformation, offsets, P-Delta, mass or cables",
        "cases": [build_case(case, spec["section"]) for case in spec["cases"]],
    }
    args.output.write_text(json.dumps(reference, indent=2, allow_nan=False) + "\n", encoding="utf-8")
    print(f"Generated {len(reference['cases'])} OpenSees reference cases: {args.output}")


if __name__ == "__main__":
    main()
