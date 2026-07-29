namespace Arkadia.Data;

public sealed class MediaTypeRecord
{
    public string Id        { get; set; } = "";
    public string Name      { get; set; } = "";
    public int    SortOrder { get; set; }
    public bool   IsSeeded  { get; set; }
}
