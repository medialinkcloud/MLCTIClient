using System.Windows.Forms;

namespace MedialinkCTIClient;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Any(x => string.Equals(x, "--self-test-update", StringComparison.OrdinalIgnoreCase)))
            return UpdateManager.SelfTest() ? 0 : 87;
        if(args.Any(x => string.Equals(x,"--self-test-blf",StringComparison.OrdinalIgnoreCase)))
            return BlfLogic.SelfTest() ? 0 : 91;
        if (args.Any(x => string.Equals(x, "--self-test-demo", StringComparison.OrdinalIgnoreCase)))
        {
            try { return CtiDemoClient.SelfTest() ? 0 : 21; }
            catch (Exception e) { DiagnosticLog.Error("DEMO_SELFTEST_FAILED", e); return 22; }
        }
        if (args.Any(x => string.Equals(x, "--self-test-license", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                bool pass = CtiLicenseVerifier.SelfTest();
                DiagnosticLog.Info(pass ? "LICENSE_CRYPTO_SELF_TEST_OK" : "LICENSE_CRYPTO_SELF_TEST_FAILED");
                return pass ? 0 : 79;
            }
            catch (Exception ex) { DiagnosticLog.Error("LICENSE_CRYPTO_SELF_TEST_FAILED", ex); return 79; }
        }

        // Per il riavvio dalla tray, il nuovo processo deve attendere
        // la terminazione del vecchio PRIMA di tentare il mutex single-instance.
        if (args.Any(x => string.Equals(x, "--restart-after-exit", StringComparison.OrdinalIgnoreCase)))
        {
            if (!TrayLifecycleRules.TryParseRestartArguments(args, out int parentPid))
            {
                DiagnosticLog.Warn("TRAY_RESTART_INVALID_ARGUMENTS");
                return 32;
            }
            try
            {
                using var previous = System.Diagnostics.Process.GetProcessById(parentPid);
                if (!previous.WaitForExit(15000))
                {
                    DiagnosticLog.Warn("TRAY_RESTART_PARENT_STILL_RUNNING");
                    MessageBox.Show("Il client precedente non si e chiuso in 15 secondi. " +
                        "Riavvio annullato: non e stata avviata una seconda istanza.",
                        "Medialink CTI Client", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return 33;
                }
            }
            catch (ArgumentException) { /* Processo precedente gia terminato. */ }
            catch (Exception e)
            {
                DiagnosticLog.Error("TRAY_RESTART_WAIT_FAILED", e);
                return 33;
            }
        }
        // A newly compiled staging EXE can request a graceful tray shutdown.
        if (args.Any(x => string.Equals(x, "--request-exit", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                using var quitEvent = EventWaitHandle.OpenExisting(@"Local\MedialinkCTIClientStop");
                return quitEvent.Set() ? 0 : 31;
            }
            catch (WaitHandleCannotBeOpenedException) { return 0; } // Not running.
            catch { return 31; }
        }
        if (args.Any(x => string.Equals(x, "--self-test-country-settings", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                bool pass = SettingsForm.SelfTestCountrySelection();
                DiagnosticLog.Info(pass ? "COUNTRY_SETTINGS_SELF_TEST_OK" : "COUNTRY_SETTINGS_SELF_TEST_FAILED");
                return pass ? 0 : 77;
            }
            catch (Exception e)
            {
                DiagnosticLog.Error("COUNTRY_SETTINGS_SELF_TEST_FAILED", e);
                return 77;
            }
        }
        if (args.Any(x => string.Equals(x, "--self-test-window-chrome", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                bool pass = PopupForm.SelfTestWindowChrome();
                DiagnosticLog.Info(pass ? "WINDOW_CHROME_SELF_TEST_OK" : "WINDOW_CHROME_SELF_TEST_FAILED");
                return pass ? 0 : 66;
            }
            catch (Exception e) { DiagnosticLog.Error("WINDOW_CHROME_SELF_TEST_FAILED", e); return 66; }
        }
        if (args.Any(x => string.Equals(x, "--self-test-ldap-duplicate-scope", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                bool pass = LdapDuplicateScopeRules.SelfTest();
                DiagnosticLog.Info(pass ? "LDAP_DUPLICATE_SCOPE_SELF_TEST_OK" : "LDAP_DUPLICATE_SCOPE_SELF_TEST_FAILED");
                return pass ? 0 : 67;
            }
            catch (Exception e) { DiagnosticLog.Error("LDAP_DUPLICATE_SCOPE_SELF_TEST_FAILED", e); return 67; }
        }
        if (args.Any(x => string.Equals(x, "--self-test-ldap-recovery", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                bool pass = LdapClient.SelfTestSafeReadFallback() && PopupForm.SelfTestStaleLdapInvalidation();
                DiagnosticLog.Info(pass ? "LDAP_RECOVERY_SELF_TEST_OK" : "LDAP_RECOVERY_SELF_TEST_FAILED");
                return pass ? 0 : 65;
            }
            catch (Exception e) { DiagnosticLog.Error("LDAP_RECOVERY_SELF_TEST_FAILED", e); return 65; }
        }
        if (args.Any(x => string.Equals(x, "--self-test-cdr", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                bool pass = CdrClient.SelfTest();
                DiagnosticLog.Info(pass ? "CDR_SELF_TEST_OK" : "CDR_SELF_TEST_FAILED");
                return pass ? 0 : 35;
            }
            catch (Exception e)
            {
                DiagnosticLog.Error("CDR_SELF_TEST_FAILED", e);
                return 35;
            }
        }
        if (args.Any(x => string.Equals(x, "--self-test-monitor", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                bool pass = AmiMonitor.SelfTest();
                DiagnosticLog.Info(pass ? "AMI_MONITOR_SELF_TEST_OK" : "AMI_MONITOR_SELF_TEST_FAILED");
                return pass ? 0 : 36;
            }
            catch (Exception e)
            {
                DiagnosticLog.Error("AMI_MONITOR_SELF_TEST_FAILED", e);
                return 36;
            }
        }
        if (args.Any(x => string.Equals(x, "--self-test-dial", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                bool pass = AmiClient.SelfTestClickToDial();
                DiagnosticLog.Info(pass ? "AMI_DIAL_SELF_TEST_OK" : "AMI_DIAL_SELF_TEST_FAILED");
                return pass ? 0 : 37;
            }
            catch (Exception e)
            {
                DiagnosticLog.Error("AMI_DIAL_SELF_TEST_FAILED", e);
                return 37;
            }
        }
        if (args.Any(x => string.Equals(x, "--self-test-call-actions", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                bool pass = LiveCallActions.SelfTest();
                DiagnosticLog.Info(pass ? "CALL_ACTIONS_SELF_TEST_OK" : "CALL_ACTIONS_SELF_TEST_FAILED");
                return pass ? 0 : 38;
            }
            catch (Exception e)
            {
                DiagnosticLog.Error("CALL_ACTIONS_SELF_TEST_FAILED", e);
                return 38;
            }
        }
        if (args.Any(x => string.Equals(x, "--self-test-phone-discovery", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                bool pass = AmiPhoneDiscovery.SelfTest();
                DiagnosticLog.Info(pass ? "PHONE_DISCOVERY_SELF_TEST_OK" : "PHONE_DISCOVERY_SELF_TEST_FAILED");
                return pass ? 0 : 39;
            }
            catch (Exception e)
            {
                DiagnosticLog.Error("PHONE_DISCOVERY_SELF_TEST_FAILED", e);
                return 39;
            }
        }
        if (args.Any(x => string.Equals(x, "--self-test-cti-json", StringComparison.OrdinalIgnoreCase)))
        {
            try { return PhoneControlClient.SelfTest() ? 0 : 40; }
            catch (Exception e) { DiagnosticLog.Error("CTI_RESPONSE_SELF_TEST_FAILED", e); return 40; }
        }
        if (args.Any(x => string.Equals(x, "--self-test-mini-popup", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                bool pass = MiniPopupRules.SelfTest();
                DiagnosticLog.Info(pass ? "MINI_POPUP_SELF_TEST_OK" : "MINI_POPUP_SELF_TEST_FAILED");
                return pass ? 0 : 41;
            }
            catch (Exception e)
            {
                DiagnosticLog.Error("MINI_POPUP_SELF_TEST_FAILED", e);
                return 41;
            }
        }
        if (args.Any(x => string.Equals(x, "--self-test-snom", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                bool pass = SnomPhoneDriver.SelfTest() && SnomHangupRules.SelfTest();
                DiagnosticLog.Info(pass ? "SNOM_DRIVER_SELF_TEST_OK" : "SNOM_DRIVER_SELF_TEST_FAILED");
                return pass ? 0 : 42;
            }
            catch (Exception e) { DiagnosticLog.Error("SNOM_DRIVER_SELF_TEST_FAILED", e); return 42; }
        }
        if (args.Any(x => string.Equals(x, "--self-test-yealink", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                bool pass = YealinkPhoneDriver.SelfTest() && YealinkHangupRules.SelfTest();
                DiagnosticLog.Info(pass ? "YEALINK_DRIVER_SELF_TEST_OK" : "YEALINK_DRIVER_SELF_TEST_FAILED");
                return pass ? 0 : 68;
            }
            catch (Exception e) { DiagnosticLog.Error("YEALINK_DRIVER_SELF_TEST_FAILED", e); return 68; }
        }
        if (args.Any(x => string.Equals(x, "--self-test-grandstream-https", StringComparison.OrdinalIgnoreCase)))
        {
            try { return GrandstreamDirectTls.SelfTest() ? 0 : 43; }
            catch (Exception e) { DiagnosticLog.Error("GRP_CTI_HTTPS_SELF_TEST_FAILED", e); return 43; }
        }
        if (args.Any(x => string.Equals(x, "--self-test-ldap-write", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                bool pass = UcmContactsApiClient.SelfTest();
                DiagnosticLog.Info(pass ? "UCM_LDAP_WRITE_SELF_TEST_OK" : "UCM_LDAP_WRITE_SELF_TEST_FAILED");
                return pass ? 0 : 44;
            }
            catch (Exception e) { DiagnosticLog.Error("UCM_LDAP_WRITE_SELF_TEST_FAILED", e); return 44; }
        }
        if (args.Any(x => string.Equals(x, "--self-test-dtmf", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                bool pass = AmiClient.SelfTestDtmf();
                DiagnosticLog.Info(pass ? "DTMF_SELF_TEST_OK" : "DTMF_SELF_TEST_FAILED");
                return pass ? 0 : 46;
            }
            catch (Exception e) { DiagnosticLog.Error("DTMF_SELF_TEST_FAILED", e); return 46; }
        }
        if (args.Any(x => string.Equals(x, "--self-test-tray", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                bool pass = TrayLifecycleRules.SelfTest();
                DiagnosticLog.Info(pass ? "TRAY_LIFECYCLE_SELF_TEST_OK" : "TRAY_LIFECYCLE_SELF_TEST_FAILED");
                return pass ? 0 : 47;
            }
            catch (Exception e) { DiagnosticLog.Error("TRAY_LIFECYCLE_SELF_TEST_FAILED", e); return 47; }
        }
        if (args.Any(x => string.Equals(x, "--self-test-cdr-grid", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                bool pass = PopupForm.SelfTestCdrGrid();
                DiagnosticLog.Info(pass ? "CDR_GRID_SELF_TEST_OK" : "CDR_GRID_SELF_TEST_FAILED");
                return pass ? 0 : 45;
            }
            catch (Exception e) { DiagnosticLog.Error("CDR_GRID_SELF_TEST_FAILED", e); return 45; }
        }
        if (args.Any(x => string.Equals(x, "--self-test-live-ldap", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                bool pass = LiveNameLookupRules.SelfTest() &&
                    PhoneNumbers.SearchKey("3479784437") == "3479784437" &&
                    PhoneNumbers.SearchKey("+393479784437") == "3479784437" &&
                    PhoneNumbers.SearchKey("00393479784437") == "3479784437" &&
                    PhoneNumbers.SearchKey("393479784437") == "3479784437" &&
                    UcmContactWriteRules.SameNumber("393479784437", "3479784437");
                DiagnosticLog.Info(pass ? "CTI_LIVE_LDAP_SELF_TEST_OK" : "CTI_LIVE_LDAP_SELF_TEST_FAILED");
                return pass ? 0 : 52;
            }
            catch (Exception e) { DiagnosticLog.Error("CTI_LIVE_LDAP_SELF_TEST_FAILED", e); return 52; }
        }
        if (args.Any(x => string.Equals(x, "--self-test-ldap-summary", StringComparison.OrdinalIgnoreCase)))
        {
            try { return LdapSummaryFormatter.SelfTest() ? 0 : 61; }
            catch (Exception e) { DiagnosticLog.Error("LDAP_SUMMARY_SELF_TEST_FAILED", e); return 61; }
        }
        if (args.Any(x => string.Equals(x, "--self-test-ldap-selection", StringComparison.OrdinalIgnoreCase)))
        {
            try { return LdapSelectionRules.SelfTest() ? 0 : 49; }
            catch (Exception e) { DiagnosticLog.Error("LDAP_SELECTION_SELF_TEST_FAILED", e); return 49; }
        }
        if (args.Any(x => string.Equals(x, "--self-test-cdr-ldap-unknown", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                bool pass = PopupForm.SelfTestCdrLdapUnknown();
                DiagnosticLog.Info(pass ? "CDR_LDAP_UNKNOWN_SELF_TEST_OK" : "CDR_LDAP_UNKNOWN_SELF_TEST_FAILED");
                return pass ? 0 : 55;
            }
            catch (Exception e) { DiagnosticLog.Error("CDR_LDAP_UNKNOWN_SELF_TEST_FAILED", e); return 55; }
        }
        if (args.Any(x => string.Equals(x, "--self-test-ldap-edit-toggle", StringComparison.OrdinalIgnoreCase)))
        {
            try { return PopupForm.SelfTestLdapEditToggle() ? 0 : 50; }
            catch (Exception e) { DiagnosticLog.Error("LDAP_EDIT_TOGGLE_SELF_TEST_FAILED", e); return 50; }
        }
        if (args.Any(x => string.Equals(x, "--self-test-call-identity", StringComparison.OrdinalIgnoreCase)))
        {
            try { return CallIdentityPresentation.SelfTest() ? 0 : 48; }
            catch (Exception e) { DiagnosticLog.Error("CALL_IDENTITY_SELF_TEST_FAILED", e); return 48; }
        }
        if (args.Any(x => string.Equals(x, "--self-test-ldap-delete", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                bool pass = UcmContactsApiClient.SelfTest() && PopupForm.SelfTestLdapDeleteUi();
                DiagnosticLog.Info(pass ? "UCM_LDAP_DELETE_SELF_TEST_OK" : "UCM_LDAP_DELETE_SELF_TEST_FAILED");
                return pass ? 0 : 54;
            }
            catch (Exception e) { DiagnosticLog.Error("UCM_LDAP_DELETE_SELF_TEST_FAILED", e); return 54; }
        }
        if (args.Any(x => string.Equals(x, "--self-test-book-sort", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                bool pass = PopupForm.SelfTestContactSorting();
                DiagnosticLog.Info(pass ? "BOOK_SORT_SELF_TEST_OK" : "BOOK_SORT_SELF_TEST_FAILED");
                return pass ? 0 : 74;
            }
            catch (Exception e) { DiagnosticLog.Error("BOOK_SORT_SELF_TEST_FAILED", e); return 74; }
        }
        if (args.Any(x => string.Equals(x, "--self-test-dial-prefix", StringComparison.OrdinalIgnoreCase)))
        {
            bool pass = DialNumberRules.SelfTest();
            DiagnosticLog.Info(pass ? "DIAL_PREFIX_SELF_TEST_OK" : "DIAL_PREFIX_SELF_TEST_FAILED");
            return pass ? 0 : 76;
        }
        if (args.Any(x => string.Equals(x, "--self-test-global-hotkey", StringComparison.OrdinalIgnoreCase)))
        {
            bool pass = GlobalHotkeyRules.SelfTest() && ClipboardDialSelection.SelfTest();
            DiagnosticLog.Info(pass ? "GLOBAL_HOTKEY_SELF_TEST_OK" : "GLOBAL_HOTKEY_SELF_TEST_FAILED");
            return pass ? 0 : 75;
        }
        // Windows invokes the associated URL handler with exactly one URL argument.
        // Manual integration can also supply: --external-dial <tel:/callto: URL>.
        string? externalNumber = null;
        if (args.Length > 0 && (ExternalDialUri.HasSupportedScheme(args[0]) ||
            string.Equals(args[0], "--external-dial", StringComparison.OrdinalIgnoreCase)))
        {
            string? uri = args.Length == 1 ? args[0] :
                args.Length == 2 && args[0].Equals("--external-dial", StringComparison.OrdinalIgnoreCase)
                    ? args[1] : null;
            if (!ExternalDialUri.TryParse(uri, out var validNumber))
            {
                DiagnosticLog.Warn("EXTERNAL_DIAL_URI_REJECTED");
                MessageBox.Show("Collegamento telefonico non valido o non supportato.",
                    "Medialink CTI Client", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 71;
            }
            externalNumber = validNumber;
        }
        if (args.Any(x => string.Equals(x, "--self-test-external-dial", StringComparison.OrdinalIgnoreCase)))
        {
            bool pass = ExternalDialUri.SelfTest() && ExternalDialRegistry.SelfTest();
            DiagnosticLog.Info(pass ? "EXTERNAL_DIAL_SELF_TEST_OK" : "EXTERNAL_DIAL_SELF_TEST_FAILED");
            return pass ? 0 : 72;
        }
        DiagnosticLog.Info("APP_START", "version=1.2.0-rc1");
        if (args.Any(x => string.Equals(x, "--verify-settings", StringComparison.OrdinalIgnoreCase)))
        {
            DiagnosticLog.Info("CONFIG_PREFLIGHT_START");
            try
            {
                // Reading only. Never call ConfigStore.Save in the preflight.
                var config = ConfigStore.Load();
                if (!File.Exists(ConfigStore.FilePath))
                {
                    DiagnosticLog.Info("CONFIG_PREFLIGHT_OK", "no_settings_first_install=true");
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(config.Extension))
                        throw new InvalidDataException("Internal extension not set");
                    DiagnosticLog.Info("CONFIG_PREFLIGHT_OK", "existing_settings_readable=true");
                }
                return 0;
            }
            catch (Exception e)
            {
                DiagnosticLog.Error("CONFIG_PREFLIGHT_FAILED", e);
                return 30;
            }
        }
        Application.ThreadException += (_, e) => DiagnosticLog.Error("UI_THREAD_EXCEPTION", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) DiagnosticLog.Error("UNHANDLED_EXCEPTION", ex);
            else DiagnosticLog.Warn("UNHANDLED_NON_EXCEPTION");
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            DiagnosticLog.Error("UNOBSERVED_TASK_EXCEPTION", e.Exception);
        };
        using var mutex = new Mutex(true, @"Local\MedialinkCTIClient", out var first);
        if (!first)
        {
            if (externalNumber is not null)
            {
                bool delivered = ExternalDialPipe.SendAsync(externalNumber).GetAwaiter().GetResult();
                if (delivered)
                {
                    DiagnosticLog.Info("EXTERNAL_DIAL_DELIVERED", "instance=existing");
                    return 0;
                }
                MessageBox.Show("Il client è già aperto, ma non ha accettato la richiesta esterna. " +
                    "Verificare che Click-to-Dial esterno sia abilitato e che il client sia pronto.",
                    "Medialink CTI Client", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 73;
            }
            DiagnosticLog.Warn("SECOND_INSTANCE_BLOCKED");
            MessageBox.Show("Medialink CTI Client è già in esecuzione nell'area di notifica.",
                "Medialink CTI Client", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 10;
        }
        // Remove the legacy protocol when it is owned by this application.
        // Failure must never block startup or license activation.
        try { ExternalDialRegistry.RemoveLegacyMlctiIfOwned(); }
        catch (Exception e) { DiagnosticLog.Error("LEGACY_MLCTI_CLEANUP_FAILED", e); }
        try { Application.Run(new TrayApplicationContext(externalNumber)); }
        catch (Exception e)
        {
            DiagnosticLog.Error("APP_START_FAILED", e);
            MessageBox.Show("Avvio non riuscito. Le impostazioni NON sono state modificate. " +
                "Consultare i log diagnostici in AppData\\Local\\Medialink\\CTIClient\\Logs.",
                "Medialink CTI Client", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { DiagnosticLog.Info("APP_STOP"); }
        return 0;
    }
}
