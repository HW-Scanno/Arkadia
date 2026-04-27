using System;

namespace Arkadia.Data;

public sealed class DatLineRecord
{
    public string   Id            { get; set; } = "";
    public string   PlatformId    { get; set; } = "";
    public string   Name          { get; set; } = "";
    public string   Authority          { get; set; } = "";
    public string   DatCategory        { get; set; } = "";
    public string   Version            { get; set; } = "";
    public string   StorageStrategyId  { get; set; } = "";
    public string   DataStorePath      { get; set; } = "";   // relative: data/systems/<pid>/<id>.db
    public int      ReleaseCount       { get; set; }
    public DateTime ImportedAtUtc      { get; set; } = DateTime.UtcNow;
    /// <summary>"none" | "file_extension" | "release_folder"</summary>
    public string   TransformStrategyType { get; set; } = "none";
    /// <summary>FK to transforms.transform_id. Set when TransformStrategyType is "release_folder".</summary>
    public string   FolderTransformId     { get; set; } = "";
    /// <summary>"archives_pre_extraction" | "all_files"</summary>
    public string   FileHandling          { get; set; } = "archives_pre_extraction";
}
