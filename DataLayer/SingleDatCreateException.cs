using System;

namespace Arkadia.Data;

/// <summary>Why <see cref="CatalogService.CreateSingleDatLine"/> rejected a record.</summary>
public enum SingleDatCreateFailure
{
    InvalidRecord,
    IdCollision,
    StorePathCollision,
    UnknownHardwareFamily,
    UnknownMediaType,
}

/// <summary>A deterministic (non-database-fault) rejection from <see cref="CatalogService.CreateSingleDatLine"/>.</summary>
public sealed class SingleDatCreateException : Exception
{
    public SingleDatCreateFailure Failure { get; }

    public SingleDatCreateException(SingleDatCreateFailure failure, string message) : base(message)
        => Failure = failure;
}
