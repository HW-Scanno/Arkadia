# Arkadia Cache & Curation Pipeline Milestone

**Date:** 2026-05-07

**Test status:**
- 927/927 tests passing
- 0 warnings
- 0 errors

**Documentation status:**
- `docs/CACHE_CURATION_PIPELINE.md` created and linked from `README.md`, `docs/USER_MANUAL.md`, and `docs/DEVELOPER_NOTES.md`.
- `docs/QA/CACHE_CURATION_REAL_WORLD_TEST_PLAN.md` created and linked from `docs/CACHE_CURATION_PIPELINE.md`.

---

## Implemented pipeline

- ScreenScraper settings with mandatory `softname`
- Official folders:
  - `incoming-csv/`
  - `scrape-cache/`
  - `scrape-cache/screenscraper/`
  - `staging-cache/`
  - `staging-cache/screenscraper/`
- ScreenScraper Cache Builder
- Sanitized payloads
- `response.ssuser` removal
- `UpdatePayloads` (re-fetch + sanitized JSON comparison; only changed payloads overwritten; only new/missing media re-downloaded)
- `Force` rebuild (overwrites every staged payload and media file)
- staging-cache management (Manage Staging dialog with Complete / Resumable / Unknown / Empty status)
- Dashboard SCRAPE STAGING Top 5 (sorted by disk size, informational)
- Registered cache package manager (Register / Verify / Detach / Delete File + Detach / Refresh)
- Verify Package (manifest presence, gameslist cross-checks, payload validation, media existence, sanitization audit)
- Offline single scrape through ScreenScraper Cache
- Bulk Scraping through ScreenScraper Cache
- Manage Media (per-release dialog with preview, set preferred, exclude, restore, save credits, open file/folder, delete file)
- Media curation:
  - preferred
  - excluded
  - SHA-256 excluded hash persistence
  - credits
  - delete-after-exclude safety
  - Add Media (embedded panel, file picker, media-type prompt)
- Extra Notes (per-release notes, placeholder `No extra notes.`)
- Provider constants cleanup
- ScreenScraper cache ZIP layout constants
- Media type normalization:
  - canonical `physical`
  - incoming alias `physical-media`
- `SetPreferred` atomic transaction
- `AddMediaFile` cleanup on DB failure
- Cancellation mid-loop test for Bulk Scraping

---

## Important invariants

- Offline single scrape and Bulk Scraping use registered cache only.
- Bulk Scraping makes no online ScreenScraper calls.
- Existing non-empty canonical metadata is not overwritten in default bulk mode.
- Extra Notes are never overwritten by provider import/merge/bulk.
- Media credits, preferred media, excluded media, and curation rows are preserved by bulk.
- Excluded media hashes prevent rejected assets from being reintroduced.
- Provider/source provenance is not exposed in Manage Media or Arkadia-facing identity.
- Credits are curated attribution, not provider provenance.
- ScreenScraper Cache ZIPs are bootstrap material, not final AMP packages.
- AMP/`.ark` is planned only and not implemented.

---

## Known intentional non-implemented items

- AMP export/import
- `.ark` container / chunking
- AMP downloader
- Online fallback for Bulk Scraping
- Exclude Reason dialog
- Image dimensions display
- Video duration display
- Suspicious tiny-file verification heuristic
- Library view display for Extra Notes

---

## Next recommended steps

1. Return to Sonnet for code/test execution work.
2. Run the real-world manual test plan on a small platform such as Atomiswave.
3. Fix any real-world bugs discovered.
4. Polish Catalog center column UI, including physical media border removal.
5. Start formal AMP/`.ark` specification only after real tests pass.
