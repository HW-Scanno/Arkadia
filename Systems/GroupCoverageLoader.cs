using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Arkadia.Data;

namespace Arkadia.Systems;

/// <summary>
/// Computes a Group DAT's wanted coverage by opening its leaf databases and summing statuses. This is the
/// ONLY place that opens Group-leaf DBs for coverage, and it is always called off the UI thread (lazily,
/// on demand) — never during the synchronous startup/RefreshSystems path. The formula matches
/// <see cref="SystemPlatform"/>: Σ present ÷ Σ max(0, releaseCount − unwanted).
/// </summary>
public static class GroupCoverageLoader
{
    /// <summary>
    /// Sums present and unwanted across the given leaves (each existing leaf DB opened exactly once). The
    /// raw pair plugs straight into <see cref="SystemPlatform"/> math; the group card derives its wanted
    /// denominator as Σ releaseCount − Σ unwanted. <paramref name="onLeafLoaded"/> is invoked after each
    /// leaf so a UI can report progress (e.g. "173 / 410").
    /// </summary>
    public static (int Present, int Unwanted) Compute(
        IReadOnlyList<(string DataStorePath, int ReleaseCount)> leaves,
        string                                                  dataDir,
        Action<int>?                                            onLeafLoaded      = null,
        CancellationToken                                       cancellationToken = default)
    {
        int present = 0, unwanted = 0;
        for (int i = 0; i < leaves.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (dataStorePath, _) = leaves[i];

            if (dataStorePath.Length > 0)
            {
                var abs = Path.Combine(dataDir, dataStorePath);
                if (File.Exists(abs))
                {
                    var c = new DatLineStore(abs).GetAllStatusCounts();
                    present  += c.Present;
                    unwanted += c.Unwanted;
                }
            }
            onLeafLoaded?.Invoke(i + 1);
        }
        return (present, unwanted);
    }
}
