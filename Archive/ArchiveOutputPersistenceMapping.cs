using System;

namespace Arkadia.Archive;

/// <summary>Maps archive-output enums to/from their persisted <c>dat_lines</c> string values.</summary>
public static class ArchiveOutputPersistenceMapping
{
    public static string FormToDb(ArchiveDatLineOutputForm form) => form switch
    {
        ArchiveDatLineOutputForm.SingleFileFlat         => "single_file_flat",
        ArchiveDatLineOutputForm.MultiFileReleaseFolder => "multi_file_release_folder",
        _                                               => "unknown",
    };

    public static string StateToDb(ArchiveOutputValidationState state) => state switch
    {
        ArchiveOutputValidationState.ValidFullSet        => "valid_full_set",
        ArchiveOutputValidationState.ValidWithExclusions => "valid_with_exclusions",
        ArchiveOutputValidationState.CollisionUnresolved => "collision_unresolved",
        ArchiveOutputValidationState.Stale               => "stale",
        _                                                => "unknown",
    };

    public static ArchiveDatLineOutputForm FormFromDb(string? db) => db switch
    {
        "single_file_flat"          => ArchiveDatLineOutputForm.SingleFileFlat,
        "multi_file_release_folder" => ArchiveDatLineOutputForm.MultiFileReleaseFolder,
        _                           => ArchiveDatLineOutputForm.Unknown,
    };

    public static ArchiveOutputValidationState StateFromDb(string? db) => db switch
    {
        "valid_full_set"       => ArchiveOutputValidationState.ValidFullSet,
        "valid_with_exclusions" => ArchiveOutputValidationState.ValidWithExclusions,
        "collision_unresolved" => ArchiveOutputValidationState.CollisionUnresolved,
        "stale"                => ArchiveOutputValidationState.Stale,
        _                      => ArchiveOutputValidationState.Unknown,
    };
}
