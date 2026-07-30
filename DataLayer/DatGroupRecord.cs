using System;
using Arkadia.Data.Identifiers;

namespace Arkadia.Data;

/// <summary>
/// A Group DAT (<c>dat_groups</c> in catalog.db): an additive super-unit grouping many leaf
/// <c>dat_lines</c>. See <c>docs/SPECS/ARKADIA_GROUP_DAT_V1_SPEC.md</c>.
///
/// <para><see cref="Id"/> is a <see cref="DatGroupId"/> (immutable, lowercase, collision-safe),
/// not a raw string. <see cref="CurrentRevision"/> bootstraps at 0 and is advanced only by a
/// future finalizer — never by generic CRUD. A leaf belongs to at most one group; Single DAT
/// leaves have <c>group_id = NULL</c>.</para>
/// </summary>
public sealed class DatGroupRecord
{
    public DatGroupId Id                 { get; init; }
    public string     DisplayName        { get; init; } = "";
    public string     HardwareFamilyId   { get; init; } = "";
    public string     Authority          { get; init; } = "";
    public int        CurrentRevision    { get; init; }
    public DateTime   CreatedAtUtc       { get; init; }
    public DateTime   UpdatedAtUtc       { get; init; }
}
