using System.Text;

namespace CaseRegister.Application.Auth;

/// <summary>
/// Where a request came from, as far as the proxy knows: the two-letter country and the city, and nothing finer (never coordinates, never a
/// region or a postcode). It comes from Cloudflare through the Pages Function and is believed only on a request that passed the proxy secret
/// check, like the client address. It is used to show people where they last signed in and to notice a sign-in from somewhere new. It is never
/// compared with an IP address: two addresses in one city are the same place.
/// </summary>
public sealed record ClientLocation(string Country, string? City)
{
    /// <summary>The header the Pages Function sends it in: "ZM|Lusaka", the city percent-encoded.</summary>
    public const string HeaderName = "X-Client-Geo";

    /// <summary>Where the proxy gate leaves it for the rest of the request (HttpContext.Items).</summary>
    public const string ItemKey = "CaseRegister.ClientLocation";

    public const int MaxCityLength = 100;

    /// <summary>"Lusaka, ZM", or "ZM" when the city is not known.</summary>
    public string Display => City is null ? Country : $"{City}, {Country}";

    /// <summary>The same place, whatever the capitals.</summary>
    public bool IsSamePlaceAs(ClientLocation? other) =>
        other is not null
        && string.Equals(Country, other.Country, StringComparison.OrdinalIgnoreCase)
        && string.Equals(City ?? string.Empty, other.City ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    /// <summary>One clean location from the header, or null if it is anything else (a missing country, a country that is not two capital letters, junk).</summary>
    public static ClientLocation? Parse(string? header)
    {
        if (string.IsNullOrWhiteSpace(header) || header.Length > 3 + (MaxCityLength * 9))
        {
            return null;
        }

        var bar = header.IndexOf('|', StringComparison.Ordinal);
        var country = bar < 0 ? header : header[..bar];
        if (country.Length != 2 || !country.All(character => character is >= 'A' and <= 'Z'))
        {
            return null;
        }

        var city = bar < 0 ? string.Empty : Clean(header[(bar + 1)..]);

        return new ClientLocation(country, city.Length == 0 ? null : city);
    }

    /// <summary>Percent-decoded, control characters removed, trimmed and cut to a sane length. Empty if it cannot be read.</summary>
    private static string Clean(string encoded)
    {
        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(encoded);
        }
        catch (UriFormatException)
        {
            return string.Empty;
        }

        // Bytes that are not valid UTF-8 are left as they were ("%FF") or come out as replacement characters; a name made of those is not a name.
        for (var index = 0; index + 2 < decoded.Length; index++)
        {
            if (decoded[index] == '%' && char.IsAsciiHexDigit(decoded[index + 1]) && char.IsAsciiHexDigit(decoded[index + 2]))
            {
                return string.Empty;
            }
        }

        var builder = new StringBuilder(decoded.Length);
        foreach (var character in decoded)
        {
            if (!char.IsControl(character) && character != '�')
            {
                builder.Append(character);
            }
        }

        var cleaned = builder.ToString().Trim();

        return cleaned.Length <= MaxCityLength ? cleaned : cleaned[..MaxCityLength];
    }
}

/// <summary>The location of the request being handled, or null when the proxy did not say (or this request did not come through it).</summary>
public interface IClientLocationAccessor
{
    ClientLocation? Current { get; }
}
