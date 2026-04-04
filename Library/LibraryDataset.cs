using System.Collections.Generic;

namespace Arkadia.Library;

/// <summary>A single DAT-line dataset: one platform + one DAT source + its entries.</summary>
public sealed record LibraryDataset(
    string Platform,
    string DatLine,
    IReadOnlyList<LibraryEntry> Entries);
