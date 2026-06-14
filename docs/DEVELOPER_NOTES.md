# Arkadia — Developer Notes

---

## Solution / Project Structure

```
Arkadia.sln
├── Arkadia.csproj                  — main application (Avalonia 11 / .NET 8 / Windows)
│   ├── Data/                       — all database and storage logic
│   │   ├── DatLineStore.cs         — per-DAT SQLite store (releases, artifacts, status guards)
│   │   ├── CatalogService.cs       — global catalog: volumes, assignments, DA map
│   │   ├── VolumePathResolver.cs   — workspace-first then disk-mount volume root resolution
│   │   └── DiskDiscoveryService.cs — enumerates drives with ARKADIA.DISK.json markers
│   ├── Library/                    — LibraryEntry, title resolution helpers
│   ├── Providers/                  — scraper clients and import services
│   ├── Ingestion/                  — DAT parsing and ingest pipeline, color converters
│   ├── Themes/                     — theme engine, palette slices
│   ├── Systems/                    — system/hardware-family management
│   ├── Volumes/                    — volume lifecycle management
│   │   ├── VolumeArtifactPathBuilder.cs  — flat path authority (use everywhere)
│   │   ├── AppendVolumePlanner.cs        — dry-run: candidate selection + SkipReason constants
│   │   ├── AppendVolumeService.cs        — execution: copy → verify → DB commit
│   │   ├── VolumeFillbackPlanner.cs      — dry-run: source selection for cross-volume move
│   │   ├── VolumeFillbackService.cs      — execution: move/copy → verify → delete → DB update
│   │   └── VolumeVerifyService.cs        — recursive scan, SHA-1 classification, recovery
│   ├── LocalArchive/               — local archive verify and repair
│   │   ├── LocalArchiveVerifyService.cs  — filesystem-first scan, redundancy detection, repair
│   │   ├── LocalArchiveVerifyPlan.cs     — scan result: entries, counts, IsClean
│   │   ├── LocalArchiveEntry.cs          — per-file classification + AssignedVolumeLabel/VolumeFilePath
│   │   └── AssignedVolumeInfo.cs         — volume assignment context for redundancy detection
│   ├── Purge/                      — purge planner/executor, analytics
│   ├── Disks/                      — disk registration and management
│   ├── Pending/                    — pending artifact management
│   ├── Staging/                    — staging area operations
│   ├── Dashboard/                  — dashboard view components
│   ├── Controls/                   — shared UI controls
│   └── Catalog/                    — catalog-level services
│       └── Ark/                    — ARK backup/restore pipeline (writer, verifier, plan, restore)
└── Arkadia.Tests.csproj            — xUnit tests (1480 tests, no UI dependency)
    ├── Data/                       — store, proposal, mapping, metadata, status guard tests
    ├── Volumes/                    — append, fillback, verify volume, diagnostics tests
    ├── LocalArchive/               — verify archive, redundancy, repair tests
    ├── Purge/                      — purge planner, analytics tests
    ├── Providers/                  — scraper parser, import service, candidate tests
    ├── Ark/                        — ARK verifier, plan service, restore service tests
    └── MergeProposalVmTests.cs     — view-model tests for merge dialog
```

Key AXAML settings:
- `AvaloniaUseCompiledBindingsByDefault=true` — all bindings use compiled bindings; DataTemplate must declare `x:DataType`.
- `xmlns:ark="using:Arkadia"` — project root namespace alias used throughout MainWindow.axaml.

---

## Main Databases

### `catalog.db`

Global catalog database. Created at `data/catalog.db` on first launch.

| Table | Purpose |
|---|---|
| `hardware_families` | Systems / platform groups |
| `dat_lines` | Imported DAT files, one row per DAT |
| `catalog_settings` | Key/value settings store |
| `metadata_value_mappings` | Global normalization rules (field, match_value, replacement, enabled) |

Managed by `CatalogService` in `Data/CatalogService.cs`.

### Per-DAT Databases

One SQLite DB per DAT line, stored at:
```
data/platforms/<hardwareFamilyId>/<datLineId>/releases.db
```

Managed by `DatLineStore` in `Data/DatLineStore.cs`.

---

## Important Tables (Per-DAT DB)

### `releases`

Core release record: `release_id`, `name`, `region`, `languages`, `format`, `size`, `tier`, `status`.

### `release_metadata`

Canonical metadata for a release: one row per release with all metadata fields as columns (title, original_title, developer, publisher, year, genre, subgenre, players, release_type, rating, languages, alternate_titles, description, notes, scraped_at_utc).

### `release_metadata_field_state`

Per-field state for each release: `release_id`, `field`, `source` ("manual" / "screenscraper" / ""), `provider`, `locked` (0/1).

Source and provider record where a value came from. `locked=1` prevents automated overwrite.

### `release_metadata_proposals`

Pending proposals from a provider scrape: `release_id`, `provider`, `field`, `value`, `scraped_at`, `accepted` (0/1).

Proposals start as `accepted=0`. When the user applies a field in Merge Metadata, `accepted` is set to 1 and the canonical `release_metadata` row is updated.

### `release_provider_payloads`

Raw JSON payload from the last scrape: `release_id`, `provider`, `payload` (TEXT), `saved_at`.

One row per (release, provider). Used for scrape-from-cache (planned feature).

---

## Media Storage Layout

All scraped media lives under `data/media/<hardwareFamilyId>/<datLineId>/`. File stems use the pattern:

- **Screenshots, fanart, logos, etc.:** `<releaseStem>_<index>.<ext>` — e.g., `super_mario_world_0.png`
- **Regional covers:** `<releaseStem>_<region>_<index>.<ext>` — e.g., `super_mario_world_us_0.jpg`

Subfolders:

| Folder | Content |
|---|---|
| `covers-front/` | Regional front cover artwork |
| `covers-back/` | Regional back cover artwork |
| `covers-spine/` | Regional spine artwork |
| `covers-wrap/` | Regional full-wrap artwork |
| `screenshots-title/` | Title screen captures |
| `screenshots/` | Gameplay captures |
| `fanart/` | Fanart images |
| `videos/` | Gameplay/trailer videos |
| `logos-hd/` | High-resolution logos |
| `logos/` | Standard logos |
| `marquees/` | Arcade marquee artwork |
| `flyers/` | Arcade flyer artwork |
| `manuals/` | Manual PDFs or images |
| `physical/` | Physical media flat photos |
| `physical-texture/` | Physical media texture photos |
| `metadata/` | Raw provider JSON payloads (also stored in DB) |

Deduplication uses `ScreenScraperClient.DownloadMediaAsync` which checks:
1. Existing file by expected byte size (size-based duplicate guard)
2. Existing file at the exact stem (stem-based duplicate guard)

---

## Scraper Architecture

### `ScraperProviderDialog`

Simple provider-selection dialog. Shows available providers with configured/not-configured status. Returns the selected provider ID string.

### `ScrapeReviewDialog`

Full candidate search and selection dialog. On open, performs an automatic search using `ScreenScraperClient.SearchCandidatesAsync`. Displays results with title, year, system, and thumbnail. Also supports direct ROM-hash lookup as an exact-match fallback. Returns `ScrapeReviewResult` with either a `Candidate` or a `DirectResult` (`ScreenScraperResult`).

### `ScreenScraperClient`

Static class. Key methods:

| Method | Purpose |
|---|---|
| `SearchCandidatesAsync(...)` | Search by title, return candidate list |
| `FetchDetailsByGameIdAsync(...)` | Fetch full metadata + media URLs by game ID |
| `DownloadMediaAsync(...)` | Download one media asset to disk (with dedup, format detection, retry) |
| `ParseGameJson(json)` | Internal: parse ScreenScraper API response into `ScreenScraperResult` |
| `NormalizeGenres(genre, subgenre)` | Internal: split combined genre strings like "Action / Platformer" |
| `TryResolveSystemId(platformId, out id)` | Map Arkadia platform ID to ScreenScraper system ID |

### `ScreenScraperImportService`

Extracted service (`Providers/ScreenScraperImportService.cs`) — the non-UI post-result pipeline.

Constructor: `new ScreenScraperImportService(string dataDir)`.

`ImportAsync(entry, result, mappings, progress, ct)` → `MediaDownloadSummary`:

1. Normalizes metadata fields via `MetadataValueNormalizer`
2. Saves proposals via `DatLineStore.ApplyProviderProposals(..., autoApplyEmptyFields: false)`
3. Saves raw JSON payload to DB via `DatLineStore.SaveProviderPayload`
4. Writes JSON file to `metadata/` subfolder
5. Downloads all media categories (covers, screenshots, fanart, video, logos, marquees, flyers, manuals, physical)
6. Returns `MediaDownloadSummary` with per-category download counts

Rate-limit exceptions propagate to the caller. Per-asset failures are swallowed.

### `MergeMetadataDialog`

Review dialog for pending proposals. Uses `ProposalRowVm` (INotifyPropertyChanged) for compiled bindings. Proposals are loaded via `DatLineStore.LoadMetadataProposals`. On apply, `DatLineStore.MarkMetadataProposalAccepted` and `DatLineStore.SaveReleaseMetadata` are called for each accepted field.

**UNLOCK toggle**: MANUAL and LOCKED fields have a toggle button that sets `IsOverridden=true`, enabling the row's checkbox.

---

## Metadata Rules

### Canonical Metadata

The authoritative current value is stored in `release_metadata`. This is what the UI displays and what is compared when building proposals.

### Proposals

Proposals (`release_metadata_proposals`) are uncommitted provider suggestions. They accumulate across scrapes. The Merge Metadata dialog is the review step where the user promotes proposals to canonical.

`autoApplyEmptyFields=true` (used in bulk scrape): auto-applies proposals for empty fields, setting `accepted=1` immediately.

`autoApplyEmptyFields=false` (used in manual scrape): saves all proposals as `accepted=0`. Nothing is written to `release_metadata` until the user acts in Merge Metadata.

### Locks

`release_metadata_field_state.locked=1` means the field will not be touched by `autoApplyEmptyFields=true`. It will show LOCKED in the Merge dialog. The user must explicitly UNLOCK it in the dialog for that merge session.

### Source Tracking

`release_metadata_field_state.source` records how the current canonical value was set:
- `"manual"` — set via Edit Metadata dialog
- `"screenscraper"` — set via Merge Metadata after a scrape
- `""` — no recorded source (e.g., legacy imports)

### Metadata Value Mappings

`CatalogService.LoadMetadataValueMappings()` loads all active rules. `MetadataValueNormalizer.Normalize(field, value, mappings)` applies them: case-insensitive match on (field, match_value), returns replacement or trimmed input unchanged.

Applied at:
- `ScreenScraperImportService.ImportAsync` — before proposals are saved
- `EditMetadataDialog.OnSave` — before canonical write
- `MainWindow.UpdateCatalogHero` — before badge display

---

## DAT Provider Architecture

### Where DAT Provider Data Lives

DAT-derived data lives in two layers:

**`releases` table (per-DAT DB)**
The `releases` table stores what the DAT says a release *is*: `release_id`, `name`, `region`, `languages`, `format`, `size`, `tier`, `status`. This data comes from the imported DAT file and is the permanent technical identity of a release. It is never overwritten by metadata scraping.

**`catalog.db` — `dat_lines` table**
The `dat_lines` table records each imported DAT scope: authority (e.g., `mame`, `no-intro`, `redump`), media type, hardware family association. Display labels (`MAME · ROM`, `Redump · DVD`) are assembled at runtime from these fields — the raw `name` column is a neutral media-type string, not a formatted display label.

### `release_metadata` vs `releases`

| Column | Table | Origin |
|---|---|---|
| `name` | `releases` | DAT file — technical release name |
| `region` | `releases` | DAT file — encoded in filename or DAT tag |
| `format` | `releases` | DAT file — media format/type |
| `size` | `releases` | DAT file — byte count or checksum |
| `title` | `release_metadata` | Metadata provider (ScreenScraper) |
| `original_title` | `release_metadata` | Metadata provider |
| `developer` / `publisher` | `release_metadata` | Metadata provider |
| `year` / `genre` / `rating` | `release_metadata` | Metadata provider |
| `description` | `release_metadata` | Metadata provider |

`release_type` in `release_metadata` is **not** the same as `format` in `releases`. `format` is the raw media type from the DAT (ROM, DVD, CHD, …). `release_type` is a normalized classification from the metadata provider (Retail, Fan Translation, Prototype, …). They are independent and may co-exist without conflict.

### MAME-Specific Considerations

MAME shortnames (e.g., `anmlbskt`) are stored in `releases.name` as the technical identifier — equivalent to the role a No-Intro ROM checksum plays for cartridge systems. They are never replaced by scraped titles.

MAME DATs may also carry:
- Parent/clone relationships
- BIOS and device dependencies
- Driver working state
- Software list associations
- Technical flags (inputs, controls, screen orientation)

None of this data has dedicated storage today. Future extraction services (see below) will add dedicated tables for it without touching `releases` or `release_metadata`.

**Scrape As System** for MAME hardware families should be set to `arcade` (ScreenScraper system ID for arcade machines). This is stored on the `hardware_families` row and used by `ScreenScraperClient.TryResolveSystemId`.

### Hardware Family Lifecycle

Deleting a hardware family (`hardware_families` row) is blocked at the service layer while any `dat_lines` rows reference it. This prevents orphaned per-DAT databases.

---

## Future Extraction Service Candidates

### DAT-Derived Technical Facts

The following complementary data can be extracted from MAME or other provider DATs and stored separately from metadata proposals. These are DAT-derived facts, not scraper suggestions — they should not participate in the proposal/merge workflow.

| Service | Responsibility | Storage target |
|---|---|---|
| `DatProviderService` | Authority-agnostic DAT parsing, release ingest, `dat_lines` management | `catalog.db` + per-DAT `releases` |
| `MameDatExtractionService` | Parse and persist MAME-specific fields: driver, parent/clone, BIOS/device, working state, software lists, technical flags | New per-DAT table(s): `mame_release_facts`, `mame_relationships` |
| `HardwareFamilyService` | Hardware family CRUD, Scrape As System resolution, deletion guard | `catalog.db` `hardware_families` |
| `DatDerivedFactsStore` | Generic store layer for any DAT-provider-sourced supplemental facts (non-metadata, non-proposal) | Per-DAT DB, separate from `release_metadata` |

### Why Separate Storage

DAT-derived facts must:
1. **Never be overwritten by ScreenScraper** — they come from an authoritative technical source.
2. **Survive re-scrapes** — even if a release is re-scraped or proposals are cleared, technical facts persist.
3. **Feed read-only surfaces** — badges, working state indicators, parent/clone trees, future filters — without entering the proposal/merge workflow.
4. **Remain distinct from `release_metadata`** — to preserve the clean boundary between technical identity and enriched display data.

---

## Volume and Archive Stack

See [ARCHIVE_AND_VOLUME_MODEL.md](ARCHIVE_AND_VOLUME_MODEL.md) for the full model. Key invariants:

- **Flat layout.** Active volume artifacts live at `<volume root>\<filename>`. No release-name subfolders. `VolumeArtifactPathBuilder.GetFlatFullPath()` is the single authority. Use it everywhere.
- **Filesystem-first.** All verify/repair workflows enumerate physical files first. DB is reconciled against disk.
- **No silent deletion.** Repair moves files to managed locations; nothing is discarded silently.
- **DB after verification.** Append creates VA rows only after hash-verified copy. Fillback deletes source only after verified copy.

### UNWANTED semantics

- `UNWANTED` is a curator veto, not a lifecycle state.
- `DatLineStore.UpdateReleaseStatus()` is SQL-guarded: `AND status != 'unwanted'`. Ingestion cannot reset unwanted to present.
- `DatLineStore.RestoreWantedRelease()` is the **only** allowed exit from unwanted.
- UNWANTED WINS: if any release linked to an artifact is unwanted, that artifact is excluded from all automatic flows.

See [UNWANTED_RELEASES.md](UNWANTED_RELEASES.md) for the full invariant table.

### CatalogService — volume-related methods

| Method | Purpose |
|---|---|
| `GetAssignedDerivedIdsByDatLine(dlId)` | Fast set for Append candidate exclusion |
| `GetAssignedDerivedIdsWithVolumesByDatLine(dlId)` | Verbose skip reasons (daId → volume label) |
| `GetAllAssignmentsForDatLine(dlId)` | Full map for Verify Archive redundancy (daId, volumeId, label, diskId?) workspace-first |
| `AddVolumeArtifactAndIncrementSize(va, bytes)` | Atomic VA row + size increment after verified copy |

### DatLineStore — key methods added for archive/unwanted

| Method | Purpose |
|---|---|
| `GetAllWantedArtifactInfos()` | All non-unwanted DAs (NOT EXISTS subquery) |
| `GetAllArchiveArtifactInfos()` | All DAs including unwanted (for verify archive) |
| `GetUnwantedArtifactCount()` | Diagnostic: DAs where any linked release is unwanted |
| `UpdateReleaseStatus(id, status)` | Guarded: never touches unwanted |
| `RestoreWantedRelease(id)` | Only allowed exit from unwanted |
| `DeleteDerivedArtifactAndLinks(daId, cik)` | Repair: remove DA + content link |

### incoming-skip

Centralized suspension zone: `incoming-skip\<platform>\`. Written by ingestion (unwanted skip), Verify Archive repair (unwanted/unknown/mismatch/redundant), and manual quarantine. Never scanned by Append or Build Volume. See [INGESTION_PIPELINE.md](INGESTION_PIPELINE.md).

---

## Current Cleanup Status

### Extracted

- `ScreenScraperImportService` — moved from inline `OnCatalogScrape` local code to `Providers/ScreenScraperImportService.cs`. `MainWindow.OnCatalogScrape` now calls `await _scrapeImport.ImportAsync(...)`.

### MainWindow Still Owns

- Dialog orchestration (ScraperProviderDialog, ScrapeReviewDialog, MergeMetadataDialog, EditMetadataDialog)
- Catalog list rendering (RebuildCatalogList)
- Hero panel updates (UpdateCatalogHero, BuildGallery, BuildCoverGallery, BuildExtras, BuildManuals)
- Badge display (SetBadge, TryLoadBadgeIcon, NormalizeBadgeKey)
- Settings persistence (LoadAllSettings, OnSaveSettings, LoadMappingsSettings)
- Status display (SetScrapeStatus)

MainWindow.axaml.cs is ~13,860 lines and is the primary cleanup target.

---

## Future Cleanup Candidates

| Service | Responsibility | Blocker |
|---|---|---|
| `MetadataMergeService` | Apply proposals to canonical metadata outside of dialog | None — pure data |
| `MediaDiscoveryService` | FindCovers, FindGallery, FindExtras, FindManuals | None — wraps MediaStore calls |
| `CatalogBadgeService` | Badge icon loading, normalization, cache | Needs Bitmap; Avalonia init required |
| Provider abstraction | `IScraperProvider` interface for multi-provider support | Requires ScrapeReviewDialog redesign |
| ViewModel layer | MVVM for catalog list and hero | Large refactor; low priority vs. features |

The safest next extraction after `ScreenScraperImportService` is `MetadataMergeService` — it is pure data logic with no UI dependencies and is currently duplicated between the merge dialog and any future bulk-apply path.

---

## ARK Service Stack

All ARK services live in `Catalog/Ark/` and use `namespace Arkadia`.

| Service | Constructor | Responsibility |
|---|---|---|
| `ArkWriterService` | `(string baseDir, CatalogService catalog)` | Write `.ark` ZIP from the current data directory |
| `ArkPackageVerifierService` | `()` | Verify integrity and policy compliance of an `.ark` package |
| `ArkRestorePlanService` | `()` | Dry-run: plan restore without writing anything |
| `ArkRestoreService` | `()` | Execute restore via atomic staging workflow |

**Key invariants:**

| Invariant | Enforced by |
|---|---|
| `CredentialsExcluded=true` in manifest | Verifier (Error) + RestorePlan (blocks) |
| `CachePackagesExcluded=true` in manifest | Verifier (Error) + RestorePlan (blocks) |
| `FormatVersion="0.5"` | RestorePlan (blocks on mismatch) |
| No backslash / absolute / `..` / empty path segments | Verifier + RestorePlan + RestoreService |
| `catalog.db` present in staging before commit | RestoreService (throws on absence) |
| Staging deleted on failure before commit | RestoreService `catch` block (`stagingCommitted` flag) |
| Post-restore "Verify ALL" warning always emitted | RestorePlan.Warnings + RestoreResult.Warnings |

**Database copy:** `ArkWriterService` uses `BackupDatabase()` (SQLite Online Backup API) rather than raw file copy. This is required because Arkadia opens all databases in WAL mode; a raw file copy would produce an inconsistent snapshot.

**Staging directory:** Created as `{parent-of-target}/.ark-restore-{yyyyMMddHHmmss}-{guid8}` so it is on the same filesystem as the target, making `Directory.Move` an atomic rename. The `.ark-restore-` prefix allows test cleanup via `Directory.GetDirectories(parent, ".ark-restore-*")`.

**`stagingCommitted` flag pattern:** Set to `true` only after `Directory.Move(stagingDir, fullTargetPath)` succeeds. The `catch` block deletes staging only when this flag is `false`, preventing accidental deletion of committed data if an exception fires after commit.

**AMP registry restore path:** `registry/amp-packages.json` in the archive maps to `{target}/ark-restore/amp-packages.json`, not the operational registry location. This avoids restoring registry entries that reference paths invalid on the restore machine.

### ARK Backups UI

The Backups sidebar section is the UI entry point for ARK export.

**Folder:** `backups\` — created by `ArkadiaFolders.EnsureCreated` at application startup via `ArkadiaFolders.Backups` constant.

**Layout:** `ViewBackups` is a two-pane grid:
- **BACKUP pane** — Create Backup button + scrollable log window showing timestamped progress lines.
- **RESTORE pane** — `ArkBackupsList` listing `.ark` files from `backups\`, Restore Selected button, Refresh button.

**Backup creation flow:**
1. `ArkExportPlanService.PlanExport()` produces the plan and surfaces any Issues to the log.
2. `ArkWriterService.Write()` runs off the UI thread via `Task.Run`.
3. `ArkPackageVerifierService` is called after writing to verify the package; results are shown in the log.
4. On success the log shows **BACKUP COMPLETE** and `RefreshArkBackupsList()` reloads the RESTORE pane.

**`_arkBusy` flag:** Set `true` for the duration of a backup or restore operation. Disables both Create Backup and Restore Selected while an operation is in progress.

**Live restore — intentionally blocked:** Restore Selected shows an `InfoDialog` explaining the restriction and the selected file path. It does not invoke any restore service. Reason: `CatalogService` and `DatLineStore` use the default SQLite connection pool; replacing `data\` files while connections are alive is unsafe. A restart-safe restore flow is deferred to a future phase.

**Tests:**
- `ArkadiaFoldersTests.EnsureCreated_CreatesBackupsFolder` — verifies `backups\` is created.
- `ArkUiHelpersTests` — covers `SuggestedArkFileName` pattern and `BackupsFolder` helper.
- Current total: **1480 tests**.

---

## Cache & Curation Pipeline

The cache and curation subsystem has its own dedicated reference: [docs/CACHE_CURATION_PIPELINE.md](CACHE_CURATION_PIPELINE.md).

That document covers, with code-backed accuracy:

- ScreenScraper Cache ZIP layout (`manifest.json`, `gameslist.csv`, `payloads/<gameId>.json`, `media/<type>/<file>`)
- Payload sanitization (credential placeholders, `response.ssuser` removal; applied at staging and again at ZIP creation)
- `ScreenScraperCachePackageVerifier` behavior (presence-only manifest check, severity scheme, tolerated extras)
- Provider IDs (`screenscraper` source, `screenscraper-cache` cache provider, UI label "ScreenScraper Cache")
- Media type normalization (`MediaStore.NormalizeMediaType`; `physical-media` → `physical`)
- Official folders: `incoming-media/` created at startup; default source root for the Media Intake Workbench; never auto-deleted
- `release_media_curation` (preferred / excluded / sha256 / credits / notes; auto-preferred rules; Delete vs Exclude semantics)
  - **Exclude**: sets `is_excluded = 1`, stores SHA-256, row persists after file removal, prevents reintroduction
  - **Delete File**: removes file from disk and curation row from DB; does not create exclusion; does not prevent reintroduction; safe on Missing assets (row-only cleanup)
- Manage Media dual-pane workbench (release navigation; grouped asset list; Incoming Media browser; import: copy → SHA-256 verify → curation row → optional source delete)
- `release_extra_notes` (per-release notes; placeholder `No extra notes.`)
- Bulk Scraping behavior (cache-only/offline; `Missing Only` criteria; preservation of curation; cooperative cancellation)
- AMP future direction (planned, not implemented; `.amp` extension; provider-agnostic, no provenance, credits allowed, exclusions included; `.ark` is reserved for Arkadia Backup / Archive, not AMP; chunking post-v1; see docs/SPECS/ARKADIA_MEDIA_PACK_V1_SPEC.md)

Update that document, not this one, when the cache/curation pipeline changes.
