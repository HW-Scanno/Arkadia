using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Arkadia.Archive;
using Arkadia.Data;

namespace Arkadia.GroupDats;

/// <summary>Per-leaf archive-output validation outcome for a proposed Group configuration (read-only).</summary>
public enum GroupConfigureLeafValidationState { Clean, Collision, Error }

/// <summary>One leaf's validation result under the proposed uniform config.</summary>
public sealed record GroupConfigureLeafValidation(string LeafId, GroupConfigureLeafValidationState State, string? Detail = null);

/// <summary>
/// The current per-setting values across a Group's leaves: a common value, or null meaning "Mixed". This
/// is a read-only representation — the Group persists no configuration of its own; the values live on the
/// individual <c>dat_lines</c>.
/// </summary>
public sealed record GroupConfigurePreview(
    int     LeafCount,
    string? CommonFileHandling,        // null ⇒ Mixed
    string? CommonTransformStrategy,   // null ⇒ Mixed
    string? CommonFolderTransformId)   // null ⇒ Mixed ("" is a real common value = no folder transform)
{
    public bool FileHandlingMixed      => CommonFileHandling is null;
    public bool TransformStrategyMixed => CommonTransformStrategy is null;
    public bool FolderTransformMixed   => CommonFolderTransformId is null;
}

/// <summary>
/// A frozen, immutable apply plan produced only after the whole Group validated clean. It captures the
/// exact expected membership at validation time so the atomic apply can reject membership drift, plus the
/// single uniform configuration to write to every leaf. No group-config persistence, no per-leaf overrides.
/// </summary>
public sealed record GroupConfigurePlan(
    string                   GroupId,
    ImmutableArray<string>   ExpectedLeafIds,
    string                   FileHandling,           // "archives_pre_extraction" | "all_files"
    string                   TransformStrategyType,  // "none" | "release_folder"
    string?                  FolderTransformId);     // required for release_folder; null otherwise

/// <summary>Pure helpers for the Group Configure preview and per-leaf validation classification (no I/O).</summary>
public static class GroupConfigure
{
    /// <summary>Computes the common-or-Mixed value of each setting across the group's leaves.</summary>
    public static GroupConfigurePreview BuildPreview(IReadOnlyList<DatLineRecord> leaves)
    {
        static string? Common<T>(IReadOnlyList<DatLineRecord> src, Func<DatLineRecord, T> sel, Func<T, string> fmt)
        {
            if (src.Count == 0) return null;
            var distinct = src.Select(sel).Distinct().ToList();
            return distinct.Count == 1 ? fmt(distinct[0]) : null;
        }

        return new GroupConfigurePreview(
            LeafCount:               leaves.Count,
            CommonFileHandling:      Common(leaves, l => l.FileHandling ?? "", s => s),
            CommonTransformStrategy: Common(leaves, l => l.TransformStrategyType ?? "", s => s),
            CommonFolderTransformId: Common(leaves, l => l.FolderTransformId ?? "", s => s));
    }

    /// <summary>Maps a leaf's archive-output collision count to a validation state (0 ⇒ Clean).</summary>
    public static GroupConfigureLeafValidationState ClassifyLeaf(int wantedSubsetCollisionCount)
        => wantedSubsetCollisionCount > 0 ? GroupConfigureLeafValidationState.Collision : GroupConfigureLeafValidationState.Clean;

    /// <summary>True when every leaf validated Clean (apply is allowed only then).</summary>
    public static bool AllClean(IReadOnlyList<GroupConfigureLeafValidation> results)
        => results.Count > 0 && results.All(r => r.State == GroupConfigureLeafValidationState.Clean);

    /// <summary>
    /// Structural check of a proposed configuration BEFORE validation: release_folder requires a folder
    /// transform; none must not carry one. Returns null when valid, else a human-readable reason.
    /// </summary>
    public static string? ValidateConfigShape(string transformStrategyType, string? folderTransformId)
        => transformStrategyType switch
        {
            "release_folder" when string.IsNullOrEmpty(folderTransformId) => "Per release folder requires a folder transform.",
            "none" when !string.IsNullOrEmpty(folderTransformId)          => "The None strategy must not have a folder transform.",
            "none" or "release_folder"                                    => null,
            _                                                             => $"Unsupported transform strategy for Group Configure: '{transformStrategyType}'.",
        };
}

/// <summary>
/// Read-only per-leaf archive-output validation for a proposed uniform Group config. "Clean" means the leaf
/// was successfully opened AND validated with no collision — never "nothing was available to validate": a
/// missing/unreadable leaf database or any load/validation failure is <see cref="GroupConfigureLeafValidationState.Error"/>,
/// which blocks the whole Group Apply. Never mutates: it reads releases/files and runs the pure validator;
/// it never marks releases unwanted, never writes the leaf DB/catalog/filesystem.
/// </summary>
public static class GroupConfigureLeafValidator
{
    public static GroupConfigureLeafValidation ValidateLeaf(
        DatLineRecord                  leaf,
        string                         dataDir,
        string                         strategy,
        TransformRecord?               folderTransform,
        IReadOnlyList<TransformRecord> allTransforms)
    {
        try
        {
            var abs = leaf.DataStorePath.Length > 0 ? Path.Combine(dataDir, leaf.DataStorePath) : "";
            if (abs.Length == 0 || !File.Exists(abs))
                return new GroupConfigureLeafValidation(leaf.Id, GroupConfigureLeafValidationState.Error, "leaf database is missing");

            var emptyExt = new Dictionary<string, ExtensionTransformMapping>(StringComparer.OrdinalIgnoreCase);
            var config   = ArchiveOutputConfigFactory.BuildConfig(
                leaf.HardwareFamilyId, leaf.Id, strategy, folderTransform, emptyExt, allTransforms);
            var store    = new DatLineStore(abs);
            var inputs   = ArchiveOutputConfigFactory.BuildReleaseInputs(store.LoadReleases(), store.LoadAllReleaseFiles());
            var result   = ArchiveOutputValidator.Validate(config, inputs);
            return new GroupConfigureLeafValidation(leaf.Id, GroupConfigure.ClassifyLeaf(result.WantedSubsetCollisions.Count));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Unreadable DB / open / load / validate failure → not validatable → Error (blocks apply).
            return new GroupConfigureLeafValidation(leaf.Id, GroupConfigureLeafValidationState.Error, ex.GetType().Name);
        }
    }
}
