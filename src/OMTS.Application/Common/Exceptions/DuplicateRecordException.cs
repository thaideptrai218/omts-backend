namespace OMTS.Application.Common.Exceptions;

/// <summary>
/// A unique rule was broken by a concurrent save that passed the pre-check (e.g. two requests creating the
/// same username at once). Thrown by <see cref="Interfaces.Persistence.IUnitOfWork.SaveChangesAsync"/>;
/// the API answers 409 MSG-GEN-02 instead of a 500.
/// </summary>
public sealed class DuplicateRecordException(Exception innerException)
    : Exception("A unique value is already used by another record.", innerException);
