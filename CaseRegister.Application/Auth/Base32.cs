namespace CaseRegister.Application.Auth;

/// <summary>RFC 4648 base32 without padding: the form authenticator apps expect a shared key in.</summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var characters = new char[(data.Length * 8 + 4) / 5];
        var buffer = 0;
        var bitsInBuffer = 0;
        var index = 0;

        foreach (var value in data)
        {
            buffer = (buffer << 8) | value;
            bitsInBuffer += 8;

            while (bitsInBuffer >= 5)
            {
                characters[index++] = Alphabet[(buffer >> (bitsInBuffer - 5)) & 0x1F];
                bitsInBuffer -= 5;
            }
        }

        if (bitsInBuffer > 0)
        {
            characters[index] = Alphabet[(buffer << (5 - bitsInBuffer)) & 0x1F];
        }

        return new string(characters);
    }

    public static byte[] Decode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var bytes = new List<byte>(text.Length * 5 / 8);
        var buffer = 0;
        var bitsInBuffer = 0;

        foreach (var character in text.TrimEnd('='))
        {
            var value = Alphabet.IndexOf(char.ToUpperInvariant(character), StringComparison.Ordinal);
            if (value < 0)
            {
                throw new FormatException("Not a base32 string.");
            }

            buffer = (buffer << 5) | value;
            bitsInBuffer += 5;

            if (bitsInBuffer >= 8)
            {
                bytes.Add((byte)((buffer >> (bitsInBuffer - 8)) & 0xFF));
                bitsInBuffer -= 8;
            }
        }

        return [.. bytes];
    }
}
