# Arkadia — Ingestion Pipeline

This document describes the ingest pipeline that moves incoming ROM/archive files through transform/compression and deposits them in the local archive.

---

## Overview

Ingestion takes files from `incoming\<platform>\` and:

1. Identifies which DB releases they correspond to (by filename/hash)
2. Skips files matched only to unwanted releases (moves to `incoming-skip`)
3. Transforms/compresses source files to the target derived format (CHD, RVZ, etc.)
4. Promotes transformed files into the local archive at `archive\<platform>\<datLine>\`
5. Creates or updates `derived_artifacts` rows and release status

The pipeline is orchestrated in `MainWindow.axaml.cs` (long method `OnIngestDatLine` / related phases). Progress is reported via `IProgress<IngestionProgress>`.

---

## Key folders

| Folder | Role |
|---|---|
| `incoming\<platform>\` | Arrival zone — source files for ingest |
| `incoming-skip\<platform>\` | Suspension zone — files Arkadia cannot or should not process |
| `archive\<platform>\<datLine>\` | Destination — derived artifacts at rest |

---

## Unwanted early-skip (Phase B / Phase 8)

This is the most important invariant for unwanted releases.

**Phase B — fan-out split:**

When a source file matches one or more DB releases, they are split into:
- `wantedPending` — releases whose status is not `unwanted`
- unwanted targets — logged as `unwanted-skipped`, not processed

If `wantedPending` is empty (all matched releases are unwanted):
- The source is deferred to Phase 8.
- `allTargetsUnwanted.Add(srcPath)` — source file is NOT processed in the normal pipeline.

If `wantedPending` is non-empty:
- Unwanted targets are logged but ignored.
- Only wanted targets are processed.

**Phase 8 — incoming-skip move:**

Files in `allTargetsUnwanted` are moved to `incoming-skip\<platform>\`:
```csharp
var destPath = IncomingSkipUniquePath(skipDir, fileName);
File.Move(src, destPath);
result.Operations.Add(new IngestionOperation(fileName, "unwanted-skipped", ...));
result.UnwantedSkipped++;
```

The ingestion result includes `UnwantedSkipped` for the summary dialog.

**`UpdateReleaseStatus` guard:**

After a successful transform and promotion, ingestion calls `UpdateReleaseStatus(releaseId, "present")`. This call is SQL-guarded to never touch unwanted releases:

```sql
UPDATE releases SET status = $status WHERE id = $id AND status != 'unwanted'
```

Even if a bug caused an unwanted release to reach the update call, the guard would prevent the status from changing.

---

## Ingestion operations log

Each step emits an `IngestionOperation` with an action key:

| Key | Meaning |
|---|---|
| `unwanted-skipped` | Matched release is unwanted — skipped |
| `transform-started` | Compression/transform begun |
| `transform-complete` | Transform succeeded |
| `transform-failed` | Transform failed |
| `promote-started` | Moving artifact to archive |
| `promote-complete` | Artifact in archive, DB updated |
| `skip-failed` | Could not move file to incoming-skip |

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
| Phase B fan-out excludes unwanted targets | `MainWindow` ingest loop |
| Phase 8 moves all-unwanted source to incoming-skip | `MainWindow` Phase 8 |
| `UpdateReleaseStatus` SQL guard prevents promotion resetting unwanted | `DatLineStore.UpdateReleaseStatus` |
| `RestoreWantedRelease` is the only exit from unwanted | `DatLineStore.RestoreWantedRelease` |

See [UNWANTED_RELEASES.md](UNWANTED_RELEASES.md) for the full invariant table.

---

## Archive path convention

After ingestion, the derived artifact path stored in `derived_artifacts.archive_path` is:

```
archive/<platform>/<datLineId>/<filename>
```

This is a relative path from `AppContext.BaseDirectory`. `LocalArchiveVerifyService` and `AppendVolumePlanner` both interpret this path relative to `appRoot`.

Paths starting with `incoming-skip/` are excluded from Append candidates (`IncomingSkipIgnored` skip reason). This is why Verify Archive redundant-copy repair can safely move an archive file to incoming-skip without re-introducing it into Append candidates — the DA row still exists but its `archive_path` now begins with `incoming-skip/`.
