namespace Arkadia.Data;

public sealed class TransformRecord
{
    public string Id              { get; set; } = "";
    public string Name            { get; set; } = "";
    /// <summary>FK to tools.tool_id. Empty for no_compression (no external tool needed).</summary>
    public string ToolId          { get; set; } = "";
    /// <summary>Command template; empty string is the special no_compression case.</summary>
    public string CommandTemplate { get; set; } = "";
    /// <summary>Output file extension including the dot, e.g. ".chd". Empty = same as input.</summary>
    public string OutputExtension { get; set; } = "";
    public bool   IsEnabled       { get; set; } = true;

    /// <summary>
    /// Authoritative processor kind: "file_oriented" or "folder_oriented".
    /// Determines which processor handles the release during ingestion.
    /// </summary>
    public string ProcessorType { get; set; } = "file_oriented";

    /// <summary>
    /// What the transform produces: "file" (single output file) or "folder" (output directory tree).
    /// </summary>
    public string OutputKind { get; set; } = "file";

    /// <summary>
    /// Archive quality tier: "A" (source-equivalent), "B" (lossless transform), "C" (lossy transform).
    /// </summary>
    public string ArchiveTier { get; set; } = "A";

    /// <summary>
    /// Legacy field kept for backward compatibility with existing ingestion guards.
    /// Authoritative value is ProcessorType; this is derived from it on save.
    /// </summary>
    public string TransformType { get; set; } = "file_strategy";

    // Computed helpers
    public bool IsFileOriented   => ProcessorType == "file_oriented";
    public bool IsFolderOriented => ProcessorType == "folder_oriented";
    public bool OutputIsFile     => OutputKind == "file";
    public bool OutputIsFolder   => OutputKind == "folder";

    // Legacy helpers kept so existing callsites compile without change
    public bool IsFileStrategy   => ProcessorType != "folder_oriented";
    public bool IsFolderStrategy => ProcessorType == "folder_oriented";
}
