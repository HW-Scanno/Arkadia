# Arkadia

![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)
![Platform](https://img.shields.io/badge/platform-Windows-0078D4)
![Status](https://img.shields.io/badge/status-active%20development-orange)
![License](https://img.shields.io/badge/license-MIT-blue)

Arkadia is a preservation-grade desktop application for managing large offline ROM archives across multiple physical disks, with an integrated metadata catalog and online/offline scraping workflow with coming Arkadia Media Packs.

It provides a structured, integrity-first approach to organizing, verifying, and distributing artifact collections derived from DAT files — ensuring filesystem state and catalog state are always aligned, and that no artifact is ever considered present without independent verification.

---

## Current Project Goal

Arkadia is being developed toward a complete personal preservation workflow tool:

1. Import DAT files from preservation authorities (No-Intro, Redump, TOSEC, MAME, FBNeo, EggmansWorld)
2. Manage a multi-disk physical archive with integrity verification
3. Browse the full collection via a rich Catalog with cover art, screenshots, metadata, and media previews
4. Scrape metadata and media from ScreenScraper with a review-before-apply flow
5. Normalize metadata values consistently via configurable mapping rules

---

## High-Level Architecture

```
Arkadia.sln
├── Arkadia.csproj          — Avalonia 11 / .NET 8 desktop app (Windows)
│   ├── Data/               — DatLineStore, CatalogService, MediaStore, normalizers
│   ├── Library/            — LibraryEntry, title resolution
│   ├── Providers/          — ScreenScraperClient, ScreenScraperImportService
│   ├── Ingestion/          — DAT parsing and ingest pipeline
│   ├── Themes/             — Theme engine, palette management
│   └── MainWindow.axaml    — Single-window UI with view switching
└── Arkadia.Tests.csproj    — xUnit test project (~590 tests)
```

The application is a single-window app with a left nav bar switching between views: Dashboard, Analytics, Providers, Systems, Operations, Catalog, Settings.

---

## Data Layout

At runtime, Arkadia writes all data next to the executable (`AppContext.BaseDirectory`):

```
data/
  catalog.db                         — global catalog (systems, DAT lines, settings, mappings)
  media/
    <hardwareFamilyId>/
      <datLineId>/
        covers-front/                 — regional front covers
        covers-back/
        covers-spine/
        covers-wrap/
        screenshots-title/            — title screen shots
        screenshots/                  — gameplay screenshots
        fanart/
        videos/
        logos-hd/
        logos/
        marquees/
        flyers/
        manuals/
        physical/                     — physical media photos
        physical-texture/
        metadata/                     — raw provider JSON payloads
  platforms/
    <hardwareFamilyId>/
      <datLineId>/
        releases.db                   — per-DAT SQLite DB (releases, artifacts, proposals, etc.)

libraries/
  lib-vlc/win-x64/                   — LibVLC runtime (not committed; place manually)

logs/
  ingest/
  volume-verify/
  repair/
  imagecache/
  integrity/

themes/
  visual/default/badges/             — badge icon assets (committed)

tools/                               — external tools (7zip, chdman — not committed)
```

---

## Key Concepts

### DAT Providers

A **DAT provider** is an authority that defines release identity and technical catalog data. DAT provider data is canonical technical input — it determines what a release *is* (name, region, format, size, checksum, parent/clone relationships). It is entirely separate from **metadata providers** such as ScreenScraper, which enrich entries with titles, artwork, and descriptions but do not replace DAT identity.

Supported DAT providers:

| Provider | Typical scope |
|---|---|
| No-Intro | Cartridge-based systems (SNES, GBA, N64, …) |
| Redump | Optical disc systems (PS1, PS2, Saturn, …) |
| TOSEC | Broad multi-platform coverage |
| MAME | Arcade drivers, BIOS sets, devices, software lists |
| FBNeo | Arcade (Final Burn Neo) |
| EggmansWorld | Supplemental/community collections |

### Systems, Hardware Families, and DAT Lines

A **Hardware Family** groups related DAT lines under one catalog context (e.g., all SNES releases, or all MAME arcade entries). A **DAT Line** represents one imported DAT scope under a family — each has its own isolated SQLite database. Display labels are generated at runtime from authority + media type (e.g., `MAME · ROM`, `Redump · DVD`); the raw `dat_lines.name` column stores neutral media-type data, not formatted display text.

Deleting a hardware family is blocked while any DAT lines exist under it.

### Catalog

The **Catalog** view is the primary browsing interface. It shows all releases across a selected system and DAT line, with filtering, search, and a detail panel showing:

- Cover gallery (regional covers: front / back / spine / wrap)
- Media gallery (videos, screenshots, fanart)
- Extras (logos, flyers, marquees)
- Manuals (PDF/image files, opened in the system viewer)
- Physical media photos
- Metadata badges (region, system, year, type, size)
- Metadata quality checklist and quality indicator
- Description

### MAME Provider

MAME DATs describe arcade drivers, BIOS sets, devices, and software lists. Release identifiers are **shortnames** (e.g., `anmlbskt`) — not human-readable titles. When scraping MAME releases, set **Scrape As System** to `arcade` so lookups target the correct external provider system. Because shortnames rarely match title searches, use the **exact ROM fallback** in `ScrapeReviewDialog` to locate the correct game by hash.

Future MAME-specific complementary extraction (driver metadata, parent/clone relationships, working state, technical flags) will be stored separately from provider metadata and will not be overwritten by ScreenScraper.

### DAT vs Metadata Provider Separation

| Data | Origin |
|---|---|
| Release identity, shortname | DAT provider |
| Region, format, media type | DAT provider |
| Size, checksum | DAT provider |
| Parent / clone / software-list relationships | DAT provider |
| Technical flags (working state, BIOS, device) | DAT provider |
| Title, original title, description | Metadata provider (ScreenScraper) |
| Developer, publisher, year, genre | Metadata provider |
| Rating, players | Metadata provider |
| Screenshots, video, covers, manuals | Metadata provider |

DAT-derived facts feed badges, quality indicators, and future filters. They are never overwritten by metadata scraping.

### Manual Scrape Flow

1. **Provider selection** — choose ScreenScraper
2. **ScrapeReviewDialog** — search candidates by title, or accept an exact ROM match; review results before committing
3. **Fetch details** — full metadata and media URLs are retrieved
4. **ScreenScraperImportService** — normalizes fields, saves proposals as pending (`accepted=0`), stores raw JSON payload, downloads all media
5. **MergeMetadataDialog** — review each proposed field; fields with existing values show SAME/MANUAL/LOCKED status; user selects which to apply; LOCKED fields cannot be overwritten without explicit override

### Recommended Workflows

**Standard workflow:**
1. Create System and set Scrape As System ID if needed
2. Import DAT file
3. Review Catalog — releases are immediately browsable with DAT-derived identity
4. Scrape metadata — provider proposals are saved as pending
5. Merge metadata — review proposed fields, apply selections
6. Use Edit Metadata for manual corrections or locked-field overrides

**MAME / arcade workflow:**
1. Import MAME-derived DAT — shortnames become release identifiers
2. Set Scrape As System = `arcade`
3. For each release, use ScrapeReviewDialog with exact ROM fallback (shortnames don't match title search)
4. Future complementary extraction will enrich working state, driver metadata, and parent/clone trees without touching metadata proposals

### Edit Metadata

Manual metadata entry for any release. Each field has a lock checkbox. Controlled-vocabulary fields (genre, release_type, region, etc.) are normalized via the metadata value mapping rules before saving.

### Metadata Field Locks

Each metadata field can be **locked** per release. Locked fields are skipped when proposals are auto-applied (during bulk scrape) and show a LOCKED badge in the Merge Metadata dialog. Locks can be toggled per-field in both Edit Metadata and Merge Metadata.

### Metadata Value Mappings

A global table of normalization rules (`field`, `match_value`, `replacement`, `enabled`). Rules are seeded with defaults (e.g., `region/wor → World`, `release_type/fantranslation → Fan Translation`) and can be managed in **Settings → Metadata Value Mappings**. Applied at proposal-save time, manual-edit time, and badge display.

### Media Filesystem Layout

Media files use indexed stems: `<releaseStem>_<index>.<ext>` (screenshots, fanart, logos, etc.) or `<releaseStem>_<region>_<index>.<ext>` (regional covers). The `ScreenScraperClient` deduplicates by expected byte size before downloading.

### LibVLC

Video previews in the Catalog gallery require LibVLC. Place the `libvlc.dll` and related files in `libraries/lib-vlc/win-x64/` next to the executable. If absent, videos fall back to a text label with a play overlay.

---

## Building

```bash
git clone https://github.com/your-org/arkadia.git
cd arkadia
dotnet build
dotnet test
dotnet run --project Arkadia.csproj
```

**Requirements:** .NET 8 SDK, Windows 10+.

---

## Publishing (Portable win-x64)

```bash
dotnet publish Arkadia.csproj -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=false -p:PublishReadyToRun=true -o publish/
```

The `publish/` directory contains the self-contained executable. Copy `libraries/lib-vlc/win-x64/` alongside it for video playback support.

---

## Current Major Features

| Area | Status |
|---|---|
| Multi-volume archive management | Stable |
| DAT import (No-Intro, Redump, TOSEC, MAME, FBNeo, EggmansWorld) | Stable |
| SHA1 integrity verification | Stable |
| Volume plan / build / append / reabsorb | Stable |
| Repair workflow | Stable |
| Catalog browse with cover/media gallery | Stable |
| ScreenScraper manual scrape | Stable |
| Metadata proposals + Merge dialog | Stable |
| Edit Metadata with field locks | Stable |
| Metadata value mappings (Settings) | Stable |
| Theme engine with palette support | Stable |
| LibVLC video preview | Stable |
| Bulk / automatic scrape | Planned |
| Scrape from cached payload | Planned |
| Additional providers | Planned |

---

## Documentation

- [User Manual](docs/USER_MANUAL.md)
- [Developer Notes](docs/DEVELOPER_NOTES.md)
- [Cache & Curation Pipeline](docs/CACHE_CURATION_PIPELINE.md) — ScreenScraper Cache Builder, Manage Staging, Registered Cache Manager, Verify Package, offline / bulk scraping, Manage Media, Extra Notes, AMP future direction
- [Roadmap](docs/ROADMAP.md)

---

## License

MIT License. See [LICENSE](LICENSE) for details.
