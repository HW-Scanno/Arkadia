# Arkadia — Archive and Volume Model

This document describes the relationship between the local archive, volumes, and incoming-skip. Read this before touching any code that reads or writes physical files.

---

## Guiding principle

> **Filesystem reality always takes precedence over database assumptions.**

Every verify/repair workflow starts by enumerating physical files. DB state is reconciled against what is on disk, never the other way around.

---

## The three storage tiers

### 1. Local archive (`archive\<platform>\<datLine>\`)

The local archive is the **active source of truth** for derived artifacts. It contains the canonical copies of all physical artifacts (ROM files, CHD files, etc.) organised by platform and DAT line.

The archive **layout is uniform per DAT line** — one archive output *form* for the whole line (see [Archive output policy](#archive-output-policy)):

```
# SingleFileFlat form (CHD, ZIP, single-output file_extension):
archive\
  dc\
    dc-redump-gd\
      Sonic Adventure (USA).chd     ← <SafeReleaseName>.<ext>
      Crazy Taxi (USA).chd

# MultiFileReleaseFolder form (No Compression Folder, multi-output file_extension):
archive\
  psx\
    psx-redump\
      Some Game (USA)\              ← <SafeReleaseName>\
        Some Game (USA).cue          ← original inner filenames
        Some Game (USA) (Track 1).bin
        Some Game (USA) (Track 2).bin
```

**Properties:**
- Each file is matched against the `derived_artifacts` DB table by SHA-1.
- The archive is the source Append Volume copies from.
- The archive is the subject of Verify Archive scans (recursive — classifies both forms and legacy layouts).
- Ingestion (via the transform/compress pipeline) deposits completed artifacts here.
- **`derived_artifacts.relative_path` is authoritative for readers.** Append/Build/Fillback/Repair resolve archive sources via `relative_path` — they must never reconstruct an archive path from platform/DAT-line/release name. Verify Archive scans recursively. This is why both current and legacy layouts read correctly.

**What archive is NOT:**
- It is not a backup of volumes. Volumes are placement copies; the archive is the source.
- **Volumes are always flat** (`<volume root>\<artifact>`); the per-DAT-line archive *form* is independent of volume layout and does not change it.

### 2. Volumes (`volumes\<volume label>\` or disk mount)

Volumes are **placement copies** of archive artifacts. They exist to distribute content across physical disks for consumers (emulators, game consoles, etc.).

```
volumes\
  GamingDrive-A\
    Gran Turismo 4.chd
    God of War.chd
  GamingDrive-B\
    Super Mario World (USA).sfc
```

**Volume layout is always flat:**

```
<volume root>\<artifact filename>    ← CORRECT
<volume root>\Release Name\artifact  ← WRONG (legacy; Verify Volume will detect and relocate)
```

`VolumeArtifactPathBuilder.GetFlatFullPath(volumeRoot, fileName)` is the single authority for volume artifact paths. Use it everywhere — in write, verify, repair, and reabsorb code paths.

**Volume state is tracked in `catalog.db`:**
- `volumes` table: volume ID, label, planned/actual size bytes
- `volume_artifacts` table: which derived artifacts are on which volume
- `volume_locations` table: disk_id, location_type (workspace/disk), is_current

**Volumes can reside on:**
- Workspace: `<appRoot>\volumes\<SafeLabel(label)>\`
- Mounted disk: `<diskMountpoint>\<SafeLabel(label)>\` (identified by `ARKADIA.DISK.json` marker)

`VolumePathResolver.Resolve(label, diskId, appRoot, mountedDisks?)` centralizes resolution, workspace-first.

### 3. incoming-skip (`incoming-skip\<platform>\`)

incoming-skip is the **centralized suspension and quarantine area**. Files land here when they cannot be (or should not be) processed normally.

```
incoming-skip\
  ps2\
    Skipped-Unwanted.chd
    Unknown-File-From-Archive.chd
    Redundant-Already-On-Volume.chd
```

**Sources that write to incoming-skip:**
| Source | Condition |
|---|---|
| Ingestion | Incoming file matched only to unwanted releases (`unwanted-moved`) |
| Ingestion stale cleanup | Leftover `staging`/`source` files for a now-unwanted release, when the folder maps exclusively to unwanted releases (`stale-staging-unwanted-moved` / `stale-source-unwanted-moved`) |
| Verify Archive repair | UnwantedArchiveArtifact, UnknownArchiveFile, ArchiveHashMismatch |
| Verify Archive repair | RedundantArchiveCopy (after volume re-verification) |

**incoming-skip is never scanned by:**
- Append Volume (candidates come from `GetAllWantedArtifactInfos()` which uses DB paths)
- Build Volume
- Active archive scans

Files in incoming-skip are inert. They are not deleted automatically. To reintroduce a file, move it back to `incoming\<platform>\` manually and re-ingest.

---

## Archive output policy

A DAT line has **one uniform archive output form**. There is **no per-release fallback** from flat to foldered.

| Form | Path | When |
|---|---|---|
| **SingleFileFlat** | `archive/<platform>/<datLine>/<SafeReleaseName>.<ext>` | release_shape (CHD), release_folder single-file (ZIP), `file_extension` where every wanted release yields exactly one output |
| **MultiFileReleaseFolder** | `archive/<platform>/<datLine>/<SafeReleaseName>/<original files>` | No Compression Folder, `file_extension` where any wanted release yields ≥2 outputs |

**Naming rules:**
- SingleFileFlat artifact names are **release-name-based** (`SafeReleaseName + ext`), never source/main-input-based. CHD is `Sonic Adventure (USA).chd`, not `disc.chd`.
- MultiFileReleaseFolder preserves **original inner filenames** inside the release folder; the folder name isolates common names like `track01.bin`.
- `Archive/ArchiveArtifactPathBuilder` (`Arkadia.Archive`) is the **single write-path authority** — writers never hand-roll archive paths (use `GetRelativePath(...)` for files, `GetReleaseFolderRoot(...)` for the MultiFileReleaseFolder release root). Reads always go through `derived_artifacts.relative_path`.

**The DAT is authoritative.** Arkadia validates *ambiguity* (two releases that would produce the same archive artifact name); it does **not** reinterpret multi-disc structure or override the DAT's release modelling.

**Collisions are DAT-line ambiguities, resolved curatorially — not by per-release fallback.** If a SingleFileFlat line has two wanted releases that normalize to the same artifact name, that is a collision requiring curator action (Exclude one / Abort); Arkadia never silently switches those releases to a folder. See [INGESTION_PIPELINE.md → Archive output validation](INGESTION_PIPELINE.md#archive-output-validation-form-collision-review-and-gate).

**Idempotency for existing artifacts:** the new naming applies to **new** writes only. If a derived artifact already exists for a release (any stored `relative_path`, including a legacy `disc.chd`), the writer keeps writing to that stored path — existing artifacts are never orphaned or re-transformed under a different name.

**Defense-in-depth:** the runtime `ArchiveWriteCollisionGuard` (keyed on `content_identity_key`) refuses to overwrite/reuse a target that belongs to a different content identity, emitting `archive-collision` — independent of, and in addition to, the config-time validation.

**Verify DAT ≠ Verify Archive/Volume.** **Verify DAT** validates this **policy metadata** across all DAT lines (form + collision state + fingerprints persisted on `dat_lines`); it is read-only over releases and touches no files. **Verify Archive** and **Verify Volume** verify the physical filesystem (`archive\…` and volume roots respectively). See [INGESTION_PIPELINE.md → Verify DAT](INGESTION_PIPELINE.md#verify-dat-batch-policy-validation).

---

## Archive vs volume — key differences

| Property | Archive | Volume |
|---|---|---|
| Path pattern | `archive\<platform>\<datLine>\<file>` | `<volumeRoot>\<file>` (flat) |
| Purpose | Canonical source, ingest target | Placement copy for use |
| DB tracking | `derived_artifacts` in per-DAT DB | `volume_artifacts` in catalog.db |
| SHA-1 authority | Yes (DA.expected_sha1) | Verified against DA.expected_sha1 |
| Append source | Yes (archive → volume) | No |
| Deletable by repair | Never | VA row removed on unwanted move, or on confirmed-missing reconciliation |
| Redundant copy | Archive copy redundant if on volume | Volume copy is primary |

---

## Managed volume subfolders

Verify Volume uses managed subfolders within the volume root to hold non-active files:

```
<volume root>\
  Game.chd               ← active artifact (flat)
  ARKADIA.DISK.json      ← Arkadia system file (never moved)
  unwanted\
    Removed-Game.chd     ← moved here by Verify Volume when linked to unwanted release
  known\
    Known-Extra.chd      ← moved here by Verify Volume for known non-active files
  unknown\
    Mystery-File.bin     ← moved here by Verify Volume for unrecognised files
```

`VolumeVerifyService.ManagedFolderNames` = `{ "unwanted", "known", "unknown" }` (case-insensitive).

Files inside managed folders are not classified as active volume content during a scan.

---

## Data integrity rules

1. **No file is ever considered present without independent verification.** Append copies a file then verifies its SHA-1 hash before creating the `volume_artifacts` DB row and incrementing `actual_size_bytes`.

2. **DB writes follow physical verification.** Fillback verifies the copy before deleting the source and updating DB. Verify Archive redundant-copy repair re-verifies the volume copy SHA-1 before moving the archive file.

3. **No silent deletion.** Every repair path moves files to a managed location. Archive repair moves to `incoming-skip`. Volume repair moves to `unwanted\`, `known\`, or `unknown\` within the volume. Nothing is silently discarded.

4. **Repair does not modify DA or release DB rows for redundant/unknown moves.** Only unwanted artifact repair removes DA rows (from the per-DAT DB). Redundant archive copy repair leaves all DB rows intact.

5. **`volume_artifacts` is physical-placement truth, and Verify Volume corrects it.** When a **reachable** volume is scanned and an assigned artifact is confirmed missing (absent from the flat path and not found as a valid misplaced artifact), Verify Volume removes the stale `volume_artifacts` row and decrements `actual_size_bytes` (the DA is marked `missing`; no physical file, `derived_artifacts`, or `release_content_links` row is deleted). Unreachable volumes are never reconciled. This is a per-artifact decrement, **not** a full `actual_size_bytes` recompute — see [VOLUME_WORKFLOWS.md](VOLUME_WORKFLOWS.md#missing-assignment-reconciliation) for scope and the collision edge.

---

## Cross-cutting code references

| Concern | File |
|---|---|
| Flat volume path | `Volumes/VolumeArtifactPathBuilder.cs` |
| Volume root resolution | `DataLayer/VolumePathResolver.cs` |
| Volume assignment map (for redundancy) | `DataLayer/CatalogService.GetAllAssignmentsForDatLine()` |
| Archive verify + repair | `LocalArchive/LocalArchiveVerifyService.cs` |
| Volume verify + repair | `Volumes/VolumeVerifyService.cs` |
| Append plan + execution | `Volumes/AppendVolumePlanner.cs`, `AppendVolumeService.cs` |
| Fillback plan + execution | `Volumes/VolumeFillbackPlanner.cs`, `VolumeFillbackService.cs` |
| incoming-skip path helper | `MainWindow.axaml.cs:IncomingSkipUniquePath()` |
| Disk discovery | `DataLayer/DiskDiscoveryService.DiscoverAll()` |
