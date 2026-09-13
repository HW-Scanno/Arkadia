using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Arkadia.Data;
using Arkadia.Ingestion;

namespace Arkadia.GroupDats;

/// <summary>
/// Read-only archive-entry scan across a Group's leaf DATs for the Configure Group notice. Opens each leaf DB with
/// <see cref="DatLineStore.OpenReadOnly"/> (no schema creation, no writes), summarizes it, and releases the per-leaf
/// release-file lists immediately. A missing/unreadable leaf never aborts the scan — it is recorded in
/// <see cref="DatArchiveEntrySummary.UnscannedLeafIds"/> and the usable results from other leaves are kept.
/// Never touches incoming, staging, extracted files, the catalog, or ingestion state. Intended to run off the UI thread.
/// </summary>
public static class GroupArchiveEntryScanner
{
    public static DatArchiveEntrySummary Scan(
        IReadOnlyList<DatLineRecord> leaves,
        string                       dataDir,
        CancellationToken            cancellationToken = default,
        IProgress<int>?              progress          = null)
    {
        var parts     = new List<DatArchiveEntrySummary>(leaves.Count);
        var unscanned = new List<string>();

        for (int i = 0; i < leaves.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var leaf = leaves[i];
            var dsp  = leaf.DataStorePath ?? "";
            var abs  = dsp.Length > 0 ? Path.Combine(dataDir, dsp) : "";

            // Never construct a store for a missing path.
            if (abs.Length == 0 || !File.Exists(abs))
            {
                unscanned.Add(leaf.Id);
            }
            else
            {
                try
                {
                    var store = DatLineStore.OpenReadOnly(abs);
                    try
                    {
                        parts.Add(DatArchiveEntrySummary.ForLeaf(leaf.Id, store.LoadReleases(), store.LoadAllReleaseFiles()));
                    }
                    finally
                    {
                        store.ReleaseConnections();   // don't keep hundreds of pooled leaf connections alive
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
                {
                    unscanned.Add(leaf.Id);
                }
            }

            progress?.Report(i + 1);
        }

        return DatArchiveEntrySummary.Merge(parts, unscanned);
    }
}
