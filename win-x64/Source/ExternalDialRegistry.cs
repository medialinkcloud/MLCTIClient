using Microsoft.Win32;

namespace MedialinkCTIClient;

/// <summary>
/// ADVERTISES only tel:/callto: as optional Windows Default Apps choices.
/// Does not register mlcti:, override UserChoice or hijack existing defaults.
/// Removes an old mlcti: association only when it is provably ours.
/// </summary>
internal static class ExternalDialRegistry
{
    private const string Classes = @"Software\Classes\";
    private const string AppPrefix = "MedialinkCTIClient.";
    private const string CapabilitiesPath = @"Software\Medialink\CTIClient\Capabilities";
    private const string RegisteredApplications = @"Software\RegisteredApplications";
    private const string RegisteredName = "Medialink CTI Client";

    private static string ExecutablePath()
    {
        string path = Environment.ProcessPath ?? "";
        if (string.IsNullOrWhiteSpace(path) ||
            !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new InvalidOperationException("Eseguibile Windows necessario per registrare Click-to-Dial.");
        return path;
    }

    internal static string CommandFor(string path) => "\"" + path + "\" --external-dial \"%1\"";

    // Match only a complete historic command for our executable, not another app.
    // This also fixes the old match which missed MLCTIClient.exe.
    private static bool IsOurCommand(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return System.Text.RegularExpressions.Regex.IsMatch(value.Trim(),
            @"^""[^""]*\\(?:MLCTIClient|MedialinkCTI)\.exe""\s+--external-dial\s+""%1""$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }

    public static void RemoveLegacyMlctiIfOwned()
    {
        const string legacyKey = @"Software\Classes\mlcti";
        using (var key = Registry.CurrentUser.OpenSubKey(legacyKey, false))
        {
            if (key is null) return;
            if (!string.Equals(key.GetValue(null) as string, "URL:Medialink CTI Client",
                    StringComparison.OrdinalIgnoreCase) ||
                !IsOurCommand(ReadCommand(legacyKey)))
            {
                DiagnosticLog.Info("EXTERNAL_DIAL_LEGACY_MLCTI_PRESERVED", "reason=other_owner_or_unverified");
                return;
            }
        }
        Registry.CurrentUser.DeleteSubKeyTree(legacyKey, false);
        DiagnosticLog.Info("EXTERNAL_DIAL_LEGACY_MLCTI_REMOVED", "user_scope=true");
    }

    private static string? ReadCommand(string keyPath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath + @"\shell\open\command", false);
        return key?.GetValue(null) as string;
    }

    private static void RemoveIfOwned(string className)
    {
        string path = Classes + className;
        if (IsOurCommand(ReadCommand(path)))
            Registry.CurrentUser.DeleteSubKeyTree(path, false);
    }

    public static void Configure(bool enabled)
    {
        // Migration is safe even when another program owns mlcti:.
        RemoveLegacyMlctiIfOwned();
        if (!enabled)
        {
            // Remove ONLY our own registrations. Never delete a different app's protocol.
            foreach (string cls in new[] { AppPrefix + "tel", AppPrefix + "callto" })
                RemoveIfOwned(cls);
            using (var registered = Registry.CurrentUser.OpenSubKey(RegisteredApplications, writable: true))
            {
                if (string.Equals(registered?.GetValue(RegisteredName) as string,
                    CapabilitiesPath, StringComparison.OrdinalIgnoreCase))
                    registered?.DeleteValue(RegisteredName, false);
            }
            using (var caps = Registry.CurrentUser.OpenSubKey(CapabilitiesPath, false))
            {
                if (string.Equals(caps?.GetValue("ApplicationName") as string,
                    RegisteredName, StringComparison.Ordinal))
                    Registry.CurrentUser.DeleteSubKeyTree(CapabilitiesPath, false);
            }
            DiagnosticLog.Info("EXTERNAL_DIAL_REGISTRATION", "enabled=false; user_scope=true");
            return;
        }

        string command = CommandFor(ExecutablePath());
        // Separate, proprietary ProgIDs do not alter tel: or callto: associations.
        foreach (var scheme in new[] { "tel", "callto" })
        {
            string progId = AppPrefix + scheme;
            using var key = Registry.CurrentUser.CreateSubKey(Classes + progId, true)
                ?? throw new IOException("Impossibile registrare " + progId);
            key.SetValue(null, "URL:Medialink CTI " + scheme, RegistryValueKind.String);
            key.SetValue("URL Protocol", "", RegistryValueKind.String);
            using var open = key.CreateSubKey(@"shell\open\command", true)
                ?? throw new IOException("Impossibile configurare " + progId);
            open.SetValue(null, command, RegistryValueKind.String);
        }
        using (var caps = Registry.CurrentUser.CreateSubKey(CapabilitiesPath, true)
            ?? throw new IOException("Impossibile registrare le capabilities Windows"))
        {
            caps.SetValue("ApplicationName", RegisteredName, RegistryValueKind.String);
            caps.SetValue("ApplicationDescription", "Click-to-Dial VoIP Medialink", RegistryValueKind.String);
            using var assoc = caps.CreateSubKey("URLAssociations", true)
                ?? throw new IOException("Impossibile registrare le associazioni URL");
            assoc.SetValue("tel", AppPrefix + "tel", RegistryValueKind.String);
            assoc.SetValue("callto", AppPrefix + "callto", RegistryValueKind.String);
        }
        using (var registered = Registry.CurrentUser.CreateSubKey(RegisteredApplications, true)
            ?? throw new IOException("Impossibile pubblicare l'app nelle impostazioni Windows"))
            registered.SetValue(RegisteredName, CapabilitiesPath, RegistryValueKind.String);
        DiagnosticLog.Info("EXTERNAL_DIAL_REGISTRATION",
            "enabled=true; user_scope=true; tel_callto=available_as_user_choice");
    }

    internal static bool SelfTest() =>
        CommandFor(@"C:\Program Files\Medialink\MedialinkCTI.exe") ==
        "\"C:\\Program Files\\Medialink\\MedialinkCTI.exe\" --external-dial \"%1\"" &&
        IsOurCommand("\"C:\\MedialinkCTI.exe\" --external-dial \"%1\"") &&
        IsOurCommand("\"C:\\MLCTIClient.exe\" --external-dial \"%1\"") &&
        !IsOurCommand("\"C:\\OtherPhone.exe\" \"%1\"");
}
