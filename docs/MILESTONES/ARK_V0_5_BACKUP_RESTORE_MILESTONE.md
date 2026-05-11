# ARK v0.5 — Backup / Restore — Milestone

**Completed:** 2026-05-11
**Tests at milestone:** 1159 (all passing) — service layer
**Updated:** 2026-05-11 — Phase 6B Backups UI complete; 1163 tests passing
**Branch:** main

---

## 1. Summary

ARK v0.5 is the first complete implementation of the Arkadia Backup / Archive format. The milestone covers the full backup/restore pipeline: export, integrity verification, dry-run restore planning, and atomic restore execution.

ARK v0.5 backs up Arkadia database state only — the global catalog, all per-DAT-line databases, and optionally the AMP registry. Media files, provider credentials, and cache packages are intentionally excluded.

The core trust rule is established: **restored state is expected state; Verify ALL / Verify Volume restores trust.**

---

## 2. Baseline

| Metric | Value |
|---|---|
| Tests at milestone | 1159 |
| Tests added this milestone | 34 (Phases 3–5) |
| Tests added Phase 3 (verifier) | 14 |
| Tests added Phase 4 (plan) | 10 |
| Tests added Phase 5 (restore) | 10 |
| Previous baseline | 1125 (v2.0 milestone) |
| Test framework | xUnit |
| Build target | .NET 8 / Avalonia 11 / Windows |

All 1159 tests pass. No regressions from the v2.0 baseline.

---

## 3. Service stack

| Service | File | Responsibility |
|---|---|---|
| `ArkWriterService` | `Catalog/Ark/ArkWriterService.cs` | Export: write `.ark` ZIP from the current data directory |
| `ArkPackageVerifierService` | `Catalog/Ark/ArkPackageVerifierService.cs` | Verify integrity and policy compliance of `.ark` packages |
| `ArkRestorePlanService` | `Catalog/Ark/ArkRestorePlanService.cs` | Dry-run: plan restore without writing anything |
| `ArkRestoreService` | `Catalog/Ark/ArkRestoreService.cs` | Execute restore via atomic staging workflow |

**Supporting models:**

| Model | File |
|---|---|
| `ArkExportOptions` | `Catalog/Ark/ArkExportOptions.cs` |
| `ArkWriteResult` | `Catalog/Ark/ArkWriteResult.cs` |
| `ArkFileHashEntry` | `Catalog/Ark/ArkFileHashEntry.cs` (internal) |
| `ArkPackageVerificationSeverity` | `Catalog/Ark/ArkPackageVerificationSeverity.cs` |
| `ArkPackageVerificationIssue` | `Catalog/Ark/ArkPackageVerificationIssue.cs` |
| `ArkPackageVerificationResult` | `Catalog/Ark/ArkPackageVerificationResult.cs` |
| `ArkRestorePlanEntry` | `Catalog/Ark/ArkRestorePlanEntry.cs` |
| `ArkRestorePlan` | `Catalog/Ark/ArkRestorePlan.cs` |
| `ArkRestoreResult` | `Catalog/Ark/ArkRestoreResult.cs` |

---

## 4. Package format

**Container:** ZIP (`System.IO.Compression.ZipArchive`, `CompressionLevel.Optimal`)
**Extension:** `.ark`
**Sidecar:** `<package>.ark.sha256` — SHA-256 hex digest of the complete `.ark` file

**Layout:**

```
manifest.json
hashes/files.sha256.json
db/catalog.db
db/systems/<hardwareFamilyId>/<datLineId>.db    (one per DAT line)
registry/amp-packages.json                       (optional; IncludeAmpRegistry=true)
```

**Manifest fields:** `FormatName="Arkadia Backup"`, `FormatVersion="0.5"`, `CreatedAtUtc`, `ArkadiaAppVersion`, `CredentialsExcluded=true`, `CachePackagesExcluded=true`, `MediaIncluded=false`, `AmpRegistryIncluded`, `DatLineCount`, `StoreCount`, `HashAlgorithm="SHA-256"`.

---

## 5. Safety semantics

**Path safety** — enforced by verifier and restore service:

- No backslash in archive entry paths
- No absolute paths (no leading `/`)
- No `..` traversal segments
- No empty path segments

Any violation causes the verifier to raise an Error and blocks restore.

**Mandatory manifest checks** — any violation blocks restore:

- `FormatName == "Arkadia Backup"`
- `FormatVersion == "0.5"`
- `CredentialsExcluded == true`
- `CachePackagesExcluded == true`
- `HashAlgorithm == "SHA-256"`

**Hash integrity** — SHA-256 of every content file is verified before restore is permitted.

---

## 6. Restore semantics

**Atomic staging workflow:**

1. Create staging dir in parent of target: `{parent}/.ark-restore-{yyyyMMddHHmmss}-{guid8}`
2. Extract all planned entries to staging; check cancellation before each entry
3. Verify `catalog.db` is present in staging
4. Commit:
   - Target missing → `Directory.Move(staging, target)`
   - Target empty → delete empty dir, then `Directory.Move(staging, target)`
   - Target non-empty + `overwrite=true` → move target to `{target}.pre-ark-restore-{stamp}`, then move staging to target
5. On any exception before commit: delete staging dir; target is unmodified

**Overwrite policy:**

| Target state | `overwrite` | Outcome |
|---|---|---|
| Missing or empty | any | Restore proceeds unconditionally |
| Non-empty | `false` | `InvalidOperationException`; target unchanged |
| Non-empty | `true` | Previous data moved aside; restore proceeds |

**AMP registry restore path:**
`registry/amp-packages.json` → `{target}/ark-restore/amp-packages.json`
Not restored to the operational location; placed for manual inspection and re-registration.

**Cancellation:**
`CancellationToken` checked before each entry. Staging deleted before `OperationCanceledException` propagates.

---

## 7. Core trust rule

> **Imported/restored state is expected state. Verified state is trusted state.**

ARK restore re-establishes internal catalog state — what the catalog expects to exist, based on DAT data and prior curation. It does not verify that files are actually present on disk at their recorded paths.

Run **Verify ALL** or **Verify Volume** after every restore to re-establish trusted state.

This warning is mandatory and cannot be suppressed. It is always present in both `ArkRestorePlan.Warnings` and `ArkRestoreResult.Warnings`.

---

## 8. Exclusions

| Content | Excluded | Reason |
|---|---|---|
| Provider credentials | Always | Privacy; credentials must be re-entered after restore |
| Provider cache packages | Always | Large; can be re-downloaded or rebuilt independently |
| Media files | Always (v0.5) | Out of scope for v0.5 database-state backup |
| Log files | Always | Ephemeral; not needed for state restore |
| Tool binaries | Always | Not application state |
| Staging / temp directories | Always | Ephemeral |

`CredentialsExcluded=true` and `CachePackagesExcluded=true` are mandatory manifest flags enforced by the verifier. A package with either flag set to `false` is blocked from restore.

---

## 9. Known limitations

- Media files not backed up — manage separately from database state.
- AMP registry restored to `ark-restore/` only — not to the operational registry location.
- Absolute paths in databases (volume locations, media paths) may be invalid on the restore machine; run Verify ALL after restore.
- Live restore from the UI is intentionally blocked while Arkadia is running — restart-safe restore is planned.
- Full replacement only — no merge restore.
- No incremental / delta backup.
- No encryption.
- Settings export not implemented.

---

## 10. Next steps

| Phase | Description |
|---|---|
| Phase 7 | Restart-safe restore — trigger ARK restore on next application start |
| Phase 8 | Scheduled backup |
| Phase 9+ | Media inclusion, incremental backup, encryption |
| Optional | Verify Selected — verify an individual .ark from the Backups view |
| Optional | Settings export |

---

## 11. Phase 6B — Backups UI

**Completed:** 2026-05-11
**Tests after Phase 6B:** 1163 (all passing, 0 errors, 0 warnings, build clean)

### Implemented

| Item | Detail |
|---|---|
| `backups\` folder | Created at startup via `ArkadiaFolders.Backups` |
| Backups sidebar section | Nav button between Logs and Settings |
| Create Backup UI | BACKUP pane with button + log window |
| `.ark` creation | Written to `backups\arkadia-backup-<timestamp>.ark` |
| `.ark.sha256` sidecar | Written alongside the package |
| Automatic verification | `ArkPackageVerifierService` called after write |
| Log output | Timestamped progress lines; **BACKUP COMPLETE** on success |
| Backup list | RESTORE pane lists `.ark` files from `backups\` |
| Restore Selected | Enabled on selection; intentionally blocked for live restore |
| Live restore block | `InfoDialog` explains restriction and shows file path |
| Backup list refresh | Automatic after successful backup creation; manual Refresh button |

### Tests added (Phase 6B)

| Test | File |
|---|---|
| `EnsureCreated_CreatesBackupsFolder` | `ArkadiaFoldersTests.cs` |
| `SuggestedArkFileName_HasArkExtension` | `ArkUiHelpersTests.cs` |
| `SuggestedArkFileName_MatchesExpectedPattern` | `ArkUiHelpersTests.cs` |
| `BackupsFolder_ReturnsPathUnderBaseDir` | `ArkUiHelpersTests.cs` |

Previous baseline: 1159 → Phase 6B: **1163** (+4)
