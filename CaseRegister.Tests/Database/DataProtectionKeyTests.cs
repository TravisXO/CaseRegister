using System.Net;
using System.Xml.Linq;
using CaseRegister.Tests.Testing;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace CaseRegister.Tests.Database;

[Collection(PostgresTestGroup.Name)]
public class DataProtectionKeyTests(PostgresFixture postgres)
{
    private const string Email = "member@example.test";

    private static async Task<List<DataProtectionKey>> KeysAsync(ApiFactory factory) =>
        await factory.WithDbAsync(db => db.DataProtectionKeys.AsNoTracking().ToListAsync());

    [Fact]
    public async Task Keys_are_created_in_the_database_when_the_application_starts()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);

        _ = factory.CreateApiClient(); // starting the app initialises the key ring

        var keys = await KeysAsync(factory);
        Assert.NotEmpty(keys);
        Assert.All(keys, key => Assert.False(string.IsNullOrWhiteSpace(key.Xml)));
    }

    [Fact]
    public async Task Keys_are_encrypted_with_the_certificate_and_never_stored_in_the_clear()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.CreateUserAsync(Email);
        await factory.CreateApiClient().SignInAsync(Email);

        var key = (await KeysAsync(factory)).First();

        Assert.Contains("EncryptedData", key.Xml, StringComparison.Ordinal);
        Assert.Contains("X509", key.Xml, StringComparison.Ordinal);

        // The plain-text form of a key carries its secret in a <masterKey> element.
        Assert.DoesNotContain("masterKey", key.Xml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_key_lives_for_90_days()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.CreateUserAsync(Email);
        await factory.CreateApiClient().SignInAsync(Email);

        var key = XElement.Parse((await KeysAsync(factory)).First().Xml!);
        var created = DateTimeOffset.Parse(key.Element("creationDate")!.Value, System.Globalization.CultureInfo.InvariantCulture);
        var expires = DateTimeOffset.Parse(key.Element("expirationDate")!.Value, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(TimeSpan.FromDays(90), expires - created);
    }

    [Fact]
    public async Task A_session_issued_by_one_server_is_valid_on_another_because_the_keys_are_shared()
    {
        await using var first = await ApiFactory.CreateAsync(postgres);
        var user = await first.CreateUserAsync(Email);
        var response = await first.CreateApiClient().SignInWithTwoFactorAsync(first, user);
        var cookieHeader = ApiClient.SetCookieHeader(response, "__Host-cr")!;
        var cookiePair = cookieHeader.Split(';')[0];

        // A second, separate instance of the app on the same database, as a second server would be.
        await using var second = first.CreateSibling();
        using var plain = second.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = false,
        });
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/auth/session", UriKind.Relative));
        request.Headers.Add("Cookie", cookiePair);

        var result = await plain.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
    }

    [Fact]
    public async Task A_made_up_session_cookie_is_refused()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var plain = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = false,
        });
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/auth/session", UriKind.Relative));
        request.Headers.Add("Cookie", "__Host-cr=CfDJ8NotARealCookieAtAll");

        var result = await plain.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, result.StatusCode);
    }

    [Fact]
    public async Task A_new_key_uses_AES_256_GCM()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);

        _ = factory.CreateApiClient();

        var key = Assert.Single(await KeysAsync(factory));
        Assert.Contains("AES_256_GCM", key.Xml, StringComparison.Ordinal);
        Assert.DoesNotContain("AES_256_CBC", key.Xml, StringComparison.Ordinal);
    }
}
