using System.IO;

namespace Arkadia.Data;

public sealed record ScreenScraperCachePackageRecord(
    int    Id,
    string PackagePath,
    string SystemName,
    string SystemId,
    int    GameCount,
    int    MediaCount,
    string BuiltAt,
    string IndexedAt,
    string Status)
{
    public string FileName       => Path.GetFileName(PackagePath);
    public string BuiltAtShort   => BuiltAt.Length   >= 10 ? BuiltAt[..10]   : BuiltAt;
    public string IndexedAtShort => IndexedAt.Length >= 10 ? IndexedAt[..10] : IndexedAt;
    public bool   IsAvailable    => Status == "Available";
    public bool   IsMissing      => Status != "Available";
}
