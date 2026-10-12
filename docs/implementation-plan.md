# COTVCF Reliability and Product Enhancement Plan

This plan is executed sequentially. A phase is complete only when its acceptance criteria pass and both the application build and relevant offline checks are green. Live SAP2000 reads or writes remain outside automated verification unless explicitly requested.

## Baseline

- Base commit: `c661a0d`.
- Preserve the five-tab compact engineering workflow and the shared preview layout.
- Preserve immutable-baseline child generation, STA-only CSI calls, return-code checks, model identity/unit guards, and rollback behavior.
- Keep the main solution limited to `src/Mcotis.Desktop/Mcotis.Desktop.csproj`.

## Phase 1 - Reliability foundation

Status: Complete

Deliverables:

- Reject malformed numeric editor text instead of retaining a hidden previous value.
- Count loading candidates without materializing ranges; enforce a common materialization ceiling before allocation.
- Validate cable Sync enums, references, cycles, factors, resolved values, and finite/nonnegative ranges during config save/open.
- Save configuration and result artifacts through same-volume temporary files and atomic replacement.
- Reset result artifact state at every run and propagate consolidation/report failures in the typed run outcome.
- Store recovery journals and application logs under a writable per-user application-data directory.
- Encode all untrusted report values before inserting them into HTML.
- Release replaced controller COM references on the STA lane without closing the borrowed SAP2000 application.

Acceptance criteria:

- Invalid text blocks commit/save/run and is visibly invalid.
- Extreme loading ranges return a capped count without allocating the range.
- Broken/circular Sync configuration cannot be saved or opened.
- Failed artifact generation cannot report a fully successful study or expose a stale report as current.
- Existing workflow and SAP write-boundary checks remain green.

## Phase 2 - Run provenance

Status: Complete

Deliverables:

- Add an atomic `StudyRun.json` manifest with schema version, run ID, timestamps, baseline/config fingerprints, selected candidate keys, worker count, per-candidate state, artifact paths, sizes, and SHA-256 checksums.
- Record build, analysis, verification, extraction, consolidation, and report stages.
- Validate artifact ownership against the current run before enabling result actions.
- Surface run ID and manifest health in Tab 05.

Acceptance criteria:

- Every started run has a readable manifest even after cancellation or partial failure.
- Reports from an older/different run are never presented as current.
- Manifest writes are atomic and recoverable.

## Phase 3 - Resume and retry

Status: Complete

Deliverables:

- Discover the latest compatible manifest for the current baseline/config fingerprint.
- Resume unfinished candidates, retry failed candidates, and rebuild explicitly selected candidates.
- Reuse verified analyzed children without overwriting them.
- Explain why a candidate is resumable, reusable, blocked, or requires deletion/rebuild.

Acceptance criteria:

- Interrupted studies resume deterministically.
- Retry dispatches only eligible failed/cancelled jobs.
- Existing child-safety and no-overwrite guarantees remain intact.

## Phase 4 - Preflight and candidate filters

Status: Complete

Deliverables:

- Add a compact preflight dashboard showing L/H/P counts, selected/excluded totals, SAP instances, baseline fingerprint, units, output coverage, existing children, estimated disk footprint, and blocking issues.
- Make validation issues navigate to the owning tab and, where practical, the owning row/control.
- Add persisted candidate-filter rules for loading, height, and pretension bounds plus cross-dimension predicates.
- Evaluate filters lazily and retain exclusion reasons for auditability.

Acceptance criteria:

- No child process starts until preflight passes.
- Total before/after filtering is exact without materializing an unbounded Cartesian product.
- Filtered candidates are reproducible from the saved project.

## Phase 5 - Decision results

Status: In progress

Deliverables:

- Persist study acceptance criteria for absolute U3 and cable-force bounds.
- Evaluate each completed candidate as Pass, Warning, Fail, or Incomplete with explicit governing reasons.
- Provide sortable ranking using normalized weighted deflection, pretension, and height metrics.
- Add shortlist export to self-contained HTML and Excel source data.

Acceptance criteria:

- Status never relies on color alone and every result retains engineering units.
- Missing results are Incomplete, never treated as zero or Pass.
- Ranking is deterministic and covered by offline tests.

## Phase 6 - Cable Sync UX

Status: Planned

Deliverables:

- Add a compact master/follower hierarchy and resolved-force preview.
- Show immediate orphan/cycle/factor validation.
- When removing a master, require an explicit dependent action: reassign or convert followers to Independent.
- Preview candidate-count impact before bulk Sync changes.

Acceptance criteria:

- The UI cannot silently create an orphaned follower.
- Sync hierarchy and resolved values match `PretensionCandidates` exactly.

## Phase 7 - SAP operations and recovery UX

Status: Planned

Deliverables:

- Centralize controller attachment health and COM-reference lifetime.
- Add read-only connection health details and baseline/model fingerprint status.
- Add a Recovery Center over journal metadata with open-folder, inspect, and guided retry actions; no automatic model mutation.
- Add Open Candidate in SAP where model identity checks permit it.
- Expose worker restart/recovery state and actionable failure context.

Acceptance criteria:

- Repeated refreshes do not accumulate controller COM references.
- Recovery data remains writable and discoverable in installed deployments.
- No recovery UI performs a SAP write without the existing guards and an explicit user action.

## Phase 8 - Feature-oriented presentation refactor and final hardening

Status: Planned

Deliverables:

- Extract tab-specific presentation state and commands behind focused feature view models without duplicating application/domain logic.
- Extract tab content into reusable WPF views while retaining one shared viewer and the current tab rail.
- Replace ad-hoc operation messages with typed outcomes where practical.
- Add regression checks for numeric conversion, candidate ceilings, config Sync compatibility, manifest/resume, artifact failure, filter/ranking behavior, and semantic UI labels.
- Remove the test project's hard-coded SAP DLL path in favor of the same overridable `SapInstallDir` property as the app.
- Update README and UI handoff documentation to match shipped behavior.

Acceptance criteria:

- `dotnet build COTVCF.sln` succeeds with no warnings.
- `dotnet run --project tests/COT_VCF.Checks` passes.
- Offline UI smoke passes.
- `git diff --check` passes and no unrelated user changes are overwritten.
