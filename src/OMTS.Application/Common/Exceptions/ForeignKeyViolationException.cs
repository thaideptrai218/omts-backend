namespace OMTS.Application.Common.Exceptions;

/// <summary>
/// An INSERT or UPDATE of the save points to a row that is not there (a foreign key), e.g. a row removed by a parallel save after it was read. Thrown by
/// <see cref="Interfaces.Persistence.IUnitOfWork.SaveChangesAsync"/>. It is not a concurrency conflict and the API does not map it (500, as any database error): only a use
/// case that knows such a row can be removed meanwhile catches it and reads again (SALE-05 for a harvest lot or a product).
/// </summary>
public sealed class ForeignKeyViolationException(Exception innerException)
    : Exception("A row this save points to does not exist.", innerException);
