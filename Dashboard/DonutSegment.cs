using Avalonia.Media;

namespace Arkadia.Dashboard;

/// <summary>One slice of a <see cref="DonutChartControl"/>.</summary>
public sealed class DonutSegment
{
    /// <summary>Raw value — proportions are computed from the total of all segments.</summary>
    public required double Value { get; init; }

    /// <summary>Fill brush for this arc slice.</summary>
    public required IBrush Fill { get; init; }
}
