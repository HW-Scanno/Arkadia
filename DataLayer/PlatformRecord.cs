namespace Arkadia.Data;

public sealed class HardwareFamilyRecord
{
    public string Id                { get; set; } = "";
    public string Name              { get; set; } = "";
    public string Manufacturer      { get; set; } = "";
    public string EcosystemId       { get; set; } = "";
    public string HardwareTypeId    { get; set; } = "";
    public string YearOfRelease     { get; set; } = "";
    public string Media             { get; set; } = "";
    public string Notes             { get; set; } = "";
    public string Cpu               { get; set; } = "";
    public string Memory            { get; set; } = "";
    public string Graphics          { get; set; } = "";
    public string Sound             { get; set; } = "";
    public string DisplayResolution { get; set; } = "";
    public string AspectRatio       { get; set; } = "";
    public string ScrapeSystemId   { get; set; } = "";
}
