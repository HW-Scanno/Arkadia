namespace Arkadia;

public sealed record AmpPackageVerificationIssue(
    AmpPackageVerificationSeverity Severity,
    string                         Area,
    string                         Message);
