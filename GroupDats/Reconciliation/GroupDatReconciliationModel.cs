using System;
using Arkadia.Data;

namespace Arkadia.GroupDats;

/// <summary>Whether the preview creates a new Group DAT or updates an existing one.</summary>
public enum GroupDatReconciliationMode { NewGroup, UpdateGroup }

/// <summary>Kind of a resolved reconciliation decision.</summary>
public enum GroupDatDecisionKind { Update, NewLeaf, Absent }

/// <summary>
/// A discovered DAT available for manual resolution. Wraps the immutable Phase-3A
/// <see cref="DiscoveredDatLeaf"/>. Session identity is <see cref="CandidateId"/> (a stable token,
/// never a visual index). <see cref="DatToken"/> is editable and feeds new-leaf id proposals.
/// </summary>
public sealed class IncomingDatCandidate
{
    public string           CandidateId { get; }
    public DiscoveredDatLeaf Leaf       { get; }
    public string           DatToken    { get; set; }

    /// <summary>
    /// A user-typed final leaf id, or null when the id still tracks the automatic proposal.
    /// When null, the effective id is recomputed from folder + DAT tokens; when set, it is a manual
    /// override that is not silently overwritten by later token changes.
    /// </summary>
    public string? FinalIdOverride { get; internal set; }

    public bool IsFinalIdManual => FinalIdOverride is not null;

    /// <summary>
    /// In New-Group mode every parsed DAT is an automatic new-leaf <b>draft</b>; this holds the
    /// media type chosen for it, or null until the user picks one. Purely in-memory — no DB write.
    /// </summary>
    public string? DraftMediaTypeId { get; internal set; }

    internal IncomingDatCandidate(string candidateId, DiscoveredDatLeaf leaf, string datToken)
    {
        CandidateId = candidateId; Leaf = leaf; DatToken = datToken;
    }

    public string RelativePath => Leaf.RelativePath;
    public string FileName     => Leaf.FileName;
    public string SourcePath   => Leaf.SourcePath;
    public string HeaderName   => Leaf.DatName;
    public string Version      => Leaf.DatVersion;
    public string Date         => Leaf.DatDate;
    public string Author       => Leaf.DatAuthor;
    public int    ReleaseCount => Leaf.GameCount;
    public string FolderPath   => FolderTokenTree.FolderOf(Leaf.RelativePath);
}

/// <summary>An existing Group leaf available for manual resolution (identity = <see cref="GroupDatExistingLeaf.DatLineId"/>).</summary>
public sealed class ExistingGroupLeafCandidate
{
    public GroupDatExistingLeaf Leaf { get; }
    internal ExistingGroupLeafCandidate(GroupDatExistingLeaf leaf) => Leaf = leaf;
    public string DatLineId => Leaf.DatLineId;
}

/// <summary>One resolved decision (update / new leaf / absent). Immutable once created; Undo removes it.</summary>
public sealed class GroupDatDecision
{
    public string                     DecisionId  { get; }
    public GroupDatDecisionKind        Kind        { get; }
    public IncomingDatCandidate?       Dat         { get; }   // Update, NewLeaf
    public ExistingGroupLeafCandidate? Leaf        { get; }   // Update, Absent
    public string?                     FinalId     { get; }   // NewLeaf
    public string?                     MediaTypeId { get; }   // NewLeaf (Update keeps the existing leaf's media type)

    internal GroupDatDecision(
        GroupDatDecisionKind kind, IncomingDatCandidate? dat, ExistingGroupLeafCandidate? leaf,
        string? finalId, string? mediaTypeId)
    {
        DecisionId  = Guid.NewGuid().ToString("N");
        Kind = kind; Dat = dat; Leaf = leaf; FinalId = finalId; MediaTypeId = mediaTypeId;
    }
}
