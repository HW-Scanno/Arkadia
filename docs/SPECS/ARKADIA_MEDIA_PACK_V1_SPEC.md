# Arkadia Media Pack v1 — Specification

**Status:** Planned. Not implemented. No export, import, or verifier code exists in the current build.

**Last updated:** 2026-05-08

---

> **Core product rule:**
> **AMP is not a backup. ARK is not a media pack.**
>
> `.amp` — Arkadia Media Pack — curated, distributable, provider-agnostic media/metadata package.
> `.ark` — Arkadia Backup / Archive — reserved for future backup/restore packages of internal application state.
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
- **v1 container:** ZIP-compatible archive (to be confirmed at implementation time; another container may be chosen if justified).
- **Format identity** is determined by the presence and validity of `manifest.json` inside the archive, not by the file extension alone.
- **MIME type label:** `application/x-arkadia-media-pack`
- `.ark` is reserved for Arkadia Backup / Archive and **must not** be used for AMP packages.

---

## 4. Proposed v1 archive layout

```
manifest.json           — package identity, version, scope, counts, hash algorithm
releases.json           — release records with identity fields and canonical metadata
media/
  cover-front/
  cover-back/
  cover-spine/
  cover-wrap/
  screenshot/
  screenshot-title/
  logo/
  logo-hd/
  video/
  physical/
  physical-texture/
  manual/
  marquee/
  flyer/
curation/
  exclusions.json       — SHA-256 hashes of rejected assets with release association
  notes.json            — extra notes per release (user-authored curation text)
hashes/
  files.sha256.json     — SHA-256 for every media file listed in the package
```

**Forbidden directories / files:**

- No `payloads/` directory or any raw provider JSON.
- No `ssuser` field anywhere.
- No credentials or credential placeholders.
- No provider URLs.
- No source package names or provider branding.
- No visible provider IDs in any user-facing field.

---

## 5. `manifest.json`

Required fields:

| Field | Type | Description |
|---|---|---|
| `format` | string | Must be `"arkadia-media-pack"` |
| `ampVersion` | string | Spec version, e.g. `"1.0"` |
| `packageId` | string (UUID or slug) | Stable unique identifier for this package |
| `packageName` | string | Human-readable name |
| `systemId` | string | Arkadia hardware family / system identifier |
| `systemName` | string | Display name of the system |
| `hardwareFamilyId` | string | Internal hardware family ID |
| `datLineId` | string or null | DAT line scope, if single-DAT package |
| `createdAtUtc` | ISO 8601 | Creation timestamp |
| `createdByApp` | string | `"Arkadia"` |
| `createdByVersion` | string | App version string |
| `releaseCount` | integer | Number of releases in `releases.json` |
| `mediaCount` | integer | Total media files included |
| `totalBytes` | integer | Sum of all media file sizes |
| `hashAlgorithm` | string | `"SHA-256"` |
| `credits` | string or null | Attribution text for the package as a whole |
| `notes` | string or null | Free-form curator notes |
| `legalNote` | string or null | Distribution restriction or licensing note |

Example:

```json
{
  "format": "arkadia-media-pack",
  "ampVersion": "1.0",
  "packageId": "a3f8c2b1-0e44-4d6a-9e12-112233445566",
  "packageName": "Atomiswave Complete Media Pack",
  "systemId": "atomiswave",
  "systemName": "Atomiswave",
  "hardwareFamilyId": "atomiswave",
  "datLineId": "atomiswave-mame-001",
  "createdAtUtc": "2026-05-08T14:00:00Z",
  "createdByApp": "Arkadia",
  "createdByVersion": "2.0.0",
  "releaseCount": 47,
  "mediaCount": 312,
  "totalBytes": 186432000,
  "hashAlgorithm": "SHA-256",
  "credits": "Media curated from public domain and freely redistributable sources.",
  "notes": null,
  "legalNote": "Distribute only media confirmed as freely redistributable in your jurisdiction."
}
```

---

## 6. Release identity and matching

Each release entry in `releases.json` carries identity fields used to match against the target catalog on import.

**Identity priority (highest to lowest):**

1. Arkadia `release_id` — if the target catalog was built from the same DAT line, this is the most reliable match.
2. DAT entry name / ROM shortname — the authoritative technical name from the DAT file.
3. Title + original title — human-facing metadata match.
4. System + title combination — fallback if release_id and DAT name are absent.

**Matching rules:**

- Import must support a **dry-run** that reports match results without writing anything.
- **Ambiguous matches** (multiple candidates with equal confidence) must not be auto-applied; they must be flagged for user review.
- **No match** must be reported clearly; the release is skipped.
- Matching must not depend on provider IDs. There are no provider IDs in AMP.

---

## 7. Canonical metadata

AMP may carry canonical Arkadia metadata for each release. These are the same fields stored in `release_metadata`:

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
- `region` (if applicable)

**Rules:**

- Store Arkadia canonical values, not raw provider field values.
- On import, apply to empty fields only by default.
- Existing user-edited or locked metadata must not be overwritten without explicit user action.
- AMP carries no `source` or `provider` column for metadata — provenance is not surfaced.

---

## 8. Media entries

Each entry in `hashes/files.sha256.json` describes one media file:

| Field | Type | Description |
|---|---|---|
| `releaseKey` | string | DAT entry name or release_id used for matching |
| `mediaType` | string | e.g. `cover-front`, `screenshot`, `logo-hd` |
| `archivePath` | string | Path inside the AMP archive |
| `fileName` | string | Original file name |
| `sha256` | string | SHA-256 hex digest |
| `size` | integer | File size in bytes |
| `preferred` | boolean | Whether this is the preferred asset for its type/release |
| `credits` | string or null | Attribution for this specific asset |
| `notes` | string or null | Optional curator note |

**Rules:**

- Include accepted/curated media only. No deleted or missing assets.
- Excluded assets are represented in `curation/exclusions.json` by hash only — not bundled as files.
- Preferred status and credits travel with the asset entry.
- Provider identity, source URLs, and provider branding are forbidden.

---

## 9. Exclusions

`curation/exclusions.json` carries rejection decisions as hashes, not files.

| Field | Type | Description |
|---|---|---|
| `sha256` | string | SHA-256 of the rejected asset |
| `releaseKey` | string | Release association |
| `mediaType` | string or null | Media type if known |
| `reason` | string or null | Reserved — for a future Exclude Reason dialog |
| `createdAtUtc` | ISO 8601 or null | When the exclusion was recorded |

**Rules:**

- **Delete** is not an exclusion. Delete removes the local file/record and does not prevent reintroduction.
- **Exclude** records a rejection hash and prevents reintroduction.
- AMP import merges exclusions: a rejected asset that arrives in an AMP import will not be introduced if its hash matches a local exclusion, and AMP exclusions should be merged into the local exclusion set.

---

## 10. Extra Notes

`curation/notes.json` carries per-release extra notes.

| Field | Type | Description |
|---|---|---|
| `releaseKey` | string | Release association |
| `notes` | string | User-authored curator text |
| `updatedAtUtc` | ISO 8601 or null | Last edit timestamp |

**Rules:**

- Extra notes are user-owned curation text, not provider metadata.
- Import should preserve existing local extra notes by default. Overwrite or merge must be an explicit user choice.

---

## 11. Privacy and provenance rules

**Forbidden in AMP:**

- Raw provider payloads (ScreenScraper JSON responses or equivalent)
- `ssuser` or any provider username/identifier
- API credentials or credential placeholders
- Provider-specific URLs
- Provider package names or identifiers
- Provider branding in any user-facing field
- Any field that surfaces which external provider was the source of a metadata value or media file

**Allowed:**

- Curated Credits text (attribution is not technical provenance)
- User-authored notes and extra notes
- Canonical Arkadia metadata fields
- Media files
- Internal AMP format fields identifying the package as Arkadia/AMP (e.g. `"format": "arkadia-media-pack"`)

Credits are attribution. They are not technical provenance. A credits string like `"Artwork community, public domain"` is allowed; a credits string like `"ScreenScraper game ID 4512"` is not.

---

## 12. Export behaviour (planned)

1. User selects scope: DAT line, system, or individual releases.
2. Collect canonical metadata for each release in scope.
3. Collect curated/accepted media files; verify SHA-256 for each.
4. Include preferred flags, credits, extra notes, and exclusion hashes.
5. Skip: raw provider payloads, deleted media, provider URLs, provider IDs.
6. Produce an export report (counts, missing media, hash failures) before committing to disk.
7. Write `.amp` archive.

---

## 13. Import / apply behaviour (planned)

1. Open `.amp` and verify package structure (see §14).
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
   - Preserve local credits, preferred state, and extra notes unless user explicitly chooses otherwise.
5. No online calls during import.

---

## 14. Verification

The AMP verifier checks:

- Archive is readable (not corrupt or truncated).
- `manifest.json` exists and passes schema validation.
- `manifest.format` is `"arkadia-media-pack"`.
- `releases.json` exists and is valid JSON.
- All media files listed in `hashes/files.sha256.json` are present in the archive.
- No zero-byte media files.
- SHA-256 of each media file matches the declared hash.
- No forbidden provider/provenance fields present.
- No raw payload directories (`payloads/`, `metadata/`, or equivalent provider artefact directories).
- No duplicate release keys (unless explicitly allowed by spec version).
- No duplicate archive paths.
- Report severity levels: error (import blocked), warning (import allowed with caution), info.

---

## 15. Relation to ScreenScraper Cache

- A ScreenScraper Cache ZIP is a **bootstrap/source artefact** — it seeds the scraping pipeline with provider data.
- AMP is the **curated output** of that pipeline.
- Provider cache data may contribute media and metadata that eventually becomes part of an AMP package after curation review.
- AMP must not contain ScreenScraper Cache package structure, raw payload directories, or ScreenScraper identity fields.
- Once an AMP package exists for a system, repeated offline application should prefer AMP over re-applying a provider cache package, because AMP carries curated/accepted state rather than raw proposals.

---

## 16. Relation to `.ark` backups

- `.ark` is reserved for **Arkadia Backup / Archive** packages. This format is not yet specified or implemented.
- `.ark` packages may contain SQLite database state, indexes, application settings, and local configuration. They are primarily private and environment-specific.
- `.ark` packages may preserve internal technical provenance if needed for audit, debug, or state restore — this is not a user-facing curation concern.
- AMP must remain portable, provider-agnostic, and distribution-suitable. ARK may be restore-oriented and implementation-coupled.
- The two formats must never be conflated. An AMP is not a backup. An ARK is not a media pack.

---

## 17. Future / not v1

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

---

## 18. Implementation phases

| Phase | Description | Status |
|---|---|---|
| 0 | Spec only | Current |
| 1 | AMP export dry-run / report | Not started |
| 2 | Single-file `.amp` export | Not started |
| 3 | AMP verifier | Not started |
| 4 | AMP import dry-run | Not started |
| 5 | AMP apply / import | Not started |
| 6 | Chunking, download, mirrors | Future / post-v1 |

---

## 19. Open questions

- Exact release identity priority order (release_id vs. DAT name vs. title) — to be finalised at implementation time.
- Exact archive layout — the layout in §4 is proposed; final structure may differ.
- Whether `manifest.json` includes a global content hash over all included files.
- Credits / legal metadata model — how attribution travels for individually credited assets vs. package-level credits.
- Conflict resolution UI design for import.
- Whether v1 uses a strict ZIP container or allows a different archive format.
- How to handle packages that span multiple DAT lines or multiple systems (multi-system AMP).
- Whether AMP is always per-system or can be multi-system in a single file.
