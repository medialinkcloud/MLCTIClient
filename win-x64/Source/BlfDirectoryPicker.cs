using System.Drawing;
using System.Windows.Forms;

namespace MedialinkCTIClient;

// Explicit selection only. LDAP retrieval never auto-monitors all extensions.
internal sealed class BlfDirectoryPicker : Form
{
    private readonly CheckedListBox list = new() { Dock=DockStyle.Fill, CheckOnClick=true,
        IntegralHeight=false, Font=new Font("Segoe UI", 10) };
    private readonly TextBox filter = new() { Dock=DockStyle.Top, PlaceholderText="Cerca interno o nome..." };
    private readonly List<Contact> available;
    private readonly HashSet<string> selected = new(StringComparer.Ordinal);
    private bool rebuilding;
    public string SelectedExtensions { get; private set; }

    public BlfDirectoryPicker(List<Contact> candidates, string current, string own)
    {
        Text="Seleziona interni BLF dalla rubrica UCM";
        Width=565; Height=610; MinimumSize=new Size(430,430);
        StartPosition=FormStartPosition.CenterParent;
        Font=new Font("Segoe UI",9.5f);
        available = candidates.Where(c => c.Number != own)
            .GroupBy(c => c.Number, StringComparer.Ordinal)
            .Select(g => g.First()).OrderBy(c => c.Number.PadLeft(8,'0')).ToList();
        foreach(var item in BlfLogic.Parse(current, own)) selected.Add(item.Extension);
        SelectedExtensions=current;
        var note=new Label { Dock=DockStyle.Top, Height=50, Padding=new Padding(8),
            Text="Seleziona gli interni da monitorare. La rubrica LDAP non viene modificata." };
        var footer=new Panel { Dock=DockStyle.Bottom, Height=52 };
        var confirm=new Button { Text="Conferma selezione", Width=160, Height=32, Anchor=AnchorStyles.Right|AnchorStyles.Top };
        var cancel=new Button { Text="Annulla", Width=95, Height=32, Anchor=AnchorStyles.Right|AnchorStyles.Top,
            DialogResult=DialogResult.Cancel };
        footer.Controls.Add(confirm);footer.Controls.Add(cancel);
        footer.Resize += (_,_) => {confirm.Left=footer.ClientSize.Width-268;confirm.Top=10;
            cancel.Left=footer.ClientSize.Width-104;cancel.Top=10;};
        Controls.Add(list);Controls.Add(filter);Controls.Add(note);Controls.Add(footer);
        filter.TextChanged += (_,_) => Render();
        list.ItemCheck += (_,args) =>
        {
            if(rebuilding || args.Index<0 || args.Index>=list.Items.Count)return;
            var item=(Choice)list.Items[args.Index];
            if(args.NewValue==CheckState.Checked)selected.Add(item.Contact.Number);
            else selected.Remove(item.Contact.Number);
        };
        confirm.Click += (_,_) =>
        {
            if(selected.Count>100) {MessageBox.Show(this,"Massimo 100 interni BLF.");return;}
            var labels=available.ToDictionary(c=>c.Number,c=>c.Name,StringComparer.Ordinal);
            // Keep previously selected numbers even if temporarily absent from LDAP.
            SelectedExtensions=string.Join(";",selected.OrderBy(n=>n.PadLeft(8,'0'),StringComparer.Ordinal)
                .Select(n=>n+":"+(labels.TryGetValue(n,out var name)?name:"Interno "+n).Replace(';',' ').Replace(':',' ')));
            DialogResult=DialogResult.OK;Close();
        };
        AcceptButton=confirm;CancelButton=cancel;
        Render();
    }
    private sealed class Choice
    {
        public Contact Contact {get;}
        public Choice(Contact c){Contact=c;}
        public override string ToString()=>Contact.Number+"  —  "+Contact.Name;
    }
    private void Render()
    {
        rebuilding=true;
        try
        {
            list.BeginUpdate();list.Items.Clear();
            string term=filter.Text.Trim();
            foreach(var c in available.Where(c=>term.Length==0 || c.Number.Contains(term,StringComparison.OrdinalIgnoreCase)
                || c.Name.Contains(term,StringComparison.OrdinalIgnoreCase)))
                list.Items.Add(new Choice(c),selected.Contains(c.Number));
        }
        finally{list.EndUpdate();rebuilding=false;}
    }
}
