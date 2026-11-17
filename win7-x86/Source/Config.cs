using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MedialinkCTIClient;

internal sealed class AppConfig
{
    public bool StartWithWindows { get; set; } = false;
    public bool KeepPopupOnTop { get; set; } = false;
    public string BlfExtensions { get; set; } = "";
    public string PresenceAvatarPath { get; set; } = "";
    public string BlfPickupPrefix { get; set; } = "**";
    // HTTPS SIP Presence refresh rate (seconds), configured in BLF settings.
    public int PresenceRefreshSeconds { get; set; } = 15;
    // Disabled by default. External URIs always require manual confirmation;
    // tel:/callto: associations remain an explicit Windows user choice.
    public bool EnableExternalClickToDial { get; set; } = false;
    // Separate, opt-in Windows-wide shortcut for the selection in Outlook/browser/text editors.
    public bool EnableExternalHotkey { get; set; } = false;
    public string ExternalHotkeyGesture { get; set; } = "Ctrl+D";
    // Single setting for URI protocols and selection hotkey. Legacy installs default to confirmation ON.
    public bool ExternalDialRequireConfirmation { get; set; } = true;
    // Used for outbound composition across manual dialing, CDR, directory and external CTD.
    // A domestic number stays untouched; +CC / 00CC of the selected country is stripped.
    public string DialCountryIso { get; set; } = "IT";
    // PBX trunk seizure code: optional, digits only; not applied to short internal extensions.
    public string ExternalLineAccessPrefix { get; set; } = "";
    // Maximum CDR items retained in memory; older settings.json files default to 150.
    public int CdrMaxRecords { get; set; } = 150;
    // 0 means no automatic close. Timeout hides only the mini popup, never ends a call.
    public int MiniPopupTimeoutSeconds { get; set; } = 20;
    // On by default: identical SIP auto-answer headers to the proven Medialink Service Desk click-to-dial.
    // This applies only to a NEW outbound call; it cannot answer a separate incoming call already ringing.
    public bool AutoAnswerClickToDial { get; set; } = true;
    // Migration marker: old v0.1.6 settings stored AutoAnswer=false by default.
    // Existing clients are upgraded to the requested AutoAnswer behavior once, without rewriting settings.json.
    // After saving, a user can intentionally disable AutoAnswer and that choice will be preserved.
    public int DialPreferenceSchemaVersion { get; set; } = 0;
    // Remote answering is device-specific, never implied by UCM AMI.
    public string PhoneType { get; set; } = "Grandstream"; // Grandstream, Snom, Yealink, Altro
    public bool AutoDetectPhoneAddress { get; set; } = true; // Use live AMI SIP registration, no static IP required.
    public string PhoneAddress { get; set; } = ""; // Optional manual fallback when auto-detection is disabled.
    public string PhoneCtiProtocol { get; set; } = "Automatico"; // Automatico, HTTP, HTTPS
    public int PhoneCtiPort { get; set; } = 0; // 0 = default 80/443 or port from same-device redirect
    public bool AllowUntrustedPhoneCertificate { get; set; } = false; // Explicit LAN lab exception only
    public string PhoneControlPasswordProtected { get; set; } = "";
    [JsonIgnore] public string PhoneControlPassword { get; set; } = "";
    // Snom D-Series authenticates with HTTP Digest. Stored independently of Grandstream CTI passcode.
    public string YealinkWebUsername { get; set; } = "";
    public string YealinkWebPasswordProtected { get; set; } = "";
    [JsonIgnore] public string YealinkWebPassword { get; set; } = "";
    public string SnomWebUsername { get; set; } = "admin";
    public string SnomWebPasswordProtected { get; set; } = "";
    [JsonIgnore] public string SnomWebPassword { get; set; } = "";
    public string Extension { get; set; } = "11";
    // Optional display label for the logged-in extension, e.g. Antonio.
    // If blank, the client attempts an exact LDAP lookup of the extension.
    public string ExtensionDisplayName { get; set; } = "";
    public string AmiHost { get; set; } = "192.168.74.160";
    public int AmiPort { get; set; } = 7777;
    public bool AmiTls { get; set; } = false;
    public string AmiUser { get; set; } = "";
    public string AmiSecretProtected { get; set; } = "";
    [JsonIgnore] public string AmiSecret { get; set; } = "";
    public int InternalExtensionMaxLength { get; set; } = 4;
    public string InternalContext { get; set; } = "from-internal";
    public string OutboundContext { get; set; } = "outbound-allroutes";

    public string LdapHost { get; set; } = "192.168.74.160";
    public int LdapPort { get; set; } = 389;
    public bool LdapSsl { get; set; } = false;
    public string LdapBaseDn { get; set; } = "dc=pbx,dc=com";
    // Explicit destination for CDR -> UCM LDAP writes. Empty means feature disabled.
    public string LdapWritePhonebookDn { get; set; } = "";
    // Explicit opt-in: API addContact/updateContact are disabled by default.
    // LDAP reading and caller name presentation remain active independently.
    public bool EnableLdapContactEditing { get; set; } = false;
    public string LdapUser { get; set; } = "";
    // UCM LDAP query templates: % is replaced by the escaped search term.
    public string LdapNameFilter { get; set; } = "(CallerIDName=%)";
    public string LdapNumberFilter { get; set; } = "(|(AccountNumber=%)(MobileNumber=%)(HomeNumber=%))";
    public string LdapNameAttributes { get; set; } = "CallerIDName FirstName LastName";
    public string LdapNumberAttributes { get; set; } = "AccountNumber MobileNumber HomeNumber";
    public string LdapSecretProtected { get; set; } = "";
    [JsonIgnore] public string LdapSecret { get; set; } = "";

    public string ApiHost { get; set; } = "192.168.74.160";
    public int ApiPort { get; set; } = 8089;
    public string ApiUser { get; set; } = "";
    public string ApiSecretProtected { get; set; } = "";
    [JsonIgnore] public string ApiSecret { get; set; } = "";
    public bool AllowUntrustedApiCertificate { get; set; } = false;
}

internal static class ConfigStore
{
    public static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Medialink", "CTIClient");
    public static readonly string FilePath = Path.Combine(DirectoryPath, "settings.json");

    public static AppConfig Load()
    {
        if (!File.Exists(FilePath))
        {
            DiagnosticLog.Warn("CONFIG_NOT_FOUND", "defaults_used=true");
            return new AppConfig();
        }
        DiagnosticLog.Info("CONFIG_FOUND");
        AppConfig config;
        try
        {
            config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath))
                ?? throw new InvalidDataException("File impostazioni vuoto.");
        }
        catch (Exception e)
        {
            DiagnosticLog.Error("CONFIG_PARSE_FAILED", e);
            throw;
        }
        if (config.DialPreferenceSchemaVersion < 1)
        {
            config.AutoAnswerClickToDial = true;
            config.DialPreferenceSchemaVersion = 1;
            DiagnosticLog.Info("DIAL_AUTOANSWER_LEGACY_CONFIG_ENABLED", "legacy=pre-v0.1.7; config_file_unchanged=true");
        }
        try
        {
            config.AmiSecret = UnprotectField(config.AmiSecretProtected, "AMI");
            config.LdapSecret = UnprotectField(config.LdapSecretProtected, "LDAP");
            config.ApiSecret = UnprotectField(config.ApiSecretProtected, "CDR");
            config.PhoneControlPassword = UnprotectField(config.PhoneControlPasswordProtected, "PHONE");
            config.SnomWebPassword = UnprotectField(config.SnomWebPasswordProtected, "SNOM");
            config.YealinkWebPassword = UnprotectField(config.YealinkWebPasswordProtected, "YEALINK");
        }
        catch (Exception e)
        {
            DiagnosticLog.Error("CONFIG_DECRYPT_FAILED", e);
            throw; // Never silently replace a saved configuration with blanks.
        }
        DiagnosticLog.Info("CONFIG_LOAD_OK",
            $"ext_set={!string.IsNullOrWhiteSpace(config.Extension)}; ami_user_set={!string.IsNullOrWhiteSpace(config.AmiUser)}; " +
            $"ldap_user_set={!string.IsNullOrWhiteSpace(config.LdapUser)}; cdr_user_set={!string.IsNullOrWhiteSpace(config.ApiUser)}; " +
            $"ami_secret_set={!string.IsNullOrWhiteSpace(config.AmiSecretProtected)}; " +
            $"ldap_secret_set={!string.IsNullOrWhiteSpace(config.LdapSecretProtected)}; " +
            $"cdr_secret_set={!string.IsNullOrWhiteSpace(config.ApiSecretProtected)}");
        return config;
    }

    // Do not log the password or the ciphertext, including on errors.
    private static string UnprotectField(string encrypted, string field)
    {
        try
        {
            string value = Dpapi.Unprotect(encrypted);
            DiagnosticLog.Info("CONFIG_FIELD_DECRYPT_OK", "field=" + field);
            return value;
        }
        catch (Exception e)
        {
            DiagnosticLog.Error("CONFIG_FIELD_DECRYPT_FAILED_" + field, e);
            throw;
        }
    }

    public static void Save(AppConfig config)
    {
        Directory.CreateDirectory(DirectoryPath);
        if (File.Exists(FilePath))
        {
            try
            {
                File.Copy(FilePath, FilePath + ".bak", true);
                DiagnosticLog.Info("CONFIG_BACKUP_OK");
            }
            catch (Exception e)
            {
                DiagnosticLog.Error("CONFIG_BACKUP_FAILED", e);
                throw new IOException("Impossibile creare backup impostazioni: salvataggio annullato.", e);
            }
        }
        config.AmiSecretProtected = Dpapi.Protect(config.AmiSecret);
        config.LdapSecretProtected = Dpapi.Protect(config.LdapSecret);
        config.ApiSecretProtected = Dpapi.Protect(config.ApiSecret);
        config.PhoneControlPasswordProtected = Dpapi.Protect(config.PhoneControlPassword);
        config.SnomWebPasswordProtected = Dpapi.Protect(config.SnomWebPassword);
        config.YealinkWebPasswordProtected = Dpapi.Protect(config.YealinkWebPassword);
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, json, Encoding.UTF8);
        LegacyCompat.MoveOverwrite(temp, FilePath);
        DiagnosticLog.Info("CONFIG_SAVE_OK");
    }
}

/// <summary>
/// Standard Windows user-scoped DPAPI. Uses the same no-entropy/current-user
/// mechanism as the historical configuration format and the verified PowerShell
/// diagnostic. No conversion or migration of settings.json is necessary.
/// </summary>
internal static class Dpapi
{
    public static string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return "";
        byte[] raw = Encoding.UTF8.GetBytes(plaintext);
        try
        {
            byte[] encrypted = ProtectedData.Protect(raw, null, DataProtectionScope.CurrentUser);
            try { return Convert.ToBase64String(encrypted); }
            finally { LegacyCompat.ZeroMemory(encrypted); }
        }
        finally { LegacyCompat.ZeroMemory(raw); }
    }

    public static string Unprotect(string ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext)) return "";
        byte[] raw = Convert.FromBase64String(ciphertext);
        try
        {
            byte[] decrypted = ProtectedData.Unprotect(raw, null, DataProtectionScope.CurrentUser);
            try { return Encoding.UTF8.GetString(decrypted); }
            finally { LegacyCompat.ZeroMemory(decrypted); }
        }
        finally { LegacyCompat.ZeroMemory(raw); }
    }
}
