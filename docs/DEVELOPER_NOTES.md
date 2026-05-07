# Arkadia — Developer Notes

---

## Solution / Project Structure

```
Arkadia.sln
├── Arkadia.csproj                  — main application (Avalonia 11 / .NET 8 / Windows)
│   ├── Data/                       — all database and storage logic
│   ├── Library/                    — LibraryEntry, title resolution helpers
│   ├── Providers/                  — scraper clients and import services
│   ├── Ingestion/                  — DAT parsing and ingest pipeline
│   ├── Themes/                     — theme engine, palette slices
│   ├── Systems/                    — system/hardware-family management
│   ├── Volumes/                    — volume lifecycle management
│   ├── Disks/                      — disk registration and management
│   ├── Pending/                    — pending artifact management
│   ├── Staging/                    — staging area operations
│   ├── Dashboard/                  — dashboard view components
│   └── Controls/                   — shared UI controls
└── Arkadia.Tests.csproj            — xUnit tests (~590 tests, no UI dependency)
    ├── Data/                       — store, proposal, mapping, metadata tests
    ├── Providers/                  — scraper parser, import service, candidate tests
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

## Current Cleanup Status

### Extracted

- `ScreenScraperImportService` — moved from inline `OnCatalogScrape` local code to `Providers/ScreenScraperImportService.cs`. `MainWindow.OnCatalogScrape` now calls `await _scrapeImport.ImportAsync(...)`.

### MainWindow Still Owns

- Dialog orchestration (ScraperProviderDialog, ScrapeReviewDialog, MergeMetadataDialog, EditMetadataDialog)
- Catalog list and grid rendering (RebuildCatalogList, RebuildCatalogGrid)
- Hero panel updates (UpdateCatalogHero, BuildGallery, BuildCoverGallery, BuildExtras, BuildManuals)
- Badge display (SetBadge, TryLoadBadgeIcon, NormalizeBadgeKey)
- Settings persistence (LoadAllSettings, OnSaveSettings, LoadMappingsSettings)
- Status display (SetScrapeStatus)

MainWindow.axaml.cs is ~12,800 lines and is the primary cleanup target.

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

## Cache & Curation Pipeline

The cache and curation subsystem has its own dedicated reference: [docs/CACHE_CURATION_PIPELINE.md](CACHE_CURATION_PIPELINE.md).

That document covers, with code-backed accuracy:

- ScreenScraper Cache ZIP layout (`manifest.json`, `gameslist.csv`, `payloads/<gameId>.json`, `media/<type>/<file>`)
- Payload sanitization (credential placeholders, `response.ssuser` removal; applied at staging and again at ZIP creation)
- `ScreenScraperCachePackageVerifier` behavior (presence-only manifest check, severity scheme, tolerated extras)
- Provider IDs (`screenscraper` source, `screenscraper-cache` cache provider, UI label "ScreenScraper Cache")
- Media type normalization (`MediaStore.NormalizeMediaType`; `physical-media` → `physical`)
- `release_media_curation` (preferred / excluded / sha256 / credits / notes; auto-preferred rules)
- `release_extra_notes` (per-release notes; placeholder `No extra notes.`)
- Bulk Scraping behavior (cache-only/offline; `Missing Only` criteria; preservation of curation; cooperative cancellation)
- AMP future direction (planned, not implemented; provider-agnostic, no provenance, credits allowed, exclusions included; `.ark` extension and chunking only planned)

Update that document, not this one, when the cache/curation pipeline changes.
