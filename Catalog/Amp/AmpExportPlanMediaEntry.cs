namespace Arkadia;

public sealed record AmpExportPlanMediaEntry(
    string  MediaType,
    string  FilePath,
    string  Sha256,
    long    SizeBytes,
    bool    IsPreferred,
    string? Credits);
