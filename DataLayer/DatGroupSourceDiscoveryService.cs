using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;

namespace Arkadia.Data;

/// <summary>
/// Pure, DB-independent discovery of candidate leaf DATs under a Group DAT source directory.
///
/// <para>Read-only: it opens source files to parse them but never writes, creates directories,
/// caches, stages, touches runtime <c>data/</c>, or opens any database. It requires no
/// CatalogService, connection string, hardware family, authority, or id. Given the same directory
/// tree it produces the same ordered result. It answers only "which candidate DAT leaves exist and
/// how did parsing go?" — no matching, id assignment, fingerprinting, or planning (later phases).</para>
///
/// <para>A candidate is any file whose extension is <c>.dat</c> (case-insensitive), matching the
/// existing Single DAT import filter. Malformed/unreadable DATs are represented, not fatal.
/// Directory reparse points (symlinks/junctions) are NOT followed. Relative-path collisions
/// (case-insensitive) are a blocking diagnostic; files are never auto-chosen or merged.</para>
/// </summary>
public sealed class DatGroupSourceDiscoveryService
{
    /// <summary>
    /// Scans <paramref name="sourceRoot"/> recursively and returns an in-memory manifest.
    /// Throws <see cref="ArgumentException"/> for a null/blank root (contract violation) and
    /// <see cref="OperationCanceledException"/> if cancelled. A missing root or a root that is a
    /// file is reported as a blocking diagnostic (not an exception).
    /// </summary>
    public DatGroupDiscoveryResult Discover(string sourceRoot, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceRoot))
            throw new ArgumentException("Source root must be a non-empty path.", nameof(sourceRoot));

        cancellationToken.ThrowIfCancellationRequested();

        var root = Path.GetFullPath(sourceRoot);

        if (!Directory.Exists(root))
        {
            var (code, message) = File.Exists(root)
                ? (DatGroupDiscoveryDiagnosticCodes.SourceRootNotDirectory, "Source root is a file, not a directory.")
                : (DatGroupDiscoveryDiagnosticCodes.SourceRootMissing,      "Source root does not exist.");
            return Empty(root, new DatGroupDiscoveryDiagnostic(
                code, DatGroupDiscoveryDiagnosticSeverity.Error, message));
        }

        var diagnostics = new List<DatGroupDiscoveryDiagnostic>();
        var candidates  = new List<(string Rel, string Abs, string FileName)>();

        // Iterative traversal (no recursion → no stack-overflow risk on deep trees).
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dir = stack.Pop();

            List<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(dir).ToList();
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                diagnostics.Add(new DatGroupDiscoveryDiagnostic(
                    DatGroupDiscoveryDiagnosticCodes.SourceRootEnumerationFailed,
                    DatGroupDiscoveryDiagnosticSeverity.Error,
                    $"Could not enumerate directory ({ex.GetType().Name}).",
                    RelOrNull(root, dir)));
                continue;
            }

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (Directory.Exists(entry))
                {
                    // Do not follow reparse points (symlinks / junctions): avoids escaping the
                    // root and traversal loops.
                    if (IsReparsePoint(entry))
                    {
                        diagnostics.Add(new DatGroupDiscoveryDiagnostic(
                            DatGroupDiscoveryDiagnosticCodes.ReparsePointSkipped,
                            DatGroupDiscoveryDiagnosticSeverity.Warning,
                            "Skipped a directory reparse point (symlink/junction).",
                            RelOrNull(root, entry)));
                        continue;
                    }
                    stack.Push(entry);
                    continue;
                }

                // File. Only .dat candidates are considered; everything else is ignored silently.
                if (!IsDatCandidate(entry)) continue;

                var rel = TryMakeRelative(root, entry);
                if (rel is null)
                {
                    diagnostics.Add(new DatGroupDiscoveryDiagnostic(
                        DatGroupDiscoveryDiagnosticCodes.RelativePathOutsideRoot,
                        DatGroupDiscoveryDiagnosticSeverity.Error,
                        "A candidate resolved outside the source root and was skipped.",
                        null));
                    continue;
                }

                candidates.Add((rel, entry, Path.GetFileName(entry)));
            }
        }

        // Deterministic order by normalized relative path (Ordinal, culture-independent).
        candidates.Sort((a, b) => string.CompareOrdinal(a.Rel, b.Rel));

        // Case-insensitive relative-path collisions → blocking; never auto-choose or merge.
        diagnostics.AddRange(DetectRelativePathCollisions(candidates.Select(c => c.Rel).ToList()));

        // Parse candidates (in sorted order) and build leaves.
        var leaves = new List<DiscoveredDatLeaf>(candidates.Count);
        foreach (var (rel, abs, fileName) in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            leaves.Add(BuildLeaf(rel, abs, fileName, diagnostics, cancellationToken));
        }

        // Deterministic diagnostic order: by relative path (global first), then code.
        diagnostics.Sort((a, b) =>
        {
            var byPath = string.CompareOrdinal(a.RelativePath ?? "", b.RelativePath ?? "");
            return byPath != 0 ? byPath : string.CompareOrdinal(a.Code, b.Code);
        });

        return new DatGroupDiscoveryResult
        {
            SourceRoot  = root,
            Leaves      = leaves,
            Diagnostics = diagnostics,
        };
    }

    private static DiscoveredDatLeaf BuildLeaf(
        string rel, string abs, string fileName,
        List<DatGroupDiscoveryDiagnostic> diagnostics, CancellationToken cancellationToken)
    {
        // Readability probe first so an unreadable file is distinguished from a malformed one.
        try
        {
            using var fs = File.OpenRead(abs);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            var diag = new DatGroupDiscoveryDiagnostic(
                DatGroupDiscoveryDiagnosticCodes.DatReadFailed,
                DatGroupDiscoveryDiagnosticSeverity.Error,
                $"Could not read DAT file ({ex.GetType().Name}).",
                rel);
            diagnostics.Add(diag);
            return new DiscoveredDatLeaf
            {
                RelativePath = rel, FileName = fileName, SourcePath = abs,
                Status = DiscoveredDatLeafStatus.ReadFailed, Diagnostic = diag,
            };
        }

        cancellationToken.ThrowIfCancellationRequested();
        var result = DatParser.Parse(abs);

        if (!result.Success)
        {
            // result.ErrorMessage is parser-controlled and contains no absolute path.
            var diag = new DatGroupDiscoveryDiagnostic(
                DatGroupDiscoveryDiagnosticCodes.DatParseFailed,
                DatGroupDiscoveryDiagnosticSeverity.Error,
                result.ErrorMessage.Length > 0 ? result.ErrorMessage : "DAT parsing failed.",
                rel);
            diagnostics.Add(diag);
            return new DiscoveredDatLeaf
            {
                RelativePath = rel, FileName = fileName, SourcePath = abs,
                Status = DiscoveredDatLeafStatus.ParseFailed, Diagnostic = diag,
            };
        }

        return new DiscoveredDatLeaf
        {
            RelativePath = rel, FileName = fileName, SourcePath = abs,
            Status     = DiscoveredDatLeafStatus.Parsed,
            DatName    = result.Name,
            DatVersion = result.Version,
            DatDate    = result.Date,
            DatAuthor  = result.Author,
            // Deep, immutable snapshot: the manifest never exposes the parser's mutable
            // ParsedGame/ParsedRom or their List<T>.
            Games = SnapshotGames(result.Games),
        };
    }

    /// <summary>
    /// Pure deep snapshot of the parser's games into immutable <see cref="DiscoveredDatGame"/> /
    /// <see cref="DiscoveredDatRom"/> arrays. Copies every value exactly, preserves order and
    /// multiplicity, and retains no reference to the parser's mutable collections — so later
    /// mutation of the parser result cannot affect the manifest. Extracted (internal) so the
    /// isolation guarantee is directly testable; no public helper is exposed.
    /// </summary>
    internal static ImmutableArray<DiscoveredDatGame> SnapshotGames(IReadOnlyList<DatParser.ParsedGame> games)
    {
        var gamesBuilder = ImmutableArray.CreateBuilder<DiscoveredDatGame>(games.Count);
        for (int i = 0; i < games.Count; i++)
        {
            var g          = games[i];
            var romsBuilder = ImmutableArray.CreateBuilder<DiscoveredDatRom>(g.Roms.Count);
            for (int j = 0; j < g.Roms.Count; j++)
            {
                var r = g.Roms[j];
                romsBuilder.Add(new DiscoveredDatRom(r.Name, r.Size, r.Crc, r.Md5, r.Sha1));
            }
            gamesBuilder.Add(new DiscoveredDatGame(
                g.Name, g.Region, g.Languages, g.ContentKey, g.WorkingState, romsBuilder.MoveToImmutable()));
        }
        return gamesBuilder.MoveToImmutable();
    }

    /// <summary>
    /// Pure collision detector (extracted so it is testable without a case-sensitive filesystem):
    /// one blocking diagnostic per group of relative paths that are equal under
    /// <see cref="StringComparer.OrdinalIgnoreCase"/>. Deterministically ordered.
    /// </summary>
    internal static List<DatGroupDiscoveryDiagnostic> DetectRelativePathCollisions(IReadOnlyList<string> relativePaths)
    {
        var result = new List<DatGroupDiscoveryDiagnostic>();
        foreach (var group in relativePaths
                     .GroupBy(r => r, StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1))
        {
            var representative = group.OrderBy(r => r, StringComparer.Ordinal).First();
            result.Add(new DatGroupDiscoveryDiagnostic(
                DatGroupDiscoveryDiagnosticCodes.RelativePathCollision,
                DatGroupDiscoveryDiagnosticSeverity.Error,
                $"{group.Count()} candidate DATs share a case-insensitively identical relative path.",
                representative));
        }
        result.Sort((a, b) => string.CompareOrdinal(a.RelativePath ?? "", b.RelativePath ?? ""));
        return result;
    }

    private static bool IsDatCandidate(string path) =>
        Path.GetExtension(path).Equals(".dat", StringComparison.OrdinalIgnoreCase);

    private static bool IsReparsePoint(string dir)
    {
        try
        {
            return (new DirectoryInfo(dir).Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            // If attributes cannot be read, be conservative and treat it as a link (skip).
            return true;
        }
    }

    /// <summary>
    /// Relative path from <paramref name="root"/> to <paramref name="fullPath"/>, normalized to
    /// forward slashes, or null if it would escape the root. Preserves original casing/Unicode.
    /// </summary>
    private static string? TryMakeRelative(string root, string fullPath)
    {
        var rel = Path.GetRelativePath(root, fullPath);
        if (rel.Length == 0 || rel == "." ||
            rel.StartsWith("..", StringComparison.Ordinal) ||
            Path.IsPathRooted(rel))
            return null;
        return rel.Replace('\\', '/');
    }

    private static string? RelOrNull(string root, string fullPath)
    {
        var rel = TryMakeRelative(root, fullPath);
        return rel;   // null for the root itself or an escaping path → treated as global
    }

    private static DatGroupDiscoveryResult Empty(string root, DatGroupDiscoveryDiagnostic diagnostic) => new()
    {
        SourceRoot  = root,
        Leaves      = Array.Empty<DiscoveredDatLeaf>(),
        Diagnostics = new[] { diagnostic },
    };
}
