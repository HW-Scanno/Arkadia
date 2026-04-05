namespace Arkadia.Data;

/// <summary>
/// One ROM/file entry declared in a Logiqx DAT for a specific release.
/// Corresponds to a single &lt;rom&gt; child element of a &lt;game&gt;.
/// </summary>
public sealed class ReleaseFileRecord
{
    public string Id        { get; set; } = "";
    public string ReleaseId { get; set; } = "";
    public string RomName   { get; set; } = "";
    public string Size      { get; set; } = "";
    public string Crc       { get; set; } = "";
    public string Md5       { get; set; } = "";
    public string Sha1      { get; set; } = "";
}
