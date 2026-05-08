# Arkadia Cache & Curation Pipeline

_Last revised: 2026-05-08. Scope: Arkadia desktop app (Avalonia 11 / .NET 8). This document describes the cache and curation pipeline as currently implemented, separating shipped behavior from planned future work._

---

## 1. Overview

Arkadia builds and consumes self-contained ZIP **cache packages** that bundle metadata payloads and media for a given DAT line. Packages are produced by the **ScreenScraper Cache Builder**, registered into the local catalog, and consumed offline by the rest of the application.

Once a package is registered, all metadata application, media extraction, single-release scraping, and bulk scraping run against the local cache. No online ScreenScraper calls are made during catalog use; online traffic happens only inside the Cache Builder itself.

Curation lives on top of the cache: per-release **Manage Media**, **Extra Notes**, **Credits**, **Preferred** and **Excluded** flags, all stored in the per-DAT-line SQLite database.

---

## 2. Current pipeline

```
ScreenScraper API
       │
       ▼
Cache Builder ── staging-cache/screenscraper/<package>/
       │           ├── manifest.json
       │           ├── gameslist.csv
       │           ├── payloads/<gameId>.json
       │           └── media/<type>/<file>
       ▼
ZIP package ── scrape-cache/screenscraper/<package>.zip
       │
       ▼
Registered Cache Manager ── catalog DB index
       │
       ▼
Offline consumers:
  • Single-release scrape (offline)
  • Bulk scraping (cache-only)
  • Manage Media / Extra Notes / Credits
```

The provider is shown in the UI as **"ScreenScraper Cache"**; its internal provider ID is `screenscraper-cache`. The upstream source provider ID is `screenscraper`.

---

## 3. Official folders

Folders are created under the application/base directory (`AppContext.BaseDirectory`) at startup, except provider-specific staging subfolders which may be created on demand.

| Folder | Purpose | Created |
|---|---|---|
| `incoming-csv/` | DAT/CSV imports awaiting ingestion | At startup |
| `incoming-media/` | Default source folder for manual media intake via the Media Intake Workbench | At startup |
| `scrape-cache/` | Root for built cache ZIPs | At startup |
| `scrape-cache/screenscraper/` | Default output location for ScreenScraper Cache ZIPs | At startup |
| `staging-cache/` | Root for in-progress builds | At startup |
| `staging-cache/screenscraper/` | Per-package staging folders for the ScreenScraper builder | On demand |

`incoming-media/` is the default root that the Media Intake Workbench points to on first open. Arkadia never deletes files from `incoming-media/` automatically — source deletion happens only when the user explicitly enables **Delete after import**, and only after a successful copy and SHA-256 verification.

Helper text in dialogs reads "Relative paths resolve from the application directory."

---

## 4. ScreenScraper settings

Settings stored per-user:

- `devid`, `devpassword` — developer credentials
- `ssid`, `sspassword` — user credentials
- `softname` — software identification token

These values are never persisted into payloads. Both at staging time and again at ZIP-creation time, the builder runs payload sanitization (see **§13 Security and privacy**).

---

## 5. ScreenScraper Cache Builder

The Cache Builder constructs a single ScreenScraper Cache ZIP for one DAT line.

### Inputs

- DAT line / system selection
- Package name (sanitized → file name)
- Output ZIP path (default: `scrape-cache/screenscraper/<package-name>.zip`)
- Staging root (default: `staging-cache/`)

### Build options (UI checkboxes)

| Checkbox | Flag | Default | Behavior |
|---|---|---|---|
| Force rebuild / refetch (overwrite existing staged files) | `Force` | off | Force rebuild overwrites every staged payload and media file. Skips the already-built guard. Independent of UpdatePayloads. |
| Re-check existing payloads for updates (re-fetches to compare; only re-downloads changed media) | `UpdatePayloads` | off | Re-checks payloads by re-fetching from the API and comparing sanitized JSON. Only payloads whose JSON has changed are overwritten; only new/missing media are re-downloaded. |
| Keep staging folder after successful build | `KeepStaging` | on | If off, the staging folder is deleted after a successful ZIP. |
| Index package in catalog after build | `IndexAfterBuild` | on | If on, the resulting ZIP is automatically registered. |

### Output: ScreenScraper Cache ZIP layout

```
manifest.json
gameslist.csv
payloads/<gameId>.json
media/<type>/<file>
```

`manifest.json` records: `version`, `provider` (= `screenscraper`), `cacheProviderId` (= `screenscraper-cache`), `systemId`, `systemName`, `builtAtUtc`, `gameCount`, `payloadCount`, `mediaCount`, `mediaCountByType`, `mediaTypes`.

The ZIP is only finalized when the build is complete (all expected payloads present, no rate or safety limits hit). Otherwise the staging folder is left in place for resumption.

---

## 6. Manage Staging

The Manage Staging dialog enumerates `staging-cache/screenscraper/<package>/` folders and exposes per-folder actions.

### Status labels

Exactly four labels are used:

- **Complete** — `payloadCount >= totalGames` and `totalGames > 0`
- **Resumable** — `totalGames > 0` and `payloadCount < totalGames`
- **Unknown** — `totalGames == 0` and `payloadCount == 0`, but files are present
- **Empty** — `totalGames == 0` and `payloadCount == 0`, no files

### Completion percentage

Completion % counts payloads against expected game count; media files are tracked separately.

```
percent = payloadCount / totalGames * 100
```

### Default sort and dashboard tile

The Manage Staging list is sorted by **last updated descending**, with size as a tiebreaker. The dashboard SCRAPE STAGING tile shows the five largest staging folders by disk size. The dashboard tile is informational; manage them via the Manage Staging dialog.

### Actions

- **Open Folder** — opens the staging folder in the OS file manager.
- **Delete** — removes the staging folder after confirmation. The deletion is path-traversal-guarded: only direct children of the staging provider root can be deleted. Completed cache ZIPs are not affected.

---

## 7. Registered Cache Manager

Lists ZIP packages currently registered in the catalog.

### Columns and status

Each row exposes the package file name, the indexed game count, and a status (`Available`, `Missing`, etc.). Status reflects whether the underlying ZIP is reachable on disk.

### Actions

- **Register Package** — opens a file picker for a `.zip`, indexes it, reports `<n> games, <m> media entries` or "Package already registered." Re-registration of the same path is a no-op.
- **Verify** — runs **§8 Verify Package** for the selected row. Enabled only for `Available` packages.
- **Detach** — removes the package from the catalog index; the ZIP file is left on disk.
- **Delete File + Detach** — deletes the ZIP from disk and detaches it. Disabled for `Missing` packages.
- **Refresh** — re-reads the registered package list and file availability/status from the catalog DB/filesystem. It does not re-index ZIP contents.

---

## 8. Verify Package

Verify performs read-only validation of a registered package and produces a textual report.

### What is checked

- ZIP can be opened.
- `manifest.json` exists. (Verify checks that `manifest.json` exists. It does not validate individual manifest fields.)
- `gameslist.csv` exists.
- `gameslist.csv` row count vs `cache_packages.game_count` (cross-check).
- `cache_packages.game_count` vs actual `cache_package_games` row count.
- For each indexed game with `has_payload = 1`:
  - Payload entry exists in the ZIP.
  - Payload is non-zero.
  - Payload is valid JSON and contains `response.jeu`.
  - `jeu.id` matches the expected game ID.
  - No unsanitized credential query params (literal and `\u0026`-escaped forms).
  - No `response.ssuser` present.
- For each indexed media entry: file exists in the ZIP and is non-zero.

### Severity scheme

| Condition | Severity |
|---|---|
| File missing on disk | Error |
| ZIP unreadable | Error |
| `manifest.json` missing | Error |
| `gameslist.csv` missing | Error |
| Missing expected payload | **Warning** |
| Missing indexed media | **Warning** |
| Zero-byte payload or media | Error |
| Payload not valid JSON | Error |
| Payload missing `response.jeu` | Error |
| `jeu.id` mismatch | Warning |
| `gameslist.csv` row count vs index mismatch | Warning |
| `game_count` vs actual rows mismatch | Warning |
| Unsanitized credential param | Error |
| `response.ssuser` present | Error |

### Tolerances

Verify tolerates extra files in the ZIP that are not indexed. They are simply not checked.

---

## 9. Offline Single Scrape

Triggered from a single library entry. Uses only the registered cache; no online calls.

- Looks up the release in the catalog cache index by name and DAT line.
- If a single match is found, builds proposals from the cached payload.
- Proposals are presented to the user; the user accepts or rejects per field. Curation rules apply (see §14).
- Media listed in the cached payload can be extracted into the on-disk media tree.

If the cache index has no match, single scrape reports no result. There is no online fallback.

---

## 10. Bulk Scraping

Cache-only and offline. Bulk Scraping makes no online ScreenScraper calls.

### Scope

- **Current Release** — the currently selected release.
- **Missing Only** — releases that are not yet "complete" (see below).
- **Entire DAT** — every release in the current DAT line.

"Missing Only" includes releases whose quality score is below 6 or which lack a cover-front image on disk. Specifically a release counts as **complete** when:

- `Metadata.QualityScore >= 6`, AND
- at least one cover-front file exists at `data/media/<hardwareFamilyId>/<datLineId>/covers-front/<release-stem>_*`.

### Options

- **Auto-apply empty fields only** — only fill canonical metadata fields that are currently empty.
- **Extract missing media** — extract any media listed in the payload that is not already on disk.
- **Respect excluded media** — never re-extract assets the user has excluded.
- **Overwrite existing media** — overwrite media files already on disk. The implementation skips already-present files before writing rather than relying solely on `FileMode.Create`.

### Per-release status

| Status | Meaning |
|---|---|
| Matched | Single cache match found; proposals/media applied per options. |
| No Match | Cache index has no entry for this release. |
| Ambiguous | More than one cache candidate; **no data is applied or saved** as a safety rule. |
| Error | Exception during processing. |

### Curation preservation

Bulk Scraping preserves:

- Extra Notes
- Media Credits
- Preferred media flags
- Exclusions
- Non-empty canonical metadata (when "Auto-apply empty fields only" is on)

### UI cancellation

Stop cancels the run cooperatively. Internally, `CancellationTokenSource.Cancel()` is called and `RunAsync` throws `OperationCanceledException`. The dialog updates the label to `Stopped.` and hides the Stop button. Already-applied changes from completed releases are kept.

### Parsed metadata fields

Authoritative list of canonical fields proposed by the cache importer:

- `title`
- `original_title`
- `developer`
- `publisher`
- `year`
- `languages`
- `description`
- `genre`
- `subgenre`
- `players`
- `rating`

---

## 11. Manage Media — Media Intake Workbench

Per-release dual-pane workbench for reviewing, curating, and importing media assets. Open it from the Catalog by selecting a release and clicking **Manage Media**.

The dialog header includes **Previous / Next** release navigation so you can move between releases without closing and reopening the window.

### Left pane — current release media

The left pane lists every known media asset for the selected release across all media types.

- A **media type filter** at the top narrows the list to a single type. The default view (**All**) groups assets by media type with subtle section headers.
- Selecting an asset shows its details in the center panel: file name, media type, file size, status, SHA-256 (when available), a preview, and a credits editor.
- Status badge colors: `Preferred` (green), `Excluded` (red), `Missing` (orange).

### What is not shown

Manage Media does not expose provider or source provenance. Where the asset came from (online ScreenScraper, cache, manual import) is not surfaced in this dialog.

### Curation actions on a selected asset

- **Set Preferred** — enabled only when the asset exists, is not already preferred, and is not excluded.
- **Exclude** — marks the asset as rejected. Computes and stores SHA-256 (if the file exists) so future scrapes/imports can recognize and skip the rejected file. The exclusion row persists even if the file is later deleted from disk. Exclusions are recorded without a reason.
- **Restore** — clears an exclusion.
- **Save Credits** — persists a Credits string for the asset. Credits are curated attribution, not provider provenance.
- **Open File / Open Folder** — OS-level open / reveal-in-folder. Enabled only for existing files.
- **Delete File** — removes the file and its curation row. See **Delete vs Exclude** below.

### Right pane — Incoming Media browser

The right pane browses a source folder for files to import into Arkadia.

- Default root: `incoming-media/` (see §3).
- Use **Browse…** to point the browser at any other folder.
- Selecting a file shows a preview (image / video placeholder / PDF placeholder / generic). Missing or unreadable files show a placeholder; preview loading never throws.
- The **target media type** selector controls which media type the imported file is assigned. Options come from `ReleaseMediaCurationService.MediaTypeOrder`.
- **Delete after import** — when checked, the source file is deleted after a successful import. When unchecked, the source file is left in place.
- **Import** — starts the import workflow described below.

### Manual import workflow

When you click **Import**, Arkadia:

1. Validates the selected release, source file, and target media type.
2. Computes the source file SHA-256.
3. Copies the source file to `data/media/<hardwareFamilyId>/<datLineId>/<media-type>/` using the canonical release stem naming.
4. Computes the destination SHA-256.
5. Verifies source hash matches destination hash.
6. Creates the curation row **only after verification succeeds**.
7. Refreshes the current release media list.
8. If **Delete after import** was enabled: deletes the source file only after all of the above has succeeded.

Source files are never deleted before successful verification. A failed verification leaves no curation row; the destination file may remain for inspection.

### Delete vs Exclude

These are distinct actions with different intent and different effects on the DB.

**Exclude** is a curator rejection action:
- Computes and stores the asset SHA-256 (if the file exists on disk).
- Sets `is_excluded = 1` in the `release_media_curation` row.
- The exclusion row persists even after the file is removed from disk — the exclusion is a decision, not a file pointer.
- Excluded hashes prevent the asset from being reintroduced by future scrapes, imports, or bulk runs (when "Respect excluded media" is on).
- Use Exclude when you want Arkadia to remember a rejected asset and prevent it from coming back.

**Delete File** is a local cleanup action:
- Does **not** create or update an exclusion.
- Does **not** compute or store SHA-256.
- Does **not** prevent future reintroduction.
- If the file exists on disk: deletes the file first, then removes the curation row from DB. If the file deletion fails, the DB row is left intact.
- If the asset is **Missing** (file already absent from disk): removes only the curation row from DB; no filesystem action.
- If the asset is **Missing/Excluded**: removing it deletes the exclusion record too; the asset may be reintroduced by future scrapes or imports.
- Use Delete File when you only want to remove the local file and/or record.

### `physical-media` alias

ScreenScraper's `physical-media` alias is normalized to `physical` at import; you should never see `physical-media` in the catalog DB or Manage Media UI. The string remains in the builder/source path because that is what ScreenScraper emits, and DAT-line SQL migrations rewrite legacy rows that may have been stored before normalization existed.

---

## 12. Extra Notes

Per-release free-text notes stored in `release_extra_notes`.

- Single text field with the placeholder **"No extra notes."** when empty.
- Saved on Save; preserved by Bulk Scraping.
- Independent of media Credits (which are per-asset, not per-release).

---

## 13. Security and privacy

### Payload sanitization

Two layers, both implemented:

- **At staging time** — `ScreenScraperPayloadSanitizer.SanitizeJson` is applied to every payload when it lands in the staging folder.
- **At ZIP creation** — payloads are re-sanitized when written into the ZIP (defense in depth).

Sanitization rules:

- Credential query params (`devid`, `devpassword`, `ssid`, `sspassword`, `softname`) — replaced with placeholders (`<DEVID>`, `<DEVPASSWORD>`, `<SSID>`, `<SSPASSWORD>`, `<SOFTNAME>`). This includes the `\u0026`-escaped form produced by `JsonSerializer`.
- `response.ssuser` — removed.

The Verify Package report flags any leakage of credentials or `ssuser` as Error severity.

### What is safe to share

A built and verified ScreenScraper Cache ZIP contains no credentials and no `ssuser` block. It still contains payload metadata and media as returned by ScreenScraper, so usual licensing/attribution rules apply.

---

## 14. Curation principles

- **User-facing curation is sticky.** Excluded assets, preferred selections, credits, and extra notes survive re-imports and bulk runs.
- **Auto-preferred is conservative.** New media imported when no existing row is excluded for that type may be auto-set as preferred. If any row for that media type is excluded, the new file is not auto-promoted.
- **Bulk does not regress.** "Auto-apply empty fields only" prevents bulk scraping from overwriting existing canonical metadata.
- **Ambiguous matches do nothing.** A cache match with multiple candidates is recorded as Ambiguous; nothing is applied, nothing is saved.
- **Exclude remembers; Delete forgets.** Exclude stores a SHA-256 hash and prevents the asset from being reintroduced. Delete File removes the file and curation row without creating an exclusion — a future scrape or import may bring the asset back.
- **Delete on Missing is safe.** Clicking Delete File on a Missing asset (file already absent from disk) removes only the curation row; no filesystem action is taken.
- **Provenance vs attribution.** Provider provenance is not surfaced in Manage Media. Credits is a curated attribution string, not a record of where the file came from.
- **Import verification before commit.** A curation row is only created after source and destination SHA-256 hashes match; source files are never deleted before successful verification.
- **Normalization is centralized.** `MediaStore.NormalizeMediaType` is the single authority for media type aliases (e.g. `physical-media` → `physical`). UI layers should never need to know about aliases.

---

## 15. Troubleshooting

| Symptom | Likely cause | Action |
|---|---|---|
| Cache Builder finishes but no ZIP appears | Build did not reach completion (rate limit, error, or unfetched payloads). Staging folder is kept for resumption. | Reopen the builder for the same package; it will resume. Or use Manage Staging to inspect/clean. |
| Manage Staging shows **Resumable** | Some payloads are not yet downloaded. | Reopen the builder to continue. |
| Manage Staging shows **Unknown** | Folder has files but no readable game count. | Inspect folder contents; consider deleting if it is junk. |
| Verify reports unsanitized credentials | A payload was written before sanitization, or sanitization was skipped. | Rebuild with Force; the second sanitization pass at ZIP write should catch any survivors. Treat the ZIP as not safe to share until clean. |
| Bulk Scraping reports many `No Match` | Cache index does not contain those releases. | Confirm the registered package is for the correct DAT line and was built from a sufficiently complete game list. |
| Bulk Scraping reports `Ambiguous` | Multiple cache candidates for one release. | Resolve via single-release scrape; bulk intentionally does not guess. |
| `physical-media` appears anywhere in catalog DB or Manage Media | Pre-normalization legacy data, or a regression. | Re-run DAT-line migrations; if it persists in the UI, file as a bug. |
| Registered Cache Manager shows `Missing` | ZIP file moved or deleted out-of-band. | Use **Detach** to clean up, or restore the file. |

---

## 16. Manual test plan

A real-world checklist for verifying the cache/curation pipeline. Run on a clean working copy or a snapshot you can roll back.

> The expanded, official QA checklist (with per-step Pass/Fail boxes, edge cases, acceptance criteria, and a failure-log template) lives in [docs/QA/CACHE_CURATION_REAL_WORLD_TEST_PLAN.md](QA/CACHE_CURATION_REAL_WORLD_TEST_PLAN.md). The summary below stays here as a lightweight reference; use the QA document for actual test passes.

### 16.1 Setup

- **Data needed:** valid ScreenScraper credentials, one DAT line with at least ~20 entries, one DAT line with no expected matches.
- **Expected baseline:** clean `incoming-csv/`, `incoming-media/`, `scrape-cache/`, `staging-cache/` under the application directory.

### 16.2 Settings

1. Open Settings → ScreenScraper.
2. Enter `devid`, `devpassword`, `ssid`, `sspassword`, `softname`.
3. Save. **Expected:** values persist across restart.

### 16.3 Cache Builder — happy path

1. Open Cache Builder, pick the small DAT line, give a package name.
2. Leave defaults: Force off, UpdatePayloads off, KeepStaging on, IndexAfterBuild on.
3. Start. **Expected:** progress advances; on completion a ZIP is written under `scrape-cache/screenscraper/<name>.zip` and the package appears in Registered Cache Manager.
4. Open the ZIP externally. **Expected:** layout is `manifest.json`, `gameslist.csv`, `payloads/`, `media/`.

### 16.4 Resumable build

1. Build a package; interrupt before completion (close app or kill).
2. Reopen Manage Staging. **Expected:** the package shows **Resumable** with a sensible completion %.
3. Reopen Cache Builder for the same package; resume. **Expected:** previously fetched payloads are reused; only remaining payloads are fetched; ZIP is produced.

### 16.5 Force rebuild

1. Re-run a built package with **Force rebuild** on.
2. **Expected:** every staged payload is overwritten; existing media files are removed and re-downloaded; sanitization remains correct.

### 16.6 UpdatePayloads

1. Re-run a built package with **UpdatePayloads** on, **Force** off.
2. **Expected:** payloads are re-fetched and compared after sanitization. Unchanged payloads are reused. Changed payloads are overwritten and any new/missing media for them is downloaded; existing media is not re-downloaded.

### 16.7 Verify Package

1. Verify a freshly built ZIP. **Expected:** Status `Valid`, no errors.
2. Manually corrupt one payload (e.g. truncate to zero bytes) and re-Verify. **Expected:** Error severity for that payload; overall Status `Error`.
3. Inject a credential into a payload and re-Verify. **Expected:** Error.
4. Add an extra unrelated file inside the ZIP. **Expected:** Verify still passes (extra files tolerated).

### 16.8 Registered Cache Manager

1. **Refresh.** Expected: re-reads the package list and file availability/status from the catalog DB/filesystem; does not re-index ZIP contents.
2. Move the ZIP file out of the folder. Refresh. **Expected:** status flips to `Missing`; Verify and Delete actions are disabled.
3. **Detach** a package. **Expected:** removed from the index; file remains.
4. **Delete File + Detach.** Expected: file removed from disk; entry removed from index.

### 16.9 Offline Single Scrape

1. Pick a release with a known cache match. Run single scrape.
2. **Expected:** proposals appear; accepting writes only the canonical fields you accept.
3. Pick a release with no cache match. **Expected:** no result, no error.

### 16.10 Bulk Scraping

1. Run **Current Release**. **Expected:** report shows 1 entry processed.
2. Run **Missing Only**. **Expected:** count equals the number of releases with `QualityScore < 6` or no cover-front file on disk.
3. Run **Entire DAT** with "Auto-apply empty fields only" on. **Expected:** existing canonical metadata is preserved; only empty fields are filled.
4. Pre-set one release's media as Preferred and another as Excluded. Run with "Respect excluded media" on. **Expected:** Preferred remains, Excluded is not re-extracted.
5. Set an Extra Note and save Credits on a media asset. Run bulk. **Expected:** Extra Notes and Credits remain unchanged.
6. Trigger a multi-candidate match. **Expected:** status `Ambiguous`; no proposals/metadata written for that release.
7. Press **Stop** mid-run. **Expected:** label reads `Stopped.`, Stop button hides; results from completed releases remain applied.

### 16.11 Manage Media

1. Open Manage Media for a release with several assets. **Expected:** dual-pane workbench opens; asset list, detail panel, preview, and badges render correctly; release navigation header is visible.
2. Use **Previous / Next** to switch releases without closing the dialog. **Expected:** media list updates.
3. **Set Preferred** on an asset. Pick another and set it Preferred. **Expected:** previous selection is no longer Preferred for that media type.
4. **Exclude** an asset. **Expected:** badge becomes `Excluded`; Restore is enabled, Set Preferred is disabled. No reason prompt appears.
5. **Restore.** Expected: asset returns to default status.
6. **Save Credits** on an asset. **Expected:** persists across reload.
7. **Delete File** on an existing active asset. **Expected:** file removed from disk; curation row removed from DB; asset disappears from list. No exclusion row is created.
8. **Delete File** on a Missing asset (file already absent). **Expected:** curation row removed from DB; no filesystem action; asset disappears from list.
9. Place files in `incoming-media/`. Open Manage Media right pane. **Expected:** files listed.
10. Select a file and click **Import** (delete-after unchecked). **Expected:** file imported into the canonical media tree; source file remains in `incoming-media/`.
11. Select a file and click **Import** with **Delete after import** checked. **Expected:** file imported; source file deleted only after successful copy+verify.
12. Confirm `physical-media` never appears in the UI; canonical type is `physical`.

### 16.12 Extra Notes

1. Open a release with no notes. **Expected:** placeholder reads exactly `No extra notes.`
2. Save a note. Reopen. **Expected:** note persists.
3. Run Bulk Scraping. **Expected:** the saved note is unchanged afterward.

### 16.13 Security smoke test

1. Open a built ZIP and grep payloads for `devid=`, `ssid=`, `softname=`, and `"ssuser"`. **Expected:** no real credential values; only `<DEVID>`, `<DEVPASSWORD>`, `<SSID>`, `<SSPASSWORD>`, `<SOFTNAME>` placeholders, and no `ssuser` block.
2. Run Verify on the same ZIP. **Expected:** clean.

---

## 17. Future: Arkadia Media Package (AMP)

**Status: planned. Not implemented.** No AMP/`.amp` code exists in the current build. See [docs/SPECS/ARKADIA_MEDIA_PACK_V1_SPEC.md](SPECS/ARKADIA_MEDIA_PACK_V1_SPEC.md) for the full specification.

### Intent

AMP is intended as a provider-agnostic, redistributable package format for curated media plus metadata. The goals are:

- **Provider-agnostic** — AMP packages do not encode "this came from ScreenScraper" or any other provider; consumers should not be able to read provenance from the format.
- **No visible provider provenance** — the format intentionally does not surface provider IDs; canonical metadata, media, and curation are what flow through.
- **Credits allowed** — curated attribution travels with assets.
- **Exclusions included** — excluded items are part of the package so curation decisions are portable, not silently lost.
- **`.amp` extension** — the official package extension. `.ark` is reserved for Arkadia Backup / Archive and must not be used for AMP. Chunking is planned post-v1.

### Out of scope for this document

The on-disk AMP layout, chunk format, signing, and import/export tooling are not yet specified or implemented and are not described here. Until AMP ships, the ScreenScraper Cache ZIP described in §5 remains the only cache package format.

---

## Revision notes

- 2026-05-08 — Media Intake Workbench and curation semantics update:
  - **§3 Folders**: added `incoming-media/` with notes on auto-creation and no-auto-delete policy.
  - **§11 Manage Media**: full rewrite as dual-pane Media Intake Workbench. Documents release navigation, grouped asset list, incoming media browser, safe import workflow (copy → SHA-256 verify → curation row → optional source delete), and the Delete vs Exclude distinction. Removed the old "Add Media embedded panel" description (replaced by Incoming Media import).
  - **§14 Curation principles**: added "Exclude remembers; Delete forgets", "Delete on Missing is safe", and "Import verification before commit" bullets.
  - **§16.1 Setup**: added `incoming-media/` to expected baseline.
  - **§16.11 Manage Media**: corrected Delete File steps; added Missing asset delete; added Incoming Media / Import steps; added release navigation step.

- 2026-05-07 — Final pass after targeted read-only code audit. Corrections applied:
  - **§3 Folders**: clarified root is `AppContext.BaseDirectory`; `staging-cache/screenscraper/` is created on demand.
  - **§5 Cache Builder**: documented `UpdatePayloads` as implemented (sanitized JSON comparison; only changed payloads overwritten; only new/missing media re-downloaded). Documented `Force` as overwrite of every staged payload and media file, independent of UpdatePayloads.
  - **§6 Manage Staging**: status labels locked to **Complete / Resumable / Unknown / Empty**. Completion % defined as payloads-only. Dashboard Top 5 documented as sorted by disk size and informational; actions live in the Manage Staging dialog.
  - **§7 Registered Cache Manager**: clarified Refresh re-reads list/status, does not re-index ZIPs.
  - **§8 Verify Package**: scoped manifest check to **presence only** (no field-level validation). Missing payload and missing media moved to **Warning**; zero-byte / invalid JSON / missing `response.jeu` / unsanitized credentials / `ssuser` are **Error**. Verify tolerates extra files.
  - **§10 Bulk Scraping**: Missing Only criteria stated as `QualityScore < 6` OR no cover-front file. UI cancellation behavior documented (`Stopped.` label, Stop hides, partial results retained). Authoritative parsed-fields list documented.
  - **§11 Manage Media**: Add Media documented as embedded panel using `ReleaseMediaCurationService.MediaTypeOrder`; no region selector. Exclude reason documented as currently null in the UI. `physical-media` normalization documented.
  - **§17 AMP**: documented strictly as planned/not implemented; no claims of partial code.

## Remaining open questions

- None currently identified.
