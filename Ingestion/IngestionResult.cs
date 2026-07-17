using System.Collections.Generic;

namespace Arkadia.Ingestion;

public enum IngestionStatus { Success, PartialSuccess, Failed }

public sealed class IngestionResult
{
    public int FilesScanned             { get; set; }
    public int FilesMatched             { get; set; }
    /// <summary>
    /// Count of files copied/moved into <c>staging</c> for wanted processing.
    /// Surfaced to the user as "Files staged" — it is NOT archived files or
    /// derived artifacts. Internal name retained to avoid model churn.
    /// </summary>
    public int FilesCopied              { get; set; }
    /// <summary>Complete releases moved from staging into the source/workdir transform input (Phase 7).</summary>
    public int ReleaseInputsAssembled   { get; set; }
    /// <summary>Derived artifacts (e.g. CHD) committed to the DB this run.</summary>
    public int DerivedArtifactsCreated  { get; set; }
    /// <summary>Releases whose derived artifact already existed and was verified/satisfied (no re-transform).</summary>
    public int AlreadyPresent           { get; set; }
    public int ReleasesPresent          { get; set; }
    public int FilesSkipped             { get; set; }
    public int UnwantedSkipped          { get; set; }
    public int TransformsFailed         { get; set; }
    public int ReleasesIncomplete       { get; set; }
    public int FilesDeletedFromIncoming { get; set; }
    /// <summary>Stale staging files relocated to incoming-skip because their release is now unwanted.</summary>
    public int StaleStagingMoved        { get; set; }
    /// <summary>Stale source files relocated to incoming-skip because their release is now unwanted.</summary>
    public int StaleSourceMoved         { get; set; }
    public List<ExtractedArchiveInfo> ExtractedArchiveInfos { get; } = new();
    public List<IngestionOperation> Operations        { get; } = new();
    /// <summary>Non-null only for hard failures that aborted the pipeline.</summary>
    public string?                  Error             { get; set; }

    public IngestionStatus Status =>
        Error is not null                                    ? IngestionStatus.Failed         :
        TransformsFailed == 0 && ReleasesIncomplete == 0     ? IngestionStatus.Success        :
        ReleasesPresent  >  0                                ? IngestionStatus.PartialSuccess :
                                                               IngestionStatus.Failed;

    public bool Success => Status == IngestionStatus.Success;

    public string StatusText => Status switch
    {
        IngestionStatus.Success        => "SUCCESS",
        IngestionStatus.PartialSuccess => "PARTIAL SUCCESS",
        _                              => "FAILED",
    };
}
