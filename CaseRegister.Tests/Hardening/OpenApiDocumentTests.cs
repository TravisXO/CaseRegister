using CaseRegister.Tests.Architecture;
using CaseRegister.Tests.Testing;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace CaseRegister.Tests.Hardening;

/// <summary>
/// The front end's types are generated from frontend/openapi.json. This test makes that file the API's own description, so the two
/// cannot drift apart: change an endpoint without regenerating the file and this fails, and says how to fix it.
/// </summary>
public sealed class OpenApiDocumentTests
{
    private static string FilePath => Path.Combine(SolutionGraph.SolutionRoot, "frontend", "openapi.json");

    private static async Task<string> CurrentDocumentAsync()
    {
        using var factory = ApiFactory.WithoutDatabase();
        var provider = factory.Services.GetRequiredKeyedService<IOpenApiDocumentProvider>("v1");
        var document = await provider.GetOpenApiDocumentAsync();

        await using var writer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        document.SerializeAsV3(new OpenApiJsonWriter(writer));

        return writer.ToString().Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd() + "\n";
    }

    [Fact]
    public async Task The_committed_api_description_is_what_the_api_describes_itself_as()
    {
        var current = await CurrentDocumentAsync();

        if (Environment.GetEnvironmentVariable("UPDATE_OPENAPI") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            await File.WriteAllTextAsync(FilePath, current);
        }

        Assert.True(File.Exists(FilePath), "frontend/openapi.json is missing. Create it with: UPDATE_OPENAPI=1 dotnet test --filter OpenApiDocumentTests, then run npm run api:types in frontend.");
        var committed = (await File.ReadAllTextAsync(FilePath)).Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.True(
            current == committed,
            "The API's description has changed. Regenerate frontend/openapi.json with: UPDATE_OPENAPI=1 dotnet test --filter OpenApiDocumentTests, then run npm run api:types in frontend, and commit both.");
    }

    [Fact]
    public async Task Every_endpoint_the_front_end_calls_says_what_it_answers_with()
    {
        var current = await CurrentDocumentAsync();

        foreach (var path in new[]
        {
            "/api/auth/csrf", "/api/auth/sign-in", "/api/auth/2fa/verify", "/api/auth/session", "/api/auth/2fa/enrol", "/api/auth/2fa/enrol/confirm",
            "/api/auth/sessions", "/api/auth/sign-up", "/api/auth/staff-setup/start", "/api/auth/staff-setup/complete", "/api/me",
            "/api/me/practising-certificates", "/api/admin/pending-actions", "/api/admin/applications", "/api/audit/entries", "/api/audit/verify",
        })
        {
            Assert.Contains($"\"{path}\"", current, StringComparison.Ordinal);
        }

        foreach (var schema in new[] { "CsrfResponse", "StatusResponse", "SessionResponse", "EnrolmentResponse", "RecoveryCodesResponse", "ProfileResponse", "PendingActionResponse", "ApplicationView", "AuditPageResponse", "AuditVerification", "SessionInfo", "CertificateInfo" })
        {
            Assert.Contains($"\"{schema}\"", current, StringComparison.Ordinal);
        }
    }
}
