namespace Arkadia;

/// <summary>One row in the DAT Line Verify live table.</summary>
public sealed class VerifyRow
{
    public required string Volume { get; init; }
    public required string Result { get; init; }  // VERIFIED | MISSING | MISMATCH | UNEXPECTED | SKIPPED
    public required string Path   { get; init; }
    public required string Detail { get; init; }  // size / sha1 fragment / skip reason
}
