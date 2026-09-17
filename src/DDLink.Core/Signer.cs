using System.Security.Cryptography;
using System.Text;

namespace DDLink.Core;

/// <summary>
/// Message authentication between the plugin and the platform.
/// The signature is HMAC-SHA256 over "{unix timestamp}.{raw body}", hex encoded.
/// Signing the timestamp lets the platform reject replayed requests.
/// </summary>
public static class Signer
{
    public const string TimestampHeader = "X-DD-Timestamp";
    public const string SignatureHeader = "X-DD-Signature";
    private const string SignaturePrefix = "v1=";

    public static string Sign(string secret, long unixTimestamp, ReadOnlySpan<byte> body)
    {
        var prefix = Encoding.UTF8.GetBytes($"{unixTimestamp}.");
        var data = new byte[prefix.Length + body.Length];
        prefix.CopyTo(data, 0);
        body.CopyTo(data.AsSpan(prefix.Length));

        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), data);
        return SignaturePrefix + Convert.ToHexStringLower(mac);
    }

    public static bool Verify(string secret, long unixTimestamp, ReadOnlySpan<byte> body, string signature)
    {
        var expected = Encoding.UTF8.GetBytes(Sign(secret, unixTimestamp, body));
        var actual = Encoding.UTF8.GetBytes(signature);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
