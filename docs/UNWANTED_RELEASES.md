# Arkadia — UNWANTED Releases

This document explains the UNWANTED status, its semantics, and how every workflow in Arkadia is expected to honour it.

---

## What UNWANTED means

UNWANTED is a **curator veto**, not a lifecycle state. When a curator marks a release as unwanted, they are saying: "I never want Arkadia to automatically process, ingest, copy, or promote this release."

It is intentionally permanent and resistant to accidental reversal. UNWANTED is not the same as "missing" or "not yet ingested" — it is an explicit exclusion decision.

---

## How to set and remove UNWANTED

**Set:** Use the "Mark as Unwanted" action in the UI (Catalog or Operations view). This calls `DatLineStore.UpdateReleaseStatus(id, "unwanted")` — the only way to enter the state.

**Remove:** Use the explicit "Restore Wanted Release" UI action. This calls `DatLineStore.RestoreWantedRelease(releaseId)` and **nowhere else**. Restoring sets `status = 'missing'` and `show_in_catalog = 1`.

---

## The SQL guard

`DatLineStore.UpdateReleaseStatus()` contains a guard clause:

```sql
UPDATE releases SET status = $status WHERE id = $id AND status != 'unwanted'
```

This means **no generic lifecycle update can remove a release from unwanted**. Ingestion confirming a file is present cannot reset unwanted to present. Recalculation routines cannot overwrite unwanted. Only `RestoreWantedRelease()` removes the guard.

---

## UNWANTED WINS on hash collision

When multiple DB artifacts share the same SHA-1 hash, the classification resolves to unwanted if **any** linked release is unwanted. The wanted release does not "win" the tie.

This applies in:
- `LocalArchiveVerifyService.Verify()` — dbBySha1 index resolves to unwanted when collision exists
- `DatLineStore.GetAllWantedArtifactInfos()` — NOT EXISTS subquery excludes artifacts with any unwanted link
- `DatLineStore.GetUnwantedArtifactCount()` — counts artifacts where any linked release is unwanted
- `VolumeVerifyService` — `FindArtifactBySha1()` checks any linked release for unwanted

---

## What each workflow does with UNWANTED

### Ingestion

1. **Early unwanted classification (Phase 6):** When an incoming file matches an unwanted release, that target is logged as `unwanted-classified` and is **not staged**. If *every* match is unwanted, the file is deferred to Phase 8 (no transform, no staging, no assembly into `source`).
2. **Fan-out:** Incoming files matched to both wanted and unwanted releases process only the wanted targets. Unwanted targets are logged as `unwanted-classified`. The source file is not moved to incoming-skip as long as at least one wanted target is being processed.
3. **All-unwanted physical move (Phase 8):** If all matched targets for a source file are unwanted, the file is physically moved to `incoming-skip\<platform>\` and logged as `unwanted-moved` (or `unwanted-move-failed` on error); the `UnwantedSkipped` counter is incremented once per file moved.
4. **`UpdateReleaseStatus("present")` is guarded:** Ingestion cannot reset an unwanted release to present.

> **Two distinct actions, not a duplicate.** `unwanted-classified` (Phase 6, match-time) and `unwanted-moved` (Phase 8, physical move) are logged separately. `Unwanted skipped` is its own summary counter, distinct from the generic `Files skipped`.

### Append Volume

- `GetAllWantedArtifactInfos()` excludes artifacts where any linked release is unwanted.
- `GetAssignedDerivedIdsByDatLine()` (for already-assigned check) includes all artifacts regardless of wanted status — this is intentional to prevent re-adding excluded content.
- Plan diagnostics include `ReleaseUnwantedSkipped` counter.

### Build Volume

- Follows the same wanted-only candidate selection as Append.

### Verify Volume

- Artifact lookup via `FindArtifactBySha1()` returns unwanted state.
- If the found artifact is unwanted, the volume file is moved to `<volume root>\unwanted\`, the `volume_artifacts` row is removed, and `actual_size_bytes` is decremented.
- This is safe and non-destructive: the artifact remains in the archive, and `RestoreWantedRelease()` can restore the curator decision.

### Verify Archive

- Archive file classified as `UnwantedArchiveArtifact` when its SHA-1 matches a DA linked to any unwanted release.
- Repair moves the file to `incoming-skip\<platform>\` and removes the `derived_artifacts` row (plus content links).
- Release status is **not** changed — the release remains `unwanted` after repair.

---

## Invariants to preserve

| Invariant | Enforced by |
|---|---|
| Only `RestoreWantedRelease()` leaves unwanted | `UpdateReleaseStatus` SQL guard |
| Ingestion cannot promote unwanted to present | `UpdateReleaseStatus` SQL guard |
| Artifact with any unwanted link is excluded from wanted queries | `GetAllWantedArtifactInfos` NOT EXISTS subquery |
| Ingestion unwanted targets get `unwanted-classified` log entry | MainWindow Phase 6 fan-out |
| Ingestion all-unwanted source moves to incoming-skip (`unwanted-moved`) | MainWindow Phase 8 |
| Volume unwanted files moved to `unwanted\` subfolder, VA row removed | `VolumeVerifyService` |
| Archive unwanted files moved to `incoming-skip`, DA row removed | `LocalArchiveVerifyService.Repair` |

---

## Test coverage

Unwanted guard tests live in:
- `Arkadia.Tests/Data/DatLineStoreUnwantedGuardTests.cs` — direct guard/restore tests
- `Arkadia.Tests/Data/DatLineStoreStatusGuardTests.cs` — status transition rules
- `Arkadia.Tests/LocalArchive/LocalArchiveVerifyServiceTests.cs` — archive classify/repair
- `Arkadia.Tests/Volumes/AppendVolumeDiagnosticsTests.cs` — append exclusion
- `Arkadia.Tests/Purge/PurgeAnalyticsTests.cs` — purge/restore
