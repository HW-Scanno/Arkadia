using System;
using System.Collections.Generic;
using System.Linq;
using Arkadia.Data;

namespace Arkadia.Volumes;

/// <summary>Which kind of collection owns a volume.</summary>
public enum VolumeOwnerKind { SingleDat, Group }

/// <summary>
/// A typed top-level volume-owner choice for the unified Create Volume dialog. Identity is
/// (<see cref="Kind"/>, <see cref="OwnerId"/>) — never the human <see cref="Display"/> text. For a Single DAT
/// <see cref="OwnerId"/> is the DatLineId; for a Group it is the GroupId.
/// </summary>
public sealed record VolumeOwnerSelection(VolumeOwnerKind Kind, string OwnerId, string PlatformId, string Display)
{
    // Avalonia renders an untemplated combo item via ToString().
    public override string ToString() => Display;

    /// <summary>
    /// Descriptive Single-owner display: <c>Single · &lt;Authority&gt; · &lt;name&gt;</c>. Falls back
    /// gracefully so it never collapses to an ambiguous <c>Single · cd</c> when the technical DatLineId is
    /// available: authority+name → authority+id → name+id → id. Identity is unaffected (always the DatLineId).
    /// </summary>
    public static string SingleDisplay(string? authorityName, string? name, string datLineId)
    {
        var a = (authorityName ?? "").Trim();
        var n = (name ?? "").Trim();
        string core =
            a.Length > 0 && n.Length > 0 ? $"{a} · {n}"          :
            a.Length > 0                 ? $"{a} · {datLineId}"   :
            n.Length > 0                 ? $"{n} · {datLineId}"   :
                                           datLineId;
        return $"Single · {core}";
    }

    /// <summary>The suggested-label prefix for this owner: DatLineId for Single, GroupId for Group.</summary>
    public string LabelPrefix => OwnerId;

    /// <summary>
    /// Builds the header-only <see cref="VolumeRecord"/> for this owner (ownership dispatched on
    /// <see cref="Kind"/>): Single → DatLineId set / GroupId null; Group → GroupId set / DatLineId null.
    /// Always <c>init</c>, actual 0. SaveVolume enforces the XOR/FK invariant.
    /// </summary>
    public VolumeRecord ToVolumeRecord(string label, long plannedSizeBytes) => new()
    {
        Id               = Guid.NewGuid().ToString("N"),
        Label            = label,
        PlatformId       = PlatformId,
        DatLineId        = Kind == VolumeOwnerKind.SingleDat ? OwnerId : null,
        GroupId          = Kind == VolumeOwnerKind.Group     ? OwnerId : null,
        Status           = "init",
        PlannedSizeBytes = plannedSizeBytes,
        ActualSizeBytes  = 0,
        CreatedAt        = DateTime.UtcNow,
    };
}

/// <summary>
/// Pure builder for the unified Create Volume owner selector. For a platform it lists the platform's standalone
/// Single DATs (dat lines whose id is NOT a Group leaf) followed by the platform's Groups — never the Group leaf
/// dat lines individually. Group-leaf membership comes from the supplied catalog-derived set, not naming.
/// Deterministic: Single first then Group, each ordered by display name (OrdinalIgnoreCase).
/// </summary>
public static class VolumeOwnerSelectionBuilder
{
    public static List<VolumeOwnerSelection> Build(
        string                             platformId,
        IReadOnlyList<DatLineRecord>       datLines,
        IReadOnlyList<DatGroupRecord>      groups,
        ISet<string>                       groupLeafIds,
        IReadOnlyDictionary<string, string> authorityNames)
    {
        string AuthorityDisplay(string authorityId)
            => authorityNames.TryGetValue(authorityId, out var name) ? name : authorityId;

        var owners = new List<VolumeOwnerSelection>();

        owners.AddRange(datLines
            .Where(d => d.HardwareFamilyId == platformId && !groupLeafIds.Contains(d.Id))
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Select(d => new VolumeOwnerSelection(VolumeOwnerKind.SingleDat, d.Id, platformId,
                VolumeOwnerSelection.SingleDisplay(AuthorityDisplay(d.Authority), d.Name, d.Id))));

        owners.AddRange(groups
            .Where(g => g.HardwareFamilyId == platformId)
            .OrderBy(g => g.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(g => new VolumeOwnerSelection(VolumeOwnerKind.Group, g.Id.Value, platformId, $"Group · {g.DisplayName}")));

        return owners;
    }
}

/// <summary>
/// Pure best-effort label-suggestion logic for the unified Create Volume dialog. The prefix is the strongest
/// available technical owner identity: the selected owner's <c>OwnerId</c> (DatLineId or GroupId), else the
/// technical PlatformId when no collection is selected. Numbering itself is delegated to
/// <see cref="VolumeLabelNumbering"/> (global scan, case-insensitive, max+1, gaps skipped).
/// </summary>
public static class VolumeLabelSuggestion
{
    public static string Prefix(VolumeOwnerSelection? owner, string platformId)
        => owner is not null ? owner.OwnerId : platformId;

    public static string Suggest(IReadOnlyList<string> existingLabels, VolumeOwnerSelection? owner, string platformId)
    {
        var prefix = Prefix(owner, platformId);
        return string.IsNullOrEmpty(prefix) ? "" : VolumeLabelNumbering.SuggestLabel(existingLabels, prefix);
    }
}
