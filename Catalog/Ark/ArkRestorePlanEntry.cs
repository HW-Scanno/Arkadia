namespace Arkadia;

public sealed record ArkRestorePlanEntry(
    string ArchivePath,
    string TargetPath,
    long   SizeBytes,
    string Category,
    bool   WillRestore);
