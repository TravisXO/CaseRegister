using System.Collections.Concurrent;
using CaseRegister.Application.Auth;
using CaseRegister.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaseRegister.Tests.Testing;

/// <summary>A breached-password checker the test controls, so no test touches the network.</summary>
public sealed class FakeBreachedPasswordChecker : IBreachedPasswordChecker
{
    private readonly ConcurrentBag<string> _checked = [];

    public HashSet<string> BreachedPasswords { get; } = [];

    public bool Unavailable { get; set; }

    public IReadOnlyCollection<string> CheckedPasswords => _checked;

    public Task<BreachCheckResult> CheckAsync(string password, CancellationToken cancellationToken = default)
    {
        _checked.Add(password);

        if (Unavailable)
        {
            return Task.FromResult(BreachCheckResult.Unavailable);
        }

        return Task.FromResult(BreachedPasswords.Contains(password) ? BreachCheckResult.Breached : BreachCheckResult.NotBreached);
    }
}

/// <summary>Stands in for Cloudflare: only <see cref="ValidToken"/> passes. Records every call.</summary>
public sealed class FakeTurnstileVerifier : ITurnstileVerifier
{
    public const string ValidToken = "valid-turnstile-token";

    private readonly ConcurrentQueue<(string? Token, string? RemoteIp)> _calls = new();

    public IReadOnlyCollection<(string? Token, string? RemoteIp)> Calls => _calls;

    public Task<bool> VerifyAsync(string? token, string? remoteIpAddress, CancellationToken cancellationToken = default)
    {
        _calls.Enqueue((token, remoteIpAddress));

        return Task.FromResult(token == ValidToken);
    }
}

/// <summary>The real password hasher, plus a count of password verifications (each one is the slow part).</summary>
public sealed class CountingPasswordHasher(IOptions<PasswordHasherOptions> options, VerificationCounter counter)
    : IPasswordHasher<ApplicationUser>
{
    private readonly PasswordHasher<ApplicationUser> _inner = new(options);

    public string HashPassword(ApplicationUser user, string password)
    {
        counter.IncrementHash();

        return _inner.HashPassword(user, password);
    }

    public PasswordVerificationResult VerifyHashedPassword(ApplicationUser user, string hashedPassword, string providedPassword)
    {
        counter.Increment();

        return _inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
    }
}

public sealed class VerificationCounter
{
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public void Increment() => Interlocked.Increment(ref _count);

    private int _hashes;

    /// <summary>Passwords hashed (also the slow part). Sign-up does exactly one on every path.</summary>
    public int Hashes => Volatile.Read(ref _hashes);

    public void IncrementHash() => Interlocked.Increment(ref _hashes);
}

/// <summary>Collects log messages so tests can check that something was logged, and what was not.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<(LogLevel Level, string Category, string Message)> _entries = new();

    public IReadOnlyCollection<(LogLevel Level, string Category, string Message)> Entries => _entries;

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(string category, ConcurrentQueue<(LogLevel, string, string)> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            entries.Enqueue((logLevel, category, formatter(state, exception)));
    }
}
