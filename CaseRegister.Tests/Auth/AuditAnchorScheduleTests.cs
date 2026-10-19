using System.Collections.Concurrent;
using CaseRegister.Application.Auditing;
using CaseRegister.Domain.Accounts;
using CaseRegister.Domain.Approvals;
using CaseRegister.Infrastructure.Auditing;
using CaseRegister.Tests.Database;
using CaseRegister.Tests.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CaseRegister.Tests.Auth;

/// <summary>Stands in for the R2 bucket: keeps what it is given, can be made to fail or to be slow, and can look at the database at the moment it is written to.</summary>
public sealed class FakeAnchorStore : IAuditAnchorSink
{
    private readonly ConcurrentQueue<AuditAnchor> _anchors = new();

    public bool IsConfigured => true;

    public IReadOnlyList<AuditAnchor> Anchors => [.. _anchors];

    /// <summary>While true every write fails, as when the bucket is unreachable.</summary>
    public bool Failing { get => Volatile.Read(ref _failing); set => Volatile.Write(ref _failing, value); }

    private bool _failing;

    /// <summary>How long a write takes before it answers.</summary>
    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    /// <summary>Called at the moment of each write (before it is stored), to check what the database looked like from outside the writer's transaction.</summary>
    public Func<AuditAnchor, Task>? OnWrite { get; set; }

    public int Attempts => _attempts;

    private int _attempts;

    public async Task WriteAsync(AuditAnchor anchor, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _attempts);
        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, cancellationToken);
        }

        if (OnWrite is not null)
        {
            await OnWrite(anchor);
        }

        if (Failing)
        {
            throw new IOException("The anchor store could not be reached.");
        }

        _anchors.Enqueue(anchor);
    }

    public Task<IReadOnlyList<AuditAnchor>> ReadAllAsync(CancellationToken cancellationToken = default) => Task.FromResult(Anchors);
}

/// <summary>When anchors are stored, and what happens when the store is down.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class AuditAnchorScheduleTests(PostgresFixture postgres)
{
    private static Task<ApiFactory> FactoryAsync(PostgresFixture postgres, FakeAnchorStore store) =>
        ApiFactory.CreateAsync(postgres, configureServices: services => services.Replace(ServiceDescriptor.Singleton<IAuditAnchorSink>(store)));

    private static DateTimeOffset At(int month, int day, int hour, int minute = 0) => new(DateTime.UtcNow.Year + 1, month, day, hour, minute, 0, TimeSpan.Zero);

    private static async Task<List<string>> ActionsAsync(ApiFactory factory) =>
        [.. (await factory.AuditEntriesAsync()).Select(entry => entry.Action)];

    // --- When ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task One_is_written_when_a_new_Zambian_day_begins_and_not_again_the_same_day()
    {
        var store = new FakeAnchorStore();
        await using var factory = await FactoryAsync(postgres, store);
        factory.Clock.SetUtcNow(At(3, 10, 8));

        await factory.WriteManyAsync(1);
        Assert.Single(store.Anchors); // the first entry of the day is anchored once it has committed, and it is in the anchor

        factory.Clock.Advance(TimeSpan.FromMinutes(30));
        await factory.WriteManyAsync(5);
        Assert.Single(store.Anchors);

        factory.Clock.SetUtcNow(At(3, 11, 8)); // the next Zambian day
        await factory.WriteManyAsync(1);

        Assert.Equal(2, store.Anchors.Count);
        var head = await factory.AuditHeadAsync();
        Assert.Equal(head.LastSequence, store.Anchors[^1].Sequence);
        Assert.True((await factory.VerifyAuditAsync()).Intact);
    }

    [Fact]
    public async Task One_is_written_by_the_first_entry_after_an_hour_has_passed_since_the_last_anchor()
    {
        var store = new FakeAnchorStore();
        await using var factory = await FactoryAsync(postgres, store);
        factory.Clock.SetUtcNow(At(6, 4, 8));
        await factory.WriteManyAsync(2);
        Assert.Single(store.Anchors);

        factory.Clock.Advance(TimeSpan.FromMinutes(59));
        await factory.WriteManyAsync(3);
        Assert.Single(store.Anchors); // not yet an hour

        factory.Clock.Advance(TimeSpan.FromMinutes(2));
        await factory.WriteManyAsync(1);
        Assert.Equal(2, store.Anchors.Count); // the first entry after the hour

        var head = await factory.AuditHeadAsync();
        Assert.Equal(head.LastSequence, store.Anchors[^1].Sequence);

        // And the hour starts again from that anchor.
        factory.Clock.Advance(TimeSpan.FromMinutes(30));
        await factory.WriteManyAsync(2);
        Assert.Equal(2, store.Anchors.Count);
    }

    [Fact]
    public async Task One_is_written_straight_after_every_confirmed_proposal_and_includes_the_confirmation()
    {
        var store = new FakeAnchorStore();
        await using var factory = await FactoryAsync(postgres, store);
        var admins = await factory.CreateAdminsAsync(3);
        var staff = await factory.CreateUserAsync("staff@example.test", staff: true);
        var before = store.Anchors.Count;

        await factory.ProposeAndConfirmAsync(admins[0], admins[1], Proposals.AssignRole(staff, Role.CaseReviewer));

        Assert.True(store.Anchors.Count > before);
        var entries = await factory.AuditEntriesAsync();
        var confirmed = entries.Last(entry => entry.Action == "ActionConfirmed");
        var anchor = store.Anchors[^1];
        Assert.True(anchor.Sequence >= confirmed.Sequence, "the anchor covers the confirmation");
        Assert.Equal(entries.Single(entry => entry.Sequence == anchor.Sequence).EntryHash, anchor.Hash);

        // Another, and another: each confirmed proposal asks for its own, however recent the last anchor was.
        var again = store.Anchors.Count;
        var other = await factory.CreateUserAsync("staff2@example.test", staff: true);
        await factory.ProposeAndConfirmAsync(admins[1], admins[2], Proposals.AssignRole(other, Role.CaseReviewer));
        Assert.True(store.Anchors.Count > again);
    }

    [Fact]
    public async Task A_proposal_that_is_only_made_and_not_confirmed_asks_for_no_extra_anchor()
    {
        var store = new FakeAnchorStore();
        await using var factory = await FactoryAsync(postgres, store);
        var admins = await factory.CreateAdminsAsync(3);
        var staff = await factory.CreateUserAsync("staff@example.test", staff: true);
        await factory.WriteManyAsync(1);
        var before = store.Anchors.Count;

        await factory.ProposeOkAsync(admins[0], Proposals.AssignRole(staff, Role.CaseReviewer));

        Assert.Equal(before, store.Anchors.Count);
    }

    // --- After the commit ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_anchor_is_stored_only_after_the_work_it_follows_has_committed()
    {
        var store = new FakeAnchorStore();
        await using var factory = await FactoryAsync(postgres, store);
        var admins = await factory.CreateAdminsAsync(3);
        var staff = await factory.CreateUserAsync("staff@example.test", staff: true);
        var proposal = await factory.ProposeOkAsync(admins[0], Proposals.AssignRole(staff, Role.CaseReviewer));

        // At the moment the bucket is written to, a different connection must already be able to see the confirmed proposal and its entries.
        var seen = new List<string>();
        store.OnWrite = async anchor =>
        {
            var visible = await factory.WithDbAsync(db => db.AuditLog.CountAsync(entry => entry.Sequence == anchor.Sequence));
            var status = await factory.WithDbAsync(db => db.PendingActions.Where(action => action.Id == proposal.Id).Select(action => action.Status).SingleAsync());
            seen.Add($"{visible}:{status}");
        };

        await factory.ConfirmOkAsync(admins[1], proposal);

        Assert.NotEmpty(seen);
        Assert.All(seen, line => Assert.Equal($"1:{PendingActionStatus.Confirmed}", line));
    }

    [Fact]
    public async Task A_transaction_that_rolls_back_stores_no_anchor_for_what_it_never_committed()
    {
        var store = new FakeAnchorStore();
        await using var factory = await FactoryAsync(postgres, store);
        await factory.WriteManyAsync(1);
        var anchors = store.Anchors.Count;
        var entries = (await factory.AuditEntriesAsync()).Count;

        // A confirmation entry written inside a transaction that is then rolled back.
        using (var scope = factory.Services.CreateScope())
        {
            var inner = scope.ServiceProvider.GetRequiredService<CaseRegister.Infrastructure.Persistence.AppDbContext>();
            await inner.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var transaction = await inner.Database.BeginTransactionAsync();
                await scope.ServiceProvider.GetRequiredService<IAuditLog>().WriteAsync(new AuditEntry(AuditAction.ActionConfirmed, null, Subject: "x", TargetType: "Probe"));
                await transaction.RollbackAsync();
            });
        }

        Assert.Equal(entries, (await factory.AuditEntriesAsync()).Count);
        Assert.Equal(anchors, store.Anchors.Count);
    }

    // --- When the store is down ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_store_that_is_down_never_blocks_the_action_and_is_recorded_once_as_deferred()
    {
        var store = new FakeAnchorStore { Failing = true };
        await using var factory = await FactoryAsync(postgres, store);
        var admins = await factory.CreateAdminsAsync(3);
        var staff = await factory.CreateUserAsync("staff@example.test", staff: true);

        // A confirmed proposal goes through even though its anchor cannot be stored.
        await factory.ProposeAndConfirmAsync(admins[0], admins[1], Proposals.AssignRole(staff, Role.CaseReviewer));
        Assert.Contains(Role.CaseReviewer, await factory.RolesOfAsync(staff));
        Assert.Empty(store.Anchors);

        var actions = await ActionsAsync(factory);
        Assert.Equal(1, actions.Count(action => action == "AnchorDeferred"));
        Assert.True((await factory.AuditHeadAsync()).AnchorDeferred);
        Assert.True((await factory.VerifyAuditAsync()).Intact);

        // While it stays down, more writes still work and do not each add an entry.
        await factory.WriteManyAsync(3);
        Assert.Equal(1, (await ActionsAsync(factory)).Count(action => action == "AnchorDeferred"));
    }

    [Fact]
    public async Task The_next_audit_write_retries_and_the_deferred_mark_is_cleared()
    {
        var store = new FakeAnchorStore { Failing = true };
        await using var factory = await FactoryAsync(postgres, store);
        await factory.WriteManyAsync(2);
        Assert.True((await factory.AuditHeadAsync()).AnchorDeferred);
        Assert.Empty(store.Anchors);

        store.Failing = false;
        await factory.WriteManyAsync(1);

        var anchor = Assert.Single(store.Anchors);
        Assert.Equal((await factory.AuditHeadAsync()).LastSequence, anchor.Sequence);
        Assert.False((await factory.AuditHeadAsync()).AnchorDeferred);

        // Back to normal: no further anchor until one is due.
        await factory.WriteManyAsync(3);
        Assert.Single(store.Anchors);
    }

    [Fact]
    public async Task A_store_that_is_too_slow_is_given_up_on_after_three_seconds_and_deferred()
    {
        var store = new FakeAnchorStore { Delay = TimeSpan.FromSeconds(30) };
        await using var factory = await FactoryAsync(postgres, store);
        var started = System.Diagnostics.Stopwatch.StartNew();

        await factory.WriteManyAsync(1);

        Assert.InRange(started.Elapsed, TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(10));
        Assert.Empty(store.Anchors);
        Assert.Contains("AnchorDeferred", await ActionsAsync(factory));
    }

    [Fact]
    public async Task A_command_that_only_reads_anchors_asks_for_none_to_be_stored()
    {
        var store = new FakeAnchorStore();
        await using var factory = await FactoryAsync(postgres, store);

        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<CaseRegister.Infrastructure.Auditing.AuditAnchorGate>().Suppress();
            await scope.ServiceProvider.GetRequiredService<IAuditVerifier>().VerifyAndRecordAsync(false, null, null);
        }

        Assert.Empty(store.Anchors);
        Assert.DoesNotContain("AnchorDeferred", await ActionsAsync(factory));
    }

    // --- Verifying against every anchor ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_row_changed_before_an_anchor_was_taken_of_it_is_found_by_the_first_anchor_that_disagrees()
    {
        var store = new FakeAnchorStore();
        await using var factory = await FactoryAsync(postgres, store);
        factory.Clock.SetUtcNow(At(8, 1, 8));
        await factory.WriteManyAsync(3, "early");
        factory.Clock.Advance(TimeSpan.FromMinutes(61));
        await factory.WriteManyAsync(3, "later");
        factory.Clock.Advance(TimeSpan.FromMinutes(61));
        await factory.WriteManyAsync(2, "latest");
        Assert.True(store.Anchors.Count >= 3);

        // Someone with the database but not the key edits an old row and rebuilds nothing else.
        await factory.TamperAsync("UPDATE audit_log SET detail = 'changed' WHERE sequence = 2");

        var result = await factory.VerifyAuditAsync();

        Assert.False(result.Intact);
        Assert.Equal(AuditBreakKind.BadHash, result.FirstBreak!.Kind);
        Assert.Equal(2, result.FirstBreak.Sequence);
        Assert.Equal(store.Anchors.Count, result.AnchorsChecked);
    }

    [Fact]
    public async Task A_chain_cut_back_with_its_head_to_match_diverges_from_the_anchors_stored_outside_the_database()
    {
        var store = new FakeAnchorStore();
        await using var factory = await FactoryAsync(postgres, store);
        factory.Clock.SetUtcNow(At(9, 1, 8));
        await factory.WriteManyAsync(4, "a");
        factory.Clock.Advance(TimeSpan.FromMinutes(61));
        await factory.WriteManyAsync(4, "b");
        var anchors = store.Anchors.OrderBy(anchor => anchor.Sequence).ToList();
        Assert.True(anchors.Count >= 2);

        // The tail is cut back past the second anchor and the head made to agree: the chain alone looks fine, the anchor does not.
        var keep = (await factory.AuditEntriesAsync()).Single(entry => entry.Sequence == anchors[0].Sequence);
        await factory.TamperAsync($"DELETE FROM audit_log WHERE sequence > {keep.Sequence}; UPDATE audit_chain_head SET last_sequence = {keep.Sequence}, last_hash = '{keep.EntryHash}'");

        var result = await factory.VerifyAuditAsync();

        Assert.False(result.Intact);
        Assert.Equal(AuditBreakKind.AnchorMismatch, result.FirstBreak!.Kind);
        Assert.Equal(anchors[1].Sequence, result.FirstBreak.Sequence);
    }
}
