using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CaseRegister.Application.Auth;
using CaseRegister.Infrastructure.Identity;

namespace CaseRegister.Tests.Testing;

/// <summary>Reusable steps of the two-factor flows, driven through the real HTTP API.</summary>
public static class TwoFactorFlows
{
    public static async Task<string?> StatusAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString();

    public static async Task<List<string>> RecoveryCodesAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        return [.. body.GetProperty("recoveryCodes").EnumerateArray().Select(code => code.GetString()!)];
    }

    /// <summary>Signs in with only a password, for an account that has no two-factor yet: a restricted session.</summary>
    public static async Task<ApiClient> RestrictedSessionAsync(ApiFactory factory, ApplicationUser user)
    {
        var client = factory.CreateApiClient();
        var response = await client.SignInAsync(user.Email!);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("two-factor-enrolment-required", await StatusAsync(response));

        return client;
    }

    public static async Task<(HttpResponseMessage Response, string? SharedKey, string? OtpAuthUri)> StartEnrolmentAsync(ApiClient client)
    {
        var response = await client.SendAsync(HttpMethod.Post, "/api/auth/2fa/enrol");
        if (response.StatusCode != HttpStatusCode.OK)
        {
            return (response, null, null);
        }

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        return (response, body.GetProperty("sharedKey").GetString(), body.GetProperty("otpAuthUri").GetString());
    }

    public static Task<HttpResponseMessage> ConfirmEnrolmentAsync(ApiClient client, string code) =>
        client.SendAsync(HttpMethod.Post, "/api/auth/2fa/enrol/confirm", new { code });

    /// <summary>The whole enrolment: start, take the key as an authenticator app would, confirm with a valid code. Returns the recovery codes.</summary>
    public static async Task<List<string>> EnrolFullyAsync(ApiFactory factory, ApiClient client, ApplicationUser user)
    {
        var (start, sharedKey, _) = await StartEnrolmentAsync(client);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        factory.RememberTwoFactorKey(user, Base32.Decode(sharedKey!));

        var confirm = await ConfirmEnrolmentAsync(client, factory.NextTotpCode(user));
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);

        return await RecoveryCodesAsync(confirm);
    }

    /// <summary>Gets as far as the password step of an account that has two-factor, so a code is the next thing asked for.</summary>
    public static async Task<ApiClient> AtTheCodeStepAsync(ApiFactory factory, ApplicationUser user, string? forwardedFor = null)
    {
        var client = factory.CreateApiClient();
        var response = await client.SignInAsync(user.Email!, forwardedFor: forwardedFor);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("two-factor-required", await StatusAsync(response));

        return client;
    }
}
