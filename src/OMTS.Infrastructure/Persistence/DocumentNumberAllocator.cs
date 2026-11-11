using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Infrastructure.Persistence;

/// <summary>
/// The document number sequences (<see cref="DocumentSequence"/>), each number taken in a context of its own so that it is
/// committed at once, whatever happens to the caller's save. On SQL Server one atomic <c>UPDATE … OUTPUT</c> takes the next
/// value (parallel callers queue on the row lock and each get their own); a key without a row is created on its first use.
/// The in-memory test provider has no row lock, so its read-increment-save runs under a process lock.
/// </summary>
internal sealed class DocumentNumberAllocator(DbContextOptions<OmtsDbContext> options) : IDocumentNumberAllocator
{
    private const int MaxAttempts = 5;
    private static readonly SemaphoreSlim InMemoryLock = new(1, 1);

    public async Task<long> NextAsync(string key, CancellationToken cancellationToken)
    {
        await using var db = new OmtsDbContext(options);
        return db.Database.IsRelational() ? await NextRelationalAsync(db, key, cancellationToken) : await NextInMemoryAsync(db, key, cancellationToken);
    }

    public async Task<long> PeekAsync(string key, CancellationToken cancellationToken)
    {
        await using var db = new OmtsDbContext(options);
        var last = await db.DocumentSequences.AsNoTracking().Where(s => s.Key == key).Select(s => (long?)s.LastValue).FirstOrDefaultAsync(cancellationToken);
        return (last ?? 0) + 1;
    }

    private static async Task<long> NextRelationalAsync(OmtsDbContext db, string key, CancellationToken cancellationToken)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                await using var update = Command(db, "UPDATE [DocumentSequences] SET [LastValue] = [LastValue] + 1 OUTPUT INSERTED.[LastValue] WHERE [Key] = @key", key);
                if (await update.ExecuteScalarAsync(cancellationToken) is long next)
                {
                    return next;
                }

                try
                {
                    await using var insert = Command(db, "INSERT INTO [DocumentSequences] ([Key], [LastValue]) VALUES (@key, 1)", key);
                    await insert.ExecuteNonQueryAsync(cancellationToken);
                    return 1;
                }
                catch (SqlException exception) when (exception.Number is 2601 or 2627 && attempt < MaxAttempts)
                {
                    // A parallel first call created the row: take the next value of it.
                }
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static async Task<long> NextInMemoryAsync(OmtsDbContext db, string key, CancellationToken cancellationToken)
    {
        await InMemoryLock.WaitAsync(cancellationToken);
        try
        {
            var sequence = await db.DocumentSequences.SingleOrDefaultAsync(s => s.Key == key, cancellationToken);
            if (sequence is null)
            {
                sequence = new DocumentSequence { Key = key };
                db.DocumentSequences.Add(sequence);
            }

            sequence.LastValue++;
            await db.SaveChangesAsync(cancellationToken);
            return sequence.LastValue;
        }
        finally
        {
            InMemoryLock.Release();
        }
    }

    private static DbCommand Command(OmtsDbContext db, string sql, string key)
    {
        var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@key";
        parameter.Value = key;
        command.Parameters.Add(parameter);
        return command;
    }
}

