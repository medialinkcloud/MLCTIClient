using System.Globalization;
using System.Windows.Forms;

namespace MedialinkCTIClient;

/// <summary>Regole testabili per il ciclo di vita dell'applicazione in tray.</summary>
internal static class TrayLifecycleRules
{
    // Non intercettare le chiusure di Windows, dell'applicazione o del sistema.
    internal static bool ShouldHidePanel(CloseReason reason) =>
        reason == CloseReason.UserClosing;

    internal static bool TryParseRestartArguments(string[] args, out int parentPid)
    {
        parentPid = 0;
        return args.Length == 2 &&
               string.Equals(args[0], "--restart-after-exit", StringComparison.OrdinalIgnoreCase) &&
               int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out parentPid) &&
               parentPid > 0;
    }

    internal static bool SelfTest()
    {
        return ShouldHidePanel(CloseReason.UserClosing) &&
               !ShouldHidePanel(CloseReason.ApplicationExitCall) &&
               !ShouldHidePanel(CloseReason.WindowsShutDown) &&
               !ShouldHidePanel(CloseReason.TaskManagerClosing) &&
               TryParseRestartArguments(new[] { "--restart-after-exit", "123" }, out var pid) && pid == 123 &&
               !TryParseRestartArguments(new[] { "--restart-after-exit", "0" }, out _) &&
               !TryParseRestartArguments(new[] { "--restart-after-exit", "abc" }, out _) &&
               !TryParseRestartArguments(new[] { "--restart-after-exit" }, out _);
    }
}
