namespace OMTS.Application.Common.Exceptions;

/// <summary>
/// The record was changed by someone else since it was read (optimistic concurrency, NFR-09). Thrown by
/// <see cref="Interfaces.Persistence.IUnitOfWork.SaveChangesAsync"/>; handlers either retry or answer
/// MSG06, and the API maps an unhandled one to 409 MSG06.
/// </summary>
public sealed class ConcurrencyConflictException(Exception innerException)
    : Exception("The record was changed by another request.", innerException);
