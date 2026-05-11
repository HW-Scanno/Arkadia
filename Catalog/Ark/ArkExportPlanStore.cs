namespace Arkadia;

public sealed record ArkExportPlanStore(
    string ArchivePath,
    string SourcePath,
    long   SizeBytes,
    string Category,
    bool   Included);
