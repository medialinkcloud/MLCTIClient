namespace MedialinkCTIClient;
// Central gate. Commercial credentials take precedence; DEMO remains eligible only
// where no commercial license has been configured on this Windows user profile.
internal static class DemoLicenseGate
{
    internal static bool IsAuthorized(out string reason)
    {
        try
        {
            if (CommercialLicense.IsConfigured())
                return CommercialLicense.IsAuthorized(out reason);
            var verified = DemoLicenseClient.VerifySaved();
            reason = "DEMO valida: " + verified.DaysRemaining + " giorni residui";
            return true;
        }
        catch (Exception ex)
        {
            reason = ex is FileNotFoundException
                ? "Nessuna licenza presente. Aprire Licenza commerciale oppure Attivazione Demo dal menu."
                : "Licenza assente, scaduta o non verificabile: " + ex.Message;
            return false;
        }
    }
    internal static void Require()
    {
        if (!IsAuthorized(out var reason)) throw new InvalidOperationException("CTI bloccato: " + reason);
    }
}
