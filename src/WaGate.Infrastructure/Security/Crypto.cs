using System.Security.Cryptography;
using System.Text;

namespace WaGate.Infrastructure.Security;

public static class Crypto
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";

    /// <summary>Gera um token aleatório criptograficamente seguro.</summary>
    public static string NewToken(int length = 32)
    {
        Span<char> chars = stackalloc char[length];
        for (var i = 0; i < length; i++)
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(chars);
    }

    public static string NewSecret() => "whsec_" + NewToken(40);

    /// <summary>Assinatura enviada aos clientes: hex(HMAC-SHA256(secret, "{timestamp}.{body}")).</summary>
    public static string SignWebhook(string secret, long timestamp, string body)
    {
        var data = Encoding.UTF8.GetBytes($"{timestamp}.{body}");
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), data);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>Valida o header X-Webhook-Hmac do WAHA (HMAC-SHA512 sobre o corpo cru).</summary>
    public static bool VerifyWahaHmac(string key, ReadOnlySpan<byte> rawBody, string? headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerValue)) return false;
        var expected = HMACSHA512.HashData(Encoding.UTF8.GetBytes(key), rawBody);
        byte[] provided;
        try { provided = Convert.FromHexString(headerValue.Trim()); }
        catch (FormatException) { return false; }
        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }

    public static bool FixedTimeEquals(string? a, string? b)
    {
        if (a is null || b is null) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
    }
}
