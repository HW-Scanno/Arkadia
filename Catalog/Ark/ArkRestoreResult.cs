using System.Collections.Generic;

namespace Arkadia;

public sealed record ArkRestoreResult(
    bool                  Success,
    string                ArkFilePath,
    string                TargetDataDir,
    string                StagingDir,
    string?               PreviousDataBackupDir,
    int                   RestoredEntryCount,
    long                  RestoredBytes,
    bool                  OverwriteUsed,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Issues);
