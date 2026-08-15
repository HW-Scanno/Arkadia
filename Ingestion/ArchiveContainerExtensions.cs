using System;
using System.Collections.Generic;
using System.IO;

namespace Arkadia.Ingestion;

/// <summary>
/// The single authority for what counts as an extractable archive container: <c>.zip</c>, <c>.7z</c>,
/// <c>.rar</c> (case-insensitive). Single pre-ingest (<c>RunPreIngest</c>) and the Group planner both use this
/// exact set; the Group runtime bridge reuses it so no drifting extension definition can appear. This is not a
/// policy framework — just the shared constant + a path predicate.
/// </summary>
public static class ArchiveContainerExtensions
{
    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".zip", ".7z", ".rar" };

    /// <summary>True when the file's extension is a recognized archive container.</summary>
    public static bool IsContainer(string path) => All.Contains(Path.GetExtension(path));
}
