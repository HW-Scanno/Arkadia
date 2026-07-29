namespace Arkadia.Data;

public sealed class StorageStrategyRecord
{
    public string Id          { get; set; } = "";
    public string Name        { get; set; } = "";
    public string Description { get; set; } = "";
    public int    SortOrder   { get; set; }
}
