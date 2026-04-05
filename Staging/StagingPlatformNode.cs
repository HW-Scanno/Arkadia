using System.Collections.Generic;

namespace Arkadia.Staging;

public sealed class StagingPlatformNode
{
    public required string                      PlatformId   { get; init; }
    public required string                      PlatformName { get; init; }
    public required List<StagingDatLineNode>    DatLines     { get; init; }
}
