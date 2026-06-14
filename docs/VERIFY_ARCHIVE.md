# Arkadia — Verify Archive

Verify Archive scans the local archive directory for a single DAT line, classifies every physical file, and optionally repairs issues by moving files to `incoming-skip`.

---

## Core semantics

**Filesystem-first.** The scan enumerates physical files first. DB artifacts that have no physical file are counted in `AbsentFromArchiveCount` (a diagnostic) but are **not** emitted as main scan entries and do not affect `IsClean`.

**SHA-1-first matching.** Each physical file is hashed with SHA-1. The hash is looked up against the `derived_artifacts` DB index first. Filename lookup is used only as a fallback for hash-mismatch detection (when the filename matches a DB entry but the hash differs).

**No silent deletion.** All repair actions move files; nothing is deleted.

---

## Archive directory

```
archive\<platform>\<datLine>\
```

Example:
```
archive\ps2\ps2-redump-dvd\Gran Turismo 4.chd
```

Verify Archive scans this directory recursively (supports subdirectories, though the canonical layout is flat).

---

## Classifications

| Class | Meaning | IsRepairable |
|---|---|---|
| `WantedArchiveOk` | Hash matches a wanted DB artifact; no volume assignment (or no volume map provided) | false |
| `UnwantedArchiveArtifact` | Hash matches a DA linked to any unwanted release (UNWANTED WINS) | true |
| `UnknownArchiveFile` | Hash matches no DA in the DB | true |
| `ArchiveHashMismatch` | Filename matches a DA but hash differs | true |
| `ArchiveDuplicateCollision` | Multiple DB rows reference the same archive filename | false |
| `ArchiveMissingFile` | Not emitted in main scan — diagnostic only via `AbsentFromArchiveCount` | n/a |
| `RedundantArchiveCopy` | Hash matches a wanted DA that is already on a reachable assigned volume | true |
| `AssignedVolumeUnavailable` | Hash matches a wanted DA assigned to a volume that cannot be resolved | false |

---

## Redundancy detection

When an `assignedVolumes` map is passed to `Verify()`, wanted artifacts that are already assigned to a volume get classified as `RedundantArchiveCopy` (volume reachable) or `AssignedVolumeUnavailable` (volume not reachable).

The `assignedVolumes` map is built by the caller (`MainWindow.OnSysVerifyArchive`):

1. Call `CatalogService.GetAllAssignmentsForDatLine(datLineId)` — returns tuples ordered workspace-first.
2. Call `DiskDiscoveryService.DiscoverAll()` once to build a `diskId → mountpoint` map.
3. For each assignment, call `VolumePathResolver.Resolve(label, diskId, appRoot, mountedDisks)`.
4. First reachable assignment per artifact wins (workspace-first ordering).

`AssignedVolumeUnavailable` makes `IsClean` false — it requires manual attention (mount the volume, then re-verify).

---

## Plan properties

```csharp
public sealed class LocalArchiveVerifyPlan
{
    public int FilesScanned { get; }           // total physical files found
    public int WantedOk { get; }               // WantedArchiveOk count
    public int UnwantedArtifacts { get; }      // UnwantedArchiveArtifact count
    public int UnknownFiles { get; }           // UnknownArchiveFile count
    public int HashMismatches { get; }         // ArchiveHashMismatch count
    public int DuplicateCollisions { get; }    // ArchiveDuplicateCollision count
    public int RedundantCopies { get; }        // RedundantArchiveCopy count
    public int VolumeUnavailableWarnings { get; } // AssignedVolumeUnavailable count
    public int RepairableCount { get; }        // entries where IsRepairable == true
    public int AbsentFromArchiveCount { get; } // diagnostic only; not in IsClean
    public bool IsClean { get; }               // see below
}
```

`IsClean` is true when: `UnwantedArtifacts == 0 && UnknownFiles == 0 && HashMismatches == 0 && DuplicateCollisions == 0 && VolumeUnavailableWarnings == 0`.

Note: `RedundantCopies > 0` does not make `IsClean` false — redundant copies are clean from a data-integrity perspective. They are repairable (the archive copy can be safely moved) but do not represent corruption.

---

## Repair behaviour

`LocalArchiveVerifyService.Repair(plan, store, progress?)` processes all `IsRepairable` entries.

### RedundantArchiveCopy

1. **Re-verify volume copy BEFORE moving archive.** Computes SHA-1 of the file at `entry.VolumeFilePath`.
2. If the volume file is missing or its hash does not match `entry.ExpectedSha1`:
   - Emits `archive-volume-copy-missing`
   - Leaves archive file in place — no move
3. If volume copy is verified:
   - Moves archive file to `incoming-skip\<platform>\` (collision-safe name)
   - Emits `archive-redundant-moved`
   - **No DB changes** — DA rows, VA rows, and release status remain unchanged.

### UnwantedArchiveArtifact

1. Moves archive file to `incoming-skip\<platform>\` (collision-safe name).
2. Removes `derived_artifacts` row and content link via `store.DeleteDerivedArtifactAndLinks()`.
3. Release status is **not** changed — the release remains `unwanted`.

### UnknownArchiveFile / ArchiveHashMismatch

1. Moves archive file to `incoming-skip\<platform>\` (collision-safe name).
2. No DB changes.

---

## Progress event keys

Emitted by `LocalArchiveVerifyService` via `IProgress<LocalArchiveVerifyProgress>`:

| Key | Phase | Meaning |
|---|---|---|
| `archive-found-file` | Scan | File enumerated |
| `archive-hashing` | Scan | SHA-1 computation in progress |
| `archive-wanted-ok` | Scan | Classified WantedArchiveOk |
| `archive-unwanted-found` | Scan | Classified UnwantedArchiveArtifact |
| `archive-unknown-found` | Scan | Classified UnknownArchiveFile |
| `archive-hash-mismatch` | Scan | Classified ArchiveHashMismatch |
| `archive-collision` | Scan | Classified ArchiveDuplicateCollision |
| `archive-redundant-copy` | Scan | Classified RedundantArchiveCopy |
| `archive-volume-unavailable` | Scan | Classified AssignedVolumeUnavailable |
| `archive-repair-moving` | Repair | File being moved to incoming-skip |
| `archive-repair-moved` | Repair | File moved (unwanted/unknown/mismatch) |
| `archive-repair-skipped` | Repair | Already absent, skipped |
| `archive-error` | Repair | Move or DB operation failed |
| `archive-redundant-moved` | Repair | Redundant copy moved after volume re-verification |
| `archive-volume-copy-missing` | Repair | Volume copy gone or corrupt — archive kept in place |

Colors for these keys are in `Ingestion/VerifyResultColorConverter.cs`.

---

## Dialog (LocalArchiveVerifyDialog)

- 9-column live stats bar: SCANNED / WANTED OK / UNWANTED / UNKNOWN / MISMATCH / REDUNDANT / UNAVAILABLE / REPAIRABLE / REPAIRED
- Filter checkboxes: Wanted OK / Unwanted / Unknown / Mismatch / Redundant / Unavailable / Repair / Scan detail
- Repair All button (enabled when `RepairableCount > 0`)
- `IsClean` false → status label shows repairable count

---

## Test coverage

`Arkadia.Tests/LocalArchive/LocalArchiveVerifyServiceTests.cs` — 25 tests covering:
- Filesystem-first semantics (tests 1–2)
- WantedOk, UnwantedArtifact, UnknownFile, HashMismatch classifications (3–6)
- Repair: move to incoming-skip, collision-safe naming, progress callbacks (7–12)
- Repair: DA row removal for unwanted, release status preservation (13–14)
- IsClean conditions (15–17)
- RedundantArchiveCopy detection and repair (18–22)
- AssignedVolumeUnavailable, DoesNotModifyDbRows, IsClean for unavailable (23–25)
