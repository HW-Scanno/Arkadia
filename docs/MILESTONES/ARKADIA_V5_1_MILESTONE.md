# Arkadia Milestone v5.1 — Curation Core & Project Structure

**Date:** 2026-05-08
**Build:** clean — 0 errors, 0 warnings
**Tests:** 975 / 975 passing

---

## Summary

Arkadia v5.1 establishes the core curation foundation: cache package workflow, offline scraping, bulk scraping, curated media management, incoming media intake, Extra Notes, AMP specification, and project structure cleanup.

---

## 1. Cache & Curation Pipeline

- ScreenScraper Cache Builder UI with Force rebuild and UpdatePayloads modes
- Payload sanitization: `response.ssuser` removal and credential placeholder stripping applied at staging and again at ZIP creation
- Staging-cache management (Manage Staging dialog)
- Dashboard SCRAPE STAGING Top 5 widget
- Registered Cache Package Manager (register, deregister, list)
- Verify Package with severity scheme (error / warning / info)
- Offline Single Scrape from registered ScreenScraper Cache
- Bulk Scraping from registered ScreenScraper Cache
- No online ScreenScraper calls during offline single scrape or bulk scrape

## 2. Official folders

| Folder | Purpose |
|---|---|
| `incoming-csv/` | Incoming CSV imports |
| `incoming-media/` | Manual media intake source; created at startup, never auto-deleted |
| `scrape-cache/` | Registered cache package root |
| `scrape-cache/screenscraper/` | ScreenScraper-specific registered packages |
| `staging-cache/` | Cache builder staging root |
| `staging-cache/screenscraper/` | ScreenScraper-specific staging |

## 3. Catalog UI

- Catalog Grid view removed; Catalog is list-only
- Catalog hero redesigned as a sober catalog sheet
- Primary and secondary badge/pill rows removed
- Compact metadata label/value grid added (Status, System, Year, Region, Genre, Developer, Publisher, Language, Rating, Players); empty fields hidden
- Original title subtitle rendered only when meaningfully different from display title
- Action buttons split into two centered rows:
  - **Row 1 — release actions:** Open in Library · Edit Metadata · Edit Extra Notes
  - **Row 2 — metadata workflow:** Scrape · Merge Metadata
- Description, Extra Notes, and Physical Media layout polished
- Physical Media border/separator cleanup completed

## 4. Media Intake Workbench

- Manage Media redesigned as dual-pane Media Intake Workbench
- Release navigation inside Manage Media
- Current release media list grouped by media type
- Selected asset preview enlarged
- Central action buttons consolidated
- Incoming Media browser pane (reads from `incoming-media/`)
- Incoming file preview
- Manual import workflow: copy → SHA-256 verify → curation row → optional source delete
- Preview safety: `TryLoadBitmap` returns null on missing/corrupt files; `Image.Source` detached before `Dispose()` to prevent Avalonia layout-pass crashes

## 5. Curation semantics

| Action | Behaviour |
|---|---|
| **Exclude** | Persistent rejection. Computes and stores SHA-256. Row persists after file removal. Prevents reintroduction when "Respect excluded media" is on. |
| **Delete File** | Local cleanup. Removes file from disk and curation row from DB. Does not create exclusion. Does not prevent reintroduction. |
| **Delete on Missing** | Removes curation row only. No filesystem action. |
| **Delete on Missing + Excluded** | Removes the exclusion record. No filesystem action. |
| **Credits** | Curated attribution. Not provider provenance. Travel with assets in AMP. |
| **Extra Notes** | Release-level user curation text. Never overwritten by provider import, merge, or bulk. |

## 6. AMP / ARK product distinction

> **Core product rule: AMP is not a backup. ARK is not a media pack.**

| | `.amp` — Arkadia Media Pack | `.ark` — Arkadia Backup / Archive |
|---|---|---|
| Purpose | Curated, distributable, provider-agnostic media/metadata package | Database and application state backup/restore |
| Contains raw provider payloads | No — forbidden | Possibly |
| Provider provenance visible | No — forbidden | Possibly |
| Intended for distribution | Yes, where legally permissible | No |
| Status | Planned — specification written | Planned — not yet specified |

AMP v1 specification: [docs/SPECS/ARKADIA_MEDIA_PACK_V1_SPEC.md](../SPECS/ARKADIA_MEDIA_PACK_V1_SPEC.md)

## 7. Documentation updated

- `docs/CACHE_CURATION_PIPELINE.md` — dual-pane workbench, import workflow, Delete/Exclude semantics, `incoming-media/`, preview safety, AMP `.amp` extension correction
- `docs/QA/CACHE_CURATION_REAL_WORLD_TEST_PLAN.md` — updated for current UI and semantics
- `docs/SPECS/ARKADIA_MEDIA_PACK_V1_SPEC.md` — new: full AMP v1 specification (19 sections)
- `docs/MILESTONES/CACHE_CURATION_PIPELINE_MILESTONE.md` — updated test count and feature list
- `docs/MILESTONES/CATALOG_MEDIA_INTAKE_UI_CHECKPOINT.md` — new: pre-QA checkpoint
- `docs/USER_MANUAL.md` — Catalog Grid removal, action row split, workbench description
- `docs/DEVELOPER_NOTES.md` — AMP `.amp` extension, `.ark` clarification, structure notes

## 8. Project structure cleanup

Project root now intentionally contains only:

```
App.axaml + .cs
Program.cs
MainWindow.axaml + .cs
ArkadiaFolders.cs
```

Domain files moved into logical folders in seven batches, each verified independently:

| Batch | Target folder | Files moved |
|---|---|---|
| 1 | `Disks/` | 7 files (CreateDiskDialog, DiscoveredDiskRow, InitializeDiskDialog, PickDriveDialog) |
| 2 | `Volumes/` | 22 files (all volume management dialogs, row models, DecisionColorConverter) |
| 3 | `Ingestion/` | 18 files (all DAT lifecycle dialogs, VerifyRow, VerifyResultColorConverter) |
| 4 | `Systems/` | 8 files (AuthorityManagerDialog, CreatePlatformDialog, PlatformTypeManagerDialog, ToolDialog) |
| 5 | `Providers/` | 15 files (all ScreenScraper cache dialogs, CacheBuilderHelper, ScrapeReviewDialog, ScraperProviderDialog) |
| 6 | `Catalog/` | 16 files (all catalog feature dialogs, services, helpers) |
| 7 | `Controls/` | 7 files (ConfirmDialog, InfoDialog, TextBoxCommands, ActionColorConverter, ToUpperConverter) |
| 8 | `Systems/` | 2 files (ImageCacheProgressDialog) |

**Migration rules applied throughout:**
- Namespaces intentionally kept as `namespace Arkadia` — matches established convention from `Providers/` AXAML files
- `x:Class` declarations unchanged — Avalonia resolves by namespace, not file path
- `xmlns:local` and `xmlns:ark` declarations unchanged
- No `.csproj` changes — SDK globbing picks up `**/*.cs` and `**/*.axaml` in all subfolders automatically
- Every batch verified with `dotnet build` (0 errors, 0 warnings) and `dotnet test` (975/975 passing)

---

## Important invariants

- Offline scrape and Bulk Scraping use registered cache only — no online ScreenScraper calls
- Bulk Scraping does not overwrite existing non-empty canonical metadata in default mode
- Extra Notes are never overwritten by provider import, merge, or bulk
- Media credits, preferred media, exclusions, and curation rows are preserved by bulk
- Excluded media hashes prevent rejected assets from being reintroduced
- Provider/source provenance is not exposed in Manage Media or Arkadia-facing identity
- ScreenScraper Cache ZIPs are bootstrap material, not final AMP packages
- AMP/`.amp` is specification-only — not implemented
- ARK/`.ark` backup is planned only — not specified or implemented

---

## Known intentional non-implemented items

- AMP export / import
- AMP verifier
- AMP export dry-run / report
- ARK backup / export / import
- ES-DE export
- Exclude Reason dialog
- Image dimensions display
- Video duration display
- Suspicious tiny-file verification heuristic
- Library view display for Extra Notes
- Real-world QA run not yet completed

---

## Next recommended steps

1. Implement AMP export dry-run / report (Phase 1)
2. Implement single-file `.amp` export (Phase 2)
3. Implement AMP verifier (Phase 3)
4. Implement AMP import dry-run (Phase 4)
5. Implement AMP apply / import (Phase 5)
6. Run real-world usage testing in parallel as issues emerge
7. Consider ES-DE export only after AMP foundation is stable
