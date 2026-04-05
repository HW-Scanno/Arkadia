using System;

namespace Arkadia.Data;

public sealed class ReleaseArtifactRecord
{
    public string   Id           { get; set; } = "";
    public string   ReleaseId    { get; set; } = "";
    public string   ArtifactId   { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
}
