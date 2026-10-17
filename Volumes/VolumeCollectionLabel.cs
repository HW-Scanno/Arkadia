namespace Arkadia.Volumes;

/// <summary>
/// Pure, presentation-only formatter for the Volumes "COLLECTION" column. It renders the owning collection of a
/// volume from ALREADY-RESOLVED display names (authority/media-type/group names looked up by the caller from the
/// canonical <c>authorities</c> / <c>media_types</c> / <c>dat_groups</c> tables) — it never resolves ids itself and
/// never duplicates canonical naming. The produced string is NEVER an identity: all volume actions route on
/// <c>RawDatLineId</c> / <c>GroupId</c> / volume id, never on this label.
/// </summary>
public static class VolumeCollectionLabel
{
    /// <summary>
    /// Single-owned volume: the persisted Display Name when set, otherwise <c>&lt;Authority&gt; · &lt;Media&gt;</c>
    /// (e.g. "ReDump · DVD"). Graceful fallback
    /// (media type normally always resolves — it is <c>NOT NULL DEFAULT 'other'</c>):
    /// authority+media → both; media only (authority missing/unresolvable) → media; neither → the DAT-line name;
    /// name unavailable → the technical dat_line_id. Inputs are resolved display names; casing is passed through.
    /// </summary>
    public static string ForSingle(string? authorityName, string? mediaTypeName, string? datLineName, string datLineId,
                                   string? displayName = null)
    {
        // A persisted Display Name (DAT-NAMING) wins; legacy rows (NULL) fall through to the historical label.
        var d = (displayName ?? "").Trim();
        if (d.Length > 0) return d;

        var a = (authorityName ?? "").Trim();
        var m = (mediaTypeName ?? "").Trim();
        var n = (datLineName   ?? "").Trim();
        return
            a.Length > 0 && m.Length > 0 ? $"{a} · {m}" :
            m.Length > 0                 ? m            :
            n.Length > 0                 ? n            :
                                           datLineId;
    }

    /// <summary>Group-owned volume: the Group display name (defensive fallback to the technical group id if empty).</summary>
    public static string ForGroup(string? groupDisplayName, string groupId)
    {
        var g = (groupDisplayName ?? "").Trim();
        return g.Length > 0 ? g : groupId;
    }
}
