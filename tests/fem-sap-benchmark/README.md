# FEM against SAP2000 — benchmark fixture

Input of the like-for-like comparison of the internal FEM (Tab 07) with a completed SAP2000 study:

```
dotnet run --project tests/Mcotis.Desktop.Checks -- --fem-sap
```

It runs offline — nothing here connects to SAP2000 — and writes `fem-vs-sap.md` and `fem-vs-sap.csv` into `.work/fem-vs-sap/`.

| File | What it is |
|---|---|
| `model.s2k` | SAP2000's own text copy of the baseline model (`COT Framing_V1.2.$2k`, written by SAP2000 beside the `.sdb`). Geometry, sections, materials, load patterns, joint loads, load cases, supports. |
| `project.json` | The Mcotis project the study ran with (Tabs 01–05), without its Print Setup. |
| `sap-results.json` | The study's raw SAP2000 results reduced to what is compared: per candidate, every cable's target and final force, the deflection of every monitored point and the reactions of every support, with the load cases they were read from. |

## What "like for like" means here

- **Same model**: the FEM inputs are read from `model.s2k` by `tests/SapModelText.cs`, the way the app reads them from a running SAP2000.
- **Same candidates**: composed from `project.json` by the app's own code and checked against the keys, truss heights and cable target forces SAP2000 was given.
- **Same stage**: SAP2000's results come from staged cases that continue from the `Pretension` case, in which SAP2000 meets the cable target forces under that case's own loads (self-weight included). The FEM runs the same cases, fits the pretension in the same case with SAP2000's own tolerance, and is compared case for case.
- **Same loads**: the sum of the vertical reactions is checked first. It does not depend on stiffness, so it shows that both analyses carry the same loads.
- **Same cables**: the study changes the truss height by moving joints. A cable's ends move with them, so its chord gets longer, but SAP2000's cable objects keep the undeformed length they were defined with. `sap-results.json` holds, per candidate, the undeformed length and the chord of every cable as SAP2000's text copy of that child model lists them (`CABLE SHAPE DATA`). The benchmark gives the FEM those undeformed lengths for the like-for-like table, and also reports the FEM with every cable as long as its chord.

## Refreshing it from another study

```
dotnet run --project tests/Mcotis.Desktop.Checks -- --fem-sap-capture "<model>.$2k" "<project>.json" "<Study>/RawData/Study_Raw_Results.json" tests/fem-sap-benchmark
```

The capture only reads. Cable lengths come from the `.$2k` file in each candidate folder beside `RawData`; without those files the benchmark falls back to the baseline chord lengths.

The agreement bands in `tests/FemSapBenchmark.cs` were set from this study; a different model needs its own.

## About this fixture

It is the study of 5 Oct 2026, run before the study learned to carry cable lengths over to a new truss height
(`SapGateway.MovePointsAsync`). Its child models therefore hold cables at their baseline length, and the benchmark
shows both that and the like-for-like agreement. A study run with the fix records every cable at the length of its
chord; re-capturing the fixture from it makes tables 1 and 2 of the report the same.
