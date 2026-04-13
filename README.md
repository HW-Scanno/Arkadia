# Arkadia

![Build](https://img.shields.io/badge/build-passing-brightgreen)
![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)
![Platform](https://img.shields.io/badge/platform-Windows-0078D4)
![Status](https://img.shields.io/badge/status-active%20development-orange)
![License](https://img.shields.io/badge/license-MIT-blue)

Arkadia is a preservation-grade desktop application for managing large offline archives across multiple physical disks. It provides a structured, integrity-first approach to organizing, verifying, and distributing artifact collections derived from DAT files — ensuring that filesystem state and catalog state are always aligned and that no artifact is ever considered present without independent verification.

---

## Key Features

- Multi-volume archive management with support for offline and removable disks
- DAT-line-based catalog structure with per-artifact derived content tracking
- Deterministic volume planning against declared disk capacity
- Safe materialization pipeline: plan, build, append, and reabsorb
- SHA1-based integrity verification at the artifact level
- Verify Volume and Verify ALL operations for full-collection consistency checks
- Repair workflow for reintegrating missing artifacts from external sources
- Capacity-aware volume resizing with occupied-size and disk-overcommit guards
- Formal volume lifecycle with explicit INIT, PRESENT, and LOST states
- Full operation audit logs written per-session

---

## Why Arkadia?

Most ROM management tools focus on scanning, matching, and building sets from a single working directory. Arkadia is designed for a different problem: managing a collection that has already been built, across multiple physical disks, over time, where individual disks may be offline or unavailable at any given moment.

The core challenge is coherence. When content is spread across several offline disks and a local archive, it becomes easy for the catalog to drift out of sync with physical reality — files get moved, disks fail, sets are extended, and space is reclaimed. Arkadia addresses this by treating every operation as an explicit, logged, and verifiable state transition, and by refusing to mark artifacts as present without confirmation.

The result is a system where the answer to "where is this artifact?" is always authoritative, and the answer to "is this artifact intact?" can always be verified against a known hash.

---

## Core Concepts

### DAT Line

A DAT line is the foundational unit of organization. It corresponds to a single DAT file — a catalog of known releases and their expected file content — imported from a preservation authority. Each DAT line has its own isolated SQLite database tracking the status of every release and derived artifact within it.

### Derived Artifacts

When a release is ingested, its files are processed through a transform pipeline to produce derived artifacts — the actual stored files. Each derived artifact has a known SHA1 hash, a recorded size, and a status reflecting whether it is present in the local archive, present on a volume, missing, or lost. Derived artifacts are the atomic unit of integrity tracking.

### Volumes

A volume is a named, bounded storage container for derived artifacts. It has a declared planned capacity and an actual occupied size. Volumes are assigned artifacts from a single DAT line and can be physically located in the local archive workspace or on an external disk. The volume lifecycle is described in detail below.

### Disks

A disk is a registered external storage device with a declared total capacity. Multiple volumes can be assigned to a single disk, subject to capacity constraints. Arkadia tracks disks by an internal identifier written to the disk at initialization time, making mount-point-independent discovery possible.

### Local Archive

The local archive is the workspace within the application's data directory where derived artifacts reside after ingestion and before being materialized into a volume. It serves as the source for all plan and build operations.

---

## Volume Lifecycle

Volumes follow a formal lifecycle with explicit state transitions. Each state has defined semantics:

```
[INIT]
  Volume created. No artifacts assigned. No files materialized.
  Plan is available.

    |
    | Plan
    v

[INIT, artifacts assigned]
  Volume has a content plan. Artifacts linked in catalog.
  Files remain in local archive.

    |
    | Build
    v

[PRESENT]
  Files moved from local archive into volume workspace.
  Volume is physically materialized. Catalog aligned.
  Location: LOCAL (workspace) or ON DISK (external disk).

    |
    | Append (optional, incremental)
    v

[PRESENT, extended]
  Additional artifacts from local archive copied into the volume.
  Each artifact is copy-verified before the archive copy is removed.
  Catalog updated immediately after each successful append.

    |
    | Mark Lost
    v

[LOST]
  Volume content is no longer accessible.
  Affected artifacts propagated to lost state in the DAT-line database.
  Releases recalculated. Volume retained in catalog for auditing.

    |
    | Verify ALL / Verify Volume (if volume is found)
    | -> if fully verified clean: restored to PRESENT
    |
    | Delete Volume (irreversible)
    v

[Deleted]
  Volume record, artifact mappings, and location records permanently removed
  from the catalog. Artifact and release state is not modified here — that
  was handled at Mark Lost time.
```

**Reabsorb** is an alternative path from PRESENT:

```
[PRESENT]
    |
    | Reabsorb
    v

Files copied back into local archive, verified, then deleted from volume.
Artifact statuses restored to present-in-archive. Volume deleted on full success.
```

---

## Operations Overview

### Plan

Runs the volume content planner against the selected DAT line. Produces a preview showing which releases will be included, skipped, or deferred based on available archive content and remaining volume capacity. No files are moved. On confirmation, artifact assignments are written to the catalog and actual size is calculated.

Available for all volumes in INIT or PRESENT state. Disabled for LOST volumes.

### Build

Moves planned artifacts from the local archive into the volume's workspace directory. Sets the volume location to `workspace` (LOCAL). Promotes the volume from INIT to PRESENT. Updates derived artifact statuses and recalculates release statuses immediately — no subsequent verify pass is needed to align state.

### Append

Extends a PRESENT volume with new artifacts that have appeared in the local archive since the volume was last built. Each artifact is copied to the volume, SHA1-verified against the source, and only then removed from the archive. The operation aborts on the first verification failure, leaving any successfully transferred artifacts coherent. Catalog state is updated immediately on full success.

### Verify Volume

Performs a full SHA1 scan of every artifact in a selected volume. Compares each file against its recorded hash. Marks artifacts as present or missing in the DAT-line database. Recalculates release statuses. Updates volume health to `ok` or `crit`. If the volume was LOST and all artifacts are verified intact, the volume is restored to PRESENT.

### Verify ALL

Runs a combined integrity check across the local archive and all accessible volumes for the selected DAT line. Checks both archive-resident and volume-resident artifacts. Handles per-volume health updates, LOST restore logic, mismatch quarantine, and unexpected file detection in a single pass. Results are written to an audit log.

### Repair

Reintegrates missing artifacts into a volume from an external incoming-repair directory. Runs ingest, verifies recovered content, and copies verified files into the volume. Designed for recovery scenarios where a volume has degraded health due to missing artifacts.

### Reabsorb

Safely reverses the Build operation. Copies volume artifacts back into the local archive, verifies each transfer, and deletes the volume-side copies on success. On full success, the volume record is deleted. On partial success, transferred artifacts are removed from the volume's mapping and the volume is retained with reduced content.

### Resize

Updates the planned capacity of a volume. Subject to two hard validation rules:

1. The new size cannot be smaller than the current occupied size (sum of present artifact sizes).
2. For disk-backed volumes, the new size cannot exceed the allocatable capacity remaining on the target disk, accounting for all other volumes already planned on that disk.

Both checks are enforced before any catalog write occurs.

### Mark Lost

Records that a volume's content is no longer accessible. Propagates the loss to derived artifacts that are exclusively on this volume, marking them lost in the DAT-line database. Recalculates affected release statuses. Volume metadata is preserved in the catalog. The operation is reversible via Verify Volume or Verify ALL if the physical content is later found.

### Delete Volume

Permanently removes a LOST volume from the catalog. Deletes the volume record, all artifact mappings, and all location records in a single atomic transaction. Does not modify artifact or release state — that was already handled at Mark Lost time. Irreversible.

---

## Installation

Arkadia is a .NET 8 desktop application built with Avalonia UI and targets Windows.

**Prerequisites:**

- Windows 10 or later
- [.NET 8 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)

**Build from source:**

```bash
git clone https://github.com/your-org/arkadia.git
cd arkadia
dotnet build -c Release
dotnet run --project Arkadia.csproj
```

A catalog database is created automatically on first launch in the application data directory.

---

## Current Status

Arkadia is under active development. Core volume management, integrity verification, and DAT-line catalog operations are functional. The system is used in production for personal preservation workflows but should be considered pre-release software. Breaking schema changes may occur between versions.

---

## Roadmap

- Catalog view: browse all releases and artifacts across DAT lines with filtering and status summaries
- Export / Build Set: generate a distributable set from verified archive content

---

## License

MIT License. See [LICENSE](LICENSE) for details.
