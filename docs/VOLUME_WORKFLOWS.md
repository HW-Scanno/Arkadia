# Arkadia — Volume Workflows

This document covers the three volume management workflows: Append Volume, Fillback Volume, and Verify Volume. For the underlying archive/volume model, see [ARCHIVE_AND_VOLUME_MODEL.md](ARCHIVE_AND_VOLUME_MODEL.md).

---

## Volume layout invariant

All active artifacts in a volume are stored **flat** in the volume root — no release-name subfolders:

```
<volume root>\<artifact filename>    ← correct
<volume root>\Release Name\artifact  ← wrong (legacy; Verify Volume relocates these)
```

`VolumeArtifactPathBuilder.GetFlatFullPath(volumeRoot, fileName)` is the authority. Use it everywhere.

---

## Append Volume

Append fills a selected volume from the active local archive. It is a copy (not a move) — the archive source is never deleted by Append.

### Candidate selection

Append selects candidates from `DatLineStore.GetAllWantedArtifactInfos()`. Candidates must satisfy all of:

1. Not linked to any unwanted release (UNWANTED WINS — excluded at query level)
2. Not already assigned to any volume (`GetAssignedDerivedIdsByDatLine`)
3. Physical archive file exists at the path recorded in `derived_artifacts`
4. Archive path is not under `incoming-skip\` (IncomingSkipIgnored skip reason)
5. Size > 0 and valid (InvalidSize skip reason)
6. Expected SHA-1 is non-empty (InvalidHash skip reason)
7. Target path does not already exist on the volume (TargetPathExists skip reason)
8. Size fits within remaining target free space (TooLargeForRemainingTargetSpace skip reason)

Stale assignments (volume no longer in catalog) are detected and reported as `StaleVolumeAssignment`.

### Execution

1. `AppendVolumePlanner.Plan()` — dry-run producing `AppendVolumePlan` with all entries and diagnostic counters.
2. `AppendVolumeService.Execute()` — for each planned entry:
   - Copy archive file to target path
   - Compute SHA-1 of copy
   - If hash matches: call `CatalogService.AddVolumeArtifactAndIncrementSize(va, sizeBytes)` (atomic DB commit)
   - If hash mismatch: delete partial copy, log error
   - Emit progress event: `append-copying`, `append-copied`, or `append-error`

DB write only happens after successful hash verification. No partial state is committed.

### SkipReason constants (`AppendVolumePlanner.SkipReason`)

| Constant | Meaning |
|---|---|
| `AlreadyAssigned` | DA is assigned to a volume (label in verbose reason) |
| `StaleVolumeAssignment` | Assigned volume no longer in catalog |
| `ArchiveMissing` | Physical archive file not found (path in verbose reason) |
| `TargetPathExists` | Target path already exists on volume |
| `TooLargeForRemainingTargetSpace` | DA size exceeds available volume free space |
| `InvalidHash` | Expected SHA-1 is empty |
| `InvalidSize` | DA size is zero or negative |
| `IncomingSkipIgnored` | Archive path is inside incoming-skip/ |
| `ReleaseUnwanted` | All linked releases are unwanted |

### Diagnostics (AppendVolumePlan)

The plan exposes rich counters for the dialog:

| Field | Meaning |
|---|---|
| `TotalDerivedArtifactsForDatLine` | Total DAs for the DAT line (wanted + unwanted) |
| `ReleaseUnwantedSkipped` | DAs excluded because linked to unwanted releases |
| `TotalCandidates` | DAs that passed unwanted filter |
| `AlreadyAssignedSkipped` | Excluded: already on a volume |
| `ArchiveMissingSkipped` | Excluded: no physical file |
| `TargetCollisionSkipped` | Excluded: target path exists |
| `TooLargeSkipped` | Excluded: too large |
| `ExcludedIncomingSkipPath` | Excluded: path inside incoming-skip |
| `ExcludedZeroOrInvalidSize` | Excluded: zero/invalid size |
| `ActiveArchivePhysicalFileCount` | Physical files in archive directory |
| `ActiveArchiveKnownWantedFileCount` | Archive files matching a wanted DA |
| `ActiveArchiveUnassignedWantedFileCount` | Matching archive files not yet on any volume |
| `LargestCandidateBytes` / `SmallestCandidateBytes` | Size range of candidates |
| `DominantReasonHint` | Human-readable string for the most common skip reason |

---

## Fillback Volume

Fillback moves active wanted artifacts from one volume (source) to another (target). It reclaims space on the source by migrating content to the target. The source and target are selected explicitly in the UI.

### Candidate selection

Source candidates are active artifacts physically present on the source volume (flat path, non-managed subfolder). They must:
- Exist physically at `<source root>\<filename>`
- Not already exist on the target (`TargetCollision` skip)
- Fit within remaining target free space (`TooLargeForRemainingTargetSpace` skip; Fillback continues to smaller later files)

### Execution

For each planned entry:

**Same-disk move** (source and target on the same filesystem):
1. `File.Move(source, target)` — atomic rename
2. Verify hash of target against `expected_sha1`
3. Update DB: add VA row for target, remove VA row for source, update both sizes
4. Emit `fillback-moved`

**Cross-disk copy** (source and target on different filesystems):
1. `File.Copy(source, target)`
2. Verify hash of target against `expected_sha1`
3. Delete source file
4. Update DB: add VA row for target, remove VA row for source, update both sizes
5. Emit `fillback-copied-verified-deleted`

DB update occurs only after successful verification and (for cross-disk) deletion of source. No partial commit.

After execution completes, the volume UI is refreshed via `RefreshAfterVolumeStorageMutation`.

### SkipReasons

`VolumeFillbackPlanner.SkipReason`: `SourceFileMissing`, `AlreadyOnTarget`, `TargetCollision`, `TooLargeForRemainingTargetSpace`.

If many files show `SourceFileMissing`, run Verify Volume on the source first — the source volume may have a subfolder layout (legacy) that needs to be migrated to flat.

---

## Verify Volume

Verify Volume performs a full recursive scan of a volume root, classifies every physical file by SHA-1 hash, executes recovery moves, and updates the DB.

### Scan scope

`VolumeVerifyService.Verify(volumeId, volumeRoot, store, allDatLineDbPaths, ...)` scans the entire volume root recursively. Files inside managed subfolders are tagged but not classified as active content.

### Managed subfolders

`VolumeVerifyService.ManagedFolderNames` = `{ "unwanted", "known", "unknown" }`.

Files in these folders are reported as managed content, not as active artifacts, and are not counted in active artifact stats.

### Recovery actions

| Situation | Action |
|---|---|
| Wanted artifact found flat in root | `verify-ok` — no action |
| Wanted artifact found in wrong subfolder | Move to root (`misplaced-restored`) |
| Artifact linked to any unwanted release | Move to `<volume root>\unwanted\`, remove VA row, decrement `actual_size_bytes` |
| Known non-active file (in catalog but not wanted here) | Move to `<volume root>\known\` |
| Unknown file (no hash match in any DAT-line DB) | Move to `<volume root>\unknown\` |

### Flat layout enforcement

Any active artifact found inside a subdirectory (old nested layout: `<volume root>\Release Name\artifact`) is treated as misplaced and moved to the volume root. This is the migration path from the old layout to the flat layout.

### Progress events

Emitted via `IProgress<FoundFileProgress>` and `IProgress<VolumeVerifyProgress>`:

| Key | Meaning |
|---|---|
| `found-file` | File enumerated during scan |
| `hashing` | SHA-1 being computed |
| `classified` | Classification determined |
| `verify-ok` | Active artifact, hash verified |
| `missing` | Expected artifact not found on disk |
| `misplaced-found` | Active artifact in wrong subfolder |
| `misplaced-restored` | Moved to volume root |
| `unwanted-found` | Linked to unwanted release |
| `unwanted-moved` | Moved to `unwanted\` |
| `known-unexpected-found` | Known file that shouldn't be here |
| `known-unexpected-moved` | Moved to `known\` |
| `unknown-found` | No hash match |
| `unknown-moved` | Moved to `unknown\` |
| `collision` | Move blocked (name collision in target) |

Colors are in `Ingestion/VerifyResultColorConverter.cs`.

---

## Safety principles

1. **Archive is never deleted by volume workflows.** Append copies from archive; it does not remove the source. Fillback moves between volumes; it does not touch the archive.

2. **DB updates follow physical verification.** Append and Fillback both verify copy hash before committing DB changes.

3. **Verify Volume does not delete files.** Recovery moves files to managed subfolders within the volume. Nothing is discarded.

4. **UNWANTED WINS.** Volume verify uses `FindArtifactBySha1()` which returns unwanted classification when any linked release is unwanted. Unwanted content is moved out of the active area.
