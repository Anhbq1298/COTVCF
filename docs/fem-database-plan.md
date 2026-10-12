# FEM/VCF Model Database Plan

## Goal and boundary

Replace the current monolithic `.cotfem.json` persistence with a versioned, portable SQLite model database that can restore the editable FEM/VCF workspace after reopening the app. The database covers the resolved model used by the internal FEM workflow:

- geometry, joints, frames, cables, restraints, and object assignments;
- load patterns, load assignments, nonlinear-static load cases, load-case components, and load combinations;
- cable target-force case classification and target assignments;
- materials, frame sections, cable sections, tendon sections, section modifiers, and member assignments;
- FEM Output Settings, monitored object groups, visualization options, and report criteria;
- FEM analysis settings and the app's edit lock state.

The database explicitly excludes MB tab state: design ranges, candidate generation, study selection, MB output settings, study journals, and MB report artifacts. If MB or SAP generated the current FEM model, the resolved joints, members, loads, materials, and sections are persisted, but the controls and parametric data that generated them are not.

## Files and ownership

Use three distinct file roles:

| Role | Suggested name | Ownership and behavior |
|---|---|---|
| Input database | `ProjectName.cotfem.db` | User-owned source model. Open is read/copy; explicit Save atomically replaces this file. |
| Working database | `%LOCALAPPDATA%/Mcotis/Sessions/<model-id>/working.cotfem.db` | App-owned live session. Every accepted edit is committed here so an app restart can recover the last state. |
| Direct-run output database | `output/<run-id>.cotfem.out.db` | Immutable analysis output for one direct run. Run creates it; Save never merges results into the input database. |
| Optimization output databases | `optimization/<optimization-id>/<candidate-id>.cotfem.out.db` | Future optimization layout. Each candidate owns a complete output database tied to the same locked base revision. |

On Open, copy the input database into a session directory and edit the working copy. Maintain a small session manifest containing the input path, model ID, last opened time, saved revision, and working revision. On startup, reopen a cleanly closed session or offer recovery when the working revision is newer than the saved revision.

The `output` and `optimization` directories live beside the input database in a project-owned result directory. The working database keeps an output registry with the exact paths, model ID, revision hash, run ID, and file hash for every file created by the app.

On Save, validate the working model, checkpoint/close the working connection, create a complete SQLite backup beside the destination, and atomically replace the input database. Reopen the working connection after the replacement. Detect external changes to the input file with its saved revision and SHA-256 before replacement.

Use `Microsoft.Data.Sqlite`, foreign keys, explicit transactions, and schema migrations. WAL may be used for the working database, but Save must checkpoint it and produce a single self-contained destination file without `-wal` or `-shm` sidecars.

## State and revision rules

Do not reuse `ModelInfo.IsLocked`; that value represents the source/SAP model lock. Add a separate application model state:

| State | Editing | Save | Run | Result status |
|---|---:|---:|---:|---|
| Unlocked, clean | Enabled | Enabled | Enabled when valid | No output is allowed to remain for the active model. Run validates and locks automatically. |
| Unlocked, modified | Enabled | Enabled | Enabled when valid | No output is allowed to remain for the active model. Run validates and locks automatically. |
| Locked | Disabled | Enabled | Enabled | Run and post-processing use the exact locked revision. |
| Running | Disabled | Disabled | Already active | Output database is written transactionally. |

Run owns the lock transition. When the user presses Run, the app first performs full validation, commits the working transaction, creates an immutable revision record and content hash, records `locked_revision_id`, and only then starts analysis. There is no requirement for the user to press Lock separately. A failed or cancelled Run remains locked so the user can rerun the identical model or explicitly Unlock it.

Unlock is a destructive result reset. Before changing the model to editable state, the app deletes every registered direct-run output and every registered optimization candidate database belonging to the active model/revision, clears the output registry and in-memory results, and removes empty app-owned output directories. It then creates a new editable revision based on the formerly locked revision. Post-processing stays empty until the user presses Run again.

Deletion must be narrowly scoped: verify each database header, model ID, revision hash, normalized path, and registry entry before deleting it. Never recursively delete an unverified directory or unrelated user file. If a registered output cannot be deleted, Unlock fails and the model remains locked so the state cannot claim to be editable while valid results still exist.

Persist the lock state in the working database and in the input database after Save. Reopening restores both the model and its saved lock state. Every output database records the input model ID, revision ID, and content hash. An unlocked model must have no registered output databases; the user must Run to recreate them.

## Domain object model

Names are currently used as object identity. Introduce stable IDs so rename operations do not break assignments:

- `JointId`, `MemberId`, `MaterialId`, `SectionId`, `LoadPatternId`, `LoadCaseId`, and `LoadCombinationId`;
- unique user-facing names remain editable and are protected by case-insensitive unique indexes;
- references use IDs and foreign keys, never display names.

Add explicit model objects while retaining a conversion adapter to the current `GeometryModel` and solver contracts:

- `JointObject`: ID, name, coordinates, coordinate system, and metadata;
- `FrameObject`: ID, name, start/end joint IDs, section assignment, releases, local-axis data, and metadata;
- `CableObject`: ID, name, start/end joint IDs, cable section assignment, target-force assignment, sync/dependency data, and metadata;
- reserve a member discriminator for `TendonObject` and later object types without changing the base schema.

### Parametric object naming

All geometry created or rebuilt by the parametric workflow must be named automatically. Add one `GeometryNamingService` used by every geometry handler; individual builders must not infer or invent names independently.

The default naming policy is role- and topology-based:

| Object | Default generated names |
|---|---|
| Top, middle, bottom chord joints | `TTrPt001...`, `MTrPt001...`, `BTrPt001...`, ordered left to right |
| End-frame/support joints | Role plus side and outward sequence, for example `EndPtL001`, `EndPtR001`, `SupPtL001`, `SupPtR001` |
| Top, middle, bottom chord frames | `TCh001...`, `MCh001...`, `BCh001...`, ordered left to right |
| Vertical/Vierendeel frames | `VFr001...`, ordered left to right and bottom to top |
| Mega columns and end frames | Side-aware sequences such as `MColL001`, `MColR001`, `EFrL001`, `EFrR001` |
| Symmetric cable pairs | `Cable001A` and `Cable001B`, then `Cable002A/B...`, ordered from the outer support toward midspan |
| Centre/self-symmetric cable | `Cable001C...` when a topology has a genuinely unpaired centre cable |

The exact prefixes are stored as a versioned naming policy so a later UI can change display conventions without changing stable IDs. The first implementation uses the defaults above.

Each generated object stores an immutable `generation_key` such as object type, structural role, level, side, bay/line index, and symmetric-pair index. Rebuilding parametric geometry matches by this key to preserve IDs and assignments for unchanged physical objects, then recomputes contiguous display names for the complete generated set. Adding or removing bays therefore cannot leave numbering gaps, duplicate names, apostrophe suffixes, or asymmetric cable names.

Imported and manually created objects store their origin (`Imported`, `Generated`, or `User`). Parametric regeneration renames only `Generated` objects. A case-insensitive collision with an imported/user name is a blocking validation error with a rename preview; the generator never silently overwrites or mutates an unrelated object. All assignments continue to follow stable IDs while display names change.

Create focused handlers under `src/Mcotis.Desktop/Geometry/Handlers/`:

- `JointGeometryHandler.cs` for coordinates, restraint validation, rename/delete rules, and solver-node conversion;
- `FrameGeometryHandler.cs` for topology, section/release validation, member geometry, and solver-frame conversion;
- `CableGeometryHandler.cs` for topology, section/target validation, independent/dependent sync resolution, and solver-cable conversion;
- `GeometryNamingService.cs` for deterministic role/topology names, symmetry pairing, collision checks, and rename maps;
- `GeometryModelAdapter.cs` to build the existing name-based `GeometryModel` snapshot required by the viewer and solver during migration.

Handlers own geometry behavior and validation. SQL mapping stays in repository classes so geometry logic does not depend on SQLite.

## Input database schema

All tables include stable primary keys and, where applicable, `created_utc`, `updated_utc`, and revision ownership. Exact DDL is finalized in Phase 1, but the initial schema must contain these groups.

### Database and revisions

- `database_info`: format name, schema version, database ID, app version, created/saved timestamps, force unit, and length unit.
- `model_state`: active revision, saved revision, edit state, locked revision, and model display metadata.
- `model_revisions`: revision ID/number, parent revision, state, content SHA-256, timestamp, and optional note.
- `geometry_naming_policy`: policy version, prefixes, padding width, and symmetry suffixes.

### Geometry and assignments

- `joints` and `joint_restraints`, including object origin and optional generation key;
- `members` with a `member_type` discriminator, start/end joint foreign keys, object origin, and optional generation key;
- `frame_objects`, `cable_objects`, and future subtype tables for type-specific data;
- `member_section_assignments`;
- optional extensibility tables `object_tags` and `object_user_properties` for metadata that must not alter the core schema.

### Loads and analysis definitions

- `load_patterns`: name, engineering type, self-weight multiplier, and sequence;
- `joint_load_assignments`, `member_distributed_load_assignments`, `member_point_load_assignments`, and `member_gravity_load_assignments`;
- `cable_target_assignments`: cable ID, pattern ID, target force, relative location, and sync/dependency definition;
- `load_cases`: name, `NonlinearStatic` analysis type, purpose (`Normal` or `CableTargetForces`), continue-from case, run flag, and sequence;
- `load_case_components`: case ID, pattern ID, factor, and sequence;
- `load_combinations` and `load_combination_terms`;
- `analysis_settings`: target tolerance, maximum fit iterations, frame mesh segments, and cable-sag option.

### FEM Output Settings

Create a new Output Settings tab for the FEM workflow. It is independent from the MB `Tab05OutputSettings` DTO but carries equivalent proven behavior where it applies to the internal solver:

- `output_settings`: units, default result case/combination, deformation scale, diagram scale, label density, sign convention, envelope mode, and enabled result modules;
- `output_monitor_groups`: named groups for deflection joints, member forces, left/right supports, cable forces, and user-defined groups;
- `output_monitor_members`: group-to-joint/frame/cable assignments by stable object ID and display order;
- `output_result_selections`: selected case/combination and components for each result family;
- `output_limit_bands`: optional lower/upper engineering limits and display/qualification flags;
- `output_chart_settings`: default chart view, station sampling, grouping, series visibility, and palette/line-style policy.

Fixed result units remain displacement mm, force kN, and moment kNm. Output Settings are model inputs and are editable only while the model is unlocked. Changing them therefore requires Unlock, deletes the old outputs under the common lifecycle rule, and requires a new Run.

The first version supports:

- deflection monitor joints and a downward-deflection limit band;
- frame groups for axial force, shear, bending moment, and station results;
- left/right support groups with point reactions and foundation totals;
- cable groups with force, utilization, active/slack state, sag, and target-fit convergence;
- an explicit result case or combination for every result family, never an implicit first-case fallback.

Load cases default to `NonlinearStatic`. Only a case whose purpose is `CableTargetForces` may run target-force fitting. Every other case is solved normally and does not alter cable preset forces.

### Materials

- `materials`: shared identity, name, `material_type`, grade, display color, mass/weight density, elastic modulus, Poisson ratio, thermal coefficient, and cost data;
- type-specific detail tables for concrete, structural steel, rebar, tendon, aluminum, cold-formed steel, masonry, and general/no-design material properties.

The discriminator values follow the current `MaterialKind` values. Unknown future types must be rejected by an older app with a clear newer-schema message rather than silently loaded as `Other`.

### Sections

Classify sections first by assignment category, then by physical shape:

| Category | Supported section types |
|---|---|
| Frame: standard | Rectangle, Box/Tube, I/Wide Flange, Pipe, Circle, Channel, Tee, Angle |
| Frame: composite | Concrete Filled Tube, Steel Encased Composite, Mega Composite |
| Frame: general | General / Section Designer with explicit area, I33, I22, torsion, shear area 2, and shear area 3 |
| Cable | Material plus area and cable modifiers |
| Tendon | Material plus area and tendon modifiers |

Use `sections` as the shared identity table, with `section_category` and `section_type`. Store standard dimensions in `frame_section_dimensions`, explicit calculated/given values in `section_property_values`, composite constituents in `composite_section_components`, cable/tendon data in `strand_section_properties`, and modifiers in `section_modifiers`. This keeps section classification queryable while allowing new shapes and composite components to be added through migrations.

## Output database schema

Create the output in a temporary file and atomically rename it only after the run completes. A failed or cancelled run may retain a clearly marked diagnostic database, but it must never report `Completed`.

- `run_info`: run ID, state, source input path, input database ID, locked revision ID/hash, app/solver version, units, timestamps, and failure message;
- `run_cases`: case/combination identity, name, type, purpose, execution order, convergence state, and iteration counts;
- `joint_displacements`;
- `support_reactions`;
- `frame_end_results` and `frame_station_results`;
- `cable_results`: target force, applied preset, final axial force, active/slack state, sag, and tangent stiffness ratio;
- `cable_fit_iterations`: cable, target case, iteration number, applied preset, resulting force, and ratio;
- `run_messages`: warnings and diagnostic messages.

Index result tables by `(run_case_id, object_id)` and frame stations by `(run_case_id, frame_id, station)`. Store display names as run-time snapshots in addition to IDs so an output remains readable after the input model is renamed or moved.

Post-processing, the 2D viewer, HTML reports, and charts must read through `IAnalysisOutputRepository`; they should be able to reopen a completed output database without rerunning analysis.

For future optimization, one locked base revision produces an `optimization/<optimization-id>/` folder. Each candidate writes to its own temporary database and atomically promotes it to `<candidate-id>.cotfem.out.db` after completion. Candidate databases contain their candidate parameters, base revision hash, result tables, convergence history, and status. An optimization index/manifest records candidate IDs and database hashes but does not combine all candidate result rows into one large database. Unlock removes the registered candidate databases and optimization index for that model/revision.

## Visualization and post-processing target

Bring the FEM result experience to the level already established by MB reports while using the new output databases as the only numerical source:

- the 2D model viewer overlays axial force/cable force, shear, bending moment, support reactions, and deflected shape with consistent legends, engineering units, pan/zoom, label visibility, and selectable result case/combination;
- deflected shape shows undeformed and deformed geometry together, the active deformation scale, monitored points, exact values, and configured limit bands;
- frame diagrams preserve signed station results and never collapse an auto-meshed frame to one undocumented governing value;
- cable target-fit charts appear only for `CableTargetForces` cases and plot preset, result, target band, and every fit iteration; normal cases show cable force results without a convergence chart;
- support reactions show both vector direction on the model and exact point/foundation totals in tables;
- every chart tooltip and table row identifies case/combination, object, station, component, units, and governing source;
- missing/nonconverged data displays `n/a` with a reason and is never converted to zero.

Generate self-contained HTML from the output database with the reusable MB interaction patterns:

- Summary as the default view;
- Profile and station drill-down for frame groups;
- Envelope and Station Matrix views where multiple objects/stations exist;
- result-family and monitored-group selectors;
- consistent series colors plus line styles/labels so meaning does not rely on color alone;
- optional limit-band shading, governing-value tables, source-data tables, and the existing report print engine;
- charts and tables generated from the same typed query result so they cannot disagree.

Output Settings controls which modules, objects, cases, components, limits, and initial chart views are produced. Post-Processing contains only View Results/report actions and does not duplicate setup controls.

## Application boundaries

Add interfaces in Application and SQLite implementations in Infrastructure:

- `IModelSessionRepository` for open, working autosave, revision, Run-triggered lock, Unlock cleanup, and atomic Save;
- `IGeometryRepository`, `ILoadRepository`, `IMaterialRepository`, and `ISectionRepository` for typed model data;
- `IOutputSettingsRepository` for monitor groups, result selections, limits, and visualization defaults;
- `IAnalysisOutputRepository` for run creation, batched result writes, completion, and result reads;
- `ModelSessionService` to coordinate dirty state, recovery, lock/unlock, and Save;
- `FemRunPersistenceService` to lock a validated model snapshot, register its output database, and commit or remove incomplete output.
- a future `OptimizationPersistenceService` to create the optimization folder/index and isolate each candidate in its own database.

Presentation binds to the service and domain objects. View models do not issue SQL, and the solver does not know about SQLite. The existing solver receives an immutable adapter snapshot so the numerical implementation can remain unchanged during this work.

## UI changes

- File menu: New Model Database, Open Model Database, Save, Save As, and Recover Session.
- Add a top-level FEM Output Settings tab outside MB. It contains result-family case/combo selectors, monitor-object pickers, member groups, support groups, cable groups, limits, diagram defaults, and chart defaults.
- Add a compact state control showing `UNLOCKED`, `UNLOCKED · MODIFIED`, `LOCKED · REV n`, or `RUNNING`.
- Run validates and locks automatically. Validation failures are grouped by Geometry, Loads, Materials, Sections, and Analysis Setup without changing the state.
- Show Unlock only for a locked model. Unlock deletes the registered outputs first, clears every result view, and enables editing only after cleanup succeeds.
- Disable all model editors while locked; keep result selection and post-processing available.
- Enable Run for a valid unlocked model and for rerunning an already locked revision.
- Show the active input path, working revision, saved revision, and latest matching output in the status area.
- Do not show stale results after Unlock. Output opening verifies that the model is locked and that its model ID and revision hash match exactly.
- Post-Processing opens the selected completed output and restores the Output Settings defaults, while still allowing temporary zoom/filter/view changes that do not modify the model database.

## Migration and compatibility

Keep `.cotfem.json` as a read-only import format during the transition:

1. Validate the existing file with the current reader.
2. Import geometry, resolved loads, load definitions, material/section libraries, assignments, and FEM settings.
3. Deliberately ignore `CotVcfConfigDto` MB ranges, candidate/study data, study journals, and MB report artifacts. If the old file contains compatible monitored joints/frames/cables, result-case selections, or limit bands, translate those values once into the new FEM Output Settings tables without retaining the MB DTO.
4. Convert name references to stable IDs and report every unresolved or duplicate reference before creating the database.
5. Derive generation keys and preview the automatic naming map; do not silently rename imported/user objects during migration.
6. Save the new database beside the JSON only after the import transaction passes validation.

Convert the embedded default model to the new database resource after the importer and round-trip checks pass. Do not remove the old reader until at least one released format version can migrate it.

## Implementation phases

### Phase 1 - Contracts and schema

- Add `Microsoft.Data.Sqlite` and separate input/output schema migration runners.
- Define stable ID value types, edit states, revision metadata, and repository interfaces.
- Write schema creation and migration checks before wiring UI.

Acceptance: a new empty database can be created, reopened, migrated idempotently, and rejected cleanly when its schema is newer than the app.

### Phase 2 - Geometry objects and handlers

- Add joint/frame/cable domain objects and the three geometry handlers.
- Add `GeometryNamingService`, generation keys, origin tracking, symmetric cable pairing, rename previews, and collision validation.
- Add ID-based validation and `GeometryModelAdapter`.
- Keep viewer and solver behavior unchanged through adapter checks.

Acceptance: rename/delete operations preserve or reject references deterministically; identical parametric input produces identical contiguous joint/frame/cable names; add/remove-bay operations preserve IDs and assignments by generation key; symmetric cables share a sequence with `A/B` suffixes; and the adapted geometry matches the current solver input.

### Phase 3 - Complete input persistence

- Persist geometry, naming policy, restraints, loading assignments, patterns, cases, combinations, materials, sections, assignments, FEM settings, and FEM Output Settings.
- Implement working-session autosave and startup recovery.
- Ensure no MB configuration table or DTO is written.

Acceptance: save/open round trips every supported object, output monitor group, result selection, limit, and property; restarting without explicit Save recovers the working revision; the saved input changes only after Save.

### Phase 4 - Lock, unlock, and revision UI

- Add dirty tracking, model validation, revision hashes, Run-triggered Lock, destructive Unlock cleanup, and edit gating.
- Persist and restore the state independently of the SAP lock state.

Acceptance: reopening restores geometry, setup, assignments, and lock state; Run atomically locks a valid unlocked model before analysis; an invalid model cannot Run; Unlock deletes all verified outputs and leaves post-processing empty until the next Run.

### Phase 5 - Output database and post-processing

- Write one output database per run in batches within transactions.
- Persist all cases/combinations, frame stations, cable target convergence, reactions, displacements, messages, and provenance.
- Switch the viewer, exact-value tables, self-contained HTML charts, and post-processing to the output repository.
- Implement the Output Settings tab and the MB-level Summary, Profile/station, Envelope, and Station Matrix views.
- Add diagram overlays for axial/cable force, shear, moment, reactions, and deflected shape, including legends, units, scaling, and traceable tooltips.
- Add the output registry and narrowly scoped cleanup used by Unlock.
- Reserve the `optimization/<optimization-id>/<candidate-id>.cotfem.out.db` layout and candidate provenance contract.

Acceptance: a completed output can be reopened after restarting the app and reproduces the same diagrams, tables, and HTML charts while the model remains locked; every displayed value is traceable to its case/object/station; missing data is never shown as zero; a failed write cannot appear as a completed run; Unlock removes the registered direct and optimization outputs; a result cannot be mistaken for another input revision.

### Phase 6 - JSON migration and default model

- Implement the one-way `.cotfem.json` importer.
- Convert the embedded default FEM model.
- Keep a diagnostic JSON export only if needed for support; it is not the primary save format.

Acceptance: current sample/default files import with no unresolved references and produce numerically equivalent solver inputs.

### Phase 7 - Hardening and release

- Add indexes and batch-size tuning for frame-station outputs.
- Test atomic replacement, external input modification, interrupted Save, corrupt working database, cancelled Run, and recovery cleanup.
- Update UI handoff and user documentation.

Acceptance: `dotnet build COTVCF.sln`, `dotnet run --project tests/COT_VCF.Checks`, database round-trip/recovery checks, and `git diff --check` all pass. No automated check connects to SAP2000.

## Required regression checks

- Input round trip for every geometry object, load assignment, material kind, and section type.
- Output Settings round trip for monitor groups, stable object references, case/combination selections, limit bands, and chart defaults.
- Deterministic parametric naming for every joint, frame, and cable, including bay add/remove, collision rejection, and symmetric cable sequence pairing.
- Case purpose round trip: `CableTargetForces` runs fitting; `Normal` never does.
- Independent/dependent cable sync round trip and missing/cyclic dependency rejection.
- Referential integrity for object deletion, renaming, and section/material assignment.
- Lock state and revision hash restoration after normal close and crash recovery.
- Run on a valid unlocked model creates the locked revision before any output is written.
- Unlock deletes only verified, registered outputs, clears result state, and requires a new Run before output exists again.
- A locked failed/cancelled run can be retried without changing its revision.
- Future optimization layout keeps one isolated database per candidate and removes all registered candidate outputs on Unlock.
- Save failure leaves the previous input database byte-for-byte valid.
- Output provenance rejects or marks a mismatched input revision.
- Numerical result round trip for joints, reactions, frames/stations, cables, and fit history.
- Viewer/table/HTML agreement for each result family, including signed station values, units, governing source, and missing-result behavior.
- Verification that MB ranges, candidate selections, study data, and MB reports never enter the FEM input database.

## Decisions fixed by this plan

- SQLite is the persistence engine; JSON remains an import format.
- The app edits a recoverable working copy; Save atomically overwrites the chosen input database.
- Run automatically creates an immutable locked revision and writes a separate immutable output database.
- Unlock deletes the active revision's registered outputs; results cannot survive into editable state.
- Optimization stores one output database per candidate under an app-owned optimization folder.
- Geometry uses stable IDs, typed object handlers, generation keys, and centralized automatic naming while the current solver is preserved behind an adapter.
- FEM has its own persisted Output Settings tab and MB-level result visualization driven by output databases.
- The FEM database contains resolved model state only and excludes MB tab state.
