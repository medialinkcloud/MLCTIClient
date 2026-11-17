using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MedialinkCTIClient;

// RC2 public-key verification. This file contains PUBLIC key material only.
// Does NOT grant CTI access: enforcement/offline policy is a separate release gate.
internal static class DemoLicenseVerifier
{
    private const string PublicPem = """
-----BEGIN PUBLIC KEY-----
MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEA+bNTIffoeVwlkywCq00W
IzQ8bPsUfFM5miHq3qFysdIwwDStp1/y4VmT6f9umIg1klb65vTPca8j/rZ2C9iO
heisSaqqitIdlZ4ELsLYAaWsT7WEgmPLHrgxWQAYWRzmEfQ3lA0P45cUl3rhGpEd
GGP1Ks3JUO/UK03mlF+BzGbAz91AsFMmGN6nC79ao39eJGWKY/jEN3UbYJAg2AQQ
U4xS0bqiPibEsyaDcBQVafvCxCwTX7VtSOsnZTT/OD60jBU19uMe3ZDhLEaraUgv
QnAsbkUa479HEILwjC/2ksxpfOky0apluhwEs8kW8Ff8qF6u1ttE07fjWaZweSK5
5ArXex6mdI0UVKp7sXyb3R+WRiS7Dngw0kt5EM2tLj0xAAuzFCo540j0FUqPZw/a
FEToIet7L+pra4LBe7RNO8wrzJJwbnQg3/J6du71ldbJ1sNj45jmJ48WxmoGXiX1
5tEAx58nS47Jko4dTMrxrNflkY/Bpwj17VVwx1Semg0fAgMBAAE=
-----END PUBLIC KEY-----
""";
    private const string ExpectedSpki = "0A6B99BA7BD4DE8656529FD1EA5F81B2A4A2EFFBD842FD68734847DA7DFD87C5";

    internal sealed class Verified
    {
        public string LicenseId { get; private set; }
        public DateTimeOffset StartedUtc { get; private set; }
        public DateTimeOffset ExpiresUtc { get; private set; }
        public int DaysRemaining { get; private set; }
        public Verified(string id, DateTimeOffset started, DateTimeOffset expires, int remaining)
        { LicenseId=id; StartedUtc=started; ExpiresUtc=expires; DaysRemaining=remaining; }
    }

    private static string Required(JsonElement objectElement, string name)
    {
        if (!objectElement.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Campo licenza assente/non valido: " + name);
        return value.GetString() ?? throw new InvalidDataException("Campo licenza nullo: " + name);
    }

    private static DateTimeOffset ParseUtc(JsonElement license, string property)
    {
        var value = Required(license, property);
        if (!DateTimeOffset.TryParseExact(value, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) ||
            !value.EndsWith("Z", StringComparison.Ordinal))
            throw new InvalidDataException("Data UTC non conforme: " + property);
        return parsed;
    }

    // Python json.dumps(sort_keys=True, separators=(',', ':'), ensure_ascii=False, allow_nan=False).
    // JSON numbers here must be plain integers; floats/NaN are rejected. Escaping matches
    // Python for valid UTF-8 strings, including literal non-ASCII and control characters.
    private static void AppendQuoted(StringBuilder sb, string value)
    {
        sb.Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    private static void AppendCanonical(StringBuilder sb, JsonElement item, bool root)
    {
        switch (item.ValueKind)
        {
            case JsonValueKind.Object:
                sb.Append('{');
                bool first = true;
                var properties = item.EnumerateObject().ToArray();
                if (properties.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length)
                    throw new InvalidDataException("Proprietà JSON duplicate non consentite");
                foreach (var property in properties.OrderBy(x => x.Name, StringComparer.Ordinal))
                {
                    if (root && property.Name == "signature") continue;
                    if (!first) sb.Append(',');
                    first = false;
                    AppendQuoted(sb, property.Name);
                    sb.Append(':');
                    AppendCanonical(sb, property.Value, false);
                }
                sb.Append('}');
                break;
            case JsonValueKind.Array:
                sb.Append('[');
                bool itemFirst = true;
                foreach (var value in item.EnumerateArray())
                {
                    if (!itemFirst) sb.Append(',');
                    itemFirst = false;
                    AppendCanonical(sb, value, false);
                }
                sb.Append(']');
                break;
            case JsonValueKind.String: AppendQuoted(sb, item.GetString() ?? ""); break;
            case JsonValueKind.Number:
                // Server RC2 emits only integer JSON numbers for this format.
                if (!item.TryGetInt64(out long n) || item.GetRawText() != n.ToString(CultureInfo.InvariantCulture))
                    throw new InvalidDataException("Numero JSON non canonico");
                sb.Append(n.ToString(CultureInfo.InvariantCulture));
                break;
            case JsonValueKind.True: sb.Append("true"); break;
            case JsonValueKind.False: sb.Append("false"); break;
            case JsonValueKind.Null: sb.Append("null"); break;
            default: throw new InvalidDataException("Valore JSON non supportato");
        }
    }

    // Win7/net48: RSA public modulus/exponent from the exact production SPKI
    // fingerprint 0A6B99BA7BD4DE8656529FD1EA5F81B2A4A2EFFBD842FD68734847DA7DFD87C5 (verified during package construction).
    // CAPI supports RSA3072 PKCS#1 v1.5 SHA-256 without newer ImportFromPem APIs.
    private const string ModulusB64 = "+bNTIffoeVwlkywCq00WIzQ8bPsUfFM5miHq3qFysdIwwDStp1/y4VmT6f9umIg1klb65vTPca8j/rZ2C9iOheisSaqqitIdlZ4ELsLYAaWsT7WEgmPLHrgxWQAYWRzmEfQ3lA0P45cUl3rhGpEdGGP1Ks3JUO/UK03mlF+BzGbAz91AsFMmGN6nC79ao39eJGWKY/jEN3UbYJAg2AQQU4xS0bqiPibEsyaDcBQVafvCxCwTX7VtSOsnZTT/OD60jBU19uMe3ZDhLEaraUgvQnAsbkUa479HEILwjC/2ksxpfOky0apluhwEs8kW8Ff8qF6u1ttE07fjWaZweSK55ArXex6mdI0UVKp7sXyb3R+WRiS7Dngw0kt5EM2tLj0xAAuzFCo540j0FUqPZw/aFEToIet7L+pra4LBe7RNO8wrzJJwbnQg3/J6du71ldbJ1sNj45jmJ48WxmoGXiX15tEAx58nS47Jko4dTMrxrNflkY/Bpwj17VVwx1Semg0f";
    private const string ExponentB64 = "AQAB";
    internal static bool VerifySignature(byte[] data, byte[] signature)
    {
        if (signature.Length != 384) return false;
        using (var rsa = new RSACryptoServiceProvider(3072, new CspParameters(24)))
        {
            rsa.PersistKeyInCsp = false;
            rsa.ImportParameters(new RSAParameters {
                Modulus = Convert.FromBase64String(ModulusB64),
                Exponent = Convert.FromBase64String(ExponentB64)
            });
            using (var sha = SHA256.Create())
            {
                var digest = sha.ComputeHash(data);
                return rsa.VerifyHash(digest, "2.16.840.1.101.3.4.2.1", signature);
            }
        }
    }

    internal static byte[] CanonicalBytes(JsonElement license)
    {
        if (license.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Licenza non è un oggetto JSON");
        var sb = new StringBuilder();
        AppendCanonical(sb, license, true);
        return new UTF8Encoding(false, true).GetBytes(sb.ToString());
    }

    internal static Verified Verify(JsonElement license, string deviceHash, DateTimeOffset nowUtc)
    {
        if (license.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Licenza non valida");
        if (Required(license, "product") != "MLCTICLIENT" || Required(license, "edition") != "DEMO" ||
            Required(license, "customer") != "DEMO" || Required(license, "appliance_serial") != deviceHash)
            throw new InvalidDataException("Licenza non corrispondente al prodotto o al PC");
        if (!license.TryGetProperty("license_version", out var version) || !version.TryGetInt32(out var v) || v != 1 ||
            !license.TryGetProperty("trial_days", out var days) || !days.TryGetInt32(out var d) || d != 30)
            throw new InvalidDataException("Versione o durata Demo inattesa");
        var id = Required(license, "license_id");
        if (!id.StartsWith("MLCTI-DEMO-", StringComparison.Ordinal))
            throw new InvalidDataException("Identificativo licenza inatteso");
        var issued = ParseUtc(license, "issued_at");
        var started = ParseUtc(license, "trial_started_at");
        var expires = ParseUtc(license, "expires_at");
        if (issued != started || expires != started.AddDays(30))
            throw new InvalidDataException("Cronologia Demo incoerente");
        if (!license.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array ||
            features.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String) ||
            !new[] { "click_to_dial", "ldap", "cdr", "call_control" }
                .All(x => features.EnumerateArray().Any(y => y.GetString() == x)))
            throw new InvalidDataException("Funzionalità Demo non conformi");
        var rawSignature = Required(license, "signature");
        byte[] signature;
        try { signature = Convert.FromBase64String(rawSignature); }
        catch (FormatException) { throw new InvalidDataException("Firma Base64 non valida"); }
        if (signature.Length != 384) throw new InvalidDataException("Firma RSA di lunghezza errata");
        if (!VerifySignature(CanonicalBytes(license), signature))
            throw new CryptographicException("Firma RSA della licenza non valida");
        if (nowUtc.ToUniversalTime() < started) throw new InvalidDataException("Orologio locale antecedente all'attivazione");
        if (nowUtc.ToUniversalTime() >= expires) throw new InvalidDataException("Demo 30 giorni scaduta");
        var remaining = (int)Math.Ceiling((expires - nowUtc.ToUniversalTime()).TotalDays);
        return new Verified(id, started, expires, remaining);
    }

    internal static void VerifySignedPayload(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object ||
            !item.TryGetProperty("signature", out var sig) || sig.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Firma licenza assente");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(sig.GetString() ?? ""); }
        catch (FormatException) { throw new InvalidDataException("Firma licenza non Base64"); }
        if (bytes.Length != 384) throw new CryptographicException("Lunghezza firma licenza inattesa");
        if (!VerifySignature(CanonicalBytes(item), bytes))
            throw new CryptographicException("Firma RSA commerciale non valida");
    }

    // STAGE 4: safe cryptographic interoperability probe. No trial record,
    // entitlement or persistent license is produced by this method.
    internal static bool SelfTestCrypto(string? signatureFile)
    {
        const string sample = """
{"customer":"DÉMO","features":["click_to_dial","ldap"],"product":"MLCTICLIENT","trial_days":30}
""";
        using var doc = JsonDocument.Parse("""
{"trial_days":30,"product":"MLCTICLIENT","features":["click_to_dial","ldap"],"customer":"DÉMO","signature":"ignored"}
""");
        var canonical = Encoding.UTF8.GetString(CanonicalBytes(doc.RootElement));
        if (!string.Equals(canonical, sample.TrimEnd('\r','\n'), StringComparison.Ordinal))
            throw new CryptographicException("Canonical JSON differs from Python reference");
        // Public key is pinned via the independently checked SPKI fingerprint in the build manifest.

        if (signatureFile is null) return true; // static checks only, not real signature E2E.
        if (!File.Exists(signatureFile)) throw new FileNotFoundException("RSA signature probe file missing", signatureFile);
        var encoded = File.ReadAllText(signatureFile).Trim();
        byte[] signature = Convert.FromBase64String(encoded);
        if (signature.Length != 384) throw new CryptographicException("Probe signature must be 384 bytes");
        var data = Encoding.ASCII.GetBytes("MLCTI-DEMO-RC2-SIGNER-PREFLIGHT-V1");
        if (!VerifySignature(data, signature))
            throw new CryptographicException("Production RSA probe verification failed");
        // Negative test: a single modified byte must invalidate the signature.
        data[0] ^= 1;
        if (VerifySignature(data, signature))
            throw new CryptographicException("Modified message accepted unexpectedly");
        return true;
    }
}
