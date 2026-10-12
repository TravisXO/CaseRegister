using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace CaseRegister.Application.Auditing;

/// <summary>
/// JSON canonicalisation as in RFC 8785 (the JSON Canonicalization Scheme): object keys sorted by their
/// UTF-16 code units, no whitespace, strings escaped the one minimal way, numbers written the way
/// ECMAScript writes them, UTF-8 output. The same data always gives the same bytes, so a hash over it
/// can be recomputed later.
/// </summary>
public static class Jcs
{
    /// <summary>The canonical text of a JSON value. A null node is the JSON value null.</summary>
    public static string Canonicalise(JsonNode? node)
    {
        var builder = new StringBuilder();
        Write(builder, node);

        return builder.ToString();
    }

    public static byte[] CanonicaliseToUtf8(JsonNode? node) => Encoding.UTF8.GetBytes(Canonicalise(node));

    private static void Write(StringBuilder builder, JsonNode? node)
    {
        switch (node)
        {
            case null:
                builder.Append("null");
                break;

            case JsonObject jsonObject:
                builder.Append('{');
                var first = true;
                foreach (var property in jsonObject.OrderBy(property => property.Key, StringComparer.Ordinal))
                {
                    if (!first)
                    {
                        builder.Append(',');
                    }

                    first = false;
                    WriteString(builder, property.Key);
                    builder.Append(':');
                    Write(builder, property.Value);
                }

                builder.Append('}');
                break;

            case JsonArray jsonArray:
                builder.Append('[');
                for (var index = 0; index < jsonArray.Count; index++)
                {
                    if (index > 0)
                    {
                        builder.Append(',');
                    }

                    Write(builder, jsonArray[index]);
                }

                builder.Append(']');
                break;

            case JsonValue value:
                WriteValue(builder, value);
                break;
        }
    }

    private static void WriteValue(StringBuilder builder, JsonValue value)
    {
        switch (value.GetValueKind())
        {
            case System.Text.Json.JsonValueKind.String:
                WriteString(builder, value.GetValue<string>());
                break;
            case System.Text.Json.JsonValueKind.True:
                builder.Append("true");
                break;
            case System.Text.Json.JsonValueKind.False:
                builder.Append("false");
                break;
            case System.Text.Json.JsonValueKind.Null:
                builder.Append("null");
                break;
            case System.Text.Json.JsonValueKind.Number:
                builder.Append(FormatNumberText(value.ToJsonString()));
                break;
            default:
                throw new InvalidOperationException("That kind of JSON value cannot be canonicalised.");
        }
    }

    /// <summary>
    /// A number from its JSON text, whatever .NET type it was built from. Whole numbers a double holds exactly are
    /// written as they are; anything else goes through double, as the RFC says.
    /// </summary>
    private static string FormatNumberText(string text) =>
        long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole) && Math.Abs(whole) <= 9_007_199_254_740_992L
            ? whole.ToString(CultureInfo.InvariantCulture)
            : FormatNumber(double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture));

    /// <summary>The escapes RFC 8785 allows: the short forms for quote, backslash and the five named controls, \u00xx for the rest of the controls, nothing else.</summary>
    private static void WriteString(StringBuilder builder, string text)
    {
        builder.Append('"');

        foreach (var character in text)
        {
            switch (character)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                case < ' ': builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture)); break;
                default: builder.Append(character); break;
            }
        }

        builder.Append('"');
    }

    /// <summary>A number as ECMAScript's Number-to-String writes it (RFC 8785 section 3.2.2.3).</summary>
    public static string FormatNumber(double number)
    {
        if (double.IsNaN(number) || double.IsInfinity(number))
        {
            throw new ArgumentOutOfRangeException(nameof(number), "JSON has no NaN or infinity.");
        }

        if (number == 0)
        {
            return "0"; // and minus zero too
        }

        var sign = number < 0 ? "-" : string.Empty;

        // The shortest text that reads back as the same number, split into its digits and where the point goes.
        var shortest = Math.Abs(number).ToString("R", CultureInfo.InvariantCulture);
        var exponent = 0;
        var mantissa = shortest;
        var marker = shortest.IndexOfAny(['E', 'e']);
        if (marker >= 0)
        {
            mantissa = shortest[..marker];
            exponent = int.Parse(shortest[(marker + 1)..], CultureInfo.InvariantCulture);
        }

        var point = mantissa.IndexOf('.', StringComparison.Ordinal);
        var digits = mantissa.Replace(".", string.Empty, StringComparison.Ordinal);
        var pointPosition = point < 0 ? digits.Length : point;

        var leadingZeros = digits.Length - digits.TrimStart('0').Length;
        digits = digits.TrimStart('0').TrimEnd('0');
        pointPosition -= leadingZeros;

        var k = digits.Length; // digits
        var n = pointPosition + exponent; // the point sits after n digits

        string text;
        if (k <= n && n <= 21)
        {
            text = digits + new string('0', n - k);
        }
        else if (0 < n && n <= 21)
        {
            text = digits[..n] + "." + digits[n..];
        }
        else if (-6 < n && n <= 0)
        {
            text = "0." + new string('0', -n) + digits;
        }
        else
        {
            var e = n - 1;
            var exponentText = (e < 0 ? "-" : "+") + Math.Abs(e).ToString(CultureInfo.InvariantCulture);
            text = k == 1 ? digits + "e" + exponentText : digits[0] + "." + digits[1..] + "e" + exponentText;
        }

        return sign + text;
    }
}
