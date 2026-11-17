using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
namespace MedialinkCTIClient;
internal static class ApplicationInfo
{
    internal static void Show(IWin32Window? owner = null)
    {
        var assembly = typeof(ApplicationInfo).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString() ?? "non disponibile";
        string file = assembly.Location;
        string date = File.Exists(file) ? File.GetLastWriteTime(file).ToString("dd/MM/yyyy HH:mm") : "non disponibile";
        using var dialog = new Form { Text="Informazioni - MLCTIClient", Width=485, Height=330,
            BackColor=Color.White, Font=new Font("Segoe UI",10), FormBorderStyle=FormBorderStyle.FixedDialog,
            StartPosition=FormStartPosition.CenterParent, MaximizeBox=false, MinimizeBox=false, ShowInTaskbar=false };
        var label = new Label { Dock=DockStyle.Fill, Padding=new Padding(25,18,12,5),
            Text="MEDIALINK CTI CLIENT\n\nVersione dettagliata: " + version +
                 "\nData file build: " + date + "\nArchitettura: " + (Environment.Is64BitProcess ? "x64" : "x86") +
                 "\n\nProduttore: Medialink S.r.l.\nAutore: Antonio Vaccaro" +
                 "\nCopyright © 2026 Medialink S.r.l. - Tutti i diritti riservati." };
        var ok = new Button { Text="Chiudi", Dock=DockStyle.Bottom, Height=36, DialogResult=DialogResult.OK };
        dialog.Controls.Add(label); dialog.Controls.Add(ok); dialog.AcceptButton=ok;
        if(owner==null) dialog.ShowDialog(); else dialog.ShowDialog(owner);
    }
}
