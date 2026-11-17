using Microsoft.Win32;

namespace MedialinkCTIClient;

/// <summary>Startup per il SOLO utente corrente (HKCU Run); non richiede amministratore.</summary>
internal static class WindowsStartup
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MedialinkCTIClient";

    public static string? ExistingCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
        return key?.GetValue(ValueName) as string;
    }

    public static bool IsCurrentExecutableRegistered()
    {
        var expected = QuoteExecutable();
        return string.Equals(ExistingCommand()?.Trim(), expected,
            StringComparison.OrdinalIgnoreCase);
    }

    public static void Configure(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true)
            ?? throw new InvalidOperationException("Impossibile aprire la chiave Avvio di Windows.");
        if (enabled) key.SetValue(ValueName, QuoteExecutable(), RegistryValueKind.String);
        else key.DeleteValue(ValueName, false);
        DiagnosticLog.Info("AUTOSTART_UPDATED", "enabled=" + enabled);
    }

    public static void Restore(string? previous)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true)
            ?? throw new InvalidOperationException("Impossibile ripristinare la chiave Avvio di Windows.");
        if (previous is null) key.DeleteValue(ValueName, false);
        else key.SetValue(ValueName, previous, RegistryValueKind.String);
    }

    private static string QuoteExecutable()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Percorso eseguibile Windows non valido per l'avvio automatico.");
        return "\"" + path + "\"";
    }
}
