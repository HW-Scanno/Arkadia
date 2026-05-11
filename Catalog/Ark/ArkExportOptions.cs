namespace Arkadia;

public sealed record ArkExportOptions(
    bool IncludeMedia       = false,
    bool IncludeSettings    = true,
    bool IncludeAmpRegistry = true);
