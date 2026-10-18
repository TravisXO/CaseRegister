using System.Data.Common;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using CaseRegister.Application.Approvals;
using CaseRegister.Application.Auditing;
using CaseRegister.Domain.Accounts;
using CaseRegister.Domain.Notifications;
using CaseRegister.Infrastructure.Persistence;
using CaseRegister.Tests.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace CaseRegister.Tests.Database;

/// <summary>Counts every connection the application opens through Entity Framework.</summary>
public sealed class ConnectionCounter : DbConnectionInterceptor
{
    private int _opened;

    public int Opened => _opened;

    public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _opened);

        return base.ConnectionOpeningAsync(connection, eventData, result, cancellationToken);
    }
}

/// <summary>
/// Makes the next commit look, to the application, as though the connection dropped just as the database acknowledged it: the commit happened,
/// but the application is told it failed the way a lost connection fails. Only for tests.
/// </summary>
public sealed class LostCommitAcknowledgement : DbTransactionInterceptor
{
    private int _armed;
    private int _lost;

    /// <summary>How many acknowledgements have been lost.</summary>
    public int Lost => _lost;

    public void ArmForNextCommit() => Interlocked.Exchange(ref _armed, 1);

    public override async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await base.TransactionCommittedAsync(transaction, eventData, cancellationToken);

        if (Interlocked.CompareExchange(ref _armed, 0, 1) == 1)
        {
            Interlocked.Increment(ref _lost);

            throw new NpgsqlException("Exception while reading from stream", new IOException("The connection was lost after the commit."));
        }
    }
}

/// <summary>The database going away for a moment, a commit whose acknowledgement is lost, and the health checks that must not wake it.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ResilienceTests(PostgresFixture postgres)
{
    private static (ApiFactoryHooks Hooks, Action<IServiceCollection> Configure) Hooks()
    {
        var hooks = new ApiFactoryHooks(new ConnectionCounter(), new LostCommitAcknowledgement());

        return (hooks, services =>
        {
            services.AddSingleton<IInterceptor>(hooks.Connections);
            services.AddSingleton<IInterceptor>(hooks.Acknowledgement);
        });
    }

    private sealed record ApiFactoryHooks(ConnectionCounter Connections, LostCommitAcknowledgement Acknowledgement);

    // --- The retry settings ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_database_is_retried_at_most_three_times_with_at_most_five_seconds_between()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var scope = factory.Services.CreateScope();
        var strategy = scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.CreateExecutionStrategy();

        Assert.Equal("NpgsqlRetryingExecutionStrategy", strategy.GetType().Name);
        var strategyType = typeof(Microsoft.EntityFrameworkCore.Storage.ExecutionStrategy);
        var maxRetries = (int)strategyType.GetProperty("MaxRetryCount", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(strategy)!;
        var maxDelay = (TimeSpan)strategyType.GetProperty("MaxRetryDelay", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(strategy)!;
        Assert.Equal(3, maxRetries);
        Assert.Equal(TimeSpan.FromSeconds(5), maxDelay);
    }

    // --- A lost commit acknowledgement ---------------------------------------------------------------------------------------

    [Fact]
    public async Task An_audit_write_whose_commit_acknowledgement_is_lost_is_not_written_twice()
    {
        var (hooks, configure) = Hooks();
        await using var factory = await ApiFactory.CreateAsync(postgres, configureServices: configure);
        await factory.WriteManyAsync(1, "before");
        var before = (await factory.AuditEntriesAsync()).Count;

        hooks.Acknowledgement.ArmForNextCommit();
        await factory.WriteManyAsync(1, "interrupted");

        Assert.Equal(1, hooks.Acknowledgement.Lost);
        var entries = await factory.AuditEntriesAsync();
        Assert.Equal(before + 1, entries.Count(entry => entry.Action != nameof(AuditAction.AnchorDeferred)));
        Assert.Single(entries, entry => entry.Subject == "interrupted-1");
        Assert.True((await factory.VerifyAuditAsync()).Intact);
    }

    [Fact]
    public async Task A_confirmation_whose_commit_acknowledgement_is_lost_is_applied_once_with_one_set_of_audit_entries()
    {
        var (hooks, configure) = Hooks();
        await using var factory = await ApiFactory.CreateAsync(postgres, configureServices: configure);
        var admins = await factory.CreateAdminsAsync(3);
        var staff = await factory.CreateUserAsync("staff@example.test", staff: true);
        var proposal = await factory.ProposeOkAsync(admins[0], Proposals.AssignRole(staff, Role.CaseReviewer));
        var confirmedBefore = (await factory.AuditEntriesAsync()).Count(entry => entry.Action == nameof(AuditAction.ActionConfirmed));

        hooks.Acknowledgement.ArmForNextCommit();
        var result = await factory.ConfirmAsync(admins[1], proposal);

        Assert.Equal(1, hooks.Acknowledgement.Lost);
        Assert.Equal(ConfirmOutcome.Confirmed, result.Outcome);
        Assert.False(result.WasRepeat, "the confirmation went through once; it was not found already done");
        var entries = await factory.AuditEntriesAsync();
        Assert.Equal(confirmedBefore + 1, entries.Count(entry => entry.Action == nameof(AuditAction.ActionConfirmed)));
        Assert.Single(entries, entry => entry.Action == nameof(AuditAction.RolesChanged) && entry.Subject == staff.Id.ToString());
        Assert.Single(await factory.RolesOfAsync(staff));
        Assert.Equal(1, await factory.WithDbAsync(db => db.Notifications.CountAsync(n => n.RecipientId == admins[0].Id && n.Type == NotificationType.ProposalConfirmed)));
        Assert.True((await factory.VerifyAuditAsync()).Intact);
    }

    [Fact]
    public async Task A_confirmation_interrupted_by_a_brief_database_pause_completes_exactly_once_with_one_audit_entry()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var admins = await factory.CreateAdminsAsync(3);
        var staff = await factory.CreateUserAsync("staff@example.test", staff: true);
        var proposal = await factory.ProposeOkAsync(admins[0], Proposals.AssignRole(staff, Role.CaseReviewer));
        var confirmedBefore = (await factory.AuditEntriesAsync()).Count(entry => entry.Action == nameof(AuditAction.ActionConfirmed));

        // The database stops answering for two seconds while the confirmation is being made.
        Docker("pause", postgres.ContainerId);
        Task<ConfirmResult> confirming;
        try
        {
            confirming = factory.ConfirmAsync(admins[1], proposal);
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
        finally
        {
            Docker("unpause", postgres.ContainerId);
        }

        var result = await confirming;

        Assert.Equal(ConfirmOutcome.Confirmed, result.Outcome);
        var entries = await factory.AuditEntriesAsync();
        Assert.Equal(confirmedBefore + 1, entries.Count(entry => entry.Action == nameof(AuditAction.ActionConfirmed)));
        Assert.Single(entries, entry => entry.Action == nameof(AuditAction.RolesChanged) && entry.Subject == staff.Id.ToString());
        Assert.Contains(Role.CaseReviewer, await factory.RolesOfAsync(staff));
        Assert.True((await factory.VerifyAuditAsync()).Intact);
    }

    private static void Docker(string verb, string container)
    {
        using var process = Process.Start(new ProcessStartInfo("docker", [verb, container]) { RedirectStandardOutput = true, RedirectStandardError = true })
            ?? throw new InvalidOperationException("docker could not be started");
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"docker {verb} failed: {process.StandardError.ReadToEnd()}");
    }

    // --- The liveness check never touches the database -------------------------------------------------------------------------

    [Fact]
    public async Task Live_never_opens_a_database_connection_and_ready_does()
    {
        var (hooks, configure) = Hooks();
        await using var factory = await ApiFactory.CreateAsync(postgres, configureServices: configure);
        var client = factory.CreateApiClient();
        await client.GetAsync("/health/live"); // the host is up and anything it does at startup is done
        var opened = hooks.Connections.Opened;

        // 1. Not one connection is opened for it, however often it is asked.
        for (var call = 0; call < 25; call++)
        {
            using var response = await client.Http.GetAsync("/health/live");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(opened, hooks.Connections.Opened);

        // 2. With the database not answering at all, it still answers at once. (A connection to a paused server hangs until it times out.)
        Docker("pause", postgres.ContainerId);
        try
        {
            var timer = Stopwatch.StartNew();
            for (var call = 0; call < 25; call++)
            {
                using var response = await client.Http.GetAsync("/health/live");
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }

            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2), $"25 calls took {timer.Elapsed}");
        }
        finally
        {
            Docker("unpause", postgres.ContainerId);
        }

        // Ready, by contrast, does ask the database (this proves the counter would have seen a connection).
        using var ready = await client.Http.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.True(hooks.Connections.Opened > opened);
    }

    [Fact]
    public async Task The_platform_health_check_is_the_live_one_and_the_ready_one_is_documented_as_not_to_be_pinged()
    {
        var root = Architecture.SolutionGraph.SolutionRoot;

        Assert.Contains("healthCheckPath: /health/live", File.ReadAllText(Path.Combine(root, "render.yaml")), StringComparison.Ordinal);
        var docs = File.ReadAllText(Path.Combine(root, "docs", "deployment.md"));
        Assert.Contains("/health/ready", docs, StringComparison.Ordinal);
        Assert.Contains("Do not ping", docs, StringComparison.Ordinal);
        await Task.CompletedTask;
    }
}
