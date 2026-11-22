using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace TempHire.Server.Tests;

/// <summary>Counts the queries a context sends: pass it to <see cref="SaveFixture.NewContext"/>.</summary>
internal sealed class CommandCounter : DbCommandInterceptor
{
    public int Readers { get; private set; }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Readers++;
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Readers++;
        return ValueTask.FromResult(result);
    }
}
