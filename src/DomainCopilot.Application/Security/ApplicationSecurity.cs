using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Generators;

namespace DomainCopilot.Application.Security;

public sealed class PasswordError(string message) : Exception(message);
public sealed class TokenError(string message) : Exception(message);

public static class ApplicationSecurity
{
    public const int DefaultN = 16384, DefaultR = 8, DefaultP = 1, KeyLength = 64;
    public const int DEFAULT_N = DefaultN, DEFAULT_R = DefaultR, DEFAULT_P = DefaultP, KEY_LEN = KeyLength;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    // The accepted limits cap scrypt memory at 64 MiB (128 * N * r) and work
    // at 2,097,152 (N * r * p). The legacy Python defaults use 16 MiB and
    // 131,072 work units. These checks run before invoking the KDF.
    public const int MaxScryptN = 65_536;
    public const int MaxScryptR = 16;
    public const int MaxScryptP = 4;
    public const long MaxScryptMemoryBytes = 64L * 1024 * 1024;
    public const long MaxScryptWork = 2_097_152;

    public static string HashPassword(string password)
    {
        if (string.IsNullOrEmpty(password)) throw new PasswordError("password must not be empty");
        var salt = RandomNumberGenerator.GetBytes(16);
        var derived = SCrypt.Generate(StrictUtf8.GetBytes(password), salt, DefaultN, DefaultR, DefaultP, KeyLength);
        return $"scrypt${DefaultN}${DefaultR}${DefaultP}${Encode(salt)}${Encode(derived)}";
    }

    public static bool VerifyPassword(string password, string stored)
    {
        try
        {
            if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(stored)) return false;
            var parts = stored.Split('$');
            if (parts.Length != 6 || parts[0] != "scrypt") return false;
            if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ||
                !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var r) ||
                !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var p) ||
                !IsSupportedScryptParameters(n, r, p)) return false;
            var salt = Decode(parts[4]);
            var expected = Decode(parts[5]);
            if (salt.Length != 16 || expected.Length != KeyLength) return false;
            var actual = SCrypt.Generate(StrictUtf8.GetBytes(password), salt, n, r, p, expected.Length);
            return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (Exception error) when (error is FormatException or ArgumentException or OverflowException or IOException or
            CryptographicException or Org.BouncyCastle.Crypto.CryptoException or DecoderFallbackException)
        { return false; }
    }

    private static bool IsSupportedScryptParameters(int n, int r, int p)
    {
        if (n < 2 || n > MaxScryptN || (n & (n - 1)) != 0 || r < 1 || r > MaxScryptR || p < 1 || p > MaxScryptP)
            return false;
        // Bounds above ensure these products fit in Int64 before evaluation.
        var memoryBytes = 128L * n * r;
        var work = (long)n * r * p;
        return memoryBytes <= MaxScryptMemoryBytes && work <= MaxScryptWork;
    }

    public static string DummyHash()
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var derived = SCrypt.Generate(Encoding.UTF8.GetBytes("dummy"), salt, DefaultN, DefaultR, DefaultP, KeyLength);
        return $"scrypt${DefaultN}${DefaultR}${DefaultP}${Encode(salt)}${Encode(derived)}";
    }

    public static string CreateToken(string tenantId, long userId, string role, string secret, int ttlSeconds)
    {
        var key = GetSigningKey(secret);
        var exp = new BigInteger(Math.Truncate(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d + ttlSeconds));
        var payload = "{\"tenant_id\":" + PythonJsonString(tenantId) +
            ",\"user_id\":" + userId.ToString(CultureInfo.InvariantCulture) +
            ",\"role\":" + PythonJsonString(role) +
            ",\"exp\":" + exp.ToString(CultureInfo.InvariantCulture) + "}";
        var encoded = Encode(Encoding.UTF8.GetBytes(payload));
        var signature = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(encoded));
        return encoded + "." + Encode(signature);
    }

    public static IReadOnlyDictionary<string, object?> VerifyToken(string token, string secret, double? now = null)
    {
        if (string.IsNullOrEmpty(token) || !token.Contains('.')) throw new TokenError("invalid token");
        var key = GetSigningKey(secret);
        var separator = token.LastIndexOf('.');
        var encodedPayload = token[..separator];
        var suppliedSignature = token[(separator + 1)..];
        var expectedSignature = Encode(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(encodedPayload)));
        var suppliedBytes = Encoding.ASCII.GetBytes(suppliedSignature);
        var expectedBytes = Encoding.ASCII.GetBytes(expectedSignature);
        if (suppliedBytes.Length != expectedBytes.Length || !CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes))
            throw new TokenError("invalid signature");

        try
        {
            var jsonBytes = DecodeBase64Url(encodedPayload);
            using var document = JsonDocument.Parse(jsonBytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new TokenError("invalid token");
            var root = document.RootElement;
            if (!root.TryGetProperty("exp", out var expiration)) throw new TokenError("invalid token");
            BigInteger exp;
            if (expiration.ValueKind == JsonValueKind.True) exp = BigInteger.One;
            else if (expiration.ValueKind == JsonValueKind.False) exp = BigInteger.Zero;
            else if (expiration.ValueKind == JsonValueKind.Number && BigInteger.TryParse(expiration.GetRawText(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) exp = parsed;
            else throw new TokenError("token expired");

            var timestamp = new BigInteger(Math.Truncate(now ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d));
            if (exp < timestamp) throw new TokenError("token expired");
            foreach (var claim in new[] { "tenant_id", "user_id", "role" })
                if (!root.TryGetProperty(claim, out _)) throw new TokenError("invalid token");

            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject()) result[property.Name] = ReadJsonValue(property.Value);
            return result;
        }
        catch (TokenError) { throw; }
        catch (Exception error) when (error is FormatException or JsonException or ArgumentException or OverflowException or DecoderFallbackException)
        { throw new TokenError("malformed token"); }
    }

    private static byte[] GetSigningKey(string secret)
    {
        if (string.IsNullOrEmpty(secret) || secret.EnumerateRunes().Count() < 32) throw new TokenError("auth secret must be at least 32 characters");
        byte[] bytes;
        try { bytes = StrictUtf8.GetBytes(secret); }
        catch (EncoderFallbackException) { throw new TokenError("auth secret must be at least 32 characters"); }
        return bytes;
    }

    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] Decode(string value) => DecodeBase64Url(value);

    private static byte[] DecodeBase64Url(string value)
    {
        if (value.Length % 4 == 1) throw new FormatException("invalid base64url");
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized += new string('=', (4 - normalized.Length % 4) % 4);
        return Convert.FromBase64String(normalized);
    }

    private static object? ReadJsonValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => ReadJsonObject(element),
        JsonValueKind.Array => element.EnumerateArray().Select(ReadJsonValue).ToArray(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number when element.TryGetInt64(out var value) => value,
        JsonValueKind.Number when BigInteger.TryParse(element.GetRawText(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var big) => big,
        JsonValueKind.Number when double.TryParse(element.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out var floating) => floating,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => throw new TokenError("invalid token")
    };

    private static IReadOnlyDictionary<string, object?> ReadJsonObject(JsonElement element)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject()) result[property.Name] = ReadJsonValue(property.Value);
        return result;
    }

    private static string PythonJsonString(string value)
    {
        var result = new StringBuilder(value.Length + 2).Append('"');
        foreach (var rune in value.EnumerateRunes())
        {
            switch (rune.Value)
            {
                case 0x22: result.Append("\\\""); break;
                case 0x5c: result.Append("\\\\"); break;
                case 0x08: result.Append("\\b"); break;
                case 0x0c: result.Append("\\f"); break;
                case 0x0a: result.Append("\\n"); break;
                case 0x0d: result.Append("\\r"); break;
                case 0x09: result.Append("\\t"); break;
                default:
                    if (rune.Value < 0x20) result.Append("\\u").Append(rune.Value.ToString("x4", CultureInfo.InvariantCulture));
                    else if (rune.Value <= 0x7f) result.Append((char)rune.Value);
                    else if (rune.Value <= 0xffff) result.Append("\\u").Append(rune.Value.ToString("x4", CultureInfo.InvariantCulture));
                    else
                    {
                        var scalar = rune.Value - 0x10000;
                        result.Append("\\u").Append((0xd800 + (scalar >> 10)).ToString("x4", CultureInfo.InvariantCulture));
                        result.Append("\\u").Append((0xdc00 + (scalar & 0x3ff)).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    break;
            }
        }
        return result.Append('"').ToString();
    }
}
