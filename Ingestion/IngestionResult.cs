using System.Collections.Generic;

namespace Arkadia.Ingestion;

public enum IngestionStatus { Success, PartialSuccess, Failed }

public sealed class IngestionResult
{
    public int FilesScanned             { get; set; }
    public int FilesMatched             { get; set; }
    public int FilesCopied              { get; set; }
    public int ReleasesPresent          { get; set; }
    public int FilesSkipped             { get; set; }
    public int TransformsFailed         { get; set; }
    public int ReleasesIncomplete       { get; set; }
    public int FilesDeletedFromIncoming { get; set; }
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
