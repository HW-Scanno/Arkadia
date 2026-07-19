using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Arkadia.Ingestion;

namespace Arkadia.Archive;

/// <summary>
/// Top-level, pure/DB-free entry point that resolves the DAT-line output form,
/// builds the plan, analyzes collisions over both the FULL set and the current
/// WANTED subset, and computes the validation state + fingerprints.
///
/// Validation belongs to DAT import/configuration, DAT update/reimport, transform
/// strategy change, and legacy-line validation — NOT normal ingestion, which must
/// only consult the (structural) staleness via <see cref="ComputeState"/>.
///
/// Key correction (M1a→): a DAT line whose FULL release set is collision-free is
/// <see cref="ArchiveOutputValidationState.ValidFullSet"/> and stays valid under
/// normal Exclude/Restore curation, because the STRUCTURAL fingerprint is
/// wanted-agnostic. Only a structural change (strategy/ext/DAT identity/names/files)
/// makes it stale.
/// </summary>
public static class ArchiveOutputValidator
{
    public static ArchiveOutputValidationResult Validate(
        ArchiveOutputConfig config,
        IReadOnlyList<ArchiveReleaseInput> releases)
    {
        // Form is a stable per-line property → resolved over the full set (status-agnostic).
        var form = ArchiveDatLineOutputFormResolver.ResolveStructural(config, releases);

        var allCandidates = ArchiveOutputPlanBuilder.Build(config, form, releases);
        var wantedCandidates = allCandidates
            .Where(c => !ArchiveDatLineOutputFormResolver.IsUnwanted(c.Status))
            .ToList();

        var fullSetCollisions      = ArchiveOutputCollisionAnalyzer.AnalyzeFullSet(allCandidates);
        var wantedSubsetCollisions = ArchiveOutputCollisionAnalyzer.Analyze(allCandidates);

        var state = fullSetCollisions.Count == 0
            ? ArchiveOutputValidationState.ValidFullSet
            : wantedSubsetCollisions.Count == 0
                ? ArchiveOutputValidationState.ValidWithExclusions
                : ArchiveOutputValidationState.CollisionUnresolved;

        var structuralFp = ArchiveOutputFingerprint.ComputeStructural(config, form, releases);
        var exclusionFp  = ArchiveOutputFingerprint.ComputeExclusion(releases);

        return new ArchiveOutputValidationResult(
            form, state, wantedCandidates, fullSetCollisions, wantedSubsetCollisions,
            structuralFp, exclusionFp);
    }

    /// <summary>
    /// Non-interactive state a future ingestion run consults. Only the STRUCTURAL
    /// fingerprint gates staleness — normal Exclude/Restore never makes a ValidFullSet
    /// line stale. If the structural fingerprint matches the stored one, the freshly
    /// computed <paramref name="current"/> state is authoritative (so a restored
    /// exclusion that reintroduces a collision surfaces as CollisionUnresolved, not Stale).
    /// </summary>
    public static ArchiveOutputValidationState ComputeState(
        ArchiveOutputValidationResult current,
        string? storedStructuralFingerprint)
    {
        if (string.IsNullOrEmpty(storedStructuralFingerprint))
            return ArchiveOutputValidationState.Unknown;
        if (!string.Equals(storedStructuralFingerprint, current.StructuralFingerprint, StringComparison.Ordinal))
            return ArchiveOutputValidationState.Stale;
        return current.State;
    }
}

/// <summary>
/// Two independent fingerprints:
///   • <see cref="ComputeStructural"/> — wanted-agnostic; changes only when the
///     theoretical output plan can change (strategy, output kind/ext, resolved form,
///     release ids/names/normalized safe names, output-relevant file names).
///   • <see cref="ComputeExclusion"/> — the exclusion decision (sorted unwanted
///     release ids); only relevant when a line is ValidWithExclusions.
/// </summary>
public static class ArchiveOutputFingerprint
{
    /// <summary>Wanted-agnostic structural fingerprint. Does NOT include wanted/unwanted status.</summary>
    public static string ComputeStructural(
        ArchiveOutputConfig config,
        ArchiveDatLineOutputForm form,
        IReadOnlyList<ArchiveReleaseInput> releases)
    {
        var sb = new StringBuilder();
        sb.Append("form=").Append(form).Append('|');
        sb.Append("strategy=").Append(config.StrategyType).Append('|');
        sb.Append("singleExt=").Append(config.SingleFileOutputExtension).Append('|');
        sb.Append("folderOutputsFolder=").Append(config.FolderOutputsFolder ? '1' : '0').Append('|');

        foreach (var kv in config.ExtensionRules.OrderBy(k => k.Key, StringComparer.Ordinal))
            sb.Append("rule:").Append(kv.Key).Append('=')
              .Append(kv.Value.IsDiscard ? '1' : '0').Append(',')
              .Append(kv.Value.OutputExtension).Append('|');

        foreach (var r in releases.OrderBy(x => x.ReleaseId, StringComparer.Ordinal))
        {
            // No status here — curation must not change the structural fingerprint.
            sb.Append("rel:").Append(r.ReleaseId).Append('~')
              .Append(r.ReleaseName).Append('~')
              .Append(IngestionPaths.SafeFolderName(r.ReleaseName)).Append('~');
            foreach (var f in r.Files.OrderBy(f => f.RomName, StringComparer.Ordinal))
                sb.Append(f.RomName).Append(',');
            sb.Append('|');
        }

        return Sha1Hex(sb.ToString());
    }

    /// <summary>Fingerprint of the exclusion decision — the sorted set of unwanted release ids.</summary>
    public static string ComputeExclusion(IReadOnlyList<ArchiveReleaseInput> releases)
    {
        var excluded = releases
            .Where(r => ArchiveDatLineOutputFormResolver.IsUnwanted(r.Status))
            .Select(r => r.ReleaseId)
            .OrderBy(id => id, StringComparer.Ordinal);

        return Sha1Hex("excluded=" + string.Join(",", excluded));
    }

    private static string Sha1Hex(string s)
    {
        using var sha = SHA1.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
    }
}
