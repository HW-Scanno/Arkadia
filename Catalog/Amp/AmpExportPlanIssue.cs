namespace Arkadia;

public sealed record AmpExportPlanIssue(
    AmpExportPlanSeverity Severity,
    string                Area,
    string                Message);
