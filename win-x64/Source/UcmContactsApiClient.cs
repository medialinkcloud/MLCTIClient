using System.Net.Http.Json;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MedialinkCTIClient;

internal sealed record UcmWriteOutcome(bool CommandAccepted, bool ApplyAcknowledged, bool ApplyTimedOut);

/// <summary>
/// UCM HTTPS API contact writer. The login follows the validated CDR client.
/// Only explicit user actions can mutate LDAP. An applyChanges timeout is
/// INDETERMINATE, not a failed add/edit; never automatically replay a write.
/// </summary>
internal sealed class UcmContactsApiClient
{
    public async Task<UcmWriteOutcome> SaveDetailsAsync(AppConfig cfg, UcmContactDetails details,
        UcmContactDetails? original, CancellationToken ct = default)
    {
        DemoLicenseGate.Require();
        if (!cfg.EnableLdapContactEditing)
            throw new InvalidOperationException("Aggiunta e modifica LDAP disabilitate nelle Impostazioni > Rubrica LDAP.");
        UcmContactWriteRules.ValidateDetails(details, cfg.InternalExtensionMaxLength, original is not null);
        if (string.IsNullOrWhiteSpace(cfg.ApiHost) || cfg.ApiPort is < 1 or > 65535 ||
            string.IsNullOrWhiteSpace(cfg.ApiUser) || string.IsNullOrEmpty(cfg.ApiSecret))
            throw new InvalidOperationException("Configurare l'account API UCM con permessi Contatti.");
        if (IPAddress.TryParse(cfg.LdapHost, out var ldapIp) &&
            IPAddress.TryParse(cfg.ApiHost, out var apiIp) && !ldapIp.Equals(apiIp))
            throw new InvalidOperationException("Server API e LDAP diversi: scrittura bloccata.");
        if (original is not null)
        {
            UcmContactWriteRules.ValidateDestinationDn(original.PhonebookDn);
            if (!string.Equals(original.PhonebookDn, details.PhonebookDn, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Spostamento fra rubriche non supportato dalla modifica.");
            if (original.AccountNumber.Length == 0 || !original.AccountNumber.All(char.IsAsciiDigit))
                throw new InvalidOperationException("Account Number originale assente: modifica non sicura.");
        }

        using var handler = new HttpClientHandler();
        if (cfg.AllowUntrustedApiCertificate)
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(90) };
        string url = $"https://{cfg.ApiHost}:{cfg.ApiPort}/api";
        string cookie = "";
        bool accepted = false;
        string operation = original is null ? "addContact" : "updateContact";
        try
        {
            using (var challenge = await PostAsync(http, url,
                new { request = new { action = "challenge", user = cfg.ApiUser, version = "1.0" } }, ct))
            {
                var value = GetNested(challenge.RootElement, "response", "challenge");
                if (value.Length == 0) throw new InvalidOperationException("API UCM: challenge assente.");
                var token = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(value + cfg.ApiSecret)))
                    .ToLowerInvariant();
                using var login = await PostAsync(http, url,
                    new { request = new { action = "login", user = cfg.ApiUser, token } }, ct);
                cookie = GetNested(login.RootElement, "response", "cookie");
                if (cookie.Length == 0) throw new InvalidOperationException("Login API UCM non riuscito.");
            }
            var request = BuildWriteRequest(cookie, details, original);

            using (var answer = await PostAsync(http, url, new { request }, ct))
            {
                EnsureAcceptedStatus(answer.RootElement, operation);
                accepted = true;
            }
            try
            {
                using var apply = await PostAsync(http, url,
                    new { request = new { action = "applyChanges", cookie } }, ct);
                EnsureAcceptedStatus(apply.RootElement, "applyChanges");
                DiagnosticLog.Info("UCM_LDAP_CONTACT_SAVED", "operation=" + operation + "; apply=ack");
                return new UcmWriteOutcome(true, true, false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // UCM may continue applying changes after the HTTP client stops waiting.
                DiagnosticLog.Warn("UCM_LDAP_APPLY_INDETERMINATE", "operation=" + operation + "; timeout=90s");
                return new UcmWriteOutcome(true, false, true);
            }
            catch (Exception e)
            {
                DiagnosticLog.Error("UCM_LDAP_APPLY_FAILED", e);
                throw new InvalidOperationException("Comando di modifica accettato, ma applicazione delle modifiche non confermata. " +
                    "Controllare il contatto sulla UCM prima di riprovare.", e);
            }
        }
        catch (Exception e)
        {
            if (!accepted) DiagnosticLog.Error("UCM_LDAP_CONTACT_REQUEST_FAILED", e);
            if (!accepted && e is HttpRequestException)
                throw new InvalidOperationException("Connessione HTTPS API fallita: controllare server, porta e certificato.", e);
            throw;
        }
        finally
        {
            if (cookie.Length > 0)
            {
                try
                {
                    using var logoutTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    using var _ = await PostAsync(http, url,
                        new { request = new { action = "logout", cookie } }, logoutTimeout.Token);
                }
                catch { /* No retry or altered write outcome on logout failure. */ }
            }
        }
    }

    /// <summary>
    /// Delete exactly one pre-identified UCM LDAP object. Never retries a mutation.
    /// The UI must re-read the object and obtain explicit confirmation first.
    /// </summary>
    public async Task<UcmWriteOutcome> DeleteContactAsync(AppConfig cfg, UcmContactDetails original,
        CancellationToken ct = default)
    {
        DemoLicenseGate.Require();
        if (!cfg.EnableLdapContactEditing)
            throw new InvalidOperationException("Eliminazione LDAP disabilitata nelle Impostazioni > Rubrica LDAP.");
        UcmContactWriteRules.ValidateDestinationDn(original.PhonebookDn);
        if (original.AccountNumber.Length == 0 || original.AccountNumber.Length > 20 ||
            !original.AccountNumber.All(char.IsAsciiDigit))
            throw new InvalidOperationException("Account Number originale non valido: eliminazione bloccata.");
        if (string.IsNullOrWhiteSpace(cfg.ApiHost) || cfg.ApiPort is < 1 or > 65535 ||
            string.IsNullOrWhiteSpace(cfg.ApiUser) || string.IsNullOrEmpty(cfg.ApiSecret))
            throw new InvalidOperationException("Configurare un utente API con il permesso ldapServer/deleteContact.");
        if (IPAddress.TryParse(cfg.LdapHost, out var ldapIp) &&
            IPAddress.TryParse(cfg.ApiHost, out var apiIp) && !ldapIp.Equals(apiIp))
            throw new InvalidOperationException("Server API e LDAP diversi: eliminazione bloccata.");

        using var handler = new HttpClientHandler();
        if (cfg.AllowUntrustedApiCertificate)
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(90) };
        string url = $"https://{cfg.ApiHost}:{cfg.ApiPort}/api";
        string cookie = "";
        bool accepted = false;
        try
        {
            using (var challenge = await PostAsync(http, url,
                new { request = new { action = "challenge", user = cfg.ApiUser, version = "1.0" } }, ct))
            {
                var value = GetNested(challenge.RootElement, "response", "challenge");
                if (value.Length == 0) throw new InvalidOperationException("API UCM: challenge assente.");
                var token = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(value + cfg.ApiSecret)))
                    .ToLowerInvariant();
                using var login = await PostAsync(http, url,
                    new { request = new { action = "login", user = cfg.ApiUser, token } }, ct);
                cookie = GetNested(login.RootElement, "response", "cookie");
                if (cookie.Length == 0) throw new InvalidOperationException("Login API UCM non riuscito.");
            }
            var request = BuildDeleteRequest(cookie, original);
            using (var answer = await PostAsync(http, url, new { request }, ct))
            {
                EnsureAcceptedStatus(answer.RootElement, "deleteContact");
                accepted = true;
            }
            try
            {
                using var apply = await PostAsync(http, url,
                    new { request = new { action = "applyChanges", cookie } }, ct);
                EnsureAcceptedStatus(apply.RootElement, "applyChanges");
                DiagnosticLog.Info("UCM_LDAP_CONTACT_DELETED", "apply=ack");
                return new UcmWriteOutcome(true, true, false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                DiagnosticLog.Warn("UCM_LDAP_DELETE_APPLY_INDETERMINATE", "timeout=90s");
                return new UcmWriteOutcome(true, false, true);
            }
            catch (Exception e)
            {
                DiagnosticLog.Error("UCM_LDAP_DELETE_APPLY_FAILED", e);
                throw new InvalidOperationException("deleteContact accettato, ma applyChanges non confermato. " +
                    "Verificare sulla UCM prima di ripetere l'eliminazione.", e);
            }
        }
        catch (Exception e)
        {
            if (!accepted) DiagnosticLog.Error("UCM_LDAP_DELETE_REQUEST_FAILED", e);
            if (!accepted && e is HttpRequestException)
                throw new InvalidOperationException("Connessione HTTPS API fallita: server, porta o certificato.", e);
            throw;
        }
        finally
        {
            if (cookie.Length > 0)
            {
                try
                {
                    using var logoutTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    using var _ = await PostAsync(http, url,
                        new { request = new { action = "logout", cookie } }, logoutTimeout.Token);
                }
                catch { /* No retry of deleteContact on logout error. */ }
            }
        }
    }

    internal static Dictionary<string, object> BuildDeleteRequest(string cookie, UcmContactDetails original)
    {
        UcmContactWriteRules.ValidateDestinationDn(original.PhonebookDn);
        if (original.AccountNumber.Length == 0 || original.AccountNumber.Length > 20 ||
            !original.AccountNumber.All(char.IsAsciiDigit))
            throw new InvalidOperationException("Identificativo contatto non valido per eliminazione.");
        var identity = new { phonebook_dn = original.PhonebookDn, accountnumber = original.AccountNumber };
        // Grandstream documentation includes this second field when the object
        // has ring-group associations. Same identity; never affects other objects.
        return new Dictionary<string, object>
        {
            ["action"] = "deleteContact", ["cookie"] = cookie,
            ["ldap_contacts"] = identity, ["ringgroup_mem_exten"] = identity
        };
    }

    internal static Dictionary<string, object> BuildWriteRequest(string cookie, UcmContactDetails details,
        UcmContactDetails? original)
    {
        string operation = original is null ? "addContact" : "updateContact";
        var request = new Dictionary<string, object>
        {
            ["action"] = operation, ["cookie"] = cookie,
            ["accountnumber"] = details.AccountNumber,
            ["calleridname"] = details.CallerIdName,
            ["firstname"] = details.FirstName, ["lastname"] = details.LastName,
            ["email"] = details.Email, ["fax"] = details.Fax,
            ["department"] = details.Department, ["position"] = details.Position,
            ["homenumber"] = details.HomeNumber, ["mobilenumber"] = details.MobileNumber
        };
        if (original is null)
            request["phonebook_dn"] = details.PhonebookDn;
        else
            request["ldap_contacts"] = new { phonebook_dn = original.PhonebookDn,
                accountnumber = original.AccountNumber };
        // Firmware-dependent optional field present in Grandstream updateContact example.
        if (details.FamilyNumber.Length > 0)
            request["family_number"] = details.FamilyNumber;
        return request;
    }

    private static void EnsureAcceptedStatus(JsonElement root, string action)
    {
        if (root.TryGetProperty("status", out var top) && top.ValueKind == JsonValueKind.Number &&
            top.TryGetInt32(out int code) && code == 0) return;
        if (root.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.Object &&
            response.TryGetProperty("status", out var nested) && nested.ValueKind == JsonValueKind.Number &&
            nested.TryGetInt32(out int inner) && inner == 0) return;
        throw new InvalidDataException("Risposta UCM priva di conferma " + action + ". Verificare sulla UCM.");
    }

    private static string GetNested(JsonElement root, string parent, string key)
    {
        if (root.TryGetProperty(parent, out var x) && x.ValueKind == JsonValueKind.Object &&
            x.TryGetProperty(key, out var y)) return y.ToString();
        return "";
    }

    private static async Task<JsonDocument> PostAsync(HttpClient http, string url, object data, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(url, data, cancellationToken: ct);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;
        JsonElement status = default;
        if (root.TryGetProperty("status", out var top)) status = top;
        else if (root.TryGetProperty("response", out var inside) && inside.ValueKind == JsonValueKind.Object &&
                 inside.TryGetProperty("status", out var nested)) status = nested;
        if (status.ValueKind == JsonValueKind.Number && status.TryGetInt32(out int code) && code != 0)
        {
            doc.Dispose();
            throw new InvalidOperationException($"La UCM ha rifiutato il comando API (codice {code}).");
        }
        return doc;
    }

    public static bool SelfTest()
    {
        using var success = JsonDocument.Parse("{\"response\":{\"need_apply\":\"yes\"},\"status\":0}");
        if (GetNested(success.RootElement, "response", "need_apply") != "yes" ||
            !UcmContactWriteRules.SelfTest()) return false;
        var before = UcmContactDetails.New("ou=medialink,dc=pbx,dc=com", "0444123456", "Originale");
        var after = before.Copy();
        after.AccountNumber = "0444999888";
        var deleted = BuildDeleteRequest("TEST_COOKIE", before);
        using var deleteJson = JsonDocument.Parse(JsonSerializer.Serialize(deleted));
        var deleteRoot = deleteJson.RootElement;
        if (deleteRoot.GetProperty("action").GetString() != "deleteContact" ||
            deleteRoot.GetProperty("ldap_contacts").GetProperty("phonebook_dn").GetString() != before.PhonebookDn ||
            deleteRoot.GetProperty("ldap_contacts").GetProperty("accountnumber").GetString() != before.AccountNumber ||
            deleteRoot.GetProperty("ringgroup_mem_exten").GetProperty("accountnumber").GetString() != before.AccountNumber ||
            !deleteRoot.TryGetProperty("cookie", out _)) return false;
        try
        {
            BuildDeleteRequest("TEST_COOKIE", new UcmContactDetails {
                PhonebookDn = "ou=pbx,dc=pbx,dc=com", AccountNumber = "62" });
            return false;
        }
        catch (InvalidOperationException) { /* system LDAP is read-only */ }
        var add = BuildWriteRequest("TEST_COOKIE", before, null);
        var change = BuildWriteRequest("TEST_COOKIE", after, before);
        if (add.GetValueOrDefault("action") as string != "addContact" ||
            add.GetValueOrDefault("phonebook_dn") as string != before.PhonebookDn ||
            change.GetValueOrDefault("action") as string != "updateContact" ||
            change.ContainsKey("phonebook_dn") || !change.ContainsKey("ldap_contacts")) return false;
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(change));
        var contact = json.RootElement.GetProperty("ldap_contacts");
        return contact.GetProperty("phonebook_dn").GetString() == before.PhonebookDn &&
            contact.GetProperty("accountnumber").GetString() == before.AccountNumber &&
            json.RootElement.GetProperty("accountnumber").GetString() == after.AccountNumber;
    }
}
