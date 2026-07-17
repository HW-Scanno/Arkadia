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
| `unwanted-classified` | Matched an unwanted release — will not be staged (Phase 6) |
| `unwanted-moved` | Physical file moved to `incoming-skip\<platform>` (Phase 8) |
| `unwanted-move-failed` | Could not move an unwanted file to `incoming-skip` |
| `release-input-assembled` | Complete release moved from `staging` into the temporary `source` transform-input area (Phase 7) |
| `release-input-assembly-failed` | Could not assemble the release input in `source` |
| `transform` | Compression/transform executed |
| `derived-committed` | Derived artifact hashed and written to the DB |
| `already-present` | Derived artifact already existed and was verified — no re-transform |
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

> `Unwanted skipped` is counted **separately** from `Files skipped`. An all-unwanted run therefore shows `Files skipped: 0` alongside a positive `Unwanted skipped`, and the summary adds a clarifying note ("no wanted releases acquired; N unwanted file(s) moved to incoming-skip").

> **Counter caveat:** `Derived artifacts created` counts DB commits. On the per-file (`file_extension`) path a re-ingest of an already-derived file still records a commit, so the count can slightly exceed genuinely new artifacts; the release-shape (CHD) path is exact because it short-circuits to `Already present` before committing.

---

## incoming-skip

`incoming-skip\<platform>\` is Arkadia's centralized suspension zone. Files here are inert — they are not scanned by ingest, Append, or Build Volume.

Files land in incoming-skip from multiple sources:

| Source | Condition |
|---|---|
| Ingestion Phase 8 | All matched releases are unwanted |
| Verify Archive repair | UnwantedArchiveArtifact, UnknownArchiveFile, ArchiveHashMismatch |
| Verify Archive repair | RedundantArchiveCopy (after volume re-verification) |

`IncomingSkipUniquePath(dir, fileName)` generates collision-safe names (e.g., `Game (2).chd`) so existing files are never overwritten.

To reintroduce a suspended file, move it manually to `incoming\<platform>\` and trigger ingest again.

---

## UNWANTED guard summary

| Guard | Location |
|---|---|
| Phase 6 fan-out excludes unwanted targets (`unwanted-classified`) | `MainWindow` ingest loop |
| Phase 8 moves all-unwanted source to incoming-skip (`unwanted-moved`) | `MainWindow` Phase 8 |
| `UpdateReleaseStatus` SQL guard prevents promotion resetting unwanted | `DatLineStore.UpdateReleaseStatus` |
| `RestoreWantedRelease` is the only exit from unwanted | `DatLineStore.RestoreWantedRelease` |

See [UNWANTED_RELEASES.md](UNWANTED_RELEASES.md) for the full invariant table.

---

## Known limitations (future cleanup — not current behavior)

These are **not** implemented today; they are noted so the docs don't over-promise:

- **Stale `staging`/`source` is not swept.** If a transform fails (source kept for recovery), a source delete is denied by the OS, or a release is marked unwanted *after* it was partially staged in an earlier run, the leftover files remain in `staging`/`source`. Nothing automatically relocates or removes them yet. There is **no** stale-staging or stale-source sweeper. Manual cleanup (move aside to `incoming-skip`, never silent-delete) is the only current remedy.
- **Interrupted-run resumability** for files already in `staging` is not yet automatic.

---

## Archive path convention

After ingestion, the derived artifact path stored in `derived_artifacts.archive_path` is:

```
archive/<platform>/<datLineId>/<filename>
```

This is a relative path from `AppContext.BaseDirectory`. `LocalArchiveVerifyService` and `AppendVolumePlanner` both interpret this path relative to `appRoot`.

Paths starting with `incoming-skip/` are excluded from Append candidates (`IncomingSkipIgnored` skip reason). This is why Verify Archive redundant-copy repair can safely move an archive file to incoming-skip without re-introducing it into Append candidates — the DA row still exists but its `archive_path` now begins with `incoming-skip/`.
