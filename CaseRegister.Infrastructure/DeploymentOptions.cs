namespace CaseRegister.Infrastructure;

/// <summary>
/// What kind of deployment this is. For now the only one allowed is <see cref="Demo"/>: all data is fake, and the application will not start
/// as anything else. That keeps real personal data out until a decision has been made about where in Zambia (or under what arrangement) it is
/// hosted. The setting has no default, so it has to be chosen on purpose for every environment.
/// </summary>
public sealed class DeploymentOptions
{
    public const string ModeKey = "Deployment:Mode";

    public const string Demo = "Demo";

    public string? Mode { get; set; }

    public static bool IsValid(string? mode) => string.Equals(mode, Demo, StringComparison.Ordinal);

    public const string Problem =
        $"{ModeKey} must be set to \"{Demo}\", which is the only mode allowed for now. Set it with the Deployment__Mode environment variable "
        + "(or 'dotnet user-secrets' locally). It has no default on purpose.";
}
