using System;

namespace Arkadia.Data;

public sealed class DerivedArtifactRecord
{
    public string    Id                 { get; set; } = "";
    public string    StorageStrategyId  { get; set; } = "";
    public string    FileName           { get; set; } = "";
    public string    RelativePath       { get; set; } = "";
    public long      SizeBytes          { get; set; }
    public string    Crc                { get; set; } = "";
    public string    Md5                { get; set; } = "";
    public string    Sha1               { get; set; } = "";
    public string    ContentIdentityKey { get; set; } = "";
    public string    Status             { get; set; } = "";
    public DateTime  CreatedAtUtc       { get; set; }
    public DateTime? VerifiedAtUtc      { get; set; }
}
