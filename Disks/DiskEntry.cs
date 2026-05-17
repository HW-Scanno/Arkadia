using Avalonia.Media;

namespace Arkadia.Disks;

/// <summary>View-model for a single row in the Disks table.</summary>
public sealed class DiskEntry
{
    public required string Id                    { get; init; }
    public required string Label                 { get; init; }
    public required string Status                { get; init; }
    public required string Family                { get; init; }
    public required long   DeclaredCapacityBytes { get; init; }
    public required long   UsedBytes             { get; init; }
    public required string Filesystem            { get; init; }
    public required string Brand                 { get; init; }
    public required string Model                 { get; init; }
    public required string Serial                { get; init; }

    public long   FreeBytes   => DeclaredCapacityBytes - UsedBytes;
    public double UsageRatio  => DiskUsageMath.CalculateUsageRatio(UsedBytes, DeclaredCapacityBytes);

    public string CapacityLabel => FormatBytes(DeclaredCapacityBytes);
    public string UsedLabel     => FormatBytes(UsedBytes);
    public string FreeLabel     => FormatBytes(FreeBytes);
    public string ModelLabel    => string.IsNullOrEmpty(Model)  ? "—" : Model;
    public string SerialLabel   => string.IsNullOrEmpty(Serial) ? "—" : Serial;
    public string FilesystemLabel => string.IsNullOrEmpty(Filesystem) ? "—" : Filesystem;

    public IBrush StatusBrush => Status switch
    {
        "available" => new SolidColorBrush(Color.Parse("#4CAF50")),
        "assigned"  => new SolidColorBrush(Color.Parse("#4CAF50")),
        "lost"      => new SolidColorBrush(Color.Parse("#EF5350")),
        _           => new SolidColorBrush(Color.Parse("#888899")),
    };

    public string StatusLabel => Status switch
    {
        "available" => "OK",
        "assigned"  => "OK",
        "lost"      => "LOST",
        _           => Status.ToUpperInvariant(),
    };

    private static string FormatBytes(long b)
    {
        if (b <= 0)            return "0 B";
        if (b < 1024L)         return $"{b} B";
        if (b < 1024L * 1024)  return $"{b / 1024.0:F1} KB";
        if (b < 1024L * 1024 * 1024) return $"{b / (1024.0 * 1024):F1} MB";
        return $"{b / (1024.0 * 1024 * 1024):F2} GB";
    }
}
