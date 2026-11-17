using System.Drawing;
using System.Windows.Forms;

namespace MedialinkCTIClient;

/// <summary>Complete UCM contact editor. It never writes directly; the caller
/// performs duplicate checks, obtains confirmation and issues the HTTPS API command.</summary>
internal sealed class AddContactForm : Form
{
    private readonly Dictionary<string, TextBox> fields = new(StringComparer.Ordinal);
    private readonly int maxInternalDigits;
    private readonly bool editing;
    public UcmContactDetails Result { get; private set; }

    public AddContactForm(UcmContactDetails initial, int maxInternalDigits, bool editing)
    {
        this.maxInternalDigits = maxInternalDigits;
        this.editing = editing;
        Result = initial.Copy();
        Text = editing ? "Modifica contatto LDAP" : "Nuovo contatto LDAP";
        Size = new Size(565, 680);
        MinimumSize = new Size(500, 500);
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9.5F);
        BackColor = Color.White;

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 57,
            FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10, 9, 14, 0),
            BackColor = Color.FromArgb(245, 248, 252) };
        var save = new Button { Text = editing ? "Salva modifiche" : "Aggiungi contatto",
            Width = 149, Height = 34 };
        var cancel = new Button { Text = "Annulla", Width = 90, Height = 34,
            DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        if (editing && initial.CanEdit)
        {
            var remove = new Button { Text = "Elimina contatto", Width = 140, Height = 34,
                ForeColor = Color.FromArgb(161, 34, 37),
                BackColor = Color.FromArgb(255, 242, 242) };
            remove.Click += (_, _) =>
            {
                // Intent only: the parent will re-read the LDAP entry and ask
                // explicit confirmation before calling deleteContact.
                DialogResult = DialogResult.No;
                Close();
            };
            buttons.Controls.Add(remove);
        }

        var scroller = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(16, 9, 16, 8) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
            ColumnCount = 2, RowCount = 0, Padding = new Padding(2, 0, 2, 6) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 152));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddField("accountnumber", "Account Number *", initial.AccountNumber);
        AddField("calleridname", "Nome chiamante *", initial.CallerIdName);
        AddField("firstname", "Nome", initial.FirstName);
        AddField("lastname", "Cognome", initial.LastName);
        AddField("email", "Email", initial.Email);
        AddField("mobilenumber", "Cellulare", initial.MobileNumber);
        AddField("homenumber", "Telefono", initial.HomeNumber);
        AddField("family_number", "Numero famiglia", initial.FamilyNumber);
        AddField("fax", "Fax", initial.Fax);
        AddField("department", "Reparto", initial.Department);
        AddField("position", "Ruolo / posizione", initial.Position);

        foreach (var (key, label, _) in specification)
        {
            var index = layout.RowCount++;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft }, 0, index);
            var input = fields[key];
            input.Dock = DockStyle.Fill;
            input.Margin = new Padding(0, 7, 2, 6);
            layout.Controls.Add(input, 1, index);
        }
        layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        layout.Controls.Add(new Label { Text = "Rubrica LDAP", Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft }, 0, layout.RowCount - 1);
        layout.Controls.Add(new Label { Text = initial.PhonebookDn, Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true,
            ForeColor = Color.FromArgb(64, 91, 116) }, 1, layout.RowCount - 1);
        scroller.Controls.Add(layout);
        Controls.Add(scroller);
        Controls.Add(buttons);

        save.Click += (_, _) =>
        {
            try
            {
                var result = initial.Copy();
                result.AccountNumber = Value("accountnumber");
                result.CallerIdName = Value("calleridname");
                result.FirstName = Value("firstname");
                result.LastName = Value("lastname");
                result.Email = Value("email");
                result.MobileNumber = Value("mobilenumber");
                result.HomeNumber = Value("homenumber");
                result.FamilyNumber = Value("family_number");
                result.Fax = Value("fax");
                result.Department = Value("department");
                result.Position = Value("position");
                UcmContactWriteRules.ValidateDetails(result, maxInternalDigits, editing);
                Result = result;
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Contatto non valido", MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        };
        AcceptButton = save;
        CancelButton = cancel;
        Shown += (_, _) => { BringToFront(); Activate(); fields["calleridname"].Focus(); };
    }

    private readonly List<(string Key, string Label, string Value)> specification = new();
    private void AddField(string key, string label, string value)
    {
        specification.Add((key, label, value));
        fields[key] = new TextBox { Text = value, BorderStyle = BorderStyle.FixedSingle };
    }
    private string Value(string key) => fields[key].Text.Trim();
}
