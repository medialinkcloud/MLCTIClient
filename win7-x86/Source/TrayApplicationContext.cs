using System.IO;
using System.Drawing;
using System.Diagnostics;
using System.Windows.Forms;

namespace MedialinkCTIClient;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private AppConfig config;
    private readonly NotifyIcon tray;
    private readonly Icon appIcon;
    private readonly Icon trayIcon;
    private readonly PopupForm popup;
    private bool closing;
    private bool startupCdrTriggered;
    private bool openingPanel;
    private AmiMonitor? monitor;
    private readonly UpdateSafetyGate updateSafety = new UpdateSafetyGate();
    private readonly UpdateMaintenanceObservation maintenanceObservation;
    private readonly UpdateStatusPipe updateStatusPipe;
    private CancellationTokenSource? monitorCancel;
    private CancellationTokenSource? cdrEndCancel;
    private SynchronizationContext? uiContext;

    private readonly EventWaitHandle quitEvent =
        new(false, EventResetMode.AutoReset, @"Local\MedialinkCTIClientStop");
    private readonly System.Windows.Forms.Timer quitPoll = new() { Interval = 300 };
    private readonly CancellationTokenSource externalDialCancel = new();
    private readonly string? startupExternalNumber;
    private GlobalDialHotkey? globalDialHotkey;

    public TrayApplicationContext(string? externalNumber = null)
    {
        startupExternalNumber = externalNumber;
        maintenanceObservation = new UpdateMaintenanceObservation(() => closing ? "BLOCCATO: client in chiusura." : updateSafety.GetStatus());
        updateStatusPipe = new UpdateStatusPipe(() => closing ? "BLOCCATO: client in chiusura." : updateSafety.GetStatus());
        try { config = ConfigStore.Load(); }
        catch (Exception e)
        {
            DiagnosticLog.Error("CONFIG_LOAD_FAILED_STARTUP", e);
            MessageBox.Show("Impossibile leggere le impostazioni salvate. Per sicurezza " +
                "il client non sarà avviato e NON sovrascriverà le credenziali. " +
                "Verificare settings.json e la cartella Logs.",
                "Medialink CTI Client", MessageBoxButtons.OK, MessageBoxIcon.Error);
            throw;
        }
        appIcon = LoadAppIcon();
        trayIcon = LoadTrayIcon();
        popup = new PopupForm(config, OpenSettings) { Icon = appIcon };
        popup.OutgoingDialRequested += number => { updateSafety.OutgoingIntent(); maintenanceObservation.Reevaluate(); monitor?.RegisterOutgoingIntent(number); };
        popup.OutgoingDialFailed += number => monitor?.ClearOutgoingIntent(number);
        var menu = new ContextMenuStrip();
        menu.Items.Add("Apri pannello", null, async (_, _) => await OpenPanelAsync());
        menu.Items.Add("Nascondi pannello", null, (_, _) => { if (!closing) popup.Hide(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Impostazioni...", null, (_, _) => OpenSettings());
        var infoMenu = new ToolStripMenuItem("Informazioni");
        infoMenu.DropDownItems.Add("Informazioni su MLCTIClient...", null, (_, _) => ApplicationInfo.Show());
        infoMenu.DropDownItems.Add("Verifica aggiornamenti...", null, async (_, _) =>
        {
            var result = await UpdateManager.CheckAsync(true);
            if (!closing && result != null) MessageBox.Show(result, "MLCTIClient - Aggiornamenti",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        });
        infoMenu.DropDownItems.Add("A.5 LAB - verifica e scarica installer...", null, async (_, _) =>
        {
            var confirm = MessageBox.Show(
                "Scaricare e verificare il pacchetto LAB 7.4.7? Non verra installato ne eseguito.",
                "MLCTIClient - Collaudo A.5", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes) return;
            try
            {
                string result = await UpdateManager.StagePublishedLabAsync();
                if (!closing) MessageBox.Show(result, "MLCTIClient - LAB", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Error("UPDATE_A5_LAB_STAGE_FAILED", ex);
                if (!closing) MessageBox.Show("Download LAB non riuscito o verifica fallita. Consultare il log.",
                    "MLCTIClient - LAB", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        });
        var stableUpdateMenuItem = infoMenu.DropDownItems.Add("A.6.9 - Aggiorna MLCTIClient (LAB)...", null, async (_, _) =>
        {
            if (closing) return;
            // SEC4.26: controllo e download senza consenso installazione.
            try
            {
                var update = await UpdateManager.StagePublishedStableAsync();
                if (closing) return;
                                // SEC4-U9: confirmation is performed by the
                // external updater after RSA preflight.
                // External updater independently verifies the signed RSA manifest and installer hash.
                var updater = Path.Combine(AppContext.BaseDirectory, "MLCTIUpdater.exe");
                if (!File.Exists(updater)) throw new FileNotFoundException("Updater esterno non installato", updater);
                var start = new ProcessStartInfo(updater)
                {
                    UseShellExecute = true,
                    WorkingDirectory = AppContext.BaseDirectory
                };
                start.Arguments = "--install --installer " + QuoteArg(update.InstallerPath) +
                    " --sha256 " + update.Sha256 +
                    " --manifest " + QuoteArg(update.ManifestPath) +
                    " --signature " + QuoteArg(update.SignaturePath) +
                    " --pid " + Process.GetCurrentProcess().Id +
                    " --restart " + QuoteArg(Application.ExecutablePath);
                //
                // Preflight MUST pass before requesting shutdown; otherwise keep the client running.
                var preflight = new ProcessStartInfo(updater)
                { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory };
                preflight.Arguments = start.Arguments.Replace("--install", "--preflight");
                using (var checkedProcess = Process.Start(preflight))
                {
                    if (checkedProcess == null || !checkedProcess.WaitForExit(30000) || checkedProcess.ExitCode != 0)
                        throw new InvalidOperationException("Firma RSA / integrità installer non valide. MLCTIClient resta aperto.");
                }
                                // SEC4.26: verifica il binario updater autorizzato.
                string updaterHash;
                using (var stream = File.OpenRead(updater))
                using (var sha = System.Security.Cryptography.SHA256.Create())
                {
                    updaterHash = BitConverter.ToString(
                        sha.ComputeHash(stream)).Replace("-", "");
                }

                if (!string.Equals(
                    updaterHash,
                    "25BF0E2E811E2A6480B5BE0E1B2DC1BBDECBD171DC6EACC01E31BE78AFFFD93D",
                    StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        "Updater non corrispondente alla versione autorizzata.");

                // La conferma finale e gestita dal processo updater.
                // Il client continua a funzionare durante la richiesta.
                start.UseShellExecute = false;
                start.RedirectStandardOutput = true;
                start.CreateNoWindow = true;

                using (var launchedUpdater = Process.Start(start))
                {
                    if (launchedUpdater == null)
                        throw new InvalidOperationException(
                            "Impossibile avviare updater.");

                                                            // Nessun timeout mentre l'utente sceglie.
                    // EOF o uscita dell'updater sono gestiti come errore.
                    string decision =
                        await launchedUpdater.StandardOutput.ReadLineAsync();

                    if (decision == null)
                        throw new InvalidOperationException(
                            "Updater terminato senza comunicare una decisione.");

                    

                    if (decision != "SEC48|INSTALL|AUTO_READY")
                        throw new InvalidOperationException(
                            "Conferma updater non ricevuta.");

                    // Esce soltanto dopo il consenso espresso nell'updater.
                    Quit();
                    return;
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Error("A66_UPDATE_FAILED", ex);
                if (!closing) MessageBox.Show("Aggiornamento non riuscito. Controllare i log.",
                    "MLCTIClient", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        });
        menu.Items.Add(infoMenu);
        // Controllo automatico all'avvio, massimo una volta ogni 24 ore.
        var autoUpdateTimer = new System.Windows.Forms.Timer();
        autoUpdateTimer.Interval = 8000;
        autoUpdateTimer.Tick += (_, _) =>
        {
            autoUpdateTimer.Stop();
            autoUpdateTimer.Dispose();
            if (closing) return;

            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                    @"Software\Medialink\MLCTIClient\AutoUpdate"))
                {
                    if (key == null) return;

                    long ticks;
                    string stored = Convert.ToString(
                        key.GetValue("LastCheckLabUtcTicks", "0")) ?? "0";

                    if (!long.TryParse(stored, out ticks)) ticks = 0;

                    long now = DateTime.UtcNow.Ticks;

                    if (ticks > 0 && now >= ticks &&
                        now - ticks < TimeSpan.FromHours(24).Ticks)
                        return;

                    key.SetValue("LastCheckLabUtcTicks",
                        now.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }

                stableUpdateMenuItem.PerformClick();
            }
            catch (Exception ex)
            {
                DiagnosticLog.Error("AUTO_UPDATE_STARTUP_FAILED", ex);
            }
        };
        autoUpdateTimer.Start();
        menu.Items.Add("Licenza DEMO 30 giorni...", null, (_, _) =>
        {
            using var dialog = new DemoLicenseConnectionForm();
            dialog.ShowDialog();
        });
        menu.Items.Add("Licenza commerciale / grace 7 giorni...", null, (_, _) =>
        {
            using var dialog = new CommercialLicenseForm();
            dialog.ShowDialog();
        });
        menu.Items.Add("Stato licenza...", null, (_, _) =>
        {
            DemoLicenseGate.IsAuthorized(out string licenseStatus);
            MessageBox.Show(licenseStatus + "\nDopo l'attivazione riavviare il client.",
                "Medialink CTI Client - Licenza", MessageBoxButtons.OK, MessageBoxIcon.Information);
        });
        menu.Items.Add("Apri cartella log...", null, (_, _) =>
        {
            try { DiagnosticLog.OpenFolder(); }
            catch (Exception e)
            {
                DiagnosticLog.Error("OPEN_LOG_FOLDER_FAILED", e);
                MessageBox.Show("Impossibile aprire la cartella log.", "Medialink CTI Client");
            }
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Riavvia client", null, (_, _) => Restart());
        menu.Items.Add("Chiudi client", null, (_, _) => Quit());
        tray = new NotifyIcon
        {
            Icon = trayIcon,
            Text = "Medialink CTI Client",
            Visible = true,
            ContextMenuStrip = menu
        };
        // Click sinistro = Apri/porta in primo piano. Niente toggle:
        // il vecchio Deactivate puo nascondere la finestra prima del MouseClick
        // sulla tray e impedire la successiva riapertura.
        tray.MouseClick += async (_, e) =>
        {
            if (e.Button == MouseButtons.Left) await OpenPanelAsync();
        };
        quitPoll.Tick += (_, _) =>
        {
            if (quitEvent.WaitOne(0))
            {
                DiagnosticLog.Info("REMOTE_EXIT_REQUESTED");
                Quit();
            }
        };
        quitPoll.Start();
        updateStatusPipe.Start();
        Application.Idle += OnFirstIdle;
    }

    private async void OnFirstIdle(object? sender, EventArgs e)
    {
        if (startupCdrTriggered || closing) return;
        startupCdrTriggered = true;
        Application.Idle -= OnFirstIdle;
        uiContext = SynchronizationContext.Current;
        // Discover updates even when activation has not yet been completed.
        _ = UpdateManager.CheckAsync(false);
        if (!DemoLicenseGate.IsAuthorized(out var startupLicenseStatus))
        {
            DiagnosticLog.Warn("LICENSING_STARTUP_BLOCKED");
            return; // Tray / licensing dialogs remain available. Restart after activation.
        }
        StartMonitor();
        ConfigureGlobalHotkey();
        // IPC accepts numbers only from this Windows user/session. Callback
        // always schedules a user-visible confirmation on the WinForms thread.
        _ = Task.Run(() => ExternalDialPipe.RunAsync(QueueExternalNumberFromPipe,
            externalDialCancel.Token));
        if (startupExternalNumber is not null)
            QueueExternalNumberOnUi(startupExternalNumber);
        try { await popup.SynchronizeCdrAtStartupAsync(); }
        catch (Exception ex) { DiagnosticLog.Error("CDR_STARTUP_SYNC_FAILED", ex); }
    }

    private void ConfigureGlobalHotkey()
    {
        globalDialHotkey?.Dispose();
        globalDialHotkey = null;
        if (closing || !config.EnableExternalHotkey) return;
        try
        {
            globalDialHotkey = new GlobalDialHotkey(config.ExternalHotkeyGesture,
                async () =>
                {
                    try
                    {
                        string? number = await ClipboardDialSelection.CaptureAsync(config.ExternalHotkeyGesture);
                        if (!string.IsNullOrEmpty(number) && !closing)
                            await popup.PromptExternalDialAsync(number);
                    }
                    catch (Exception e)
                    {
                        DiagnosticLog.Error("EXTERNAL_HOTKEY_CAPTURE_FAILED", e);
                        MessageBox.Show("Numero selezionato non disponibile. Evidenzia un solo numero " +
                            "nell'applicazione e riprova.", "Click-to-Dial", MessageBoxButtons.OK,
                            MessageBoxIcon.Information);
                    }
                });
            DiagnosticLog.Info("EXTERNAL_HOTKEY_REGISTERED", "gesture=configured");
        }
        catch (Exception e)
        {
            DiagnosticLog.Error("EXTERNAL_HOTKEY_REGISTER_FAILED", e);
            MessageBox.Show("La scorciatoia globale non è disponibile: potrebbe essere già " +
                "utilizzata da un'altra applicazione. Puoi cambiarla nelle Impostazioni generali.",
                "Medialink CTI Client", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void QueueExternalNumberFromPipe(string number)
    {
        if (closing || !config.EnableExternalClickToDial || uiContext is null)
            throw new InvalidOperationException("Click-to-Dial esterno non abilitato o client non pronto.");
        uiContext.Post(_ => QueueExternalNumberOnUi(number), null);
    }

    private void QueueExternalNumberOnUi(string number)
    {
        if (closing) return;
        if (!config.EnableExternalClickToDial)
        {
            DiagnosticLog.Warn("EXTERNAL_DIAL_DISABLED");
            MessageBox.Show("Click-to-Dial esterno non abilitato. " +
                "Attivalo da Impostazioni > Generali.", "Medialink CTI Client",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _ = ShowExternalConfirmationSafelyAsync(number);
    }

    private async Task ShowExternalConfirmationSafelyAsync(string number)
    {
        try { if (!closing) await popup.PromptExternalDialAsync(number); }
        catch (Exception e) { DiagnosticLog.Error("EXTERNAL_DIAL_CONFIRMATION_FAILED", e); }
    }

    private static Icon LoadAppIcon()
    {
        var assembly = typeof(TrayApplicationContext).Assembly;
        using var stream = assembly.GetManifestResourceStream("MedialinkCTIClient.Assets.MedialinkCTI.ico");
        return stream is null ? (Icon)SystemIcons.Application.Clone() : new Icon(stream);
    }

    private static Icon LoadTrayIcon()
    {
        var assembly = typeof(TrayApplicationContext).Assembly;
        using var stream = assembly.GetManifestResourceStream("MedialinkCTIClient.Assets.MedialinkCTI.TrayLarge.ico");
        return stream is null ? LoadAppIcon() : new Icon(stream);
    }

    private async Task OpenPanelAsync()
    {
        if (closing) return;
        if (!DemoLicenseGate.IsAuthorized(out var licenseReason))
        {
            MessageBox.Show(licenseReason, "Licenza MLCTIClient",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (openingPanel)
        {
            if (popup.Visible) { popup.Activate(); popup.BringToFront(); }
            return;
        }
        openingPanel = true;
        try
        {
            if (popup.IsDisposed)
                throw new ObjectDisposedException(nameof(PopupForm),
                    "Il pannello risulta chiuso anziche nascosto.");
            await popup.OpenAsync();
        }
        catch (Exception e)
        {
            DiagnosticLog.Error("TRAY_OPEN_PANEL_FAILED", e);
            if (!closing)
                MessageBox.Show("Impossibile aprire il pannello. Consultare i log diagnostici.",
                    "Medialink CTI Client", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { openingPanel = false; }
    }

    private void Restart()
    {
        if (closing) return;
        try
        {
            var executable = System.Windows.Forms.Application.ExecutablePath ?? Application.ExecutablePath;
            if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
                throw new FileNotFoundException("Eseguibile MLCTIClient non trovato.", executable);

            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory
            };
            // Il nuovo processo attende l'uscita del vecchio prima del mutex.
            start.Arguments = "--restart-after-exit " +
                Process.GetCurrentProcess().Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            using var restarted = Process.Start(start)
                ?? throw new InvalidOperationException("Avvio del nuovo processo non riuscito.");
            DiagnosticLog.Info("TRAY_RESTART_REQUESTED");
        }
        catch (Exception e)
        {
            // Non chiudere la sessione corrente se non siamo riusciti
            // ad avviare il nuovo processo.
            DiagnosticLog.Error("TRAY_RESTART_FAILED", e);
            MessageBox.Show("Riavvio non riuscito. Il client attuale rimane aperto. " +
                "Consultare i log diagnostici.", "Medialink CTI Client",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        Quit();
    }

    private void StartMonitor()
    {
        if (!DemoLicenseGate.IsAuthorized(out var licenseReason))
        {
            DiagnosticLog.Warn("LICENSING_MONITOR_BLOCKED");
            return;
        }
        monitorCancel?.Cancel();
        monitorCancel?.Dispose();
        cdrEndCancel?.Cancel();
        cdrEndCancel?.Dispose();
        cdrEndCancel = new CancellationTokenSource();
        monitorCancel = new CancellationTokenSource();
        updateSafety.Reset();
        maintenanceObservation.Revoke("MONITOR_RESTART");
        var current = new AmiMonitor();
        popup.ResetBlf();
        monitor = current;
        var token = monitorCancel.Token;
        var cdrToken = cdrEndCancel.Token;
        current.InitialSnapshotComplete += () =>
        {
            if (!closing && ReferenceEquals(current, monitor)) { updateSafety.SetSnapshotComplete(); maintenanceObservation.Reevaluate(); }
        };
        current.ConnectionChanged += connected =>
        {
            if (closing || !ReferenceEquals(current, monitor)) return;
            updateSafety.SetConnection(connected);
            maintenanceObservation.Reevaluate();
            uiContext?.Post(_ =>
            {
                if (!closing && ReferenceEquals(current, monitor))
                    popup.SetMonitorConnected(connected);
            }, null);
        };
        current.BlfChanged += (extension, state) =>
        {
            if (closing || !ReferenceEquals(current, monitor)) return;
            uiContext?.Post(_ => { if (!closing && ReferenceEquals(current, monitor)) popup.UpdateBlf(extension, state); }, null);
        };
        current.StateChanged += snapshot =>
        {
            if (closing || !ReferenceEquals(current, monitor)) return;
            updateSafety.SetCall(snapshot);
            maintenanceObservation.Reevaluate();
            uiContext?.Post(_ => { if (!closing && ReferenceEquals(current, monitor)) popup.SetLiveCall(snapshot); }, null);
        };
        current.LocalCallEnded += () =>
        {
            if (closing || !ReferenceEquals(current, monitor)) return;
            // A few bounded delayed checks only after call completion: CDR is not guaranteed immediately ready.
            _ = Task.Run(async () =>
            {
                foreach (int delay in new[] { 2, 6, 12 })
                {
                    try { await Task.Delay(TimeSpan.FromSeconds(delay), cdrToken); }
                    catch (OperationCanceledException) { break; }
                    if (cdrToken.IsCancellationRequested || closing || !ReferenceEquals(current, monitor)) break;
                    uiContext?.Post(async _ =>
                    {
                        if (!closing && ReferenceEquals(current, monitor))
                            await popup.RefreshCdrAfterCallAsync();
                    }, null);
                }
            }, cdrToken);
        };
        var snapshotConfig = config;
        _ = Task.Run(() => current.RunAsync(snapshotConfig, token), token);
    }

    private bool OpenSettings()
    {
        popup.Hide();
        // Never intercept shortcuts while editing settings or entering credentials.
        globalDialHotkey?.Dispose();
        globalDialHotkey = null;
        DiagnosticLog.Info("SETTINGS_OPEN");
        try
        {
            using var form = new SettingsForm(config) { Icon = appIcon };
            if (form.ShowDialog() == DialogResult.OK && form.Saved is { } updated)
            {
                config = updated;
                popup.UpdateConfiguration(config);
                StartMonitor();
                DiagnosticLog.Info("SETTINGS_APPLIED");
                return true;
            }
            return false;
        }
        finally { ConfigureGlobalHotkey(); }
    }

    private static string QuoteArg(string value)
    {
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    private void Quit()
    {
        maintenanceObservation.Revoke("CLIENT_CLOSING");
        updateSafety.Reset();
        closing = true;
        updateStatusPipe.Dispose();
        DiagnosticLog.Info("TRAY_EXIT");
        Application.Idle -= OnFirstIdle;
        monitorCancel?.Cancel(); cdrEndCancel?.Cancel(); externalDialCancel.Cancel();
        globalDialHotkey?.Dispose(); globalDialHotkey = null;
        quitPoll.Stop(); quitPoll.Dispose(); quitEvent.Dispose(); externalDialCancel.Dispose();
        tray.Visible = false;
        tray.Dispose();
        popup.Dispose();
        trayIcon.Dispose();
        appIcon.Dispose();
        ExitThread();
    }
}
