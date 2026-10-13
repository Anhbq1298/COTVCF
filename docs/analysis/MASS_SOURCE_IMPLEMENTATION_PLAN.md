# MCOTiS — Define > Mass Source: Detailed Implementation Plan and QA Checklist

**Date:** 2026-10-13  
**Status:** DESIGN / TODO ONLY — no production code changed.  
**Depends on:** [MODAL_EIGEN_IMPLEMENTATION_PLAN.md](MODAL_EIGEN_IMPLEMENTATION_PLAN.md), [LOAD_CASE_STATE_REFACTOR_PLAN.md](LOAD_CASE_STATE_REFACTOR_PLAN.md), [MODAL_SOURCE_QA.md](MODAL_SOURCE_QA.md).  
**Scope:** Standalone FEM core and desktop (2D and 3D); excludes MB and CSI-runtime coupling.

## A. Fixed decisions: requested behavior

- [ ] Add a persistent **Define → Mass Source** tab/pane adjacent to Define → Load Patterns and Load Cases. Native workspace has the same shared editor.
- [ ] Support multiple named Mass Sources, immutable IDs, a single default, Add/Copy/Modify/Delete and Save/Open.
- [ ] **Structural SW is always automatically included**, with effective factor 1.0 through *element self mass*, not via the SW static load pattern. Display a read-only: "SW / Element Self Mass / 1.00 / Auto Included".
- [ ] User selects **additional mass from load patterns** (SDL, DEAD, LIVE, equipment, etc.) and enters multipliers per pattern; e.g. SDL=1.0, LIVE=0.25. Do not silently preselect other patterns.
- [ ] A selected load pattern containing generated SW never contributes that SW a second time. Exclude the generated selfweight portion using **LoadPattern.SelfWeightMultiplier and assignment provenance**, not pattern name. Explicit downward gravity load assignments carried by that same pattern may still contribute.
- [ ] Show a mass preview: SW [t], individually selected pattern [t], assigned point mass [t] when supported, total [t], total and free mass in UX/UY/UZ [t], center of mass, unsupported/negative/double-count warnings.
- [ ] Selecting a Mass Source is part of **Define → Load Cases → Modal (Eigen)**. A nonlinear/P-Delta source case can supply the tangent stiffness; the modal load case obtains the effective mass source by explicit resolved default/parent inheritance.
- [ ] SW **mass** does not change load cases, static loads, load combinations or gravity forces; static SW pattern multiplier retains its existing meaning.

**Critical terminology:** generated *selfweight load* is a force, while *self mass* is inertia. CSI explicitly warns about double counting when material self mass and SW-containing load patterns are both selected. We enforce a stricter product policy: structural self mass always on and generated SW always removed from load-derived extra mass. This is a MCOTiS choice, **not** a claim that CSI locks this choice in its UI.

## B. Authoritative references and provenance

| ID | Direct source | Supports | Status |
|---|---|---|---|
| C-MS-01 | [CSI SAP2000 Mass Source](https://docs.csiamerica.com/help-files/sap/Menus/Define/Mass_Source.htm) | Named/default sources, self mass + load-pattern combinations, downward global-Z load / g, explicit double-count warning, nonlinear source inheritance | Public HTML behavioral reference; independently inspected |
| C-MS-02 | [CSI ETABS Mass Source](https://docs.csiamerica.com/help-files/etabs/Menus/Define/Mass_Source.htm) | Define → Mass Source UI, volume × material mass density, user pattern multipliers, additional masses, downward load/g | Public HTML behavioral reference |
| C-LP-01 | [CSI SAP2000 Load Patterns](https://docs.csiamerica.com/help-files/sap/Menus/Define/Load_Pattern/Define_Load_Patterns.htm) | SelfWeightMultiplier adds generated gravity load to a static pattern; ordinarily one pattern carries SW | Public HTML behavioral reference |
| O-MOD-01 | [OpenSees modalProperties](https://opensees.github.io/OpenSeesDocumentation/user/manual/analysis/modalProperties.html) | Mass assembly, CoM, total/free mass, lumped vs consistent mass, eigen participation | Public numerical/formula reference |
| O-FRAME-01 | [OpenSees elasticBeamColumn](https://opensees.github.io/OpenSeesDocumentation/user/manual/model/elements/elasticBeamColumn.html) | Beam mass per length, lumped vs consistent mass option | Reference comparison, not runtime dependency |
| C-MOD-01 | [CSI SAP2000 Modal Form](https://docs.csiamerica.com/help-files/sap/Menus/Define/Load_Cases/Modal_Form.htm) | Unstressed or end-of-nonlinear reference stiffness | Behavioral reference |
| B-CANDIDATE | O'Hara & Ramming (2015), *Numerical Structural Analysis*, chapters noted in [literature.md](literature.md) | Candidate general numerical structural analysis source | **Book equations not re-inspected; not verified for new mass formula** |
| C-MANUAL | [CSI Analysis Reference Manual](https://docs.csiamerica.com/manuals/sap2000/CSiRefer.pdf) | Candidate mathematical authority | **Exact PDF equations not re-inspected here; verification OPEN** |

**SOURCE_VERIFIED** requires explicit source equation/section, stated assumptions, coded method, independent hand or OpenSees reference, tolerance rationale, engineering reviewer/date. No new mass rule is automatically SOURCE_VERIFIED by this plan.

## C. User experience specification — Define → Mass Source

New persistent UI:

~~~text
DEFINE / MASS SOURCE
[+ Add] [Copy] [Modify] [Delete] [Set as Default]
Name    Default   SW (auto)   Additional patterns        Total mass
MS1     Yes       1.00        SDL×1.00; LIVE×0.25         [Preview]
MS2     No        1.00        EQUIP×1.00                  [Preview]

MASS SOURCE DATA — MS1
Element Self Mass
  [x, locked] Structural SW — Auto Included — Factor 1.00
  Source: material mass density × section volume × mass modifier

Additional Mass from Load Patterns
  [x] Include Selected Patterns
  Pattern                 Factor    Eligible mass     Warning
  SDL                     1.00      [t]               —
  LIVE                    0.25      [t]               —
  DEAD                    1.00      [t]               Generated SW excluded
  [+ Add Pattern] [- Remove]

Mass Inventory
  Element self mass      [t]        Additional point mass [t]
  SDL                    [t]        LIVE                  [t]
  Total physical mass    [t]        Free UX / UY / UZ     [t/t/t]
  [Preview] [Warnings]                             [Cancel] [Save]
~~~

- [ ] Default Mass Source named MS1 for new blank/native models. No automatic DEAD/SDL/LL selection.
- [ ] Read-only SW row must **not** be just another editable LoadPattern term. The app computes it even when a load pattern named SW does not exist.
- [ ] Add pattern shows all model load patterns with name/type/SW multiplier, but disables unsupported target-only prestress, non-gravity patterns if none of their assignments is eligible. Handle mixed patterns with explicit breakdown.
- [ ] Show amount "Excluded generated SW" when a selected pattern has nonzero SelfWeightMultiplier; if no additional assignments, contribution is **0 t**.
- [ ] Provide enabled extra mass section only for supported joint/line mass features; no inert controls.
- [ ] Modal case form shows Mass Source with an "Edit Mass Sources" link; default or parent-inherited resolved selection clearly labelled.
- [ ] Preview never runs Modal analysis; calculation belongs in Analysis service, VM only displays immutable computed ledger.
- [ ] Compact app-level WPF theme, no new numerical or CSI code in UI.

## D. Mass from SW and load patterns: physics contract

### D1. Structural element self mass (always included)

Native base units kN, m, t, seconds. Gravity is **negative global Z**, with nominal g=9.80665 m/s².

For each compiled prismatic frame piece:

~~~math
m_{SW,e} = \rho_e A_e L_e\,\mu_{mass,e}\quad[t]
~~~

For straight cable same expression with cable mass modifier, subject to sag-vs-chord length QA. Initial **lumped** uniform two-node mass: half to I, half to J per participating translation. Frame splitting must preserve total mass.

Contrast with static selfweight force:

~~~math
W_{SW,e}=\gamma_e A_e L_e\,\mu_{weight,e}\quad[kN].
~~~

**Never** infer mass by dividing static SW load/g if material MassDensity is already being used. WeightModifier and MassModifier are independent. If gamma > 0 but rho = 0, flag missing/possibly deliberately massless input; **do not silently replace rho with gamma/g**. Add an explicit, user-approved material-data conversion later if required.

One physically distributed scalar element mass m usually contributes the same inertia m in each permitted **translational direction**; this does **not** make the physical total 3m. Direct nodal rotational inertia is separate [t·m²]; no artificial rotational mass in the lumped MVP.

### D2. Pattern mass (user-selected additional portion)

For selected pattern P with factor alpha_P and explicit signed downward weight W_P:

~~~math
m_P=\frac{\alpha_P W_{P,explicit\ gravity}}{g},\qquad
M_{total}=M_{self}+M_{assigned}+\sum_P \alpha_P M_{P,explicit\ gravity}.
~~~

M matrices (capital) are the *assembled inertia matrices*, not sums of UX/UY/UZ mass totals. The multiplier is dimensionless and **does not contain g**. Do not use load-case ULS factors automatically.

**Gravity eligibility by load kind (native repository):**

| Source | Handling |
|---|---|
| PointLoad vertical Fz | Use -Fz/g at the target joint; only gravity weight portion; moments/horizontal components are **not** inertial mass |
| FrameDistributedLoad globalZ / Gravity | Project onto downward global Z, integrate physical intensity along the assigned member segment |
| FrameDistributedLoad local axis | Transform load direction with verified local axes; project to global gravity, then integrate |
| FramePointLoad vertical / Gravity | Project downward, distribute physical weight to two adjacent nodes according to load position |
| Generated pattern SelfWeightMultiplier | **Always exclude** from pattern-derived mass; already in m_SW based on rho |
| CableTargetForce, temperature, prestress | Not gravitational mass; explicitly excluded/unsupported |
| Shell/tendon pattern loads | BLOCK until supported kernel for shell/tendon mass; never silently drop |
| Horizontal load, applied moment | Not gravity weight; show excluded warning, do not treat as mass |
| Ambiguous legacy aggregated force with unknown SW provenance | BLOCK conversion and show remediation; do not subtract guessed rho*g |

**Proposed MCOTiS element-level mass lumping (requires independent formula review):** for downward distributed line intensity w(s) on a two-node element length L, with s from I:

~~~math
m_I = \frac{\alpha}{g}\int_0^L\left(1-\frac{s}{L}\right)w(s)\,ds,
\qquad
m_J = \frac{\alpha}{g}\int_0^L\frac{s}{L}w(s)\,ds.
~~~

For downward point load W at relative position xi: m_I=alpha W(1-xi)/g, m_J=alpha W xi/g. For constant w: each end alpha wL/(2g). Use actual subelement bounds for partial/trapezoidal loads and split members. Do **not** use frame bending-equivalent nodal **moments** as point mass.

This nodal distribution is **our documented proposed method**, **not** the proprietary CSI tributary-lumping algorithm. It needs a reviewed FE derivation, conservation check, centroid QA, split invariance, and independent reference benchmarks before shipping.

### D3. The exact automatic SW exclusion algorithm

~~~text
for each MassSource:
    M = structural_self_mass_from_material_rho(model)   // always, factor=1.0
    if include_assigned_mass: M += assigned_joint_line_mass(...)
    for (patternId, factor) in selected_patterns:
        contributions = load_assignments_for_pattern(patternId)
        // NOTE: generated SelfWeightMultiplier contributions are NOT in this list
        for assignment in contributions:
            if explicitly_gravitational(assignment): M += factor * convert_to_mass(assignment)
            elif unsupported(assignment): report/block as applicable
    validate(M) // finite, symmetric, no invalid negative lumped mass
~~~

The compiler/storage must retain generated-SW-versus-explicit assignment provenance; **never** infer by pattern name "SW" or assume the factor in a pattern named DEAD is zero. If legacy import has already flattened static SW into a joint force, it must not be reintroduced into the mass source without verified separation.

Concrete example: DEAD has SelfWeightMultiplier=1 plus an explicitly applied 30 kN downward partition force. Selecting DEAD=1 adds **30/g tonnes only**, while all frame element selfmass stays included once.

**Sign:** downward global Fz negative contributes positive mass. Upward positive Fz contributes negative signed contribution. If net effective mass becomes negative, **reject/run fail with source and joint**, do not take absolute value or quietly clip (CSI SAP/ETABS policies differ; this is MCOTiS conservative product policy).

### D4. Reduced DOFs and results

The mass-from-loads gravity projection is physically global -Z even for modal UX/UY XY-plane DOF settings. It must not be treated as an attempt to apply a static disabled-UZ load to that eigen case. Review current ModelCompiler validation which checks all stored patterns to avoid incorrectly disallowing valid modal mass configuration. Do not silently change model DOFs.

Preview:
- physical selfmass once [t], extra by pattern/source [t];
- per-joint mass contributions with point ObjectId and XYZ;
- total translational inertia UX/UY/UZ [t] (including restrained DOFs);
- mass on *free* DOFs by direction used in modal participation;
- CoM with labelled definition; absent/inactive directions display N/A;
- mass-source hash and source case stiffness fingerprint;
- mismatch warning if gamma/(rho*g) differs significantly; engineer review, no forced equality.

Modal percentages use rᵀMr as the actual direction participation denominator; do not renormalize to 100% over only extracted modes ([OP-MP]).

## E. Repository code-change map

### Core
- src/Mcotis.Core/ModelDocument.cs and DocumentEditor.cs: MassSources dictionary, DefaultMassSourceId, assigned masses, immutable transaction & undo/redo.
- src/Mcotis.Core/Definitions.cs or new Mass.cs: MassSource, MassPatternComponent, JointMassAssignment.
- src/Mcotis.Core/Naming.cs, Primitives.cs: ObjectKind.MassSource, NameScope.MassSource, generated MS names.
- src/Mcotis.Core/DocumentValidator.cs: references, finite factors, source existence, default/del safeguards, case mass link.
- src/Mcotis.Core/AnalysisInputHasher.cs: rho, mass modifiers, selected pattern IDs/factors, effective gravity convention and masses.

### Analysis / Application
- new src/Mcotis.Analysis/Modal/MassAssembler.cs: element self mass and nodal/global assembly.
- new src/Mcotis.Analysis/Modal/GravityMassPatternInterpreter.cs: explicit native load assignments only, signed gravity projection, pattern provenance, exclusion generated SW.
- new src/Mcotis.Analysis/Modal/ModalMassInventory.cs and MassValidation.cs: typed per-source/per-joint ledger, QA diagnostics, no UI.
- extend src/Mcotis.Analysis/ModelCompiler.cs to share verified DOF map but distinguish mass eligibility from static force restrictions.
- src/Mcotis.Application/DocumentCommands.cs / CaseExecutionCoordinator: transactional edits, modal mass-source resolution and invalidation.

### Desktop
- src/Mcotis.Desktop/Presentation/MainWindow.xaml: Define → Mass Source menu item and dedicated hidden FEM pane TabItem.
- src/Mcotis.Desktop/Presentation/MainViewModel.Menu.cs: **append** FemPaneMassSources=9, preserve 0..8, explicitly map to FEM Inputs page in FemPageOf, PaneTitle="MASS SOURCES".
- src/Mcotis.Desktop/Presentation/MainWindow.xaml.cs: navigation only.
- new MassSourcesViewModel.cs and shared editor MassSourceWindow.xaml/.xaml.cs (or reusable pane) with compact rows, read-only auto SW entry, preview.
- src/Mcotis.Desktop/Presentation/NativeModelWindow.xaml/.cs: share Mass Source editor in native workspace.
- LoadCaseWindow.xaml and native modal case editor: selector for effective MassSource, parent inheritance.

### Persistence
- src/Mcotis.Persistence/NativeDocumentFile.cs, NativeDocumentFormat.cs: native .mcotis **3→4** as part of shared case/domain migration, unless already upgraded.
- src/Mcotis.Desktop/Configuration/FemDatabase.cs: SQLite input FemInputSnapshot **1→2**, include normalized mass_sources, mass_source_pattern_terms, joint_mass_assignments; JSON snapshot remains declared authority during migration.
- src/Mcotis.Desktop/Configuration/FemModelFile.cs: legacy .cotfem.json **1→2**; old model defaults to SW-only mass source, never auto-add LL.
- src/Mcotis.Desktop/Resources/Default.cotfem.db: migrate default safely; old static load patterns unchanged.
- .cotfem.out.db: separately versioned mass inventory/result provenance and six-DOF modal output.

Illustrative normalized SQL (final FKs/indexes must be validated against v2 schema):

~~~sql
CREATE TABLE mass_sources (
  mass_source_id TEXT PRIMARY KEY,
  name TEXT NOT NULL UNIQUE COLLATE NOCASE,
  is_default INTEGER NOT NULL CHECK(is_default IN (0,1)),
  include_self_mass INTEGER NOT NULL CHECK(include_self_mass=1),
  include_assigned_mass INTEGER NOT NULL CHECK(include_assigned_mass IN (0,1)),
  payload_json TEXT NOT NULL
);
CREATE UNIQUE INDEX ux_default_ms ON mass_sources(is_default) WHERE is_default=1;
CREATE TABLE mass_source_pattern_terms (
  mass_source_id TEXT NOT NULL REFERENCES mass_sources(mass_source_id) ON DELETE CASCADE,
  pattern_id TEXT NOT NULL REFERENCES load_patterns(pattern_id) ON DELETE RESTRICT,
  multiplier REAL NOT NULL, sequence_no INTEGER NOT NULL,
  PRIMARY KEY(mass_source_id,pattern_id)
);
CREATE TABLE joint_mass_assignments (
  point_id TEXT PRIMARY KEY REFERENCES joints(object_id) ON DELETE CASCADE,
  mux_t REAL NOT NULL, muy_t REAL NOT NULL, muz_t REAL NOT NULL,
  irx_tm2 REAL NOT NULL, iry_tm2 REAL NOT NULL, irz_tm2 REAL NOT NULL
);
~~~

## F. Phased implementation tickets (checkbox TODOs)

### MS-00: Source contract and rule book
- [ ] Review CSI-MS-SAP, CSI-MS-ETABS, CSI-LP and OpenSees mass docs; register source type/limitations.
- [ ] Verify local-to-global gravity directions, native load schema, self-generated vs explicit SW provenance.
- [ ] Obtain independently checked formula for proposed 2-node gravity-to-mass lumping and supported partial/triangular member loads.
- [ ] Define public policy for upward forces, material rho/gamma mismatch, cable sag length and unavailable shell objects.
- **Gate:** engineering sign/units and error rules finalized before numerical coding.

### MS-01: Native MassSource domain
- [ ] Add named MassSource and default pointer, stable IDs, immutable pattern terms and nodal masses to Core.
- [ ] Add transactional Create/Copy/Modify/Delete/SetDefault and referential integrity checks.
- [ ] Define SW policy as invariant (always on); mass source name and selected patterns independent of load case.
- [ ] Hash all numerical inputs; rename-only retains IDs.
- **Gate:** domain tests with two sources and independent IDs; no static solver changes.

### MS-02: Persistence and migration
- [ ] Integrate schema migration with companion load-case PR; one schema bump, not a separate conflicting version.
- [ ] Save/load .mcotis, .cotfem.db and legacy .cotfem.json, including source default and factors.
- [ ] Old model migrates to MS1 selfmass-only; no new SDL/LL terms; static SW multiplier unchanged.
- [ ] Update bundled Default.cotfem.db and verify output-registry/revision/hash semantics.
- **Gate:** one focused roundtrip fixture for old and new documents.

### MS-03: Automatic structural selfmass assembler
- [ ] Assemble element rho × A × L × mass modifier for every compiled frame piece and cable.
- [ ] Lump physical mass to nodes using reviewed scheme; keep active free and restrained DOF mass inventory.
- [ ] Check split-member mass conservation, rotation massless rules, zero/invalid rho warning.
- **Gate:** independent hand numbers, repeated mesh/3D orientation benchmark.

### MS-04: User-selected load-pattern mass — MVP mandatory
- [ ] Implement explicit native gravity assignment selection per PatternId; generated SW excluded.
- [ ] Integrate PointLoad Fz, member globalZ/Gravity line load, member point load and supported local loads.
- [ ] Convert downward vertical physical weight/g with chosen factor, local-to-global projection and correct integration.
- [ ] Record ledger by object/joint/pattern and excluded generated SW; block unsupported ambiguous cases.
- [ ] Refuse invalid negative net mass; never take abs(upward) or silently clip.
- **Gate:** SDL + LIVE×0.25 changes mass; selecting SW-only pattern changes **nothing**.

### MS-05: Define → Mass Source tab and native editor
- [ ] Add menu, append pane index 9 (do not renumber other panes), add Mass Source grid and modal-case selector.
- [ ] Show permanently checked/read-only SW row and interactive additional-pattern table.
- [ ] Add Preview Mass Inventory, validation messages, named sources and default selection.
- [ ] Reuse app-level WPF styles and shared Core editor in both main and native workspaces.
- **Gate:** edit/save/reopen model UI with two sources and correct source preview, no numerical WPF code.

### MS-06: Modal solver and run-result integration
- [ ] Resolve effective mass source and inherited-from-nonlinear behavior when applicable; parent runs first.
- [ ] Feed assembled M to modal massless-DOF reducer and eigen solver, not static RHS.
- [ ] Results inventory and participation use same M; changes propagate hash/stale state.
- [ ] Persist source provenance and mass audit in output DB and report; show N/A for inactive mass direction.
- **Gate:** SW-only vs SW+0.25 LIVE produces physically consistent change in eigen frequencies.

### MS-07: Independent QA/release
- [ ] Verify all supported assignment-to-mass formulas against inspected source/hand benchmark; reviewer sign-off.
- [ ] Compare equivalent model against OpenSeesPy, matching lumped/consistent mass and supports.
- [ ] Run focused mass-source tests plus the existing modal benchmark and static regression gate.
- [ ] Confirm absent mass, negative mass, double SW, unsupported shells/cables and parent-failure behaviors fail safely.
- **Gate:** user can run 2D or 3D eigen modal without SAP/ETABS installed, with accurate mass and traceable sources.

**Revised dependency:** selected load-pattern mass MS-04 is **part of modal MVP** (MOD-06); it is no longer deferred to MOD-10. P-Delta prestressed modal MOD-07 still waits for a verified converged nonlinear tangent stiffness.

## G. Small but sufficient benchmark suite

| ID | Fixture | Expected result |
|---|---|---|
| MASS-01 | Frame rho 7.85 t/m³, A 0.01 m², L 10 m, modifier 1.0 | 0.785 t selfmass; 0.3925 t to each element end per permitted translation. |
| MASS-02 | Same frame split at mid-joint | Physical total still 0.785 t; end nodes 0.19625 t, central 0.3925 t. |
| MASS-03 | Explicit SDL downward joint force 98.0665 kN, factor 1.0 | Additional mass 10.0 t (at g=9.80665). |
| MASS-04 | Explicit LIVE downward 39.2266 kN, factor 0.25 | Additional mass 1.0 t; factor 0 -> none. |
| MASS-05 | Selected pattern generated selfweight multiplier=1, no extra assignments | No additional mass beyond auto SW; modal frequencies unchanged. |
| MASS-06 | DEAD selfweight multiplier=1 + explicit SDL gravity assignment | Auto SW once plus SDL/g only; show excluded generated SW. |
| MASS-07 | Upward force with net negative lumped inertial DOF, target-only pattern, unsupported shell load | Explicit source/joint error; no fabricated modal frequencies. |
| MASS-08 | Variable/partial frame gravity load, 3D tilted member and split nodes | Nodal masses sum to independently integrated downward weight/g, proper CoM. |
| MASS-09 | Save/reopen two sources, rename pattern, migrate old schema | IDs/factors/default preserved; static SW case unchanged. |
| MASS-10 | Same K, SW only vs SW+LIVE×0.25 | Calculated frequency response changes consistently with added mass; eigen residual/participation valid. |

Tests MASS-03/04 concern total mass, not automatically **free** mass if an applied mass is on a fixed joint. In modal tests place the added inertia on participating free points. Keep tests grouped, parameterized and fast; full static suite runs at merge/release, not every edit.

## H. Risk table

| Risk | Preventive requirement |
|---|---|
| Mass density vs weight density disagreement | QA warning; no hidden gamma/g replacement. |
| Double count SW when selected DEAD includes generated selfweight | Generated vs explicit assignment separation; independently test patterns with nonzero multiplier. |
| Import loses selfweight provenance | Explicit error/remediation; never guess and subtract an equivalent rho*g. |
| Horizontal action or prestress wrongly converted to mass | Gravity-only classifier with excluded/unsupported entries in ledger. |
| Massless rotational DOF artificially fixed | Modal condenser solves free massless DOFs properly; no fake restraint/mass. |
| Native 2D XY gravity vs active DOFs mismatch | Distinguish gravity mass interpretation from statically applying a vertical force. |
| Zero/negative eigenmass | Fail with affected joint/DOF, no clipped or invented frequencies. |
| Stale mass source used with nonlinear Kt | Typed dependency + mass-source inheritance fingerprint; block failed parent. |
| Shell/large-sag cable mass assumptions | Explicit unsupported physics or independently verified approximate mass policy. |

## I. Final acceptance checklist

- [ ] **Define → Mass Source** visible in main FEM and native workspaces.
- [ ] **SW auto 1.0** included for every Mass Source, even without SW load pattern.
- [ ] Additional load patterns selectable/editable with independent factors.
- [ ] SW generated load in selected patterns **never** counted again.
- [ ] Same source definitions persisted with stable IDs in native and SQLite versions.
- [ ] Preview displays physical SW, selected pattern masses, free/global UX UY UZ mass and warnings.
- [ ] Modal case uses saved source and computes physically correct result and participation.
- [ ] Failure diagnostics expose name/XYZ/DOF, no silent regularization or fallback.
- [ ] Formula source and independent benchmark QA links completed before software released.
