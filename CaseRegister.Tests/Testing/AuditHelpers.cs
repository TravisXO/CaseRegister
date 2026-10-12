using CaseRegister.Application.Auditing;
using CaseRegister.Infrastructure.Persistence;
using CaseRegister.Tests.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CaseRegister.Tests.Testing;

/// <summary>Writing to, tampering with and checking the audit chain, the way a test (or an attacker with the database) would.</summary>
public static class AuditHelpers
{
    public static async Task WriteAuditAsync(this ApiFactory factory, AuditEntry entry)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IAuditLog>().WriteAsync(entry);
    }

    /// <summary>Writes <paramref name="count"/> entries one after another, each different.</summary>
    public static async Task WriteManyAsync(this ApiFactory factory, int count, string label = "entry")
    {
        for (var index = 1; index <= count; index++)
        {
            await factory.WriteAuditAsync(new AuditEntry(AuditAction.RecordViewed, null, Subject: $"{label}-{index}", TargetType: "Probe", Detail: $"{label} {index}"));
        }
    }

    public static Task<List<AuditLogRecord>> AuditEntriesAsync(this ApiFactory factory) =>
        factory.WithDbAsync(db => db.AuditLog.AsNoTracking().OrderBy(record => record.Sequence).ToListAsync());

    public static Task<AuditChainHead> AuditHeadAsync(this ApiFactory factory) =>
        factory.WithDbAsync(db => db.AuditChainHeads.AsNoTracking().SingleAsync());

    public static async Task<AuditVerification> VerifyAuditAsync(this ApiFactory factory, bool fromLastGoodAnchor = false)
    {
        using var scope = factory.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<IAuditVerifier>().VerifyAsync(fromLastGoodAnchor);
    }

    public static Task OwnerSqlAsync(this ApiFactory factory, string sql) => PostgresFixture.ExecuteAsync(factory.OwnerConnectionString!, sql);

    /// <summary>
    /// What someone with direct access to the database as its owner could do: switch the protection off for a moment,
    /// change the rows, and switch it back on. The application role cannot do any of this.
    /// </summary>
    public static Task TamperAsync(this ApiFactory factory, string sql) =>
        factory.OwnerSqlAsync($"ALTER TABLE audit_log DISABLE TRIGGER USER; {sql}; ALTER TABLE audit_log ENABLE TRIGGER USER;");

    /// <summary>Makes every insert into the audit log fail, as a full disk or a broken connection would.</summary>
    public static Task BreakAuditWritesAsync(this ApiFactory factory) =>
        factory.OwnerSqlAsync(
            """
            CREATE FUNCTION fail_audit_insert() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'simulated audit failure'; END $$;
            CREATE TRIGGER fail_audit_insert BEFORE INSERT ON audit_log FOR EACH ROW EXECUTE FUNCTION fail_audit_insert();
            """);

    public static Task RepairAuditWritesAsync(this ApiFactory factory) =>
        factory.OwnerSqlAsync("DROP TRIGGER fail_audit_insert ON audit_log; DROP FUNCTION fail_audit_insert();");
}

public static class UserTransactions
{
    /// <summary>
    /// Runs work inside a transaction the test starts itself (the application's retrying database strategy requires
    /// that to happen inside the strategy), and then either commits it or rolls it back.
    /// </summary>
    public static async Task InTransactionAsync(this ApiFactory factory, Func<IServiceProvider, Task> work, bool commit)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            await work(scope.ServiceProvider);

            if (commit)
            {
                await transaction.CommitAsync();
            }
            else
            {
                await transaction.RollbackAsync();
            }
        });
    }

    /// <summary>The status of a request, where the test server turns an unhandled exception in the application into a thrown one: that is a 500.</summary>
    public static async Task<System.Net.HttpStatusCode> StatusOfAsync(Func<Task<HttpResponseMessage>> send)
    {
        try
        {
            return (await send()).StatusCode;
        }
        catch (Exception)
        {
            return System.Net.HttpStatusCode.InternalServerError;
        }
    }
}
