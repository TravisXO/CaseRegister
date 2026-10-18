using System.Text.Json;
using CaseRegister.Api.Cli;
using CaseRegister.Application.Auditing;
using CaseRegister.Tests.Database;
using CaseRegister.Tests.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CaseRegister.Tests.Auth;

/// <summary>Anchors (the chain's latest sequence and hash, kept outside the database), and the two commands that use the audit chain.</summary>
[Collection(PostgresTestGroup.Name)]
public class AuditAnchorTests(PostgresFixture postgres)
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private static readonly byte[] _testKey = Convert.FromBase64String(ApiFactory.TestKeyBase64);

    private static IReadOnlyList<AuditAnchor> AnchorsOf(ApiFactory factory) =>
        File.Exists(factory.AnchorFilePath)
            ? [.. File.ReadAllLines(factory.AnchorFilePath).Where(line => line.Length > 0).Select(line => JsonSerializer.Deserialize<AuditAnchor>(line, _json)!)]
            : [];

    private static async Task<AuditAnchor?> AnchorNowAsync(ApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<IAuditAnchoring>().AnchorNowAsync(null);
    }

    /// <summary>A day in the future in Zambian time, so the test clock (which only moves forward) can be set to it.</summary>
    private static DateTimeOffset At(int month, int day, int hour, int minute = 0, int second = 0) =>
        new(DateTime.UtcNow.Year + 1, month, day, hour, minute, second, TimeSpan.Zero);

    // --- When an anchor is written -------------------------------------------------------------------------------

    [Fact]
    public async Task The_first_entry_of_a_new_Zambian_day_is_anchored_once_it_has_committed()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        factory.Clock.SetUtcNow(At(3, 10, 14)); // 16:00 in Zambia on 10 March
        await factory.WriteManyAsync(3, "day-one");

        // The first entry of the day is anchored after its commit; the next two find today already done.
        var afterFirstDay = AnchorsOf(factory);
        Assert.Equal(1, Assert.Single(afterFirstDay).Sequence);

        factory.Clock.SetUtcNow(At(3, 10, 14, 30)); // half an hour later, the same day: nothing is due
        await factory.WriteManyAsync(1, "late");
        Assert.Single(AnchorsOf(factory));

        factory.Clock.SetUtcNow(At(3, 10, 22)); // midnight in Zambia: 11 March
        await factory.WriteManyAsync(1, "day-two");

        var anchors = AnchorsOf(factory);
        var entries = await factory.AuditEntriesAsync();
        Assert.Equal(2, anchors.Count);
        Assert.Equal(5, anchors[1].Sequence); // the chain as it stood once the new day's first entry had committed
        Assert.Equal(entries[4].EntryHash, anchors[1].Hash);
        Assert.Equal($"{At(3, 10, 0).Year}-03-11", anchors[1].ZambianDate);
        Assert.Equal(ApiFactory.TestKeyId, anchors[1].KeyId);
        Assert.Equal(5, entries.Count);
    }

    [Fact]
    public async Task Further_entries_on_the_same_day_write_no_more_anchors()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        factory.Clock.SetUtcNow(At(5, 2, 8));

        await factory.WriteManyAsync(12);

        Assert.Single(AnchorsOf(factory));
    }

    [Fact]
    public async Task Anchors_are_signed_so_an_edited_one_is_noticed()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.WriteManyAsync(4);
        var anchor = Assert.Single(AnchorsOf(factory));

        Assert.Equal(
            AuditChainFormat.AnchorMac(_testKey, anchor.Sequence, anchor.Hash, anchor.KeyId, AuditChainFormat.FormatTime(anchor.CreatedAt), anchor.ZambianDate),
            anchor.Mac);
        Assert.True((await factory.VerifyAuditAsync()).Intact);
    }

    [Fact]
    public async Task A_store_that_cannot_be_written_never_stops_an_action_and_is_logged()
    {
        var blocker = Path.GetTempFileName(); // a file where a folder is needed: the anchor file can never be created
        try
        {
            await using var factory = await ApiFactory.CreateAsync(postgres, anchorFilePath: Path.Combine(blocker, "anchors.jsonl"));

            await factory.WriteManyAsync(4); // does not throw

            var entries = await factory.AuditEntriesAsync();
            Assert.Equal(4, entries.Count(entry => entry.Action == nameof(AuditAction.RecordViewed)));
            Assert.Equal(1, entries.Count(entry => entry.Action == nameof(AuditAction.AnchorDeferred))); // said once, not on every try
            Assert.Contains(factory.Logs.Entries, log => log.Level == LogLevel.Error && log.Message.Contains("audit anchor", StringComparison.Ordinal));
            Assert.True((await factory.VerifyAuditAsync()).Intact);
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    [Fact]
    public async Task With_no_anchor_store_configured_nothing_is_written_and_verification_says_so()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres, anchorFilePath: string.Empty);

        await factory.WriteManyAsync(5);

        var result = await factory.VerifyAuditAsync();
        Assert.True(result.Intact);
        Assert.False(result.AnchorStoreConfigured);
        Assert.Equal(0, result.AnchorsChecked);
        Assert.False(File.Exists(factory.AnchorFilePath));
    }

    // --- Anchors catch what the chain alone cannot -----------------------------------------------------------------

    [Fact]
    public async Task A_chain_cut_back_with_its_head_to_match_is_caught_by_the_anchor()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.WriteManyAsync(8);
        await AnchorNowAsync(factory);
        var anchored = Assert.Single(AnchorsOf(factory).OrderByDescending(a => a.Sequence).Take(1));
        var entries = await factory.AuditEntriesAsync();
        var keep = entries.Single(entry => entry.Sequence == 5);

        // Whoever did this made the table and the head agree with each other, so the chain by itself looks fine.
        await factory.TamperAsync($"DELETE FROM audit_log WHERE sequence > 5; UPDATE audit_chain_head SET last_sequence = 5, last_hash = '{keep.EntryHash}'");

        var result = await factory.VerifyAuditAsync();
        Assert.False(result.Intact);
        Assert.Equal(AuditBreakKind.AnchorMismatch, result.FirstBreak!.Kind);
        Assert.Equal(anchored.Sequence, result.FirstBreak.Sequence);
        Assert.Contains("no such entry", result.FirstBreak.Message, StringComparison.Ordinal);
    }

    /// <summary>What a restored backup looks like: the chain and the head agree, but the bucket holds anchors made after the copy was taken.</summary>
    private async Task<ApiFactory> AnOlderCopyAsync()
    {
        var factory = await ApiFactory.CreateAsync(postgres);
        await factory.WriteManyAsync(4);
        await AnchorNowAsync(factory);
        await factory.WriteManyAsync(4);
        await AnchorNowAsync(factory);
        var keep = (await factory.AuditEntriesAsync()).Single(entry => entry.Sequence == 4);
        await factory.TamperAsync($"DELETE FROM audit_log WHERE sequence > 4; UPDATE audit_chain_head SET last_sequence = 4, last_hash = '{keep.EntryHash}'");

        return factory;
    }

    [Fact]
    public async Task A_restored_copy_is_intact_when_only_anchors_newer_than_the_copy_are_missing_from_it()
    {
        await using var factory = await AnOlderCopyAsync();

        var plain = await factory.VerifyAuditAsync();
        Assert.False(plain.Intact); // checked as the live database, the newer anchor says the tail was cut off

        using var scope = factory.Services.CreateScope();
        var restored = await scope.ServiceProvider.GetRequiredService<IAuditVerifier>().VerifyAsync(false, restoredBackup: true);

        Assert.True(restored.Intact);
        Assert.Equal(4, restored.EntriesChecked);
        Assert.True(restored.NewerAnchors >= 1);
        Assert.True(restored.AnchorsChecked >= 1, "the anchor the copy can answer for is still compared");
    }

    [Fact]
    public async Task A_restored_copy_is_still_checked_against_every_anchor_it_can_answer_for()
    {
        await using var factory = await AnOlderCopyAsync();
        var keep = (await factory.AuditEntriesAsync()).Single(entry => entry.Sequence == 4);
        var plain = new string('b', 64);
        await factory.TamperAsync($"UPDATE audit_log SET entry_hash = '{plain}' WHERE sequence = 4; UPDATE audit_chain_head SET last_hash = '{plain}'");
        Assert.NotEqual(keep.EntryHash, plain);

        using var scope = factory.Services.CreateScope();
        var restored = await scope.ServiceProvider.GetRequiredService<IAuditVerifier>().VerifyAsync(false, restoredBackup: true);

        Assert.False(restored.Intact);
    }

    [Fact]
    public async Task The_verify_command_accepts_restored_backup_and_says_how_many_anchors_it_could_not_compare()
    {
        await using var factory = await AnOlderCopyAsync();

        var (exit, terminal) = await RunAsync(factory, "verify-audit", "--restored-backup");

        Assert.Equal(AuditCommands.Succeeded, exit);
        Assert.Contains(terminal.Output, line => line.Contains("newer than this copy", StringComparison.Ordinal));
        Assert.Contains(terminal.Output, line => line.Contains("The audit chain is intact", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_entry_that_differs_from_what_was_anchored_is_an_anchor_mismatch()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.WriteManyAsync(6);

        // An anchor that is genuine (signed with the key) but of a different hash than the chain has at that number.
        using var scope = factory.Services.CreateScope();
        var key = scope.ServiceProvider.GetRequiredService<IAuditKeyring>().Current;
        var forged = AuditAnchoring_Make(key, 4, new string('e', 64), factory.Clock.GetUtcNow());
        File.AppendAllText(factory.AnchorFilePath, JsonSerializer.Serialize(forged, _json) + "\n");

        var result = await factory.VerifyAuditAsync();

        Assert.Equal(AuditBreakKind.AnchorMismatch, result.FirstBreak!.Kind);
        Assert.Equal(4, result.FirstBreak.Sequence);
        Assert.Contains("does not match the anchor", result.FirstBreak.Message, StringComparison.Ordinal);
    }

    private static AuditAnchor AuditAnchoring_Make(AuditKey key, long sequence, string hash, DateTimeOffset now)
    {
        var createdAt = AuditChainFormat.FormatTime(now);
        var date = AuditChainFormat.ZambianDate(now).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        return new AuditAnchor(sequence, hash, key.Id, AuditChainFormat.TruncateToMicroseconds(now), date, AuditChainFormat.AnchorMac(key.Secret, sequence, hash, key.Id, createdAt, date));
    }

    [Fact]
    public async Task An_anchor_file_that_was_edited_is_reported_as_tampered()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.WriteManyAsync(4);
        var line = File.ReadAllText(factory.AnchorFilePath);
        File.WriteAllText(factory.AnchorFilePath, line.Replace("\"sequence\":1", "\"sequence\":2", StringComparison.Ordinal)); // a number changed, the signature not

        var result = await factory.VerifyAuditAsync();

        Assert.Equal(AuditBreakKind.AnchorTampered, result.FirstBreak!.Kind);
    }

    [Fact]
    public async Task An_anchor_file_that_cannot_be_read_is_reported()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.WriteManyAsync(3);
        File.AppendAllText(factory.AnchorFilePath, "this is not an anchor\n");

        var result = await factory.VerifyAuditAsync();

        Assert.Equal(AuditBreakKind.AnchorsUnreadable, result.FirstBreak!.Kind);
    }

    // --- Verifying from the last good anchor -----------------------------------------------------------------------

    [Fact]
    public async Task Starting_from_the_last_good_anchor_skips_what_it_vouches_for_but_still_checks_everything_after()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.WriteManyAsync(10);
        await AnchorNowAsync(factory);
        var anchor = AnchorsOf(factory).OrderBy(a => a.Sequence).Last();
        await factory.WriteManyAsync(3, "after");
        var headSequence = (await factory.AuditHeadAsync()).LastSequence;

        // A fault in the old part: caught from the start, and not looked at from the anchor.
        await factory.TamperAsync("UPDATE audit_log SET detail = 'tampered' WHERE sequence = 2");
        var full = await factory.VerifyAuditAsync();
        var fromAnchor = await factory.VerifyAuditAsync(fromLastGoodAnchor: true);

        Assert.Equal(AuditBreakKind.BadHash, full.FirstBreak!.Kind);
        Assert.Equal(2, full.FirstBreak.Sequence);
        Assert.True(fromAnchor.Intact);
        Assert.Equal(anchor.Sequence, fromAnchor.FromAnchorSequence);
        Assert.Equal(headSequence - anchor.Sequence, fromAnchor.EntriesChecked);

        // A fault after the anchor: caught either way.
        await factory.TamperAsync($"UPDATE audit_log SET detail = 'tampered' WHERE sequence = {headSequence - 1}");
        var later = await factory.VerifyAuditAsync(fromLastGoodAnchor: true);
        Assert.Equal(headSequence - 1, later.FirstBreak!.Sequence);
    }

    [Fact]
    public async Task With_no_anchor_to_start_from_it_starts_at_the_beginning()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres, anchorFilePath: string.Empty);
        await factory.WriteManyAsync(5);

        var result = await factory.VerifyAuditAsync(fromLastGoodAnchor: true);

        Assert.True(result.Intact);
        Assert.Null(result.FromAnchorSequence);
        Assert.Equal(5, result.EntriesChecked);
    }

    [Fact]
    public async Task A_bad_newest_anchor_is_skipped_for_starting_but_still_reported()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.WriteManyAsync(9);
        await AnchorNowAsync(factory);
        var good = AnchorsOf(factory).OrderBy(a => a.Sequence).Last();
        await factory.WriteManyAsync(4, "more");
        using var scope = factory.Services.CreateScope();
        var forged = AuditAnchoring_Make(scope.ServiceProvider.GetRequiredService<IAuditKeyring>().Current, good.Sequence + 3, new string('d', 64), factory.Clock.GetUtcNow());
        File.AppendAllText(factory.AnchorFilePath, JsonSerializer.Serialize(forged, _json) + "\n");

        var result = await factory.VerifyAuditAsync(fromLastGoodAnchor: true);

        Assert.False(result.Intact);
        Assert.Equal(good.Sequence, result.FromAnchorSequence); // started from the newest anchor that was still good
        Assert.Equal(AuditBreakKind.AnchorMismatch, result.FirstBreak!.Kind);
        Assert.Equal(good.Sequence + 3, result.FirstBreak.Sequence);
    }

    // --- The anchor-audit and verify-audit commands -----------------------------------------------------------------

    private static async Task<(int ExitCode, ScriptedTerminal Terminal)> RunAsync(ApiFactory factory, params string[] args)
    {
        var terminal = new ScriptedTerminal([], []);
        var exitCode = await AuditCommands.RunAsync(args, factory.Services, terminal);

        return (exitCode, terminal);
    }

    [Fact]
    public async Task The_anchor_command_writes_an_anchor_of_the_chain_as_it_stands_and_logs_that_it_did()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.WriteManyAsync(5);
        var before = AnchorsOf(factory).Count;

        var (exit, terminal) = await RunAsync(factory, "anchor-audit");

        Assert.Equal(AuditCommands.Succeeded, exit);
        var anchor = AnchorsOf(factory)[^1];
        Assert.Equal(before + 1, AnchorsOf(factory).Count);
        Assert.Equal(5, anchor.Sequence);
        Assert.Equal((await factory.AuditEntriesAsync())[4].EntryHash, anchor.Hash);
        Assert.Contains(terminal.Output, line => line.StartsWith("Anchored sequence 5", StringComparison.Ordinal));
        var logged = Assert.Single(await factory.AuditEntriesAsync(), entry => entry.Action == "AuditAnchored");
        Assert.Equal("system", logged.ActorType);
        Assert.Equal("5", logged.Subject);
    }

    [Fact]
    public async Task The_anchor_command_has_nothing_to_do_on_an_empty_log_or_without_a_store()
    {
        await using var empty = await ApiFactory.CreateAsync(postgres);
        await using var noStore = await ApiFactory.CreateAsync(postgres, anchorFilePath: string.Empty);
        await noStore.WriteManyAsync(2);

        var (emptyExit, emptyTerminal) = await RunAsync(empty, "anchor-audit");
        var (noStoreExit, _) = await RunAsync(noStore, "anchor-audit");

        Assert.Equal(AuditCommands.Failed, emptyExit);
        Assert.Contains(emptyTerminal.Output, line => line.Contains("Nothing was anchored", StringComparison.Ordinal));
        Assert.Equal(AuditCommands.Failed, noStoreExit);
        Assert.Empty(AnchorsOf(empty));
    }

    [Fact]
    public async Task The_verify_command_reports_an_intact_chain_and_records_the_check()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.WriteManyAsync(5);

        var (exit, terminal) = await RunAsync(factory, "verify-audit");

        Assert.Equal(AuditCommands.Succeeded, exit);
        Assert.Contains(terminal.Output, line => line.Contains("The audit chain is intact", StringComparison.Ordinal));
        var logged = Assert.Single(await factory.AuditEntriesAsync(), entry => entry.Action == "AuditVerified");
        Assert.Equal("system", logged.ActorType);
        Assert.Contains("intact", logged.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_verify_command_names_the_first_broken_link_and_exits_with_a_failure()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.WriteManyAsync(6);
        await factory.TamperAsync("UPDATE audit_log SET detail = 'tampered' WHERE sequence = 3");

        var (exit, terminal) = await RunAsync(factory, "verify-audit");

        Assert.Equal(AuditCommands.Failed, exit);
        Assert.Contains(terminal.Output, line => line.StartsWith("BROKEN at sequence 3: BadHash", StringComparison.Ordinal));
        var logged = Assert.Single(await factory.AuditEntriesAsync(), entry => entry.Action == "AuditVerified");
        Assert.Contains("BROKEN", logged.Detail, StringComparison.Ordinal); // and the check is recorded even when it finds a break
    }

    [Fact]
    public async Task The_verify_command_can_start_from_the_last_good_anchor()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.WriteManyAsync(6);
        await AnchorNowAsync(factory);
        await factory.WriteManyAsync(2);

        var (exit, terminal) = await RunAsync(factory, "verify-audit", "--from-anchor");

        Assert.Equal(AuditCommands.Succeeded, exit);
        Assert.Contains(terminal.Output, line => line.Contains("starting after the anchor at 6", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("verify-audit", "--password", "x")]
    [InlineData("verify-audit", "--everything")]
    [InlineData("anchor-audit", "--from-anchor")]
    public async Task The_commands_take_only_the_options_they_document(params string[] args)
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.WriteManyAsync(2);

        var (exit, terminal) = await RunAsync(factory, args);

        Assert.Equal(AuditCommands.Misused, exit);
        Assert.Contains(terminal.Output, line => line.StartsWith("Usage:", StringComparison.Ordinal));
        Assert.DoesNotContain(await factory.AuditEntriesAsync(), entry => entry.Action is "AuditVerified" or "AuditAnchored");
    }

    [Fact]
    public void The_commands_are_recognised_by_name_only()
    {
        Assert.True(AuditCommands.Handles("verify-audit"));
        Assert.True(AuditCommands.Handles("anchor-audit"));
        Assert.False(AuditCommands.Handles("seed-admin"));
        Assert.False(AuditCommands.Handles(null));
    }
}
