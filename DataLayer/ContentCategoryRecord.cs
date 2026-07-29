namespace Arkadia.Data;

public sealed class ContentCategoryRecord
{
    public string Id        { get; set; } = "";
    public string Name      { get; set; } = "";
    public int    SortOrder { get; set; }
    public bool   IsSeeded  { get; set; }
}
