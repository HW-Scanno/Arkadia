namespace Arkadia;

/// <summary>View-model row for the Write Volume to Disk progress table.</summary>
public sealed class WriteVolumeRow
{
    public required string Action    { get; init; }  // "copy" | "verify"
    public required string Path      { get; init; }
    public required string SizeLabel { get; init; }
}
