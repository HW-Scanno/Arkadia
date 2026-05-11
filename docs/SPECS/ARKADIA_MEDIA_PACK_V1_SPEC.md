# Arkadia Media Pack v1 — Specification

**Status:** Phases 1–7A implemented. Pipeline complete: plan → report → write → verify. Default Attribution block seeded by writer. Local registry and Providers UI complete.

**Last updated:** 2026-05-10 (Attribution added; phases 5–7A, open questions closed)

---

> **Core product rule:**
> **AMP is not a backup. ARK is not a media pack.**
>
> `.amp` — Arkadia Media Pack — curated, distributable, provider-agnostic media/metadata package.
> `.ark` — Arkadia Backup / Archive — v0.5 implemented; database and application state backup/restore.
> These two package families are entirely distinct and must never be conflated.

---

## 1. Purpose

AMP is the final curated output of Arkadia's media/metadata curation pipeline.

- AMP replaces dependency on provider cache packages for repeated offline application. Once you have an AMP, you do not need the original ScreenScraper Cache ZIP to apply the same curated content again.
- AMP is Arkadia-native and provider-agnostic. The format does not encode which provider produced any given piece of content.
- AMP is not a ScreenScraper Cache ZIP. A cache ZIP is a bootstrap/source artefact used to seed the pipeline. AMP is the curated output of that pipeline.
- AMP is not a provider payload archive. Raw ScreenScraper JSON responses are forbidden from AMP packages.
- AMP is not a SQLite database dump or application state snapshot. It carries curated canonical content, not internal schema rows.
- AMP is not `.ark`. The `.ark` extension is reserved for Arkadia Backup / Archive packages.

---

## 2. Package family distinction

| Feature | `.amp` — Arkadia Media Pack | `.ark` — Arkadia Backup / Archive |
|---|---|---|
| Primary purpose | Curated media and metadata distribution | Backup and restore of Arkadia internal state |
| Intended audience | Any Arkadia user; potentially public | Personal / private; environment-specific |
| Contains SQLite DB dump | No | Possibly (per backup mode) |
| Contains curated media | Yes | Possibly (per backup mode) |
| Contains raw provider payloads | No — forbidden | Possibly (implementation detail) |
| Provider provenance visible | No — forbidden | Possibly (for audit/debug/restore) |
| Intended for distribution | Yes, where legally permissible | No — not a public media format |
| Intended for restore | No — apply/import, not restore | Yes — rigid state restoration |
| Matching / import behaviour | Release-identity matching; dry-run supported | Restore by internal key |
| Privacy expectations | No private credentials, no API keys, no usernames | May contain local paths; not for distribution |

---

## 3. Container and extension

- **Official extension:** `.amp`
- **v1 container:** Standard ZIP archive (`System.IO.Compression.ZipArchive`, `CompressionLevel.Optimal`).
- **Format identity** is determined by the presence and validity of `manifest.json` inside the archive, not by the file extension alone.
- **MIME type label:** `application/x-arkadia-media-pack`
- `.ark` is reserved for Arkadia Backup / Archive and **must not** be used for AMP packages.

---

## 4. Archive layout

The implemented v1 layout:

```
manifest.json                         — package identity, format version, counts
releases.json                         — release records with metadata and media entries
curation/
  exclusions.json                     — SHA-256 hashes of rejected assets
  notes.json                          — per-release user-authored curator notes
hashes/
  files.sha256.json                   — SHA-256 + size for every file in the package
media/
  {mediaType}/
    {releaseId}/
      {fileName}                      — one media file per entry
```

**Media path format (implemented):**

```
media/{mediaType}/{releaseId}/{fileName}
```

Per-release namespacing (`{releaseId}`) prevents filename collisions when two releases in
the same package each have a file with the same name and media type.

**ZIP path rules (enforced by the verifier):**

- All entry paths must use forward slashes (`/`).
- No backslashes.
- No absolute paths (no leading `/`).
- No path traversal segments (`..`).
- No empty path segments (no `//`).
- No duplicate ZIP entry names.
- Known root directories: `media/`, `curation/`, `hashes/`.
- Known root-level files: `manifest.json`, `releases.json`.
- Any other root directory or root-level file is flagged as a Warning.

**Forbidden directories / files:**

- No `payloads/` directory or any raw provider JSON.
- No `metadata/` directory.
- No `provider/` directory.
- No `ssuser` field anywhere.
- No credentials or credential-bearing URLs.
- No provider URLs.
- No source package names or provider branding.

---

## 5. JSON conventions

AMP v1 uses **PascalCase** JSON field names throughout all JSON files (`manifest.json`,
`releases.json`, `curation/exclusions.json`, `curation/notes.json`,
`hashes/files.sha256.json`).

This follows `System.Text.Json` default serialization with `WriteIndented = true` and no
naming policy, which preserves C# record property names as-is.

- Field names are PascalCase: `ReleaseId`, `FormatName`, `SizeBytes`, etc.
- SHA-256 values are lowercase hexadecimal strings (64 characters).
- All JSON files are UTF-8 encoded without BOM.
- ZIP entry sizes reported in `hashes/files.sha256.json` refer to the ZIP entry's
  uncompressed length (i.e., `ZipArchiveEntry.Length`).

---

## 6. `manifest.json`

`manifest.json` is a single JSON object describing the package scope and format identity.

**Required fields:**

| Field | Type | Required value / description |
|---|---|---|
| `FormatName` | string | Must be exactly `"Arkadia Media Pack"` |
| `FormatVersion` | string | Must be `"1"` for v1 packages |
| `CreatedAtUtc` | string | ISO 8601 UTC timestamp (`"O"` round-trip format) |
| `HardwareFamilyId` | string | Arkadia hardware family identifier, e.g. `"snes"` |
| `DatLineId` | string | DAT line scope, e.g. `"snes-nointro"` |
| `SystemName` | string | Human-readable system name, e.g. `"Super Nintendo"` |
| `ReleaseCount` | integer | Number of release entries in `releases.json` |
| `MediaFileCount` | integer | Total number of media files in the package |
| `TotalMediaBytes` | integer | Sum of all media file uncompressed sizes in bytes |
| `ExclusionCount` | integer | Number of entries in `curation/exclusions.json` |
| `ExtraNotesCount` | integer | Number of entries in `curation/notes.json` |
| `Attribution` | object | Package-level attribution/legal notice (see below) |

**Attribution object** (seeded automatically by `AmpExportWriterService`):

| Field | Type | Description |
|---|---|---|
| `Attribution.Notice` | string | Generic legal notice covering all included material |
| `Attribution.GeneralCredits` | string | Generic list of community/data sources that may have contributed |

`Attribution` is seeded automatically in every package generated by Arkadia. It is
**not** technical provenance — it does not identify which external provider produced
which asset. `ScreenScraper` may appear as one item in `GeneralCredits` alongside
other community sources; this does not make the AMP ScreenScraper-specific. The
format remains Arkadia-native and provider-agnostic.

Default `Notice`:
> Arkadia Media Packs may include curated media and metadata originating from community-maintained databases, preservation projects, public archives, contributors, and third-party metadata/media sources.
>
> All included material remains subject to its original license, terms, and attribution requirements.

Default `GeneralCredits`:
> General credits may include, but are not limited to: ScreenScraper community, LaunchBox Games Database community, MobyGames, Wikipedia, GameFAQs, The Cover Project, EmuMovies community, Hyperspin community, Progetto-Snaps, Arcade-Museum flyer archives, No-Intro, Redump, TOSEC, MAME project data, community contributors, and original rights holders where applicable.

**Verification rules:**

- `FormatName` must equal `"Arkadia Media Pack"` — mismatch is an **Error**.
- `FormatVersion` must equal `"1"` — mismatch is a **Warning** (readable but version-suspect).
- All required identity/count fields must be present — missing field is an **Error**.
- `ReleaseCount` and `MediaFileCount` must match the actual counts in `releases.json` — mismatch is a **Warning**.
- `Attribution` block missing → **Warning**.
- `Attribution.Notice` missing or empty → **Warning**.
- `Attribution.GeneralCredits` missing or empty → **Warning**.
- Missing `Attribution` does not make a package unreadable; older packages without this field remain usable.

**Example:**

```json
{
  "FormatName": "Arkadia Media Pack",
  "FormatVersion": "1",
  "CreatedAtUtc": "2026-05-10T14:00:00.0000000Z",
  "HardwareFamilyId": "snes",
  "DatLineId": "snes-nointro",
  "SystemName": "Super Nintendo",
  "ReleaseCount": 47,
  "MediaFileCount": 312,
  "TotalMediaBytes": 186432000,
  "ExclusionCount": 12,
  "ExtraNotesCount": 3,
  "Attribution": {
    "Notice": "Arkadia Media Packs may include curated media and metadata originating from ...",
    "GeneralCredits": "General credits may include, but are not limited to: ScreenScraper community, ..."
  }
}
```

**Not in v1 manifest:**
The following fields appeared in earlier spec drafts and are **not** part of the v1
implementation: `format`, `ampVersion`, `packageId`, `packageName`, `systemId`,
`createdByApp`, `createdByVersion`, `hashAlgorithm`, `notes`, `legalNote`.
The following are also **not** in `Attribution`: `Provider`, `Source`, `ProviderId`,
`ScreenScraperProvider`, `SourcePackage`, `ScrapedFrom`.

---

## 7. `releases.json`

`releases.json` is a JSON array of release objects, ordered by `ReleaseId` (ascending,
ordinal). Each release carries identity fields, canonical metadata, and its media entries.

**Release object fields:**

| Field | Type | Description |
|---|---|---|
| `ReleaseId` | string | Arkadia internal release identifier (required) |
| `DatName` | string | DAT entry name / ROM shortname (required) |
| `Title` | string | Display title |
| `OriginalTitle` | string | Original language title, if different |
| `SortTitle` | string | Sort key title |
| `Developer` | string | Developer name |
| `Publisher` | string | Publisher name |
| `Year` | string | Release year |
| `Languages` | string | Language codes |
| `AlternateTitles` | string | Alternate or regional titles |
| `Description` | string | Description text |
| `Genre` | string | Primary genre |
| `Subgenre` | string | Subgenre |
| `Players` | string | Player count description |
| `ReleaseType` | string | Release type (e.g. game, demo, prototype) |
| `Rating` | string | Content rating |
| `Media` | array | List of media entries (see §8) |

**Rules:**

- `ReleaseId` and `DatName` are required for matching; their absence is an **Error**.
- Duplicate `ReleaseId` values within one package are an **Error**.
- String metadata fields default to `""` (empty string) when not available; they are never `null`.
- The array is ordered by `ReleaseId` ascending (ordinal sort) for deterministic output.
- Local absolute `FilePath` must never appear in `releases.json`.
- `ScrapedAtUtc`, provider field names, and provenance columns must not appear.

---

## 8. Media entries (inside `releases.json`)

Each release's `Media` array contains one entry per curated media file.

**Media entry fields:**

| Field | Type | Description |
|---|---|---|
| `MediaType` | string | Media type key, e.g. `"cover-front"`, `"screenshot"` |
| `ArchivePath` | string | Internal ZIP path: `media/{mediaType}/{releaseId}/{fileName}` |
| `FileName` | string | File name only (no directory component) |
| `Sha256` | string | SHA-256 hex digest of the media file (lowercase, 64 chars) |
| `SizeBytes` | long | Uncompressed file size in bytes |
| `Preferred` | boolean | Whether this is the preferred asset for its media type |
| `Credits` | string or null | Attribution text for this specific asset |

**Rules:**

- `ArchivePath` is the internal ZIP path — it is always relative and uses forward slashes.
- `FileName` is the file name only; it must not include any directory path component.
- `Sha256` must match the SHA-256 of the file as it exists in the ZIP entry.
- `SizeBytes` must match the uncompressed size of the ZIP entry.
- Media entries are ordered within each release by `MediaType` then `FileName` (ordinal ascending).
- Duplicate `ArchivePath` values within one package are an **Error**.

---

## 9. Media archive path rules

**Format:**

```
media/{mediaType}/{releaseId}/{fileName}
```

**Segment sanitization (applied by the writer, enforced by the verifier):**

Each path segment (`mediaType`, `releaseId`, `fileName`) is sanitized:
- Backslashes (`\`) are replaced with `_`.
- Forward slashes (`/`) are replaced with `_`.
- Null characters (`\0`) are replaced with `_`.
- Leading dots (`.`) are stripped from the start of each segment.

**Why per-release namespacing:**

Using `{releaseId}` as a path segment prevents collisions when two releases in the same
package have identically named files of the same media type (e.g. both have a file named
`cover.png` under `cover-front`). Without per-release namespacing, the second file would
silently overwrite the first in any naive ZIP extract.

**Ordering:**

Media files are written to the ZIP in deterministic order: releases ordered by `ReleaseId`
ascending, and within each release, media entries ordered by `MediaType` then `FilePath`
(ordinal ascending). This ensures byte-identical packages from the same plan.

---

## 10. `curation/exclusions.json`

`curation/exclusions.json` carries rejection decisions as hashes, not files. Excluded
assets are recorded so that an AMP import will not reintroduce content the curator has
explicitly rejected.

**Entry fields (v1 implementation):**

| Field | Type | Description |
|---|---|---|
| `ReleaseId` | string | Arkadia release identifier |
| `DatName` | string | DAT entry name |
| `MediaType` | string | Media type (may be empty string in Phase 3 — see note) |
| `Sha256` | string | SHA-256 hex digest of the excluded asset |

**Phase 3 limitation — structured exclusions deferred:**

In the current implementation, `AmpExportPlan` carries exclusion hashes only (not
structured per media-type). As a result, `MediaType` is always serialised as `""` (empty
string). Consumers must tolerate an empty `MediaType` in v1 packages.

This is acceptable for all current phases (export, write, verify, and local Create AMP).
Structured exclusions — with a populated `MediaType`, a `Reason` field, and a
`CreatedAtUtc` timestamp — must be revisited before AMP import/apply (Phase 10), where
understanding *which media type* was excluded per release becomes important for correct
merge behaviour. They are not required before Phase 5 (Catalog Create AMP action).

**Rules:**

- **Delete** is not an exclusion. Delete removes the local file/record and does not prevent
  reintroduction. Only `is_excluded = 1` rows are exported as exclusions.
- Deleted or forgotten curation rows do not appear in AMP exclusions.
- Exclusion rows without a SHA-256 are not valid exportable exclusions and are not included.
- The array is ordered by `ReleaseId` ascending (ordinal sort).

---

## 11. `curation/notes.json`

`curation/notes.json` carries per-release user-authored curation notes.

**Entry fields:**

| Field | Type | Description |
|---|---|---|
| `ReleaseId` | string | Arkadia release identifier |
| `DatName` | string | DAT entry name |
| `Notes` | string | User-authored curator text |

**Rules:**

- These notes come exclusively from `release_extra_notes`. They are **not** the same as
  `release_metadata.Notes` (which is a canonical metadata field, not a curator note).
- Extra Notes are user-owned curation text. Provider scrape, bulk-scraping, and metadata
  merge operations must not overwrite Extra Notes.
- Only releases with non-empty Extra Notes are included.
- The array is ordered by `ReleaseId` ascending (ordinal sort).

---

## 12. `hashes/files.sha256.json`

`hashes/files.sha256.json` provides integrity verification for all other files in the
package. It is a JSON array of hash entries.

**Entry fields:**

| Field | Type | Description |
|---|---|---|
| `Path` | string | Internal ZIP path of the file being hashed |
| `Sha256` | string | SHA-256 hex digest (lowercase, 64 characters) |
| `SizeBytes` | long | Uncompressed size of the ZIP entry in bytes |

**Coverage:**

The hash file includes entries for:
- `manifest.json`
- `releases.json`
- `curation/exclusions.json`
- `curation/notes.json`
- All media files at `media/{mediaType}/{releaseId}/{fileName}`

**`hashes/files.sha256.json` does not include itself.**

The verifier must not require `hashes/files.sha256.json` to hash itself; doing so would
create a circular dependency.

**Verification rules:**

- All four JSON files (`manifest.json`, `releases.json`, `curation/exclusions.json`,
  `curation/notes.json`) must be listed — absence of any is a **Warning**.
- Each listed `Path` must correspond to an existing ZIP entry — missing entry is a **Warning**.
- SHA-256 of each ZIP entry must match the recorded value — mismatch is an **Error**.
- `SizeBytes` must match the ZIP entry's uncompressed length — mismatch is a **Warning**.
- Media files must be non-zero size; zero-byte media is an **Error**.

---

## 13. Release identity and matching

Each release entry in `releases.json` carries identity fields used to match against the
target catalog on import.

**Identity priority (highest to lowest):**

1. Arkadia `ReleaseId` — if the target catalog was built from the same DAT line, this is the most reliable match.
2. DAT entry name (`DatName`) — the authoritative technical name from the DAT file.
3. `Title` + `OriginalTitle` — human-facing metadata match.
4. System + title combination — fallback if `ReleaseId` and `DatName` are absent.

**Matching rules (planned for Phase 9):**

- Import must support a **dry-run** that reports match results without writing anything.
- **Ambiguous matches** (multiple candidates with equal confidence) must not be auto-applied; they must be flagged for user review.
- **No match** must be reported clearly; the release is skipped.
- Matching must not depend on provider IDs. There are no provider IDs in AMP.

---

## 14. Privacy and provenance rules

**Forbidden in AMP:**

- Raw provider payloads (ScreenScraper JSON responses or equivalent)
- `"ssuser"` or any provider username/identifier
- API credentials or credential-bearing URLs
- Provider-specific URLs
- Provider package names or identifiers
- Provider branding in any user-facing field
- Any field that surfaces which external provider was the source of a metadata value or media file
- `payloads/`, `metadata/`, or `provider/` directories

**Allowed:**

- Curated Credits text (attribution is not technical provenance)
- User-authored notes and Extra Notes
- Canonical Arkadia metadata fields
- Media files
- AMP format identity fields (`FormatName`, `FormatVersion`)

Credits are attribution. They are not technical provenance. A credits string like
`"Artwork community, public domain"` is allowed; a credits string like
`"ScreenScraper game ID 4512"` is not.

---

## 15. Export behaviour (implemented — Phase 3)

1. `AmpExportPlanService.PlanExport()` collects the scope (DAT line, system, or releases).
2. Collect canonical metadata for each release in scope.
3. Collect curated/accepted media files (`is_excluded = 0`, not deleted); verify SHA-256 for each.
4. Include preferred flags, credits, Extra Notes, and exclusion hashes.
5. Skip: raw provider payloads, deleted media, provider URLs, provider IDs.
6. `AmpExportPlan` is produced — a dry-run report showing counts, missing media, hash failures, and duplicate paths. No write occurs until the user confirms.
7. `AmpExportWriterService.Write()` writes the `.amp` archive:
   - Validates all media files (exists, non-zero, SHA-256 match).
   - Pre-serialises all JSON payloads.
   - Builds `hashes/files.sha256.json` from in-memory hashes of JSON files plus declared SHA-256 of media files.
   - Writes ZIP to a `.tmp` file; moves atomically to the output path on success; deletes `.tmp` on failure.
   - Computes and returns the SHA-256 of the complete `.amp` file.

---

## 16. Import / apply behaviour (planned — Phases 9–10)

1. Open `.amp` and verify package structure (see §17).
2. Run dry-run release matching; produce a report:
   - Matched releases
   - Ambiguous matches (flagged, not auto-applied)
   - Unmatched releases (skipped)
   - Metadata fields that would change
   - Media files that would be added
   - Conflicts with existing media
   - Exclusions that would be merged
3. Present report to user. User confirms or cancels.
4. Apply:
   - Empty metadata fields only (by default).
   - New media files only (by default; no silent overwrite of existing).
   - Merge exclusions.
   - Preserve local credits, preferred state, and Extra Notes unless user explicitly chooses otherwise.
5. No online calls during import.

---

## 17. Verification (implemented — Phase 4)

`AmpPackageVerifierService.Verify(path)` returns an `AmpPackageVerificationResult`.

**Status values:**

| Status | Meaning |
|---|---|
| `Valid` | No errors or warnings. Package is safe to use. |
| `Warning` | No errors, but warnings present. Package is readable; inspect issues. |
| `Error` | One or more errors. Package should not be imported without resolution. |

**Checks (in order):**

| # | Area | Check | Severity on failure |
|---|---|---|---|
| 1 | File | `.amp` file exists | Error (early exit) |
| 2 | File | ZIP is readable (not corrupt) | Error (early exit) |
| 3 | Manifest | `manifest.json` present | Error |
| 4 | Releases | `releases.json` present | Error |
| 5 | Hashes | `hashes/files.sha256.json` present | Error |
| 6 | Exclusions | `curation/exclusions.json` present | Warning |
| 7 | Notes | `curation/notes.json` present | Warning |
| 8 | Paths | Backslash in any ZIP entry path | Error |
| 9 | Paths | Absolute path (leading `/`) | Error |
| 10 | Paths | Path traversal (`..` segment) | Error |
| 11 | Paths | Empty path segment (`//`) | Error |
| 12 | Paths | Unknown root directory | Warning |
| 13 | Paths | Duplicate ZIP entry names | Error |
| 14 | Manifest | `manifest.json` valid JSON | Error |
| 15 | Releases | `releases.json` valid JSON | Error |
| 16 | Hashes | `hashes/files.sha256.json` valid JSON | Error |
| 17 | Exclusions | `curation/exclusions.json` valid JSON | Warning |
| 18 | Notes | `curation/notes.json` valid JSON | Warning |
| 19 | Manifest | Required fields present (`FormatName`, `FormatVersion`, `HardwareFamilyId`, `DatLineId`, `SystemName`, `ReleaseCount`, `MediaFileCount`) | Error per missing field |
| 20 | Manifest | `FormatName == "Arkadia Media Pack"` | Error |
| 21 | Manifest | `FormatVersion == "1"` | Warning |
| 21a | Manifest | `Attribution` block present | Warning |
| 21b | Manifest | `Attribution.Notice` non-empty | Warning |
| 21c | Manifest | `Attribution.GeneralCredits` non-empty | Warning |
| 22 | Releases | Root element is a JSON array | Error |
| 23 | Releases | Required fields per release (`ReleaseId`, `DatName`) | Error |
| 24 | Releases | Duplicate `ReleaseId` | Error |
| 25 | Releases | Required media entry fields (`MediaType`, `ArchivePath`, `FileName`, `Sha256`, `SizeBytes`) | Error per missing |
| 26 | Releases | Duplicate `ArchivePath` | Error |
| 27 | Consistency | `manifest.ReleaseCount` vs actual release count | Warning |
| 28 | Consistency | `manifest.MediaFileCount` vs actual media entry count | Warning |
| 29 | Hashes | Required hash entry fields (`Path`, `Sha256`, `SizeBytes`) | Error per missing |
| 30 | Hashes | Required JSON files listed in hash manifest | Warning per missing |
| 31 | Hashes | Hash entry `Path` exists as a ZIP entry | Warning |
| 32 | Hashes | SHA-256 of ZIP entry matches recorded value | Error |
| 33 | Hashes | `SizeBytes` matches ZIP entry uncompressed length | Warning |
| 34 | Media | Media in `releases.json` present in ZIP | Warning |
| 35 | Media | Media ZIP entry is non-zero bytes | Error |
| 36 | Media | Media in `releases.json` listed in hash file | Warning |
| 37 | Media | ZIP `media/` entries not referenced in `releases.json` | Info |
| 38 | ForbiddenContent | Error-level forbidden tokens present in JSON files | Error |
| 39 | ForbiddenContent | Warning-level provider tokens present in JSON files | Warning |

**Result fields:**

`AmpPackageVerificationResult` exposes:

- `AmpFilePath`, `FileName`, `FileExists`, `ZipReadable`
- `ManifestPresent`, `ManifestValid`, `ReleasesPresent`, `ReleasesValid`, `HashFilePresent`, `HashFileValid`
- `ManifestReleaseCount`, `ManifestMediaFileCount`
- `ReleasesReleaseCount`, `ReleasesMediaFileCount`
- `HashFileCount`
- `MediaFilesFound`, `MediaFilesMissing`, `ZeroByteMediaFiles`, `Sha256Mismatches`
- `ForbiddenContentViolations`, `DuplicateReleaseKeys`, `DuplicateArchivePaths`
- `Issues` — list of `AmpPackageVerificationIssue(Severity, Area, Message)`
- `HasErrors`, `HasWarnings`, `Status` — computed properties
- `ToReport()` — formatted multi-line text report

---

## 18. Forbidden content detail

The verifier scans the text content of all JSON files in the package for forbidden tokens.

**Error-level tokens** (presence is an Error — provider credentials or identity leakage):

| Token | Reason |
|---|---|
| `"ssuser"` | ScreenScraper user key — must be sanitised out of all payloads |
| `devid=` | ScreenScraper developer credential parameter |
| `devpassword=` | ScreenScraper developer credential parameter |
| `ssid=` | ScreenScraper user credential parameter |
| `sspassword=` | ScreenScraper user credential parameter |
| `\u0026devid` | Unicode-escaped form of `&devid=` (produced by `JsonSerializer`) |
| `\u0026ssid` | Unicode-escaped form of `&ssid=` (produced by `JsonSerializer`) |

**Warning-level tokens** (presence is a Warning — provider naming or provenance fields):

| Token | Reason |
|---|---|
| `screenscraper` | Provider name appearing in content (see exception below) |
| `scrapedAtUtc` | Provider scrape timestamp — must not travel in AMP |
| `release_provider_payloads` | Internal Arkadia provider payload table name |
| `release_metadata_proposals` | Internal Arkadia metadata proposal table name |
| `release_metadata_field_state` | Internal Arkadia field state table name |

**Attribution allowlist — forbidden scanner exception:**

The `Attribution` object in `manifest.json` contains approved generic attribution text
that intentionally references community source names, including `ScreenScraper community`.
The verifier excludes the `Attribution` block from the forbidden content scan to avoid
false positives from the approved `GeneralCredits` text. All other JSON files and all
non-Attribution fields in `manifest.json` remain fully scanned.

**Intentionally not scanned as errors:**

Broad terms such as `source`, `provider`, `api`, `http://`, `https://`, `password` are not
scanned because they generate too many false positives in titles, descriptions, and credits
fields. The forbidden token list is intentionally narrow and targets tokens that are
unambiguous indicators of provider credential leakage or internal schema exposure.

---

## 19. Relation to ScreenScraper Cache

- A ScreenScraper Cache ZIP is a **bootstrap/source artefact** — it seeds the scraping pipeline with provider data.
- AMP is the **curated output** of that pipeline.
- Provider cache data may contribute media and metadata that eventually becomes part of an AMP package after curation review.
- AMP must not contain ScreenScraper Cache package structure, raw payload directories, or ScreenScraper identity fields.
- Once an AMP package exists for a system, repeated offline application should prefer AMP over re-applying a provider cache package, because AMP carries curated/accepted state rather than raw proposals.

---

## 20. Relation to `.ark` backups

- `.ark` is the **Arkadia Backup / Archive** format. v0.5 is implemented; see [ARK v0.5 Specification](ARKADIA_BACKUP_ARCHIVE_V0_5_SPEC.md).
- `.ark` packages may contain SQLite database state, indexes, application settings, and local configuration. They are primarily private and environment-specific.
- `.ark` packages may preserve internal technical provenance if needed for audit, debug, or state restore — this is not a user-facing curation concern.
- AMP must remain portable, provider-agnostic, and distribution-suitable. ARK may be restore-oriented and implementation-coupled.
- The two formats must never be conflated. An AMP is not a backup. An ARK is not a media pack.

---

## 21. Future / not v1

The following are explicitly deferred and not part of the v1 specification:

- Multi-chunk AMP (target ~5 GB per chunk)
- AMP downloader and mirror support
- Package signing and verification keys
- Delta / incremental AMP updates
- Compression strategy selection
- ES-DE / EmulationStation export integration
- Import conflict resolution UI
- Public package validation rules and registry
- AMP index / catalogue server
- `.ark` backup implementation
- Remote Internet Media Archive download
- Multi-system AMP — **not in v1**; one package targets exactly one `HardwareFamilyId` and one `DatLineId`. Multi-system support complicates release identity, import matching, conflict resolution, registry indexing, coverage reporting, and UI filtering; it may be revisited after import/registry design.
- Package-level credits / legal metadata — **implemented in v1** as `Attribution.Notice` and `Attribution.GeneralCredits` in `manifest.json`. Seeded automatically by `AmpExportWriterService`. See §6.
- `.sha256` sidecar file — `AmpExportWriteResult` already returns the package SHA-256 and Phase 5 may surface it in the UI, but no external `MyPack.amp.sha256` file is written. A sidecar becomes relevant for remote distribution/download: a consumer should verify the external package hash before trusting internal package contents.

---

## 22. Implementation phases

| Phase | Description | Status |
|---|---|---|
| 1 | AMP export dry-run / planning service (`AmpExportPlanService`) | **Complete** |
| 2 | Catalog dry-run report dialog (`AmpExportReportDialog`) | **Complete** |
| 3 | AMP writer service (`AmpExportWriterService`) | **Complete** |
| 4 | AMP package verifier service (`AmpPackageVerifierService`) | **Complete** |
| 5 | Catalog Create AMP action (trigger write from UI) | **Complete** |
| 6 | Local AMP registry under `scrape-cache/arkadia-media-packs/` | **Complete** |
| 7A | Providers UI — Arkadia Media Packs panel | **Complete** |
| 7B | Default Attribution block seeded in every generated AMP | **Complete** |
| 8 | Offline scrape adapter from AMP | Planned |
| 9 | AMP import/apply dry-run | Planned |
| 10 | AMP apply / import | Planned |
| Later | Remote Internet Media Archive download | Future / post-v1 |

---

## 23. Open questions

Previously open questions have been classified. The following remain genuinely open,
pending design decisions in later phases:

**Still open:**

- **Release identity priority for import matching (Phase 9)** — the priority order
  (`ReleaseId` → `DatName` → `Title`/`OriginalTitle` → system+title) is documented in §13
  but is not yet exercised by code. The exact tie-breaking rules and ambiguous-match
  thresholds remain to be defined at Phase 9 implementation time.

- **Import conflict resolution UI (Phases 9–10)** — how the user reviews and resolves
  ambiguous release matches, media conflicts, and exclusion merges is a UX design question
  deferred to Phase 9.

**Classified — no longer open:**

| Question | Decision |
|---|---|
| Multi-system AMP | **Not in v1.** One package = one `HardwareFamilyId` + one `DatLineId`. See §21. |
| Structured exclusions (`MediaType`, `Reason`, `CreatedAtUtc`) | **Deferred.** Current v1 exports hash-only exclusions (`MediaType = ""`). Must be revisited before import/apply (Phase 10). Not required before Phase 5. See §10. |
| Package-level credits / legal metadata | **Implemented.** `Attribution.Notice` and `Attribution.GeneralCredits` seeded by writer in every generated AMP. Generic, provider-agnostic. See §6. |
| `.sha256` sidecar file | **Deferred** to remote distribution/download phase. Local creation does not emit a sidecar. SHA-256 is displayed in the UI after Create AMP. See §21. |
