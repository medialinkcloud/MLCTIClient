using System.DirectoryServices.Protocols;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using System.Runtime.InteropServices;

namespace MedialinkCTIClient;

internal sealed class PopupForm : Form
{
    private AppConfig config;
    private readonly Func<bool> openSettings;
    private readonly AmiClient ami = new();
    private readonly System.Windows.Forms.Timer avayaBlfTimer = new() { Interval = 3000 };
    private IReadOnlyList<AvayaBlfRow> avayaBlf = Array.Empty<AvayaBlfRow>();
    private bool avayaBlfBusy;
    private bool avayaBlfUnavailable;
    private readonly LdapClient ldap = new();
    private readonly CdrClient cdr = new();
    private readonly Button bookTab = new(), callsTab = new(), refresh = new();
    private readonly Label recordCount = new();
    private readonly FlowLayoutPanel blfPanel = new();
    private readonly TextBox blfSearch = new();
    private readonly Label blfSearchCaption = new();
    private readonly Panel blfSidebar = new();
    private readonly Label blfSidebarTitle = new();
    private readonly Dictionary<string, BlfState> blfStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string,PresenceState> presenceStates = new(StringComparer.Ordinal);
    private readonly UcmPresenceClient presenceApi = new();
    private readonly System.Windows.Forms.Timer presenceTimer = new() { Interval = 15000 };
    private readonly Button ownPresence = new();
    private readonly Button ownPresenceBadge = new();
    private bool presenceBusy;
    private PresenceState ownPresenceState = PresenceState.Unknown;

    
    private Panel? searchFilterPanel;
    private readonly Panel callFilters = new();
    private readonly Button[] callFilterButtons = new Button[4];
    private enum CallFilter { All, Outgoing, Incoming, Missed }
    private CallFilter selectedCallFilter = CallFilter.All;
    private readonly Image callBlue = LoadIcon("CallBlue.png");
    private readonly Image outgoingIcon = LoadIcon("Outgoing.png");
    private readonly Image incomingIcon = LoadIcon("Incoming.png");
    private readonly Image missedIcon = LoadIcon("Missed.png");
    private readonly TextBox search = new();
    private readonly Label status = new(), title = new();
    // High-resolution application artwork for the call panel header (not a second app icon).
    private readonly Image headerLogoImage = LoadIcon("MedialinkCTI.png");
    private readonly PictureBox headerLogo = new();
    private readonly Button chromeRestore = new(), chromeClose = new();
    private Panel? chromeHeader;
    private readonly DataGridView grid = new();
    private readonly Panel gridHost = new();
    private readonly System.Windows.Forms.Timer debounce = new() { Interval = 450 };
    private bool cdrLoading;
    private bool cdrInitialized;
    private CancellationTokenSource? activeSearch;
    private bool showCalls;
    public long LastAutoHideTick { get; private set; }
    private List<Contact> contacts = new();
    private string? contactSortColumn;
    private bool contactSortAscending = true;
    private List<CallEntry> calls = new();
    private bool hasSavedWindowBounds;
    private readonly Panel callBar = new();
    private readonly PictureBox liveIcon = new();
    private readonly Label liveInfo = new(), liveName = new(), liveDuration = new();
    private readonly Button answerCall = new(), endCall = new();
    private readonly TextBox dialNumber = new();
    private readonly Button dialFromBar = new(), toggleKeypad = new();
    private readonly Panel keypadPanel = new();
    private readonly SemaphoreSlim dtmfQueue = new(1, 1);
    private readonly Dictionary<string, LdapContactMatch> cdrDirectory = new(StringComparer.OrdinalIgnoreCase);
    // A failed or partial search is UNKNOWN, never an assertion of absence.
    private readonly HashSet<string> unverifiedLdapKeys = new(StringComparer.OrdinalIgnoreCase);
    private DateTime lastCdrLdapCheckUtc = DateTime.MinValue;
    private int ldapCacheGeneration;
    private int ldapMutationRefreshGeneration;
    private bool cdrDirectoryAvailable;
    private readonly HashSet<string> liveLookupPending = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> liveLookupFailed = new(StringComparer.OrdinalIgnoreCase);
    // One live recheck per number/per call, even after a cached negative CDR
    // lookup; AMI may publish multiple events for the same physical call.
    private readonly HashSet<string> liveLookupAttempted = new(StringComparer.OrdinalIgnoreCase);
    private string? liveLookupChannel;
    private string extensionName = "";
    private bool extensionNameLookupPending;
    private bool extensionNameLookupAttempted;

    private readonly PhoneControlClient phone = new();
    private readonly MiniCallForm miniCall;
    private string? dismissedMiniChannel;
    private string lastMiniDecision = "";
    private readonly ToolTip controlHelp = new();
    private readonly Font plusFont = new("Segoe UI", 16F, FontStyle.Bold);
    private readonly Font editFont = new("Segoe UI", 13F, FontStyle.Bold);
    private readonly Image deleteIcon = CreateDeleteIcon();
    private readonly Image keypadIcon = CreateKeypadIcon();
    private readonly System.Windows.Forms.Timer liveClock = new() { Interval = 1000 };
    private LiveCall? currentLiveCall;
    private bool monitorConnected;
    private int dialInProgress;
    private int externalDialogInProgress;
    private int hangupInProgress;
    private int saveContactInProgress;
    private bool suppressSearchEvents;
    private int gridDataErrorCount;
    public event Action<string>? OutgoingDialRequested;
    public event Action<string>? OutgoingDialFailed;
    // While the telephone is processing an answer command, suppress duplicate clicks.
    // Reset after the corresponding AMI state change, termination, or a bounded timeout.
    private string? pendingAnswerChannel;
    private DateTime pendingAnswerStartedUtc;
    private string lastAnswerAvailability = "";


    public PopupForm(AppConfig initial, Func<bool> openSettings)
    {
        config = initial; this.openSettings = openSettings;
        Text = "Medialink CTI Client";
        Font = new Font("Segoe UI", 9F);
        // One custom blue banner with logo and window controls: no duplicate Windows title bar.
        // The custom X calls Close(); OnFormClosing converts UserClosing to Hide-to-tray.
        FormBorderStyle = FormBorderStyle.None;
        ControlBox = false;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowIcon = false;
        ShowInTaskbar = false;  // Keep the single-instance client in Windows tray.
        BackColor = Color.FromArgb(209, 217, 226);
        Padding = new Padding(1);
        MinimumSize = new Size(570, 400);
        StartPosition = FormStartPosition.Manual;
        var saved = WindowStateStore.Load();
        hasSavedWindowBounds = saved.HasValue;
        if (saved is { } bounds) Bounds = bounds;
        else Size = new Size(770, 620);
        TopMost = config.KeepPopupOnTop;
        miniCall = new MiniCallForm(AnswerCurrentCallAsync, HangupCurrentCallAsync);
        miniCall.Dismissed += () => dismissedMiniChannel = currentLiveCall?.Channel;
        Build();
        ResizeEnd += (_, _) => PersistWindowBounds();
        VisibleChanged += (_, _) =>
        {
            if (!Visible && IsHandleCreated) PersistWindowBounds();
            UpdateMiniCall();
        };
        Resize += (_, _) => { UpdateChromeRestoreSymbol(); UpdateMiniCall(); };
        Deactivate += (_, _) =>
        {
            if (!config.KeepPopupOnTop && Visible && Volatile.Read(ref saveContactInProgress) == 0 &&
                Volatile.Read(ref externalDialogInProgress) == 0)
            {
                LastAutoHideTick = Environment.TickCount64;
                Hide();
            }
        };
        debounce.Tick += async (_, _) => { debounce.Stop(); await LoadContactsAsync(); };
        liveClock.Tick += (_, _) => RenderLiveCall();
        liveClock.Start();
        UpdateHeaderSubtitle();
        presenceTimer.Interval = PresenceRefreshIntervalMs(config.PresenceRefreshSeconds);
        presenceTimer.Tick += async (_, _) => await RefreshPresenceAsync();
        presenceTimer.Start();
        avayaBlfTimer.Tick += async (_, _) => await RefreshAvayaBlfAsync();
        avayaBlfTimer.Start();

    }

    protected override bool ShowWithoutActivation => false;

    // La X e Alt+F4 nascondono il pannello, ma non terminano l'applicazione
    // nell'area di notifica. Una finestra chiusa/disposta non puo essere
    // riaperta: il ciclo di vita dell'app e gestito da TrayApplicationContext.
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (TrayLifecycleRules.ShouldHidePanel(e.CloseReason))
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    // Small monochrome red trash can; avoid platform-dependent emoji rendering.
    private static Image CreateDeleteIcon()
    {
        var bitmap = new Bitmap(22, 22);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var pen = new Pen(Color.FromArgb(177, 39, 44), 1.8F);
        g.DrawLine(pen, 4, 6, 18, 6);
        g.DrawLine(pen, 8, 3, 14, 3);
        g.DrawLine(pen, 8, 3, 8, 6);
        g.DrawLine(pen, 14, 3, 14, 6);
        g.DrawLines(pen, [new Point(6, 8), new Point(7, 19),
            new Point(15, 19), new Point(16, 8)]);
        g.DrawLine(pen, 10, 10, 10, 16);
        g.DrawLine(pen, 13, 10, 13, 16);
        return bitmap;
    }

    private static Image CreateKeypadIcon()
    {
        var bitmap = new Bitmap(35, 31);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var fill = new SolidBrush(Color.FromArgb(17, 68, 127));
        // Three columns x four rows: recognizable keypad independent of fonts.
        for (int row = 0; row < 4; row++)
            for (int col = 0; col < 3; col++)
                g.FillEllipse(fill, 7 + col * 9, 2 + row * 7, 6, 6);
        return bitmap;
    }

    private static Image LoadIcon(string name)
    {
        var assembly = typeof(PopupForm).Assembly;
        using var stream = assembly.GetManifestResourceStream("MedialinkCTIClient.Assets." + name)
            ?? throw new InvalidOperationException("Risorsa grafica non disponibile: " + name);
        using var raw = Image.FromStream(stream);
        return new Bitmap(raw);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (IsHandleCreated) PersistWindowBounds();
            debounce.Stop(); liveClock.Stop(); presenceTimer.Stop(); presenceTimer.Dispose();
            activeSearch?.Cancel();
            avayaBlfTimer.Dispose();
            miniCall.Dispose();
            debounce.Dispose(); liveClock.Dispose(); controlHelp.Dispose(); dtmfQueue.Dispose();
            plusFont.Dispose(); editFont.Dispose();
        }
        base.Dispose(disposing);
        if (disposing)
        {
            callBlue.Dispose(); outgoingIcon.Dispose(); incomingIcon.Dispose(); missedIcon.Dispose();
            deleteIcon.Dispose(); keypadIcon.Dispose(); headerLogoImage.Dispose(); ownPresence.BackgroundImage?.Dispose();
        }
    }

    // A borderless but resizable form. Hit testing preserves the normal Windows
    // drag-to-resize behavior even though the visible caption is fully custom.
    private const int WmNcHitTest = 0x0084, WmNcLButtonDown = 0x00A1;
    private const int HtClient = 1, HtCaption = 2, HtLeft = 10, HtRight = 11,
        HtTop = 12, HtTopLeft = 13, HtTopRight = 14,
        HtBottom = 15, HtBottomLeft = 16, HtBottomRight = 17;

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendChromeMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmNcHitTest && WindowState == FormWindowState.Normal && IsHandleCreated)
        {
            base.WndProc(ref m);
            if (m.Result != (nint)HtClient) return;
            Point p = PointToClient(Cursor.Position);
            int grip = Math.Max(7, DeviceDpi / 12);
            bool left = p.X >= 0 && p.X < grip, right = p.X >= ClientSize.Width - grip;
            bool top = p.Y >= 0 && p.Y < grip, bottom = p.Y >= ClientSize.Height - grip;
            int hit = top && left ? HtTopLeft : top && right ? HtTopRight :
                bottom && left ? HtBottomLeft : bottom && right ? HtBottomRight :
                left ? HtLeft : right ? HtRight : top ? HtTop : bottom ? HtBottom : HtClient;
            if (hit != HtClient) m.Result = (nint)hit;
            return;
        }
        base.WndProc(ref m);
    }

    private void BeginCaptionDrag(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || WindowState == FormWindowState.Maximized) return;
        ReleaseCapture();
        SendChromeMessage(Handle, WmNcLButtonDown, (nint)HtCaption, 0);
    }

    private void ToggleMaximized()
    {
        if (WindowState == FormWindowState.Maximized)
            WindowState = FormWindowState.Normal;
        else
        {
            PersistWindowBounds();
            MaximizedBounds = Screen.FromControl(this).WorkingArea;
            WindowState = FormWindowState.Maximized;
        }
        UpdateChromeRestoreSymbol();
    }

    private void UpdateChromeRestoreSymbol()
    {
        chromeRestore.Text = WindowState == FormWindowState.Maximized ? "❐" : "□";
        controlHelp.SetToolTip(chromeRestore,
            WindowState == FormWindowState.Maximized ? "Ripristina finestra" : "Ingrandisci finestra");
    }

    private void BuildCustomChrome(Panel header, Label subtitle)
    {
        static void Style(Button button, string text, Color hover)
        {
            button.Text = text;
            button.Size = new Size(40, 32);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = hover;
            button.BackColor = Color.FromArgb(21, 53, 84);
            button.ForeColor = Color.White;
            button.Font = new Font("Segoe UI Symbol", 12F, FontStyle.Regular);
            button.TabStop = false;
            button.Cursor = Cursors.Hand;
        }
        Style(chromeRestore, "□", Color.FromArgb(45, 79, 112));
        Style(chromeClose, "×", Color.FromArgb(179, 52, 65));
        controlHelp.SetToolTip(chromeClose, "Nascondi pannello (non chiude il client)");
        chromeRestore.Click += (_, _) => ToggleMaximized();
        chromeClose.Click += (_, _) => Close();
        var buttons = new Panel { Dock = DockStyle.Right, Width = 84, Height = 34,
            BackColor = Color.FromArgb(21, 53, 84) };
        chromeRestore.Location = new Point(1, 0);
        chromeClose.Location = new Point(43, 0);
        buttons.Controls.Add(chromeRestore);
        buttons.Controls.Add(chromeClose);
        header.Controls.Add(buttons);
        // Avatar presence, visibile in alto a destra prima dei controlli finestra.
        ownPresence.Size = new Size(38,38);
        ownPresence.Location = new Point(0,13);
        ownPresence.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        ownPresence.FlatStyle = FlatStyle.Flat;
        ownPresence.FlatAppearance.BorderSize = 2;
        ownPresence.BackColor = Color.FromArgb(95,107,125);
        ownPresence.ForeColor = Color.White;
        ownPresence.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        ownPresence.Text = "?";
        ownPresence.Cursor = Cursors.Hand;
        controlHelp.SetToolTip(ownPresence,"Imposta la propria Presence");
        var presenceMenu = new ContextMenuStrip();
        foreach (var state in new[] {PresenceState.Available,PresenceState.Away,
            PresenceState.Chat,PresenceState.Dnd,PresenceState.Unavailable})
        {
            var item = presenceMenu.Items.Add(PresenceRules.Label(state));
            // Indicatore colore associato allo stato, indipendente dal tema di Windows.
            item.Image = PresenceRules.DotImage(state);
            item.Click += async (_, _) => await SetOwnPresenceAsync(state);
        }
        presenceMenu.Items.Add(new ToolStripSeparator());
        presenceMenu.Items.Add("Scegli immagine profilo...").Click += (_, _) => ChoosePresenceAvatar();
        ownPresence.Click += (_, _) => presenceMenu.Show(ownPresence, new Point(0,ownPresence.Height));
        // Stato sempre leggibile anche quando l'avatar mostra una fotografia.
        // Il badge e' affiancato alla foto, sotto i pulsanti finestra (inclusa X).
        ownPresenceBadge.Size = new Size(116, 23);
        ownPresenceBadge.Location = new Point(0, 37);
        ownPresenceBadge.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        ownPresenceBadge.FlatStyle = FlatStyle.Flat;
        ownPresenceBadge.FlatAppearance.BorderSize = 0;
        ownPresenceBadge.ForeColor = Color.White;
        ownPresenceBadge.Font = new Font("Segoe UI", 8F, FontStyle.Bold);
        ownPresenceBadge.Cursor = Cursors.Hand;
        ownPresenceBadge.TabStop = false;
        ownPresenceBadge.Click += (_, _) =>
            presenceMenu.Show(ownPresenceBadge, new Point(0,ownPresenceBadge.Height));
        controlHelp.SetToolTip(ownPresenceBadge,"Cambia la propria Presence");
        void MakeRoundAvatar()
        {
            using var path = new System.Drawing.Drawing2D.GraphicsPath();
            path.AddEllipse(0,0,ownPresence.Width-1,ownPresence.Height-1);
            ownPresence.Region?.Dispose();
            ownPresence.Region = new Region(path);
        }
        ownPresence.Resize += (_, _) => MakeRoundAvatar();
        MakeRoundAvatar();
        RefreshOwnPresenceAvatar();
        header.Controls.Add(ownPresence);
        header.Controls.Add(ownPresenceBadge);
        header.Resize += (_, _) =>
        {
            ownPresence.Left = Math.Max(120, header.ClientSize.Width - 174);
            ownPresenceBadge.Left = Math.Max(164, header.ClientSize.Width - 126);
        };
        ownPresence.Left = Math.Max(120, header.ClientSize.Width - 174);
        ownPresenceBadge.Left = Math.Max(164, header.ClientSize.Width - 126);
        ownPresence.BringToFront();
        ownPresenceBadge.BringToFront();

        title.Anchor = AnchorStyles.Left | AnchorStyles.Top;
        subtitle.Anchor = AnchorStyles.Left | AnchorStyles.Top;
        header.Resize += (_, _) =>
        {
            int available = Math.Max(100, header.ClientSize.Width - title.Left - 135);
            title.Width = available;
            subtitle.Width = available;
        };
        header.MouseDown += BeginCaptionDrag;
        title.MouseDown += BeginCaptionDrag;
        subtitle.MouseDown += BeginCaptionDrag;
        headerLogo.MouseDown += BeginCaptionDrag;
        header.DoubleClick += (_, _) => ToggleMaximized();
        title.DoubleClick += (_, _) => ToggleMaximized();
        subtitle.DoubleClick += (_, _) => ToggleMaximized();
        UpdateChromeRestoreSymbol();
    }

    private void Build()
    {
        var surface = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
        Controls.Add(surface);

        var header = new Panel { Dock = DockStyle.Top, Height = 65, BackColor = Color.FromArgb(21, 53, 84) };
        title.Text = "Medialink CTI Client";
        title.Font = new Font("Segoe UI Semibold", 13F, FontStyle.Bold);
        title.ForeColor = Color.White;
        // The existing 1024 px application logo is rendered larger in the banner.
        headerLogo.Image = headerLogoImage;
        headerLogo.SizeMode = PictureBoxSizeMode.Zoom;
        headerLogo.Location = new Point(10, 7);
        headerLogo.Size = new Size(50, 50);
        headerLogo.BackColor = Color.Transparent;
        headerLogo.TabStop = false;
        title.Location = new Point(71, 10); title.Size = new Size(350, 28);
        title.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        var sub = new Label { Text = "Interno " + config.Extension + "  ·  Grandstream UCM", Location = new Point(72, 39),
            ForeColor = Color.FromArgb(192, 213, 231), Width = 350, Height = 18,
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
        header.Controls.Add(headerLogo);
        header.Controls.Add(title); header.Controls.Add(sub);
        header.Tag = sub;
        chromeHeader = header;
        BuildCustomChrome(header, sub);

        CreateCallBar();
        var top = new Panel { Dock = DockStyle.Top, Height = 51, BackColor = Color.White };
        StyleTab(bookTab, "Rubrica", true); bookTab.Location = new Point(13, 11); bookTab.Width = 120;
        StyleTab(callsTab, "Registro", false); callsTab.Location = new Point(137, 11); callsTab.Width = 120;
        bookTab.Click += async (_, _) => await SwitchTabAsync(false);
        callsTab.Click += async (_, _) => await SwitchTabAsync(true);
        top.Controls.Add(bookTab); top.Controls.Add(callsTab);

        callFilters.Dock = DockStyle.Top;
        callFilters.Height = 42;
        callFilters.Visible = false;
        callFilters.BackColor = Color.White;
        string[] labels = ["Tutte", "Uscenti", "Entranti", "Perse"];
        for (int i = 0; i < labels.Length; i++)
        {
            var index = i;
            var button = new Button
            {
                Text = labels[i],
                Size = new Size(97, 29),
                Location = new Point(14 + i * 103, 5),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8.4F)
            };
            button.FlatAppearance.BorderSize = 0;
            button.Click += (_, _) =>
            {
                selectedCallFilter = (CallFilter)index;
                RefreshCallFilterStyles();
                ShowCallRows();
            };
            callFilterButtons[i] = button;
            callFilters.Controls.Add(button);
        }
        RefreshCallFilterStyles();

        var filter = new Panel { Dock = DockStyle.Top, Height = 53, Padding = new Padding(15, 8, 14, 7) };
        refresh.Text = "↻"; refresh.Font = new Font("Segoe UI", 13F); refresh.Width = 39;
        refresh.Height = 32; refresh.Dock = DockStyle.Right;
        refresh.FlatStyle = FlatStyle.Flat; refresh.FlatAppearance.BorderColor = Color.FromArgb(215, 225, 235);
        refresh.BackColor = Color.FromArgb(246, 249, 252);
        refresh.Click += async (_, _) => { if (showCalls) await LoadCdrAsync(); else await LoadContactsAsync(); };
        search.PlaceholderText = "Cerca nome o numero in LDAP...";
        search.Dock = DockStyle.Fill;
        search.BorderStyle = BorderStyle.FixedSingle;
        search.Font = new Font("Segoe UI", 10);
        search.TextChanged += (_, _) => { if (suppressSearchEvents) return; if (!showCalls) { debounce.Stop(); debounce.Start(); } else ShowCallRows(); };
        var spacer = new Panel { Dock = DockStyle.Right, Width = 8 };
        filter.Controls.Add(search); filter.Controls.Add(spacer); filter.Controls.Add(refresh);
        searchFilterPanel = filter;
        // Permanent BLF sidebar: always visible alongside the central Rubrica/Registro views.
        blfSidebar.Dock = DockStyle.Right;
        blfSidebar.Width = 160; // FIX10: BLF compatto, pulsanti e titolo larghi 120 px
        blfSidebar.BackColor = Color.FromArgb(239, 245, 249);
        blfSidebar.Padding = new Padding(5, 0, 5, 5);
        // A.6.10: ricerca indipendente nella colonna BLF, compatibile con .NET 4.8.
        var blfHeader = new Panel { Dock = DockStyle.Top, Height = 105,
            BackColor = blfSidebar.BackColor };
        blfSidebarTitle.AutoSize = false;
        blfSidebarTitle.Dock = DockStyle.None;
        blfSidebarTitle.Location = new Point(5, 11);
        blfSidebarTitle.Size = new Size(120, 34);
        blfSidebarTitle.Text = "BLF";
        blfSidebarTitle.TextAlign = ContentAlignment.MiddleCenter;
        blfSidebarTitle.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        blfSidebarTitle.ForeColor = Color.White;
        blfSidebarTitle.BackColor = Color.FromArgb(28, 83, 132);
        blfSidebarTitle.BorderStyle = BorderStyle.FixedSingle;
        blfHeader.Controls.Add(blfSidebarTitle);
        blfSearchCaption.AutoSize = false;
        blfSearchCaption.Text = "Filtro";
        blfSearchCaption.ForeColor = Color.FromArgb(61, 83, 107);
        blfSearchCaption.Font = new Font("Segoe UI", 7.5f);
        blfSearchCaption.Location = new Point(5, 49);
        blfSearchCaption.Size = new Size(120, 17);
        blfHeader.Controls.Add(blfSearchCaption);
        blfSearch.Location = new Point(5, 68);
        blfSearch.Size = new Size(120, 23);
        blfSearch.Font = new Font("Segoe UI", 8.5f);
        blfSearch.BorderStyle = BorderStyle.FixedSingle;
        blfSearch.AccessibleName = "Filtro BLF per interno o nome";
        controlHelp.SetToolTip(blfSearch, "Filtra i BLF per interno o nome. Cancella per vederli tutti.");
        blfSearch.TextChanged += (_, _) => RenderBlf();
        blfHeader.Controls.Add(blfSearch);
        blfPanel.Dock = DockStyle.Fill;
        blfPanel.AutoScroll = true;
        blfPanel.FlowDirection = FlowDirection.TopDown;
        blfPanel.WrapContents = false;
        blfPanel.Padding = new Padding(5, 3, 0, 3); // FIX10: allinea pulsanti al titolo BLF
        blfPanel.BackColor = blfSidebar.BackColor;
        // FIX9: nessun contatore BLF nel pannello.
        blfSidebar.Controls.Add(blfPanel);
        blfSidebar.Controls.Add(blfHeader);
        blfPanel.Resize += (_, _) => ResizeBlfButtons();

        grid.Dock = DockStyle.Fill;
        grid.BackgroundColor = Color.White;
        grid.BorderStyle = BorderStyle.None;
        grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false;
        grid.AllowUserToResizeRows = false;
        grid.RowHeadersVisible = false;
        grid.MultiSelect = false;
        grid.ReadOnly = true;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        grid.ColumnHeadersHeight = 31; grid.RowTemplate.Height = 42;
        grid.GridColor = Color.FromArgb(238, 243, 247);
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(242, 246, 250);
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(59, 77, 97);
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(242, 246, 250);
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(224, 238, 252);
        grid.DefaultCellStyle.SelectionForeColor = Color.FromArgb(27, 56, 78);
        // Only the phonebook's Contatto and Numero columns are sortable.
        // Sorting the local result preserves the selected direction after a LDAP refresh;
        // other columns are commands and must not respond to header clicks.
        grid.ColumnHeaderMouseClick += (_, e) =>
        {
            if (showCalls || e.ColumnIndex < 0) return;
            string key = grid.Columns[e.ColumnIndex].Name;
            if (key != "name" && key != "phone") return;
            if (contactSortColumn == key) contactSortAscending = !contactSortAscending;
            else { contactSortColumn = key; contactSortAscending = true; }
            RenderContactRows();
        };
        // Never show the default DataGridView exception dialog to a customer.
        // A bad formatted cell is logged; rendering is retried only with a valid schema.
        grid.DataError += (_, e) =>
        {
            gridDataErrorCount++;
            DiagnosticLog.Error("UI_GRID_DATA_ERROR", e.Exception ??
                new InvalidOperationException($"Colonna {e.ColumnIndex}, riga {e.RowIndex}, contesto {e.Context}"));
            e.ThrowException = false;
            e.Cancel = true;
        };
        grid.CellContentClick += async (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            var column = grid.Columns[e.ColumnIndex].Name;
            var item = grid.Rows[e.RowIndex].Tag;
            if (column == "saveContact" && config.EnableLdapContactEditing &&
                grid.Rows[e.RowIndex].Cells[e.ColumnIndex] is DataGridViewButtonCell &&
                item is CallEntry cdrCall && IsExternal(cdrCall.Peer))
            {
                // The cache can outlive a change made directly in the UCM GUI.
                // On every click verify the current number in the configured
                // phonebook BEFORE enabling any write or opening its editor.
                string key = PhoneNumbers.SearchKey(cdrCall.Peer);
                LdapContactMatch match;
                try
                {
                    status.Text = "Verifica del numero nella rubrica di destinazione...";
                    var verified = await ldap.LookupForNumbersAsync(config, [cdrCall.Peer]);
                    if (!verified.TryGetValue(key, out var candidate) ||
                        LdapDestinationRules.Action(candidate, config.LdapWritePhonebookDn) == CdrLdapAction.Verify)
                        throw new InvalidOperationException("La UCM non ha confermato l'esito della ricerca mirata.");
                    match = candidate;
                    cdrDirectory[key] = match;
                    unverifiedLdapKeys.Remove(key);
                    ShowCallRows();
                }
                catch (Exception error)
                {
                    cdrDirectory.Remove(key);
                    unverifiedLdapKeys.Add(key);
                    ShowCallRows();
                    DiagnosticLog.Error("CDR_LDAP_CONTACT_VERIFY_FAILED", error);
                    status.Text = "Rubrica non verificabile: modifica e aggiunta bloccate.";
                    MessageBox.Show(this,
                        "Non è possibile verificare il contatto nella rubrica configurata. " +
                        "Nessuna modifica effettuata. " + error.Message,
                        "Verifica contatto LDAP", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                var action = LdapDestinationRules.Action(match, config.LdapWritePhonebookDn);
                if (action == CdrLdapAction.Edit && match.DestinationContact is { } editable)
                {
                    if (match.DestinationAmbiguous && MessageBox.Show(this,
                        $"Sono presenti più contatti nella rubrica per {cdrCall.Peer}. " +
                        $"Verrà aperto '{editable.DisplayName}' ({editable.AccountNumber}). Continuare?",
                        "Contatti multipli", MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning) != DialogResult.Yes) return;
                    await EditContactAsync(editable);
                }
                else if (action == CdrLdapAction.Add)
                    await SaveCdrContactAsync(cdrCall);
                else if (action == CdrLdapAction.ExistingUneditable)
                    MessageBox.Show(this,
                        "Contatto presente nella rubrica di destinazione, ma senza un " +
                        "identificativo valido per modificarlo tramite API UCM.",
                        "Contatto non modificabile", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (column == "deleteContact" && config.EnableLdapContactEditing &&
                grid.Rows[e.RowIndex].Cells[e.ColumnIndex] is DataGridViewImageCell &&
                item is Contact contactToDelete && contactToDelete.PhonebookDn.Length > 0 &&
                contactToDelete.AccountNumber.Length > 0)
            {
                await DeleteContactAsync(new UcmContactDetails {
                    PhonebookDn = contactToDelete.PhonebookDn,
                    AccountNumber = contactToDelete.AccountNumber });
                return;
            }
            if (column == "editContact" && config.EnableLdapContactEditing &&
                item is Contact selectedContact &&
                selectedContact.PhonebookDn.Length > 0 && selectedContact.AccountNumber.Length > 0 &&
                !selectedContact.PhonebookDn.Equals("ou=pbx,dc=pbx,dc=com", StringComparison.OrdinalIgnoreCase))
            {
                await EditContactAsync(new UcmContactDetails { PhonebookDn = selectedContact.PhonebookDn,
                    AccountNumber = selectedContact.AccountNumber });
                return;
            }
            if (column != "call") return;
            if (item is CallEntry selectedCall)
                await DialAsync(selectedCall.Peer);
            else if (item is Contact selectedContactToCall)
                await DialAsync(selectedContactToCall.Number);
        };
        grid.CellMouseEnter += (_, e) =>
        {
            if (e.ColumnIndex >= 0 && (grid.Columns[e.ColumnIndex].Name == "call" ||
                grid.Columns[e.ColumnIndex].Name == "saveContact" ||
                grid.Columns[e.ColumnIndex].Name == "editContact" ||
                grid.Columns[e.ColumnIndex].Name == "deleteContact")) grid.Cursor = Cursors.Hand;
        };
        grid.CellMouseLeave += (_, _) => grid.Cursor = Cursors.Default;

        // Footer left: filtered row count, then existing operational diagnostics.
        // Application Info remains available exclusively from the tray menu Informazioni.
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 29,
            BackColor = Color.FromArgb(248, 250, 253) };
        recordCount.Location = new Point(12, 4);
        recordCount.Size = new Size(160, 21);
        recordCount.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
        recordCount.ForeColor = Color.FromArgb(38, 70, 104);
        recordCount.Text = "Rubrica: 0 record";
        status.Location = new Point(178, 4); status.AutoEllipsis = true;
        status.Height = 21;
        status.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        status.ForeColor = Color.FromArgb(75, 89, 107);
        status.Text = "Pronto";
        bottom.Resize += (_, _) => status.Width = Math.Max(30, bottom.ClientSize.Width - 190);
        status.Width = Math.Max(30, bottom.ClientSize.Width - 190);
        bottom.Controls.Add(recordCount);
        bottom.Controls.Add(status);

        // WinForms Dock=Top controls render in reverse insertion order.
        gridHost.Dock = DockStyle.Fill;
        gridHost.BackColor = Color.White;
        gridHost.Controls.Add(grid);
        surface.Controls.Add(gridHost);
        surface.Controls.Add(filter);
        surface.Controls.Add(callFilters);
        surface.Controls.Add(top);
        surface.Controls.Add(keypadPanel);
        surface.Controls.Add(callBar);
        surface.Controls.Add(blfSidebar);
        surface.Controls.Add(header);
        surface.Controls.Add(bottom);
        SetupGrid(false);
        StyleTab(bookTab, "Rubrica", true);
        grid.Visible = true;
        RenderBlf();
        Shown += async (_, _) => await LoadContactsAsync();
        Shown += async (_, _) => await RefreshPresenceAsync();
        Shown += async (_, _) => await RefreshAvayaBlfAsync();
    }

    public void ResetBlf()
    {
        blfStates.Clear();
        RenderBlf();
    }

    public void UpdateBlf(string extension, BlfState state)
    {
        if (config.PhoneType == "Avaya" ||
            !BlfLogic.Parse(config.BlfExtensions, config.Extension).Any(e => e.Extension == extension)) return;
        blfStates[extension] = state;
        RenderBlf();
    }

    private static int PresenceRefreshIntervalMs(int seconds) =>
        (seconds is >= 5 and <= 300 ? seconds : 15) * 1000;

    private async Task RefreshPresenceAsync()
    {
        if (config.PhoneType == "Avaya") return;

        DiagnosticLog.Info("PRESENCE_ENTER",
            "busy="+presenceBusy+
            "; disposed="+IsDisposed+
            "; handle_ready="+IsHandleCreated+
            "; api_user_set="+(!string.IsNullOrWhiteSpace(config.ApiUser))+
            "; api_secret_set="+(!string.IsNullOrEmpty(config.ApiSecret)));

        if (presenceBusy || IsDisposed || !IsHandleCreated)
        {
            DiagnosticLog.Info("PRESENCE_SKIPPED", "reason=FORM_NOT_READY_OR_BUSY");
            return;
        }

        if (string.IsNullOrEmpty(config.ApiSecret) ||
            string.IsNullOrWhiteSpace(config.ApiUser))
        {
            DiagnosticLog.Info("PRESENCE_SKIPPED", "reason=API_CREDENTIALS_MISSING");
            return;
        }
        presenceBusy=true;
        try
        {
            var extensions=BlfLogic.Parse(config.BlfExtensions,config.Extension)
                .Select(e=>e.Extension).Concat(new[]{config.Extension})
                .Distinct(StringComparer.Ordinal).Take(25).ToArray();
            var result=await presenceApi.ReadAsync(config,extensions,CancellationToken.None);
            if (IsDisposed) return;
            presenceStates.Clear();
            foreach(var pair in result) presenceStates[pair.Key]=pair.Value;
            DiagnosticLog.Info("PRESENCE_READ", "requested="+extensions.Length+
                "; returned="+result.Count+"; recognized="+result.Values.Count(v=>v!=PresenceState.Unknown));
            // Diagnostica aggregata: non registrare interni, nominativi, cookie o credenziali.
            var monitoredExtensions = BlfLogic.Parse(config.BlfExtensions, config.Extension)
                .Select(e => e.Extension).Distinct(StringComparer.Ordinal).ToArray();
            var matched = monitoredExtensions.Count(ext => result.ContainsKey(ext));
            DiagnosticLog.Info("PRESENCE_STATES",
                "available="+result.Values.Count(v=>v==PresenceState.Available)+
                "; away="+result.Values.Count(v=>v==PresenceState.Away)+
                "; chat="+result.Values.Count(v=>v==PresenceState.Chat)+
                "; dnd="+result.Values.Count(v=>v==PresenceState.Dnd)+
                "; unavailable="+result.Values.Count(v=>v==PresenceState.Unavailable)+
                "; unknown="+result.Values.Count(v=>v==PresenceState.Unknown)+
                "; blf_configured="+monitoredExtensions.Length+
                "; blf_matched="+matched+
                "; blf_non_available="+monitoredExtensions.Count(ext =>
                    result.TryGetValue(ext, out var state) &&
                    state!=PresenceState.Available && state!=PresenceState.Unknown));
            ownPresenceState=presenceStates.TryGetValue(config.Extension,out var own) ? own : PresenceState.Unknown;
            RefreshOwnPresenceAvatar();
            RenderBlf();
        }
        catch(Exception ex)
        {
            presenceStates.Clear(); ownPresenceState=PresenceState.Unknown;
            RefreshOwnPresenceAvatar(); RenderBlf();
            DiagnosticLog.Error("PRESENCE_READ_FAILED",ex);
        }
        finally { presenceBusy=false; }
    }

    private async Task SetOwnPresenceAsync(PresenceState desired)
    {
        if (presenceBusy) { MessageBox.Show(this,"Presence: operazione già in corso."); return; }
        presenceBusy=true;
        ownPresence.Enabled=false;
        try
        {
            var result=await presenceApi.SetOwnAsync(config,desired,CancellationToken.None);
            if (!result.Applied)
            {
                MessageBox.Show(this,
                    "La modifica SIP Presence non risulta ancora attiva sulla UCM. " +
                    "Non è stato eseguito applyChanges generale. " +
                    "Verifica eventuali modifiche pendenti sul centralino.",
                    "Presence in attesa",MessageBoxButtons.OK,MessageBoxIcon.Warning);
                return;
            }
            ownPresenceState=desired;
            presenceStates[config.Extension]=desired;
            RefreshOwnPresenceAvatar();
            RenderBlf();
        }
        catch(Exception ex)
        {
            DiagnosticLog.Error("PRESENCE_SET_FAILED",ex);
            MessageBox.Show(this,"Impossibile confermare la Presence sulla UCM. " +
                "Nessuna modifica locale dello stato è stata applicata.",
                "Presence",MessageBoxButtons.OK,MessageBoxIcon.Warning);
        }
        finally { presenceBusy=false; ownPresence.Enabled=true; }
    }

    // U15: avatar persistente nella configurazione utente.
    // Nessuna immagine viene inviata al centralino o al server licenze.
    private static string SavePresenceAvatarCopy(string inputPath)
    {
        string folder = System.IO.Path.Combine(
            ConfigStore.DirectoryPath, "Profile");

        System.IO.Directory.CreateDirectory(folder);

        string destination = System.IO.Path.Combine(folder, "avatar.png");

        string temporary = System.IO.Path.Combine(
            folder, "avatar-" + Guid.NewGuid().ToString("N") + ".png");

        try
        {
            using (var stream = System.IO.File.OpenRead(inputPath))
            using (var source = Image.FromStream(stream))
            {
                if (source.Width > 4096 || source.Height > 4096)
                    throw new InvalidOperationException("Immagine troppo grande");

                using var copy = new Bitmap(source);

                copy.Save(
                    temporary,
                    System.Drawing.Imaging.ImageFormat.Png);
            }

            System.IO.File.Copy(temporary, destination, true);

            return destination;
        }
        finally
        {
            if (System.IO.File.Exists(temporary))
                System.IO.File.Delete(temporary);
        }
    }
    private void ChoosePresenceAvatar()
    {
        using var chooser = new OpenFileDialog
        {
            Title="Immagine profilo locale BLF",
            Filter="Immagini|*.png;*.jpg;*.jpeg;*.bmp",
            CheckFileExists=true
        };
        if(chooser.ShowDialog(this)!=DialogResult.OK) return;
        try
        {
            // Il file resta in locale: nessun caricamento foto sul centralino o server licenze.
            using var stream=System.IO.File.OpenRead(chooser.FileName);
            using var source=Image.FromStream(stream);
            if(source.Width>4096 || source.Height>4096) throw new InvalidOperationException("Immagine troppo grande");
            var preview=new Bitmap(source);
            var old=ownPresence.BackgroundImage;
            ownPresence.BackgroundImage=preview;
            ownPresence.BackgroundImageLayout=ImageLayout.Zoom;
            old?.Dispose();
            config.PresenceAvatarPath = SavePresenceAvatarCopy(chooser.FileName);
            ConfigStore.Save(config);
            RefreshOwnPresenceAvatar();
        }
        catch(Exception ex)
        {
            DiagnosticLog.Error("PRESENCE_AVATAR_FAILED",ex);
            MessageBox.Show(this,"Impossibile salvare l'immagine profilo.","Avatar",MessageBoxButtons.OK,MessageBoxIcon.Warning);
        }
    }

    private void RefreshOwnPresenceAvatar()
    {
        // Migra il percorso precedente se la foto originale e ancora presente.
        // Se la copia persistente esiste, recuperala anche quando il
        // precedente percorso dell'immagine non e piu disponibile.
        try
        {
            string stored = System.IO.Path.Combine(
                ConfigStore.DirectoryPath, "Profile", "avatar.png");

            if (!string.IsNullOrWhiteSpace(config.PresenceAvatarPath))
            {
                string current = config.PresenceAvatarPath;

                if (!string.Equals(
                    System.IO.Path.GetFullPath(current),
                    System.IO.Path.GetFullPath(stored),
                    StringComparison.OrdinalIgnoreCase))
                {
                    if (System.IO.File.Exists(current))
                    {
                        config.PresenceAvatarPath = SavePresenceAvatarCopy(current);
                        ConfigStore.Save(config);
                    }
                    else if (System.IO.File.Exists(stored))
                    {
                        config.PresenceAvatarPath = stored;
                        ConfigStore.Save(config);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("PRESENCE_AVATAR_MIGRATION_FAILED", ex);
        }
        if (ownPresence.BackgroundImage==null && System.IO.File.Exists(config.PresenceAvatarPath))
        {
            try
            {
                using var stream=System.IO.File.OpenRead(config.PresenceAvatarPath);
                using var original=Image.FromStream(stream);
                ownPresence.BackgroundImage=new Bitmap(original);
                ownPresence.BackgroundImageLayout=ImageLayout.Zoom;
            }
            catch { /* avatar opzionale */ }
        }
        ownPresence.Text = ownPresence.BackgroundImage != null ? "" :
            string.IsNullOrWhiteSpace(config.ExtensionDisplayName) ?
            config.Extension : string.Concat(config.ExtensionDisplayName.Split(' ')
                .Where(part=>part.Length>0).Take(2).Select(part=>part.Substring(0,1))).ToUpperInvariant();
        // La fotografia mantiene uno sfondo neutro; il badge separato porta il colore.
        ownPresence.BackColor = Color.FromArgb(95,107,125);
        ownPresence.FlatAppearance.BorderColor = Color.FromArgb(208,219,231);
        ownPresenceBadge.BackColor = PresenceRules.Color(ownPresenceState);
        ownPresenceBadge.Text = PresenceRules.Label(ownPresenceState);
        controlHelp.SetToolTip(ownPresence,"Presence: "+PresenceRules.Label(ownPresenceState)+" — clic per cambiare");
        controlHelp.SetToolTip(ownPresenceBadge,"Presence: "+PresenceRules.Label(ownPresenceState)+" — clic per cambiare");
    }

    private void ResizeBlfButtons()
    {
        const int width = 120;
        foreach (Control control in blfPanel.Controls)
            if (control is Button button) button.Width = width;
    }

    // A.6.10: filtro solo visuale; nessuna modifica alle sottoscrizioni AMI / presenza.
    private async Task RefreshAvayaBlfAsync()
    {
        if (config.PhoneType != "Avaya" || !Visible || IsDisposed || avayaBlfBusy) return;
        var current = config;
        avayaBlfBusy = true;
        try
        {
            var rows = await AvayaGatewayClient.BlfAsync(current);
            if (IsDisposed || !ReferenceEquals(config, current)) return;
            if (!avayaBlfUnavailable && avayaBlf.SequenceEqual(rows)) return;
            avayaBlfUnavailable = false;
            avayaBlf = rows;
            RenderBlf();
        }
        catch (Exception e)
        {
            if (IsDisposed || !ReferenceEquals(config, current) || avayaBlfUnavailable) return;
            DiagnosticLog.Error("AVAYA_BLF_FAILED", e);
            avayaBlfUnavailable = true;
            avayaBlf = Array.Empty<AvayaBlfRow>();
            RenderBlf();
        }
        finally { avayaBlfBusy = false; }
    }

    private void RenderBlf()
    {
        if (IsDisposed) return;
        blfPanel.SuspendLayout();
        try
        {
            blfPanel.Controls.Clear();
            bool avaya = config.PhoneType == "Avaya";
            IReadOnlyList<BlfEntry> entries = avaya
                ? avayaBlf.Select(row => new BlfEntry(row.Extension, row.Name)).ToArray()
                : BlfLogic.Parse(config.BlfExtensions, config.Extension);
            if (entries.Count == 0)
            {
                blfPanel.Controls.Add(new Label { Width=120, Height=73,
                    Text=avaya
                        ? (avayaBlfUnavailable ? "BLF Avaya non disponibile." : "Nessun interno BLF Avaya disponibile.")
                        : "Nessun BLF configurato. Apri Impostazioni → BLF.",
                    Padding=new Padding(5,10,0,0), ForeColor=Color.DimGray });
                return;
            }
            string query = blfSearch.Text.Trim();
            if (query.Length != 0)
                entries = entries.Where(entry =>
                    entry.Extension.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    entry.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (entries.Count == 0)
            {
                blfPanel.Controls.Add(new Label { Width=120, Height=34,
                    Text="Nessun BLF trovato", Padding=new Padding(3,8,0,0),
                    ForeColor=Color.DimGray });
                return;
            }
            foreach (var entry in entries)
            {
                BlfState s = avaya
                    ? AvayaGatewayClient.MapStatus(avayaBlf.First(row => row.Extension == entry.Extension).Status)
                    : blfStates.TryGetValue(entry.Extension, out var state) ? state : BlfState.Unknown;
                var color = s switch {
                    BlfState.Free => Color.FromArgb(29,178,86), BlfState.Busy => Color.FromArgb(219,62,61),
                    BlfState.Ringing => Color.FromArgb(244,196,48), BlfState.Dnd => Color.FromArgb(149,85,206),
                    BlfState.Offline => Color.FromArgb(147,155,164), _ => Color.FromArgb(210,218,226) };
                PresenceState presence = presenceStates.TryGetValue(entry.Extension,out var pres) ? pres : PresenceState.Unknown;
                var sipLabel = s switch { BlfState.Free=>"Libero", BlfState.Busy=>"Occupato",
                    BlfState.Ringing=>"Squilla", BlfState.Dnd=>"DND", BlfState.Offline=>"Offline",
                    _=>"Sconosciuto" };
                var presenceLabel = presence == PresenceState.Unknown ? "n/d" : PresenceRules.Label(presence);
                var ext = entry.Extension;
                var title = ext + " - " + entry.Name;
                var foreground = (s==BlfState.Offline || s==BlfState.Unknown || s==BlfState.Ringing) ?
                    Color.FromArgb(25,31,39) : Color.White;
                var presenceColor = PresenceRules.Color(presence);
                var borderColor = s == BlfState.Offline ? Color.FromArgb(95,109,126) :
                    Color.FromArgb(28,83,132);
                var btn = new Button { Width=120, Height=47, Margin=new Padding(0, 1, 0, 1),
                    Text="", FlatStyle=FlatStyle.Flat, BackColor=blfSidebar.BackColor,
                    Font=new Font("Segoe UI",7.6f,FontStyle.Bold),
                    Enabled=avaya ? s==BlfState.Free && ext!=config.Extension :
                        s==BlfState.Free || s==BlfState.Busy || s==BlfState.Ringing || s==BlfState.Dnd };
                btn.FlatAppearance.BorderSize=0;
                btn.FlatAppearance.MouseOverBackColor=blfSidebar.BackColor;
                btn.FlatAppearance.MouseDownBackColor=blfSidebar.BackColor;
                controlHelp.SetToolTip(btn, title+"\n"+sipLabel+"\n"+presenceLabel);
                btn.Paint += (_, args) =>
                {
                    args.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    var rectangle = new Rectangle(1,1,btn.ClientSize.Width-3,btn.ClientSize.Height-3);
                    const int r = 8;
                    using var shape = new System.Drawing.Drawing2D.GraphicsPath();
                    shape.AddArc(rectangle.Left,rectangle.Top,r*2,r*2,180,90);
                    shape.AddArc(rectangle.Right-r*2,rectangle.Top,r*2,r*2,270,90);
                    shape.AddArc(rectangle.Right-r*2,rectangle.Bottom-r*2,r*2,r*2,0,90);
                    shape.AddArc(rectangle.Left,rectangle.Bottom-r*2,r*2,r*2,90,90);
                    shape.CloseFigure();
                    using (var fill = new SolidBrush(color)) args.Graphics.FillPath(fill,shape);
                    using var oldClip = args.Graphics.Clip;
                    using (var clip = new Region(shape))
                    {
                        args.Graphics.SetClip(clip,System.Drawing.Drawing2D.CombineMode.Replace);
                        using var stripe = new SolidBrush(presenceColor);
                        args.Graphics.FillRectangle(stripe, 1,1,21,btn.ClientSize.Height-2);
                    }
                    args.Graphics.Clip = oldClip;
                    using (var pen = new Pen(borderColor,1.5f)) args.Graphics.DrawPath(pen,shape);
                    var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
                    TextRenderer.DrawText(args.Graphics,title,btn.Font,new Rectangle(29,2,87,14),foreground,flags);
                    TextRenderer.DrawText(args.Graphics,sipLabel,btn.Font,new Rectangle(29,16,87,14),foreground,flags);
                    TextRenderer.DrawText(args.Graphics,presenceLabel,btn.Font,new Rectangle(29,30,87,14),foreground,flags);
                };
                btn.Click += async (_, _) =>
                {
                    if (avaya)
                    {
                        if (config.PhoneType == "Avaya" && s == BlfState.Free && ext != config.Extension)
                            await DialAsync(ext);
                        return;
                    }
                    var current = blfStates.TryGetValue(ext,out var live) ? live : BlfState.Unknown;
                    var number = BlfLogic.Target(current,ext,config.BlfPickupPrefix);
                    if(number==null) { status.Text="BLF: stato non disponibile per la composizione."; return; }
                    await DialAsync(number);
                };
                blfPanel.Controls.Add(btn);
            }
            ResizeBlfButtons();
        }
        finally { blfPanel.ResumeLayout(); }
    }

    // SEC.4.156 - Icona telefono azzurra approvata.
    private static Image LoadU15DialIcon()
    {
        const string encoded = "iVBORw0KGgoAAAANSUhEUgAAAB4AAAAcCAYAAAB2+A+pAAABy0lEQVR42u3WzYuNYRgG8N/x/ZWvYqFGURQLbJTyUVJKzYKNP0AWvoqtBiWzoZRiY4EtViQLjSRsJA1RLFAkMSKjRkejGZvr1NtpPo5z5m1m4amn637u533P9d7Pee7rvivOXjUeY5JxGv+JJwzxVrxDtTC/obNV4ikj7G3AvSH809GBhThQRsTng3dQKczt8e8v66hnB8/U+bsK9rwyiJ8Ej47wzI4yiK8H1w+xdyh4sgzi24XjnFW3dz+4tKx0ehssps8yvIx9ulniyihFYjMeFD5yMFPyewUGyoj4YYHoVLAnWG2WtFHJXBvswGKsyXo1TpRJ/ALvY7/GF+wq3OrDdc/PRdsoqthwkVieI1+AbtzAxeydw63Yd9GLD+jHL1zA1GaJB7Ay9jo8wj4ci68dP7Ct7r2ZOIi+Vsrim4JOb8SzpNOWOvlsj6bPz8d9TsRHWqnHXalatUv3O9FMjtJtKghPb/6OV1nPaLQsDjceYxp+5see4hNWxVdqB9IfGb2S9ZJE2IPdw9T8P2PV+gxiT3K7VioX4Vr2qnge9YPLY91zfc2lq+A4PhY6lZrYXML3Mpu9zohHBXOwM/dg77/0XK2OPtyccO3tXzhLWTaynhA1AAAAAElFTkSuQmCC";
        byte[] bytes = Convert.FromBase64String(encoded);
        using (var stream = new System.IO.MemoryStream(bytes))
        using (var source = Image.FromStream(stream))
        {
            return new Bitmap(source);
        }
    }
    private void CreateCallBar()
    {
        callBar.Dock = DockStyle.Top;
        callBar.Height = 105;
        callBar.BackColor = Color.FromArgb(237, 247, 251);
        liveIcon.SizeMode = PictureBoxSizeMode.Zoom;
        liveIcon.Location = new Point(13, 16);
        liveIcon.Size = new Size(27, 27);
        liveInfo.Location = new Point(49, 8);
        liveInfo.Height = 23; // Numero: sempre una sola riga.
        liveInfo.AutoEllipsis = true;
        liveInfo.TextAlign = ContentAlignment.MiddleLeft;
        liveInfo.Font = new Font("Segoe UI", 12.4F, FontStyle.Bold);
        liveInfo.ForeColor = Color.Black;
        liveName.Location = new Point(49, 30);
        liveName.Height = 23; // Nome LDAP: sempre una sola riga.
        liveName.AutoEllipsis = true;
        liveName.TextAlign = ContentAlignment.MiddleLeft;
        liveName.Font = new Font("Segoe UI", 11.4F, FontStyle.Bold);
        liveName.ForeColor = Color.FromArgb(16, 82, 178);
        liveDuration.Font = new Font("Segoe UI Semibold", 9.3F, FontStyle.Bold);
        liveDuration.ForeColor = Color.FromArgb(0, 117, 148);
        liveDuration.TextAlign = ContentAlignment.MiddleCenter;
        liveDuration.Height = 36;
        answerCall.Text = "Rispondi";
        answerCall.Size = new Size(80, 31);
        answerCall.FlatStyle = FlatStyle.Flat;
        answerCall.BackColor = Color.FromArgb(222, 247, 238);
        answerCall.ForeColor = Color.FromArgb(24, 120, 88);
        answerCall.FlatAppearance.BorderSize = 0;
        answerCall.Click += async (_, _) => await AnswerCurrentCallAsync();
        controlHelp.SetToolTip(answerCall,
            "Risponde alla chiamata in squillo tramite controllo CTI del telefono fisico.");
        endCall.Text = "Termina";
        endCall.Size = new Size(76, 31);
        endCall.FlatStyle = FlatStyle.Flat;
        endCall.BackColor = Color.FromArgb(252, 232, 233);
        endCall.ForeColor = Color.FromArgb(165, 44, 56);
        endCall.FlatAppearance.BorderSize = 0;
        endCall.Click += async (_, _) => await HangupCurrentCallAsync();
        controlHelp.SetToolTip(endCall,
            "Termina esclusivamente il canale AMI dell'interno configurato, se univoco.");

        var dialRow = new Panel { Dock = DockStyle.Bottom, Height = 42, Padding = new Padding(11, 3, 11, 5), BackColor = callBar.BackColor };
        dialNumber.Dock = DockStyle.Fill;
        dialNumber.Font = new Font("Segoe UI", 10F);
        dialNumber.PlaceholderText = "Numero da chiamare...";
        dialNumber.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await DialFromBarAsync(); }
        };
        dialFromBar.Text = "";
        dialFromBar.Image = new Bitmap(callBlue, new Size(18, 18));
        dialFromBar.ImageAlign = ContentAlignment.MiddleCenter; 
        dialFromBar.Font = new Font("Segoe UI Symbol", 21, FontStyle.Regular);
        dialFromBar.AccessibleName = "Chiama";
        dialFromBar.TextAlign = ContentAlignment.MiddleCenter;
        dialFromBar.Width = 28;
        dialFromBar.Dock = DockStyle.Right;
        dialFromBar.FlatStyle = FlatStyle.Flat;
        dialFromBar.BackColor = dialRow.BackColor;
        dialFromBar.FlatAppearance.BorderSize = 0;
        dialFromBar.FlatAppearance.MouseOverBackColor = dialRow.BackColor;
        dialFromBar.FlatAppearance.MouseDownBackColor = dialRow.BackColor;
        dialFromBar.UseVisualStyleBackColor = false;
        dialFromBar.FlatAppearance.MouseOverBackColor = dialRow.BackColor;
        dialFromBar.FlatAppearance.MouseDownBackColor = dialRow.BackColor;
        dialFromBar.UseVisualStyleBackColor = false;
        dialFromBar.Disposed += (_, _) => dialFromBar.Image?.Dispose();
        dialFromBar.Click += async (_, _) => await DialFromBarAsync();
        toggleKeypad.Text = "";
        toggleKeypad.Image = keypadIcon;
        toggleKeypad.ImageAlign = ContentAlignment.MiddleCenter;
        toggleKeypad.Width = 59;
        controlHelp.SetToolTip(toggleKeypad, "Mostra o nascondi il tastierino numerico");
        toggleKeypad.Dock = DockStyle.Right;
        toggleKeypad.FlatStyle = FlatStyle.Flat;
        toggleKeypad.BackColor = Color.FromArgb(228, 238, 249);
        toggleKeypad.FlatAppearance.BorderSize = 0;
        toggleKeypad.Click += (_, _) =>
        {
            keypadPanel.Visible = !keypadPanel.Visible;
            toggleKeypad.BackColor = keypadPanel.Visible
                ? Color.FromArgb(188, 219, 252) : Color.FromArgb(228, 238, 249);
        };
        // WinForms docks rightmost last: [Numero | Chiama | Tastierino].
        dialRow.Controls.Add(dialNumber);
        dialRow.Controls.Add(dialFromBar);
        dialRow.Controls.Add(toggleKeypad);
        callBar.Controls.Add(liveIcon);
        callBar.Controls.Add(liveInfo);
        callBar.Controls.Add(liveName);
        callBar.Controls.Add(liveDuration);
        callBar.Controls.Add(answerCall);
        callBar.Controls.Add(endCall);
        callBar.Controls.Add(dialRow);
        callBar.Resize += (_, _) => ArrangeLiveBar();
        CreateKeypad();
        ArrangeLiveBar();
        RenderLiveCall();
    }

    private void CreateKeypad()
    {
        keypadPanel.Dock = DockStyle.Top;
        keypadPanel.Height = 169;
        keypadPanel.BackColor = Color.FromArgb(237, 247, 251);
        keypadPanel.Visible = false;
        keypadPanel.Padding = new Padding(12, 2, 12, 7);
        var keys = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 4 };
        for (int col = 0; col < 3; col++) keys.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3333F));
        for (int row = 0; row < 4; row++) keys.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
        string[] digits = ["1", "2", "3", "4", "5", "6", "7", "8", "9", "*", "0", "#"];
        for (int i = 0; i < digits.Length; i++)
        {
            string digit = digits[i];
            var key = new Button { Text = digit, Dock = DockStyle.Fill, Margin = new Padding(3, 1, 3, 1),
                FlatStyle = FlatStyle.Flat, BackColor = Color.White,
                Font = new Font("Segoe UI Semibold", 12F, FontStyle.Bold) };
            key.FlatAppearance.BorderColor = Color.FromArgb(203, 217, 232);
            key.Click += async (_, _) => await HandleKeypadDigitAsync(digit);
            keys.Controls.Add(key, i % 3, i / 3);
        }
        keypadPanel.Controls.Add(keys);
    }

    private Task DialFromBarAsync() => DialAsync(dialNumber.Text);

    private async Task HandleKeypadDigitAsync(string digit)
    {
        var call = currentLiveCall;
        if (call is null || !monitorConnected || !call.ConnectedUtc.HasValue)
        {
            // Before a call, the dial field accepts ordinary digits. * and #
            // are DTMF-only keys, since DialAsync normalizes numeric destinations.
            if (digit is "*" or "#")
            {
                status.Text = "* e # disponibili come DTMF durante la conversazione.";
                return;
            }
            dialNumber.SelectedText = digit;
            return;
        }
        if (call.ActiveChannels != 1)
        {
            status.Text = "DTMF non disponibile: più canali attivi per l'interno.";
            return;
        }
        await dtmfQueue.WaitAsync();
        try
        {
            if (!monitorConnected || currentLiveCall?.Channel != call.Channel ||
                currentLiveCall.ConnectedUtc is null || currentLiveCall.ActiveChannels != 1)
            {
                status.Text = "DTMF annullato: chiamata non più attiva.";
                return;
            }
            await ami.SendDtmfAsync(config, call.Channel, digit);
            status.Text = "DTMF inviato al centralino; confermare la ricezione dal servizio remoto.";
            DiagnosticLog.Info("CTI_DTMF_SENT"); // Never log digits: these may be IVR PINs.
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("CTI_DTMF_FAILED", ex);
            status.Text = "DTMF: " + ex.Message;
        }
        finally { dtmfQueue.Release(); }
    }

    private void ArrangeLiveBar()
    {
        // Only one action can be shown. Avoid reserving empty space for a hidden button.
        var actions = LiveCallActions.For(currentLiveCall, monitorConnected);
        int rightEdge = callBar.ClientSize.Width - 10;
        answerCall.Top = endCall.Top = 16;
        if (actions.ShowAnswer)
        {
            answerCall.Left = rightEdge - answerCall.Width;
            rightEdge = answerCall.Left - 6;
        }
        else if (actions.ShowHangup)
        {
            endCall.Left = rightEdge - endCall.Width;
            rightEdge = endCall.Left - 6;
        }
        liveDuration.Width = 54;
        liveDuration.Left = rightEdge - liveDuration.Width - 4;
        liveDuration.Top = 13;
        liveInfo.Width = Math.Max(80, liveDuration.Left - liveInfo.Left - 4);
        liveName.Width = liveInfo.Width;
    }

    public void SetLiveCall(LiveCall? call)
    {
        // An answer is confirmed only by AMI, not by HTTP 200 from the telephone.
        if (pendingAnswerChannel is not null)
        {
            if (call?.Channel == pendingAnswerChannel && call.ConnectedUtc.HasValue)
                DiagnosticLog.Info("CTI_ANSWER_AMI_CONFIRMED", "state=Up; call=connected");
            else if (call is null || call.Channel != pendingAnswerChannel)
                DiagnosticLog.Warn("CTI_ANSWER_AMI_NO_CONNECTION", "state=ended_or_changed");
        }
        if (call is null || call.Channel != pendingAnswerChannel || call.ConnectedUtc.HasValue)
            pendingAnswerChannel = null;
        if (currentLiveCall?.Channel != call?.Channel) dismissedMiniChannel = null;
        // A new call must be able to recheck LDAP after an earlier negative
        // lookup. Keep retries bounded: only one lookup per number per call.
        if (!string.Equals(liveLookupChannel, call?.Channel, StringComparison.OrdinalIgnoreCase))
        {
            liveLookupChannel = call?.Channel;
            liveLookupAttempted.Clear();
            liveLookupFailed.Clear();
        }
        currentLiveCall = call;
        if (call is not null && monitorConnected) QueueLiveNameLookup(call.Peer);
        RenderLiveCall();
    }

    public void SetMonitorConnected(bool connected)
    {
        monitorConnected = connected;
        if (connected && !extensionNameLookupAttempted &&
            string.IsNullOrWhiteSpace(config.ExtensionDisplayName))
            _ = ResolveExtensionNameAsync();
        if (connected && currentLiveCall is not null) QueueLiveNameLookup(currentLiveCall.Peer);
        if (!connected)
        {
            // Never offer controls based on a stale channel after an AMI disconnect.
            currentLiveCall = null;
            pendingAnswerChannel = null;
        }
        RenderLiveCall();
    }

    private void UpdateHeaderSubtitle()
    {
        if (Controls.Count == 0 || Controls[0] is not Panel surface) return;
        var header = surface.Controls.OfType<Panel>().FirstOrDefault(p => p.Tag is Label);
        if (header?.Tag is Label subtitle)
            subtitle.Text = CallIdentityPresentation.ExtensionSubtitle(
                !string.IsNullOrWhiteSpace(config.ExtensionDisplayName)
                    ? config.ExtensionDisplayName : extensionName, config.Extension);
    }

    private async Task ResolveExtensionNameAsync()
    {
        if (extensionNameLookupPending || extensionNameLookupAttempted ||
            string.IsNullOrWhiteSpace(config.Extension)) return;
        extensionNameLookupAttempted = true;
        extensionNameLookupPending = true;
        var cfg = config;
        try
        {
            extensionName = await ldap.LookupInternalExtensionNameAsync(cfg, cfg.Extension);
            if (IsDisposed || !ReferenceEquals(cfg, config)) return;
            if (string.IsNullOrWhiteSpace(extensionName))
            {
                var found = await ldap.LookupForNumbersAsync(cfg, [cfg.Extension]);
                if (IsDisposed || !ReferenceEquals(cfg, config)) return;
                extensionName = found.GetValueOrDefault(PhoneNumbers.SearchKey(cfg.Extension))?.Contact?.DisplayName ?? "";
            }
            UpdateHeaderSubtitle();
        }
        catch (Exception e)
        {
            if (!IsDisposed && ReferenceEquals(cfg, config))
                DiagnosticLog.Warn("CTI_EXTENSION_LDAP_NAME_UNAVAILABLE", e.GetType().Name);
        }
        finally { if (ReferenceEquals(cfg, config)) extensionNameLookupPending = false; }
    }

    private string ResolvedPeerName(string? peer)
    {
        var key = PhoneNumbers.SearchKey(peer);
        if (key.Length == 0) return "";
        var name = cdrDirectory.GetValueOrDefault(key)?.Contact?.DisplayName;
        if (!string.IsNullOrWhiteSpace(name)) return name;
        // An unverified key (failed/partial fresh read) may not inherit a stale
        // name from the Rubrica tab or an earlier CDR load.
        if (unverifiedLdapKeys.Contains(key)) return "";
        return contacts.FirstOrDefault(c => PhoneNumbers.SearchKey(c.Number) == key)?.Name ?? "";
    }

    private void QueueLiveNameLookup(string? peer)
    {
        var number = PhoneNumbers.Dialable(peer);
        var key = PhoneNumbers.SearchKey(number);
        if (key.Length == 0 || liveLookupPending.Contains(key) ||
            !LiveNameLookupRules.ShouldRecheck(cdrDirectory.GetValueOrDefault(key),
                liveLookupAttempted.Contains(key))) return;
        liveLookupAttempted.Add(key);
        liveLookupPending.Add(key);
        _ = ResolveLiveNameAsync(number, key, config, ldapCacheGeneration);
    }

    private async Task ResolveLiveNameAsync(string number, string key, AppConfig cfg, int generation)
    {
        try
        {
            // Extensions live in the UCM's own read-only PBX phonebook.
            // Do not depend on the general LDAP root or writable phonebook here.
            if (PhoneNumbers.Dialable(number).TrimStart('+').Length <= cfg.InternalExtensionMaxLength)
            {
                var internalName = await ldap.LookupInternalExtensionNameAsync(cfg, number);
                if (IsDisposed || !ReferenceEquals(cfg, config) ||
                    generation != ldapCacheGeneration) return;
                if (!string.IsNullOrWhiteSpace(internalName))
                {
                    cdrDirectory[key] = new LdapContactMatch(new UcmContactDetails {
                        AccountNumber = number, CallerIdName = internalName,
                        PhonebookDn = "ou=pbx,dc=pbx,dc=com"
                    }); // NOT a writable destination, never offer pencil/delete.
                    unverifiedLdapKeys.Remove(key);
                    DiagnosticLog.Info("CTI_LIVE_LDAP_LOOKUP", "result=name_found; source=pbx_read_only");
                    if (currentLiveCall is not null &&
                        PhoneNumbers.SearchKey(currentLiveCall.Peer) == key) RenderLiveCall();
                    if (showCalls) ShowCallRows();
                    return;
                }
            }
            var found = await ldap.LookupForNumbersAsync(cfg, [number]);
            if (IsDisposed || !ReferenceEquals(cfg, config) ||
                generation != ldapCacheGeneration) return;
            if (found.TryGetValue(key, out var match))
            {
                cdrDirectory[key] = match; // A fresh confirmed absence replaces stale names.
                unverifiedLdapKeys.Remove(key);
                DiagnosticLog.Info("CTI_LIVE_LDAP_LOOKUP", "result=" +
                    (!string.IsNullOrWhiteSpace(match.Contact?.DisplayName) ? "name_found" :
                     match.Contact is not null ? "entry_without_name" : "not_found"));
            }
            else
            {
                cdrDirectory.Remove(key);
                unverifiedLdapKeys.Add(key); // Partial branch search: absence unverified.
                DiagnosticLog.Warn("CTI_LIVE_LDAP_LOOKUP", "result=unverified_partial_scope");
            }
            // The call could have ended/changed during LDAP I/O.
            if (currentLiveCall is not null &&
                PhoneNumbers.SearchKey(currentLiveCall.Peer) == key) RenderLiveCall();
            if (showCalls) ShowCallRows();
        }
        catch (Exception e)
        {
            if (!IsDisposed && ReferenceEquals(cfg, config) && generation == ldapCacheGeneration)
            {
                cdrDirectory.Remove(key);
                unverifiedLdapKeys.Add(key);
                liveLookupFailed.Add(key);
                DiagnosticLog.Warn("CTI_LIVE_LDAP_NAME_UNAVAILABLE", e.GetType().Name);
                if (currentLiveCall is not null && PhoneNumbers.SearchKey(currentLiveCall.Peer) == key)
                    RenderLiveCall();
                if (showCalls) ShowCallRows();
            }
        }
        finally { if (ReferenceEquals(cfg, config)) liveLookupPending.Remove(key); }
    }

    private void RenderLiveCall()
    {
        var call = currentLiveCall;
        var actions = LiveCallActions.For(call, monitorConnected);
        controlHelp.SetToolTip(toggleKeypad, call?.ConnectedUtc is not null
            ? "Durante la conversazione il tastierino invia DTMF tramite AMI PlayDTMF (Receive=true)."
            : "Prima della chiamata il tastierino inserisce cifre nel campo Componi.");
        answerCall.Visible = actions.ShowAnswer;
        endCall.Visible = actions.ShowHangup;
        ArrangeLiveBar();

        if (call is null || !monitorConnected)
        {
            liveIcon.Image = null;
            liveInfo.Text = monitorConnected ? "Nessuna chiamata in corso" : "Monitor AMI non disponibile";
            liveName.Text = "";
            liveDuration.Text = "--:--";
            answerCall.Enabled = false;
            endCall.Enabled = false;
            lastAnswerAvailability = "";
            UpdateMiniCall();
            return;
        }

        liveIcon.Image = call.Incoming ? incomingIcon : outgoingIcon;
        var name = ResolvedPeerName(call.Peer);
        var phase = !call.DirectionKnown ? "Rilevamento chiamata" :
            call.ConnectedUtc.HasValue ? "In conversazione" :
            call.Incoming ? "In arrivo · squilla" : "In uscita";
        liveInfo.Text = CallIdentityPresentation.NumberLine(call.Peer);
        liveName.Text = CallIdentityPresentation.NameLine(name);
        controlHelp.SetToolTip(liveInfo, phase + " · " + liveInfo.Text);
        controlHelp.SetToolTip(liveName, liveName.Text);
        TimeSpan elapsed = DateTime.UtcNow - (call.ConnectedUtc ?? call.StartedUtc);
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        liveDuration.Text = elapsed.TotalHours >= 1 ? elapsed.ToString(@"hh\:mm\:ss") : elapsed.ToString(@"mm\:ss");

        // A channel must be unambiguous before issuing answer/hangup commands.
        bool single = call.ActiveChannels == 1;
        bool phoneConfigured = (config.PhoneType is "Grandstream" or "Snom" or "Yealink") &&
            (config.AutoDetectPhoneAddress || !string.IsNullOrWhiteSpace(config.PhoneAddress)) &&
            (config.PhoneType == "Yealink" &&
              (string.IsNullOrWhiteSpace(config.YealinkWebUsername) == string.IsNullOrWhiteSpace(config.YealinkWebPassword)) ||
             (config.PhoneType == "Grandstream" && !string.IsNullOrWhiteSpace(config.PhoneControlPassword)) ||
             (config.PhoneType == "Snom" && !string.IsNullOrWhiteSpace(config.SnomWebUsername) &&
              !string.IsNullOrWhiteSpace(config.SnomWebPassword)));
        if (pendingAnswerChannel == call.Channel &&
            DateTime.UtcNow - pendingAnswerStartedUtc > TimeSpan.FromSeconds(12))
        {
            DiagnosticLog.Warn("CTI_ANSWER_AMI_CONFIRM_TIMEOUT", "seconds=12; phone_command_may_have_been_sent=true");
            pendingAnswerChannel = null;  // Permit retry if no AMI answer event arrived.
        }

        // CTI acceptcall acts on the uniquely resolved physical phone, NOT on
        // an arbitrarily selected AMI channel. Two SIP contacts can ring the
        // same extension simultaneously; this must not disable Answer.
        // Hangup is intentionally unchanged: it requires exactly one channel.
        bool pending = pendingAnswerChannel == call.Channel;
        answerCall.Enabled = LiveCallActions.CanAnswerOnDevice(call, monitorConnected,
            phoneConfigured, pending);
        endCall.Enabled = actions.ShowHangup && single && Volatile.Read(ref hangupInProgress) == 0;
        if (actions.ShowAnswer)
        {
            string answerState = $"{call.Channel}/{call.ActiveChannels}/{phoneConfigured}/{pending}/{answerCall.Enabled}";
            if (answerState != lastAnswerAvailability)
            {
                lastAnswerAvailability = answerState;
                DiagnosticLog.Info("CTI_ANSWER_BUTTON_STATE", "ringing=true; active_channels=" +
                    call.ActiveChannels + "; phone_configured=" + phoneConfigured +
                    "; pending=" + pending + "; enabled=" + answerCall.Enabled);
            }
        }
        else lastAnswerAvailability = "";
        controlHelp.SetToolTip(answerCall, phoneConfigured
            ? "Accetta la chiamata in squillo sul telefono; conferma dello stato tramite eventi AMI."
            : "Per attivare Rispondi, scegli un profilo telefono, abilita il rilevamento IP AMI o IPv4 manuale e imposta le credenziali specifiche del produttore.");
        UpdateMiniCall();
    }

    private void TraceMiniDecision(string reason, LiveCall? call)
    {
        var key = reason + "|" + (call?.Channel ?? "") + "|" +
            (call?.DirectionKnown ?? false) + "|" + (call?.ConnectedUtc.HasValue ?? false);
        if (key == lastMiniDecision) return;
        lastMiniDecision = key;
        if (call is not null)
            DiagnosticLog.Info("MINI_CALL_DECISION", "reason=" + reason + "; direction=" +
                (call.DirectionKnown ? (call.Incoming ? "incoming" : "outgoing") : "unknown") +
                "; phase=" + (call.ConnectedUtc.HasValue ? "connected" : "setup"));
    }

    private void UpdateMiniCall()
    {
        var call = currentLiveCall;
        if (!MiniPopupRules.ShouldDisplay(call, monitorConnected) || call is null)
        {
            TraceMiniDecision(monitorConnected ? "no_local_call" : "monitor_offline", call);
            miniCall.Hide();
            dismissedMiniChannel = null;
            return;
        }
        if ((Visible && WindowState != FormWindowState.Minimized) || dismissedMiniChannel == call.Channel)
        {
            TraceMiniDecision(dismissedMiniChannel == call.Channel ? "dismissed" : "main_window_visible", call);
            miniCall.Hide();
            return;
        }
        TraceMiniDecision("show", call);
        string contactName = ResolvedPeerName(call.Peer);
        TimeSpan elapsed = DateTime.UtcNow - (call.ConnectedUtc ?? call.StartedUtc);
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        var actions = LiveCallActions.For(call, monitorConnected);
        miniCall.Display(call.Channel, contactName, call.Peer, elapsed, MiniPopupRules.Phase(call),
            actions.ShowAnswer, answerCall.Enabled, actions.ShowHangup, endCall.Enabled,
            Screen.FromRectangle(Bounds), config.MiniPopupTimeoutSeconds);
    }

    private async Task AnswerCurrentCallAsync()
    {
        var call = currentLiveCall;
        var actions = LiveCallActions.For(call, monitorConnected);
        if (call is null || !actions.ShowAnswer || !answerCall.Enabled ||
            pendingAnswerChannel == call.Channel)
        {
            DiagnosticLog.Warn("CTI_ANSWER_BLOCKED", "reason=" +
                (call is null ? "no_call" : !actions.ShowAnswer ? "not_incoming_ringing" :
                 !answerCall.Enabled ? "button_disabled" : "pending_answer"));
            return;
        }
        DiagnosticLog.Info("CTI_ANSWER_CLICK", "ringing=true; active_channels=" + call.ActiveChannels +
            "; profile=" + config.PhoneType + "; auto_ip=" + config.AutoDetectPhoneAddress);
        pendingAnswerChannel = call.Channel;
        pendingAnswerStartedUtc = DateTime.UtcNow;
        RenderLiveCall();
        try
        {
            await phone.AnswerAsync(config);
            DiagnosticLog.Info("PHONE_ANSWER_DISPATCHED", "awaiting_ami_state=true");
            status.Text = "Risposta inviata al telefono; attendo conferma AMI.";
        }
        catch (Exception e)
        {
            pendingAnswerChannel = null;
            DiagnosticLog.Error("PHONE_ANSWER_FAILED", e);
            status.Text = "Rispondi: " + e.Message;
            RenderLiveCall();
            miniCall.SetFeedback(e.Message, true);
        }
    }

    private async Task HangupCurrentCallAsync()
    {
        var call = currentLiveCall;
        if (call is null || !LiveCallActions.For(call, monitorConnected).ShowHangup ||
            call.ActiveChannels != 1 ||
            Interlocked.CompareExchange(ref hangupInProgress, 1, 0) != 0) return;
        endCall.Enabled = false;
        // Snom can keep the local speaker/busy tone active after UCM AMI Hangup.
        // Only an explicit user click on ONE known outgoing Snom call can trigger
        // the additional device key sequence. Other vendors remain AMI-only.
        bool releaseSnom = SnomHangupRules.NeedsPhysicalRelease(config.PhoneType, call, monitorConnected);
        bool releaseYealink = YealinkHangupRules.NeedsPhysicalRelease(config.PhoneType, call, monitorConnected);
        Exception? amiError = null;
        Exception? deviceError = null;
        try
        {
            try
            {
                await ami.HangupAsync(config, call.Channel);
                DiagnosticLog.Info("AMI_HANGUP_ACCEPTED");
            }
            catch (Exception e)
            {
                amiError = e;
                DiagnosticLog.Error("AMI_HANGUP_FAILED", e);
            }
            if (releaseSnom)
            {
                // Even if AMI failed, attempt the device release once; a stale
                // PBX channel must not leave the Snom speaker on indefinitely.
                try
                {
                    await phone.ReleaseSnomSpeakerAsync(config);
                    DiagnosticLog.Info("SNOM_LOCAL_RELEASE_HTTP_ACCEPTED", "operation=end; confirmation=device_pending");
                }
                catch (Exception e)
                {
                    deviceError = e;
                    DiagnosticLog.Error("SNOM_LOCAL_RELEASE_FAILED", e);
                }
            }
            if (releaseYealink)
            {
                // Never send this on an AMI event, incoming call or multi-channel call.
                // Generic Yealink CALLEND returns the local device to idle if supported.
                try
                {
                    await phone.ReleaseYealinkSpeakerAsync(config);
                    DiagnosticLog.Info("YEALINK_LOCAL_RELEASE_HTTP_ACCEPTED", "confirmation=AMI_or_device_pending");
                }
                catch (Exception e)
                {
                    deviceError = e;
                    DiagnosticLog.Error("YEALINK_LOCAL_RELEASE_FAILED", e);
                }
            }
            if (amiError is null && deviceError is null)
                status.Text = releaseSnom || releaseYealink
                    ? "Terminazione inviata a UCM e telefono; verificare riaggancio."
                    : "Comando Termina inviato; attendo conferma AMI.";
            else
            {
                string notice = amiError is not null && deviceError is not null
                    ? "UCM e telefono: terminazione non confermata. Controllare il dispositivo."
                    : amiError is not null
                        ? "UCM: terminazione non confermata; controllare lo stato chiamata."
                        : "UCM terminata, ma il riaggancio locale non è confermato.";
                status.Text = notice;
                miniCall.SetFeedback(notice, true);
            }
        }
        finally
        {
            Interlocked.Exchange(ref hangupInProgress, 0);
            RenderLiveCall();
        }
    }

    /// <summary>Invoked only when AMI reports local call termination, never on an interval timer.</summary>
    public Task RefreshCdrAfterCallAsync() => LoadCdrAsync(recentOnly: true);

    private static void StyleTab(Button b, string text, bool selected)
    {
        // FIX8: Rubrica e Registro usano lo stesso stile del titolo BLF.
        b.Text = text; b.Height = 34;
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.BorderColor = Color.FromArgb(43, 98, 151);
        b.BackColor = selected ? Color.FromArgb(28, 83, 132) : Color.FromArgb(224, 237, 249);
        b.ForeColor = selected ? Color.White : Color.FromArgb(27, 76, 125);
        b.Font = new Font("Segoe UI", 9.5F, FontStyle.Bold);
        b.TextAlign = ContentAlignment.MiddleCenter;
        b.Cursor = Cursors.Hand;
    }

    private void RefreshCallFilterStyles()
    {
        for (int i = 0; i < callFilterButtons.Length; i++)
        {
            var b = callFilterButtons[i];
            if (b is null) continue;
            bool selected = i == (int)selectedCallFilter;
            b.BackColor = selected ? Color.FromArgb(221, 241, 248) : Color.FromArgb(245, 249, 251);
            b.ForeColor = selected ? Color.FromArgb(0, 117, 148) : Color.FromArgb(76, 94, 110);
            b.Font = new Font("Segoe UI Semibold", 8.3F, selected ? FontStyle.Bold : FontStyle.Regular);
        }
    }

    private void SetupGrid(bool isCalls)
    {
        gridHost.Padding = isCalls ? new Padding(6, 0, 0, 0) : Padding.Empty;
        grid.Columns.Clear();
        if (isCalls)
        {
            grid.Columns.Add(new DataGridViewImageColumn
            {
                Name = "directionIcon", HeaderText = "", Width = 27,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
                ImageLayout = DataGridViewImageCellLayout.Zoom,
                SortMode = DataGridViewColumnSortMode.NotSortable
            });
            grid.Columns.Add("date", "Data");
            grid.Columns.Add("hour", "Ora");
            grid.Columns.Add("duration", "Durata");
            grid.Columns.Add("ldapName", "Nome");
            grid.Columns.Add("peer", "Numero");
            // Expand only the contact name when the form is resized.
            // Date/time/duration/destination are fixed so long names stop truncating.
            foreach (var (columnName, fixedWidth) in new[] {
                ("date", 78), ("hour", 55), ("duration", 64), ("peer", 94) })
            {
                var column = grid.Columns[columnName]!;
                column.AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
                column.Width = fixedWidth;
                column.MinimumWidth = fixedWidth;
            }
            grid.Columns["ldapName"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            grid.Columns["ldapName"]!.FillWeight = 100;
            grid.Columns["ldapName"]!.MinimumWidth = 115;
            var action = new DataGridViewButtonColumn
            {
                Name = "saveContact", HeaderText = "", UseColumnTextForButtonValue = false,
                Width = 45, MinimumWidth = 45,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                FlatStyle = FlatStyle.Flat
            };
            action.DefaultCellStyle.Font = plusFont;
            action.DefaultCellStyle.ForeColor = Color.FromArgb(24, 149, 67);
            action.DefaultCellStyle.SelectionForeColor = Color.FromArgb(24, 149, 67);
            grid.Columns.Add(action);
        }
        else
        {
            grid.Columns.Add("name", "Contatto");
            grid.Columns.Add("phone", "Numero");
            // Il nominativo sfrutta tutto lo spazio disponibile quando la finestra si allarga.
            // La larghezza del numero resta compatta e invariata.
            grid.Columns["phone"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
            grid.Columns["phone"]!.Width = 112;
            grid.Columns["phone"]!.MinimumWidth = 112;
            grid.Columns["name"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            grid.Columns["name"]!.FillWeight = 100;
            grid.Columns["name"]!.MinimumWidth = 125;
            grid.Columns["name"]!.DefaultCellStyle.Padding = new Padding(6, 0, 0, 0);
            grid.Columns["name"]!.HeaderCell.Style.Padding = new Padding(6, 0, 0, 0);
            var edit = new DataGridViewButtonColumn
            {
                Name = "editContact", HeaderText = "", Width = 44,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
                UseColumnTextForButtonValue = false, FlatStyle = FlatStyle.Flat,
                SortMode = DataGridViewColumnSortMode.NotSortable
            };
            edit.DefaultCellStyle.Font = editFont;
            grid.Columns.Add(edit);
            var remove = new DataGridViewImageColumn
            {
                Name = "deleteContact", HeaderText = "", Width = 40,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
                Image = deleteIcon,
                ImageLayout = DataGridViewImageCellLayout.Zoom,
                SortMode = DataGridViewColumnSortMode.NotSortable
            };
            remove.DefaultCellStyle.Padding = new Padding(9, 10, 9, 10);
            grid.Columns.Add(remove);
        }
        var call = new DataGridViewImageColumn
        {
            Name = "call", HeaderText = "", Image = callBlue,
            ImageLayout = DataGridViewImageCellLayout.Zoom,
            Width = 41, AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
            SortMode = DataGridViewColumnSortMode.NotSortable
        };
        call.DefaultCellStyle.Padding = new Padding(9, 8, 9, 8);
        grid.Columns.Add(call);
        foreach (DataGridViewColumn column in grid.Columns)
            column.SortMode = DataGridViewColumnSortMode.NotSortable;
        if (!isCalls)
        {
            // Programmatic lets us sort the underlying contacts consistently and
            // avoids losing the sort whenever LDAP repopulates the grid.
            grid.Columns["name"]!.SortMode = DataGridViewColumnSortMode.Programmatic;
            grid.Columns["phone"]!.SortMode = DataGridViewColumnSortMode.Programmatic;
            UpdateContactSortGlyph();
        }
    }

    private void UpdateContactSortGlyph()
    {
        foreach (var key in new[] { "name", "phone" })
        {
            if (grid.Columns.Contains(key))
                grid.Columns[key]!.HeaderCell.SortGlyphDirection =
                    key == contactSortColumn
                        ? (contactSortAscending ? SortOrder.Ascending : SortOrder.Descending)
                        : SortOrder.None;
        }
    }

    public void UpdateConfiguration(AppConfig value)
    {
        activeSearch?.Cancel();
        config = value;
        avayaBlf = Array.Empty<AvayaBlfRow>();
        avayaBlfUnavailable = false;
        presenceTimer.Interval = PresenceRefreshIntervalMs(config.PresenceRefreshSeconds);
        DiagnosticLog.Info("PRESENCE_REFRESH_CONFIG", "interval_seconds=" + (presenceTimer.Interval / 1000));
        ResetBlf();
        presenceStates.Clear(); ownPresenceState=PresenceState.Unknown; RefreshOwnPresenceAvatar();
        _ = RefreshPresenceAsync();
        pendingAnswerChannel = null;
        TopMost = config.KeepPopupOnTop;
        RenderLiveCall();
        if (Controls.Count > 0)
        {
            var surface = (Panel)Controls[0];
            var header = surface.Controls.OfType<Panel>().First(p => ReferenceEquals(p.Controls.OfType<Label>().FirstOrDefault(), title));
            if (header.Tag is Label sub) sub.Text = CallIdentityPresentation.ExtensionSubtitle(
                config.ExtensionDisplayName, config.Extension);
        }
        extensionName = "";
        extensionNameLookupAttempted = false;
        extensionNameLookupPending = false;
        liveLookupPending.Clear(); liveLookupFailed.Clear();
        liveLookupAttempted.Clear(); liveLookupChannel = null;
        UpdateHeaderSubtitle();
        if (monitorConnected && string.IsNullOrWhiteSpace(config.ExtensionDisplayName))
            _ = ResolveExtensionNameAsync();
        contacts.Clear(); calls.Clear(); cdrDirectory.Clear(); unverifiedLdapKeys.Clear();
        ldapCacheGeneration++; lastCdrLdapCheckUtc = DateTime.MinValue;
        cdrDirectoryAvailable = false;
        cdrInitialized = false; grid.Rows.Clear();
        status.Text = "Configurazione aggiornata. Apri Registro per sincronizzare i CDR.";
    }

    private void PersistWindowBounds()
    {
        if (WindowState == FormWindowState.Normal && Width >= 450 && Height >= 400)
        {
            WindowStateStore.Save(Bounds);
            hasSavedWindowBounds = true;
        }
    }

    public void PositionNearTray()
    {
        // Do not reset bounds when maximized or minimized: Windows must preserve
        // the original restore rectangle used by the standard square caption button.
        if (WindowState != FormWindowState.Normal) return;
        if (hasSavedWindowBounds)
        {
            Bounds = WindowStateStore.ClampToScreen(Bounds);
            return;
        }
        // Per la prima esecuzione usa la tray; quando l'utente ridimensiona
        // o sposta la finestra, le coordinate sono persistite in window.json.
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(Math.Max(area.Left, area.Right - Width - 12),
            Math.Max(area.Top, area.Bottom - Height - 8));
    }

    /// <summary>Read the latest CDR once after client startup (no polling).</summary>
    public async Task SynchronizeCdrAtStartupAsync()
    {
        DiagnosticLog.Info("CDR_STARTUP_SYNC_BEGIN", $"max_records={Math.Clamp(config.CdrMaxRecords, 25, 2000)}");
        await LoadCdrAsync();
        DiagnosticLog.Info("CDR_STARTUP_SYNC_END", $"loaded={cdrInitialized}; rows={calls.Count}");
    }

    public async Task OpenAsync()
    {
        PositionNearTray();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Show(); Activate(); BringToFront();
        miniCall.Hide();
        if (showCalls)
        {
            if (cdrInitialized) ShowCallRows();
            else if (!cdrLoading) await LoadCdrAsync();
        }
        else await LoadContactsAsync();
    }

    private async Task SwitchTabAsync(bool cdrMode)
    {
        if (showCalls == cdrMode) return;
        activeSearch?.Cancel(); debounce.Stop();
        // Crucial: changing search.Text fires TextChanged synchronously.
        // Never render CDR Bitmap icons while the grid still has LDAP text columns.
        suppressSearchEvents = true;
        try
        {
            showCalls = cdrMode;
            grid.Visible = true;
            if (searchFilterPanel != null) searchFilterPanel.Visible = true;
            StyleTab(bookTab, "Rubrica", !cdrMode);
            StyleTab(callsTab, "Registro", cdrMode);
            callFilters.Visible = cdrMode;
            SetupGrid(cdrMode);
            search.PlaceholderText = cdrMode ? "Filtra lo storico chiamate..." : "Cerca nome o numero in LDAP...";
            search.Text = "";
        }
        finally { suppressSearchEvents = false; }
        recordCount.Text = cdrMode ? "Registro: 0 record" : "Rubrica: 0 record";
        if (cdrMode)
        {
            if (cdrInitialized) ShowCallRows();
            else if (!cdrLoading) await LoadCdrAsync();
            else status.Text = "Sincronizzazione CDR iniziale in corso...";
        }
        else await LoadContactsAsync();
    }

    private async Task LoadContactsAsync(bool afterMutation = false)
    {
        activeSearch?.Cancel();
        var token = new CancellationTokenSource(); activeSearch = token;
        try
        {
            bool avaya = config.PhoneType == "Avaya";
            status.Text = avaya ? "Ricerca rubrica Avaya..." : "Ricerca LDAP in corso...";
            refresh.Enabled = false;
            var results = avaya
                ? await AvayaGatewayClient.SearchAsync(config, search.Text, token.Token)
                : afterMutation
                ? await ldap.SearchAfterMutationAsync(config, search.Text, token.Token)
                : await ldap.SearchAsync(config, search.Text, token.Token);
            if (token.IsCancellationRequested || showCalls) return;
            contacts = results;
            // Explicit schema guard: never write text into a stale CDR image cell.
            if (!grid.Columns.Contains("name") || grid.Columns["name"] is DataGridViewImageColumn ||
                !grid.Columns.Contains("phone") || !grid.Columns.Contains("call"))
                SetupGrid(false);
            RenderLiveCall();
            DiagnosticLog.Info(avaya ? "UI_AVAYA_ROWS" : "UI_LDAP_ROWS", $"numbers={results.Count}");
            RenderContactRows();
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            if (!token.IsCancellationRequested)
            {
                DiagnosticLog.Error(config.PhoneType == "Avaya" ? "UI_AVAYA_LOAD_FAILED" : "UI_LDAP_LOAD_FAILED", e);
                if (!showCalls)
                {
                    contacts.Clear();
                    grid.Rows.Clear();
                    recordCount.Text = "Rubrica: 0 record";
                    RenderLiveCall();
                }
                status.Text = config.PhoneType == "Avaya" ? "Rubrica Avaya: " + e.Message
                    : e is DirectoryOperationException
                    ? "LDAP: ramo non raggiungibile. Verificare Base DN e rubrica sulla UCM."
                    : "LDAP: ricerca non disponibile. " + e.Message;
            }
        }
        finally
        {
            if (ReferenceEquals(activeSearch, token))
            {
                activeSearch = null;
                refresh.Enabled = true;
            }
            token.Dispose();
        }
    }

    // Re-render rows without querying LDAP: sorting changes only presentation.
    // Keep original Contact instances in row.Tag so edit/delete targets remain exact.
    private void RenderContactRows()
    {
        if (showCalls || !grid.Columns.Contains("name") || !grid.Columns.Contains("phone")) return;
        grid.Rows.Clear();
        foreach (var c in ContactSortRules.Order(contacts, contactSortColumn, contactSortAscending))
        {
            int rowIndex = grid.Rows.Add();
            var row = grid.Rows[rowIndex];
            row.Cells["name"].Value = c.Name;
            row.Cells["phone"].Value = c.Number;
            row.Cells["call"].Value = callBlue;
            if (config.EnableLdapContactEditing && c.PhonebookDn.Length > 0 && c.AccountNumber.Length > 0 &&
                !c.PhonebookDn.Equals("ou=pbx,dc=pbx,dc=com", StringComparison.OrdinalIgnoreCase))
            {
                row.Cells["editContact"].Value = "✎";
                row.Cells["editContact"].ToolTipText = "Modifica i dettagli LDAP del contatto";
                row.Cells["deleteContact"].Value = deleteIcon;
                row.Cells["deleteContact"].ToolTipText = "Elimina definitivamente il contatto LDAP (con conferma)";
            }
            else
            {
                row.Cells["editContact"] = new DataGridViewTextBoxCell { Value = "" };
                row.Cells["deleteContact"] = new DataGridViewTextBoxCell { Value = "" };
            }
            row.Tag = c;
            row.Cells["call"].ToolTipText = "Chiama " + c.Name;
        }
        UpdateContactSortGlyph();
        recordCount.Text = $"Rubrica: {grid.Rows.Count} record";
        status.Text = LdapSummaryFormatter.Build(contacts);
        status.AutoEllipsis = true;
        controlHelp.SetToolTip(status, status.Text);
    }

    private async Task LoadCdrAsync(bool recentOnly = false)
    {
        if (cdrLoading) return;
        cdrLoading = true;
        try
        {
            refresh.Enabled = false;
            if (showCalls && !recentOnly) status.Text = "Caricamento CDR in corso...";
            var result = await cdr.GetForExtensionAsync(config, recentOnly);
            DiagnosticLog.Info("CDR_ROWS", $"count={result.Count}");
            // Durante il polling, conserva lo storico già caricato ma sostituisci
            // eventuali sessioni che sono state completate dopo il primo download.
            // La query corrente contiene gli aggiornamenti più recenti.
            var combined = recentOnly ? result.Concat(calls) : result.AsEnumerable();
            var next = combined.GroupBy(c => c.Id, StringComparer.Ordinal)
                .Select(g => g.First())
                .OrderByDescending(c => SortTime(c.Timestamp))
                .ThenByDescending(c => c.Timestamp, StringComparer.Ordinal)
                .Take(Math.Clamp(config.CdrMaxRecords, 25, 2000))
                .ToList();
            bool changed = !recentOnly || !calls.SequenceEqual(next);
            calls = next;
            cdrInitialized = true;
            // Every CDR number has its own exact LDAP lookup, including entries
            // regardless of how many LDAP contacts exist in the UCM directory.
            bool rescanLdap = !recentOnly ||
                DateTime.UtcNow - lastCdrLdapCheckUtc >= TimeSpan.FromMinutes(1);
            bool ldapUpdated = await RefreshCdrDirectoryAsync(next.Select(c => c.Peer), force: rescanLdap);
            if (showCalls)
            {
                if (changed || ldapUpdated) ShowCallRows();
                else status.Text = $"{calls.Count} chiamate · aggiornato {DateTime.Now:HH:mm:ss}";
            }
        }
        catch (Exception e)
        {
            DiagnosticLog.Error("UI_CDR_LOAD_FAILED", e);
            if (showCalls) status.Text = "CDR: " + e.Message;
        }
        finally { cdrLoading = false; refresh.Enabled = true; }
    }

    private static DateTime SortTime(string timestamp)
    {
        if (DateTime.TryParse(timestamp, CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out var parsed)) return parsed;
        if (DateTime.TryParse(timestamp, CultureInfo.CurrentCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out parsed)) return parsed;
        return DateTime.MinValue;
    }

    private static bool MatchesDirection(CallEntry c, CallFilter filter) => filter switch
    {
        CallFilter.All => true,
        CallFilter.Outgoing => c.Direction == "Uscita",
        CallFilter.Incoming => c.Direction == "Entrata",
        CallFilter.Missed => c.Direction == "Persa",
        _ => true
    };

    private Image DirectionImage(CallEntry call) => call.Direction switch
    {
        "Uscita" => outgoingIcon,
        "Entrata" => incomingIcon,
        "Persa" => missedIcon,
        _ => incomingIcon
    };

    private static bool IsExternal(string number, int internalMax)
    {
        var normalized = PhoneNumbers.Dialable(number).TrimStart('+');
        return normalized.Length > internalMax && normalized.All(char.IsAsciiDigit);
    }

    private bool IsExternal(string number) => IsExternal(number, config.InternalExtensionMaxLength);

    private LdapContactMatch? CdrMatch(string number) =>
        cdrDirectory.GetValueOrDefault(PhoneNumbers.SearchKey(number));

    private async Task<bool> RefreshCdrDirectoryAsync(IEnumerable<string> numbers, bool force,
        bool afterMutation = false)
    {
        var requested = numbers.Select(PhoneNumbers.Dialable).Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(n => force || (!cdrDirectory.ContainsKey(PhoneNumbers.SearchKey(n)) &&
                                   !unverifiedLdapKeys.Contains(PhoneNumbers.SearchKey(n)))).ToArray();
        if (requested.Length == 0) return false;
        var keys = requested.Select(PhoneNumbers.SearchKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int generation = ldapCacheGeneration;
        if (force)
        {
            generation = ++ldapCacheGeneration;
            lastCdrLdapCheckUtc = DateTime.UtcNow;
            // On a forced refresh, never keep a stale name while a new LDAP read
            // fails. Unverified is safer than displaying/deleting the old record.
            foreach (var key in keys)
            {
                cdrDirectory.Remove(key);
                unverifiedLdapKeys.Add(key);
            }
            contacts.RemoveAll(c => keys.Contains(PhoneNumbers.SearchKey(c.Number)));
            if (showCalls) ShowCallRows();
        }
        try
        {
            var lookups = afterMutation
                ? await ldap.LookupAfterMutationAsync(config, requested)
                : await ldap.LookupForNumbersAsync(config, requested);
            if (generation != ldapCacheGeneration || IsDisposed) return false;
            foreach (var pair in lookups)
            {
                cdrDirectory[pair.Key] = pair.Value;
                unverifiedLdapKeys.Remove(pair.Key);
            }
            // Missing dictionary keys mean the global base was unavailable and
            // only the configured phonebook was searched: do not invent absence.
            foreach (var key in keys.Except(lookups.Keys, StringComparer.OrdinalIgnoreCase))
            {
                cdrDirectory.Remove(key);
                unverifiedLdapKeys.Add(key);
            }
            cdrDirectoryAvailable = keys.All(k => !unverifiedLdapKeys.Contains(k));
            if (currentLiveCall is not null &&
                keys.Contains(PhoneNumbers.SearchKey(currentLiveCall.Peer))) RenderLiveCall();
            return true;
        }
        catch (Exception e)
        {
            if (generation != ldapCacheGeneration || IsDisposed) return false;
            foreach (var key in keys)
            {
                cdrDirectory.Remove(key);
                unverifiedLdapKeys.Add(key);
            }
            contacts.RemoveAll(c => keys.Contains(PhoneNumbers.SearchKey(c.Number)));
            cdrDirectoryAvailable = false;
            DiagnosticLog.Error("CDR_LDAP_NAME_LOOKUP_FAILED", e);
            if (showCalls) status.Text = "CDR disponibili; LDAP non verificabile (usa ? per ricontrollare).";
            if (currentLiveCall is not null &&
                keys.Contains(PhoneNumbers.SearchKey(currentLiveCall.Peer))) RenderLiveCall();
            return false;
        }
    }

    // The CDR can retain a historical caller name even after LDAP deletion.
    // The name is display-only: edit/add is decided solely by destination lookup.
    private static string CdrDisplayName(CallEntry call, LdapContactMatch? match)
    {
        var current = match?.Contact?.DisplayName;
        if (!string.IsNullOrWhiteSpace(current)) return current;
        var historic = call.PeerName?.Trim() ?? "";
        return historic.Length > 0 && !UcmContactWriteRules.SameNumber(historic, call.Peer)
            ? historic : "";
    }

    private void ShowCallRows()
    {
        if (!showCalls) return;
        if (!grid.Columns.Contains("directionIcon") ||
            grid.Columns["directionIcon"] is not DataGridViewImageColumn ||
            !grid.Columns.Contains("saveContact") ||
            grid.Columns["saveContact"] is not DataGridViewButtonColumn ||
            !grid.Columns.Contains("call") || grid.Columns["call"] is not DataGridViewImageColumn)
            SetupGrid(true);
        var query = search.Text.Trim();
        var filtered = calls.Where(c => MatchesDirection(c, selectedCallFilter))
            .Where(c =>
            {
                var ldapName = CdrDisplayName(c, CdrMatch(c.Peer));
                return query.Length == 0 ||
                    (c.Peer + " " + ldapName + " " + c.Direction + " " + c.Duration + " " +
                     c.AnsweredBy + " " + c.Timestamp).Contains(query, StringComparison.OrdinalIgnoreCase);
            })
            .OrderByDescending(c => SortTime(c.Timestamp))
            .ThenByDescending(c => c.Timestamp, StringComparer.Ordinal).ToList();
        grid.Rows.Clear();
        foreach (var c in filtered)
        {
            var stamp = SortTime(c.Timestamp);
            string date = stamp == DateTime.MinValue ? c.Timestamp : stamp.ToString("dd/MM/yyyy");
            string hour = stamp == DateTime.MinValue ? "—" : stamp.ToString("HH:mm");
            string typeLabel = c.Direction == "Uscita" ? "Effettuata" :
                c.Direction == "Entrata" ? "Risposta" : "Persa";
            var found = CdrMatch(c.Peer);
            int rowIndex = grid.Rows.Add();
            var row = grid.Rows[rowIndex];
            row.Cells["directionIcon"].Value = DirectionImage(c);
            row.Cells["date"].Value = date;
            row.Cells["hour"].Value = hour;
            row.Cells["duration"].Value = c.Duration;
            row.Cells["peer"].Value = c.Peer;
            row.Cells["ldapName"].Value = CdrDisplayName(c, found);
            if (found?.Ambiguous == true)
                row.Cells["ldapName"].ToolTipText =
                    "Più contatti in rubrica: mostrato il primo secondo la priorità.";
            else if (found?.Contact is null && !string.IsNullOrWhiteSpace(c.PeerName))
                row.Cells["ldapName"].ToolTipText =
                    "Nome storico CDR: non conferma che il contatto esista ancora nella rubrica.";
            if (config.EnableLdapContactEditing && IsExternal(c.Peer))
            {
                var action = LdapDestinationRules.Action(found, config.LdapWritePhonebookDn);
                if (action == CdrLdapAction.Edit)
                {
                    row.Cells["saveContact"].Value = "✎";
                    row.Cells["saveContact"].Style.Font = editFont;
                    row.Cells["saveContact"].Style.ForeColor = Color.FromArgb(26, 90, 156);
                    row.Cells["saveContact"].ToolTipText = found!.DestinationAmbiguous
                        ? "Più contatti nella destinazione: verifica prima di modificare"
                        : "Modifica contatto verificato nella rubrica di destinazione";
                }
                else if (action == CdrLdapAction.Add)
                {
                    row.Cells["saveContact"].Value = "+";
                    row.Cells["saveContact"].Style.Font = plusFont;
                    row.Cells["saveContact"].Style.ForeColor = Color.FromArgb(24, 149, 67);
                    row.Cells["saveContact"].ToolTipText =
                        "Assente nella rubrica di destinazione: verifica nuovamente prima dell'aggiunta";
                }
                else if (action == CdrLdapAction.Verify)
                {
                    row.Cells["saveContact"].Value = "?";
                    row.Cells["saveContact"].Style.Font = editFont;
                    row.Cells["saveContact"].Style.ForeColor = Color.FromArgb(95, 111, 135);
                    row.Cells["saveContact"].ToolTipText = "Verifica la presenza nella rubrica configurata";
                }
                else
                {
                    row.Cells["saveContact"] = new DataGridViewTextBoxCell { Value = "" };
                    row.Cells["saveContact"].ToolTipText =
                        "Contatto esistente nella destinazione ma non modificabile tramite API UCM";
                }
            }
            else row.Cells["saveContact"] = new DataGridViewTextBoxCell { Value = "" };
            row.Cells["call"].Value = callBlue;
            row.Cells["directionIcon"].ToolTipText = typeLabel;
            row.Cells["duration"].ToolTipText = "Durata conversazione (billsec)";
            row.Cells["call"].ToolTipText = "Richiama " + c.Peer;
            row.Tag = c;
        }
        recordCount.Text = $"Registro: {grid.Rows.Count} record";
        status.Text = $"{filtered.Count} chiamate · interno {config.Extension}" +
            (cdrDirectoryAvailable ? "" : " · LDAP: usa ? per verifica mirata");
    }

    // All writes are explicitly confirmed and validated against real LDAP.
    // No background write occurs during CDR synchronization or AMI monitoring.
    private async Task SaveCdrContactAsync(CallEntry call)
    {
        if (!config.EnableLdapContactEditing) return; // Defensive check: never write while disabled.
        if (Interlocked.CompareExchange(ref saveContactInProgress, 1, 0) != 0)
        { status.Text = "Operazione LDAP già in corso."; return; }
        try
        {
            var dn = UcmContactWriteRules.ValidateDestinationDn(config.LdapWritePhonebookDn);
            var number = UcmContactWriteRules.ValidateNumber(call.Peer, config.InternalExtensionMaxLength);
            var initial = UcmContactDetails.New(dn, number, call.PeerName);
            using var dialog = new AddContactForm(initial, config.InternalExtensionMaxLength, editing: false) { TopMost = true };
            if (!Visible) Show();
            BringToFront();
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            var details = dialog.Result;
            if (MessageBox.Show(this, $"Aggiungere '{details.CallerIdName}' alla rubrica {dn}?",
                "Conferma nuovo contatto", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            status.Text = "Verifico i duplicati nella rubrica di destinazione...";
            await ldap.VerifyWritableDestinationAsync(config);
            foreach (var n in ContactNumbers(details))
            {
                if (await ldap.ContainsNumberInDestinationAsync(config, n))
                    throw new InvalidOperationException("Uno dei numeri è già presente nella rubrica " +
                        config.LdapWritePhonebookDn + ". Utilizzare Modifica sul contatto esistente; " +
                        "nessuna aggiunta effettuata.");
            }
            status.Text = "Invio nuovo contatto alla UCM...";
            var outcome = await new UcmContactsApiClient().SaveDetailsAsync(config, details, null);
            bool refreshed = !outcome.ApplyAcknowledged ||
                await RefreshAfterMutationSafelyAsync(null, details);
            if (!refreshed)
                DiagnosticLog.Warn("UCM_LDAP_WRITE_SAVED_REFRESH_PENDING", "operation=addContact");
            // A timeout in applyChanges does not undo an accepted addContact.
            status.Text = outcome.ApplyTimedOut
                ? "Contatto accettato; esito applyChanges non confermato. Verifica sulla UCM."
                : "Contatto accettato dalla UCM.";
            MessageBox.Show(this, outcome.ApplyTimedOut
                ? "La UCM ha accettato il contatto, ma applyChanges non ha risposto entro il timeout. " +
                  "Verificare prima di tentare di inserirlo nuovamente."
                : "Contatto aggiunto; verificare i dati sulla rubrica LDAP della UCM.",
                "Rubrica LDAP", MessageBoxButtons.OK,
                outcome.ApplyTimedOut ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }
        catch (Exception e)
        {
            DiagnosticLog.Error("UCM_LDAP_UI_ADD_FAILED", e);
            status.Text = "Contatto non confermato: " + e.Message;
            MessageBox.Show(this, e.Message, "Aggiunta contatto LDAP", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { Interlocked.Exchange(ref saveContactInProgress, 0); }
    }

    private static IEnumerable<string> ContactNumbers(UcmContactDetails d) =>
        new[] { d.AccountNumber, d.MobileNumber, d.HomeNumber, d.FamilyNumber, d.Fax }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .DistinctBy(x => PhoneNumbers.SearchKey(x));

    // After a UCM write the original AccountNumber may have been renamed. Drop
    // old AND new LDAP keys so the CDR / mini-popup cannot keep a stale name.
    // The fresh reads are bounded and safe to repeat; the API write is not.
    private async Task<bool> RefreshAfterContactMutationAsync(UcmContactDetails? before, UcmContactDetails? after)
    {
        var changedNumbers = Enumerable.Empty<string>()
            .Concat(before is null ? [] : ContactNumbers(before))
            .Concat(after is null ? [] : ContactNumbers(after))
            .Where(x => PhoneNumbers.SearchKey(x).Length > 0)
            .DistinctBy(PhoneNumbers.SearchKey)
            .ToArray();
        var changedKeys = changedNumbers.Select(PhoneNumbers.SearchKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var key in changedKeys)
        {
            cdrDirectory.Remove(key);
            unverifiedLdapKeys.Add(key);
            liveLookupAttempted.Remove(key);
            liveLookupFailed.Remove(key);
        }
        // The Rubrica-tab browse list is also a name fallback for live calls.
        // Invalidate it immediately, even when that tab is not currently open.
        contacts.RemoveAll(c => changedKeys.Contains(PhoneNumbers.SearchKey(c.Number)));
        var numbersToResolve = calls.Select(c => c.Peer).Concat(changedNumbers);
        if (currentLiveCall is { } live) numbersToResolve = numbersToResolve.Append(live.Peer);
        bool namesUpdated = await RefreshCdrDirectoryAsync(numbersToResolve, force: true, afterMutation: true);
        if (!showCalls)
            await LoadContactsAsync(afterMutation: true);
        else
            ShowCallRows();
        if (currentLiveCall is not null && namesUpdated) RenderLiveCall();
        DiagnosticLog.Info("UCM_LDAP_CACHE_REFRESH", "lookup_ok=" + namesUpdated +
            "; impacted_numbers=" + changedKeys.Count);
        return namesUpdated;
    }

    // Post-write read recovery: the HTTPS write is NEVER repeated. LDAP may
    // temporarily return NoSuchObject while the UCM rebuilds its address books.
    private async Task<bool> RefreshAfterMutationSafelyAsync(
        UcmContactDetails? before, UcmContactDetails? after)
    {
        int retryTicket = ++ldapMutationRefreshGeneration;
        try
        {
            if (await RefreshAfterContactMutationAsync(before, after)) return true;
        }
        catch (Exception error)
        {
            DiagnosticLog.Warn("UCM_LDAP_REFRESH_DEFERRED", error.GetType().Name);
        }
        _ = RecoverLdapAfterMutationAsync(config, before, after, retryTicket);
        return false;
    }

    private async Task RecoverLdapAfterMutationAsync(AppConfig cfg,
        UcmContactDetails? before, UcmContactDetails? after, int ticket)
    {
        // Bounded delayed READ-ONLY retries, even when the form is hidden.
        // The next contact mutation invalidates earlier pending recovery tasks.
        foreach (int delayMs in new[] { 3000, 9000, 20000 })
        {
            try
            {
                await Task.Delay(delayMs);
                if (IsDisposed || !ReferenceEquals(config, cfg) ||
                    ticket != ldapMutationRefreshGeneration) return;
                if (await RefreshAfterContactMutationAsync(before, after))
                {
                    DiagnosticLog.Info("UCM_LDAP_REFRESH_RECOVERED", "scope=read_only");
                    return;
                }
            }
            catch (ObjectDisposedException) { return; }
            catch (Exception error)
            {
                DiagnosticLog.Warn("UCM_LDAP_REFRESH_RETRY_PENDING", error.GetType().Name);
            }
        }
        if (!IsDisposed && ticket == ldapMutationRefreshGeneration)
            DiagnosticLog.Warn("UCM_LDAP_REFRESH_MANUAL_NEEDED", "writes_retried=false");
    }

    private async Task EditContactAsync(UcmContactDetails selection)
    {
        if (!config.EnableLdapContactEditing) return; // Defensive check for stale UI events.
        if (Interlocked.CompareExchange(ref saveContactInProgress, 1, 0) != 0)
        { status.Text = "Operazione LDAP già in corso."; return; }
        try
        {
            // Re-read all attributes by ORIGINAL identity, not from the truncated UI cache.
            var original = await ldap.ReadContactAsync(config, selection.PhonebookDn, selection.AccountNumber);
            using var dialog = new AddContactForm(original, config.InternalExtensionMaxLength, editing: true)
                { TopMost = true };
            if (!Visible) Show();
            BringToFront();
            var editorResult = dialog.ShowDialog(this);
            if (editorResult == DialogResult.No)
            {
                await DeleteContactCoreAsync(original);
                return;
            }
            if (editorResult != DialogResult.OK) return;
            var edited = dialog.Result;
            if (MessageBox.Show(this,
                $"Modificare '{original.DisplayName}' nella rubrica {original.PhonebookDn}? " +
                "I dati precedenti verranno sostituiti.",
                "Conferma modifica contatto", MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes) return;
            status.Text = "Verifico modifiche e possibili duplicati...";
            // An existing number already shared by several LDAP records may remain
            // unchanged. Only newly introduced numbers need a conflict check.
            // This permits editing the FIRST selected contact without silently
            // changing any of the other matches.
            var originalNumbers = ContactNumbers(original)
                .Select(PhoneNumbers.SearchKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var n in ContactNumbers(edited))
            {
                if (originalNumbers.Contains(PhoneNumbers.SearchKey(n))) continue;
                if (await ldap.HasOtherContactWithNumberAsync(config, n, original.PhonebookDn, original.AccountNumber))
                    throw new InvalidOperationException("Un nuovo numero appartiene già a un altro contatto LDAP. " +
                        "La modifica è stata annullata.");
            }
            var outcome = await new UcmContactsApiClient().SaveDetailsAsync(config, edited, original);
            bool refreshed = !outcome.ApplyAcknowledged ||
                await RefreshAfterMutationSafelyAsync(original, edited);
            if (!refreshed)
                DiagnosticLog.Warn("UCM_LDAP_WRITE_SAVED_REFRESH_PENDING", "operation=updateContact");
            status.Text = outcome.ApplyTimedOut ? "Modifica accettata, applicazione non ancora confermata." :
                "Modifica inviata alla UCM.";
            MessageBox.Show(this, outcome.ApplyTimedOut
                ? "updateContact accettato, ma applyChanges non ha confermato la conclusione. " +
                  "Verificare il contatto UCM prima di ripetere la modifica."
                : "Modifica accettata e applicata dalla UCM.",
                "Contatto LDAP", MessageBoxButtons.OK,
                outcome.ApplyTimedOut ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }
        catch (Exception e)
        {
            if (e is DirectoryOperationException ||
                e.Message.Contains("non più presente", StringComparison.OrdinalIgnoreCase))
            {
                try { await RefreshAfterContactMutationAsync(selection, null); }
                catch (Exception refreshError) { DiagnosticLog.Warn("UCM_LDAP_REFRESH_AFTER_STALE_EDIT", refreshError.GetType().Name); }
            }
            DiagnosticLog.Error("UCM_LDAP_UI_EDIT_FAILED", e);
            status.Text = "Modifica non confermata: " + e.Message;
            MessageBox.Show(this, e.Message, "Modifica contatto LDAP", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { Interlocked.Exchange(ref saveContactInProgress, 0); }
    }

    private async Task DeleteContactAsync(UcmContactDetails selection)
    {
        if (!config.EnableLdapContactEditing) return;
        if (Interlocked.CompareExchange(ref saveContactInProgress, 1, 0) != 0)
        { status.Text = "Operazione LDAP già in corso."; return; }
        try { await DeleteContactCoreAsync(selection); }
        finally { Interlocked.Exchange(ref saveContactInProgress, 0); }
    }

    private async Task DeleteContactCoreAsync(UcmContactDetails selection)
    {
        // Called either with an already-held operation guard from the editor,
        // or with the guard held by DeleteContactAsync from the contact grid.
        if (!config.EnableLdapContactEditing) return;
        try
        {
            UcmContactWriteRules.ValidateDestinationDn(selection.PhonebookDn);
            var original = await ldap.ReadContactAsync(config, selection.PhonebookDn, selection.AccountNumber);
            if (!original.CanEdit)
                throw new InvalidOperationException("Contatto non cancellabile: identità LDAP non valida.");
            if (!Visible) Show();
            BringToFront();
            string label = string.IsNullOrWhiteSpace(original.DisplayName) ? "(senza nome)" : original.DisplayName;
            if (MessageBox.Show(this,
                $"Eliminare definitivamente questo contatto LDAP?\n\n" +
                $"Nome: {label}\nAccount Number: {original.AccountNumber}\n" +
                $"Rubrica: {original.PhonebookDn}\n\n" +
                "La cancellazione non è annullabile e può rimuovere eventuali riferimenti nei gruppi. " +
                "Se la rubrica è sincronizzata, il contatto potrebbe essere ricreato.\n\nContinuare?",
                "Conferma eliminazione contatto", MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

            // Re-read immediately before deletion: never delete an identity that
            // changed or disappeared while the confirmation window was open.
            var latest = await ldap.ReadContactAsync(config, original.PhonebookDn, original.AccountNumber);
            if (latest.DisplayName != original.DisplayName ||
                latest.HomeNumber != original.HomeNumber ||
                latest.MobileNumber != original.MobileNumber ||
                latest.FamilyNumber != original.FamilyNumber ||
                latest.Fax != original.Fax)
                throw new InvalidOperationException("Il contatto è cambiato dopo la conferma. " +
                    "Riaprirlo e verificare prima di eliminarlo.");
            status.Text = "Eliminazione contatto LDAP in corso...";
            var outcome = await new UcmContactsApiClient().DeleteContactAsync(config, latest);
            bool refreshed = !outcome.ApplyAcknowledged ||
                await RefreshAfterMutationSafelyAsync(latest, null);
            if (!refreshed)
                DiagnosticLog.Warn("UCM_LDAP_WRITE_SAVED_REFRESH_PENDING", "operation=deleteContact");
            status.Text = outcome.ApplyTimedOut
                ? "deleteContact accettato; applicazione non confermata. Verificare sulla UCM."
                : "Eliminazione accettata dalla UCM.";
            MessageBox.Show(this, outcome.ApplyTimedOut
                ? "La UCM ha accettato deleteContact, ma applyChanges non ha risposto entro 90 secondi. " +
                  "L'esito è incerto: controllare sulla UCM prima di ritentare."
                : "Eliminazione accettata e applicazione confermata dalla UCM.",
                "Eliminazione contatto LDAP", MessageBoxButtons.OK,
                outcome.ApplyTimedOut ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }
        catch (Exception e)
        {
            DiagnosticLog.Error("UCM_LDAP_UI_DELETE_FAILED", e);
            status.Text = "Eliminazione non confermata: " + e.Message;
            MessageBox.Show(this, e.Message, "Eliminazione contatto LDAP",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // Regression: a stale LDAP name must disappear after a forced refresh
    // invalidates its lookup (until a fresh authoritative search succeeds).
    public static bool SelfTestStaleLdapInvalidation()
    {
        using var ui = new PopupForm(new AppConfig { InternalExtensionMaxLength = 4,
            EnableLdapContactEditing = true }, () => false);
        const string number = "3479784437";
        var key = PhoneNumbers.SearchKey(number);
        ui.calls.Add(new CallEntry("test-1", "2026-11-08 08:00", "Uscita", number, "00:18", "ANSWERED"));
        ui.cdrDirectory[key] = new LdapContactMatch(new UcmContactDetails {
            PhonebookDn = "ou=medialink,dc=pbx,dc=com", AccountNumber = number,
            CallerIdName = "OLD LDAP NAME" });
        ui.cdrInitialized = true;
        ui.SwitchTabAsync(true).GetAwaiter().GetResult();
        if (ui.grid.Rows.Count != 1 ||
            !Equals(ui.grid.Rows[0].Cells["ldapName"].Value, "OLD LDAP NAME")) return false;
        ui.cdrDirectory.Remove(key);
        ui.unverifiedLdapKeys.Add(key);
        ui.contacts.Clear();
        ui.ShowCallRows();
        return string.IsNullOrWhiteSpace(ui.grid.Rows[0].Cells["ldapName"].Value?.ToString()) &&
            Equals(ui.grid.Rows[0].Cells["saveContact"].Value, "?") &&
            ui.ResolvedPeerName(number).Length == 0;
    }

    // Offline window chrome regression; all checks are in-memory WinForms properties.
    // No UCM, SIP, LDAP or credentials required.
    public static bool SelfTestWindowChrome()
    {
        using var ui = new PopupForm(new AppConfig(), () => false);
        return ui.FormBorderStyle == FormBorderStyle.None &&
            !ui.ControlBox && !ui.ShowIcon && ui.chromeHeader is not null &&
            ui.chromeRestore.Parent is Panel &&
            ReferenceEquals(ui.chromeRestore.Parent, ui.chromeClose.Parent) &&
            ReferenceEquals(ui.chromeClose.Parent!.Parent, ui.chromeHeader) &&
            ui.headerLogo.Image is not null &&
            ui.headerLogo.Width == 50 && ui.headerLogo.Height == 50 &&
            ui.liveInfo.Font.Bold && ui.liveInfo.ForeColor == Color.Black &&
            ui.liveName.Font.Bold && ui.liveName.ForeColor == Color.FromArgb(16, 82, 178) &&
            ui.toggleKeypad.Image is not null && ui.toggleKeypad.Text == "" &&
            ui.title.Left > ui.headerLogo.Right &&
            TrayLifecycleRules.ShouldHidePanel(CloseReason.UserClosing);
    }

    // Offline, Windows-only UI smoke test; no LDAP, CDR, API or SIP actions.
    public static bool SelfTestCdrGrid()
    {
        // Supply the selected writable DN; otherwise a verified remote contact
        // legitimately cannot show the pencil and the test is a false failure.
        using var ui = new PopupForm(new AppConfig { InternalExtensionMaxLength = 4,
            EnableLdapContactEditing = true,
            LdapWritePhonebookDn = "ou=medialink,dc=pbx,dc=com" }, () => false);
        ui.calls.Add(new CallEntry("test-1", "2026-11-07 15:00", "Entrata", "331234567", "00:12", "ANSWERED"));
        ui.calls.Add(new CallEntry("test-2", "2026-11-07 14:00", "Uscita", "0444123456", "00:30", "ANSWERED"));
        ui.calls.Add(new CallEntry("test-3", "2026-11-07 13:00", "Persa", "202", "00:00", "NO ANSWER"));
        ui.cdrDirectory[PhoneNumbers.SearchKey("331234567")] =
            new LdapContactMatch(null, DestinationVerified:true);
        var checkedContact = new UcmContactDetails { AccountNumber = "0444123456",
            PhonebookDn = "ou=medialink,dc=pbx,dc=com", CallerIdName = "Cliente LDAP" };
        ui.cdrDirectory[PhoneNumbers.SearchKey("0444123456")] =
            new LdapContactMatch(checkedContact, DestinationVerified:true, DestinationContact:checkedContact);
        ui.cdrDirectory[PhoneNumbers.SearchKey("202")] =
            new LdapContactMatch(null, DestinationVerified:true);
        ui.cdrDirectoryAvailable = true;
        ui.cdrInitialized = true;
        ui.SwitchTabAsync(true).GetAwaiter().GetResult();
        if (ui.gridDataErrorCount != 0 || ui.grid.Rows.Count != 3) return false;
        if (ui.grid.Columns["directionIcon"] is not DataGridViewImageColumn ||
            ui.grid.Columns["saveContact"] is not DataGridViewButtonColumn ||
            ui.grid.Columns["call"] is not DataGridViewImageColumn ||
            ui.grid.Columns["kind"] is not null ||
            ui.grid.Columns["answeredBy"] is not null || // Esito removed: no longer useful in CDR view.
            ui.grid.Columns["date"] is null || ui.grid.Columns["hour"] is null ||
            ui.grid.Columns["duration"] is null || ui.grid.Columns["ldapName"] is null ||
            ui.grid.Columns["ldapName"]!.AutoSizeMode != DataGridViewAutoSizeColumnMode.Fill ||
            ui.grid.Columns["date"]!.AutoSizeMode != DataGridViewAutoSizeColumnMode.None ||
            ui.grid.Columns["hour"]!.AutoSizeMode != DataGridViewAutoSizeColumnMode.None ||
            ui.grid.Columns["duration"]!.AutoSizeMode != DataGridViewAutoSizeColumnMode.None ||
            ui.grid.Columns["peer"]!.AutoSizeMode != DataGridViewAutoSizeColumnMode.None ||
            ui.grid.Columns["peer"]!.Width != 94 ||
            ui.grid.Columns["ldapName"]!.HeaderText != "Nome" ||
            ui.grid.Columns["ldapName"]!.DisplayIndex >= ui.grid.Columns["peer"]!.DisplayIndex ||
            ui.grid.Columns["peer"]!.DisplayIndex >= ui.grid.Columns["saveContact"]!.DisplayIndex ||
            ui.grid.Columns["saveContact"]!.DisplayIndex >= ui.grid.Columns["call"]!.DisplayIndex)
            return false;
        var rows = ui.grid.Rows.Cast<DataGridViewRow>()
            .ToDictionary(r => ((CallEntry)r.Tag!).Peer, StringComparer.OrdinalIgnoreCase);
        return Equals(rows["331234567"].Cells["saveContact"].Value, "+") &&
            Equals(rows["0444123456"].Cells["saveContact"].Value, "✎") &&
            Equals(rows["0444123456"].Cells["ldapName"].Value, "Cliente LDAP") &&
            rows["202"].Cells["saveContact"] is DataGridViewTextBoxCell &&
            rows["331234567"].Cells["directionIcon"].FormattedValue is Image &&
            rows["331234567"].Cells["call"].FormattedValue is Image &&
            ui.gridDataErrorCount == 0;
    }

    internal static bool SelfTestContactSorting()
    {
        if (!ContactSortRules.SelfTest()) return false;
        using var ui = new PopupForm(new AppConfig(), () => false);
        ui.SetupGrid(false);
        if (ui.grid.Columns["name"]?.SortMode != DataGridViewColumnSortMode.Programmatic ||
            ui.grid.Columns["phone"]?.SortMode != DataGridViewColumnSortMode.Programmatic ||
            ui.grid.Columns["call"]?.SortMode != DataGridViewColumnSortMode.NotSortable ||
            ui.grid.Columns["editContact"]?.SortMode != DataGridViewColumnSortMode.NotSortable)
            return false;
        var a = new Contact("Zeta", "100", "", "ou=medialink,dc=pbx,dc=com", "100");
        var b = new Contact("Alfa", "200", "", "ou=medialink,dc=pbx,dc=com", "200");
        ui.contacts = [a, b];
        ui.contactSortColumn = "name";
        ui.contactSortAscending = true;
        ui.RenderContactRows();
        if (!ReferenceEquals(ui.grid.Rows[0].Tag, b) ||
            ui.grid.Columns["name"]?.HeaderCell.SortGlyphDirection != SortOrder.Ascending)
            return false;
        ui.contactSortAscending = false;
        ui.RenderContactRows();
        if (!ReferenceEquals(ui.grid.Rows[0].Tag, a) ||
            ui.grid.Columns["name"]?.HeaderCell.SortGlyphDirection != SortOrder.Descending)
            return false;
        ui.contactSortColumn = "phone";
        ui.contactSortAscending = false;
        ui.RenderContactRows();
        return ReferenceEquals(ui.grid.Rows[0].Tag, b) &&
            ui.grid.Columns["phone"]?.HeaderCell.SortGlyphDirection == SortOrder.Descending &&
            ui.grid.Columns["name"]?.HeaderCell.SortGlyphDirection == SortOrder.None;
    }

    // A name from a different phonebook or old CDR MUST NOT enable the pencil.
    // This is the regression reported for 3479784437 after removal from medialink.
    public static bool SelfTestCdrHistoricNameNotEditable()
    {
        const string target = "ou=medialink,dc=pbx,dc=com";
        const string number = "3479784437";
        using var ui = new PopupForm(new AppConfig { InternalExtensionMaxLength = 4,
            EnableLdapContactEditing = true, LdapWritePhonebookDn = target }, () => false);
        ui.calls.Add(new CallEntry("test-history", "2026-11-08 08:00", "Uscita",
            number, "00:16", "ANSWERED", "ANTONIO VACCARO"));
        var oldFromOtherBook = new UcmContactDetails {
            AccountNumber = number, CallerIdName = "ANTONIO VACCARO",
            PhonebookDn = "ou=contacts,dc=pbx,dc=com" };
        ui.cdrDirectory[PhoneNumbers.SearchKey(number)] =
            LdapSelectionRules.ChooseWithDestination([oldFromOtherBook], target, true);
        ui.cdrInitialized = true;
        ui.SwitchTabAsync(true).GetAwaiter().GetResult();
        if (ui.grid.Columns["ldapName"]?.HeaderText != "Nome" || ui.grid.Rows.Count != 1)
            return false;
        var row = ui.grid.Rows[0];
        if (!Equals(row.Cells["ldapName"].Value, "ANTONIO VACCARO") ||
            !Equals(row.Cells["saveContact"].Value, "+")) return false;
        ui.cdrDirectory[PhoneNumbers.SearchKey(number)] = new LdapContactMatch(null,
            DestinationVerified:true);
        ui.ShowCallRows();
        row = ui.grid.Rows[0];
        if (!Equals(row.Cells["ldapName"].Value, "ANTONIO VACCARO") ||
            !Equals(row.Cells["saveContact"].Value, "+")) return false;
        ui.cdrDirectory[PhoneNumbers.SearchKey(number)] = new LdapContactMatch(oldFromOtherBook);
        ui.ShowCallRows();
        return Equals(ui.grid.Rows[0].Cells["saveContact"].Value, "?");
    }

    // Regression for a freshly-enabled LDAP editing checkbox while the CDR
    // lookup is still incomplete: the action must never vanish permanently.
    // The '?' button forces a safe per-number verification before add/edit.
    public static bool SelfTestCdrLdapUnknown()
    {
        using var ui = new PopupForm(new AppConfig {
            InternalExtensionMaxLength = 4, EnableLdapContactEditing = true }, () => false);
        ui.calls.Add(new CallEntry("test-1", "2026-11-08 07:00", "Uscita",
            "3479784437", "00:30", "ANSWERED"));
        ui.calls.Add(new CallEntry("test-2", "2026-11-08 07:00", "Entrata",
            "62", "00:12", "ANSWERED"));
        ui.cdrInitialized = true;
        ui.SwitchTabAsync(true).GetAwaiter().GetResult();
        var rows = ui.grid.Rows.Cast<DataGridViewRow>()
            .ToDictionary(r => ((CallEntry)r.Tag!).Peer, StringComparer.OrdinalIgnoreCase);
        if (!Equals(rows["3479784437"].Cells["saveContact"].Value, "?")) return false;
        if (rows["62"].Cells["saveContact"] is not DataGridViewTextBoxCell) return false;
        ui.cdrDirectory[PhoneNumbers.SearchKey("3479784437")] =
            new LdapContactMatch(null, DestinationVerified:true);
        ui.ShowCallRows();
        rows = ui.grid.Rows.Cast<DataGridViewRow>()
            .ToDictionary(r => ((CallEntry)r.Tag!).Peer, StringComparer.OrdinalIgnoreCase);
        return Equals(rows["3479784437"].Cells["saveContact"].Value, "+") &&
               rows["62"].Cells["saveContact"] is DataGridViewTextBoxCell;
    }

    // Verifies no write icons while LDAP editing is disabled, but names remain readable.
    public static bool SelfTestLdapEditToggle()
    {
        using var ui = new PopupForm(new AppConfig { InternalExtensionMaxLength = 4,
            EnableLdapContactEditing = false }, () => false);
        ui.calls.Add(new CallEntry("test-1", "2026-11-07 15:00", "Entrata", "331234567", "00:12", "ANSWERED"));
        ui.calls.Add(new CallEntry("test-2", "2026-11-07 14:00", "Uscita", "0444123456", "00:30", "ANSWERED"));
        ui.cdrDirectory[PhoneNumbers.SearchKey("331234567")] =
            new LdapContactMatch(null, DestinationVerified:true);
        var checkedContact = new UcmContactDetails { AccountNumber = "0444123456",
            PhonebookDn = "ou=medialink,dc=pbx,dc=com", CallerIdName = "Cliente LDAP" };
        ui.cdrDirectory[PhoneNumbers.SearchKey("0444123456")] =
            new LdapContactMatch(checkedContact, DestinationVerified:true, DestinationContact:checkedContact);
        ui.cdrDirectoryAvailable = true;
        ui.cdrInitialized = true;
        ui.SwitchTabAsync(true).GetAwaiter().GetResult();
        var rows = ui.grid.Rows.Cast<DataGridViewRow>()
            .ToDictionary(r => ((CallEntry)r.Tag!).Peer, StringComparer.OrdinalIgnoreCase);
        return ui.gridDataErrorCount == 0 &&
            Equals(rows["0444123456"].Cells["ldapName"].Value, "Cliente LDAP") &&
            rows.Values.All(r => r.Cells["saveContact"] is DataGridViewTextBoxCell) &&
            ui.grid.Columns["deleteContact"] is null &&
            new AppConfig().EnableLdapContactEditing == false;
    }

    // UI-only test: no network calls, no contact deletions.
    public static bool SelfTestLdapDeleteUi()
    {
        using var ui = new PopupForm(new AppConfig {
            InternalExtensionMaxLength = 4, EnableLdapContactEditing = true }, () => false);
        ui.contacts = [
            new Contact("Cliente", "0444123456", "Telefono", "ou=medialink,dc=pbx,dc=com", "0444123456"),
            new Contact("Interno", "62", "Telefono", "ou=pbx,dc=pbx,dc=com", "62")
        ];
        ui.SetupGrid(false);
        foreach (var c in ui.contacts)
        {
            var index = ui.grid.Rows.Add();
            var row = ui.grid.Rows[index];
            row.Tag = c;
            if (c.PhonebookDn == "ou=medialink,dc=pbx,dc=com")
                row.Cells["deleteContact"].Value = ui.deleteIcon;
            else row.Cells["deleteContact"] = new DataGridViewTextBoxCell { Value = "" };
        }
        return ui.grid.Columns["deleteContact"] is DataGridViewImageColumn &&
            ui.grid.Rows[0].Cells["deleteContact"] is DataGridViewImageCell &&
            ui.grid.Rows[1].Cells["deleteContact"] is DataGridViewTextBoxCell &&
            UcmContactWriteRules.ValidateDestinationDn("ou=medialink,dc=pbx,dc=com") == "ou=medialink,dc=pbx,dc=com";
    }

    /// <summary>
    /// External URI/hotkey dial handoff; the same confirmation preference applies
    /// to both sources. Validation and busy-state checks are always enforced.
    /// The existing DialAsync path is reused without changes.
    /// </summary>
    internal async Task PromptExternalDialAsync(string number)
    {
        if (!ExternalDialUri.TryValidateNumber(number, out var canonical))
        { DiagnosticLog.Warn("EXTERNAL_DIAL_REJECTED", "reason=invalid_number"); return; }
        if (Interlocked.CompareExchange(ref externalDialogInProgress, 1, 0) != 0)
        { DiagnosticLog.Warn("EXTERNAL_DIAL_REJECTED", "reason=confirmation_busy"); return; }
        try
        {
            if (IsDisposed) return;
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            PositionNearTray();
            Show(); Activate(); BringToFront();
            if (currentLiveCall is not null || Volatile.Read(ref dialInProgress) != 0)
            {
                MessageBox.Show(this, "È già presente una chiamata in corso o in preparazione. " +
                    "Terminarla prima di effettuare un nuovo Click-to-Dial.", "Click-to-Dial esterno",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!DialNumberRules.TryPrepare(canonical, config, out string prepared, out _))
            {
                MessageBox.Show(this, "Numero non valido dopo l'applicazione dei prefissi di composizione.",
                    "Click-to-Dial", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            dialNumber.Text = canonical;
            string name = ResolvedPeerName(canonical);
            string summary = string.IsNullOrWhiteSpace(name) ? "" : "\nContatto: " + name;
            if (config.ExternalDialRequireConfirmation)
            {
                var answer = MessageBox.Show(this,
                    "Richiesta di chiamata:\n\n" + canonical + summary +
                    "\nNumero da comporre: " + prepared +
                    "\n\nAvviare la chiamata dall'interno " + config.Extension + "?",
                    "Conferma Click-to-Dial", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2);
                if (answer != DialogResult.Yes)
                {
                    DiagnosticLog.Info("EXTERNAL_DIAL_CANCELLED");
                    return;
                }
                DiagnosticLog.Info("EXTERNAL_DIAL_CONFIRMED", "approval=manual");
            }
            else DiagnosticLog.Info("EXTERNAL_DIAL_AUTOMATIC", "confirmation=disabled_by_user");
            await DialAsync(canonical);
        }
        finally { Interlocked.Exchange(ref externalDialogInProgress, 0); }
    }

    private async Task DialAsync(string number)
    {
        if (!DemoLicenseGate.IsAuthorized(out var licenseReason))
        {
            status.Text = "CTI bloccato: " + licenseReason;
            return;
        }
        // Service pickup codes are never passed to numeric-only E.164 normalization.
        // Accept only a configured prefix + one explicitly monitored extension.
        string target;
        if (BlfLogic.IsPickup(number, config)) target = number;
        else if (!DialNumberRules.TryPrepare(number, config, out target, out _))
        { status.Text = "Numero non valido per la composizione."; return; }
        // The user explicitly requested one-click dialing. Avoid accidental repeated
        // AMI Originate requests while the first call command is still in flight.
        if (Interlocked.CompareExchange(ref dialInProgress, 1, 0) != 0)
        { status.Text = "Invio chiamata gia in corso..."; return; }
        try
        {
            status.Text = config.PhoneType switch
            {
                "Avaya" => "Invio chiamata al Gateway Avaya...",
                "Snom" => "Avvio chiamata Snom HTTP Digest...",
                "Yealink" => "Avvio chiamata Yealink (web diretto)...",
                _ => "Avvio immediato chiamata AMI..."
            };
            if (config.PhoneType == "Avaya")
            {
                await AvayaGatewayClient.DialAsync(config, target);
                status.Text = $"Richiesta accettata dal Gateway Avaya: {target}.";
                return;
            }
            OutgoingDialRequested?.Invoke(target);
            try
            {
                if (config.PhoneType == "Snom")
                {
                    await phone.DialSnomAsync(config, target);
                    DiagnosticLog.Info("SNOM_DIAL_REQUEST_OK", "method=http_digest; awaiting_ami_state=true");
                    status.Text = $"Chiamata Snom avviata: {target} · attendo stato AMI.";
                }
                else if (config.PhoneType == "Yealink")
                {
                    await phone.DialYealinkAsync(config, target);
                    DiagnosticLog.Info("YEALINK_DIAL_REQUEST_OK", "method=direct_action_uri; awaiting_ami_state=true");
                    status.Text = $"Chiamata Yealink inviata: {target} · attendo stato AMI.";
                }
                else
                {
                    await ami.OriginateAsync(config, target);
                    DiagnosticLog.Info("AMI_ORIGINATE_REQUEST_OK", "autoanswer=" + config.AutoAnswerClickToDial + "; display_callee=true");
                    status.Text = config.AutoAnswerClickToDial
                        ? $"Chiamata avviata: {target} · AutoAnswer inviato; attendo stato AMI."
                        : $"Chiamata avviata: {target} · Rispondi sul telefono.";
                }
            }
            catch { OutgoingDialFailed?.Invoke(target); throw; }
        }
        catch (Exception e)
        {
            DiagnosticLog.Error(config.PhoneType == "Avaya" ? "AVAYA_DIAL_REQUEST_FAILED" :
                config.PhoneType == "Snom" ? "SNOM_DIAL_REQUEST_FAILED" :
                config.PhoneType == "Yealink" ? "YEALINK_DIAL_REQUEST_FAILED" : "AMI_ORIGINATE_REQUEST_FAILED", e);
            status.Text = "Chiama: " + e.Message;
        }
        finally
        {
            Interlocked.Exchange(ref dialInProgress, 0);
        }
    }
}
