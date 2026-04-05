using System;

namespace Arkadia.Data;

public sealed class ArtifactRecord
{
    public string    Id                 { get; set; } = "";
    public string    SourceFileName     { get; set; } = "";
    public string    SourceRelativePath { get; set; } = "";
    public long      SourceSizeBytes    { get; set; }
    public string    Crc                { get; set; } = "";
    public string    Md5                { get; set; } = "";
    public string    Sha1               { get; set; } = "";
    public string    ContentIdentityKey { get; set; } = "";
    public string    Status             { get; set; } = "";
    public DateTime  CreatedAtUtc       { get; set; }
    public DateTime? VerifiedAtUtc      { get; set; }
}
