namespace Arkadia.Volumes;

public enum DestinationType { Workspace, Disk }

public enum DestinationState { Ready, NotMounted, NotEnoughFreeSpace }

/// <summary>
/// View-model for one row in the Move Volume destination picker.
/// Combines catalog disk records with runtime discovery data.
/// Mountpoint is runtime-only and never persisted.
/// </summary>
public sealed class VolumeDestination
{
    public required string          DisplayName        { get; init; }
    public required DestinationType DestinationType    { get; init; }
    public          string?         DiskId             { get; init; }
    public          string?         DiskLabel          { get; init; }
    public required long            TotalCapacityBytes { get; init; }
    public required long            FreeSpaceBytes     { get; init; }
    public required long            RequiredBytes      { get; init; }
    public required DestinationState State             { get; init; }
    public          string?         Mountpoint         { get; init; }  // runtime-only, never persisted

    public string TotalLabel    => FormatBytes(TotalCapacityBytes);
    public string FreeLabel     => FormatBytes(FreeSpaceBytes);
    public string RequiredLabel => FormatBytes(RequiredBytes);

    public string StatusText => State switch
    {
        DestinationState.Ready              => "READY",
        DestinationState.NotMounted         => "NOT MOUNTED",
        DestinationState.NotEnoughFreeSpace => "NOT ENOUGH FREE SPACE",
        _                                   => "UNKNOWN",
    };

    public bool IsSelectable => State == DestinationState.Ready;

    private static string FormatBytes(long b)
    {
        if (b <= 0)                   return "—";
        if (b >= 1L << 40)            return $"{b / (double)(1L << 40):F1} TB";
        if (b >= 1L << 30)            return $"{b / (double)(1L << 30):F1} GB";
        if (b >= 1L << 20)            return $"{b / (double)(1L << 20):F1} MB";
        return $"{b / (double)(1L << 10):F0} KB";
    }
}
