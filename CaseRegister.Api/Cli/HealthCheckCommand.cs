using System.Globalization;
using CaseRegister.Api.Hardening;

namespace CaseRegister.Api.Cli;

/// <summary>
/// "dotnet CaseRegister.Api.dll healthcheck": asks the running app whether it is alive, for the container's HEALTHCHECK. The
/// runtime image has no shell, curl or wget, so the check is the app itself. Exit 0 if it answers "Healthy", 1 if not.
/// </summary>
public static class HealthCheckCommand
{
    public const string Name = "healthcheck";
    public const int DefaultPort = 8080;
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(4);

    public static async Task<int> RunAsync(Func<string?>? readPort = null, HttpMessageHandler? handler = null)
    {
        var port = (readPort ?? (() => Environment.GetEnvironmentVariable(HardeningExtensions.PortVariable)))();
        var number = DefaultPort;
        if (!string.IsNullOrEmpty(port) && !int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out number))
        {
            return 1;
        }

        using var client = handler is null ? new HttpClient { Timeout = _timeout } : new HttpClient(handler, disposeHandler: false) { Timeout = _timeout };
        try
        {
            var body = await client.GetStringAsync(new Uri($"http://127.0.0.1:{number.ToString(CultureInfo.InvariantCulture)}{HardeningExtensions.LivePath}"));

            return body == "Healthy" ? 0 : 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return 1;
        }
    }
}
