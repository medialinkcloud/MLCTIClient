using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace MedialinkCTIClient;

// STAGE 2: an explicitly requested activation only. Signature verification and
// authorization/enforcement are intentionally NOT implemented until public key
// and canonical signed payload have been independently validated.
internal static class DemoLicenseClient
{
    internal const string ApiPath = "/api/v1/mlcti/demo/activate";
    internal const string DefaultEndpointUrl = "https://license.medialink.cloudns.eu:8443/api/v1/mlcti/demo/activate";
    private static readonly string StoreDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Medialink", "CTIClient", "Licensing");
    internal static readonly string ReceiptPath = Path.Combine(StoreDirectory, "demo-rc2-receipt.json");

    internal static Uri ParseEndpoint(string address)
    {
        if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out var u) ||
            u.Scheme != Uri.UriSchemeHttps || u.AbsolutePath != ApiPath ||
            !string.IsNullOrEmpty(u.Query) || !string.IsNullOrEmpty(u.Fragment) ||
            !string.IsNullOrEmpty(u.UserInfo))
            throw new InvalidOperationException("Usare un URL HTTPS senza query, con percorso esatto " + ApiPath);
        return u;
    }

    internal static string ComputeDeviceHash()
    {
        // MachineGuid is read locally. Only a salted SHA-256 digest is transmitted.
        // Reinstalling or reimaging Windows may change this identifier.
        // Explicit registry views: Windows 7 x86 client can run on 64-bit Windows.
        // Always prefer the native 64-bit view when available, keeping the device
        // hash identical to the previously released 64-bit client. Do not create,
        // replace, transmit or log the raw MachineGuid.
        string machineGuid = "";
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography", false);
                var candidate = Convert.ToString(key?.GetValue("MachineGuid"))?.Trim() ?? "";
                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    machineGuid = candidate;
                    break;
                }
            }
            catch (System.Security.SecurityException) { /* Try the other registry view. */ }
            catch (UnauthorizedAccessException) { /* Try the other registry view. */ }
            catch (PlatformNotSupportedException) { /* Try the other registry view. */ }
            catch (IOException) { /* Try the other registry view. */ }
        }
        if (string.IsNullOrWhiteSpace(machineGuid))
            throw new InvalidOperationException("MachineGuid di Windows non disponibile nelle viste Registro 64/32 bit: attivazione bloccata. Verificare accesso in sola lettura a HKLM\\SOFTWARE\\Microsoft\\Cryptography.");
        var raw = Encoding.UTF8.GetBytes("MEDIALINK|MLCTICLIENT|DEMO-RC2|" + machineGuid.Trim().ToUpperInvariant());
        return Convert.ToHexString(SHA256.HashData(raw));
    }

    private static HttpClient CreateHttp()
    {
        // Never disable certificate validation and never automatically follow redirects.
        return new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(12) };
    }

    internal static async Task<(int JsonStatus, int MediaStatus)> DiagnosticAsync(Uri endpoint)
    {
        using var client = CreateHttp();
        using var a = new HttpRequestMessage(HttpMethod.Post, endpoint) {
            Content = new StringContent("{\"product\":\"MLCTICLIENT\",\"device_hash\":\"INVALID\"}", Encoding.UTF8, "application/json")
        };
        using var aResponse = await client.SendAsync(a);
        using var b = new HttpRequestMessage(HttpMethod.Post, endpoint) {
            Content = new StringContent("{}", Encoding.UTF8, "text/plain")
        };
        using var bResponse = await client.SendAsync(b);
        return ((int)aResponse.StatusCode, (int)bResponse.StatusCode);
    }

    internal static async Task<(string LicenseId, string ExpiresAt, bool Reused)> ActivateAsync(Uri endpoint)
    {
        var deviceHash = ComputeDeviceHash();
        using var client = CreateHttp();
        var payload = JsonSerializer.Serialize(new { product = "MLCTICLIENT", device_hash = deviceHash });
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        using var response = await client.SendAsync(request);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            var code = (int)response.StatusCode;
            // No raw body or sensitive details in logs/messages.
            throw new InvalidOperationException($"Server licenze: HTTP {code}. Nessuna licenza salvata.");
        }
        var bytes = await response.Content.ReadAsByteArrayAsync();
        if (bytes.Length > 16384) throw new InvalidDataException("Risposta del server eccessivamente grande.");
        using var json = JsonDocument.Parse(bytes);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("license", out var lic) ||
            lic.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Risposta di attivazione non conforme.");
        string Required(string property)
        {
            if (!lic.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Campo licenza mancante: " + property);
            var text = value.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("Campo licenza vuoto: " + property);
            return text;
        }
        var serial = Required("appliance_serial");
        var licenseId = Required("license_id");
        var expires = Required("expires_at");
        var signed = Required("signature");
        if (!string.Equals(serial, deviceHash, StringComparison.Ordinal))
            throw new InvalidDataException("Dispositivo diverso da quello richiesto.");
        if (!licenseId.StartsWith("MLCTI-DEMO-", StringComparison.Ordinal))
            throw new InvalidDataException("ID licenza inatteso.");
        var signature = Convert.FromBase64String(signed);
        if (signature.Length != 384) throw new InvalidDataException("Lunghezza firma inattesa.");
        if (!DateTimeOffset.TryParse(expires, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out _))
            throw new InvalidDataException("Scadenza non interpretabile.");
        var reused = root.TryGetProperty("reused", out var r) && r.ValueKind == JsonValueKind.True;
        // Verify the actual RSA signature BEFORE persisting or presenting any entitlement.
        var verified = DemoLicenseVerifier.Verify(lic, deviceHash, DateTimeOffset.UtcNow);
        Directory.CreateDirectory(StoreDirectory);
        var saved = new {
            endpoint = endpoint.ToString(), saved_at_utc = DateTimeOffset.UtcNow.ToString("O"),
            cryptographic_signature_verified = true,
            response = root.Clone()
        };
        var temp = Path.Combine(StoreDirectory, ".demo-rc2-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(saved, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, ReceiptPath, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return (verified.LicenseId, verified.ExpiresUtc.ToString("yyyy-MM-ddTHH:mm:ss'Z'"), reused);
    }

    internal static DemoLicenseVerifier.Verified VerifySaved()
    {
        if (!File.Exists(ReceiptPath)) throw new FileNotFoundException("Nessuna ricevuta Demo locale");
        var data = File.ReadAllBytes(ReceiptPath);
        if (data.Length > 32768) throw new InvalidDataException("Ricevuta eccessivamente grande");
        using var document = JsonDocument.Parse(data);
        var root = document.RootElement;
        if (!root.TryGetProperty("response", out var response) ||
            !response.TryGetProperty("license", out var license))
            throw new InvalidDataException("Ricevuta non conforme");
        // Receipt flags/metadata are not trusted; verify the signed license afresh.
        return DemoLicenseVerifier.Verify(license, ComputeDeviceHash(), DateTimeOffset.UtcNow);
    }

}
