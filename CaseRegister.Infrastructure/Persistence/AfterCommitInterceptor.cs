using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CaseRegister.Infrastructure.Persistence;

/// <summary>
/// Runs the work queued with <see cref="AppDbContext.AfterCommit"/> once the database transaction has committed, and forgets it if the
/// transaction rolled back or failed. This is how something that is not Postgres (storing an audit anchor in R2) happens after the commit,
/// never inside a unit of work that may still be retried or undone.
/// </summary>
public sealed class AfterCommitInterceptor : DbTransactionInterceptor
{
    public override async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await base.TransactionCommittedAsync(transaction, eventData, cancellationToken);

        if (eventData.Context is AppDbContext context)
        {
            await context.RunAfterCommitAsync(cancellationToken);
        }
    }

    public override Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        (eventData.Context as AppDbContext)?.DropAfterCommit();

        return base.TransactionRolledBackAsync(transaction, eventData, cancellationToken);
    }

    public override Task TransactionFailedAsync(DbTransaction transaction, TransactionErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        (eventData.Context as AppDbContext)?.DropAfterCommit();

        return base.TransactionFailedAsync(transaction, eventData, cancellationToken);
    }
}
