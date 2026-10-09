using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace CaseRegister.Tests.Testing;

public sealed class FakeHostEnvironment(string environmentName) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = environmentName;

    public string ApplicationName { get; set; } = "CaseRegister";

    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
