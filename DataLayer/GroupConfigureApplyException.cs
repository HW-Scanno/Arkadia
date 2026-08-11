using System;

namespace Arkadia.Data;

/// <summary>Distinguishable failures of <see cref="CatalogService.ApplyDatGroupConfiguration"/>.</summary>
public enum GroupConfigureApplyError { GroupNotFound, EmptyGroup, MembershipDrift, InvalidConfig }

/// <summary>
/// Thrown by the atomic Group-configuration apply. The single transaction is always rolled back before this
/// propagates, so no leaf is left half-configured. Distinct from an unexpected <c>SqliteException</c>.
/// </summary>
public sealed class GroupConfigureApplyException : Exception
{
    public GroupConfigureApplyError Error { get; }
    public GroupConfigureApplyException(GroupConfigureApplyError error, string message) : base(message) => Error = error;
}
