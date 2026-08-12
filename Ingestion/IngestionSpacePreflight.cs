using System.Collections.Generic;

namespace Arkadia.Ingestion;

/// <summary>
/// Pure staging-space estimate, extracted verbatim from Phase 5 of the ingestion pipeline so it can be
/// reused by a future Group orchestration over a cross-leaf copy plan. The formula is unchanged:
/// Σ (sourceLength × stageableTargetCount) + a 256 MB safety buffer. No source/artifact/compression
/// estimate is added.
/// </summary>
internal static class IngestionSpacePreflight
{
    internal const long SafetyBufferBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Bytes needed to stage the given plan items. Each item is one incoming source's length and the number
    /// of stageable targets it fans out to (for Group, targets may span multiple leaves — the formula is the
    /// same). Matches the historical Single-DAT calculation exactly.
    /// </summary>
    internal static long BytesNeeded(IEnumerable<(long SourceLength, int StageableTargetCount)> items)
    {
        long total = 0;
        foreach (var (len, count) in items)
            total += len * count;
        return total + SafetyBufferBytes;
    }
}
