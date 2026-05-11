namespace Arkadia;

public sealed record ArkPackageVerificationIssue(
    ArkPackageVerificationSeverity Severity,
    string                         Area,
    string                         Message);
