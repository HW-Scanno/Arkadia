# Arkadia Group DAT v1 — Specification

**Status:** Milestone in progress. **Phase 1 implemented** (technical-id policy + value objects). **Phase 2 implemented** (additive catalog schema + Group model + minimal persistence). **Phase 3A implemented** (pure, DB-independent source discovery / preview manifest). Everything beyond Phase 3A is **approved design, not yet implemented**.

**v1 direction (Product Owner):** Group DAT Import/Update **v1 uses manual one-to-one reconciliation** (see §8a), **not** automatic matching or semantic fingerprints. The fingerprints (§7), the automatic matching evidence ladder (§8), and the Phase-3B fingerprint audit are **future research — not v1 prerequisites and not implemented**. The **non-mutating reconciliation preview** (model + window) is implemented — see §18c; **execution is not yet implemented**.

**Last updated:** 2026-08-03 (explicit identity: distinct Group Name + Group ID, caller-fixed Create/Update mode, multiple groups per System, Group-ID-prefixed leaf ids)

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

## 7. Fingerprints [FUTURE RESEARCH — NOT a v1 prerequisite / NOT IMPLEMENTED]

> **Not required for Group DAT v1.** v1 import/update is manual one-to-one reconciliation (§8a); fingerprints are neither computed nor required. This section is retained as future research (see the Phase-3B audit). The nullable fingerprint columns already in the schema may remain unused.

Two distinct fingerprints plus overlap signals:
- **Exact source fingerprint** = SHA-256 of the raw DAT bytes.
- **Semantic content fingerprint** = deterministic, **versioned**, independent of path/filename/header/version/date/whitespace/XML order; content-only.
  - The **strict** semantic fingerprint preserves release boundaries, ROM multiplicity, size, and **all** available hashes. It describes the **applied state** of the leaf, not merely the last observed source.
  - **Hash enrichment** (a ROM gaining SHA-1 over CRC/size upstream) is handled by **overlap evidence**, not by loosening the strict fingerprint.
- **`ContentKey` is not leaf identity** — it is an overlap signal only. Semantic equality must **not** invoke `ReconciliationEngine` unnecessarily. Strict fingerprint and overlap evidence are **distinct** concepts.

---

## 8. Matching evidence ladder [FUTURE RESEARCH — superseded for v1 by §8a / NOT IMPLEMENTED]

> **Not part of Group DAT v1.** The automatic observation/candidate ladder below is deferred future research; v1 uses **manual one-to-one reconciliation** (§8a) with no automatic matching, no candidate classification, and no split/merge detection.

Three independent dimensions: **observation** (`exact`, `strong_candidate`, `possible`, `ambiguous`, `unmatched`, `duplicate`, `parse_error`), **resolved action** (`update_existing_leaf`, `create_new_leaf`, `retain_existing_leaf_without_update`, `ignore_new_discovered_dat`, `retain_leaf_missing_from_source`, `blocked`), **execution state**.
- Auto **exact** only when a strong signal is unique in both directions with no duplicates/collisions.
- **Strong candidates require user confirmation in v1.**
- **Possible** → manual matching. **Split / merge / duplicate / ambiguous** → always manual and **blocking**.
- Relative path, filename, header name, and release count are **never** sufficient alone.
- An unassociated DAT may become a **new** leaf. An old leaf not found is **retained, not deleted**.
- Must distinguish a leaf that is **semantically unchanged** from a **different update deliberately not applied**.

---

## 8a. Group DAT Import/Update v1 — manual one-to-one reconciliation [APPROVED — v1 DIRECTION / NOT IMPLEMENTED]

**Product decision.** Group DAT Import and Update **v1** are driven by **manual one-to-one reconciliation**, not by automatic matching or semantic fingerprints. This section is the authoritative v1 workflow; §7 and §8 are future research and **not** prerequisites of the initial import/update.

### Non-mutating preview
- The selected Group DAT source is analyzed by the pure Phase-3A discovery (§18b) — read-only.
- The preview presents the discovered DATs (and, for an update, the group's existing leaves) **without** creating or modifying any `dat_group`, `dat_line`, leaf DB, or revision, and without any filesystem write.
- **Abort before confirmation discards only in-memory state** — nothing is created, modified, deleted, or renamed.

### Two-column reconciliation window
- **Left — discovered DATs** (new source): relative path, header name, version, date, author, release count, and media type / required fields when available.
- **Right — existing leaves** of the Group DAT (update only): `DatLineId`, previous DAT metadata, release count, media type, and status relative to the current revision.

### Manual one-to-one reconciliation
Each **discovered DAT** must be explicitly resolved as: **update an existing leaf**, **new leaf**, or **explicit exclude/skip** *(only if the product authorizes it in that implementation)*.
Each **existing leaf** must be explicitly resolved as: **associated with a discovered DAT**, or **absent from the new revision** (retained, never deleted).
- An existing leaf may be associated **at most once**; a discovered DAT may be associated **at most once**.
- Confirming an association **consumes** both the DAT and the leaf from the available reconciliation sets; **undoing** the association returns both to available.
- **"Consume" means only removing the item from the temporary reconciliation sets** — it never deletes a file or a record.

### Completion gating
Final confirmation is available **only** when: all discovered DATs are resolved; all existing leaves are resolved; there are no duplicate associations; there are no parse failures or blocking conflicts; all new `DatLineId`s are valid; and the required media types are present.

### Folder tokens (leaf-id proposal)
The source directory hierarchy is part of the preview. Each folder may receive a **manual technical token**:
- the token may contribute to the proposed `DatLineId` of new leaves;
- an **empty** token excludes that folder from the id;
- editing a token updates the proposed ids of its descendants;
- the source path stays **metadata, never permanent identity**;
- **existing leaves are never renamed** when the folder structure changes;
- **no DB table for folders in v1.**

Conceptual proposal:
```
new_leaf_id = group_id + included-folder tokens + Dat Suffix
```
Each **folder token is one atomic segment** (a single folder level cannot itself look like several: `Test Disks` → `testdisks`, not `test-disks`) — the hyphen is reserved as the separator between the id's components. The **Dat Suffix** is optional, starts empty, is appended at the end, and is only added manually to distinguish DATs with the same proposed id (never derived from the filename). The proposal is editable and must satisfy `DatLineId` rules and case-insensitive collision checks (Phase 1 policy, §6).

### Separate execution
After confirmation: the manual plan is **frozen**; import/update run in a **separate phase** that **reuses the existing Single DAT workflows**; the revision is **finalized only after all actions complete**. A **minimal journal** may later be introduced **only** for resume/consistency after confirmation — **not designed in this task**.

### Deferred / not required for v1
Explicitly **not needed** for Group DAT v1: semantic fingerprint; canonical semantic encoder; overlap evidence; automatic matching; strong/ambiguous candidate classification; automatic split/merge detection; automatic reconciliation; unattended updates. The Phase-3B fingerprint audit remains **future research** — not an implemented feature and not a v1 prerequisite. The nullable fingerprint columns already present in the schema (Phase 2) may remain **unused** and require **no rollback**; Phases 1–3A require no cleanup or revert.

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

> **v1 note:** Group DAT Import/Update **v1 is the manual one-to-one reconciliation workflow (§8a)**. The steps below that assume a **fingerprint library (4)** and a **reconciliation matcher (10)** are **future research, not v1 prerequisites**; v1 replaces automatic matching with manual reconciliation and reuses the existing Single-DAT import/update executors.

1. **ID value objects & invariants** — **DONE (Phase 1).**
2. Additive schema. 3. Group record + repository. 4. Fingerprint library. 5. Read-only discovery. 6. Import-plan preview. 7. Frozen plan + journal. 8. Import executor + recovery (extract `RunImportWork` core). 9. Create Group DAT UI. 10. Reconciliation matcher. 11. Update executor (wrap `ReconciliationEngine`). 12. Revision finalizer. 13. Group update UI. 14. Hardening + real TOSEC data.

Phases 2–7 introduce **no** Single-DAT behavior change.

---

## 18. Phase 1 — what is implemented now [IMPLEMENTED]

- `DataLayer/Identifiers/DatTechnicalIdPolicy.cs` — the pure id policy: canonical validation, reserved names, target/hard length, structured `DatTechnicalIdError`, and `NormalizeSuggestion`.
- `DataLayer/Identifiers/DatGroupId.cs`, `DataLayer/Identifiers/DatLineId.cs` — distinct immutable value objects (`TryCreateNew` strict, `FromPersisted` verbatim, ordinal equality + `CaseInsensitiveComparer`, `ConformsToNewPolicy`).
- `Arkadia.Tests/DataLayer/Identifiers/` — full unit coverage.

**Not implemented at Phase 1:** everything below except what Phase 2 (§18a) adds.

---

## 18a. Phase 2 — what is implemented now [IMPLEMENTED]

Additive catalog schema and the persistent Group model — **no workflow behavior**, no membership assignment, no revision advancement.

- **Schema (`DataLayer/CatalogService.cs`, `EnsureSchema`).** New table **`dat_groups`** `(id TEXT COLLATE NOCASE PRIMARY KEY, display_name TEXT NOT NULL, hardware_family_id TEXT NOT NULL → hardware_families ON DELETE RESTRICT, authority TEXT NOT NULL, current_revision INTEGER NOT NULL DEFAULT 0, created_at_utc, updated_at_utc)` with indexes on `hardware_family_id` and `(hardware_family_id, authority)`. New **nullable** `dat_lines` columns via idempotent `TryAddColumn`: `group_id` (→ `dat_groups(id)` ON DELETE RESTRICT), `relative_dat_path`, `source_dat_name`, `source_dat_sha256`, `semantic_fingerprint`, `semantic_fingerprint_version` (INT), `last_seen_group_revision` (INT), plus index `idx_dat_lines_group_id`. Migration is additive/idempotent, no backfill, no implicit groups, `data/` runtime untouched.

**DB-enforced numeric CHECK constraints** (added with the columns): `dat_groups.current_revision >= 0`; `semantic_fingerprint_version IS NULL OR > 0`; `last_seen_group_revision IS NULL OR >= 0`. Enforced by the DB regardless of the (absent) revision-advancement API. Modern SQLite **does** evaluate a CHECK added via `ALTER TABLE ADD COLUMN` against pre-existing rows; the migration is compatible because the new nullable columns are NULL on every legacy row and NULL satisfies both `IS NULL OR …` predicates, so **no table rebuild is needed**.
- **Model.** `DataLayer/DatGroupRecord.cs` (`Id` is a `DatGroupId`); `DataLayer/DatLineGroupMetadataRecord.cs` (a **companion** read record so `DatLineRecord` and its call sites are untouched — chosen over extending `DatLineRecord`).
- **Catalog APIs.** `CreateDatGroup` (pure INSERT; validates id policy / non-empty display name / existing hardware family; `current_revision = 0`; rejects duplicate incl. case-variant), `LoadDatGroups`, `GetDatGroup`, `DatGroupExists`, `UpdateDatGroupDisplayName` (name + `updated_at_utc` only), `GetDatLineGroupMetadata` (nullable leaf metadata read). **No** delete, generic upsert, id/family/authority change, revision change, or leaf-membership API.
- **Preservation.** `SaveDatLines` already writes only the original columns, so Group metadata is preserved on upsert and NULL on new inserts — **no change was needed**; a regression test locks this.
- Tests: `Arkadia.Tests/DataLayer/DatGroupCatalogSchemaTests.cs`.

**Still not implemented (later phases):** `dat_group_update_runs` / `dat_group_update_actions` (run/action journal), `pending_revision`, fingerprint calculator, semantic/exact fingerprint population, discovery, import/reconciliation/frozen plan, executors, finalizer, revision advancement, the full `DatLineIdSuggester`, Group DAT UI, recursive import, Group update, and manual association of existing Single DATs. The value objects and Group model are **not yet wired** into existing workflows; Single DAT import/update/ingestion/archive/verify/volumes are unchanged.

---

## 18b. Phase 3A — what is implemented now [IMPLEMENTED]

Pure, DB-independent **source discovery** producing an in-memory preview manifest. Read-only over the source; **no** DB, CatalogService, leaf DB, filesystem writes, cache, staging, id generation, fingerprint, matching, plan, or UI.

- **`DataLayer/DatGroupSourceDiscoveryService.cs`** — `Discover(string sourceRoot, CancellationToken = default)`. Iterative (non-recursive) traversal; a candidate is any file with extension `.dat` (case-insensitive, matching the existing Single DAT import filter — no new formats); non-`.dat` files are ignored silently. Reuses `DatParser.Parse` unchanged. Directory **reparse points (symlinks/junctions) are not followed** (warning `reparse-point-skipped`). Cancellation is checked before enumeration, between directories, between files, and before parsing → `OperationCanceledException`.
- **Relative paths** are normalized to `/`, never rooted, never escaping the root, original casing/Unicode preserved, and are **not** ids (no `DatTechnicalIdPolicy`). Ordering is deterministic by relative path (`Ordinal`); physical traversal order is not observable. Case-insensitive relative-path **collisions are a blocking diagnostic** (`relative-path-collision`) — files are never auto-chosen or merged (pure detector `DetectRelativePathCollisions`).
- **`DataLayer/DatGroupDiscoveryResult.cs`** — `DatGroupDiscoveryResult` (SourceRoot, ordered `Leaves`, `Diagnostics`, derived `CandidateCount`/`ParsedCount`/`FailedCount`/`HasBlockingErrors`/`CanProceedToPlanning`), `DiscoveredDatLeaf` (relative path, filename, `SourcePath` absolute = in-memory only, non-identity, not an ordering key, not persisted; status; parser metadata; `Games` as a **deeply-immutable snapshot** — `DiscoveredDatGame` / `DiscoveredDatRom` records whose game/ROM collections are `ImmutableArray<T>` (truly non-modifiable: no writable indexer, not castable to a mutable array/`List<T>`, `IList<T>` view rejects all mutation); the parser's mutable `ParsedGame`/`ParsedRom` and their `List<T>` are **never exposed** and no reference to them is retained, so mutating the parser result cannot alter the manifest; order/multiplicity/values preserved exactly, no reinterpretation), `DiscoveredDatLeafStatus` (`Parsed`/`ParseFailed`/`ReadFailed`), `DatGroupDiscoveryDiagnostic` (+ `Severity`, stable codes) — diagnostics hold only a code/severity/controlled message/relative path, never an Exception or stack trace or absolute path.
- **`CanProceedToPlanning`** is true only when the root is valid, there are no relative-path collisions, every candidate parsed, and ≥1 candidate exists — it creates no plan. A malformed/unreadable DAT is represented (not fatal) and makes the scan non-proceedable. An empty-of-DATs valid directory yields zero candidates, no blocking error, and `CanProceedToPlanning = false`.
- Tests: `Arkadia.Tests/DataLayer/DatGroupSourceDiscoveryServiceTests.cs`.

**Not implemented (Phase 3B+):** exact source SHA-256, semantic fingerprint, overlap evidence, id suggestion/assignment, catalog access, matching with `dat_groups`/`dat_lines`, reconciliation/frozen plan, run/action journal, executors, finalizer, manifest persistence, and UI.

---

## 18c. Manual reconciliation preview (model + window) — what is implemented now [IMPLEMENTED]

The **non-mutating manual reconciliation preview** for §8a. Pure logic + a code-behind window; **no execution**, no DB/filesystem writes.

- **Pure layer (`GroupDats/Reconciliation/`, namespace `Arkadia.GroupDats`):** `GroupDatCatalogPreviewData` (+ `GroupDatOption`/`GroupDatExistingGroup`/`GroupDatExistingLeaf`) — the immutable catalog snapshot handed to the window; `GroupDatReconciliationSession` (the view-model logic: identity, available sets, decisions, consume/undo, id proposal, completion gating, `BuildPlan`); `IncomingDatCandidate`/`ExistingGroupLeafCandidate`/`GroupDatDecision`; `FolderTokenTree` (in-memory folder tree from Phase-3A relative paths — no DB table); `DatLineIdComposer` (`group-id + non-empty folder tokens + Dat Suffix`, joined `-`, validated by Phase-1 `DatTechnicalIdPolicy`, case-insensitive collisions; **no media/authority/hash/truncation/silent-normalization**); `GroupDatReconciliationPlan` (deeply-immutable frozen plan carrying `SystemId`/`SystemName`/`Authority`/`GroupId`/`GroupName`/`HardwareFamilyId` and updates/new-leaves/absent-leaves + the immutable discovery snapshot — no CatalogService/DatLineStore/parser-mutable models/execution state).
- **Explicit identity model (caller-fixed mode; Group Name and Group ID are distinct).** The **mode is fixed by the caller**, never inferred from System + authority: `ForNewGroup(catalog, systemId, systemName, authority, groupName, proposedGroupId)` (Create) and `ForExistingGroup(catalog, existingGroupId)` (Update). **System id** is caller-supplied and **immutable** (read-only in the window). **Group Name** (persists as `display_name`) is a human-readable label shown in the System view — **required**, **editable in Create**, and **never part of any leaf id**; it can be renamed later by a separate action. **Group ID** is the stable technical key **and the leaf-id prefix** — suggested initially as `<systemId>-<authority>` (`SuggestGroupId`), **editable only before creation**, validated by `DatTechnicalIdPolicy`, **case-insensitively collision-checked** against existing group ids, and **immutable after creation**. **Authority** is group metadata, editable only in Create (a change re-suggests the non-overridden Group ID / Group Name). **Multiple Group DATs per System and per authority are allowed**; a Group ID collision **shows an error** and never auto-suffixes or auto-switches to Update — the user picks a different id or cancels. In Update mode System / Authority / Group Name / Group ID and the existing leaves all come from the catalog snapshot and are **read-only** (no alternative UI values).
- **Window (`GroupDats/GroupDatReconciliationDialog.axaml(.cs)`, `namespace Arkadia`):** two caller-fixed modes — **no ModeCombo / TargetCombo**. Both drive the same **sequential** flow: select a DAT → configure that one leaf (id + media type) → confirm → consume → next → Undo.
  - **Create mode** header: System (read-only) · Authority (editable) · Group Name (editable) · Group ID (editable), with the distinction *"Group Name — displayed in the System view · Group ID — permanent technical identifier and leaf-id prefix"* and a live Group-ID status. The right pane is the selected-DAT detail + leaf-id builder (Group prefix / editable atomic folder tokens / optional **Dat Suffix** / media type / final id + Auto). No global auto-draft, no simultaneous editor for all leaves, no global media-type validation.
  - **Update mode** header: System / Authority / Group Name / Group ID all **read-only**. Keeps the two-column manual reconciliation (associate incoming ↔ existing leaf / create new leaf / mark existing leaf absent / Undo). New leaves created during Update use the **existing Group ID** as prefix.
  - Completion-gated **Continue** produces the frozen plan; **Abort** discards only in-memory state.
- **Leaf-id proposal (concise, filename-free):** `<group-id> + non-empty folder tokens + optional Dat Suffix`, e.g. Group ID `c64-tosec` + `Applications/Test Disks/[NBZ]/…` → `c64-tosec-applications-testdisks-nbz`. The **Group ID is the prefix**; editing it re-prefixes the automatic (non-overridden) proposals, while changing the Group Name never touches leaf ids. **Media type, hardware family, filename, TOSEC version/date, hashes, and random suffixes are never added.** **Folder tokens are atomic** — each single folder level is suggested via `FolderTokenTree.SuggestFolderToken` (reuses `NormalizeSuggestion`, then compacts that one segment's internal separators, so `Test Disks` → `testdisks`, never `test-disks`); the hyphen is reserved for joining components. Tokens are editable and excludable (empty token). The **Dat Suffix starts empty** (the long TOSEC filename is source metadata, never auto-embedded), is appended at the end, and only distinguishes DATs with the same proposed id. **Collisions are surfaced and blocked at confirm** (a short Dat Suffix or a manual id) — **no automatic hash, truncation, or filename fallback**. A manual final id is not silently overwritten (and can be reset to auto). The global `DatTechnicalIdPolicy.NormalizeSuggestion` and final `DatLineId` validation are unchanged.
- **Entry point:** `MainWindow.OnPreviewGroupDat` ("Create Group DAT…", a contextual Systems action) requires a **selected system**, then builds `GroupDatCatalogPreviewData` from the **live `_catalog`** (read-only SELECTs only — no leaf DB open, no schema write) and opens the window in **Create** mode with that snapshot plus the selected `_selectedPlatformId`/name as the immutable System context (never `CatalogService`). An **Update** entry point (open by existing Group ID) is not yet wired from the System view — the `ForExistingGroup` session mode is implemented and tested, ready for it. Producing a plan does not execute it.
- **Non-mutation:** the window/session receive no CatalogService/DatLineStore/connection string/data dir/write callback; before execution they call none of `CreateDatGroup`/`SaveDatLines`/`UpdateDatLineMetadata`/`RunImportWork`/`RunUpdateWork`/`DatLineStore`. Previous DAT `date`/`author` are not persisted → shown as "not available" (no leaf DB opened).
- **Source stability (future execution):** the frozen plan carries the immutable discovery snapshot; execution (a later phase) must reparse all source DATs, rebuild snapshots, compare fully, and block **before any write** if any DAT changed. Not implemented here.
- Tests: `Arkadia.Tests/GroupDats/GroupDatReconciliationTests.cs`.

**Still not implemented:** import/update execution, executor extraction, `CreateDatGroup`/`SaveDatLines` wiring, revision advancement/finalization, run/action journal, resume, fingerprints, automatic matching/overlap, split/merge.

---

## 19. Open items [OPEN]

Exact ROM-token definition for the semantic fingerprint; overlap thresholds (Jaccard/containment); `dat_groups` FK/delete specifics; whether to persist a last-used source-root hint for UX; real partial-unique index vs app guard for one-active-run; handling of an existing leaf without a stored fingerprint on first group update; recovery policy for the pre-existing partial-import orphan window; slug generic-word list; short-hash length; whether `retain_existing_leaf_without_update` needs explicit confirmation.
