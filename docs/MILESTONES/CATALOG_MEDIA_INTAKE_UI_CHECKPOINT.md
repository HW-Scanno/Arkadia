# Catalog / Media Intake UI — Checkpoint

**Date:** 2026-05-08
**Build:** clean — 0 errors, 0 warnings
**Tests:** 975 / 975 passing

---

## What is stable

### Catalog view

- Grid view removed. Catalog is list-only.
- Hero panel uses a compact label/value metadata grid (two-column WrapPanel) instead of badge/pill styling.
- Metadata grid fields: Status, System, Year, Region, Genre, Developer, Publisher, Language, Rating, Players.
- Empty fields are hidden; the grid itself hides when all fields are empty.
- Action buttons split into two centered rows:
  - **Row 1 — release actions:** Open in Library · Edit Metadata · Edit Extra Notes
  - **Row 2 — metadata workflow:** Scrape · Merge Metadata
  - Scrape status label sits below Row 2.
- Physical media border/separator cleanup completed.

### Manage Media — Media Intake Workbench

- Redesigned as a dual-pane workbench: release asset list (left) + Incoming Media browser (right).
- `incoming-media/` is the official source folder for manual media intake. Created at startup, never auto-deleted.
- Import workflow: copy to media folder → SHA-256 verify → curation row written → optional source file delete.
- Delete vs Exclude semantics:
  - **Exclude** — stores SHA-256, sets `is_excluded = 1`, row persists after file removal, prevents reintroduction.
  - **Delete** — removes file from disk (if present) and removes curation row. No exclusion created. Does not prevent reintroduction.
  - **Delete on Missing** — removes the curation row only. No filesystem action.
- Four-case delete confirmation dialog covers all `(Exists, IsExcluded)` combinations.
- Preview safety: `TryLoadBitmap` returns null on missing or corrupt files; `Image.Source` is detached before any `Dispose()` call to prevent Avalonia layout-pass crashes.

### Documentation

- `CACHE_CURATION_PIPELINE.md` — updated for dual-pane workbench, import workflow, Delete/Exclude semantics, `incoming-media/` folder, preview safety.
- `QA/CACHE_CURATION_REAL_WORLD_TEST_PLAN.md` — updated to reflect current UI and semantics.
- `MILESTONES/CACHE_CURATION_PIPELINE_MILESTONE.md` — updated test count and feature list.
- `USER_MANUAL.md` and `DEVELOPER_NOTES.md` — updated for grid removal, action row split, workbench description.

---

## What is deferred

- Real-world QA pass (crash reports, screenshots, repro steps from actual usage).
- MAME DAT complementary extraction (driver metadata, parent/clone, working state).
- MetadataMergeService extraction from MainWindow.
- ViewModel layer / MVVM for catalog list and hero.

---

## Next step

Use Arkadia under real conditions. Fix issues as they surface from crash reports, screenshots, and repro steps. No planned feature work until QA pass is complete.
