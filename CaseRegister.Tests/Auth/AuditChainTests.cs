using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CaseRegister.Application.Accounts;
using CaseRegister.Application.Auditing;
using CaseRegister.Domain.Accounts;
using CaseRegister.Infrastructure.Persistence;
using CaseRegister.Tests.Database;
using CaseRegister.Tests.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CaseRegister.Tests.Auth;

/// <summary>The audit chain: how entries are linked, and how any tampering shows.</summary>
[Collection(PostgresTestGroup.Name)]
public class AuditChainTests(PostgresFixture postgres)
{
    private static readonly byte[] _testKey = Convert.FromBase64String(ApiFactory.TestKeyBase64);

    // --- The shape of the chain ---------------------------------------------------------------------------

    [Fact]
    public async Task Entries_are_numbered_from_one_with_no_gaps_and_each_points_at_the_one_before()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);

        await factory.WriteManyAsync(6);

        var entries = await factory.AuditEntriesAsync();
        Assert.Equal([1L, 2, 3, 4, 5, 6], entries.Select(entry => entry.Sequence));
        Assert.Equal(AuditChainFormat.GenesisHash, entries[0].PreviousHash);
        for (var index = 1; index < entries.Count; index++)
        {
            Assert.Equal(entries[index - 1].EntryHash, entries[index].PreviousHash);
        }

        Assert.All(entries, entry =>
        {
            Assert.Matches("^[0-9a-f]{64}$", entry.EntryHash);
            Assert.Equal(ApiFactory.TestKeyId, entry.KeyId);
        });
    }

    [Fact]
    public async Task The_head_always_says_where_the_chain_ends()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        Assert.Equal(0, (await factory.AuditHeadAsync()).LastSequence);

        await factory.WriteManyAsync(4);

        var head = await factory.AuditHeadAsync();
        var last = (await factory.AuditEntriesAsync()).Last();
        Assert.Equal(4, head.LastSequence);
        Assert.Equal(last.EntryHash, head.LastHash);
    }

    [Fact]
    public async Task An_untouched_chain_verifies_and_so_does_an_empty_one()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        Assert.True((await factory.VerifyAuditAsync()).Intact);

        await factory.WriteManyAsync(8);

        var result = await factory.VerifyAuditAsync();
        Assert.True(result.Intact);
        Assert.Equal(8, result.EntriesChecked);
        Assert.Equal(8, result.HeadSequence);
        Assert.Null(result.FirstBreak);
    }

    [Fact]
    public async Task Each_entry_is_signed_with_a_hash_that_can_be_recomputed_from_its_fields_and_the_key()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.WriteAuditAsync(new AuditEntry(AuditAction.RecordViewed, null, Subject: "item-1", TargetType: "Probe", Detail: "one", Before: new { a = 1 }, After: new { a = 2 }));

        var entry = (await factory.AuditEntriesAsync()).Single();
        var fields = new AuditFields(
            entry.Sequence,
            AuditChainFormat.FormatTime(entry.OccurredAt),
            entry.ActorType,
            entry.ActorUserId?.ToString("D"),
            entry.ActorRole,
            entry.Action,
            entry.TargetType,
            entry.Subject,
            entry.AgencyId?.ToString("D"),
            entry.CorrelationId,
            entry.IpAddress,
            entry.Detail,
            entry.BeforeJson,
            entry.AfterJson,
            entry.PreviousHash,
            entry.KeyId);

        Assert.Equal(AuditChainFormat.Hash(_testKey, fields), entry.EntryHash);
        Assert.Equal("{\"a\":1}", entry.BeforeJson);
        Assert.Equal("{\"a\":2}", entry.AfterJson);
    }

    // --- Tampering is detected ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("detail = 'tampered'")]
    [InlineData("action = 'UserReactivated'")]
    [InlineData("actor_user_id = gen_random_uuid()")]
    [InlineData("actor_role = 'Admin'")]
    [InlineData("actor_type = 'user'")]
    [InlineData("target_id = 'someone-else'")]
    [InlineData("target_type = 'Agency'")]
    [InlineData("agency_id = gen_random_uuid()")]
    [InlineData("ip_address = '203.0.113.99'")]
    [InlineData("correlation_id = 'forged'")]
    [InlineData("occurred_at = occurred_at + interval '1 second'")]
    [InlineData("occurred_at = occurred_at + interval '1 microsecond'")]
    [InlineData("after_json = '{\"state\":\"forged\"}'")]
    [InlineData("before_json = '{\"a\":1}'")]
    [InlineData("entry_hash = repeat('f', 64)")]
    public async Task An_edited_row_is_found_and_the_report_names_it(string change)
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.WriteManyAsync(6);

        await factory.TamperAsync($"UPDATE audit_log SET {change} WHERE sequence = 3");

        var result = await factory.VerifyAuditAsync();
        Assert.False(result.Intact);
        Assert.Equal(AuditBreakKind.BadHash, result.FirstBreak!.Kind);
        Assert.Equal(3, result.FirstBreak.Sequence);
        Assert.Equal(2, result.EntriesChecked); // it stopped at the first broken link
    }

    [Fact]
    public async Task A_row_that_claims_a_different_key_is_found()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.WriteManyAsync(5);

        await factory.TamperAsync("UPDATE audit_log SET key_id = 'k9' WHERE sequence = 4");

        var result = await factory.VerifyAuditAsync();
        Assert.Equal(AuditBreakKind.UnknownKey, result.FirstBreak!.Kind);
        Assert.Equal(4, result.FirstBreak.Sequence);
    }

    [Fact]
    public async Task A_row_that_points_somewhere_else_is_found()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.WriteManyAsync(5);

        await factory.TamperAsync("UPDATE audit_log SET previous_hash = repeat('f', 64) WHERE sequence = 3");

        var result = await factory.VerifyAuditAsync();
        Assert.Equal(AuditBreakKind.BadPreviousHash, result.FirstBreak!.Kind);
        Assert.Equal(3, result.FirstBreak.Sequence);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task A_deleted_row_is_found_as_a_gap_in_the_numbers(long deleted)
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.WriteManyAsync(6);

        await factory.TamperAsync($"DELETE FROM audit_log WHERE sequence = {deleted}");

        var result = await factory.VerifyAuditAsync();
        Assert.False(result.Intact);
        Assert.Equal(AuditBreakKind.SequenceGap, result.FirstBreak!.Kind);
        Assert.Equal(deleted, result.FirstBreak.Sequence);
    }

    [Fact]
    public async Task Rows_cut_off_the_end_are_found_because_the_head_still_remembers_them()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.WriteManyAsync(6);

        await factory.TamperAsync("DELETE FROM audit_log WHERE sequence >= 5");

        var result = await factory.VerifyAuditAsync();
        Assert.Equal(AuditBreakKind.HeadMismatch, result.FirstBreak!.Kind);
        Assert.Equal(5, result.FirstBreak.Sequence);
    }

    [Fact]
    public async Task Two_rows_swapped_are_found()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.WriteManyAsync(6);

        await factory.TamperAsync(
            """
            UPDATE audit_log SET sequence = 99 WHERE sequence = 2;
            UPDATE audit_log SET sequence = 2 WHERE sequence = 3;
            UPDATE audit_log SET sequence = 3 WHERE sequence = 99
            """);

        var result = await factory.VerifyAuditAsync();
        Assert.False(result.Intact);
        Assert.Equal(AuditBreakKind.BadPreviousHash, result.FirstBreak!.Kind);
        Assert.Equal(2, result.FirstBreak.Sequence);
    }

    [Fact]
    public async Task A_forged_row_added_at_the_end_without_the_key_is_found()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.WriteManyAsync(3);
        var last = (await factory.AuditEntriesAsync()).Last();

        // A plausible row, pointing at the real last hash, with a hash made without the key.
        await factory.TamperAsync(
            $"""
            INSERT INTO audit_log (id, sequence, occurred_at, actor_type, action, previous_hash, entry_hash, key_id, created_at, updated_at)
            VALUES (gen_random_uuid(), 4, now(), 'system', 'RecordViewed', '{last.EntryHash}', repeat('a', 64), 'k1', now(), now());
            UPDATE audit_chain_head SET last_sequence = 4, last_hash = repeat('a', 64)
            """);

        var result = await factory.VerifyAuditAsync();
        Assert.Equal(AuditBreakKind.BadHash, result.FirstBreak!.Kind);
        Assert.Equal(4, result.FirstBreak.Sequence);
    }

    [Fact]
    public async Task A_whole_chain_recomputed_without_the_key_is_found_at_the_first_entry()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.WriteManyAsync(6);

        await RecomputeFromAsync(factory, 1, key: Enumerable.Range(50, 32).Select(index => (byte)index).ToArray(), changeDetail: "rewritten");

        var result = await factory.VerifyAuditAsync();
        Assert.False(result.Intact);
        Assert.Equal(AuditBreakKind.BadHash, result.FirstBreak!.Kind);
        Assert.Equal(1, result.FirstBreak.Sequence);
    }

    [Fact]
    public async Task An_edit_with_every_later_hash_recomputed_by_someone_without_the_key_is_found_where_it_starts()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.WriteManyAsync(8);

        // They edit entry 4 and carefully fix every link after it, and the head, so the chain looks seamless.
        await RecomputeFromAsync(factory, 4, key: Enumerable.Range(7, 32).Select(index => (byte)index).ToArray(), changeDetail: "rewritten");

        var result = await factory.VerifyAuditAsync();
        Assert.False(result.Intact);
        Assert.Equal(4, result.FirstBreak!.Sequence);
        Assert.Equal(3, result.EntriesChecked); // the part before it was fine
    }

    [Fact]
    public async Task A_plain_unkeyed_hash_cannot_stand_in_for_the_keyed_one()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.WriteManyAsync(3);

        // The same canonical text hashed with plain SHA-256: what anyone could compute from the table alone.
        var entries = await factory.AuditEntriesAsync();
        var fields = FieldsOf(entries[2]);
        var plain = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(AuditChainFormat.Canonical(fields))));
        await factory.TamperAsync($"UPDATE audit_log SET entry_hash = '{plain}' WHERE sequence = 3; UPDATE audit_chain_head SET last_hash = '{plain}'");

        Assert.Equal(AuditBreakKind.BadHash, (await factory.VerifyAuditAsync()).FirstBreak!.Kind);
    }

    private static AuditFields FieldsOf(AuditLogRecord entry) => new(
        entry.Sequence,
        AuditChainFormat.FormatTime(entry.OccurredAt),
        entry.ActorType,
        entry.ActorUserId?.ToString("D"),
        entry.ActorRole,
        entry.Action,
        entry.TargetType,
        entry.Subject,
        entry.AgencyId?.ToString("D"),
        entry.CorrelationId,
        entry.IpAddress,
        entry.Detail,
        entry.BeforeJson,
        entry.AfterJson,
        entry.PreviousHash,
        entry.KeyId);

    /// <summary>Rewrites entries from a sequence number onward with fresh links and hashes made with the given key, and fixes the head to match.</summary>
    private static async Task RecomputeFromAsync(ApiFactory factory, long from, byte[] key, string changeDetail)
    {
        var entries = await factory.AuditEntriesAsync();
        var previous = from == 1 ? AuditChainFormat.GenesisHash : entries.Single(entry => entry.Sequence == from - 1).EntryHash;
        var statements = new List<string>();

        foreach (var entry in entries.Where(entry => entry.Sequence >= from))
        {
            var detail = entry.Sequence == from ? changeDetail : entry.Detail;
            var hash = AuditChainFormat.Hash(key, FieldsOf(entry) with { Detail = detail, PreviousHash = previous });
            statements.Add($"UPDATE audit_log SET detail = '{detail}', previous_hash = '{previous}', entry_hash = '{hash}' WHERE sequence = {entry.Sequence}");
            previous = hash;
        }

        statements.Add($"UPDATE audit_chain_head SET last_hash = '{previous}'");
        await factory.TamperAsync(string.Join("; ", statements));
    }

    // --- Appending safely ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Many_appends_at_the_same_moment_make_one_chain_with_no_fork_and_no_gap()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        const int Writers = 40;
        var gate = new TaskCompletionSource();

        var tasks = Enumerable.Range(1, Writers).Select(index => Task.Run(async () =>
        {
            await gate.Task;
            await factory.WriteAuditAsync(new AuditEntry(AuditAction.RecordViewed, null, Subject: $"parallel-{index}", TargetType: "Probe"));
        })).ToList();
        gate.SetResult();
        await Task.WhenAll(tasks);

        var entries = await factory.AuditEntriesAsync();
        Assert.Equal(Writers, entries.Count);
        Assert.Equal(Enumerable.Range(1, Writers).Select(index => (long)index), entries.Select(entry => entry.Sequence)); // 1..N, none missing, none repeated
        Assert.Equal(Writers, entries.Select(entry => entry.PreviousHash).Distinct().Count()); // no two entries share a predecessor: no fork
        Assert.Equal(Writers, entries.Select(entry => entry.Subject).Distinct().Count());
        Assert.True((await factory.VerifyAuditAsync()).Intact);
        Assert.Equal(Writers, (await factory.AuditHeadAsync()).LastSequence);
    }

    [Fact]
    public async Task Concurrent_appends_inside_their_own_transactions_serialise_on_the_chain_head()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var gate = new TaskCompletionSource();

        async Task WriteInTransactionAsync(int index)
        {
            await gate.Task;
            await factory.InTransactionAsync(
                async services =>
                {
                    await services.GetRequiredService<IAuditLog>().WriteAsync(new AuditEntry(AuditAction.RecordViewed, null, Subject: $"tx-{index}", TargetType: "Probe"));
                    await Task.Delay(20); // hold the transaction (and so the head lock) open for a moment
                },
                commit: true);
        }

        var tasks = Enumerable.Range(1, 12).Select(index => Task.Run(() => WriteInTransactionAsync(index))).ToList();
        gate.SetResult();
        await Task.WhenAll(tasks);

        Assert.Equal(Enumerable.Range(1, 12).Select(index => (long)index), (await factory.AuditEntriesAsync()).Select(entry => entry.Sequence));
        Assert.True((await factory.VerifyAuditAsync()).Intact);
    }

    [Fact]
    public async Task A_rolled_back_action_leaves_no_entry_and_no_gap()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.WriteManyAsync(2);
        var headBefore = await factory.AuditHeadAsync();

        await factory.InTransactionAsync(
            async services =>
            {
                var audit = services.GetRequiredService<IAuditLog>();
                await audit.WriteAsync(new AuditEntry(AuditAction.UserSuspended, null, Subject: "rolled-back-1", TargetType: "Probe"));
                await audit.WriteAsync(new AuditEntry(AuditAction.UserSuspended, null, Subject: "rolled-back-2", TargetType: "Probe"));
            },
            commit: false);

        Assert.Equal(headBefore.LastSequence, (await factory.AuditHeadAsync()).LastSequence);
        Assert.Equal(headBefore.LastHash, (await factory.AuditHeadAsync()).LastHash);

        await factory.WriteAuditAsync(new AuditEntry(AuditAction.RecordViewed, null, Subject: "after", TargetType: "Probe"));

        var entries = await factory.AuditEntriesAsync();
        Assert.Equal([1L, 2, 3], entries.Select(entry => entry.Sequence)); // the next number is the one the rolled-back entries would have had
        Assert.DoesNotContain(entries, entry => entry.Subject!.StartsWith("rolled-back", StringComparison.Ordinal));
        Assert.True((await factory.VerifyAuditAsync()).Intact);
    }

    [Fact]
    public async Task A_rolled_back_real_change_leaves_neither_the_change_nor_its_entry()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.WriteManyAsync(1);

        // A change that succeeds and writes its entry, and then the unit of work it belongs to fails.
        await factory.InTransactionAsync(
            async services =>
            {
                var db = services.GetRequiredService<AppDbContext>();
                db.Agencies.Add(CaseRegister.Domain.Agencies.Agency.Create("Temporary Agency"));
                await db.SaveChangesAsync();
                await services.GetRequiredService<IAuditLog>().WriteAsync(new AuditEntry(AuditAction.AgencySuspended, null, Subject: Guid.NewGuid().ToString(), TargetType: "Agency"));
            },
            commit: false);

        Assert.Equal(0, await factory.WithDbAsync(db => db.Agencies.CountAsync()));
        Assert.Single(await factory.AuditEntriesAsync());
        Assert.True((await factory.VerifyAuditAsync()).Intact);
    }


    // --- An action and its entry succeed or fail together --------------------------------------------------------

    [Fact]
    public async Task An_action_and_its_entry_are_written_together()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var staff = await factory.CreateUserAsync("staff@example.test", staff: true);

        await factory.AssignRolesAsync(staff, Role.CaseReviewer);

        var entry = Assert.Single(await factory.AuditEntriesAsync(), record => record.Action == "RolesChanged");
        Assert.Equal(staff.Id.ToString(), entry.Subject);
        Assert.Equal("User", entry.TargetType);
        Assert.Equal("{\"roles\":[]}", entry.BeforeJson);
        Assert.Equal("{\"roles\":[\"CaseReviewer\"]}", entry.AfterJson);
        Assert.Equal([Role.CaseReviewer], await factory.RolesOfAsync(staff));
    }

    [Fact]
    public async Task If_the_audit_entry_cannot_be_written_the_action_fails_with_it()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var staff = await factory.CreateUserAsync("staff@example.test", staff: true);
        await factory.BreakAuditWritesAsync();

        await Assert.ThrowsAnyAsync<Exception>(() => factory.TryChangeRolesAsync(new RoleChange(staff.Id, [Role.CaseReviewer], [])));

        Assert.Empty(await factory.RolesOfAsync(staff)); // failed closed: no change without its entry
        Assert.Equal(0, await factory.WithDbAsync(db => db.UserRoles.CountAsync()));

        await factory.RepairAuditWritesAsync();
        await factory.AssignRolesAsync(staff, Role.CaseReviewer);
        Assert.Equal([Role.CaseReviewer], await factory.RolesOfAsync(staff));
        Assert.True((await factory.VerifyAuditAsync()).Intact);
    }

    [Fact]
    public async Task A_certificate_and_its_entry_stand_or_fall_together()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var agency = await factory.CreateAgencyAsync();
        var member = await factory.CreateMemberAsync("member@example.test", agency, [Role.RegisteredAgent], currentCertificate: false);
        var client = factory.CreateApiClient();
        Assert.Equal(HttpStatusCode.OK, (await client.SignInWithTwoFactorAsync(factory, member)).StatusCode);
        await factory.BreakAuditWritesAsync();

        var status = await UserTransactions.StatusOfAsync(() => client.SendAsync(HttpMethod.Post, "/api/me/practising-certificates", new { certificateNumber = $"5001/{factory.Today.Year}", validFrom = $"{factory.Today.Year}-01-01", validTo = $"{factory.Today.Year}-12-31" }));

        Assert.Equal(HttpStatusCode.InternalServerError, status);
        Assert.Equal(0, await factory.WithDbAsync(db => db.PractisingCertificates.CountAsync())); // not kept without its entry
    }

    [Fact]
    public async Task A_failed_sign_in_still_gets_the_normal_generic_answer_when_the_audit_write_fails_and_the_error_is_logged()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync("member@example.test");
        var client = factory.CreateApiClient();
        var normal = await client.SignInAsync(user.Email!, "the wrong password entirely");
        var normalBody = await normal.Content.ReadFromJsonAsync<JsonElement>();
        await factory.BreakAuditWritesAsync();

        var unaudited = await client.SignInAsync(user.Email!, "the wrong password entirely");

        Assert.Equal(HttpStatusCode.Unauthorized, unaudited.StatusCode);
        var body = await unaudited.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(normalBody.GetProperty("title").GetString(), body.GetProperty("title").GetString());
        Assert.Equal(normalBody.GetProperty("detail").GetString(), body.GetProperty("detail").GetString());
        Assert.Contains(factory.Logs.Entries, log => log.Level == LogLevel.Error && log.Message.Contains("audit entry for SignInFailed failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failed_sign_up_audit_does_not_change_the_sign_up_answer()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.CreateUserAsync("taken@example.test");
        var client = factory.CreateApiClient();
        var normal = await SignUps.PostAsync(client, SignUps.Body(factory, "taken@example.test"));
        await factory.BreakAuditWritesAsync();

        var response = await SignUps.PostAsync(client, SignUps.Body(factory, "taken@example.test"));

        Assert.Equal(normal.StatusCode, response.StatusCode);
        Assert.Equal(await normal.Content.ReadAsStringAsync(), await response.Content.ReadAsStringAsync());
    }

    // --- Times, keys and what is kept ----------------------------------------------------------------------------

    [Fact]
    public async Task A_timestamp_with_sub_microsecond_ticks_still_verifies_after_a_round_trip_through_postgres()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var odd = new DateTimeOffset(DateTime.UtcNow.Year + 1, 3, 4, 5, 6, 7, TimeSpan.Zero).AddTicks(1_234_567); // 100 ns resolution
        factory.Clock.SetUtcNow(odd);

        await factory.WriteManyAsync(3);
        factory.Clock.Advance(TimeSpan.FromTicks(7));
        await factory.WriteManyAsync(1, "later");

        var entries = await factory.AuditEntriesAsync();
        Assert.Equal(AuditChainFormat.TruncateToMicroseconds(odd), entries[0].OccurredAt); // what Postgres kept
        Assert.Equal(0, entries[0].OccurredAt.Ticks % 10);
        Assert.True((await factory.VerifyAuditAsync()).Intact);
    }

    [Fact]
    public async Task Entry_times_come_from_the_clock_the_application_was_given()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var moment = new DateTimeOffset(DateTime.UtcNow.Year + 1, 6, 1, 12, 30, 15, TimeSpan.Zero);
        factory.Clock.SetUtcNow(moment);

        await factory.WriteManyAsync(1);

        Assert.Equal(moment, Assert.Single(await factory.AuditEntriesAsync()).OccurredAt);
    }

    [Fact]
    public async Task Key_rotation_old_entries_still_verify_when_the_old_key_is_kept_as_previous()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.WriteManyAsync(3, "old");

        await using var rotated = factory.CreateSibling(settings =>
        {
            settings["Audit:CurrentKeyId"] = ApiFactory.SecondKeyId;
            settings["Audit:PreviousKeyId"] = ApiFactory.TestKeyId;
            settings[$"Audit:Keys:{ApiFactory.SecondKeyId}"] = ApiFactory.SecondKeyBase64;
        });
        await rotated.WriteManyAsync(2, "new");

        var entries = await rotated.AuditEntriesAsync();
        Assert.Equal(["k1", "k1", "k1", "k2", "k2"], entries.Select(entry => entry.KeyId));
        var result = await rotated.VerifyAuditAsync();
        Assert.True(result.Intact);
        Assert.Equal(5, result.EntriesChecked);
    }

    [Fact]
    public async Task Once_a_key_is_no_longer_current_or_previous_entries_signed_with_it_no_longer_verify()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.WriteManyAsync(3, "old");

        await using var dropped = factory.CreateSibling(settings =>
        {
            settings["Audit:CurrentKeyId"] = ApiFactory.SecondKeyId;
            settings.Remove("Audit:PreviousKeyId");
            settings.Remove($"Audit:Keys:{ApiFactory.TestKeyId}");
            settings[$"Audit:Keys:{ApiFactory.SecondKeyId}"] = ApiFactory.SecondKeyBase64;
        });

        var result = await dropped.VerifyAuditAsync();
        Assert.Equal(AuditBreakKind.UnknownKey, result.FirstBreak!.Kind);
        Assert.Equal(1, result.FirstBreak.Sequence);
    }

    [Fact]
    public async Task An_application_that_does_not_know_the_newer_key_reports_the_entries_signed_with_it()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.WriteManyAsync(2, "old");
        await using var rotated = factory.CreateSibling(settings =>
        {
            settings["Audit:CurrentKeyId"] = ApiFactory.SecondKeyId;
            settings["Audit:PreviousKeyId"] = ApiFactory.TestKeyId;
            settings[$"Audit:Keys:{ApiFactory.SecondKeyId}"] = ApiFactory.SecondKeyBase64;
        });
        await rotated.WriteManyAsync(1, "new");

        var result = await factory.VerifyAuditAsync(); // still configured with only k1

        Assert.Equal(AuditBreakKind.UnknownKey, result.FirstBreak!.Kind);
        Assert.Equal(3, result.FirstBreak.Sequence);
    }

    [Fact]
    public async Task Secrets_in_before_and_after_never_reach_the_database()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);

        await factory.WriteAuditAsync(new AuditEntry(
            AuditAction.RecordViewed,
            null,
            Subject: "item",
            TargetType: "Probe",
            Before: new { state = "old", password = "hunter2-old", nrc = "123456/10/1" },
            After: new { state = "new", setupToken = "abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG", twoFactorCode = "123456", note = "NRC 987654/32/1" }));

        var all = string.Join('|', await factory.WithDbAsync(db => db.AuditLog.AsNoTracking().Select(e => e.BeforeJson + "|" + e.AfterJson + "|" + e.Detail).ToListAsync()));
        foreach (var secret in new[] { "hunter2", "123456/10/1", "abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG", "987654/32/1" })
        {
            Assert.DoesNotContain(secret, all, StringComparison.Ordinal);
        }

        Assert.Contains("\"state\":\"new\"", all, StringComparison.Ordinal);
        Assert.Contains("[redacted]", all, StringComparison.Ordinal);
        Assert.True((await factory.VerifyAuditAsync()).Intact);
    }

    [Fact]
    public async Task Before_and_after_are_capped_in_size_and_the_chain_still_verifies()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);

        await factory.WriteAuditAsync(new AuditEntry(AuditAction.RecordViewed, null, Subject: "big", TargetType: "Probe", After: new { notes = string.Concat(Enumerable.Repeat("lorem ipsum ", 2000)) }));

        var entry = Assert.Single(await factory.AuditEntriesAsync());
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(entry.AfterJson!) <= AuditRedactor.MaxBytes);
        Assert.Contains("\"truncated\":true", entry.AfterJson, StringComparison.Ordinal);
        Assert.True((await factory.VerifyAuditAsync()).Intact);
    }

    // --- Who, where, which request --------------------------------------------------------------------------------

    [Fact]
    public async Task An_entry_records_the_actor_and_their_role_the_agency_the_trace_id_and_the_address()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var agency = await factory.CreateAgencyAsync("Copperbelt Homes");
        var admins = await factory.CreateAdminsAsync(2);
        var member = await factory.CreateMemberAsync("member@example.test", agency, [Role.RegisteredAgent]);
        var client = factory.CreateApiClient();
        Assert.Equal(HttpStatusCode.OK, (await client.SignInWithTwoFactorAsync(factory, admins[0])).StatusCode);

        var response = await client.SendAsync(HttpMethod.Post, $"/api/admin/users/{member.Id}/suspend", new { reason = Proposals.Reason }, forwardedFor: "203.0.113.77");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var entry = Assert.Single(await factory.AuditEntriesAsync(), record => record.Action == "UserSuspended");
        Assert.Equal("user", entry.ActorType);
        Assert.Equal(admins[0].Id, entry.ActorUserId);
        Assert.Equal("Admin", entry.ActorRole);
        Assert.Equal("User", entry.TargetType);
        Assert.Equal(member.Id.ToString(), entry.Subject);
        Assert.Equal(agency.Id, entry.AgencyId); // the member's agency, so its compliance officer can see it
        Assert.Equal("203.0.113.77", entry.IpAddress);
        Assert.False(string.IsNullOrWhiteSpace(entry.CorrelationId)); // the request's trace id
        Assert.Contains("Approved", entry.BeforeJson, StringComparison.Ordinal);
        Assert.Contains("Suspended", entry.AfterJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Work_that_no_one_signed_in_did_is_recorded_as_the_system()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);

        await factory.WriteManyAsync(1);

        var entry = Assert.Single(await factory.AuditEntriesAsync());
        Assert.Equal("system", entry.ActorType);
        Assert.Null(entry.ActorUserId);
        Assert.Equal("system", entry.ActorRole);
    }

    [Fact]
    public async Task One_request_with_several_entries_gives_them_all_the_same_trace_id()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var admins = await factory.CreateAdminsAsync(2);
        var client = factory.CreateApiClient();
        await client.SignInWithTwoFactorAsync(factory, admins[0]);
        var staff = await factory.CreateUserAsync("staff@example.test", staff: true);
        var proposal = await factory.ProposeOkAsync(admins[0], Proposals.AssignRole(staff, Role.CaseReviewer));
        var checker = factory.CreateApiClient();
        await checker.SignInWithTwoFactorAsync(factory, admins[1]);

        var response = await checker.SendAsync(HttpMethod.Post, $"/api/admin/pending-actions/{proposal.Id}/confirm", new { payloadHash = proposal.PayloadHash, twoFactorCode = factory.NextTotpCode(admins[1]) });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var confirmEntries = (await factory.AuditEntriesAsync()).Where(entry => entry.Action is "ActionConfirmed" or "RolesChanged" && entry.ActorUserId == admins[1].Id).ToList();
        Assert.Equal(2, confirmEntries.Count);
        Assert.Single(confirmEntries.Select(entry => entry.CorrelationId).Distinct());
        Assert.NotNull(confirmEntries[0].CorrelationId);
    }

    // --- The rows from before the chain -------------------------------------------------------------------------

    [Fact]
    public async Task The_old_temporary_rows_become_the_start_of_the_chain_in_order()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync("old@example.test");
        await factory.OwnerSqlAsync(
            $"""
            INSERT INTO audit_log_legacy (id, action, actor_user_id, subject, ip_address, detail, created_at, updated_at) VALUES
              (gen_random_uuid(), 'SignInFailed', NULL, 'email-sha256:aaaa', '203.0.113.1', 'wrong password', '2026-01-03T10:00:00Z', now()),
              (gen_random_uuid(), 'SignInSucceeded', '{user.Id}', '{user.Id}', '203.0.113.2', 'second factor', '2026-01-01T10:00:00Z', now()),
              (gen_random_uuid(), 'PasswordChanged', '{user.Id}', NULL, NULL, NULL, '2026-01-02T10:00:00Z', now())
            """);

        await factory.WriteManyAsync(1, "first-new");

        var entries = await factory.AuditEntriesAsync();
        Assert.Equal(4, entries.Count);
        Assert.Equal(["SignInSucceeded", "PasswordChanged", "SignInFailed", "RecordViewed"], entries.Select(entry => entry.Action)); // by their old times, then the new one
        Assert.Equal([1L, 2, 3, 4], entries.Select(entry => entry.Sequence));
        Assert.Equal(AuditChainFormat.GenesisHash, entries[0].PreviousHash);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero), entries[0].OccurredAt); // their own times, not the day they were imported
        Assert.Equal("user", entries[0].ActorType);
        Assert.Equal("system", entries[2].ActorType);
        Assert.Equal("203.0.113.1", entries[2].IpAddress);
        Assert.Equal("wrong password", entries[2].Detail);
        Assert.True((await factory.VerifyAuditAsync()).Intact);
        Assert.True((await factory.AuditHeadAsync()).LegacyImported);
    }

    [Fact]
    public async Task The_old_rows_are_brought_in_once_and_left_where_they_were()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.OwnerSqlAsync(
            "INSERT INTO audit_log_legacy (id, action, subject, created_at, updated_at) VALUES (gen_random_uuid(), 'SignInFailed', 'x', '2026-01-01T10:00:00Z', now()), (gen_random_uuid(), 'SignInFailed', 'y', '2026-01-02T10:00:00Z', now())");

        await factory.WriteManyAsync(2);
        await factory.WriteManyAsync(2);

        Assert.Equal(6, (await factory.AuditEntriesAsync()).Count); // 2 old + 4 new, not 2 + 2 + 2 + 4
        Assert.Equal(2, await factory.OwnerScalarAsync("SELECT count(*) FROM audit_log_legacy"));
        Assert.True((await factory.VerifyAuditAsync()).Intact);
    }

    [Fact]
    public async Task Verifying_brings_the_old_rows_in_too_if_nothing_has_been_written_yet()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.OwnerSqlAsync("INSERT INTO audit_log_legacy (id, action, subject, created_at, updated_at) VALUES (gen_random_uuid(), 'SignInFailed', 'x', '2026-01-01T10:00:00Z', now())");

        var result = await factory.VerifyAuditAsync();

        Assert.True(result.Intact);
        Assert.Equal(1, result.EntriesChecked);
        Assert.Equal(1, result.HeadSequence);
    }
}

internal static class OwnerQueries
{
    public static async Task<long> OwnerScalarAsync(this ApiFactory factory, string sql) =>
        await PostgresFixture.ScalarAsync<long>(factory.OwnerConnectionString!, sql);
}
