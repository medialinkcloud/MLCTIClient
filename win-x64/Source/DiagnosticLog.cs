using System.Text;

namespace MedialinkCTIClient;

/// <summary>
/// File diagnostici locali, senza credenziali o dati di rubrica/CDR.
/// Scrive al massimo 5 MB per file, conserva gli ultimi 7 giorni.
/// Qualunque problema di logging non blocca la telefonia.
/// </summary>
internal static class DiagnosticLog
{
    private static readonly object Sync = new();
    // Production log: suppress high-frequency polling and UI noise. WARN and ERROR
    // are never suppressed. No runtime credentials or phone numbers are logged.
    private static readonly HashSet<string> NoisyInfo = new(StringComparer.Ordinal)
    {
        "CDR_QUERY_WINDOW", "CDR_PAGE", "CDR_RECENT_SCAN", "CDR_ROWS",
        "LDAP_BIND_OK", "LDAP_QUERY_RESULT", "LDAP_CONTACTS_RESULT", "UI_LDAP_ROWS",
        "TRAY_TOGGLE", "MINI_CALL_DECISION", "CTI_ANSWER_BUTTON_STATE",
        "UCM_LDAP_DUPLICATE_FOUND", "UCM_LDAP_DUPLICATE_NOT_FOUND",
        "PHONE_AMI_CONTACTS_COMPLETE", "AMI_LOCAL_CALL_STATE", "CONFIG_FIELD_DECRYPT_OK"
    };
    public static string LogDirectory => Path.Combine(ConfigStore.DirectoryPath, "Logs");

    private static string CurrentFile =>
        Path.Combine(LogDirectory, "MedialinkCTI-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log");

    public static void Info(string code, string details = "")
    {
        if (!NoisyInfo.Contains(code)) Write("INFO", code, details);
    }
    public static void Warn(string code, string details = "") => Write("WARN", code, details);
    public static void Error(string code, Exception error)
    {
        // Do not include Message or StackTrace: third-party exceptions can embed
        // passwords, URI tokens, LDAP filters, caller names or telephone numbers.
        Write("ERROR", code, $"exception={error.GetType().Name}; hresult=0x{error.HResult:X8}");
    }

    public static void OpenFolder()
    {
        Directory.CreateDirectory(LogDirectory);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = LogDirectory,
            UseShellExecute = true
        });
    }

    private static void Write(string level, string code, string details)
    {
        // code/details are constant text and non-sensitive counts only.
        // Never pass passwords, query text, DNs, phone numbers, or raw exceptions.
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(LogDirectory);
                var file = CurrentFile;
                if (File.Exists(file) && new FileInfo(file).Length >= 5 * 1024 * 1024)
                {
                    var archive = Path.Combine(LogDirectory,
                        $"MedialinkCTI-{DateTime.Now:yyyy-MM-dd-HHmmss-fff}.old.log");
                    File.Move(file, archive);
                }
                File.AppendAllText(file,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {code}" +
                    (details.Length > 0 ? " | " + details : "") + Environment.NewLine,
                    Encoding.UTF8);

                foreach (var old in Directory.EnumerateFiles(LogDirectory, "MedialinkCTI-*.log"))
                {
                    try
                    {
                        if (File.GetLastWriteTime(old) < DateTime.Now.AddDays(-7))
                            File.Delete(old);
                    }
                    catch { /* File in use: clean up next time */ }
                }
            }
        }
        catch { /* Never block the UI for a log write error */ }
    }
}
