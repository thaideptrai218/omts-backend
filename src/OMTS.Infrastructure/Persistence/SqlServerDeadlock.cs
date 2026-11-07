using Microsoft.Data.SqlClient;

namespace OMTS.Infrastructure.Persistence;

/// <summary>How SQL Server says that this request lost a deadlock, for the save (<see cref="UnitOfWork"/>) and for everything else (the API's exception handler).</summary>
public static class SqlServerDeadlock
{
    private const int VictimErrorNumber = 1205;

    /// <summary>EF wraps error 1205 in an InvalidOperationException ("transient failure") when no retry strategy is configured, so the whole chain is searched.</summary>
    public static bool IsVictim(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException { Number: VictimErrorNumber })
            {
                return true;
            }
        }

        return false;
    }
}
