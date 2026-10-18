using System.Net;
using CaseRegister.Application.Auditing;
using CaseRegister.Application.Auth;
using CaseRegister.Infrastructure;
using CaseRegister.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaseRegister.Tests.Security;

/// <summary>When Have I Been Pwned cannot be reached, the bundled list of the 100,000 most used breached passwords is the safety net, and says so in the audit log.</summary>
public sealed class BreachFallbackTests
{
    private sealed class RecordingAudit : IAuditLog
    {
        public List<AuditEntry> Entries { get; } = [];

        public Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);

            return Task.CompletedTask;
        }
    }

    private static ResilientBreachedPasswordChecker Checker(Func<HttpRequestMessage, HttpResponseMessage> respond, IAuditLog audit)
    {
        var http = new HttpClient(new StubHandler(respond)) { BaseAddress = new Uri(HaveIBeenPwnedClient.BaseAddress) };

        return new ResilientBreachedPasswordChecker(new HaveIBeenPwnedClient(http, NullLogger<HaveIBeenPwnedClient>.Instance), audit, NullLogger<ResilientBreachedPasswordChecker>.Instance);
    }

    private static HttpResponseMessage Down(HttpRequestMessage _) => new(HttpStatusCode.ServiceUnavailable);

    private static HttpResponseMessage Throws(HttpRequestMessage _) => throw new HttpRequestException("The connection was refused.");

    // A password of 12 or more characters that is in the list, so the 12-character minimum would not already have refused it.
    private const string InTheList = "qwerty123456";

    [Fact]
    public void The_bundled_list_has_close_to_a_hundred_thousand_passwords_and_the_usual_suspects()
    {
        Assert.InRange(BundledBreachList.Count, 99_000, 100_000);
        Assert.True(BundledBreachList.Contains("123456"));
        Assert.True(BundledBreachList.Contains(InTheList));
        Assert.True(BundledBreachList.Contains("1qaz2wsx3edc"));
        Assert.False(BundledBreachList.Contains("this passphrase is nowhere near any list 7731"));
        Assert.False(BundledBreachList.Contains("qWeRtY123456"), "matched exactly as typed, not case-folded");
        Assert.False(BundledBreachList.Contains(string.Empty));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task With_Have_I_Been_Pwned_down_a_strong_password_goes_through_and_the_fallback_is_written_down(bool throws)
    {
        var audit = new RecordingAudit();

        var result = await Checker(throws ? Throws : Down, audit).CheckAsync("this passphrase is nowhere near any list 7731");

        Assert.Equal(BreachCheckResult.NotBreached, result);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditAction.PasswordCheckFallbackUsed, entry.Action);
        Assert.Contains("is not in it", entry.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_Have_I_Been_Pwned_down_a_password_in_the_list_is_refused_and_the_fallback_is_written_down()
    {
        var audit = new RecordingAudit();

        var result = await Checker(Down, audit).CheckAsync(InTheList);

        Assert.Equal(BreachCheckResult.Breached, result);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditAction.PasswordCheckFallbackUsed, entry.Action);
        Assert.Contains("is in it", entry.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_audit_entry_never_holds_the_password_or_anything_that_identifies_a_person()
    {
        var audit = new RecordingAudit();

        await Checker(Down, audit).CheckAsync(InTheList);

        var entry = Assert.Single(audit.Entries);
        var everything = $"{entry.Detail}|{entry.Subject}|{entry.IpAddress}|{entry.Before}|{entry.After}";
        Assert.DoesNotContain(InTheList, everything, StringComparison.Ordinal);
        Assert.DoesNotContain("@", everything, StringComparison.Ordinal);
        Assert.Null(entry.ActorUserId);
        Assert.Null(entry.Subject);
    }

    [Fact]
    public async Task While_Have_I_Been_Pwned_works_the_list_is_not_used_and_nothing_is_written()
    {
        var audit = new RecordingAudit();

        // Have I Been Pwned says this password is clean, even though it is in the bundled list: its answer wins.
        var clean = await Checker(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("0018A45C4D1DEF81644B54AB7F969B88D65:1") }, audit).CheckAsync(InTheList);

        Assert.Equal(BreachCheckResult.NotBreached, clean);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task A_failure_to_write_the_note_does_not_change_the_answer()
    {
        var failing = new FailingAudit();

        Assert.Equal(BreachCheckResult.Breached, await Checker(Down, failing).CheckAsync(InTheList));
    }

    private sealed class FailingAudit : IAuditLog
    {
        public Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default) => throw new InvalidOperationException("the audit log is unavailable");
    }

    [Fact]
    public void The_application_uses_the_fallback_checker_with_the_Have_I_Been_Pwned_client_behind_it()
    {
        var services = new ServiceCollection();
        services.AddOptions();
        services.AddLogging();
        DependencyInjection.AddExternalClients(services);

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(IBreachedPasswordChecker));
        Assert.Equal(typeof(ResilientBreachedPasswordChecker), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    [Fact]
    public void The_source_of_the_list_is_credited_under_both_licences()
    {
        var notices = File.ReadAllText(Path.Combine(Architecture.SolutionGraph.SolutionRoot, "THIRD-PARTY-NOTICES.md"));

        Assert.Contains("SecLists", notices, StringComparison.Ordinal);
        Assert.Contains("MIT", notices, StringComparison.Ordinal);
        Assert.Contains("National Cyber Security Centre", notices, StringComparison.Ordinal);
        Assert.Contains("Open Government Licence", notices, StringComparison.Ordinal);
    }
}
