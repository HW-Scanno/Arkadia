# Arkadia Group DAT v1 — Specification

**Status:** Milestone started. **Phase 1 implemented** (technical-id policy + value objects + this document). Everything beyond Phase 1 is **approved design, not yet implemented**.

**Last updated:** 2026-07-29 (Phase 1)

> This document records the **approved baseline** of the Group DAT / Nested DAT / TOSEC milestone — not only Phase 1. It is the authoritative reference for the milestone. Sections are marked:
> **[APPROVED]** decision is binding · **[DEFERRED]** approved but scheduled later · **[NOT IMPLEMENTED]** no code yet · **[OPEN]** still to be decided.
>
> Do not treat deferred/not-implemented sections as available features.

---

## 1. Purpose [APPROVED]

Arkadia currently treats every imported DAT as a single technical `dat_line`. This suits flat authorities (Redump) but not authorities like **TOSEC**, where one system (e.g. Commodore 64) ships **many** DAT files across a nested directory tree.

**Group DAT** introduces an additive super-unit that groups many leaf DATs, so nested/TOSEC collections can be imported and updated as a set **without merging them** and **without changing the leaf model**.

---

## 2. Roles: Group DAT vs `dat_line` [APPROVED]

- **`dat_line` remains the technical, operational leaf.** It keeps its own SQLite DB, its archive path, and its ingestion/verify/volume behavior. Unchanged.
- **Group DAT is an additive layer above the leaf.** It owns membership and a revision counter; it never becomes an operational unit itself and never alters leaf behavior.
- **No merging.** Nested TOSEC DATs are never fused into one giant DAT — each remains a leaf.

---

## 3. Single DAT compatibility [APPROVED]

- All existing DATs remain **Single DAT**.
- A Single DAT is a `dat_line` with `group_id = NULL`.
- Existing DATs are **never** auto-reinterpreted as Group DAT.
- Existing ids are **never** changed or normalized.
- Single DAT import and update behavior is **unchanged**.

---

## 4. Additive data model (planned) [APPROVED design / NOT IMPLEMENTED]

Additive only — nullable columns and new tables; existing rows read as Single DAT.

- **`dat_groups`** (catalog.db): `id` (immutable), `display_name`, `hardware_family_id`, `authority`, `current_revision` (bootstraps at **0**), `created_at_utc`, `updated_at_utc`.
- **`dat_lines`** additive nullable columns: `group_id`, `relative_dat_path`, `source_dat_name`, `source_dat_sha256`, `semantic_fingerprint`, `semantic_fingerprint_version`, `last_seen_group_revision`.
- **`dat_group_update_runs`** and **`dat_group_update_actions`** (catalog.db): the frozen-plan + per-leaf journal.

Scope rules [APPROVED]: a Group DAT belongs initially to **one** hardware family and **one** authority; its leaves **may** have different media types; a leaf belongs to **at most one** group; associating an existing Single DAT to a group later is a **separate explicit workflow**. The **source root belongs to the run**, not the group (it may move between updates). Future FKs must be **non-destructive (RESTRICT-oriented)**. Only **one active run per group**, protected at the DB level in the schema phase.

---

## 5. Immutable technical ids [APPROVED]

- `dat_group.id` and `dat_line.id` are **immutable after creation**.
- The id is an **opaque key** but is embedded in the leaf DB filename, the archive path, and volume references, so changing it would require rewriting DB, filesystem, archive and volume state. Immutability is a **strong invariant**.
- Relative path, DAT filename, and DAT header/version/date are **not** immutable technical identity.
- **Group id and leaf id namespaces are separate** (`dat_groups.id` unique among groups; `dat_lines.id` unique among leaves). A cross-type text coincidence is permitted; it may warrant a future warning but is not a policy error.

---

## 6. New technical id policy [APPROVED — IMPLEMENTED in Phase 1]

Applies to **new** Group DAT ids and **new** Group-created leaf ids only. Implemented by `Arkadia.Data.Identifiers.DatTechnicalIdPolicy` and the value objects `DatGroupId` / `DatLineId`.

- Lowercase ASCII; characters `a-z 0-9 -`; must start and end alphanumeric; no consecutive hyphens.
- Forbidden: spaces, underscore, Unicode, dots, slash, backslash, filesystem separators, path traversal, special characters.
- Length ≥ 1; **target ≤ 48** (warning above); **hard limit ≤ 64** (blocking).
- Canonical regex: `^[a-z0-9]+(?:-[a-z0-9]+)*$` (length checked separately).
- Reserved Windows names rejected case-insensitively when the **whole** id equals one of `con, prn, aux, nul, com1..com9, lpt1..lpt9`. Composite ids like `tosec-con` are valid; `com10`/`lpt10` are valid.
- Persisted form always lowercase; **immutable after commit**.
- Collision comparison uses `StringComparer.OrdinalIgnoreCase` (defends the case-sensitive-SQLite / case-insensitive-NTFS split).

**New vs persisted distinction (anti-regression invariant):**
- `TryCreateNew` accepts only the canonical form; invalid input is **rejected, never silently rewritten**; it emits a structured `DatTechnicalIdError` (`Empty`, `NotCanonical`, `TooLong`, `ReservedName`) and a separate `exceedsRecommendedLength` warning.
- `FromPersisted` loads a legacy value **verbatim** — no lowercase, no rename, no normalization — and never blocks loading historical/non-conforming ids; it exposes a `ConformsToNewPolicy` diagnostic. It rejects only the truly unrepresentable case (`null`).
- `NormalizeSuggestion` is a **pure, deterministic, culture-invariant** helper for the future suggester: trim → invariant lowercase → Unicode FormD → drop combining marks + map non-`[a-z0-9]` to `-` → collapse/trim hyphens. It may return empty; it does **not** prepend a group id, do TOSEC path reduction, strip generic words, or compute a hash (all deferred). Equality of the value objects is ordinal on the stored value, with an explicit `CaseInsensitiveComparer` for collision sets.

Future short disambiguation hash [DEFERRED]: **8 hex characters**, extendable on collision; used only for initial disambiguation and never becomes a path dependency.

---

## 7. Fingerprints [APPROVED design / NOT IMPLEMENTED]

Two distinct fingerprints plus overlap signals:
- **Exact source fingerprint** = SHA-256 of the raw DAT bytes.
- **Semantic content fingerprint** = deterministic, **versioned**, independent of path/filename/header/version/date/whitespace/XML order; content-only.
  - The **strict** semantic fingerprint preserves release boundaries, ROM multiplicity, size, and **all** available hashes. It describes the **applied state** of the leaf, not merely the last observed source.
  - **Hash enrichment** (a ROM gaining SHA-1 over CRC/size upstream) is handled by **overlap evidence**, not by loosening the strict fingerprint.
- **`ContentKey` is not leaf identity** — it is an overlap signal only. Semantic equality must **not** invoke `ReconciliationEngine` unnecessarily. Strict fingerprint and overlap evidence are **distinct** concepts.

---

## 8. Matching evidence ladder [APPROVED design / NOT IMPLEMENTED]

Three independent dimensions: **observation** (`exact`, `strong_candidate`, `possible`, `ambiguous`, `unmatched`, `duplicate`, `parse_error`), **resolved action** (`update_existing_leaf`, `create_new_leaf`, `retain_existing_leaf_without_update`, `ignore_new_discovered_dat`, `retain_leaf_missing_from_source`, `blocked`), **execution state**.
- Auto **exact** only when a strong signal is unique in both directions with no duplicates/collisions.
- **Strong candidates require user confirmation in v1.**
- **Possible** → manual matching. **Split / merge / duplicate / ambiguous** → always manual and **blocking**.
- Relative path, filename, header name, and release count are **never** sufficient alone.
- An unassociated DAT may become a **new** leaf. An old leaf not found is **retained, not deleted**.
- Must distinguish a leaf that is **semantically unchanged** from a **different update deliberately not applied**.

---

## 9. Discovery [APPROVED design / NOT IMPLEMENTED]

Discovery is **pure, read-only, repeatable, non-mutating, DB-independent**, and represents errors/incompleteness. Selecting a root **never** starts import or update. A structural enumeration error blocks finalization; a per-file parse error is handled per-leaf but blocks finalization until resolved or explicitly ignored; a suspicious root raises a high-severity warning requiring confirmation (not automatically a technical error).

---

## 10. Frozen plan [APPROVED design / NOT IMPLEMENTED]

The reviewed plan is frozen (immutable) before execution and carries: group id, run id, base/target revision, discovery snapshot, plan version, plan fingerprint, ordered actions each with observation, resolved action, chosen leaf id, expected from/target fingerprints, source path, relative path, user decisions, acknowledged warnings. The frozen plan need **not** serialize every release/ROM. It is invalidated by any change to actions/ids/fingerprints/decisions; execution re-checks that source files and DB state have not drifted.

---

## 11. Per-leaf journal & hybrid commit [APPROVED design / NOT IMPLEMENTED]

Hybrid model: frozen plan → progressive **per-leaf** commit → per-action persisted result → resume → group finalization. One active run per group. The run records base/target revision, plan fingerprint, discovery state, run state, timestamps; each action records existing leaf, discovered DAT, matching classification, resolved decision, expected from/target fingerprints, execution state, error.

---

## 12. Stop at leaf boundary [APPROVED design / NOT IMPLEMENTED]

v1 supports only **Stop after current leaf** — no arbitrary mid-leaf cancellation. The run can halt only **between** two leaves. `StopRequested` has a **single source of truth**. A `Running` action left after a crash is reconciled at resume by verifying the **actual** fingerprint; an update is never reapplied if the expected from-fingerprint does not match the real state; a leaf already at the target fingerprint is recognized as complete.

---

## 13. Revision finalization [APPROVED design / NOT IMPLEMENTED]

Finalization advances the revision **only after** the reconciliation run completes with all required actions terminal-valid. It writes, in a single catalog.db transaction: `last_seen_group_revision` for seen leaves, updated `relative_dat_path`, updated source fingerprint/metadata, `dat_groups.current_revision`, and the run's terminal state/timestamp. Seen: successful `update_existing_leaf` / `create_new_leaf` / confirmed `retain_existing_leaf_without_update`. Not seen: `ignore_new_discovered_dat` (no leaf) and `retain_leaf_missing_from_source`. Double finalization is prevented. Run states: recoverable `Partial` / `Blocked`; add `ReadyToFinalize`; terminal `Completed` / `Abandoned`.

During a **partial run**: the current revision does not advance; no leaf is declared missing; already-updated leaves stay valid; the run is resumable.

---

## 14. `Missing from latest group` — derived [APPROVED design / NOT IMPLEMENTED]

A **derived** state, never a persisted membership flag:

```
group_id IS NOT NULL AND last_seen_group_revision < dat_groups.current_revision
```

Computable **only after** structurally-complete discovery + fully-resolved plan + finalized reconciliation. `last_seen_group_revision = NULL` in an already-finalized group is an **anomaly**, not a normal missing leaf. This concept is distinct from release `missing`/`outdated`, `unwanted`, derived-artifact missing, volume unavailable, and archive absent.

---

## 15. Partial-state recovery [APPROVED design / NOT IMPLEMENTED]

The future per-leaf executor must explicitly handle: a catalog row without a leaf DB; an empty leaf DB; a leaf DB with incomplete releases; partial release files; retry of the same `dat_line_id`. The UI duplicate gate must not block recovery of a leaf created by the same run. A leaf is not considered complete until all required writes finish. Partial states must be recognizable from the journal. **No destructive cleanup** is introduced implicitly.

---

## 16. Anti-drift invariants [APPROVED]

1. `dat_line` remains the technical leaf. 2. Group DAT does not change the archive layout. 3. Existing ids never change or get normalized. 4. New ids are lowercase, collision-safe, filesystem-safe, immutable. 5. Relative path is not identity. 6. Discovery never mutates. 7. Preview never mutates. 8. The frozen plan does not change during execution. 9. A leaf is not updated if the from-fingerprint mismatches. 10. A leaf already at target is not reapplied. 11. No leaf is deleted for being absent from a new root. 12. Missing-from-latest is computed only after finalization. 13. A partial run does not advance the revision. 14. One active reconciliation per group. 15. No cross-DB/filesystem atomicity is assumed. 16. Every partial state is recognizable and recoverable-or-blocking. 17. No mid-leaf cancellation in v1. 18. Single DAT stays unchanged with `group_id = NULL`. 19. No verify workflow becomes ingestion. 20. No UI issue causes implicit data mutation.

---

## 17. Progressive roadmap [APPROVED sequence / mostly NOT IMPLEMENTED]

1. **ID value objects & invariants** — **DONE (Phase 1).**
2. Additive schema. 3. Group record + repository. 4. Fingerprint library. 5. Read-only discovery. 6. Import-plan preview. 7. Frozen plan + journal. 8. Import executor + recovery (extract `RunImportWork` core). 9. Create Group DAT UI. 10. Reconciliation matcher. 11. Update executor (wrap `ReconciliationEngine`). 12. Revision finalizer. 13. Group update UI. 14. Hardening + real TOSEC data.

Phases 2–7 introduce **no** Single-DAT behavior change.

---

## 18. Phase 1 — what is implemented now [IMPLEMENTED]

- `DataLayer/Identifiers/DatTechnicalIdPolicy.cs` — the pure id policy: canonical validation, reserved names, target/hard length, structured `DatTechnicalIdError`, and `NormalizeSuggestion`.
- `DataLayer/Identifiers/DatGroupId.cs`, `DataLayer/Identifiers/DatLineId.cs` — distinct immutable value objects (`TryCreateNew` strict, `FromPersisted` verbatim, ordinal equality + `CaseInsensitiveComparer`, `ConformsToNewPolicy`).
- `Arkadia.Tests/DataLayer/Identifiers/` — full unit coverage.

**Not implemented (later phases):** `dat_groups`, `group_id` columns, update-run tables, fingerprint persistence, discovery, the full `DatLineIdSuggester` (abbreviation / generic-word list / short hash), Group DAT dialogs, executors, and any migration. The value objects are **not yet integrated** into existing workflows; existing `string datLineId` usage is unchanged.

---

## 19. Open items [OPEN]

Exact ROM-token definition for the semantic fingerprint; overlap thresholds (Jaccard/containment); `dat_groups` FK/delete specifics; whether to persist a last-used source-root hint for UX; real partial-unique index vs app guard for one-active-run; handling of an existing leaf without a stored fingerprint on first group update; recovery policy for the pre-existing partial-import orphan window; slug generic-word list; short-hash length; whether `retain_existing_leaf_without_update` needs explicit confirmation.
