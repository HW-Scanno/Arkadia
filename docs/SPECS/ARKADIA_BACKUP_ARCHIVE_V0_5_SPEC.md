# Arkadia Backup / Archive v0.5 — Specification

**Status:** v0.5 implemented. Export, verification, dry-run restore, and restore complete. Backups UI implemented (Phase 6B); live restore from UI intentionally blocked pending restart-safe flow.

**Last updated:** 2026-05-11

---

> **Core product rule:**
> **ARK is not a media pack. AMP is not a backup.**
>
> `.ark` — Arkadia Backup / Archive — v0.5 implemented; database and application state backup/restore.
> `.amp` — Arkadia Media Pack — curated, distributable, provider-agnostic media/metadata package.
> These two package families are entirely distinct and must never be conflated.

---

## 1. Purpose

ARK (`.ark`) is the Arkadia-native format for backing up and restoring the application's database state.

- ARK captures the complete internal catalog state at a point in time: the global catalog database, all per-DAT-line release databases, and optionally the AMP registry.
- ARK is designed for disaster recovery, machine migration, and periodic backup — not for media distribution.
- ARK restore is a full-replacement operation. It replaces the current data directory with the restored state; merge-restore is not supported in v0.5.
- ARK is not a media pack. Media files (cover art, screenshots, video) are not included in v0.5 packages.
- ARK is not AMP. An `.ark` package must never be imported as an AMP, and an `.amp` package must never be treated as an ARK.

---

## 2. ARK vs AMP

| Feature | `.ark` — Arkadia Backup / Archive | `.amp` — Arkadia Media Pack |
|---|---|---|
| Primary purpose | Full database state backup/restore | Curated media and metadata distribution |
| Intended audience | Personal / private; environment-specific | Any Arkadia user; potentially public |
| Contains SQLite DB dump | Yes | No |
| Contains curated media files | No (v0.5) | Yes |
| Contains raw provider payloads | No — excluded | No — forbidden |
| Provider provenance visible | No — excluded | No — forbidden |
| Intended for distribution | No — not a public package format | Yes, where legally permissible |
| Intended for restore | Yes — full replacement | No — apply/import, not restore |
| Restore behaviour | Atomic staging commit; full replacement | Release-identity matching; dry-run supported |
| Privacy expectations | Local paths may be embedded; not for distribution | No private credentials, no API keys, no usernames |

---

## 3. Container and extension

- **Official extension:** `.ark`
- **v0.5 container:** Standard ZIP archive (`System.IO.Compression.ZipArchive`, `CompressionLevel.Optimal`).
- **Format identity** is determined by the presence and validity of `manifest.json` inside the archive, not by the file extension alone.
- `.amp` is reserved for Arkadia Media Packs and **must not** be used for ARK packages.

---

## 4. Archive layout

The v0.5 layout:

```
manifest.json                               — package identity, format version, counts, flags
hashes/
  files.sha256.json                         — SHA-256 + size for every content file in the package
db/
  catalog.db                                — global catalog database (always present)
  systems/
    <hardwareFamilyId>/
      <datLineId>.db                        — per-DAT-line release database (one per DAT line)
registry/
  amp-packages.json                         — AMP registry (optional; only if IncludeAmpRegistry=true)
```

**Notes:**

- `manifest.json` and `hashes/files.sha256.json` are integrity/metadata files. They are never restored to the target data directory.
- `db/catalog.db` maps to `catalog.db` in the target data directory.
- `db/systems/<hw>/<dl>.db` maps to `systems/<hw>/<dl>.db` in the target data directory.
- `registry/amp-packages.json` is restored to `{target}/ark-restore/amp-packages.json`, not to the normal registry location. See §9.

---

## 5. `manifest.json`

`manifest.json` describes the package identity, format version, and content flags.

**Fields:**

| Field | Type | Value / Description |
|---|---|---|
| `FormatName` | string | Must be exactly `"Arkadia Backup"` |
| `FormatVersion` | string | Must be `"0.5"` for v0.5 packages |
| `CreatedAtUtc` | string | ISO 8601 UTC timestamp (`"O"` round-trip format) |
| `ArkadiaAppVersion` | string? | Application version string, or null if not available |
| `CredentialsExcluded` | bool | Always `true` — credentials are never included |
| `CachePackagesExcluded` | bool | Always `true` — provider cache packages are never included |
| `MediaIncluded` | bool | `false` in v0.5 — media files are not included |
| `AmpRegistryIncluded` | bool | `true` if `registry/amp-packages.json` is present; `false` otherwise |
| `DatLineCount` | int | Number of DAT-line databases included |
| `StoreCount` | int | Total number of database files included (catalog + DAT lines) |
| `HashAlgorithm` | string | Always `"SHA-256"` |

**Verification rules:**

- `FormatName` must equal `"Arkadia Backup"` — mismatch blocks restore.
- `FormatVersion` must equal `"0.5"` — mismatch blocks restore (no migrator exists).
- `CredentialsExcluded` must be `true` — `false` blocks restore.
- `CachePackagesExcluded` must be `true` — `false` blocks restore.
- `HashAlgorithm` must equal `"SHA-256"` — mismatch blocks restore.

**Example:**

```json
{
  "FormatName": "Arkadia Backup",
  "FormatVersion": "0.5",
  "CreatedAtUtc": "2026-05-11T10:00:00.0000000Z",
  "ArkadiaAppVersion": null,
  "CredentialsExcluded": true,
  "CachePackagesExcluded": true,
  "MediaIncluded": false,
  "AmpRegistryIncluded": false,
  "DatLineCount": 3,
  "StoreCount": 4,
  "HashAlgorithm": "SHA-256"
}
```

---

## 6. `hashes/files.sha256.json`

`hashes/files.sha256.json` provides integrity verification for all content files in the package.

**Entry fields:**

| Field | Type | Description |
|---|---|---|
| `Path` | string | Internal ZIP path of the file being hashed |
| `Sha256` | string | SHA-256 hex digest (lowercase, 64 characters) |
| `SizeBytes` | long | Uncompressed size of the ZIP entry in bytes |

**Coverage:**

The hash file includes entries for every content file in the archive:

- `manifest.json`
- `db/catalog.db`
- All `db/systems/<hw>/<dl>.db` files
- `registry/amp-packages.json` (if present)

**`hashes/files.sha256.json` does not include itself.**

**Verification rules:**

- All tracked files must be listed — missing entry is an Error.
- Each listed `Path` must correspond to an existing ZIP entry — missing entry is an Error.
- SHA-256 of each ZIP entry must match the recorded value — mismatch is an Error.
- Untracked entries (present in ZIP but absent from the hash manifest, excluding `manifest.json` and `hashes/files.sha256.json`) are flagged as Warnings.

---

## 7. Sidecar (`.ark.sha256`)

`ArkWriterService.Write()` creates a sidecar file alongside the output `.ark`:

```
<package>.ark.sha256
```

The sidecar contains the SHA-256 hex digest of the complete `.ark` file (lowercase, 64 characters), followed by a newline.

The sidecar allows a receiver to verify package integrity before opening the ZIP. It is not required for restore; `ArkRestoreService` does not require the sidecar to be present.

---

## 8. Export scope

`ArkWriterService.Write(options, outputPath)` produces the `.ark` from the current data directory.

**`ArkExportOptions`:**

| Option | Type | Description |
|---|---|---|
| `IncludeAmpRegistry` | bool | Whether to include `registry/amp-packages.json` |

**Always included:**

- `manifest.json` — format identity and flags
- `hashes/files.sha256.json` — SHA-256 integrity manifest
- `db/catalog.db` — global catalog database (WAL-safe copy via `BackupDatabase()`)
- `db/systems/<hw>/<dl>.db` — one per registered DAT line with a `DataStorePath`

**Conditionally included:**

- `registry/amp-packages.json` — if `IncludeAmpRegistry=true`

**Always excluded:**

- Provider credentials (ScreenScraper username, password, developer ID, developer password)
- Provider cache packages (`scrape-cache/`)
- Media files (`data/media/`)
- Log files
- Tool binaries
- Staging or temp directories

---

## 9. AMP registry restore path

When `registry/amp-packages.json` is present in the ARK package, it is restored to:

```
{targetDataDir}/ark-restore/amp-packages.json
```

It is **not** restored to the operational registry location. This is intentional:

- The registry references local cache package paths that may not be valid on the restore machine.
- Restoring it directly could register packages that no longer exist on disk.
- Placing it in `ark-restore/` allows the user to inspect it and manually re-register valid packages after restore.

---

## 10. Credential policy

Credentials are always excluded from ARK packages. `CredentialsExcluded=true` is a mandatory manifest field enforced by the verifier.

If this flag is `false` in a manifest, the package verifier raises an Error and restore is blocked.

---

## 11. Provider cache policy

Provider cache packages are always excluded from ARK packages. `CachePackagesExcluded=true` is a mandatory manifest field enforced by the verifier.

If this flag is `false` in a manifest, the package verifier raises an Error and restore is blocked.

Cache packages are large, provider-specific, and not essential for catalog state restore. They can be re-downloaded or rebuilt independently after restore.

---

## 12. Trust model — the core semantic rule

> **Imported/restored state is expected state. Verified state is trusted state.**

ARK restore re-establishes Arkadia's internal catalog state. After restore:

- **Expected state** is restored: the catalog knows what releases exist, what files are expected, and what the DAT says.
- **Trusted state** is not yet restored: volume locations and file presence have not been independently verified against the filesystem.

To re-establish trusted state after restore, run **Verify ALL** or **Verify Volume** from the Operations view. Only after independent verification does the restored state become trusted.

This separation is intentional:

- Restore is a fast, atomic database operation.
- Verification is a potentially long filesystem scan.
- Running restore without verification is safe but leaves archive trust at "expected" until verification completes.

The post-restore warning is mandatory and always included in every `ArkRestoreResult.Warnings` and `ArkRestorePlan.Warnings`.

---

## 13. Restore semantics

`ArkRestoreService.Restore(arkFilePath, targetDataDir, overwrite, ct)` performs the restore.

**Workflow:**

1. **Plan** — `ArkRestorePlanService.PlanRestore()` verifies the package and builds the restore plan. If the plan reports issues, restore is blocked.
2. **Overwrite policy** — if the target is non-empty and `overwrite=false`, `InvalidOperationException` is thrown immediately (target is not modified).
3. **Staging** — a staging directory is created in the parent of the target: `{parent}/.ark-restore-{yyyyMMddHHmmss}-{guid8}`.
4. **Extract** — all planned entries are extracted to staging. Cancellation is checked before each entry.
5. **Verify staging** — `catalog.db` must be present in staging after extraction.
6. **Commit** — staging is atomically moved to the target:
   - Target missing → `Directory.Move(staging, target)`
   - Target empty → delete empty dir, then `Directory.Move(staging, target)`
   - Target non-empty + `overwrite=true` → move existing target to `{target}.pre-ark-restore-{stamp}`, then move staging to target
7. **Post-restore warnings** — mandatory warnings are appended to the result.

**On failure before commit:**
The staging directory is deleted. The target is not modified.

**On commit failure after moving previous data aside:**
The exception message includes the staging path and the previous-data backup path for manual recovery.

**Cancellation:**
If the `CancellationToken` fires during extraction, `OperationCanceledException` is thrown after deleting the staging directory.

---

## 14. Verification after restore

After restore, `ArkRestoreResult.Warnings` always contains:

1. "Restore complete. Run Verify ALL / Verify Volume before trusting restored archive state."
2. "Restored databases may contain absolute paths that require review or relocation."

The plan service (`ArkRestorePlanService`) also always includes these warnings in `ArkRestorePlan.Warnings`, so they are visible in the dry-run before any data is written.

---

## 15. Path safety

All archive entry paths in an `.ark` package must pass safety checks. These checks are enforced by both the verifier and the restore service.

**Forbidden path patterns:**

| Pattern | Example | Reason |
|---|---|---|
| Backslash in path | `db\catalog.db` | Non-portable; Windows-specific separator |
| Absolute path | `/etc/passwd` | Traversal outside the target directory |
| `..` segment | `../evil.txt` | Directory traversal |
| Empty segment | `db//catalog.db` | Ambiguous; rejected defensively |

**Enforcement:**

- The verifier raises an Error for any unsafe path and marks the package as invalid.
- The plan service marks unsafe entries as `WillRestore=false` and adds an Issue.
- The restore service raises `InvalidOperationException` if an entry's resolved target path is outside the target data directory.

---

## 16. Version and migration policy

ARK v0.5 supports exactly one format version: `"0.5"`.

If a package's `FormatVersion` is anything other than `"0.5"`, the plan service and restore service both block restore with an Issue or Exception. No migrator exists.

Future format versions will require an explicit migrator before restore is possible.

---

## 17. Absolute path warning

Restored databases may contain absolute filesystem paths in:

- `volume_locations` — physical disk mount points
- `release_media_curation` — media file paths on disk

These paths refer to the source machine's filesystem layout and will likely not be valid on the restore machine. After restore:

1. Run **Verify ALL** or **Verify Volume** — this will identify volumes and media files that cannot be found at their recorded paths.
2. Use **Relocate Volume** or the volume management tools to update paths to the restore machine's layout.

This warning is always emitted and cannot be suppressed.

---

## 18. Current limitations (v0.5)

- **No media:** Media files are not backed up. Only database state is captured.
- **Credentials excluded:** Credentials are intentionally absent. Re-enter provider credentials after restore.
- **Cache packages excluded:** Provider cache packages must be re-registered after restore.
- **Full replacement only:** Merge restore is not supported. Non-empty target requires `overwrite=true`.
- **No delta/incremental backup:** Each `.ark` is a complete point-in-time snapshot.
- **No compression optimization:** All entries use `CompressionLevel.Optimal`; no per-entry strategy.
- **No encryption:** Package contents are not encrypted.
- **Absolute paths in DBs:** Volume locations and media paths embedded in databases may reference the source machine. See §17.
- **AMP registry not auto-restored to operational location:** Placed in `ark-restore/` for manual inspection. See §9.
- **Backup UI implemented; live restore blocked:** The Backups sidebar section exposes package creation. Live restore while the application is running is intentionally blocked — replacing `data\` files while SQLite connection pools are active is unsafe. A restart-safe restore workflow is planned.

---

## 19. Future work

The following are explicitly deferred and not part of v0.5:

- Restart-safe restore workflow (Phase 7) — backup creation UI is implemented; live restore is blocked
- Media files in ARK packages
- Incremental / delta backup
- Encryption of package contents
- Automatic path relocation after restore
- Automatic re-registration of AMP registry from `ark-restore/`
- Scheduled backup support
- Cloud / remote backup targets
- Compression strategy selection per entry type
- Package signing and verification keys

---

## 20. Implementation phases

| Phase | Description | Status |
|---|---|---|
| 1 | ARK models: `ArkExportOptions`, `ArkWriteResult`, `ArkFileHashEntry` | **Complete** |
| 2 | ARK writer service (`ArkWriterService`) | **Complete** |
| 3 | ARK package verifier (`ArkPackageVerifierService`) | **Complete** |
| 4 | ARK restore dry-run / planning (`ArkRestorePlanService`) | **Complete** |
| 5 | ARK restore into staging (`ArkRestoreService`) | **Complete** |
| 6 | Backup UI — Backups sidebar section; Create Backup + log; package verification; backup list | **Complete** |
| 7 | Restart-safe restore UI — trigger ARK restore on next application start | Planned |
| 8 | Scheduled / automatic backup | Future |
| 9 | Media files in ARK packages | Future |
| 10 | Incremental / delta backup | Future |
