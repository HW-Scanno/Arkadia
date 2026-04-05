namespace Arkadia.Staging;

public sealed class StagingSummary
{
    public int    FilesPresent       { get; set; }
    public long   TotalSizeBytes     { get; set; }
    public int    IncompleteReleases { get; set; }

    public string SizeGbLabel => TotalSizeBytes > 0
        ? $"{TotalSizeBytes / (1024.0 * 1024 * 1024):F2} GB"
        : "0.00 GB";
}
