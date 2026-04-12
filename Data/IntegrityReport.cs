using System.Collections.Generic;
using System.Linq;

namespace Arkadia.Data;

/// <summary>A single integrity violation found during catalog validation.</summary>
public sealed record IntegrityViolation(
    string DatLine,
    string FileName,
    string Detail);

/// <summary>Aggregated results from a full integrity validation run.</summary>
public sealed class IntegrityReport
{
    /// <summary>Check 1 — derived_artifact status=present but file absent from archive and no active volume mapping.</summary>
    public List<IntegrityViolation> Check1_Availability { get; } = [];
    /// <summary>Check 2 — derived_artifact status=present but mapped only to a LOST volume.</summary>
    public List<IntegrityViolation> Check2_LostVolume   { get; } = [];
    /// <summary>Check 3 — release status=present but at least one linked artifact is missing/lost.</summary>
    public List<IntegrityViolation> Check3_Release      { get; } = [];
    /// <summary>Check 4 — volume_artifacts row referencing a non-existent volume or derived_artifact.</summary>
    public List<IntegrityViolation> Check4_Orphan       { get; } = [];
    /// <summary>Check 5 — artifact physically present in both Local Archive and an active volume (redundant).</summary>
    public List<IntegrityViolation> Check5_Duplicate    { get; } = [];

    public int Check1Count => Check1_Availability.Count;
    public int Check2Count => Check2_LostVolume.Count;
    public int Check3Count => Check3_Release.Count;
    public int Check4Count => Check4_Orphan.Count;
    public int Check5Count => Check5_Duplicate.Count;

    public int TotalViolations =>
        Check1Count + Check2Count + Check3Count + Check4Count + Check5Count;

    /// <summary>True only when all structural checks (1-4) pass. Check 5 is informational.</summary>
    public bool IsHealthy => Check1Count + Check2Count + Check3Count + Check4Count == 0;
}
