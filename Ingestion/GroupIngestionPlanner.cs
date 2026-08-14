using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Arkadia.Archive;
using Arkadia.Data;

namespace Arkadia.Ingestion;

/// <summary>Progress tick during Group ingestion planning.</summary>
public sealed record GroupIngestionPlanProgress(string PhaseText, int LeafIndex, int LeafTotal);

/// <summary>
/// Builds a fully-validated, frozen <see cref="GroupIngestionPlan"/> for a Group — READ-ONLY. It resolves
/// membership, determines scope with the exact Single satisfaction rules (<see cref="RedundantIncomingPolicy"/>
/// + staging/source checks + <see cref="ResumableStagingDetector"/>), validates every in-scope leaf's DB,
/// transform config, tool dependencies and archive-output gate, guards uniform File Handling, builds the
/// cross-leaf aggregate SHA1/MD5 indexes of required targets, and inspects incoming names/counts. It never
/// pre-extracts, hashes incoming, stages, promotes, transforms, or writes catalog/leaf DBs. A missing leaf
/// DB is never created (File.Exists gate before any store construction).
/// </summary>
public static class GroupIngestionPlanner
{
    // Same container set as Single pre-ingest (.zip/.7z/.rar). .arc/.ark stay ordinary payloads.
    private static readonly HashSet<string> ContainerExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".zip", ".7z", ".rar" };

    public static GroupIngestionPlan Plan(
        CatalogService                        catalog,
        string                                dataDir,
        string                                appRoot,
        string                                groupId,
        string                                displayName,
        string                                platformId,
        IReadOnlyList<GroupLeafRecord>        leaves,
        string?                               incomingDirOverride = null,
        IProgress<GroupIngestionPlanProgress>? progress            = null)
    {
        var allTransforms = catalog.LoadTransforms();
        var allTools      = catalog.LoadTools();
        var toolsById     = allTools.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);

        // Deterministic leaf order: DatLineId Ordinal.
        var orderedLeaves = leaves.OrderBy(l => l.DatLine.Id, StringComparer.Ordinal).ToList();
        var expectedLeafIds = orderedLeaves.Select(l => l.DatLine.Id).ToImmutableArray();

        var diagnostics   = new List<GroupLeafDiagnostic>();
        var preparedLeaves = new List<GroupPreparedLeaf>();
        var requiredTargets = new List<GroupRequiredTarget>();
        var resumableReleases = new List<GroupResumableRelease>();
        var inScopeFileHandlings = new HashSet<string>(StringComparer.Ordinal);

        int total = orderedLeaves.Count;
        for (int i = 0; i < total; i++)
        {
            var leaf      = orderedLeaves[i];
            var datLineId = leaf.DatLine.Id;
            progress?.Report(new GroupIngestionPlanProgress($"Preparing leaf {i + 1} / {total}", i + 1, total));

            var dsp = leaf.DatLine.DataStorePath;
            var abs = dsp.Length > 0 ? Path.Combine(dataDir, dsp) : "";

            // Read-only DB gate — NEVER construct a store for a missing path (that would create the file).
            if (abs.Length == 0 || !File.Exists(abs))
            {
                diagnostics.Add(new GroupLeafDiagnostic(datLineId, "Leaf database is missing."));
                preparedLeaves.Add(new GroupPreparedLeaf(datLineId, dsp, 0, 0));
                continue;
            }

            List<ReleaseRecord> allReleases;
            Dictionary<string, List<ReleaseFileRecord>> allReleaseFiles;
            DatLineStore store;
            try
            {
                // STRICTLY read-only: no EnsureSchema/DDL, no create/repair/migrate. Throws on a missing or
                // structurally-incomplete DB → treated as an Error diagnostic below, never repaired.
                store           = DatLineStore.OpenReadOnly(abs);
                allReleases     = store.LoadReleases();
                allReleaseFiles = store.LoadAllReleaseFiles();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                diagnostics.Add(new GroupLeafDiagnostic(datLineId, $"Leaf database unreadable ({ex.GetType().Name})."));
                preparedLeaves.Add(new GroupPreparedLeaf(datLineId, dsp, 0, 0));
                continue;
            }

            var releases = allReleases.Where(r => r.Status != "outdated").ToDictionary(r => r.Id, StringComparer.Ordinal);

            var stagingRoot = Path.Combine(appRoot, "staging", platformId, datLineId);
            var sourceRoot  = Path.Combine(appRoot, "source",  platformId, datLineId);

            // Resumable complete-staging releases (Single semantics; read-only).
            var resumeInputs = allReleases.Select(r => new ResumeReleaseInput(
                r.Id, r.Name, r.Status,
                allReleaseFiles.TryGetValue(r.Id, out var rf) ? rf.Select(f => f.RomName).ToList() : new List<string>())).ToList();
            var resumable = ResumableStagingDetector.Detect(stagingRoot, resumeInputs);

            // Required targets = wanted ROMs of non-satisfied releases that are not already secured
            // (DB-authoritative satisfaction; else per-ROM staging/source checks — verbatim Single rules).
            var leafRequired = new List<GroupRequiredTarget>();
            foreach (var release in releases.Values)
            {
                if (release.Status == "unwanted") continue;
                var daCount = store.GetDerivedArtifactsByReleaseId(release.Id).Count;
                if (RedundantIncomingPolicy.Locate(release.Status, daCount) == ReleaseSatisfaction.Satisfied)
                    continue;   // present + ≥1 artifact → satisfied without filesystem probe

                var safeFolder = IngestionPaths.SafeFolderName(release.Name);
                if (!allReleaseFiles.TryGetValue(release.Id, out var files)) continue;
                foreach (var f in files)
                {
                    if (RomSecured(stagingRoot, sourceRoot, safeFolder, f, release.Status)) continue;
                    long sz = long.TryParse(f.Size, out var s) && s > 0 ? s : 0;
                    leafRequired.Add(new GroupRequiredTarget(
                        new GroupMatchTarget(datLineId, release.Id, f.RomName), release.Name, sz, f.Sha1, f.Md5));
                }
            }

            bool inScope = leafRequired.Count > 0 || resumable.ResumableReleaseIds.Count > 0;

            // Validate readiness ONLY for in-scope leaves (a satisfied leaf will never be finalized).
            if (inScope)
            {
                inScopeFileHandlings.Add(leaf.DatLine.FileHandling ?? "archives_pre_extraction");

                var extMappings = leaf.DatLine.TransformStrategyType == "file_extension"
                    ? catalog.LoadExtensionMappings(datLineId)
                    : new List<ExtensionTransformMapping>();
                var persistedForm = ArchiveOutputPersistenceMapping.FormFromDb(
                    catalog.GetDatLineArchiveOutputValidation(datLineId)?.Form);

                var cfg = LeafTransformConfigResolver.Resolve(
                    leaf.DatLine.StorageStrategyId, leaf.DatLine, allTransforms, allTools, extMappings, persistedForm, releases, allReleaseFiles);

                // Transform dispatch validity (missing/wrong folder transform → config error).
                if (LeafTransformDispatchResolver.Decide(cfg.StrategyType, cfg.FolderXform) == LeafTransformDispatch.FolderConfigError)
                    diagnostics.Add(new GroupLeafDiagnostic(datLineId,
                        cfg.FolderXform is null
                            ? "Transform configuration invalid: no folder-oriented transform is configured."
                            : $"Transform configuration invalid: '{cfg.FolderXform.Name}' is not folder-oriented."));

                // Required tool dependencies (exactly the Single preflight: active transform + extension transforms).
                var missingTool = MissingRequiredTool(cfg, extMappings, allTransforms, toolsById, appRoot);
                if (missingTool is not null)
                    diagnostics.Add(new GroupLeafDiagnostic(datLineId, $"Required tool missing: {missingTool}."));

                // Archive-output gate — same non-interactive gate Single runs before mutation (read-only).
                var storedFp = catalog.GetDatLineArchiveOutputValidation(datLineId)?.StructuralFingerprint;
                if (!string.IsNullOrEmpty(storedFp))
                {
                    var gateConfig   = ArchiveOutputConfigFactory.BuildConfig(
                        platformId, datLineId, cfg.StrategyType, cfg.FolderXform, cfg.ExtMappings, allTransforms);
                    var gateReleases = ArchiveOutputConfigFactory.BuildReleaseInputs(allReleases, allReleaseFiles);
                    var gate = ArchiveIngestionGateEvaluator.Evaluate(gateConfig, gateReleases, storedFp, gateEnabled: true);
                    if (!gate.Allow)
                        diagnostics.Add(new GroupLeafDiagnostic(datLineId, $"Archive output blocked: {gate.Reason}."));
                }
            }

            requiredTargets.AddRange(leafRequired);
            foreach (var rid in resumable.ResumableReleaseIds)
                resumableReleases.Add(new GroupResumableRelease(datLineId, rid));
            preparedLeaves.Add(new GroupPreparedLeaf(datLineId, dsp, leafRequired.Count, resumable.ResumableReleaseIds.Count));
        }

        // Uniform File Handling guard across IN-SCOPE leaves.
        string? fileHandling = inScopeFileHandlings.Count == 1 ? inScopeFileHandlings.First() : null;
        if (inScopeFileHandlings.Count > 1)
            diagnostics.Add(new GroupLeafDiagnostic("",
                "Group leaves use different File Handling settings. Configure Group before ingestion."));

        // Deterministic ordering + exact-duplicate collapse of authoritative targets.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var orderedRequired = requiredTargets
            .OrderBy(t => t.Target.DatLineId, StringComparer.Ordinal)
            .ThenBy(t => t.Target.ReleaseId, StringComparer.Ordinal)
            .ThenBy(t => t.Target.RomName,   StringComparer.Ordinal)
            .Where(t => seen.Add($"{t.Target.DatLineId}|{t.Target.ReleaseId}|{t.Target.RomName}"))
            .ToImmutableArray();

        var sha1Index = BuildIndex(orderedRequired, t => t.Sha1);
        var md5Index  = BuildIndex(orderedRequired, t => t.Md5);

        progress?.Report(new GroupIngestionPlanProgress("Inspecting incoming…", total, total));
        var incoming = InspectIncoming(incomingDirOverride ?? Path.Combine(appRoot, "incoming-roms", platformId));

        var orderedDiagnostics = diagnostics
            .OrderBy(d => d.DatLineId, StringComparer.Ordinal)
            .ThenBy(d => d.Reason,     StringComparer.Ordinal)
            .ToImmutableArray();

        var readiness = orderedDiagnostics.Length > 0
            ? GroupIngestionReadiness.Blocked
            : (orderedRequired.Length == 0 && resumableReleases.Count == 0
                ? GroupIngestionReadiness.NothingToDo
                : GroupIngestionReadiness.Ready);

        return new GroupIngestionPlan
        {
            GroupId            = groupId,
            DisplayName        = displayName,
            PlatformId         = platformId,
            ExpectedLeafIds    = expectedLeafIds,
            FileHandling       = fileHandling,
            PreparedLeaves     = preparedLeaves.ToImmutableArray(),
            RequiredTargets    = orderedRequired,
            AggregateSha1Index = sha1Index,
            AggregateMd5Index  = md5Index,
            ResumableReleases  = resumableReleases
                .OrderBy(r => r.DatLineId, StringComparer.Ordinal).ThenBy(r => r.ReleaseId, StringComparer.Ordinal).ToImmutableArray(),
            IncomingInspection = incoming,
            Readiness          = readiness,
            Diagnostics        = orderedDiagnostics,
        };
    }

    private static bool RomSecured(string stagingRoot, string sourceRoot, string safeFolder, ReleaseFileRecord f, string releaseStatus)
    {
        if (File.Exists(Path.Combine(stagingRoot, safeFolder, f.RomName))) return true;

        var sourcePath = Path.Combine(sourceRoot, safeFolder, f.RomName);
        if (!File.Exists(sourcePath)) return false;
        if (releaseStatus != "present") return false;   // transform-failed retry → re-stage (not secured)

        if (long.TryParse(f.Size, out var sz) && sz > 0)
        {
            try { return new FileInfo(sourcePath).Length == sz; }
            catch { return false; }
        }
        return true;   // present + no DAT size → trust source existence (Single semantics)
    }

    private static string? MissingRequiredTool(
        LeafTransformConfig cfg, IReadOnlyList<ExtensionTransformMapping> extMappings,
        IReadOnlyList<TransformRecord> allTransforms, IReadOnlyDictionary<string, ToolRecord> toolsById, string appRoot)
    {
        var requiredToolIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (cfg.ActiveXform.ToolId.Length > 0) requiredToolIds.Add(cfg.ActiveXform.ToolId);
        if (cfg.StrategyType == "file_extension")
            foreach (var m in extMappings)
            {
                if (m.IsDiscard || string.IsNullOrEmpty(m.TransformId)) continue;
                var xf = allTransforms.FirstOrDefault(t => t.Id == m.TransformId);
                if (xf?.ToolId is { Length: > 0 } tid) requiredToolIds.Add(tid);
            }

        foreach (var id in requiredToolIds)
            if (toolsById.TryGetValue(id, out var tool) &&
                !File.Exists(Path.Combine(appRoot, "tools", tool.FolderName, tool.ExecutableName)))
                return tool.FolderName.Length > 0 ? tool.FolderName : id;
        return null;
    }

    private static IReadOnlyDictionary<string, List<GroupMatchTarget>> BuildIndex(
        ImmutableArray<GroupRequiredTarget> targets, Func<GroupRequiredTarget, string> hashSelector)
    {
        var index = new Dictionary<string, List<GroupMatchTarget>>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in targets)   // already deterministically ordered
        {
            var h = hashSelector(t);
            if (h.Length == 0) continue;
            if (!index.TryGetValue(h, out var list)) { list = new(); index[h] = list; }
            list.Add(t.Target);
        }
        return index;
    }

    private static GroupIncomingInspection InspectIncoming(string incomingDir)
    {
        if (!Directory.Exists(incomingDir)) return new GroupIncomingInspection(0, 0);   // never created here

        int loose = 0, containers = 0;
        foreach (var f in Directory.EnumerateFiles(incomingDir, "*", SearchOption.AllDirectories))
        {
            if (ContainerExtensions.Contains(Path.GetExtension(f))) containers++;
            else                                                    loose++;
        }
        return new GroupIncomingInspection(loose, containers);
    }
}
