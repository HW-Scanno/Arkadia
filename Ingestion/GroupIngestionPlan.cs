using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Arkadia.Ingestion;

/// <summary>Overall readiness of a Group ingestion plan.</summary>
public enum GroupIngestionReadiness
{
    /// <summary>All in-scope leaves prepared and validated; there is work to do (or resumable finalization).</summary>
    Ready,
    /// <summary>At least one relevant leaf cannot be prepared, or File Handling is mixed — nothing may execute.</summary>
    Blocked,
    /// <summary>Every leaf is already satisfied and nothing is resumable — no ingestion work is required.</summary>
    NothingToDo,
}

/// <summary>A leaf-qualified match target (the authoritative identity — never a display label or path).</summary>
public sealed record GroupMatchTarget(string DatLineId, string ReleaseId, string RomName);

/// <summary>
/// A wanted ROM that requires NEW secure materialization (not DB-satisfied, not already staged/sourced under
/// current Single semantics). Carries the DAT-declared hashes/size for the future aggregate scan/hash, plus
/// the release name so execution can derive the staging folder without re-opening the leaf DB.
/// <para><see cref="Kind"/> classifies how the requirement can be satisfied. Every kind stays an authoritative
/// requirement of its release; only <see cref="DatFileRequirementKind.Physical"/> targets enter the aggregate
/// hash indexes (synthesizable empty files are generated at finalization; unmatchable/contradictory ones cannot be
/// satisfied and are surfaced as plan notices).</para>
/// </summary>
public sealed record GroupRequiredTarget(
    GroupMatchTarget       Target,
    string                 ReleaseName,
    long                   ExpectedSize,
    string                 Sha1,
    string                 Md5,
    DatFileRequirementKind Kind = DatFileRequirementKind.Physical);

/// <summary>A release whose staging is already complete but which still needs finalization (no new incoming needed).</summary>
public sealed record GroupResumableRelease(string DatLineId, string ReleaseId);

/// <summary>Per-leaf summary in the plan.</summary>
public sealed record GroupPreparedLeaf(
    string DatLineId,
    string DataStorePath,
    int    RequiredTargetCount,
    int    ResumableReleaseCount)
{
    /// <summary>In scope iff it has matchable required targets OR resumable finalization work.</summary>
    public bool InScope => RequiredTargetCount > 0 || ResumableReleaseCount > 0;
}

/// <summary>A readiness problem for one leaf (or a Group-level problem when <see cref="DatLineId"/> is empty).</summary>
public sealed record GroupLeafDiagnostic(string DatLineId, string Reason);

/// <summary>Severity of a non-blocking plan notice.</summary>
public enum GroupPlanNoticeSeverity { Info, Warning }

/// <summary>Stable codes for non-blocking plan notices.</summary>
public static class GroupPlanNoticeCodes
{
    /// <summary>Authoritative zero-byte hashless requirements that finalization will synthesize (informational).</summary>
    public const string SynthesizableEmpty = "SynthesizableEmpty";
    /// <summary>Zero-byte hashless requirements that will NOT be synthesized because their release is not 'missing'.</summary>
    public const string SynthesisNotPermitted = "SynthesisNotPermitted";
    /// <summary>Hashless (or CRC-only) requirements that the matcher cannot identify — their releases stay incomplete.</summary>
    public const string UnmatchableRequirement = "UnmatchableRequirement";
    /// <summary>size=0 requirements whose declared hashes are not the empty-file hashes — their releases stay incomplete.</summary>
    public const string ContradictoryZeroByte = "ContradictoryZeroByte";
}

/// <summary>
/// A NON-blocking plan notice. Unlike <see cref="GroupLeafDiagnostic"/> it does not change readiness: it makes
/// requirements that ingestion will synthesize, or can never satisfy, explicit instead of silent.
/// </summary>
public sealed record GroupPlanNotice(string DatLineId, GroupPlanNoticeSeverity Severity, string Code, string Message);

/// <summary>Read-only inspection of the incoming directory (names/counts only — no extraction, no hashing).</summary>
public sealed record GroupIncomingInspection(int LooseFiles, int ArchiveContainers);

/// <summary>
/// The frozen, read-only result of Group ingestion planning: validated membership, scope, aggregate missing
/// indexes, resumable releases, incoming inspection, and readiness/diagnostics. Immutable — it is the object
/// M9C2 will consume (after re-reading membership) to execute. It performs and represents NO mutation.
/// </summary>
public sealed class GroupIngestionPlan
{
    public required string                                             GroupId            { get; init; }
    public required string                                             DisplayName        { get; init; }
    public required string                                             PlatformId         { get; init; }
    public required ImmutableArray<string>                             ExpectedLeafIds    { get; init; }
    /// <summary>The common File Handling across in-scope leaves, or null when mixed (blocked) / no in-scope leaves.</summary>
    public required string?                                            FileHandling       { get; init; }
    public required ImmutableArray<GroupPreparedLeaf>                  PreparedLeaves     { get; init; }
    public required ImmutableArray<GroupRequiredTarget>                RequiredTargets    { get; init; }
    public required IReadOnlyDictionary<string, List<GroupMatchTarget>> AggregateSha1Index { get; init; }
    public required IReadOnlyDictionary<string, List<GroupMatchTarget>> AggregateMd5Index  { get; init; }
    public required ImmutableArray<GroupResumableRelease>              ResumableReleases  { get; init; }
    public required GroupIncomingInspection                            IncomingInspection { get; init; }
    public required GroupIngestionReadiness                            Readiness          { get; init; }
    public required ImmutableArray<GroupLeafDiagnostic>                Diagnostics        { get; init; }
    /// <summary>Non-blocking notices (synthesizable / unmatchable / contradictory requirements). Never affect readiness.</summary>
    public ImmutableArray<GroupPlanNotice>                             Notices            { get; init; } = ImmutableArray<GroupPlanNotice>.Empty;

    public int TotalLeaves        => ExpectedLeafIds.Length;
    public int InScopeLeafCount   => PreparedLeaves.Count(l => l.InScope);
    public int SatisfiedLeafCount => PreparedLeaves.Count(l => !l.InScope);
    /// <summary>Hash-matchable required targets (the ones incoming material can satisfy).</summary>
    public int MissingTargetCount => RequiredTargets.Count(t => t.Kind == DatFileRequirementKind.Physical);
    public int SynthesizableEmptyTargetCount => RequiredTargets.Count(t => t.Kind == DatFileRequirementKind.SynthesizableEmpty);
    public int UnsatisfiableTargetCount      => RequiredTargets.Count(t => DatFileRequirement.IsUnsatisfiable(t.Kind));
    public int ResumableCount     => ResumableReleases.Length;
    public int BlockedLeafCount   => Diagnostics.Count(d => d.DatLineId.Length > 0);
}
