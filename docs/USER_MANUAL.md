# Arkadia — User Manual

---

## Table of Contents

1. [First Launch and Data Folders](#1-first-launch-and-data-folders)
2. [DAT Providers and Metadata Providers](#2-dat-providers-and-metadata-providers)
3. [Creating a System](#3-creating-a-system)
4. [Importing DATs](#4-importing-dats)
5. [MAME and Arcade DATs](#5-mame-and-arcade-dats)
6. [Catalog Overview](#6-catalog-overview)
7. [Understanding the Catalog Layout](#7-understanding-the-catalog-layout)
8. [Manual Scraping Workflow](#8-manual-scraping-workflow)
9. [Merge Metadata Dialog](#9-merge-metadata-dialog)
10. [Edit Metadata Dialog](#10-edit-metadata-dialog)
11. [Locked Fields](#11-locked-fields)
12. [Metadata Value Mappings](#12-metadata-value-mappings)
13. [Opening Manuals](#13-opening-manuals)
14. [Video Previews and LibVLC](#14-video-previews-and-libvlc)
15. [Recommended Workflows](#15-recommended-workflows)
16. [Troubleshooting](#16-troubleshooting)
17. [Cache & Curation Pipeline](#17-cache--curation-pipeline)
18. [Backup and Restore (ARK)](#18-backup-and-restore-ark)

---

## 1. First Launch and Data Folders

When Arkadia starts for the first time it creates a `data/` folder in the same directory as the executable. This folder contains all application state:

```
data/
  catalog.db         — global database (systems, DAT lines, settings, mappings)
  media/             — all scraped cover art, screenshots, videos, manuals
  platforms/         — per-DAT release databases
```

You do not need to configure these paths. Everything is relative to the executable location.

If you move the executable, move the `data/` folder alongside it.

---

## 2. DAT Providers and Metadata Providers

Arkadia uses two distinct types of providers. Understanding the difference helps explain why some fields come from the DAT file and others come from scraping.

### DAT Providers

A **DAT provider** is an authority that defines release identity — what a release *is*. DAT data is canonical technical input and is never overwritten by scraping.

| Provider | Typical scope |
|---|---|
| No-Intro | Cartridge-based systems (SNES, GBA, N64, …) |
| Redump | Optical disc systems (PS1, PS2, Saturn, …) |
| TOSEC | Broad multi-platform coverage |
| MAME | Arcade drivers, BIOS sets, devices, software lists |
| FBNeo | Arcade (Final Burn Neo) |
| EggmansWorld | Supplemental/community collections |

DAT data includes: release name, region (where encoded in the filename), format/media type, size, checksum, and parent/clone relationships.

### Metadata Providers

A **metadata provider** (currently ScreenScraper) enriches releases with human-facing content: titles, descriptions, developer and publisher credits, genre classification, cover art, screenshots, videos, and manuals.

Metadata is always proposed first and applied only after your review in the **Merge Metadata** dialog. It does not replace DAT-derived identity — the DAT shortname, checksum, and format are permanent.

### Display Labels

The DAT Line label shown in the Catalog dropdown is generated at runtime from the authority and media type, for example:

- `MAME · ROM`
- `No-Intro · ROM`
- `Redump · DVD`

---

## 3. Creating a System

Before importing a DAT, you need a System entry to group it under.

1. Navigate to **Systems** in the left sidebar.
2. Click **Create Platform** (or the equivalent button for your hardware family type).
3. Enter a name (e.g., "SNES") and select the platform type.

### Scrape As / System Scraping ID

Each system has a **Scrape ID** field. This is the identifier Arkadia uses when looking up games on ScreenScraper. You can leave it as the auto-detected value or set it manually if the auto-detection picks the wrong system.

Example: the SNES hardware family ID is typically `snes`, which maps to ScreenScraper system ID 4.

---

## 4. Importing DATs

With a system created:

1. Navigate to **Providers** in the left sidebar.
2. Select the appropriate provider window (No-Intro, Redump, TOSEC, MAME, FBNeo, EggmansWorld).
3. Use **Import DAT** to load a `.dat` or `.xml` file from disk.
4. The DAT line appears under the system and its releases are imported into the database.

Each DAT line has its own isolated database. You can have multiple DATs for the same system.

---

## 5. MAME and Arcade DATs

MAME DATs differ from cartridge and disc DATs in several ways:

### Release Identifiers

MAME uses **shortnames** as release identifiers — for example, `anmlbskt` or `sf2`. These are the technical driver names, not human-readable titles. They are treated as the authoritative identity for that release, just as a No-Intro ROM checksum is for cartridge releases.

### Scrape As System

Because MAME covers arcade hardware, set **Scrape As System** to `arcade` for MAME-based hardware families. This maps to the correct external provider system on ScreenScraper. Without this, scrape lookups will target the wrong system.

To set it: go to **Systems**, select the hardware family, and enter `arcade` (or the appropriate ScreenScraper system ID) in the **Scrape As** field.

### Searching with Shortnames

Title-based candidate search will rarely find MAME shortnames — a search for `anmlbskt` will return nothing useful. Instead, use the **Exact ROM Match** fallback in `ScrapeReviewDialog`. If Arkadia can identify the ROM by its hash against ScreenScraper's database, an exact match result appears at the top of the candidate list automatically.

### Future MAME Enrichment

In a future release, Arkadia will support complementary extraction from MAME DATs, including:

- Driver metadata and working state
- Parent / clone relationships
- BIOS and device dependencies
- Software list relationships
- Technical compatibility flags

This data will be stored separately from metadata proposals and will not be affected by ScreenScraper scraping.

---

## 6. Catalog Overview

The **Catalog** view is the primary browsing interface for your collection.

At the top of the Catalog panel, use the **System** and **DAT Line** dropdowns to select which collection to browse. Use the **search box** to filter by title, and the **status filter** to show only Present / Missing / Pending / Lost releases.

Click any release in the list to select it. The right panel shows:

- Release name and alternate titles
- Cover art gallery
- Media gallery (video + screenshots)
- Extras gallery (logos, flyers, marquees)
- Manuals
- Physical media photo
- Metadata badges (region, system, year, release type, size)
- Metadata checklist
- Description

---

## 7. Understanding the Catalog Layout

### COVERS

The cover gallery shows regional artwork: **Front**, **Back**, **Spine**, and **Wrap** covers. If multiple regional variants exist for the same position (e.g., US front and EU front), use the left/right arrows to browse them.

### MEDIA

The media gallery shows **videos**, **title screenshots**, **gameplay screenshots**, and **fanart**. Videos play automatically if LibVLC is installed. The counter shows the current item type and position.

### EXTRAS

The extras gallery shows **logos** (HD and standard), **flyers**, and **marquees**.

### MANUALS

The manuals section lists any scanned manual files. Click a numbered button to open that manual in your system's default PDF or image viewer.

### RELEASE QUALITY

A 6-dot quality indicator shows how complete the metadata is. Each dot corresponds to one of: Title, Original Title, Developer, Publisher, Year, Languages.

### CHECKLIST

Six checkmarks show which core metadata fields are populated: title, original title, developer, publisher, year, languages. A green ✓ means the field is filled; a red ✗ means it is empty.

### PHYSICAL MEDIA

If physical media photos (box photos, cartridge textures) were scraped, the best available image is shown here. Texture variants are preferred over flat photos.

---

## 8. Manual Scraping Workflow

Select a release in the Catalog, then click **Scrape** in the detail panel.

### Choosing a Provider

A **Provider Selection** dialog appears. Currently ScreenScraper is the only supported provider. It shows as **Available** if credentials are configured in Settings, or **Not configured** if they are missing.

To configure ScreenScraper credentials: go to **Settings → ScreenScraper** and enter your username, password, developer ID, and developer password.

### Searching Candidates

The **ScrapeReviewDialog** opens. It performs an automatic search based on the release title. Results appear as a list of candidates. Each candidate shows the provider title, year, and system.

Click a candidate to select it. A preview area may show additional details.

If no candidates match, try editing the search box at the top and pressing Enter to search manually.

### Exact ROM Match Fallback

If Arkadia can identify the exact ROM by its hash and filename against the ScreenScraper database, an **Exact Match** result appears at the top of the list automatically. This is the most reliable match — it bypasses title disambiguation entirely.

Select the exact match and click **Accept** (or **Use This**) to proceed.

### What Happens Next

After you accept a candidate:

1. Arkadia fetches the full metadata and media URLs from ScreenScraper.
2. Provider proposals are saved (not yet applied to the release).
3. All available media is downloaded: covers, screenshots, fanart, video, logos, marquees, flyers, manuals.
4. The **Merge Metadata** dialog opens automatically for you to review the proposed fields.

---

## 9. Merge Metadata Dialog

The Merge Metadata dialog shows a row for each metadata field. Each row has:

- **Field name** — e.g., Title, Developer, Year
- **Current value** — what is currently saved for this release
- **Proposed value** — what the scraper found
- **Status badge** — see below
- **UNLOCK toggle** — for MANUAL and LOCKED fields
- **Apply checkbox** — whether to apply this field

### Status Badges

| Badge | Meaning |
|---|---|
| **NEW** | Field is empty. Proposed value can be applied. Checkbox is checked by default. |
| **SAME** | Proposed value matches current value. Checkbox is unchecked by default. |
| **MANUAL** | Field was previously set manually. Checkbox is unchecked. Click UNLOCK to enable it. |
| **LOCKED** | Field is locked. Cannot be overridden. Click UNLOCK to allow applying this field for this merge only. |
| **OVERRIDE** | You have clicked UNLOCK — the field will be applied even though it was MANUAL or LOCKED. |

### Applying Fields

Check the boxes for the fields you want to apply, then click **Apply**. Only checked fields are written to the release.

Clicking **Cancel** discards all proposed changes. The downloaded media remains.

---

## 10. Edit Metadata Dialog

Click **Edit Metadata** in the Catalog detail panel to open the manual metadata editor for the selected release.

All metadata fields are editable text boxes. **Controlled vocabulary fields** (Region, Genre, Subgenre, Players, Release Type, Rating) are normalized automatically on save using the Metadata Value Mappings rules — for example, typing `wor` in the Region field saves as `World`.

Click **Save** to write the changes. Click **Cancel** to discard.

---

## 11. Locked Fields

Each field in Edit Metadata has a **lock checkbox** on the right. When checked, that field is **locked**.

Locked fields:

- Are not overwritten during automatic (bulk) scraping.
- Show a **LOCKED** badge in the Merge Metadata dialog.
- Can only be overridden by explicitly clicking **UNLOCK** in the Merge Metadata dialog during that specific merge session.

Use locks to protect fields you have carefully curated — for example, a custom title or a developer name you have verified independently.

Lock state is preserved across scrapes and edits.

---

## 12. Metadata Value Mappings

Go to **Settings → Metadata Value Mappings** to manage normalization rules.

The table shows all current rules. Each rule has:

- **Field** — which metadata field this applies to (region, release_type, genre, etc.)
- **Match Value** — the raw value to match (case-insensitive)
- **Replacement** — the normalized value to substitute
- **Enabled** — toggle to activate or deactivate a rule without deleting it

### Default Rules

Arkadia ships with built-in rules for common abbreviations:

| Field | Match | Replacement |
|---|---|---|
| region | wor | World |
| region | eu | Europe |
| region | us | USA |
| region | jp | Japan |
| release_type | fantranslation | Fan Translation |
| release_type | fan-translation | Fan Translation |
| release_type | retail | Retail |
| ... | ... | ... |

### Adding a Rule

Use the form at the bottom of the Metadata Value Mappings section:

1. Select a **Field** from the dropdown.
2. Enter the **Match Value** (the raw value as it comes from the scraper or manual entry).
3. Enter the **Replacement** (the display value you want).
4. Check or uncheck **Enabled**.
5. Click **Add / Update**.

If a rule already exists for that field + match value, it is updated in place.

### Selecting and Deleting a Rule

Click any row in the table to load it into the form. The **Delete** button becomes active. Click Delete to remove the rule.

### When Rules Apply

Rules apply at three points:

- When a scraper saves metadata proposals.
- When you manually save in Edit Metadata.
- When the Catalog displays the region and release type badges.

Changes take effect immediately — the current catalog entry's badges refresh automatically.

---

## 13. Opening Manuals

If manuals were scraped for a release, numbered buttons appear in the MANUALS section of the Catalog detail panel. Each button corresponds to one manual file (PDF or image).

Click a button to open that manual in your system's default viewer (e.g., Adobe Acrobat, Windows Photos, etc.).

---

## 14. Video Previews and LibVLC

Catalog media previews support video playback via LibVLC. To enable it:

1. Download the LibVLC runtime for Windows (x64).
2. Place the files in `libraries/lib-vlc/win-x64/` next to the Arkadia executable.
3. Restart Arkadia.

If LibVLC is not present or fails to initialize, videos show a text label with the filename instead of playing. You can still see the filename and navigate between items; the video just won't play inline.

**Autoplay** and **audio** settings are in **Settings → Catalog**.

---

## 15. Recommended Workflows

### Standard Workflow

1. **Create System** — navigate to Systems, click Create Platform, enter a name and type.
2. **Set Scrape As System** — enter the ScreenScraper system ID for this hardware family.
3. **Import DAT** — navigate to Providers, select the appropriate provider tab, click Import DAT.
4. **Review Catalog** — releases appear immediately with DAT-derived identity (name, region, format, size).
5. **Scrape metadata** — select a release, click Scrape, choose ScreenScraper, select a candidate.
6. **Merge metadata** — review proposed fields in the Merge Metadata dialog; apply the ones you want.
7. **Edit Metadata** — use for manual corrections or to lock fields you have verified independently.

### MAME / Arcade Workflow

1. **Import MAME-derived DAT** — shortnames become release identifiers.
2. **Set Scrape As System = `arcade`** — required for ScreenScraper lookups to target the right system.
3. **Scrape with exact ROM fallback** — in ScrapeReviewDialog, use the exact match result if it appears (shortnames don't work with title search).
4. **Merge metadata as normal** — MAME shortnames remain the permanent technical identity regardless of the display title scraped.
5. *(Future)* Complementary extraction will enrich driver metadata, working state, and parent/clone relationships without touching metadata proposals.

---

## 16. Troubleshooting

### Provider not configured

The ScreenScraper provider shows "Not configured" in the provider selection dialog.

→ Go to **Settings** and fill in your ScreenScraper username, password, developer ID, and developer password.

### No candidates found

The ScrapeReviewDialog shows an empty candidates list.

→ Try editing the search query manually. Remove subtitles, punctuation, or region tags. Try just the main title. If the game is obscure, it may not be in the ScreenScraper database.

### VLC unavailable / video does not play

The media gallery shows a filename instead of playing the video.

→ LibVLC is not installed or the files are in the wrong location. See [Video Previews and LibVLC](#12-video-previews-and-libvlc).

### Metadata is not changing after scraping

After using Merge Metadata, the release shows the same values as before.

→ Check whether the fields have a **LOCKED** or **MANUAL** badge in the Merge dialog. Locked fields require you to click UNLOCK before the checkbox becomes active.

→ Also confirm you checked the Apply checkbox for each field you wanted to apply before clicking Apply.

### Merge Metadata shows SAME for everything

All proposed values already match the current metadata.

→ The release was already scraped previously and the values are up to date. You may still apply them again (uncheck "SAME" rows default to unchecked — check manually if you want to reapply), or just Cancel.

### A manually edited field is being overwritten by scraping

Your custom value was replaced after scraping.

→ Open **Edit Metadata** and check the **lock checkbox** next to that field. Save. The field will now show LOCKED in future Merge dialogs and will not be overwritten automatically.

---

## 17. Cache & Curation Pipeline

For building ScreenScraper cache packages, registering and verifying them, offline single-release scraping, bulk scraping, Manage Media, Extra Notes, and the future AMP direction, see the dedicated document:

→ [docs/CACHE_CURATION_PIPELINE.md](CACHE_CURATION_PIPELINE.md)

That document is the authoritative reference for the cache and curation flows. It covers folder layout (including `incoming-media/`), the ScreenScraper Cache Builder UI (Force / UpdatePayloads / KeepStaging / IndexAfterBuild), Manage Staging status labels, the Registered Cache Manager, the Verify Package severity scheme, Bulk Scraping scopes and options, the Manage Media dual-pane workbench (Incoming Media browser, safe import workflow, Delete vs Exclude semantics), Extra Notes, security and sanitization, troubleshooting, and a manual test plan.

---

## 18. Backup and Restore (ARK)

### What ARK is

**ARK** (`.ark`) is the Arkadia-native backup format. An ARK package is a ZIP archive containing a point-in-time snapshot of Arkadia's database state: the global catalog, all per-DAT-line release databases, and optionally the AMP registry.

ARK is for disaster recovery and machine migration. It is not a media pack and does not distribute media or metadata to other users.

> **AMP is not a backup. ARK is not a media pack.**

### What ARK backs up

| Included | Not included |
|---|---|
| Global catalog database (`catalog.db`) | Media files (cover art, screenshots, video) |
| All per-DAT release databases | Provider credentials |
| AMP registry (optional) | Provider cache packages |
| | Log files, tool binaries, temp files |

### What ARK does not back up

Media files are not included in ARK v0.5. After restoring an ARK, your catalog will show the correct expected state — releases, metadata, curation decisions — but media files must be reacquired separately (from an AMP package, a provider cache, or by re-scraping).

### Backup location

ARK backups are stored in the `backups\` folder in the Arkadia application directory. Arkadia creates this folder automatically on startup.

### Creating a backup

1. Open **Backups** from the sidebar.
2. In the **BACKUP** pane, click **Create Backup**.
3. The log window shows progress: planning, writing, and verification.
4. Arkadia creates:
   - `arkadia-backup-<timestamp>.ark` — the backup package
   - `arkadia-backup-<timestamp>.ark.sha256` — SHA-256 sidecar for integrity verification
5. Arkadia automatically verifies the generated package after writing.
6. When complete, the log shows **BACKUP COMPLETE**.
7. The backup list in the **RESTORE** pane refreshes automatically.

Keep both files together when storing or moving the backup.

### Restore pane

The **RESTORE** pane in the Backups view lists all `.ark` files in the `backups\` folder. Click **Refresh** to reload the list. Select a backup to enable the **Restore Selected** button.

**Live restore is intentionally blocked while Arkadia is running.** Arkadia maintains active SQLite services against the `data\` folder; replacing those files while the application is open is unsafe. Clicking **Restore Selected** opens an informational dialog explaining this and showing the selected package path for reference.

**To use a backup:**

- **Manual offline restore:** Close Arkadia, extract the `.ark` ZIP to your `data\` folder, then restart. Run Verify ALL or Verify Volume after restarting.
- **Restart-safe restore:** Planned for a future release.

ARK restore is always a **full replacement** — there is no merge restore. If the target directory is not empty, the existing data will be moved aside to `{target}.pre-ark-restore-{timestamp}` before the restore is committed.

**After restore:**

- Run **Verify ALL** or **Verify Volume** from the Operations view before relying on the restored archive state.
- Re-enter provider credentials (Settings → ScreenScraper).
- Re-register provider cache packages if needed.
- Review volume paths — absolute filesystem paths embedded in the databases may not be valid on the restore machine. Use the volume management tools to update them.

### Post-restore warnings

Every ARK restore always emits two mandatory warnings:

1. **Verify ALL / Verify Volume required** — restored state is expected state, not yet trusted state.
2. **Absolute paths may need review** — volume locations and media paths in the database may reference the source machine's filesystem.

### Core semantic rule

> **Restored state is expected state. Verified state is trusted state.**

ARK restore re-establishes what the catalog expects to exist. It does not verify that physical files are present on disk at their recorded locations. Only running Verify ALL or Verify Volume establishes trusted state.

For the full ARK format specification, see [docs/SPECS/ARKADIA_BACKUP_ARCHIVE_V0_5_SPEC.md](SPECS/ARKADIA_BACKUP_ARCHIVE_V0_5_SPEC.md).
