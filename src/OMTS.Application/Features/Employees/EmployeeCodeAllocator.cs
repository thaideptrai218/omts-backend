using OMTS.Application.Common.Constants;
using OMTS.Application.Common.Interfaces.Persistence;

namespace OMTS.Application.Features.Employees;

/// <summary>
/// Gives the next employee code, NV000001, NV000002… A number whose code someone already typed (or that another row of the
/// same import is about to use) is spent and skipped, never reused, so a generated code never meets a typed one.
/// </summary>
public sealed class EmployeeCodeAllocator(IDocumentNumberAllocator numbers, IEmployeeRepository employees)
{
    /// <param name="alsoTaken">Codes that are not saved yet but must not be given, in upper case.</param>
    public async Task<string> NextAsync(ISet<string>? alsoTaken, CancellationToken cancellationToken)
    {
        while (true)
        {
            var code = EmployeeCodes.Generated(await numbers.NextAsync(DocumentSequenceKeys.Employee, cancellationToken));
            if (alsoTaken?.Contains(code) != true && await employees.FindByCodeAsync(code, null, cancellationToken) is null)
            {
                return code;
            }
        }
    }
}
