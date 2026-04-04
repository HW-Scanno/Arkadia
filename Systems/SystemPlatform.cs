namespace Arkadia.Systems;

public sealed class SystemPlatform
{
    public required string Id           { get; init; }  // used as image filename, e.g. "nes"
    public required string Name         { get; init; }
    public required string Manufacturer { get; init; }
    public required string HardwareType { get; init; }
    public required int    DatLines     { get; init; }
    public required int    TotalTitles { get; init; }
    public required int    Present     { get; init; }
    public required int    Missing     { get; init; }
    public required int    Lost        { get; init; }

    public string Coverage => TotalTitles > 0
        ? $"{Present * 100 / TotalTitles}%"
        : "—";
}
