using System.Security.Cryptography;
using System.Text;

namespace EstateHub.Application.Auth;

/// <summary>
/// Authenticator-app codes (RFC 6238 TOTP: HMAC-SHA1, 30-second steps, 6 digits), the format every
/// authenticator app understands. Accepts the previous and next step for clock drift, and never
/// the same step twice (replay).
/// </summary>
public static class Totp
{
    public const int StepSeconds = 30;
    public const int SecretBytes = 20;
    private const int Drift = 1;

    public static string NewSecret() => Base32.Encode(RandomNumberGenerator.GetBytes(SecretBytes));

    /// <summary>The step the code matched, or null. Steps at or before <paramref name="lastUsedStep"/> are refused.</summary>
    public static long? Verify(string base32Secret, string code, DateTimeOffset now, long? lastUsedStep)
    {
        code = code.Trim().Replace(" ", string.Empty);
        if (code.Length != 6 || !code.All(char.IsAsciiDigit))
            return null;

        var key = Base32.Decode(base32Secret);
        var current = now.ToUnixTimeSeconds() / StepSeconds;
        for (var step = current - Drift; step <= current + Drift; step++)
        {
            if (step <= lastUsedStep)
                continue;
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(key, step, 6)), Encoding.ASCII.GetBytes(code)))
                return step;
        }

        return null;
    }

    /// <summary>The otpauth:// link authenticator apps import (shown as a QR code).</summary>
    public static string OtpAuthUri(string issuer, string account, string base32Secret) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}" +
        $"?secret={base32Secret}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits=6&period={StepSeconds}";

    /// <summary>RFC 4226 HOTP value for one step. Internal for the RFC 6238 test vectors.</summary>
    internal static string Code(byte[] key, long step, int digits)
    {
        Span<byte> counter = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counter, step);
        var hash = HMACSHA1.HashData(key, counter);

        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        var modulo = (int)Math.Pow(10, digits);
        return (binary % modulo).ToString().PadLeft(digits, '0');
    }
}

/// <summary>RFC 4648 base32 (upper case, no padding), as used in otpauth:// secrets.</summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var output = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                output.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
            output.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return output.ToString();
    }

    public static byte[] Decode(string text)
    {
        text = text.Trim().TrimEnd('=').Replace(" ", string.Empty).ToUpperInvariant();
        var output = new List<byte>(text.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in text)
        {
            var value = Alphabet.IndexOf(c);
            if (value < 0)
                throw new FormatException("Not a base32 string.");
            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
        }

        return output.ToArray();
    }
}
