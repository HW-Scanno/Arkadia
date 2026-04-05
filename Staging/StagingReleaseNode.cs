using System.Collections.Generic;
using Arkadia.Data;

namespace Arkadia.Staging;

/// <summary>One release folder found under staging/&lt;platform&gt;/&lt;datLine&gt;/&lt;release&gt;/.</summary>
public sealed class StagingReleaseNode
{
    public required string                   ReleaseName  { get; init; }
    public required string                   ReleasePath  { get; init; }  // absolute path to staging folder
    public required string                   ReleaseId    { get; init; }
    public required string                   PlatformId   { get; init; }
    public required string                   DatLineId    { get; init; }
    public required List<ReleaseFileRecord>  ExpectedFiles { get; init; }
    public required int                      FilesPresent { get; init; }  // count of expected files found on disk
    public          int                      FilesTotal   => ExpectedFiles.Count;

    /// <summary>Completion ratio label, e.g. "3/5".</summary>
    public string ProgressLabel => $"{FilesPresent}/{FilesTotal}";

    /// <summary>0.0–1.0 completion ratio used for the progress bar.</summary>
    public double ProgressRatio => FilesTotal > 0 ? (double)FilesPresent / FilesTotal : 0;
}
