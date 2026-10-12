using System.Diagnostics;
using System.Net;
using CaseRegister.Api.Cli;
using CaseRegister.Infrastructure.Health;
using CaseRegister.Tests.Database;
using CaseRegister.Tests.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace CaseRegister.Tests.Hardening;

[Collection(PostgresTestGroup.Name)]
public sealed class HealthCheckTests(PostgresFixture postgres, NoDatabaseApp noDatabase) : IClassFixture<NoDatabaseApp>
{
    private static async Task AssertStatusWordAsync(HttpResponseMessage response, HttpStatusCode status, string word)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);

        // The status word and nothing else: no JSON, no list of checks, no durations, no exception text.
        Assert.Equal(word, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Live_answers_healthy_to_anyone_even_when_the_database_cannot_be_reached()
    {
        using var client = noDatabase.Factory.Https();
        using var warmUp = await client.SendAsync(HttpMethod.Get, "/health/live");

        var timer = Stopwatch.StartNew();
        using var response = await client.SendAsync(HttpMethod.Get, "/health/live");
        timer.Stop();

        await AssertStatusWordAsync(response, HttpStatusCode.OK, "Healthy");
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2), "live must not wait for the database");
    }

    [Fact]
    public async Task Ready_says_unhealthy_with_a_503_when_the_database_cannot_be_reached_and_nothing_more()
    {
        using var client = noDatabase.Factory.Https();

        using var response = await client.SendAsync(HttpMethod.Get, "/health/ready");

        await AssertStatusWordAsync(response, HttpStatusCode.ServiceUnavailable, "Unhealthy");
    }

    [Fact]
    public async Task Ready_says_healthy_when_the_database_answers()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = factory.Https();

        using var response = await client.SendAsync(HttpMethod.Get, "/health/ready");

        await AssertStatusWordAsync(response, HttpStatusCode.OK, "Healthy");
    }

    [Fact]
    public async Task Both_are_anonymous_never_counted_by_the_rate_limit_and_carry_the_security_headers()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres, globalPerIpPerMinute: 1);
        using var client = factory.Https();

        for (var attempt = 0; attempt < 6; attempt++)
        {
            using var live = await client.SendAsync(HttpMethod.Get, attempt % 2 == 0 ? "/health/live" : "/health/ready");
            Assert.Equal(HttpStatusCode.OK, live.StatusCode);
            Assert.Equal("no-store", Assert.Single(live.Headers.GetValues("Cache-Control")));
            Assert.Equal("nosniff", Assert.Single(live.Headers.GetValues("X-Content-Type-Options")));
        }
    }

    [Fact]
    public async Task Health_checks_over_plain_http_are_answered_not_redirected()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = factory.Http();

        using var live = await client.SendAsync(HttpMethod.Get, "/health/live");
        using var ready = await client.SendAsync(HttpMethod.Get, "/health/ready");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    [Fact]
    public async Task Only_the_database_check_is_registered_and_only_ready_runs_it()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);

        var registration = Assert.Single(factory.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations);

        Assert.Equal(HealthChecksSetup.DatabaseCheckName, registration.Name);
        Assert.Equal([HealthChecksSetup.ReadyTag], registration.Tags);
        Assert.Equal(HealthStatus.Unhealthy, registration.FailureStatus);
    }

    // --- The cache --------------------------------------------------------------------------------------------

    private sealed class CountingCheck(params HealthCheckResult[] results) : IHealthCheck
    {
        private int _calls;

        public int Calls => _calls;

        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            var index = Interlocked.Increment(ref _calls) - 1;

            return Task.FromResult(results[Math.Min(index, results.Length - 1)]);
        }
    }

    [Fact]
    public async Task An_answer_is_reused_for_five_seconds_and_then_asked_for_again()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var inner = new CountingCheck(HealthCheckResult.Healthy(), HealthCheckResult.Unhealthy());
        using var cached = new CachedHealthCheck(inner, clock, HealthChecksSetup.ReadyCacheLifetime);
        var context = new HealthCheckContext();

        Assert.Equal(HealthStatus.Healthy, (await cached.CheckHealthAsync(context)).Status);
        clock.Advance(TimeSpan.FromSeconds(4.9));
        Assert.Equal(HealthStatus.Healthy, (await cached.CheckHealthAsync(context)).Status);
        Assert.Equal(1, inner.Calls);

        clock.Advance(TimeSpan.FromSeconds(0.2));
        Assert.Equal(HealthStatus.Unhealthy, (await cached.CheckHealthAsync(context)).Status);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task A_burst_of_calls_asks_the_database_once()
    {
        var inner = new CountingCheck(HealthCheckResult.Healthy());
        using var cached = new CachedHealthCheck(inner, new FakeTimeProvider(DateTimeOffset.UtcNow), HealthChecksSetup.ReadyCacheLifetime);
        var context = new HealthCheckContext();

        var results = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => cached.CheckHealthAsync(context)));

        Assert.All(results, result => Assert.Equal(HealthStatus.Healthy, result.Status));
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public void The_cache_lasts_five_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), HealthChecksSetup.ReadyCacheLifetime);
    }

    // --- The container's own check ----------------------------------------------------------------------------

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);

            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Text(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body) };

    [Theory]
    [InlineData(null, "http://127.0.0.1:8080/health/live")]
    [InlineData("", "http://127.0.0.1:8080/health/live")]
    [InlineData("10000", "http://127.0.0.1:10000/health/live")]
    public async Task The_container_check_asks_live_on_the_configured_port_and_exits_zero_when_healthy(string? port, string expectedUrl)
    {
        var handler = new RecordingHandler(_ => Text("Healthy"));

        var exitCode = await HealthCheckCommand.RunAsync(() => port, handler);

        Assert.Equal(0, exitCode);
        Assert.Equal(expectedUrl, Assert.Single(handler.Requests).ToString());
    }

    [Fact]
    public async Task The_container_check_exits_one_when_the_answer_is_not_healthy()
    {
        Assert.Equal(1, await HealthCheckCommand.RunAsync(() => "8080", new RecordingHandler(_ => Text("Unhealthy", HttpStatusCode.ServiceUnavailable))));
        Assert.Equal(1, await HealthCheckCommand.RunAsync(() => "8080", new RecordingHandler(_ => Text("Something else"))));
        Assert.Equal(1, await HealthCheckCommand.RunAsync(() => "8080", new RecordingHandler(_ => throw new HttpRequestException("refused"))));
    }

    [Fact]
    public async Task The_container_check_exits_one_for_a_port_that_is_not_a_number()
    {
        var handler = new RecordingHandler(_ => Text("Healthy"));

        Assert.Equal(1, await HealthCheckCommand.RunAsync(() => "not-a-port", handler));
        Assert.Empty(handler.Requests);
    }
}
