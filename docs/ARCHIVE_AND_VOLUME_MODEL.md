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

```
archive\
  ps2\
    ps2-redump-dvd\
      Gran Turismo 4.chd
      God of War.chd
  snes\
    no-intro-snes-rom\
      Super Mario World (USA).sfc
```

**Properties:**
- Each file is matched against the `derived_artifacts` DB table by SHA-1.
- The archive is the source Append Volume copies from.
- The archive is the subject of Verify Archive scans.
- Ingestion (via the transform/compress pipeline) deposits completed artifacts here.

**What archive is NOT:**
- It is not a backup of volumes. Volumes are placement copies; the archive is the source.
- It does not contain release-name subfolders. Files are flat within each DAT-line directory.

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
| Ingestion | Incoming file matched only to unwanted releases |
| Verify Archive repair | UnwantedArchiveArtifact, UnknownArchiveFile, ArchiveHashMismatch |
| Verify Archive repair | RedundantArchiveCopy (after volume re-verification) |

**incoming-skip is never scanned by:**
- Append Volume (candidates come from `GetAllWantedArtifactInfos()` which uses DB paths)
- Build Volume
- Active archive scans

Files in incoming-skip are inert. They are not deleted automatically. To reintroduce a file, move it back to `incoming\<platform>\` manually and re-ingest.

---

## Archive vs volume — key differences

| Property | Archive | Volume |
|---|---|---|
| Path pattern | `archive\<platform>\<datLine>\<file>` | `<volumeRoot>\<file>` (flat) |
| Purpose | Canonical source, ingest target | Placement copy for use |
| DB tracking | `derived_artifacts` in per-DAT DB | `volume_artifacts` in catalog.db |
| SHA-1 authority | Yes (DA.expected_sha1) | Verified against DA.expected_sha1 |
| Append source | Yes (archive → volume) | No |
| Deletable by repair | Never | VA row removed on unwanted move |
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

---

## Cross-cutting code references

| Concern | File |
|---|---|
| Flat volume path | `Volumes/VolumeArtifactPathBuilder.cs` |
| Volume root resolution | `Data/VolumePathResolver.cs` |
| Volume assignment map (for redundancy) | `Data/CatalogService.GetAllAssignmentsForDatLine()` |
| Archive verify + repair | `LocalArchive/LocalArchiveVerifyService.cs` |
| Volume verify + repair | `Volumes/VolumeVerifyService.cs` |
| Append plan + execution | `Volumes/AppendVolumePlanner.cs`, `AppendVolumeService.cs` |
| Fillback plan + execution | `Volumes/VolumeFillbackPlanner.cs`, `VolumeFillbackService.cs` |
| incoming-skip path helper | `MainWindow.axaml.cs:IncomingSkipUniquePath()` |
| Disk discovery | `Data/DiskDiscoveryService.DiscoverAll()` |
