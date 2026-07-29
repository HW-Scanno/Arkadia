namespace Arkadia.Data;

/// <summary>
/// Stores the DAT-declared checksums for a single logical content identity.
/// This is the logical layer — content_identity_key is the canonical key,
/// derived from DAT-declared checksums only, never from observed file hashing.
/// </summary>
public sealed class ContentIdentityRecord
{
    public required string  ContentIdentityKey { get; init; }
    /// <summary>DAT-declared SHA1 hex (40 chars), or null if not provided.</summary>
    public required string? DatSha1            { get; init; }
    /// <summary>DAT-declared MD5 hex (32 chars), or null if not provided.</summary>
    public required string? DatMd5             { get; init; }
    /// <summary>DAT-declared CRC32 hex (8 chars), or null if not provided.</summary>
    public required string? DatCrc32           { get; init; }
    public required System.DateTime CreatedAtUtc { get; init; }
}
