using System;

namespace Arkadia.Data;

public sealed class DatLineRecord
{
    public string   Id            { get; set; } = "";
    public string   PlatformId    { get; set; } = "";
    public string   Name          { get; set; } = "";
    public string   Authority          { get; set; } = "";
    public string   DatCategory        { get; set; } = "";
    public string   Version            { get; set; } = "";
    public string   StorageStrategyId  { get; set; } = "";
    public string   DataStorePath      { get; set; } = "";   // relative: data/systems/<pid>/<id>.db
    public int      ReleaseCount       { get; set; }
    public DateTime ImportedAtUtc      { get; set; } = DateTime.UtcNow;
}
