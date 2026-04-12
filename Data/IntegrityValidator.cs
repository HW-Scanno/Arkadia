using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Arkadia.Data;

/// <summary>
/// Read-only cross-database integrity validation.
/// Checks invariants across catalog.db and every dat-line SQLite store.
/// Never modifies any data.
/// </summary>
public static class IntegrityValidator
{
    private const int MaxPerCheck = 100;  // cap per check; excess is counted but not stored

    public static IntegrityReport Validate(
        CatalogService catalog,
        string dataDir,
        string appRoot)
    {
        var report = new IntegrityReport();

        // ── Load catalog-level data ────────────────────────────────────────────
        var volumes  = catalog.GetVolumes().ToDictionary(v => v.Id, System.StringComparer.Ordinal);
        var datLines = catalog.LoadDatLines()
            .Where(dl => dl.DataStorePath.Length > 0)
            .ToDictionary(dl => dl.Id, System.StringComparer.Ordinal);
        var allVA = catalog.GetAllVolumeArtifacts();

        // Build per-artifact indexes
        // An artifact may be mapped to multiple volumes; we track:
        //   activeDaIds — mapped to at least one non-lost volume
        //   lostDaIds   — mapped to at least one lost volume (and NOT to an active volume)
        var activeDaIds    = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
        var lostDaIds      = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
        var daIdToVolLabel = new Dictionary<string, string>(System.StringComparer.Ordinal);
        var vaByDatLine    = new Dictionary<string, List<VolumeArtifactRecord>>(System.StringComparer.Ordinal);

        foreach (var va in allVA)
        {
            if (!vaByDatLine.TryGetValue(va.DatLineId, out var vaList))
                vaByDatLine[va.DatLineId] = vaList = [];
            vaList.Add(va);

            if (!volumes.TryGetValue(va.VolumeId, out var vol)) continue;
            daIdToVolLabel.TryAdd(va.DerivedArtifactId, vol.Label);
            if (vol.Status == "lost")
                lostDaIds.Add(va.DerivedArtifactId);
            else
                activeDaIds.Add(va.DerivedArtifactId);
        }

        // An artifact on an active volume is not "lost" even if it's also on a lost volume
        lostDaIds.ExceptWith(activeDaIds);

        // ── Check 4a: volume_artifacts referencing a non-existent volume_id ───
        foreach (var (volId, daId) in catalog.GetOrphanVolumeArtifactsByVolumeId())
        {
            if (report.Check4_Orphan.Count >= MaxPerCheck) break;
            report.Check4_Orphan.Add(new IntegrityViolation(
                "(catalog)",
                daId,
                $"volume_id={volId} does not exist in volumes table"));
        }

        // ── Per-dat-line checks ────────────────────────────────────────────────
        var pathSep = Path.DirectorySeparatorChar;

        foreach (var (dlId, dl) in datLines)
        {
            var dbPath = Path.Combine(dataDir, dl.DataStorePath);
            if (!File.Exists(dbPath)) continue;

            var store      = new DatLineStore(dbPath);
            var derived    = store.GetDerivedArtifacts();
            var derivedById = derived.ToDictionary(d => d.Id, System.StringComparer.Ordinal);

            foreach (var da in derived)
            {
                var archivePath  = Path.Combine(appRoot, da.RelativePath.Replace('/', pathSep));
                var inArchive    = File.Exists(archivePath);
                var inActive     = activeDaIds.Contains(da.Id);
                var inLost       = lostDaIds.Contains(da.Id);

                // ── Check 1: present artifact is unreachable ───────────────
                if (da.Status == "present" && !inArchive && !inActive)
                {
                    if (report.Check1_Availability.Count < MaxPerCheck)
                        report.Check1_Availability.Add(new IntegrityViolation(
                            dl.Name, da.FileName,
                            "status=present but absent from archive and not on any active volume"));
                }

                // ── Check 2: present artifact mapped only to lost volume ───
                if (da.Status == "present" && inLost)
                {
                    var volLabel = daIdToVolLabel.TryGetValue(da.Id, out var lbl) ? lbl : "?";
                    if (report.Check2_LostVolume.Count < MaxPerCheck)
                        report.Check2_LostVolume.Add(new IntegrityViolation(
                            dl.Name, da.FileName,
                            $"status=present but only mapped to LOST volume \"{volLabel}\""));
                }

                // ── Check 5: present artifact duplicated in archive + volume ─
                if (da.Status == "present" && inArchive && inActive)
                {
                    var volLabel = daIdToVolLabel.TryGetValue(da.Id, out var lbl) ? lbl : "?";
                    if (report.Check5_Duplicate.Count < MaxPerCheck)
                        report.Check5_Duplicate.Add(new IntegrityViolation(
                            dl.Name, da.FileName,
                            $"file in both Local Archive and active volume \"{volLabel}\" (redundant copy)"));
                }
            }

            // ── Check 3: present releases with non-present artifacts ───────
            foreach (var issue in store.GetPresentReleasesWithMissingArtifacts())
            {
                if (report.Check3_Release.Count >= MaxPerCheck) break;
                report.Check3_Release.Add(new IntegrityViolation(
                    dl.Name,
                    issue.ReleaseName,
                    $"release status=present but artifact \"{issue.ArtifactFileName}\" is {issue.ArtifactStatus}"));
            }

            // ── Check 4b: volume_artifacts with non-existent derived_artifact_id ─
            if (vaByDatLine.TryGetValue(dlId, out var vaForLine))
            {
                foreach (var va in vaForLine)
                {
                    if (report.Check4_Orphan.Count >= MaxPerCheck) break;
                    if (!derivedById.ContainsKey(va.DerivedArtifactId))
                        report.Check4_Orphan.Add(new IntegrityViolation(
                            dl.Name,
                            va.DerivedArtifactId,
                            "derived_artifact_id not found in dat-line DB"));
                }
            }
        }

        // ── Check 4c: volume_artifacts whose dat_line_id is not in catalog ─────
        foreach (var (dlId, vaList) in vaByDatLine)
        {
            if (datLines.ContainsKey(dlId)) continue;
            foreach (var va in vaList)
            {
                if (report.Check4_Orphan.Count >= MaxPerCheck) break;
                report.Check4_Orphan.Add(new IntegrityViolation(
                    "(catalog)",
                    va.DerivedArtifactId,
                    $"dat_line_id={dlId} not found in dat_lines table"));
            }
        }

        return report;
    }
}
