namespace Arkadia.Data;

/// <summary>
/// Per-extension transform assignment for a DAT line using the file_extension strategy.
/// Stored in catalog.db → dat_line_extension_transforms.
/// </summary>
public sealed class ExtensionTransformMapping
{
    public string DatLineId     { get; set; } = "";
    /// <summary>Lowercase extension including the dot, e.g. ".bin". "(no ext)" for files with no extension.</summary>
    public string FileExtension { get; set; } = "";
    /// <summary>Empty when IsDiscard = true.</summary>
    public string TransformId   { get; set; } = "";
    /// <summary>True means the file is ignored (no artifact created). Default.</summary>
    public bool   IsDiscard     { get; set; } = true;
}
