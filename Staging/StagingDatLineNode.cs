using System.Collections.Generic;

namespace Arkadia.Staging;

public sealed class StagingDatLineNode
{
    public required string                      DatLineId    { get; init; }
    public required string                      DatLineName  { get; init; }
    public required List<StagingReleaseNode>    Releases     { get; init; }
}
