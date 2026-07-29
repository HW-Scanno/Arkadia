using System;

namespace Arkadia.Data;

public sealed class ReleaseRecord
{
    public string Id        { get; set; } = "";
    public string DatLineId { get; set; } = "";
    public string Name      { get; set; } = "";
    /// <summary>
    /// Lowercase status value.
    /// Allowed values: "present", "pending", "missing", "lost", "outdated", "unwanted".
    /// </summary>
    public string Status    { get; set; } = "missing";

    /// <summary>
    /// Whether this release is shown in the normal catalog view.
    /// Defaults to true. Unwanted releases default to hidden.
    /// </summary>
    public bool ShowInCatalog { get; set; } = true;
    /// <summary>"A", "B", "C", or empty string.</summary>
    public string Tier      { get; set; } = "";
    public string Region    { get; set; } = "";
    /// <summary>Comma-separated lowercase codes, e.g. "en,fr,de".</summary>
    public string Languages  { get; set; } = "";
    public string Format     { get; set; } = "";
    public string Size       { get; set; } = "";
    /// <summary>
    /// Trusted-source content identity derived from ROM SHA1 (or MD5) checksums.
    /// Format: "sha1:&lt;hex&gt;[,sha1:&lt;hex&gt;...]" — see DatParser.ComputeContentKey.
    /// Empty string for releases imported before this field was introduced.
    /// </summary>
    public string ReleaseContentKey { get; set; } = "";

    /// <summary>
    /// UTC timestamp set when this release was first introduced by a DAT update.
    /// NULL for releases created during initial import or that predate this field.
    /// This is a change marker, not a primary status.
    /// </summary>
    public DateTime? IntroducedAtUtc { get; set; }

    /// <summary>Content classification for this release. Defaults to "games".</summary>
    public string ContentCategoryId { get; set; } = "games";
}
