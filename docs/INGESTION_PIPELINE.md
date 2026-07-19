# Arkadia — Ingestion Pipeline

This document describes the ingest pipeline that moves incoming ROM/archive files through transform/compression and deposits them in the local archive.

---

## Overview

Ingestion takes files from `incoming\<platform>\` and:

1. Identifies which DB releases they correspond to (by filename/hash)
2. Skips files matched only to unwanted releases (moves to `incoming-skip`)
3. Stages wanted files, then assembles each complete release into a temporary transform-input area (`source`)
4. Transforms/compresses the assembled input to the target derived format (CHD, RVZ, etc.)
5. Promotes the derived artifact into the local archive at `archive\<platform>\<datLine>\`, then deletes the temporary source input
6. Creates or updates `derived_artifacts` rows and release status

The pipeline is orchestrated in `MainWindow.axaml.cs` (long method `OnIngestDatLine` / related phases). Progress is reported via `IProgress<IngestionProgress>`.

---

## Key folders

| Folder | Role |
|---|---|
| `incoming\<platform>\` | Arrival zone — source files for ingest |
| `staging\<platform>\<datLine>\` | Temporary hold for wanted files being copied/moved in for processing |
| `source\<platform>\<datLine>\` | **Temporary** transform-input area — a complete release assembled for the transform. **Not a durable raw archive** |
| `incoming-skip\<platform>\` | Suspension zone — files Arkadia cannot or should not process |
| `archive\<platform>\<datLine>\` | Destination — derived artifacts at rest (the durable output) |

**Lifecycle:**

```
incoming → staging → source (temporary transform input) → derived artifact → archive / volume
```

> **`source` is transient.** It holds raw release inputs only while a transform is in progress. After the derived artifact is verified and its DB rows are committed, the source input is deleted. The **durable** output is the derived artifact in `archive\...\` (or, once assigned, on a volume) — never the raw source. Raw source lingers only when a transform fails (kept as recovery material) or a delete is denied by the OS.

---

## Unwanted early-skip (Phase 6 / Phase 8)

This is the most important invariant for unwanted releases.

**Phase 6 — fan-out split (classification):**

When a source file matches one or more DB releases, they are split into:
- `wantedPending` — releases whose status is not `unwanted`
- unwanted targets — logged as **`unwanted-classified`** (matched to an unwanted release, will not be staged), not processed

If `wantedPending` is empty (all matched releases are unwanted):
- The source is deferred to Phase 8.
- `allTargetsUnwanted.Add(srcPath)` — the file is NOT staged and NOT processed in the normal pipeline.

If `wantedPending` is non-empty:
- Unwanted targets are classified/logged but ignored.
- Only wanted targets are staged and processed.

> `unwanted-classified` is a **classification** event only — no file has moved yet. The physical move happens once in Phase 8.

**Phase 8 — incoming-skip move:**

Files in `allTargetsUnwanted` are physically moved to `incoming-skip\<platform>\`:
```csharp
var destPath = IncomingSkipUniquePath(skipDir, fileName);
File.Move(src, destPath);
result.Operations.Add(new IngestionOperation(fileName, "unwanted-moved", ...));
result.UnwantedSkipped++;
```

- **`unwanted-moved`** means the physical file was moved to `incoming-skip\<platform>`.
- **`unwanted-move-failed`** is logged if the move could not be completed.

The ingestion result increments `UnwantedSkipped` **once per file moved** (in Phase 8), and it is surfaced as its own counter (`Unwanted skipped`), separate from the generic `Files skipped` counter (which covers unmatched/duplicate files).

**`UpdateReleaseStatus` guard:**

After a successful transform and promotion, ingestion calls `UpdateReleaseStatus(releaseId, "present")`. This call is SQL-guarded to never touch unwanted releases:

```sql
UPDATE releases SET status = $status WHERE id = $id AND status != 'unwanted'
```

Even if a bug caused an unwanted release to reach the update call, the guard would prevent the status from changing.

---

## Ingestion operations log

Each step emits an `IngestionOperation` with an action key. Current action keys:

| Key | Meaning |
|---|---|
| `hash` | Incoming file hashed and matched against the DB |
| `copy` / `stage-moved` | File copied or moved into `staging` for wanted processing |
| `staging-resumed` | A wanted release with complete staging residue (interrupted run) routed back into the normal transform flow |
| `unwanted-classified` | Matched an unwanted release — will not be staged (Phase 6) |
| `unwanted-moved` | Physical file moved to `incoming-skip\<platform>` (Phase 8) |
| `unwanted-move-failed` | Could not move an unwanted file to `incoming-skip` |
| `release-input-assembled` | Complete release moved from `staging` into the temporary `source` transform-input area (Phase 7) |
| `release-input-assembly-failed` | Could not assemble the release input in `source` |
| `transform` | Compression/transform executed |
| `derived-committed` | Derived artifact hashed and written to the DB |
| `already-present` | Derived artifact already existed and was verified — no re-transform |
| `archive-collision` | Runtime guard refused to overwrite a target owned by a different content identity |
| `archive-validation-blocked` | Ingestion aborted by the archive-output gate (collision_unresolved / stale) |
| `transform-failed` | Transform failed (raw source is retained for recovery) |
| `incomplete-skipped` | Release could not be completed (missing expected files) |
| `delete` | Incoming source file removed after successful staging/transform |
| `archive-deleted` | Extracted archive container deleted after successful extraction |
| `skip` / `skip-failed` | Unmatched file moved (or failed to move) to `incoming-skip` |

> **Terminology note:** `release-input-assembled` replaces the older `source-promoted` label. The word "promoted" was misleading — `source` is a temporary transform-input area, not a durable promotion target. The staging copy/move step is counted and surfaced as **`Files staged`** (formerly "Files copied"), because it counts files placed into `staging`, not archived files or derived artifacts.

---

## Ingestion counters

The final log and the progress-dialog summary render the **same** counter set from a single source of truth: `Ingestion.IngestionSummary.CoreCounters(result)`. The final log is produced by `Ingestion.IngestionLogFormatter`.

| Counter | Meaning |
|---|---|
| `Files scanned` | Files seen in `incoming` |
| `Files matched` | Files matched to known DAT entries (wanted **and** unwanted) |
| `Files staged` | Files copied/moved into `staging` for wanted processing (`IngestionResult.FilesCopied`) |
| `Release inputs assembled` | Complete releases moved from `staging` into the `source` transform input (`ReleaseInputsAssembled`) |
| `Derived artifacts created` | Derived artifacts committed to the DB this run (`DerivedArtifactsCreated`) |
| `Already present` | Releases whose derived artifact already existed and was verified (`AlreadyPresent`) |
| `Releases present` | Releases that reached `present` in this run |
| `Releases incomplete` | Releases that could not be completed |
| `Files skipped` | Non-unwanted skipped files (unmatched / duplicate-deleted) |
| `Unwanted skipped` | Files moved to `incoming-skip` because every match was unwanted (`UnwantedSkipped`) |
| `Transforms failed` | Transform failures |
| `Archives deleted` | Archive containers deleted after successful extraction |

Two edge-case rows are shown **only when non-zero** (normal runs keep the 12 counters above):

| Counter | Meaning |
|---|---|
| `Staging resumed` | Complete wanted staging residue routed back into the normal transform flow (`StagingResumed`) |
| `Stale staging moved` / `Stale source moved` | Stale files for now-unwanted releases relocated to `incoming-skip` (`StaleStagingMoved` / `StaleSourceMoved`) |

> `Unwanted skipped` is counted **separately** from `Files skipped`. An all-unwanted run therefore shows `Files skipped: 0` alongside a positive `Unwanted skipped`, and the summary adds a clarifying note ("no wanted releases acquired; N unwanted file(s) moved to incoming-skip").

> **Counter caveat:** `Derived artifacts created` counts DB commits. On the per-file (`file_extension`) path a re-ingest of an already-derived file still records a commit, so the count can slightly exceed genuinely new artifacts; the release-shape (CHD) path is exact because it short-circuits to `Already present` before committing.

---

## incoming-skip

`incoming-skip\<platform>\` is Arkadia's centralized suspension zone. Files here are inert — they are not scanned by ingest, Append, or Build Volume.

Files land in incoming-skip from multiple sources:

| Source | Condition |
|---|---|
| Ingestion Phase 8 | All matched releases are unwanted (`unwanted-moved`) |
| Ingestion stale cleanup | Leftover `staging`/`source` files for a now-unwanted release (`stale-staging-unwanted-moved` / `stale-source-unwanted-moved`) |
| Verify Archive repair | UnwantedArchiveArtifact, UnknownArchiveFile, ArchiveHashMismatch |
| Verify Archive repair | RedundantArchiveCopy (after volume re-verification) |

`IncomingSkipUniquePath(dir, fileName)` generates collision-safe names (e.g., `Game (2).chd`) so existing files are never overwritten.

To reintroduce a suspended file, move it manually to `incoming\<platform>\` and trigger ingest again.

---

## Stale unwanted cleanup

At the **start of each ingestion run** (before scan, so it applies even to an empty-incoming re-run), Arkadia relocates leftover `staging`/`source` work files that belong to releases now marked `unwanted`. This handles edge-case residue — a release partially staged then vetoed, or failed-transform/failed-delete residue for a release later vetoed — so it does not linger as active pipeline state.

`Ingestion.StaleUnwantedCleanup` implements the rule; `Ingestion.IngestionPaths` provides the shared folder-naming and collision-safe destination logic.

**Conservative rule — a folder is cleaned only when it maps *exclusively* to unwanted releases:**

- Release folders are named `SafeFileName(release.Name)`. Because that sanitization can map two different names to the same folder, a folder is relocated **only if every release whose name maps to it is currently `unwanted`**.
- Folders that also map to a wanted/pending/missing release (a name-collision) are **skipped** — the mapping is ambiguous.
- Folders that map to **no** release (orphans) are **skipped** — Arkadia never guesses from folder names alone.
- Transform workdirs live under `transform-work\`, never in `staging`/`source`, so an active in-flight transform is never in scope.

**Behavior:**

- Each file in a cleanable folder is **moved** (never deleted, never overwritten) to `incoming-skip\<platform>\` with a collision-safe name.
- The emptied release folder (and any emptied subfolders) is removed **only if every file moved successfully**; if any move fails, everything is left in place.
- Locked files or move failures are reported (`stale-staging-cleanup-failed` / `stale-source-cleanup-failed`) and never deleted.

**Counters:** `StaleStagingMoved` and `StaleSourceMoved` are surfaced in the dialog summary and final log **only when non-zero**, so ordinary runs keep the standard counter set.

> This is a **narrow, veto-scoped** cleanup — Arkadia does **not** perform a broad `staging`/`source` sweep. Residue for **wanted** releases (including failed-transform recovery material) is intentionally left untouched.

---

## Resuming interrupted wanted staging

If a wanted release has **all** expected files already present in `staging` but **no valid derived artifact** (an ingest was interrupted after staging, before/around transform), Arkadia now **resumes** it: the release is routed back through the normal Phase 7 transform path so a derived artifact is produced.

`Ingestion.ResumableStagingDetector` implements the detection; it runs at the **start** of each ingest — **before** the empty-incoming early return — so a resume can complete even with nothing new in `incoming` (no need to re-drop the raw files). Detected release IDs are added to `affectedReleaseIds`, and each is logged as `staging-resumed` and counted as `StagingResumed`.

**The detector is read-only.** It only inspects the filesystem and the release list to decide routing — it never moves, deletes, or writes anything and never touches the DB. **Phase 7 still owns** everything that follows: release-input assembly (`release-input-assembled`), transform, derived commit (`derived-committed`), source cleanup, and the `present` status update. A release is never marked `present` unless its derived artifact is created/verified and DB rows are committed.

**A release is resumed only when every guard passes** (otherwise it is skipped with a reason):

| Not resumed when… | Reason |
|---|---|
| Release status is `unwanted` | `unwanted` — a curator veto (handled by stale cleanup) |
| Release status is `present` | `already-present` — a valid derived artifact already exists (`present` is set only after a verified derived commit) |
| Its `SafeFolderName` maps to more than one release | `ambiguous-folder` — no guessing on name-sanitization collisions |
| No staging folder exists for it | `no-staging` |
| Staging is missing one or more expected files | `incomplete-staging` — remains incomplete; nothing is promoted or transformed |

Orphan staging folders (no matching release) are never considered — the detector iterates releases, not folders.

> **`Staging resumed`** = complete wanted staging residue routed back into the normal transform flow. It does **not** re-transform an already-valid derived artifact (the processors' idempotency guards prevent that), and it does **not** touch `source` residue — see the deferred `source`-complete case under [Known limitations](#known-limitations-future-work).

---

## UNWANTED guard summary

| Guard | Location |
|---|---|
| Phase 6 fan-out excludes unwanted targets (`unwanted-classified`) | `MainWindow` ingest loop |
| Phase 8 moves all-unwanted source to incoming-skip (`unwanted-moved`) | `MainWindow` Phase 8 |
| Stale `staging`/`source` for now-unwanted releases relocated to incoming-skip (exclusive-mapping only) | `Ingestion.StaleUnwantedCleanup` |
| `UpdateReleaseStatus` SQL guard prevents promotion resetting unwanted | `DatLineStore.UpdateReleaseStatus` |
| `RestoreWantedRelease` is the only exit from unwanted | `DatLineStore.RestoreWantedRelease` |

See [UNWANTED_RELEASES.md](UNWANTED_RELEASES.md) for the full invariant table.

---

## Archive output validation: form, collision review, and gate

The archive layout is **uniform per DAT line** (see [ARCHIVE_AND_VOLUME_MODEL.md → Archive output policy](ARCHIVE_AND_VOLUME_MODEL.md#archive-output-policy)). Two independent layers keep it consistent:

### Config-time validation + collision review (interactive)

Saving a DAT line in **ConfigureDatLineDialog** resolves the output form and validates the plan (`ArchiveOutputValidator`), persisting the result on `dat_lines`:

| State | Meaning | Save |
|---|---|---|
| `valid_full_set` | full release set has no collisions; curation (Exclude/Restore) does not invalidate it | allowed |
| `valid_with_exclusions` | full set collides, but the current wanted subset is clean because releases are unwanted | allowed |
| `collision_unresolved` | current wanted subset still collides | opens review |
| `unknown` | form could not be determined (e.g. strategy `none`) / legacy line | allowed |

On `collision_unresolved` the **collision review dialog** opens: two colliding releases are shown **side-by-side (Release A | Release B)** with title, safe release name, status, planned filename/path, source files with sizes and SHA1/MD5/CRC, and content identity. Actions:

- **Exclude A / Exclude B** — marks that release **unwanted** (existing curation; **no files deleted**), re-validates, and advances (3+ way groups resolve iteratively).
- **Abort** — cancels; **rolls back** any exclusions made during review and persists **no** partial config (save is atomic).

Collisions are **DAT-line ambiguities resolved curatorially** — never by switching only the colliding releases to a folder layout.

### Ingestion gate (non-interactive)

Ingestion **never shows a dialog**. At the very start of `RunIngestionWork` — before any staging/source/archive write, extraction, incoming move, or transform — the gate (`ArchiveIngestionGateEvaluator` → `ArchiveIngestionGate`) does a read-only re-validation:

| Effective state | Ingestion |
|---|---|
| `valid_full_set` / `valid_with_exclusions` | **allowed** |
| `collision_unresolved` (incl. a restored exclusion re-introducing a collision) | **blocked** |
| `stale` (DAT/strategy changed since config — structural fingerprint mismatch) | **blocked** |
| `unknown` / legacy (no stored fingerprint) | **allowed for now** |

A block sets `result.Error`, emits `archive-validation-blocked`, and returns immediately — **incoming files and release statuses are untouched**. The message tells the user to *open DAT configuration and resolve/re-save*.

### Runtime no-overwrite guard (defense-in-depth)

Independently, every archive writer consults `ArchiveWriteCollisionGuard` immediately before writing: if the target exists and belongs to a **different `content_identity_key`** (or is unclaimed), it emits `archive-collision` and refuses to overwrite — the safety net if a collision ever slips past the config/gate layers.

### Verify DAT (batch policy validation)

**Verify DAT** (the operator button between *Configure DAT* and *Update DAT*, backed by `Archive.ArchiveOutputBatchValidator`) validates the archive-output **policy metadata** for every DAT line at once. It is **not** a filesystem check:

- It is **read-only over releases** — it never moves or deletes files and never marks releases unwanted.
- For each DAT line it resolves the form, analyzes collisions, and **persists** `archive_output_form` / `validation_state` / structural + exclusion fingerprints on `dat_lines`.
- It reports counts and the list of problematic lines (`collision_unresolved`, `stale`, `unknown`/error). Missing/unknown strategy is **reported, not defaulted**.
- Problematic lines are resolved through **Configure DAT** (which runs the interactive collision review — Exclude A/B or Abort).

This is distinct from **Verify Archive** (physical local-archive filesystem verification) and **Verify Volume** (physical volume filesystem verification) — only those two perform file I/O.

---

## Known limitations (future work)

- **Complete wanted staging IS now resumed** (see [Resuming interrupted wanted staging](#resuming-interrupted-wanted-staging)). What remains deferred:
  - **Source-complete / derived-missing is not auto-retried.** A wanted release whose files sit in `source` (not `staging`) with no derived artifact is **not** automatically re-transformed. `source` residue can represent failed-transform recovery material, so retrying it is ambiguous — this remains a separate future workflow. The raw files are retained (no data loss).
  - **`present` in DB but derived physically deleted from `archive`.** If a release is marked `present` but its derived artifact was manually removed from `archive`, it is treated as complete and not resumed. This is a pre-existing edge and remains out of scope; a normal re-ingest of the raw file still repairs it via `DerivedArtifactSatisfactionChecker`.
- **Stale cleanup is scoped to unwanted releases only** (see below). Stale `staging`/`source` residue for wanted releases (e.g. failed-transform recovery material) is intentionally left in place and is **not** swept.
- **Legacy `unknown` DAT lines are not gated yet.** A DAT line never configured/validated under the archive-output policy (no stored structural fingerprint) is **allowed** to ingest as-is until it is reconfigured — this avoids blocking existing users. Its writers still apply the runtime no-overwrite guard.
- **Restore of an excluded release** in a `valid_with_exclusions` line can re-introduce a collision. The **ingestion gate catches it** (`collision_unresolved` → blocked), but a proactive UI hook that flags it at restore time is future UX work.
- **`RunIngestionWork` remains partly UI-private**, so archive-output coverage is at the helper/seam level (validator, planner, gate evaluator, path builder, write planner, collision guard) plus code comments at the call sites, rather than a full end-to-end ingestion test.
- **No archive migration has been performed.** Existing artifacts keep their stored `relative_path` (including legacy release-foldered / source-derived names); the new naming applies to new writes only.

---

## Archive path convention

After ingestion, the derived artifact path stored in `derived_artifacts.archive_path` is:

```
archive/<platform>/<datLineId>/<filename>
```

This is a relative path from `AppContext.BaseDirectory`. `LocalArchiveVerifyService` and `AppendVolumePlanner` both interpret this path relative to `appRoot`.

Paths starting with `incoming-skip/` are excluded from Append candidates (`IncomingSkipIgnored` skip reason). This is why Verify Archive redundant-copy repair can safely move an archive file to incoming-skip without re-introducing it into Append candidates — the DA row still exists but its `archive_path` now begins with `incoming-skip/`.
