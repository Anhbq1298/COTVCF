# Steel Column (Beam-Column) Design — EN 1993-1-1 — Plan for Review

Status: **Planned — awaiting Mark's review.** Nothing is implemented yet.
Date: 2026-10-18

## 1. Goal

Add **Design → Steel Column Design (EN 1993-1-1)…** for frame members under combined axial force and bending, using
the exact flow the other four column designs already use:

`Design menu → DesignReferenceWindow (Design Basis · Section · Members & Loads · Design · Optimization) →
MainViewModel.DesignMemberInputs (native FEM forces, local axes) → ColumnDesignEngine.Design → DesignResultsPanel`.

The checks follow SS EN 1993-1-1 + Singapore NA clause by clause. Anything the code requires that the app cannot
verify is **refused with the reason in the report**, never skipped silently (same rule as today's torsion refusal).

## 2. What exists today (and is reused unchanged)

| Piece | File | Reuse |
|---|---|---|
| Menu → dialog wiring | `MainWindow.xaml` (Design menu, `Tag=` enum), `MainWindow.xaml.cs DesignMenu_Click` | Add one `MenuItem`, `Tag="Steel"` |
| Dialog (4 tabs + Optimization) | `DesignReferenceWindow.xaml(.cs)` — field table `Fields[]` filtered by `ColumnDesignType` | Add steel rows to `Fields[]`; hide γC/γS; add γM1 |
| Basis / section records | `Domain/ColumnDesign.cs` `ColumnDesignBasis`, `ColumnSectionInput`, `ColumnDesignCodes` | Add steel-only properties (§4) |
| Member list / section prefill | `MainViewModel.Design.cs` `Suits`, `DesignSectionDefaults` | Add steel branch: `FrameShape.I` + steel material |
| Forces | `NativeDesignForces.Of` → `DesignStation` (F1,F2,F3,M1,M2,M3 per station, local axes) | Add station position (§5.1) |
| Engine dispatch | `ColumnDesignEngine.Designer(basis)` → abstract `ColumnDesigner` | New `SteelMemberDesigner` in its own file |
| Buckling χ | `ColumnDesignEngine.Chi` (EN 1993-1-1 6.3.1.2) | Reuse; already correct |
| Results UI / report | `DesignResultsPanel` (summary grid + text report, Copy/Save) | Unchanged |
| Torsion | `ColumnDesignEngine.TorsionRefusal` | Same rule for steel (6.2.7 not designed in phase 1) |

## 3. Scope

**Phase 1 (this plan):** doubly symmetric rolled or welded **I / H sections** (`FrameShape.I`, top = bottom flange),
steel material, Class 1, 2 or 3. Compression or tension, My + Mz + Vz + Vy.

**Refused in phase 1, with the reason in the report:**
- Class 4 cross-sections (need EN 1993-1-5 effective section) — 5.5.2(2)
- Monosymmetric I (t2b ≠ t2 or tfb ≠ tf) — LTB Mcr and Annex B factors differ
- Webs needing shear-buckling check, hw/tw > 72 ε/η — 6.2.6(6) → EN 1993-1-5
- Torsion above the existing threshold — 6.2.7
- High shear with high axial (both reductions active) is handled, not refused (6.2.8 + 6.2.9)

**Phase 2 (later, separate review):** RHS/SHS/CHS (Table 6.2 hollow rows, no LTB), Annex A (Method 1), Class 4 via
EN 1993-1-5, monosymmetric I, Steel in the Optimization tab and the Member Utilization check (see §8 risk).

## 4. Data model changes

`ColumnDesignType` — append `SteelMember` **at the end** (no reordering; production baselines key on names).

`ColumnSectionInput` — reuse `SteelDepth h`, `SteelWidth b`, `FlangeThickness tf`, `WebThickness tw`, `Fy`, `Ea`;
add:
- `RootRadius r` [mm] (rolled fillet) — needed for c/t (Table 5.2), Av (6.2.6(3)a), It, Iw
- `Fabrication` = Rolled | Welded — selects Table 6.2 / Table 6.5 rows and Av formula
- `WeldSize a` [mm] (welded only; c for flange = (b − tw − 2√2 a)/2)
- `SteelGrade` (S235…S460) and `Fu` — fy/fu by nominal thickness per Table 3.1 (or product standard if NA requires) — **decision D3**

`ColumnDesignBasis` — add steel-only:
- `GammaM1` (default 1.0) — γM0 already exists as `GammaA`
- `LtbLength` L_LT [m], `Kz`, `Kw` (end restraint factors, default 1.0), `LoadPosition` (zg: shear centre / top flange / bottom flange)
- `SwayY`, `SwayZ` (bool) — sway buckling mode per axis → Cm = 0.9 per Table B.3 note
- `InteractionMethod` = Annex B (Method 2) — Annex A listed but disabled until phase 2
- `C1Mode` = from moment diagram (default) | keyed value

`FrameSectionProperty` — add `RootRadius`. SAP2000 import already reads `radius` in `GetISection_1` and drops it
(`SapGateway.cs:1011`); carry it through. ETABS `.e2k`: check whether the I-section record has a fillet; else 0 + warning.

## 5. Engine — `Application/Design/SteelMemberDesigner.cs` (new, `: ColumnDesigner`)

### 5.1 Forces: concurrent per station, with position
The other designers take the **envelope** of |M| and |V| over the stations (conservative). For steel that is not
enough: Annex B Cm (Table B.3) and LTB C1 need the **moment diagram along the member**, and 6.2 cross-section checks
should use **concurrent** N, My, Mz, V at each station.
- Add `double Relative = double.NaN` to `DesignStation` (from `StaticFrameStation.Relative`, already in the solver output).
- Add a virtual `DesignCase(..., DesignCaseForces forces, ...)` on `ColumnDesigner` whose default calls the existing
  envelope method → RC/CFT/SRC/Mega unchanged (frozen answers kept). Only the steel designer overrides it.
- Planar fallback stations (start/end only) → C1 and Cm taken conservatively as 1.0 with a note.

### 5.2 Section properties (computed, reported)
A, Iy, Iz, Wel,y, Wel,z, Wpl,y, Wpl,z (with fillets for rolled), iy, iz, It, Iw = Iz (h − tf)²/4, Av,z, Av,y.
Check against a published table (e.g. UKC 305×305×97, UB 533×210×92) in `--steel-design` checks.

### 5.3 Cross-section classification — 5.5, Table 5.2
- ε = √(235/fy).
- Flange (outstand, compression): c/tf vs 9ε / 10ε / 14ε.
- Web (internal, bending + compression): α from the plastic neutral axis for NEd (Class 1/2 limits 396ε/(13α−1) or
  36ε/α), ψ from the elastic stress distribution for Class 3 (42ε/(0.67+0.33ψ) or 62ε(1−ψ)√(−ψ)).
- Classified **per case** with that case's NEd (class can change with axial force). Class = worst part.
- Class 4 → refused (phase 1).

### 5.4 Cross-section resistance — 6.2 (at every station, concurrent actions)
- Tension 6.2.3: Npl,Rd = A fy/γM0 (Nu,Rd net section needs hole data → state "gross section; check connections").
- Compression 6.2.4: Nc,Rd = A fy/γM0.
- Shear 6.2.6: Vpl,Rd = Av (fy/√3)/γM0; Av rolled = A − 2b tf + (tw + 2r) tf ≥ η hw tw; welded = η Σ hw tw.
  η per EN 1993-1-5 NA (**D2**).
- Bending + shear 6.2.8: if VEd > 0.5 Vpl,Rd, ρ = (2VEd/Vpl,Rd − 1)², reduced fy on Av (6.30 for I about y).
- Bending + axial 6.2.9:
  - Class 1/2: no reduction checks (6.33)–(6.35); MN,y,Rd = Mpl,y,Rd(1−n)/(1−0.5a) ≤ Mpl,y,Rd (6.36);
    MN,z,Rd per (6.37)/(6.38); biaxial (6.41) with α = 2, β = 5n ≥ 1.
  - Class 3: σx,Ed = N/A + My/Wel,y + Mz/Wel,z ≤ fy/γM0 (6.42).
- Utilisation reported per station; governing station named.

### 5.5 Member buckling — 6.3
- Flexural 6.3.1.2–6.3.1.3: Ncr = π²EI/l0² with the Design Basis l0,y / l0,z; λ̄ = √(A fy/Ncr) (Class 1–3);
  curve from **Table 6.2** (rolled: h/b > 1.2 & tf ≤ 40, 40 < tf ≤ 100, h/b ≤ 1.2 & tf ≤ 100, > 100; S460 rows;
  welded: tf ≤ 40 / > 40); α from Table 6.1; χ from the existing `Chi`. λ̄ ≤ 0.2 or NEd/Ncr ≤ 0.04 → χ = 1 (6.3.1.2(4)).
- Torsional / flexural-torsional 6.3.1.4: doubly symmetric I → Ncr,T = (G It + π² E Iw / l0,T²)/i0². Use
  χT = χ(λ̄T, curve of z-z). Check reported; governs only for short, open, light sections.
- Lateral-torsional 6.3.2:
  - Mcr — **not given in EN 1993-1-1**; use the 3-factor formula (NCCI SN003 / ENV 1993-1-1 Annex F):
    Mcr = C1 π²EIz/(kz L)² {√[(kz/kw)² Iw/Iz + (kz L)² G It/(π² E Iz) + (C2 zg)²] − C2 zg}.
  - C1 from the moment diagram over L_LT (end-moment ratio ψ, or the SN003 tables for span loading); keyed value allowed.
  - χLT: **6.3.2.3** (rolled / equivalent welded) with λ̄LT,0, β and the f-modification (6.58), or 6.3.2.2 general case
    — NA choice (**D1**). Curves from Table 6.5 (6.3.2.3) or Table 6.4 (6.3.2.2) — UK/SG NA modify them (**D1**).
  - λ̄LT ≤ λ̄LT,0 or MEd/Mcr ≤ λ̄LT,0² → no LTB reduction (6.3.2.2(4)).
- Mb,Rd = χLT Wy fy/γM1, Wy = Wpl,y (Class 1/2) or Wel,y (Class 3).

### 5.6 Interaction — 6.3.3, Annex B (Method 2)
Both equations, each ≤ 1.0:
- (6.61) NEd/(χy NRk/γM1) + kyy (My,Ed + ΔMy)/(χLT My,Rk/γM1) + kyz (Mz,Ed + ΔMz)/(Mz,Rk/γM1)
- (6.62) NEd/(χz NRk/γM1) + kzy (My,Ed + ΔMy)/(χLT My,Rk/γM1) + kzz (Mz,Ed + ΔMz)/(Mz,Rk/γM1)
- ΔM = 0 (Class 1–3).
- Member **susceptible** to torsional deformation (open I) → **Table B.2**; not susceptible (LT restrained, L_LT = 0)
  → Table B.1 with χLT = 1.
- kij: Class 1/2 plastic columns, Class 3 elastic columns of Tables B.1/B.2.
- Cmy, Cmz, CmLT from **Table B.3** using the station moments: linear (ψ), or with span moment Ms and αs/αh, uniform
  vs concentrated load rows; **sway mode → Cm = 0.9** for that axis. CmLT uses the diagram between LT restraints.
- Tension members: 6.3.3 does not apply; cross-section check 6.2.9 only (LTB still checked with N = 0 conservatively
  — **D5**).

### 5.7 Detailing / scope list (shown as the DETAILING AND SCOPE block)
Class per part (flange, web) with c/t and limit · λ̄ ≤ (no EC3 limit; report only) · shear buckling hw/tw ≤ 72ε/η ·
grade within S235–S460 (EN 1993-1-1 scope; S500–S700 → EN 1993-1-12, refuse) · tf within Table 6.2 range ·
torsion threshold.

### 5.8 Report layout (same style as RC/CFT)
Section properties → classification → per-case table (NEd, My, Mz, Vz, Vy, class, U-section, U-6.61, U-6.62,
U-shear, U-LTB) → governing case step-by-step with clause numbers → scope block.

## 6. UI

- `MainWindow.xaml`: `<MenuItem Header="_Steel Column Design (EN 1993-1-1)…" Tag="SteelMember" Click="DesignMenu_Click"/>` after
  Mega Composite (same separator group).
- `ColumnDesignCodes`: `Title`, `DesignCodes` = `"SS EN 1993-1-1 (EC3) + Singapore NA"`, `Methods` =
  `["Annex B — Method 2 (EN 1993-1-1 §6.3.3)"]`, `References` = EC3-1-1, EC3-1-5 (for scope), EC0, EN 10025, NCCI SN003 (Mcr),
  `MaterialScope` = "S235–S460; Class 4 and higher grades refused".
- `DesignReferenceWindow`:
  - Design Basis: hide γC, γS, max-concrete-grade; label `GammaABox` "γM0"; **add γM1** box. Add a "Lateral-torsional
    buckling" group: L_LT, kz, kw, load position, C1 (auto / value), sway y / sway z checkboxes.
  - Section tab: steel rows only — h, b, tf, tw, r, Fabrication (combo), weld a, grade (combo), fy, fu, E.
    `SectionProblem`: web between flanges, r ≥ 0, monosymmetric refused.
  - Members & Loads: unchanged. Suggested = `FrameShape.I` with steel material.
  - Optimization tab: **hidden for steel in phase 1** (§8).
- Dialog title / results panel: unchanged code paths.

## 7. Verification (before any result is shown — AGENTS.md rule)

New `tests/Mcotis.Desktop.Checks/SteelDesignChecks.cs`, flag `--steel-design`, also in the default quick run:
1. Section properties vs published tables (UKC 254×254×73, UB 457×191×67): A, I, Wpl, It, Iw within 1–2 %.
2. Classification: a web that moves Class 2 → 3 as NEd rises; Class 4 refused.
3. Cross-section: 6.36 / 6.41 / 6.42 vs hand calc; high shear (6.2.8) case.
4. χ: Table 6.2 curve selection for each row; χ values vs SCI P362 / Access Steel tables.
5. Mcr / χLT: SN003 worked example; 6.3.2.3 f-factor.
6. Full beam-column: at least two published worked examples (SCI P364 / Eurocode Designers' Guide beam-column
   examples, or ECCS Eurocode Design Manual Ex.) reproduced to ±1 % on 6.61/6.62 — **D6: which examples you trust**.
7. Spatial forces: M2/M3 mapping and sign (reuse the `RunSpatialForces` pattern).
8. **Regression:** `--column-design` and `production-baselines.json` unchanged (RC, CFT, SRC, Mega answers frozen).

Docs: `docs/DEVELOPMENT_LOG.md` entry (Planned now, Done per slice), User Guide article for the new menu item.

## 8. Risks

- **Frozen answers (Optimization / Member Utilization).** `BuildMemberUtilizationCheck` assigns each section the first
  design type that `Suits` it. If steel I members get a steel basis there, every VCF/TAS optimization that has steel I
  members will start showing utilisations it does not show today → baseline change. Phase 1 keeps steel **out of**
  that list; adding it is a separate, user-approved change.
- **Force envelope vs concurrent forces.** Steel uses concurrent station forces; the other types keep the envelope.
  Different behaviour across menus — stated in the report header.
- **Effective lengths and sway.** l0 is keyed per member like the other designs; the app does not derive it from
  the frame. If the FEM includes no global imperfections / P-Δ (5.2.2), sway-frame columns need sway l0 — stated in
  the Design Basis hint.
- **Fillet radius missing on imported sections** → r = 0 gives smaller Av, Wpl, It and larger c/t: all conservative,
  but a Class 2 section may be reported Class 3. Reported as a warning so you can key in r.

## 9. Decisions needed from you

- **D1 — NA values for LTB:** 6.3.2.3 with λ̄LT,0 = 0.4, β = 0.75 and the NA curve assignment (UK NA: rolled
  h/b ≤ 2 → b, 2 < h/b ≤ 3.1 → c, > 3.1 → d; welded → c/d)? Or recommended values? Please confirm against your
  SS EN 1993-1-1 NA copy.
- **D2 — η for shear area** (EN 1993-1-5 NA): 1.0 or 1.2 for ≤ S460?
- **D3 — fy by thickness:** EN 1993-1-1 Table 3.1 or product standard EN 10025-2 (finer thickness steps)?
- **D4 — γM0 / γM1:** SG NA 1.0 / 1.0 — confirm.
- **D5 — Tension + bending:** cross-section only (6.2.9), or also an LTB check with N = 0?
- **D6 — Verification examples** you want reproduced.
- **D7 — Phase 1 shapes:** I/H only, or include RHS/SHS/CHS now?
- **D8 — Annex A (Method 1):** needed in phase 1 (some consultants require it), or Annex B only?

## 10. Implementation slices (each builds and passes all checks)

1. Data model (§4) + station position, no UI, engine stub refusing everything. Checks: baselines unchanged.
2. Section properties + classification + 6.2 cross-section. Checks 1–3.
3. Flexural + torsional buckling (6.3.1). Check 4.
4. Mcr, χLT (6.3.2). Check 5.
5. Annex B interaction + Table B.3 Cm. Checks 6–7.
6. UI: menu, Design Basis LTB group, Section steel rows, Suits/prefill, results. Manual run on a TAS/VCF model.
7. Dev log, User Guide, `--steel-design` in the quick run.
