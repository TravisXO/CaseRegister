using System.Net.Http.Json;
using System.Text.Json;
using CaseRegister.Tests.Testing;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CaseRegister.Tests.Hardening;

/// <summary>One application with no database, shared by the tests that need only requests and responses.</summary>
public sealed class NoDatabaseApp : IDisposable
{
    public NoDatabaseApp() => Factory = ApiFactory.WithoutDatabase();

    public ApiFactory Factory { get; }

    public void Dispose() => Factory.Dispose();
}

internal static class HardeningHelpers
{
    public static HttpClient Https(this ApiFactory factory) => Client(factory, "https://localhost");

    public static HttpClient Http(this ApiFactory factory) => Client(factory, "http://localhost");

    private static HttpClient Client(ApiFactory factory, string baseAddress) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri(baseAddress), AllowAutoRedirect = false, HandleCookies = false });

    public static async Task<HttpResponseMessage> SendAsync(this HttpClient client, HttpMethod method, string path, HttpContent? content = null, params (string Name, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative)) { Content = content };
        foreach (var (name, value) in headers)
        {
            request.Headers.Add(name, value);
        }

        return await client.SendAsync(request);
    }

    public static StringContent Json(string text, string contentType = "application/json") => new(text, System.Text.Encoding.UTF8, contentType);

    public static async Task<JsonElement> ProblemAsync(this HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
