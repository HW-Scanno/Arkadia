# Arkadia Cache & Curation Pipeline Milestone

**Date:** 2026-05-08

**Test status:**
- 971/971 tests passing
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
  - `incoming-media/` _(default source folder for manual media intake; never auto-deleted by Arkadia)_
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
- Manage Media — dual-pane Media Intake Workbench:
  - release navigation (Previous / Next without closing the dialog)
  - asset list grouped by media type in the All view
  - detail panel with preview, credits editor, and status badges
  - Incoming Media right pane (default: `incoming-media/`; Browse to any folder)
  - safe manual import: copy → source SHA-256 → destination SHA-256 → verify match → curation row → optional source delete
  - source file never deleted before successful verification
- Media curation:
  - preferred
  - excluded (SHA-256 stored; exclusion row persists after file is removed from disk)
  - credits
  - Delete File semantics: removes file from disk and curation row from DB; does **not** create an exclusion; does not prevent future reintroduction
  - Delete File on Missing asset: removes curation row only; no filesystem action
  - Missing/Excluded row cleanup: Delete File removes the exclusion record; asset may then be reintroduced
- Preview safety: missing/corrupt files show a placeholder; preview loading never throws
- Catalog Grid view removed (list-only release column)
- Extra Notes (per-release notes, placeholder `No extra notes.`)
- Provider constants cleanup
- ScreenScraper cache ZIP layout constants
- Media type normalization:
  - canonical `physical`
  - incoming alias `physical-media`
- `SetPreferred` atomic transaction
- Cancellation mid-loop test for Bulk Scraping

---

## Important invariants

- Offline single scrape and Bulk Scraping use registered cache only.
- Bulk Scraping makes no online ScreenScraper calls.
- Existing non-empty canonical metadata is not overwritten in default bulk mode.
- Extra Notes are never overwritten by provider import/merge/bulk.
- Media credits, preferred media, excluded media, and curation rows are preserved by bulk.
- **Exclude remembers; Delete forgets.** Exclude stores a SHA-256 hash and prevents reintroduction. Delete File removes the file and curation row without creating an exclusion — a future scrape or import may bring the asset back.
- **Delete on Missing is safe.** Delete File on a Missing asset removes only the curation row; no filesystem action is taken.
- **Import verification before commit.** A curation row is created only after source and destination SHA-256 hashes match. Source files are never deleted before successful verification.
- Excluded media hashes prevent rejected assets from being reintroduced (when "Respect excluded media" is on).
- Provider/source provenance is not exposed in Manage Media or Arkadia-facing identity.
- Credits are curated attribution, not provider provenance.
- ScreenScraper Cache ZIPs are bootstrap material, not final AMP packages.
- AMP/`.amp` is planned only and not implemented. `.ark` is reserved for Arkadia Backup / Archive.

---

## Known intentional non-implemented items

- AMP export/import
- `.amp` export / import (AMP container; chunking post-v1)
- AMP downloader
- Online fallback for Bulk Scraping
- Exclude Reason dialog
- Image dimensions display
- Video duration display
- Suspicious tiny-file verification heuristic
- Library view display for Extra Notes

---

## Next recommended steps

1. Run the real-world manual test plan on a small platform such as Atomiswave.
2. Fix any real-world bugs discovered.
3. Polish Catalog center column UI, including physical media border removal.
4. AMP/`.amp` specification is now written (docs/SPECS/ARKADIA_MEDIA_PACK_V1_SPEC.md). Implement after real-world QA passes.
