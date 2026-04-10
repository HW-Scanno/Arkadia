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
    /// <summary>"file_strategy" = applied per source file. "folder_strategy" = applied to entire release folder.</summary>
    public string TransformType   { get; set; } = "file_strategy";

    // Computed helpers.
    public bool IsFileStrategy   => TransformType != "folder_strategy";
    public bool IsFolderStrategy => TransformType == "folder_strategy";
}
