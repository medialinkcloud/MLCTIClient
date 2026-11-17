using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace MedialinkCTIClient;

// RC2 STAGING: signed DEMO + machine binding, enforcement OFF until server E2E.
// Public key is administrator-controlled alongside the EXE; never load a private key.
internal static class CtiLicenseVerifier
{
    public const string ExpectedProduct = "MLCTICLIENT";
    public static string PublicKeyFile => Path.Combine(AppContext.BaseDirectory, "mlcti-public-key.json");
    public static string LicenseFile => Path.Combine(ConfigStore.DirectoryPath, "license.json");

    public static (bool SignatureValid, string Status) Verify(string licenseJson, string publicKeyJson)
    {
        try
        {
            using var license = JsonDocument.Parse(licenseJson, new JsonDocumentOptions { MaxDepth = 32 });
            using var pub = JsonDocument.Parse(publicKeyJson, new JsonDocumentOptions { MaxDepth = 8 });
            var root = license.RootElement;
            if (root.ValueKind != JsonValueKind.Object || pub.RootElement.ValueKind != JsonValueKind.Object)
                return (false, "Formato JSON non valido.");
            if (ContainsDuplicateProperty(root) || ContainsDuplicateProperty(pub.RootElement))
                return (false, "Nomi di proprietà JSON duplicati.");
            if (!TryString(root, "signature", out string signature) ||
                !TryString(root, "product", out string product) ||
                !TryString(root, "license_id", out string licenseId) ||
                !TryString(root, "expires_at", out string expiresAt))
                return (false, "Campi obbligatori mancanti.");
            if (licenseId.Length > 200 || signature.Length > 2048)
                return (false, "Dimensione campo non valida.");
            if (!TryString(pub.RootElement, "n", out var n) ||
                !TryString(pub.RootElement, "e", out var e))
                return (false, "Parametri pubblici RSA mancanti.");
            byte[] sig = Convert.FromBase64String(signature);
            byte[] modulus = Convert.FromBase64String(n);
            byte[] exponent = Convert.FromBase64String(e);
            if (modulus.Length < 256 || exponent.Length < 1 || exponent.Length > 8)
                return (false, "Dimensione chiave RSA non accettata.");
            var canonical = new StringBuilder(2048);
            WriteCanonical(root, canonical, true);
            var payload = Encoding.UTF8.GetBytes(canonical.ToString());
            bool verified;
            using (var rsa = new RSACng())
            {
                rsa.ImportParameters(new RSAParameters { Modulus = modulus, Exponent = exponent });
                verified = rsa.VerifyData(payload, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            }
            if (!verified) return (false, "Firma RSA/SHA-256 NON valida.");
            // Check business fields ONLY AFTER authentication.
            // A DEMO is always bound to the same Windows workstation, never a local install ID.
            if (TryString(root, "edition", out var edition) &&
                string.Equals(edition, "DEMO", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryString(root, "appliance_serial", out var boundHash) ||
                    !string.Equals(boundHash, CtiDemoClient.DeviceHash(), StringComparison.Ordinal))
                    return (true, "Firma valida, ma DEMO associata a un'altra postazione.");
                if (!root.TryGetProperty("trial_days", out var td) ||
                    td.ValueKind != JsonValueKind.Number || !td.TryGetInt32(out var days) ||
                    days != CtiDemoClient.TrialDays ||
                    !TryString(root, "trial_started_at", out var startText) ||
                    !DateTimeOffset.TryParse(startText, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var start) ||
                    !DateTimeOffset.TryParse(expiresAt, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var end) ||
                    end - start != TimeSpan.FromDays(CtiDemoClient.TrialDays))
                    return (true, "Firma valida, ma durata DEMO non valida.");
            }
            if (!string.Equals(product, ExpectedProduct, StringComparison.Ordinal))
                return (true, "Firma valida, ma licenza di un altro prodotto.");
            if (!string.Equals(expiresAt, "perpetual", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(expiresAt, "perpetua", StringComparison.OrdinalIgnoreCase))
            {
                DateTimeOffset expiry;
                if (DateTime.TryParseExact(expiresAt, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out DateTime expiryDate))
                {
                    expiry = new DateTimeOffset(expiryDate.Date.AddDays(1), TimeSpan.Zero).AddTicks(-1);
                }
                else if (!DateTimeOffset.TryParse(expiresAt, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out expiry))
                    return (true, "Firma valida, ma data scadenza non riconosciuta.");
                if (DateTimeOffset.UtcNow > expiry)
                    return (true, "Firma valida, ma licenza scaduta.");
            }
            return (true, "Firma, prodotto e scadenza verificati. Associazione PC da definire: licenza NON ancora abilitante.");
        }
        catch (Exception ex) when (ex is JsonException || ex is FormatException ||
                                  ex is CryptographicException || ex is ArgumentException ||
                                  ex is InvalidOperationException)
        {
            return (false, "Impossibile verificare il formato o la firma della licenza.");
        }
    }

    public static string StatusOnDisk()
    {
        try
        {
            if (!File.Exists(PublicKeyFile)) return "Chiave pubblica RSA non ancora installata.";
            if (!File.Exists(LicenseFile)) return "Licenza CTI non importata.";
            if (new FileInfo(PublicKeyFile).Length > 16384 || new FileInfo(LicenseFile).Length > 65536)
                return "File licenza o chiave troppo grande.";
            return Verify(File.ReadAllText(LicenseFile, Encoding.UTF8),
                          File.ReadAllText(PublicKeyFile, Encoding.UTF8)).Status;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("LICENSE_STATUS_READ_FAILED", ex);
            return "Impossibile leggere i file di licenza.";
        }
    }

    public static bool HasValidNonDemoLicense()
    {
        try
        {
            if (!File.Exists(LicenseFile) || !File.Exists(PublicKeyFile)) return false;
            var raw = File.ReadAllText(LicenseFile, Encoding.UTF8);
            using (var doc = JsonDocument.Parse(raw))
            {
                var root = doc.RootElement;
                if (!TryString(root, "edition", out var edition) ||
                    string.Equals(edition, "DEMO", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(edition, "TEST", StringComparison.OrdinalIgnoreCase)) return false;
            }
            var checkedLicense = Verify(raw, File.ReadAllText(PublicKeyFile, Encoding.UTF8));
            return checkedLicense.SignatureValid &&
                checkedLicense.Status.StartsWith("Firma, prodotto e scadenza verificati.", StringComparison.Ordinal);
        }
        catch { return false; }
    }

    public static (bool Success, string Message) ImportSignedJson(string json)
    {
        try
        {
            if (!File.Exists(PublicKeyFile)) return (false, "Manca la chiave pubblica Medialink.");
            if (Encoding.UTF8.GetByteCount(json) > 65536) return (false, "Licenza troppo grande.");
            var verified = Verify(json, File.ReadAllText(PublicKeyFile, Encoding.UTF8));
            if (!verified.SignatureValid ||
                !verified.Status.StartsWith("Firma, prodotto e scadenza verificati.", StringComparison.Ordinal))
                return (false, verified.Status);
            if (HasValidNonDemoLicense())
            {
                using (var document = JsonDocument.Parse(json))
                    if (TryString(document.RootElement, "edition", out var edition) &&
                        string.Equals(edition, "DEMO", StringComparison.OrdinalIgnoreCase))
                        return (false, "Non si può sostituire una licenza commerciale con una DEMO.");
            }
            Directory.CreateDirectory(ConfigStore.DirectoryPath);
            string temp = LicenseFile + ".pending";
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            if (File.Exists(LicenseFile))
            {
                string backup = LicenseFile + ".bak";
                if (File.Exists(backup)) File.Delete(backup);
                File.Replace(temp, LicenseFile, backup);
            }
            else File.Move(temp, LicenseFile);
            DiagnosticLog.Info("LICENSE_IMPORTED", "signature_valid=true; enforcement=false");
            return (true, verified.Status);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("LICENSE_IMPORT_FAILED", ex);
            return (false, "Importazione licenza non riuscita; consultare i log.");
        }
    }

    public static (bool Success, string Message) ImportFrom(string source)
    {
        try
        {
            var info = new FileInfo(source);
            if (info.Length < 10 || info.Length > 65536)
                return (false, "Dimensione licenza non valida.");
            return ImportSignedJson(File.ReadAllText(source, Encoding.UTF8));
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("LICENSE_IMPORT_FILE_FAILED", ex);
            return (false, "Impossibile leggere la licenza selezionata.");
        }
    }

    public static bool SelfTest()
    {
        string unsigned = "{\"expires_at\":\"perpetual\",\"features\":[\"dial\"],\"license_id\":\"TEST-001\",\"license_version\":1,\"product\":\"MLCTICLIENT\"}";
        using var doc = JsonDocument.Parse(unsigned);
        var canon = new StringBuilder();
        WriteCanonical(doc.RootElement, canon, true);
        if (canon.ToString() != unsigned) return false;
        using var rsa = new RSACng();
        rsa.KeySize = 2048;
        var publicParameters = rsa.ExportParameters(false);
        string signature = Convert.ToBase64String(rsa.SignData(Encoding.UTF8.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        string lic = unsigned.Substring(0, unsigned.Length-1) + ",\"signature\":\"" + signature + "\"}";
        string key = "{\"n\":\"" + Convert.ToBase64String(publicParameters.Modulus ?? throw new CryptographicException("RSA modulus missing")) + "\",\"e\":\"" + Convert.ToBase64String(publicParameters.Exponent ?? throw new CryptographicException("RSA exponent missing")) + "\"}";
        var ok = Verify(lic, key);
        if (!ok.SignatureValid || !ok.Status.StartsWith("Firma, prodotto", StringComparison.Ordinal)) return false;
        if (Verify(lic.Replace("TEST-001", "TEST-002"), key).SignatureValid) return false;
        if (Verify(lic.Replace("MLCTICLIENT", "MLCB"), key).Status.StartsWith("Firma, prodotto", StringComparison.Ordinal)) return false;
        // Text with spaces, Unicode and escaped quotes must use Python-compatible JSON canonical form.
        using var shuffled = JsonDocument.Parse("{\"product\":\"MLCTICLIENT\",\"name\":\"Città \\\"Roma\\\"\",\"signature\":\"x\",\"license_version\":1}");
        canon.Clear(); WriteCanonical(shuffled.RootElement,canon,true);
        return canon.ToString() == "{\"license_version\":1,\"name\":\"Città \\\"Roma\\\"\",\"product\":\"MLCTICLIENT\"}";
    }

    private static bool TryString(JsonElement obj, string key, out string value)
    {
        value = "";
        if (!obj.TryGetProperty(key, out var node) || node.ValueKind != JsonValueKind.String) return false;
        value = node.GetString() ?? "";
        return !string.IsNullOrWhiteSpace(value);
    }
    private static bool ContainsDuplicateProperty(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in node.EnumerateObject())
                if (!names.Add(p.Name) || ContainsDuplicateProperty(p.Value)) return true;
        }
        if (node.ValueKind == JsonValueKind.Array)
            foreach (var x in node.EnumerateArray()) if (ContainsDuplicateProperty(x)) return true;
        return false;
    }
    private static void WriteCanonical(JsonElement node, StringBuilder result, bool top)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                result.Append('{'); bool first = true;
                foreach (var p in node.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
                {
                    if (top && p.Name == "signature") continue;
                    if (!first) result.Append(','); first = false;
                    WriteString(p.Name, result); result.Append(':'); WriteCanonical(p.Value,result,false);
                }
                result.Append('}'); break;
            case JsonValueKind.Array:
                result.Append('['); bool initial = true;
                foreach (var item in node.EnumerateArray())
                {
                    if (!initial) result.Append(','); initial = false;
                    WriteCanonical(item,result,false);
                }
                result.Append(']'); break;
            case JsonValueKind.String: WriteString(node.GetString() ?? "", result); break;
            case JsonValueKind.True: result.Append("true"); break;
            case JsonValueKind.False: result.Append("false"); break;
            case JsonValueKind.Null: result.Append("null"); break;
            case JsonValueKind.Number:
                // Current license schema uses integer only. Reject float to avoid Python/.NET formatting mismatches.
                var raw = node.GetRawText();
                if (!long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long integer))
                    throw new FormatException("Il formato licenza supporta soltanto numeri interi.");
                result.Append(integer.ToString(CultureInfo.InvariantCulture)); break;
            default: throw new FormatException("Elemento JSON non consentito.");
        }
    }
    private static void WriteString(string text, StringBuilder result)
    {
        result.Append('"');
        foreach (char c in text)
        {
            switch (c)
            {
                case '"': result.Append("\\\""); break;
                case '\\': result.Append("\\\\"); break;
                case '\b': result.Append("\\b"); break;
                case '\f': result.Append("\\f"); break;
                case '\n': result.Append("\\n"); break;
                case '\r': result.Append("\\r"); break;
                case '\t': result.Append("\\t"); break;
                default:
                    if (c < 0x20) result.Append("\\u").Append(((int)c).ToString("x4",CultureInfo.InvariantCulture));
                    else result.Append(c);
                    break;
            }
        }
        result.Append('"');
    }
}

internal sealed class CtiLicenseForm : Form
{
    private readonly Label status = new();
    private readonly TextBox endpoint = new();
    public CtiLicenseForm()
    {
        Text = "Medialink CTI Client - Licenza e DEMO (staging)";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        // R7.4 Update 3: anche il dialogo licensing legacy resta visibile.
        TopMost = true;
        ShowInTaskbar = true;
        Shown += (_, _) => { BringToFront(); Activate(); };
        MaximizeBox = false; MinimizeBox = false;
        Size = new System.Drawing.Size(685, 328);
        var title = new Label { Left = 18, Top = 18, Width = 620, Height = 24,
            Text = "Stato licenza CTI e DEMO 30 giorni", Font = new System.Drawing.Font("Segoe UI", 11, System.Drawing.FontStyle.Bold) };
        status.SetBounds(18, 49, 630, 77); status.AutoEllipsis = true;
        var label = new Label { Text = "URL HTTPS del License Server Medialink:", Left = 18, Top = 135, Width = 370, Height = 18 };
        endpoint.SetBounds(18, 133, 630, 28);
        endpoint.Text = CtiDemoClient.GetSavedEndpoint();
        var demo = new Button { Text = "Attiva DEMO 30 giorni", Left = 18, Top = 207, Width = 193, Height = 34 };
        demo.Click += async (_, _) =>
        {
            demo.Enabled = false;
            try
            {
                var result = await CtiDemoClient.ActivateAsync(endpoint.Text);
                MessageBox.Show(this, result.Message, "Licensing MLCTIClient", MessageBoxButtons.OK,
                    result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                UpdateStatus();
            }
            finally { demo.Enabled = true; }
        };
        var import = new Button { Text = "Importa licenza firmata...", Left = 228, Top = 207, Width = 208, Height = 34 };
        import.Click += (_, _) =>
        {
            using (var picker = new OpenFileDialog { Filter = "Licenza JSON (*.json)|*.json", CheckFileExists = true, Multiselect = false })
            {
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                var result = CtiLicenseVerifier.ImportFrom(picker.FileName);
                MessageBox.Show(this, result.Message, "Licensing MLCTIClient", MessageBoxButtons.OK,
                    result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                UpdateStatus();
            }
        };
        var close = new Button { Text = "Chiudi", Left = 545, Top = 207, Width = 103, Height = 34 };
        close.Click += (_, _) => Close();
        Controls.Add(title); Controls.Add(status); Controls.Add(label);
        Controls.Add(endpoint); Controls.Add(demo); Controls.Add(import); Controls.Add(close);
        UpdateStatus();
    }
    private void UpdateStatus()
    {
        status.Text = CtiLicenseVerifier.StatusOnDisk() + Environment.NewLine +
            CtiDemoClient.LocalTrialSummary() + Environment.NewLine +
            "Enforcement disattivato solo durante il collaudo RC2.";
    }
}
