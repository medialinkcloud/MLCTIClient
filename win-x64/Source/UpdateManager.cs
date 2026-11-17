using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MedialinkCTIClient;

// A.5: signed metadata verification and verified LAB staging; NO installer execution.
// Installations are deliberately disabled until the production signing key,
// Authenticode checks and the external updater have been commissioned.
internal static class UpdateManager
{
    private const string BaseUrl = "https://license.medialink.cloudns.eu:8443";
    private const string Product = "mlcticlient";
    private const string InstalledVersion = "7.4.18";
    private const string Platform = "win-x64";
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    private static readonly SemaphoreSlim SingleCheck = new SemaphoreSlim(1, 1);

    // The public key is injected on the developer workstation by APPLY_A5.ps1.
    // The private key must never be copied to clients or the server.
    private static string SigningModulusBase64 => UpdateTrust.ModulusBase64;
    private static string SigningExponentBase64 => UpdateTrust.ExponentBase64;

    private static string StateFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Medialink", "MLCTIClient", "update-last-check-utc.txt");

    internal static bool SelfTest()
    {
        return CheckVersion("7.4.9", InstalledVersion) &&
            !CheckVersion(InstalledVersion, InstalledVersion) &&
            !CheckVersion("7.4.6", InstalledVersion) &&
            !CheckVersion("7.4.9.0-garbage", InstalledVersion) &&
            IsTrustedKeyConfigured() &&
            new Uri(BaseUrl).Scheme == Uri.UriSchemeHttps;
    }

    private static bool CheckVersion(string remote, string local)
    {
        return Version.TryParse(remote, out var r) && Version.TryParse(local, out var l) && r > l;
    }

    private static bool IsTrustedKeyConfigured() =>
        SigningModulusBase64.Length > 0 && SigningExponentBase64.Length > 0;

    private static bool VerifyManifest(byte[] data, byte[] signature)
    {
        if (!IsTrustedKeyConfigured()) return false;
        using (var rsa = RSA.Create())
        {
            rsa.ImportParameters(new RSAParameters {
                Modulus = Convert.FromBase64String(SigningModulusBase64),
                Exponent = Convert.FromBase64String(SigningExponentBase64)
            });
            return rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
    }

    private static bool IsDue()
    {
        if (!File.Exists(StateFile)) return true;
        var raw = File.ReadAllText(StateFile, Encoding.ASCII).Trim();
        if (!long.TryParse(raw, out long ticks)) return true;
        var last = new DateTime(ticks, DateTimeKind.Utc);
        var elapsed = DateTime.UtcNow - last;
        return elapsed >= Interval || elapsed < TimeSpan.Zero;
    }

    private static void RecordSuccess()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StateFile) ?? throw new InvalidOperationException("Update state path missing"));
        File.WriteAllText(StateFile, DateTime.UtcNow.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture), Encoding.ASCII);
    }

    // Explicit LAB download validation. This does not run any installer or touch installation data.
    // Query version 7.4.6 is a fixed LAB probe for the previously published 7.4.7 release.
    internal static async Task<string> StagePublishedLabAsync()
    {
        const string channel = "lab";
        const string probeVersion = "7.4.6";
        const string labRelease = "lab-7.4.7";
        const string labVersion = "7.4.7";
        if (!IsTrustedKeyConfigured()) throw new InvalidOperationException("Chiave pubblica RSA non configurata.");
        var url = BaseUrl + "/api/v1/updates/check?product=" + Product +
            "&version=" + probeVersion + "&platform=" + Platform + "&channel=" + channel;
        using (var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
        using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) })
        {
            using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
            {
                if (response.StatusCode != HttpStatusCode.OK)
                    throw new InvalidOperationException("API LAB: HTTP " + (int)response.StatusCode);
                if (response.Content.Headers.ContentLength is long headerLen && headerLen > 65536)
                    throw new InvalidOperationException("Risposta API eccessiva.");
                var body = await response.Content.ReadAsStringAsync();
                if (body.Length > 65536) throw new InvalidOperationException("Risposta API eccessiva.");
                using (var doc = JsonDocument.Parse(body))
                {
                    if (!doc.RootElement.GetProperty("update_available").GetBoolean())
                        throw new InvalidOperationException("Nessuna release LAB disponibile.");
                    byte[] manifest = Convert.FromBase64String(doc.RootElement.GetProperty("manifest_base64").GetString() ?? "");
                    byte[] signature = Convert.FromBase64String(doc.RootElement.GetProperty("signature_base64").GetString() ?? "");
                    if (manifest.Length == 0 || manifest.Length > 65536 || signature.Length == 0 ||
                        signature.Length > 8192 || !VerifyManifest(manifest, signature))
                        throw new CryptographicException("Firma RSA del manifest non valida.");
                    using (var signed = JsonDocument.Parse(manifest))
                    {
                        var obj = signed.RootElement;
                        if (obj.GetProperty("product").GetString() != Product ||
                            obj.GetProperty("channel").GetString() != channel ||
                            obj.GetProperty("release_id").GetString() != labRelease ||
                            obj.GetProperty("version").GetString() != labVersion)
                            throw new InvalidOperationException("Identita release LAB inattesa.");
                        JsonElement package = default;
                        bool found = false;
                        foreach (var item in obj.GetProperty("packages").EnumerateArray())
                        {
                            if (item.GetProperty("platform").GetString() == Platform)
                            { if (found) throw new InvalidOperationException("Piattaforma duplicata."); package = item; found = true; }
                        }
                        if (!found) throw new InvalidOperationException("Installer piattaforma assente.");
                        string filename = package.GetProperty("filename").GetString() ?? "";
                        string expectedHash = package.GetProperty("sha256").GetString() ?? "";
                        long expectedSize = package.GetProperty("size").GetInt64();
                        if (filename.Length < 5 || filename.Length > 160 ||
                            filename != Path.GetFileName(filename) ||
                            filename.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                            filename.Contains("..") || !filename.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                            !System.Text.RegularExpressions.Regex.IsMatch(filename, @"^[A-Za-z0-9_.-]+$") ||
                            !System.Text.RegularExpressions.Regex.IsMatch(expectedHash, @"\A[0-9a-fA-F]{64}\z") ||
                            expectedSize < 1024 || expectedSize > 160L * 1024L * 1024L)
                            throw new InvalidOperationException("Metadati installer non validi.");
                        string expectedUrl = "/api/v1/updates/files/" + Product + "/" + channel + "/" + labRelease + "/" + filename;
                        string apiUrl = doc.RootElement.GetProperty("package").GetProperty("file_url").GetString() ?? "";
                        if (!string.Equals(apiUrl, expectedUrl, StringComparison.Ordinal))
                            throw new InvalidOperationException("Percorso download non corrispondente al manifest.");
                        var staging = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "Medialink", "MLCTIClient", "UpdateStaging", labRelease);
                        Directory.CreateDirectory(staging);
                        string final = Path.Combine(staging, filename);
                        string temporary = final + ".partial";
                        if (File.Exists(temporary)) File.Delete(temporary);
                        try
                        {
                            using (var download = await client.GetAsync(BaseUrl + expectedUrl, HttpCompletionOption.ResponseHeadersRead))
                            {
                                if (download.StatusCode != HttpStatusCode.OK) throw new InvalidOperationException("Download: HTTP " + (int)download.StatusCode);
                                if (download.Content.Headers.ContentLength is long n && n != expectedSize)
                                    throw new InvalidOperationException("Dimensione dichiarata diversa dal manifest.");
                                using (var input = await download.Content.ReadAsStreamAsync())
                                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                                using (var sha = SHA256.Create())
                                {
                                    var buf = new byte[65536]; long count = 0; int read;
                                    while ((read = await input.ReadAsync(buf, 0, buf.Length)) != 0)
                                    {
                                        count += read;
                                        if (count > expectedSize) throw new InvalidOperationException("Download oltre la dimensione firmata.");
                                        sha.TransformBlock(buf, 0, read, null, 0);
                                        await output.WriteAsync(buf, 0, read);
                                    }
                                    sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                                    string actualHash = BitConverter.ToString(sha.Hash!).Replace("-", "");
                                    if (count != expectedSize || !string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                                        throw new CryptographicException("Dimensione o SHA256 non corrispondenti al manifest firmato.");
                                    output.Flush(true);
                                }
                            }
                            if (File.Exists(final)) File.Delete(final);
                            File.Move(temporary, final);
                            return "Installer LAB verificato e salvato in:\n" + final +
                                "\n\nInstallazione NON avviata. La versione 7.4.7 e un collaudo LAB.";
                        }
                        finally { if (File.Exists(temporary)) File.Delete(temporary); }
                    }
                }
            }
        }
    }

    internal static async Task<VerifiedUpdate> StagePublishedStableAsync()
    {
        const string channel = "stable";
        const string probeVersion = InstalledVersion;
        if (!IsTrustedKeyConfigured()) throw new InvalidOperationException("Chiave pubblica RSA non configurata.");
        var url = BaseUrl + "/api/v1/updates/check?product=" + Product +
            "&version=" + probeVersion + "&platform=" + Platform + "&channel=" + channel;
        using (var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
        using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) })
        {
            using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
            {
                if (response.StatusCode != HttpStatusCode.OK)
                    throw new InvalidOperationException("API STABLE: HTTP " + (int)response.StatusCode);
                if (response.Content.Headers.ContentLength is long headerLen && headerLen > 65536)
                    throw new InvalidOperationException("Risposta API eccessiva.");
                var body = await response.Content.ReadAsStringAsync();
                if (body.Length > 65536) throw new InvalidOperationException("Risposta API eccessiva.");
                using (var doc = JsonDocument.Parse(body))
                {
                    if (!doc.RootElement.GetProperty("update_available").GetBoolean())
                        throw new InvalidOperationException("Nessuna release STABLE disponibile.");
                    byte[] manifest = Convert.FromBase64String(doc.RootElement.GetProperty("manifest_base64").GetString() ?? "");
                    byte[] signature = Convert.FromBase64String(doc.RootElement.GetProperty("signature_base64").GetString() ?? "");
                    if (manifest.Length == 0 || manifest.Length > 65536 || signature.Length == 0 ||
                        signature.Length > 8192 || !VerifyManifest(manifest, signature))
                        throw new CryptographicException("Firma RSA del manifest non valida.");
                    using (var signed = JsonDocument.Parse(manifest))
                    {
                        var obj = signed.RootElement;
                        string remoteVersion = obj.GetProperty("version").GetString() ?? "";
                        string releaseId = obj.GetProperty("release_id").GetString() ?? "";
                        if (obj.GetProperty("product").GetString() != Product ||
                            obj.GetProperty("channel").GetString() != channel ||
                            !CheckVersion(remoteVersion, InstalledVersion) ||
                            !System.Text.RegularExpressions.Regex.IsMatch(releaseId, @"\A[a-z0-9][a-z0-9._-]{0,80}\z"))
                            throw new InvalidOperationException("Identita release STABLE inattesa.");
                        JsonElement package = default;
                        bool found = false;
                        foreach (var item in obj.GetProperty("packages").EnumerateArray())
                        {
                            if (item.GetProperty("platform").GetString() == Platform)
                            { if (found) throw new InvalidOperationException("Piattaforma duplicata."); package = item; found = true; }
                        }
                        if (!found) throw new InvalidOperationException("Installer piattaforma assente.");
                        string filename = package.GetProperty("filename").GetString() ?? "";
                        string expectedHash = package.GetProperty("sha256").GetString() ?? "";
                        long expectedSize = package.GetProperty("size").GetInt64();
                        if (filename.Length < 5 || filename.Length > 160 ||
                            filename != Path.GetFileName(filename) ||
                            filename.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                            filename.Contains("..") || !filename.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                            !System.Text.RegularExpressions.Regex.IsMatch(filename, @"^[A-Za-z0-9_.-]+$") ||
                            !System.Text.RegularExpressions.Regex.IsMatch(expectedHash, @"\A[0-9a-fA-F]{64}\z") ||
                            expectedSize < 1024 || expectedSize > 160L * 1024L * 1024L)
                            throw new InvalidOperationException("Metadati installer non validi.");
                        string expectedUrl = "/api/v1/updates/files/" + Product + "/" + channel + "/" + releaseId + "/" + filename;
                        string apiUrl = doc.RootElement.GetProperty("package").GetProperty("file_url").GetString() ?? "";
                        if (!string.Equals(apiUrl, expectedUrl, StringComparison.Ordinal))
                            throw new InvalidOperationException("Percorso download non corrispondente al manifest.");
                        var staging = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "Medialink", "MLCTIClient", "UpdateStaging", releaseId);
                        Directory.CreateDirectory(staging);
                        string final = Path.Combine(staging, filename);
                        string temporary = final + ".partial";
                        if (File.Exists(temporary)) File.Delete(temporary);
                        try
                        {
                            using (var download = await client.GetAsync(BaseUrl + expectedUrl, HttpCompletionOption.ResponseHeadersRead))
                            {
                                if (download.StatusCode != HttpStatusCode.OK) throw new InvalidOperationException("Download: HTTP " + (int)download.StatusCode);
                                if (download.Content.Headers.ContentLength is long n && n != expectedSize)
                                    throw new InvalidOperationException("Dimensione dichiarata diversa dal manifest.");
                                using (var input = await download.Content.ReadAsStreamAsync())
                                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                                using (var sha = SHA256.Create())
                                {
                                    var buf = new byte[65536]; long count = 0; int read;
                                    while ((read = await input.ReadAsync(buf, 0, buf.Length)) != 0)
                                    {
                                        count += read;
                                        if (count > expectedSize) throw new InvalidOperationException("Download oltre la dimensione firmata.");
                                        sha.TransformBlock(buf, 0, read, null, 0);
                                        await output.WriteAsync(buf, 0, read);
                                    }
                                    sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                                    string actualHash = BitConverter.ToString(sha.Hash!).Replace("-", "");
                                    if (count != expectedSize || !string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                                        throw new CryptographicException("Dimensione o SHA256 non corrispondenti al manifest firmato.");
                                    output.Flush(true);
                                }
                            }
                            if (File.Exists(final)) File.Delete(final);
                            File.Move(temporary, final);
                            // Persist the exact signed bytes for independent updater verification.
                            string manifestFile = Path.Combine(staging, filename + ".manifest.bin");
                            string signatureFile = Path.Combine(staging, filename + ".signature.bin");
                            File.WriteAllBytes(manifestFile + ".partial", manifest);
                            File.WriteAllBytes(signatureFile + ".partial", signature);
                            if (File.Exists(manifestFile)) File.Delete(manifestFile);
                            if (File.Exists(signatureFile)) File.Delete(signatureFile);
                            File.Move(manifestFile + ".partial", manifestFile);
                            File.Move(signatureFile + ".partial", signatureFile);
                            return new VerifiedUpdate(final, expectedHash, remoteVersion, manifestFile, signatureFile);
                        }
                        finally { if (File.Exists(temporary)) File.Delete(temporary); }
                    }
                }
            }
        }
    }

    internal static async Task<string?> CheckAsync(bool manual)
    {
        if (!await SingleCheck.WaitAsync(0)) return manual ? "Verifica già in corso." : null;
        try
        {
            if (!manual && !IsDue()) return null;
            // Query values are product/platform/version only, never machine or user identifiers.
            var url = BaseUrl + "/api/v1/updates/check?product=" + Product +
                "&version=" + InstalledVersion + "&platform=" + Platform + "&channel=stable";
            using (var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
            using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) })
            using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
            {
                if (!response.IsSuccessStatusCode || (int)response.StatusCode != 200)
                    throw new InvalidOperationException("Risposta del servizio aggiornamenti non valida.");
                if (response.Content.Headers.ContentLength is long n && n > 65536)
                    throw new InvalidOperationException("Manifest troppo grande.");
                var body = await response.Content.ReadAsStringAsync();
                if (body.Length > 65536) throw new InvalidOperationException("Risposta aggiornamenti troppo grande.");
                using (var doc = JsonDocument.Parse(body))
                {
                    bool available = doc.RootElement.GetProperty("update_available").GetBoolean();
                    if (!available)
                    {
                        RecordSuccess();
                        return manual ? "MLCTIClient è aggiornato. Nessuna nuova release STABLE disponibile." : null;
                    }
                    // Fail closed. Do not trust remote metadata before checking the detached signature.
                    if (!IsTrustedKeyConfigured())
                        return manual ? "Una release è presente, ma manca la chiave pubblica di verifica. Installazione disabilitata." : null;
                    byte[] manifestBytes = Convert.FromBase64String(doc.RootElement.GetProperty("manifest_base64").GetString() ?? "");
                    byte[] signature = Convert.FromBase64String(doc.RootElement.GetProperty("signature_base64").GetString() ?? "");
                    if (manifestBytes.Length == 0 || manifestBytes.Length > 65536 || signature.Length == 0 ||
                        signature.Length > 8192 || !VerifyManifest(manifestBytes, signature))
                        throw new InvalidOperationException("Firma digitale del manifest non valida.");
                    using (var signed = JsonDocument.Parse(manifestBytes))
                    {
                        var obj = signed.RootElement;
                        if (obj.GetProperty("product").GetString() != Product ||
                            obj.GetProperty("channel").GetString() != "stable" ||
                            !CheckVersion(obj.GetProperty("version").GetString() ?? "", InstalledVersion))
                            throw new InvalidOperationException("Manifest incompatibile.");
                        bool platformFound = false;
                        foreach (var item in obj.GetProperty("packages").EnumerateArray())
                            if (item.GetProperty("platform").GetString() == Platform) platformFound = true;
                        if (!platformFound) throw new InvalidOperationException("Nessun installer compatibile.");
                        RecordSuccess();
                        return "Nuova versione firmata disponibile: " + obj.GetProperty("version").GetString() +
                            ". Installazione automatica non ancora abilitata in questa release LAB.";
                    }
                }
            }
        }
        catch (Exception e)
        {
            DiagnosticLog.Error("UPDATE_CHECK_FAILED", e);
            return manual ? "Verifica aggiornamenti non riuscita. Controllare connessione HTTPS e log." : null;
        }
        finally { SingleCheck.Release(); }
    }
}

internal sealed class VerifiedUpdate
{
    internal string ManifestPath { get; }
    internal string SignaturePath { get; }
    internal string InstallerPath { get; }
    internal string Sha256 { get; }
    internal string Version { get; }
    internal VerifiedUpdate(string path, string sha256, string version, string manifestPath, string signaturePath)
    { InstallerPath = path; Sha256 = sha256; Version = version; ManifestPath = manifestPath; SignaturePath = signaturePath; }
}
