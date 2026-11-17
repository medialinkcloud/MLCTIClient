using System;
using System.IO;
using System.Management;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace MedialinkCTIClient;

/// <summary>
/// First DEMO activation is server-authoritative. Never create a trial from
/// an install date, a local registry flag or a missing settings.json.
/// Reinstalling CTI never grants another 30 days.
/// </summary>
internal static class CtiDemoClient
{
    public const int TrialDays = 30;
    public const string EndpointPath = "/api/v1/mlcti/demo/activate";
    private static string ServerUrlFile => Path.Combine(ConfigStore.DirectoryPath, "demo-server-url.txt");

    public static string GetSavedEndpoint()
    {
        try
        {
            if (File.Exists(ServerUrlFile))
                return File.ReadAllText(ServerUrlFile, Encoding.UTF8).Trim();
        }
        catch (Exception ex) { DiagnosticLog.Error("DEMO_URL_READ_FAILED", ex); }
        return "";
    }

    private static bool NonGeneric(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        string s = value.Trim().ToUpperInvariant();
        if (s.Length < 5 || s.Length > 256) return false;
        return !(s.Contains("TO BE FILLED") || s.Contains("DEFAULT STRING") ||
                 s.Contains("NOT SPECIFIED") || s.Contains("NOT AVAILABLE") ||
                 s.Contains("UNKNOWN") || s.Contains("INVALID") ||
                 s.Contains("NONE") || s.Contains("SYSTEM SERIAL") ||
                 s.Contains("SERIAL NUMBER") || s.Contains("NO ASSET INFORMATION") ||
                 s == "OEM" || s.Contains("OEM DEFAULT") ||
                 s == "00000000-0000-0000-0000-000000000000" ||
                 s == "FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF" ||
                 s.Replace("0", "").Replace("-", "").Length == 0);
    }

    private static string WmiValue(string wmiClass, string prop)
    {
        try
        {
            using (var search = new ManagementObjectSearcher("SELECT " + prop + " FROM " + wmiClass))
            using (var result = search.Get())
                foreach (ManagementObject obj in result)
                {
                    string val = Convert.ToString(obj[prop])?.Trim() ?? "";
                    if (NonGeneric(val)) return val.ToUpperInvariant();
                }
        }
        catch (Exception ex) { DiagnosticLog.Error("DEMO_WMI_NOT_AVAILABLE", ex); }
        return "";
    }

    private static string MachineGuid()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using (var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                using (var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography"))
                {
                    string val = Convert.ToString(key?.GetValue("MachineGuid")) ?? "";
                    if (NonGeneric(val)) return val.Trim().ToUpperInvariant();
                }
            }
            catch (Exception ex) { DiagnosticLog.Error("DEMO_MACHINEGUID_UNAVAILABLE", ex); }
        }
        return "";
    }

    public static string DeviceHash()
    {
        // SMBIOS UUID survives application uninstall/reinstall, and normally Windows reinstall.
        // Fallback to BIOS/board IDs, then OS MachineGuid (survives CTI reinstall).
        string uuid = WmiValue("Win32_ComputerSystemProduct", "UUID");
        string identity;
        if (uuid.Length > 0)
            identity = "UUID:" + uuid;
        else
        {
            string bios = WmiValue("Win32_BIOS", "SerialNumber");
            string board = WmiValue("Win32_BaseBoard", "SerialNumber");
            if (bios.Length > 0 || board.Length > 0)
                identity = "BIOS:" + bios + "|BOARD:" + board;
            else
            {
                string machine = MachineGuid();
                if (machine.Length == 0)
                    throw new InvalidOperationException("Identificativo postazione non disponibile: contattare assistenza Medialink.");
                identity = "MACHINEGUID:" + machine;
            }
        }
        using (var sha = SHA256.Create())
        {
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes("MLCTI-DEMO-V1|" + identity));
            return BitConverter.ToString(bytes).Replace("-", "");
        }
    }

    public static string LocalTrialSummary()
    {
        try
        {
            if (!File.Exists(CtiLicenseVerifier.LicenseFile))
                return "Nessuna DEMO salvata: attivazione online necessaria.";
            using (var doc = JsonDocument.Parse(File.ReadAllText(CtiLicenseVerifier.LicenseFile, Encoding.UTF8)))
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("edition", out var edition) ||
                    !string.Equals(edition.GetString(), "DEMO", StringComparison.OrdinalIgnoreCase))
                    return "Licenza diversa dalla DEMO.";
                if (!root.TryGetProperty("expires_at", out var end) ||
                    !DateTimeOffset.TryParse(end.GetString(), out var expires))
                    return "Scadenza DEMO non leggibile.";
                var remaining = expires.ToUniversalTime() - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                    return "DEMO scaduta: non rinnovabile sulla stessa postazione.";
                return "DEMO attiva: " + Math.Ceiling(remaining.TotalDays).ToString() +
                    " giorni residui (scadenza UTC " + expires.UtcDateTime.ToString("dd/MM/yyyy HH:mm") + ").";
            }
        }
        catch { return "Impossibile leggere lo stato DEMO locale."; }
    }

    public static bool SelfTest()
    {
        var a = DeviceHash();
        var b = DeviceHash();
        return a.Length == 64 && a == b && Regex.IsMatch(a, "^[A-F0-9]{64}$");
    }

    public static async Task<(bool Success, string Message)> ActivateAsync(string endpoint)
    {
        try
        {
            if (CtiLicenseVerifier.HasValidNonDemoLicense())
                return (false, "Esiste già una licenza commerciale valida: la DEMO non è necessaria.");
            if (!Uri.TryCreate(endpoint?.Trim(), UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps ||
                !uri.AbsolutePath.Equals(EndpointPath, StringComparison.Ordinal) ||
                uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
                return (false, "Inserire l'URL HTTPS completo dell'endpoint licensing Medialink.");

            string fingerprint = DeviceHash();
            var requestJson = "{\"product\":\"MLCTICLIENT\",\"device_hash\":\"" + fingerprint + "\"}";
            // TLS certificate validation is enabled by default. No HTTP fallback or redirects.
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var handler = new HttpClientHandler { AllowAutoRedirect = false })
            using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) })
            using (var content = new StringContent(requestJson, Encoding.UTF8, "application/json"))
            using (var response = await client.PostAsync(uri, content).ConfigureAwait(false))
            {
                if (response.StatusCode != HttpStatusCode.OK)
                    return (false, "Attivazione DEMO rifiutata dal License Server (HTTP " + (int)response.StatusCode + ").");
                byte[] bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                if (bytes.Length < 20 || bytes.Length > 65536)
                    return (false, "Risposta del License Server non valida.");
                using (var doc = JsonDocument.Parse(bytes))
                {
                    if (!doc.RootElement.TryGetProperty("license", out var license) ||
                        license.ValueKind != JsonValueKind.Object)
                        return (false, "Risposta priva di licenza firmata.");
                    string raw = license.GetRawText();
                    var result = CtiLicenseVerifier.ImportSignedJson(raw);
                    if (result.Success)
                    {
                        Directory.CreateDirectory(ConfigStore.DirectoryPath);
                        File.WriteAllText(ServerUrlFile, uri.AbsoluteUri, new UTF8Encoding(false));
                        DiagnosticLog.Info("DEMO_30D_ACTIVATED", "server_signed=true; device_bound=true");
                        return (true, "DEMO attivata. " + result.Message);
                    }
                    if (result.Message.Contains("scaduta"))
                        return (false, "DEMO già utilizzata su questa postazione: 30 giorni terminati. Non è possibile riattivarla reinstallando il programma.");
                    return (false, result.Message);
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException ||
                                   ex is InvalidOperationException || ex is JsonException ||
                                   ex is IOException || ex is UnauthorizedAccessException)
        {
            DiagnosticLog.Error("DEMO_ACTIVATION_FAILED", ex);
            return (false, "Impossibile attivare la DEMO. È necessaria una connessione HTTPS al License Server Medialink; la reinstallazione non azzera i 30 giorni.");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("DEMO_ACTIVATION_UNEXPECTED", ex);
            return (false, "Errore inatteso durante l'attivazione DEMO. Consultare i log.");
        }
    }
}
