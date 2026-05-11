using System;
using System.IO;

namespace Arkadia;

public static class ArkUiHelpers
{
    public static string SuggestedArkFileName() =>
        $"arkadia-backup-{DateTime.Now:yyyyMMdd-HHmmss}.ark";

    public static string BackupsFolder(string baseDir) =>
        Path.Combine(baseDir, ArkadiaFolders.Backups);
}
