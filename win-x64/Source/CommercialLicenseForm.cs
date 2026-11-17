using System;
using System.Drawing;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MedialinkCTIClient;

// R7.1: i dati hardware vengono precompilati localmente, ma inviati solo con conferma esplicita.
internal sealed class CommercialLicenseForm : Form
{
    private readonly TextBox id = new TextBox { Dock = DockStyle.Fill };
    private readonly TextBox key = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    private readonly TextBox username = new TextBox { Dock = DockStyle.Fill, MaxLength = 80 };
    private readonly TextBox email = new TextBox { Dock = DockStyle.Fill, MaxLength = 254 };
    private readonly TextBox computer = new TextBox { Dock = DockStyle.Fill, MaxLength = 80 };
    private readonly TextBox mac = new TextBox { Dock = DockStyle.Fill, MaxLength = 17 };
    private readonly CheckBox confirm = new CheckBox {
        Dock = DockStyle.Fill, AutoSize = true,
        Text = "Confermo i dati inseriti e autorizzo l'invio al License Server Medialink per la gestione della postazione."
    };
    private readonly TextBox result = new TextBox { Dock = DockStyle.Fill, ReadOnly = true, Multiline = true,
        ScrollBars = ScrollBars.Vertical };
    private readonly Button activate = new Button { AutoSize = true, Text = "Conferma e attiva licenza" };
    private readonly Button refresh = new Button { AutoSize = true, Text = "Valida online / rinnova grace" };

    private static void AddRow(TableLayoutPanel table, string label, Control field, int index)
    {
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, index);
        table.Controls.Add(field, 1, index);
    }

    internal CommercialLicenseForm()
    {
        Text = "Medialink CTI Client - Licenza commerciale";
        Width = 720; Height = 520; StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(610, 450);
        // R7.4 Update 3: la licenza commerciale resta in primo piano finche aperta.
        TopMost = true;
        ShowInTaskbar = true;
        Shown += (_, _) => { BringToFront(); Activate(); };
        var outer = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14),
            ColumnCount = 1, RowCount = 5 };
        outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        outer.Controls.Add(new Label { Text = "Nome PC e MAC proposti automaticamente: verifica i dati prima di confermare l’invio.",
            Dock = DockStyle.Fill, AutoSize = true }, 0, 0);
        var fields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 6 };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(fields, "License ID", id, 0);
        AddRow(fields, "License Key", key, 1);
        AddRow(fields, "Username *", username, 2);
        AddRow(fields, "Email *", email, 3);
        AddRow(fields, "Nome computer *", computer, 4);
        AddRow(fields, "MAC (facoltativo)", mac, 5);
        outer.Controls.Add(fields, 0, 1);
        outer.Controls.Add(confirm, 0, 2);
        var commands = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        commands.Controls.Add(activate); commands.Controls.Add(refresh);
        outer.Controls.Add(commands, 0, 3);
        outer.Controls.Add(result, 0, 4);
        Controls.Add(outer);
        computer.Text = Environment.MachineName;
        mac.Text = DetectMacAddress();
        result.Text = "License Server: license.medialink.cloudns.eu:8443\r\n" + CommercialLicense.Status();
        activate.Click += async (_, _) => await RunAsync(async () =>
        {
            var registration = new LicenseRegistration {
                Username = username.Text,
                Email = email.Text,
                ComputerName = computer.Text,
                MacAddress = mac.Text,
                Confirmed = confirm.Checked
            };
            registration.Validate();
            return await CommercialLicense.ActivateAsync(id.Text, key.Text, registration);
        });
        refresh.Click += async (_, _) => await RunAsync(CommercialLicense.RefreshAsync);
    }
    // Solo precompilazione in memoria; nessun invio prima della conferma utente.
    // Ignora loopback e schede non operative; la selezione puo' essere corretta manualmente.
    private static string DetectMacAddress()
    {
        try
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                    n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                    n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .Select(n => new { Adapter = n, Bytes = n.GetPhysicalAddress().GetAddressBytes() })
                .Where(n => n.Bytes.Length == 6 && n.Bytes.Any(b => b != 0))
                .OrderBy(n => (n.Adapter.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                               n.Adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) ? 0 : 1)
                .FirstOrDefault();
            return nic == null ? "" : string.Join(":", nic.Bytes.Select(b => b.ToString("X2")));
        }
        catch { return ""; }
    }

    private async Task RunAsync(Func<Task<string>> action)
    {
        activate.Enabled = false; refresh.Enabled = false;
        try { result.Text = await action(); }
        catch (Exception ex) { result.Text = "Operazione non riuscita: " + ex.Message; }
        finally { activate.Enabled = true; refresh.Enabled = true; }
    }
}
