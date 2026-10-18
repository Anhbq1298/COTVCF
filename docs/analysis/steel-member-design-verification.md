# Steel beam / column design — verification record

Design → Steel Beam / Column Design (`src/Mcotis.Desktop/Application/Design/SteelMemberDesigner.cs`), checked by
`dotnet run --project tests/Mcotis.Desktop.Checks -- --steel-design` (`tests/Mcotis.Desktop.Checks/SteelDesignChecks.cs`).

## What it implements

SS EN 1993-1-1 and SS EN 1993-1-5, units N, mm, MPa; y-y is the major axis (depth along local 2), compression positive.

| Step | Clause | Notes |
|---|---|---|
| Section properties | — | I with root fillets (area and centroid of the fillet material), RHS with corner radii (cold-formed 2t / 2.5t / 3t, hot-finished 1.5t), CHS closed forms; It of an I without fillets (safe side); Iw = Iz (h − tf)² / 4 |
| Yield strength | 3.2.1 Table 3.1 | nominal up to 40 mm; S235 215, S275 255, S355 335, S420 390, S450 410, S460 430 for 40–80 mm |
| Classification | 5.5 Table 5.2 | flange outstands and compressed walls in uniform compression; web by α (class 1, 2) and ψ (class 3) under N and My; CHS by d/t |
| Effective section (class 4) | EN 1993-1-5 4.4 | Aeff in compression (ψ = 1); Weff by removing the ineffective web zone be1…be2 and flange width, centroid shift included |
| Cross-section | 6.2.3 – 6.2.9 | Nt,Rd, Nc,Rd, Mc,Rd, MN,Rd (I, RHS, CHS), biaxial 6.2.9.1(6), σx check for class 3 / 4, shear reduction 6.2.8 |
| Shear | 6.2.6, EN 1993-1-5 5.2 | Av by section; unstiffened web shear buckling χw when hw/tw > 72ε/η (η = 1.2 up to S460) |
| Flexural buckling | 6.3.1 | curves a0 – d by Table 6.2 (rolled h/b and tf, welded, hollow hot-finished / cold-formed) |
| Lateral-torsional buckling | 6.3.2.3 or 6.3.2.2 | Mcr = C1 π²EIz/L² √(Iw/Iz + L²GIt/π²EIz), load at the shear centre, ends simply supported; C1 taken as Cb = 12.5 Mmax / (2.5 Mmax + 3 MA + 4 MB + 3 MC) from the FEM stations; kc = 1/√C1, f modification |
| Interaction | 6.3.3, Annex B (Method 2) | Tables B.1 / B.2; Cm = 0.6 + 0.4ψ (≥ 0.4) for end moments, 1.0 when the diagram peaks between the ends |

## Evidence

* UB 457×191×82 against the SCI Blue Book: A 104.5 vs 104.0 cm², Iy 37 051 vs 37 100 cm⁴, Iz 1871 vs 1870 cm⁴,
  Wpl,y 1831 vs 1830 cm³, Wpl,z 304 vs 304 cm³, Iw 0.922 vs 0.922 dm⁶; Mpl,y,Rd = 650.1 kNm at 355 MPa. It is 63.8 cm⁴ against
  69.2 cm⁴ (fillets left out, safe side; it enters Mcr only through the St Venant term).
* UC 254×254×89, l0 = 4 m, NEd = 1000 kN, My 120 → 60 kNm: χy 0.899, χz 0.662, 6.61 = 0.514, 6.62 = 0.636, worked outside the
  engine from the EN formulas (the script is not kept; the numbers are in the check).
* UB 457×191×82 beam, 6 m, uniform load, MEd 300 kNm: χLT = 0.479 (curve c, 6.3.2.3), Mb,Rd = 311.6 kNm, utilisation 0.963.
* Class 4 girder 1000 × 300 × 20 × 6 (S355): ρweb = 0.651 in bending, 0.270 in compression → Weff,y 6.282 × 10⁶ mm³ (gross 6.648),
  Aeff 13 558 mm² (gross 17 760).
* CHS and RHS areas and moduli against closed forms.

## Limits — read before relying on a result

* **Singapore National Annex.** γM0 = γM1 = 1.0 and γM2 = 1.25 are the EN 1993-1-1 recommended values; the Singapore NA text could not be
  retrieved here, so its factors, its choice between Annex A and Annex B, and its LTB parameters (λLT,0, β) are **not confirmed**. The factors
  are inputs in the dialog; the method is Annex B with 6.3.2.3 (λLT,0 = 0.4, β = 0.75) or 6.3.2.2.
* Only a hand-worked comparison, not a published worked example, backs the buckling and interaction values. The class 4 values were
  worked with the same method in a separate script — they test the code, not the method. Compare one class 4 member and one beam-column
  against a commercial program or a hand calculation by an engineer before design use.
* Cb stands in for the tabulated C1; it is exact for a uniform moment and close for end moments and uniform load, and C1 = 1.0 is used
  when the lateral length is shorter than the member. Load is taken at the shear centre.
* Annex B Cm for a diagram that peaks between the ends is the upper bound 1.0 of the 0.90 – 1.00 in Table B.3. A sway mode (Cm = 0.9) and second-order
  effects of the frame are the model's to carry; the check uses first-order FEM moments.
* Not designed: torsion (refused above 1 % of the largest moment, min. 1 kNm), class 4 CHS, net section and connections, mono-symmetric I
  sections, stiffened webs, fire, fatigue.
