namespace Arkadia.Data;

/// <summary>
/// Read-only view of the Group DAT metadata columns on a leaf <c>dat_lines</c> row (Phase 2).
///
/// <para>Kept as a companion record rather than extending <see cref="DatLineRecord"/> so the
/// many existing <c>DatLineRecord</c> constructors/call sites are untouched. All fields are
/// nullable: for a Single DAT (and every legacy leaf) they are <c>NULL</c>. Phase 2 only
/// <b>reads</b> these — no workflow populates them yet.</para>
///
/// <para><see cref="GroupId"/> is exposed as the raw stored string (may be <c>NULL</c>);
/// callers that need a typed id wrap it via <c>DatGroupId.FromPersisted</c>.</para>
/// </summary>
public sealed class DatLineGroupMetadataRecord
{
    public string  DatLineId                  { get; init; } = "";
    public string? GroupId                    { get; init; }
    public string? RelativeDatPath            { get; init; }
    public string? SourceDatName              { get; init; }
    public string? SourceDatSha256            { get; init; }
    public string? SemanticFingerprint        { get; init; }
    public int?    SemanticFingerprintVersion { get; init; }
    public int?    LastSeenGroupRevision      { get; init; }
}
