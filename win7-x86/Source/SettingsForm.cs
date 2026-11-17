using System.Drawing;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace MedialinkCTIClient;

internal sealed class SettingsForm : Form
{
    private readonly AppConfig source;
    private readonly Dictionary<string, TextBox> inputs = new();
    private readonly Dictionary<string, CheckBox> checks = new();
    private readonly ComboBox phoneType = new();
    private readonly ComboBox countrySelect = new();
    private readonly ComboBox phoneCtiProtocol = new();
    private readonly ToolTip phoneHelp = new()
    {
        AutoPopDelay = 30000,
        InitialDelay = 250,
        ReshowDelay = 150,
        ShowAlways = true,
        IsBalloon = true
    };
    // Keep each field alive when another vendor is selected. Hidden values are not reset.
    private readonly Dictionary<string, (int Row, Control[] Controls, float Height)> phoneRows = new();
    private readonly Dictionary<string, Label> phoneLabels = new();
    private TableLayoutPanel? phoneTable;
    private Label info = null!;
    public AppConfig? Saved { get; private set; }

    public SettingsForm(AppConfig config)
    {
        source = config;
        Text = "Medialink CTI Client · Impostazioni";
        Width = 590; Height = 650; MinimumSize = new Size(530, 530);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        Font = new Font("Segoe UI", 9.5F);
        BackColor = Color.White;
        ShowInTaskbar = false;
        Build();
        Disposed += (_, _) => phoneHelp.Dispose();
    }

    private void Build()
    {
        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(13, 8) };
        tabs.TabPages.Add(BuildGeneral());
        tabs.TabPages.Add(BuildBlf());
        tabs.TabPages.Add(BuildAmi());
        tabs.TabPages.Add(BuildLdap());
        tabs.TabPages.Add(BuildCdr());

        var footer = new Panel { Height = 82, Dock = DockStyle.Bottom, BackColor = Color.FromArgb(242, 245, 249) };
        info = new Label { Dock = DockStyle.Top, Height = 40, Padding = new Padding(14, 7, 5, 0),
            ForeColor = Color.FromArgb(68, 83, 105), Text = "Le password sono protette da Windows (DPAPI)." };
        var save = new Button { Text = "Salva", Width = 105, Height = 29, Anchor = AnchorStyles.Right | AnchorStyles.Bottom };
        var cancel = new Button { Text = "Annulla", Width = 95, Height = 29, Anchor = AnchorStyles.Right | AnchorStyles.Bottom };
        footer.Controls.Add(info); footer.Controls.Add(save); footer.Controls.Add(cancel);
        footer.Resize += (_, _) => { save.Location = new Point(footer.ClientSize.Width - 122, 47);
            cancel.Location = new Point(footer.ClientSize.Width - 224, 47); };
        save.Click += (_, _) => Save();
        cancel.Click += (_, _) => Close();
        Controls.Add(tabs); Controls.Add(footer);
        AcceptButton = save; CancelButton = cancel;
    }

    private (TabPage page, TableLayoutPanel table) CreateTab(string caption)
    {
        var page = new TabPage(caption) { BackColor = Color.White, Padding = new Padding(8) };
        var holder = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var table = new TableLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top,
            ColumnCount = 2, Padding = new Padding(5, 10, 5, 5), Margin = new Padding(0)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 195));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        holder.Controls.Add(table); page.Controls.Add(holder);
        return (page, table);
    }

    private void AddCountrySelector(TableLayoutPanel table)
    {
        int row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 47));
        table.Controls.Add(new Label { Text = "Paese / prefisso internazionale", Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true }, 0, row);
        countrySelect.DropDownStyle = ComboBoxStyle.DropDownList;
        countrySelect.Dock = DockStyle.Fill;
        countrySelect.Margin = new Padding(3, 10, 7, 8);
        // No DataSource here: a WinForms ComboBox can reset an early SelectedItem
        // to the first record when it is attached to a parent BindingContext.
        // A static items list plus SelectedIndex keeps the selected ISO stable.
        countrySelect.Items.AddRange(DialCountryCatalog.All
            .Select(country => (object)country.DisplayName).ToArray());
        table.Controls.Add(countrySelect, 1, row);
        countrySelect.SelectedIndex = DialCountryCatalog.IndexByIso(source.DialCountryIso);
        phoneHelp.SetToolTip(countrySelect,
            "Italia (+39) predefinita. Rimuove +39/0039 solo a inizio numero. Non aggiunge +39 a numeri nazionali.");
    }

    private string SelectedCountryIso()
    {
        int index = countrySelect.SelectedIndex;
        return index >= 0 && index < DialCountryCatalog.All.Count
            ? DialCountryCatalog.All[index].Iso2 : "IT";
    }

    // Read-only UI/serialization regression test. Never saves settings, modifies
    // Windows protocol associations, starts a call, or contacts the PBX.
    internal static bool SelfTestCountrySelection()
    {
        using var fresh = new SettingsForm(new AppConfig());
        fresh.CreateControl();
        if (fresh.SelectedCountryIso() != "IT" ||
            fresh.GetFromInputs().DialCountryIso != "IT") return false;

        fresh.countrySelect.SelectedIndex = DialCountryCatalog.IndexByIso("DE");
        if (fresh.SelectedCountryIso() != "DE") return false;
        var chosen = fresh.GetFromInputs();
        if (chosen.DialCountryIso != "DE") return false;
        // In-memory roundtrip reproduces the JSON persistence contract.
        string json = JsonSerializer.Serialize(chosen);
        var persisted = JsonSerializer.Deserialize<AppConfig>(json);
        if (persisted is null || persisted.DialCountryIso != "DE") return false;

        using var reopened = new SettingsForm(persisted);
        reopened.CreateControl();
        if (reopened.SelectedCountryIso() != "DE" ||
            reopened.GetFromInputs().DialCountryIso != "DE") return false;

        reopened.countrySelect.SelectedIndex = DialCountryCatalog.IndexByIso("IT");
        if (reopened.GetFromInputs().DialCountryIso != "IT") return false;
        using var itReopened = new SettingsForm(new AppConfig { DialCountryIso = "IT" });
        itReopened.CreateControl();
        if (itReopened.SelectedCountryIso() != "IT") return false;

        // Invalid/missing country gracefully falls back to Italy, not index zero.
        using var invalid = new SettingsForm(new AppConfig { DialCountryIso = "ZZ" });
        invalid.CreateControl();
        return invalid.SelectedCountryIso() == "IT";
    }

    private TabPage BuildBlf()
    {
        var (page, t) = CreateTab("BLF");
        Separator(t,"Scegli gli interni da ou=pbx,dc=pbx,dc=com (sola lettura).");
        Field(t,"Interni BLF", "BlfExtensions", source.BlfExtensions);
        var select = new Button { Text="Aggiungi / rimuovi dalla rubrica interni...", AutoSize=true,
            Margin=new Padding(8, 10, 8, 6) };
        int row = t.RowCount++;
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.Controls.Add(select, 1, row);
        select.Click += async (_, _) =>
        {
            select.Enabled = false;
            try
            {
                // Use current unsaved LDAP connection settings, not stale values from disk.
                // Password is never displayed or copied into BLF config.
                var current = GetFromInputs();
                current.LdapSecret = inputs.ContainsKey("LdapSecret") ? inputs["LdapSecret"].Text : source.LdapSecret;
                var ldap = new LdapClient();
                var available = await ldap.ListInternalExtensionsAsync(current);
                using var picker = new BlfDirectoryPicker(available, inputs["BlfExtensions"].Text, current.Extension);
                if (picker.ShowDialog(this) == DialogResult.OK)
                    inputs["BlfExtensions"].Text = picker.SelectedExtensions;
            }
            catch (Exception error)
            {
                DiagnosticLog.Error("BLF_DIRECTORY_LOAD_FAILED", error);
                MessageBox.Show(this, "Impossibile leggere la rubrica interni della UCM: " + error.Message,
                    "BLF - rubrica interni", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { select.Enabled = true; }
        };
        Field(t,"Prefisso pickup", "BlfPickupPrefix", source.BlfPickupPrefix,
            help:"Predefinito **. Sul tasto giallo compone il prefisso + numero interno.");
        Field(t,"Aggiornamento SIP Presence (sec)", "PresenceRefreshSeconds",
            (source.PresenceRefreshSeconds is >= 5 and <= 300 ? source.PresenceRefreshSeconds : 15).ToString(),
            help:"Predefinito 15 secondi. Valori consentiti 5-300; non modifica gli aggiornamenti telefonici AMI.");
        return page;
    }

    private TabPage BuildGeneral()
    {
        var (page, t) = CreateTab("Generali");
        Separator(t, "Comportamento del client nell'area di notifica di Windows.");
        bool currentlyRegistered;
        try { currentlyRegistered = WindowsStartup.IsCurrentExecutableRegistered(); }
        catch (Exception e)
        {
            DiagnosticLog.Error("AUTOSTART_READ_FAILED", e);
            currentlyRegistered = source.StartWithWindows;
        }
        Check(t, "Avvia con Windows (utente corrente)", "StartWithWindows", currentlyRegistered);
        Check(t, "Mantieni il popup in primo piano", "KeepPopupOnTop", source.KeepPopupOnTop);
        Separator(t, "CLICK-TO-DIAL ESTERNO (Windows)");
        Check(t, "Abilita Click-to-Dial esterno (tel:, callto:)",
            "EnableExternalClickToDial", source.EnableExternalClickToDial,
            "tel: e callto: sono facoltativi e possono essere assegnati in App predefinite " +
            "di Windows senza sovrascrivere le associazioni esistenti.");
        Check(t, "Attiva scorciatoia globale per numero selezionato",
            "EnableExternalHotkey", source.EnableExternalHotkey,
            "Seleziona un numero in Outlook, browser o file di testo e premi la scorciatoia. " +
            "Per leggere la selezione il client usa temporaneamente Copia e poi ripristina gli appunti, ove possibile.");
        Field(t, "Scorciatoia (es. Ctrl+D)", "ExternalHotkeyGesture", source.ExternalHotkeyGesture,
            help: "Esempi: Ctrl+D, Ctrl+Shift+D, Ctrl+Alt+F8. Scorciatoie occupate da altri programmi possono non registrarsi.");
        Check(t, "Chiedi conferma prima di comporre (link e scorciatoia)",
            "ExternalDialRequireConfirmation", source.ExternalDialRequireConfirmation,
            "Disattivando questa opzione le richieste valide compongono subito il numero. " +
            "Puoi riattivare la conferma in qualsiasi momento; consigliato per pagine web non attendibili.");
        Separator(t, "REGOLE NUMERAZIONE IN USCITA");
        AddCountrySelector(t);
        Field(t, "Prefisso impegno linea esterna", "ExternalLineAccessPrefix", source.ExternalLineAccessPrefix,
            help: "Vuoto = nessun prefisso. Per esempio 0 viene aggiunto solo ai numeri esterni, non agli interni. " +
                  "Il prefisso internazionale del Paese scelto (+39 o 0039 per Italia) viene rimosso; " +
                  "per gli altri Paesi, il + iniziale diventa 00.");
        Separator(t, "Per usare tel: o callto: da Outlook/browser, seleziona Medialink CTI Client " +
            "in Impostazioni Windows > App > App predefinite per tipo di collegamento. " +
            "Il programma va installato in una cartella definitiva prima di abilitarlo.");
        Separator(t, "Registro: sincronizzazione all'avvio e manuale. Il monitor AMI aggiorna " +
            "i CDR dopo la fine delle chiamate, con brevi tentativi se il record non è ancora disponibile.");
        Field(t, "Record CDR da conservare", "CdrMaxRecords", source.CdrMaxRecords.ToString());
        Separator(t, "Notifiche chiamata: la barra compatta mostra anche chiamate uscenti e conversazioni attive quando il pannello principale è nascosto.");
        Field(t, "Chiusura mini-popup (secondi, 0=mai)", "MiniPopupTimeoutSeconds", source.MiniPopupTimeoutSeconds.ToString());
        Separator(t, "Alla scadenza il mini-popup si nasconde per quella chiamata. La chiamata e il monitor AMI continuano regolarmente.");
        Separator(t, "Il clic sull'icona Medialink CTI apre o chiude sempre il pannello. " +
            "Se 'Mantieni il popup in primo piano' e disattivato, il pannello si chiude anche quando selezioni un'altra finestra.");
        Separator(t, "Avvio automatico: impostazione per il solo account Windows corrente. " +
            "Non modifica la configurazione di AMI, LDAP o CDR.");
        return page;
    }

    private TabPage BuildAmi()
    {
        var (page, t) = CreateTab("Telefono / AMI");
        phoneTable = t;
        Section(t, "CENTRALINO / AMI");
        Field(t, "Interno telefonico", "Extension", source.Extension,
            help: "Interno SIP da monitorare sul centralino. Deve coincidere con quello associato al telefono.");
        Field(t, "Nome utente interno", "ExtensionDisplayName", source.ExtensionDisplayName,
            help: "Esempio: Antonio. Se vuoto, il nome viene cercato nella rubrica LDAP dell'interno.");
        Field(t, "Server UCM / AMI", "AmiHost", source.AmiHost,
            help: "Indirizzo IP o nome DNS del centralino che espone AMI.");
        Field(t, "Porta AMI", "AmiPort", source.AmiPort.ToString(),
            help: "Porta AMI configurata sulla UCM (nel laboratorio è 7777).");
        Check(t, "Usa TLS per AMI", "AmiTls", source.AmiTls,
            "Abilita soltanto se il centralino supporta AMI su TLS e ha un certificato valido.");
        Field(t, "Utente AMI", "AmiUser", source.AmiUser,
            help: "Utente AMI autorizzato a leggere gli eventi e inviare i comandi già configurati.");
        Field(t, "Password AMI", "AmiSecret", source.AmiSecret, true,
            "Credenziale AMI, memorizzata con DPAPI nell'account Windows corrente.");

        Section(t, "CHIAMATE");
        Check(t, "Click-to-Dial con AutoAnswer", "AutoAnswerClickToDial", source.AutoAnswerClickToDial,
            "Su Grandstream avvia la chiamata AMI con SIP AutoAnswer. " +
            "Snom usa HTTP Digest, Yealink invia HTTPS diretto al telefono (Action URI), sempre con verifica AMI.");
        TrackPhoneRow(t, "AutoAnswerClickToDial");
        Field(t, "Cifre max interno", "InternalExtensionMaxLength", source.InternalExtensionMaxLength.ToString(),
            help: "Numeri fino a questa lunghezza sono trattati come interni, non come chiamate esterne.");
        Field(t, "Contesto interni", "InternalContext", source.InternalContext,
            help: "Contesto dialplan usato per i numeri interni.");
        Field(t, "Contesto esterni", "OutboundContext", source.OutboundContext,
            help: "Contesto dialplan usato per i numeri esterni.");

        Section(t, "CONTROLLO TELEFONO");
        PhoneTypeField(t);
        Field(t, "URL Gateway Avaya", "AvayaGatewayUrl", source.AvayaGatewayUrl,
            help: "HTTPS con certificato attendibile. HTTP sulla sola interfaccia loopback.");
        TrackPhoneRow(t, "AvayaGatewayUrl");
        Field(t, "Chiave API Gateway Avaya", "AvayaGatewayKey", source.AvayaGatewayKey, true,
            "Chiave del client API autorizzato sul Gateway, protetta con DPAPI.");
        TrackPhoneRow(t, "AvayaGatewayKey");
        Check(t, "Rileva IP automaticamente da AMI", "AutoDetectPhoneAddress", source.AutoDetectPhoneAddress,
            "Consigliato. Legge i contatti SIP registrati sulla UCM. Se più telefoni non sono distinguibili, " +
            "il client non ne seleziona uno a caso.");
        TrackPhoneRow(t, "AutoDetectPhoneAddress");
        Field(t, "IP telefono", "PhoneAddress", source.PhoneAddress,
            help: "Utilizzato soltanto disattivando il rilevamento automatico. Non serve assegnare IP statici.");
        TrackPhoneRow(t, "PhoneAddress");
        Field(t, "Passcode CTI", "PhoneControlPassword", source.PhoneControlPassword, true,
            "Passcode del controllo remoto Grandstream. Il telefono deve autorizzare il PC al controllo CTI/Action URI.");
        TrackPhoneRow(t, "PhoneControlPassword");
        Field(t, "Utente web Snom", "SnomWebUsername", source.SnomWebUsername,
            help: "Utente dell'interfaccia web Snom, usato con autenticazione HTTP Digest. Nessuna Action URI.");
        TrackPhoneRow(t, "SnomWebUsername");
        Field(t, "Password web Snom", "SnomWebPassword", source.SnomWebPassword, true,
            "Password dell'interfaccia web Snom. È indipendente dal passcode Grandstream e cifrata con DPAPI.");
        TrackPhoneRow(t, "SnomWebPassword");
        Field(t, "Utente web Yealink", "YealinkWebUsername", source.YealinkWebUsername,
            help: "Credenziale web del telefono Yealink, se richiesta. Nessun Action URL o listener sul PC.");
        TrackPhoneRow(t, "YealinkWebUsername");
        Field(t, "Password web Yealink", "YealinkWebPassword", source.YealinkWebPassword, true,
            "Credenziale separata per Yealink, cifrata con DPAPI. In HTTPS sono ammessi Digest o Basic; HTTP solo Digest.");
        TrackPhoneRow(t, "YealinkWebPassword");
        CtiProtocolField(t);
        TrackPhoneRow(t, "PhoneCtiProtocol");
        Field(t, "Porta CTI (0=automatica)", "PhoneCtiPort", source.PhoneCtiPort.ToString(),
            help: "Porta web del telefono; 0 usa quella prevista dal protocollo o dal redirect sul medesimo IP.");
        TrackPhoneRow(t, "PhoneCtiPort");
        Check(t, "Certificato HTTPS non attendibile (LAB)",
            "AllowUntrustedPhoneCertificate", source.AllowUntrustedPhoneCertificate,
            "Solo per LAN di laboratorio. In produzione utilizzare un certificato attendibile. " +
            "L'eccezione è limitata all'indirizzo privato del telefono e non deve essere usata su Internet.");
        TrackPhoneRow(t, "AllowUntrustedPhoneCertificate");

        Test(t, "Diagnostica CTI (sola lettura)",
            async () => await new PhoneControlClient().DiagnoseAsync(GetFromInputs()));
        TrackPhoneRow(t, "CtiTest");
        Test(t, "Verifica connessione AMI", async () => await new AmiClient().TestAsync(GetFromInputs()));

        phoneType.SelectedIndexChanged += (_, _) => RefreshPhoneSettings();
        checks["AutoDetectPhoneAddress"].CheckedChanged += (_, _) => RefreshPhoneSettings();
        phoneCtiProtocol.SelectedIndexChanged += (_, _) => RefreshPhoneSettings();
        RefreshPhoneSettings();
        return page;
    }

    // Hide unneeded rows without disposing their editors: switching between profiles never loses passwords.
    private void TrackPhoneRow(TableLayoutPanel table, string key)
    {
        var row = table.RowCount - 1;
        var controls = table.Controls.Cast<Control>().Where(c => table.GetRow(c) == row).ToArray();
        phoneRows.Add(key, (row, controls, table.RowStyles[row].Height));
        Control? labelCell = table.GetControlFromPosition(0, row);
        var label = labelCell as Label ?? labelCell?.Controls.OfType<Label>().FirstOrDefault();
        if (label != null) phoneLabels[key] = label;
    }

    private void SetPhoneRow(string key, bool show)
    {
        if (phoneTable is null || !phoneRows.TryGetValue(key, out var item)) return;
        var style = phoneTable.RowStyles[item.Row];
        style.SizeType = SizeType.Absolute;
        style.Height = show ? item.Height : 0;
        foreach (var c in item.Controls) c.Visible = show;
    }

    private void SetPhoneLabel(string key, string text)
    {
        if (phoneLabels.TryGetValue(key, out var label)) label.Text = text;
    }

    private void RefreshPhoneSettings()
    {
        if (phoneTable is null || !phoneRows.ContainsKey("PhoneCtiProtocol")) return;
        string vendor = phoneType.SelectedItem?.ToString() ?? "Altro";
        bool grandstream = vendor == "Grandstream";
        bool snom = vendor == "Snom";
        bool yealink = vendor == "Yealink";
        bool deviceControl = grandstream || snom || yealink;
        bool webControl = grandstream || snom || yealink;
        bool autoIp = checks["AutoDetectPhoneAddress"].Checked;
        bool https = phoneCtiProtocol.SelectedItem?.ToString() != "HTTP";

        phoneTable.SuspendLayout();
        try
        {
            SetPhoneRow("AutoAnswerClickToDial", grandstream);
            SetPhoneRow("AutoDetectPhoneAddress", deviceControl);
            SetPhoneRow("PhoneAddress", deviceControl && !autoIp);
            SetPhoneRow("PhoneControlPassword", grandstream);
            SetPhoneRow("SnomWebUsername", snom);
            SetPhoneRow("SnomWebPassword", snom);
            SetPhoneRow("AvayaGatewayUrl", vendor == "Avaya");
            SetPhoneRow("AvayaGatewayKey", vendor == "Avaya");
            SetPhoneRow("YealinkWebUsername", yealink);
            SetPhoneRow("YealinkWebPassword", yealink);
            SetPhoneRow("PhoneCtiProtocol", webControl);
            SetPhoneRow("PhoneCtiPort", webControl);
            SetPhoneRow("AllowUntrustedPhoneCertificate", webControl && https);
            SetPhoneRow("CtiTest", webControl);
            SetPhoneLabel("PhoneAddress", grandstream ? "IP Grandstream" :
                snom ? "IP Snom" : yealink ? "IP Yealink" : "IP telefono");
            SetPhoneLabel("PhoneCtiProtocol", snom ? "Protocollo web Snom" : yealink ? "Protocollo web Yealink" : "Protocollo CTI");
            SetPhoneLabel("PhoneCtiPort", snom ? "Porta web Snom (0=auto)" : yealink ? "Porta web Yealink (0=auto)" : "Porta CTI (0=auto)");
        }
        finally { phoneTable.ResumeLayout(true); }
    }

    private static void Section(TableLayoutPanel table, string caption)
    {
        int row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        var heading = new Label
        {
            Text = caption, Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomLeft,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            ForeColor = Color.FromArgb(39, 83, 137),
            Padding = new Padding(2, 6, 0, 3)
        };
        table.Controls.Add(heading, 0, row);
        table.SetColumnSpan(heading, 2);
    }

    private Control LabelWithHelp(string caption, string help)
    {
        var container = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false, Margin = new Padding(0), Padding = new Padding(0)
        };
        var name = new Label
        {
            Text = caption, AutoSize = true,
            ForeColor = Color.FromArgb(44, 56, 73),
            Margin = new Padding(0, 11, 2, 0)
        };
        container.Controls.Add(name);
        container.Controls.Add(InfoButton(help));
        return container;
    }

    private Button InfoButton(string help)
    {
        var button = new Button
        {
            Text = "ⓘ", Size = new Size(21, 21),
            FlatStyle = FlatStyle.Flat, BackColor = Color.White,
            ForeColor = Color.FromArgb(38, 102, 168),
            Font = new Font("Segoe UI Symbol", 10F, FontStyle.Regular),
            Margin = new Padding(2, 8, 0, 0),
            Cursor = Cursors.Help, TabStop = true,
            AccessibleName = "Informazioni sul campo",
            AccessibleDescription = help
        };
        button.FlatAppearance.BorderSize = 0;
        phoneHelp.SetToolTip(button, help);
        button.Click += (_, _) => MessageBox.Show(this, help, "Informazioni impostazione",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
        return button;
    }

    private TabPage BuildLdap()
    {
        var (page, t) = CreateTab("Rubrica LDAP");
        Field(t, "Server LDAP", "LdapHost", source.LdapHost);
        Field(t, "Porta LDAP", "LdapPort", source.LdapPort.ToString());
        Check(t, "Usa LDAPS (SSL)", "LdapSsl", source.LdapSsl);
        Field(t, "Base DN", "LdapBaseDn", source.LdapBaseDn);
        Check(t, "Abilita modifica rubrica LDAP", "EnableLdapContactEditing", source.EnableLdapContactEditing,
            "Abilita i comandi + (nuovo contatto) e matita (modifica) nella Rubrica e nel CDR. " +
            "Quando è disattivata, la consultazione LDAP e i nomi nelle chiamate continuano a funzionare.");
        Field(t, "Rubrica destinazione (DN)", "LdapWritePhonebookDn", source.LdapWritePhonebookDn,
            help: "Rubrica esterna già esistente sulla UCM, es. ou=contatti,dc=pbx,dc=com. " +
                  "Non utilizzare ou=pbx,dc=pbx,dc=com: rubrica interni di sola lettura. " +
                  "Per scrivere, attivare anche Abilita modifica rubrica LDAP.");
        Field(t, "Bind DN / Utente", "LdapUser", source.LdapUser);
        Field(t, "Password LDAP", "LdapSecret", source.LdapSecret, true);
        Separator(t, "Filtri LDAP UCM (il simbolo % indica il testo cercato). I nomi degli attributi sono separati da spazi.");
        Field(t, "Filtro ricerca nomi", "LdapNameFilter", source.LdapNameFilter);
        Field(t, "Filtro ricerca numeri", "LdapNumberFilter", source.LdapNumberFilter);
        Field(t, "Attributi nomi", "LdapNameAttributes", source.LdapNameAttributes);
        Field(t, "Attributi numeri", "LdapNumberAttributes", source.LdapNumberAttributes);
        Separator(t, "La Base DN dc=pbx,dc=com include le sottocartelle. La diagnosi verifica Bind, ricerca e presenza dei numeri; non modifica il server.");
        Test(t, "Verifica destinazione rubrica (sola lettura)", async () =>
        {
            await new LdapClient().VerifyWritableDestinationAsync(GetFromInputs());
            return "Rubrica di destinazione trovata e non di sistema. Scrittura HTTPS API da verificare.";
        });
        Test(t, "Diagnostica LDAP", async () =>
        {
            var report = await new LdapClient().DiagnoseAsync(GetFromInputs());
            MessageBox.Show(this, report, "Diagnostica LDAP - Medialink CTI",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return "Verifica LDAP completata.";
        });
        return page;
    }

    private TabPage BuildCdr()
    {
        var (page, t) = CreateTab("Registro CDR");
        Separator(t, "API HTTPS Grandstream: Integrations → API Configuration → HTTPS API Settings (New). Credenziali diverse da AMI.");
        Field(t, "Server API", "ApiHost", source.ApiHost);
        Field(t, "Porta HTTPS", "ApiPort", source.ApiPort.ToString());
        Field(t, "Utente API", "ApiUser", source.ApiUser);
        Field(t, "Password API", "ApiSecret", source.ApiSecret, true);
        Check(t, "Accetta certificato non fidato (SOLO TEST)", "AllowUntrustedApiCertificate", source.AllowUntrustedApiCertificate);
        Test(t, "Verifica e carica CDR", async () =>
        {
            var found = await new CdrClient().GetForExtensionAsync(GetFromInputs());
            return $"Connessione CDR riuscita. {found.Count} righe per interno {inputs["Extension"].Text}.";
        });
        return page;
    }

    private void CtiProtocolField(TableLayoutPanel table)
    {
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
        table.Controls.Add(LabelWithHelp("Protocollo CTI",
            "Protocollo di controllo telefono: per Yealink Automatico usa solo HTTPS senza downgrade; " +
            "HTTP è una scelta esplicita (Digest soltanto). Per Snom Automatico usa HTTP. Grandstream resta invariato."), 0, row);
        phoneCtiProtocol.DropDownStyle = ComboBoxStyle.DropDownList;
        phoneCtiProtocol.Items.AddRange(new object[] { "Automatico", "HTTP", "HTTPS" });
        phoneCtiProtocol.SelectedItem = phoneCtiProtocol.Items.Contains(source.PhoneCtiProtocol)
            ? source.PhoneCtiProtocol : "Automatico";
        phoneCtiProtocol.Dock = DockStyle.Fill;
        phoneCtiProtocol.Margin = new Padding(3, 9, 6, 7);
        table.Controls.Add(phoneCtiProtocol, 1, row);
    }

    private void PhoneTypeField(TableLayoutPanel table)
    {
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
        table.Controls.Add(LabelWithHelp("Tipo telefono",
            "Grandstream: passcode CTI. Snom: HTTP Digest senza Action URI. " +
            "Yealink generico: richieste dirette HTTPS Action URI al telefono, senza Action URL né listener sul PC; " +
            "abilitare Action URI e autorizzare solo l'IP del computer sul telefono. Alcune funzioni variano col firmware."), 0, row);
        phoneType.DropDownStyle = ComboBoxStyle.DropDownList;
        phoneType.Items.AddRange(new object[] { "Grandstream", "Snom", "Yealink", "Avaya", "Altro" });
        phoneType.SelectedItem = phoneType.Items.Contains(source.PhoneType) ? source.PhoneType : "Altro";
        phoneType.Dock = DockStyle.Fill;
        phoneType.Margin = new Padding(3, 9, 6, 7);
        table.Controls.Add(phoneType, 1, row);
    }

    private void Field(TableLayoutPanel table, string label, string key, string value, bool password = false, string? help = null)
    {
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
        var l = new Label { Text = label, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft,
            Dock = DockStyle.Fill, ForeColor = Color.FromArgb(44, 56, 73) };
        var tb = new TextBox { Text = value, Dock = DockStyle.Fill, UseSystemPasswordChar = password,
            Margin = new Padding(3, 9, 6, 7) };
        inputs[key] = tb;
        table.Controls.Add(help is null ? l : LabelWithHelp(label, help), 0, row);
        table.Controls.Add(tb, 1, row);
    }

    private void Check(TableLayoutPanel table, string label, string key, bool value, string? help = null)
    {
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        var cb = new CheckBox { Text = label, Checked = value, Dock = DockStyle.Fill,
            Margin = new Padding(3, 8, 3, 4), ForeColor = Color.FromArgb(44, 56, 73) };
        checks[key] = cb;
        if (help is null)
        {
            table.Controls.Add(cb, 0, row);
            table.SetColumnSpan(cb, 2);
        }
        else
        {
            cb.AutoSize = true;
            cb.Dock = DockStyle.None;
            cb.Margin = new Padding(3, 8, 6, 0);
            var line = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false, Margin = new Padding(0), Padding = new Padding(0)
            };
            line.Controls.Add(cb);
            line.Controls.Add(InfoButton(help));
            table.Controls.Add(line, 0, row);
            table.SetColumnSpan(line, 2);
        }
    }

    private static void Separator(TableLayoutPanel table, string label)
    {
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var l = new Label { Text = label, AutoSize = true, MaximumSize = new Size(495, 0),
            Margin = new Padding(3, 12, 2, 10), ForeColor = Color.FromArgb(89, 107, 128) };
        table.Controls.Add(l, 0, row); table.SetColumnSpan(l, 2);
    }

    private void Test(TableLayoutPanel table, string text, Func<Task> action)
    {
        Test(table, text, async () => { await action(); return "Connessione AMI riuscita."; });
    }

    private void Test(TableLayoutPanel table, string text, Func<Task<string>> action)
    {
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 55));
        var button = new Button { Text = text, AutoSize = true, Height = 32,
            Margin = new Padding(3, 9, 3, 9), BackColor = Color.FromArgb(226, 236, 247), FlatStyle = FlatStyle.Flat };
        button.Click += async (_, _) =>
        {
            button.Enabled = false; info.Text = "Verifica in corso...";
            try
            {
                DiagnosticLog.Info("CONNECTION_TEST_START", "kind=" + TestKind(text));
                info.Text = await action();
                DiagnosticLog.Info("CONNECTION_TEST_OK", "kind=" + TestKind(text));
            }
            catch (Exception e)
            {
                DiagnosticLog.Error("CONNECTION_TEST_FAILED_" + TestKind(text), e);
                info.Text = "Errore: " + e.Message;
            }
            finally { button.Enabled = true; }
        };
        table.Controls.Add(button, 0, row); table.SetColumnSpan(button, 2);
    }

    private static string TestKind(string title) =>
        title.Contains("LDAP", StringComparison.OrdinalIgnoreCase) ||
        title.Contains("rubrica", StringComparison.OrdinalIgnoreCase) ? "LDAP" :
        title.Contains("CDR", StringComparison.OrdinalIgnoreCase) ? "CDR" :
        title.Contains("CTI", StringComparison.OrdinalIgnoreCase) ? "CTI" : "AMI";

    private AppConfig GetFromInputs()
    {
        static int Port(string value, string what)
        {
            if (!int.TryParse(value, out var n) || n is < 1 or > 65535)
                throw new InvalidOperationException(what + ": valore non valido.");
            return n;
        }
        var c = new AppConfig
        {
            StartWithWindows = checks["StartWithWindows"].Checked,
            KeepPopupOnTop = checks["KeepPopupOnTop"].Checked,
            BlfExtensions = inputs["BlfExtensions"].Text.Trim(),
            BlfPickupPrefix = inputs["BlfPickupPrefix"].Text.Trim(),
            PresenceRefreshSeconds = int.TryParse(inputs["PresenceRefreshSeconds"].Text.Trim(), out var presenceSeconds) && presenceSeconds is >= 5 and <= 300
                ? presenceSeconds
                : throw new InvalidOperationException("Intervallo SIP Presence: inserire un numero da 5 a 300 secondi."),
            EnableExternalClickToDial = checks["EnableExternalClickToDial"].Checked,
            EnableExternalHotkey = checks["EnableExternalHotkey"].Checked,
            ExternalHotkeyGesture = inputs["ExternalHotkeyGesture"].Text.Trim(),
            ExternalDialRequireConfirmation = checks["ExternalDialRequireConfirmation"].Checked,
            DialCountryIso = SelectedCountryIso(),
            ExternalLineAccessPrefix = inputs["ExternalLineAccessPrefix"].Text.Trim(),
            CdrMaxRecords = 150,
            MiniPopupTimeoutSeconds = source.MiniPopupTimeoutSeconds,
            AutoAnswerClickToDial = checks["AutoAnswerClickToDial"].Checked,
            DialPreferenceSchemaVersion = 1,
            PhoneType = phoneType.SelectedItem?.ToString() ?? "Altro",
            PhoneAddress = inputs["PhoneAddress"].Text.Trim(),
            AutoDetectPhoneAddress = checks["AutoDetectPhoneAddress"].Checked,
            PhoneControlPassword = inputs["PhoneControlPassword"].Text,
            SnomWebUsername = inputs["SnomWebUsername"].Text.Trim(),
            SnomWebPassword = inputs["SnomWebPassword"].Text,
            AvayaGatewayUrl = inputs["AvayaGatewayUrl"].Text.Trim(),
            AvayaGatewayKey = inputs["AvayaGatewayKey"].Text.Trim(),
            YealinkWebUsername = inputs["YealinkWebUsername"].Text.Trim(),
            YealinkWebPassword = inputs["YealinkWebPassword"].Text,
            PhoneCtiProtocol = phoneCtiProtocol.SelectedItem?.ToString() ?? "Automatico",
            AllowUntrustedPhoneCertificate = checks["AllowUntrustedPhoneCertificate"].Checked,
            Extension = inputs["Extension"].Text.Trim(),
            ExtensionDisplayName = inputs["ExtensionDisplayName"].Text.Trim(),
            AmiHost = inputs["AmiHost"].Text.Trim(),
            AmiPort = Port(inputs["AmiPort"].Text, "Porta AMI"),
            AmiUser = inputs["AmiUser"].Text.Trim(),
            AmiSecret = inputs["AmiSecret"].Text,
            AmiTls = checks["AmiTls"].Checked,
            InternalContext = inputs["InternalContext"].Text.Trim(),
            OutboundContext = inputs["OutboundContext"].Text.Trim(),
            LdapHost = inputs["LdapHost"].Text.Trim(),
            LdapPort = Port(inputs["LdapPort"].Text, "Porta LDAP"),
            LdapSsl = checks["LdapSsl"].Checked,
            LdapBaseDn = inputs["LdapBaseDn"].Text.Trim(),
            LdapWritePhonebookDn = inputs["LdapWritePhonebookDn"].Text.Trim(),
            EnableLdapContactEditing = checks["EnableLdapContactEditing"].Checked,
            LdapNameFilter = inputs["LdapNameFilter"].Text.Trim(),
            LdapNumberFilter = inputs["LdapNumberFilter"].Text.Trim(),
            LdapNameAttributes = inputs["LdapNameAttributes"].Text.Trim(),
            LdapNumberAttributes = inputs["LdapNumberAttributes"].Text.Trim(),
            LdapUser = inputs["LdapUser"].Text.Trim(),
            LdapSecret = inputs["LdapSecret"].Text,
            ApiHost = inputs["ApiHost"].Text.Trim(),
            ApiPort = Port(inputs["ApiPort"].Text, "Porta API"),
            ApiUser = inputs["ApiUser"].Text.Trim(),
            ApiSecret = inputs["ApiSecret"].Text,
            AllowUntrustedApiCertificate = checks["AllowUntrustedApiCertificate"].Checked
        };
        if (!BlfLogic.ValidPrefix(c.BlfPickupPrefix))
            throw new InvalidOperationException("Prefisso pickup BLF: usare solo *, # e cifre, massimo 12 caratteri.");
        if (!string.IsNullOrEmpty(c.LdapWritePhonebookDn))
            UcmContactWriteRules.ValidateDestinationDn(c.LdapWritePhonebookDn);
        if (!PhoneNumbers.ValidExtension(c.Extension)) throw new InvalidOperationException("Interno deve avere da 1 a 8 cifre.");
        if (c.ExtensionDisplayName.Length > 90 || c.ExtensionDisplayName.Contains('\n') || c.ExtensionDisplayName.Contains('\r'))
            throw new InvalidOperationException("Nome utente interno: massimo 90 caratteri, senza righe multiple.");
        if (!int.TryParse(inputs["InternalExtensionMaxLength"].Text, out var length) || length is < 1 or > 8)
            throw new InvalidOperationException("Lunghezza massima interno deve essere fra 1 e 8.");
        c.InternalExtensionMaxLength = length;
        if (!int.TryParse(inputs["PhoneCtiPort"].Text, out var ctiPort) || ctiPort is < 0 or > 65535)
            throw new InvalidOperationException("Porta CTI telefono: indicare 0 oppure una porta 1-65535.");
        c.PhoneCtiPort = ctiPort;
        if (c.PhoneType == "Avaya") AvayaGatewayClient.Validate(c);
        if (c.PhoneType == "Yealink" &&
            (string.IsNullOrWhiteSpace(c.YealinkWebUsername) != string.IsNullOrWhiteSpace(c.YealinkWebPassword)))
            throw new InvalidOperationException("Yealink: specificare sia utente sia password web oppure lasciarli entrambi vuoti.");
        if (!int.TryParse(inputs["CdrMaxRecords"].Text, out var maxCdr) || maxCdr is < 25 or > 2000)
            throw new InvalidOperationException("Record CDR da conservare: inserire un numero fra 25 e 2000.");
        c.CdrMaxRecords = maxCdr;
        if (!Regex.IsMatch(c.ExternalLineAccessPrefix, @"^[0-9]{0,6}$"))
            throw new InvalidOperationException("Impegno linea esterna: usare da 0 a 6 cifre, senza spazi.");
        if (!GlobalHotkeyRules.TryParse(c.ExternalHotkeyGesture, out _, out _))
            throw new InvalidOperationException("Scorciatoia non valida. Usare Ctrl+D, Ctrl+Shift+D, Ctrl+Alt+F8 o simili.");
        if (!int.TryParse(inputs["MiniPopupTimeoutSeconds"].Text, out var miniSeconds) || miniSeconds is < 0 or > 3600)
            throw new InvalidOperationException("Timeout mini-popup: inserire 0 (mai) oppure un valore fra 1 e 3600 secondi.");
        c.MiniPopupTimeoutSeconds = miniSeconds;
        return c;
    }

    private void Save()
    {
        try
        {
            var c = GetFromInputs();
            // HKCU Run e settings.json: rollback della chiave se il salvataggio fallisce.
            var previousStartup = WindowsStartup.ExistingCommand();
            WindowsStartup.Configure(c.StartWithWindows);
            bool externalRegistrationAttempted = false;
            try
            {
                externalRegistrationAttempted = true;
                ExternalDialRegistry.Configure(c.EnableExternalClickToDial);
                ConfigStore.Save(c);
            }
            catch
            {
                try { WindowsStartup.Restore(previousStartup); }
                catch (Exception rollbackError)
                { DiagnosticLog.Error("AUTOSTART_ROLLBACK_FAILED", rollbackError); }
                if (externalRegistrationAttempted)
                {
                    try { ExternalDialRegistry.Configure(source.EnableExternalClickToDial); }
                    catch (Exception rollbackError)
                    { DiagnosticLog.Error("EXTERNAL_DIAL_REGISTRATION_ROLLBACK_FAILED", rollbackError); }
                }
                throw;
            }
            Saved = c;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception e)
        {
            DiagnosticLog.Error("SETTINGS_SAVE_FAILED", e);
            MessageBox.Show(e.Message, "Impostazioni non salvate", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
