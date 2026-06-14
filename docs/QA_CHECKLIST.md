# Arkadia — Volume & Archive QA Checklist

Manual QA checklist for volume and archive workflows. Run against a test installation with a small DAT line (20–60 entries recommended). For cache/curation QA, see [QA/CACHE_CURATION_REAL_WORLD_TEST_PLAN.md](QA/CACHE_CURATION_REAL_WORLD_TEST_PLAN.md).

_Last revised: 2026-06-14. Companion to [ARCHIVE_AND_VOLUME_MODEL.md](ARCHIVE_AND_VOLUME_MODEL.md), [VOLUME_WORKFLOWS.md](VOLUME_WORKFLOWS.md), and [VERIFY_ARCHIVE.md](VERIFY_ARCHIVE.md)._

---

## Environment setup

| # | Action | Expected |
|---|---|---|
| E1 | Build: `dotnet build Arkadia.sln -c Release` | 0 errors, 0 warnings |
| E2 | Test: `dotnet test Arkadia.sln -c Release` | 1480 tests passing, 0 failures |
| E3 | Confirm `archive\<platform>\<datLine>\` has at least 3 known-good artifacts | SHA-1s match DB |
| E4 | Confirm at least one workspace volume exists under `volumes\` | Volume root directory exists |

---

## §1 Verify Archive — baseline scan

| # | Action | Expected | Failure notes |
|---|---|---|---|
| 1.1 | Open Operations → Verify Local Archive for the test DAT line | Dialog opens; scan starts automatically | |
| 1.2 | Wait for scan to complete | Phase label shows "Scan complete — archive is clean." or repairable count | |
| 1.3 | SCANNED stat matches number of physical files in `archive\<platform>\<datLine>\` | Counts match | If counts differ, check for hidden files or read errors |
| 1.4 | WANTED OK stat > 0 | Files classified correctly | |
| 1.5 | AbsentFromArchiveCount shown in StatusLabel if any | Shown as informational note only | Should NOT appear in main entry list |
| 1.6 | Close dialog | Dialog closes cleanly | |

---

## §2 Verify Archive — unwanted artifact

| # | Action | Expected | Failure notes |
|---|---|---|---|
| 2.1 | Mark one catalog release as Unwanted | Status changes to "unwanted" in catalog | |
| 2.2 | Copy the corresponding physical archive file to `archive\<platform>\<datLine>\` (simulate a leftover file) | File exists on disk | |
| 2.3 | Run Verify Archive | UNWANTED stat = 1; entry appears in list with red color | |
| 2.4 | Click Repair All | File is moved to `incoming-skip\<platform>\`; REPAIRED stat = 1 | Archive directory no longer contains the file |
| 2.5 | Verify release status is still "unwanted" after repair | Status unchanged | Repair must not alter release status |
| 2.6 | Restore the release to Wanted | Status → missing | |

---

## §3 Verify Archive — unknown file

| # | Action | Expected | Failure notes |
|---|---|---|---|
| 3.1 | Copy an unrelated file (e.g. `random.bin`) to `archive\<platform>\<datLine>\` | File exists | |
| 3.2 | Run Verify Archive | UNKNOWN stat = 1; entry appears with amber color | |
| 3.3 | Click Repair All | File moved to `incoming-skip\<platform>\`; no DB row removed | |

---

## §4 Verify Archive — redundant copy

| # | Action | Expected | Failure notes |
|---|---|---|---|
| 4.1 | Append at least one artifact to a workspace volume (see §6) | Volume has the artifact; VA row exists |
| 4.2 | Keep the corresponding archive file in place | Archive file still exists |
| 4.3 | Run Verify Archive (redundancy detection requires a volume to be configured) | REDUNDANT stat = 1; entry appears with cyan color |
| 4.4 | Click Repair All | Volume copy re-verified; archive file moved to `incoming-skip\<platform>\`; DA and VA rows remain |
| 4.5 | Confirm DA row still in DB | `derived_artifacts` row exists for the artifact | Repair must not delete DA rows |
| 4.6 | Confirm VA row still in DB | `volume_artifacts` row exists | Repair must not delete VA rows |

---

## §5 Verify Archive — volume unavailable

| # | Action | Expected | Failure notes |
|---|---|---|---|
| 5.1 | Arrange: an artifact assigned to a volume whose root directory does not exist | Volume not reachable |
| 5.2 | Run Verify Archive | UNAVAILABLE stat = 1; entry appears with red color; IsClean = false | |
| 5.3 | Click Repair All | Unavailable entry is NOT moved (IsRepairable = false); REPAIRED stat unchanged | |
| 5.4 | Close dialog | No file deleted, no DB change | |

---

## §6 Append Volume — basic plan and execute

| # | Action | Expected | Failure notes |
|---|---|---|---|
| 6.1 | Open Operations → Append Volume; select test volume | Plan dialog opens |
| 6.2 | Review plan stats | TOTAL DA count matches `GetAllWantedArtifactInfos()` size; PLANNED count > 0 if archive has unassigned files |
| 6.3 | Check filter bar: "All", "Planned", "Skipped" work | Row list updates correctly |
| 6.4 | Click Execute | Progress dialog shows append-copying → append-copied for each file |
| 6.5 | Verify target files are flat in volume root | `<volume root>\<filename>` exists; no subfolders created |
| 6.6 | Verify VA rows created | `volume_artifacts` rows exist for copied files |
| 6.7 | Verify archive source files still exist | Archive not deleted by Append |
| 6.8 | Re-open Append plan | All previously appended files show as AlreadyAssigned |

---

## §7 Append Volume — incoming-skip exclusion

| # | Action | Expected | Failure notes |
|---|---|---|---|
| 7.1 | Move an archive file to `incoming-skip\<platform>\` and update the DA row's `archive_path` to start with `incoming-skip/` | File is in incoming-skip |
| 7.2 | Run Append plan | Artifact shows as IncomingSkipIgnored in skip list | Must NOT appear as a candidate |

---

## §8 Fillback Volume

| # | Action | Expected | Failure notes |
|---|---|---|---|
| 8.1 | Open Operations → Fillback; select source and target volumes | Plan dialog opens |
| 8.2 | Review plan entries | Source files listed; skip reasons shown for excluded entries |
| 8.3 | Click Execute | Files moved (same disk) or copied+deleted (cross disk) |
| 8.4 | Verify source files no longer on source volume | `<source root>\<filename>` does not exist |
| 8.5 | Verify target files exist flat in target root | `<target root>\<filename>` exists |
| 8.6 | Verify DB: VA row on source removed, VA row on target added | `volume_artifacts` updated |
| 8.7 | Verify `actual_size_bytes` updated on both volumes | Usage display updated |

---

## §9 Verify Volume — flat layout enforcement

| # | Action | Expected | Failure notes |
|---|---|---|---|
| 9.1 | Create a test subfolder inside the volume root with a wanted artifact inside: `<vol>\TestFolder\game.chd` | File in subfolder |
| 9.2 | Run Verify Volume | `misplaced-found` event emitted; `misplaced-restored` moves file to volume root |
| 9.3 | Confirm `<vol>\game.chd` exists and `<vol>\TestFolder\game.chd` does not | Flat layout enforced |

---

## §10 Verify Volume — unwanted content

| # | Action | Expected | Failure notes |
|---|---|---|---|
| 10.1 | Mark a release as Unwanted while its artifact is physically present on a volume | Release is unwanted; artifact still on disk |
| 10.2 | Run Verify Volume | `unwanted-found` → `unwanted-moved`: artifact moved to `<vol>\unwanted\`; VA row removed; `actual_size_bytes` decremented |
| 10.3 | Confirm release status is still "unwanted" | Verify Volume does not modify release status |

---

## §11 UNWANTED guard

| # | Action | Expected | Failure notes |
|---|---|---|---|
| 11.1 | Mark a release as Unwanted | Status = "unwanted" |
| 11.2 | Attempt ingest of a file matching that release | File moved to `incoming-skip\<platform>\`; release status remains "unwanted" |
| 11.3 | Run Append Volume plan | Artifact for the unwanted release does not appear as a candidate |
| 11.4 | Confirm no automatic flow can set status to "present" | UpdateReleaseStatus SQL guard enforced |
| 11.5 | Restore the release via Restore Wanted Release | Status → missing; catalog entry reappears |

---

## §12 Build / publish

| # | Action | Expected | Failure notes |
|---|---|---|---|
| 12.1 | `dotnet build Arkadia.sln -c Release` | 0 errors, 0 warnings |
| 12.2 | `dotnet test Arkadia.sln -c Release` | 1480 passing, 0 failed |
| 12.3 | `dotnet publish Arkadia/Arkadia.csproj /p:PublishProfile=win-x64-portable` | Publish succeeds; `publish\win-x64\Arkadia.exe` exists |
| 12.4 | Launch published binary | App opens cleanly; no startup errors |

---

## Failure log template

```
# QA Failure Log — [date] — [tester]

## Failure [N]

Step:         §<section>.<step>
Action:       <what was done>
Expected:     <what should have happened>
Actual:       <what happened instead>
Severity:     Critical / High / Medium / Low
Repro notes:  <any notes to reproduce>
Screenshot:   <if applicable>
```
