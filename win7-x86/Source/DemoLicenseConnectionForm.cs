using System.Drawing;
using System.Windows.Forms;

namespace MedialinkCTIClient;

internal sealed class DemoLicenseConnectionForm : Form
{
    private readonly TextBox urlBox = new() { Dock = DockStyle.Top, ReadOnly = true, TabStop = false, Text = DemoLicenseClient.DefaultEndpointUrl };
    private readonly Button activateButton = new() { Text = "Attiva Demo 30 giorni", AutoSize = true, Dock = DockStyle.Top };
    private readonly Button checkButton = new() { Text = "Verifica collegamento HTTPS (senza attivazione)", AutoSize = true, Dock = DockStyle.Top };
    private readonly Button verifyButton = new() { Text = "Verifica licenza salvata (firma RSA e scadenza)", AutoSize = true, Dock = DockStyle.Top };
    private readonly TextBox results = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };

    public DemoLicenseConnectionForm()
    {
        Text = "Medialink CTI Client - Licenza Demo 30 giorni (RC2 Stage 3)";
        Width = 740; Height = 410; MinimumSize = new Size(570, 340);
        StartPosition = FormStartPosition.CenterScreen;
        // R7.4 Update 3: mantiene la finestra licenza visibile sopra alle altre finestre.
        TopMost = true;
        ShowInTaskbar = true;
        Shown += (_, _) => { BringToFront(); Activate(); };
        Font = new Font("Segoe UI", 9.5F);
        var note = new Label {
            Dock = DockStyle.Top, Height = 85,
            Text = "Attivazione server-authoritative: per ogni dispositivo la Demo dura 30 giorni dalla prima attivazione. " +
                   "Una richiesta ripetuta riutilizza la licenza esistente. " +
                   "Stage 3 verifica la firma RSA e la scadenza prima di salvare la risposta. Il blocco operativo CTI è attivo. Riavviare il client dopo l’attivazione."
        };
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };
        panel.Controls.Add(results);
        panel.Controls.Add(verifyButton);
        panel.Controls.Add(activateButton);
        panel.Controls.Add(checkButton);
        panel.Controls.Add(urlBox);
        panel.Controls.Add(note);
        Controls.Add(panel);
        // Endpoint preconfigurato, non dipendente da valori vuoti o editabili.
        urlBox.Text = DemoLicenseClient.DefaultEndpointUrl;
        checkButton.Click += async (_, _) => await CheckAsync();
        activateButton.Click += async (_, _) => await ActivateAsync();
        verifyButton.Click += (_, _) => VerifySaved();
        results.Text = File.Exists(DemoLicenseClient.ReceiptPath)
            ? "Ricevuta locale presente. Usa il pulsante di verifica RSA.\r\n" + DemoLicenseClient.ReceiptPath
            : "Nessuna ricevuta Demo locale presente. L'attivazione reale avviene SOLO con il pulsante dedicato.";
    }

    private static Uri Endpoint() => DemoLicenseClient.ParseEndpoint(DemoLicenseClient.DefaultEndpointUrl);
    private void Busy(bool busy) { activateButton.Enabled = !busy; checkButton.Enabled = !busy; verifyButton.Enabled = !busy; }

    private async Task CheckAsync()
    {
        try
        {
            var endpoint = Endpoint(); Busy(true);
            var (a, b) = await DemoLicenseClient.DiagnosticAsync(endpoint);
            var ok = a == 400 && b == 415;
            results.Text = $"POST JSON non valido: HTTP {a} (atteso 400)\r\n" +
                           $"POST Content-Type errato: HTTP {b} (atteso 415)\r\n" +
                           (ok ? "HTTPS RC2 operativo. Nessuna licenza creata." : "Risposte inattese: NON attivare.");
            DiagnosticLog.Info(ok ? "DEMO_RC2_CONNECTIVITY_OK" : "DEMO_RC2_CONNECTIVITY_UNEXPECTED_STATUS");
        }
        catch (Exception ex)
        {
            results.Text = "Verifica fallita: " + ex.Message + "\r\nNon disabilitare TLS.";
            DiagnosticLog.Warn("DEMO_RC2_CONNECTIVITY_FAILED");
        }
        finally { Busy(false); }
    }

    private async Task ActivateAsync()
    {
        try
        {
            var endpoint = Endpoint();
            if (MessageBox.Show(
                "Confermi l'ATTIVAZIONE REALE della Demo 30 giorni su questo PC? " +
                "Il server registrerà l'identificativo pseudonimo e inizierà la decorrenza dalla prima attivazione. " +
                "La firma RSA e la scadenza vengono verificate prima di salvare la licenza. Il client CTI viene bloccato alla scadenza.",
                "Conferma attivazione Demo", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            Busy(true);
            results.Text = "Richiesta HTTPS in corso...";
            var (id, expires, reused) = await DemoLicenseClient.ActivateAsync(endpoint);
            results.Text = "Licenza firmata RSA verificata e salvata.\r\n" +
                "ID: " + id + "\r\nScadenza dichiarata dal server: " + expires + "\r\n" +
                "Licenza già esistente: " + (reused ? "Sì" : "No") + "\r\n" +
                "Firma RSA: VERIFICATA. Riavviare il client per abilitare il monitor.\r\n" +
                "Ricevuta: " + DemoLicenseClient.ReceiptPath;
            DiagnosticLog.Info("DEMO_RC2_ACTIVATION_RESPONSE_STORED");
        }
        catch (Exception ex)
        {
            results.Text = "Attivazione non completata localmente: " + ex.Message +
                           "\r\nSe il server ha già registrato il PC, una nuova richiesta riutilizzerà la Demo esistente.";
            DiagnosticLog.Warn("DEMO_RC2_ACTIVATION_FAILED");
        }
        finally { Busy(false); }
    }

    private void VerifySaved()
    {
        try
        {
            var v = DemoLicenseClient.VerifySaved();
            results.Text = $"Firma RSA: VALIDA\r\nLicenza: {v.LicenseId}\r\n" +
                $"Inizio: {v.StartedUtc:yyyy-MM-dd HH:mm:ss} UTC\r\n" +
                $"Scadenza: {v.ExpiresUtc:yyyy-MM-dd HH:mm:ss} UTC\r\n" +
                $"Giorni residui (arrotondati): {v.DaysRemaining}\r\n" +
                "Il blocco operativo del client è attivo.";
            DiagnosticLog.Info("DEMO_RC2_RSA_VERIFY_OK");
        }
        catch (Exception ex)
        {
            results.Text = "Licenza NON VALIDA o non verificabile: " + ex.Message;
            DiagnosticLog.Warn("DEMO_RC2_RSA_VERIFY_FAILED");
        }
    }

}
