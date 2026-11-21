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

> **Lazy directory creation.** Per-DAT, per-leaf and per-release folders under `staging\`, `source\`, `archive\` and `data\media\` are created **only immediately before a file is actually written or moved there**, never because a DAT, Group, leaf or release exists. Concretely:
> - a release staging folder is created when the first file is staged into it;
> - `source\<platform>\<datLine>\<release>\` is created when a complete release is promoted from staging;
> - the archive parent is created right before the transform writes (CHD and RVZ workdirs create it at promotion);
> - a media subfolder is created right before a media file is saved.
>
> DAT/Group import and update, Library load, and path resolution (`MediaStore.NextIndexed*`) create nothing. A logical Source Artifact (e.g. the ISO behind a verified RVZ) never creates a source folder. Every reader treats a missing folder as empty. Platform-level `incoming-roms\<platform>\` and `incoming-skip\<platform>\` are still created when ingestion starts. Existing empty folders are not removed automatically (see Maintenance → Clean Empty Folders).

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
| `present-without-artifact` | Inconsistent DB state — a `present` release with no `derived_artifacts` row; not treated as satisfied, so it is allowed to stage and rebuild (logged for visibility) |
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
| `empty-synthesized` | An authoritative DAT requirement declaring `size="0"` with **no** CRC/MD5/SHA1 was generated as an empty file in `staging` and verified to be 0 bytes (only for `missing` releases; never overwrites an existing file) |
| `empty-synthesis-failed` | An authoritative empty requirement could not be synthesized/verified (unexpected existing file, unsafe path, I/O error, or release not `missing`) — the release stays incomplete |
| `dat-hash-enriched` | A file with no SHA1/MD5 match was accepted by **CRC32 + size** against a CRC-only DAT requirement; the SHA1/MD5 computed from the file were recorded in the leaf's `release_files` (see [CRC32 + size fallback](#crc32--size-fallback-for-crc-only-dat-requirements)) |
| `dat-hash-conflict` | Two incoming files with the same CRC32+size but different SHA1 in one run: the first acceptance stands, this one is not matched |
| `requirement-unsatisfiable` | The release has a requirement no ingestion path can satisfy: no SHA1/MD5 without an explicit `size="0"` (incl. CRC-only), or `size="0"` with hashes that are not the empty-file hashes — the release stays incomplete |
| `file-commit-failed` | A required file could not be committed under the file-oriented strategy (no extension mapping, mapped transform missing, or no usable identity) — the release is **not** marked present |
| `verify-failed` | A promoted source file failed verification against its DAT requirement (SHA1 mismatch, or a non-empty file for a `size="0"` requirement) — the release is **not** marked present |
| `release-commit-failed` | Defense-in-depth: not every required file of the release was committed or discarded by strategy — the release is **not** marked present |
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

## CRC32 + size fallback for CRC-only DAT requirements

Some DATs (e.g. No-Intro Atari ST `.ipf`) declare ROMs with only `size` + `crc`. Matching is SHA1 → MD5, so such a requirement (`DatFileRequirementKind.Unmatchable`) is never indexed and a correct file would be moved to `incoming-skip` as `skip`.

**Single-DAT ingestion** therefore adds a last-resort step (`Ingestion.CrcSizeFallbackMatcher`, index built by `LeafIngestionNeeds.CrcSizeIndex`), consulted **only after both SHA1 and MD5 missed**:

- Candidates are CRC-only requirements with a valid CRC and a positive declared size. The file is read for CRC32 only if its length equals a declared size.
- On a CRC32 **and** size match the file is accepted, and its already-computed SHA1/MD5 are written to `release_files` (`DatLineStore.EnrichReleaseFileHashes`) — **only into blank hash fields; DAT-declared hashes are never overwritten**. The requirement then behaves as an ordinary `Physical` one (commit identity `sha1:…`, SHA1 verification, future SHA1 matching). Logged as `dat-hash-enriched`.
- A release already satisfied (DB-authoritative) is **never** matched by CRC, so a CRC-only match can never feed `duplicate-deleted`; such a file is moved to `incoming-skip` as before.
- `unwanted` releases are matched (file routed to `incoming-skip` as `unwanted-moved`) but their DAT rows are not enriched.
- Same CRC32+size with a different SHA1 in one run → `dat-hash-conflict`, first acceptance stands.
- The source `.dat` file is never modified. Update DAT preserves locally recorded hashes when the rom (name, size, CRC) is unchanged and the new DAT still declares no SHA1/MD5 (`DatHashEnrichment.PreserveEnrichedHashes`); hashes supplied by the DAT win.
- **Not covered:** Group DAT ingestion (`GroupIngestionPlanner`/`Execution`) and the Group leaf build/update path still treat CRC-only requirements as unmatchable. Enriched hashes are not flagged as "locally derived" in the schema — the `dat-hash-enriched` log line is the audit trail.

---

## RVZ (GameCube / Wii): verified conversion and Deep Hashing

An RVZ is a lossless container of a **logical ISO**. Redump DATs identify the ISO, never the RVZ file. Arkadia therefore separates the two identities using the existing schema (no new tables or columns):

| Layer | RVZ meaning |
|---|---|
| `content_identities` | The DAT ISO identity (`sha1:<dat sha1>`) |
| `source_artifacts` | The **logical** ISO: verified logical SHA1/MD5/CRC32 and logical size. `source_artifacts` has no path, so this never claims that an ISO exists in `source/` — none is created |
| `derived_artifacts` | The **physical** RVZ: physical SHA1/MD5/CRC32 and physical size, `storage_strategy_id = rvz_compression`, **Tier A** (verified to reconstruct the source ISO) |

Verify Archive, Verify Volume, Append, Build Volume and Repair keep operating on the physical RVZ (SHA-1 / size), exactly as they do for CHD. Repair Pass B (Tier A source rebuild) matches a **physically supplied** ISO in `incoming-repair` whose SHA1 equals the recorded source SHA1; it never assumes an ISO exists locally.

DolphinTool (`tools\dolphintool\DolphinTool.exe`, registry row `dolphintool`) is always run in **portable mode**: Arkadia creates an empty `portable.txt` next to the executable before any invocation, so Dolphin never writes to `Documents`/`AppData` or the `HKCU\Software\Dolphin Emulator\UserConfigPath` registry value.

### Phase A — verified ISO → RVZ (`Ingestion.RvzConversion`)

- The default transform is `convert -f rvz -b 131072 -c zstd -l 5 -i "{input}" -o "{output}"` (lossless, no scrubbing). The historical default without `-b` always failed, because DolphinTool requires a block size. Existing catalogs are corrected **only** when the template equals that exact historical value; a customized template is never touched.
- The conversion runs in the private workdir `transform-work/rvz/<job>/`, which is on the same filesystem as the archive. The source ISO is exposed as a hardlink; if that is impossible, the tool reads the source path directly. **The ISO is never copied.**
- Acceptance requires all of the following:
  - a valid RVZ header (magic `RVZ\x01`, logical size equal to the source size, container size equal to the file length);
  - `DolphinTool verify` SHA1/MD5/CRC32 equal to the source ISO's hashes.
  Only then is the output promoted with a native `File.Move` (no overwrite) and committed as Tier A. On any failure the source is preserved (`transform-failed`) and the workdir is removed.
- The archive collision guard and commit order are unchanged. Every other transform keeps its historical direct path.
- Because Group finalization reuses the same file-oriented processor, a Group leaf mapped to `rvz_compression` also gets the verified conversion.

### Phase B — Deep Hashing (`Ingestion.RvzDeepHashing`, Single DAT only)

This phase runs right after archive extraction and before the ordinary matcher. It is active only when **all** of these hold:
- the candidate is a `.rvz` file;
- the run is an ordinary Single-DAT ingest (never Repair, never Group);
- the leaf stores its roms with `rvz_compression` (an extension mapping, or the active storage transform);
- DolphinTool is installed.
Otherwise the files continue through the ordinary pipeline unchanged (`deep-hash-unavailable` is logged when the tool is missing).

For each candidate:
1. **Header (72 bytes).** If this is not an RVZ container, the file is handed back to the ordinary pipeline.
2. **One physical pass** computes SHA1, MD5 and CRC32 together, with no separate MD5 pass. If the DAT identifies the file **physically**, it is handed back, which preserves the ordinary matching semantics. Otherwise these hashes are reused for registration.
3. **Logical-size pre-filter.** If no RVZ-eligible requirement declares the header's logical size, the file goes to `rvz-no-match` → `incoming-skip`, and DolphinTool is not run.
4. **One `DolphinTool verify -i` pass** (no temporary ISO, nothing written to disk). A tool failure or malformed output is **not** a mismatch: the file is preserved in place (`deep-hash-failed`), other candidates continue, and each failure counts in `TransformsFailed`. The run is therefore reported as PARTIAL SUCCESS or FAILED, never SUCCESS, and the failure is listed in the log's FAILURES section.
5. **Match.** The logical SHA1 must equal the DAT SHA1. The DAT size, MD5 and CRC32 must also agree wherever the DAT declares them (otherwise `rvz-logical-conflict`). A successful verify alone is never enough, and CRC32 alone never matches. **MD5 may be absent:** on CPUs with SHA-1 hardware acceleration, DolphinTool's default verify report prints `MD5 not computed` (observed with DolphinTool 2609b). SHA1, CRC32 and logical size are then still enforced, and the logical source MD5 is stored as NULL, never copied from the DAT. The same rule applies to the Phase A conversion check. In Details, such a logical source shows **Source Verified ✔** only when it is linked from its RVZ derived artifact and its SHA1 and size (plus CRC32 when declared) match the DAT (`RvzIds.IsVerifiedLogicalSourceWithoutMd5`); every other case keeps the existing "all declared DAT hashes match" rule. A missing CRC32 is always a tool failure. If the default report says `SHA1 not computed` (a CPU-dependent default in some builds; current upstream always computes SHA1), Arkadia runs **one** extra `verify -i FILE -a sha1`, which prints only the bare digest, and reuses the CRC32, MD5 and problems from the first report. This fallback never runs when the first report contains SHA1. If it fails or prints anything other than a single 40-hex line, the result is an explicit tool failure.
6. **Disposition:**
   - more than one matching release → `rvz-ambiguous` → `incoming-skip`;
   - `unwanted` → `unwanted-moved`;
   - already PRESENT → `rvz-already-present` → **the redundant incoming RVZ is deleted** (`duplicate-deleted`, `FilesSkipped`). "PRESENT" means DB-authoritative satisfaction: status `present` **and** a derived artifact, via `RedundantIncomingPolicy`. A matching DAT identity alone is not enough, and a `present` release without an artifact is adopted instead. Only the candidate is deleted: the archived artifact is never read or touched, and no extra hashing is done. For an extracted ZIP/7z/RAR member, only that member is deleted; the container stays with Phase 9;
   - not a single-file RVZ-eligible release → `rvz-unsupported-release`;
   - otherwise → **adopted**.
7. **Adoption** uses the existing `ArchiveWritePlanner` and `ArchiveWriteCollisionGuard`. An existing target is never overwritten (`archive-collision` / `rvz-target-exists`), and an artifact of another strategy is never re-pointed (`rvz-existing-artifact-conflict`).
   - **Same filesystem → native `File.Move` only** (`rvz-adopted-moved`).
   - Across filesystems → copy, then size check **and SHA1 of the destination**, which must equal the physical SHA1 already computed. A mismatch removes the copy and preserves the incoming file (`rvz-transfer-failed`). The incoming file is deleted only after the verified copy and the DB commit (`rvz-adopted-copied`).
   - The DB rows are written as described in the table above, and the release becomes `present` through the guarded `UpdateReleaseStatus`.
   - If the commit fails, the transfer is undone.

Consumed candidates are removed from the ordinary matcher, staging and Phase 8. An adopted RVZ that came out of an extracted ZIP/7z is associated with its release for Phase 9; container cleanup is unchanged.

I/O per adopted RVZ: 72-byte header read, one physical read, one DolphinTool read, then a rename. A cross-filesystem copy adds one write plus one destination read. Non-RVZ files incur no extra reads.

---

## Staging admission is DB-authoritative

For deciding whether incoming material should be **staged**, Arkadia trusts the database. It does **not** probe the filesystem to make this decision.

`Ingestion.RedundantIncomingPolicy.Locate(status, derivedArtifactRowCount)` (Phase 4b) classifies each candidate release:

| DB state | Result | Staging admission |
|---|---|---|
| `present` **and** ≥1 `derived_artifacts` row | `Satisfied` | **not stageable** — incoming constituent is redundant (Phase 8 → `duplicate-deleted`) |
| `present` **and** no `derived_artifacts` row | `PresentWithoutArtifact` | **stageable** — inconsistent state; stage so a real artifact can be rebuilt (logged `present-without-artifact`) |
| `unwanted` | (handled by UNWANTED-WINS) | **never stageable** |
| `missing` / `pending` / anything else | `NotSatisfied` | **stageable** if the incoming material is useful |

Satisfaction is decided from DB state **alone** — a `present` release with an artifact row is satisfied regardless of where that artifact is recorded: local archive, reachable volume, **offline assigned volume**, legacy `relative_path`, or any other DB-tracked durable location. Ingestion no longer performs disk-existence or volume-reachability probes for staging admission (the former `assigned-volume-unavailable` quarantine path is gone).

> **Reconciliation belongs to Verify, not ingest.** If the DB says a release is `present` but the file is actually missing or corrupt (disk failure, manual deletion, filesystem drift), that is discovered and repaired by **Verify Archive** / **Verify Volume** — not by normal ingestion. Ingestion is deliberately **not** an integrity-verification workflow, so a good incoming copy of a release the DB already calls satisfied is treated as a duplicate. The only per-file physical hash check that survives (`DerivedArtifactSatisfactionChecker`, Phase 7) applies to a release that is actually being (re)assembled, not to admission.

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
  - **`present` in DB but derived physically deleted/corrupt in `archive` or on a volume.** Because staging admission is [DB-authoritative](#staging-admission-is-db-authoritative), a `present` release with a `derived_artifacts` row is treated as satisfied and is **not** re-staged — even by a fresh re-ingest of the raw file (the incoming copy is `duplicate-deleted`). Reconciling filesystem drift is the job of **Verify Archive** / **Verify Volume**, which detect the missing/mismatched file and update DB state so a subsequent ingest can rebuild. Ingestion is intentionally not an integrity check.
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
