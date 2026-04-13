# Arkadia — User Manual

---

## Table of Contents

1. [Introduction](#1-introduction)
2. [Core Concepts](#2-core-concepts)
3. [First-Time Setup](#3-first-time-setup)
4. [Basic Workflow](#4-basic-workflow)
5. [Verification](#5-verification)
6. [Repair](#6-repair)
7. [Reabsorb](#7-reabsorb)
8. [Resize](#8-resize)
9. [Mark Lost and Delete Volume](#9-mark-lost-and-delete-volume)
10. [Best Practices](#10-best-practices)
11. [Troubleshooting](#11-troubleshooting)

---

## 1. Introduction

Arkadia is a desktop application for managing preservation-grade offline archives. Its primary purpose is to help you organize large collections of files — organized according to DAT files from preservation authorities — across multiple physical disks, while ensuring that every file is where the catalog says it is and that no corruption goes undetected.

The core problem Arkadia solves is coherence at scale. When a collection grows large enough to span several external disks, and those disks are not always connected, it becomes easy for the actual state of your archive to diverge from what you believe it to contain. Files get moved manually, disks are lost or damaged, new content is added incrementally over time. Without a system that enforces consistency, the result is a catalog you cannot trust.

Arkadia addresses this by:

- Tracking every file at the artifact level, with a known SHA1 hash and recorded size
- Recording which files are in the local archive, which are on a volume, and which are missing or lost
- Requiring verification before marking anything as present
- Enforcing a formal volume lifecycle so that files are never silently moved or changed
- Writing an audit log for every significant operation

Arkadia does not replace a general-purpose file manager. It is a management layer on top of your storage — it knows where everything is, validates that what is there is correct, and gives you the tools to act when it is not.

---

## 2. Core Concepts

### DAT Line

A DAT line is Arkadia's unit of organization for a collection. It corresponds to a single DAT file — an XML catalog produced by a preservation authority that describes a set of known releases and their expected file contents (names, sizes, and hashes).

When you import a DAT file into Arkadia, it creates a DAT line. The DAT line gets its own isolated database that tracks every release and every derived artifact within it. All subsequent operations — ingestion, planning, verification — operate within the context of a DAT line.

A single platform (e.g. a console or computer system) can have multiple DAT lines if you maintain sets from more than one source.

### Artifacts

When you ingest files into Arkadia, those files are processed through a transform pipeline configured for the DAT line. The output of this pipeline is a set of derived artifacts — the actual files that Arkadia stores and tracks.

Each artifact has:

- A unique identifier
- A file name and relative path within the archive
- A recorded size in bytes
- A SHA1 hash computed at ingest time
- A status: `present` (in local archive), `present` (on a volume), `missing`, or `lost`

Artifacts are the atomic unit of integrity tracking. Every verify operation works at the artifact level.

### Volumes

A volume is a named, bounded container for artifacts. It has a planned capacity (how much space it is allowed to use), an actual occupied size (the sum of the artifacts currently stored in it), and a location.

Volumes hold artifacts from a single DAT line. A volume goes through a formal lifecycle: it starts as INIT (empty, unbuilt), becomes PRESENT after its content is physically materialized, and can be moved to LOST if its content becomes inaccessible. Volume state is always explicit — there is no ambiguous intermediate state.

The volume lifecycle is covered in detail in Section 4.

### Disks

A disk is a registered external storage device. When you initialize a disk in Arkadia, it writes a small marker file to the disk containing a unique identifier. Arkadia uses this identifier to recognize the disk regardless of which drive letter or mount point it appears on.

Each disk has a declared total capacity. When you assign volumes to a disk, Arkadia tracks how much of that declared capacity is committed to planned volumes — this is the planning capacity, not transient free space.

A disk can hold multiple volumes.

### Local Archive

The local archive is the directory within Arkadia's data folder where derived artifacts live after ingestion and before they are built into a volume. Think of it as a staging area.

When you ingest new content, artifacts land in the local archive. When you build a volume, files are moved from the local archive into the volume's workspace. The local archive is also where artifacts return when you reabsorb a volume.

---

## 3. First-Time Setup

### Step 1 — Launch Arkadia

On first launch, Arkadia creates its catalog database in the application data directory. No manual setup is needed.

### Step 2 — Create a Platform

Navigate to the **Systems** view. Click **Create Platform**.

Fill in the platform name and any relevant hardware metadata (manufacturer, CPU, etc.). This is primarily for organization — the platform groups your DAT lines.

### Step 3 — Import a DAT File

With the platform selected, click **Import DAT**.

Select the DAT file from your filesystem. Arkadia will parse it and create a DAT line record. Once imported, the DAT line appears in the Systems tree under its platform.

The DAT line now has a list of expected releases and their file definitions, but no artifacts yet.

### Step 4 — Configure a Transform Strategy

Before ingesting, the DAT line needs to know how to transform incoming files into derived artifacts. Select the DAT line and click **Configure DAT**.

Choose a transform strategy:

- **Per file extension** — different transforms are applied based on file extension. Common for multi-format sets.
- **Per release folder** — a single transform is applied to all files in a release.

Assign transform rules for each extension or folder as appropriate. If you want a file type to be excluded from storage, mark it as Discard.

### Step 5 — Ingest Initial Artifacts

Place your source files in the incoming directory that Arkadia is configured to monitor, or use the **Ingest Files** action from the Systems toolbar with the DAT line selected.

Arkadia will scan the incoming content, match files to releases in the DAT, apply the configured transforms, and store the resulting derived artifacts in the local archive.

After ingest, the DAT line's status counts will update to show how many releases are now present.

### Step 6 — Register a Disk (optional, for multi-disk setup)

If you plan to store volumes on external disks, navigate to the **Disks** view and click **Initialize Disk**. Select the target drive from the list.

Arkadia writes its marker to the disk and registers it in the catalog with the disk's declared capacity. You can set a label to identify it.

---

## 4. Basic Workflow

Once content is ingested into the local archive, the typical workflow is to organize it into volumes and move those volumes to disk for long-term storage.

### A. Check Available Unassigned Artifacts

Before creating a volume, it is useful to know how much material is available.

Select a DAT line in the **Systems** view. The detail pane on the right shows:

- **Present** — releases with all artifacts verified present
- **Unassigned** — number of present derived artifacts not yet assigned to any volume
- **Unassigned Size** — total size of those unassigned artifacts

These two values tell you how much unallocated material is available for a new volume or an Append operation.

### B. Create a New Volume

Navigate to the **Volumes** view. Click **Create Volume**.

Fill in:

- **Label** — a human-readable name for the volume (used for folder naming)
- **Platform** — the platform this volume belongs to
- **DAT Line** — the specific DAT line whose artifacts this volume will hold
- **Planned Capacity** — the maximum size this volume is allowed to hold, in GB

After confirming, the volume appears in the list with status **INIT**. No files have been moved yet.

### C. Plan the Volume

Select the new INIT volume and click **Plan**.

Arkadia runs its planner against the DAT line's local archive content. It shows a preview listing every release along with one of three decisions:

- **include** — the release fits in the remaining capacity and all its artifacts are available in the archive
- **skip** — the release is already assigned to another volume
- **defer** — the release would not fit in the remaining capacity

Review the plan. If you are satisfied, click **Apply and Build** to proceed.

> Note: Plan is also available for PRESENT volumes if you want to add more releases to an existing volume. In that case, only unassigned releases that fit in the remaining capacity will be included.

### D. Build the Volume

The Build step happens automatically when you confirm the plan preview. Arkadia:

1. Moves each planned artifact from the local archive into the volume's workspace directory
2. Sets the volume's location to **LOCAL** (workspace)
3. Promotes the volume from INIT to **PRESENT**
4. Updates artifact statuses and recalculates release statuses immediately

After Build, the volume shows status **LOCAL** in the list. The files are physically present in `volumes/<label>/` within Arkadia's data directory.

### E. Move the Volume to Disk (optional)

If the volume should live on an external disk, connect the target disk and select the volume. Click **Move**.

Arkadia will:

1. Copy files from the workspace to the disk (into a folder named after the volume label)
2. Verify each file SHA1 after copy
3. Delete the workspace copy only after verification passes
4. Update the catalog location to point to the disk

If verification fails on any file, the copy is aborted. The source remains intact.

After Move, the volume shows status **ON DISK**. Disconnect the disk — Arkadia retains the catalog location and will show ON DISK even when the disk is not connected. This is a catalog claim, not a live availability check.

### F. Append More Data (optional)

If new artifacts for this DAT line are ingested into the local archive after the volume was built, you can add them to an existing PRESENT volume without rebuilding.

Select the volume and click **Append**.

Arkadia finds artifacts assigned to this volume that are present in the local archive but not yet in the volume. It shows a confirmation listing the files and total size.

On confirmation, for each artifact:

1. The file is copied from the archive to the volume
2. The copy is SHA1-verified against the source
3. If verification passes, the archive copy is deleted
4. If verification fails, the destination copy is removed and the operation aborts

Catalog state is updated after successful completion. Each artifact that is successfully transferred and verified is immediately coherent — you do not need to run Verify after a successful Append.

---

## 5. Verification

Verification is Arkadia's mechanism for confirming that what the catalog claims to be present actually is present, and that the files are intact.

### Verify Volume

Use **Verify Volume** to check the contents of a single volume.

**When to use:**
- After receiving a disk back from long-term storage
- After any manual intervention on the volume folder
- As a routine integrity check
- After a disk reports errors

**What it does:**

1. Opens the volume's verify dialog showing expected artifact count
2. For each artifact assigned to the volume, checks whether the file exists at its expected path
3. If the file exists and has a recorded SHA1, computes the SHA1 and compares it
4. Each artifact is marked OK, MISSING, or MISMATCH
5. After scanning, updates the DAT-line database: present artifacts set to `present`, bad artifacts set to `missing`
6. Recalculates release statuses
7. Sets the volume health to `ok` (all clean) or `crit` (any failures)

**LOST restore:** If the volume was marked LOST and all artifacts verify clean, the volume is automatically restored to PRESENT with its location recorded.

**To run:** Select the volume and click **Verify Volume**. The disk must be connected if the volume is ON DISK.

### Verify ALL

Use **Verify ALL** to run a comprehensive check across the entire DAT line — both the local archive and all accessible volumes.

**When to use:**
- Periodically as a full-collection integrity check
- Before major operations (building new volumes, reabsorbing)
- After a suspected storage incident

**What it does:**

1. Checks all artifacts in the local archive for the selected DAT line
2. For each connected volume in the DAT line, runs the same per-artifact scan as Verify Volume
3. Identifies missing files, hash mismatches, and unexpected files
4. Quarantines unexpected files (if configured) by moving them to an incoming-skip directory
5. Updates artifact statuses and volume health for every volume scanned
6. Writes a full audit log

**Cancellation:** Verify ALL can be cancelled mid-run. Volumes that have already been scanned will have had their state updated. Volumes not yet reached are left unchanged.

**To run:** Select the DAT line in the Systems view and click **Verify ALL**. Only connected disks will be scanned. Disconnected volumes are skipped (noted in the results).

### Reading Verification Results

The verify dialog shows live counters during the scan:

| Counter  | Meaning                                          |
|----------|--------------------------------------------------|
| Expected | Total artifacts to be checked                    |
| Verified | Artifacts confirmed intact (file found, hash OK) |
| Missing  | Artifacts whose file was not found               |
| Mismatch | Artifacts found but with incorrect hash or size  |

At completion, the phase line summarizes the outcome and whether health was updated.

---

## 6. Repair

Use Repair when a volume has degraded health (status WARNING) due to missing artifacts, and you have replacement copies of those files available.

### When to Use Repair

- After Verify Volume or Verify ALL identifies missing artifacts
- When you have a known-good external copy of the missing content
- To bring a volume back to full health without rebuilding it from scratch

### How Repair Works

Repair is a guided reintegration workflow:

1. Select the damaged volume and click **Repair Volume**
2. Arkadia identifies which artifacts are missing from the volume
3. It searches the `incoming-repair/<platform>` directory for files matching the expected content (by SHA1/hash)
4. Any matched files are ingested and verified
5. Verified content is copied into the volume at the correct paths
6. The DAT-line database is updated and release statuses are recalculated
7. Volume health is recalculated

Repair does not overwrite existing files — it only fills in gaps.

### Preparing the Incoming Repair Directory

Place replacement files in:

```
<arkadia-data>/incoming-repair/<platform-id>/
```

Arkadia will scan this directory during the repair operation. Files are matched by content identity (hash), not by filename, so the folder structure within `incoming-repair` does not need to match the volume structure.

---

## 7. Reabsorb

Reabsorb reverses the Build operation. It moves content from a volume back into the local archive, verifying each transfer, then removes the volume.

### When to Use Reabsorb

- You want to consolidate content from multiple small volumes into a larger one
- A volume needs to be replanned with different content
- You are decommissioning a disk and want to recover the content

### What Reabsorb Does

1. Copies each artifact from the volume back to its expected path in the local archive
2. Verifies the copy by SHA1
3. Deletes the volume-side copy only after verification passes
4. On full success: updates artifact statuses, recalculates releases, deletes the volume record
5. On partial success: keeps the volume with only the non-transferred artifacts remaining; the transferred artifacts are recovered to the archive

### Important Notes

- Reabsorb requires the volume to be physically accessible (LOCAL or ON DISK with the disk connected)
- Reabsorb does not apply to LOST volumes
- After a successful full reabsorb, the volume is deleted from the catalog — it no longer exists
- After a partial reabsorb, the volume is retained with reduced content and you can attempt the remaining artifacts later

---

## 8. Resize

Resize updates a volume's planned capacity. This is a metadata-only operation — no files are moved.

### When to Use Resize

- The original planned size was too small and you want to Append more content
- You are reorganizing volume assignments and need to adjust capacity allocations

### Constraints

Two hard rules are enforced before any resize is committed:

**Rule 1 — Cannot shrink below occupied size**

The new planned size must be greater than or equal to the current occupied size (the actual sum of artifact sizes stored in the volume). You cannot set a planned capacity smaller than what is already there.

If this check fails, Arkadia shows the current occupied size and the shortfall, and the resize is blocked.

**Rule 2 — Cannot exceed disk allocatable capacity (disk-backed volumes only)**

If the volume is assigned to a disk, the new planned size must fit within the disk's allocatable capacity: the disk's declared total capacity minus the planned sizes of all other volumes already assigned to that disk.

If this check fails, Arkadia shows the available capacity and the overcommit amount, and the resize is blocked.

Both checks happen before any database write. A blocked resize has no effect on the catalog.

---

## 9. Mark Lost and Delete Volume

### Mark Lost

Use **Mark Lost** when a volume's content is no longer accessible and you do not expect to recover it.

**What it does:**

1. Sets the volume status to LOST
2. Identifies all derived artifacts that are exclusively on this volume (not present on any other volume)
3. Sets those artifacts to `lost` status in the DAT-line database
4. Recalculates release statuses — releases whose artifacts are now lost will reflect that
5. Writes an audit log

The volume remains in the catalog after Mark Lost. Its metadata, artifact mappings, and location records are preserved. This allows the volume to be restored if the content is found later.

**Mark Lost is reversible.** If you later find the disk and can connect it, run Verify Volume or Verify ALL. If all artifacts verify clean, the volume is automatically restored to PRESENT.

### Delete Volume

Use **Delete Volume** only after a LOST volume has been confirmed unrecoverable and you want to clean up the catalog.

**Requirements:** The volume must be in LOST status. Delete Volume is blocked for PRESENT and INIT volumes.

**What it does:**

Permanently removes from the catalog:
- The volume record
- All volume-artifact mappings
- All volume location records

**What it does not do:**

Delete Volume does not modify artifact statuses or release states. Those were already updated when the volume was marked LOST. If you delete a volume that was LOST, the artifacts remain in `lost` status in the DAT-line database.

**This action is irreversible.** There is a confirmation dialog, but once confirmed the volume is gone from the catalog permanently.

---

## 10. Best Practices

### Verify Regularly

Run **Verify ALL** periodically — at minimum when you reconnect a disk after long-term storage, and before any major reorganization. Do not assume a volume is healthy just because it was healthy the last time you checked it. Storage media degrades silently.

### Do Not Move Volume Folders Manually

Arkadia tracks volume locations in its catalog. If you move a volume folder outside of Arkadia (using Explorer or a terminal), the catalog will not reflect the change. Use the **Move** operation to relocate a volume — it copies, verifies, updates the catalog, and then removes the source. Never move volume content by hand.

### Use Append Instead of Rebuild

If you have new content for a DAT line and a PRESENT volume with remaining capacity, use **Append** rather than creating a new volume. A new volume adds overhead: another planned unit, another disk slot, another entity to track. Append is designed for incremental growth.

### Use Reabsorb Instead of Manual File Recovery

If you need to recover content from a volume — for any reason — use the **Reabsorb** operation. It is copy-verify-delete with catalog alignment. Manual file copies leave the catalog in a stale state and require a Verify pass to reconcile. Reabsorb does it correctly in one operation.

### Keep Volumes to a Single DAT Line

Arkadia enforces this structurally, but it is worth understanding why: a volume's artifact tracking, verification, and status propagation all operate relative to a single DAT-line database. Mixed-DAT volumes would require cross-database joins for every integrity operation. Keep each volume's content from one DAT line.

### Do Not Resize Below Plan

When setting initial planned capacity, err on the side of slightly larger rather than slightly smaller. Resize upward is always possible as long as disk capacity allows. Resize downward is blocked once content is placed. Plan for some growth room.

### Act on WARNING Volumes

A volume showing WARNING status has `health = crit` — one or more of its artifacts did not verify correctly. Do not defer action on WARNING volumes. Run Verify Volume to confirm the scope of the problem, then either Repair if files are recoverable or Mark Lost if they are not.

---

## 11. Troubleshooting

### Disk Not Mounted / Volume Shows ON DISK but Cannot Be Accessed

**Symptom:** A volume shows status ON DISK, but operations like Verify, Move, or Append fail with an error that the volume could not be found.

**Cause:** Arkadia's ON DISK label reflects the catalog record, not live disk discovery. The disk may not be connected or may not have been recognized.

**Resolution:**
1. Connect the disk
2. Verify that the disk's marker file is present (this should happen automatically if the disk was properly initialized in Arkadia)
3. Retry the operation — Arkadia performs live discovery at operation time

If the disk is connected but not recognized, check that the drive has been properly initialized via the Disks view. A disk that was never initialized in Arkadia will not be detected.

### Missing Artifacts After Verify

**Symptom:** Verify Volume or Verify ALL reports MISSING artifacts. Volume health is set to crit (WARNING).

**Cause:** The file expected at that path in the volume is not present. This may be due to accidental deletion, a partially completed previous operation, or physical storage failure.

**Resolution:**

1. Note which artifacts are missing (the verify results list shows paths)
2. Check whether you have a backup or external copy of the missing content
3. If yes: place the content in `incoming-repair/<platform-id>/` and run **Repair Volume**
4. If no: the content is unrecoverable. Run **Mark Lost** to record the volume's loss and update release states

### MISMATCH During Verify

**Symptom:** Verify Volume or Verify ALL reports MISMATCH for one or more artifacts. The file exists but its SHA1 does not match the recorded hash.

**Cause:** The file content has changed since it was stored. This indicates corruption — either storage media failure, filesystem error, or accidental overwrite.

**Behavior:** Arkadia records these artifacts as missing (hash-mismatched files are not considered present). Volume health is set to crit.

**Resolution:**

The same as for missing artifacts — if you have a known-good copy, use Repair. If not, Mark Lost. Do not attempt to manually replace the file and re-verify; use the Repair workflow which handles verification and catalog alignment correctly.

### Resize Blocked — Below Occupied Size

**Symptom:** Resize shows "Cannot Resize Volume" with a message about occupied size.

**Cause:** The new planned size you entered is smaller than the sum of artifact sizes currently stored in the volume.

**Resolution:** Enter a planned size equal to or greater than the reported occupied size. If you want to reduce the volume footprint, you must first Reabsorb some or all of its content, then resize.

### Resize Blocked — Disk Capacity Exceeded

**Symptom:** Resize shows "Cannot Resize Volume" with a message about disk capacity.

**Cause:** The volume is assigned to a disk. The new planned size would exceed what is allocatable on that disk, given the planned sizes of other volumes already on the same disk.

**Resolution:**

- Use a smaller planned size that fits within the available capacity shown in the error
- Or, move one of the other volumes on that disk to a different disk first, then retry

### Plan Shows No Candidates

**Symptom:** Clicking Plan shows "No Planning Candidates" or the plan preview shows all releases as deferred or skip.

**Cause possibilities:**

- No content has been ingested for this DAT line yet (run Ingest first)
- All releases are already assigned to other volumes (check Unassigned in the Systems detail pane)
- The volume's planned capacity is smaller than the smallest available release

**Resolution:**

Check the Systems detail pane for the DAT line. If **Unassigned** shows 0, all present content is already in volumes. Ingest new content or Reabsorb an existing volume to free up artifacts.

If unassigned count is positive but all candidates show as deferred, the volume's planned capacity may be too small. Try Resize to increase it.

### Volume Stuck at WARNING After Repair

**Symptom:** Repair completed but the volume is still showing WARNING / health crit.

**Cause:** Not all missing artifacts were found in the repair source directory. Repair only fills in what it can match. If some artifacts remain missing, health stays crit.

**Resolution:** Check the repair operation log (in `logs/volume-repair/`) to see which artifacts were recovered and which were not matched. Locate the remaining missing content and place it in the incoming-repair directory, then run Repair again.
