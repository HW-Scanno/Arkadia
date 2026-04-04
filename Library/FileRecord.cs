namespace Arkadia.Library;

/// <summary>A single file shown in the Library detail panel — filename + hash.</summary>
public sealed record FileRecord(string FileName, string Hash);
