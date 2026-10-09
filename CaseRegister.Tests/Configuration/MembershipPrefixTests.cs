using CaseRegister.Application.Accounts;
using CaseRegister.Tests.Architecture;
using CaseRegister.Tests.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CaseRegister.Tests.Configuration;

/// <summary>The membership number's display prefix is one configuration setting and nothing else.</summary>
public class MembershipPrefixTests
{
    private const string SettingKey = "Membership:DisplayPrefix";

    private static string PrefixIn(ApiFactory factory) =>
        factory.Services.GetRequiredService<IOptions<MembershipOptions>>().Value.DisplayPrefix;

    [Fact]
    public void The_shipped_default_is_set_in_appsettings_json()
    {
        using var factory = ApiFactory.WithoutDatabase(settings => settings.Remove(SettingKey));

        Assert.Equal("ZIEA", PrefixIn(factory));
    }

    [Fact]
    public void The_setting_can_be_changed_without_touching_any_code()
    {
        using var factory = ApiFactory.WithoutDatabase(settings => settings[SettingKey] = "REG");

        Assert.Equal("REG", PrefixIn(factory));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1234")]
    [InlineData("A B")]
    [InlineData("ZIEA!")]
    [InlineData("ABCDEFGHIJK")] // eleven letters: one too many
    public void A_bad_prefix_stops_the_application_starting(string prefix)
    {
        using var factory = ApiFactory.WithoutDatabase(settings => settings[SettingKey] = prefix);

        var failure = Record.Exception(() => factory.CreateApiClient());

        Assert.NotNull(failure);
        Assert.Contains(SettingKey, failure.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ZIEA", true)]
    [InlineData("Z", true)]
    [InlineData("ABCDEFGHIJ", true)] // ten letters is allowed
    [InlineData("ZIEA2", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_one_to_ten_letters_make_a_valid_prefix(string? prefix, bool valid)
    {
        Assert.Equal(valid, MembershipOptions.IsValidPrefix(prefix));
    }

    [Fact]
    public void The_options_class_carries_no_default_so_the_value_lives_in_one_place()
    {
        Assert.Equal(string.Empty, new MembershipOptions().DisplayPrefix);
    }

    [Fact]
    public void The_brand_appears_only_in_appsettings_json_and_never_in_the_code()
    {
        // Generated migrations are the one exception: they are a record of what was applied to a database.
        var sourceFolders = new[] { "CaseRegister.Domain", "CaseRegister.Application", "CaseRegister.Infrastructure", "CaseRegister.Api" };
        var codeFiles = sourceFolders
            .SelectMany(folder => Directory.EnumerateFiles(Path.Combine(SolutionGraph.SolutionRoot, folder), "*.*", SearchOption.AllDirectories))
            .Where(path => path.EndsWith(".cs", StringComparison.Ordinal) || path.EndsWith(".csproj", StringComparison.Ordinal))
            .Where(path => !IsUnder(path, "obj") && !IsUnder(path, "bin") && !IsUnder(path, "Migrations"));

        var offenders = codeFiles
            .Where(path => File.ReadAllText(path).Contains("ziea", StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetRelativePath(SolutionGraph.SolutionRoot, path))
            .ToList();

        Assert.Empty(offenders);

        var appsettings = File.ReadAllText(Path.Combine(SolutionGraph.SolutionRoot, "CaseRegister.Api", "appsettings.json"));
        Assert.Equal(1, appsettings.Split("ZIEA", StringSplitOptions.None).Length - 1);
    }

    private static bool IsUnder(string path, string folderName) =>
        path.Contains($"{Path.DirectorySeparatorChar}{folderName}{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
}
