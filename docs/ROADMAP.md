# Arkadia — Roadmap

This is a near-term development roadmap. Items are roughly ordered by priority.

---

## Near-Term

### 1. Scraper UX Polish

- Status indicators during download (per-category progress, not just phase labels)
- Cancel button during active scrape
- Better empty-result and error messaging in ScrapeReviewDialog
- Keyboard navigation in candidate list

### 2. Scrape from Cached Payload

Re-run the import pipeline using a previously saved provider JSON payload without making a new network request. Useful for re-applying metadata after mapping rules change, or recovering from a failed merge.

Implementation: add a "Re-scrape from cache" action that calls `ScreenScraperImportService.ImportAsync` with the stored `release_provider_payloads` JSON rather than fetching from the API.

### 3. Bulk Scrape Review Queue

Scrape multiple releases in sequence without interactive candidate selection:

- Queue up all un-scraped releases for a DAT line
- For each: attempt exact ROM match, fall back to best title match above a confidence threshold
- Present a review queue showing proposed metadata for all releases
- User approves/rejects/skips per release before any writes

### 4. Code Cleanup / Service Extraction

Continue the service extraction started with `ScreenScraperImportService`:

- Extract `MetadataMergeService` — apply proposals to canonical metadata (pure data, no UI)
- Extract `MediaDiscoveryService` — discovery wrappers for gallery/cover/extras/manuals
- Reduce `MainWindow.axaml.cs` (~12,800 lines) to UI orchestration only

### 5. Badge Icon Assets

Add the badge icon PNG files for region, system, status, and type to `themes/visual/default/badges/`. Currently most badges show text only because the icon files are missing. The infrastructure (`TryLoadBadgeIcon`, `NormalizeBadgeKey`) is already in place.

### 6. Additional Providers

Add support for at least one more metadata provider alongside ScreenScraper:

- LaunchBox Games Database (LGDB)
- MobyGames

Requires: abstract `IScraperProvider` interface, provider-specific client implementation, provider selection in `ScraperProviderDialog`.

---

## Medium-Term

- Export / Build Set: generate a distributable ROM set from verified archive content
- Collection statistics and analytics improvements
- Volume health dashboard
- Batch integrity verification across all systems

---

## Long-Term / Speculative

- Cross-platform support (Linux / macOS via Avalonia headless or native)
- Plugin-based provider system
- Web UI companion for remote browsing
