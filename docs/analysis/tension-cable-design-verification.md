# Tension cable design — verification record

Design → Tension Cable Design (`src/Mcotis.Desktop/Application/Design/TensionCableDesign.cs`), checked by
`dotnet run --project tests/Mcotis.Desktop.Checks -- --cable-design` (`tests/Mcotis.Desktop.Checks/CableDesignChecks.cs`; the window by `--cable-design-ui`).

## What it implements

Source: *Technical Options Study — 100 m mega-column stay-supported structure* (18 Oct 2026), sections 3–5, and the VSL Stay Cable Selector built from it.

| Step | Rule |
|---|---|
| Screen | n = ceil( T / (f × MBL_unit × η) ), f = 0.50 or 0.60, η ≤ 1; capacity n f MBL; use T / capacity. A first-pass proxy, not an EN 1993-1-11 resistance. |
| VSL SSI 2000 | 18 sizes 6-12 … 6-187; MBL = N × 279 kN; 150 mm² and 1.172 kg/m per strand; E = 195 GPa; pipe OD from the 2018 catalogue. |
| FATZER FLC | Ø80, 100, 120, 135, 150: Fmin, F_Rd, EA and weight from the planning aid (05/2023) p. 20, galvanized FLC, E = 160 GPa. |
| Utracon PT | 37 × Ø15.7 strand tendon, duct Ø135; offered only when ticked, status CHECK (not shown to be a qualified exposed stay). |
| Cost | The study's per-metre build-up (its section 05): material + pipe + 2 × anchorage ÷ L + installation, per unit × units; contingency and GST optional. Indicative author rates, not quotations. |
| Elongation | ΔL = T L / ΣEA; thermal ΔL = 12×10⁻⁶ × 40 × L. |

## Evidence

- All 12 forces (10 … 120 MN) × 3 systems at L = 50 m: the selected units, type and installed rate agree with the study's pages 4–5; the largest difference of the 36 rates is S$0.5/m (the study rounds).
- The 120 MN worked example (section 3.2–3.3): 5 × 6-187 = 130.43 MN, ΣEA 27 349 MN, ΔL 219 mm; 11 × Ø150 = 126.50 MN, 27 775 MN, 216 mm; 24 × 37 = 123.88 MN, 25 974 MN, 231 mm; the cost-optimised VSL choice is 6 × 6-151 = 126.39 MN.
- Refusals: no tension (slack), no length, more units than allowed, a screening level outside (0, 1], no system offered — each with its reason and no stay invented.

## Limits

- The VSL pipe diameters and strand counts are those of the 2018 SSI 2000e catalogue as transcribed in the study; confirm current supply and approval with VSL.
- FATZER's published F_Rd (Fuk / 1.5) is shown beside a selection, but the screen is the 50 / 60 % one.
- Rates are budgeting assumptions; fatigue, sag, anchorage slip, relaxation, wind-rain vibration, accidental loss of a stay, staged stressing and the anchorage steelwork are not checked. The optimization suggests an assembly for the largest tension of a case's solved results (as the cable utilization does), not a staged, geometrically non-linear stay design.
