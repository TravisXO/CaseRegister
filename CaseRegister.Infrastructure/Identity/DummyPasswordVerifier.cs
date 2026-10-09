using Microsoft.AspNetCore.Identity;

namespace CaseRegister.Infrastructure.Identity;

/// <summary>
/// Does the same slow password-hash work as a real check, against a throwaway hash. Sign-in runs it
/// whenever it would otherwise skip a real check (unknown email, locked, not approved), so those cases
/// take as long as a wrong password and the response time reveals nothing about the account.
/// </summary>
internal sealed class DummyPasswordVerifier(IPasswordHasher<ApplicationUser> hasher)
{
    // Built once, with the configured hasher, so it costs exactly what a real verification costs.
    private static readonly object _lock = new();
    private static string? _dummyHash;

    public void Verify(string password)
    {
        var user = new ApplicationUser();
        hasher.VerifyHashedPassword(user, DummyHash(user), password);
    }

    private string DummyHash(ApplicationUser user)
    {
        if (_dummyHash is not null)
        {
            return _dummyHash;
        }

        lock (_lock)
        {
            return _dummyHash ??= hasher.HashPassword(user, Guid.NewGuid().ToString("N"));
        }
    }
}
