using System.Globalization;
using System.Text.Json;

namespace MedialinkCTIClient;

// Stage 5: non-invasive, read-only license status. NOT an entitlement gate.
// RSA validation always runs through the existing DemoLicenseVerifier.
internal static class DemoLicenseStatus
{
    internal sealed record Result(bool Valid, string Message, string? LicenseId,
        DateTimeOffset? ExpiresUtc, int? DaysRemaining);

    internal static Result Inspect()
    {
        try
        {
            var verified = DemoLicenseClient.VerifySaved();
            return new Result(true,
                $"DEMO VALIDA\nLicenza: {verified.LicenseId}\nScadenza: " +
                verified.ExpiresUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss zzz", CultureInfo.InvariantCulture) +
                $"\nGiorni residui (arrotondati): {verified.DaysRemaining}\nFirma RSA: verificata.",
                verified.LicenseId, verified.ExpiresUtc, verified.DaysRemaining);
        }
        catch (FileNotFoundException)
        {
            return new Result(false,
                "Nessuna ricevuta Demo locale. Aprire 'Licenza Demo 30G · Attivazione...' " +
                "per recuperare dal server la licenza eventualmente gia emessa.\n" +
                "Nessun controllo CTI viene modificato da Stage 5.", null, null, null);
        }
        catch (Exception ex)
        {
            // Never accept receipt metadata such as cryptographic_signature_verified.
            // Message is local only: no sensitive receipt content is logged.
            return new Result(false,
                "Licenza Demo non utilizzabile: " + ex.Message + "\n" +
                "Verificare orologio Windows e ricevuta; non disabilitare TLS.\n" +
                "Stage 5 non applica ancora blocchi CTI.", null, null, null);
        }
    }
}
