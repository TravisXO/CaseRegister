using System.Collections.Frozen;
using System.IO.Compression;

namespace CaseRegister.Infrastructure.Security;

/// <summary>
/// The 100,000 most used passwords found in breaches, kept inside the application (gzipped, as an embedded resource) for the times Have I Been Pwned
/// cannot be reached. It is the NCSC list "100k-most-used-passwords-NCSC.txt" from the SecLists project (MIT licence), which the UK National
/// Cyber Security Centre published under the Open Government Licence v3.0: see THIRD-PARTY-NOTICES.md. A password is checked against it exactly as typed.
/// It is a safety net for a sign-up while a service is down, not a replacement for the full breach check.
/// </summary>
public static class BundledBreachList
{
    private const string ResourceName = "CaseRegister.Infrastructure.ncsc-100k-passwords.txt.gz";

    private static readonly Lazy<FrozenSet<string>> _passwords = new(Load);

    /// <summary>How many passwords are in the list.</summary>
    public static int Count => _passwords.Value.Count;

    public static bool Contains(string password) => _passwords.Value.Contains(password);

    private static FrozenSet<string> Load()
    {
        using var resource = typeof(BundledBreachList).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The bundled password list is missing from the application.");
        using var gzip = new GZipStream(resource, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, System.Text.Encoding.UTF8);

        HashSet<string> passwords = new(StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length > 0)
            {
                passwords.Add(line);
            }
        }

        return passwords.ToFrozenSet(StringComparer.Ordinal);
    }
}
