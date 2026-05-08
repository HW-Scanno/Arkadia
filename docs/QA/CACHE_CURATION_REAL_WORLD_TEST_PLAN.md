# Arkadia Cache & Curation Pipeline — Real-World Test Plan

_Last revised: 2026-05-08. Companion to [docs/CACHE_CURATION_PIPELINE.md](../CACHE_CURATION_PIPELINE.md). Run on a clean working copy or a snapshot you can roll back._

This document is the official manual QA checklist for the cache and curation pipeline. Each section is a numbered series of `Step / Action / Expected / Failure notes`. Tick the **Pass / Fail** box per step. Use the Failure Log Template at the end to capture defects.

Confirmed conventions used throughout:
- All folders are created under `AppContext.BaseDirectory` (the application directory).
- Staging path: `staging-cache/screenscraper/<package>/`.
- Output ZIP path: `scrape-cache/screenscraper/<package>.zip`.
- `incoming-media/` is the default source folder for manual media intake; Arkadia never auto-deletes files from it.
- Extra Notes placeholder when empty: `No extra notes.`
- No Exclude Reason dialog exists; exclusions are stored without a reason.
- **Exclude** stores SHA-256 and prevents reintroduction. **Delete File** removes the file/row without creating an exclusion; a future scrape or import may reintroduce the asset.
- **Delete File on a Missing asset** removes only the curation row; no filesystem action.
- Offline Single Scrape and Bulk Scraping make **no** online ScreenScraper calls.
- Monitor for ScreenScraper hosts (`screenscraper.fr`, `neoclone.screenscraper.fr`) during offline tests; any traffic to these hosts during §10–§11 is a failure.
- `physical-media` is an incoming alias; canonical media type is `physical`.
- Missing payload / missing media → **Warning**. Zero-byte payload / media → **Error**.
- Verify Package tolerates extra unindexed files in the ZIP.
- Dashboard SCRAPE STAGING Top 5 is sorted by disk size descending and is read-only.

---

## 1. Environment Setup

| # | Action | Expected | Failure notes | Pass/Fail |
|---|---|---|---|---|
| 1.1 | Pick or build a clean working copy of Arkadia on the target machine. | App launches without prior catalog state, or with a snapshot you can restore. | Note OS version, .NET runtime, and Arkadia build hash. | ☐ |
| 1.2 | Confirm `AppContext.BaseDirectory` contents include `incoming-csv/`, `incoming-media/`, `scrape-cache/`, `scrape-cache/screenscraper/`, `staging-cache/`. | All five folders exist after first launch. | If any are missing, capture which and check launch logs. | ☐ |
| 1.2a | Place at least 2–3 test image files (e.g. PNG/JPG) into `incoming-media/` for use in §12 import tests. | Files are present and accessible. | — | ☐ |
| 1.3 | Confirm `staging-cache/screenscraper/` is **not** required to exist before a build. | Folder is absent until the first build runs. | If it pre-exists from a leftover snapshot, delete before continuing. | ☐ |
| 1.4 | Have valid ScreenScraper credentials ready (`devid`, `devpassword`, `ssid`, `sspassword`, `softname`). | Credentials work against the live API. | If credentials fail, abort and fix before continuing. | ☐ |
| 1.5 | Pick a small target DAT line (recommended: Atomiswave or similar, ~20–60 entries). | DAT line is imported and visible in Catalog. | If too large, swap for a smaller line — long fetches make iteration painful. | ☐ |
| 1.6 | Pick a second DAT line that is unlikely to match anything in the cache (used for No-Match coverage). | DAT line imported. | — | ☐ |
| 1.7 | Open a network monitor (e.g. Fiddler, Wireshark, browser devtools, or `netstat`) able to see traffic to `screenscraper.fr` and `neoclone.screenscraper.fr`. | Monitor active. | Required for offline-call enforcement in §10–§11. | ☐ |

---

## 2. ScreenScraper Settings

| # | Action | Expected | Failure notes | Pass/Fail |
|---|---|---|---|---|
| 2.1 | Open Settings → ScreenScraper. | Settings page renders. | — | ☐ |
| 2.2 | Enter `devid`, `devpassword`, `ssid`, `sspassword`, `softname`. Save. | Save succeeds without validation error. | `softname` is mandatory; missing it should block save. | ☐ |
| 2.3 | Restart Arkadia. Reopen Settings → ScreenScraper. | All five values are still populated. | If any are blank, persistence is broken. | ☐ |
| 2.4 | Clear `softname` and try to save. | Save is rejected with a validation message. | Capture the exact message text. | ☐ |

---

## 3. Cache Builder Happy Path

| # | Action | Expected | Failure notes | Pass/Fail |
|---|---|---|---|---|
| 3.1 | Open Cache Builder for the small DAT line. | Builder dialog opens with sensible defaults. | — | ☐ |
| 3.2 | Pick a package name. Note it for later steps. | Watermark shows `scrape-cache/screenscraper/<name>.zip (auto)`. | If watermark differs, capture screenshot. | ☐ |
| 3.3 | Confirm checkbox defaults: Force off, UpdatePayloads off, KeepStaging on, IndexAfterBuild on. | Defaults match. | — | ☐ |
| 3.4 | Click Start. | Progress advances through fetch/sanitize/download/zip stages. | Capture rate-limit pauses if any. | ☐ |
| 3.5 | On completion, check `scrape-cache/screenscraper/<name>.zip` exists. | ZIP file is present. | If missing, build did not finish — see §4. | ☐ |
| 3.6 | Open the ZIP externally. | Layout is `manifest.json`, `gameslist.csv`, `payloads/<gameId>.json`, `media/<type>/<file>`. | List any unexpected entries. | ☐ |
| 3.7 | Open Registered Cache Manager. | The new package appears with status `Available`. | If not auto-registered, check that `IndexAfterBuild` was on. | ☐ |
| 3.8 | Confirm `staging-cache/screenscraper/<name>/` still exists (KeepStaging on). | Folder retained. | — | ☐ |

---

## 4. Resumable Build

| # | Action | Expected | Failure notes | Pass/Fail |
|---|---|---|---|---|
| 4.1 | Start a fresh build for a new package name. | Build begins. | Use a name not previously used. | ☐ |
| 4.2 | Interrupt before completion (close window or kill process) once at least 5 payloads are staged. | App closes. | — | ☐ |
| 4.3 | Restart Arkadia, open Manage Staging. | The package appears with status **Resumable** and a non-trivial completion %. | If status is **Unknown** or **Empty**, classification is wrong. | ☐ |
| 4.4 | Reopen Cache Builder for the same package name. Start. | Previously fetched payloads are reused; only remaining payloads are fetched. | Inspect logs/progress to confirm reuse. | ☐ |
| 4.5 | Wait for completion. | ZIP appears under `scrape-cache/screenscraper/<name>.zip`. | — | ☐ |
| 4.6 | Manage Staging shows status **Complete** for that package (until KeepStaging cleanup if enabled). | Status flips to **Complete**. | — | ☐ |

---

## 5. Force Rebuild

| # | Action | Expected | Failure notes | Pass/Fail |
|---|---|---|---|---|
| 5.1 | Note the modification timestamps of staged payloads under `staging-cache/screenscraper/<name>/payloads/` and a sample of media files. | Timestamps recorded. | — | ☐ |
| 5.2 | Reopen Cache Builder for the existing package. Enable **Force rebuild**. UpdatePayloads off. Start. | Build runs without skipping due to "already built". | If guard prevents start, Force is broken. | ☐ |
| 5.3 | After completion, re-check the timestamps from 5.1. | Every staged payload was overwritten. Existing media files were removed and re-downloaded. | Spot-check at least 3 payloads and 3 media files. | ☐ |
| 5.4 | Run Verify Package on the resulting ZIP. | Status `Valid`, no sanitization findings. | Force must not break sanitization. | ☐ |

---

## 6. UpdatePayloads

| # | Action | Expected | Failure notes | Pass/Fail |
|---|---|---|---|---|
| 6.1 | Reopen Cache Builder for the existing package. Enable **UpdatePayloads**. Force off. Start. | Builder re-fetches each payload from the API. | Watch network monitor for API calls. | ☐ |
| 6.2 | After completion, inspect a payload that has not changed upstream. | Payload file timestamp is unchanged or content is identical (reused). | — | ☐ |
| 6.3 | If any payload changed upstream during the run, inspect that payload. | Payload was overwritten; only **new/missing** media for that payload was downloaded. Existing media files were not re-fetched. | If existing media is being re-fetched, behavior diverges from spec. | ☐ |
| 6.4 | Run Verify Package on the resulting ZIP. | Status `Valid`. | — | ☐ |

---

## 7. Manage Staging

| # | Action | Expected | Failure notes | Pass/Fail |
|---|---|---|---|---|
| 7.1 | Open Manage Staging. | Dialog lists all `staging-cache/screenscraper/<package>/` folders. | — | ☐ |
| 7.2 | Default sort is by last updated descending, with size as tiebreaker. | Sort order matches. | — | ☐ |
| 7.3 | Verify the four exact status labels are used: **Complete**, **Resumable**, **Unknown**, **Empty**. | No other labels appear. | Capture screenshot of any other label. | ☐ |
| 7.4 | Manually create an empty folder under `staging-cache/screenscraper/zzz-empty/`. Refresh. | Row appears with status **Empty**. | — | ☐ |
| 7.5 | Drop a stray text file into another new folder `zzz-unknown/`. Refresh. | Row appears with status **Unknown**. | — | ☐ |
| 7.6 | Select a row → **Open Folder**. | OS file manager opens the staging folder. | — | ☐ |
| 7.7 | Select a row → **Delete**. Confirm. | Folder is deleted; list refreshes. Completed `*.zip` packages are untouched. | — | ☐ |
| 7.8 | Attempt path-traversal: temporarily symlink a folder outside the provider root and try to delete via the dialog. | Deletion is refused (path-traversal guard rejects non-direct children). | If deletion is accepted, this is a security regression. | ☐ |
| 7.9 | Completion percentage on a Resumable folder follows `payloadCount / totalGames * 100`. | Sanity-check against folder contents. | — | ☐ |

---

## 8. Dashboard SCRAPE STAGING Top 5

| # | Action | Expected | Failure notes | Pass/Fail |
|---|---|---|---|---|
| 8.1 | Have at least 6 staging folders of varying sizes. | Setup ready. | Build small dummy folders if needed. | ☐ |
| 8.2 | Open Dashboard. | SCRAPE STAGING tile shows up to 5 rows. | — | ☐ |
| 8.3 | Rows are sorted by disk size descending. | Largest folder first. | — | ☐ |
| 8.4 | Tile is informational/read-only. No actions are exposed on rows directly. | Clicking rows performs no destructive action. | If actions are present, this contradicts the spec. | ☐ |
| 8.5 | All management actions live in Manage Staging dialog. | Confirm by opening the dialog. | — | ☐ |

---

## 9. Register / Verify Package

| # | Action | Expected | Failure notes | Pass/Fail |
|---|---|---|---|---|
| 9.1 | Open Registered Cache Manager. Click **Register Package**, pick the freshly built ZIP. | Status message: `Registered: <n> games, <m> media entries.` | — | ☐ |
| 9.2 | Click **Register Package** again with the same ZIP. | Status message: `Package already registered.` | No duplicate row. | ☐ |
| 9.3 | Click **Refresh**. | List reloads from catalog DB; ZIP existence/status updates. **Refresh does not re-index ZIP contents.** | If contents look re-parsed, refresh has changed scope. | ☐ |
| 9.4 | Click **Verify** on the package. | Verification runs; report dialog shows Status `Valid`, no errors. | — | ☐ |
| 9.5 | Move the ZIP file out of `scrape-cache/screenscraper/` to a temp location. Click **Refresh**. | Row status flips to `Missing`; **Verify** and **Delete File + Detach** buttons disable. | — | ☐ |
| 9.6 | Move the ZIP back. Refresh. | Row returns to `Available`. | — | ☐ |
| 9.7 | Manually corrupt one payload entry to zero bytes (use an external tool). Verify again. | Result includes Error severity for that payload; overall Status `Error`. | Capture report text. | ☐ |
| 9.8 | Inject `devid=REALVALUE` into another payload. Verify again. | Sanitization Error reported. | — | ☐ |
| 9.9 | Inject a `"ssuser"` block into a payload. Verify again. | Sanitization Error reported. | — | ☐ |
| 9.10 | Add an extra unindexed file inside the ZIP (e.g. `README.txt`). Verify again. | Status remains `Valid`; extra files are tolerated. | — | ☐ |
| 9.11 | Manually delete one payload entry that is indexed. Verify again. | **Warning** reported (not Error). | If Error is reported, severity scheme is wrong. | ☐ |
| 9.12 | Manually delete one media entry that is indexed. Verify again. | **Warning** reported. | — | ☐ |
| 9.13 | **Detach** a package. | Row removed from index; ZIP file remains on disk. | — | ☐ |
| 9.14 | Re-register the same path. **Delete File + Detach.** | ZIP removed from disk; row removed from index. | — | ☐ |

---

## 10. Offline Single Scrape

> Network monitor must be active for this section. Any traffic to `screenscraper.fr` or `neoclone.screenscraper.fr` is a failure.

| # | Action | Expected | Failure notes | Pass/Fail |
|---|---|---|---|---|
| 10.1 | Pick a release with a known cache match. Trigger single scrape. | Proposals appear from the cached payload. | Confirm zero outbound calls to ScreenScraper. | ☐ |
| 10.2 | Accept some fields, reject others. | Only accepted canonical fields are persisted. | Curation rules apply. | ☐ |
| 10.3 | Pick a release with no cache match. Trigger single scrape. | UI reports no result. No error. No online fallback. | — | ☐ |
| 10.4 | From a release with a multi-candidate cache hit, trigger single scrape. | UI presents the candidates for resolution; nothing is auto-applied. | — | ☐ |

---

## 11. Bulk Scraping

> Network monitor must be active for this section. Bulk Scraping is cache-only/offline. Any traffic to `screenscraper.fr` or `neoclone.screenscraper.fr` is a failure.

| # | Action | Expected | Failure notes | Pass/Fail |
|---|---|---|---|---|
| 11.1 | Open Bulk Scraping with a single release selected. Run **Current Release**. | Report shows 1 entry processed. No outbound API calls. | — | ☐ |
| 11.2 | Run **Missing Only**. | Count equals the number of releases with `QualityScore < 6` OR no cover-front file at `data/media/<hwFamilyId>/<datLineId>/covers-front/<release-stem>_*`. | Spot-check by listing both groups. | ☐ |
| 11.3 | Run **Entire DAT** with **Auto-apply empty fields only** enabled. Pre-populate one release with custom values. | Existing canonical metadata is preserved on that release; only empty fields are filled. | — | ☐ |
| 11.4 | Pre-set one release's media as **Preferred** and another release's media as **Excluded**. Run with **Respect excluded media** enabled. | Preferred remains. Excluded is not re-extracted. | — | ☐ |
| 11.5 | Set an Extra Note on a release and Save Credits on a media asset. Run bulk. | Both Extra Notes and Credits remain unchanged. | — | ☐ |
| 11.6 | Trigger a release that yields a multi-candidate cache match in bulk. | Status `Ambiguous`. **No proposals or metadata are written for that release.** | — | ☐ |
| 11.7 | Run a long bulk operation. Press **Stop** mid-run. | Label updates to `Stopped.`, Stop button hides. Already-applied changes from completed releases remain. | — | ☐ |
| 11.8 | Run on the No-Match DAT line. | Most/all rows report `No Match`. No errors. | — | ☐ |
| 11.9 | Confirm parsed canonical fields applied include only: `title`, `original_title`, `developer`, `publisher`, `year`, `languages`, `description`, `genre`, `subgenre`, `players`, `rating`. | No other canonical fields are written by bulk. | — | ☐ |
| 11.10 | Throughout 11.1–11.9, network monitor shows zero traffic to ScreenScraper hosts. | Confirmed offline. | If any traffic appears, capture URL and stack trace. | ☐ |

---

## 12. Manage Media — Media Intake Workbench

| # | Action | Expected | Failure notes | Pass/Fail |
|---|---|---|---|---|
| 12.1 | Open Manage Media for a release with several assets. | Dual-pane workbench opens. Left pane shows asset list grouped by media type; right pane shows the `incoming-media/` browser. Header shows **Previous / N of M / Next** navigation. | — | ☐ |
| 12.2 | Status badges use `Preferred` (green), `Excluded` (red), `Missing` (orange) where applicable. | Colors match. | — | ☐ |
| 12.3 | Confirm Manage Media does **not** display any provider/source provenance field. | No "from ScreenScraper / cache / manual" text anywhere in the dialog. | If present, this contradicts the spec. | ☐ |
| 12.4 | Click **Previous** and **Next** to navigate releases without closing the dialog. | Media list, detail panel, and preview update to the new release. Navigation buttons disable at the first/last release. | — | ☐ |
| 12.5 | Select a non-preferred, existing, non-excluded asset. Click **Set Preferred**. | Asset becomes Preferred for its media type; previous Preferred for that type is cleared. | — | ☐ |
| 12.6 | Click **Exclude** on an existing asset. | Badge becomes `Excluded`; **Restore** enabled, **Set Preferred** disabled. **No Exclude Reason dialog appears.** Inspect DB: `is_excluded = 1` and `file_sha256` is populated. | If a reason dialog opens, or SHA-256 is null, behavior diverges from spec. | ☐ |
| 12.7 | Manually delete the excluded file from disk externally. Reload dialog. | Asset still appears as **Missing / Excluded**. The exclusion row survives file removal. | If the row disappears, exclusion persistence is broken. | ☐ |
| 12.8 | Click **Restore** on an excluded asset. | Asset returns to default status; `is_excluded = 0`. | — | ☐ |
| 12.9 | Edit the Credits field and click **Save Credits**. Reload dialog. | Credits persist. | — | ☐ |
| 12.10 | Select an existing asset. Click **Open File**. | OS opens the file. | — | ☐ |
| 12.11 | Click **Open Folder** on an existing asset. | OS reveals the file in its folder. | — | ☐ |
| 12.12 | **Delete File on an existing active asset**. Confirm. | File removed from disk. Curation row removed from DB. Asset disappears from the list. **No exclusion row is created.** | If asset remains listed, or `is_excluded` appears in DB, this is a regression. | ☐ |
| 12.13 | Run Bulk Scraping on the same release after the delete in 12.12 (with "Respect excluded media" on). | Deleted asset may be reintroduced from cache — this is correct behavior, since no exclusion was stored. | Confirm no ghost exclusion prevents it. | ☐ |
| 12.14 | **Delete File on a Missing asset** (file already absent from disk). Confirm. | Curation row removed from DB. Asset disappears from list. No filesystem action taken. No exclusion row created. | — | ☐ |
| 12.15 | **Exclude** an asset, then delete the file externally. Verify the Missing/Excluded row exists. Then click **Delete File**. Confirm. | Curation row (including the exclusion) removed. Asset disappears. Future scrapes/imports may now reintroduce it. | — | ☐ |
| 12.16 | Open the Incoming Media right pane. Confirm `incoming-media/` contents are listed. | Files placed in 1.2a are visible. | — | ☐ |
| 12.17 | Select an image file in the right pane. | Preview renders. Import button becomes enabled. | — | ☐ |
| 12.18 | Click **Import** with **Delete after import** unchecked. | File copied into `data/media/<hwFamilyId>/<datLineId>/<media-type>/`. Curation row created. Source file **remains** in `incoming-media/`. Asset appears in the left pane. | — | ☐ |
| 12.19 | Click **Import** on a second file with **Delete after import** checked. | Same as above, but source file is deleted from `incoming-media/` **after** successful copy and SHA-256 verification. | If source is deleted before verification, or survives after, the workflow is broken. | ☐ |
| 12.20 | Use **Browse…** to point the right pane at a different folder. | Pane refreshes to show that folder's contents. | — | ☐ |
| 12.21 | Set the **target media type** to a specific type before import. | Imported asset appears under that media type in the left pane. | — | ☐ |
| 12.22 | Place a zero-byte or corrupt file in `incoming-media/`. Attempt to import it. | Import reports failure gracefully. No curation row created. No partial file in the media tree. | — | ☐ |
| 12.23 | Browse the entire UI. Confirm `physical-media` never appears as a label, badge, type, or filter. | Canonical type is `physical` everywhere. | — | ☐ |
| 12.24 | Inspect `release_media_curation` DB rows after all §12 steps. | No `physical-media` type values. No provider/source provenance fields. | — | ☐ |

---

## 13. Extra Notes

| # | Action | Expected | Failure notes | Pass/Fail |
|---|---|---|---|---|
| 13.1 | Open a release with no notes. | Placeholder reads exactly `No extra notes.` | Any other placeholder text is a failure. | ☐ |
| 13.2 | Type a note and Save. Reopen. | Note persists. | — | ☐ |
| 13.3 | Run Bulk Scraping on the DAT line. | Saved note is unchanged afterward. | — | ☐ |
| 13.4 | Trigger a single scrape and accept all proposals. | Saved note is unchanged afterward. | — | ☐ |
| 13.5 | Empty the note and Save. | Placeholder returns to `No extra notes.` | — | ☐ |

---

## 14. Security Smoke Test

| # | Action | Expected | Failure notes | Pass/Fail |
|---|---|---|---|---|
| 14.1 | Extract a built ZIP. Search payload files for `devid=`, `devpassword=`, `ssid=`, `sspassword=`, `softname=`. | Only placeholder values appear: `<DEVID>`, `<DEVPASSWORD>`, `<SSID>`, `<SSPASSWORD>`, `<SOFTNAME>`. No real values. | Capture any real value found. | ☐ |
| 14.2 | Search payloads for the literal string `"ssuser"`. | Zero matches. | If found, sanitization is broken. | ☐ |
| 14.3 | Search payloads for the `\u0026`-escaped form (e.g. `\u0026devid=`). | Only placeholders, no real values. | — | ☐ |
| 14.4 | Run Verify Package on the ZIP. | Status `Valid`; zero sanitization findings. | — | ☐ |
| 14.5 | Inspect staging payloads under `staging-cache/screenscraper/<package>/payloads/`. | Same sanitization invariants hold at the staging layer. | — | ☐ |

---

## 15. Edge Cases

| # | Action | Expected | Failure notes | Pass/Fail |
|---|---|---|---|---|
| 15.1 | Try to start a build with an empty package name. | Save/start is rejected with a validation message. | — | ☐ |
| 15.2 | Try to register a non-ZIP file. | Registration rejects with a clear error; no DB row created. | — | ☐ |
| 15.3 | Try to register a ZIP missing `manifest.json`. | Registration may succeed but Verify reports `manifest.json` missing as Error. | — | ☐ |
| 15.4 | Run Bulk Scraping with no registered cache packages for the DAT line. | All releases report `No Match`; no online fallback. | — | ☐ |
| 15.5 | Place two registered packages indexing the same release. Run single scrape. | UI presents both candidates (Ambiguous-style). | — | ☐ |
| 15.6 | Build a package whose payload count equals zero (DAT line with no matches at the API). | Build does not produce a final ZIP; staging remains for inspection. | — | ☐ |
| 15.7 | Create a `staging-cache/screenscraper/` subfolder with a name containing path-traversal characters (e.g. `..hack`). | Manage Staging tolerates the listing; Delete is refused if the path is not a direct child. | — | ☐ |
| 15.8 | Manually edit a `release_media_curation` row to set a legacy `physical-media` value. Restart Arkadia. | DAT-line migration normalizes the row to `physical`. | — | ☐ |
| 15.9 | Add Media for a file with an extension not in the picker filters via "All Files". | File is accepted; appears in list with generic preview. | — | ☐ |
| 15.10 | Press Stop in Bulk Scraping immediately after Start (before the first iteration completes). | Run cancels; no proposals or media writes occur. UI shows `Stopped.` | — | ☐ |

---

## 16. Acceptance Criteria

The pipeline is accepted as production-ready for this release when **all** of the following hold:

- [ ] All §1–§15 steps are Pass, with any Fails resolved or explicitly waived in the Failure Log.
- [ ] No outbound traffic to `screenscraper.fr` or `neoclone.screenscraper.fr` was observed during §10 and §11.
- [ ] Verify Package on at least one freshly-built and one corruption-injected ZIP behaves per the documented severity scheme.
- [ ] Manage Staging surfaces only the four labels: **Complete**, **Resumable**, **Unknown**, **Empty**.
- [ ] Dashboard SCRAPE STAGING Top 5 is sorted by disk size descending and exposes no destructive actions.
- [ ] `incoming-media/` exists at startup; files placed there appear in the Manage Media Incoming Media pane.
- [ ] Import (Copy to Arkadia) creates a curation row only after SHA-256 verification. Source file is not deleted unless Delete after import is checked.
- [ ] Delete File on an existing asset removes the file from disk and the curation row from DB. **No exclusion row is created.**
- [ ] Delete File on a Missing asset removes only the curation row; no filesystem action.
- [ ] Exclude persists as a Missing/Excluded row after the file is deleted from disk.
- [ ] Manage Media never displays `physical-media` and never displays provider/source provenance.
- [ ] Extra Notes placeholder is exactly `No extra notes.` and survives single + bulk scraping.
- [ ] No Exclude Reason dialog is present.
- [ ] Bulk Scraping preserves Extra Notes, Credits, Preferred, Excluded, and non-empty canonical metadata.
- [ ] Sanitization Smoke Test (§14) finds no real credentials and no `ssuser` in any payload.

---

## 17. Failure Log Template

For each failed step, fill in one entry. Keep these in `docs/QA/FAILURES/<date>-<short-slug>.md` or paste below.

```
### Failure <ID>

- Date / time:           YYYY-MM-DD HH:MM (TZ)
- Tester:                <name>
- Build / commit:        <git hash>
- OS / runtime:          <OS version> / .NET <version>
- Test plan section:     §<n>
- Step:                  <e.g. 11.4>
- Action attempted:      <verbatim from step>
- Expected:              <verbatim from step>
- Observed:              <what actually happened>
- Severity:              Blocker | Major | Minor | Cosmetic
- Logs / screenshots:    <paths or attachments>
- Network monitor notes: <traffic seen, if any>
- Suspected area:        <module / dialog / service>
- Reproduction steps:    1. …
                         2. …
                         3. …
- Workaround (if any):   <text>
- Linked issue:          <tracker URL>
```

End of test plan.
